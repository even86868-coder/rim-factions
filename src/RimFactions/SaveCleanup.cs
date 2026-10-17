using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using Verse;

namespace RimFactions
{
    /// system:SaveCleanup — a save loaded after a mod was removed (Vehicle Framework, say) keeps empty slots where that
    /// mod's map parts, areas and game parts were. RimWorld then trips over them every tick: path-finding fails and
    /// nobody can walk. On every load those empty slots are cleared out. It only removes things that are already
    /// gone, uses no randomness, and runs the same way in every player's game.
    public static class SaveCleanup
    {
        public static void Run()
        {
            int removed = 0;
            var game = Current.Game;
            if (game == null) return;
            removed += Clean(game.components);
            if (game.World != null) removed += Clean(game.World.components);
            foreach (var map in game.Maps)
            {
                removed += Clean(map.components);
                var areas = map.areaManager?.AllAreas;
                if (areas != null && areas.Any(a => a == null))
                {
                    removed += areas.RemoveAll(a => a == null);
                    try { HarmonyLib.AccessTools.Method(typeof(AreaManager), "UpdateAllAreasLinks")?.Invoke(map.areaManager, null); }
                    catch (System.Exception e) { Log.Warning("[Rim Factions] area links: " + e.Message); }
                }
            }
            if (removed > 0)
                Log.Message($"[Rim Factions] cleared {removed} broken parts left in this save by a removed mod");
        }

        static int Clean<T>(List<T> list) where T : class => list?.RemoveAll(x => x == null) ?? 0;
    }
}
