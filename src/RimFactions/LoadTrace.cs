using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RimFactions
{
    /// system:LoadTrace — finds what freezes the game. Each load step (the game's, Multiplayer's, and every mod's map,
    /// game and world parts) and each part of a frame is tracked while it runs. Load steps are logged when they start
    /// and, if slow, when they finish. A watchdog thread notices when the game stops drawing frames for 15 seconds and
    /// logs which steps it is inside, the loading screen's task, and what is queued to run after loading. Frame parts
    /// are never logged unless the game is stuck.
    public static class LoadTrace
    {
        struct Step { public MethodBase method; public long started; }

        static readonly List<Step> inside = new List<Step>();
        static readonly HashSet<MethodBase> loud = new HashSet<MethodBase>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long lastFrame = -1;
        public static volatile string driverPart;
        static string pendingAfterLoad = "";

        static FieldInfo currentEvent, eventText, toExecuteWhenFinished;

        public static void Init(Harmony h)
        {
            // the watchdog first, so it runs even if a trace below can't be set up
            new Thread(Watch) { IsBackground = true, Name = "RimFactions watchdog" }.Start();
            // logged when they start: the steps of loading a save, the game's and Multiplayer's
            var load = new List<IEnumerable<MethodBase>>
            {
                M("Verse.Root_Play", "Start"),
                M("Verse.Profile.MemoryUtility", "ClearAllMapsAndWorld"),
                M("Verse.SavedGameLoaderNow", "LoadGameFromSaveFileNow"),
                M("Verse.ScribeLoader", "InitLoading"),
                M("Verse.ScribeLoader", "FinalizeLoading"),
                M("Verse.CrossRefHandler", "ResolveAllCrossReferences"),
                M("Verse.PostLoadIniter", "DoAllPostLoadInits"),
                M("Verse.Game", "LoadGame"),
                M("Verse.Game", "FinalizeInit"),
                M("Verse.Map", "FinalizeLoading"),
                M("Verse.Map", "FinalizeInit"),
                M("RimWorld.Planet.World", "FinalizeInit"),
                M("Multiplayer.Client.Replay", "LoadReplay"),
                M("Multiplayer.Client.Replay", "LoadInfo"),
                M("Multiplayer.Client.Replay", "LoadGameData"),
                M("Multiplayer.Client.Saving.Loader", "ReloadGame"),
                M("Multiplayer.Client.SaveLoad", "LoadInMainThread"),
                M("Multiplayer.Client.SaveLoad", "ClearState"),
                M("Multiplayer.Client.TickPatch", "SetSimulation"),
                M("Multiplayer.Client.HostUtil", "HostServer"),
            };
            // tracked silently: big pieces of reading a save and of every frame
            var quiet = new List<IEnumerable<MethodBase>>
            {
                M("Verse.Game", "ExposeSmallComponents"),
                M("RimWorld.Planet.World", "ExposeData"),
                M("RimWorld.Planet.World", "ExposeComponents"),
                M("Verse.Map", "ExposeData"),
                M("Verse.Map", "ExposeComponents"),
                M("Verse.Root_Play", "Update"),
                M("Verse.Root_Entry", "Update"),
                M("Verse.LongEventHandler", "LongEventsUpdate"),
                M("Verse.LongEventHandler", "ExecuteToExecuteWhenFinished"),
                M("Verse.TickManager", "TickManagerUpdate"),
                M("Verse.TickManager", "DoSingleTick"),
                M("Verse.MapInterface", "MapInterfaceUpdate"),
                M("Verse.UIRoot_Play", "UIRootOnGUI"),
                M("Verse.WindowStack", "WindowStackOnGUI"),
                M("Verse.WorldInterface", "WorldInterfaceUpdate"),
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
                        if (typeof(MapComponent).IsAssignableFrom(t)) { load.Add(One(t, "FinalizeInit")); quiet.Add(One(t, "ExposeData")); }
                        else if (typeof(GameComponent).IsAssignableFrom(t)) { load.Add(One(t, "FinalizeInit")); load.Add(One(t, "LoadedGame")); quiet.Add(One(t, "ExposeData")); }
                        else if (typeof(WorldComponent).IsAssignableFrom(t)) { load.Add(One(t, "FinalizeInit")); quiet.Add(One(t, "ExposeData")); }
                    }
                    catch { }
                }
            }
            foreach (var m in load.SelectMany(x => x)) if (m != null) loud.Add(m);
            int n = 0;
            foreach (var m in load.Concat(quiet).SelectMany(x => x).Where(m => m != null && m.GetMethodBody() != null).Distinct())
                try { h.Patch(m, prefix: new HarmonyMethod(typeof(LoadTrace), nameof(Start)), finalizer: new HarmonyMethod(typeof(LoadTrace), nameof(End))); n++; }
                catch { }

            currentEvent = AccessTools.Field(typeof(LongEventHandler), "currentEvent");
            eventText = currentEvent == null ? null : AccessTools.Field(currentEvent.FieldType, "eventText");
            toExecuteWhenFinished = AccessTools.Field(typeof(LongEventHandler), "toExecuteWhenFinished");
            foreach (var pre in M("Verse.LongEventHandler", "ExecuteToExecuteWhenFinished"))
                try { h.Patch(pre, prefix: new HarmonyMethod(typeof(LoadTrace), nameof(NoteAfterLoad))); } catch { }

            Log.Message($"[Rim Factions] load tracing ready ({n} steps, watchdog on)");
        }

        /// Every version (overload) of a method; none when the type or method isn't there.
        static IEnumerable<MethodBase> M(string type, string name)
        {
            try
            {
                var t = AccessTools.TypeByName(type);
                if (t != null)
                    return t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == name).Cast<MethodBase>().ToList();
            }
            catch { }
            return Enumerable.Empty<MethodBase>();
        }

        static IEnumerable<MethodBase> One(Type t, string name) { var m = Declared(t, name); return m == null ? Enumerable.Empty<MethodBase>() : new[] { m }; }

        static MethodInfo Declared(Type t, string name) => t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);

        /// Called by the mod's per-frame driver: the game is drawing frames.
        public static void Frame()
        {
            FreezeProbe.NoteMainThread();
            Interlocked.Exchange(ref lastFrame, clock.ElapsedMilliseconds);
            if (savedTraces != null && Current.ProgramState == ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting) StackTracesBack();
        }

        // Unity can loop forever while building the stack trace of a message logged in the middle of loading a save
        // (seen loading Multiplayer saves with many mods). While a save loads, messages are logged without one.
        static Dictionary<UnityEngine.LogType, UnityEngine.StackTraceLogType> savedTraces;

        static void NoStackTraces()
        {
            if (savedTraces != null) return;
            try
            {
                savedTraces = new Dictionary<UnityEngine.LogType, UnityEngine.StackTraceLogType>();
                foreach (UnityEngine.LogType lt in Enum.GetValues(typeof(UnityEngine.LogType)))
                {
                    savedTraces[lt] = UnityEngine.Application.GetStackTraceLogType(lt);
                    UnityEngine.Application.SetStackTraceLogType(lt, UnityEngine.StackTraceLogType.None);
                }
                Log.Message("[Rim Factions] loading: messages are logged without stack traces until the game is ready");
            }
            catch (Exception e) { Log.Warning("[Rim Factions] stack traces: " + e.Message); }
        }

        static void StackTracesBack()
        {
            var saved = savedTraces;
            savedTraces = null;
            try { foreach (var kv in saved) UnityEngine.Application.SetStackTraceLogType(kv.Key, kv.Value); } catch { }
            Log.Message("[Rim Factions] game ready: stack traces back on");
        }

        static void Start(MethodBase __originalMethod)
        {
            if (loud.Contains(__originalMethod) && savedTraces == null && UnityEngine.Application.isPlaying && FreezeProbe.OnMainThread) NoStackTraces();
            if (loud.Contains(__originalMethod)) Log.Message($"[Rim Factions] load step: {Name(__originalMethod)}");
            lock (inside) inside.Add(new Step { method = __originalMethod, started = clock.ElapsedMilliseconds });
        }

        static Exception End(MethodBase __originalMethod, Exception __exception)
        {
            long took = -1;
            lock (inside)
                for (int i = inside.Count - 1; i >= 0; i--)
                    if (inside[i].method == __originalMethod) { took = clock.ElapsedMilliseconds - inside[i].started; inside.RemoveRange(i, inside.Count - i); break; }
            if (loud.Contains(__originalMethod) && (took >= 200 || __exception != null))
                Log.Message($"[Rim Factions] load step done: {Name(__originalMethod)} in {took} ms{(__exception != null ? " (failed: " + __exception.Message + ")" : "")}");
            return __exception;
        }

        static void NoteAfterLoad()
        {
            try
            {
                if (toExecuteWhenFinished?.GetValue(null) is List<Action> list && list.Count > 0)
                    pendingAfterLoad = string.Join(", ", list.Select(a => Name(a.Method)).Distinct().Take(30));
            }
            catch { }
        }

        static void Watch()
        {
            long reportedFor = -1, lastReport = 0;
            while (true)
            {
                Thread.Sleep(2000);
                try
                {
                    long last = Interlocked.Read(ref lastFrame);
                    if (last < 0) continue;
                    long now = clock.ElapsedMilliseconds, stuck = now - last;
                    if (stuck < 15000) { if (reportedFor == last) Log.Message($"[Rim Factions] watchdog: the game is moving again"); reportedFor = -1; continue; }
                    if (reportedFor == last && now - lastReport < 30000) continue;
                    reportedFor = last; lastReport = now;
                    Log.Warning("[Rim Factions] watchdog: " + Report(now, stuck) + "\n  " + FreezeProbe.Sample());
                }
                catch (Exception e) { Log.Warning("[Rim Factions] watchdog: " + e.Message); }
            }
        }

        static string Report(long now, long stuck)
        {
            string steps;
            lock (inside) steps = inside.Count == 0 ? "none of the tracked steps" : string.Join(" > ", inside.Select(s => $"{Name(s.method)} ({(now - s.started) / 1000}s)"));
            string task = null;
            try { var ev = currentEvent?.GetValue(null); task = ev == null ? null : eventText?.GetValue(ev) as string; } catch { }
            string ticks = null;
            try { ticks = Current.Game?.tickManager?.TicksGame.ToString(); } catch { }
            return $"no frame for {stuck / 1000}s. State {Current.ProgramState}, game tick {ticks ?? "-"}, loading task \"{task ?? "-"}\"" +
                   $", Rim Factions part {driverPart ?? "-"}. Inside: {steps}. Last queued after loading: {(pendingAfterLoad.Length > 0 ? pendingAfterLoad : "-")}";
        }

        static string Name(MethodBase m) => m == null ? "?" : $"{m.DeclaringType?.Assembly.GetName().Name}: {m.DeclaringType?.FullName}.{m.Name}";
    }
}
