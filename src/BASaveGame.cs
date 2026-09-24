using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
            // Prevent the MelonLoader console's QuickEdit mode from freezing the game:
            // clicking into the console puts it in text-selection mode, which blocks every
            // write to it. Since our code (and MelonLogger) run on the game's main thread,
            // a blocked console write hangs the whole game. Turning QuickEdit off makes an
            // accidental click harmless.
            DisableConsoleQuickEdit();

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

            try { HarmonyInstance.PatchAll(System.Reflection.Assembly.GetExecutingAssembly()); }
            catch (Exception e) { LoggerInstance.Warning("PatchAll: " + e.Message); }
            LaunchProbe.Install(HarmonyInstance);  // full-load recon: logs battle launches to live_launch.txt
            LoadFlow.Install(HarmonyInstance);     // F10 full load: world-ready hook + default-spawn suppression

            _enabled = true;
            LoggerInstance.Msg("BA Save Game ready. F5 = QUICKSAVE, F6 = load dry-run, F10 = LOAD (relaunch saved battle; works from main menu), F12 = spawn all saved units into this battle. F4 = dump mission script. Inspector: F7/F8/F9/F11.");
        }

        private static bool _enabled;
        private bool _inputWarned;

        public override void OnUpdate()
        {
            if (!_enabled) return;
            LoadGame.Pump();  // drives an in-progress F12 spawn batch (one unit per frame)
            LoadFlow.Pump();  // drives the F10 post-load stages (restore progress, settle, reconcile)
            MissionState.Tick();  // hooks the event bus once per battle to track the active map sector
            ScriptDump.Tick();    // records mission-script node activity once per battle (recon)
            try
            {
                if (Input.GetKeyDown(KeyCode.F4)) ScriptDump.Dump();
                else if (Input.GetKeyDown(KeyCode.F5)) Inspector.WriteQuickSave();
                else if (Input.GetKeyDown(KeyCode.F6)) LoadGame.DryRun();
                else if (Input.GetKeyDown(KeyCode.F7)) Inspector.WorldSummary();
                else if (Input.GetKeyDown(KeyCode.F8)) Inspector.ComponentCensus();
                else if (Input.GetKeyDown(KeyCode.F9)) Inspector.UnitDump();
                else if (Input.GetKeyDown(KeyCode.F10)) LoadFlow.Begin();
                else if (Input.GetKeyDown(KeyCode.F11)) Inspector.UnitRecords();
                else if (Input.GetKeyDown(KeyCode.F12)) LoadGame.SpawnAllUnits();
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

        // ---- Console QuickEdit hardening (Win32) ----

        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private void DisableConsoleQuickEdit()
        {
            try
            {
                IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;   // no console attached
                if (!GetConsoleMode(h, out uint mode)) return;
                uint newMode = (mode & ~ENABLE_QUICK_EDIT_MODE) | ENABLE_EXTENDED_FLAGS;
                if (newMode != mode) SetConsoleMode(h, newMode);
                LoggerInstance.Msg("Console QuickEdit disabled (clicking the console won't freeze the game).");
            }
            catch (Exception e) { LoggerInstance.Warning("DisableConsoleQuickEdit: " + e.Message); }
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
