using System;
using System.Diagnostics;
using System.IO;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BASaveGame.SaveMod), "BA Save Game", "0.1.0", "Nick")]
[assembly: MelonGame(null, null)] // any Unity game; guarded at runtime instead

namespace BASaveGame
{
    /// <summary>
    /// Entry point for the Broken Arrow save-game mod.
    ///
    /// Current stage: scaffold only. It verifies the modding environment
    /// (MelonLoader up, EasyAntiCheat NOT loaded) and prepares the save
    /// directory. Actual save/load hooks are added after the recon phase
    /// pins down the ECS entry points — see the plan file.
    /// </summary>
    public class SaveMod : MelonMod
    {
        internal static string SaveDir;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("BA Save Game initializing...");

            // This build targets an offline-only install with no online capability, so
            // there is no ban surface to protect against. We do NOT block on anti-cheat.
            // Keep a precise, non-blocking note only if the real EAC runtime module is present.
            if (IsAntiCheatRuntimeLoaded())
                LoggerInstance.Warning(
                    "EasyAntiCheat runtime module detected. Continuing anyway (offline install). " +
                    "Do not use this mod in any online/ranked context.");

            try
            {
                // AppData\LocalLow\SteelBalalaikaStudio\BrokenArrow\Saves
                string localLow = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "SteelBalalaikaStudio", "BrokenArrow");
                SaveDir = Path.Combine(localLow, "Saves");
                Directory.CreateDirectory(SaveDir);
                LoggerInstance.Msg("Save directory: " + SaveDir);
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Could not prepare save directory: " + e);
            }

            _enabled = true;
            LoggerInstance.Msg("BA Save Game ready. F5 = QUICKSAVE. Inspector: F7 summary, F8 census, F9 unit dump, F11 unit records.");
        }

        private static bool _enabled;
        private bool _inputWarned;

        public override void OnUpdate()
        {
            if (!_enabled) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.F5)) Inspector.WriteQuickSave();
                else if (Input.GetKeyDown(KeyCode.F7)) Inspector.WorldSummary();
                else if (Input.GetKeyDown(KeyCode.F8)) Inspector.ComponentCensus();
                else if (Input.GetKeyDown(KeyCode.F9)) Inspector.UnitDump();
                else if (Input.GetKeyDown(KeyCode.F10)) Inspector.SerializerTest();
                else if (Input.GetKeyDown(KeyCode.F11)) Inspector.UnitRecords();
            }
            catch (Exception e)
            {
                if (!_inputWarned)
                {
                    _inputWarned = true;
                    LoggerInstance.Warning("Legacy Input unavailable (" + e.Message +
                        "). Hotkeys disabled; will add an alternate trigger if needed.");
                }
            }
        }

        /// <summary>
        /// Precise, informational-only check for the actual EasyAntiCheat runtime module.
        /// Matches only the known EAC client module base names (not any substring like "EAC"),
        /// to avoid false positives from unrelated modules. Never used to block.
        /// </summary>
        private static bool IsAntiCheatRuntimeLoaded()
        {
            try
            {
                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    string name = (m.ModuleName ?? string.Empty).ToLowerInvariant();
                    if (name == "easyanticheat_x64.dll" ||
                        name == "easyanticheat_x86.dll" ||
                        name == "easyanticheat.dll")
                        return true;
                }
            }
            catch { /* enumeration failure is not meaningful here */ }
            return false;
        }
    }
}
