using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using MelonLoader;
using Il2CppBrokenArrow.Client.Ecs.Controllers;        // GameController, UnitsInfoService
using Il2CppBrokenArrow.Client.Ecs.Utils;              // UnitStatistic
using Il2CppBrokenArrow.Client.Ecs.Decks_v2;           // SharedPlayerDeck
using Il2CppBrokenArrow.Client.Ecs.Decks.Models;       // IDeckDataModel, DeckSlotModel
using Il2CppBrokenArrow.DataBase.Models;               // Units
using Il2CppBrokenArrow.Shared.Ecs;                    // DataBaseService
using Il2CppBrokenArrow.Shared.Ecs.Services;           // Session
using Il2CppBrokenArrow.Client.Ecs.Economy;            // RefundDelayService, RefundDelayData
using UnitCountDict = Il2CppSystem.Collections.Generic.Dictionary<Il2CppBrokenArrow.DataBase.Models.Units, int>;

namespace BASaveGame
{
    /// <summary>
    /// Deck usage during a battle: how many of each deck card a player has already spent. Kept by
    /// GameController.GetUnitStatistic (UnitStatistic service): per player, _playersDict and
    /// _unitLeftCount, both Units -> count. Units overrides Equals/GetHashCode, so a key rebuilt from
    /// (unit id + option ids) matches the game's own. Saved and restored as exact values, through the
    /// service's own AddUnit/RemoveUnit/SetRefundCount so its events and UI refresh fire.
    /// </summary>
    internal static class DeckState
    {
        private static UnitStatistic Stats =>
            GameController.IsInstanceAlive ? GameController.Instance.GetUnitStatistic : null;

        // ================= SAVE =================

        /// <summary>The "deck" JSON member, or null if there's no unit statistic in this battle.</summary>
        internal static string CaptureJson()
        {
            var stats = Stats;
            if (stats == null) return null;
            return "\"deck\": {\"used\": [" + DictJson(stats._playersDict) + "], \"left\": [" + DictJson(stats._unitLeftCount) +
                   "], \"refunds\": [" + RefundsJson() + "]}";
        }

        private static RefundDelayService Refunds
        {
            get
            {
                try { return Session.GetService(Il2CppInterop.Runtime.Il2CppType.Of<RefundDelayService>())?.Cast<RefundDelayService>(); }
                catch { return null; }
            }
        }

        // Units on their way back to the deck: [owner, unitId, [opts], delay, uid, trackDead, elapsed, name]
        private static string RefundsJson()
        {
            var list = Refunds?._refundDataList;
            if (list == null) return "";
            var items = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r == null || r.UnitData == null) continue;
                items.Add("[" + r.OwnerPlayerID + ", " + r.UnitData.Id + ", [" + string.Join(", ", OptionIdsOf(r.UnitData)) + "], " +
                          F(r.RepurchaseDelay) + ", " + r.UID + ", " + (r.TrackDeadValue ? "true" : "false") + ", " + F(r.CurrentTime) + ", " +
                          JsonSerializer.Serialize(r.UnitData.Name ?? "") + "]");
            }
            return string.Join(", ", items);
        }

        private static string F(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static string DictJson(Il2CppSystem.Collections.Generic.Dictionary<int, UnitCountDict> perPlayer)
        {
            if (perPlayer == null) return "";
            var items = new List<string>();
            var pen = perPlayer.GetEnumerator();
            while (pen.MoveNext())
            {
                int player = pen.Current.Key;
                var dict = pen.Current.Value;
                if (dict == null) continue;
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    Units u = en.Current.Key;
                    if (u == null) continue;
                    items.Add("[" + player + ", " + u.Id + ", [" + string.Join(", ", OptionIdsOf(u)) + "], " + en.Current.Value +
                              ", " + JsonSerializer.Serialize(u.Name ?? "") + "]");
                }
            }
            return string.Join(", ", items);
        }

        private static List<int> OptionIdsOf(Units u)
        {
            var ids = new List<int>();
            try
            {
                var opts = u.CurrentOptions;
                if (opts != null) for (int i = 0; i < opts.Count; i++) if (opts[i] != null) ids.Add(opts[i].Id);
            }
            catch { }
            ids.Sort();
            return ids;
        }

        // ================= LOAD =================

        internal sealed class Entry { public int player, unitId, count; public int[] opts; public string name; }
        internal sealed class Refund { public Entry unit; public float delay, elapsed; public int uid; public bool trackDead; }
        internal sealed class Saved { public List<Entry> used = new List<Entry>(), left = new List<Entry>(); public List<Refund> refunds = new List<Refund>(); }

        internal static Saved Parse(JsonElement root)
        {
            if (!root.TryGetProperty("deck", out var d)) return null;
            var s = new Saved();
            Read(d, "used", s.used);
            Read(d, "left", s.left);
            if (d.TryGetProperty("refunds", out var rf))
                foreach (var e in rf.EnumerateArray())
                {
                    var opts = new List<int>();
                    foreach (var o in e[2].EnumerateArray()) opts.Add(o.GetInt32());
                    s.refunds.Add(new Refund
                    {
                        unit = new Entry { player = e[0].GetInt32(), unitId = e[1].GetInt32(), opts = opts.ToArray(), name = e[7].GetString() },
                        delay = e[3].GetSingle(), uid = e[4].GetInt32(), trackDead = e[5].GetBoolean(), elapsed = e[6].GetSingle(),
                    });
                }
            return s;
        }

        private static void Read(JsonElement d, string name, List<Entry> into)
        {
            if (!d.TryGetProperty(name, out var arr)) return;
            foreach (var e in arr.EnumerateArray())
            {
                var opts = new List<int>();
                foreach (var o in e[2].EnumerateArray()) opts.Add(o.GetInt32());
                into.Add(new Entry { player = e[0].GetInt32(), unitId = e[1].GetInt32(), opts = opts.ToArray(), count = e[3].GetInt32(),
                                     name = e.GetArrayLength() > 4 ? e[4].GetString() : "" });
            }
        }

        internal static void Restore(Saved saved, Action<string> log)
        {
            var stats = Stats;
            if (stats == null) { log("deck: no UnitStatistic in this battle"); return; }
            log("deck before: used=[" + Summary(stats._playersDict) + "] left=[" + Summary(stats._unitLeftCount) + "]");
            RestoreRefunds(stats, saved, log);   // first: adding a refund may touch the counts restored below

            int changed = 0, same = 0, failed = 0;
            foreach (var e in saved.used)
            {
                try
                {
                    Units key = KeyFor(stats._playersDict, e);
                    if (key == null) { failed++; log("  used: no unit for " + e.name + " (" + e.unitId + ")"); continue; }
                    int cur = Count(stats._playersDict, e.player, key);
                    if (cur == e.count) { same++; continue; }
                    if (e.count > cur) stats.AddUnit(e.player, key, e.count - cur);
                    else stats.RemoveUnit(e.player, key, cur - e.count);
                    try { stats.RefreshPlayerUnit(e.player, key); } catch { }
                    log("  used p" + e.player + " " + e.name + ": " + cur + " -> " + Count(stats._playersDict, e.player, key) + " (saved " + e.count + ")");
                    changed++;
                }
                catch (Exception ex) { failed++; log("  used " + e.name + " threw: " + ex.Message); }
            }
            foreach (var e in saved.left)
            {
                try
                {
                    Units key = KeyFor(stats._unitLeftCount, e) ?? KeyFor(stats._playersDict, e);
                    if (key == null) { failed++; continue; }
                    int cur = Count(stats._unitLeftCount, e.player, key);
                    if (cur == e.count) { same++; continue; }
                    stats.SetRefundCount(e.player, key, e.count, false);
                    try { stats.RefreshPlayerUnit(e.player, key); } catch { }
                    log("  left p" + e.player + " " + e.name + ": " + cur + " -> " + Count(stats._unitLeftCount, e.player, key) + " (saved " + e.count + ")");
                    changed++;
                }
                catch (Exception ex) { failed++; log("  left " + e.name + " threw: " + ex.Message); }
            }
            log("deck: " + changed + " card count(s) restored, " + same + " already right" + (failed > 0 ? ", " + failed + " failed" : ""));
            log("deck after: used=[" + Summary(stats._playersDict) + "] left=[" + Summary(stats._unitLeftCount) + "]");
        }

        private static void RestoreRefunds(UnitStatistic stats, Saved saved, Action<string> log)
        {
            if (saved.refunds.Count == 0) return;
            var svc = Refunds;
            if (svc == null) { log("refunds: no RefundDelayService"); return; }
            var have = new HashSet<int>();
            var list = svc._refundDataList;
            if (list != null) for (int i = 0; i < list.Count; i++) if (list[i] != null) have.Add(list[i].UID);
            int added = 0;
            foreach (var r in saved.refunds)
            {
                try
                {
                    if (have.Contains(r.uid)) continue;
                    Units key = KeyFor(stats._playersDict, r.unit);
                    if (key == null) { log("  refund: no unit for " + r.unit.name); continue; }
                    var data = new RefundDelayData(key, r.unit.player, r.delay, r.uid, r.trackDead);
                    data.CurrentTime = r.elapsed;
                    svc.AddRefundData(data);
                    added++;
                    log("  refund p" + r.unit.player + " " + r.unit.name + ": " + r.elapsed.ToString("0.0") + "/" + r.delay.ToString("0.0") + "s");
                }
                catch (Exception ex) { log("  refund " + r.unit.name + " threw: " + ex.Message); }
            }
            log("refunds: " + added + "/" + saved.refunds.Count + " pending refund(s) restored");
        }

        /// <summary>The Units key to use: an existing key in the player's dict, a deck-slot unit, or a rebuilt one.</summary>
        private static Units KeyFor(Il2CppSystem.Collections.Generic.Dictionary<int, UnitCountDict> perPlayer, Entry e)
        {
            string want = string.Join(",", e.opts);
            if (perPlayer != null && perPlayer.TryGetValue(e.player, out UnitCountDict dict) && dict != null)
            {
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    Units u = en.Current.Key;
                    if (u != null && u.Id == e.unitId && string.Join(",", OptionIdsOf(u)) == want) return u;
                }
            }
            try
            {
                if (SharedPlayerDeck.TryGetDeck(e.player, out IDeckDataModel deck) && deck?.GetSlots != null)
                {
                    var sen = deck.GetSlots.GetEnumerator();
                    while (sen.MoveNext())
                    {
                        var slots = sen.Current.Value;
                        if (slots == null) continue;
                        for (int i = 0; i < slots.Length; i++)
                        {
                            var slot = slots[i];
                            if (slot == null) continue;
                            foreach (Units u in new[] { SafeUnit(slot, false), SafeUnit(slot, true) })
                                if (u != null && u.Id == e.unitId && string.Join(",", OptionIdsOf(u)) == want) return u;
                        }
                    }
                }
            }
            catch { }
            try
            {
                var db = Session.GetService(Il2CppInterop.Runtime.Il2CppType.Of<DataBaseService>())?.Cast<DataBaseService>();
                Units u = db?.UnitsLoader.GetNewUnit(e.unitId, false);
                if (u != null) UnitsInfoService.ApplyMods(LoadGame.OptionIds(e.opts), u);
                return u;
            }
            catch { return null; }
        }

        private static Units SafeUnit(DeckSlotModel slot, bool transport)
        {
            try { return transport ? slot.GetTransportUnitData() : slot.GetUnitData(); } catch { return null; }
        }

        private static int Count(Il2CppSystem.Collections.Generic.Dictionary<int, UnitCountDict> perPlayer, int player, Units key)
        {
            if (perPlayer == null || !perPlayer.TryGetValue(player, out UnitCountDict dict) || dict == null) return 0;
            return dict.TryGetValue(key, out int c) ? c : 0;
        }

        private static string Summary(Il2CppSystem.Collections.Generic.Dictionary<int, UnitCountDict> perPlayer)
        {
            if (perPlayer == null) return "null";
            var sb = new StringBuilder();
            var pen = perPlayer.GetEnumerator();
            while (pen.MoveNext())
            {
                var dict = pen.Current.Value;
                int n = 0, total = 0;
                if (dict != null) { var en = dict.GetEnumerator(); while (en.MoveNext()) { n++; total += en.Current.Value; } }
                sb.Append(" p").Append(pen.Current.Key).Append(':').Append(n).Append(" cards/").Append(total);
            }
            return sb.ToString().Trim();
        }
    }
}
