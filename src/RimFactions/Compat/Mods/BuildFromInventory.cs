using System.Collections;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace RimFactions.Compat.Mods
{
    /// Build From Inventory (MemeGoddess): it remembers, per tick, whether a resource is in anyone's inventory, but
    /// forgets only at the end of a tick. Right-clicking a blueprint asks the same question from the player's screen
    /// between ticks, so that player's game kept an answer the other game didn't have, and a pawn (a mech, in
    /// Desync-15) chose different work. While live the memory is also cleared at the start of each tick, as vanilla
    /// does with its own, so every game decides from the same state.
    [CompatFor("memegoddess.buildfrominventory")]
    public static class BuildFromInventory
    {
        static System.Type compType;
        static AccessTools.FieldRef<object, IDictionary> cached;
        static AccessTools.FieldRef<ItemAvailability, Map> mapOf;

        public static void Init()
        {
            compType = AccessTools.TypeByName("Build_From_Inventory.ItemInvAvailabilityMapComp");
            if (compType == null) return;
            cached = AccessTools.FieldRefAccess<IDictionary>(compType, "cachedResults");
            mapOf = AccessTools.FieldRefAccess<ItemAvailability, Map>("map");
            new Harmony("melty.rimfactions.compat.buildfrominventory")
                .Patch(AccessTools.Method(typeof(ItemAvailability), nameof(ItemAvailability.Tick)), postfix: new HarmonyMethod(typeof(BuildFromInventory), nameof(ClearAtTickStart)));
        }

        static void ClearAtTickStart(ItemAvailability __instance)
        {
            if (!MP.IsInMultiplayer) return;
            var map = mapOf(__instance);
            if (map == null) return;
            foreach (var comp in map.components)
                if (comp != null && comp.GetType() == compType) cached(comp)?.Clear();
        }
    }
}
