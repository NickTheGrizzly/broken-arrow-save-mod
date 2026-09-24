using System;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.MissionEditor.MissionResolver;  // ScenarioSource, ScenariosService
using Il2CppBrokenArrow.Client.Ecs.Decks_v2;           // PreloadSharedPlayerDeck
using Il2CppBrokenArrow.Client.Ecs.Decks.Models;       // IDeckDataModel
using Il2CppBrokenArrow.Client.Ecs.Utils;              // SceneLoadManager, ISceneLoadManager
using Il2CppBrokenArrow.Client.Ecs.UI;                 // SceneTransition
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppMapLoadingServices.ObsoleteApi;             // ChangeSceneExtraOptions

namespace BASaveGame
{
    /// <summary>
    /// DIAGNOSTIC (full-load recon): record exactly how the game launches a battle, so the load
    /// can replay the same call. Logs every SceneTransition.ChangeScene / SceneLoadManager.LoadScene*
    /// call with its arguments plus the launch globals (decks, scenario options, active scenario),
    /// and logs when GameController.OnScenarioControllerLoaded fires (the injection point).
    /// Output: Saves\live_launch.txt.
    ///
    /// Patched manually, each target in its own try/catch — a failure here must not stop the
    /// other patches (PatchAll would abort on the first one).
    /// </summary>
    internal static class LaunchProbe
    {
        internal static void Install(HarmonyLib.Harmony h)
        {
            Patch(h, typeof(SceneTransition), "ChangeScene", nameof(ChangeScenePrefix), prefix: true);
            Patch(h, typeof(SceneLoadManager), "LoadSceneAsync", nameof(LoadSceneAsyncPrefix), prefix: true);
            Patch(h, typeof(SceneLoadManager), "LoadScene", nameof(LoadScenePrefix), prefix: true);
            Patch(h, typeof(GameController), "OnScenarioControllerLoaded", nameof(ScenarioLoadedPostfix), prefix: false);
        }

        private static void Patch(HarmonyLib.Harmony h, Type t, string method, string handler, bool prefix)
        {
            try
            {
                MethodInfo target = AccessTools.Method(t, method);
                if (target == null) { Log("patch skip: " + t.Name + "." + method + " not found"); return; }
                var hm = new HarmonyMethod(typeof(LaunchProbe).GetMethod(handler, BindingFlags.Static | BindingFlags.NonPublic));
                if (prefix) h.Patch(target, prefix: hm); else h.Patch(target, postfix: hm);
            }
            catch (Exception e) { Log("patch FAILED " + t.Name + "." + method + ": " + e.Message); }
        }

        private static void ChangeScenePrefix(ScenarioSource src, string sceneName, bool editorMode, string overrideTitle,
                                              ChangeSceneExtraOptions extraOptions)
        {
            Safe(() =>
            {
                Log("==== SceneTransition.ChangeScene @ " + DateTime.Now.ToString("s"));
                Log("  src: " + Src(src));
                Log("  sceneName='" + sceneName + "' editorMode=" + editorMode + " overrideTitle='" + overrideTitle + "'");
                Log("  extraOptions: Restarted=" + extraOptions.Restarted + " BattleWasEnded=" + extraOptions.BattleWasEnded +
                    " SpectatorMode=" + extraOptions.SpectatorMode);
                Log(Globals());
            });
        }

        private static void LoadSceneAsyncPrefix(ScenarioSource src, string sceneName, bool editorMode)
        {
            Safe(() =>
            {
                Log("==== SceneLoadManager.LoadSceneAsync @ " + DateTime.Now.ToString("s"));
                Log("  src: " + Src(src));
                Log("  sceneName='" + sceneName + "' editorMode=" + editorMode);
                Log(Globals());
            });
        }

        private static void LoadScenePrefix(string sceneName, bool editorMode, string loadMissionFileName, bool sharedMode)
        {
            Safe(() =>
            {
                Log("==== SceneLoadManager.LoadScene @ " + DateTime.Now.ToString("s"));
                Log("  sceneName='" + sceneName + "' editorMode=" + editorMode + " loadMissionFileName='" +
                    loadMissionFileName + "' sharedMode=" + sharedMode);
            });
        }

        private static void ScenarioLoadedPostfix()
        {
            Safe(() =>
            {
                Log("==== GameController.OnScenarioControllerLoaded (world ready) @ " + DateTime.Now.ToString("s"));
                try { Log("  CurrentMapName='" + GameController.Instance.CurrentMapName + "'"); } catch { }
                try { Log("  unit count now: " + Inspector.CountUnits()); } catch { }
                Log(Globals());
            });
        }

        // ---- formatting ----

        private static string Src(ScenarioSource s)
        {
            if (s == null) return "null";
            return string.Format("name='{0}' folder='{1}' local={2} workshop={3} hash={4}",
                S(() => s.Name), S(() => s.Folder), S(() => s.IsLocal), S(() => s.IsWorkshop), S(() => s.Hash));
        }

        private static string Deck(IDeckDataModel d)
        {
            if (d == null) return "null";
            return string.Format("'{0}' file='{1}' country={2} spec={3}/{4}",
                S(() => d.Name), S(() => d.FileName), S(() => d.CountryID), S(() => d.Spec1ID), S(() => d.Spec2ID));
        }

        private static string Globals()
        {
            var sb = new StringBuilder();
            sb.Append("  decks: Alpha=").Append(Deck(PreloadSharedPlayerDeck.Alpha))
              .Append(" | Bravo=").Append(Deck(PreloadSharedPlayerDeck.Bravo))
              .Append(" | ScenarioStartDeck=").Append(Deck(PreloadSharedPlayerDeck.ScenarioStartDeck))
              .Append(" | IsMissionRestart=").Append(S(() => PreloadSharedPlayerDeck.IsMissionRestart));
            sb.Append("\n  ActiveScenario: ").Append(Src(ScenariosService.ActiveScenario));
            try
            {
                var mgr = ISceneLoadManager.Instance;
                if (mgr != null)
                {
                    sb.Append("\n  SceneLoadManager.LoadScenario: ").Append(Src(mgr.LoadScenario));
                    sb.Append("\n  ScenarioPublicOptions: {");
                    var opts = mgr.ScenarioPublicOptions;
                    if (opts != null)
                    {
                        var en = opts.GetEnumerator();
                        bool first = true;
                        while (en.MoveNext())
                        {
                            if (!first) sb.Append(", ");
                            sb.Append(en.Current.Key).Append(": ").Append(en.Current.Value);
                            first = false;
                        }
                    }
                    sb.Append("}");
                }
            }
            catch (Exception e) { sb.Append("\n  SceneLoadManager read threw: ").Append(e.Message); }
            return sb.ToString();
        }

        private static string S(Func<object> f)
        {
            try { return Convert.ToString(f()); } catch (Exception e) { return "<err:" + e.Message + ">"; }
        }

        private static void Safe(Action a)
        {
            try { a(); } catch (Exception e) { Log("probe threw: " + e.Message); }
        }

        private static void Log(string s)
        {
            MelonLogger.Msg("[launch] " + s);
            try
            {
                if (!string.IsNullOrEmpty(SaveMod.SaveDir))
                    File.AppendAllText(Path.Combine(SaveMod.SaveDir, "live_launch.txt"), s + Environment.NewLine);
            }
            catch { }
        }
    }
}
