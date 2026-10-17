using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// Test driver, only active with -rimfactions-test=<tag>. Reads commands from
    /// {localappdata}/RimFactions/test/<tag>.cmd (one per line) and writes results to <tag>.log.
    public class TestHarness : MonoBehaviour
    {
        public static bool AutoAccept;
        static string tag, cmdPath, logPath;
        float next;

        public static void Init()
        {
            if (!GenCommandLine.TryGetCommandLineArg("rimfactions-test", out tag) || tag.NullOrEmpty()) return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimFactions", "test");
            Directory.CreateDirectory(dir);
            cmdPath = Path.Combine(dir, tag + ".cmd");
            logPath = Path.Combine(dir, tag + ".log");
            var go = new GameObject("RimFactions.Test");
            DontDestroyOnLoad(go);
            go.AddComponent<TestHarness>();
            Out("harness ready");
        }

        public static void Trace(string s) { if (logPath != null) Out("trace " + s); }

        static void Out(string s)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); } catch { }
        }

        void Update()
        {
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 1f;
            if (!File.Exists(cmdPath)) return;
            string[] lines;
            try { lines = File.ReadAllLines(cmdPath); File.Delete(cmdPath); } catch { return; }
            foreach (var line in lines.Where(l => l.Trim().Length > 0))
            {
                try { Run(line.Trim().Split(' ')); }
                catch (Exception e) { Out($"ERROR {line}: {e}"); }
            }
        }

        static Map Home => Arrival.HomeMap;

        static void Run(string[] a)
        {
            Out("> " + string.Join(" ", a));
            switch (a[0])
            {
                case "status":
                    Out($"link={Link.Role} status={Link.Status} connected={Link.Connected} me={Link.MyId} state={Current.ProgramState} live={Live.InSession} hosting={MpBridge.Hosting} mpuser={MpBridge.MyUsername}");
                    foreach (var f in Roster.Players) Out($"  player {f.id} {f.Label} online={f.online} tile={f.tile} colonists={f.colonists} gw={Roster.Goodwill(Link.MyId, f.id)} {Roster.Kind(Link.MyId, f.id)}");
                    if (Current.ProgramState == ProgramState.Playing)
                    {
                        var comp = Current.Game.GetComponent<RFGameComp>();
                        Out($"  colonistsSpawned={PawnsFinder.AllMaps_FreeColonistsSpawned.Count()} away={comp.away.Count} liveVisits={comp.liveVisits.Count(v => !v.done)} together={comp.togetherOpen} owners={string.Join(",", comp.owners.Distinct())} departures={comp.departures.Count}");
                        Out($"  ticks={Find.TickManager.TicksGame} speed={Find.TickManager.CurTimeSpeed} paused={Find.TickManager.Paused} skyfallers={Home?.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).Count + Home?.listerThings.AllThings.Count(x => x is Skyfaller)} worldPawnsHuman={Find.WorldPawns.AllPawnsAlive.Count(p => p.RaceProps.Humanlike)}");
                        Out($"  silverOnHome={Home?.listerThings.ThingsOfDef(ThingDefOf.Silver).Sum(t => t.stackCount)} markers={Find.WorldObjects.AllWorldObjects.OfType<FriendColony>().Count()} trackerTrips={Find.Maps.Sum(m => m.GetComponent<TripTracker>().trips.Count)}");
                        foreach (var p in Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).Where(p => p.RaceProps.Humanlike))
                            Out($"    pawn {p.LabelShort} faction={p.Faction?.Name} downed={p.Downed} owner={comp.OwnerOf(p)}");
                        foreach (var l in Find.LetterStack.LettersListForReading.Skip(Math.Max(0, Find.LetterStack.LettersListForReading.Count - 5)))
                            Out($"    letter: {l.Label}");
                    }
                    break;
                case "spawnsilver":
                    var silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                    silver.stackCount = int.Parse(a[1]);
                    GenPlace.TryPlaceThing(silver, Home.Center, Home, ThingPlaceMode.Near);
                    break;
                case "gift":
                    Gifts.GiftItems(Home.listerThings.ThingsOfDef(ThingDefOf.Silver).ToList(), Roster.OthersWithColony.First());
                    break;
                case "giftcolonist":
                    Gifts.GiftColonist(Home.mapPawns.FreeColonistsSpawned.First(), Roster.OthersWithColony.First());
                    break;
                case "trip":
                    Trips.Send(Home.mapPawns.FreeColonistsSpawned.Take(int.Parse(a[3])).ToList(), Roster.OthersWithColony.First(), a[1], a[2]);
                    break;
                case "accept": AutoAccept = true; break;
                case "speed":
                    if (Live.InSession)
                    {
                        var mp = HarmonyLib.AccessTools.TypeByName("Multiplayer.Client.Multiplayer");
                        var world = HarmonyLib.AccessTools.Property(mp, "AsyncWorldTime").GetValue(null);
                        var send = HarmonyLib.AccessTools.Method((HarmonyLib.AccessTools.TypeByName("Multiplayer.Client.AsyncTime.MpTimeControls") ?? HarmonyLib.AccessTools.TypeByName("Multiplayer.Client.AsyncTime.TimeControlPatch")), "SendTimeChange");
                        send.Invoke(null, new object[] { world, (TimeSpeed)int.Parse(a[1]) });
                        foreach (var m in Find.Maps)
                        {
                            var comp = HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("Multiplayer.Client.Extensions") ?? typeof(object), "AsyncTime")?.Invoke(null, new object[] { m });
                            if (comp != null) send.Invoke(null, new object[] { comp, (TimeSpeed)int.Parse(a[1]) });
                        }
                    }
                    else Find.TickManager.CurTimeSpeed = (TimeSpeed)int.Parse(a[1]);
                    break;
                case "endtrips":
                    foreach (var m in Find.Maps)
                        foreach (var t in m.GetComponent<TripTracker>().trips) t.startTick -= RFSettings.HelpStayTicks * 2;
                    break;
                case "sendhome": Live.EndVisits(); break;
                case "together": Live.OpenTogether(); break;
                case "jointogether": Live.JoinTogether(); break;
                case "split":
                    var comp2 = Current.Game.GetComponent<RFGameComp>();
                    var me = MpBridge.MyUsername;
                    Live.Depart(Home.mapPawns.FreeColonistsSpawned.Where(p => comp2.OwnerOf(p) == me).ToList(), me);
                    break;
                case "hello": Link.SendHello(); break;
                case "helpcall": HelpCall.Call(Home); break;
                case "answerhelp":
                    var call = Find.LetterStack.LettersListForReading.OfType<HelpCallLetter>().LastOrDefault();
                    if (call == null) { Out("no help call letter"); break; }
                    call.Choices.First().action();
                    break;
                case "trade":
                    // forms a one-colonist caravan with 200 silver and trades with the first friend automatically
                    var pawn = Home.mapPawns.FreeColonistsSpawned.Where(p => !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
                        .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Social).Level).First();
                    var silverStack = ThingMaker.MakeThing(ThingDefOf.Silver); silverStack.stackCount = 200;
                    pawn.inventory.innerContainer.TryAdd(silverStack);
                    var caravan = RimWorld.Planet.CaravanMaker.MakeCaravan(new[] { pawn }, Faction.OfPlayer, Home.Tile, true);
                    Trade.autoDeal = a.Length < 2 || a[1] != "ui";
                    Trade.Request(caravan, Roster.OthersWithColony.First().id);
                    break;
                case "planetevent": Link.Hub?.ForceEvent(); break;
                case "livecompat":
                    Out($"  defs={DefDatabase<Compat.LiveCompatDef>.DefCount} active={string.Join(",", DefDatabase<Compat.LiveCompatDef>.AllDefsListForReading.Where(d => d.Active).Select(d => d.packageId + ":" + d.status))}");
                    Out($"  unsafe={string.Join(",", Compat.LiveCompat.UnsafeActive().Select(d => d.packageId))} results={string.Join(",", Compat.LiveCompat.results.Select(kv => kv.Key + "=" + kv.Value))}");
                    break;
                case "autocombat":
                    // toggles hunt mode on the first colonist the way its button does (from the interface)
                    var acData = HarmonyLib.AccessTools.Method("BigAndSmall.DraftedActionHolder:GetData")
                        .Invoke(null, new object[] { Home.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First() });
                    HarmonyLib.AccessTools.Method(acData.GetType(), "ToggleHuntMode").Invoke(acData, null);
                    break;
                case "vtedeposit":
                    // deposits silver from the home map into the first bank the way the bank window does
                    var tm = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("VanillaTradingExpanded.TradingManager"), "Instance").GetValue(null);
                    var bank = ((System.Collections.IList)HarmonyLib.Traverse.Create(tm).Property("Banks").GetValue())[0];
                    HarmonyLib.AccessTools.Method(bank.GetType(), "DepositSilver").Invoke(bank, new object[] { Home.listerThings.ThingsOfDef(ThingDefOf.Silver).ToList(), int.Parse(a[1]) });
                    break;
                case "modstate":
                    var hunts = Home.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Select(p =>
                        HarmonyLib.Traverse.Create(HarmonyLib.AccessTools.Method("BigAndSmall.DraftedActionHolder:GetData").Invoke(null, new object[] { p })).Field("hunt").GetValue<bool>());
                    var tm2 = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("VanillaTradingExpanded.TradingManager"), "Instance").GetValue(null);
                    var deposits = ((System.Collections.IList)HarmonyLib.Traverse.Create(tm2).Property("Banks").GetValue()).Cast<object>().Select(b => HarmonyLib.Traverse.Create(b).Property("DepositAmount").GetValue<int>());
                    var vteSettings = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("VanillaTradingExpanded.VanillaTradingExpandedMod"), "settings").GetValue(null);
                    var am = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("AM.Core"), "Settings").GetValue(null);
                    Out($"  hunt={string.Join(",", hunts)} deposits={string.Join(",", deposits)} silver={Home.listerThings.ThingsOfDef(ThingDefOf.Silver).Sum(t => t.stackCount)} vteNewsRate={HarmonyLib.Traverse.Create(vteSettings).Field("newsSpawnRate").GetValue()} amExecutions={HarmonyLib.Traverse.Create(am).Field("EnableExecutions").GetValue()} ticks={Find.TickManager.TicksGame}");
                    break;
                case "lobbyopen": Lobby.Open(); break;
                case "lobbyready": Lobby.SetReady(true); break;
                case "lobbystart":
                    // host already in a quick-test game: what Start + the new game's first load would do
                    Lobby.SendState(true, Lobby.Mode, Lobby.Relation, true);
                    HarmonyLib.Traverse.Create(typeof(Lobby)).Field("hostSettingUp").SetValue(Lobby.Mode);
                    Lobby.HostGameStarted();
                    break;
                case "lobby":
                    Out($"  open={Lobby.IsOpen} started={Lobby.Started} mode={Lobby.Mode} relation={Lobby.Relation} in={Lobby.IAmIn} state={Lobby.State}");
                    break;
                case "starters":
                    var sc = Current.Game.GetComponent<RFGameComp>();
                    Out($"  starters={string.Join(",", sc.starters.Select(p => p.LabelShort + ":" + sc.OwnerOf(p)))} window={Find.WindowStack.IsOpen<MakeColonistsWindow>()}");
                    break;
                case "reroll": Starters.Reroll(MpBridge.MyUsername, 0, 2); break;
                case "rename": Starters.Rename(MpBridge.MyUsername, 1, "Tessa", "Tess", "Test"); break;
                case "dropin": Starters.DropIn(MpBridge.MyUsername); Find.WindowStack.WindowOfType<MakeColonistsWindow>()?.Close(); break;
                case "color": Lobby.SetMyColor(Lobby.Palette[int.Parse(a[1])]); break;
                case "save": GameDataSaveLoader.SaveGame(a[1]); break;
                case "shared":
                    Out($"  shared={SharedSaves.IsShared(Current.Game)} id={SharedSaves.GameIdOf(Current.Game)} status={SharedSaves.StatusKey} {string.Join("|", SharedSaves.StatusArgs)} cloud={Rules.Local.cloudFolder}");
                    break;
                case "capturepawn":
                    // writes the first colonist as PawnData, standing in for colonists made on the new-game screen
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(logPath), "pawn.xml"), PawnData.Capture(Home.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First()).ToString());
                    break;
                case "earlyload":
                    var px = File.ReadAllText(Path.Combine(Path.GetDirectoryName(logPath), "pawn.xml"));
                    var renamed = System.Xml.Linq.XElement.Parse(px);
                    renamed.SetAttributeValue("first", "Early"); renamed.SetAttributeValue("nick", "Early"); renamed.SetAttributeValue("last", "Bird");
                    EarlyColonists.mine = Enumerable.Repeat(renamed.ToString(), 2).ToList();
                    break;
                case "sendmine": EarlyColonists.SendToHost(); break;
                case "hostquick":
                    // the host's side of "start together": lobby started, a quick setup, the colonist page with the real game start behind it
                    Lobby.SendState(true, Lobby.Together, "Ally", true);
                    HarmonyLib.Traverse.Create(typeof(Lobby)).Field("hostSettingUp").SetValue(Lobby.Together);
                    LongEventHandler.QueueLongEvent(() =>
                    {
                        Current.ProgramState = ProgramState.Entry;
                        Game.ClearCaches();
                        Current.Game = new Game();
                        Current.Game.InitData = new GameInitData();
                        Current.Game.Scenario = ScenarioDefOf.Crashlanded.scenario.CopyForEditing();
                        Find.Scenario.PreConfigure();
                        Current.Game.storyteller = new Storyteller(StorytellerDefOf.Cassandra, DifficultyDefOf.Rough);
                        Current.Game.World = RimWorld.Planet.WorldGenerator.GenerateWorld(0.05f, GenText.RandomSeedString(), RimWorld.Planet.OverallRainfall.Normal,
                            RimWorld.Planet.OverallTemperature.Normal, RimWorld.Planet.OverallPopulation.Normal, RimWorld.Planet.LandmarkDensity.Normal);
                        Find.GameInitData.ChooseRandomStartingTile();
                        Find.GameInitData.mapSize = 150;
                        Find.Scenario.PostIdeoChosen();
                        Find.WindowStack.Add(new Page_ConfigureStartingPawns { nextAct = PageUtility.InitGameStart });
                    }, "GeneratingMap", false, null);
                    break;
                case "earlybegin": EarlyColonists.mine = null; EarlyColonists.Begin(); break;
                case "pagestatus":
                    var pg = Find.WindowStack.WindowOfType<Page_ConfigureStartingPawns>();
                    Out($"  page={pg != null} state={Current.ProgramState} scenario={Find.Scenario?.name} pawns={(Find.GameInitData == null ? "-" : Find.GameInitData.startingPawnCount + ":" + string.Join(",", Find.GameInitData.startingAndOptionalPawns.Select(p => p.LabelShort)))} windows={string.Join(",", Find.WindowStack.Windows.Select(w => w.GetType().Name))}");
                    break;
                case "earlydone":
                    var page2 = Find.WindowStack.WindowOfType<Page_ConfigureStartingPawns>();
                    if (page2 != null) HarmonyLib.Traverse.Create(page2).Method("DoNext").GetValue();
                    break;
                case "kitcheck":
                    var kc = Current.Game.GetComponent<RFGameComp>();
                    var work = HarmonyLib.Traverse.Create(new MainTabWindow_Work()).Property("Pawns").GetValue<IEnumerable<Pawn>>().Select(p => p.LabelShort);
                    var shared = HarmonyLib.Traverse.Create(new MainTabWindow_SharedWork()).Property("Pawns").GetValue<IEnumerable<Pawn>>().Select(p => p.LabelShort);
                    Out($"  kits={string.Join(",", kc.kits)} colors={string.Join(",", kc.ownerColors.Select(k => k.Key + ":" + k.Value))} silver={Home.listerThings.ThingsOfDef(ThingDefOf.Silver).Sum(t => t.stackCount)} pods={Home.listerThings.AllThings.Count(t => t is Skyfaller)} work=[{string.Join(",", work)}] shared=[{string.Join(",", shared)}] active={OwnColonists.Active}");
                    break;
                case "pickscen":
                    // the friend picks a scenario in the picker (Crashlanded, or the preselected one)
                    var pick = Find.WindowStack.WindowOfType<ScenarioPickWindow>();
                    Out($"  picker={pick != null}");
                    if (pick != null)
                    {
                        var all = HarmonyLib.Traverse.Create(pick).Field("all").GetValue<List<Scenario>>();
                        var sel = all.FirstOrDefault(sc => sc.name == (a.Length > 1 ? string.Join(" ", a.Skip(1)) : "")) ?? HarmonyLib.Traverse.Create(pick).Field("selected").GetValue<Scenario>();
                        Out($"  picked {sel?.name}");
                        pick.Close();
                        EarlyColonists.BeginWith(sel);
                    }
                    break;
                case "minecount": Out($"  mine={EarlyColonists.mine?.Count.ToString() ?? "none"} state={Current.ProgramState}"); break;
                case "friendcols":
                    foreach (var kv in Lobby.friendColonists)
                        Out($"  from {kv.Key} mp={kv.Value.mpName} pawns={string.Join(",", kv.Value.pawns.Select(x => PawnData.Make(x, Faction.OfPlayer).LabelShort))}");
                    Out($"  members={string.Join(",", Lobby.MemberColonists().Select(t => t.f.player + ":" + t.ready))} stage={Lobby.Stage}");
                    break;
                case "owncheck":
                    var oc = Current.Game.GetComponent<RFGameComp>();
                    foreach (var p in Home.mapPawns.FreeColonistsSpawned)
                    {
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(p, false);
                        var orders = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, p.Position.ToVector3Shifted() + new Vector3(2, 0, 0), out _);
                        Out($"  select {p.LabelShort} owner={oc.OwnerOf(p)} -> selected={Find.Selector.IsSelected(p)} gizmos={p.GetGizmos().Count()} orders={orders.Count}");
                    }
                    Find.Selector.ClearSelection();
                    break;
                case "pwon":   // feature:PersonalWork: switch mine on/off
                    PersonalWork.SetPersonal(MpBridge.MyUsername, a.Length < 2 || a[1] != "off");
                    break;
                case "pwbuild":   // place a wall blueprint through the designator (synced, so it carries who ordered it)
                    var wallCell = Home.mapPawns.FreeColonistsSpawned.First().Position + new IntVec3(int.Parse(a[1]), 0, 3);
                    var des = new Designator_Build(ThingDefOf.Wall);
                    des.SetStuffDef(ThingDefOf.WoodLog);
                    des.DesignateSingleCell(wallCell);
                    Out($"  placed wall at {wallCell}");
                    break;
                case "pwcheck":
                    var pc = Current.Game.GetComponent<RFGameComp>();
                    Out($"  personal=[{string.Join(",", pc.personalWork)}] orders={pc.orderOwners.Count}");
                    foreach (var bp in Home.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(Home.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)))
                    {
                        pc.orderOwners.TryGetValue(PersonalWork.ThingKey(bp), out var by);
                        Out($"  {bp.LabelShort} at {bp.Position} by={by} " + string.Join(" ", Home.mapPawns.FreeColonistsSpawned.Select(p => $"{p.LabelShort}({pc.OwnerOf(p)}):{(bp.IsForbidden(p) ? "no" : "ok")}")));
                    }
                    break;
                case "devmode":
                    var ss = HarmonyLib.Traverse.Create(HarmonyLib.AccessTools.TypeByName("Multiplayer.Client.Multiplayer")).Property("LocalServer").Field("settings");
                    Out($"  debugMode={ss.Field("debugMode").GetValue()} scope={ss.Field("devModeScope").GetValue()}");
                    break;
                case "reload":   // feature:LoadSaveTogether: load the newest save of this name for everyone
                    var save = ReloadSave.Saves().FirstOrDefault(t => System.IO.Path.GetFileNameWithoutExtension(t.file.Name) == a[1]);
                    if (save.file == null) Out("  no save " + a[1]); else ReloadSave.Start(save.file, save.mp);
                    break;
                case "langerrors":
                    foreach (var le in LanguageDatabase.activeLanguage.loadErrors) Out("  lang: " + le);
                    break;
                case "trylive":
                    Compat.LiveCompat.ConfirmLive(() => Out("  live would start now"));
                    break;
                case "rules":
                    var act = Rules.Active;
                    Out($"  hostSets={Rules.HostSets} host={Rules.HostName} callForHelp={act.callForHelp} trade={act.tradeCaravans} raids={act.playerRaids} callCount={act.helpCallSendCount} raidGoodwill={act.raidGoodwill} assaultEvery={act.assaultEveryDays}");
                    break;
                case "setrule":
                    var fld = HarmonyLib.Traverse.Create(Rules.Local).Field(a[1]);
                    if (fld.GetValueType() == typeof(bool)) fld.SetValue(bool.Parse(a[2])); else fld.SetValue(int.Parse(a[2]));
                    Link.SendHello();
                    break;
                case "settingswin":
                    var mod = LoadedModManager.ModHandles.OfType<RimFactionsMod>().First();
                    Find.WindowStack.Add(new Dialog_ModSettings(mod));
                    break;
                case "modsask": ModCompat.Request(Roster.Others.First().id); break;
                case "modsshow":
                    var fr = Roster.Others.First();
                    var theirs = ModCompat.Of(fr.id);
                    if (theirs == null) { Out("  no list yet"); break; }
                    var mineIds = new System.Collections.Generic.HashSet<string>(ModCompat.Mine.Select(mm => mm.id));
                    var theirIds = new System.Collections.Generic.HashSet<string>(theirs.Select(mm => mm.id));
                    Out($"  mine={ModCompat.Mine.Count} theirs={theirs.Count}");
                    foreach (var mm in theirs.Where(mm => !mineIds.Contains(mm.id))) Out($"  they-only {mm.name} ws={mm.workshop} live={ModCompat.LiveStatus(mm)}");
                    foreach (var mm in ModCompat.Mine.Where(mm => !theirIds.Contains(mm.id))) Out($"  me-only {mm.name} ws={mm.workshop} live={ModCompat.LiveStatus(mm)}");
                    Find.WindowStack.Add(new ModCompareWindow(fr));
                    break;
                case "dlcs":
                    Out($"  mine={string.Join(",", Dlc.Mine)}");
                    foreach (var f in Roster.Others) Out($"  friend {f.player} dlcs={(f.dlcs == null ? "?" : string.Join(",", f.dlcs))} layer={f.layer}");
                    break;
                case "makexeno":
                    // gives the first colonist a Biotech xenotype and a Royalty psylink with a psycast, for expansion tests
                    var xp = Home.mapPawns.FreeColonistsSpawned.First();
                    if (ModsConfig.BiotechActive) xp.genes.SetXenotype(DefDatabase<XenotypeDef>.GetNamed("Hussar"));
                    if (ModsConfig.RoyaltyActive)
                    {
                        xp.ChangePsylinkLevel(2, sendLetter: false);
                        xp.abilities.GainAbility(DefDatabase<AbilityDef>.GetNamed("Skip"));
                    }
                    Out($"  {xp.LabelShort}: xeno={xp.genes?.Xenotype?.defName} psylink={xp.GetPsylinkLevel()}");
                    break;
                case "checksend":
                    var target = Roster.OthersWithColony.First();
                    foreach (var p in Home.mapPawns.FreeColonistsSpawned)
                        Out($"  {p.LabelShort} -> {target.player}: missing={string.Join(",", Dlc.Missing(target, new[] { PawnData.Capture(p) }))}");
                    Out($"  live mismatch={string.Join(",", Dlc.LiveMismatch(target))}");
                    break;
                case "giftfirst":
                    var gp = Home.mapPawns.FreeColonistsSpawned.First();
                    var gf = Roster.OthersWithColony.First();
                    var miss = Dlc.Missing(gf, new[] { PawnData.Capture(gp) });
                    if (miss.Count > 0) { Out("  blocked: " + Dlc.Reason(gf, miss)); break; }
                    Gifts.GiftColonist(gp, gf);
                    break;
                case "pawninfo":
                    foreach (var p in Home.mapPawns.FreeColonistsSpawned)
                        Out($"  {p.LabelShort}: xeno={p.genes?.Xenotype?.defName} xenogenes={p.genes?.Xenogenes.Count} psylink={p.GetPsylinkLevel()} abilities={string.Join(",", p.abilities?.abilities.Select(ab => ab.def.defName) ?? Enumerable.Empty<string>())}");
                    break;
                case "homearea":
                    foreach (var c in GenRadial.RadialCellsAround(Home.Center, 25f, true)) if (c.InBounds(Home)) Home.areaManager.Home[c] = true;
                    var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = 75;
                    GenPlace.TryPlaceThing(steel, Home.Center, Home, ThingPlaceMode.Near);
                    break;
                case "errors":
                    foreach (var msg in Log.Messages.Where(x => x.type != LogMessageType.Message || x.text.Contains("Rim Factions")).Reverse().Take(12).Reverse())
                        Out($"  [{msg.type}] {msg.text.Substring(0, Math.Min(400, msg.text.Length))} || {msg.StackTrace?.Split('\n').Take(4).Aggregate("", (s, l) => s + " / " + l.Trim())}");
                    break;
                case "camera":
                    if (a.Length > 1 && a[1] == "world") CameraJumper.TryShowWorld();
                    else Find.CameraDriver.JumpToCurrentMapLoc(Home.Center);
                    break;
                case "select":
                    Find.Selector.ClearSelection();
                    foreach (var p in Home.mapPawns.FreeColonistsSpawned.Take(a.Length > 1 ? int.Parse(a[1]) : 1)) Find.Selector.Select(p);
                    break;
                case "window": Find.WindowStack.Add(new RimFactionsWindow()); break;
                case "closewindows": Find.WindowStack.Windows.ToList().ForEach(w => w.Close(false)); break;
                case "letters": Find.LetterStack.LettersListForReading.LastOrDefault()?.OpenLetter(); break;
                default: Out("unknown command"); break;
            }
        }
    }
}
