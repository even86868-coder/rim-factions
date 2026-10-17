using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimFactions
{
    /// Everything Rim Factions keeps in a save.
    public class RFGameComp : GameComponent, IThingHolder
    {
        public List<AwayTrip> away = new List<AwayTrip>();                          // system:AwayPawns
        /// Colonists away on a trip wait here: not on a map and not world pawns, so the game leaves them alone.
        public ThingOwner<Pawn> awayHolder;
        public Dictionary<string, Faction> remoteFactions = new Dictionary<string, Faction>();
        public List<LiveVisit> liveVisits = new List<LiveVisit>();                  // feature:TripHelpLive
        public bool togetherOpen;                                                   // feature:StartTogether
        public List<Pawn> ownedPawns = new List<Pawn>();
        public List<string> owners = new List<string>();
        public List<Departure> departures = new List<Departure>();                  // feature:SplitOff
        public List<string> kits = new List<string>();
        public Dictionary<string, string> ownerColors = new Dictionary<string, string>();   // feature:PlayerColors: session name -> hex                               // feature:StartingKit: players who got theirs
        public List<Pawn> starters = new List<Pawn>();                              // feature:MakeColonists (held in awayHolder)
        public Dictionary<string, string> orderOwners = new Dictionary<string, string>();   // feature:PersonalWork: order key -> player who gave it
        public HashSet<string> personalWork = new HashSet<string>();                // feature:PersonalWork: players whose colonists only do their orders

        List<string> rfKeys; List<Faction> rfValues;

        public RFGameComp(Game game) { awayHolder = new ThingOwner<Pawn>(this, false, LookMode.Deep); }

        public IThingHolder ParentHolder => null;
        public ThingOwner GetDirectlyHeldThings() => awayHolder;
        public void GetChildHolders(List<IThingHolder> outChildren) => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

        public string OwnerOf(Pawn p) { int i = ownedPawns.IndexOf(p); return i >= 0 ? owners[i] : null; }

        public void SetOwner(Pawn p, string owner)
        {
            int i = ownedPawns.IndexOf(p);
            if (i >= 0) owners[i] = owner; else { ownedPawns.Add(p); owners.Add(owner); }
        }

        public LiveVisit VisitOf(Pawn p) => liveVisits.FirstOrDefault(v => !v.done && v.pawns.Contains(p));

        public override void StartedNewGame() => Lobby.HostGameStarted();

        public override void GameComponentTick() => SyncCheck.Tick();   // read-only fingerprints while live

        public override void FinalizeInit()
        {
            SaveCleanup.Run();   // before anything ticks
            Markers.Refresh();
            Net.Link.SendHello();
        }

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref awayHolder, "rfAwayHolder", this);
            Scribe_Collections.Look(ref away, "rfAway", LookMode.Deep);
            Scribe_Collections.Look(ref remoteFactions, "rfRemoteFactions", LookMode.Value, LookMode.Reference, ref rfKeys, ref rfValues);
            Scribe_Collections.Look(ref liveVisits, "rfLiveVisits", LookMode.Deep);
            Scribe_Values.Look(ref togetherOpen, "rfTogetherOpen");
            Scribe_Collections.Look(ref ownedPawns, "rfOwnedPawns", LookMode.Reference);
            Scribe_Collections.Look(ref owners, "rfOwners", LookMode.Value);
            Scribe_Collections.Look(ref departures, "rfDepartures", LookMode.Deep);
            Scribe_Collections.Look(ref starters, "rfStarters", LookMode.Reference);
            Scribe_Collections.Look(ref kits, "rfKits", LookMode.Value);
            Scribe_Collections.Look(ref ownerColors, "rfOwnerColors", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref orderOwners, "rfOrderOwners", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref personalWork, "rfPersonalWork", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                away = away ?? new List<AwayTrip>();
                awayHolder = awayHolder ?? new ThingOwner<Pawn>(this, false, LookMode.Deep);
                remoteFactions = remoteFactions ?? new Dictionary<string, Faction>();
                liveVisits = liveVisits ?? new List<LiveVisit>();
                ownedPawns = ownedPawns ?? new List<Pawn>();
                owners = owners ?? new List<string>();
                departures = departures ?? new List<Departure>();
                starters = starters?.Where(p => p != null).ToList() ?? new List<Pawn>();
                kits = kits ?? new List<string>();
                ownerColors = ownerColors ?? new Dictionary<string, string>();
                orderOwners = orderOwners ?? new Dictionary<string, string>();
                personalWork = personalWork ?? new HashSet<string>();
                for (int i = ownedPawns.Count - 1; i >= 0; i--)
                    if (ownedPawns[i] == null) { ownedPawns.RemoveAt(i); owners.RemoveAt(i); }
            }
        }
    }

    /// A friend's colonists in this colony during a live help trip.
    public class LiveVisit : IExposable
    {
        public string tripId, senderId;
        public List<Pawn> pawns = new List<Pawn>();
        public List<string> originalIds = new List<string>();
        public List<string> survivors = new List<string>(); // PawnData xml
        public bool done;

        public string OriginalId(Pawn p) { int i = pawns.IndexOf(p); return i >= 0 && i < originalIds.Count ? originalIds[i] : null; }

        public void ExposeData()
        {
            Scribe_Values.Look(ref tripId, "tripId");
            Scribe_Values.Look(ref senderId, "senderId");
            Scribe_Values.Look(ref done, "done");
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
            Scribe_Collections.Look(ref originalIds, "originalIds", LookMode.Value);
            Scribe_Collections.Look(ref survivors, "survivors", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { survivors = survivors ?? new List<string>(); pawns = pawns ?? new List<Pawn>(); }
        }
    }

    public class Departure : IExposable
    {
        public string owner;
        public Caravan caravan;

        public void ExposeData()
        {
            Scribe_Values.Look(ref owner, "owner");
            Scribe_References.Look(ref caravan, "caravan");
        }
    }
}
