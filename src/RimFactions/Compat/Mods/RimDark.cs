using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace RimFactions.Compat.Mods
{
    /// RimDark 40k framework (Core40k): ranks and armor/weapon customization were changed in one player's game only.
    /// A friend's new rank vanished on every resync (the host's game never had it), and customized armor showed
    /// the old look to everyone else.
    ///  - Unlocking and removing ranks go to every game.
    ///  - The customizer edits the gear right away in the editing player's game (that's its preview); on Accept, the
    ///    finished look of each edited piece is sent to every game, then the same accept step (pay, start the work,
    ///    or apply at once) runs everywhere.
    [CompatFor("phonicmas.rimdark.frameworkcore")]
    public static class RimDark
    {
        const string Utility = "DecorationWorkUtility";
        static Type utility;
        static bool applying;

        public static void Init()
        {
            int n = 0;
            var rankComp = AccessTools.TypeByName("Core40k.CompRankInfo") ?? AccessTools.TypeByName("CompRankInfo");
            if (rankComp != null)
            {
                n += SyncMethods.Register(AccessTools.Method(rankComp, "UnlockRank", new[] { AccessTools.TypeByName("Core40k.RankDef") ?? AccessTools.TypeByName("RankDef"), typeof(bool) }));
                n += SyncMethods.Register(AccessTools.Method(rankComp, "RemoveRank"));
            }

            utility = AccessTools.TypeByName("Core40k." + Utility) ?? AccessTools.TypeByName(Utility);
            if (utility != null)
            {
                var h = new Harmony("melty.rimfactions.compat.rimdark");
                var commit = AccessTools.Method(utility, "CommitImmediately");
                var capture = AccessTools.Method(utility, "CaptureAll");
                if (commit != null) { h.Patch(commit, prefix: new HarmonyMethod(typeof(RimDark), nameof(CommitPrefix))); n++; }
                if (capture != null) { h.Patch(capture, prefix: new HarmonyMethod(typeof(RimDark), nameof(CapturePrefix))); n++; }
                foreach (var settle in new[] { "SettleFromMap", "SettleFromInventory", "DiscardAll", "CommitAll" })
                    n += SyncMethods.Register(AccessTools.Method(utility, settle));
            }
            Log.Message($"[Rim Factions] RimDark 40k: synced {n} rank and customization actions");
        }

        static bool Interface => MP.IsInMultiplayer && MP.InInterface && !applying;

        // ---- Accept with no cost or work: apply at once, everywhere ----

        static bool CommitPrefix(Pawn pawn, System.Collections.IList comps)
        {
            if (!Interface) return true;
            var (things, types, xmls) = Snapshot(comps);
            ApplyAndCommit(pawn, things, types, xmls, false);
            return false;
        }

        // ---- Accept with cost or work: remember the new look everywhere (the paying / work steps are synced above) ----

        static bool CapturePrefix(System.Collections.IList comps)
        {
            if (!Interface) return true;
            var (things, types, xmls) = Snapshot(comps);
            var pawn = things.OfType<Thing>().Select(t => t.ParentHolder as Pawn_ApparelTracker).FirstOrDefault(a => a != null)?.pawn
                       ?? things.OfType<Thing>().Select(t => t.ParentHolder as Pawn_EquipmentTracker).FirstOrDefault(e => e != null)?.pawn;
            ApplyAndCommit(pawn, things, types, xmls, true);
            return false;
        }

        [SyncMethod]
        public static void ApplyAndCommit(Pawn pawn, List<Thing> things, List<string> types, List<string> xmls, bool capture)
        {
            var comps = new List<ThingComp>();
            for (int i = 0; i < things.Count; i++)
            {
                var comp = (things[i] as ThingWithComps)?.AllComps.FirstOrDefault(c => c.GetType().FullName == types[i]);
                if (comp == null) continue;
                Load(comp, xmls[i]);
                comps.Add(comp);
            }
            if (comps.Count == 0) return;
            // the mod's own step, with the list type it expects
            var listType = typeof(List<>).MakeGenericType(AccessTools.TypeByName("Core40k.CompGraphicParent") ?? AccessTools.TypeByName("CompGraphicParent"));
            var list = (System.Collections.IList)Activator.CreateInstance(listType);
            foreach (var c in comps) list.Add(c);
            applying = true;
            try
            {
                if (capture) AccessTools.Method(utility, "CaptureAll").Invoke(null, new object[] { list });
                else AccessTools.Method(utility, "CommitImmediately").Invoke(null, new object[] { pawn, list });
            }
            finally { applying = false; }
            pawn?.Drawer?.renderer?.SetAllGraphicsDirty();
            foreach (var t in things) (t as ThingWithComps)?.Notify_ColorChanged();
        }

        // ---- a comp's saved data as text, and back ----

        static (List<Thing>, List<string>, List<string>) Snapshot(System.Collections.IList comps)
        {
            var things = new List<Thing>(); var types = new List<string>(); var xmls = new List<string>();
            foreach (ThingComp c in comps)
            {
                if (c?.parent == null) continue;
                things.Add(c.parent); types.Add(c.GetType().FullName); xmls.Add(Save(c));
            }
            return (things, types, xmls);
        }

        static string TempFile => Path.Combine(Path.GetTempPath(), "RimFactions-comp.xml");

        static string Save(ThingComp comp)
        {
            Scribe.saver.InitSaving(TempFile, "comp");
            try { comp.PostExposeData(); }
            finally { Scribe.saver.FinalizeSaving(); }
            return File.ReadAllText(TempFile);
        }

        static void Load(ThingComp comp, string xml)
        {
            try
            {
                File.WriteAllText(TempFile, xml);
                Scribe.loader.InitLoading(TempFile);
                try { comp.PostExposeData(); }
                finally { Scribe.loader.FinalizeLoading(); }
            }
            catch (Exception e) { Log.Warning($"[Rim Factions] RimDark: couldn't apply {comp.parent}'s look: {e.Message}"); }
        }
    }
}
