using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace RimFactions
{
    /// fix:ColonyNames — "give your faction a name" (and naming a settlement) opens in every player's game, but the
    /// answer only changed the game of the player who typed it; everyone else kept the window, stuck. In a live game
    /// the name now goes to everyone, and once anyone has named it the window closes for all. The window also gets
    /// a close button there, so it can never lock a player in.
    public static class ColonyNames
    {
        public static void Init(Harmony h)
        {
            if (!MP.enabled) return;
            MP.RegisterSyncMethod(typeof(NamePlayerFactionDialogUtility), nameof(NamePlayerFactionDialogUtility.Named));
            MP.RegisterSyncMethod(typeof(NamePlayerSettlementDialogUtility), nameof(NamePlayerSettlementDialogUtility.Named));
            var closeAll = new HarmonyMethod(typeof(ColonyNames), nameof(CloseNamingWindows));
            h.Patch(AccessTools.Method(typeof(NamePlayerFactionDialogUtility), nameof(NamePlayerFactionDialogUtility.Named)), postfix: closeAll);
            h.Patch(AccessTools.Method(typeof(NamePlayerSettlementDialogUtility), nameof(NamePlayerSettlementDialogUtility.Named)), postfix: closeAll);
            var escape = new HarmonyMethod(typeof(ColonyNames), nameof(AllowClosing));
            foreach (var t in new[] { typeof(Dialog_NamePlayerFaction), typeof(Dialog_NamePlayerSettlement), typeof(Dialog_NamePlayerFactionAndSettlement) })
                foreach (var ctor in AccessTools.GetDeclaredConstructors(t))
                    h.Patch(ctor, postfix: escape);
        }

        static bool Naming(Window w) => w is Dialog_NamePlayerFaction || w is Dialog_NamePlayerSettlement || w is Dialog_NamePlayerFactionAndSettlement;

        /// Runs in every player's game once the name is in.
        static void CloseNamingWindows()
        {
            if (!MP.IsInMultiplayer || Find.WindowStack == null) return;
            foreach (var w in Find.WindowStack.Windows.Where(Naming).ToList())
                Find.WindowStack.TryRemove(w, doCloseSound: false);
        }

        static void AllowClosing(Window __instance)
        {
            if (!MP.IsInMultiplayer) return;
            __instance.doCloseX = true;
            __instance.closeOnCancel = true;
        }
    }
}
