using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:Lobby — start a new game with friends from the main menu. The host (the game that hosts the planet)
    /// opens a lobby, picks Together (one colony, everyone makes their own colonists) or Separate (everyone founds
    /// their own colony on the host's planet) and how the players' factions start out (allies, neutral, rivals).
    /// Friends linked to the planet see it, pick their color and say they're ready; the host sets up the world.
    public static class Lobby
    {
        public const string Together = "together", Separate = "separate";
        public static readonly string[] Relations = { "Ally", "Neutral", "Hostile" };

        /// The hub's lobby, from the roster: <lobby open mode relation started host><m id ready/>...</lobby>
        public static XElement State;
        public static bool IsOpen => State != null && (bool?)State.Attribute("open") == true;
        public static bool Started => IsOpen && (bool?)State.Attribute("started") == true;
        public static string Mode => (string)State?.Attribute("mode") ?? Together;
        public static string Relation => (string)State?.Attribute("relation") ?? "Neutral";
        public static bool IAmHost => Link.Role == LinkRole.Hub;
        public static bool IAmIn => State?.Elements("m").Any(m => (string)m.Attribute("id") == Link.MyId) == true;
        public static bool IsReady(string id) => State?.Elements("m").Any(m => (string)m.Attribute("id") == id && (bool?)m.Attribute("ready") == true) == true;

        /// host: a new game for this lobby is being set up (mode), so its start opens the together session
        static string hostSettingUp;
        /// client: what I already did for this lobby's start
        static bool acted;
        static float nextLook;

        // ---- host ----

        public static void Open() => SendState(true, Together, "Ally", false);

        public static void SendState(bool open, string mode, string relation, bool started, string stage = "", params XAttribute[] extra) =>
            Link.Send(new Msg("Lobby", "hub", new XElement("lobby", new XAttribute("open", open), new XAttribute("mode", mode),
                new XAttribute("relation", relation), new XAttribute("started", started), new XAttribute("host", Link.MyId),
                new XAttribute("stage", stage ?? ""), extra)));

        // ---- landing together (feature:LandTogether) ----

        /// "pawns" once the host is on RimWorld's colonist page.
        public static string Stage => (string)State?.Attribute("stage") ?? "";
        public static bool HostMakingTogether => IAmHost && hostSettingUp == Together;
        /// The host is on the colonist page: friends make theirs now, with the host's scenario and storyteller.
        public static void HostAtPawns() => SendState(true, Mode, Relation, true, "pawns",
            new XAttribute("scenario", Find.Scenario?.name ?? ""),
            new XAttribute("storyteller", Find.Storyteller?.def?.defName ?? ""),
            new XAttribute("difficulty", Find.Storyteller?.difficultyDef?.defName ?? ""));

        public static string HostScenario => (string)State?.Attribute("scenario");
        public static string HostStoryteller => (string)State?.Attribute("storyteller");
        public static string HostDifficulty => (string)State?.Attribute("difficulty");

        /// host: each friend's finished colonists (their Multiplayer name, their colonists as PawnData).
        public static readonly Dictionary<string, (string mpName, List<XElement> pawns)> friendColonists = new Dictionary<string, (string, List<XElement>)>();
        /// host: friends' colonists in the new game, and whose they are, until the together session tags them.
        public static readonly Dictionary<Pawn, string> pendingOwners = new Dictionary<Pawn, string>();

        static void OnFriendColonists(Msg m)
        {
            if (!IAmHost) return;
            var pawns = m.x.Element("pawns")?.Elements("pawn").Select(e => new XElement(e)).ToList() ?? new List<XElement>();
            friendColonists[m.From] = (m.Get("mp") ?? Roster.Get(m.From)?.player, pawns);
        }

        /// Lobby members (not the host) and whether their colonists have arrived here.
        public static IEnumerable<(Friend f, bool ready)> MemberColonists() =>
            (State?.Elements("m") ?? Enumerable.Empty<XElement>()).Select(e => Roster.Get((string)e.Attribute("id")))
                .Where(f => f != null && !f.IsMe).Select(f => (f, friendColonists.ContainsKey(f.id)));

        public static void Close() => SendState(false, Mode, Relation, false);

        /// The host starts: everyone's relations are set, then the host goes through RimWorld's new-game screens.
        public static void Start()
        {
            SendState(true, Mode, Relation, true);
            hostSettingUp = Mode;
            WorldParams.usePlanet = false;
            Find.WindowStack.Add(new Page_SelectScenario());
        }

        /// The host's new game has finished loading.
        public static void HostGameStarted()
        {
            if (hostSettingUp == null || !IAmHost) return;
            var mode = hostSettingUp;
            hostSettingUp = null;
            SharedSaves.MarkShared(Current.Game);   // feature:SharedSaves: everyone keeps the newest copy
            if (mode == Together)
            {
                // friends' colonists that landed with mine are theirs (feature:LandTogether)
                var comp = Current.Game.GetComponent<RFGameComp>();
                foreach (var kv in pendingOwners) comp.SetOwner(kv.Key, kv.Value);
                var home = Find.Maps.FirstOrDefault(m => m.IsPlayerHome);
                foreach (var friend in pendingOwners.Values.Distinct()) StartingKit.Give(home, friend);   // feature:StartingKit
                pendingOwners.Clear();
                friendColonists.Clear();
            }
            if (mode == Together)
                LongEventHandler.ExecuteWhenFinished(() => { if (Find.Maps.Any(m => m.IsPlayerHome)) Live.OpenTogether(); });
            // Separate: friends start their own colonies as soon as the planet shows up in the roster.
        }

        // ---- everyone ----

        /// One lobby window at a time.
        public static void ShowWindow() { if (!Find.WindowStack.IsOpen<LobbyWindow>()) Find.WindowStack.Add(new LobbyWindow()); }

        public static void SetReady(bool ready) =>
            Link.Send(new Msg("LobbyMember", "hub", new XElement("m", new XAttribute("ready", ready))));

        /// Every frame: open the lobby window for friends, and follow the host when the game starts.
        // ---- asking the host (feature:AskHost) ----

        /// What I asked the host for: "new" (a new game together) or "join" (their colony); followed up automatically.
        static string asked;
        /// host: Start was pressed in a game, so the new-game screens open once the main menu is up
        static bool startAfterMenu;

        public static void Init()
        {
            Link.On("WantTogether", OnWant);
            Link.On("MyColonists", OnFriendColonists);
        }

        public static void Ask(string what)
        {
            asked = what;
            Link.Send(new Msg("WantTogether", Roster.HubId, new XElement("what", what)));
            Messages.Message("RF_AskedHost".Translate(Roster.Get(Roster.HubId)?.player ?? "?"), MessageTypeDefOf.NeutralEvent, false);
        }

        static void OnWant(Msg m)
        {
            if (!IAmHost) return;
            var who = Roster.Get(m.From)?.player ?? "?";
            bool playing = Current.ProgramState == ProgramState.Playing;
            if (m.Get("what") == "join")
            {
                if (Live.InSession) { Link.SendHello(); return; }   // already open: their join button is there
                if (!playing || !Find.Maps.Any(mp => mp.IsPlayerHome)) { Find.WindowStack.Add(new Dialog_MessageBox("RF_WantNoColony".Translate(who))); return; }
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_WantJoin".Translate(who), () => Compat.LiveCompat.ConfirmLive(Live.OpenTogether)));
            }
            else
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_WantNew".Translate(who), () => { if (!IsOpen) Open(); ShowWindow(); }));
        }

        /// host: Start from inside a game leaves it first.
        public static void StartFromAnywhere()
        {
            if (Current.ProgramState != ProgramState.Playing) { Start(); return; }
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_LobbyLeaveGame".Translate(), () => { startAfterMenu = true; GenScene.GoToMainMenu(); }, destructive: true));
        }

        public static void Update()
        {
            if (Time.realtimeSinceStartup < nextLook) return;
            nextLook = Time.realtimeSinceStartup + 0.5f;
            // whoever I asked said yes: follow them in
            if (asked == "join" && Roster.Together != null && !Live.InSession && Current.ProgramState != ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting)
            { asked = null; Live.JoinTogether(); }
            if (!IsOpen) { acted = false; return; }
            if (Current.ProgramState != ProgramState.Entry || LongEventHandler.AnyEventNowOrWaiting || Find.WindowStack == null) return;
            if (IAmHost)
            {
                if (startAfterMenu) { startAfterMenu = false; Start(); }
                return;
            }

            if (!Started)
            {
                acted = false;
                if (!IAmIn && !Find.WindowStack.IsOpen<LobbyWindow>() && !Find.WindowStack.IsOpen<JoinWelcomeWindow>()) Lobby.ShowWindow();
                return;
            }
            // the host is on the colonist page: mine opens by itself (feature:LandTogether)
            if (Mode == Together && Stage == "pawns" && EarlyColonists.mine == null && !EarlyColonists.making && !EarlyColonists.failed && IAmIn
                && !Find.WindowStack.Windows.Any(w => w is Page))
                EarlyColonists.Begin();
            if (acted || !(IAmIn || Find.WindowStack.IsOpen<LobbyWindow>())) return;
            // Already connecting (e.g. Multiplayer reconnects by itself after restarting with the host's settings).
            if (Live.InSession) { acted = true; return; }
            var host = Roster.Get(Roster.HubId);
            if (Mode == Together && Roster.Together != null)
            {
                acted = true;
                Find.WindowStack.WindowOfType<LobbyWindow>()?.Close();
                Live.JoinTogether();
            }
            else if (Mode == Separate && Roster.World != null && !(host?.colony).NullOrEmpty())
            {
                acted = true;
                Find.WindowStack.WindowOfType<LobbyWindow>()?.Close();
                JoinWelcome.StartOnPlanet();
            }
        }

        // ---- colors (feature:PlayerColors) ----

        public static readonly Color[] Palette =
        {
            new Color(0.90f, 0.30f, 0.30f), new Color(0.95f, 0.60f, 0.20f), new Color(0.95f, 0.85f, 0.30f), new Color(0.55f, 0.85f, 0.35f),
            new Color(0.25f, 0.75f, 0.55f), new Color(0.30f, 0.80f, 0.90f), new Color(0.35f, 0.55f, 0.95f), new Color(0.60f, 0.45f, 0.95f),
            new Color(0.90f, 0.45f, 0.85f), new Color(0.95f, 0.95f, 0.95f), new Color(0.60f, 0.45f, 0.30f), new Color(0.55f, 0.55f, 0.60f),
        };

        public static Color? MyColor => ColorUtility.TryParseHtmlString("#" + Rules.Local.playerColor, out var c) && !Rules.Local.playerColor.NullOrEmpty() ? c : (Color?)null;

        public static void SetMyColor(Color c)
        {
            Rules.Local.playerColor = ColorUtility.ToHtmlStringRGB(c);
            Rules.Local.Write();
            Link.SendHello();
        }

        /// A row of color swatches; the current one is outlined.
        public static float ColorPicker(Rect r)
        {
            float size = 26f;
            var mine = MyColor;
            for (int i = 0; i < Palette.Length; i++)
            {
                var sw = new Rect(r.x + i * (size + 4f), r.y, size, size);
                Widgets.DrawBoxSolid(sw, Palette[i]);
                if (mine.HasValue && ColorUtility.ToHtmlStringRGB(mine.Value) == ColorUtility.ToHtmlStringRGB(Palette[i])) Widgets.DrawBox(sw.ExpandedBy(2f), 2);
                if (Widgets.ButtonInvisible(sw)) SetMyColor(Palette[i]);
            }
            return size + 4f;
        }

        /// A player's color by their player or Multiplayer name; a steady fallback from the name otherwise.
        public static Color ColorOf(string name)
        {
            if (name == null) return Color.white;
            // a shared game keeps every player's color under their session name
            if (Current.Game?.GetComponent<RFGameComp>()?.ownerColors is Dictionary<string, string> cols && cols.TryGetValue(name, out var hex)
                && ColorUtility.TryParseHtmlString("#" + hex, out var kept)) return kept;
            if (name == Link.MyName || name == MpBridge.MyUsername) return MyColor ?? Roster.Get(Link.MyId)?.color ?? Palette[0];
            var f = Roster.Players.FirstOrDefault(p => p.player == name);
            if (f != null && f.color != Color.white) return f.color;
            return Palette[(name.GetHashCode() & 0x7fffffff) % Palette.Length];
        }
    }

    public class LobbyWindow : Window
    {
        public override Vector2 InitialSize => new Vector2(620f, 600f);

        public LobbyWindow()
        {
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            if (!Lobby.IAmHost && !Lobby.IAmIn) Lobby.SetReady(false);
        }

        bool starting;

        public override void PreClose()
        {
            base.PreClose();
            // closing the lobby cancels it, except when it closes because the host pressed Start
            if (Lobby.IAmHost && !Lobby.Started && !starting) Lobby.Close();
        }

        public override void DoWindowContents(Rect inRect)
        {
            var l = new Listing_Standard();
            l.Begin(inRect);
            Text.Font = GameFont.Medium;
            l.Label("RF_LobbyTitle".Translate());
            Text.Font = GameFont.Small;
            if (!Lobby.IsOpen && !Lobby.IAmHost) { l.Label("RF_LobbyClosed".Translate()); l.End(); return; }
            var hostName = Roster.Get(Roster.HubId)?.player ?? "?";
            l.Label(Lobby.IAmHost ? "RF_LobbyHostHint".Translate() : "RF_LobbyGuestHint".Translate(hostName));
            l.GapLine();

            // how the game starts
            bool host = Lobby.IAmHost && !Lobby.Started;
            l.Label("RF_LobbyMode".Translate());
            foreach (var mode in new[] { Lobby.Together, Lobby.Separate })
            {
                bool on = Lobby.Mode == mode;
                if (l.RadioButton(("RF_LobbyMode_" + mode).Translate(), on, 12f, ("RF_LobbyModeDesc_" + mode).Translate(), null, !host) && host && !on)
                    Lobby.SendState(true, mode, Lobby.Relation, false);
            }
            if (Lobby.Mode == Lobby.Together)
            {
                if (host)
                {
                    int n = Rules.Local.togetherStartPawns;
                    l.Label("RF_LobbyPawnsEach".Translate(n));
                    int v = Mathf.RoundToInt(l.Slider(n, 1, 5));
                    if (v != n) { Rules.Local.togetherStartPawns = v; Rules.Local.Write(); Link.SendHello(); }
                }
                else
                {
                    l.Label("RF_LobbyPawnsEach".Translate(RFSettings.TogetherStartPawns));
                    // make them now, while the host sets up the world
                    if (Lobby.Stage == "pawns" && EarlyColonists.mine == null)
                    {
                        GUI.color = new Color(1f, 0.85f, 0.4f);
                        l.Label("RF_HostAtPawns".Translate(Roster.Get(Roster.HubId)?.player ?? "?"));
                        GUI.color = Color.white;
                    }
                    if (EarlyColonists.mine != null)
                    {
                        GUI.color = new Color(0.5f, 0.9f, 0.5f);
                        l.Label("RF_EarlyHave".Translate(EarlyColonists.mine.Count));
                        GUI.color = Color.white;
                        if (Lobby.Stage == "pawns" && l.ButtonText("RF_EarlyRemake".Translate())) { EarlyColonists.failed = false; EarlyColonists.Begin(); }
                    }
                    else if (Lobby.Stage == "pawns" && !Live.InSession && l.ButtonText("RF_EarlyMake".Translate())) { EarlyColonists.failed = false; EarlyColonists.Begin(); }
                    else if (Lobby.Stage != "pawns") l.Label("RF_EarlyWaitHost".Translate(Roster.Get(Roster.HubId)?.player ?? "?"));
                }
            }
            l.Gap(6);
            l.Label("RF_LobbyRelation".Translate());
            foreach (var rel in Lobby.Relations)
            {
                bool on = Lobby.Relation == rel;
                if (l.RadioButton(("RF_Kind_" + rel).Translate(), on, 12f, ("RF_LobbyRelationDesc_" + rel).Translate(), null, !host) && host && !on)
                    Lobby.SendState(true, Lobby.Mode, rel, false);
            }
            l.GapLine();

            // my color
            l.Label("RF_LobbyMyColor".Translate());
            var pick = l.GetRect(30f);
            Lobby.ColorPicker(pick);
            l.Gap(4);

            // who's here
            l.Label("RF_LobbyPlayers".Translate());
            foreach (var f in Roster.Players.Where(p => p.online))
            {
                var r = l.GetRect(26f);
                Widgets.DrawBoxSolid(new Rect(r.x, r.y + 3, 20, 20), f.IsMe ? Lobby.MyColor ?? f.color : f.color);
                bool isHost = f.id == Roster.HubId;
                bool inLobby = isHost || Lobby.State?.Elements("m").Any(m => (string)m.Attribute("id") == f.id) == true;
                string status = isHost ? "RF_LobbyHost".Translate().ToString() : !inLobby ? "RF_LobbyNotIn".Translate().ToString()
                    : Lobby.IsReady(f.id) ? "RF_LobbyReady".Translate().ToString() : "RF_LobbyNotReady".Translate().ToString();
                Widgets.Label(new Rect(r.x + 28, r.y, r.width - 28, r.height), $"{f.player}{(f.IsMe ? " " + "RF_You".Translate() : "")}  —  {status}");
            }
            l.GapLine();

            if (Lobby.Started)
            {
                l.Label(Lobby.IAmHost ? "RF_LobbyHostSettingUp".Translate() : Lobby.Mode == Lobby.Together ? "RF_LobbyWaitTogether".Translate(hostName) : "RF_LobbyWaitSeparate".Translate(hostName));
                // by hand, in case the automatic step didn't happen
                if (!Lobby.IAmHost && Lobby.Mode == Lobby.Together && Roster.Together != null && !Live.InSession && l.ButtonText("RF_JoinTogether".Translate(hostName)))
                { Close(); Live.JoinTogether(); }
                if (!Lobby.IAmHost && Lobby.Mode == Lobby.Separate && Roster.World != null && l.ButtonText("RF_StartOnPlanet".Translate()))
                { Close(); JoinWelcome.StartOnPlanet(); }
            }
            else if (Lobby.IAmHost)
            {
                if (l.ButtonText("RF_LobbyStart".Translate()))
                {
                    var notReady = Roster.Players.Where(p => p.online && !p.IsMe && !Lobby.IsReady(p.id)).Select(p => p.player).ToList();
                    void Go() { starting = true; Close(false); Compat.LiveCompat.ConfirmLive(Lobby.StartFromAnywhere); }
                    if (notReady.Count > 0) Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_LobbyStartNotReady".Translate(string.Join(", ", notReady)), Go));
                    else Go();
                }
            }
            else
            {
                bool ready = Lobby.IsReady(Link.MyId);
                if (l.ButtonText(ready ? "RF_LobbyUnready".Translate() : "RF_LobbyImReady".Translate())) Lobby.SetReady(!ready);
            }
            l.End();
        }
    }

    // ================= together: make your own colonists (feature:MakeColonists) =================

    /// In a together game each friend gets their own starting colonists, generated in every game at once
    /// (synced, so the games stay the same) and waiting off the map until their player is happy with them.
    public static class Starters
    {
        static float nextCheck;

        static RFGameComp Comp => Current.Game?.GetComponent<RFGameComp>();
        public static List<Pawn> Of(string owner) => Comp?.starters.Where(p => Comp.OwnerOf(p) == owner).ToList() ?? new List<Pawn>();

        [SyncMethod]
        public static void Create(string owner)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (!comp.togetherOpen || comp.owners.Contains(owner)) return;
            for (int i = 0; i < RFSettings.TogetherStartPawns; i++)
            {
                var p = Generate(null);
                comp.SetOwner(p, owner);
                comp.awayHolder.TryAdd(p);
                comp.starters.Add(p);
            }
        }

        static Pawn Generate(Gender? gender)
        {
            var kind = Faction.OfPlayer.def.basicMemberKind ?? PawnKindDefOf.Colonist;
            return PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, PawnGenerationContext.PlayerStarter,
                forceGenerateNewPawn: true, fixedGender: gender));
        }

        // Waiting colonists aren't on a map, so Multiplayer can't send them: they go by owner and slot.
        static Pawn Slot(string owner, int slot) { var mine = Of(owner); return slot >= 0 && slot < mine.Count ? mine[slot] : null; }

        /// Swap one waiting colonist for a fresh one (optionally of a given gender).
        [SyncMethod]
        public static void Reroll(string owner, int slot, int gender)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            var pawn = Slot(owner, slot);
            if (pawn == null) return;
            var fresh = Generate(gender == 1 ? Gender.Male : gender == 2 ? Gender.Female : (Gender?)null);
            int i = comp.starters.IndexOf(pawn);
            comp.awayHolder.Remove(pawn);
            int oi = comp.ownedPawns.IndexOf(pawn);
            if (oi >= 0) { comp.ownedPawns.RemoveAt(oi); comp.owners.RemoveAt(oi); }
            Find.WorldPawns.PassToWorld(pawn, RimWorld.Planet.PawnDiscardDecideMode.Discard);
            comp.starters[i] = fresh;
            comp.SetOwner(fresh, owner);
            comp.awayHolder.TryAdd(fresh);
        }

        [SyncMethod]
        public static void Rename(string owner, int slot, string first, string nick, string last)
        {
            var pawn = Slot(owner, slot);
            if (pawn == null || first.NullOrEmpty()) return;
            pawn.Name = new NameTriple(first, nick.NullOrEmpty() ? first : nick, last ?? "");
        }

        /// Done: my colonists drop into the colony.
        [SyncMethod]
        public static void DropIn(string owner)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            var mine = comp.starters.Where(p => comp.OwnerOf(p) == owner).ToList();
            var map = Find.Maps.Where(m => m.IsPlayerHome).OrderByDescending(m => m.mapPawns.FreeColonistsSpawnedCount).FirstOrDefault();
            if (mine.Count == 0 || map == null) return;
            foreach (var p in mine) { comp.starters.Remove(p); comp.awayHolder.Remove(p); }
            DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, mine.Cast<Thing>(), forbid: false);
            StartingKit.Give(map, owner);
        }

        /// Every frame in a together session: show my "make your colonists" window while mine are waiting.
        public static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck || !Live.InSession || Comp == null) return;
            nextCheck = Time.realtimeSinceStartup + 1f;
            var me = MpBridge.MyUsername;
            if (me != null && Of(me).Count > 0 && !Find.WindowStack.IsOpen<MakeColonistsWindow>())
                Find.WindowStack.Add(new MakeColonistsWindow(me));
        }
    }

    public class MakeColonistsWindow : Window
    {
        readonly string owner;
        int selected;

        public override Vector2 InitialSize => new Vector2(980f, 720f);

        public MakeColonistsWindow(string owner)
        {
            this.owner = owner;
            doCloseX = false;
            closeOnCancel = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var mine = Starters.Of(owner);
            if (mine.Count == 0) { Close(); return; }
            selected = Mathf.Clamp(selected, 0, mine.Count - 1);
            var pawn = mine[selected];

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), "RF_MakeTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, 24f), "RF_MakeHint".Translate());

            // list of my colonists
            var list = new Rect(inRect.x, inRect.y + 70f, 170f, inRect.height - 120f);
            for (int i = 0; i < mine.Count; i++)
            {
                var r = new Rect(list.x, list.y + i * 64f, list.width, 60f);
                if (i == selected) Widgets.DrawHighlightSelected(r); else Widgets.DrawHighlightIfMouseover(r);
                Widgets.ThingIcon(new Rect(r.x + 4, r.y + 6, 48, 48), mine[i]);
                Widgets.Label(new Rect(r.x + 56, r.y + 8, r.width - 56, 48), mine[i].LabelShortCap + "\n" + mine[i].story?.TitleShortCap);
                if (Widgets.ButtonInvisible(r)) selected = i;
            }

            // the colonist's card, as on RimWorld's own start screen
            var card = new Rect(list.xMax + 12f, list.y, inRect.width - list.width - 12f, inRect.height - 120f);
            CharacterCardUtility.DrawCharacterCard(card, pawn, null, default, true);

            // buttons
            float by = inRect.yMax - 40f, bx = card.x;
            if (Widgets.ButtonText(new Rect(bx, by, 150, 36), "RF_MakeRandomize".Translate())) Starters.Reroll(owner, selected, 0);
            if (Widgets.ButtonText(new Rect(bx + 160, by, 110, 36), "RF_MakeMale".Translate())) Starters.Reroll(owner, selected, 1);
            if (Widgets.ButtonText(new Rect(bx + 280, by, 110, 36), "RF_MakeFemale".Translate())) Starters.Reroll(owner, selected, 2);
            if (Widgets.ButtonText(new Rect(bx + 400, by, 130, 36), "RF_MakeRename".Translate())) Find.WindowStack.Add(new RenameStarterWindow(owner, selected, pawn));
            if (Widgets.ButtonText(new Rect(inRect.xMax - 200, by, 200, 36), "RF_MakeDropIn".Translate()))
            {
                Starters.DropIn(owner);
                NewsFeed.Send("JoinedTogether", new XElement("player", owner), new XElement("count", mine.Count));
                Close();
            }
        }
    }

    public class RenameStarterWindow : Window
    {
        readonly string owner;
        readonly int slot;
        string first, nick, last;

        public override Vector2 InitialSize => new Vector2(420f, 260f);

        public RenameStarterWindow(string owner, int slot, Pawn pawn)
        {
            this.owner = owner;
            this.slot = slot;
            var n = pawn.Name as NameTriple;
            first = n?.First ?? pawn.LabelShort; nick = n?.Nick ?? ""; last = n?.Last ?? "";
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var l = new Listing_Standard();
            l.Begin(inRect);
            l.Label("RF_MakeFirst".Translate()); first = l.TextEntry(first);
            l.Label("RF_MakeNick".Translate()); nick = l.TextEntry(nick);
            l.Label("RF_MakeLast".Translate()); last = l.TextEntry(last);
            if (l.ButtonText("OK".Translate()) && !first.NullOrEmpty()) { Starters.Rename(owner, slot, first.Trim(), nick.Trim(), last.Trim()); Close(); }
            l.End();
        }
    }

    // ================= name tags (feature:NameTags) =================

    /// Each player's name over their pawns, in their color, whenever more than one player shares the game.
    [HarmonyPatch(typeof(PawnUIOverlay), nameof(PawnUIOverlay.DrawPawnGUIOverlay))]
    static class NameTags
    {
        static readonly AccessTools.FieldRef<PawnUIOverlay, Pawn> pawnField = AccessTools.FieldRefAccess<PawnUIOverlay, Pawn>("pawn");

        static void Postfix(PawnUIOverlay __instance)
        {
            if (!Rules.Local.nameTags) return;
            var pawn = pawnField(__instance);
            var comp = Current.Game?.GetComponent<RFGameComp>();
            if (pawn == null || comp == null || !pawn.Spawned) return;
            var owner = comp.OwnerOf(pawn);
            if (owner == null || comp.owners.Distinct().Count() < 2) return;
            var pos = (pawn.DrawPos + new Vector3(0f, 0f, 0.85f)).MapToUIPosition();
            GenMapUI.DrawThingLabel(pos, owner, Lobby.ColorOf(owner));
        }
    }
}
