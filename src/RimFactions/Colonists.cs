using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimFactions.Net;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:MakeColonistsEarly — in a together lobby a friend makes their colonists while the host is still setting
    /// up the world: RimWorld's own new-game screens open, and pressing Start on the colonist page keeps the colonists
    /// (as PawnData) instead of starting a game. When the friend joins the host's session, those exact colonists are
    /// built in every game at once (synced) and drop into the colony as theirs.
    public static class EarlyColonists
    {
        /// I'm going through the new-game screens only to make my colonists.
        public static bool making;
        /// My finished colonists, waiting for the together session. Kept on disk too, so they survive the restart
        /// that takes the host's mod settings.
        public static List<string> mine
        {
            get
            {
                if (cache == null && System.IO.File.Exists(FilePath))
                    try { cache = XElement.Load(FilePath).Elements("pawn").Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList(); } catch { }
                return cache;
            }
            set
            {
                cache = value;
                try
                {
                    if (value == null) System.IO.File.Delete(FilePath);
                    else new XElement("pawns", value.Select(XElement.Parse)).Save(FilePath);
                }
                catch (System.Exception e) { Log.Warning("[Rim Factions] keeping made colonists: " + e.Message); }
            }
        }
        static List<string> cache;
        static string FilePath => System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RimFactions-my-colonists.xml");

        /// feature:LandTogether: my finished colonists go to the host's game, so they land with the host's own;
        /// sending them is my "ready".
        public static void SendToHost()
        {
            if (mine == null || Roster.HubId == null || Link.Role != LinkRole.Client) return;
            Link.Send(new Msg("MyColonists", Roster.HubId, new XElement("mp", MyMpName()), new XElement("pawns", mine.Select(XElement.Parse))));
            Lobby.SetReady(true);
        }

        /// The name Multiplayer will give me in the session (owners are kept by that name).
        static string MyMpName()
        {
            var settings = AccessTools.Field(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "settings")?.GetValue(null);
            var name = settings == null ? null : Traverse.Create(settings).Field("username").GetValue<string>();
            return name.NullOrEmpty() ? Link.MyName : name;
        }

        /// Straight to the colonist page: no scenario, storyteller, world or landing-site screens. Like RimWorld's own
        /// quick start, a small private setup is made in the background with the host's scenario and storyteller (they
        /// decide what kind of colonists you can make); only the colonists are kept, and they land in the host's world.
        public static void Begin()
        {
            making = true;
            Find.WindowStack.WindowOfType<LobbyWindow>()?.Close(false);
            LongEventHandler.QueueLongEvent(() =>
            {
                Current.ProgramState = ProgramState.Entry;
                Game.ClearCaches();
                Current.Game = new Game();
                Current.Game.InitData = new GameInitData();
                Current.Game.Scenario = (HostScenarioDef() ?? ScenarioDefOf.Crashlanded.scenario).CopyForEditing();
                Find.Scenario.PreConfigure();
                var teller = DefDatabase<StorytellerDef>.GetNamedSilentFail(Lobby.HostStoryteller ?? "") ?? StorytellerDefOf.Cassandra;
                var difficulty = DefDatabase<DifficultyDef>.GetNamedSilentFail(Lobby.HostDifficulty ?? "") ?? DifficultyDefOf.Rough;
                Current.Game.storyteller = new Storyteller(teller, difficulty);
                Current.Game.World = WorldGenerator.GenerateWorld(0.05f, GenText.RandomSeedString(), OverallRainfall.Normal,
                    OverallTemperature.Normal, OverallPopulation.Normal, LandmarkDensity.Normal);
                Find.GameInitData.ChooseRandomStartingTile();
                Find.GameInitData.mapSize = 250;
                Find.Scenario.PostIdeoChosen();
            }, "RF_EarlyPreparing", false, e => { making = false; Log.Error("[Rim Factions] preparing colonist creation failed: " + e); });
            LongEventHandler.ExecuteWhenFinished(() => { if (making) Find.WindowStack.Add(new Page_ConfigureStartingPawns()); });
        }

        static Scenario HostScenarioDef()
        {
            var name = Lobby.HostScenario;
            if (name.NullOrEmpty()) return null;
            return ScenarioLister.AllScenarios().FirstOrDefault(s => s.name == name);
        }

        /// Synced: my colonists from my creation screen join the colony, in every game identically.
        [SyncMethod]
        public static void Arrive(string owner, string pawnsXml)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (!comp.togetherOpen || comp.owners.Contains(owner)) return;
            var map = Find.Maps.Where(m => m.IsPlayerHome).OrderByDescending(m => m.mapPawns.FreeColonistsSpawnedCount).FirstOrDefault();
            if (map == null) return;
            var pawns = new List<Thing>();
            foreach (var x in XElement.Parse(pawnsXml).Elements("pawn").Take(Mathf.Max(1, RFSettings.TogetherStartPawns)))
            {
                var p = PawnData.Make(x, Faction.OfPlayer);
                comp.SetOwner(p, owner);
                pawns.Add(p);
            }
            if (pawns.Count > 0) DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, pawns, forbid: false);
            StartingKit.Give(map, owner);
        }

        /// Called once I'm in the session: send my early colonists, if I made any. True when it did.
        /// Synced: colonists that landed under the name I had before joining become mine under my session name.
        [SyncMethod]
        public static void Retag(string from, string to)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (from == to || comp.owners.Contains(to)) return;
            for (int i = 0; i < comp.owners.Count; i++) if (comp.owners[i] == from) comp.owners[i] = to;
        }

        public static bool TryArrive(string me)
        {
            // landed with the host's own colonists (feature:LandTogether), maybe under my pre-join name
            var sent = MyMpName();
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (sent != me && comp.owners.Contains(sent) && !comp.owners.Contains(me)) { Retag(sent, me); mine = null; return true; }
            if (comp.owners.Contains(me)) { mine = null; return true; }
            if (mine == null || mine.Count == 0) return false;
            var xml = new XElement("pawns", mine.Select(XElement.Parse)).ToString(SaveOptions.DisableFormatting);
            mine = null;   // used up (file removed)
            Arrive(me, xml);
            NewsFeed.Send("JoinedTogether", new XElement("player", me), new XElement("count", RFSettings.TogetherStartPawns));
            return true;
        }
    }

    /// The colonist page asks for as many colonists as the host chose for each player.
    [HarmonyPatch(typeof(Page_ConfigureStartingPawns), nameof(Page_ConfigureStartingPawns.PreOpen))]
    static class EarlyColonistsCount
    {
        static void Postfix()
        {
            if (Lobby.HostMakingTogether) Lobby.HostAtPawns();   // friends: make yours now (feature:LandTogether)
            if (!EarlyColonists.making || Find.GameInitData == null) return;
            int n = Mathf.Max(1, RFSettings.TogetherStartPawns);
            while (Find.GameInitData.startingAndOptionalPawns.Count < n) StartingPawnUtility.AddNewPawn();
            Find.GameInitData.startingPawnCount = n;
            Messages.Message("RF_EarlyHint".Translate(n), MessageTypeDefOf.NeutralEvent, false);
        }
    }

    /// Start on the colonist page: keep the colonists and go back to the main menu instead of starting a game.
    /// (Patched by hand in Init, so a renamed method only switches this off.)
    static class EarlyColonistsDone
    {
        public static void Init(Harmony h)
        {
            var m = AccessTools.Method(typeof(Page_ConfigureStartingPawns), "DoNext");
            if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(EarlyColonistsDone), nameof(Prefix)));
            else Log.Warning("[Rim Factions] colonist page not found; early colonists are off");
            HostPawnPage.Init(h);
            var pawns = AccessTools.PropertyGetter(typeof(MainTabWindow_PawnTable), "Pawns");
            if (pawns != null) h.Patch(pawns, postfix: new HarmonyMethod(typeof(OwnColonistsTables), "Postfix"));
        }

        static bool confirmed;

        static bool Prefix(Page_ConfigureStartingPawns __instance)
        {
            if (Lobby.HostMakingTogether) return HostStart(__instance);
            if (!EarlyColonists.making) return true;
            EarlyColonists.making = false;
            var init = Find.GameInitData;
            EarlyColonists.mine = init.startingAndOptionalPawns.Take(init.startingPawnCount)
                .Select(p => PawnData.Capture(p).ToString(SaveOptions.DisableFormatting)).ToList();
            EarlyColonists.SendToHost();
            Messages.Message("RF_EarlyReady".Translate(EarlyColonists.mine.Count), MessageTypeDefOf.PositiveEvent, false);
            GenScene.GoToMainMenu();
            return false;
        }

        /// feature:LandTogether: friends' colonists join the starting colonists, so everyone lands at once.
        static bool HostStart(Page_ConfigureStartingPawns page)
        {
            var waiting = Lobby.MemberColonists().Where(t => !t.ready).Select(t => t.f.player).ToList();
            if (waiting.Count > 0 && !confirmed)
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_PawnPageStartWithout".Translate(string.Join(", ", waiting)), () =>
                {
                    confirmed = true;
                    Traverse.Create(page).Method("DoNext").GetValue();
                }));
                return false;
            }
            confirmed = false;
            var init = Find.GameInitData;
            foreach (var (mpName, pawns) in Lobby.friendColonists.Values)
                foreach (var x in pawns.Take(Mathf.Max(1, RFSettings.TogetherStartPawns)))
                {
                    try
                    {
                        var p = PawnData.Make(x, Faction.OfPlayer);
                        init.startingAndOptionalPawns.Insert(init.startingPawnCount, p);
                        init.startingPawnCount++;
                        Lobby.pendingOwners[p] = mpName;
                    }
                    catch (System.Exception e) { Log.Warning("[Rim Factions] a friend's colonist couldn't be made: " + e.Message); }
                }
            return true;
        }
    }

    /// The host's colonist page: tells friends to make theirs, and shows who's ready.
    static class HostPawnPage
    {
        public static void Init(Harmony h)
        {
            var m = AccessTools.Method(typeof(Page_ConfigureStartingPawns), nameof(Page_ConfigureStartingPawns.DoWindowContents));
            if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(HostPawnPage), nameof(Postfix)));
        }

        static void Postfix(Rect rect)
        {
            if (!Lobby.HostMakingTogether) return;
            var list = Lobby.MemberColonists().ToList();
            if (list.Count == 0) return;
            var box = new Rect(rect.xMax - 300f, rect.y, 300f, 28f + list.Count * 24f);
            Widgets.DrawBoxSolid(box, new Color(0f, 0f, 0f, 0.6f));
            var r = box.ContractedBy(4f);
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), "RF_PawnPageFriends".Translate());
            float y = r.y + 24f;
            foreach (var (f, ready) in list)
            {
                GUI.color = ready ? new Color(0.5f, 0.9f, 0.5f) : Color.gray;
                Widgets.Label(new Rect(r.x, y, r.width, 24f), ready ? "RF_PawnPageReady".Translate(f.player) : "RF_PawnPageMaking".Translate(f.player));
                y += 24f;
            }
            GUI.color = Color.white;
        }
    }

    /// Backing out of the new-game screens ends colonist making.
    [HarmonyPatch(typeof(Page_SelectScenario), nameof(Page_SelectScenario.PreOpen))]
    static class EarlyColonistsReset
    {
        static void Postfix() { if (Current.ProgramState == ProgramState.Entry && Find.WindowStack.IsOpen<LobbyWindow>()) EarlyColonists.making = false; }
    }

    // ================= feature:OwnColonists =================

    /// In a shared colony each player runs their own colonists: other players' colonists can't be selected or
    /// given orders, and the Work / Schedule / Assign tabs list only yours. Colonists nobody owns (newcomers) are
    /// everyone's. The host's rules can switch this off.
    public static class OwnColonists
    {
        static float lastMessage;

        /// Personal colonists are on: a live session, the host's rule, and two or more players own colonists.
        public static bool Active
        {
            get
            {
                if (!Live.InSession || !Rules.Active.ownColonistsOnly || MpBridge.MyUsername == null) return false;
                var comp = Current.Game?.GetComponent<RFGameComp>();
                return comp != null && comp.owners.Distinct().Count() > 1;
            }
        }

        public static bool Mine(Pawn p) => Current.Game?.GetComponent<RFGameComp>()?.OwnerOf(p) == MpBridge.MyUsername;

        public static bool Blocked(Pawn p)
        {
            if (p == null || !Live.InSession || !Rules.Active.ownColonistsOnly) return false;
            var comp = Current.Game?.GetComponent<RFGameComp>();
            var owner = comp?.OwnerOf(p);
            var me = MpBridge.MyUsername;
            return owner != null && me != null && owner != me && comp.owners.Distinct().Count() > 1;
        }

        public static void Tell(Pawn p)
        {
            if (Time.realtimeSinceStartup - lastMessage < 2f) return;
            lastMessage = Time.realtimeSinceStartup;
            var owner = Current.Game.GetComponent<RFGameComp>().OwnerOf(p);
            Messages.Message("RF_NotYourColonist".Translate(p.LabelShortCap, owner), p, MessageTypeDefOf.RejectInput, false);
        }
    }

    [HarmonyPatch(typeof(Selector), nameof(Selector.Select))]
    static class OwnColonistsSelect
    {
        static bool Prefix(object obj)
        {
            if (!(obj is Pawn p) || !OwnColonists.Blocked(p)) return true;
            OwnColonists.Tell(p);
            return false;
        }
    }

    static class OwnColonistsTables
    {
        /// The Work tab itself: only my colonists (shared ones have their own tab); other tables: mine and shared ones.
        public static IEnumerable<Pawn> Postfix(IEnumerable<Pawn> __result, MainTabWindow_PawnTable __instance) =>
            __instance.GetType() == typeof(MainTabWindow_Work) && OwnColonists.Active
                ? __result.Where(OwnColonists.Mine)
                : __result.Where(p => !OwnColonists.Blocked(p));
    }
}
