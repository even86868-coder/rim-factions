using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:StartingKit — in a together colony every player starts with the scenario's starting things, not
    /// only the host: the same silver, food, medicine, resources, animals and mechs the scenario gives the host,
    /// dropped by pod next to that player's colonists (once per player).
    public static class StartingKit
    {
        /// Gives this player their kit, unless they had it already. Runs in every game at once (callers are synced or
        /// run before the session opens).
        public static void Give(Map map, string owner, string scenario = null)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (map == null || owner == null || comp.kits.Contains(owner) || Find.Scenario == null) return;
            comp.kits.Add(owner);
            var things = Make(ScenarioFromText(scenario) ?? Find.Scenario);   // the player's own scenario (feature:OwnScenario)
            if (things.Count == 0) return;
            var near = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => comp.OwnerOf(p) == owner)?.Position ?? DropCellFinder.TradeDropSpot(map);
            DropPodUtility.DropThingsNear(near, map, things, forbid: false);
        }

        /// A scenario as the text of a scenario file, to send between games.
        public static string ScenarioToText(Scenario scen)
        {
            if (scen == null) return null;
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimFactions-out.rsc");
                GameDataSaveLoader.SaveScenario(scen.CopyForEditing(), path);
                return System.IO.File.ReadAllText(path);
            }
            catch (System.Exception e) { Log.Warning("[Rim Factions] couldn't write a scenario: " + e.Message); return null; }
        }

        public static Scenario ScenarioFromText(string text)
        {
            if (text.NullOrEmpty()) return null;
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimFactions-in.rsc");
                System.IO.File.WriteAllText(path, text);
                return GameDataSaveLoader.TryLoadScenario(path, ScenarioCategory.CustomLocal, out var scen) ? scen : null;
            }
            catch (System.Exception e) { Log.Warning("[Rim Factions] couldn't read a scenario: " + e.Message); return null; }
        }

        static List<Thing> Make(Scenario scenario)
        {
            var things = new List<Thing>();
            foreach (var part in scenario.AllParts)
            {
                try
                {
                    if (part is ScenPart_ScatterThings)
                    {
                        // resources the scenario scatters near the start (steel, wood, ...): the same amount again
                        if (!(part is ScenPart_ScatterThingsNearPlayerStart)) continue;
                        var t = Traverse.Create(part);
                        var def = t.Field("thingDef").GetValue<ThingDef>();
                        var stuff = t.Field("stuff").GetValue<ThingDef>();
                        int count = t.Field("count").GetValue<int>();
                        while (def != null && count > 0)
                        {
                            var thing = ThingMaker.MakeThing(def, stuff);
                            thing.stackCount = Mathf.Min(count, def.stackLimit);
                            count -= thing.stackCount;
                            things.Add(thing.TryMakeMinified());
                        }
                    }
                    else things.AddRange(part.PlayerStartingThings());
                }
                catch (System.Exception e) { Log.Warning($"[Rim Factions] starting kit: skipped {part.GetType().Name}: {e.Message}"); }
            }
            return things;
        }
    }

    /// feature:OwnColonists tools: see whose a colonist is, take unowned colonists, and (host) hand colonists over.
    public static class Ownership
    {
        [SyncMethod]
        public static void SetOwners(List<Pawn> pawns, string owner, bool onlyUnowned)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            foreach (var p in pawns.Where(p => p != null && (!onlyUnowned || comp.OwnerOf(p) == null)))
                comp.SetOwner(p, owner);
        }

        public static List<Pawn> SelectedColonists() =>
            Find.Selector.SelectedPawns.Where(p => p.IsColonist).ToList();

        /// Buttons for the Rim Factions window during a shared session.
        public static void Buttons(Listing_Standard l)
        {
            if (!Live.InSession || MpBridge.MyUsername == null) return;
            var sel = SelectedColonists();
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (sel.Any(p => comp.OwnerOf(p) == null) && l.ButtonText("RF_ClaimSelected".Translate(sel.Count(p => comp.OwnerOf(p) == null))))
                SetOwners(sel, MpBridge.MyUsername, true);
            if (MpBridge.Hosting && sel.Count > 0)
                foreach (var name in MP.GetPlayers().Select(pl => pl.Username).Where(n => !n.NullOrEmpty()).Distinct())
                    if (l.ButtonText("RF_GiveSelected".Translate(sel.Count, name))) SetOwners(sel, name, false);
        }

        /// feature:PlayerColors in a shared game: each player's color, kept in the game under their session name.
        [SyncMethod]
        public static void SetColor(string owner, string hex)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (!owner.NullOrEmpty() && !hex.NullOrEmpty()) comp.ownerColors[owner] = hex;
        }

        /// Every few seconds in a session: make sure the game knows my color.
        public static void PushMyColor()
        {
            var me = MpBridge.MyUsername;
            var mine = Lobby.MyColor;
            var comp = Current.Game?.GetComponent<RFGameComp>();
            if (me == null || mine == null || comp == null) return;
            var hex = ColorUtility.ToHtmlStringRGB(mine.Value);
            if (!comp.ownerColors.TryGetValue(me, out var have) || have != hex) SetColor(me, hex);
        }

        /// Logged once per session, so a player's log shows who owns what.
        public static void LogOwners()
        {
            var comp = Current.Game?.GetComponent<RFGameComp>();
            if (comp == null) return;
            Log.Message("[Rim Factions] colonist owners: " + string.Join(", ",
                comp.ownedPawns.Select((p, i) => $"{p?.LabelShort}={comp.owners[i]}")) + $" (me: {MpBridge.MyUsername}, rule: {Rules.Active.ownColonistsOnly})");
        }
    }

    /// The owning player in the colonist's info panel.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
    static class OwnerInInspect
    {
        static void Postfix(Pawn __instance, ref string __result)
        {
            var owner = Current.Game?.GetComponent<RFGameComp>()?.OwnerOf(__instance);
            if (owner != null) __result = (__result.NullOrEmpty() ? "" : __result + "\n") + "RF_OwnerLine".Translate(owner);
        }
    }
}

namespace RimFactions
{
    /// feature:SharedWork — with personal colonists the Work tab shows only yours; colonists nobody owns get their
    /// own "Shared work" tab that every player can use.
    public class MainTabWindow_SharedWork : MainTabWindow_Work
    {
        protected override System.Collections.Generic.IEnumerable<Pawn> Pawns
        {
            get
            {
                var comp = Current.Game.GetComponent<RFGameComp>();
                return base.Pawns.Where(p => comp.OwnerOf(p) == null);
            }
        }
    }

    public class MainButtonWorker_SharedWork : MainButtonWorker_ToggleTab
    {
        public override bool Visible => base.Visible && OwnColonists.Active;
    }
}
