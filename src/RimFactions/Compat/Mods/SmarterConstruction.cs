using HarmonyLib;

namespace RimFactions.Compat.Mods
{
    /// Smarter Construction (dhultgren): its newer versions keep per-game caches with random expiry times
    /// (System.Random, different in every game), and the bundled Multiplayer Compatibility patch no longer fits.
    /// While live its caching is off, so every game makes the same building decisions; solo play keeps it.
    [CompatFor("dhultgren.smarterconstruction")]
    public static class SmarterConstruction
    {
        public static void Init() =>
            LiveSettings.Fix(() => AccessTools.Property("SmarterConstruction.SmarterConstruction:Settings").GetValue(null), ("EnableCaching", false));
    }
}
