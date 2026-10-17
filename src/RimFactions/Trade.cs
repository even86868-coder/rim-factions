using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RimFactions.Net;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:TradeCaravan — a caravan that reaches a friend's colony trades with its real stockpile.
    /// The friend's game sends its stock (message:TradeStock); the deal goes back as message:TradeDeal and
    /// is applied to their save: what you bought leaves their stockpile, what you paid lands by drop pod.
    public static class Trade
    {
        static string waitingFor;          // requestId we asked for
        static Caravan waitingCaravan;
        static string waitingFriend;
        public static FriendTrader pending; // a trade dialog that may have executed a deal
        public static bool autoDeal;        // test harness only

        public static void Init()
        {
            Link.On("TradeStockRequest", OnStockRequest);
            Link.On("TradeStock", OnStock);
        }

        public static void Request(Caravan caravan, string friendId)
        {
            waitingFor = Guid.NewGuid().ToString("N").Substring(0, 10);
            waitingCaravan = caravan;
            waitingFriend = friendId;
            Link.Send(new Msg("TradeStockRequest", friendId, new XElement("requestId", waitingFor)));
            Messages.Message("RF_TradeAsking".Translate(Roster.NameOf(friendId)), caravan, MessageTypeDefOf.NeutralEvent, false);
        }

        /// Friend's game: what it has in its stockpiles, unless it can't trade right now.
        static void OnStockRequest(Msg m)
        {
            var reply = new Msg("TradeStock", m.From, new XElement("requestId", m.Get("requestId")));
            var map = Arrival.HomeMap;
            if (!Inbox.CanHandleNow || map == null) { reply.x.Add(new XElement("busy", true)); Link.Send(reply); return; }
            var items = new XElement("items");
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                         .Where(t => t.Spawned && !(t is Pawn) && !(t is Corpse) && t.def.category == ThingCategory.Item && t.def.tradeability != Tradeability.None
                                     && (t.IsInAnyStorage() || map.areaManager.Home[t.Position]) && !t.IsForbidden(Faction.OfPlayer))
                         .OrderByDescending(t => t.MarketValue * t.stackCount)
                         .Take(RFSettings.TradeStockMaxStacks))
            {
                var x = ItemData.Capture(t);
                x.SetAttributeValue("thing", t.ThingID);
                items.Add(x);
            }
            reply.x.Add(items);
            Link.Send(reply);
        }

        /// Our game: the stock arrived; open the trade screen if the caravan is still there.
        static void OnStock(Msg m)
        {
            if (m.Get("requestId") != waitingFor) return;
            waitingFor = null;
            var caravan = waitingCaravan;
            if (m.x.Element("busy") != null) { Messages.Message("RF_TradeBusy".Translate(Roster.NameOf(m.From)), MessageTypeDefOf.RejectInput, false); return; }
            if (caravan == null || caravan.Destroyed || Current.ProgramState != ProgramState.Playing) return;
            var trader = new FriendTrader(waitingFriend, m.x.Element("items")?.Elements("item") ?? Enumerable.Empty<XElement>());
            var negotiator = BestCaravanPawnUtility.FindBestNegotiator(caravan, trader.Faction, trader.TraderKind);
            if (negotiator == null) { TestHarness.Trace("trade: no negotiator"); Messages.Message("RF_TradeNoNegotiator".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            pending = trader;
            if (autoDeal)
            {
                // test harness: buy one of their most valuable stacks' items, pay with 50 silver
                autoDeal = false;
                var good = trader.Goods.FirstOrDefault(g => g.def != ThingDefOf.Silver);
                if (good != null) trader.GiveSoldThingToPlayer(good, 1, negotiator);
                var silver = CaravanInventoryUtility.AllInventoryItems(caravan).FirstOrDefault(i => i.def == ThingDefOf.Silver);
                if (silver != null) trader.GiveSoldThingToTrader(silver, Mathf.Min(50, silver.stackCount), negotiator);
                TestHarness.Trace($"autoDeal bought={trader.bought.Count} sold={trader.sold.Count} goods={trader.Goods.Count()}");
                return;
            }
            Find.WindowStack.Add(new Dialog_Trade(negotiator, trader));
        }

        /// Called every frame: once the trade screen has closed, send what changed hands.
        public static void Tick()
        {
            if (pending == null || TradeSession.Active || Find.WindowStack.IsOpen<Dialog_Trade>()) return;
            var t = pending;
            pending = null;
            if (t.bought.Count == 0 && t.sold.Count == 0) return;
            Link.Send(new Msg("TradeDeal", t.friendId,
                new XElement("taken", t.bought.Select(b => new XElement("take", new XAttribute("thing", b.thingId), new XAttribute("def", b.def), new XAttribute("count", b.count)))),
                new XElement("given", t.sold)));
            Roster.ChangeGoodwill(t.friendId, RFSettings.TradeGoodwill, "trade");
            NewsFeed.Send("TradeDone", new XElement("from", Link.MyId), new XElement("to", t.friendId),
                new XElement("bought", t.bought.Sum(b => b.count)), new XElement("sold", t.sold.Sum(s => (int?)s.Attribute("count") ?? 1)));
        }

        /// Friend's game (via Inbox): apply the deal to this save.
        public static void ApplyDeal(Msg m)
        {
            var map = Arrival.HomeMap;
            var lines = new List<string>();
            int shortBy = 0;
            foreach (var take in m.x.Element("taken")?.Elements("take") ?? Enumerable.Empty<XElement>())
            {
                int count = (int)take.Attribute("count");
                var def = DefDatabase<ThingDef>.GetNamedSilentFail((string)take.Attribute("def"));
                var exact = Find.Maps.SelectMany(mp => mp.listerThings.AllThings).FirstOrDefault(x => x.ThingID == (string)take.Attribute("thing"));
                int left = Remove(exact, count);
                // The exact stack moved or shrank since: take the rest from the same kind of item in storage.
                if (left > 0 && def != null && map != null)
                    foreach (var other in map.listerThings.ThingsOfDef(def).Where(x => x.Spawned && x.IsInAnyStorage()).ToList())
                    {
                        if (left <= 0) break;
                        left = Remove(other, left);
                    }
                shortBy += left;
                lines.Add($"- {(def?.label ?? (string)take.Attribute("def"))} x{count - left}");
            }
            var given = new List<Thing>();
            foreach (var ix in m.x.Element("given")?.Elements("item") ?? Enumerable.Empty<XElement>())
                given.AddRange(ItemData.MakeAll(ix));
            LookTargets look = LookTargets.Invalid;
            if (given.Count > 0 && map != null) look = new LookTargets(Arrival.DropAt(map, given), map);
            var text = "RF_TradeDealText".Translate(Roster.NameOf(m.From), lines.Count > 0 ? string.Join("\n", lines) : "-",
                given.Count > 0 ? string.Join(", ", given.Select(g => g.LabelCap.ToString())) : "-");
            if (shortBy > 0) text += "\n\n" + "RF_TradeShort".Translate(shortBy);
            Find.LetterStack.ReceiveLetter("RF_TradeDealLabel".Translate(), text, LetterDefOf.NeutralEvent, look);
        }

        static int Remove(Thing t, int count)
        {
            if (t == null || t.Destroyed || count <= 0) return count;
            int n = Mathf.Min(count, t.stackCount);
            if (n >= t.stackCount) t.Destroy(DestroyMode.Vanish);
            else t.SplitOff(n).Destroy(DestroyMode.Vanish);
            return count - n;
        }
    }

    /// The friend's colony as a trader: its goods are stand-ins for their stockpile.
    public class FriendTrader : ITrader
    {
        public readonly string friendId;
        readonly List<Thing> goods = new List<Thing>();
        readonly Dictionary<Thing, string> sourceIds = new Dictionary<Thing, string>();
        public readonly List<(string thingId, string def, int count)> bought = new List<(string, string, int)>();
        public readonly List<XElement> sold = new List<XElement>();

        public FriendTrader(string friendId, IEnumerable<XElement> items)
        {
            this.friendId = friendId;
            foreach (var x in items)
            {
                var t = ItemData.Make(x);
                if (t.def.defName != (string)x.Attribute("def")) continue; // unknown modded item: not offered
                goods.Add(t);
                sourceIds[t] = (string)x.Attribute("thing");
            }
        }

        public TraderKindDef TraderKind => DefDatabase<TraderKindDef>.GetNamed("RF_FriendTrader");
        public IEnumerable<Thing> Goods => goods;
        public int RandomPriceFactorSeed => friendId.GetHashCode();
        public string TraderName => Roster.NameOf(friendId);
        public bool CanTradeNow => true;
        public float TradePriceImprovementOffsetForPlayer => 0f;
        public Faction Faction => RemoteFactions.Get(friendId);
        public TradeCurrency TradeCurrency => TradeCurrency.Silver;

        public IEnumerable<Thing> ColonyThingsWillingToBuy(Pawn playerNegotiator)
        {
            var caravan = playerNegotiator.GetCaravan();
            if (caravan == null) return Enumerable.Empty<Thing>();
            // system:DlcCheck: only offer what the friend's game can load
            var friend = Roster.Get(friendId);
            return CaravanInventoryUtility.AllInventoryItems(caravan).Where(t => Dlc.Missing(friend, new[] { ItemData.Capture(t) }).Count == 0);
        }

        public void GiveSoldThingToTrader(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            var thing = toGive.SplitOff(countToGive);
            sold.Add(ItemData.Capture(thing));
            if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
        }

        public void GiveSoldThingToPlayer(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            sourceIds.TryGetValue(toGive, out var id);
            var thing = toGive.SplitOff(countToGive);
            if (thing == toGive) goods.Remove(toGive);
            bought.Add((id, toGive.def.defName, countToGive));
            var caravan = playerNegotiator.GetCaravan();
            if (caravan != null) CaravanInventoryUtility.GiveThing(caravan, thing);
        }
    }

    /// Trades every item.
    public class StockGenerator_Everything : StockGenerator
    {
        public override IEnumerable<Thing> GenerateThings(PlanetTile forTile, Faction faction = null) => Enumerable.Empty<Thing>();
        public override bool HandlesThingDef(ThingDef thingDef) => thingDef.tradeability != Tradeability.None;
    }

    public class CaravanArrivalAction_TradeFriend : CaravanArrivalAction
    {
        FriendColony colony;

        public CaravanArrivalAction_TradeFriend() { }
        public CaravanArrivalAction_TradeFriend(FriendColony colony) { this.colony = colony; }

        public override string Label => "RF_TradeWith".Translate(colony?.Label ?? "?");
        public override string ReportString => "CaravanTrading".Translate(colony?.Label ?? "?");

        public static FloatMenuAcceptanceReport CanTrade(FriendColony colony)
        {
            var f = colony?.Friend;
            if (f == null) return false;
            if (!Rules.Active.tradeCaravans) return FloatMenuAcceptanceReport.WithFailMessage("RF_FeatureOff".Translate());
            if (!f.online) return FloatMenuAcceptanceReport.WithFailMessage("RF_TradeOffline".Translate(f.Label));
            if (Roster.Kind(Link.MyId, f.id) == "Hostile") return FloatMenuAcceptanceReport.WithFailMessage("RF_TradeHostile".Translate(f.Label));
            return Link.Connected;
        }

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            var r = base.StillValid(caravan, destinationTile);
            if (!r) return r;
            if (colony == null || colony.Destroyed || colony.Tile != destinationTile) return false;
            return CanTrade(colony);
        }

        public override void Arrived(Caravan caravan) => Trade.Request(caravan, colony.friendId);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref colony, "colony");
        }
    }
}
