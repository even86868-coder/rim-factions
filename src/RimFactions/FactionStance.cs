using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:FactionStance — a "Players" row at the top of RimWorld's Factions tab: one button per other player,
    /// showing how my colony stands with theirs, to set it to friendly, neutral or hostile. It goes through the
    /// planet's host, so both players' games, the player list and everyone's news agree. (Friends' colonies are hidden
    /// factions, which the tab's own list never shows, so the row sits above it. The host can set any pair:
    /// feature:RelationCheat.)
    [HarmonyPatch(typeof(FactionUIUtility), nameof(FactionUIUtility.DoWindowContents))]
    static class FactionStance
    {
        static void Postfix(Rect fillRect)
        {
            if (!Link.Connected) return;
            var others = Roster.Players.Where(p => !p.IsMe).ToList();
            if (others.Count == 0) return;
            var row = new Rect(fillRect.x + 10f, fillRect.y + 6f, fillRect.width - 20f, 30f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.x, row.y, 80f, row.height), "RF_StancePlayers".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            float x = row.x + 84f;
            foreach (var f in others)
            {
                var r = new Rect(x, row.y, 220f, row.height);
                if (r.xMax > row.xMax) break;
                var now = Roster.Kind(Link.MyId, f.id);
                Widgets.DrawBoxSolid(new Rect(r.x, r.y + 4f, 5f, r.height - 8f), f.color);
                if (Widgets.ButtonText(new Rect(r.x + 8f, r.y, r.width - 8f, r.height), $"{f.Label}: {("RF_Kind_" + now).Translate()}"))
                    Find.WindowStack.Add(new FloatMenu(Options(f, now)));
                x = r.xMax + 8f;
            }
        }

        static List<FloatMenuOption> Options(Friend f, string now)
        {
            var opts = new List<FloatMenuOption>();
            foreach (var kind in new[] { "Ally", "Neutral", "Hostile" })
            {
                var k = kind;
                opts.Add(new FloatMenuOption(("RF_Stance_" + kind).Translate(f.Label), () =>
                    Link.Send(new Msg("SetRelation", "hub", new XElement("a", Link.MyId), new XElement("b", f.id), new XElement("kind", k))))
                { Disabled = kind == now });
            }
            return opts;
        }
    }
}
