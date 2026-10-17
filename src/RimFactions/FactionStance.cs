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
    /// feature:FactionStance — in RimWorld's Factions tab, each other player's colony has a button to set how my colony
    /// stands with theirs: friendly, neutral or hostile. It goes through the planet's host, so both players' games,
    /// the player list and everyone's news agree. (The host can set any pair: feature:RelationCheat.)
    [HarmonyPatch(typeof(FactionUIUtility), "DrawFactionRow")]
    static class FactionStance
    {
        static void Postfix(Faction faction, float rowY, Rect fillRect, float __result)
        {
            if (faction == null || faction.def?.defName != "RF_RemoteColony" || !Link.Connected) return;
            var comp = Current.Game?.GetComponent<RFGameComp>();
            var friendId = comp?.remoteFactions.FirstOrDefault(kv => kv.Value == faction).Key;
            if (friendId == null || friendId == Link.MyId) return;
            var r = new Rect(fillRect.xMax - 150f, rowY + Mathf.Max(0f, (__result - 28f) / 2f), 140f, 28f);
            var now = Roster.Kind(Link.MyId, friendId);
            if (!Widgets.ButtonText(r, "RF_StanceButton".Translate(("RF_Kind_" + now).Translate()))) return;
            var opts = new List<FloatMenuOption>();
            foreach (var kind in new[] { "Ally", "Neutral", "Hostile" })
            {
                var k = kind;
                opts.Add(new FloatMenuOption(("RF_Stance_" + kind).Translate(Roster.NameOf(friendId)), () =>
                    Link.Send(new Msg("SetRelation", "hub", new XElement("a", Link.MyId), new XElement("b", friendId), new XElement("kind", k))))
                { Disabled = kind == now });
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }
}
