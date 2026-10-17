using System;
using System.Collections.Generic;
using System.IO;
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
    /// system:LiveSession — the only times games run in lockstep: feature:TripHelpLive and
    /// feature:StartTogether / feature:SplitOff. Everything else stays in each player's own save.
    public static class Live
    {
        public static bool InSession => MpBridge.InSession;
        static RFGameComp Comp => Current.Game?.GetComponent<RFGameComp>();

        // sender side of a live help request, waiting for the friend's answer
        public static (string tripId, string friendId, List<Pawn> pawns) pendingRequest;
        static string pendingConnect;
        static float connectAt;

        // host side
        static string readyFor;             // tripId to answer once hosting is up
        static bool togetherAnnounce;
        static readonly HashSet<string> returnSent = new HashSet<string>();
        static float convertAt = -1;
        static readonly HashSet<string> removing = new HashSet<string>();
        static bool hadSession;
        static float sessionSince;
        static bool wasInSession;

        // the visitor's own game while it is away (survives restarts)
        class VisitorState { public string tripId, homeSave, friendId; }
        static VisitorState visitor;
        static string StatePath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimFactions", "live.xml");

        // together
        static bool claimed;
        public static string splitPending;
        static bool ownersLogged;

        public static void Init()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var x = XElement.Load(StatePath);
                    visitor = new VisitorState { tripId = (string)x.Attribute("trip"), homeSave = (string)x.Attribute("home"), friendId = (string)x.Attribute("friend") };
                }
            }
            catch { }
        }

        static void SaveVisitor()
        {
            try
            {
                if (visitor == null) { if (File.Exists(StatePath)) File.Delete(StatePath); return; }
                new XElement("live", new XAttribute("trip", visitor.tripId), new XAttribute("home", visitor.homeSave), new XAttribute("friend", visitor.friendId)).Save(StatePath);
            }
            catch { }
        }

        static string PickAddress(string steam, string lan, string friendSteamId)
        {
            bool sameSteam = friendSteamId != null && friendSteamId == SteamTransport.MyId.ToString();
            if (steam != null && !sameSteam && SteamTransport.Available) return steam;
            return lan ?? steam;
        }

        // ================= live help (feature:TripHelpLive) =================

        /// Friend's game: ask whether to open a live session for this trip.
        public static void AskAccept(Msg m)
        {
            var count = m.x.Element("pawns").Elements("pawn").Count();
            if (TestHarness.AutoAccept) { Accept(m); return; }
            Find.WindowStack.Add(new Dialog_MessageBox(
                "RF_LiveAsk".Translate(Roster.NameOf(m.From), count),
                "RF_Accept".Translate(), () => Accept(m),
                "RF_Decline".Translate(), () => Link.Send(new Msg("LiveDecline", m.From, new XElement("tripId", m.Get("tripId")), new XElement("reason", "declined"))),
                title: "RF_LiveAskTitle".Translate()));
        }

        static void Accept(Msg m)
        {
            if (InSession) { Link.Send(new Msg("LiveDecline", m.From, new XElement("tripId", m.Get("tripId")), new XElement("reason", "busy"))); return; }
            var map = Arrival.HomeMap;
            var visit = new LiveVisit { tripId = m.Get("tripId"), senderId = m.From };
            var pawns = new List<Thing>();
            foreach (var px in m.x.Element("pawns").Elements("pawn"))
            {
                var p = PawnData.Make(px, Faction.OfPlayer);
                visit.pawns.Add(p);
                visit.originalIds.Add((string)px.Attribute("id"));
                pawns.Add(p);
            }
            Comp.liveVisits.Add(visit);
            Arrival.DropAt(map, pawns);
            Roster.ChangeGoodwill(m.From, RFSettings.HelpTripGoodwill, "help");
            NewsFeed.Send("TripArrived", new XElement("from", m.From), new XElement("to", Link.MyId), new XElement("stance", "help"),
                new XElement("mode", "live"), new XElement("count", pawns.Count));
            readyFor = visit.tripId;
            if (!MpBridge.Host(Find.World.info.name + " (Rim Factions)"))
                Messages.Message("RF_HostFailed".Translate(), MessageTypeDefOf.RejectInput, false);
        }

        /// Sender's game: the friend is hosting; take the colonists away, save, and join.
        public static void OnReady(Msg m)
        {
            var req = pendingRequest;
            if (req.tripId != m.Get("tripId")) return;
            pendingRequest = default;
            Trips.Away(req.pawns, req.tripId, req.friendId, "help", "live");
            var home = "RimFactions-home-" + GenFile.SanitizedFileName(Faction.OfPlayer.Name);
            GameDataSaveLoader.SaveGame(home);
            visitor = new VisitorState { tripId = req.tripId, homeSave = home, friendId = req.friendId };
            SaveVisitor();
            pendingConnect = PickAddress(m.Get("steam"), m.Get("lan"), Roster.Get(req.friendId)?.steamId);
            connectAt = Time.realtimeSinceStartup + 1f;
            GenScene.GoToMainMenu();
        }

        public static void OnDecline(Msg m)
        {
            if (pendingRequest.tripId == m.Get("tripId")) pendingRequest = default;
            Messages.Message("RF_LiveDeclined".Translate(Roster.NameOf(m.From)), MessageTypeDefOf.RejectInput, false);
        }

        // ================= gizmos during a session =================

        public static IEnumerable<Gizmo> Gizmos(Pawn p)
        {
            var comp = Comp;
            if (comp == null) yield break;
            if (comp.VisitOf(p) is LiveVisit visit)
                yield return new Command_Once
                {
                    defaultLabel = "RF_SendVisitorsHome".Translate(),
                    defaultDesc = "RF_SendVisitorsHomeDesc".Translate(),
                    icon = RFTex.Trip,
                    action = () => SendHome(visit.tripId, Find.Selector.SelectedPawns.Where(x => comp.VisitOf(x) == visit).ToList())
                };
            // feature:SplitOff: only from a caravan out on the world map (CaravanSplitOff), never from colonists on the map
        }

        /// Synced: this caravan of mine leaves the shared colony to become my own game.
        [SyncMethod]
        public static void DepartCaravan(Caravan caravan, string owner)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (caravan == null || caravan.Destroyed || comp.departures.Any(d => d.owner == owner)) return;
            if (!caravan.PawnsListForReading.Any(p => comp.OwnerOf(p) == owner)) return;
            comp.departures.Add(new Departure { owner = owner, caravan = caravan });
        }

        [SyncMethod]
        public static void SendHome(string tripId, List<Pawn> pawns)
        {
            var visit = Current.Game.GetComponent<RFGameComp>().liveVisits.FirstOrDefault(v => v.tripId == tripId);
            if (visit == null) return;
            if (pawns == null || pawns.Count == 0) pawns = visit.pawns.ToList();
            foreach (var p in pawns.Where(p => visit.pawns.Contains(p) && !p.Dead && p.Spawned))
            {
                var data = PawnData.Capture(p);
                data.SetAttributeValue("id", visit.OriginalId(p));
                visit.survivors.Add(data.ToString(SaveOptions.DisableFormatting));
                p.DeSpawn();
                Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Discard);
            }
            if (visit.pawns.All(p => p.Dead || visit.survivors.Any(s => s.Contains($"id=\"{visit.OriginalId(p)}\""))))
                visit.done = true;
        }

        /// Host's button: everyone visiting goes home.
        public static void EndVisits()
        {
            foreach (var v in Comp.liveVisits.Where(v => !v.done).ToList()) SendHome(v.tripId, new List<Pawn>());
        }

        // ================= together (feature:StartTogether, feature:SplitOff) =================

        /// The name Multiplayer gives me in a session (owners are kept under it).
        public static string MyName() =>
            AccessTools.Field(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "username")?.GetValue(null) as string ?? Link.MyName;

        public static void OpenTogether()
        {
            var comp = Comp;
            var me = AccessTools.Field(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "username")?.GetValue(null) as string ?? Link.MyName;
            // a colony nobody owns yet becomes the host's; a shared save keeps who owns what (newcomers stay shared)
            if (comp.owners.Count == 0)
                foreach (var p in PawnsFinder.AllMaps_FreeColonists.Where(p => p.Faction == Faction.OfPlayer && comp.OwnerOf(p) == null))
                    comp.SetOwner(p, me);
            comp.togetherOpen = true;
            if (!comp.kits.Contains(me)) comp.kits.Add(me);   // feature:StartingKit: the host had the scenario's
            togetherAnnounce = true;
            if (!MpBridge.Host(Find.World.info.name + " (Rim Factions)"))
                Messages.Message("RF_HostFailed".Translate(), MessageTypeDefOf.RejectInput, false);
        }

        public static void JoinTogether()
        {
            var s = Roster.Together;
            if (s == null) return;
            if (ReloadSave.Reloading) { Lobby.FollowHost(); return; }   // the host is loading a save: join once it's up
            var host = Roster.Players.FirstOrDefault(p => p.id == (string)s.Attribute("host"));
            pendingConnect = PickAddress((string)s.Attribute("steam"), (string)s.Attribute("lan"), host?.steamId);
            connectAt = Time.realtimeSinceStartup + 1f;
            claimed = false;
            if (Current.ProgramState == ProgramState.Playing) GenScene.GoToMainMenu();
        }

        [SyncMethod]
        public static void Depart(List<Pawn> pawns, string owner)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            pawns = pawns.Where(p => p.Spawned && comp.OwnerOf(p) == owner).ToList();
            if (pawns.Count == 0) return;
            var caravan = CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, pawns[0].Map.Tile, true);
            comp.departures.Add(new Departure { owner = owner, caravan = caravan });
        }

        [SyncMethod]
        public static void RemoveDeparted(string owner)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            foreach (var d in comp.departures.Where(d => d.owner == owner).ToList())
            {
                if (d.caravan != null && !d.caravan.Destroyed)
                {
                    foreach (var p in d.caravan.PawnsListForReading.ToList()) { d.caravan.RemovePawn(p); Discard(p); }
                    d.caravan.Destroy();
                }
                comp.departures.Remove(d);
            }
        }

        static void Discard(Pawn p)
        {
            if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(p);
            else Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Discard);
        }

        /// The leaver's game after it turned solo: drop the shared colony, keep my caravan.
        static void FinishSplit(string owner)
        {
            var comp = Comp;
            var mine = comp.departures.FirstOrDefault(d => d.owner == owner)?.caravan;
            foreach (var map in Find.Maps.ToList())
            {
                var parent = map.Parent;
                Current.Game.DeinitAndRemoveMap(map, false);
                if (parent != null && !parent.Destroyed) parent.Destroy();
            }
            foreach (var c in Find.WorldObjects.Caravans.Where(c => c != mine && c.Faction == Faction.OfPlayer).ToList())
            {
                foreach (var p in c.PawnsListForReading.ToList()) { c.RemovePawn(p); Discard(p); }
                c.Destroy();
            }
            comp.departures.Clear(); comp.ownedPawns.Clear(); comp.owners.Clear(); comp.liveVisits.Clear();
            comp.togetherOpen = false;
            NewsFeed.Send("SplitOff", new XElement("player", owner), new XElement("count", mine?.PawnsListForReading.Count ?? 0));
            if (mine != null) { CameraJumper.TryJumpAndSelect(mine); }
            Find.LetterStack.ReceiveLetter("RF_SplitDoneLabel".Translate(), "RF_SplitDoneText".Translate(), LetterDefOf.PositiveEvent, mine);
            Link.SendHello();
            Markers.Refresh();
            var save = "RimFactions-" + GenFile.SanitizedFileName(owner);
            LongEventHandler.QueueLongEvent(() => GameDataSaveLoader.SaveGame(save), "SavingLongEvent", false, null);
        }

        // ================= driving it, once per frame =================

        /// feature:LoadSaveTogether: the session ends on purpose, so don't tell friends it closed.
        public static void ForgetSession() => hadSession = false;

        /// feature:LoadSaveTogether: once hosting, tell friends where to join.
        public static void AnnounceWhenHosting() => togetherAnnounce = true;

        /// A join is queued or Multiplayer is connecting.
        public static bool Connecting => pendingConnect != null || InSession;

        public static void Update()
        {
            // joining: from the main menu, connect to the session
            if (pendingConnect != null && Current.ProgramState == ProgramState.Entry && Time.realtimeSinceStartup > connectAt && !LongEventHandler.AnyEventNowOrWaiting)
            {
                var addr = pendingConnect;
                pendingConnect = null;
                if (InSession) return;   // Multiplayer is already connecting this game
                if (!MpBridge.Connect(addr)) Messages.Message("RF_ConnectFailed".Translate(addr), MessageTypeDefOf.RejectInput, false);
            }

            if (!InSession) wasInSession = false;
            var comp = Comp;
            if (Current.ProgramState != ProgramState.Playing || comp == null) return;

            if (InSession)
            {
                if (!wasInSession) { sessionSince = Time.realtimeSinceStartup; ownersLogged = false; }
                if (Time.realtimeSinceStartup - sessionSince > 15f) Compat.HostConfigs.AdoptIfApplied();
                if (!ownersLogged && Time.realtimeSinceStartup - sessionSince > 20f) { ownersLogged = true; Ownership.LogOwners(); }
                if (MP.InInterface && Time.realtimeSinceStartup - sessionSince > 6f && Time.frameCount % 300 == 0) Ownership.PushMyColor();
                wasInSession = true;
                hadSession = true;
                var me = MpBridge.MyUsername;
                // Synced calls only once the session is settled and we're in interface context; otherwise
                // Multiplayer would run them locally and the games would drift apart.
                bool canSync = MP.InInterface && Time.realtimeSinceStartup - sessionSince > 5f;
                // host: tell the friend where to join
                if (MpBridge.Hosting && (readyFor != null || togetherAnnounce))
                {
                    var (steam, lan) = MpBridge.Addresses();
                    if (steam != null || lan != null)
                    {
                        if (readyFor != null)
                        {
                            var v = comp.liveVisits.FirstOrDefault(x => x.tripId == readyFor);
                            if (v != null) Link.Send(new Msg("LiveReady", v.senderId, new XElement("tripId", v.tripId), new XElement("steam", steam), new XElement("lan", lan)));
                            readyFor = null;
                        }
                        if (togetherAnnounce)
                        {
                            Link.Send(new Msg("Together", "hub", new XElement("session", new XAttribute("open", true), new XAttribute("host", Link.MyId),
                                new XAttribute("steam", steam ?? ""), new XAttribute("lan", lan ?? ""), new XAttribute("name", Find.World.info.name))));
                            togetherAnnounce = false;
                        }
                    }
                }

                // host: finished visits go home, then back to solo
                if (MpBridge.Hosting)
                {
                    foreach (var v in comp.liveVisits.Where(v => v.done && !returnSent.Contains(v.tripId)))
                    {
                        returnSent.Add(v.tripId);
                        var dead = v.pawns.Where(p => p.Dead).ToList();
                        Link.Send(new Msg("TripReturn", v.senderId, new XElement("tripId", v.tripId),
                            new XElement("survivors", v.survivors.Select(XElement.Parse)),
                            new XElement("dead", dead.Select(p => new XElement("pawn", new XAttribute("id", v.OriginalId(p))))),
                            new XElement("captured")));
                        NewsFeed.Send("TripReturned", new XElement("from", v.senderId), new XElement("to", Link.MyId),
                            new XElement("survivors", v.survivors.Count), new XElement("dead", dead.Count), new XElement("captured", 0));
                        if (!comp.togetherOpen && comp.liveVisits.All(x => x.done)) convertAt = Time.realtimeSinceStartup + 4f;
                    }
                    if (convertAt > 0 && Time.realtimeSinceStartup > convertAt)
                    {
                        convertAt = -1;
                        comp.liveVisits.RemoveAll(x => x.done);
                        MpBridge.ConvertToSingleplayer();
                        return;
                    }
                    // a player who split off and left: their caravan leaves the shared game
                    foreach (var d in comp.departures.Where(d => canSync && !MpBridge.PlayerConnected(d.owner) && !removing.Contains(d.owner)).ToList())
                    {
                        removing.Add(d.owner);
                        RemoveDeparted(d.owner);
                    }
                }

                // visitor: my trip is over, go home
                if (visitor != null && comp.liveVisits.Any(v => v.tripId == visitor.tripId && v.done) && !MpBridge.Hosting)
                {
                    var home = visitor.homeSave;
                    visitor = null;
                    SaveVisitor();
                    MpBridge.Stop();
                    GameDataSaveLoader.LoadGame(home);
                    return;
                }

                // together: claim my starting colonists once
                // my colonists already landed with the host's (feature:LandTogether): nothing left to bring
                if (canSync && comp.owners.Contains(me) && EarlyColonists.mine != null) EarlyColonists.mine = null;
                if (canSync && comp.togetherOpen && me != null && !claimed && !MpBridge.Hosting && !comp.owners.Contains(me))
                {
                    claimed = true;
                    // feature:MakeColonistsEarly: the colonists I made while the host set up; else feature:MakeColonists
                    if (!EarlyColonists.TryArrive(me)) Starters.Create(me, RFSettings.TogetherStartPawns);
                }

                // split: my caravan exists, turn my copy into my own game
                if (me != null && splitPending == null && comp.departures.Any(d => d.owner == me) && !MpBridge.Hosting)
                {
                    splitPending = me;
                    MpBridge.ConvertToSingleplayer();
                }
                return;
            }

            if (splitPending != null && !LongEventHandler.AnyEventNowOrWaiting)
            {
                var owner = splitPending;
                splitPending = null;
                FinishSplit(owner);
            }
            if (comp.togetherOpen && hadSession && !MpBridge.Hosting)
            {
                // the session ended (host converted back to solo)
                hadSession = false;
                comp.togetherOpen = false;
                Link.Send(new Msg("Together", "hub", new XElement("session", new XAttribute("open", false))));
            }
            NewsFeed.Flush();
            Inbox.Process();
        }

        public static bool VisitorAway => visitor != null;
        public static string VisitorFriend => visitor?.friendId;
    }
}

namespace RimFactions
{
    /// feature:SplitOff on a caravan: a caravan out on the world map made of my own colonists (no one else's) can found
    /// my own colony; a clear warning asks first, so it can't be clicked by mistake.
    [HarmonyLib.HarmonyPatch(typeof(RimWorld.Planet.Caravan), nameof(RimWorld.Planet.Caravan.GetGizmos))]
    static class CaravanSplitOff
    {
        static System.Collections.Generic.IEnumerable<Gizmo> Postfix(System.Collections.Generic.IEnumerable<Gizmo> __result, RimWorld.Planet.Caravan __instance)
        {
            foreach (var g in __result) yield return g;
            var comp = Current.Game?.GetComponent<RFGameComp>();
            var me = MpBridge.MyUsername;
            // the host keeps the shared colony; friends split off from it
            if (comp == null || !comp.togetherOpen || me == null || MpBridge.Hosting || __instance.Faction != Faction.OfPlayer) yield break;
            var people = __instance.PawnsListForReading.Where(p => p.RaceProps.Humanlike).ToList();
            if (people.Count == 0 || people.Any(p => comp.OwnerOf(p) != me)) yield break;   // only my own colonists in it
            var caravan = __instance;
            yield return new Command_Action
            {
                defaultLabel = "RF_SplitOff".Translate(),
                defaultDesc = "RF_SplitOffDesc".Translate(),
                icon = RFTex.Trip,
                action = () => Find.WindowStack.Add(new Dialog_MessageBox("RF_SplitConfirm".Translate(people.Count),
                    "RF_SplitConfirmYes".Translate(), () => Live.DepartCaravan(caravan, me),
                    "Cancel".Translate(), null, "RF_SplitConfirmTitle".Translate(), buttonADestructive: true))
            };
        }
    }
}
