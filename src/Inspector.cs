using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MelonLoader;
using Il2CppDefaultEcs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;

namespace BASaveGame
{
    /// <summary>
    /// Read-only battle inspector (recon PoC). Proves our hooks bind to the live
    /// game and censuses the simulation ECS world so we can design the save schema.
    ///
    /// F7 = world summary (GameController state + entity counts).
    /// F8 = component census over GameContext (entities-per-component-type).
    ///
    /// Writes a report to <SaveDir>\inspector_*.txt and mirrors to the MelonLoader log.
    /// Nothing is written to the game; no external I/O.
    /// </summary>
    internal static class Inspector
    {
        // Cached reflection handles for driving DefaultEcs generics.
        private static MethodInfo _withDef;    // EntityQueryBuilder.With<T>()
        private static MethodInfo _getDef;     // Entity.Get<T>()
        private static MethodInfo _hasDef;     // Entity.Has<T>()
        private static MethodInfo _getAllDef;  // World.GetAll<T>()
        private static bool _genericProbeFailed;

        // Save-critical components to read full schema for (via safe GetAll).
        private static readonly string[] SchemaComponents =
        {
            "Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent",
            "Il2CppBrokenArrow.Client.Ecs.Spawn.Components.UnitModelInfoComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.UnitBattleInfoSetComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.TransformComponent",
            "Il2CppBrokenArrow.Shared.Ecs.AltitudeComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.AmmunitionBoxComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.CounterMeasuresComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.CommandsComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.Movement.SpeedComponent",
            "Il2CppBrokenArrow.Client.Ecs.Transports.Components.LoadedComponent",
            "Il2CppBrokenArrow.Client.Ecs.Transports.Components.CargoContainerComponent",
        };

        // Components whose field values we dump in full for sampled units.
        private static readonly string[] CuratedUnitComponents =
        {
            "Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.TransformComponent",
            "Il2CppBrokenArrow.Shared.Ecs.AltitudeComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.HealthComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.Movement.SpeedComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.Movement.MaxSpeedComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.Movement.MovingComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.CommandsComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.WeaponComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.TurretComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.AmmunitionBoxComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.CounterMeasuresComponent",
            "Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.StressComponent",
            "Il2CppBrokenArrow.Client.Ecs.Transports.Components.LoadedComponent",
            "Il2CppBrokenArrow.Shared.Ecs.Components.UnitGroundVehicleFlag",
            "Il2CppBrokenArrow.Shared.Ecs.Components.UnitInfantryFlag",
            "Il2CppBrokenArrow.Client.Ecs.Infantry.Components.SoldierComponent",
        };

        internal static void WorldSummary()
        {
            var sb = new StringBuilder();
            Line(sb, "===== BA Inspector: WORLD SUMMARY =====");
            Line(sb, "time: " + DateTime.Now.ToString("s"));

            if (!GameController.IsInstanceAlive)
            {
                Line(sb, "GameController.IsInstanceAlive = false (not in a battle). Load a skirmish/PvE and try again.");
                Flush(sb, "summary");
                return;
            }

            GameController gc = GameController.Instance;
            Line(sb, "GameController.Instance = OK");
            SafeLine(sb, "CurrentMapName", () => gc.CurrentMapName);
            SafeLine(sb, "GameTime", () => gc.GameTime.ToString());
            SafeLine(sb, "TimeScale", () => gc.TimeScale.ToString());
            SafeLine(sb, "IsEnabledEcs", () => gc.IsEnabledEcs.ToString());
            SafeLine(sb, "LoadStatus", () => gc.LoadStatus.ToString());

            World world = null;
            try { world = gc.GameContext; } catch (Exception e) { Line(sb, "GameContext threw: " + e.Message); }
            if (world == null) { Line(sb, "GameContext == null"); Flush(sb, "summary"); return; }

            Line(sb, "GameContext (sim World) = OK");
            SafeLine(sb, "  World.MaxCapacity", () => world.MaxCapacity.ToString());
            SafeLine(sb, "  World.LastEntityId", () => world.LastEntityId.ToString());
            SafeLine(sb, "  enabled entities", () => CountAll(world).ToString());
            SafeLine(sb, "  disabled entities", () => CountDisabled(world).ToString());

            try
            {
                World ui = gc.UiContext;
                if (ui != null) SafeLine(sb, "  UiContext entities", () => CountAll(ui).ToString());
            }
            catch { /* UiContext optional */ }

            Flush(sb, "summary");
        }

        internal static void ComponentCensus()
        {
            var sb = new StringBuilder();
            Line(sb, "===== BA Inspector: COMPONENT CENSUS =====");
            Line(sb, "time: " + DateTime.Now.ToString("s"));

            if (!GameController.IsInstanceAlive) { Line(sb, "Not in a battle."); Flush(sb, "census"); return; }
            World world;
            try { world = GameController.Instance.GameContext; }
            catch (Exception e) { Line(sb, "GameContext threw: " + e.Message); Flush(sb, "census"); return; }
            if (world == null) { Line(sb, "GameContext == null"); Flush(sb, "census"); return; }

            int total = CountAll(world);
            Line(sb, "enabled entities: " + total);

            List<Type> candidates = ComponentCandidateTypes();
            Line(sb, "candidate component types probed: " + candidates.Count);

            if (!EnsureGenericProbe())
            {
                Line(sb, "!! Generic reflection probe unavailable in this Il2CppInterop build; census skipped.");
                Line(sb, "   (World summary + counts still valid; value-dump PoC will use a different path.)");
                Flush(sb, "census");
                return;
            }

            var results = new List<KeyValuePair<string, int>>();
            int errors = 0;
            foreach (Type t in candidates)
            {
                try
                {
                    int c = CountWith(world, t);
                    if (c > 0) results.Add(new KeyValuePair<string, int>(t.FullName, c));
                }
                catch { errors++; }
            }

            results.Sort((a, b) => b.Value.CompareTo(a.Value));
            Line(sb, "component types PRESENT (entities-with): " + results.Count + "   (probe errors: " + errors + ")");
            Line(sb, "----------------------------------------------");
            foreach (var kv in results)
                Line(sb, string.Format("{0,6}  {1}", kv.Value, kv.Key));

            Flush(sb, "census");
        }

        internal static void UnitDump()
        {
            var sb = new StringBuilder();
            Line(sb, "===== BA Inspector: UNIT VALUE DUMP =====");
            Line(sb, "time: " + DateTime.Now.ToString("s"));

            if (!GameController.IsInstanceAlive) { Line(sb, "Not in a battle."); Flush(sb, "units"); return; }
            World world;
            try { world = GameController.Instance.GameContext; }
            catch (Exception e) { Line(sb, "GameContext threw: " + e.Message); Flush(sb, "units"); return; }
            if (world == null) { Line(sb, "GameContext == null"); Flush(sb, "units"); return; }

            if (!EnsureGenericProbe()) { Line(sb, "generic reflection unavailable; abort."); Flush(sb, "units"); return; }

            Assembly ba = typeof(GameController).Assembly;
            Type unitCompType = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent");
            if (unitCompType == null) { Line(sb, "UnitComponent type not found."); Flush(sb, "units"); return; }

            var unitEntities = EntitiesWith(world, unitCompType);
            Line(sb, "unit entities (UnitComponent): " + unitEntities.Count);
            if (unitEntities.Count >= 2)
                Line(sb, "distinct ids sample: " + SafeEntityId(unitEntities[0]) + " | " +
                                                   SafeEntityId(unitEntities[1]) + " | " +
                                                   SafeEntityId(unitEntities[unitEntities.Count - 1]));
            Line(sb, "");

            // Per-unit component archetype (Has<T> is safe). First few units only.
            List<Type> present = CandidatesPresent(world, out int _);
            int listed = 0;
            foreach (Entity e in unitEntities)
            {
                if (listed++ >= 5) break;
                var has = new List<string>();
                foreach (Type t in present)
                    try { if (EntityHas(e, t)) has.Add(ShortName(t)); } catch { }
                Line(sb, "unit " + SafeEntityId(e) + " components(" + has.Count + "): " + string.Join(", ", has));
            }
            Line(sb, "");

            // VALUE READS via World.GetAll<T>() -> ToArray() (dense component copies; no byref).
            // World-wide (not unit-filtered) but reveal real field+property values + schema safely.
            Line(sb, "===== component schema samples (GetAll<T>, fields + properties) =====");
            foreach (string n in SchemaComponents)
                DumpGetAll(sb, world, ba, n, 2);

            Flush(sb, "units");

            // Association experiment: does a TYPED per-entity Get<T> marshal correctly
            // (unlike the reflection Get<T>, which returned byref garbage / crashed)?
            // Writes incrementally to live_typedget.txt so a crash still shows progress.
            TypedGetExperiment(world, unitEntities, ba);
        }

        // Generic GetAll<T> reader via reflection (T resolved from name). GetAll returns
        // component COPIES (no byref), so reading fields AND properties here is safe.
        private static void DumpGetAll(StringBuilder sb, World world, Assembly ba, string typeName, int maxElems)
        {
            string shortn = typeName.Replace("Il2CppBrokenArrow.", "");
            try
            {
                Type t = ba.GetType(typeName);
                if (t == null) { Line(sb, "[GetAll " + shortn + "] type not found"); return; }
                object span = _getAllDef.MakeGenericMethod(t).Invoke(world, null);
                object arr = span.GetType().GetMethod("ToArray", Type.EmptyTypes).Invoke(span, null);
                int len = (int)arr.GetType().GetProperty("Length").GetValue(arr);
                MethodInfo getItem = arr.GetType().GetMethod("get_Item", new[] { typeof(int) });
                Line(sb, "[GetAll " + shortn + "] length=" + len);
                int nn = Math.Min(len, maxElems);
                for (int i = 0; i < nn; i++)
                {
                    object item = getItem.Invoke(arr, new object[] { i });
                    Line(sb, "  [" + i + "] " + (item == null ? "null" : ""));
                    if (item != null) DumpMembers(sb, item, "      ");
                }
            }
            catch (Exception ex) { Line(sb, "[GetAll " + shortn + "] threw: " + ex.GetType().Name + " " + ex.Message); }
        }

        private static void TypedGetExperiment(World world, List<Entity> units, Assembly ba)
        {
            AppendLive("typedget", "==== typed Get<T> experiment @ " + DateTime.Now.ToString("s") + " ====");
            Type deadT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.DeadComponent");

            // Pick the first ALIVE unit (has Health, not Dead) to reduce edge-case risk.
            Entity pick = default; bool found = false;
            foreach (Entity e in units)
            {
                bool dead = false;
                try { if (deadT != null) dead = EntityHas(e, deadT); } catch { }
                if (!dead) { pick = e; found = true; break; }
            }
            if (!found) { AppendLive("typedget", "no alive unit found"); return; }
            AppendLive("typedget", "picked unit " + SafeEntityId(pick));

            AppendLive("typedget", "step1: calling typed pick.Get<HealthComponent>() ...");
            var hc = pick.Get<Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.HealthComponent>();
            AppendLive("typedget", "step2: returned; reading _health ...");
            AppendLive("typedget", "  _health=" + hc._health + " MaxHealth=" + hc.MaxHealth + " Immortal=" + hc.Immortal);
            AppendLive("typedget", "SUCCESS: typed Get<HealthComponent> read valid data (health should be sane, e.g. 100).");
        }

        // ---- entity counting (no generics needed) ----

        private static int CountAll(World world)
        {
            var set = world.GetEntities().AsSet();
            try { return set.Count; }
            finally { try { set.Dispose(); } catch { } }
        }

        private static int CountDisabled(World world)
        {
            var set = world.GetDisabledEntities().AsSet();
            try { return set.Count; }
            finally { try { set.Dispose(); } catch { } }
        }

        // ---- component census via EntityQueryBuilder.With<T>().AsSet().Count ----

        private static bool EnsureGenericProbe()
        {
            if (_withDef != null) return true;
            if (_genericProbeFailed) return false;
            try
            {
                _withDef = typeof(EntityQueryBuilder).GetMethods()
                    .FirstOrDefault(m => m.Name == "With"
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 0);
                _getDef = typeof(Entity).GetMethods()
                    .FirstOrDefault(m => m.Name == "Get"
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 0);
                _hasDef = typeof(Entity).GetMethods()
                    .FirstOrDefault(m => m.Name == "Has"
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 0);
                _getAllDef = typeof(World).GetMethods()
                    .FirstOrDefault(m => m.Name == "GetAll"
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 0);
                if (_withDef == null) { _genericProbeFailed = true; return false; }
                return true;
            }
            catch { _genericProbeFailed = true; return false; }
        }

        // ---- generic entity access via reflection over Il2CppInterop ----

        private static List<Entity> EntitiesWith(World world, Type componentType)
        {
            var result = new List<Entity>();
            var builder = world.GetEntities();
            var builder2 = (EntityQueryBuilder)_withDef.MakeGenericMethod(componentType).Invoke(builder, null);
            var set = builder2.AsSet();
            try
            {
                // NOTE: ReadOnlySpan<T> indexer (get_Item -> byref T) does NOT marshal under
                // Il2CppInterop (returns the same element repeatedly). ToArray() copies into a
                // real Il2CppStructArray<Entity>, which indexes correctly.
                var arr = set.GetEntities().ToArray();
                int len = arr.Length;
                for (int i = 0; i < len; i++)
                    result.Add(arr[i]);
            }
            finally { try { set.Dispose(); } catch { } }
            return result;
        }

        private static bool EntityHas(Entity e, Type componentType)
        {
            object boxed = e; // box the struct so reflection can invoke instance method
            return (bool)_hasDef.MakeGenericMethod(componentType).Invoke(boxed, null);
        }

        private static object EntityGet(Entity e, Type componentType)
        {
            object boxed = e;
            return _getDef.MakeGenericMethod(componentType).Invoke(boxed, null);
        }

        private static List<Type> CandidatesPresent(World world, out int total)
        {
            total = CountAll(world);
            var present = new List<Type>();
            foreach (Type t in ComponentCandidateTypes())
            {
                try { if (CountWith(world, t) > 0) present.Add(t); }
                catch { }
            }
            return present;
        }

        // ---- value formatting ----

        private static void DumpMembers(StringBuilder sb, object comp, string indent, bool fieldsOnly = false)
        {
            Type ct = comp.GetType();
            const BindingFlags BF = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            bool any = false;

            foreach (FieldInfo f in ct.GetFields(BF))
            {
                any = true;
                Line(sb, indent + f.Name + " = " + Fmt(SafeVal(() => f.GetValue(comp))));
            }
            if (fieldsOnly) { if (!any) Line(sb, indent + "(no declared public fields; type=" + ct.FullName + ")"); return; }
            foreach (PropertyInfo p in ct.GetProperties(BF))
            {
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                string n = p.Name;
                if (n == "Pointer" || n == "WasCollected" || n == "ObjectClass") continue;
                any = true;
                Line(sb, indent + n + " = " + Fmt(SafeVal(() => p.GetValue(comp))));
            }
            if (!any) Line(sb, indent + "(no declared public members; type=" + ct.FullName + ")");
        }

        private static object SafeVal(Func<object> f) { try { return f(); } catch (Exception e) { return "<err:" + e.Message + ">"; } }

        private static string Fmt(object v)
        {
            if (v == null) return "null";
            Type t = v.GetType();
            if (t.IsPrimitive || v is string || v is decimal) return v.ToString();
            if (t.IsEnum) return v.ToString();
            string tn = t.Name;
            if (tn == "Vector3") return FmtProps(v, "x", "y", "z");
            if (tn == "Vector2") return FmtProps(v, "x", "y");
            if (tn == "Quaternion") return FmtProps(v, "x", "y", "z", "w");
            // Fall back to ToString if it looks meaningful, else the type name.
            string s = SafeVal(() => v.ToString()) as string ?? "";
            if (!string.IsNullOrEmpty(s) && s != t.FullName && !s.StartsWith("Il2Cpp")) return s + "  <" + tn + ">";
            return "<" + tn + ">";
        }

        private static string FmtProps(object v, params string[] names)
        {
            var parts = new List<string>();
            Type t = v.GetType();
            foreach (string n in names)
            {
                object val = null;
                PropertyInfo p = t.GetProperty(n);
                if (p != null) val = SafeVal(() => p.GetValue(v));
                else { FieldInfo f = t.GetField(n); if (f != null) val = SafeVal(() => f.GetValue(v)); }
                parts.Add(n + "=" + (val == null ? "?" : val.ToString()));
            }
            return "(" + string.Join(", ", parts) + ")";
        }

        private static string ShortName(Type t)
        {
            string n = t.FullName ?? t.Name;
            return n.Replace("Il2CppBrokenArrow.", "");
        }

        private static string SafeEntityId(Entity e)
        {
            try { return "W" + e.WorldId + ":E" + e.EntityId + ":v" + e.Version; }
            catch { return "entity"; }
        }

        private static int CountWith(World world, Type componentType)
        {
            var builder = world.GetEntities();
            MethodInfo withT = _withDef.MakeGenericMethod(componentType);
            var builder2 = (EntityQueryBuilder)withT.Invoke(builder, null);
            var set = builder2.AsSet();
            try { return set.Count; }
            finally { try { set.Dispose(); } catch { } }
        }

        private static List<Type> ComponentCandidateTypes()
        {
            var list = new List<Type>();
            try
            {
                Assembly ba = typeof(GameController).Assembly;
                foreach (Type t in ba.GetTypes())
                {
                    if (t.IsGenericTypeDefinition || t.IsInterface || t.IsEnum) continue;
                    if (t.IsAbstract) continue;
                    string ns = t.Namespace ?? "";
                    string nm = t.Name ?? "";
                    bool looksLikeComponent =
                        nm.EndsWith("Component", StringComparison.Ordinal) ||
                        ns.IndexOf(".Components", StringComparison.Ordinal) >= 0;
                    if (looksLikeComponent && ns.IndexOf("Ecs", StringComparison.Ordinal) >= 0)
                        list.Add(t);
                }
            }
            catch (Exception e) { MelonLogger.Warning("candidate scan failed: " + e.Message); }
            return list;
        }

        // ---- output helpers ----

        private static void Line(StringBuilder sb, string s) { sb.AppendLine(s); MelonLogger.Msg(s); }

        // Immediate, crash-durable logging: append one line straight to disk + log.
        private static void AppendLive(string tag, string s)
        {
            MelonLogger.Msg("[" + tag + "] " + s);
            try
            {
                if (!string.IsNullOrEmpty(SaveMod.SaveDir))
                    File.AppendAllText(Path.Combine(SaveMod.SaveDir, "live_" + tag + ".txt"), s + Environment.NewLine);
            }
            catch { }
        }

        private static void SafeLine(StringBuilder sb, string label, Func<string> get)
        {
            try { Line(sb, "  " + label + " = " + get()); }
            catch (Exception e) { Line(sb, "  " + label + " threw: " + e.Message); }
        }

        private static void Flush(StringBuilder sb, string tag)
        {
            try
            {
                if (string.IsNullOrEmpty(SaveMod.SaveDir)) return;
                string path = Path.Combine(SaveMod.SaveDir,
                    "inspector_" + tag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, sb.ToString());
                MelonLogger.Msg("Inspector report written: " + path);
            }
            catch (Exception e) { MelonLogger.Warning("could not write report: " + e.Message); }
        }
    }
}
