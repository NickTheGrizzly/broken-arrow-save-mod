using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppBrokenArrow.ScriptEngine.Core;             // NodeCore, NodeLogic, PortCore
using Il2CppBrokenArrow.ScriptEngine.Loader;           // NodeController

namespace BASaveGame
{
    /// <summary>
    /// RECON (script-state save): dump the RUNNING mission script graph so we can see what state a
    /// native save would have to capture. F4 writes Saves\script_dump_*.txt:
    ///   - script variables (NodeController.CustomVars) and the static MissionStorage,
    ///   - node activation history recorded since the battle started (OnActivateNode/OnDeactivateNode),
    ///   - every node: id, type, name, Status, ports (signal received / connections / value) and the
    ///     node logic's own fields (only field-backed properties, so no getter side effects).
    /// Written line by line with flush, so a native crash still leaves everything up to that point.
    /// </summary>
    internal static class ScriptDump
    {
        // ---------- activation recorder (delegate append, no Harmony) ----------
        private sealed class Activity { public int activations, deactivations; public float first = -1, last = -1; }
        private static readonly Dictionary<int, Activity> _activity = new Dictionary<int, Activity>();
        private static readonly List<string> _timeline = new List<string>();
        private static IntPtr _hookedNc;
        private static float _nextCheck;
        private static Il2CppSystem.Action<NodeCore> _onAct, _onDeact;   // kept alive for the native side

        internal static NodeController CurrentNodeController()
        {
            if (!GameController.IsInstanceAlive) return null;
            var sc = GameController.Instance?.GetScenarioController;
            return sc?.GetNodeController;
        }

        /// <summary>Called every frame from OnUpdate: attaches the recorder once per battle.</summary>
        internal static void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + 0.25f;
            try
            {
                var nc = CurrentNodeController();
                if (nc == null || nc.Pointer == _hookedNc) return;
                _hookedNc = nc.Pointer;
                _activity.Clear();
                _timeline.Clear();
                _onAct ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<NodeCore>>(
                    new Action<NodeCore>(n => Record(n, true)));
                _onDeact ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<NodeCore>>(
                    new Action<NodeCore>(n => Record(n, false)));
                nc.add_OnActivateNode(_onAct);
                nc.add_OnDeactivateNode(_onDeact);
                MelonLogger.Msg("[script] recording mission-script node activity for this battle (F4 = dump).");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[script] recorder unavailable: " + e.Message);
                try { _hookedNc = CurrentNodeController()?.Pointer ?? IntPtr.Zero; } catch { }
            }
        }

        /// <summary>Game time of the node's last activation this battle (or carried over from a load); -1 = never.</summary>
        internal static float LastActivation(int nodeId) =>
            _activity.TryGetValue(nodeId, out var a) ? a.last : -1f;

        /// <summary>After a load: carry the saved activation time forward so the next save can compute timers.</summary>
        internal static void SeedActivation(int nodeId, float gameTime)
        {
            if (!_activity.TryGetValue(nodeId, out var a)) _activity[nodeId] = a = new Activity();
            if (a.first < 0) a.first = gameTime;
            a.last = gameTime;
        }

        private static void Record(NodeCore n, bool activated)
        {
            try
            {
                int id = n.ID;
                float t = GameTime();
                if (!_activity.TryGetValue(id, out var a)) _activity[id] = a = new Activity();
                if (activated) { a.activations++; if (a.first < 0) a.first = t; } else a.deactivations++;
                a.last = t;
                if (_timeline.Count < 5000)
                    _timeline.Add(t.ToString("0.0") + "s " + (activated ? "+" : "-") + id + " " + SafeTypeName(n.Logic));
            }
            catch { }
        }

        internal static float GameTime()
        {
            try { return GameController.Instance._ecsLoader.GameTime; } catch { return UnityEngine.Time.time; }
        }

        // ---------- F4 dump ----------
        internal static void Dump()
        {
            string path = Path.Combine(SaveMod.SaveDir, "script_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            MelonLogger.Msg("[script] dumping mission script to " + path);
            using var w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            try
            {
                var nc = CurrentNodeController();
                if (nc == null) { w.WriteLine("no NodeController (not in a battle?)"); return; }
                w.WriteLine("==== mission script dump @ " + DateTime.Now.ToString("s") + "  gameTime=" + GameTime().ToString("0.0"));
                w.WriteLine("IsDataLoaded=" + nc.IsDataLoaded + " StepMode=" + nc.StepMode + " DEBUG=" + NodeController.DEBUG);

                DumpVariables(w, nc);

                var list = nc._nodeList;
                int count = list?.Count ?? 0;
                var byStatus = new Dictionary<string, int>();
                for (int i = 0; i < count; i++)
                {
                    string s = "?";
                    try { s = list[i].Status.ToString(); } catch { }
                    byStatus[s] = byStatus.TryGetValue(s, out int c) ? c + 1 : 1;
                }
                var sb = new StringBuilder();
                foreach (var kv in byStatus) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                w.WriteLine("nodes: " + count + "  by status: " + sb);

                w.WriteLine();
                w.WriteLine("---- activation recorder: " + _activity.Count + " distinct nodes, " + _timeline.Count + " events ----");
                foreach (string line in _timeline) w.WriteLine("  " + line);

                w.WriteLine();
                w.WriteLine("---- nodes ----");
                for (int i = 0; i < count; i++)
                {
                    try { DumpNode(w, list[i]); }
                    catch (Exception e) { w.WriteLine("  node[" + i + "] threw: " + e.Message); }
                }
                w.WriteLine("==== end ====");
            }
            catch (Exception e) { w.WriteLine("DUMP THREW: " + e); }
            MelonLogger.Msg("[script] dump done.");
        }

        private static void DumpVariables(StreamWriter w, NodeController nc)
        {
            w.WriteLine();
            w.WriteLine("---- CustomVars ----");
            try
            {
                var dict = nc.CustomVars?._customVariablesDict;
                if (dict == null) w.WriteLine("  (null)");
                else
                {
                    var en = dict.GetEnumerator();
                    while (en.MoveNext()) w.WriteLine("  " + en.Current.Key + " = " + en.Current.Value);
                }
            }
            catch (Exception e) { w.WriteLine("  threw: " + e.Message); }

            w.WriteLine("---- MissionStorage (static) ----");
            try
            {
                var ms = NodeController.MissionStorage;
                var vars = ms?._variables;
                if (vars == null) w.WriteLine("  (null)");
                else
                {
                    var en = vars.GetEnumerator();
                    while (en.MoveNext())
                    {
                        string v;
                        try { v = Fmt(en.Current.Value?.Value); } catch (Exception e) { v = "<" + e.Message + ">"; }
                        w.WriteLine("  " + en.Current.Key + " = " + v);
                    }
                }
            }
            catch (Exception e) { w.WriteLine("  threw: " + e.Message); }
        }

        private static void DumpNode(StreamWriter w, NodeCore n)
        {
            if (n == null) { w.WriteLine("  (null node)"); return; }
            var logic = n.Logic;
            string name = "";
            try { name = n.Info?.Data?.KeyName ?? ""; } catch { }
            bool startup = false;
            try { startup = n.Info != null && n.Info.Startup; } catch { }
            _activity.TryGetValue(n.ID, out var act);

            w.WriteLine();
            w.WriteLine("#" + n.ID + (n.SubID != 0 ? "/" + n.SubID : "") + " " + SafeTypeName(logic) +
                        (name.Length > 0 ? " '" + name + "'" : "") + " status=" + n.Status +
                        (startup ? " STARTUP" : "") +
                        (act != null ? " act=" + act.activations + " deact=" + act.deactivations +
                                       " first=" + act.first.ToString("0.0") + " last=" + act.last.ToString("0.0") : ""));
            DumpPorts(w, "in  ", n.Inputs, false);
            DumpPorts(w, "out ", n.Outputs, true);
            DumpPorts(w, "prop", n.Props, true);
            DumpLogicFields(w, logic);
        }

        private static void DumpPorts(StreamWriter w, string kind, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PortCore> ports, bool withValue)
        {
            if (ports == null) return;
            for (int i = 0; i < ports.Length; i++)
            {
                var p = ports[i];
                if (p == null) continue;
                string pname = "?";
                try { pname = p.Info?.Name ?? "?"; } catch { }
                string line = "    " + kind + " " + pname + " conn=" + p.GetConnectedCount +
                              (p.SignalWasReceived ? " SIGNALED" : "");
                if (withValue)
                {
                    try { line += " = " + Fmt(p.GetFieldValue()); } catch (Exception e) { line += " = <" + e.Message + ">"; }
                }
                w.WriteLine(line);
            }
        }

        // Only Il2CppInterop field accessors (they have a matching NativeFieldInfoPtr_<name> static),
        // declared on the concrete logic type and its bases, stopping at NodeLogic's own fields.
        private static readonly Dictionary<string, List<PropertyInfo>> _fieldProps = new Dictionary<string, List<PropertyInfo>>();

        private static void DumpLogicFields(StreamWriter w, NodeLogic logic)
        {
            if (logic == null) return;
            Type t = ManagedTypeOf(logic);
            if (t == null) { w.WriteLine("    (no managed type for " + SafeTypeName(logic) + ")"); return; }
            object typed;
            try { typed = Activator.CreateInstance(t, logic.Pointer); }
            catch (Exception e) { w.WriteLine("    (wrap failed: " + e.Message + ")"); return; }

            foreach (var pi in FieldProps(t))
            {
                string v;
                try { v = Fmt(pi.GetValue(typed)); }
                catch (Exception e) { v = "<" + (e.InnerException ?? e).Message + ">"; }
                w.WriteLine("    f " + pi.DeclaringType.Name + "." + pi.Name + " = " + v);
            }
        }

        private static List<PropertyInfo> FieldProps(Type t)
        {
            if (_fieldProps.TryGetValue(t.FullName, out var cached)) return cached;
            var result = new List<PropertyInfo>();
            for (Type cur = t; cur != null && cur != typeof(Il2CppSystem.Object) && cur != typeof(Il2CppObjectBase); cur = cur.BaseType)
            {
                foreach (var pi in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                    if (cur.GetField("NativeFieldInfoPtr_" + pi.Name, BindingFlags.NonPublic | BindingFlags.Static) == null) continue;
                    result.Add(pi);
                }
            }
            _fieldProps[t.FullName] = result;
            return result;
        }

        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        private static readonly Assembly _gameAsm = typeof(GameController).Assembly;

        internal static Type ManagedTypeOf(Il2CppObjectBase o)
        {
            string full;
            try { full = o.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName; } catch { return null; }
            if (_typeCache.TryGetValue(full, out var t)) return t;
            t = _gameAsm.GetType("Il2Cpp" + full) ?? _gameAsm.GetType("Il2Cpp" + full.Replace('/', '+'));
            if (t == null)
            {
                int plus = full.LastIndexOf('+');
                if (plus > 0) t = _gameAsm.GetType("Il2Cpp" + full.Substring(0, plus) + "+" + full.Substring(plus + 1));
            }
            _typeCache[full] = t;
            return t;
        }

        internal static string SafeTypeName(Il2CppObjectBase o)
        {
            if (o == null) return "null";
            try
            {
                string full = o.Cast<Il2CppSystem.Object>().GetIl2CppType().FullName;
                const string pre = "BrokenArrow.ScriptEngine.";
                return full.StartsWith(pre, StringComparison.Ordinal) ? full.Substring(pre.Length) : full;
            }
            catch { return o.GetType().Name; }
        }

        /// <summary>Short, safe string for any value: primitives, strings, collections (count), il2cpp objects.</summary>
        private static string Fmt(object v)
        {
            if (v == null) return "null";
            switch (v)
            {
                case string s: return "\"" + (s.Length > 120 ? s.Substring(0, 120) + "..." : s) + "\"";
                case float f: return f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                case double d: return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                case bool or int or long or short or byte or uint or Enum: return v.ToString();
            }
            if (v is Il2CppObjectBase ob)
            {
                string tn = SafeTypeName(ob);
                try
                {
                    var cnt = ob.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                    if (cnt != null) return tn + "[" + cnt.GetValue(ob) + "]";
                    var len = ob.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                    if (len != null) return tn + "[" + len.GetValue(ob) + "]";
                }
                catch { }
                // Boxed primitives come back as Il2CppSystem.Object: unbox the common ones.
                try
                {
                    var io = ob.Cast<Il2CppSystem.Object>();
                    string s = io.ToString();
                    if (s != null && s.Length > 120) s = s.Substring(0, 120) + "...";
                    return s == null || s == tn || s.EndsWith(tn, StringComparison.Ordinal) ? tn : tn + " {" + s + "}";
                }
                catch { return tn; }
            }
            string str = v.ToString();
            return str.Length > 120 ? str.Substring(0, 120) + "..." : str;
        }
    }
}
