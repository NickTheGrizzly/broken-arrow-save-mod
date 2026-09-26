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
            public int Index;            // 0 = quicksave, 1..Count = slots
            public string Path;
            public bool Exists;
            public string Scenario = "", Map = "";
            public float GameTime;
            public DateTime SavedLocal;

            public string Title => Index == 0 ? "Quicksave" : "Slot " + Index;

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

            /// <summary>"Parnu Invasion · 26:40 · Sep 25 01:41" (or "Empty").</summary>
            public string Summary => !Exists ? "Empty" : Name + " · " + BattleTime + " · " + SavedLocal.ToString("MMM d HH:mm");
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

        internal static Info Read(int index)
        {
            string path = SlotPath(index);
            var info = new Info { Index = index, Path = path };
            if (!File.Exists(path)) return info;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var c) && c.stamp == stamp) return c.info;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                info.Exists = true;
                if (root.TryGetProperty("map", out var m)) info.Map = m.GetString() ?? "";
                if (root.TryGetProperty("gameTime", out var gt) && gt.ValueKind == JsonValueKind.Number) info.GameTime = gt.GetSingle();
                if (root.TryGetProperty("launch", out var l) && l.TryGetProperty("scenario", out var sc)) info.Scenario = sc.GetString() ?? "";
                info.SavedLocal = root.TryGetProperty("savedUtc", out var su) && DateTime.TryParse(su.GetString(), null,
                                      System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                                  ? dt.ToLocalTime() : File.GetLastWriteTime(path);
            }
            catch
            {
                info.Exists = true;
                info.Scenario = "(unreadable save)";
                info.SavedLocal = File.GetLastWriteTime(path);
            }
            _cache[path] = (stamp, info);
            return info;
        }

        /// <summary>"PvE (1-3p, USA) Parnu Invasion" -> "Parnu Invasion".</summary>
        private static string ShortName(string scenario, string map)
        {
            string s = string.IsNullOrEmpty(scenario) ? map : scenario;
            int close = s.IndexOf(')');
            if (s.StartsWith("PvE", StringComparison.OrdinalIgnoreCase) && close > 0 && close + 1 < s.Length) s = s.Substring(close + 1).Trim();
            return s;
        }
    }
}
