using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace RimFactions.Net
{
    /// A link message: <m t="type" from="playerId" to="playerId|*|hub">...</m>
    public class Msg
    {
        public XElement x;
        public string Type => (string)x.Attribute("t");
        public string From => (string)x.Attribute("from");
        public string To => (string)x.Attribute("to");

        public Msg(string type, string to = "hub", params object[] content)
        {
            x = new XElement("m", new XAttribute("t", type), new XAttribute("from", Link.MyId), new XAttribute("to", to), content);
        }

        Msg(XElement x) { this.x = x; }

        public static Msg Parse(string text)
        {
            try { return new Msg(XElement.Parse(text)); }
            catch (Exception e) { Log.Warning("[Rim Factions] bad link message: " + e.Message); return null; }
        }

        public string Get(string name) => (string)x.Element(name) ?? (string)x.Attribute(name);
        public int GetInt(string name, int def = 0) => int.TryParse(Get(name), out var v) ? v : def;
        public override string ToString() => x.ToString(SaveOptions.DisableFormatting);
    }

    public enum LinkRole { Off, Hub, Client }

    /// system:LinkClient — every game's connection to the planet. The hosting game also runs the hub
    /// (system:LinkHub) and talks to it directly.
    public class Link : MonoBehaviour
    {
        public static string MyId { get; private set; }
        public static string MyName { get; private set; }
        public static LinkRole Role { get; private set; }
        public static Hub Hub { get; private set; }
        public static string HubAddress { get; private set; }   // what joiners use
        public static string Status { get; private set; } = "RF_StatusOff";
        public static bool Connected => Role == LinkRole.Hub || (Role == LinkRole.Client && hubPeer != null && helloAcked);

        static readonly Dictionary<string, Action<Msg>> handlers = new Dictionary<string, Action<Msg>>();
        static readonly ConcurrentQueue<Incoming> inbox = new ConcurrentQueue<Incoming>();
        static SteamTransport steam;
        static TcpTransport tcp;
        static string hubPeer;
        static string joinAddress;
        static bool helloAcked, announcedJoin;
        static float nextHello, nextReconnect;
        static float steamRetryUntil;
        static Link instance;

        public static void On(string type, Action<Msg> handler) => handlers[type] = handler;

        public static void Init()
        {
            var dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimFactions");
            Directory.CreateDirectory(dir);
            var idFile = Path.Combine(dir, "player-id.txt");
            if (!File.Exists(idFile)) File.WriteAllText(idFile, Guid.NewGuid().ToString("N"));
            MyId = File.ReadAllText(idFile).Trim();
            MyName = SteamManager.Initialized ? Steamworks.SteamFriends.GetPersonaName() : Environment.UserName;

            var go = new GameObject("RimFactions.Link");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Link>();

            if (GenCommandLine.TryGetCommandLineArg("rimfactions-join", out var join) && !join.NullOrEmpty())
                StartClient(join);
            else if (GenCommandLine.CommandLineArgPassed("rimfactions-host"))
                StartHub();
        }

        // ---- hub ----

        public static void StartHub()
        {
            StopAll();
            Hub = new Hub();
            tcp = new TcpTransport();
            tcp.Listen();
            if (SteamTransport.Available)
            {
                steam = new SteamTransport();
                SteamTransport.AcceptAnyone = true;
            }
            Role = LinkRole.Hub;
            // Melty hands joiners a host name; a Steam relay id goes in as "<id>.steamlobby".
            HubAddress = SteamTransport.Available ? SteamTransport.MyId + ".steamlobby" : $"tcp:127.0.0.1:{tcp.Port}";
            Status = "RF_StatusHosting";
            if (SteamTransport.Available) HostAddressLog.Write(RFSettings.HostLogPrefix + HubAddress);
            else
            {
                // Steam can come up a little after the mod does: keep trying for a while (joiners wait on this line)
                HostAddressLog.Write("Steam isn't ready yet: waiting for it before friends can join");
                steamRetryUntil = Time.realtimeSinceStartup + 90f;
            }
            HostAddressLog.Write($"Local test address: tcp:127.0.0.1:{tcp.Port}");
            Hub.LocalHello(Hello.Make());
        }

        /// Hosting started before Steam was ready: open the planet over Steam as soon as it is, and log the address
        /// Melty hands to joiners. If Steam never comes, a LAN address (when LAN play is on) so friends nearby can join.
        static void RetrySteamHosting()
        {
            if (Time.frameCount % 60 != 0) return;
            if (SteamTransport.Available)
            {
                steamRetryUntil = 0f;
                steam = steam ?? new SteamTransport();
                SteamTransport.AcceptAnyone = true;
                HubAddress = SteamTransport.MyId + ".steamlobby";
                HostAddressLog.Write(RFSettings.HostLogPrefix + HubAddress);
                return;
            }
            if (Time.realtimeSinceStartup < steamRetryUntil) return;
            steamRetryUntil = 0f;
            var lan = LanAddress();
            if (TcpTransport.LanEnabled && lan != null)
            {
                HubAddress = $"tcp:{lan}:{tcp.Port}";
                HostAddressLog.Write(RFSettings.HostLogPrefix + HubAddress);
            }
            else HostAddressLog.Write("Steam isn't running: friends can't reach this planet (start Steam and play again)");
        }

        static string LanAddress()
        {
            try
            {
                return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString();
            }
            catch { return null; }
        }

        // ---- client ----

        public static void StartClient(string address)
        {
            StopAll();
            joinAddress = address.Trim();
            announcedJoin = false;
            Role = LinkRole.Client;
            HubAddress = joinAddress;
            TryConnect();
        }

        static void TryConnect()
        {
            helloAcked = false;
            hubPeer = null;
            Status = "RF_StatusConnecting";
            try
            {
                var a = joinAddress;
                if (a.StartsWith("tcp:"))
                {
                    var parts = a.Substring(4).Split(':');
                    tcp = tcp ?? new TcpTransport();
                    hubPeer = tcp.Connect(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : RFSettings.LinkTcpPort);
                }
                else
                {
                    if (!SteamTransport.Available) { Status = "RF_StatusNoSteam"; return; }
                    var id = a.StartsWith("steam:") ? a.Substring(6) : a;
                    if (id.EndsWith(".steamlobby")) id = id.Substring(0, id.Length - ".steamlobby".Length);
                    if (!ulong.TryParse(id, out var steamId)) { Status = "RF_StatusBadAddress"; return; }
                    steam = steam ?? new SteamTransport();
                    SteamTransport.Allowed.Add(steamId);
                    hubPeer = "steam:" + steamId;
                }
                SendHello();
            }
            catch (Exception e)
            {
                Log.Warning("[Rim Factions] could not reach the planet host: " + e.Message);
                hubPeer = null;
                Status = "RF_StatusUnreachable";
            }
        }

        public static void StopAll()
        {
            steam?.Stop(); tcp?.Stop();
            steam = null; tcp = null; hubPeer = null; Hub = null;
            Role = LinkRole.Off;
            Status = "RF_StatusOff";
        }

        // ---- sending ----

        /// Sends to the hub (which forwards to msg.To when it is a player or "*").
        public static void Send(Msg m)
        {
            if (Role == LinkRole.Hub) { Hub.FromPlayer(MyId, null, m); return; }
            if (Role != LinkRole.Client || hubPeer == null) { Log.Warning($"[Rim Factions] not connected; dropped {m.Type}"); return; }
            Transport(hubPeer)?.Send(hubPeer, m.ToString());
        }

        public static void SendHello()
        {
            nextHello = Time.realtimeSinceStartup + 30f;
            var hello = Hello.Make();
            if (Role == LinkRole.Hub) Hub.LocalHello(hello);
            else if (hubPeer != null) Transport(hubPeer)?.Send(hubPeer, hello.ToString());
        }

        internal static ITransport Transport(string peer) => peer.StartsWith("steam:") ? (ITransport)steam : tcp;

        /// Hub to a peer (used by Hub).
        internal static void SendRaw(string peer, string text)
        {
            if (peer == null) return;
            Transport(peer)?.Send(peer, text);
        }

        /// Messages addressed to this game.
        internal static void Deliver(Msg m)
        {
            if (m.Type != "Roster") TestHarness.Trace("recv " + (m.ToString().Length > 1500 ? m.ToString().Substring(0, 1500) + "..." : m.ToString()));
            if (m.Type == "Welcome")
            {
                if (Role == LinkRole.Client && !helloAcked && !announcedJoin)
                {
                    // feature:JoinWelcome: tell Melty this game got in, and greet the player once.
                    announcedJoin = true;
                    HostAddressLog.Write("Melty joined: " + joinAddress);
                    JoinWelcome.Pending = true;
                }
                helloAcked = true; Status = "RF_StatusConnected"; return;
            }
            if (m.Type == "Full") { Status = "RF_StatusFull"; StopAll(); return; }
            if (handlers.TryGetValue(m.Type, out var h))
            {
                try { h(m); }
                catch (Exception e) { Log.Error($"[Rim Factions] handling {m.Type}: {e}"); }
            }
        }

        void Update()
        {
            // Keep the link (and a hub other players rely on) running while the game is in the background.
            if (Role != LinkRole.Off && !Application.runInBackground) Application.runInBackground = true;
            steam?.Poll(inbox);
            tcp?.Poll(inbox);
            while (inbox.TryDequeue(out var inc))
            {
                if (Role == LinkRole.Hub) { Hub.FromPeer(inc); continue; }
                if (inc.peer != hubPeer) continue;
                if (inc.text == null) { hubPeer = null; helloAcked = false; Status = "RF_StatusUnreachable"; nextReconnect = Time.realtimeSinceStartup + 5f; continue; }
                var m = Msg.Parse(inc.text);
                if (m != null) Deliver(m);
            }

            if (Role == LinkRole.Client)
            {
                if (hubPeer == null && Time.realtimeSinceStartup > nextReconnect) { nextReconnect = Time.realtimeSinceStartup + 10f; TryConnect(); }
                else if (Time.realtimeSinceStartup > nextHello) SendHello();
            }
            else if (Role == LinkRole.Hub)
            {
                if (steamRetryUntil > 0f) RetrySteamHosting();
                if (Time.realtimeSinceStartup > nextHello) SendHello();
                Hub.Tick();
            }
        }
    }

    /// The Hello every game sends: who it is and where its colony stands on the planet.
    public static class Hello
    {
        static readonly string Boot = Guid.NewGuid().ToString("N").Substring(0, 8);

        public static Msg Make()
        {
            var m = new Msg("Hello", "hub",
                new XElement("player", Link.MyName),
                new XElement("steamId", SteamTransport.MyId),
                new XElement("version", RimFactionsMod.Version),   // system:VersionCheck
                new XElement("boot", Boot),   // a new value each time the game starts (hook:DropGhosts)
                Dlc.HelloElement());
            if (Lobby.MyColor is Color mine) m.x.Add(new XElement("color", ColorUtility.ToHtmlStringRGB(mine)));
            if (Current.ProgramState == ProgramState.Playing && Find.World != null && !Live.InSession)
            {
                var home = Find.Maps.FirstOrDefault(mp => mp.IsPlayerHome);
                var fac = RimWorld.Faction.OfPlayer;
                m.x.Add(new XElement("colony", home?.Parent?.LabelCap.ToString() ?? fac.Name),
                    new XElement("faction", fac.Name),
                    Lobby.MyColor == null ? new XElement("color", ColorUtility.ToHtmlStringRGB(fac.Color)) : null,
                    new XElement("tile", home != null ? (int)home.Tile : -1),
                    new XElement("layer", home?.Tile.Layer?.Def?.defName ?? ""),
                    new XElement("colonists", RimWorld.PawnsFinder.AllMaps_FreeColonists.Count()),
                    new XElement("wealth", (int)Find.Maps.Where(mp => mp.IsPlayerHome).Sum(mp => mp.wealthWatcher.WealthTotal)),
                    WorldParams.Capture());
            }
            return m;
        }
    }
}
