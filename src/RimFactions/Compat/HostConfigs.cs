using System;
using System.Linq;
using HarmonyLib;
using Verse;

namespace RimFactions.Compat
{
    /// system:AutoHostConfigs — joining a live game whose mod settings differ from yours used to offer "Connect
    /// anyway", which desyncs within seconds (different settings, different game). When the mod list and files
    /// already match and only settings differ, Rim Factions takes Multiplayer's own "Fix and restart" path with the
    /// host's settings: the game restarts once and reconnects by itself. Once in, the host's settings become yours
    /// (feature:KeepHostConfigs, old files backed up), so later joins don't restart again.
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
            var pending = AccessTools.TypeByName("Multiplayer.Client.Windows.PendingPlayerWindow");
            var request = pending == null ? null : AccessTools.Inner(pending, "Request");
            var enqueue = request == null ? null : AccessTools.Method(pending, "EnqueueJoinRequest",
                new[] { typeof(Steamworks.CSteamID), typeof(Action<,>).MakeGenericType(request, typeof(bool)) });
            if (enqueue != null)
                new Harmony("melty.rimfactions.autoaccept").Patch(enqueue, prefix: new HarmonyMethod(typeof(HostConfigs), nameof(AutoAccept)));
            else Log.Warning("[Rim Factions] Multiplayer join requests not found; friends must be accepted by hand");

            // hook:DropGhosts on the session host itself: a Steam join from someone already in the player list (a
            // restarted game) replaces their stale entry instead of being ignored.
            try
            {
                var h = new Harmony("melty.rimfactions.ghosts");
                var integ = AccessTools.TypeByName("Multiplayer.Client.Networking.SteamP2PIntegration");
                var packet = integ == null ? null : AccessTools.Inner(integ, "SteamPacket");
                var read = integ == null ? null : AccessTools.Method(integ, "ReadPackets");
                var connected = AccessTools.Method(AccessTools.TypeByName("Multiplayer.Common.PlayerManager"), "OnConnected");
                if (packet != null && read != null && connected != null)
                {
                    h.Patch(read, postfix: new HarmonyMethod(AccessTools.Method(typeof(GhostFilter<>).MakeGenericType(packet), "Postfix")));
                    h.Patch(connected, postfix: new HarmonyMethod(typeof(HostConfigs), nameof(Connected)));
                }
                else Log.Warning("[Rim Factions] Multiplayer's Steam join handling not found; restarted friends may need the host to re-host");
            }
            catch (Exception e) { Log.Warning("[Rim Factions] ghost clearing unavailable: " + e.Message); }

            // Rim Factions' own settings are personal (your color, your cloud folder) or reach everyone through the
            // planet's rules anyway, so Multiplayer must neither compare them nor hand the host's to a joiner.
            // Settings that only change how your own screen looks or sounds don't need to match either: a difference
            // there used to cost a full restart on joining.
            var ignored = AccessTools.Field(AccessTools.TypeByName("Multiplayer.Client.Util.SyncConfigs"), "ignoredConfigsModIds");
            if (ignored?.GetValue(null) is string[] ids)
                ignored.SetValue(null, ids.Concat(LooksOnly).Distinct().ToArray());
        }

        static readonly string[] LooksOnly =
        {
            "melty.rimfactions", "mlie.showmeyourhands", "caesarv6.damageindicators", "neronix17.ui.faunapedia", "leo.cleancommandbar",
            "automatic.bionicicons", "peppsen.pmusic", "ih.clean.textures", "creeper.betterinfocard", "andromeda.nicehealthtab",
            "arandomkiwi.rimthemes", "brrainz.cameraplus", "jaxe.rimhud", "telardo.dragselect", "memegoddess.showmeyourhands",
            "m00nl1ght.mappreview", "ilyvion.usethisinstead", "dubwise.dubsmintmenus", "dubwise.dubsmintminimap", "fluffy.modmanager",
        };

        // ---- feature:KeepHostConfigs ----

        static bool adopted;

        /// Joined with the host's settings (after Multiplayer's settings restart): make them my own settings too, so the
        /// next join already matches and the game doesn't have to restart again. My old files are backed up first.
        public static void AdoptIfApplied()
        {
            if (adopted || !Live.InSession || MpBridge.Hosting) return;
            adopted = true;
            try
            {
                var sync = AccessTools.TypeByName("Multiplayer.Client.Util.SyncConfigs");
                if (!Traverse.Create(sync).Property("Applicable").GetValue<bool>()) return;
                var temp = AccessTools.Field(sync, "TempConfigsPath")?.GetValue(null) as string;
                if (temp == null || !System.IO.Directory.Exists(temp)) return;
                var config = GenFilePaths.ConfigFolderPath;
                var backup = System.IO.Path.Combine(config, "RimFactions-backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                int changed = 0;
                void Backup(string file)
                {
                    var to = System.IO.Path.Combine(backup, file.Substring(config.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, '/'));
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                    System.IO.File.Copy(file, to, true);
                }

                // the host's files become mine
                var hostFiles = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in System.IO.Directory.GetFiles(temp, "*", System.IO.SearchOption.AllDirectories))
                {
                    var rel = src.Substring(temp.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, '/');
                    hostFiles.Add(rel);
                    var dst = System.IO.Path.Combine(config, rel);
                    if (System.IO.File.Exists(dst) && System.IO.File.ReadAllText(dst) == System.IO.File.ReadAllText(src)) continue;
                    if (System.IO.File.Exists(dst)) Backup(dst);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                    System.IO.File.Copy(src, dst, true);
                    changed++;
                }

                // settings files the host doesn't have (the host uses that mod's defaults) step aside, for the mods
                // Multiplayer compares
                var skip = new System.Collections.Generic.HashSet<string>(((string[])AccessTools.Field(sync, "ignoredConfigsModIds").GetValue(null)), StringComparer.OrdinalIgnoreCase);
                var compared = LoadedModManager.RunningModsListForReading.Where(m => !skip.Contains(m.PackageIdPlayerFacing)).Select(m => m.FolderName).ToList();
                foreach (var file in System.IO.Directory.GetFiles(config, "Mod_*.xml"))
                {
                    var name = System.IO.Path.GetFileName(file);
                    if (hostFiles.Contains(name) || !compared.Any(f => name.StartsWith("Mod_" + f + "_", StringComparison.OrdinalIgnoreCase))) continue;
                    Backup(file);
                    System.IO.File.Delete(file);
                    changed++;
                }
                if (changed == 0) return;
                Log.Message($"[Rim Factions] kept the host's mod settings as mine ({changed} files; old ones in {backup})");
                Messages.Message("RF_KeptHostConfigs".Translate(), RimWorld.MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] couldn't keep the host's mod settings: " + e.Message); }
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

        static readonly System.Collections.Generic.Dictionary<object, DateTime> born = new System.Collections.Generic.Dictionary<object, DateTime>();

        static void Connected(object conn) { lock (born) born[conn] = DateTime.UtcNow; }

        /// A join packet from a Steam player who's already listed: if that entry is more than a few seconds old it's
        /// a ghost of a game that restarted, so it goes and the new join is let through.
        public static void JoinFrom(Steamworks.CSteamID remote)
        {
            try
            {
                var server = AccessTools.Property(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "LocalServer")?.GetValue(null);
                if (server == null) return;
                var pm = Traverse.Create(server).Field("playerManager").GetValue();
                var players = ((System.Collections.IEnumerable)Traverse.Create(pm).Property("Players").GetValue()).Cast<object>().ToList();
                foreach (var p in players)
                {
                    var conn = Traverse.Create(p).Field("conn").GetValue();
                    var id = Traverse.Create(conn).Field("remoteId").GetValue();
                    if (!(id is Steamworks.CSteamID cid) || cid != remote) continue;
                    DateTime when;
                    lock (born) if (!born.TryGetValue(conn, out when)) when = DateTime.MinValue;
                    if ((DateTime.UtcNow - when).TotalSeconds < 10) continue;   // still joining right now: a repeated join packet
                    var reason = Enum.Parse(AccessTools.TypeByName("Multiplayer.Common.MpDisconnectReason"), "ConnectingFailed");
                    AccessTools.Method(pm.GetType(), "SetDisconnected").Invoke(pm, new[] { conn, reason });
                    Log.Message($"[Rim Factions] {remote} rejoined: cleared their old connection");
                }
            }
            catch (Exception e) { Log.Warning("[Rim Factions] clearing a rejoining player failed: " + e.Message); }
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
                if (diff.Contains("Files match: False"))
                    Find.WindowStack.Add(new Dialog_MessageBox("RF_FilesDiffer".Translate(VersionCheck.Mismatch ?? RimFactionsMod.Version, RimFactionsMod.Version)));
                bool onlyConfigs = diff.Contains("Config sync enabled: True") && diff.Contains("Configs match: False")
                    && diff.Contains("Mod list diff: None") && diff.Contains("Files match: True");
                // Already restarted with the host's settings once: don't loop, leave the choice to the player.
                bool alreadyApplied = Traverse.Create(AccessTools.TypeByName("Multiplayer.Client.Util.SyncConfigs")).Property("Applicable").GetValue<bool>();
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

namespace RimFactions.Compat
{
    /// Looks at each Steam packet Multiplayer reads; join packets go past HostConfigs.JoinFrom first (hook:DropGhosts).
    static class GhostFilter<T>
    {
        static readonly System.Reflection.FieldInfo join = AccessTools.Field(typeof(T), "joinPacket");
        static readonly System.Reflection.FieldInfo remote = AccessTools.Field(typeof(T), "remote");

        public static void Postfix(ref System.Collections.Generic.IEnumerable<T> __result) => __result = Filter(__result);

        static System.Collections.Generic.IEnumerable<T> Filter(System.Collections.Generic.IEnumerable<T> packets)
        {
            foreach (var p in packets)
            {
                if ((bool)join.GetValue(p)) HostConfigs.JoinFrom((Steamworks.CSteamID)remote.GetValue(p));
                yield return p;
            }
        }
    }
}
