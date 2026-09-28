using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using MelonLoader;
using Il2CppDefaultEcs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppBrokenArrow.MissionEditor.Systems;         // MissionEntitiesStorageSystem, MissionEntityRecord
using Il2CppBrokenArrow.MissionEditor.Data.ObjectiveZone; // ObjectiveZoneScript
using Il2CppBrokenArrow.Shared.Ecs.MissionEditor;      // EcsEventBus
using Il2CppBrokenArrow.Shared.Ecs.Enums;              // TaskStatus
using System.Text.Json;
using IntList = Il2CppSystem.Collections.Generic.List<int>;
using IntReadOnlyList = Il2CppSystem.Collections.Generic.IReadOnlyList<int>;

namespace BASaveGame
{
    /// <summary>
    /// PvE mission progress (Phase B, "Option 1: replay progression").
    ///
    /// Everything goes through GameController.GetEcsEventBus.Gameplay — the same delegates the
    /// mission script's nodes call (KillUnits, ChangeObjectiveOwner, ActivatePlayableZone,
    /// GetUnitInfo, AssignGroup, Get/SetMoney...). Unit mission UIDs come from the static
    /// MissionEntitiesStorageSystem registry (uid -> record with Entity).
    /// </summary>
    internal static class MissionState
    {
        // ---------------- effect journal ----------------
        // The mission script changes the world through event-bus calls (sector, objectives panel,
        // visibility of zones/markers/props/spawners/units, income curve). Those are one-shot calls
        // whose effect outlives the node that made them, so the node graph alone can't restore
        // them. We append our own handler to each delegate (no Harmony: patching the game's
        // handlers crashed scene load), keep the LATEST call per target, save it and replay it.
        private sealed class Entry { public long seq; public string kind; public string key; public int a; public int b; }
        private static readonly Dictionary<string, Entry> _journal = new Dictionary<string, Entry>();
        private static long _seq;
        private static IntPtr _hookedController;
        private static float _nextHookCheck;
        private static readonly List<Il2CppSystem.Delegate> _keepAlive = new List<Il2CppSystem.Delegate>();

        private static void Note(string kind, string key, int a, int b)
        {
            _journal[kind + ":" + key] = new Entry { seq = ++_seq, kind = kind, key = key, a = a, b = b };
        }

        /// <summary>Active playable zone per the journal: uid, -1 = full map, int.MinValue = unknown.</summary>
        internal static int ActivePlayableZone =>
            _journal.TryGetValue("pz:", out var e) ? e.a : int.MinValue;

        /// <summary>Called every frame from OnUpdate: attaches the journal once per battle.</summary>
        internal static void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextHookCheck) return;
            _nextHookCheck = now + 0.25f;
            try
            {
                if (!GameController.IsInstanceAlive) { _hookedController = IntPtr.Zero; return; }
                var gc = GameController.Instance;
                if (gc == null || gc.Pointer == _hookedController) return;
                var bus = gc.GetEcsEventBus;
                var gp = bus?.Gameplay;
                // Wait until the game has wired its own handlers, so we append rather than get overwritten.
                if (gp == null || gp.ActivatePlayableZone == null) return;
                _hookedController = gc.Pointer;
                _journal.Clear();

                int hooked = 0;
                gp.ActivatePlayableZone = Add(gp.ActivatePlayableZone, new Action<int, bool>((uid, snap) => Note("pz", "", uid, snap ? 1 : 0)), ref hooked);
                gp.DeactivatePlayableZone = Add(gp.DeactivatePlayableZone, new Action(() => Note("pz", "", -1, 0)), ref hooked);
                gp.SetObjectiveVisible = Add(gp.SetObjectiveVisible, Vis("objVis"), ref hooked);
                gp.SetMarkerVisible = Add(gp.SetMarkerVisible, Vis("markerVis"), ref hooked);
                gp.SetPropsVisible = Add(gp.SetPropsVisible, Vis("propsVis"), ref hooked);
                gp.SetSpawnerVisible = Add(gp.SetSpawnerVisible, Vis("spawnerVis"), ref hooked);
                gp.SetUnitVisible = Add(gp.SetUnitVisible, Vis("unitVis"), ref hooked);
                gp.SetUnitGroupVisible = Add(gp.SetUnitGroupVisible, new Action<bool, string>((v, g) => Note("groupVis", g ?? "", v ? 1 : 0, 0)), ref hooked);
                gp.SetIncomeCurveUsageState = Add(gp.SetIncomeCurveUsageState, new Action<bool>(v => Note("incomeCurve", "", v ? 1 : 0, 0)), ref hooked);
                var ui = bus.UI;
                if (ui != null)
                {
                    var h = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EcsEventBus.UIBus.UpdateTaskDel>(
                        new Action<int, TaskStatus>((uid, st) => Note("task", uid.ToString(), (int)st, 0)));
                    _keepAlive.Add(h);
                    ui.UpdateTask = Il2CppSystem.Delegate.Combine(ui.UpdateTask, h).Cast<EcsEventBus.UIBus.UpdateTaskDel>();
                    hooked++;
                }
                ModLog.Dev("[mission] journaling " + hooked + " mission effects (sector, objectives, visibility) for this battle.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[mission] effect journal unavailable: " + e.Message);
            }
        }

        private static Action<bool, int> Vis(string kind) => (v, uid) => Note(kind, uid.ToString(), v ? 1 : 0, 0);

        private static MethodInfo _convert;

        private static T Add<T>(T current, Delegate managed, ref int hooked) where T : Il2CppSystem.Delegate
        {
            _convert ??= Array.Find(typeof(Il2CppInterop.Runtime.DelegateSupport).GetMethods(BindingFlags.Public | BindingFlags.Static),
                m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            var converted = (Il2CppSystem.Delegate)_convert.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { managed });
            _keepAlive.Add(converted);
            hooked++;
            return Il2CppSystem.Delegate.Combine(current, converted).Cast<T>();
        }

        private static EcsEventBus.GameplayBus Gameplay =>
            GameController.IsInstanceAlive ? GameController.Instance.GetEcsEventBus?.Gameplay : null;

        // ================= SAVE =================

        /// <summary>Saved entity id -> (mission uid, group names) for every registered mission unit.</summary>
        internal static Dictionary<int, KeyValuePair<int, string>> UnitIds()
        {
            var result = new Dictionary<int, KeyValuePair<int, string>>();
            try
            {
                var gp = Gameplay;
                var data = MissionEntitiesStorageSystem._data;
                if (data == null) return result;
                var en = data.GetEnumerator();
                while (en.MoveNext())
                {
                    int uid = en.Current.Key;
                    MissionEntityRecord rec = en.Current.Value;
                    if (rec == null) continue;
                    string groups = "";
                    try
                    {
                        if (gp?.GetUnitInfo != null && gp.GetUnitInfo.Invoke(uid, out string _, out string g)) groups = g ?? "";
                    }
                    catch { }
                    result[rec.Entity.EntityId] = new KeyValuePair<int, string>(uid, groups);
                }
            }
            catch (Exception e) { MelonLogger.Warning("[mission] unit ids: " + e.Message); }
            return result;
        }

        /// <summary>How many live entities share a mission uid with another (would break script unit references).</summary>
        internal static int DuplicateUids()
        {
            int dupes = 0;
            try
            {
                var seen = new HashSet<int>();
                var b = MissionEntitiesStorageSystem._entityBindings;
                if (b == null) return 0;
                // The registry is static: entries from a previous battle's world can linger. Only
                // count entities of the current world.
                int worldId = GameController.Instance.GameContext.WorldId;
                var en = b.GetEnumerator();
                while (en.MoveNext())
                {
                    bool alive = false;
                    try { alive = en.Current.Key.WorldId == worldId && en.Current.Key.IsAlive; } catch { }
                    if (alive && !seen.Add(en.Current.Value)) dupes++;
                }
            }
            catch { }
            return dupes;
        }

        /// <summary>Every objective zone uid in the mission (zones in locked sectors included).</summary>
        private static List<int> ZoneUids()
        {
            var uids = new List<int>();
            foreach (var obj in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<ObjectiveZoneScript>()))
            {
                var z = obj.TryCast<ObjectiveZoneScript>();
                if (z == null || z.Data == null) continue;
                int uid = z.Data.UID;
                if (!uids.Contains(uid)) uids.Add(uid);
            }
            return uids;
        }

        /// <summary>The "mission" JSON member: zone owners, money/income per player, effect journal.</summary>
        internal static string MissionJson(IEnumerable<int> playerUids)
        {
            var gp = Gameplay;
            var zones = new List<string>();
            try
            {
                foreach (int zuid in ZoneUids())
                {
                    int team = int.MinValue;
                    try { if (gp?.GetCapturedObjectiveZoneTeam != null) team = gp.GetCapturedObjectiveZoneTeam.Invoke(zuid); } catch { }
                    if (team != int.MinValue) zones.Add("[" + zuid + ", " + team + "]");
                }
            }
            catch (Exception e) { MelonLogger.Warning("[mission] zones: " + e.Message); }

            var money = new List<string>();
            foreach (int p in playerUids)
            {
                try
                {
                    float m = gp?.GetMoney != null ? gp.GetMoney.Invoke(p) : float.NaN;
                    float inc = gp?.GetIncome != null ? gp.GetIncome.Invoke(p) : float.NaN;
                    money.Add("[" + p + ", " + F(m) + ", " + F(inc) + "]");
                }
                catch { }
            }

            var entries = new List<Entry>(_journal.Values);
            entries.Sort((x, y) => x.seq.CompareTo(y.seq));
            var journal = new List<string>();
            foreach (var e in entries)
                journal.Add("[" + JsonSerializer.Serialize(e.kind) + ", " + JsonSerializer.Serialize(e.key) + ", " + e.a + ", " + e.b + "]");

            int pz = ActivePlayableZone;
            return "\"mission\": {\"playZone\": " + (pz == int.MinValue ? "null" : pz.ToString()) +
                   ", \"zones\": [" + string.Join(", ", zones) + "], \"money\": [" + string.Join(", ", money) + "]" +
                   ", \"journal\": [" + string.Join(", ", journal) + "]}";
        }

        // ---------------- uid guard (loaded battles) ----------------
        // Every mission entity, units the player orders included, has a mission uid. A load
        // restarts the mission, so the game's own uid sequence starts again from the mission's
        // first free uid, while the restored army keeps its saved uids (the script refers to
        // them). The next ordered unit then gets a uid a restored unit already has, and its spawn
        // throws inside SpawnService (EditorObjectDataComponent added twice): credits spent, no unit.
        // So in a loaded battle, every new spawn without a uid or with a taken one is given a free
        // uid first; the game honours a preset SpawnData.UID (that's how the army keeps its own).
        private static int _lastGivenUid;

        internal static void ResetUidGuard() => _lastGivenUid = 0;

        internal static void GiveFreeUid(Il2CppBrokenArrow.Client.Ecs.Spawn.SpawnData data, Action<string> log)
        {
            try
            {
                if (data == null) return;
                var registry = MissionEntitiesStorageSystem._data;
                if (registry == null) return;
                int uid = data.UID;
                if (uid != 0 && !registry.ContainsKey(uid)) return;   // the game's own uid is free: keep it
                int max = _lastGivenUid;
                var en = registry.GetEnumerator();
                while (en.MoveNext()) if (en.Current.Key > max) max = en.Current.Key;
                _lastGivenUid = max + 1;
                data.UID = _lastGivenUid;
                string name = "?";
                try { name = data.UnitToSpawn?.Name ?? "?"; } catch { }
                log("uid guard: " + name + " uid " + (uid == 0 ? "none" : uid + " (taken)") + " -> " + _lastGivenUid);
            }
            catch (Exception e) { log("uid guard threw: " + e.Message); }
        }

        // ================= LOAD =================

        internal sealed class Saved
        {
            public float gameTime;
            public List<KeyValuePair<int, int>> zones = new List<KeyValuePair<int, int>>();
            public List<float[]> money = new List<float[]>();
            public List<(string kind, string key, int a, int b)> journal = new List<(string, string, int, int)>();
            public int? legacyPlayZone;
        }

        internal static Saved Parse(JsonElement root)
        {
            var s = new Saved();
            if (root.TryGetProperty("gameTime", out var gt) && gt.ValueKind == JsonValueKind.Number) s.gameTime = gt.GetSingle();
            if (!root.TryGetProperty("mission", out var m)) return s;
            if (m.TryGetProperty("playZone", out var pz) && pz.ValueKind == JsonValueKind.Number) s.legacyPlayZone = pz.GetInt32();
            if (m.TryGetProperty("zones", out var zs))
                foreach (var z in zs.EnumerateArray()) s.zones.Add(new KeyValuePair<int, int>(z[0].GetInt32(), z[1].GetInt32()));
            if (m.TryGetProperty("money", out var ms))
                foreach (var x in ms.EnumerateArray())
                    s.money.Add(new[] { x[0].GetSingle(), Num(x[1]), Num(x[2]) });
            if (m.TryGetProperty("journal", out var js))
                foreach (var j in js.EnumerateArray())
                    s.journal.Add((j[0].GetString(), j[1].GetString(), j[2].GetInt32(), j[3].GetInt32()));
            return s;
        }

        private static float Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : float.NaN;

        internal static void RestoreGameTime(float gameTime, Action<string> log)
        {
            try
            {
                var loader = GameController.Instance._ecsLoader;
                if (loader != null && gameTime > 0f) { loader.GameTime = gameTime; log("game time -> " + gameTime.ToString("0.0") + "s"); }
            }
            catch (Exception e) { log("game time restore threw: " + e.Message); }
        }

        /// <summary>Set capture-zone owners. The script's capture listeners may react; the node restore overwrites that.</summary>
        internal static void RestoreZones(List<KeyValuePair<int, int>> zones, Action<string> log)
        {
            var gp = Gameplay;
            if (gp?.ChangeObjectiveOwner == null) { log("zones: no ChangeObjectiveOwner delegate"); return; }
            int changed = 0, same = 0;
            foreach (var z in zones)
            {
                try
                {
                    int now = gp.GetCapturedObjectiveZoneTeam != null ? gp.GetCapturedObjectiveZoneTeam.Invoke(z.Key) : int.MinValue;
                    if (now == z.Value) { same++; continue; }
                    gp.ChangeObjectiveOwner.Invoke(z.Key, z.Value);
                    int after = gp.GetCapturedObjectiveZoneTeam != null ? gp.GetCapturedObjectiveZoneTeam.Invoke(z.Key) : int.MinValue;
                    log("  zone " + z.Key + ": owner " + now + " -> " + after + " (saved " + z.Value + ")");
                    changed++;
                }
                catch (Exception e) { log("  zone " + z.Key + " threw: " + e.Message); }
            }
            log("capture zones: " + changed + " changed, " + same + " already right");
        }

        /// <summary>Replay the saved effect journal (latest call per target, in original order).</summary>
        internal static void ReplayJournal(Saved s, Action<string> log)
        {
            var bus = GameController.IsInstanceAlive ? GameController.Instance.GetEcsEventBus : null;
            var gp = bus?.Gameplay;
            if (gp == null) { log("journal: no event bus"); return; }
            var journal = s.journal;
            if (journal.Count == 0 && s.legacyPlayZone.HasValue)
                journal = new List<(string, string, int, int)> { ("pz", "", s.legacyPlayZone.Value, 1) };

            int ok = 0, failed = 0;
            var counts = new Dictionary<string, int>();
            foreach (var (kind, key, a, b) in journal)
            {
                try
                {
                    int.TryParse(key, out int id);
                    bool on = a != 0;
                    switch (kind)
                    {
                        case "pz": if (a < 0) gp.DeactivatePlayableZone?.Invoke(); else gp.ActivatePlayableZone?.Invoke(a, b != 0); break;
                        case "objVis": gp.SetObjectiveVisible?.Invoke(on, id); break;
                        case "markerVis": gp.SetMarkerVisible?.Invoke(on, id); break;
                        case "propsVis": gp.SetPropsVisible?.Invoke(on, id); break;
                        case "spawnerVis": gp.SetSpawnerVisible?.Invoke(on, id); break;
                        case "unitVis": gp.SetUnitVisible?.Invoke(on, id); break;
                        case "groupVis": gp.SetUnitGroupVisible?.Invoke(on, key); break;
                        case "incomeCurve": gp.SetIncomeCurveUsageState?.Invoke(on); break;
                        case "task": bus.UI?.UpdateTask?.Invoke(id, (TaskStatus)a); break;
                        default: continue;
                    }
                    ok++;
                    counts[kind] = counts.TryGetValue(kind, out int c) ? c + 1 : 1;
                }
                catch (Exception e) { if (failed++ < 5) log("  journal " + kind + ":" + key + " threw: " + e.Message); }
            }
            var sb = new StringBuilder();
            foreach (var kv in counts) sb.Append(' ').Append(kv.Key).Append('x').Append(kv.Value);
            log("journal: replayed " + ok + " effect(s):" + sb + (failed > 0 ? " (" + failed + " failed)" : "") +
                "; active sector now " + ActivePlayableZone);
        }

        /// <summary>Put restored units back into the mission groups they belonged to.</summary>
        internal static void AssignGroups(Dictionary<int, Entity> spawned, Dictionary<int, string> savedGroups, Action<string> log)
        {
            var gp = Gameplay;
            if (gp?.AssignGroup == null) { log("groups: no AssignGroup delegate"); return; }
            var byGroup = new Dictionary<string, List<int>>();
            int noUid = 0;
            foreach (var kv in spawned)
            {
                if (!savedGroups.TryGetValue(kv.Key, out string groups) || string.IsNullOrEmpty(groups)) continue;
                int uid = UidOf(kv.Value);
                if (uid == int.MinValue) { noUid++; continue; }
                foreach (string g in groups.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = g.Trim();
                    if (name.Length == 0) continue;
                    if (!byGroup.TryGetValue(name, out var list)) byGroup[name] = list = new List<int>();
                    list.Add(uid);
                }
            }
            foreach (var kv in byGroup)
            {
                try { gp.AssignGroup.Invoke(kv.Key, ToList(kv.Value)); }
                catch (Exception e) { log("  group '" + kv.Key + "' threw: " + e.Message); }
            }
            log("groups: " + byGroup.Count + " restored" + (noUid > 0 ? ", " + noUid + " spawned unit(s) had no mission uid" : ""));
        }

        /// <summary>Make every registered mission unit that isn't one of ours disappear.</summary>
        internal static void RemoveAllExcept(ICollection<Entity> keep, Action<string> log)
        {
            var gp = Gameplay;
            if (gp?.KillUnits == null) { log("cleanup: no KillUnits delegate"); return; }
            var keepIds = new HashSet<int>();
            foreach (Entity e in keep) keepIds.Add(e.EntityId);

            var doomed = new List<int>();
            var data = MissionEntitiesStorageSystem._data;
            if (data != null)
            {
                var en = data.GetEnumerator();
                while (en.MoveNext())
                {
                    MissionEntityRecord rec = en.Current.Value;
                    if (rec == null) continue;
                    Entity e = rec.Entity;
                    bool alive = false;
                    try { alive = e.IsAlive; } catch { }
                    if (alive && !keepIds.Contains(e.EntityId)) doomed.Add(en.Current.Key);
                }
            }
            int before = Inspector.CountUnits();
            try { if (doomed.Count > 0) gp.KillUnits.Invoke(ToList(doomed), true); }
            catch (Exception e) { log("cleanup threw: " + e.Message); }
            log("cleanup: removed " + doomed.Count + " non-saved unit(s) (units " + before + " -> " + Inspector.CountUnits() + ")");
        }

        internal static void RestoreMoney(List<float[]> money, Action<string> log)
        {
            var gp = Gameplay;
            if (gp?.SetMoney == null) return;
            foreach (float[] m in money)
            {
                try
                {
                    int player = (int)m[0];
                    if (float.IsNaN(m[1])) continue;
                    float before = gp.GetMoney != null ? gp.GetMoney.Invoke(player) : float.NaN;
                    gp.SetMoney.Invoke(player, m[1]);
                    float incBefore = gp.GetIncome != null ? gp.GetIncome.Invoke(player) : float.NaN;
                    if (!float.IsNaN(m[2]) && incBefore != m[2]) gp.SetIncome?.Invoke(player, m[2]);
                    float incNow = gp.GetIncome != null ? gp.GetIncome.Invoke(player) : float.NaN;
                    log("  player " + player + ": money " + F(before) + " -> " + F(m[1]) +
                        " | income " + F(incBefore) + " -> " + F(incNow) + " (saved " + F(m[2]) + ")");
                }
                catch (Exception e) { log("  money threw: " + e.Message); }
            }
        }

        // ---- helpers ----

        private static int UidOf(Entity e)
        {
            try
            {
                var bindings = MissionEntitiesStorageSystem._entityBindings;
                if (bindings != null && bindings.TryGetValue(e, out int uid)) return uid;
            }
            catch { }
            return int.MinValue;
        }

        private static IntReadOnlyList ToList(List<int> ids)
        {
            var list = new IntList();
            foreach (int id in ids) list.Add(id);
            return list.Cast<IntReadOnlyList>();
        }

        private static string F(float f) =>
            float.IsNaN(f) ? "null" : f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
