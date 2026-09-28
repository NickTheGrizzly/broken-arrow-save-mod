using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BASaveGame
{
    /// <summary>
    /// Save slots: Saves\Slots\slot1..slotN.basave plus the quicksave (F5). Each file is a full
    /// .basave; the list shows scenario, in-battle time and when it was saved.
    /// </summary>
    internal static class SaveSlots
    {
        internal const int Count = 6;

        internal sealed class Info
        {
            public int Index;            // 0 = quicksave, 1..Count = slots, -1 = some other file
            public string Path;
            public bool Exists;
            public string Scenario = "", Map = "";
            public float GameTime;
            public DateTime SavedLocal;
            public int Version;

            /// <summary>Why this save can't be loaded ("unreadable save", "made by an older version of the mod"...), or null.</summary>
            public string Problem;

            public string Title => Index == 0 ? "Quicksave" : Index > 0 ? "Slot " + Index : System.IO.Path.GetFileNameWithoutExtension(Path);

            /// <summary>Scenario name without the "PvE (1-3p, USA)" prefix.</summary>
            public string Name => ShortName(Scenario, Map);

            /// <summary>In-battle clock at save time, "26:40" or "1:05:12".</summary>
            public string BattleTime
            {
                get
                {
                    var t = TimeSpan.FromSeconds(Math.Max(0, GameTime));
                    return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.ToString("mm\\:ss") : t.ToString("mm\\:ss");
                }
            }

            /// <summary>"Parnu Invasion · 26:40", or the problem for a save that can't be loaded.</summary>
            public string Label => Problem == null ? Name + " · " + BattleTime
                                 : string.IsNullOrEmpty(Name) ? "(" + Problem + ")" : Name + " · (" + Problem + ")";

            /// <summary>"Parnu Invasion · 26:40 · Sep 25 01:41" (or "Empty").</summary>
            public string Summary => !Exists ? "Empty" : Label + " · " + SavedLocal.ToString("MMM d HH:mm");
        }

        internal static string Dir => Path.Combine(SaveMod.SaveDir, "Slots");
        internal static string SlotPath(int index) => index == 0 ? SaveMod.QuickSavePath : Path.Combine(Dir, "slot" + index + ".basave");

        private static readonly Dictionary<string, (DateTime stamp, Info info)> _cache = new Dictionary<string, (DateTime, Info)>();

        /// <summary>Quicksave (index 0) followed by every slot, existing or not.</summary>
        internal static List<Info> All()
        {
            var list = new List<Info>();
            for (int i = 0; i <= Count; i++) list.Add(Read(i));
            return list;
        }

        /// <summary>Existing manual slots (1..Count), in slot order.</summary>
        internal static List<Info> ManualSlots() => All().FindAll(x => x.Index > 0 && x.Exists);

        /// <summary>The quicksave, or null if there isn't one.</summary>
        internal static Info Quick() { var q = Read(0); return q.Exists ? q : null; }

        /// <summary>Existing saves, newest first.</summary>
        internal static List<Info> Existing()
        {
            var list = All().FindAll(x => x.Exists);
            list.Sort((a, b) => b.SavedLocal.CompareTo(a.SavedLocal));
            return list;
        }

        internal static Info Read(int index) => Inspect(SlotPath(index), index);

        /// <summary>Read a save's header (cached per file timestamp) and check that it can be loaded.</summary>
        internal static Info Inspect(string path) => Inspect(path, IndexOf(path));

        private static Info Inspect(string path, int index)
        {
            var info = new Info { Index = index, Path = path };
            if (!File.Exists(path)) return info;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var c) && c.stamp == stamp) return c.info;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                info.Exists = true;
                info.Version = root.TryGetProperty("saveVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;
                if (root.TryGetProperty("map", out var m)) info.Map = m.GetString() ?? "";
                if (root.TryGetProperty("gameTime", out var gt) && gt.ValueKind == JsonValueKind.Number) info.GameTime = gt.GetSingle();
                if (root.TryGetProperty("launch", out var l) && l.TryGetProperty("scenario", out var sc)) info.Scenario = sc.GetString() ?? "";
                info.SavedLocal = root.TryGetProperty("savedUtc", out var su) && DateTime.TryParse(su.GetString(), null,
                                      System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                                  ? dt.ToLocalTime() : File.GetLastWriteTime(path);
                // Only the current format is loaded: older saves lack parts of the mission state,
                // and loading them half-restored would leave the mission in a wrong state.
                if (info.Version < Inspector.SaveVersion) info.Problem = "made by an older version of the mod";
                else if (info.Version > Inspector.SaveVersion) info.Problem = "made by a newer version of the mod";
                else if (string.IsNullOrEmpty(info.Scenario)) info.Problem = "no battle info in the save";
                else if (IsUnsupported(info.Scenario)) info.Problem = "mission not supported";
            }
            catch
            {
                info.Exists = true;
                info.Problem = "unreadable save";
                info.SavedLocal = File.GetLastWriteTime(path);
            }
            _cache[path] = (stamp, info);
            return info;
        }

        // Missions whose own scripting (in-mission faction/unit selection, scripted waypoints) the
        // save doesn't capture: loading them leaves the mission broken, so they're not supported.
        private static readonly string[] UnsupportedMissions = { "Welcome to Kadaga", "Assault on Daugavpils" };

        internal static bool IsUnsupported(string scenario) =>
            !string.IsNullOrEmpty(scenario) &&
            Array.Exists(UnsupportedMissions, m => scenario.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Name of the scenario being played (empty outside a battle or if unknown).</summary>
        internal static string CurrentScenario()
        {
            try
            {
                var src = Il2CppBrokenArrow.Client.Ecs.Utils.ISceneLoadManager.Instance?.LoadScenario
                          ?? Il2CppBrokenArrow.MissionEditor.MissionResolver.ScenariosService.ActiveScenario;
                return src?.Name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>True in a battle of a mission the mod doesn't support.</summary>
        internal static bool CurrentUnsupported => IsUnsupported(CurrentScenario());

        /// <summary>Delete a save file. False (with the reason) if it couldn't be removed.</summary>
        internal static bool Delete(Info slot, out string error)
        {
            error = null;
            try
            {
                if (File.Exists(slot.Path)) File.Delete(slot.Path);
                _cache.Remove(slot.Path);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        private static int IndexOf(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                for (int i = 0; i <= Count; i++)
                    if (string.Equals(Path.GetFullPath(SlotPath(i)), full, StringComparison.OrdinalIgnoreCase)) return i;
            }
            catch { }
            return -1;
        }

        /// <summary>"PvE (1-3p, USA) Parnu Invasion" -> "Parnu Invasion".</summary>
        private static string ShortName(string scenario, string map)
        {
            string s = string.IsNullOrEmpty(scenario) ? map : scenario;
            if (string.IsNullOrEmpty(s)) return "";
            int close = s.IndexOf(')');
            if (s.StartsWith("PvE", StringComparison.OrdinalIgnoreCase) && close > 0 && close + 1 < s.Length) s = s.Substring(close + 1).Trim();
            return s;
        }
    }
}
