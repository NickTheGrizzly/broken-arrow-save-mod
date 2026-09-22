using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Shared.Ecs.Services;          // Session
using Il2CppBrokenArrow.Shared.Ecs;                    // DataBaseService
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppBrokenArrow.Client.Ecs.GNetwork.Services.Spawn; // NetworkSpawnService, NetworkUnitSpawner
using Il2CppBrokenArrow.Client.Ecs.Spawn;              // SpawnData, SpawnService
using Il2CppBrokenArrow.Client.Ecs.Utils;             // PlayerInfo
using Il2CppBrokenArrow.DataBase.Models;               // Units
using Il2CppVContainer;                                 // IObjectResolver (DI)
using Il2CppCysharp.Threading.Tasks;                   // UniTask (spawn returns one)
using Il2CppDefaultEcs;                                 // Entity (captured spawn result)
using UnityEngine;                                     // Vector3, Quaternion

namespace BASaveGame
{
    /// <summary>
    /// LOAD side. Stage 1 = a SAFE dry-run (F6): parse the save and resolve every
    /// ingredient needed to spawn (services, unit blueprint by id, player) WITHOUT
    /// spawning anything. Confirms the recipe before we attempt a visible spawn.
    /// </summary>
    internal static class LoadGame
    {
        // The spawner lives in a DI scope we can't resolve from outside. We capture it via a
        // Harmony prefix on its own Send* methods (see bottom) — those fire only IN-BATTLE on
        // the main thread (never at menu/loading), so there's no boot-time / off-thread crash.
        internal static NetworkUnitSpawner Spawner;
        internal static Il2CppBrokenArrow.Client.Ecs.Spawn.SpawnCommandWorker DeployWorker;

        private struct Rec { public int owner, typeId; public float hp; public float[] pos; public float[] rot; }

        // Pending spawn UniTask observation (see SpawnFirstUnit / PumpPending).
        private static UniTask.Awaiter _awaiter;
        private static bool _awaiting;
        private static int _countBefore;

        // Capture of the entity UnitBuilder.InitUnitData creates for OUR spawn (gated so we don't
        // grab the game's own concurrent spawns). InitUnitData runs synchronously inside our
        // SpawnService.SpawnUnit call when the unit's model is already loaded (true for the PoC).
        internal static bool CapturingSpawn;
        internal static bool LastSpawnedValid;
        internal static Entity LastSpawned;

        // True from our F12 spawn call until its UniTask completes; lets teardown probes
        // (UnitBuilder.DestroyUnit) report only what happens during OUR spawn.
        internal static bool SpawnWindow;

        /// <summary>
        /// Called every frame from OnUpdate. When the pending spawn UniTask finishes, observe it:
        /// GetResult() returns quietly on success or rethrows the (otherwise swallowed) async
        /// exception — which is what we need to see WHY the unit doesn't appear.
        /// </summary>
        internal static void PumpPending()
        {
            if (!_awaiting) return;
            try { if (!_awaiter.IsCompleted) return; }
            catch (Exception e) { _awaiting = false; Live("awaiter.IsCompleted threw: " + e.Message); return; }

            _awaiting = false;
            CapturingSpawn = false;
            SpawnWindow = false;
            try
            {
                _awaiter.GetResult();
                Live("spawn UniTask COMPLETED with no exception.");
            }
            catch (Exception e)
            {
                Live("spawn UniTask FAULTED: " + e.Message);
                Live(e.ToString());
            }

            // Did a unit actually appear, and where?
            int after = Inspector.CountUnits();
            Live("unit count after: " + after + " (delta " + (after - _countBefore) + ")");
            if (LastSpawnedValid) Live("spawned entity: " + Inspector.DescribeEntity(LastSpawned));
            else Live("spawned entity: not captured (InitUnitData ran async / model not preloaded?)");
        }

        internal static void DryRun()
        {
            Live("==== load dry-run @ " + DateTime.Now.ToString("s") + " ====");
            if (!GameController.IsInstanceAlive) { Live("not in a battle"); return; }

            string path = Path.Combine(SaveMod.SaveDir, "quicksave.basave");
            if (!File.Exists(path)) { Live("no save at " + path); return; }

            Rec? first = ParseFirstUnit(path, out int total);
            Live("parsed units: " + total);
            if (first == null) { Live("no unit parsed"); return; }
            Rec r = first.Value;
            Live(string.Format("unit#1: owner={0} typeId={1} hp={2} pos=[{3},{4},{5}]",
                r.owner, r.typeId, r.hp, r.pos[0], r.pos[1], r.pos[2]));

            DataBaseService db = GetSvc<DataBaseService>("DataBaseService");
            Live("NetworkUnitSpawner: " + (Spawner != null ? "OK" : "null — deploy at least one unit this battle so the hook fires"));

            // Resolve the unit blueprint by id.
            if (db != null)
            {
                try
                {
                    Units units = db.GetUnitById(r.typeId, false);
                    if (units != null)
                        Live("GetUnitById(" + r.typeId + ") = " + units.Name + " (Id=" + units.Id + ", Cost=" + units.Cost + ")");
                    else
                        Live("GetUnitById(" + r.typeId + ") = null");
                }
                catch (Exception e) { Live("GetUnitById threw: " + e.Message); }
            }

            // Resolve the owning player.
            try
            {
                var session = GameController.Instance.GameSession;
                PlayerInfo p = session.GetPlayer(r.owner);
                Live("GetPlayer(" + r.owner + ") = " + (p != null ? "OK" : "null"));
            }
            catch (Exception e) { Live("GetPlayer threw: " + e.Message); }

            Live("dry-run done. If services + blueprint + player all resolved, we can spawn next.");
        }

        /// <summary>
        /// PoC (F12): spawn the FIRST saved unit into the CURRENT running battle via the
        /// game's own OFFLINE spawn service. The deploy probe proved offline deploys go
        /// through SpawnCommandWorker (never the network spawner), and its downstream builder
        /// is the STATIC SpawnService.SpawnUnit(SpawnData, IUnitLoadScope) — which internally
        /// runs UnitBuilder.CreateUnitEcs + InvokeUnitSpawned + the initial placement command.
        /// Static => no DI/VContainer scope to resolve (that was the whole blocker).
        ///
        /// We fire the returned UniTask and do NOT await it: the model load is async, so the
        /// entity materializes over the next frames. Watch the battlefield / press F7 after.
        /// v1 spawns at the saved SpawnerPosition with no move order (RequestPosition left null)
        /// and no cost; HP is not yet written (full HP for now).
        /// </summary>
        internal static void SpawnFirstUnit()
        {
            Live("==== spawn PoC @ " + DateTime.Now.ToString("s") + " ====");
            if (!GameController.IsInstanceAlive) { Live("not in a battle"); return; }

            string path = Path.Combine(SaveMod.SaveDir, "quicksave.basave");
            if (!File.Exists(path)) { Live("no save at " + path); return; }

            Rec? first = ParseFirstUnit(path, out int total);
            if (first == null) { Live("no unit parsed"); return; }
            Rec r = first.Value;
            Live(string.Format("spawning unit#1: owner={0} typeId={1} pos=[{2},{3},{4}] rot=[{5},{6},{7},{8}]",
                r.owner, r.typeId, r.pos[0], r.pos[1], r.pos[2], r.rot[0], r.rot[1], r.rot[2], r.rot[3]));

            // Blueprint + owner (both confirmed resolvable in the dry-run).
            Units units;
            PlayerInfo player;
            try
            {
                DataBaseService db = GetSvc<DataBaseService>("DataBaseService");
                if (db == null) { Live("abort: DataBaseService null"); return; }
                // GetUnitById returns the RAW blueprint: its Current* sub-models (mobility, armor,
                // abilities...) aren't loaded, so CreateUnitEcs asked the asset loader for a null
                // address and aborted (GameLogs: ArgumentNullException in GetOrLoadAssetAsync).
                // Deploys use a LOADED unit — get one from the game's own loader.
                Units raw = db.GetUnitById(r.typeId, false);
                Live("raw    GetUnitById:        " + DescribeUnits(raw));
                units = null;
                try { units = db.UnitsLoader.GetNewUnit(r.typeId, false); }
                catch (Exception e) { Live("UnitsLoader.GetNewUnit threw: " + e.Message); }
                Live("loaded UnitsLoader.GetNewUnit: " + DescribeUnits(units));
                if (units == null) { Live("abort: no loaded unit for typeId " + r.typeId); return; }

                // Still failing with a null asset address (GameLogs: GetOrLoadAssetAsync(null) inside
                // CreateUnitEcs). Suspect the per-instance Current* strings (CurrentAudioPreset etc.),
                // which the deck path fills via UnitsInfoService.ApplyMods(slotOptions, unit).
                Live("strings before: " + DescribeUnitStrings(units));
                try { UnitsInfoService.ApplyMods(EmptyOptionIds(), units); Live("ApplyMods(empty) OK"); }
                catch (Exception e) { Live("ApplyMods threw: " + e.Message); }
                Live("strings after ApplyMods: " + DescribeUnitStrings(units));
                // Fallback: any Current* still null -> use the base value.
                try
                {
                    if (units.CurrentAudioPreset == null) units.CurrentAudioPreset = units.AudioPreset;
                    if (units.CurrentThumbnail == null) units.CurrentThumbnail = units.ThumbnailFileName;
                    if (units.CurrentPortrait == null) units.CurrentPortrait = units.PortraitFileName;
                }
                catch (Exception e) { Live("Current* fallback threw: " + e.Message); }
                Live("strings final: " + DescribeUnitStrings(units));
                player = GameController.Instance.GameSession.GetPlayer(r.owner);
                if (player == null) { Live("abort: GetPlayer(" + r.owner + ") null"); return; }
                Live("resolved blueprint=" + units.Name + " owner=" + r.owner);
            }
            catch (Exception e) { Live("abort resolving inputs: " + e.Message); return; }

            // Yaw from the saved quaternion (SpawnData.RotationY is Euler-Y degrees). Prefer this
            // over the fiddly Nullable<Quaternion> RequestRotate for the first attempt.
            float yaw;
            try { yaw = new Quaternion(r.rot[0], r.rot[1], r.rot[2], r.rot[3]).eulerAngles.y; }
            catch { yaw = 0f; }

            SpawnData sd;
            try
            {
                sd = new SpawnData
                {
                    UnitToSpawn = units,
                    UnitIDToSpawn = r.typeId,
                    OwnerInfo = player,
                    SpawnerPosition = new Vector3(r.pos[0], r.pos[1], r.pos[2]),
                    RotationY = yaw,
                    IsCostFree = true,
                    SkinId = units.CurrentSkinId,
                    AmmoPercent = 100,  // int, defaults to 0 = empty magazines
                    OptionIds = EmptyOptionIds(),  // stock unit; null may skip option/Current* setup
                };
                Live("built SpawnData (SpawnerPosition set, RequestPosition null, IsCostFree=true, yaw=" + yaw.ToString("0.0") +
                     ", SkinId=" + sd.SkinId + ", AmmoPercent=100)");
            }
            catch (Exception e) { Live("abort building SpawnData: " + e.Message); return; }

            // Fire the offline spawn. Static call — no spawner/DI needed. Do NOT block on the UniTask.
            try
            {
                var scope = GameController.Instance.UnitLoadScope;
                Live("scope: " + (scope != null ? "OK" : "null"));
                // Clear the pipeline probe's per-method dedup so THIS spawn logs a fresh trace
                // (the game's own startup spawns already tripped the once-per-session entries).
                SpawnPipelineProbe.Reset();
                // Snapshot the unit count and arm entity capture so we can tell afterwards whether
                // the unit actually exists in the world and where it landed.
                _countBefore = Inspector.CountUnits();
                Live("unit count before: " + _countBefore);
                LastSpawnedValid = false;
                CapturingSpawn = true;
                SpawnWindow = true;
                // Do NOT fire-and-forget: capture the UniTask's awaiter so we can observe its
                // result. The async chain swallows exceptions; GetResult() rethrows them (with an
                // il2cpp stack trace) once complete. Poll it each frame in PumpPending().
                _awaiter = SpawnService.SpawnUnit(sd, scope).GetAwaiter();
                CapturingSpawn = false;  // synchronous InitUnitData (cached model) already captured
                _awaiting = true;
                Live("SpawnService.SpawnUnit invoked; awaiting UniTask result...");
                if (_awaiter.IsCompleted) PumpPending();  // may already be faulted/done synchronously
            }
            catch (Exception e) { Live("SpawnService.SpawnUnit threw: " + e.Message); }
        }

        // Which per-instance sub-models are populated on a Units object (raw blueprint vs loaded).
        private static string DescribeUnits(Units u)
        {
            if (u == null) return "null";
            string S(Func<object> f) { try { return Convert.ToString(f()); } catch (Exception e) { return "<err:" + e.Message + ">"; } }
            return string.Format("ptr={0:X} name={1} model={2} mobility={3} armor={4} options={5} skin={6} baseUnit={7}",
                u.Pointer.ToInt64(), S(() => u.Name), S(() => u.ModelFileName),
                S(() => u.CurrentMobility != null), S(() => u.CurrentArmor != null),
                S(() => u.CurrentOptions == null ? "null" : u.CurrentOptions.Count.ToString()),
                S(() => u.CurrentSkinId), S(() => u.BaseUnit != null));
        }

        // The asset-address strings CreateUnitEcs may look up (base vs per-instance "Current*").
        private static string DescribeUnitStrings(Units u)
        {
            if (u == null) return "null";
            string S(Func<string> f) { try { string v = f(); return v == null ? "<null>" : "'" + v + "'"; } catch (Exception e) { return "<err:" + e.Message + ">"; } }
            return "audio=" + S(() => u.AudioPreset) + " curAudio=" + S(() => u.CurrentAudioPreset) +
                   " thumb=" + S(() => u.ThumbnailFileName) + " curThumb=" + S(() => u.CurrentThumbnail) +
                   " portrait=" + S(() => u.PortraitFileName) + " curPortrait=" + S(() => u.CurrentPortrait);
        }

        private static Il2CppSystem.Collections.Generic.ICollection<int> EmptyOptionIds() =>
            new Il2CppSystem.Collections.Generic.List<int>().Cast<Il2CppSystem.Collections.Generic.ICollection<int>>();

        private static T GetSvc<T>(string label) where T : Il2CppSystem.Object
        {
            try
            {
                var t = Il2CppInterop.Runtime.Il2CppType.Of<T>();
                Il2CppSystem.Object o = Session.GetService(t);
                T svc = o == null ? null : o.Cast<T>();
                Live("service " + label + ": " + (svc != null ? "OK" : "null"));
                return svc;
            }
            catch (Exception e) { Live("service " + label + " threw: " + e.Message); return null; }
        }

        private static Rec? ParseFirstUnit(string path, out int total)
        {
            total = 0;
            Rec? first = null;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.IndexOf("\"eid\"", StringComparison.Ordinal) < 0) continue;
                total++;
                if (first != null) continue;
                try
                {
                    int owner = IntOf(line, "\"owner\":\\s*(-?\\d+)");
                    int typeId = IntOf(line, "\"typeId\":\\s*(-?\\d+)");
                    float hp = FloatOf(line, "\"hp\":\\s*([-0-9.eE]+)");
                    float[] pos = ArrOf(line, "\"pos\":\\s*\\[([^\\]]+)\\]", 3);
                    float[] rot = ArrOf(line, "\"rot\":\\s*\\[([^\\]]+)\\]", 4);
                    first = new Rec { owner = owner, typeId = typeId, hp = hp, pos = pos, rot = rot };
                }
                catch (Exception e) { Live("parse err: " + e.Message); }
            }
            return first;
        }

        private static int IntOf(string s, string pat)
        { var m = Regex.Match(s, pat); return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0; }
        private static float FloatOf(string s, string pat)
        { var m = Regex.Match(s, pat); return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0f; }
        private static float[] ArrOf(string s, string pat, int n)
        {
            var m = Regex.Match(s, pat);
            var outv = new float[n];
            if (!m.Success) return outv;
            string[] parts = m.Groups[1].Value.Split(',');
            for (int i = 0; i < n && i < parts.Length; i++)
                float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out outv[i]);
            return outv;
        }

        internal static void Live(string s)
        {
            MelonLogger.Msg("[load] " + s);
            try
            {
                if (!string.IsNullOrEmpty(SaveMod.SaveDir))
                    File.AppendAllText(Path.Combine(SaveMod.SaveDir, "live_load.txt"), s + Environment.NewLine);
            }
            catch { }
        }
    }

    // Capture the spawner by prefixing its own Send* methods. These fire only when a unit
    // actually deploys — in a running battle, on the main thread — so, unlike get_UnitSpawner
    // (called at menu/loading off the main thread → memory-corruption crash), there is no
    // boot-time hook and no lingering trampoline on a hot/background path. __instance IS the
    // spawner. The prefix is a no-op after the first capture.
    [HarmonyPatch]
    internal static class SpawnerCapturePatch
    {
        // Any of these, called during battle setup / deploy, gives us __instance = the spawner.
        private static readonly System.Collections.Generic.HashSet<string> Names =
            new System.Collections.Generic.HashSet<string>
            {
                "SendSpawnUnit", "SendSpawnContainer", "SendSpawnCargo",
                "SpawnRemote", "SpawnRemoteDeadUnit", "InitUnitForDeck",
            };

        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var methods = typeof(NetworkUnitSpawner).GetMethods()
                .Where(m => Names.Contains(m.Name))
                .Cast<MethodBase>()
                .ToList();
            LoadGame.Live("SpawnerCapturePatch targeting " + methods.Count + " methods");
            return methods;
        }

        private static void Prefix(NetworkUnitSpawner __instance, MethodBase __originalMethod)
        {
            if (LoadGame.Spawner == null && __instance != null)
            {
                LoadGame.Spawner = __instance;
                LoadGame.Live("captured NetworkUnitSpawner via " + __originalMethod.Name);
            }
        }
    }

    // DIAGNOSTIC: the offline spawn path bypasses NetworkUnitSpawner. Probe the deploy handler
    // (SpawnCommandWorker) to confirm deploys reach it (and that our method-patching works on the
    // deploy path at all). Logs on each fire; captures the worker instance for follow-up.
    [HarmonyPatch]
    internal static class DeployProbePatch
    {
        private static readonly System.Collections.Generic.HashSet<string> Names =
            new System.Collections.Generic.HashSet<string>
            { "OnExecuteOrderInternal", "OnExecuteOrder", "RequestGameEntitySpawn" };

        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var methods = typeof(Il2CppBrokenArrow.Client.Ecs.Spawn.SpawnCommandWorker).GetMethods()
                .Where(m => Names.Contains(m.Name)).Cast<MethodBase>().ToList();
            LoadGame.Live("DeployProbePatch targeting " + methods.Count + " methods");
            return methods;
        }

        private static bool _logged;

        private static void Prefix(Il2CppBrokenArrow.Client.Ecs.Spawn.SpawnCommandWorker __instance, MethodBase __originalMethod)
        {
            LoadGame.DeployWorker = __instance;
            // OnExecuteOrderInternal ticks every frame while an order is in progress; log only the
            // first fire per session so it doesn't drown out the spawn-pipeline diagnostics.
            if (!_logged) { _logged = true; LoadGame.Live("DEPLOY probe: " + __originalMethod.Name + " fired (further fires suppressed)"); }
        }
    }

    // DIAGNOSTIC for the F12 spawn PoC. SpawnService.SpawnUnit returned without throwing but no
    // unit appeared → the failure is inside the async chain (we fire-and-forget the UniTask).
    // Prefix the offline spawn pipeline's own methods (all static, all run in-battle on the main
    // thread → safe) to see the FURTHEST point reached. Each name logs once per session.
    //   SpawnService.SpawnUnit(SpawnData,scope)  → our entry
    //   UnitBuilder.CreateUnitEcs               → async model+entity build
    //   UnitBuilder.InitUnitData                → synchronous entity core (returns the Entity)
    //   SpawnService.InvokeUnitSpawned(ref e,..) → success tail; logs the produced entity id
    //   SpawnService.InitialCommandFromSpawn    → initial placement/command
    // Whichever is the LAST to fire localizes where the chain dies.
    [HarmonyPatch]
    internal static class SpawnPipelineProbe
    {
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>();

        // Called from SpawnFirstUnit right before our spawn so each F12 press logs a fresh trace.
        internal static void Reset() { lock (_seen) _seen.Clear(); }

        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var list = new System.Collections.Generic.List<MethodBase>();
            void Add(Type t, string name)
            {
                foreach (var m in t.GetMethods(F))
                    if (m.Name == name) list.Add(m);
            }
            Add(typeof(SpawnService), "SpawnUnit");             // both overloads; harmless
            Add(typeof(SpawnService), "InvokeUnitSpawned");
            Add(typeof(SpawnService), "InitialCommandFromSpawn");
            Add(typeof(UnitBuilder), "CreateUnitEcs");
            Add(typeof(UnitBuilder), "InitUnitData");
            LoadGame.Live("SpawnPipelineProbe targeting " + list.Count + " methods");
            return list;
        }

        private static void Prefix(MethodBase __originalMethod)
        {
            string n = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            bool fresh; lock (_seen) fresh = _seen.Add(n);
            if (fresh) LoadGame.Live("PIPE probe: " + n + " reached");
        }
    }

    // Capture the Entity created for OUR spawn. UnitBuilder.InitUnitData returns the new unit entity
    // and (unlike the ref-Entity / async methods) patches reliably. Gated by LoadGame.CapturingSpawn
    // so we only grab our own spawn, not the game's concurrent ones. Entity is a small struct so
    // marshaling __result once per spawn on the main thread is safe.
    [HarmonyPatch(typeof(UnitBuilder), "InitUnitData")]
    internal static class SpawnEntityCapture
    {
        private static void Postfix(Il2CppDefaultEcs.Entity __result)
        {
            if (!LoadGame.CapturingSpawn) return;
            LoadGame.CapturingSpawn = false;
            LoadGame.LastSpawned = __result;
            LoadGame.LastSpawnedValid = true;
            // State at birth — compare with the describe logged at completion. If it has
            // UnitComponent here but not later, something tore it down after InitUnitData.
            LoadGame.Live("captured spawned entity @InitUnitData: " + Inspector.DescribeEntity(__result));
        }
    }

    // Teardown probe: UnitBuilder.DestroyUnit(UnitModelInfo) is static with a ref-type param
    // (patches reliably). Logs every call inside our spawn window, so a rejected-then-destroyed
    // spawn shows up here with the unit name.
    [HarmonyPatch(typeof(UnitBuilder), "DestroyUnit")]
    internal static class SpawnDestroyProbe
    {
        private static void Prefix(UnitModelInfo unitModelInfo)
        {
            if (!LoadGame.SpawnWindow) return;
            string info = "?";
            try
            {
                string prefab = unitModelInfo?.PrefabRootScript != null ? unitModelInfo.PrefabRootScript.gameObject.name : "null";
                info = "id=" + unitModelInfo?.Id + " prefab=" + prefab;
            }
            catch { }
            LoadGame.Live("DESTROY probe: UnitBuilder.DestroyUnit called during our spawn (" + info + ")");
        }
    }
}
