using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
using Verse;

namespace RimFactions.Net
{
    /// system:LinkHub — runs inside the hosting player's game. Keeps the planet's roster, goodwill
    /// (system:Goodwill), world params and the together session, relays messages and queues them for
    /// players who are offline. State survives restarts in {localappdata}/RimFactions/hub.xml.
    public class Hub
    {
        public class Player
        {
            public string id, peer;
            public XElement hello;
            public bool online;
            public string Name => (string)hello?.Element("player") ?? "?";
        }

        readonly Dictionary<string, Player> players = new Dictionary<string, Player>();
        readonly Dictionary<string, string> peerToPlayer = new Dictionary<string, string>();
        readonly Dictionary<string, int> goodwill = new Dictionary<string, int>();
        readonly Dictionary<string, string> kinds = new Dictionary<string, string>();
        readonly Dictionary<string, List<string>> queued = new Dictionary<string, List<string>>();
        XElement world, together, lobby;   // lobby: feature:Lobby
        bool dirty;
        float nextSave;

        static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimFactions", "hub.xml");

        public Hub() { Load(); }

        static string Key(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        // ---- input ----

        public void LocalHello(Msg hello) => FromPlayer(Link.MyId, null, hello);

        public void FromPeer(Incoming inc)
        {
            if (inc.text == null)
            {
                if (peerToPlayer.TryGetValue(inc.peer, out var gone) && players.TryGetValue(gone, out var gp))
                {
                    gp.online = false; gp.peer = null;
                    peerToPlayer.Remove(inc.peer);
                    BroadcastRoster();
                }
                return;
            }
            var m = Msg.Parse(inc.text);
            if (m?.From == null) return;
            FromPlayer(m.From, inc.peer, m);
        }

        public void FromPlayer(string playerId, string peer, Msg m)
        {
            if (m.Type == "Hello") { OnHello(playerId, peer, m); return; }
            if (!players.ContainsKey(playerId)) return; // must say hello first

            switch (m.Type)
            {
                case "Goodwill":
                    ChangeGoodwill(m.Get("a"), m.Get("b"), m.GetInt("change"), m.Get("reason"));
                    return;
                case "SetRelation":   // the host sets any pair (feature:RelationCheat); a player sets their own (feature:FactionStance)
                    if (playerId != Link.MyId && m.Get("a") != playerId && m.Get("b") != playerId) return;
                    SetRelation(m.Get("a"), m.Get("b"), m.Get("kind"));
                    return;
                case "Lobby":
                    if (playerId != Link.MyId) return;   // only the host runs the lobby
                    OnLobby(m.x.Element("lobby"));
                    return;
                case "LobbyMember":
                    if (lobby == null) return;
                    lobby.Elements("m").Where(e => (string)e.Attribute("id") == playerId).Remove();
                    lobby.Add(new XElement("m", new XAttribute("id", playerId), new XAttribute("ready", (bool?)m.x.Element("m")?.Attribute("ready") ?? false)));
                    BroadcastRoster();
                    return;
                case "Together":
                    together = m.x.Element("session") is XElement s && (string)s.Attribute("open") == "true" ? new XElement(s) : null;
                    dirty = true;
                    BroadcastRoster();
                    return;
            }
            Route(m);
        }

        void OnHello(string playerId, string peer, Msg m)
        {
            if (!players.TryGetValue(playerId, out var p))
            {
                // the limit is on players here now: someone who played once and is offline doesn't keep a seat
                if (players.Values.Count(o => o.online) >= RFSettings.MaxPlayers) { if (peer != null) Link.SendRaw(peer, new Msg("Full", playerId).ToString()); return; }
                p = players[playerId] = new Player { id = playerId };
            }
            bool wasOnline = p.online;
            // keep the last known colony when this hello comes from the main menu
            var old = p.hello;
            p.hello = new XElement(m.x);
            // the friend's game restarted: drop the old Multiplayer connection it left behind (hook:DropGhosts)
            var oldBoot = (string)old?.Element("boot");
            if (oldBoot != null && oldBoot != (string)p.hello.Element("boot") && playerId != Link.MyId)
                RimFactions.Compat.HostConfigs.DropGhost((string)p.hello.Element("steamId"));
            if (p.hello.Element("colony") == null && old?.Element("colony") != null)
                foreach (var e in old.Elements().Where(e => e.Name != "player" && e.Name != "steamId" && e.Name != "version"))
                    p.hello.Add(new XElement(e));
            p.online = true;
            if (peer != null) { p.peer = peer; peerToPlayer[peer] = playerId; }
            if (playerId == Link.MyId && p.hello.Element("world") is XElement w) world = new XElement(w);
            dirty = true;

            var welcome = new Msg("Welcome", playerId);
            Deliver(p, welcome.ToString());
            if (!wasOnline && queued.TryGetValue(playerId, out var q))
            {
                foreach (var text in q) Deliver(p, text);
                queued.Remove(playerId);
            }
            BroadcastRoster();
        }

        void Route(Msg m)
        {
            // feature:PlanetEvent: during a planet-wide assault, anyone sending help to another colony earns a reward
            if (eventId != null && m.Type == "Trip" && m.Get("stance") == "help" && m.From != m.To) helpers.Add(m.From);
            var to = m.To;
            if (to == "*") { foreach (var p in players.Values) Deliver(p, m.ToString(), queueIfOffline: false); return; }
            if (to == null || !players.TryGetValue(to, out var target)) return;
            Deliver(target, m.ToString());
        }

        void Deliver(Player p, string text, bool queueIfOffline = true)
        {
            if (p.id == Link.MyId) { var m = Msg.Parse(text); if (m != null) Link.Deliver(m); return; }
            if (p.online && p.peer != null) { Link.SendRaw(p.peer, text); return; }
            if (!queueIfOffline) return;
            if (!queued.TryGetValue(p.id, out var q)) queued[p.id] = q = new List<string>();
            q.Add(text);
            dirty = true;
        }

        public bool IsOnline(string playerId) => players.TryGetValue(playerId, out var p) && p.online;

        // ---- goodwill (system:Goodwill) ----

        void ChangeGoodwill(string a, string b, int change, string reason)
        {
            if (a == null || b == null || a == b || change == 0) return;
            var k = Key(a, b);
            goodwill.TryGetValue(k, out var before);
            int now = Mathf.Clamp(before + change, -100, 100);
            goodwill[k] = now;
            kinds.TryGetValue(k, out var kind);
            kind = kind ?? "Neutral";
            var newKind = kind;
            if (kind != "Hostile" && now <= RFSettings.HostileAt) newKind = "Hostile";
            else if (kind != "Ally" && now >= RFSettings.AllyAt) newKind = "Ally";
            else if (kind == "Hostile" && now >= 0) newKind = "Neutral";
            else if (kind == "Ally" && now <= 0) newKind = "Neutral";
            kinds[k] = newKind;
            dirty = true;
            if (newKind != kind)
                Route(new Msg("News", "*", new XElement("news", "RelationChanged"),
                    new XElement("a", a), new XElement("b", b), new XElement("kind", newKind), new XElement("previous", kind)));
            BroadcastRoster();
        }

        /// feature:RelationCheat — the host sets two colonies' relation outright (goodwill jumps to match).
        void SetRelation(string a, string b, string kind)
        {
            if (a == null || b == null || a == b || !(kind == "Hostile" || kind == "Neutral" || kind == "Ally")) return;
            var k = Key(a, b);
            kinds.TryGetValue(k, out var before);
            before = before ?? "Neutral";
            goodwill[k] = kind == "Ally" ? RFSettings.AllyAt : kind == "Hostile" ? RFSettings.HostileAt : 0;
            kinds[k] = kind;
            dirty = true;
            if (before != kind)
                Route(new Msg("News", "*", new XElement("news", "RelationChanged"),
                    new XElement("a", a), new XElement("b", b), new XElement("kind", kind), new XElement("previous", before)));
            BroadcastRoster();
        }

        // ---- lobby (feature:Lobby) ----

        void OnLobby(XElement l)
        {
            if (l == null || (string)l.Attribute("open") != "true") { lobby = null; BroadcastRoster(); return; }
            bool wasStarted = (string)lobby?.Attribute("started") == "true";
            var members = lobby?.Elements("m").ToList() ?? new List<XElement>();
            lobby = new XElement(l);
            foreach (var mm in members) lobby.Add(mm);
            if (!wasStarted && (string)l.Attribute("started") == "true")
            {
                // everyone in the lobby starts out with the relation the host picked
                var ids = new List<string> { Link.MyId };
                ids.AddRange(members.Select(e => (string)e.Attribute("id")).Where(id => id != Link.MyId));
                var kind = (string)l.Attribute("relation") ?? "Neutral";
                int value = kind == "Ally" ? RFSettings.AllyAt : kind == "Hostile" ? RFSettings.HostileAt : 0;
                for (int i = 0; i < ids.Count; i++)
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        var k = Key(ids[i], ids[j]);
                        goodwill[k] = value;
                        kinds[k] = kind;
                    }
                dirty = true;
            }
            BroadcastRoster();
        }

        // ---- roster (system:Roster) ----

        public XElement RosterXml()
        {
            var x = new XElement("roster");
            foreach (var p in players.Values)
                x.Add(new XElement("p", new XAttribute("id", p.id), new XAttribute("online", p.online), p.hello.Elements().Where(e => e.Name != "world")));
            foreach (var kv in goodwill)
            {
                var ab = kv.Key.Split('|');
                kinds.TryGetValue(kv.Key, out var kind);
                x.Add(new XElement("gw", new XAttribute("a", ab[0]), new XAttribute("b", ab[1]), new XAttribute("v", kv.Value), new XAttribute("kind", kind ?? "Neutral")));
            }
            if (world != null) x.Add(new XElement(world));
            if (together != null) x.Add(new XElement(together));
            if (lobby != null) x.Add(new XElement(lobby));
            x.Add(new XElement("hub", Link.MyId));
            x.Add(RimFactions.Rules.Local?.ToXml()); // system:PlanetRules: the host's rules apply to everyone
            return x;
        }

        void BroadcastRoster()
        {
            var roster = RosterXml();
            foreach (var p in players.Values.Where(p => p.online))
                Deliver(p, new Msg("Roster", p.id, roster).ToString(), queueIfOffline: false);
        }

        // ---- persistence ----

        public void Tick()
        {
            if (dirty && Time.realtimeSinceStartup > nextSave) { nextSave = Time.realtimeSinceStartup + 5f; Save(); }
            PlanetEventTick();
        }

        // ---- planet events (feature:PlanetEvent) ----
        // Timed by the host's own game clock: only runs while the host is in a solo colony.

        string eventId;
        int eventEndsTick, nextEventTick = -1;
        readonly HashSet<string> helpers = new HashSet<string>();

        /// Test harness: start an assault now that ends after 2 in-game hours.
        public void ForceEvent()
        {
            nextEventTick = Find.TickManager.TicksGame;
            forcedShort = true;
        }
        bool forcedShort;

        void PlanetEventTick()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null || RimFactions.Live.InSession) return;
            if (RimFactions.Rules.Local?.planetAssaults == false) return;
            int now = Find.TickManager.TicksGame;
            if (nextEventTick < 0 || nextEventTick - now > RFSettings.PlanetEventEveryTicks * 2) nextEventTick = now + RFSettings.PlanetEventFirstTicks;

            if (eventId == null)
            {
                int colonies = players.Values.Count(p => p.online && p.hello?.Element("colony") != null);
                if (now < nextEventTick || colonies < RFSettings.PlanetEventMinPlayers) return;
                eventId = Guid.NewGuid().ToString("N").Substring(0, 8);
                eventEndsTick = now + (forcedShort ? 5000 : RFSettings.PlanetEventDurationTicks);
                forcedShort = false;
                helpers.Clear();
                var kind = Rand.Chance(0.5f) ? "mech" : "raid";
                Route(new Msg("PlanetEvent", "*", new XElement("eventId", eventId), new XElement("kind", kind)));
                return;
            }

            if (now < eventEndsTick) return;
            foreach (var h in helpers)
                if (players.ContainsKey(h))
                    Route(new Msg("PlanetReward", h, new XElement("eventId", eventId), new XElement("silver", RFSettings.PlanetEventRewardSilver)));
            Route(new Msg("News", "*", new XElement("news", "PlanetDefended"), new XElement("helpers", string.Join(",", helpers))));
            eventId = null;
            nextEventTick = now + RFSettings.PlanetEventEveryTicks;
        }

        void Save()
        {
            dirty = false;
            try
            {
                var x = new XElement("hub",
                    players.Values.Select(p => new XElement("player", new XAttribute("id", p.id), new XElement(p.hello))),
                    goodwill.Select(kv => new XElement("gw", new XAttribute("k", kv.Key), new XAttribute("v", kv.Value), new XAttribute("kind", kinds.TryGetValue(kv.Key, out var kd) ? kd : "Neutral"))),
                    queued.Select(kv => new XElement("queue", new XAttribute("id", kv.Key), kv.Value.Select(t => new XElement("q", t)))));
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                x.Save(StatePath);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] hub save failed: " + e.Message); }
        }

        void Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                var x = XElement.Load(StatePath);
                foreach (var p in x.Elements("player"))
                    players[(string)p.Attribute("id")] = new Player { id = (string)p.Attribute("id"), hello = p.Elements().FirstOrDefault() };
                foreach (var g in x.Elements("gw"))
                {
                    goodwill[(string)g.Attribute("k")] = (int)g.Attribute("v");
                    kinds[(string)g.Attribute("k")] = (string)g.Attribute("kind");
                }
                foreach (var q in x.Elements("queue"))
                    queued[(string)q.Attribute("id")] = q.Elements("q").Select(e => e.Value).ToList();
                // other games on the host's own Steam account (second copies, tests) aren't players to keep
                var mine = Net.SteamTransport.MyId.ToString();
                foreach (var id in players.Values.Where(pl => pl.id != Link.MyId && mine != "0" && (string)pl.hello?.Element("steamId") == mine).Select(pl => pl.id).ToList())
                    players.Remove(id);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] hub state unreadable, starting fresh: " + e.Message); }
        }
    }
}
