using System;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using Verse;
using Verse.AI;

namespace RimFactions.Compat.Mods
{
    /// Smarter Construction (dhultgren): it keeps caches that aren't saved (work priorities, "is this colonist stuck",
    /// enclosed rooms), so a friend who joins starts with empty ones while the host's are hours old, and their games
    /// pick different jobs (Desync-06/07: a patient found a doctor in one game and not the other). The bundled
    /// Multiplayer Compatibility patch no longer fits. While live its job changes are off and pawns choose work the
    /// vanilla way; solo play keeps the whole mod.
    [CompatFor("dhultgren.smarterconstruction")]
    public static class SmarterConstruction
    {
        static bool Live => MP.IsInMultiplayer;

        public static void Init()
        {
            LiveSettings.Fix(() => AccessTools.Property("SmarterConstruction.SmarterConstruction:Settings").GetValue(null), ("EnableCaching", false));
            var h = new Harmony("melty.rimfactions.compat.smarterconstruction");
            HarmonyMethod Of(string name) => new HarmonyMethod(typeof(SmarterConstruction), name);
            void Patch(string target, string prefix)
            {
                var m = AccessTools.Method(target);
                if (m != null) h.Patch(m, prefix: Of(prefix));
                else Log.Warning("[Rim Factions] Smarter Construction: " + target + " not found");
            }
            Patch("SmarterConstruction.Patches.Patch_WorkGiver_Scanner_GetPriority:PriorityPostfix", nameof(SkipWhenLive));
            Patch("SmarterConstruction.Patches.WorkGiver_ConstructFinishFrames_JobOnThing:Postfix", nameof(SkipWhenLive));
            Patch("SmarterConstruction.Patches.TranspilerHelper:EndJobIfEnclosing", nameof(NeverEndJob));
            Patch("SmarterConstruction.Patches.Patch_PawnsGettingStuck:Postfix", nameof(ToilsUnchanged));
            Patch("SmarterConstruction.Patches.CustomGenClosest:ClosestThing_Global_Reachable_Custom", nameof(VanillaClosest));
        }

        static bool SkipWhenLive() => !Live;

        static bool NeverEndJob(ref bool __result)
        {
            if (!Live) return true;
            __result = false;
            return false;
        }

        static bool ToilsUnchanged(IEnumerable<Toil> __0, ref IEnumerable<Toil> __result)
        {
            if (!Live) return true;
            __result = __0;
            return false;
        }

        static bool VanillaClosest(IntVec3 __0, Map __1, IEnumerable<Thing> __2, PathEndMode __3, TraverseParms __4, float __5,
            Predicate<Thing> __6, Func<Thing, float> __7, bool __8, ref Thing __result)
        {
            if (!Live) return true;
            __result = GenClosest.ClosestThing_Global_Reachable(__0, __1, __2, __3, __4, __5, __6, __7, __8);
            return false;
        }
    }
}
