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
    /// Launch recipe (recorded from real launches with a diagnostic probe, since removed):
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
        private static CommandJournal.Saved _orders;
        private static List<(int uid, System.Text.Json.JsonElement moves)> _playerOrders;

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

        private static SaveSlots.Info _info;     // the save being loaded (for messages)
        private static float _pendingSince;
        private const float WorldReadyTimeout = 300f;

        /// <summary>F10: load the quicksave.</summary>
        internal static void BeginQuickLoad()
        {
            if (!File.Exists(SaveMod.QuickSavePath)) { Fail("No quicksave yet. Press F5 during a battle to make one."); return; }
            Begin(SaveMod.QuickSavePath);
        }

        /// <summary>
        /// Relaunch the battle stored in <paramref name="path"/> and restore it. Returns null when
        /// the load has started, otherwise the reason it couldn't (already shown to the player).
        /// </summary>
        internal static string Begin(string path)
        {
            Log("==== full load @ " + DateTime.Now.ToString("s") + " <- " + path + " ====");
            if (Busy) return Fail("A saved game is already loading.");

            var info = SaveSlots.Inspect(path);
            if (!info.Exists) return Fail("There is no save in " + info.Title + ".");
            if (info.Problem != null) return Fail("Can't load " + info.Title + ": " + info.Problem + ".");
            _info = info;
            _path = path;

            Launch? l = ReadLaunch();
            if (l == null) return Fail("Can't load " + info.Title + ": no battle info in the save.");
            Launch launch = l.Value;
            Log("save launch: scenario='" + launch.scenario + "' scene='" + launch.scene + "' deck='" + launch.deck + "'");

            ScenarioSource src = FindScenario(launch);
            if (src == null) return Fail("Can't load " + info.Title + ": the mission \"" + info.Name + "\" isn't installed.");
            Log("scenario resolved: " + src.Name + " (hash " + src.Hash + ")");

            // In a battle there are two ways to launch, both the game's own:
            //  - same scenario as the running battle -> "Restart mission" route (IsMissionRestart +
            //    ChangeScene(src, "", Restarted: true)); the game keeps the chosen deck across it.
            //  - different scenario -> back to the main menu first, then launch from there.
            // (A plain ChangeScene from a battle lost the deck to the old battle's teardown.)
            bool restart = false;
            if (GameController.IsInstanceAlive)
            {
                if (IsRunning(src)) { restart = true; Log("in a battle of the same scenario: using the game's Restart mission route"); }
                else
                {
                    try
                    {
                        var t = ISceneTransition.Instance;
                        if (t == null) return Fail("Couldn't start the load: the game's scene loader isn't available.");
                        _viaMenu = path;
                        _viaMenuSince = UnityEngine.Time.realtimeSinceStartup;
                        _menuSeenAt = -1f;
                        Log("in a battle of another scenario: returning to the main menu first");
                        t.ChangeScene(null, "Hangar_Scene", false, null, null, null, default(ChangeSceneExtraOptions));
                    }
                    catch (Exception e) { _viaMenu = null; return Fail("Couldn't return to the main menu to load: " + e.Message); }
                    return null;
                }
            }
            LoadGame.SourcePath = path;

            if (!SetDeck(launch.deck))
                Notify.Error("The deck used in this save (" + launch.deck + ") wasn't found; loading with your current deck.",
                             "[load] deck '" + launch.deck + "' not found; the game will use its current deck");

            try
            {
                var transition = ISceneTransition.Instance;
                if (transition == null) return Fail("Couldn't start the load: the game's scene loader isn't available.");
                Pending = true;
                _pendingSince = UnityEngine.Time.realtimeSinceStartup;
                _suppressing = true;
                Suppressed = 0;
                if (restart)
                {
                    PreloadSharedPlayerDeck.IsMissionRestart = true;
                    var opts = default(ChangeSceneExtraOptions);
                    opts.Restarted = true;
                    transition.ChangeScene(src, "", false, null, null, null, opts);
                }
                else transition.ChangeScene(src, launch.scene, false, null, null, null, default(ChangeSceneExtraOptions));
                Log("ChangeScene issued; default units will be suppressed until the world is ready");
            }
            catch (Exception e)
            {
                Pending = false;
                _suppressing = false;
                LoadGame.SourcePath = null;
                return Fail("Couldn't start the saved battle: " + e.Message);
            }
            return null;
        }

        /// <summary>A load that stops: one log line + an on-screen message. Returns the message.</summary>
        private static string Fail(string message)
        {
            Log("abort: " + message);
            Notify.Error(message, "[load] " + message);
            return message;
        }

        // ---- hooks ----

        private static void WorldReadyPostfix()
        {
            if (!Pending) return;
            Pending = false;
            int units = -1;
            try { units = Inspector.CountUnits(); } catch { }
            string deck = "?";
            try { deck = PreloadSharedPlayerDeck.ScenarioStartDeck?.FileName ?? "NONE (game will use a fallback deck!)"; } catch { }
            Log("world ready: suppressed " + Suppressed + " default spawn(s) so far; units on map: " + units + "; deck: " + deck + ".");
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
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (Pending && now - _pendingSince > WorldReadyTimeout)
            {
                Fail("The saved battle didn't finish loading. Try loading it again.");
                Finish();
                return;
            }
            if (_stage == Stage.Idle) return;
            if (!GameController.IsInstanceAlive)
            {
                LoadGame.BatchDone = null;
                Fail("Loading stopped: the battle was closed before it finished.");
                Finish();
                return;
            }
            if (_stage == Stage.Spawning)
            {
                // The spawn batch normally ends in OnArmySpawned. If it never started (no units in
                // the save, or the unit database wasn't available) finish the restore without it.
                if (!LoadGame.BatchRunning && LoadGame.BatchDone != null)
                {
                    LoadGame.BatchDone = null;
                    OnArmySpawned(new Dictionary<int, Il2CppDefaultEcs.Entity>(), new Dictionary<int, string>());
                }
                return;
            }

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
                // From here on, new spawns in this battle get uids that can't collide with the
                // restored army's saved ones (see MissionState.GiveFreeUid).
                MissionState.ResetUidGuard();
                _loadedBattle = GameController.Instance.Pointer;

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
                if (_orders != null) CommandJournal.Replay(_orders, Log);   // last: the script's patrol/attack orders
                CommandJournal.ReplayPlayerOrders(_playerOrders, Log);      // then the moves the player had given
            }
            catch (Exception e)
            {
                Log("restore threw: " + e);
                Finish();
                Notify.Error("Loaded " + Describe() + ", but part of the mission state couldn't be restored.",
                             "[load] " + Describe() + ": restore error: " + (e.InnerException ?? e).Message);
                return;
            }
            Finish();
            Log("==== load complete (spawns suppressed during load: " + Suppressed + ") ====");
            int ok = LoadGame.SpawnedOk, total = LoadGame.SpawnTotal;
            string units = ok + "/" + total + " units";
            if (ok < total)
                Notify.Error("Loaded " + Describe() + ": " + (total - ok) + " of " + total + " units couldn't be restored.",
                             "[load] Loaded " + Describe() + ": " + units + " restored");
            else
                Notify.Info("Loaded " + Describe(), "[load] Loaded " + Describe() + ": " + units + " restored");
        }

        /// <summary>"Slot 2 (Parnu Invasion, 26:40)".</summary>
        private static string Describe() =>
            _info == null ? "the save" : _info.Title + " (" + _info.Name + ", " + _info.BattleTime + ")";

        private static void Finish()
        {
            LoadGame.SourcePath = null;
            _stage = Stage.Idle;
            _suppressing = false;
            Pending = false;
            LoadGame.NativeUids = false;
        }

        // The battle a load restored (its GameController), for the uid guard.
        private static IntPtr _loadedBattle;

        private static bool InLoadedBattle =>
            _loadedBattle != IntPtr.Zero && GameController.IsInstanceAlive && GameController.Instance.Pointer == _loadedBattle;

        // While a load is in progress: skip every spawn that isn't ours (returns an already-completed
        // task so callers awaiting it just continue). After a load, in that battle: make sure the new
        // unit's mission uid is free.
        private static bool SuppressSpawnPrefix(SpawnData __0, ref UniTask __result)
        {
            if (_suppressing)
            {
                if (LoadGame.OwnSpawnCall) return true;
                Suppressed++;
                __result = UniTask.CompletedTask;
                return false;
            }
            if (InLoadedBattle) MissionState.GiveFreeUid(__0, Log);
            return true;
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
            _orders = null;
            _playerOrders = null;
            try
            {
                string path = _path;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                _mission = MissionState.Parse(root);
                _script = ScriptState.Parse(root);
                _deck = DeckState.Parse(root);
                _gameMode = GameModeState.Parse(root);
                _orders = CommandJournal.Parse(root);
                _playerOrders = CommandJournal.ParsePlayerOrders(root);
                Log("save state: gameTime=" + _mission.gameTime.ToString("0.0") + " zones=" + _mission.zones.Count +
                    " players=" + _mission.money.Count + " journal=" + _mission.journal.Count +
                    " scriptNodes=" + (_script != null ? _script.nodes.Count.ToString() : "none") +
                    " deckEntries=" + (_deck != null ? (_deck.used.Count + _deck.left.Count).ToString() : "none") +
                    " refunds=" + (_deck != null ? _deck.refunds.Count.ToString() : "none") +
                    " gameMode=" + (_gameMode != null ? ((Il2CppNetworkCommon.Enums.GameModeType)_gameMode.type).ToString() : "none"));
                return _script != null || _deck != null || _gameMode != null || _orders != null || _mission.zones.Count > 0 || _mission.journal.Count > 0 || _mission.legacyPlayZone.HasValue;
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
            if (now - _viaMenuSince > 90f) { _viaMenu = null; Fail("Loading stopped: the main menu didn't come up."); return; }
            if (GameController.IsInstanceAlive || !MainMenuUp()) { _menuSeenAt = -1f; return; }
            if (_menuSeenAt < 0f) { _menuSeenAt = now; return; }
            if (now - _menuSeenAt < 1.5f) return;
            string path = _viaMenu;
            _viaMenu = null;
            Log("main menu ready; launching the save");
            Begin(path);
        }

        /// <summary>Is <paramref name="src"/> the scenario of the battle that's running now?</summary>
        private static bool IsRunning(ScenarioSource src)
        {
            bool Same(ScenarioSource s) => s != null && s.Name == src.Name && (string.IsNullOrEmpty(s.Hash) || s.Hash == src.Hash);
            try { if (Same(ISceneLoadManager.Instance?.LoadScenario)) return true; } catch { }
            try { if (Same(ScenariosService.ActiveScenario)) return true; } catch { }
            return false;
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
