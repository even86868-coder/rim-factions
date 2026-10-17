using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace RimFactions.Compat
{
    /// system:LiveCompat — Rim Factions' own compatibility pack for live play (live visits, playing together).
    ///
    /// Two ways to add a mod, so newer mods slot in easily:
    ///  1. A LiveCompatDef in Defs/LiveCompat/*.xml: its live-play status, a note for players, and simple
    ///     fixes (methods, fields, lambdas to keep in step) — no code needed.
    ///  2. A class in src/RimFactions/Compat/Mods/ marked [CompatFor("package.id")] with a static Init(),
    ///     for anything that needs real patching. It only runs when that mod is loaded.
    /// Each mod is set up on its own: one that fails is logged and the rest carry on.
    public class LiveCompatDef : Def
    {
        public string packageId;
        /// safe, covered (bundled Multiplayer Compatibility or host-settings sync handles it), patched (Rim Factions
        /// patches it), caution (works, with a note), unsafe (players are warned before live play)
        public string status = "covered";
        public string note;
        public List<string> syncMethods = new List<string>();   // "Namespace.Type:Method"
        public List<string> syncFields = new List<string>();    // "Namespace.Type:field.path"
        public List<string> syncLambdas = new List<string>();   // "Namespace.Type:ParentMethod:ordinal"

        public bool Active => ModLister.GetActiveModWithIdentifier(packageId, ignorePostfix: true) != null;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class CompatForAttribute : Attribute
    {
        public readonly string packageId;
        public CompatForAttribute(string packageId) { this.packageId = packageId; }
    }

    public static class LiveCompat
    {
        public static readonly Dictionary<string, string> results = new Dictionary<string, string>();

        public static LiveCompatDef For(string packageId) =>
            DefDatabase<LiveCompatDef>.AllDefsListForReading.FirstOrDefault(d => string.Equals(d.packageId, packageId, StringComparison.OrdinalIgnoreCase));

        /// Called once at startup, after defs are loaded.
        public static void Init()
        {
            if (!MP.enabled) return;
            foreach (var def in DefDatabase<LiveCompatDef>.AllDefsListForReading.Where(d => d.Active))
            {
                try
                {
                    foreach (var s in def.syncMethods)
                    {
                        var p = s.Split(':');
                        MP.RegisterSyncMethod(AccessTools.TypeByName(p[0]), p[1]);
                    }
                    foreach (var s in def.syncFields)
                    {
                        var p = s.Split(':');
                        MP.RegisterSyncField(AccessTools.TypeByName(p[0]), p[1]);
                    }
                    foreach (var s in def.syncLambdas)
                    {
                        var p = s.Split(':');
                        MP.RegisterSyncMethodLambda(AccessTools.TypeByName(p[0]), p[1], int.Parse(p[2]));
                    }
                    if (def.syncMethods.Count + def.syncFields.Count + def.syncLambdas.Count > 0) results[def.packageId] = "ok";
                }
                catch (Exception e)
                {
                    results[def.packageId] = "failed: " + e.Message;
                    Log.Warning($"[Rim Factions] live compatibility for {def.packageId} failed: {e.Message}");
                }
            }

            foreach (var type in typeof(LiveCompat).Assembly.GetTypes())
            {
                var attr = type.GetCustomAttributes(typeof(CompatForAttribute), false).OfType<CompatForAttribute>().FirstOrDefault();
                if (attr == null || ModLister.GetActiveModWithIdentifier(attr.packageId, ignorePostfix: true) == null) continue;
                try
                {
                    AccessTools.Method(type, "Init")?.Invoke(null, null);
                    results[attr.packageId] = "ok";
                }
                catch (Exception e)
                {
                    results[attr.packageId] = "failed: " + (e.InnerException?.Message ?? e.Message);
                    Log.Warning($"[Rim Factions] live compatibility for {attr.packageId} failed: {e}");
                }
            }
            try { AutoSync.Init(); } catch (Exception e) { Log.Warning("[Rim Factions] auto-sync failed: " + e.Message); }
            if (results.Count > 0) Log.Message("[Rim Factions] live compatibility: " + string.Join(", ", results.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        /// Active mods players should be warned about before live play.
        public static List<LiveCompatDef> UnsafeActive() =>
            DefDatabase<LiveCompatDef>.AllDefsListForReading.Where(d => d.status == "unsafe" && d.Active).ToList();

        /// Runs `go` now, or after a warning that names the mods which can break live play.
        public static void ConfirmLive(Action go)
        {
            var bad = UnsafeActive();
            if (bad.Count == 0) { go(); return; }
            var lines = string.Join("\n", bad.Select(d => $"- {d.LabelCap}: {d.note}"));
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_LiveUnsafeWarn".Translate(lines), go, destructive: true));
        }
    }
}
