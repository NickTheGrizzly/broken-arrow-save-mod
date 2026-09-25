using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppBrokenArrow.Client.Ecs.GameMode;           // GameModeService, ConquestMode, DestructionMode, WarGoalsMode, IGameMode
using Il2CppNetworkCommon.Enums;                       // GameModeType, TeamSide, TimerType
using Il2CppNetworkCommon.Messages.GameModes;          // GameModeReconnectData
using Il2CppNetworkCommon.Messages.Game;               // ObjectiveZonesInfo, ObjectiveZoneData
using SideDict = Il2CppSystem.Collections.Generic.Dictionary<Il2CppNetworkCommon.Enums.TeamSide, int>;

namespace BASaveGame
{
    /// <summary>
    /// Skirmish game-mode state (Conquest / Destruction / War Goals): victory, destruction and
    /// conquest points, current phase, captured zones and the mode timers. Restored through the
    /// game's own reconnect path — IGameMode.OnReconnectData(GameModeReconnectData), which is what
    /// multiplayer uses to put a rejoining client back into a running match.
    /// Scripted PvE missions run with GameModeType.None; their progress is ScriptState.
    /// </summary>
    internal static class GameModeState
    {
        private static GameModeService Service
        {
            get
            {
                try { return GameController.IsInstanceAlive ? GameController.Instance._ecsLoader?._gameModeService : null; }
                catch { return null; }
            }
        }

        // ================= SAVE =================

        internal static string CaptureJson()
        {
            var gms = Service;
            if (gms == null) return null;
            var sb = new StringBuilder();
            sb.Append("\"gameMode\": {\"type\": ").Append((int)gms.GameModeType);
            sb.Append(", \"timers\": [").Append(TimersJson(gms.Timers)).Append("]");

            var mode = gms._currentGameMode;
            var conquest = mode?.TryCast<ConquestMode>();
            var destruction = mode?.TryCast<DestructionMode>();
            var wargoals = mode?.TryCast<WarGoalsMode>();
            if (conquest != null)
            {
                sb.Append(", \"victory\": [").Append(SidesJson(conquest._victoryPoints)).Append("]");
                sb.Append(", \"destruction\": [").Append(SidesJson(conquest._destructionPoints)).Append("]");
                sb.Append(", \"conquest\": [").Append(SidesJson(conquest._conquestPoints)).Append("]");
                sb.Append(", \"phase\": ").Append(conquest._currentPhase);
                sb.Append(", \"captured\": [").Append(CapturedJson(conquest._capturedZones)).Append("]");
            }
            else if (wargoals != null)
            {
                sb.Append(", \"victory\": [").Append(SidesJson(wargoals._victoryPoints)).Append("]");
                sb.Append(", \"destruction\": [").Append(SidesJson(wargoals._destructionPoints)).Append("]");
                sb.Append(", \"phase\": ").Append(wargoals._currentPhase);
                sb.Append(", \"captured\": [").Append(CapturedJson(wargoals._capturedZones)).Append("]");
            }
            else if (destruction != null)
            {
                sb.Append(", \"victory\": [").Append(SidesJson(destruction._victoryPoints)).Append("]");
                sb.Append(", \"destruction\": [").Append(SidesJson(destruction._destructionPoints)).Append("]");
                sb.Append(", \"phase\": ").Append(destruction._currentPhase);
            }
            sb.Append("}");
            return sb.ToString();
        }

        private static string TimersJson(Il2CppSystem.Collections.Generic.Dictionary<TimerType, float> timers)
        {
            if (timers == null) return "";
            var items = new List<string>();
            var en = timers.GetEnumerator();
            while (en.MoveNext()) items.Add("[" + (int)en.Current.Key + ", " + Inv(en.Current.Value) + "]");
            return string.Join(", ", items);
        }

        private static string SidesJson(SideDict d)
        {
            if (d == null) return "";
            var items = new List<string>();
            var en = d.GetEnumerator();
            while (en.MoveNext()) items.Add("[" + (int)en.Current.Key + ", " + en.Current.Value + "]");
            return string.Join(", ", items);
        }

        private static string CapturedJson(Il2CppSystem.Collections.Generic.Dictionary<TeamSide, Il2CppSystem.Collections.Generic.HashSet<int>> d)
        {
            if (d == null) return "";
            var items = new List<string>();
            var en = d.GetEnumerator();
            while (en.MoveNext())
            {
                var zones = new List<int>();
                if (en.Current.Value != null) { var zen = en.Current.Value.GetEnumerator(); while (zen.MoveNext()) zones.Add(zen.Current); }
                items.Add("[" + (int)en.Current.Key + ", [" + string.Join(", ", zones) + "]]");
            }
            return string.Join(", ", items);
        }

        // ================= LOAD =================

        internal sealed class Saved
        {
            public int type;
            public List<KeyValuePair<int, float>> timers = new List<KeyValuePair<int, float>>();
            public List<KeyValuePair<int, int>> victory, destruction, conquest;
            public int? phase;
            public List<KeyValuePair<int, int[]>> captured;
        }

        internal static Saved Parse(JsonElement root)
        {
            if (!root.TryGetProperty("gameMode", out var g)) return null;
            var s = new Saved { type = g.GetProperty("type").GetInt32() };
            if (g.TryGetProperty("timers", out var t))
                foreach (var x in t.EnumerateArray()) s.timers.Add(new KeyValuePair<int, float>(x[0].GetInt32(), x[1].GetSingle()));
            s.victory = Sides(g, "victory");
            s.destruction = Sides(g, "destruction");
            s.conquest = Sides(g, "conquest");
            if (g.TryGetProperty("phase", out var p)) s.phase = p.GetInt32();
            if (g.TryGetProperty("captured", out var c))
            {
                s.captured = new List<KeyValuePair<int, int[]>>();
                foreach (var x in c.EnumerateArray())
                {
                    var zones = new List<int>();
                    foreach (var z in x[1].EnumerateArray()) zones.Add(z.GetInt32());
                    s.captured.Add(new KeyValuePair<int, int[]>(x[0].GetInt32(), zones.ToArray()));
                }
            }
            return s;
        }

        private static List<KeyValuePair<int, int>> Sides(JsonElement g, string name)
        {
            if (!g.TryGetProperty(name, out var arr)) return null;
            var list = new List<KeyValuePair<int, int>>();
            foreach (var x in arr.EnumerateArray()) list.Add(new KeyValuePair<int, int>(x[0].GetInt32(), x[1].GetInt32()));
            return list;
        }

        /// <param name="zoneOwners">zone uid -> owning team uid, from the mission section of the save.</param>
        internal static void Restore(Saved s, List<KeyValuePair<int, int>> zoneOwners, Action<string> log)
        {
            var gms = Service;
            if (gms == null) { log("game mode: no GameModeService"); return; }
            if (s.type == (int)GameModeType.None) { log("game mode: none (scripted mission)"); RestoreTimers(gms, s, log); return; }
            if ((int)gms.GameModeType != s.type) { log("game mode: saved type " + (GameModeType)s.type + " but battle runs " + gms.GameModeType + "; skipped"); return; }

            var mode = gms._currentGameMode;
            if (mode == null) { log("game mode: no current game mode"); return; }
            log("game mode " + gms.GameModeType + " before: victory=" + Describe(mode.VictoryPoints));

            try
            {
                var data = new GameModeReconnectData();
                data.GameModeType = (GameModeType)s.type;
                if (s.victory != null) data.VictoryPoints = ToDict(s.victory);
                if (s.destruction != null) data.DestructionPoints = ToDict(s.destruction);
                if (s.conquest != null) data.ConquestPoints = ToDict(s.conquest);
                if (s.phase.HasValue) data.CurrentPhase = s.phase.Value;

                var conquest = mode.TryCast<ConquestMode>();
                var wargoals = mode.TryCast<WarGoalsMode>();
                if (conquest != null) data.ConquestModeData = conquest._initData;
                if (wargoals != null) data.WarGoalsModeInitData = wargoals._initData;
                data.ObjectiveZonesInfo = ZonesInfo(s, zoneOwners);

                mode.OnReconnectData(data);
                log("game mode after: victory=" + Describe(mode.VictoryPoints) + (s.phase.HasValue ? " phase " + s.phase : ""));
            }
            catch (Exception e) { log("game mode reconnect threw: " + (e.InnerException ?? e).Message); }

            RestoreTimers(gms, s, log);
        }

        private static void RestoreTimers(GameModeService gms, Saved s, Action<string> log)
        {
            var timers = gms.Timers;
            if (timers == null || s.timers.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var t in s.timers)
            {
                try
                {
                    var key = (TimerType)t.Key;
                    timers.TryGetValue(key, out float before);
                    timers[key] = t.Value;
                    try { gms.TimerChangedEvent?.Invoke(key, t.Value); } catch { }
                    sb.Append(' ').Append(key).Append(' ').Append(before.ToString("0")).Append("->").Append(t.Value.ToString("0"));
                }
                catch (Exception e) { sb.Append(" [timer " + t.Key + " threw: " + e.Message + "]"); }
            }
            log("game mode timers:" + sb);
        }

        // Zone owners for the reconnect message; team uid -> side comes from the saved captured sets.
        private static ObjectiveZonesInfo ZonesInfo(Saved s, List<KeyValuePair<int, int>> zoneOwners)
        {
            var info = new ObjectiveZonesInfo();
            info.Data = new Il2CppSystem.Collections.Generic.Dictionary<int, ObjectiveZoneData>();
            info.TeamUIDTeamSide = new Il2CppSystem.Collections.Generic.Dictionary<int, TeamSide>();
            var sideOfZone = new Dictionary<int, int>();
            if (s.captured != null) foreach (var c in s.captured) foreach (int z in c.Value) sideOfZone[z] = c.Key;
            if (zoneOwners == null) return info;
            foreach (var z in zoneOwners)
            {
                var zd = new ObjectiveZoneData();
                zd.ZoneUID = z.Key;
                zd.TeamUID = z.Value;
                info.Data[z.Key] = zd;
                if (sideOfZone.TryGetValue(z.Key, out int side) && !info.TeamUIDTeamSide.ContainsKey(z.Value))
                    info.TeamUIDTeamSide[z.Value] = (TeamSide)side;
            }
            return info;
        }

        private static SideDict ToDict(List<KeyValuePair<int, int>> list)
        {
            var d = new SideDict();
            foreach (var kv in list) d[(TeamSide)kv.Key] = kv.Value;
            return d;
        }

        private static string Describe(Il2CppSystem.Collections.Generic.IReadOnlyDictionary<TeamSide, int> d)
        {
            if (d == null) return "null";
            try
            {
                var sb = new StringBuilder();
                var dict = d.TryCast<SideDict>();
                if (dict == null) return "?";
                var en = dict.GetEnumerator();
                while (en.MoveNext()) sb.Append(en.Current.Key).Append('=').Append(en.Current.Value).Append(' ');
                return sb.ToString().Trim();
            }
            catch { return "?"; }
        }

        private static string Inv(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
