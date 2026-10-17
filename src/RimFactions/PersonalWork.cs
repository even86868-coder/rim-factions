using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:PersonalWork — each player can switch on "my colonists only work on my orders". Blueprints (and the
    /// frames they become), designations (mine, chop, harvest, deconstruct, hunt, tame, haul...) and workbench bills
    /// remember which player ordered them; with the switch on, that player's colonists skip everyone else's orders.
    /// Work nobody ordered (hauling, cleaning, firefighting...) stays everyone's, and shared colonists work on anything.
    /// Who ordered what is taken from the Multiplayer command being run, so every game records the same thing.
    public static class PersonalWork
    {
        /// The player whose Multiplayer command is running right now (null outside commands).
        public static string Issuer;

        public static void Init(Harmony h)
        {
            foreach (var type in new[] { "Multiplayer.Client.AsyncTimeComp", "Multiplayer.Client.AsyncTime.AsyncWorldTimeComp" })
            {
                var m = AccessTools.Method(AccessTools.TypeByName(type), "ExecuteCmd");
                if (m == null) { Log.Warning("[Rim Factions] personal work: " + type + " not found"); continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(PersonalWork), nameof(CmdStart)), finalizer: new HarmonyMethod(typeof(PersonalWork), nameof(CmdEnd)));
            }
        }

        static void CmdStart(object cmd)
        {
            Issuer = null;
            try
            {
                int id = Traverse.Create(cmd).Field("playerId").GetValue<int>();
                if (id < 0) return;
                var session = Traverse.Create(AccessTools.TypeByName("Multiplayer.Client.Multiplayer")).Field("session").GetValue();
                var info = session == null ? null : Traverse.Create(session).Method("GetPlayerInfo", id).GetValue();
                Issuer = info == null ? null : Traverse.Create(info).Field("username").GetValue<string>();
            }
            catch { Issuer = null; }
        }

        static System.Exception CmdEnd(System.Exception __exception) { Issuer = null; return __exception; }

        static RFGameComp Comp => Current.Game?.GetComponent<RFGameComp>();

        // ---- recording orders ----

        public static void Record(string key)
        {
            var comp = Comp;
            if (Issuer == null || comp == null || !Live.InSession || !comp.togetherOpen) return;
            comp.orderOwners[key] = Issuer;
        }

        public static string ThingKey(Thing t) => "t" + t.thingIDNumber;
        static string CellKey(Map map, IntVec3 c) => $"c{map.uniqueID}:{c.x}:{c.z}";
        public static string BillKey(Bill b) => "b" + b.GetUniqueLoadID();

        public static void RecordDesignation(Designation d)
        {
            if (d?.designationManager?.map == null) return;
            Record(d.target.HasThing ? ThingKey(d.target.Thing) : CellKey(d.designationManager.map, d.target.Cell));
        }

        // ---- who may work on what ----

        /// This colonist's owner keeps it off other players' orders: returns that owner, else null. Only a colonist's
        /// own player decides this; nobody can stop someone else's colonists from helping them.
        static string Restricted(Pawn pawn)
        {
            var comp = Comp;
            if (pawn == null || comp == null || (comp.personalWork.Count == 0 && comp.noHelp.Count == 0) || !Live.InSession) return null;
            var owner = comp.OwnerOf(pawn);
            return owner != null && (comp.personalWork.Contains(owner) || comp.noHelp.Contains(pawn)) ? owner : null;
        }

        public static bool Helps(Pawn pawn) => Restricted(pawn) == null;

        /// One colonist: does it work on other players' orders too?
        [SyncMethod]
        public static void SetHelps(Pawn pawn, bool helps)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            var owner = comp.OwnerOf(pawn);
            if (owner == null) return;
            if (comp.personalWork.Remove(owner))   // the all-colonists switch becomes per colonist
                for (int i = 0; i < comp.ownedPawns.Count; i++)
                    if (comp.owners[i] == owner && comp.ownedPawns[i] != null && !comp.noHelp.Contains(comp.ownedPawns[i])) comp.noHelp.Add(comp.ownedPawns[i]);
            if (helps) comp.noHelp.Remove(pawn);
            else if (!comp.noHelp.Contains(pawn)) comp.noHelp.Add(pawn);
        }

        static bool OthersOrder(string key, string me)
        {
            return Comp.orderOwners.TryGetValue(key, out var who) && who != me;
        }

        /// Things another player ordered are "forbidden" to my colonists while my switch is on.
        public static bool BlockedThing(Thing t, Pawn pawn)
        {
            var me = Restricted(pawn);
            if (me == null || t == null) return false;
            return OthersOrder(ThingKey(t), me);   // a blueprint, frame or designated thing
        }

        public static bool BlockedCell(IntVec3 c, Pawn pawn)
        {
            var me = Restricted(pawn);
            if (me == null || pawn.Map == null) return false;
            // only solid cells (mining); walkable cells stay open so nobody gets stuck
            if (c.Walkable(pawn.Map)) return false;
            return OthersOrder(CellKey(pawn.Map, c), me);
        }

        public static bool BlockedBill(Bill bill, Pawn pawn)
        {
            var me = Restricted(pawn);
            return me != null && OthersOrder(BillKey(bill), me);
        }

        // ---- the switch ----

        [SyncMethod]
        public static void SetPersonal(string owner, bool on)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (owner.NullOrEmpty()) return;
            if (on) comp.personalWork.Add(owner); else comp.personalWork.Remove(owner);
            comp.noHelp.RemoveAll(p => comp.OwnerOf(p) == owner);   // the switch sets all my colonists
        }

        public static bool IsOn(string owner) => Comp?.personalWork.Contains(owner) == true;

        /// A checkbox for the Rim Factions window and the Work tab.
        public static void Toggle(Rect r)
        {
            var me = MpBridge.MyUsername;
            if (me == null || !Live.InSession || Comp?.togetherOpen != true) return;
            bool on = IsOn(me), was = on;
            Widgets.CheckboxLabeled(r, "RF_PersonalWork".Translate(), ref on);
            TooltipHandler.TipRegion(r, "RF_PersonalWorkDesc".Translate());
            if (on != was) SetPersonal(me, on);
        }
    }

    // ---- recording hooks (run inside Multiplayer commands) ----

    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild))]
    static class OrderBlueprint { static void Postfix(Blueprint_Build __result) { if (__result != null) PersonalWork.Record(PersonalWork.ThingKey(__result)); } }

    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForInstall))]
    static class OrderInstall { static void Postfix(Blueprint_Install __result) { if (__result != null) PersonalWork.Record(PersonalWork.ThingKey(__result)); } }

    /// A blueprint becoming a frame keeps its orderer (this runs during ticks, not commands).
    [HarmonyPatch(typeof(Blueprint), nameof(Blueprint.TryReplaceWithSolidThing))]
    static class OrderFrame
    {
        static void Postfix(Blueprint __instance, ref Thing createdThing)
        {
            var comp = Current.Game?.GetComponent<RFGameComp>();
            if (comp == null || createdThing == null) return;
            if (comp.orderOwners.TryGetValue(PersonalWork.ThingKey(__instance), out var who)) comp.orderOwners[PersonalWork.ThingKey(createdThing)] = who;
        }
    }

    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
    static class OrderDesignation { static void Postfix(Designation newDes) => PersonalWork.RecordDesignation(newDes); }

    [HarmonyPatch(typeof(BillStack), nameof(BillStack.AddBill))]
    static class OrderBill { static void Postfix(Bill bill) { if (bill != null) PersonalWork.Record(PersonalWork.BillKey(bill)); } }

    // ---- restriction hooks ----

    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), typeof(Thing), typeof(Pawn))]
    static class PersonalWorkThing { static void Postfix(Thing t, Pawn pawn, ref bool __result) { if (!__result && PersonalWork.BlockedThing(t, pawn)) __result = true; } }

    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), typeof(IntVec3), typeof(Pawn))]
    static class PersonalWorkCell { static void Postfix(IntVec3 c, Pawn pawn, ref bool __result) { if (!__result && PersonalWork.BlockedCell(c, pawn)) __result = true; } }

    [HarmonyPatch(typeof(Bill), nameof(Bill.PawnAllowedToStartAnew))]
    static class PersonalWorkBill { static void Postfix(Bill __instance, Pawn p, ref bool __result) { if (__result && PersonalWork.BlockedBill(__instance, p)) __result = false; } }

    /// The switch at the top of the Work tab.
    [HarmonyPatch(typeof(MainTabWindow_Work), nameof(MainTabWindow_Work.DoWindowContents))]
    static class PersonalWorkTab
    {
        static void Postfix(Rect rect) => PersonalWork.Toggle(new Rect(rect.xMax - 360f, rect.y, 340f, 24f));
    }

    /// feature:PersonalWork — the Work tab's "Help others" column: for each of my colonists, whether it also works on
    /// orders other players gave. Other players' colonists show no box: that's their choice.
    public class PawnColumnWorker_HelpOthers : PawnColumnWorker_Checkbox
    {
        protected override bool HasCheckbox(Pawn pawn) =>
            Live.InSession && Current.Game?.GetComponent<RFGameComp>() is RFGameComp comp && comp.togetherOpen && comp.OwnerOf(pawn) == MpBridge.MyUsername;

        protected override bool GetValue(Pawn pawn) => PersonalWork.Helps(pawn);

        protected override void SetValue(Pawn pawn, bool value, PawnTable table) => PersonalWork.SetHelps(pawn, value);

        protected override string GetTip(Pawn pawn) => "RF_HelpOthersTip".Translate();
    }
}

namespace RimFactions
{
    /// feature:PersonalWork — blueprints and frames say which player ordered them.
    [HarmonyPatch(typeof(Blueprint), nameof(Blueprint.GetInspectString))]
    static class OrderedByBlueprint { static void Postfix(Thing __instance, ref string __result) => __result = OrderedBy.Add(__instance, __result); }

    [HarmonyPatch(typeof(Frame), nameof(Frame.GetInspectString))]
    static class OrderedByFrame { static void Postfix(Thing __instance, ref string __result) => __result = OrderedBy.Add(__instance, __result); }

    static class OrderedBy
    {
        public static string Add(Thing t, string text)
        {
            var comp = Current.Game?.GetComponent<RFGameComp>();
            if (comp == null || !comp.orderOwners.TryGetValue(PersonalWork.ThingKey(t), out var who)) return text;
            var line = "RF_OrderedBy".Translate(who).Colorize(Lobby.ColorOf(who));
            return text.NullOrEmpty() ? line : text + "\n" + line;
        }
    }
}
