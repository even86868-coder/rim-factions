using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimFactions
{
    /// system:SyncCheck — in a live session, every 2500 ticks each game writes one line with fingerprints of the
    /// things that decide who works on what (owners, personal-work switches, orders, each colonist's wounds, doctor
    /// work, forbidden patients). Comparing the host's and a friend's Player.log shows which part split first when a
    /// desync report alone can't tell. It only reads; it changes nothing in the game.
    public static class SyncCheck
    {
        public static void Tick()
        {
            if (!Live.InSession || Find.TickManager == null) return;
            int tick = Find.TickManager.TicksGame;
            if (tick % 250 == 0 && tick != lastMove) RecordMovement(tick);
            if (tick % 2500 != 0 || tick == last) return;
            last = tick;
            var comp = Current.Game?.GetComponent<RFGameComp>();
            if (comp == null) return;
            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
            var owners = Hash(string.Join(",", comp.ownedPawns.Select((p, i) => p?.thingIDNumber + "=" + comp.owners[i])));
            var personal = Hash(string.Join(",", comp.personalWork.OrderBy(x => x)));
            var orders = Hash(string.Join(",", comp.orderOwners.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
            var wounds = new StringBuilder();
            var work = new StringBuilder();
            var forbid = new StringBuilder();
            foreach (var p in colonists)
            {
                wounds.Append(p.thingIDNumber).Append(':').Append(p.health.hediffSet.hediffs.Count(h => h.TendableNow())).Append(p.health.HasHediffsNeedingTendByPlayer() ? "!" : "").Append(',');
                work.Append(p.workSettings?.GetPriority(WorkTypeDefOf.Doctor) ?? -1).Append(p.Drafted ? "d" : "").Append(',');
                foreach (var q in colonists) if (q != p && q.IsForbidden(p)) forbid.Append(p.thingIDNumber).Append('>').Append(q.thingIDNumber).Append(',');
            }
            Log.Message($"[Rim Factions] sync check tick {tick}: owners {owners} personal {personal} orders {comp.orderOwners.Count}/{orders} " +
                        $"wounds {Hash(wounds.ToString())} doctors {Hash(work.ToString())} forbidden {Hash(forbid.ToString())} colonists {colonists.Count}");
            Log.Message($"[Rim Factions] sync check power tick {tick}: {PowerSummary(false)}");
        }

        static int last = -1, lastMove = -1;

        // ---- movement, kept in memory and written out only when Multiplayer reports a desync ----

        static readonly System.Collections.Generic.Queue<string> recent = new System.Collections.Generic.Queue<string>();

        /// Every 250 ticks: each colonist's cell, job and move speed. Only the last 40 (10,000 ticks) are kept.
        static void RecordMovement(int tick)
        {
            lastMove = tick;
            var sb = new StringBuilder();
            foreach (var p in PawnsFinder.AllMaps_FreeColonistsSpawned.OrderBy(p => p.thingIDNumber))
                sb.Append(p.LabelShort).Append('@').Append(p.Position.x).Append(',').Append(p.Position.z)
                  .Append(' ').Append(p.CurJobDef?.defName ?? "-").Append(' ')
                  .Append(p.GetStatValue(StatDefOf.MoveSpeed).ToString("0.00")).Append("; ");
            recent.Enqueue($"tick {tick}: {sb}");
            while (recent.Count > 40) recent.Dequeue();
        }

        /// Multiplayer's "Desynced" window opened: write the recent movement so the host's and friend's logs can be
        /// compared colonist by colonist.
        public static void DumpRecent()
        {
            if (recent.Count == 0) return;
            Log.Message("[Rim Factions] movement before the desync (compare with the other player's log):\n" + string.Join("\n", recent));
            recent.Clear();
        }

        public static void Init(HarmonyLib.Harmony h)
        {
            var window = HarmonyLib.AccessTools.TypeByName("Multiplayer.Client.DesyncedWindow");
            if (window == null) return;
            // the window's own constructors only (patching an inherited method would hit every window)
            foreach (var ctor in HarmonyLib.AccessTools.GetDeclaredConstructors(window))
                h.Patch(ctor, postfix: new HarmonyLib.HarmonyMethod(typeof(SyncCheck), nameof(DumpRecent)));
        }

        /// Every power network on every map: batteries and their exact stored energy, and each building's on/off and
        /// power gain. Short form: one hash per network; long form (at a desync): every building, one per line.
        static string PowerSummary(bool full)
        {
            var sb = new StringBuilder();
            foreach (var map in Find.Maps)
            {
                var nets = map.powerNetManager?.AllNetsListForReading;
                if (nets == null) continue;
                foreach (var net in nets.OrderBy(n => n.transmitters.Count > 0 ? n.transmitters.Min(c => c.parent.thingIDNumber) : -1))
                {
                    int id = net.transmitters.Count > 0 ? net.transmitters.Min(c => c.parent.thingIDNumber) : -1;
                    var detail = new StringBuilder();
                    foreach (var b in net.batteryComps.OrderBy(c => c.parent.thingIDNumber))
                        detail.Append(b.parent.thingIDNumber).Append(" battery ").Append(b.parent.def.defName).Append(' ').Append(b.StoredEnergy.ToString("R")).Append('\n');
                    foreach (var c in net.powerComps.OrderBy(c => c.parent.thingIDNumber))
                        detail.Append(c.parent.thingIDNumber).Append(' ').Append(c.parent.def.defName).Append(c.PowerOn ? " on " : " off ").Append(c.PowerOutput.ToString("R")).Append('\n');
                    if (full) sb.Append($"map {map.uniqueID} net {id}:\n").Append(detail);
                    else sb.Append($"m{map.uniqueID}n{id} b{net.batteryComps.Count} p{net.powerComps.Count} {Hash(detail.ToString())}; ");
                }
            }
            return sb.ToString();
        }

        static string Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) h = (h ^ c) * 16777619;
                return h.ToString("x8");
            }
        }
    }
}
