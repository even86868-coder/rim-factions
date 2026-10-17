using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace RimFactions.Compat.Mods
{
    /// Drafted Auto-Combat (RedMattis): its hunt / cover / charge / AI / ability buttons changed one game only.
    /// Every toggle now goes through Multiplayer's sync, and its gameplay settings are the defaults while live.
    /// Also covers the copy built into Big and Small (same package id and types).
    [CompatFor("RedMattis.AutoCombat")]
    public static class DraftedAutoCombat
    {
        static readonly string[] toggles = { "ToggleCoverMode", "ToggleMeleeCharge", "ToggleHuntMode", "ToggleFullAIControl", "ToggleAutoForAll", "ToggleAutoCastFor" };
        static MethodInfo getData;
        static Type dataType;

        public static void Init()
        {
            dataType = AccessTools.TypeByName("BigAndSmall.DraftedActionData");
            getData = AccessTools.Method("BigAndSmall.DraftedActionHolder:GetData");
            var harmony = new Harmony("melty.rimfactions.compat.autocombat");
            foreach (var name in toggles)
                harmony.Patch(AccessTools.Method(dataType, name), prefix: new HarmonyMethod(typeof(DraftedAutoCombat), nameof(TogglePrefix)));
            // Standalone: BS.settings; built into Big and Small: BS.Settings. Gameplay ones get the defaults.
            var bs = AccessTools.TypeByName("BigAndSmall.BS");
            LiveSettings.Fix(() => AccessTools.Field(bs, "settings")?.GetValue(null) ?? AccessTools.Property(bs, "Settings")?.GetValue(null),
                ("enableDraftedJobs", false), ("autoCombatResets", false), ("autoCombatResetsLongCharge", false));
        }

        static bool TogglePrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface) return true;
            var pawn = Traverse.Create(__instance).Property("Pawn").GetValue<Pawn>();
            if (pawn == null) return false;
            int force = -1;
            string ability = null;
            if (__args.Length == 1 && __args[0] is bool b) force = b ? 1 : 0;
            if (__args.Length == 1 && __args[0] is Def def) ability = def.defName;
            Toggle(pawn, Array.IndexOf(toggles, __originalMethod.Name), force, ability);
            return false;
        }

        [SyncMethod]
        public static void Toggle(Pawn pawn, int which, int force, string ability)
        {
            if (which < 0 || pawn == null) return;
            var data = getData.Invoke(null, new object[] { pawn });
            var method = AccessTools.Method(dataType, toggles[which]);
            object[] args = method.GetParameters().Length == 0 ? new object[0]
                : which == 5 ? new object[] { DefDatabase<AbilityDef>.GetNamedSilentFail(ability) }
                : new object[] { force < 0 ? null : (object)(force == 1) };
            method.Invoke(data, args);
        }
    }
}
