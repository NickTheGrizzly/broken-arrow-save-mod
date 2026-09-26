using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Shared.Ecs.Services;           // Session
using Il2CppBrokenArrow.MissionEditor.MissionResolver;  // ScenarioSource, ScenariosService, ScenarioLocation
using Il2CppBrokenArrow.Client.Ecs.Decks;              // DeckService
using Il2CppBrokenArrow.Client.Ecs.Decks.Models;       // IDeckDataModel, DeckDataModel
using Il2CppBrokenArrow.Client.Ecs.Decks_v2;           // PreloadSharedPlayerDeck
using Il2CppBrokenArrow.Client.Ecs.Utils;              // ISceneLoadManager
using Il2CppBrokenArrow.Client.Ecs.UI;                 // ISceneTransition
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppBrokenArrow.Client.Ecs.Spawn;              // SpawnService, SpawnData
using Il2CppBrokenArrow.Shared.Ecs.MemoryController;   // IUnitLoadScope
using Il2CppCysharp.Threading.Tasks;                   // UniTask
using Il2CppMapLoadingServices.ObsoleteApi;             // ChangeSceneExtraOptions

namespace BASaveGame
{
    /// <summary>
    /// FULL LOAD (F10, from the main menu or in battle): relaunch the saved scenario exactly the
    /// way the menu does, keep the scenario's default units from spawning, and once the world is
    /// ready spawn the saved army with the normal F12 batch.
    ///
    /// Launch recipe (recorded from real launches — see LaunchProbe / live_launch.txt):
    ///   PreloadSharedPlayerDeck.ScenarioStartDeck = player's deck
    ///   ISceneTransition.Instance.ChangeScene(src, sceneName, false, null, null, null, default)
    ///   -> SceneLoadManager.LoadSceneAsync -> GameController.OnScenarioControllerLoaded (world ready)
    /// </summary>
    internal static class LoadFlow
    {
        /// <summary>True from our relaunch until the world is ready.</summary>
        internal static bool Pending;
        internal static int Suppressed;

        // Every spawn that isn't ours is skipped from the relaunch until the restore is complete:
        // the script's start-up (and anything it reacts to while we restore) must not add units.
        // The saved army IS the mission's unit state, re-registered under its saved mission uids.
        private static bool _suppressing;

        // After world ready (the script has run its start-up chain):
        //   Start    -> game time, capture-zone owners (script listeners may react)
        //   Zones    -> [settle] stop every node the fresh start left running, spawn the saved army
        //   Spawning -> [batch done] node state, effect journal, money, resume running nodes, done
        private enum Stage { Idle, Start, Zones, Spawning }
        private static Stage _stage = Stage.Idle;
        private static float _stageAt;
        private const float StartDelay = 1.5f, ZoneSettle = 2f;
        private static MissionState.Saved _mission;
        private static ScriptState.Saved _script;
        private static DeckState.Saved _deck;
        private static GameModeState.Saved _gameMode;

        private struct Launch { public string scenario, folder, hash, scene, deck; }

        internal static void Install(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(GameController), "OnScenarioControllerLoaded"),
                        postfix: new HarmonyMethod(typeof(LoadFlow).GetMethod(nameof(WorldReadyPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Log("patch FAILED OnScenarioControllerLoaded: " + e.Message); }

            try
            {
                MethodInfo spawn = AccessTools.Method(typeof(SpawnService), "SpawnUnit", new[] { typeof(SpawnData), typeof(IUnitLoadScope) });
                h.Patch(spawn, prefix: new HarmonyMethod(typeof(LoadFlow).GetMethod(nameof(SuppressSpawnPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Log("patch FAILED SpawnService.SpawnUnit: " + e.Message); }
        }

        private static string _path;

        /// <summary>True from F10 / a Load click until the restore has finished.</summary>
        internal static bool Busy => Pending || _stage != Stage.Idle || _viaMenu != null;

        // A load started inside a battle goes through the main menu first, exactly like the game
        // (battles are only ever launched from the hangar). Launching straight from a battle let the
        // old battle's teardown clear PreloadSharedPlayerDeck.ScenarioStartDeck after we set it, and
        // the new battle fell back to an "every unit x99" deck.
        private static string _viaMenu;
        private static float _viaMenuSince, _menuSeenAt;

        /// <summary>F10: load the quicksave.</summary>
        internal static void Begin() => Begin(SaveMod.QuickSavePath);

        /// <summary>Relaunch the battle stored in <paramref name="path"/> and restore it.</summary>
        internal static void Begin(string path)
        {
            Log("==== full load @ " + DateTime.Now.ToString("s") + " <- " + path + " ====");
            if (Busy) { Log("a load is already in progress"); return; }
            if (GameController.IsInstanceAlive)
            {
                try
                {
                    var t = ISceneTransition.Instance;
                    if (t == null) { Log("abort: ISceneTransition.Instance is null"); return; }
                    _viaMenu = path;
                    _viaMenuSince = UnityEngine.Time.realtimeSinceStartup;
                    _menuSeenAt = -1f;
                    Log("in a battle: returning to the main menu first (like the game's own launch path)");
                    t.ChangeScene(null, "Hangar_Scene", false, null, null, null, default(ChangeSceneExtraOptions));
                }
                catch (Exception e) { _viaMenu = null; Log("abort: return to main menu threw: " + e.Message); }
                return;
            }
            _path = path;
            LoadGame.SourcePath = path;

            Launch? l = ReadLaunch();
            if (l == null) return;
            Launch launch = l.Value;
            Log("save launch: scenario='" + launch.scenario + "' scene='" + launch.scene + "' deck='" + launch.deck + "'");

            ScenarioSource src = FindScenario(launch);
            if (src == null) { Log("abort: scenario '" + launch.scenario + "' not found"); return; }
            Log("scenario resolved: " + src.Name + " (hash " + src.Hash + ")");

            if (!SetDeck(launch.deck)) Log("warning: deck '" + launch.deck + "' not set; the game will use its current deck");

            try
            {
                var transition = ISceneTransition.Instance;
                if (transition == null) { Log("abort: ISceneTransition.Instance is null"); return; }
                Pending = true;
                _suppressing = true;
                Suppressed = 0;
                transition.ChangeScene(src, launch.scene, false, null, null, null, default(ChangeSceneExtraOptions));
                Log("ChangeScene issued; default units will be suppressed until the world is ready");
            }
            catch (Exception e) { Pending = false; _suppressing = false; Log("abort: ChangeScene threw: " + e.Message); }
        }

        // ---- hooks ----

        private static void WorldReadyPostfix()
        {
            if (!Pending) return;
            Pending = false;
            int units = -1;
            try { units = Inspector.CountUnits(); } catch { }
            Log("world ready: suppressed " + Suppressed + " default spawn(s) so far; units on map: " + units + ".");
            if (!ReadState())
            {
                Log("no mission state in the save: spawning the saved army only");
                _stage = Stage.Zones;
                _stageAt = -999f;
                return;
            }
            _stage = Stage.Start;
            _stageAt = UnityEngine.Time.realtimeSinceStartup;
        }

        /// <summary>Called every frame from OnUpdate: drives the post-world-ready load stages.</summary>
        internal static void Pump()
        {
            if (_viaMenu != null) { PumpViaMenu(); return; }
            if (_stage == Stage.Idle || _stage == Stage.Spawning) return;
            if (!GameController.IsInstanceAlive) { Log("battle ended during load; stopping"); Finish(); return; }
            float now = UnityEngine.Time.realtimeSinceStartup;

            if (_stage == Stage.Start && now - _stageAt >= StartDelay)
            {
                Log("restoring world state...");
                MissionState.RestoreGameTime(_mission.gameTime, Log);
                MissionState.RestoreZones(_mission.zones, Log);
                _stage = Stage.Zones;
                _stageAt = now;
            }
            else if (_stage == Stage.Zones && now - _stageAt >= ZoneSettle)
            {
                if (_script != null) ScriptState.CancelRunning(Log);
                Log("spawning saved army as mission units (units on map now: " + Inspector.CountUnits() + ", suppressed so far: " + Suppressed + ")...");
                _stage = Stage.Spawning;
                LoadGame.NativeUids = true;
                LoadGame.BatchDone = OnArmySpawned;
                LoadGame.SpawnAllUnits();
            }
        }

        private static void OnArmySpawned(Dictionary<int, Il2CppDefaultEcs.Entity> spawned, Dictionary<int, string> groups)
        {
            LoadGame.NativeUids = false;
            try
            {
                int dupes = MissionState.DuplicateUids();
                Log("mission uids: " + LoadGame.UidReport + (dupes > 0 ? "; WARNING " + dupes + " duplicate uid(s)" : ""));
                var unbound = LoadGame.UnboundSpawned(spawned);
                if (unbound.Count > 0) MissionState.AssignGroups(unbound, groups, Log);  // fallback where the uid didn't take
                MissionState.RemoveAllExcept(spawned.Values, Log);        // anything that slipped past suppression

                // World effects first: anything the script does in reaction is overwritten by the
                // node restore that follows.
                if (_mission != null) MissionState.ReplayJournal(_mission, Log);

                List<Il2CppBrokenArrow.ScriptEngine.Core.NodeCore> resume = null;
                if (_script != null)
                {
                    ScriptState.CancelRunning(Log);                       // again: our spawns/zones/journal may have woken nodes
                    resume = ScriptState.Restore(_script, Log);
                }
                if (_mission != null && _mission.money.Count > 0) { Log("restoring money:"); MissionState.RestoreMoney(_mission.money, Log); }
                if (_deck != null) DeckState.Restore(_deck, Log);
                if (_gameMode != null) GameModeState.Restore(_gameMode, _mission?.zones, Log);
                if (resume != null && resume.Count > 0) ScriptState.Resume(resume, _script.gameTime, Log);
            }
            catch (Exception e) { Log("restore threw: " + e); }
            Finish();
            Log("==== load complete (spawns suppressed during load: " + Suppressed + ") ====");
        }

        private static void Finish()
        {
            LoadGame.SourcePath = null;
            _stage = Stage.Idle;
            _suppressing = false;
            Pending = false;
            LoadGame.NativeUids = false;
        }

        // Skip every spawn that isn't ours while a load is in progress (returns an already-completed
        // task so callers awaiting it just continue).
        private static bool SuppressSpawnPrefix(ref UniTask __result)
        {
            if (!_suppressing || LoadGame.OwnSpawnCall) return true;
            Suppressed++;
            __result = UniTask.CompletedTask;
            return false;
        }

        // ---- scenario + deck ----

        private static ScenarioSource FindScenario(Launch l)
        {
            bool Match(ScenarioSource s) =>
                s != null && s.Name == l.scenario && (string.IsNullOrEmpty(l.hash) || s.Hash == l.hash);

            try { if (Match(ScenariosService.ActiveScenario)) return ScenariosService.ActiveScenario; } catch { }
            try
            {
                var mgr = ISceneLoadManager.Instance;
                if (mgr != null && Match(mgr.LoadScenario)) return mgr.LoadScenario;
            }
            catch { }

            ScenariosService svc = GetSvc<ScenariosService>();
            if (svc == null) { Log("ScenariosService not available"); return null; }
            foreach (ScenarioLocation loc in Enum.GetValues(typeof(ScenarioLocation)))
            {
                try
                {
                    if (svc.TryGetScenario(l.scenario, loc, out ScenarioSource s) && s != null)
                    {
                        if (!string.IsNullOrEmpty(l.hash) && s.Hash != l.hash)
                            Log("note: scenario hash differs (saved " + l.hash + ", found " + s.Hash + ") — using it anyway");
                        return s;
                    }
                }
                catch (Exception e) { Log("TryGetScenario(" + loc + ") threw: " + e.Message); }
            }
            return null;
        }

        private static bool SetDeck(string deckFile)
        {
            if (string.IsNullOrEmpty(deckFile)) return false;
            try
            {
                var current = PreloadSharedPlayerDeck.ScenarioStartDeck;
                if (current != null && current.FileName == deckFile) { Log("deck already selected: " + deckFile); return true; }

                DeckService decks = GetSvc<DeckService>();
                if (decks == null) { Log("DeckService not available"); return false; }
                if (decks.TryLoadDeckModel(deckFile, out DeckDataModel deck) && deck != null)
                {
                    PreloadSharedPlayerDeck.ScenarioStartDeck = deck.Cast<IDeckDataModel>();
                    Log("deck set: " + deckFile);
                    return true;
                }
                Log("deck '" + deckFile + "' not found by DeckService");
            }
            catch (Exception e) { Log("SetDeck threw: " + e.Message); }
            return false;
        }

        private static T GetSvc<T>() where T : Il2CppSystem.Object
        {
            try
            {
                Il2CppSystem.Object o = Session.GetService(Il2CppInterop.Runtime.Il2CppType.Of<T>());
                return o == null ? null : o.Cast<T>();
            }
            catch (Exception e) { Log("service " + typeof(T).Name + " threw: " + e.Message); return null; }
        }

        // ---- save file ----

        private static Launch? ReadLaunch()
        {
            string path = _path;
            if (!File.Exists(path)) { Log("no save at " + path); return null; }
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.IndexOf("\"launch\"", StringComparison.Ordinal) < 0) continue;
                return new Launch
                {
                    scenario = Str(line, "scenario"),
                    folder = Str(line, "folder"),
                    hash = Str(line, "hash"),
                    scene = Str(line, "scene"),
                    deck = Str(line, "deck"),
                };
            }
            Log("save has no launch info — press F5 in a battle with this build to re-save");
            return null;
        }

        /// <summary>Parse mission + script state from the save. False when the save has neither.</summary>
        private static bool ReadState()
        {
            _mission = null;
            _script = null;
            _deck = null;
            _gameMode = null;
            try
            {
                string path = _path;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                _mission = MissionState.Parse(root);
                _script = ScriptState.Parse(root);
                _deck = DeckState.Parse(root);
                _gameMode = GameModeState.Parse(root);
                Log("save state: gameTime=" + _mission.gameTime.ToString("0.0") + " zones=" + _mission.zones.Count +
                    " players=" + _mission.money.Count + " journal=" + _mission.journal.Count +
                    " scriptNodes=" + (_script != null ? _script.nodes.Count.ToString() : "none") +
                    " deckEntries=" + (_deck != null ? (_deck.used.Count + _deck.left.Count).ToString() : "none") +
                    " refunds=" + (_deck != null ? _deck.refunds.Count.ToString() : "none") +
                    " gameMode=" + (_gameMode != null ? ((Il2CppNetworkCommon.Enums.GameModeType)_gameMode.type).ToString() : "none"));
                return _script != null || _deck != null || _gameMode != null || _mission.zones.Count > 0 || _mission.journal.Count > 0 || _mission.legacyPlayZone.HasValue;
            }
            catch (Exception e) { Log("reading save state threw: " + e.Message); return false; }
        }

        private static string Str(string line, string key)
        {
            var m = Regex.Match(line, "\"" + key + "\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : "";
        }

        // Wait for the main menu to be up and settled, then start the real load from there.
        private static void PumpViaMenu()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _viaMenuSince > 90f) { Log("gave up waiting for the main menu"); _viaMenu = null; return; }
            if (GameController.IsInstanceAlive || !MainMenuUp()) { _menuSeenAt = -1f; return; }
            if (_menuSeenAt < 0f) { _menuSeenAt = now; return; }
            if (now - _menuSeenAt < 1.5f) return;
            string path = _viaMenu;
            _viaMenu = null;
            Log("main menu ready; launching the save");
            Begin(path);
        }

        private static bool MainMenuUp()
        {
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MainMenuScreen>()))
                {
                    var s = o.TryCast<MainMenuScreen>();
                    if (s != null && s.gameObject.activeInHierarchy) return true;
                }
            }
            catch { }
            return false;
        }

        private static void Log(string s) => LoadGame.Live("[flow] " + s);
    }
}
