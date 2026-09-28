using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppBrokenArrow.ScriptEngine.Core;             // NodeCore, NodeLogic, PortCore, NodeStatus
using Il2CppBrokenArrow.ScriptEngine.Editor.Properties; // UnitObject
using Il2CppBrokenArrow.ScriptEngine.Nodes.Timing;     // NodeDelay

namespace BASaveGame
{
    /// <summary>
    /// Mission-script state: the part of a save that makes a PvE load "native". The mission script
    /// is a node graph (NodeController._nodeList); its progress lives inside the nodes as plain
    /// values (see recon/SCRIPT_STATE.md). Save = per node: Status, which ports received a signal,
    /// and the logic's state fields (bool/int/float/enum/string/Vector3, UnitObject = mission-uid
    /// list). Load = write those back onto the freshly started graph, after cancelling whatever
    /// the fresh start left running, then resume the nodes that were mid-flight at save time.
    /// </summary>
    internal static class ScriptState
    {
        private const int StIdle = 0, StPre = 1, StInProcess = 2;

        // Fields that describe live wiring rather than progress: never written back.
        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "_subscribed", "_initialized", "_hasCapturedConnects", "_hasLostConnects",
        };

        private enum Kind { Bool, Int, Float, Enum, Str, Vec3, Units, Cts, Disposable }
        private sealed class FieldSpec { public PropertyInfo Pi; public Kind Kind; }
        private static readonly Dictionary<Type, List<FieldSpec>> _specs = new Dictionary<Type, List<FieldSpec>>();

        // ================= SAVE =================

        /// <summary>The "script" JSON member (no trailing comma). Null when not in a scripted battle.</summary>
        internal static string CaptureJson(out int nodeCount)
        {
            nodeCount = 0;
            var nc = ScriptDump.CurrentNodeController();
            var list = nc?._nodeList;
            if (list == null || list.Count == 0) return null;

            float now = ScriptDump.GameTime();
            var sb = new StringBuilder();
            sb.Append("\"script\": {\"gameTime\": ").Append(Inv(now)).Append(", \"nodes\": [\n");
            int written = 0;
            for (int i = 0; i < list.Count; i++)
            {
                NodeCore n = list[i];
                if (n == null) continue;
                string line;
                try { line = NodeJson(n); }
                catch (Exception e) { ModLog.DevWarn("[script] node " + SafeId(n) + ": " + e.Message); continue; }
                if (line == null) continue;
                if (written++ > 0) sb.Append(",\n");
                sb.Append("    ").Append(line);
            }
            sb.Append("\n  ]}");
            nodeCount = written;
            return sb.ToString();
        }

        private static string NodeJson(NodeCore n)
        {
            int st = (int)n.Status;
            var sig = new List<string>();
            CollectSignals(n.Inputs, "i:", sig);
            CollectSignals(n.Outputs, "o:", sig);
            CollectSignals(n.Props, "p:", sig);

            var fields = new StringBuilder();
            int fieldCount = 0;
            object typed = Typed(n.Logic, out Type t);
            if (typed != null)
            {
                foreach (var f in Specs(t))
                {
                    if (f.Kind == Kind.Cts || f.Kind == Kind.Disposable) continue;
                    string v;
                    try { v = ValueJson(f, f.Pi.GetValue(typed)); }
                    catch { continue; }
                    if (v == null) continue;
                    if (fieldCount++ > 0) fields.Append(", ");
                    fields.Append(JsonSerializer.Serialize(f.Pi.Name)).Append(": ").Append(v);
                }
            }
            if (st == StIdle && sig.Count == 0 && fieldCount == 0) return null;

            var sb = new StringBuilder();
            sb.Append("{\"k\": \"").Append(Key(n)).Append("\", \"t\": ").Append(JsonSerializer.Serialize(ScriptDump.SafeTypeName(n.Logic)));
            sb.Append(", \"st\": ").Append(st);
            if (st == StInProcess)
            {
                float la = ScriptDump.LastActivation(n.ID);
                if (la >= 0) sb.Append(", \"la\": ").Append(Inv(la));
            }
            if (sig.Count > 0)
            {
                sb.Append(", \"sig\": [");
                for (int i = 0; i < sig.Count; i++) sb.Append(i > 0 ? ", " : "").Append(JsonSerializer.Serialize(sig[i]));
                sb.Append("]");
            }
            if (fieldCount > 0) sb.Append(", \"f\": {").Append(fields).Append("}");
            sb.Append("}");
            return sb.ToString();
        }

        private static void CollectSignals(Il2CppReferenceArray<PortCore> ports, string prefix, List<string> into)
        {
            if (ports == null) return;
            for (int i = 0; i < ports.Length; i++)
            {
                var p = ports[i];
                if (p == null || !p._SignalWasReceived_k__BackingField) continue;
                into.Add(prefix + (p.Info?.Name ?? i.ToString()));
            }
        }

        private static string ValueJson(FieldSpec f, object v)
        {
            switch (f.Kind)
            {
                case Kind.Bool: return (bool)v ? "true" : "false";
                case Kind.Int: return Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                case Kind.Enum: return Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                case Kind.Float:
                    double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    return double.IsNaN(d) || double.IsInfinity(d) ? null : d.ToString("R", CultureInfo.InvariantCulture);
                case Kind.Str: return v == null ? "null" : JsonSerializer.Serialize((string)v);
                case Kind.Vec3:
                    var vec = (UnityEngine.Vector3)v;
                    return "[" + Inv(vec.x) + ", " + Inv(vec.y) + ", " + Inv(vec.z) + "]";
                case Kind.Units:
                    if (v == null) return "null";
                    var uo = ((Il2CppObjectBase)v).Cast<UnitObject>();
                    var ul = uo._unitList;
                    var sb = new StringBuilder("{\"u\": [");
                    if (ul != null) for (int i = 0; i < ul.Count; i++) sb.Append(i > 0 ? ", " : "").Append(ul[i]);
                    return sb.Append("]}").ToString();
            }
            return null;
        }

        // ================= LOAD =================

        internal sealed class Saved
        {
            public float gameTime;
            public List<JsonElement> nodes = new List<JsonElement>();
        }

        /// <summary>Parse the "script" member of a save; null if the save has none.</summary>
        internal static Saved Parse(JsonElement root)
        {
            if (!root.TryGetProperty("script", out var s) || s.ValueKind != JsonValueKind.Object) return null;
            var r = new Saved();
            if (s.TryGetProperty("gameTime", out var gt) && gt.ValueKind == JsonValueKind.Number) r.gameTime = gt.GetSingle();
            if (s.TryGetProperty("nodes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) r.nodes.Add(e.Clone());
            return r;
        }

        /// <summary>
        /// Stop everything the fresh start left running (delays, pulses, moves, spawn waits):
        /// cancel their CancellationTokenSources, dispose their subscriptions, mark them Idle.
        /// </summary>
        internal static void CancelRunning(Action<string> log)
        {
            var list = ScriptDump.CurrentNodeController()?._nodeList;
            if (list == null) { log("script: no node graph"); return; }
            int cancelled = 0, handles = 0;
            var kinds = new Dictionary<string, int>();
            for (int i = 0; i < list.Count; i++)
            {
                NodeCore n = list[i];
                if (n == null || (int)n.Status != StInProcess) continue;
                object typed = Typed(n.Logic, out Type t);
                if (typed != null)
                {
                    foreach (var f in Specs(t))
                    {
                        try
                        {
                            object v = f.Pi.GetValue(typed);
                            if (v == null) continue;
                            if (f.Kind == Kind.Cts) { ((Il2CppObjectBase)v).Cast<Il2CppSystem.Threading.CancellationTokenSource>().Cancel(); handles++; }
                            else if (f.Kind == Kind.Disposable) { ((Il2CppObjectBase)v).Cast<Il2CppSystem.IDisposable>().Dispose(); handles++; }
                        }
                        catch { }
                    }
                }
                n.Status = NodeStatus.Idle;
                cancelled++;
                string tn = ScriptDump.SafeTypeName(n.Logic);
                kinds[tn] = kinds.TryGetValue(tn, out int c) ? c + 1 : 1;
            }
            var sb = new StringBuilder();
            foreach (var kv in kinds) sb.Append(' ').Append(kv.Key).Append('×').Append(kv.Value);
            log("script: stopped " + cancelled + " node(s) the fresh start left running (" + handles + " handles):" + sb);
        }

        /// <summary>Write the saved node state onto the live graph. Returns the nodes to resume.</summary>
        internal static List<NodeCore> Restore(Saved saved, Action<string> log)
        {
            var resume = new List<NodeCore>();
            var list = ScriptDump.CurrentNodeController()?._nodeList;
            if (list == null) { log("script: no node graph to restore into"); return resume; }

            var byKey = new Dictionary<string, NodeCore>();
            int dupes = 0;
            for (int i = 0; i < list.Count; i++)
            {
                NodeCore n = list[i];
                if (n == null) continue;
                string k = Key(n);
                if (byKey.ContainsKey(k)) dupes++; else byKey[k] = n;
            }

            // Every node absent from the save had nothing to record (Idle, no signals, no fields):
            // reset those that the fresh start touched, so the graph matches the save exactly.
            var inSave = new HashSet<string>();
            foreach (var e in saved.nodes) if (e.TryGetProperty("k", out var k)) inSave.Add(k.GetString());

            int matched = 0, missing = 0, typeMismatch = 0, fieldsWritten = 0, fieldErrors = 0, flagsWritten = 0;
            foreach (var e in saved.nodes)
            {
                string key = e.GetProperty("k").GetString();
                if (!byKey.TryGetValue(key, out NodeCore n)) { missing++; continue; }
                string savedType = e.TryGetProperty("t", out var te) ? te.GetString() : null;
                if (savedType != null && savedType != ScriptDump.SafeTypeName(n.Logic)) { typeMismatch++; continue; }
                matched++;

                // Port flags: exactly the saved set is signalled.
                var sig = new HashSet<string>();
                if (e.TryGetProperty("sig", out var sg)) foreach (var x in sg.EnumerateArray()) sig.Add(x.GetString());
                flagsWritten += SetSignals(n.Inputs, "i:", sig) + SetSignals(n.Outputs, "o:", sig) + SetSignals(n.Props, "p:", sig);

                if (e.TryGetProperty("f", out var fe))
                {
                    object typed = Typed(n.Logic, out Type t);
                    if (typed != null)
                    {
                        var specs = new Dictionary<string, FieldSpec>();
                        foreach (var f in Specs(t)) specs[f.Pi.Name] = f;
                        foreach (var prop in fe.EnumerateObject())
                        {
                            if (!specs.TryGetValue(prop.Name, out var f) || !f.Pi.CanWrite) continue;
                            try { if (WriteField(f, typed, prop.Value)) fieldsWritten++; }
                            catch (Exception ex)
                            {
                                if (fieldErrors++ < 10) log("  field " + key + "." + prop.Name + " threw: " + (ex.InnerException ?? ex).Message);
                            }
                        }
                    }
                }

                int st = e.TryGetProperty("st", out var se) ? se.GetInt32() : StIdle;
                if (st == StInProcess)
                {
                    n.Status = NodeStatus.Idle;  // Resume() starts it again properly
                    resume.Add(n);
                    if (e.TryGetProperty("la", out var la)) ScriptDump.SeedActivation(n.ID, la.GetSingle());
                }
                else n.Status = (NodeStatus)st;
            }

            int reset = 0;
            foreach (var kv in byKey)
            {
                if (inSave.Contains(kv.Key)) continue;
                NodeCore n = kv.Value;
                int touched = 0;
                if ((int)n.Status != StIdle) { n.Status = NodeStatus.Idle; touched++; }
                touched += SetSignals(n.Inputs, "i:", null) + SetSignals(n.Outputs, "o:", null) + SetSignals(n.Props, "p:", null);
                if (touched > 0) reset++;
            }

            log("script: restored " + matched + "/" + saved.nodes.Count + " saved nodes (" + fieldsWritten + " fields, " +
                flagsWritten + " port flags changed; " + reset + " other nodes reset)" +
                (missing > 0 ? ", " + missing + " not in this graph" : "") +
                (typeMismatch > 0 ? ", " + typeMismatch + " type mismatches (skipped)" : "") +
                (dupes > 0 ? ", " + dupes + " duplicate keys in live graph" : "") +
                (fieldErrors > 0 ? ", " + fieldErrors + " field errors" : ""));
            return resume;
        }

        /// <summary>Start again the nodes that were mid-flight at save time (timers get their remaining time).</summary>
        internal static void Resume(List<NodeCore> nodes, float saveGameTime, Action<string> log)
        {
            int ok = 0, failed = 0;
            foreach (NodeCore n in nodes)
            {
                string what = "#" + Key(n) + " " + ScriptDump.SafeTypeName(n.Logic);
                try
                {
                    var logic = n.Logic;
                    var delay = logic.TryCast<NodeDelay>();
                    if (delay != null)
                    {
                        float la = ScriptDump.LastActivation(n.ID);
                        if (la >= 0 && saveGameTime > 0)
                        {
                            float remaining = Math.Max(0.1f, delay.Time - (saveGameTime - la));
                            what += " (" + delay.Time.ToString("0.0") + "s -> " + remaining.ToString("0.0") + "s left)";
                            delay.Time = remaining;
                        }
                    }
                    logic.Run(false);
                    ok++;
                    if (ok <= 40) log("  resumed " + what + " -> " + n.Status);
                }
                catch (Exception e)
                {
                    failed++;
                    log("  resume FAILED " + what + ": " + (e.InnerException ?? e).Message);
                }
            }
            log("script: resumed " + ok + " running node(s)" + (failed > 0 ? ", " + failed + " failed" : ""));
        }

        // ================= helpers =================

        private static int SetSignals(Il2CppReferenceArray<PortCore> ports, string prefix, HashSet<string> signalled)
        {
            if (ports == null) return 0;
            int changed = 0;
            for (int i = 0; i < ports.Length; i++)
            {
                var p = ports[i];
                if (p == null) continue;
                bool want = signalled != null && signalled.Contains(prefix + (p.Info?.Name ?? i.ToString()));
                if (p._SignalWasReceived_k__BackingField != want) { p._SignalWasReceived_k__BackingField = want; changed++; }
            }
            return changed;
        }

        private static bool WriteField(FieldSpec f, object typed, JsonElement v)
        {
            object cur = f.Pi.GetValue(typed);
            object next;
            switch (f.Kind)
            {
                case Kind.Bool: next = v.GetBoolean(); break;
                case Kind.Int: next = Convert.ChangeType(v.GetInt64(), f.Pi.PropertyType, CultureInfo.InvariantCulture); break;
                case Kind.Enum: next = Enum.ToObject(f.Pi.PropertyType, v.GetInt64()); break;
                case Kind.Float: next = Convert.ChangeType(v.GetDouble(), f.Pi.PropertyType, CultureInfo.InvariantCulture); break;
                case Kind.Str: next = v.ValueKind == JsonValueKind.Null ? null : v.GetString(); break;
                case Kind.Vec3:
                    next = new UnityEngine.Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
                    break;
                case Kind.Units: return WriteUnits(f, typed, cur, v);
                default: return false;
            }
            if (Equals(cur, next)) return false;
            f.Pi.SetValue(typed, next);
            return true;
        }

        private static bool WriteUnits(FieldSpec f, object typed, object cur, JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.Null)
            {
                if (cur == null) return false;
                f.Pi.SetValue(typed, null);
                return true;
            }
            var want = new List<int>();
            foreach (var x in v.GetProperty("u").EnumerateArray()) want.Add(x.GetInt32());
            if (cur != null)
            {
                var ul = ((Il2CppObjectBase)cur).Cast<UnitObject>()._unitList;
                if (ul != null && ul.Count == want.Count)
                {
                    bool same = true;
                    for (int i = 0; i < want.Count && same; i++) same = ul[i] == want[i];
                    if (same) return false;
                }
            }
            var uo = new UnitObject();
            foreach (int uid in want) uo.Add(uid);
            f.Pi.SetValue(typed, uo);
            return true;
        }

        /// <summary>The logic as its concrete interop type (so its own fields are reachable).</summary>
        private static object Typed(NodeLogic logic, out Type t)
        {
            t = null;
            if (logic == null) return null;
            t = ScriptDump.ManagedTypeOf(logic);
            if (t == null) return null;
            try { return Activator.CreateInstance(t, logic.Pointer); } catch { return null; }
        }

        // Field-backed properties (Il2CppInterop emits NativeFieldInfoPtr_<name>) from the concrete
        // type down to — but excluding — NodeLogic's own bookkeeping fields.
        private static List<FieldSpec> Specs(Type t)
        {
            if (_specs.TryGetValue(t, out var cached)) return cached;
            var result = new List<FieldSpec>();
            for (Type cur = t; cur != null && cur != typeof(NodeLogic) && cur != typeof(Il2CppSystem.Object); cur = cur.BaseType)
            {
                foreach (var pi in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!pi.CanRead || pi.GetIndexParameters().Length > 0 || Skip.Contains(pi.Name)) continue;
                    if (cur.GetField("NativeFieldInfoPtr_" + pi.Name, BindingFlags.NonPublic | BindingFlags.Static) == null) continue;
                    Kind? k = KindOf(pi.PropertyType);
                    if (k.HasValue) result.Add(new FieldSpec { Pi = pi, Kind = k.Value });
                }
            }
            _specs[t] = result;
            return result;
        }

        private static Kind? KindOf(Type pt)
        {
            if (pt == typeof(bool)) return Kind.Bool;
            if (pt == typeof(int) || pt == typeof(long) || pt == typeof(short) || pt == typeof(byte) || pt == typeof(uint)) return Kind.Int;
            if (pt == typeof(float) || pt == typeof(double)) return Kind.Float;
            if (pt.IsEnum) return Kind.Enum;
            if (pt == typeof(string)) return Kind.Str;
            if (pt == typeof(UnityEngine.Vector3)) return Kind.Vec3;
            if (pt == typeof(UnitObject)) return Kind.Units;
            if (pt == typeof(Il2CppSystem.Threading.CancellationTokenSource)) return Kind.Cts;
            if (pt == typeof(Il2CppSystem.IDisposable)) return Kind.Disposable;
            return null;
        }

        private static string Key(NodeCore n) => n.ID + "/" + n.SubID;
        private static string SafeId(NodeCore n) { try { return Key(n); } catch { return "?"; } }
        private static string Inv(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    }
}
