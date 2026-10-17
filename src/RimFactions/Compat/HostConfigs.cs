using System;
using System.Linq;
using HarmonyLib;
using Verse;

namespace RimFactions.Compat
{
    /// system:AutoHostConfigs — joining a live game whose mod settings differ from yours used to offer "Connect
    /// anyway", which desyncs within seconds (different settings, different game). When the mod list and files
    /// already match and only settings differ, Rim Factions takes Multiplayer's own "Fix and restart" path with the
    /// host's settings: the game restarts once and reconnects by itself. The host's settings last until your next
    /// normal start; none of your files are changed.
    public static class HostConfigs
    {
        public static void Init()
        {
            var window = AccessTools.TypeByName("Multiplayer.Client.JoinDataWindow");
            var postOpen = window == null ? null : AccessTools.Method(window, "PostOpen");
            if (postOpen == null) { Log.Warning("[Rim Factions] Multiplayer join window not found"); return; }
            new Harmony("melty.rimfactions.hostconfigs").Patch(postOpen, postfix: new HarmonyMethod(typeof(HostConfigs), nameof(WindowOpened)));

            // hook:AutoAcceptFriends — Multiplayer asks the host to accept every Steam joiner in a small popup; a friend
            // linked to this planet is accepted at once, so joining never hangs waiting on a popup nobody saw.
            var pending = AccessTools.TypeByName("Multiplayer.Client.PendingPlayerWindow");
            var request = pending == null ? null : AccessTools.Inner(pending, "Request");
            var enqueue = request == null ? null : AccessTools.Method(pending, "EnqueueJoinRequest",
                new[] { typeof(Steamworks.CSteamID), typeof(Action<,>).MakeGenericType(request, typeof(bool)) });
            if (enqueue != null)
                new Harmony("melty.rimfactions.autoaccept").Patch(enqueue, prefix: new HarmonyMethod(typeof(HostConfigs), nameof(AutoAccept)));
            else Log.Warning("[Rim Factions] Multiplayer join requests not found; friends must be accepted by hand");

            // Rim Factions' own settings are personal (your color, your cloud folder) or reach everyone through the
            // planet's rules anyway, so Multiplayer must neither compare them nor hand the host's to a joiner.
            var ignored = AccessTools.Field(AccessTools.TypeByName("Multiplayer.Client.SyncConfigs"), "ignoredConfigsModIds");
            if (ignored?.GetValue(null) is string[] ids && !ids.Contains("melty.rimfactions"))
                ignored.SetValue(null, ids.Append("melty.rimfactions").ToArray());
        }

        /// hook:DropGhosts — a friend whose game restarted (e.g. to take the host's settings) leaves a "ghost" of
        /// themselves in the host's Multiplayer player list: Steam never reports the old connection gone, and
        /// Multiplayer then ignores the friend's new join, so they wait forever. Called by the hub when a friend's
        /// game says hello from a new start: their old Steam player is disconnected so the new join goes through.
        public static void DropGhost(string steamId)
        {
            if (!ulong.TryParse(steamId, out var id) || id == 0) return;
            var server = AccessTools.Property(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "LocalServer")?.GetValue(null);
            if (server == null) return;
            var enqueue = AccessTools.Method(server.GetType(), "Enqueue");
            enqueue?.Invoke(server, new object[] { (Action)(() =>
            {
                try
                {
                    var pm = Traverse.Create(server).Field("playerManager").GetValue();
                    var players = ((System.Collections.IEnumerable)Traverse.Create(pm).Property("Players").GetValue()).Cast<object>().ToList();
                    foreach (var p in players.Where(p => Traverse.Create(p).Field("steamId").GetValue<ulong>() == id))
                    {
                        var conn = Traverse.Create(p).Field("conn").GetValue();
                        var reason = Enum.Parse(AccessTools.TypeByName("Multiplayer.Common.MpDisconnectReason"), "ConnectingFailed");
                        AccessTools.Method(pm.GetType(), "SetDisconnected").Invoke(pm, new[] { conn, reason });
                        Log.Message($"[Rim Factions] cleared {id}'s old connection so they can rejoin");
                    }
                }
                catch (Exception e) { Log.Warning("[Rim Factions] clearing an old connection failed: " + e.Message); }
            }) });
        }

        static bool AutoAccept(Steamworks.CSteamID steamId)
        {
            var id = steamId.m_SteamID.ToString();
            var friend = Roster.Players.FirstOrDefault(f => f.steamId == id);
            if (friend == null) return true;   // not on this planet: Multiplayer asks as usual
            AccessTools.Method(AccessTools.TypeByName("Multiplayer.Client.SteamIntegration"), "AcceptPlayerJoinRequest")?.Invoke(null, new object[] { steamId });
            Log.Message($"[Rim Factions] accepted {friend.player} joining over Steam");
            return false;
        }

        static void WindowOpened(Window __instance)
        {
            try
            {
                var diff = Traverse.Create(__instance).Method("DiffString").GetValue<string>();
                bool onlyConfigs = diff.Contains("Config sync enabled: True") && diff.Contains("Configs match: False")
                    && diff.Contains("Mod list diff: None") && diff.Contains("Files match: True");
                // Already restarted with the host's settings once: don't loop, leave the choice to the player.
                bool alreadyApplied = Traverse.Create(AccessTools.TypeByName("Multiplayer.Client.SyncConfigs")).Property("Applicable").GetValue<bool>();
                if (!onlyConfigs || alreadyApplied) return;

                var remote = Traverse.Create(__instance).Field("remote").GetValue();
                var connector = Traverse.Create(__instance).Field("connector").GetValue();
                var fix = (Window)Activator.CreateInstance(AccessTools.TypeByName("Multiplayer.Client.FixAndRestartWindow"), remote, connector);
                Traverse.Create(fix).Field("applyModList").SetValue(false);
                Traverse.Create(fix).Field("applyConfigs").SetValue(true);
                Log.Message("[Rim Factions] mod settings differ from the host's: restarting with theirs and reconnecting");
                HostAddressLog.Write("Applying host's mod settings and reconnecting");
                LongEventHandler.QueueLongEvent(() => Traverse.Create(fix).Method("DoRestart").GetValue(), "RF_ApplyingHostConfigs", false, null);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] couldn't apply the host's mod settings: " + e.Message); }
        }
    }
}
