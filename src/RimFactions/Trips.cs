using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimFactions.Net;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimFactions
{
    /// feature:TripHelpAI, feature:TripRaidAI and the sending half of feature:TripHelpLive.
    public static class Trips
    {
        public static void Choose(List<Pawn> pawns)
        {
            if (pawns.Count == 0) return;
            Gifts.ChooseFriend(friend =>
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RF_TripHelpAI".Translate(), () => Send(pawns, friend, "help", "ai")),
                    new FloatMenuOption("RF_TripRaidAI".Translate(), () => Send(pawns, friend, "raid", "ai")),
                };
                var live = new FloatMenuOption("RF_TripHelpLive".Translate(), () => Compat.LiveCompat.ConfirmLive(() => Send(pawns, friend, "help", "live")));
                var mismatch = Dlc.LiveMismatch(friend);
                if (!Rules.Active.liveVisits) { live.Disabled = true; live.Label += " — " + "RF_FeatureOff".Translate(); }
                else if (!friend.online) { live.Disabled = true; live.Label += " " + "RF_NeedsOnline".Translate(); }
                else if (mismatch.Count > 0) { live.Disabled = true; live.Label += " — " + "RF_LiveNeedsSameDlc".Translate(string.Join(", ", mismatch.Select(Dlc.Label))); }
                else if (pawns.Any(x => x.RaceProps.IsMechanoid)) { live.Disabled = true; live.Label += " — " + "RF_LiveNoMechs".Translate(); }
                options.Add(live);
                if (!Rules.Active.playerRaids) { options[1].Disabled = true; options[1].Label += " — " + "RF_FeatureOff".Translate(); }
                Find.WindowStack.Add(new FloatMenu(options));
            }, pawns.Select(PawnData.Capture));
        }

        public static void Send(List<Pawn> pawns, Friend to, string stance, string mode)
        {
            pawns = pawns.Where(p => p.Spawned && !p.Dead).ToList();
            if (pawns.Count == 0) return;
            var tripId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var msg = new Msg("Trip", to.id, new XElement("tripId", tripId), new XElement("stance", stance), new XElement("mode", mode),
                new XElement("pawns", pawns.Select(PawnData.Capture)));

            if (mode == "live")
            {
                // Pawns stay home until the friend accepts (Live.OnReady takes them away).
                Live.pendingRequest = (tripId, to.id, pawns);
                Link.Send(msg);
                Messages.Message("RF_LiveAsked".Translate(to.Label), MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            Away(pawns, tripId, to.id, stance, mode);
            Link.Send(msg);
            if (stance == "raid") Roster.ChangeGoodwill(to.id, RFSettings.RaidGoodwill, "raid");
            Messages.Message(("RF_TripLeft_" + stance).Translate(pawns.Count, to.Label), MessageTypeDefOf.NeutralEvent, false);
        }

        /// system:AwayPawns — the originals wait as world pawns until their trip comes back.
        public static void Away(List<Pawn> pawns, string tripId, string friendId, string stance, string mode)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            var trip = new AwayTrip { tripId = tripId, friendId = friendId, stance = stance, mode = mode, pawns = new List<Pawn>(pawns) };
            foreach (var p in pawns)
            {
                p.DeSpawn();
                comp.awayHolder.TryAdd(p, canMergeWithExistingStacks: false);
            }
            comp.away.Add(trip);
        }

        // ---- receiving (friend's game) ----

        /// Returns false when the trip must wait (live request while busy).
        public static bool Receive(Msg m)
        {
            var mode = m.Get("mode");
            if (mode == "live") { Live.AskAccept(m); return true; }

            var map = Arrival.HomeMap;
            var stance = m.Get("stance");
            var faction = RemoteFactions.Get(m.From);
            RemoteFactions.SetRelation(faction, stance == "raid" ? FactionRelationKind.Hostile : FactionRelationKind.Ally);

            var pawns = new List<Pawn>();
            var originalIds = new List<string>();
            foreach (var px in m.x.Element("pawns").Elements("pawn"))
            {
                pawns.Add(PawnData.Make(px, faction));
                originalIds.Add((string)px.Attribute("id"));
            }

            IntVec3 spot;
            Lord lord;
            if (stance == "raid")
            {
                if (!RCellFinder.TryFindRandomPawnEntryCell(out spot, map, CellFinder.EdgeRoadChance_Hostile)) spot = CellFinder.RandomEdgeCell(map);
                foreach (var p in pawns) GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(spot, map, 5), map);
                lord = LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: false, canTimeoutOrFlee: true, sappers: false, useAvoidGridSmart: false, canSteal: false), map, pawns);
            }
            else
            {
                spot = Arrival.DropAt(map, pawns.Cast<Thing>().ToList());
                lord = LordMaker.MakeNewLord(faction, new LordJob_AssistColony(faction, spot), map, pawns);
                Roster.ChangeGoodwill(m.From, RFSettings.HelpTripGoodwill, "help");
            }

            var tracker = map.GetComponent<TripTracker>();
            tracker.trips.Add(new VisitTrip
            {
                tripId = m.Get("tripId"), senderId = m.From, stance = stance, startTick = Find.TickManager.TicksGame,
                lastThreatTick = Find.TickManager.TicksGame, pawns = pawns, originalIds = originalIds,
            });

            NewsFeed.Send("TripArrived", new XElement("from", m.From), new XElement("to", Link.MyId), new XElement("stance", stance),
                new XElement("mode", "ai"), new XElement("count", pawns.Count));
            Find.LetterStack.ReceiveLetter(("RF_TripHereLabel_" + stance).Translate(), ("RF_TripHereText_" + stance).Translate(Roster.NameOf(m.From), pawns.Count),
                stance == "raid" ? LetterDefOf.ThreatBig : LetterDefOf.PositiveEvent, new LookTargets(pawns));
            return true;
        }

        // ---- the trip comes home (sender's game) ----

        static void TakeOut(RFGameComp comp, Pawn p)
        {
            if (comp.awayHolder.Contains(p)) comp.awayHolder.Remove(p);
            else if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
        }

        public static void ReceiveReturn(Msg m)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            var trip = comp.away.FirstOrDefault(t => t.tripId == m.Get("tripId"));
            if (trip == null) return;
            comp.away.Remove(trip);

            TestHarness.Trace($"return trip={trip.tripId} originals={string.Join(",", trip.pawns.Select(p => p.ThingID + ":" + p.Faction?.Name))}");
            var map = Arrival.HomeMap;
            var back = new List<Thing>();
            var lines = new List<string>();
            foreach (var px in m.x.Element("survivors")?.Elements("pawn") ?? Enumerable.Empty<XElement>())
            {
                var orig = trip.pawns.FirstOrDefault(p => p.ThingID == (string)px.Attribute("id"));
                if (orig == null) continue;
                TakeOut(comp, orig);
                PawnData.ApplyReturn(orig, px);
                back.Add(orig);
                trip.pawns.Remove(orig);
            }
            foreach (var dx in m.x.Element("dead")?.Elements("pawn") ?? Enumerable.Empty<XElement>())
            {
                var orig = trip.pawns.FirstOrDefault(p => p.ThingID == (string)dx.Attribute("id"));
                if (orig == null) continue;
                trip.pawns.Remove(orig);
                lines.Add("RF_ReturnDied".Translate(orig.LabelShort));
                TakeOut(comp, orig);
                orig.Kill(null);
            }
            foreach (var cx in m.x.Element("captured")?.Elements("pawn") ?? Enumerable.Empty<XElement>())
            {
                var orig = trip.pawns.FirstOrDefault(p => p.ThingID == (string)cx.Attribute("id"));
                if (orig == null) continue;
                trip.pawns.Remove(orig);
                lines.Add("RF_ReturnCaptured".Translate(orig.LabelShort, Roster.NameOf(trip.friendId)));
                TakeOut(comp, orig);
                orig.SetFaction(RemoteFactions.Get(trip.friendId));
                Find.WorldPawns.PassToWorld(orig, PawnDiscardDecideMode.KeepForever);
            }
            // Anyone not mentioned comes back too (never leave originals stranded).
            foreach (var orig in trip.pawns.ToList())
            {
                TakeOut(comp, orig);
                back.Add(orig);
            }

            LookTargets look = LookTargets.Invalid;
            if (back.Count > 0)
            {
                var spot = Arrival.DropAt(map, back);
                look = new LookTargets(spot, map);
            }
            Find.LetterStack.ReceiveLetter("RF_ReturnLabel".Translate(), "RF_ReturnText".Translate(Roster.NameOf(trip.friendId), back.Count) +
                (lines.Count > 0 ? "\n\n" + string.Join("\n", lines) : ""), lines.Count > 0 ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent, look);
        }
    }

    public class AwayTrip : IExposable
    {
        public string tripId, friendId, stance, mode;
        public List<Pawn> pawns = new List<Pawn>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref tripId, "tripId");
            Scribe_Values.Look(ref friendId, "friendId");
            Scribe_Values.Look(ref stance, "stance");
            Scribe_Values.Look(ref mode, "mode");
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) pawns?.RemoveAll(p => p == null);
        }
    }

    /// A friend's colonists visiting this map (AI trip).
    public class VisitTrip : IExposable
    {
        public string tripId, senderId, stance;
        public int startTick, lastThreatTick;
        public bool leaving, sawThreat;
        public List<Pawn> pawns = new List<Pawn>();
        public List<string> originalIds = new List<string>();
        public List<XElement> survivors = new List<XElement>();
        public List<string> survivorXml = new List<string>();

        public string OriginalId(Pawn p) { int i = pawns.IndexOf(p); return i >= 0 && i < originalIds.Count ? originalIds[i] : null; }

        public void ExposeData()
        {
            Scribe_Values.Look(ref tripId, "tripId");
            Scribe_Values.Look(ref senderId, "senderId");
            Scribe_Values.Look(ref stance, "stance");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref lastThreatTick, "lastThreatTick");
            Scribe_Values.Look(ref leaving, "leaving");
            Scribe_Values.Look(ref sawThreat, "sawThreat");
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
            Scribe_Collections.Look(ref originalIds, "originalIds", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.Saving) survivorXml = survivors.Select(s => s.ToString(SaveOptions.DisableFormatting)).ToList();
            Scribe_Collections.Look(ref survivorXml, "survivors", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) survivors = (survivorXml ?? new List<string>()).Select(XElement.Parse).ToList();
        }
    }

    /// system:TripTracker — follows AI trips on this map until everyone has left, died or been captured.
    public class TripTracker : MapComponent
    {
        public List<VisitTrip> trips = new List<VisitTrip>();

        public TripTracker(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (Live.InSession || Find.TickManager.TicksGame % 250 != 0 || trips.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            foreach (var trip in trips.ToList())
            {
                if (GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: false) && trip.stance == "help") { trip.lastThreatTick = now; trip.sawThreat = true; }

                if (!trip.leaving)
                {
                    // Helpers stay a day; if they came during (or met) a fight, they go once it has been calm a while.
                    bool timeUp = trip.stance == "help"
                        ? now - trip.startTick > RFSettings.HelpStayTicks || (trip.sawThreat && now - trip.lastThreatTick > RFSettings.HelpCalmTicks)
                        : now - trip.startTick > RFSettings.RaidMaxTicks;
                    if (timeUp) StartLeaving(trip);
                }

                // Downed helpers are carried home by the others when they leave.
                if (trip.leaving && trip.stance == "help")
                    foreach (var p in trip.pawns.Where(p => p.Spawned && p.Downed && !p.Dead).ToList())
                        Survive(trip, p, despawn: true);

                bool raidOver = trip.stance == "raid" && !trip.pawns.Any(p => p.Spawned && !p.Dead && !p.Downed && !p.IsPrisoner);
                if (trip.pawns.All(p => Resolved(trip, p)) || (raidOver && trip.leaving) || (raidOver && now - trip.startTick > 2500))
                    Finish(trip);
            }
        }

        bool Resolved(VisitTrip trip, Pawn p) => p.Dead || p.Destroyed || trip.survivors.Any(s => (string)s.Attribute("id") == trip.OriginalId(p));

        void StartLeaving(VisitTrip trip)
        {
            trip.leaving = true;
            var walkers = trip.pawns.Where(p => p.Spawned && !p.Dead && !p.Downed && !p.IsPrisoner).ToList();
            if (walkers.Count == 0) return;
            foreach (var p in walkers) p.GetLord()?.RemovePawn(p);
            LordMaker.MakeNewLord(walkers[0].Faction, new LordJob_ExitMapBest(LocomotionUrgency.Jog, canDig: false, canDefendSelf: true), map, walkers);
        }

        public static bool Tracked(Pawn p, out VisitTrip trip)
        {
            trip = null;
            var map = p.MapHeld;
            if (map == null) return false;
            trip = map.GetComponent<TripTracker>()?.trips.FirstOrDefault(t => t.pawns.Contains(p));
            return trip != null;
        }

        public void Survive(VisitTrip trip, Pawn p, bool despawn)
        {
            var data = PawnData.Capture(p);
            data.SetAttributeValue("id", trip.OriginalId(p));
            trip.survivors.Add(data);
            if (despawn) p.DeSpawn();
        }

        void Finish(VisitTrip trip)
        {
            trips.Remove(trip);
            var dead = trip.pawns.Where(p => p.Dead).ToList();
            var captured = trip.pawns.Where(p => !p.Dead && !trip.survivors.Any(s => (string)s.Attribute("id") == trip.OriginalId(p))).ToList();
            Link.Send(new Msg("TripReturn", trip.senderId, new XElement("tripId", trip.tripId),
                new XElement("survivors", trip.survivors),
                new XElement("dead", dead.Select(p => new XElement("pawn", new XAttribute("id", trip.OriginalId(p))))),
                new XElement("captured", captured.Select(p => new XElement("pawn", new XAttribute("id", trip.OriginalId(p)))))));
            NewsFeed.Send("TripReturned", new XElement("from", trip.senderId), new XElement("to", Link.MyId),
                new XElement("survivors", trip.survivors.Count), new XElement("dead", dead.Count), new XElement("captured", captured.Count));
            // Stand-ins that went home are gone from this game.
            foreach (var p in trip.pawns.Where(p => !p.Dead && !p.Spawned && !captured.Contains(p)))
                if (!p.Destroyed) p.Destroy();
            if (trip.stance == "help")
                RemoteFactions.SetRelation(RemoteFactions.Get(trip.senderId), FactionRelationKind.Neutral);
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref trips, "rfTrips", LookMode.Deep);
            if (trips == null) trips = new List<VisitTrip>();
        }
    }

    /// hook:TripExitHook — a visiting colonist walking off the map has survived the trip.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
    static class TripExitHook
    {
        static void Prefix(Pawn __instance)
        {
            if (Live.InSession || !TripTracker.Tracked(__instance, out var trip)) return;
            __instance.MapHeld.GetComponent<TripTracker>().Survive(trip, __instance, despawn: false);
        }
    }
}
