using System.Collections.Generic;
using System.Linq;
using RimFactions.Net;
using RimWorld;
using Verse;

namespace RimFactions
{
    /// feature:PlanetEvent, the colony side: a planet-wide assault hits this colony, and helpers are paid.
    public static class PlanetEvents
    {
        public static void Start(Msg m)
        {
            var map = Arrival.HomeMap;
            if (map == null) return;
            bool mech = m.Get("kind") == "mech" && Faction.OfMechanoids != null && !Faction.OfMechanoids.defeated;
            var parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.forced = true;
            parms.points *= RFSettings.PlanetEventPointsFactor;
            parms.faction = mech ? Faction.OfMechanoids : Find.FactionManager.RandomRaidableEnemyFaction(allowNonHumanlike: false);
            if (parms.faction == null) return;
            // A real fight now (not a dormant cluster), so colonies can call each other for help.
            parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
            parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
            Find.LetterStack.ReceiveLetter("RF_PlanetEventLabel".Translate(), ("RF_PlanetEventText_" + (mech ? "mech" : "raid")).Translate(),
                LetterDefOf.ThreatBig);
            IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
        }

        public static void Reward(Msg m)
        {
            var map = Arrival.HomeMap;
            if (map == null) return;
            var things = new List<Thing>();
            int silver = m.GetInt("silver", RFSettings.PlanetEventRewardSilver);
            while (silver > 0)
            {
                var s = ThingMaker.MakeThing(ThingDefOf.Silver);
                s.stackCount = System.Math.Min(silver, ThingDefOf.Silver.stackLimit);
                silver -= s.stackCount;
                things.Add(s);
            }
            var spot = Arrival.DropAt(map, things);
            Find.LetterStack.ReceiveLetter("RF_PlanetRewardLabel".Translate(), "RF_PlanetRewardText".Translate(m.GetInt("silver")),
                LetterDefOf.PositiveEvent, new LookTargets(spot, map));
        }
    }
}
