using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;
using Verse.Steam;

namespace RimFactions
{
    /// Calls into the Multiplayer mod by name, so Rim Factions survives its internal changes and loads even
    /// when a player's Multiplayer is a different build.
    public static class MpBridge
    {
        static Type T(string name) => AccessTools.TypeByName(name);

        public static bool InSession => MP.enabled && MP.IsInMultiplayer;

        static object LocalServer => AccessTools.Property(T("Multiplayer.Client.Multiplayer"), "LocalServer")?.GetValue(null);
        public static bool Hosting => LocalServer is object s && Traverse.Create(s).Field("running").GetValue<bool>();

        /// hook:HostWindowDefaults counterpart for code: opens the current solo game as a live session.
        public static bool Host(string gameName)
        {
            var settingsType = T("Multiplayer.Common.ServerSettings");
            var s = Activator.CreateInstance(settingsType);
            var t = Traverse.Create(s);
            t.Field("gameName").SetValue(gameName);
            t.Field("maxPlayers").SetValue(RFSettings.MaxPlayers);
            t.Field("steam").SetValue(SteamManager.Initialized);
            t.Field("lan").SetValue(true);
            t.Field("lanAddress").SetValue(LanIp());
            t.Field("direct").SetValue(false);
            t.Field("multifaction").SetValue(false);
            t.Field("asyncTime").SetValue(false);
            t.Field("pauseOnJoin").SetValue(true);
            var host = AccessTools.Method(T("Multiplayer.Client.HostWindow"), "HostProgrammatically");
            return (bool)host.Invoke(null, new[] { s });
        }

        static string LanIp()
        {
            var ep = T("Multiplayer.Common.Util.Endpoints") ?? T("Multiplayer.Common.Endpoints");
            var m = ep == null ? null : AccessTools.Method(ep, "GetLocalIpAddress");
            return (m?.Invoke(null, null) as string) ?? "127.0.0.1";
        }

        /// The connect strings joiners can use: steam:<id> and lnl:<lan ip>:<port>.
        public static (string steam, string lan) Addresses()
        {
            string steam = null, lan = null;
            var server = LocalServer;
            if (server == null) return (null, null);
            var tr = Traverse.Create(server);
            if (tr.Field("settings").Field("steam").GetValue<bool>() && SteamManager.Initialized)
                steam = "steam:" + Steamworks.SteamUser.GetSteamID().m_SteamID;
            if (tr.Field("netManagers").GetValue() is IEnumerable managers)
                foreach (var man in managers)
                {
                    if (man?.GetType().Name != "LiteNetLanManager") continue;
                    var mt = Traverse.Create(man);
                    var ip = mt.Field("lanAddress").GetValue()?.ToString();
                    var net = mt.Field("lanManager").GetValue();
                    if (net == null || ip == null) continue;
                    var nt = Traverse.Create(net);
                    int port = nt.Property("LocalPort").PropertyExists() ? nt.Property("LocalPort").GetValue<int>() : nt.Field("LocalPort").GetValue<int>();
                    if (port > 0) lan = $"lnl:{ip}:{port}";
                }
            return (steam, lan);
        }

        /// Joins a live session from the main menu.
        public static bool Connect(string connString)
        {
            var reg = T("Multiplayer.Client.Util.ConnectorRegistry");
            var args = new object[] { connString, null };
            if (!(bool)AccessTools.Method(reg, "TryParse").Invoke(null, args)) return false;
            AccessTools.Method(T("Multiplayer.Client.ClientUtil"), "TryConnectWithWindow").Invoke(null, new[] { args[1], false });
            return true;
        }

        public static void ConvertToSingleplayer() =>
            AccessTools.Method(T("Multiplayer.Client.Saving.ConvertToSp"), "DoConvert").Invoke(null, null);

        public static void Stop() =>
            AccessTools.Method(T("Multiplayer.Client.Multiplayer"), "StopMultiplayerAndClearAllWindows").Invoke(null, null);

        public static string MyUsername => MP.enabled && MP.IsInMultiplayer ? MP.PlayerName : null;

        public static bool PlayerConnected(string username) =>
            MP.GetPlayers().Any(p => p.Username == username);
    }
}
