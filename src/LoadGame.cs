using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using Il2CppBrokenArrow.Shared.Ecs.Services;          // Session
using Il2CppBrokenArrow.Shared.Ecs;                    // DataBaseService
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController, UnitsInfoService
using Il2CppBrokenArrow.Client.Ecs.Spawn;              // SpawnData, SpawnService, UnitBuilder
using Il2CppBrokenArrow.Client.Ecs.Utils;             // PlayerInfo
using Il2CppBrokenArrow.DataBase.Models;               // Units
using Il2CppCysharp.Threading.Tasks;                   // UniTask (spawn returns one)
using Il2CppDefaultEcs;                                 // Entity, World, Components<T>
using Il2CppInterop.Runtime.InteropTypes.Arrays;       // Il2CppStructArray / Il2CppArrayBase
using UnityEngine;                                     // Vector3, Quaternion, Time
using HealthComponent = Il2CppBrokenArrow.Client.Ecs.BattleSystem.Components.HealthComponent;
using AmmunitionContainer = Il2CppBrokenArrow.Client.Ecs.BattleSystem.AmmunitionContainer;

namespace BASaveGame
{
    /// <summary>
    /// LOAD side.
    ///   F6  = dry-run: resolve every saved unit's blueprint + owner, spawn nothing.
    ///   F12 = spawn every saved unit into the CURRENT battle, one per frame, verifying each.
    ///
    /// Spawn recipe (offline path; all static, no DI):
    ///   UnitsLoader.GetNewUnit(typeId) -> UnitsInfoService.ApplyMods(options, unit)
    ///   -> SpawnData -> SpawnService.SpawnUnit(sd, GameController.Instance.UnitLoadScope).
    /// ApplyMods is REQUIRED: it fills CurrentAudioPreset etc. Without it CreateUnitEcs loads a
    /// null asset address, the game logs the error (GameLogs\Gamelog__*.log), destroys the unit,
    /// and the spawn UniTask still completes "successfully".
    /// </summary>
    internal static class LoadGame
    {
        private struct Rec
        {
            public int owner, typeId;
            public string typeName;
            public float hp, maxHp;
            public int skin;                     // -1 = not in save (v1)
            public int[] opts;                   // option ids; empty for v1 saves
            public List<KeyValuePair<int, int>> ammo;  // (ammoId, count); empty for v1 saves
            public int eid;                      // entity id at save time (links passengers to vehicles)
            public int inUnit, inBld;            // container: saved unit eid / building entity id; -1 = none
            public float[] pos, rot;
        }

        // Batch state (F12). Units spawn sequentially: start one, wait for its UniTask, verify,
        // then the next — so each result is unambiguous and entity capture is ours alone.
        private static Queue<Rec> _queue;
        private static int _index, _total, _ok, _hpRestored;
        private static List<string> _failures;
        private static DataBaseService _db;

        // For the passenger pass after all spawns: every saved unit, and saved eid -> new entity.
        private static List<Rec> _all;
        private static Dictionary<int, Entity> _spawned;
        private static int _loaded, _loadWanted;

        // ForceLoadCommand queues an order the game carries out over later frames, so loads are
        // verified by polling after they're issued rather than in the same frame.
        private struct PendingLoad { public Entity cargo, container; public string label; }
        private static List<PendingLoad> _pendingLoads;
        private static float _loadDeadline;
        private const float LoadVerifySeconds = 5f;

        // In-flight spawn.
        private static Rec _current;
        private static UniTask.Awaiter _awaiter;
        private static bool _awaiting;
        private static int _countBefore;
        private static float _startedAt;
        private const float SpawnTimeoutSeconds = 15f;

        // Set by SpawnEntityCapture (UnitBuilder.InitUnitData postfix) while our spawn is in flight.
        internal static bool CapturingSpawn;
        internal static bool LastSpawnedValid;
        internal static Entity LastSpawned;

        internal static void DryRun()
        {
            Live("==== load dry-run @ " + DateTime.Now.ToString("s") + " ====");
            if (!GameController.IsInstanceAlive) { Live("not in a battle"); return; }

            List<Rec> units = ReadSave();
            if (units == null) return;
            DataBaseService db = GetSvc<DataBaseService>("DataBaseService");
            if (db == null) return;

            int ok = 0;
            for (int i = 0; i < units.Count; i++)
            {
                Rec r = units[i];
                string problem = null;
                try
                {
                    if (db.UnitsLoader.GetNewUnit(r.typeId, false) == null) problem = "no blueprint";
                    else if (GameController.Instance.GameSession.GetPlayer(r.owner) == null) problem = "no player " + r.owner;
                }
                catch (Exception e) { problem = "threw: " + e.Message; }

                if (problem == null) ok++;
                else Live(string.Format("  #{0} {1} ({2}) owner={3}: {4}", i + 1, r.typeName, r.typeId, r.owner, problem));
            }
            Live("dry-run done: " + ok + "/" + units.Count + " units resolvable.");
        }

        /// <summary>F12: queue every saved unit; Pump() spawns them one per frame.</summary>
        internal static void SpawnAllUnits()
        {
            Live("==== spawn all @ " + DateTime.Now.ToString("s") + " ====");
            if (_queue != null) { Live("a spawn batch is already running"); return; }
            if (!GameController.IsInstanceAlive) { Live("not in a battle"); return; }

            List<Rec> units = ReadSave();
            if (units == null || units.Count == 0) return;
            _db = GetSvc<DataBaseService>("DataBaseService");
            if (_db == null) return;

            _queue = new Queue<Rec>(units);
            _index = 0;
            _total = units.Count;
            _ok = 0;
            _hpRestored = 0;
            _all = units;
            _spawned = new Dictionary<int, Entity>();
            _loaded = 0;
            _loadWanted = 0;
            _failures = new List<string>();
            Live("queued " + _total + " units; spawning one per frame...");
        }

        /// <summary>Called every frame from OnUpdate. Drives the F12 batch.</summary>
        internal static void Pump()
        {
            if (_queue == null) return;
            if (!GameController.IsInstanceAlive) { Live("battle ended; aborting spawn batch"); EndBatch(); return; }

            if (_awaiting)
            {
                bool done;
                try { done = _awaiter.IsCompleted; }
                catch (Exception e) { Abandon("awaiter threw: " + e.Message); return; }
                if (!done)
                {
                    if (Time.realtimeSinceStartup - _startedAt > SpawnTimeoutSeconds)
                        Abandon("timed out after " + SpawnTimeoutSeconds + "s");
                    return;
                }
                Finish();
                return;  // next unit on the next frame
            }

            if (_queue.Count == 0)
            {
                if (_pendingLoads == null)
                {
                    LoadPassengers();  // issues the load commands
                    _loadDeadline = Time.realtimeSinceStartup + LoadVerifySeconds;
                }
                if (!VerifyLoads()) return;  // keep polling until all loaded or timeout
                Summary();
                EndBatch();
                return;
            }
            StartNext();
        }

        private static void StartNext()
        {
            _current = _queue.Dequeue();
            _index++;
            Rec r = _current;
            try
            {
                Units units = _db.UnitsLoader.GetNewUnit(r.typeId, false);
                if (units == null) { Record(false, "no blueprint for typeId " + r.typeId); return; }

                // Saved options reproduce the original's loadout (they change max HP, weapons...).
                UnitsInfoService.ApplyMods(OptionIds(r.opts), units);
                // Guard for unit types where ApplyMods leaves these unset: a null here is exactly
                // the address CreateUnitEcs chokes on.
                if (units.CurrentAudioPreset == null) units.CurrentAudioPreset = units.AudioPreset;
                if (units.CurrentThumbnail == null) units.CurrentThumbnail = units.ThumbnailFileName;
                if (units.CurrentPortrait == null) units.CurrentPortrait = units.PortraitFileName;

                PlayerInfo player = GameController.Instance.GameSession.GetPlayer(r.owner);
                if (player == null) { Record(false, "no player " + r.owner); return; }

                var sd = new SpawnData
                {
                    UnitToSpawn = units,
                    UnitIDToSpawn = r.typeId,
                    OwnerInfo = player,
                    SpawnerPosition = new Vector3(r.pos[0], r.pos[1], r.pos[2]),
                    RotationY = Yaw(r.rot),
                    IsCostFree = true,
                    SkinId = r.skin >= 0 ? r.skin : units.CurrentSkinId,
                    AmmoPercent = 100,             // int, defaults to 0 = empty; exact counts restored after spawn
                    OptionIds = OptionIds(r.opts),
                };

                _countBefore = Inspector.CountUnits();
                LastSpawnedValid = false;
                CapturingSpawn = true;
                _awaiter = SpawnService.SpawnUnit(sd, GameController.Instance.UnitLoadScope).GetAwaiter();
                _awaiting = true;
                _startedAt = Time.realtimeSinceStartup;
                if (_awaiter.IsCompleted) Finish();  // cached model => completes synchronously
            }
            catch (Exception e)
            {
                CapturingSpawn = false;
                _awaiting = false;
                Record(false, "threw: " + e.Message);
            }
        }

        private static void Finish()
        {
            _awaiting = false;
            CapturingSpawn = false;

            string fault = null;
            try { _awaiter.GetResult(); }
            catch (Exception e) { fault = e.Message; }

            int delta = Inspector.CountUnits() - _countBefore;
            bool alive = false;
            if (LastSpawnedValid) { try { alive = LastSpawned.IsAlive; } catch { } }

            // The captured entity is the authoritative signal; the unit-count delta is only a
            // fallback (other units can die or deploy between the two counts).
            bool ok = fault == null && (LastSpawnedValid ? alive : delta >= 1);
            if (ok && LastSpawnedValid) _spawned[_current.eid] = LastSpawned;

            // Restore saved damage (spawns come in at full HP). Only for units that were damaged.
            string hpNote = "";
            if (ok && LastSpawnedValid && _current.hp > 0f && _current.hp < _current.maxHp - 0.01f)
            {
                hpNote = " | " + RestoreHealth(LastSpawned, _current.hp, _current.maxHp);
                if (hpNote.Contains("->") && !hpNote.Contains("MISMATCH")) _hpRestored++;
            }
            if (ok && LastSpawnedValid && _current.ammo != null && _current.ammo.Count > 0)
                hpNote += " | " + RestoreAmmo(LastSpawned, _current.ammo);

            string detail = (LastSpawnedValid ? Inspector.DescribeEntity(LastSpawned) : "entity not captured") +
                            hpNote +
                            " delta=" + delta +
                            (fault != null ? " fault=" + fault : "") +
                            (ok ? "" : "  <- see GameLogs for the game's error");
            Record(ok, detail);
        }

        /// <summary>
        /// After every unit has spawned: put passengers back into their vehicle's new copy and
        /// garrisons back into their building, via the game's SpawnService.ForceLoadCommand
        /// (plain Entity args — no byref). Vehicles may appear after their passengers in the save,
        /// which is why this is a separate pass rather than part of each spawn.
        /// </summary>
        private static void LoadPassengers()
        {
            _pendingLoads = new List<PendingLoad>();
            Dictionary<int, Entity> containers = null;  // live container entities, for buildings
            foreach (Rec r in _all)
            {
                if (r.inUnit < 0 && r.inBld < 0) continue;
                _loadWanted++;
                string who = string.Format("{0} ({1}) E{2}", r.typeName, r.typeId, r.eid);

                if (!_spawned.TryGetValue(r.eid, out Entity cargo)) { Live("  load skip " + who + ": passenger wasn't spawned"); continue; }

                Entity container;
                string where;
                if (r.inUnit >= 0)
                {
                    if (!_spawned.TryGetValue(r.inUnit, out container)) { Live("  load skip " + who + ": its vehicle E" + r.inUnit + " wasn't spawned"); continue; }
                    where = "vehicle (saved E" + r.inUnit + " -> E" + container.EntityId + ")";
                }
                else
                {
                    containers ??= Inspector.ContainerEntities();
                    if (!containers.TryGetValue(r.inBld, out container) || Inspector.IsUnit(container))
                    { Live("  load skip " + who + ": building E" + r.inBld + " not found"); continue; }
                    where = "building E" + r.inBld;
                }

                try
                {
                    SpawnService.ForceLoadCommand(cargo, container, false);
                    _pendingLoads.Add(new PendingLoad { cargo = cargo, container = container, label = who + " -> " + where });
                }
                catch (Exception ex) { Live("  load " + who + " -> " + where + " threw: " + ex.Message); }
            }
            if (_pendingLoads.Count > 0)
                Live("  issued " + _pendingLoads.Count + " load command(s); verifying for up to " + LoadVerifySeconds + "s...");
        }

        /// <summary>Returns true when every pending load is confirmed or the deadline passed.</summary>
        private static bool VerifyLoads()
        {
            for (int i = _pendingLoads.Count - 1; i >= 0; i--)
            {
                PendingLoad p = _pendingLoads[i];
                if (Inspector.IsLoaded(p.cargo) || Inspector.IsInside(p.container, p.cargo))
                {
                    _loaded++;
                    Live("  load OK: " + p.label);
                    _pendingLoads.RemoveAt(i);
                }
            }
            if (_pendingLoads.Count == 0) return true;
            if (Time.realtimeSinceStartup < _loadDeadline) return false;

            foreach (PendingLoad p in _pendingLoads)
                Live("  load NOT confirmed after " + LoadVerifySeconds + "s: " + p.label +
                     "  (alive=" + SafeAlive(p.cargo) + ")");
            return true;
        }

        private static string SafeAlive(Entity e) { try { return e.IsAlive.ToString(); } catch { return "?"; } }

        // Give up on the in-flight spawn (timeout / awaiter failure) and move on.
        private static void Abandon(string why)
        {
            _awaiting = false;
            CapturingSpawn = false;
            Record(false, why);
        }

        private static void Record(bool ok, string detail)
        {
            Rec r = _current;
            string line = string.Format("#{0}/{1} {2} ({3}) owner={4} -> {5}  {6}",
                _index, _total, r.typeName, r.typeId, r.owner, ok ? "OK" : "FAIL", detail);
            Live(line);
            if (ok) _ok++;
            else _failures.Add(line);
        }

        private static void Summary()
        {
            Live("spawn all done: " + _ok + "/" + _total + " spawned, " + _failures.Count + " failed, " +
                 _hpRestored + " damaged units had HP restored, " +
                 _loaded + "/" + _loadWanted + " passengers/garrisons put back inside.");
            foreach (string f in _failures) Live("  FAILED " + f);
        }

        private static void EndBatch()
        {
            _queue = null;
            _all = null;
            _spawned = null;
            _pendingLoads = null;
            _awaiting = false;
            CapturingSpawn = false;
            _db = null;
        }

        /// <summary>
        /// Write saved HP into a spawned unit's HealthComponent via the pool's REAL backing array
        /// (Components&lt;T&gt;._components, indexed by _mapping[entityId]). HealthComponent is a small
        /// blittable struct, so the array indexer reads/writes native memory directly — no
        /// Entity.Get/Set&lt;T&gt; (byref/in params don't marshal). Guarded: only writes when the live
        /// MaxHealth matches the saved maxHp, i.e. we're definitely looking at the right element.
        /// </summary>
        private static string RestoreHealth(Entity e, float hp, float savedMax)
        {
            try
            {
                World world = GameController.Instance.GameContext;
                Components<HealthComponent> comps = world.GetComponents<HealthComponent>();
                Il2CppStructArray<int> mapping = comps._mapping;
                Il2CppArrayBase<HealthComponent> arr = comps._components;

                int eid = e.EntityId;
                if (mapping == null || arr == null) return "hp skip: no pool";
                if (eid < 0 || eid >= mapping.Length) return "hp skip: entity outside mapping";
                int idx = mapping[eid];
                if (idx < 0 || idx >= arr.Length) return "hp skip: bad index " + idx;

                HealthComponent h = arr[idx];
                string note = "";
                if (Math.Abs(h.MaxHealth - savedMax) > 0.01f)
                {
                    // Loadout differs (e.g. missing option). A modest difference is still the right
                    // element — keep the damage fraction. A wild one means a bad read: don't write.
                    float ratio = savedMax > 0f ? h.MaxHealth / savedMax : 0f;
                    if (ratio < 0.5f || ratio > 2f)
                        return "hp skip: live MaxHealth " + h.MaxHealth + " vs saved " + savedMax;
                    note = " scaled: live max " + h.MaxHealth + " vs saved " + savedMax;
                    hp = hp / savedMax * h.MaxHealth;
                    savedMax = h.MaxHealth;
                }

                float before = h._health;
                try { h.CurrentHealth = hp; }   // game's own setter first
                catch { h._health = hp; }
                arr[idx] = h;

                float after = arr[idx]._health;
                return "hp " + before + "->" + after + " (target " + hp + "/" + savedMax + note + ")" +
                       (Math.Abs(after - hp) > 0.01f ? " MISMATCH" : "");
            }
            catch (Exception ex) { return "hp write threw: " + ex.Message; }
        }

        /// <summary>
        /// Set each saved ammo type's count on the spawned unit via the game's own
        /// AmmunitionContainer.OverrideAmmo (a method on a real object — no struct writes).
        /// Only lowers counts that differ; clamps to the container's max.
        /// </summary>
        private static string RestoreAmmo(Entity e, List<KeyValuePair<int, int>> saved)
        {
            try
            {
                var box = Inspector.ReadAmmoBox(e);
                if (box == null) return "ammo skip: no ammo box";
                int set = 0, same = 0, missing = 0, mismatch = 0;
                foreach (var s in saved)
                {
                    if (!box.ContainsKey(s.Key)) { missing++; continue; }
                    AmmunitionContainer c = box[s.Key];
                    if (c == null) { missing++; continue; }
                    int max = c.MaxAmmoQuantity.Value;
                    int target = Math.Max(0, Math.Min(s.Value, max));
                    if (c.AmmoQuantity.Value == target) { same++; continue; }
                    c.OverrideAmmo(target);
                    if (c.AmmoQuantity.Value == target) set++; else mismatch++;
                }
                if (set + mismatch + missing == 0) return "ammo full";
                return "ammo set " + set + ", unchanged " + same +
                       (missing > 0 ? ", " + missing + " type(s) not on unit" : "") +
                       (mismatch > 0 ? ", " + mismatch + " MISMATCH" : "");
            }
            catch (Exception ex) { return "ammo write threw: " + ex.Message; }
        }

        // SpawnData.RotationY is Euler-Y degrees; the save stores a quaternion.
        private static float Yaw(float[] rot)
        {
            try { return new Quaternion(rot[0], rot[1], rot[2], rot[3]).eulerAngles.y; }
            catch { return 0f; }
        }

        private static Il2CppSystem.Collections.Generic.ICollection<int> OptionIds(int[] ids)
        {
            var list = new Il2CppSystem.Collections.Generic.List<int>();
            if (ids != null) foreach (int id in ids) list.Add(id);
            return list.Cast<Il2CppSystem.Collections.Generic.ICollection<int>>();
        }

        private static T GetSvc<T>(string label) where T : Il2CppSystem.Object
        {
            try
            {
                Il2CppSystem.Object o = Session.GetService(Il2CppInterop.Runtime.Il2CppType.Of<T>());
                T svc = o == null ? null : o.Cast<T>();
                if (svc == null) Live("service " + label + ": null");
                return svc;
            }
            catch (Exception e) { Live("service " + label + " threw: " + e.Message); return null; }
        }

        // ---- save parsing (one unit per line, as written by Inspector.WriteQuickSave) ----

        private static List<Rec> ReadSave()
        {
            string path = Path.Combine(SaveMod.SaveDir, "quicksave.basave");
            if (!File.Exists(path)) { Live("no save at " + path); return null; }

            var units = new List<Rec>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.IndexOf("\"eid\"", StringComparison.Ordinal) < 0) continue;
                try
                {
                    units.Add(new Rec
                    {
                        owner = IntOf(line, "\"owner\":\\s*(-?\\d+)"),
                        typeId = IntOf(line, "\"typeId\":\\s*(-?\\d+)"),
                        typeName = StrOf(line, "\"typeName\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\""),
                        hp = FloatOf(line, "\"hp\":\\s*([-0-9.eE]+)"),
                        maxHp = FloatOf(line, "\"maxHp\":\\s*([-0-9.eE]+)"),
                        eid = IntOf(line, "\"eid\":\\s*(-?\\d+)"),
                        inUnit = Regex.IsMatch(line, "\"inUnit\":") ? IntOf(line, "\"inUnit\":\\s*(-?\\d+)") : -1,
                        inBld = Regex.IsMatch(line, "\"inBld\":") ? IntOf(line, "\"inBld\":\\s*(-?\\d+)") : -1,
                        skin = Regex.IsMatch(line, "\"skin\":") ? IntOf(line, "\"skin\":\\s*(-?\\d+)") : -1,
                        opts = IntsOf(line, "\"opts\":\\s*\\[([^\\]]*)\\]"),
                        ammo = PairsOf(line, "\"ammo\":\\s*\\[((?:\\s*\\[[^\\]]*\\]\\s*,?)*)\\s*\\]"),
                        pos = ArrOf(line, "\"pos\":\\s*\\[([^\\]]+)\\]", 3),
                        rot = ArrOf(line, "\"rot\":\\s*\\[([^\\]]+)\\]", 4),
                    });
                }
                catch (Exception e) { Live("parse err: " + e.Message); }
            }
            Live("save: " + units.Count + " units");
            return units;
        }

        private static int IntOf(string s, string pat)
        { var m = Regex.Match(s, pat); return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0; }
        private static float FloatOf(string s, string pat)
        { var m = Regex.Match(s, pat); return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0f; }
        private static int[] IntsOf(string s, string pat)
        {
            var m = Regex.Match(s, pat);
            var outv = new List<int>();
            if (m.Success)
                foreach (string part in m.Groups[1].Value.Split(','))
                    if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) outv.Add(v);
            return outv.ToArray();
        }
        private static List<KeyValuePair<int, int>> PairsOf(string s, string pat)
        {
            var outv = new List<KeyValuePair<int, int>>();
            var m = Regex.Match(s, pat);
            if (!m.Success) return outv;
            foreach (Match p in Regex.Matches(m.Groups[1].Value, "\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*\\]"))
                outv.Add(new KeyValuePair<int, int>(
                    int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture)));
            return outv;
        }
        private static string StrOf(string s, string pat)
        { var m = Regex.Match(s, pat); return m.Success ? m.Groups[1].Value : "?"; }
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

    // Capture the Entity created for OUR spawn so Finish() can check it's alive. InitUnitData returns
    // the new unit entity and, unlike the async / ref-Entity spawn methods, patches reliably. Gated by
    // LoadGame.CapturingSpawn (armed only while one of our spawns is in flight) and takes the first
    // result only. Entity is a small struct, so marshaling __result once per spawn is safe.
    [HarmonyPatch(typeof(UnitBuilder), "InitUnitData")]
    internal static class SpawnEntityCapture
    {
        private static void Postfix(Entity __result)
        {
            if (!LoadGame.CapturingSpawn) return;
            LoadGame.CapturingSpawn = false;
            LoadGame.LastSpawned = __result;
            LoadGame.LastSpawnedValid = true;
        }
    }
}
