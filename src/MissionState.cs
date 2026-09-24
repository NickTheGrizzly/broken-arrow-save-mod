using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Il2CppDefaultEcs;
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController
using Il2CppBrokenArrow.Client.Ecs.Spawn;              // SpawnUtils
using Il2CppBrokenArrow.Client.Ecs.UI;                 // SceneTransition
using Il2CppBrokenArrow.MissionEditor.Systems;         // MissionEntitiesStorageSystem, MissionEntityRecord
using Il2CppBrokenArrow.MissionEditor.Data.ObjectiveZone; // ObjectiveZoneScript
using Il2CppBrokenArrow.Shared.Ecs.MissionEditor;      // EcsEventBus
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
        // Last playable zone (map sector) the game activated; -1 = full map; int.MinValue = unknown.
        internal static int ActivePlayableZone = int.MinValue;

        internal static void Install(HarmonyLib.Harmony h)
        {
            Patch(h, typeof(SpawnUtils), "OnActivatePlayableZone", nameof(ZoneActivatedPostfix));
            Patch(h, typeof(SpawnUtils), "OnDeactivatePlayableZone", nameof(ZoneDeactivatedPostfix));
            Patch(h, typeof(SceneTransition), "ChangeScene", nameof(SceneChangePostfix));
        }

        private static void Patch(HarmonyLib.Harmony h, Type t, string method, string handler)
        {
            try
            {
                MethodInfo target = AccessTools.Method(t, method);
                if (target == null) { MelonLogger.Warning("[mission] patch skip: " + t.Name + "." + method); return; }
                h.Patch(target, postfix: new HarmonyMethod(typeof(MissionState).GetMethod(handler, BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { MelonLogger.Warning("[mission] patch FAILED " + t.Name + "." + method + ": " + e.Message); }
        }

        private static void ZoneActivatedPostfix(int uid) { ActivePlayableZone = uid; }
        private static void ZoneDeactivatedPostfix() { ActivePlayableZone = -1; }
        private static void SceneChangePostfix() { ActivePlayableZone = int.MinValue; }  // new battle: forget

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

        /// <summary>One JSON line: active sector, capture-zone owners, money/income per player.</summary>
        internal static string MissionJson(IEnumerable<int> playerUids)
        {
            var gp = Gameplay;
            var zones = new List<string>();
            try
            {
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ObjectiveZoneScript>()))
                {
                    var z = obj.TryCast<ObjectiveZoneScript>();
                    if (z == null || z.Data == null) continue;
                    int zuid = z.Data.UID;
                    int team = -999;
                    try { if (gp?.GetCapturedObjectiveZoneTeam != null) team = gp.GetCapturedObjectiveZoneTeam.Invoke(zuid); } catch { }
                    zones.Add("[" + zuid + ", " + team + "]");
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

            return "\"mission\": {\"playZone\": " + (ActivePlayableZone == int.MinValue ? "null" : ActivePlayableZone.ToString()) +
                   ", \"zones\": [" + string.Join(", ", zones) + "], \"money\": [" + string.Join(", ", money) + "]}";
        }

        // ================= LOAD =================

        /// <summary>
        /// Step 1 of a load: put the mission back where it was, using the mission's own calls so
        /// its script reacts (unlocks sectors, advances phases). Returns a log summary.
        /// </summary>
        internal static void RestoreProgress(float gameTime, int? playZone, List<KeyValuePair<int, int>> zones, Action<string> log)
        {
            var gp = Gameplay;
            if (gp == null) { log("mission restore: no event bus"); return; }

            try
            {
                var loader = GameController.Instance._ecsLoader;
                if (loader != null && gameTime > 0f) { loader.GameTime = gameTime; log("game time -> " + gameTime.ToString("0.0") + "s"); }
            }
            catch (Exception e) { log("game time restore threw: " + e.Message); }

            int changed = 0, same = 0;
            foreach (var z in zones)
            {
                try
                {
                    int now = gp.GetCapturedObjectiveZoneTeam != null ? gp.GetCapturedObjectiveZoneTeam.Invoke(z.Key) : int.MinValue;
                    if (now == z.Value) { same++; continue; }
                    gp.ChangeObjectiveOwner?.Invoke(z.Key, z.Value);
                    int after = gp.GetCapturedObjectiveZoneTeam != null ? gp.GetCapturedObjectiveZoneTeam.Invoke(z.Key) : int.MinValue;
                    log("  zone " + z.Key + ": owner " + now + " -> " + after + " (saved " + z.Value + ")");
                    changed++;
                }
                catch (Exception e) { log("  zone " + z.Key + " threw: " + e.Message); }
            }
            log("capture zones: " + changed + " changed, " + same + " already right");

            if (playZone.HasValue)
            {
                try
                {
                    if (playZone.Value < 0) gp.DeactivatePlayableZone?.Invoke();
                    else gp.ActivatePlayableZone?.Invoke(playZone.Value, true);
                    log("playable zone -> " + playZone.Value);
                }
                catch (Exception e) { log("playable zone threw: " + e.Message); }
            }
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
                    float incNow = gp.GetIncome != null ? gp.GetIncome.Invoke(player) : float.NaN;
                    log("  player " + player + ": money " + F(before) + " -> " + F(m[1]) +
                        " | income now " + F(incNow) + " (saved " + F(m[2]) + ")");
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
