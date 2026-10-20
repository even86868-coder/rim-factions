using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RimFactions
{
    /// system:LoadTrace — while a save loads, each step (the game's, every map part, game part and world part from
    /// every mod) is logged when it starts and when it finishes, with how long it took. If loading freezes, the last
    /// "load step" line without a "done" names the mod that is stuck. Only while loading; nothing is logged in play.
    public static class LoadTrace
    {
        static readonly Dictionary<MethodBase, Stopwatch> running = new Dictionary<MethodBase, Stopwatch>();

        static bool Loading => Current.ProgramState != ProgramState.Playing;

        public static void Init(Harmony h)
        {
            var targets = new List<MethodBase>
            {
                AccessTools.Method(typeof(Game), nameof(Game.LoadGame)),
                AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)),
                AccessTools.Method(typeof(Map), nameof(Map.FinalizeLoading)),
                AccessTools.Method(typeof(Map), nameof(Map.FinalizeInit)),
                AccessTools.Method(typeof(World), nameof(World.FinalizeInit)),
            };
            // every mod's map, game and world parts
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsGenericTypeDefinition) continue;
                    try
                    {
                        if (typeof(MapComponent).IsAssignableFrom(t)) targets.Add(Declared(t, "FinalizeInit"));
                        else if (typeof(GameComponent).IsAssignableFrom(t)) { targets.Add(Declared(t, "FinalizeInit")); targets.Add(Declared(t, "LoadedGame")); }
                        else if (typeof(WorldComponent).IsAssignableFrom(t)) targets.Add(Declared(t, "FinalizeInit"));
                    }
                    catch { }
                }
            }
            int n = 0;
            foreach (var m in targets.Where(m => m != null && m.GetMethodBody() != null).Distinct())
                try { h.Patch(m, prefix: new HarmonyMethod(typeof(LoadTrace), nameof(Start)), finalizer: new HarmonyMethod(typeof(LoadTrace), nameof(End))); n++; }
                catch { }
            Log.Message($"[Rim Factions] load tracing ready ({n} load steps)");
        }

        static MethodInfo Declared(Type t, string name) => t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);

        static void Start(MethodBase __originalMethod)
        {
            if (!Loading) return;
            Log.Message($"[Rim Factions] load step: {Name(__originalMethod)}");
            running[__originalMethod] = Stopwatch.StartNew();
        }

        static Exception End(MethodBase __originalMethod, Exception __exception)
        {
            if (running.TryGetValue(__originalMethod, out var sw))
            {
                running.Remove(__originalMethod);
                if (sw.ElapsedMilliseconds >= 200 || __exception != null)
                    Log.Message($"[Rim Factions] load step done: {Name(__originalMethod)} in {sw.ElapsedMilliseconds} ms{(__exception != null ? " (failed: " + __exception.Message + ")" : "")}");
            }
            return __exception;
        }

        static string Name(MethodBase m) => $"{m.DeclaringType?.Assembly.GetName().Name}: {m.DeclaringType?.FullName}.{m.Name}";
    }
}
