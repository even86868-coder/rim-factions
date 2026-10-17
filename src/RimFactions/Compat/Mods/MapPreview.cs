using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace RimFactions.Compat.Mods
{
    /// Map Preview (m00nl1ght): it draws a preview of a world tile on a background thread by running map generation,
    /// which rolls the game's shared dice from that thread. Only the player looking at the world map does this, so the
    /// games' dice drift apart (Desync-11). While live, previews aren't generated; solo play keeps them.
    [CompatFor("m00nl1ght.mappreview")]
    public static class MapPreview
    {
        public static void Init()
        {
            var m = AccessTools.Method("MapPreview.MapPreviewGenerator:GeneratePreview");
            if (m == null) { Log.Warning("[Rim Factions] Map Preview: generator not found"); return; }
            new Harmony("melty.rimfactions.compat.mappreview").Patch(m, prefix: new HarmonyMethod(typeof(MapPreview), nameof(NotLive)));
        }

        static bool NotLive() => !MP.IsInMultiplayer;
    }
}
