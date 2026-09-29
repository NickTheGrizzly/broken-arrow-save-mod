using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
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
        private static bool _quiet;   // re-issuing player orders: don't journal them as script orders
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
            if (target == null || _quiet) return;
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

        // ================= player orders =================
        // Orders a human player gave by hand live on the unit (CommandsComponent: current command +
        // queue) and never pass through the script bus. At save time we read the move orders of
        // human players' units (target position + kind); after a load they're re-issued through
        // the same bus by the unit's mission uid (restored units keep theirs), without journaling
        // them as script orders. Attacks/loads/other command types aren't restored.

        private static Type _commandsType;

        /// <summary>
        /// A unit's pending orders, current first: moves ["move"|"fast"|"attackMove", x, y, z],
        /// ["unload", x, y, z] and airstrikes ["strike", x, y, z, attackType, targetUid]. Null if none.
        /// <paramref name="uidOf"/> maps an entity id to its mission uid (0 = none), for strike targets.
        /// </summary>
        internal static string UnitMovesJson(Il2CppDefaultEcs.World world, int entityId, Func<int, int> uidOf)
        {
            try
            {
                _commandsType ??= typeof(GameController).Assembly.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.CommandsComponent");
                var cc = Inspector.ReadComp(world, _commandsType, entityId) as Il2CppBrokenArrow.Shared.Ecs.Components.CommandsComponent;
                if (cc == null) return null;
                var moves = new List<string>();
                var current = cc.CurrentCommand;
                AddCommand(current, moves, uidOf);
                var queue = cc.Commands;
                var seen = new List<string>();
                if (current != null) seen.Add(Describe(current) + "*");
                if (queue != null)
                    foreach (var c in queue.ToArray())
                    {
                        // The current command can still sit at the head of the queue: don't record it twice.
                        if (c == null || (current != null && c.Pointer == current.Pointer)) continue;
                        seen.Add(Describe(c));
                        AddCommand(c, moves, uidOf);
                    }
                if (SaveMod.DevMode) ModLog.Dev("[orders] E" + entityId + " commands: " + (seen.Count == 0 ? "none" : string.Join(", ", seen)) + " -> saved " + moves.Count);
                return moves.Count == 0 ? null : "[" + string.Join(", ", moves) + "]";
            }
            catch (Exception e) { ModLog.DevWarn("[orders] unit E" + entityId + " orders: " + e.Message); return null; }
        }

        /// <summary>"AirstrikeCommand[completed]" etc., for the developer log.</summary>
        private static string Describe(Il2CppBrokenArrow.Shared.Ecs.Commands.ICommand command)
        {
            try
            {
                var bc = command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.BaseCommand>();
                string name = ScriptDump.SafeTypeName(command);
                if (bc == null) return name;
                return name + (bc.WasCompleted ? "[completed]" : "") + (bc.WasCanceled ? "[canceled]" : "");
            }
            catch { return "?"; }
        }

        private static void AddCommand(Il2CppBrokenArrow.Shared.Ecs.Commands.ICommand command, List<string> moves, Func<int, int> uidOf)
        {
            if (command == null) return;
            var bc = command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.BaseCommand>();
            if (bc == null || bc.WasCompleted || bc.WasCanceled) return;

            // Jets' precision strikes: the strike points still to hit (from the current one on).
            var precision = command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.PrecisionStrikeCommand>();
            if (precision != null)
            {
                var all = precision._allStrikePositions;
                if (all == null || all.Length == 0) return;
                var currentPoint = precision._currentStrikePoint;
                int from = 0;
                if (currentPoint != null)
                    for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].Pointer == currentPoint.Pointer) { from = i; break; }
                var pts = new List<string>();
                for (int i = from; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    var q = all[i].Point;
                    pts.Add(F(q.x) + ", " + F(q.y) + ", " + F(q.z));
                }
                if (pts.Count > 0) moves.Add("[\"precision\", " + string.Join(", ", pts) + "]");
                return;
            }

            // Artillery fire missions: point or line (creeping is re-issued as a line), ammo, duration.
            var fire = command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.FireMissionCommand>();
            if (fire != null)
            {
                // SecondTargetPosition / MissionSettings are Nullable<struct> fields, which Il2CppInterop
                // reads with a shifted layout (a point came back as (y, z, 0) and sent the guns to the
                // map corner). Read them from the object's memory instead: IL2CPP Nullable<T> is
                // { bool hasValue; T value } with the value at +4.
                int mode = 1, ammo = 1, duration = 1;   // Point / Explosion / Short
                var a = fire.FirstTargetPosition;
                var b = a;
                try
                {
                    IntPtr info = FieldAddress(fire, typeof(Il2CppBrokenArrow.Client.Ecs.Commands.FireMissionCommand), "MissionSettings");
                    if (info != IntPtr.Zero && Marshal.ReadByte(info) != 0)
                    {
                        int am = Marshal.ReadInt32(info + 4), du = Marshal.ReadInt32(info + 8), mo = Marshal.ReadInt32(info + 12);
                        if (am >= 0 && am <= 3) ammo = am;
                        if (du >= 0 && du <= 3) duration = du;
                        if (mo >= 1 && mo <= 3) mode = mo;
                    }
                    IntPtr second = FieldAddress(fire, typeof(Il2CppBrokenArrow.Client.Ecs.Commands.FireMissionCommand), "SecondTargetPosition");
                    if (mode >= 2 && second != IntPtr.Zero && Marshal.ReadByte(second) != 0)
                    {
                        var s2 = new UnityEngine.Vector3(ReadFloat(second + 4), ReadFloat(second + 8), ReadFloat(second + 12));
                        // A barrage line is a few hundred metres at most: anything else is a bad read.
                        if ((s2 - a).magnitude < 3000f) b = s2; else mode = 1;
                    }
                    else mode = 1;
                }
                catch (Exception e) { mode = 1; ModLog.DevWarn("[orders] fire mission settings: " + e.Message); }
                ModLog.Dev("[orders] fire mission: mode " + mode + ", ammo " + ammo + ", duration " + duration + ", at " + a + (mode >= 2 ? " to " + b : ""));
                moves.Add("[\"fire\", " + F(a.x) + ", " + F(a.y) + ", " + F(a.z) + ", " + F(b.x) + ", " + F(b.y) + ", " + F(b.z) +
                          ", " + mode + ", " + ammo + ", " + duration + "]");
                return;
            }

            // "Back to base": drive to the spawner, then despawn with a refund.
            if (command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.BackToBaseCommand>() != null)
            {
                moves.Add("[\"base\", 0, 0, 0]");
                return;
            }

            var strike = command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.AirstrikeCommand>();
            if (strike != null)
            {
                int targetUid = 0;
                try
                {
                    var target = strike.TargetEntity;
                    if (target.HasValue) targetUid = uidOf(target.Value.EntityId);
                }
                catch { }
                var s = strike.FirstTargetPosition;
                moves.Add("[\"strike\", " + F(s.x) + ", " + F(s.y) + ", " + F(s.z) + ", " + (int)strike.AttackType + ", " + targetUid + "]");
                return;
            }

            var move = command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.BaseMoveCommand>();
            if (move == null) return;
            string kind = command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.UnloadCommand>() != null ? "unload"
                        : command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.FastMoveCommand>() != null ? "fast"
                        : command.TryCast<Il2CppBrokenArrow.Client.Ecs.Commands.MoveAndAttackCommand>() != null ? "attackMove"
                        : "move";
            var p = move.TargetPosition;
            moves.Add("[\"" + kind + "\", " + F(p.x) + ", " + F(p.y) + ", " + F(p.z) + "]");
        }

        private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        private static float ReadFloat(IntPtr p) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p));

        /// <summary>Address of an IL2CPP instance field inside <paramref name="obj"/> (via the interop class's field-info pointer).</summary>
        private static IntPtr FieldAddress(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj, Type interopType, string field)
        {
            var fi = interopType.GetField("NativeFieldInfoPtr_" + field, BindingFlags.NonPublic | BindingFlags.Static);
            if (fi == null) return IntPtr.Zero;
            var info = (IntPtr)fi.GetValue(null);
            if (info == IntPtr.Zero) return IntPtr.Zero;
            uint offset = Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(info);
            return obj.Pointer + (int)offset;
        }

        /// <summary>The "playerOrders" member: [{"uid": N, "m": [[kind, x, y, z], ...]}, ...].</summary>
        internal static string PlayerOrdersJson(List<KeyValuePair<int, string>> perUnit) =>
            perUnit.Count == 0 ? null
            : "\"playerOrders\": [" + string.Join(", ", perUnit.ConvertAll(kv => "{\"uid\": " + kv.Key + ", \"m\": " + kv.Value + "}")) + "]";

        internal static List<(int uid, JsonElement moves)> ParsePlayerOrders(JsonElement root)
        {
            var list = new List<(int, JsonElement)>();
            if (root.TryGetProperty("playerOrders", out var po) && po.ValueKind == JsonValueKind.Array)
                foreach (var o in po.EnumerateArray()) list.Add((o.GetProperty("uid").GetInt32(), o.GetProperty("m").Clone()));
            return list;
        }

        /// <summary>Re-issue human players' saved move orders to the restored units, in order (the rest queued).</summary>
        internal static void ReplayPlayerOrders(List<(int uid, JsonElement moves)> orders, Action<string> log)
        {
            if (orders == null || orders.Count == 0) return;
            var cmd = GameController.IsInstanceAlive ? GameController.Instance.GetEcsEventBus?.Commands : null;
            if (cmd?.MoveSimple == null) { log("player orders: no command bus"); return; }
            int units = 0, moves = 0, failed = 0;
            _replaying = true;
            _quiet = true;
            try
            {
                foreach (var (uid, m) in orders)
                {
                    bool first = true;
                    foreach (var mv in m.EnumerateArray())
                    {
                        try
                        {
                            string kind = mv[0].GetString();
                            var at = new UnityEngine.Vector3(mv[1].GetSingle(), mv[2].GetSingle(), mv[3].GetSingle());
                            if (kind == "unload")
                            {
                                var u = new UnloadCommandData();
                                u.UnitID = uid;
                                u.TargetPosition = at;
                                u.Queue = !first;
                                u.Blocking = false;
                                u.UnloadedUnitsBuffer = new IntList();
                                cmd.UnloadCommand?.Invoke(u);
                                moves++;
                                first = false;
                                continue;
                            }
                            if (kind == "base")
                            {
                                var r = new Il2CppBrokenArrow.ScriptEngine.Data.NodeRefundData();
                                r.UnitUID = uid;
                                r.BackToBase = true;
                                r.Queue = !first;
                                r.Blocking = false;
                                cmd.RefundCommand?.Invoke(r);
                                moves++;
                                first = false;
                                continue;
                            }
                            if (kind == "precision")
                            {
                                var p = new Il2CppBrokenArrow.ScriptEngine.Data.NodePrecisionStrikeCommandData();
                                p.UnitUID = uid;
                                var points = new Il2CppSystem.Collections.Generic.List<Il2CppBrokenArrow.ScriptEngine.Data.NodePrecisionStrikeData>();
                                for (int i = 1; i + 2 < mv.GetArrayLength(); i += 3)
                                {
                                    var pt = new Il2CppBrokenArrow.ScriptEngine.Data.NodePrecisionStrikeData();
                                    pt.TargetPosition = new UnityEngine.Vector3(mv[i].GetSingle(), mv[i + 1].GetSingle(), mv[i + 2].GetSingle());
                                    points.Add(pt);
                                }
                                p.CommandData = points;
                                p.RandomSpreadPositions = new Il2CppSystem.Collections.Generic.HashSet<UnityEngine.Vector3>();
                                p.Queue = !first;
                                p.Blocking = false;
                                cmd.PrecisionStrikeCommand?.Invoke(p);
                                moves++;
                                first = false;
                                continue;
                            }
                            if (kind == "fire")
                            {
                                var start = at;
                                var end = new UnityEngine.Vector3(mv[4].GetSingle(), mv[5].GetSingle(), mv[6].GetSingle());
                                int mode = mv[7].GetInt32();
                                var ammoType = (AmmoTypeEnum)Math.Max(0, mv[8].GetInt32() - 1);   // unit enums start with None
                                var duration = (DurationEnum)Math.Max(0, mv[9].GetInt32() - 1);
                                if (mode >= 2)
                                {
                                    var l = new FireMissionLineData();
                                    l.UnitUID = uid;
                                    l.TargetStartVector = start;
                                    l.TargetEndVector = end;
                                    l.AmmoType = ammoType;
                                    l.Duration = duration;
                                    l.Queue = !first;
                                    l.Blocking = false;
                                    cmd.FireMissionLineTarget?.Invoke(l, out _, out _);   // outs: where the game aimed
                                }
                                else
                                {
                                    var pd = new FireMissionPointData();
                                    pd.UnitUID = uid;
                                    pd.TargetVector = start;
                                    pd.AmmoType = ammoType;
                                    pd.Duration = duration;
                                    pd.Queue = !first;
                                    pd.Blocking = false;
                                    cmd.FireMissionPointTarget?.Invoke(pd, out _);
                                }
                                moves++;
                                first = false;
                                continue;
                            }
                            if (kind == "strike")
                            {
                                var a = new Il2CppBrokenArrow.ScriptEngine.Data.NodeAirstrikeData();
                                a.UnitUID = uid;
                                a.TargetVector = at;
                                a.AttackType = (AirAttackType)mv[4].GetInt32();
                                int targetUid = mv[5].GetInt32();
                                if (targetUid > 0) { var t = new IntList(); t.Add(targetUid); a.TargetUnitList = t.Cast<IntReadOnlyList>(); }
                                a.IgnoreFOW = true;   // the strike was already committed when saved
                                a.Queue = !first;
                                a.Blocking = false;
                                cmd.AirstrikeCommand?.Invoke(a);
                                moves++;
                                first = false;
                                continue;
                            }
                            var d = new MoveSimpleData();
                            d.UnitUID = uid;
                            d.TargetVector = new UnityEngine.Vector3(mv[1].GetSingle(), mv[2].GetSingle(), mv[3].GetSingle());
                            d.PathfindingMethod = kind == "fast" ? PathfindingMethod.Fast : PathfindingMethod.Normal;
                            d.RulesOfEngagement = kind == "attackMove" ? RulesOfEngagementEnum.Aggressive
                                                : kind == "fast" ? RulesOfEngagementEnum.Retaliate : RulesOfEngagementEnum.Defensive;
                            d.Queue = !first;
                            d.Blocking = false;
                            cmd.MoveSimple.Invoke(d);
                            moves++;
                            first = false;
                        }
                        catch (Exception e) { if (failed++ < 5) log("  player order for uid " + uid + " threw: " + (e.InnerException ?? e).Message); }
                    }
                    units++;
                }
            }
            finally { _replaying = false; _quiet = false; }
            log("player orders: re-issued " + moves + " move(s) to " + units + " unit(s)" + (failed > 0 ? " (" + failed + " failed)" : ""));
        }

        private static IntReadOnlyList ToList(JsonElement arr)
        {
            var list = new IntList();
            if (arr.ValueKind == JsonValueKind.Array) foreach (var x in arr.EnumerateArray()) list.Add(x.GetInt32());
            return list.Cast<IntReadOnlyList>();
        }
    }
}
