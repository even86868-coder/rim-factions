using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Steamworks;
using Verse;
using Verse.Steam;

namespace RimFactions.Net
{
    /// One raw message from a peer. Peer ids are "steam:<id64>" or "tcp:<n>".
    public struct Incoming
    {
        public string peer;
        public string text;   // null = peer disconnected
    }

    public interface ITransport
    {
        void Send(string peer, string text);
        void Poll(ConcurrentQueue<Incoming> into);
        void Close(string peer);
        void Stop();
    }

    /// system:SteamTransport — Steam P2P on setting:LinkSteamChannel. Steam relays the traffic, so nobody
    /// forwards ports.
    public class SteamTransport : ITransport
    {
        static Callback<P2PSessionRequest_t> sessionReq;
        public static readonly HashSet<ulong> Allowed = new HashSet<ulong>();
        public static bool AcceptAnyone;
        readonly int channel = RFSettings.LinkSteamChannel;
        byte[] buf = new byte[64 * 1024];

        public static bool Available => SteamManager.Initialized;
        public static ulong MyId => SteamManager.Initialized ? SteamUser.GetSteamID().m_SteamID : 0;

        public SteamTransport()
        {
            if (sessionReq == null)
                sessionReq = Callback<P2PSessionRequest_t>.Create(req =>
                {
                    var id = req.m_steamIDRemote.m_SteamID;
                    if (AcceptAnyone || Allowed.Contains(id))
                        SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
                });
        }

        public void Send(string peer, string text)
        {
            if (!peer.StartsWith("steam:") || !ulong.TryParse(peer.Substring(6), out var id)) return;
            Allowed.Add(id);
            var data = Encoding.UTF8.GetBytes(text);
            if (!SteamNetworking.SendP2PPacket(new CSteamID(id), data, (uint)data.Length, EP2PSend.k_EP2PSendReliable, channel))
                Log.Warning("[Rim Factions] Steam send failed to " + peer);
        }

        public void Poll(ConcurrentQueue<Incoming> into)
        {
            while (SteamNetworking.IsP2PPacketAvailable(out uint size, channel))
            {
                if (buf.Length < size) buf = new byte[size];
                if (!SteamNetworking.ReadP2PPacket(buf, size, out uint read, out CSteamID remote, channel)) continue;
                into.Enqueue(new Incoming { peer = "steam:" + remote.m_SteamID, text = Encoding.UTF8.GetString(buf, 0, (int)read) });
            }
        }

        public void Close(string peer)
        {
            if (peer.StartsWith("steam:") && ulong.TryParse(peer.Substring(6), out var id))
                SteamNetworking.CloseP2PChannelWithUser(new CSteamID(id), channel);
        }

        public void Stop() { }
    }

    /// system:TcpTransport — length-prefixed UTF-8 over TCP. Loopback only unless RIMFACTIONS_LAN=1.
    public class TcpTransport : ITransport
    {
        class Conn
        {
            public TcpClient client;
            public NetworkStream stream;
            public readonly object writeLock = new object();
        }

        readonly ConcurrentDictionary<string, Conn> conns = new ConcurrentDictionary<string, Conn>();
        readonly ConcurrentQueue<Incoming> inbox = new ConcurrentQueue<Incoming>();
        TcpListener listener;
        int nextId;
        volatile bool stopped;

        public int Port { get; private set; }

        public static bool LanEnabled => Environment.GetEnvironmentVariable("RIMFACTIONS_LAN") == "1";

        /// Hub side: listen on setting:LinkTcpPort (or the next free one).
        public void Listen()
        {
            var ip = LanEnabled ? IPAddress.Any : IPAddress.Loopback;
            for (int port = RFSettings.LinkTcpPort; port < RFSettings.LinkTcpPort + 20; port++)
            {
                try
                {
                    listener = new TcpListener(ip, port);
                    listener.Start();
                    Port = port;
                    break;
                }
                catch (SocketException) { listener = null; }
            }
            if (listener == null) throw new Exception("no free TCP port for the Rim Factions link");
            new Thread(AcceptLoop) { IsBackground = true, Name = "RimFactions TCP accept" }.Start();
        }

        /// Client side: connect to host:port, returns the peer id.
        public string Connect(string host, int port)
        {
            var c = new TcpClient();
            c.Connect(host, port);
            return Add(c);
        }

        void AcceptLoop()
        {
            while (!stopped)
            {
                try { Add(listener.AcceptTcpClient()); }
                catch { if (stopped) return; }
            }
        }

        string Add(TcpClient c)
        {
            c.NoDelay = true;
            var id = "tcp:" + Interlocked.Increment(ref nextId);
            var conn = new Conn { client = c, stream = c.GetStream() };
            conns[id] = conn;
            new Thread(() => ReadLoop(id, conn)) { IsBackground = true, Name = "RimFactions TCP " + id }.Start();
            return id;
        }

        void ReadLoop(string id, Conn conn)
        {
            try
            {
                var len = new byte[4];
                while (!stopped)
                {
                    ReadExact(conn.stream, len, 4);
                    int n = BitConverter.ToInt32(len, 0);
                    if (n < 0 || n > 64 * 1024 * 1024) throw new IOException("bad frame");
                    var data = new byte[n];
                    ReadExact(conn.stream, data, n);
                    inbox.Enqueue(new Incoming { peer = id, text = Encoding.UTF8.GetString(data) });
                }
            }
            catch { }
            conns.TryRemove(id, out _);
            try { conn.client.Close(); } catch { }
            inbox.Enqueue(new Incoming { peer = id, text = null });
        }

        static void ReadExact(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) throw new IOException("closed");
                got += r;
            }
        }

        public void Send(string peer, string text)
        {
            if (!conns.TryGetValue(peer, out var conn)) return;
            var data = Encoding.UTF8.GetBytes(text);
            var frame = new byte[4 + data.Length];
            BitConverter.GetBytes(data.Length).CopyTo(frame, 0);
            data.CopyTo(frame, 4);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { lock (conn.writeLock) conn.stream.Write(frame, 0, frame.Length); }
                catch { try { conn.client.Close(); } catch { } }
            });
        }

        public void Poll(ConcurrentQueue<Incoming> into)
        {
            while (inbox.TryDequeue(out var m)) into.Enqueue(m);
        }

        public void Close(string peer)
        {
            if (conns.TryRemove(peer, out var c)) try { c.client.Close(); } catch { }
        }

        public void Stop()
        {
            stopped = true;
            try { listener?.Stop(); } catch { }
            foreach (var c in conns.Values) try { c.client.Close(); } catch { }
            conns.Clear();
        }
    }
}
