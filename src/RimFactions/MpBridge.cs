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
            var host = AccessTools.Method(T("Multiplayer.Client.HostWindow"), "HostProgrammatically");
            return (bool)host.Invoke(null, new[] { Settings(gameName) });
        }

        /// feature:LoadSaveTogether: load one of Multiplayer's own saves and host it (from the main menu).
        public static bool HostReplay(string path, string gameName)
        {
            try
            {
                var settings = Settings(gameName);
                var hostServer = AccessTools.Method(T("Multiplayer.Client.HostUtil"), "HostServer");
                Action loaded = () => hostServer.Invoke(null, new[] { settings, (object)true });
                Action cancel = GenScene.GoToMainMenu;
                var load = AccessTools.Method(T("Multiplayer.Client.Replay"), "LoadReplay");
                load.Invoke(null, new object[] { new System.IO.FileInfo(path), true, loaded, cancel, "MpLoading", false });
                return true;
            }
            catch (Exception e) { Log.Warning("[Rim Factions] couldn't load the Multiplayer save: " + e); return false; }
        }

        static object Settings(string gameName)
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
            DevModeSynced(t);
            return s;
        }

        /// Multiplayer only sends dev-mode actions (spawning things, editing pawns...) to everyone when the session is
        /// hosted with its debug mode on; without it the host's dev tools changed only the host's game and desynced.
        /// Dev mode stays the host's alone.
        public static void DevModeSynced(Traverse settings)
        {
            try
            {
                settings.Field("debugMode").SetValue(true);
                var scope = settings.Field("devModeScope");
                var type = scope.GetValueType();
                if (type != null && type.IsEnum && Enum.IsDefined(type, "HostOnly")) scope.SetValue(Enum.Parse(type, "HostOnly"));
            }
            catch (Exception e) { Log.Warning("[Rim Factions] couldn't turn on Multiplayer's dev mode sync: " + e.Message); }
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
