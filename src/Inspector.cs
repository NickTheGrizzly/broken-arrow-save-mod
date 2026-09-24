using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MelonLoader;
using Il2CppDefaultEcs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;
using Il2CppBrokenArrow.Client.Ecs.BattleSystem;             // AmmunitionContainer
using Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components;  // AmmunitionBoxComponent
using Il2CppInterop.Runtime.InteropTypes.Arrays;

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
        private static MethodInfo _getAllDef;         // World.GetAll<T>()
        private static MethodInfo _getComponentsDef;  // World.GetComponents<T>()
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
        }

        // Pure-data components safe to serialize (no Unity object refs / delegates).
        // Matched by simple type-name suffix against Il2CppSystem.Type.FullName.
        private static readonly HashSet<string> SerWhitelist = new HashSet<string>(StringComparer.Ordinal)
        {
            "TransformComponent", "HealthComponent", "AltitudeComponent",
            "SpeedComponent", "MaxSpeedComponent", "AccelerationComponent",
            "RotationSpeedComponent", "MaxRotationSpeedComponent", "MovingComponent",
            "StaticPositionComponent", "StaticRotationComponent",
            "TerrainTypeComponent", "VerticalOrientationComponent",
        };

        // Filter predicate handed to the DefaultEcs serializer. Receives the il2cpp
        // System.Type of each component; return true to include it.
        private static bool SerFilter(Il2CppSystem.Type t)
        {
            try
            {
                string full = t.FullName ?? "";
                int dot = full.LastIndexOf('.');
                string simple = dot >= 0 ? full.Substring(dot + 1) : full;
                bool ok = SerWhitelist.Contains(simple);
                if (ok) AppendLive("ser", "  include: " + full);
                return ok;
            }
            catch { return false; }
        }

        // F10: test DefaultEcs TextSerializer on a few live units with a pure-data filter.
        // If this works, its output IS (essentially) our save format and proves save/load.
        internal static void SerializerTest()
        {
            AppendLive("ser", "==== serializer test @ " + DateTime.Now.ToString("s") + " ====");
            if (!GameController.IsInstanceAlive) { AppendLive("ser", "not in battle"); return; }
            World world;
            try { world = GameController.Instance.GameContext; }
            catch (Exception e) { AppendLive("ser", "GameContext threw: " + e.Message); return; }
            if (world == null) { AppendLive("ser", "GameContext null"); return; }
            if (!EnsureGenericProbe()) { AppendLive("ser", "generic probe unavailable"); return; }

            Assembly ba = typeof(GameController).Assembly;
            Type unitT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent");
            Type deadT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.DeadComponent");

            List<Entity> units = EntitiesWith(world, unitT);
            AppendLive("ser", "unit entities: " + units.Count);
            if (units.Count == 0) return;

            try
            {
                AppendLive("ser", "step1: building filter delegate...");
                Func<Il2CppSystem.Type, bool> managed = SerFilter;
                var pred = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Predicate<Il2CppSystem.Type>>(managed);

                AppendLive("ser", "step2: creating TextSerializer(filter)...");
                var ser = new Il2CppDefaultEcs.Serialization.TextSerializer(pred);

                AppendLive("ser", "step3: collecting up to 3 alive units...");
                var list = new Il2CppSystem.Collections.Generic.List<Entity>();
                int added = 0;
                foreach (Entity e in units)
                {
                    bool dead = false;
                    try { if (deadT != null) dead = EntityHas(e, deadT); } catch { }
                    if (dead) continue;
                    list.Add(e);
                    AppendLive("ser", "  + " + SafeEntityId(e));
                    if (++added >= 3) break;
                }
                AppendLive("ser", "entities queued: " + list.Count);

                AppendLive("ser", "step4: serializing to MemoryStream...");
                var ms = new Il2CppSystem.IO.MemoryStream();
                var en = list.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Entity>>();
                ser.Serialize(ms, en);
                AppendLive("ser", "step5: serialized OK. bytes=" + ms.Length);

                var arr = ms.ToArray();
                var bytes = new byte[arr.Length];
                for (int i = 0; i < arr.Length; i++) bytes[i] = arr[i];
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                AppendLive("ser", "---- TEXT START (" + bytes.Length + " bytes) ----");
                foreach (string ln in text.Split('\n')) AppendLive("ser", ln.TrimEnd('\r'));
                AppendLive("ser", "---- TEXT END ----");
            }
            catch (Exception ex)
            {
                AppendLive("ser", "EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message);
                if (ex.InnerException != null) AppendLive("ser", "  inner: " + ex.InnerException.Message);
            }
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

        // ===== Universal per-entity component read via DefaultEcs pool mapping =====
        // Components<T> exposes _mapping (entityId -> dense index). The dense array itself
        // must be read via GetAll<T>().ToArray() — the raw _components indexer mis-marshals
        // large value-type structs (e.g. TransformComponent), while ToArray() copies correctly.
        // Cached per component type per UnitRecords() pass.
        private sealed class PoolView
        {
            public Il2CppStructArray<int> Mapping;
            public object Arr;         // Il2CppArrayBase<T> from ToArray()
            public MethodInfo GetItem; // Arr.get_Item(int)
            public int Len;
        }
        private static Dictionary<Type, PoolView> _pools;

        private static PoolView Pool(World world, Type t)
        {
            if (_pools.TryGetValue(t, out PoolView p)) return p;
            p = new PoolView();
            try
            {
                object comps = _getComponentsDef.MakeGenericMethod(t).Invoke(world, null); // Components<T>
                p.Mapping = (Il2CppStructArray<int>)comps.GetType().GetMethod("get__mapping").Invoke(comps, null);
                object span = _getAllDef.MakeGenericMethod(t).Invoke(world, null);          // Span<T>
                p.Arr = span.GetType().GetMethod("ToArray", Type.EmptyTypes).Invoke(span, null);
                p.Len = (int)p.Arr.GetType().GetProperty("Length").GetValue(p.Arr);
                p.GetItem = p.Arr.GetType().GetMethod("get_Item", new[] { typeof(int) });
            }
            catch { p.Mapping = null; }
            _pools[t] = p;
            return p;
        }

        private static object ReadComp(World world, Type t, int entityId)
        {
            PoolView p = Pool(world, t);
            if (p.Mapping == null || p.Arr == null) return null;
            if (entityId < 0 || entityId >= p.Mapping.Length) return null;
            int idx = p.Mapping[entityId];
            if (idx < 0 || idx >= p.Len) return null;
            return p.GetItem.Invoke(p.Arr, new object[] { idx });
        }

        private static object Call(object o, string getter)
        {
            if (o == null) return null;
            try { var m = o.GetType().GetMethod(getter, Type.EmptyTypes); return m == null ? null : m.Invoke(o, null); }
            catch (Exception e) { return "<err:" + e.Message + ">"; }
        }
        private static object FieldVal(object o, string field)
        {
            if (o == null) return null;
            try { var f = o.GetType().GetField(field); return f == null ? null : f.GetValue(o); }
            catch (Exception e) { return "<err:" + e.Message + ">"; }
        }

        // F11: assemble real per-unit save records using the mapping reader.
        internal static void UnitRecords()
        {
            var sb = new StringBuilder();
            Line(sb, "===== BA Inspector: UNIT RECORDS (mapping reader) =====");
            Line(sb, "time: " + DateTime.Now.ToString("s"));
            if (!GameController.IsInstanceAlive) { Line(sb, "not in battle"); Flush(sb, "records"); return; }
            World world;
            try { world = GameController.Instance.GameContext; }
            catch (Exception e) { Line(sb, "ctx: " + e.Message); Flush(sb, "records"); return; }
            if (world == null || !EnsureGenericProbe()) { Line(sb, "no world / no generics"); Flush(sb, "records"); return; }

            _pools = new Dictionary<Type, PoolView>();
            Assembly ba = typeof(GameController).Assembly;
            Type unitT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent");
            Type healthT = ba.GetType("Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.HealthComponent");
            Type transT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.TransformComponent");
            Type deadT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.DeadComponent");

            var units = EntitiesWith(world, unitT);
            Line(sb, "unit entities: " + units.Count);
            Line(sb, "");

            int shown = 0;
            foreach (Entity e in units)
            {
                int eid = e.EntityId;
                bool dead = false; try { dead = EntityHas(e, deadT); } catch { }

                object uc = null, hc = null, tc = null;
                try { uc = ReadComp(world, unitT, eid); } catch (Exception ex) { Line(sb, "  uc err: " + ex.Message); }
                try { hc = ReadComp(world, healthT, eid); } catch { }
                try { tc = ReadComp(world, transT, eid); } catch { }

                object owner = Call(Call(uc, "get_Owner"), "get_UID");
                object team = Call(Call(uc, "get_Owner"), "get_TeamSide");
                object ud = Call(uc, "get_UnitData");
                object typeId = Call(ud, "get_Id");
                object typeName = Call(ud, "get_Name");
                object hp = FieldVal(hc, "_health");
                object maxhp = FieldVal(hc, "MaxHealth");
                object pos = Fmt(Call(tc, "get_Position"));

                Line(sb, string.Format("#{0,-2} E{1,-5} dead={2,-5} owner={3} team={4} type={5}/{6} hp={7}/{8} pos={9}",
                    ++shown, eid, dead, owner, team, typeId, typeName, hp, maxhp, pos));

                if (shown >= 20) { Line(sb, "... (truncated at 20)"); break; }
            }
            Flush(sb, "records");
        }

        // ===== LOAD support: existence/location checks for a spawned entity =====

        /// <summary>Count of live UnitComponent entities (a spawn should bump this by 1). -1 on error.</summary>
        internal static int CountUnits()
        {
            try
            {
                var world = GameController.Instance.GameContext;
                if (world == null || !EnsureGenericProbe()) return -1;
                Assembly ba = typeof(GameController).Assembly;
                Type unitT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent");
                return CountWith(world, unitT);
            }
            catch { return -1; }
        }

        /// <summary>Describe one entity: does it carry UnitComponent / a model, its owner, and its
        /// world position (via the reliable UnitModelInfoComponent -> Unity transform path).</summary>
        internal static string DescribeEntity(Entity e)
        {
            try
            {
                var world = GameController.Instance.GameContext;
                if (world == null || !EnsureGenericProbe()) return "no world/generics";
                _pools = new Dictionary<Type, PoolView>();
                Assembly ba = typeof(GameController).Assembly;
                Type unitT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent");
                Type modelT = ba.GetType("Il2CppBrokenArrow.Client.Ecs.Spawn.Components.UnitModelInfoComponent");
                int eid = e.EntityId;
                // Identity + liveness first: a disposed/recycled entity reports Has<T>=false for
                // everything, which would otherwise look like "built without components".
                string alive = SafeVal(() => e.IsAlive) + "/" + SafeVal(() => e.IsAliveVersion);
                string ident = string.Format("E{0} W{1} v{2} (gameWorld W{3}) alive/aliveVer={4}",
                    eid, e.WorldId, e.Version, SafeVal(() => world.WorldId), alive);

                // Surface Has<T> failures instead of reporting them as false.
                string hasUnit, hasModel;
                bool unitOk = false, modelOk = false;
                try { unitOk = EntityHas(e, unitT); hasUnit = unitOk.ToString(); } catch (Exception ex) { hasUnit = "<err:" + ex.Message + ">"; }
                try { modelOk = EntityHas(e, modelT); hasModel = modelOk.ToString(); } catch (Exception ex) { hasModel = "<err:" + ex.Message + ">"; }

                string pos = "n/a";
                if (modelOk)
                {
                    object umi = ReadComp(world, modelT, eid);
                    object tr = Call(Call(Call(umi, "get_Data"), "get_PrefabRootScript"), "get_transform");
                    pos = Fmt(Call(tr, "get_position"));
                }
                object owner = null, typeId = null;
                if (unitOk)
                {
                    object uc = ReadComp(world, unitT, eid);
                    owner = Call(Call(uc, "get_Owner"), "get_UID");
                    typeId = Call(Call(uc, "get_UnitData"), "get_Id");
                }
                return string.Format("{0} hasUnit={1} hasModel={2} type={3} owner={4} pos={5}", ident, hasUnit, hasModel, typeId, owner, pos);
            }
            catch (Exception ex) { return "describe err: " + ex.Message; }
        }

        // ===== Ammo + options (shared by save and load) =====

        /// <summary>
        /// A unit's live ammo containers, keyed by ammunition id. AmmunitionBoxComponent is a
        /// reference-holding component (same kind as UnitComponent, which reads reliably), and
        /// AmmunitionContainer is a real class — so reads/writes through it hit the live unit.
        /// </summary>
        internal static Il2CppSystem.Collections.Generic.Dictionary<int, AmmunitionContainer> ReadAmmoBox(Entity e)
        {
            var world = GameController.Instance.GameContext;
            if (world == null || !EnsureGenericProbe()) return null;
            _pools = new Dictionary<Type, PoolView>();
            return AmmoBoxOf(world, e.EntityId);
        }

        private static Il2CppSystem.Collections.Generic.Dictionary<int, AmmunitionContainer> AmmoBoxOf(World world, int entityId)
        {
            var box = ReadComp(world, typeof(AmmunitionBoxComponent), entityId) as AmmunitionBoxComponent;
            return box?.AmmunitionBox;
        }

        /// <summary>(ammoId, container) pairs; explicit enumerator (interop dictionaries don't
        /// reliably bind to C# foreach).</summary>
        internal static List<KeyValuePair<int, AmmunitionContainer>> AmmoEntries(
            Il2CppSystem.Collections.Generic.Dictionary<int, AmmunitionContainer> dict)
        {
            var list = new List<KeyValuePair<int, AmmunitionContainer>>();
            if (dict == null) return list;
            var en = dict.GetEnumerator();
            while (en.MoveNext())
            {
                var kv = en.Current;
                list.Add(new KeyValuePair<int, AmmunitionContainer>(kv.Key, kv.Value));
            }
            return list;
        }

        // ===== Passengers / garrisons =====

        private const string CargoContainerTypeName = "Il2CppBrokenArrow.Client.Ecs.Transports.Components.CargoContainerComponent";
        private const string LoadedTypeName = "Il2CppBrokenArrow.Client.Ecs.Transports.Components.LoadedComponent";

        /// <summary>
        /// Occupant entity id -> the container entity it is inside (vehicle or building segment).
        /// Built from each CargoContainerComponent.UnitsInside (reference-holding component, reads
        /// reliably) — NOT from LoadedComponent, a large struct that mis-marshals.
        /// </summary>
        private static Dictionary<int, Entity> CargoMap(World world)
        {
            var map = new Dictionary<int, Entity>();
            Type cT = typeof(GameController).Assembly.GetType(CargoContainerTypeName);
            if (cT == null) return map;
            foreach (Entity c in EntitiesWith(world, cT))
            {
                try
                {
                    var cc = ReadComp(world, cT, c.EntityId) as Il2CppBrokenArrow.Client.Ecs.Transports.Components.CargoContainerComponent;
                    var inside = cc?.UnitsInside;
                    if (inside == null || inside.Count == 0) continue;
                    var arr = inside.ToArray();  // FastList.get_Item returns byref — use the array copy
                    for (int i = 0; i < arr.Length; i++) map[arr[i].EntityId] = c;
                }
                catch { }
            }
            return map;
        }

        /// <summary>Live cargo-container entities by id (to re-find a building segment on load).</summary>
        internal static Dictionary<int, Entity> ContainerEntities()
        {
            var result = new Dictionary<int, Entity>();
            var world = GameController.Instance.GameContext;
            if (world == null || !EnsureGenericProbe()) return result;
            Type cT = typeof(GameController).Assembly.GetType(CargoContainerTypeName);
            if (cT == null) return result;
            foreach (Entity c in EntitiesWith(world, cT)) result[c.EntityId] = c;
            return result;
        }

        internal static bool IsUnit(Entity e)
        {
            try { return EntityHas(e, typeof(GameController).Assembly.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent")); }
            catch { return false; }
        }

        internal static bool IsLoaded(Entity e)
        {
            try { return EntityHas(e, typeof(GameController).Assembly.GetType(LoadedTypeName)); }
            catch { return false; }
        }

        /// <summary>True if <paramref name="cargo"/> is in the container's UnitsInside list.</summary>
        internal static bool IsInside(Entity container, Entity cargo)
        {
            try
            {
                var world = GameController.Instance.GameContext;
                if (world == null || !EnsureGenericProbe()) return false;
                _pools = new Dictionary<Type, PoolView>();
                Type cT = typeof(GameController).Assembly.GetType(CargoContainerTypeName);
                var cc = ReadComp(world, cT, container.EntityId) as Il2CppBrokenArrow.Client.Ecs.Transports.Components.CargoContainerComponent;
                var inside = cc?.UnitsInside;
                if (inside == null || inside.Count == 0) return false;
                var arr = inside.ToArray();
                for (int i = 0; i < arr.Length; i++) if (arr[i].EntityId == cargo.EntityId) return true;
                return false;
            }
            catch { return false; }
        }

        // ===== SAVE: write a .basave of the current battle's living units =====
        internal static void WriteQuickSave()
        {
            if (!GameController.IsInstanceAlive) { MelonLogger.Warning("[save] not in a battle"); return; }
            World world;
            try { world = GameController.Instance.GameContext; }
            catch (Exception e) { MelonLogger.Error("[save] GameContext: " + e.Message); return; }
            if (world == null || !EnsureGenericProbe()) { MelonLogger.Error("[save] no world/generics"); return; }

            _pools = new Dictionary<Type, PoolView>();
            Assembly ba = typeof(GameController).Assembly;
            Type unitT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.UnitComponent");
            Type healthT = ba.GetType("Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.HealthComponent");
            Type transT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.TransformComponent");
            Type deadT = ba.GetType("Il2CppBrokenArrow.Shared.Ecs.Components.DeadComponent");
            Type modelT = ba.GetType("Il2CppBrokenArrow.Client.Ecs.Spawn.Components.UnitModelInfoComponent");

            string mapName = ""; float gameTime = 0f;
            try { mapName = GameController.Instance.CurrentMapName; } catch { }
            try { gameTime = GameController.Instance.GameTime; } catch { }

            var units = EntitiesWith(world, unitT);
            // Who is inside what (vehicle passengers, building garrisons). Pools must exist first.
            Dictionary<int, Entity> cargoMap;
            try { cargoMap = CargoMap(world); }
            catch (Exception ex) { cargoMap = new Dictionary<int, Entity>(); MelonLogger.Warning("[save] cargo map: " + ex.Message); }
            int passengers = 0;

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"saveVersion\": 3,\n");  // v2: per-unit skin, opts, ammo; v3: inUnit/inBld
            sb.Append("  \"gameVersion\": \"1.2.0\",\n");
            sb.Append("  \"savedUtc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
            sb.Append("  \"map\": \"").Append(Esc(mapName)).Append("\",\n");
            sb.Append("  \"gameTime\": ").Append(Inv(gameTime)).Append(",\n");
            sb.Append("  \"units\": [\n");

            int written = 0, skippedDead = 0;
            for (int u = 0; u < units.Count; u++)
            {
                Entity e = units[u];
                int eid = e.EntityId;
                bool dead = false; try { dead = EntityHas(e, deadT); } catch { }
                if (dead) { skippedDead++; continue; }

                object uc = ReadComp(world, unitT, eid);
                if (uc == null) continue;
                object ownerObj = Call(uc, "get_Owner");
                int owner = ToInt(Call(ownerObj, "get_UID"));
                string team = Convert.ToString(Call(ownerObj, "get_TeamSide"));
                object ud = Call(uc, "get_UnitData");
                int typeId = ToInt(Call(ud, "get_Id"));
                string typeName = Convert.ToString(Call(ud, "get_Name"));

                object hc = ReadComp(world, healthT, eid);
                float hp = ToFloat(FieldVal(hc, "_health"));
                float maxHp = ToFloat(FieldVal(hc, "MaxHealth"));

                // Position/rotation from the Unity Transform (large ECS structs mis-marshal;
                // UnitModelInfoComponent is a true ref type -> reliable -> GameObject transform).
                object umi = ReadComp(world, modelT, eid);
                object data = Call(umi, "get_Data");
                object prefab = Call(data, "get_PrefabRootScript");
                object tr = Call(prefab, "get_transform");
                object pos = Call(tr, "get_position");
                object rot = Call(tr, "get_rotation");

                // Loadout: option ids + skin, so the load spawns the same configuration (options
                // change max HP, weapons, etc.). Ammo: exact count per ammunition type.
                var optIds = new List<int>();
                int skin = -1;
                var unitData = ud as Il2CppBrokenArrow.DataBase.Models.Units;
                try
                {
                    if (unitData != null)
                    {
                        skin = unitData.CurrentSkinId;
                        var opts = unitData.CurrentOptions;
                        if (opts != null)
                            for (int k = 0; k < opts.Count; k++)
                                if (opts[k] != null) optIds.Add(opts[k].Id);
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[save] options for E" + eid + ": " + ex.Message); }

                var ammo = new List<string>();
                try
                {
                    foreach (var kv in AmmoEntries(AmmoBoxOf(world, eid)))
                        if (kv.Value != null)
                            ammo.Add("[" + kv.Key + ", " + kv.Value.AmmoQuantity.Value + "]");
                }
                catch (Exception ex) { MelonLogger.Warning("[save] ammo for E" + eid + ": " + ex.Message); }

                if (written++ > 0) sb.Append(",\n");
                sb.Append("    {");
                sb.Append("\"eid\": ").Append(eid);
                sb.Append(", \"owner\": ").Append(owner);
                sb.Append(", \"team\": \"").Append(Esc(team)).Append("\"");
                sb.Append(", \"typeId\": ").Append(typeId);
                sb.Append(", \"typeName\": \"").Append(Esc(typeName)).Append("\"");
                sb.Append(", \"hp\": ").Append(Inv(hp));
                sb.Append(", \"maxHp\": ").Append(Inv(maxHp));
                sb.Append(", \"pos\": [").Append(Inv(Num(pos, "x"))).Append(", ").Append(Inv(Num(pos, "y"))).Append(", ").Append(Inv(Num(pos, "z"))).Append("]");
                sb.Append(", \"rot\": [").Append(Inv(Num(rot, "x"))).Append(", ").Append(Inv(Num(rot, "y"))).Append(", ").Append(Inv(Num(rot, "z"))).Append(", ").Append(Inv(Num(rot, "w"))).Append("]");
                sb.Append(", \"skin\": ").Append(skin);
                sb.Append(", \"opts\": [").Append(string.Join(", ", optIds)).Append("]");
                sb.Append(", \"ammo\": [").Append(string.Join(", ", ammo)).Append("]");
                if (cargoMap.TryGetValue(eid, out Entity container))
                {
                    // A unit container refers to another saved unit by eid; anything else is a
                    // building segment, identified by its entity id (exact within one battle).
                    sb.Append(IsUnit(container) ? ", \"inUnit\": " : ", \"inBld\": ").Append(container.EntityId);
                    passengers++;
                }
                sb.Append("}");
            }
            sb.Append("\n  ]\n}\n");

            try
            {
                string path = Path.Combine(SaveMod.SaveDir, "quicksave.basave");
                File.WriteAllText(path, sb.ToString());
                MelonLogger.Msg("[save] wrote " + written + " units (" + passengers + " inside a vehicle/building, skipped " + skippedDead + " dead) -> " + path);
            }
            catch (Exception ex) { MelonLogger.Error("[save] write failed: " + ex.Message); }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
        private static string Inv(float f) { return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
        private static int ToInt(object o) { try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; } }
        private static float ToFloat(object o) { try { return o == null ? 0f : Convert.ToSingle(o); } catch { return 0f; } }
        private static float Num(object o, string name)
        {
            if (o == null) return 0f;
            try
            {
                var p = o.GetType().GetProperty(name);
                object v = p != null ? p.GetValue(o) : o.GetType().GetField(name)?.GetValue(o);
                return ToFloat(v);
            }
            catch { return 0f; }
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
                _getComponentsDef = typeof(World).GetMethods()
                    .FirstOrDefault(m => m.Name == "GetComponents"
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
