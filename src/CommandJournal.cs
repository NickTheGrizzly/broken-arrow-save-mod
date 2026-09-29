using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppBrokenArrow.Shared.Ecs.MissionEditor;      // EcsEventBus, MoveSimpleData, MoveComplexData, CaptureZoneData, AttackUnitData
using Il2CppBrokenArrow.Shared.Ecs.Enums;              // WayPathEnum
using IntList = Il2CppSystem.Collections.Generic.List<int>;
using IntReadOnlyList = Il2CppSystem.Collections.Generic.IReadOnlyList<int>;

namespace BASaveGame
{
    /// <summary>
    /// Movement orders the mission script gave (patrols, attack waves, waypoint paths). The script
    /// issues them once, through EcsEventBus.Commands, and moves on; a load restarts the mission
    /// and restores the node graph, but nothing re-issues those orders, so the restored units
    /// stood still. We append our own handler to each command delegate (like MissionState's effect
    /// journal), keep the latest orders per target (unit uid, uid list or group; queued orders are
    /// kept in sequence), save them, and replay them after a load. Restored units keep their
    /// mission uids and groups, so the orders reach the same units.
    /// Replays are non-blocking: the script nodes that waited on them were restored separately.
    /// Player orders don't go through this bus; they're not restored.
    /// </summary>
    internal static class CommandJournal
    {
        private sealed class Entry { public long seq; public string kind; public string target; public Dictionary<string, object> fields; }

        // target key -> orders for it, oldest first (a non-queued order replaces the list)
        private static readonly Dictionary<string, List<Entry>> _orders = new Dictionary<string, List<Entry>>();
        private static long _seq;
        private static IntPtr _hooked;
        private static float _nextCheck;
        private static bool _replaying;
        private static readonly List<Il2CppSystem.Delegate> _keepAlive = new List<Il2CppSystem.Delegate>();

        private static readonly Dictionary<string, Type> Kinds = new Dictionary<string, Type>
        {
            { "moveSimple", typeof(MoveSimpleData) },
            { "moveComplex", typeof(MoveComplexData) },
            { "capture", typeof(CaptureZoneData) },
            { "attack", typeof(AttackUnitData) },
        };

        /// <summary>Called every frame from OnUpdate: attaches the journal once per battle.</summary>
        internal static void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + 0.25f;
            try
            {
                if (!GameController.IsInstanceAlive) { _hooked = IntPtr.Zero; return; }
                var gc = GameController.Instance;
                if (gc == null || gc.Pointer == _hooked) return;
                var cmd = gc.GetEcsEventBus?.Commands;
                // Wait until the game has wired its own handlers, so we append rather than get overwritten.
                if (cmd == null || cmd.MoveSimple == null) return;
                _hooked = gc.Pointer;
                _orders.Clear();

                cmd.MoveSimple = Combine(cmd.MoveSimple, new Action<MoveSimpleData>(d => Note("moveSimple", d)));
                cmd.MoveComplex = Combine(cmd.MoveComplex, new Action<MoveComplexData>(d => Note("moveComplex", d)));
                cmd.CaptureObjective = Combine(cmd.CaptureObjective, new Action<CaptureZoneData>(d => Note("capture", d)));
                cmd.AttackEnemy = Combine(cmd.AttackEnemy, new Action<AttackUnitData>(d => Note("attack", d)));
                cmd.SetWaypoint = Combine(cmd.SetWaypoint,
                    new Action<int, int, string, IntReadOnlyList, WayPathEnum>((unit, wp, groups, list, path) => NoteWaypoint(unit, wp, groups, list, path)));
                cmd.CancelUnitCommands = Combine(cmd.CancelUnitCommands, new Action<IntReadOnlyList, bool>((list, all) => NoteCancel(list, all)));
                ModLog.Dev("[orders] journaling mission-script unit orders for this battle.");
            }
            catch (Exception e) { ModLog.Warn("[orders] order journal unavailable: " + e.Message); }
        }

        private static MethodInfo _convert;

        private static T Combine<T>(T current, Delegate managed) where T : Il2CppSystem.Delegate
        {
            _convert ??= Array.Find(typeof(Il2CppInterop.Runtime.DelegateSupport).GetMethods(BindingFlags.Public | BindingFlags.Static),
                m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            var converted = (Il2CppSystem.Delegate)_convert.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { managed });
            _keepAlive.Add(converted);
            return Il2CppSystem.Delegate.Combine(current, converted).Cast<T>();
        }

        // ---------------- recording ----------------

        private static void Note(string kind, BaseCommandData d)
        {
            try
            {
                if (d == null) return;
                var fields = Read(d, Kinds[kind]);
                Add(kind, TargetKey(d.UnitUID, d.GroupName, d.UnitList), fields, fields.TryGetValue("Queue", out var q) && q is bool b && b);
            }
            catch (Exception e) { ModLog.DevWarn("[orders] record " + kind + ": " + e.Message); }
        }

        private static void NoteWaypoint(int unit, int waypoint, string groups, IntReadOnlyList list, WayPathEnum path)
        {
            try
            {
                var fields = new Dictionary<string, object>
                {
                    { "unit", unit }, { "waypoint", waypoint }, { "groups", groups ?? "" }, { "list", Ids(list) }, { "path", (int)path },
                };
                Add("waypoint", TargetKey(unit, groups, list), fields, false);
            }
            catch (Exception e) { ModLog.DevWarn("[orders] record waypoint: " + e.Message); }
        }

        private static void NoteCancel(IntReadOnlyList list, bool all)
        {
            if (_replaying) return;
            try
            {
                if (all) { _orders.Clear(); return; }
                foreach (int uid in Ids(list)) _orders.Remove("u:" + uid);
            }
            catch { }
        }

        private static void Add(string kind, string target, Dictionary<string, object> fields, bool queued)
        {
            if (target == null) return;
            var e = new Entry { seq = ++_seq, kind = kind, target = target, fields = fields };
            if (!queued || !_orders.TryGetValue(target, out var list)) _orders[target] = list = new List<Entry>();
            list.Add(e);
        }

        private static string TargetKey(int unit, string groups, IntReadOnlyList list)
        {
            if (unit != 0) return "u:" + unit;
            var ids = Ids(list);
            if (ids.Count > 0) { ids.Sort(); return "l:" + string.Join(",", ids); }
            if (!string.IsNullOrEmpty(groups)) return "g:" + groups;
            return null;
        }

        private static List<int> Ids(IntReadOnlyList list)
        {
            var ids = new List<int>();
            if (list == null) return ids;
            var l = list.TryCast<IntList>();
            if (l != null) for (int i = 0; i < l.Count; i++) ids.Add(l[i]);
            return ids;
        }

        // Every settable property of the command data (base class included), as plain values.
        private static Dictionary<string, object> Read(object data, Type t)
        {
            var fields = new Dictionary<string, object>();
            foreach (var p in Props(t))
            {
                object v;
                try { v = p.GetValue(data); } catch { continue; }
                var pt = p.PropertyType;
                if (pt == typeof(int) || pt == typeof(float) || pt == typeof(bool) || pt == typeof(string)) fields[p.Name] = v;
                else if (pt.IsEnum) fields[p.Name] = Convert.ToInt32(v);
                else if (pt == typeof(UnityEngine.Vector3)) { var x = (UnityEngine.Vector3)v; fields[p.Name] = new[] { x.x, x.y, x.z }; }
                else if (pt == typeof(IntReadOnlyList)) fields[p.Name] = Ids((IntReadOnlyList)v);
                else if (pt == typeof(AggressiveRadiusData))
                {
                    var a = (AggressiveRadiusData)v;
                    fields[p.Name] = new[] { a.Grounds, a.Helicopters, a.Planes };
                }
            }
            return fields;
        }

        private static readonly Dictionary<Type, List<PropertyInfo>> _props = new Dictionary<Type, List<PropertyInfo>>();

        private static List<PropertyInfo> Props(Type t)
        {
            if (_props.TryGetValue(t, out var list)) return list;
            list = new List<PropertyInfo>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (p.DeclaringType == null || !(p.DeclaringType.Namespace ?? "").StartsWith("Il2CppBrokenArrow")) continue;
                if (p.Name == "BlockingCallback") continue;
                list.Add(p);
            }
            _props[t] = list;
            return list;
        }

        // ---------------- save ----------------

        internal static string CaptureJson()
        {
            var all = new List<Entry>();
            foreach (var l in _orders.Values) all.AddRange(l);
            if (all.Count == 0) return null;
            all.Sort((a, b) => a.seq.CompareTo(b.seq));
            var sb = new StringBuilder("\"orders\": [");
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (i > 0) sb.Append(", ");
                sb.Append("{\"k\": ").Append(JsonSerializer.Serialize(e.kind))
                  .Append(", \"t\": ").Append(JsonSerializer.Serialize(e.target))
                  .Append(", \"f\": ").Append(JsonSerializer.Serialize(e.fields)).Append('}');
            }
            ModLog.Dev("[orders] saved " + all.Count + " order(s) for " + _orders.Count + " target(s)");
            return sb.Append(']').ToString();
        }

        // ---------------- load ----------------

        internal sealed class Saved { public List<(string kind, JsonElement fields)> orders = new List<(string, JsonElement)>(); }

        internal static Saved Parse(JsonElement root)
        {
            if (!root.TryGetProperty("orders", out var os) || os.ValueKind != JsonValueKind.Array) return null;
            var s = new Saved();
            foreach (var o in os.EnumerateArray()) s.orders.Add((o.GetProperty("k").GetString(), o.GetProperty("f").Clone()));
            return s;
        }

        /// <summary>Re-issue the saved orders, oldest first. Blocking is dropped (no node waits on them now).</summary>
        internal static void Replay(Saved s, Action<string> log)
        {
            var cmd = GameController.IsInstanceAlive ? GameController.Instance.GetEcsEventBus?.Commands : null;
            if (s == null || s.orders.Count == 0) return;
            if (cmd == null) { log("orders: no command bus"); return; }
            int ok = 0, failed = 0;
            var counts = new Dictionary<string, int>();
            _replaying = true;
            try
            {
                foreach (var (kind, f) in s.orders)
                {
                    try
                    {
                        switch (kind)
                        {
                            case "moveSimple": cmd.MoveSimple?.Invoke(Build<MoveSimpleData>(f)); break;
                            case "moveComplex": cmd.MoveComplex?.Invoke(Build<MoveComplexData>(f)); break;
                            case "capture": cmd.CaptureObjective?.Invoke(Build<CaptureZoneData>(f)); break;
                            case "attack": cmd.AttackEnemy?.Invoke(Build<AttackUnitData>(f)); break;
                            case "waypoint":
                                cmd.SetWaypoint?.Invoke(f.GetProperty("unit").GetInt32(), f.GetProperty("waypoint").GetInt32(),
                                    f.GetProperty("groups").GetString(), ToList(f.GetProperty("list")), (WayPathEnum)f.GetProperty("path").GetInt32());
                                break;
                            default: continue;
                        }
                        ok++;
                        counts[kind] = counts.TryGetValue(kind, out int c) ? c + 1 : 1;
                    }
                    catch (Exception e) { if (failed++ < 5) log("  order " + kind + " threw: " + (e.InnerException ?? e).Message); }
                }
            }
            finally { _replaying = false; }
            var sb = new StringBuilder();
            foreach (var kv in counts) sb.Append(' ').Append(kv.Key).Append('x').Append(kv.Value);
            log("orders: re-issued " + ok + " unit order(s):" + sb + (failed > 0 ? " (" + failed + " failed)" : ""));
        }

        private static T Build<T>(JsonElement f) where T : BaseCommandData, new()
        {
            var d = new T();
            foreach (var p in Props(typeof(T)))
            {
                if (!f.TryGetProperty(p.Name, out var v)) continue;
                var pt = p.PropertyType;
                object val;
                if (pt == typeof(int)) val = v.GetInt32();
                else if (pt == typeof(float)) val = v.GetSingle();
                else if (pt == typeof(bool)) val = v.GetBoolean();
                else if (pt == typeof(string)) val = v.ValueKind == JsonValueKind.Null ? null : v.GetString();
                else if (pt.IsEnum) val = Enum.ToObject(pt, v.GetInt32());
                else if (pt == typeof(UnityEngine.Vector3)) val = new UnityEngine.Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
                else if (pt == typeof(IntReadOnlyList)) val = ToList(v);
                else if (pt == typeof(AggressiveRadiusData))
                {
                    var a = new AggressiveRadiusData { Grounds = v[0].GetInt32(), Helicopters = v[1].GetInt32(), Planes = v[2].GetInt32() };
                    val = a;
                }
                else continue;
                try { p.SetValue(d, val); } catch { }
            }
            d.Blocking = false;
            d.BlockingCallback = null;
            return d;
        }

        private static IntReadOnlyList ToList(JsonElement arr)
        {
            var list = new IntList();
            if (arr.ValueKind == JsonValueKind.Array) foreach (var x in arr.EnumerateArray()) list.Add(x.GetInt32());
            return list.Cast<IntReadOnlyList>();
        }
    }
}
