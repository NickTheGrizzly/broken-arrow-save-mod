using System;
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
        /// <summary>True from our relaunch until the world is ready: suppress non-mod spawns.</summary>
        internal static bool Pending;
        internal static int Suppressed;

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

        /// <summary>F10.</summary>
        internal static void Begin()
        {
            Log("==== full load @ " + DateTime.Now.ToString("s") + " ====");
            if (Pending) { Log("a load is already in progress"); return; }

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
                Suppressed = 0;
                transition.ChangeScene(src, launch.scene, false, null, null, null, default(ChangeSceneExtraOptions));
                Log("ChangeScene issued; default units will be suppressed until the world is ready");
            }
            catch (Exception e) { Pending = false; Log("abort: ChangeScene threw: " + e.Message); }
        }

        // ---- hooks ----

        private static void WorldReadyPostfix()
        {
            if (!Pending) return;
            Pending = false;
            int units = -1;
            try { units = Inspector.CountUnits(); } catch { }
            Log("world ready: suppressed " + Suppressed + " default spawn(s); units on map now: " + units + ". Spawning saved army...");
            LoadGame.SpawnAllUnits();
        }

        // Skip every spawn that isn't ours while a load is pending (returns an already-completed
        // task so callers awaiting it just continue).
        private static bool SuppressSpawnPrefix(ref UniTask __result)
        {
            if (!Pending || LoadGame.OwnSpawnCall) return true;
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
            string path = Path.Combine(SaveMod.SaveDir, "quicksave.basave");
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

        private static string Str(string line, string key)
        {
            var m = Regex.Match(line, "\"" + key + "\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : "";
        }

        private static void Log(string s) => LoadGame.Live("[flow] " + s);
    }
}
