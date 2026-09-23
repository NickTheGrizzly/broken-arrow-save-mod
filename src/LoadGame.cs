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
using Il2CppDefaultEcs;                                 // Entity
using UnityEngine;                                     // Vector3, Quaternion, Time

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
            public float hp;
            public float[] pos, rot;
        }

        // Batch state (F12). Units spawn sequentially: start one, wait for its UniTask, verify,
        // then the next — so each result is unambiguous and entity capture is ours alone.
        private static Queue<Rec> _queue;
        private static int _index, _total, _ok;
        private static List<string> _failures;
        private static DataBaseService _db;

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

            if (_queue.Count == 0) { Summary(); EndBatch(); return; }
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

                UnitsInfoService.ApplyMods(EmptyOptionIds(), units);
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
                    SkinId = units.CurrentSkinId,
                    AmmoPercent = 100,             // int, defaults to 0 = empty magazines
                    OptionIds = EmptyOptionIds(),  // stock unit
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
            string detail = (LastSpawnedValid ? Inspector.DescribeEntity(LastSpawned) : "entity not captured") +
                            " delta=" + delta +
                            (fault != null ? " fault=" + fault : "") +
                            (ok ? "" : "  <- see GameLogs for the game's error");
            Record(ok, detail);
        }

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
            Live("spawn all done: " + _ok + "/" + _total + " spawned, " + _failures.Count + " failed.");
            foreach (string f in _failures) Live("  FAILED " + f);
        }

        private static void EndBatch()
        {
            _queue = null;
            _awaiting = false;
            CapturingSpawn = false;
            _db = null;
        }

        // SpawnData.RotationY is Euler-Y degrees; the save stores a quaternion.
        private static float Yaw(float[] rot)
        {
            try { return new Quaternion(rot[0], rot[1], rot[2], rot[3]).eulerAngles.y; }
            catch { return 0f; }
        }

        private static Il2CppSystem.Collections.Generic.ICollection<int> EmptyOptionIds() =>
            new Il2CppSystem.Collections.Generic.List<int>().Cast<Il2CppSystem.Collections.Generic.ICollection<int>>();

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
