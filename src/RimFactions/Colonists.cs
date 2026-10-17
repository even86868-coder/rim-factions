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
        /// How many colonists I may bring (the lobby's limit within my scenario), set on the colonist page.
        public static int limit;
        /// The last try failed: don't reopen by itself (the lobby's button tries again).
        public static bool failed;
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
            Link.Send(new Msg("MyColonists", Roster.HubId, new XElement("mp", MyMpName()), new XElement("pawns", mine.Select(XElement.Parse)),
                new XElement("scen", myScenario ?? "")));
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
        /// My scenario's file, sent with my colonists so my starting kit is my scenario's (feature:StartingKit).
        public static string myScenario
        {
            get { try { return System.IO.File.Exists(ScenPath) ? System.IO.File.ReadAllText(ScenPath) : null; } catch { return null; } }
            set { try { if (value == null) System.IO.File.Delete(ScenPath); else System.IO.File.WriteAllText(ScenPath, value); } catch { } }
        }
        static string ScenPath => System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RimFactions-my-scenario.rsc");

        /// First I pick my own scenario; my colonists are made in it.
        public static void Begin()
        {
            Find.WindowStack.WindowOfType<LobbyWindow>()?.Close(false);
            if (!Find.WindowStack.IsOpen<ScenarioPickWindow>()) Find.WindowStack.Add(new ScenarioPickWindow());
        }

        public static void BeginWith(Scenario chosen)
        {
            making = true;
            LongEventHandler.QueueLongEvent(() =>
            {
                Current.ProgramState = ProgramState.Entry;
                Game.ClearCaches();
                Current.Game = new Game();
                Current.Game.InitData = new GameInitData();
                Current.Game.Scenario = (chosen ?? HostScenarioDef() ?? ScenarioDefOf.Crashlanded.scenario).CopyForEditing();
                Find.Scenario.PreConfigure();
                var teller = DefDatabase<StorytellerDef>.GetNamedSilentFail(Lobby.HostStoryteller ?? "") ?? StorytellerDefOf.Cassandra;
                var difficulty = DefDatabase<DifficultyDef>.GetNamedSilentFail(Lobby.HostDifficulty ?? "") ?? DifficultyDefOf.Rough;
                Current.Game.storyteller = new Storyteller(teller, difficulty);
                Current.Game.World = WorldGenerator.GenerateWorld(0.05f, GenText.RandomSeedString(), OverallRainfall.Normal,
                    OverallTemperature.Normal, OverallPopulation.Normal, LandmarkDensity.Normal);
                Find.GameInitData.ChooseRandomStartingTile();
                Find.GameInitData.mapSize = 250;
                Find.Scenario.PostIdeoChosen();
                // the page opens as the setup's last step (a "when finished" action would run before the setup)
                if (Find.GameInitData == null || Find.GameInitData.startingAndOptionalPawns.Count == 0)
                    throw new System.Exception("no starting colonists were generated");
                Find.WindowStack.Add(new Page_ConfigureStartingPawns());
            }, "RF_EarlyPreparing", false, Fail);
        }

        /// Shows why colonist creation couldn't open, and logs it in full.
        static void Fail(System.Exception e)
        {
            making = false;
            failed = true;
            Log.Error("[Rim Factions] preparing colonist creation failed: " + e);
            Find.WindowStack.Add(new Dialog_MessageBox("RF_EarlyFailed".Translate(e.GetType().Name + ": " + e.Message)));
        }

        public static Scenario HostScenarioDef()
        {
            // exactly the host's scenario, from the file they sent
            var text = Lobby.HostScenarioFile;
            if (!text.NullOrEmpty())
                try
                {
                    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimFactions-host-scenario-in.rsc");
                    System.IO.File.WriteAllText(path, text);
                    if (GameDataSaveLoader.TryLoadScenario(path, ScenarioCategory.CustomLocal, out var scen) && scen != null) return scen;
                }
                catch (System.Exception e) { Log.Warning("[Rim Factions] host's scenario unreadable, matching by name: " + e.Message); }
            var name = Lobby.HostScenario;
            if (name.NullOrEmpty()) return null;
            return ScenarioLister.AllScenarios().FirstOrDefault(s => s.name == name);
        }

        /// Synced: my colonists from my creation screen join the colony, in every game identically.
        [SyncMethod]
        public static void Arrive(string owner, string pawnsXml, string scenario)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (!comp.togetherOpen || comp.owners.Contains(owner)) return;
            var map = Find.Maps.Where(m => m.IsPlayerHome).OrderByDescending(m => m.mapPawns.FreeColonistsSpawnedCount).FirstOrDefault();
            if (map == null) return;
            var pawns = new List<Thing>();
            foreach (var x in XElement.Parse(pawnsXml).Elements("pawn").Take(12))
            {
                var p = PawnData.Make(x, Faction.OfPlayer);
                comp.SetOwner(p, owner);
                pawns.Add(p);
            }
            if (pawns.Count > 0) DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, pawns, forbid: false);
            StartingKit.Give(map, owner, scenario);
        }

        /// Called once I'm in the session: send my early colonists, if I made any. True when it did.
        /// Synced: colonists that landed under the name I had before joining become mine under my session name.
        [SyncMethod]
        public static void Retag(string from, string to)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (from == to || comp.owners.Contains(to)) return;
            for (int i = 0; i < comp.owners.Count; i++) if (comp.owners[i] == from) comp.owners[i] = to;
            for (int i = 0; i < comp.kits.Count; i++) if (comp.kits[i] == from) comp.kits[i] = to;   // the kit came with them
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
            var scen = myScenario;
            mine = null;   // used up (file removed)
            myScenario = null;
            Arrive(me, xml, scen ?? "");
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
            // the lobby's limit, and never more than my scenario starts with; units the scenario requires at the start
            // (a ghoul, say) come on top, so I still have my own colonist next to them
            var init = Find.GameInitData;
            int scenarioCount = Mathf.Max(1, init.startingPawnCount);
            int Req<T>(List<T> l) where T : StartingPawnCount => l?.Where(c => c.requiredAtStart).Sum(c => c.count) ?? 0;
            int need = Mathf.Max(1, Req(init.startingPawnsRequired) + Req(init.startingXenotypesRequired)) + Req(init.startingMutantsRequired);
            int n = Mathf.Clamp(Mathf.Max(Mathf.Min(RFSettings.TogetherStartPawns, scenarioCount), need), 1, scenarioCount);
            init.startingPawnCount = n;
            EarlyColonists.limit = n;
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
            PrepareCarefullyStart.Init(h);
        }

        static bool confirmed;

        static bool Prefix(Page_ConfigureStartingPawns __instance)
        {
            if (Lobby.HostMakingTogether) return HostStart(__instance);
            if (!EarlyColonists.making) return true;
            PrepareCarefullyStart.ApplyFrom(__instance);   // Prepare Carefully was used, but Start was pressed here
            CaptureMine();
            return false;
        }

        /// A friend's colonists are made: keep them (with their possessions and my scenario's kit) for the host.
        public static void CaptureMine()
        {
            EarlyColonists.making = false;
            var init = Find.GameInitData;
            int n = EarlyColonists.limit > 0 ? Mathf.Min(init.startingPawnCount, EarlyColonists.limit) : init.startingPawnCount;
            var pawns = init.startingAndOptionalPawns.Take(n).ToList();
            // the things each colonist brings (chosen on the colonist page or in Prepare Carefully) travel in their pockets
            foreach (var p in pawns)
                if (init.startingPossessions != null && init.startingPossessions.TryGetValue(p, out var stuff) && p.inventory != null)
                    foreach (var c in stuff)
                        try
                        {
                            var thing = ThingMaker.MakeThing(c.ThingDef, c.ThingDef.MadeFromStuff ? GenStuff.DefaultStuffFor(c.ThingDef) : null);
                            thing.stackCount = Mathf.Max(1, c.Count);
                            p.inventory.innerContainer.TryAdd(thing);
                        }
                        catch (System.Exception e) { Log.Warning($"[Rim Factions] {p.LabelShort}'s {c.ThingDef?.defName}: {e.Message}"); }
            EarlyColonists.mine = pawns.Select(p => PawnData.Capture(p).ToString(SaveOptions.DisableFormatting)).ToList();
            EarlyColonists.myScenario = StartingKit.ScenarioToText(Find.Scenario);
            EarlyColonists.SendToHost();
            Messages.Message("RF_EarlyReady".Translate(EarlyColonists.mine.Count), MessageTypeDefOf.PositiveEvent, false);
            GenScene.GoToMainMenu();
        }

        /// Host: friends who haven't readied yet. Asks once; true means go ahead.
        public static bool ReadyOrConfirmed(System.Action retry)
        {
            var waiting = Lobby.MemberColonists().Where(t => !t.ready).Select(t => t.f.player).ToList();
            if (waiting.Count == 0 || confirmed) return true;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_PawnPageStartWithout".Translate(string.Join(", ", waiting)), () =>
            {
                confirmed = true;
                retry();
            }));
            return false;
        }

        /// feature:LandTogether: friends' colonists join the starting colonists, so everyone lands at once.
        static bool HostStart(Page_ConfigureStartingPawns page)
        {
            if (!ReadyOrConfirmed(() => Traverse.Create(page).Method("DoNext").GetValue())) return false;
            // Prepare Carefully was used, but Start was pressed here: apply its colonists and gear (friends join there)
            if (!PrepareCarefullyStart.ApplyFrom(page)) AddFriends();
            return true;
        }

        public static void AddFriends()
        {
            confirmed = false;
            var init = Find.GameInitData;
            // my own starting colonists are mine from the start (they're still in their drop pods when the session opens)
            var hostName = Live.MyName();
            foreach (var mine in init.startingAndOptionalPawns.Take(init.startingPawnCount)) Lobby.pendingOwners[mine] = hostName;
            foreach (var (mpName, pawns) in Lobby.friendColonists.Values)
                foreach (var x in pawns.Take(12))
                {
                    try
                    {
                        var p = PawnData.Make(x, Faction.OfPlayer);
                        init.startingAndOptionalPawns.Insert(init.startingPawnCount, p);
                        // every starting colonist needs an entry here, or placing the colonists on the map fails part-way
                        // (their own items come with their starting kit instead)
                        init.startingPossessions[p] = new List<ThingDefCount>();
                        init.startingPawnCount++;
                        Lobby.pendingOwners[p] = mpName;
                    }
                    catch (System.Exception e) { Log.Warning("[Rim Factions] a friend's colonist couldn't be made: " + e.Message); }
                }
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

    /// In a shared colony each player runs their own colonists: other players' colonists can be inspected but not
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

    // Other players' colonists can be selected and inspected (mood, needs, health, gear), but not given orders:
    // no buttons, no right-click orders, no dropping their gear. Tending, feeding and rescuing them works as usual.

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    static class OwnColonistsGizmos
    {
        static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance) =>
            OwnColonists.Blocked(__instance) ? Enumerable.Empty<Gizmo>() : __result;
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    static class OwnColonistsOrders
    {
        static void Prefix(ref List<Pawn> selectedPawns, ref bool __state)
        {
            if (selectedPawns == null || !selectedPawns.Any(OwnColonists.Blocked)) return;
            OwnColonists.Tell(selectedPawns.First(OwnColonists.Blocked));
            var mine = selectedPawns.Where(p => !OwnColonists.Blocked(p)).ToList();
            if (mine.Count > 0) selectedPawns = mine; else __state = true;
        }

        static void Postfix(ref List<FloatMenuOption> __result, bool __state)
        {
            if (__state) __result = new List<FloatMenuOption>();
        }
    }

    [HarmonyPatch(typeof(ITab_Pawn_Gear), "CanControl", MethodType.Getter)]
    static class OwnColonistsGear
    {
        static void Postfix(ITab_Pawn_Gear __instance, ref bool __result)
        {
            if (__result && OwnColonists.Blocked(Traverse.Create(__instance).Property("SelPawnForGear").GetValue<Pawn>())) __result = false;
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

namespace RimFactions
{
    /// feature:OwnScenario — a friend picks their own scenario for their colonists (the host's is preselected). They can
    /// make as many colonists as the lobby allows, but never more than that scenario starts with.
    public class ScenarioPickWindow : Window
    {
        Scenario selected;
        Vector2 scroll;
        readonly List<Scenario> all;

        public override Vector2 InitialSize => new Vector2(860f, 640f);

        public ScenarioPickWindow()
        {
            doCloseX = true;
            absorbInputAroundWindow = true;
            all = ScenarioLister.AllScenarios().Where(s => s.showInUI).ToList();
            var host = EarlyColonists.HostScenarioDef();
            selected = all.FirstOrDefault(s => host != null && s.name == host.name) ?? host ?? all.FirstOrDefault();
            if (host != null && !all.Contains(host)) all.Insert(0, host);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), "RF_PickScenarioTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, 44f), "RF_PickScenarioHint".Translate(RFSettings.TogetherStartPawns));

            var list = new Rect(inRect.x, inRect.y + 86f, 300f, inRect.height - 136f);
            var view = new Rect(0, 0, list.width - 16f, all.Count * 32f);
            Widgets.BeginScrollView(list, ref scroll, view);
            for (int i = 0; i < all.Count; i++)
            {
                var r = new Rect(0, i * 32f, view.width, 30f);
                if (all[i] == selected) Widgets.DrawHighlightSelected(r); else Widgets.DrawHighlightIfMouseover(r);
                Widgets.Label(r.ContractedBy(4f), all[i].name);
                if (Widgets.ButtonInvisible(r)) selected = all[i];
            }
            Widgets.EndScrollView();

            var info = new Rect(list.xMax + 12f, list.y, inRect.width - list.width - 12f, list.height);
            if (selected != null) Widgets.LabelScrollable(info, selected.GetFullInformationText(), ref infoScroll);

            if (Widgets.ButtonText(new Rect(inRect.xMax - 260f, inRect.yMax - 40f, 260f, 38f), "RF_PickScenarioGo".Translate()) && selected != null)
            {
                Close();
                EarlyColonists.BeginWith(selected);
            }
        }

        Vector2 infoScroll;
    }
}

namespace RimFactions
{
    /// feature:NameTags: each colonist's own name (on the map and in the colonist bar) shows in its owner's color.
    [HarmonyPatch(typeof(PawnNameColorUtility), nameof(PawnNameColorUtility.PawnNameColorOf))]
    static class OwnerNameColor
    {
        static void Postfix(Pawn pawn, ref Color __result)
        {
            if (!Rules.Local.nameTags || pawn == null || !Live.InSession) return;
            var owner = Current.Game?.GetComponent<RFGameComp>()?.OwnerOf(pawn);
            if (owner != null) __result = Lobby.ColorOf(owner);
        }
    }
}

namespace RimFactions
{
    /// feature:PrepareCarefullyTogether — Prepare Carefully starts the game itself instead of pressing the colonist
    /// page's Start, so Rim Factions follows it there: a friend's prepared colonists (with their gear, possessions,
    /// pets and items) go to the host, and the host's prepared colonists land with the friends'.
    static class PrepareCarefullyStart
    {
        static System.Type controller;

        public static void Init(Harmony h)
        {
            controller = AccessTools.TypeByName("EdB.PrepareCarefully.ControllerPage");
            if (controller == null) return;
            var start = AccessTools.Method(controller, "StartGame");
            var prepare = AccessTools.Method(controller, "PreparePawns");
            if (start == null || prepare == null) { Log.Warning("[Rim Factions] Prepare Carefully changed; starting together uses the normal colonist page"); return; }
            h.Patch(start, prefix: new HarmonyMethod(typeof(PrepareCarefullyStart), nameof(StartPrefix)));
            h.Patch(prepare, postfix: new HarmonyMethod(typeof(PrepareCarefullyStart), nameof(PreparedPostfix)));
            Log.Message("[Rim Factions] Prepare Carefully: starting together supported");
        }

        static bool StartPrefix(object __instance)
        {
            if (Lobby.HostMakingTogether)
                return EarlyColonistsDone.ReadyOrConfirmed(() => Traverse.Create(__instance).Method("StartGame").GetValue());
            if (!EarlyColonists.making) return true;
            // a friend: apply what was prepared to the colonists and scenario, then send them instead of starting
            var t = Traverse.Create(__instance);
            t.Method("PreparePawns").GetValue();
            t.Method("PrepareRelationships").GetValue();
            t.Method("PrepareEquipment").GetValue();
            foreach (var w in Find.WindowStack.Windows.Where(w => w.GetType().Namespace?.StartsWith("EdB.PrepareCarefully") == true).ToList()) w.Close(false);
            EarlyColonistsDone.CaptureMine();
            return false;
        }

        /// Start was pressed on the normal colonist page after using Prepare Carefully: its pawn and gear choices only
        /// take effect from its own Start button, so the equipment list (weapons, armor, items, pets) was lost.
        /// Apply them the same way here. True when it did (the host's friends were then added by PreparedPostfix).
        public static bool ApplyFrom(Page_ConfigureStartingPawns page)
        {
            if (controller == null) return false;
            try
            {
                var mod = AccessTools.Property(AccessTools.TypeByName("EdB.PrepareCarefully.Mod"), "Instance")?.GetValue(null);
                var state = mod == null ? null : Traverse.Create(mod).Property("State").GetValue();
                if (state == null || Traverse.Create(state).Property("Customizations").GetValue() == null) return false;
                if (Traverse.Create(state).Property("OriginalPage").GetValue() != page) return false;   // left over from another game
                var ctrl = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(controller);
                Traverse.Create(ctrl).Field("<State>k__BackingField").SetValue(state);
                var t = Traverse.Create(ctrl);
                t.Method("PreparePawns").GetValue();
                t.Method("PrepareRelationships").GetValue();
                t.Method("PrepareEquipment").GetValue();
                Log.Message("[Rim Factions] applied Prepare Carefully's colonists and gear (Start was pressed on the colonist page)");
                return true;
            }
            catch (System.Exception e)
            {
                Log.Warning("[Rim Factions] couldn't apply Prepare Carefully's choices: " + (e.InnerException?.Message ?? e.Message));
                return false;
            }
        }

        /// Host: Prepare Carefully just rebuilt the starting colonists from its own list; friends' colonists join now.
        static void PreparedPostfix()
        {
            if (Lobby.HostMakingTogether) EarlyColonistsDone.AddFriends();
        }
    }
}
