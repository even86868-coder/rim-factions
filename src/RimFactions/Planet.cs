using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimFactions.Net;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// One player on the planet, as the hub last reported it.
    public class Friend
    {
        public string id, player, colony, faction;
        public Color color = Color.white;
        public int tile = -1, colonists, wealth;
        public bool online;
        public List<string> dlcs;   // null: an older Rim Factions that doesn't say
        public string layer;        // Odyssey map layer of their colony; empty or the surface layer for the planet surface
        public string steamId;
        public bool IsMe => id == Link.MyId;
        public string Label => colony.NullOrEmpty() ? player : $"{colony} ({player})";
    }

    /// system:Roster — what this game knows about the planet.
    public static class Roster
    {
        public static List<Friend> Players = new List<Friend>();
        public static XElement World, Together;
        public static string HubId;
        static readonly Dictionary<string, (int value, string kind)> goodwill = new Dictionary<string, (int, string)>();

        public static IEnumerable<Friend> Others => Players.Where(p => !p.IsMe);
        public static IEnumerable<Friend> OthersWithColony => Others.Where(p => !p.colony.NullOrEmpty());
        public static Friend Get(string id) => Players.FirstOrDefault(p => p.id == id);
        public static string NameOf(string id) => Get(id)?.Label ?? "?";

        public static int Goodwill(string a, string b) => goodwill.TryGetValue(Key(a, b), out var g) ? g.value : 0;
        public static string Kind(string a, string b) => goodwill.TryGetValue(Key(a, b), out var g) ? g.kind : "Neutral";
        static string Key(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        public static void Init()
        {
            Link.On("Roster", OnRoster);
            Link.On("News", NewsFeed.OnNews);
        }

        static void OnRoster(Msg m)
        {
            var r = m.x.Element("roster");
            if (r == null) return;
            Players = r.Elements("p").Select(p =>
            {
                var f = new Friend
                {
                    id = (string)p.Attribute("id"),
                    online = (bool)p.Attribute("online"),
                    player = (string)p.Element("player"),
                    colony = (string)p.Element("colony"),
                    faction = (string)p.Element("faction"),
                    steamId = (string)p.Element("steamId"),
                    tile = (int?)p.Element("tile") ?? -1,
                    colonists = (int?)p.Element("colonists") ?? 0,
                    wealth = (int?)p.Element("wealth") ?? 0,
                    dlcs = p.Element("dlcs") is XElement d ? d.Value.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries).ToList() : null,
                    layer = (string)p.Element("layer"),
                };
                if (ColorUtility.TryParseHtmlString("#" + (string)p.Element("color"), out var c)) f.color = c;
                return f;
            }).ToList();
            goodwill.Clear();
            foreach (var g in r.Elements("gw"))
                goodwill[Key((string)g.Attribute("a"), (string)g.Attribute("b"))] = ((int)g.Attribute("v"), (string)g.Attribute("kind"));
            World = r.Element("world");
            Together = r.Element("session");
            Lobby.State = r.Element("lobby");
            HubId = (string)r.Element("hub");
            if (r.Element("rules") is XElement rules)
            {
                Rules.FromHost = RimFactionsSettings.FromXml(rules);
                Rules.HostName = Get(HubId)?.player;
            }
            Markers.Refresh();
        }

        /// Asks the hub to change goodwill between me and a friend.
        public static void ChangeGoodwill(string friendId, int change, string reason) =>
            Link.Send(new Msg("Goodwill", "hub", new XElement("a", Link.MyId), new XElement("b", friendId),
                new XElement("change", change), new XElement("reason", reason)));
    }

    /// Friend colonies shown on this game's world map (system:Roster).
    public class FriendColony : WorldObject
    {
        public string friendId;
        public Friend Friend => Roster.Get(friendId);

        public override string Label => Friend?.Label ?? base.Label;
        public override Color ExpandingIconColor => Friend?.color ?? base.ExpandingIconColor;

        public override string GetInspectString()
        {
            var f = Friend;
            if (f == null) return "RF_FriendGone".Translate();
            return "RF_FriendInspect".Translate(f.player, f.online ? "RF_Online".Translate() : "RF_Offline".Translate(),
                f.colonists, ((float)f.wealth).ToStringMoney(), Roster.Goodwill(Link.MyId, f.id).ToString("+0;-0;0"),
                ("RF_Kind_" + Roster.Kind(Link.MyId, f.id)).Translate());
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(RimWorld.Planet.Caravan caravan)
        {
            foreach (var o in base.GetFloatMenuOptions(caravan)) yield return o;
            foreach (var o in RimWorld.Planet.CaravanArrivalActionUtility.GetFloatMenuOptions(
                         () => CaravanArrivalAction_TradeFriend.CanTrade(this), () => new CaravanArrivalAction_TradeFriend(this),
                         "RF_TradeWith".Translate(Label), caravan, Tile, this))
                yield return o;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref friendId, "friendId");
        }
    }

    public static class Markers
    {
        public static void Refresh()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.World == null || Live.InSession) return;
            var def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("RF_FriendColony");
            if (def == null) return;
            var existing = Find.WorldObjects.AllWorldObjects.OfType<FriendColony>().ToList();
            var surface = Find.WorldGrid.Surface;
            foreach (var f in Roster.OthersWithColony)
            {
                var marker = existing.FirstOrDefault(o => o.friendId == f.id);
                // Odyssey: only colonies on the planet surface get a surface marker
                if (!f.layer.NullOrEmpty() && f.layer != surface.Def?.defName) continue;
                int tile = FindTile(f.tile);
                if (tile < 0) continue;
                if (marker == null)
                {
                    marker = (FriendColony)WorldObjectMaker.MakeWorldObject(def);
                    marker.friendId = f.id;
                    marker.Tile = new PlanetTile(tile, surface);
                    Find.WorldObjects.Add(marker);
                }
                else if ((int)marker.Tile != tile)
                    marker.Tile = new PlanetTile(tile, surface);
                existing.Remove(marker);
            }
            foreach (var stale in existing.Where(o => Roster.Get(o.friendId) == null || o.friendId == Link.MyId)) stale.Destroy();
        }

        /// The friend's tile, or the nearest free land tile when this world's tile is taken or water.
        static int FindTile(int wanted)
        {
            var grid = Find.WorldGrid;
            if (wanted < 0 || wanted >= grid.TilesCount) return -1;
            bool Free(int t) => !grid[t].WaterCovered && Find.WorldObjects.ObjectsAt(t).All(o => o is FriendColony);
            if (Free(wanted)) return wanted;
            if (TileFinder.TryFindPassableTileWithTraversalDistance(wanted, 1, 6, out var near, t => Free(t))) return near;
            return wanted;
        }
    }

    /// feature:SharedPlanet — the world the hub plays on, captured and pre-filled for joiners.
    public static class WorldParams
    {
        public static XElement Capture()
        {
            var i = Find.World?.info;
            if (i == null) return null;
            return new XElement("world", new XAttribute("seed", i.seedString), new XAttribute("coverage", i.planetCoverage),
                new XAttribute("rain", (int)i.overallRainfall), new XAttribute("temp", (int)i.overallTemperature),
                new XAttribute("pop", (int)i.overallPopulation), new XAttribute("pollution", i.pollution),
                new XAttribute("landmarks", (int)i.landmarkDensity));
        }

        public static bool usePlanet;
    }

    [HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.PreOpen))]
    static class WorldParamsPrefill
    {
        static void Postfix(Page_CreateWorldParams __instance)
        {
            var w = Roster.World;
            if (!WorldParams.usePlanet || w == null || Link.Role != LinkRole.Client) return;
            var t = Traverse.Create(__instance);
            t.Field("seedString").SetValue((string)w.Attribute("seed"));
            t.Field("planetCoverage").SetValue((float)w.Attribute("coverage"));
            t.Field("rainfall").SetValue((OverallRainfall)(int)w.Attribute("rain"));
            t.Field("temperature").SetValue((OverallTemperature)(int)w.Attribute("temp"));
            t.Field("population").SetValue((OverallPopulation)(int)w.Attribute("pop"));
            t.Field("pollution").SetValue((float)w.Attribute("pollution"));
            t.Field("landmarkDensity").SetValue((LandmarkDensity)(int)w.Attribute("landmarks"));
            Messages.Message("RF_UsingFriendPlanet".Translate(), MessageTypeDefOf.NeutralEvent, false);
        }
    }

    /// system:NewsLetters — News messages become letters in this game.
    public static class NewsFeed
    {
        public static void Send(string news, params XElement[] fields) =>
            Link.Send(new Msg("News", "*", new XElement("news", news), fields));

        static readonly List<Msg> pending = new List<Msg>();

        /// Shows news held back while a live session was running (letters there would desync it).
        public static void Flush()
        {
            if (Current.ProgramState != ProgramState.Playing || Live.InSession) return;
            var list = pending.ToList();
            pending.Clear();
            foreach (var m in list) OnNews(m);
        }

        public static void OnNews(Msg m)
        {
            if (Current.ProgramState != ProgramState.Playing || Live.InSession) { pending.Add(m); return; }
            var kind = m.Get("news");
            string N(string field) => Roster.NameOf(m.Get(field));
            TaggedString label, text;
            LetterDef def = LetterDefOf.NeutralEvent;
            switch (kind)
            {
                case "GiftSent":
                    label = "RF_NewsGiftLabel".Translate(); def = LetterDefOf.PositiveEvent;
                    text = "RF_NewsGiftText".Translate(N("from"), N("to"), m.Get("summary"), m.Get("value"));
                    break;
                case "ColonistGifted":
                    label = "RF_NewsColonistLabel".Translate(); def = LetterDefOf.PositiveEvent;
                    text = "RF_NewsColonistText".Translate(N("from"), N("to"), m.Get("pawn"));
                    break;
                case "TripArrived":
                    label = "RF_NewsTripLabel".Translate(); def = m.Get("stance") == "raid" ? LetterDefOf.ThreatBig : LetterDefOf.PositiveEvent;
                    text = ("RF_NewsTripText_" + m.Get("stance")).Translate(N("from"), N("to"), m.Get("count"), ("RF_Mode_" + m.Get("mode")).Translate());
                    break;
                case "TripReturned":
                    label = "RF_NewsReturnLabel".Translate();
                    text = "RF_NewsReturnText".Translate(N("from"), N("to"), m.Get("survivors"), m.Get("dead"), m.Get("captured"));
                    break;
                case "RelationChanged":
                    label = "RF_NewsRelationLabel".Translate(); def = m.Get("kind") == "Hostile" ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent;
                    text = "RF_NewsRelationText".Translate(N("a"), N("b"), ("RF_Kind_" + m.Get("kind")).Translate(), ("RF_Kind_" + m.Get("previous")).Translate());
                    break;
                case "TradeDone":
                    label = "RF_NewsTradeLabel".Translate();
                    text = "RF_NewsTradeText".Translate(N("from"), N("to"), m.Get("bought"), m.Get("sold"));
                    break;
                case "PlanetDefended":
                    label = "RF_NewsDefendedLabel".Translate(); def = LetterDefOf.PositiveEvent;
                    var names = (m.Get("helpers") ?? "").Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries).Select(Roster.NameOf).ToList();
                    text = names.Count > 0 ? "RF_NewsDefendedText".Translate(string.Join(", ", names), RFSettings.PlanetEventRewardSilver) : "RF_NewsDefendedNobody".Translate();
                    break;
                case "JoinedPlanet":
                    label = "RF_NewsJoinedLabel".Translate();
                    text = "RF_NewsJoinedText".Translate(m.Get("player"));
                    break;
                case "JoinedTogether":
                    label = "RF_NewsTogetherLabel".Translate(); def = LetterDefOf.PositiveEvent;
                    text = "RF_NewsTogetherText".Translate(m.Get("player"), m.Get("count"));
                    break;
                case "SplitOff":
                    label = "RF_NewsSplitLabel".Translate();
                    text = "RF_NewsSplitText".Translate(m.Get("player"), m.Get("count"));
                    break;
                default: return;
            }
            Find.LetterStack.ReceiveLetter(label, text, def);
        }
    }
}
