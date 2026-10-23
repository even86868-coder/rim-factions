using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimFactions
{
    /// feature:KeepColonists — a player's own colonists never leave the colony for good through a mental break: no
    /// "giving up and leaving" (fed up with civilization) and no running wild. They break in another way instead
    /// (the game picks a different break). Colonists nobody owns, and dev mode's forced breaks, work as usual.
    public static class KeepColonists
    {
        static bool Leaves(Def def) => def != null && (def.defName == "GiveUpExit" || def.defName == "RunWild");

        static bool Owned(Pawn pawn) => pawn != null && Current.Game?.GetComponent<RFGameComp>()?.OwnerOf(pawn) != null;

        /// The game won't choose these breaks for an owned colonist, so it picks another one.
        [HarmonyPatch(typeof(MentalBreakWorker), nameof(MentalBreakWorker.BreakCanOccur))]
        static class NotThisBreak
        {
            static void Postfix(MentalBreakWorker __instance, Pawn pawn, ref bool __result)
            {
                if (__result && (Leaves(__instance.def) || Leaves(__instance.def?.mentalState)) && Owned(pawn)) __result = false;
            }
        }

        /// Any other way into these states (events, mods) is refused too, unless forced from dev mode.
        [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
        static class NotThisState
        {
            static bool Prefix(MentalStateHandler __instance, MentalStateDef stateDef, bool forced, ref bool __result, Pawn ___pawn)
            {
                if (forced || !Leaves(stateDef) || !Owned(___pawn)) return true;
                __result = false;
                return false;
            }
        }
    }
}
