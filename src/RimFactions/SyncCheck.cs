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
        }

        static int last = -1;

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
