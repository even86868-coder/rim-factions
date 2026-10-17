using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimFactions.Net;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:GiftItems and feature:GiftColonist. Gifts travel between separate games over the link.
    public static class Gifts
    {
        public static bool Usable => Link.Connected && !Live.InSession && Roster.OthersWithColony.Any();

        /// "Which colony?" menu.
        /// payload: what would travel, so friends whose game lacks an expansion it needs are greyed out
        /// with the reason (system:DlcCheck).
        public static void ChooseFriend(Action<Friend> then, IEnumerable<XElement> payload = null)
        {
            var sending = payload?.ToList();
            var options = Roster.OthersWithColony.Select(f =>
            {
                var o = new FloatMenuOption($"{f.Label}{(f.online ? "" : " " + "RF_OfflineSuffix".Translate())}", () => then(f), BaseContent.WhiteTex, f.color);
                var missing = sending == null ? new List<string>() : Dlc.Missing(f, sending);
                if (missing.Count > 0) { o.Disabled = true; o.Label += " — " + Dlc.Reason(f, missing); }
                return o;
            }).ToList();
            if (options.Count == 0) { Messages.Message("RF_NoOtherColonies".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        // news:GiftSent
        public static void GiftItems(List<Thing> things, Friend to)
        {
            things = things.Where(t => t.Spawned && !t.Destroyed).ToList();
            if (things.Count == 0) return;
            float value = things.Sum(t => t.MarketValue * t.stackCount);
            var summary = things.Count == 1 ? things[0].LabelCap.ToString() : "RF_ItemStacks".Translate(things.Count, things.Sum(t => t.stackCount)).ToString();
            var items = things.Select(ItemData.Capture).ToList();
            foreach (var t in things) t.Destroy(DestroyMode.Vanish);

            Link.Send(new Msg("Gift", to.id, new XElement("items", items)));
            Roster.ChangeGoodwill(to.id, Mathf.Min(RFSettings.GiftGoodwillCap, Mathf.RoundToInt(value * RFSettings.GoodwillPerSilver)), "gift");
            NewsFeed.Send("GiftSent", new XElement("from", Link.MyId), new XElement("to", to.id),
                new XElement("summary", summary), new XElement("value", value.ToStringMoney()));
            Messages.Message("RF_GiftSent".Translate(to.Label), MessageTypeDefOf.PositiveEvent, false);
        }

        // news:ColonistGifted
        public static void GiftColonist(Pawn pawn, Friend to)
        {
            var data = PawnData.Capture(pawn);
            var name = pawn.LabelShort;
            pawn.DeSpawn();
            pawn.SetFaction(RemoteFactions.Get(to.id));
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            Link.Send(new Msg("Gift", to.id, new XElement("pawns", data)));
            Roster.ChangeGoodwill(to.id, RFSettings.ColonistGiftGoodwill, "colonist");
            NewsFeed.Send("ColonistGifted", new XElement("from", Link.MyId), new XElement("to", to.id), new XElement("pawn", name));
        }

        public static void Receive(Msg m)
        {
            var map = Arrival.HomeMap;
            var things = new List<Thing>();
            foreach (var ix in m.x.Element("items")?.Elements("item") ?? Enumerable.Empty<XElement>())
                things.AddRange(ItemData.MakeAll(ix));
            foreach (var px in m.x.Element("pawns")?.Elements("pawn") ?? Enumerable.Empty<XElement>())
            {
                var p = PawnData.Make(px, Faction.OfPlayer);
                things.Add(p);
            }
            if (things.Count == 0) return;
            var spot = Arrival.DropAt(map, things);
            Find.LetterStack.ReceiveLetter("RF_GiftArrivedLabel".Translate(), "RF_GiftArrivedText".Translate(Roster.NameOf(m.From),
                string.Join(", ", things.Select(t => t.LabelCap.ToString()))), LetterDefOf.PositiveEvent, new LookTargets(spot, map));
        }
    }

    /// One click per selection; the action gathers every selected item.
    public class Command_Once : Command_Action
    {
        public Command_Once() { alsoClickIfOtherInGroupClicked = false; }
    }

    [HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.GetGizmos))]
    static class ItemGiftGizmo
    {
        static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, ThingWithComps __instance)
        {
            foreach (var g in gizmos) yield return g;
            var t = __instance;
            if (t is Pawn || t is Corpse || t.def.category != ThingCategory.Item || !t.Spawned || !t.def.EverHaulable || !t.Map.IsPlayerHome || !Gifts.Usable) yield break;

            yield return new Command_Once
            {
                defaultLabel = "RF_GiftItems".Translate(),
                defaultDesc = "RF_GiftItemsDesc".Translate(),
                icon = RFTex.Gift,
                action = () =>
                {
                    var things = Find.Selector.SelectedObjects.OfType<Thing>()
                        .Where(x => !(x is Pawn) && !(x is Corpse) && x.def.category == ThingCategory.Item && x.Spawned && x.Map == t.Map).ToList();
                    Gifts.ChooseFriend(f => Gifts.GiftItems(things, f), things.Select(ItemData.Capture));
                }
            };
        }
    }

    /// Colonist gizmos: gift, send on a trip (hook:ColonistGizmos), and leave a together session (feature:SplitOff).
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    static class ColonistGizmos
    {
        static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (var g in gizmos) yield return g;
            var p = __instance;
            if (!p.Spawned || p.Faction != Faction.OfPlayer) yield break;

            if (Live.InSession)
            {
                foreach (var g in Live.Gizmos(p)) yield return g;
                yield break;
            }
            // Biotech: mechanitor mechs go on trips too (not as gifts: they need their mechanitor)
            if (!(p.IsFreeColonist || p.IsColonyMech) || p.Downed || !Gifts.Usable) yield break;

            if (p.IsFreeColonist)
                yield return new Command_Action
                {
                    defaultLabel = "RF_GiftColonist".Translate(),
                    defaultDesc = "RF_GiftColonistDesc".Translate(p.LabelShort),
                    icon = RFTex.Gift,
                    action = () => Gifts.ChooseFriend(f => Gifts.GiftColonist(p, f), new[] { PawnData.Capture(p) })
                };
            yield return new Command_Once
            {
                defaultLabel = "RF_SendTrip".Translate(),
                defaultDesc = "RF_SendTripDesc".Translate(),
                icon = RFTex.Trip,
                action = () =>
                {
                    var pawns = Find.Selector.SelectedPawns.Where(x => x.Spawned && x.Faction == Faction.OfPlayer && (x.IsFreeColonist || x.IsColonyMech) && !x.Downed && x.Map == p.Map).ToList();
                    Trips.Choose(pawns);
                }
            };
        }
    }
}
