using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace RimFactions.Compat.Mods
{
    /// Fortified Features Framework (the Dead Man's Switch mods): its list of lost mechs (which come back to their
    /// overseer later) was trimmed whenever the game was saved, and only in the game doing the saving. Multiplayer saves
    /// in one player's game (autosaves, the copy sent to a joining friend), so the players' lists drifted apart, and
    /// when the return timer ran out one game rolled the dice and the other didn't (Desync-13). While live, saving no
    /// longer changes the list; instead every game drops gone or dead mechs from it at the same moment each tick.
    [CompatFor("aoba.framework")]
    public static class Fortified
    {
        static AccessTools.FieldRef<object, List<Thing>> lostMechs;

        public static void Init()
        {
            var type = AccessTools.TypeByName("Fortified.GameComponent_DMS");
            if (type == null) return;
            lostMechs = AccessTools.FieldRefAccess<List<Thing>>(type, "lostMechs");
            var h = new Harmony("melty.rimfactions.compat.fortified");
            h.Patch(AccessTools.Method(type, "Distinct"), prefix: new HarmonyMethod(typeof(Fortified), nameof(SameEverywhere)));
            h.Patch(AccessTools.Method(type, "GameComponentTick"), prefix: new HarmonyMethod(typeof(Fortified), nameof(TidyEachTick)));
        }

        static bool Live => MP.IsInMultiplayer;

        /// Replaces the trim done on save and on load: the same rule every tick uses.
        static bool SameEverywhere(ref List<Thing> target)
        {
            if (!Live) return true;
            target = Tidy(target);
            return false;
        }

        static void TidyEachTick(object __instance)
        {
            if (!Live) return;
            ref var list = ref lostMechs(__instance);
            if (list == null || list.Count == 0) return;
            if (list.Any(Gone) || list.Distinct().Count() != list.Count) list = Tidy(list);
        }

        static bool Gone(Thing t) => t == null || t.Destroyed || (t is Pawn p && p.Dead) || !(t is Pawn);

        static List<Thing> Tidy(List<Thing> list)
        {
            var kept = new List<Thing>();
            if (list == null) return kept;
            foreach (var t in list)
                if (!Gone(t) && !kept.Contains(t)) kept.Add(t);
            return kept;
        }
    }
}
