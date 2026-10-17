using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using Verse;
using Verse.AI;

namespace RimFactions.Compat.Mods
{
    /// Melee Animation (Epicguru): its executions, grapples, lassos, duels and unique skills are decided on
    /// background threads and land on the frame the animation is drawn, which differs between players' games.
    /// While live it runs in "animations only" mode: melee looks the same, but fights resolve the normal way.
    /// Solo play and AI trips keep everything.
    [CompatFor("co.uk.epicguru.meleeanimation")]
    public static class MeleeAnimation
    {
        public static void Init()
        {
            var h = new Harmony("melty.rimfactions.compat.meleeanimation");
            HarmonyMethod Of(string name) => new HarmonyMethod(typeof(MeleeAnimation), name);

            // The threaded scan that starts executions and grapples.
            h.Patch(AccessTools.Method("AM.Processing.MapPawnProcessor:Tick"), prefix: Of(nameof(NotLive)));
            // Drafted right-click options: execute, lasso, duel, skills.
            h.Patch(AccessTools.Method("AM.UI.DraftedFloatMenuOptionsUI:GenerateMenuOptions"), postfix: Of(nameof(NoOptions)));
            // Friendly duels for fun, and watching them.
            h.Patch(AccessTools.Method("AM.AutoDuel.JoyGiver_FriendlyDuel:TryGiveJob"), prefix: Of(nameof(NoJob)));
            h.Patch(AccessTools.Method("AM.AutoDuel.JoyGiver_SpectateFriendlyDuel:TryGiveJob"), prefix: Of(nameof(NoJob)));

            LiveSettings.Fix(() => AccessTools.Field("AM.Core:Settings").GetValue(null),
                ("EnableExecutions", false), ("AutoExecute", false), ("EnemiesCanExecute", false),
                ("AutoGrapple", false), ("EnemiesCanGrapple", false), ("LassoSpawnChance", 0f),
                ("EnableUniqueSkills", false), ("MultithreadedMatrixCalculations", false), ("MaxProcessingThreads", 1));
        }

        static bool Live => MP.IsInMultiplayer;

        static bool NotLive() => !Live;

        static IEnumerable<FloatMenuOption> NoOptions(IEnumerable<FloatMenuOption> __result) =>
            Live ? Enumerable.Empty<FloatMenuOption>() : __result;

        static bool NoJob(ref Job __result)
        {
            if (!Live) return true;
            __result = null;
            return false;
        }
    }
}
