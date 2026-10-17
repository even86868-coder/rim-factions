using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RimFactions.Net;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimFactions
{
    /// Gifts, trips and returns that reach this game while it isn't in a playable solo game are kept here
    /// (on disk, so a quit doesn't lose them) and handled once a colony is loaded.
    public static class Inbox
    {
        static readonly List<Msg> items = new List<Msg>();
        static string PathFile => System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RimFactions", "inbox.xml");
        static bool loaded;

        public static void Init()
        {
            foreach (var t in new[] { "Gift", "Trip", "TripReturn", "LiveReady", "LiveDecline", "TradeDeal", "PlanetEvent", "PlanetReward" })
                Link.On(t, Add);
        }

        static void Add(Msg m)
        {
            Load();
            items.Add(m);
            Save();
            Process();
        }

        public static bool CanHandleNow => Current.ProgramState == ProgramState.Playing && !Live.InSession && !LongEventHandler.AnyEventNowOrWaiting
                                            && Find.Maps.Any(mp => mp.IsPlayerHome);

        public static void Process()
        {
            Load();
            if (!CanHandleNow || items.Count == 0) return;
            foreach (var m in items.ToList())
            {
                bool done = true;
                try
                {
                    switch (m.Type)
                    {
                        case "Gift": Gifts.Receive(m); break;
                        case "Trip": done = Trips.Receive(m); break;
                        case "TripReturn": Trips.ReceiveReturn(m); break;
                        case "LiveReady": Live.OnReady(m); break;
                        case "LiveDecline": Live.OnDecline(m); break;
                        case "TradeDeal": Trade.ApplyDeal(m); break;
                        case "PlanetEvent": PlanetEvents.Start(m); break;
                        case "PlanetReward": PlanetEvents.Reward(m); break;
                    }
                }
                catch (Exception e) { Log.Error($"[Rim Factions] {m.Type} failed: {e}"); }
                if (done) items.Remove(m);
            }
            Save();
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (File.Exists(PathFile))
                    foreach (var e in XElement.Load(PathFile).Elements("m"))
                        if (Msg.Parse(e.ToString()) is Msg m) items.Add(m);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] inbox unreadable: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile));
                new XElement("inbox", items.Select(m => m.x)).Save(PathFile);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] inbox save failed: " + e.Message); }
        }
    }

    /// Where things from friends land in this game.
    public static class Arrival
    {
        public static Map HomeMap => Find.Maps.Where(m => m.IsPlayerHome).OrderByDescending(m => m.mapPawns.FreeColonistsSpawnedCount).FirstOrDefault();

        public static IntVec3 DropAt(Map map, List<Thing> things)
        {
            var spot = DropCellFinder.TradeDropSpot(map);
            DropPodUtility.DropThingsNear(spot, map, things, forbid: false, allowFogged: false);
            return spot;
        }
    }

    /// Local NPC factions standing in for friends' colonies (AI trips, gifted colonists).
    public static class RemoteFactions
    {
        public static Faction Get(string friendId)
        {
            var comp = Current.Game.GetComponent<RFGameComp>();
            if (comp.remoteFactions.TryGetValue(friendId, out var f) && f != null && Find.FactionManager.AllFactionsListForReading.Contains(f))
            {
                Refresh(f, friendId);
                return f;
            }
            var def = DefDatabase<FactionDef>.GetNamed("RF_RemoteColony");
            f = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(def, default, true));
            Find.FactionManager.Add(f);
            foreach (var other in Find.FactionManager.AllFactionsListForReading)
                if (other != f && other.RelationWith(f, true) == null)
                {
                    other.SetRelation(new FactionRelation(f, FactionRelationKind.Neutral));
                }
            comp.remoteFactions[friendId] = f;
            Refresh(f, friendId);
            return f;
        }

        static void Refresh(Faction f, string friendId)
        {
            var friend = Roster.Get(friendId);
            if (friend == null) return;
            f.Name = friend.colony.NullOrEmpty() ? friend.player : friend.colony;
            f.color = friend.color;
        }

        public static void SetRelation(Faction f, FactionRelationKind kind)
        {
            f.SetRelationDirect(Faction.OfPlayer, kind, false);
            foreach (var map in Find.Maps) map.attackTargetsCache.Notify_FactionHostilityChanged(Faction.OfPlayer, f);
        }
    }
}
