using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Steamworks;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace RimFactions
{
    /// feature:ModSync — one button to play with a friend's exact mod list: missing Workshop mods are
    /// subscribed and downloaded, the active list is set to theirs in their load order, and RimWorld restarts.
    /// Your own list is saved first; "Restore my own mod list" brings it back.
    public static class ModSync
    {
        public class Plan
        {
            public List<string> order = new List<string>();      // their active list, as ids
            public List<ModInfo> download = new List<ModInfo>();  // Workshop mods you don't have yet
            public List<string> unavailable = new List<string>(); // can't be downloaded (local mods, DLCs you don't own)
            public bool same;
        }

        static string BackupPath => Path.Combine(GenFilePaths.ConfigFolderPath, "RimFactions-my-mods.txt");
        public static bool HasBackup => File.Exists(BackupPath);

        static ModMetaData Installed(string id) => ModLister.GetModWithIdentifier(id, ignorePostfix: true);

        /// null until the friend's list has arrived.
        public static Plan Make(Friend friend)
        {
            var order = ModCompat.OrderOf(friend.id);
            var theirs = ModCompat.Of(friend.id);
            if (order == null || theirs == null) return null;
            var plan = new Plan();
            foreach (var id in order)
            {
                if (Installed(id) != null) { plan.order.Add(id); continue; }
                var info = theirs.FirstOrDefault(m => m.id == id);
                if (info != null && info.workshop != 0) { plan.download.Add(info); plan.order.Add(id); }
                else plan.unavailable.Add(info?.name ?? id);
            }
            plan.same = plan.download.Count == 0 &&
                plan.order.SequenceEqual(ModsConfig.ActiveModsInLoadOrder.Select(m => m.PackageIdPlayerFacing.ToLowerInvariant()));
            return plan;
        }

        public static void Start(Friend friend)
        {
            ModCompat.Request(friend.id);
            Find.WindowStack.Add(new ModSyncWindow(friend));
        }

        /// Switch to these mods and restart. Your current list is saved once, before the first switch.
        public static void Apply(List<string> ids)
        {
            if (!HasBackup) File.WriteAllLines(BackupPath, ModsConfig.ActiveModsInLoadOrder.Select(m => m.PackageId));
            SetAndRestart(ids);
        }

        public static void Restore()
        {
            var ids = File.ReadAllLines(BackupPath).ToList();
            File.Delete(BackupPath);
            SetAndRestart(ids);
        }

        static void SetAndRestart(List<string> ids)
        {
            var list = ids.Select(id => Installed(id)?.PackageId).Where(id => id != null).Distinct().ToList();
            ModsConfig.SetActiveToList(list);
            ModsConfig.Save();
            ModsConfig.RestartFromChangedMods();
        }

        static bool menuAsked, menuRequested;

        /// feature:ModSyncAtStart — at the main menu, once per launch: if my mods don't match the host's, offer to
        /// switch now. Then the one restart happens at the start, instead of Multiplayer restarting the game on every join.
        public static void MenuCheck()
        {
            if (menuAsked || Current.ProgramState != ProgramState.Entry || LongEventHandler.AnyEventNowOrWaiting || Find.WindowStack == null) return;
            if (!Net.Link.Connected || Net.Link.Role != Net.LinkRole.Client || !(Roster.Get(Roster.HubId) is Friend host)) return;
            if (!menuRequested) { menuRequested = true; ModCompat.Request(host.id); return; }
            var plan = Make(host);
            if (plan == null) return;
            menuAsked = true;
            if (plan.same) return;
            var mine = ModsConfig.ActiveModsInLoadOrder.Select(m => m.PackageIdPlayerFacing.ToLowerInvariant()).ToList();
            int differ = plan.order.Except(mine).Count() + mine.Except(plan.order).Count();
            var text = "RF_ModSyncPrompt".Translate(host.player, differ == 0 ? "RF_ModSyncOrderOnly".Translate().ToString() : differ.ToString()).ToString();
            // say plainly what would be switched off: a save that uses those mods loses their things if loaded without them
            var off = mine.Except(plan.order).Select(id => ModLister.GetModWithIdentifier(id, ignorePostfix: true)?.Name ?? id).ToList();
            if (off.Count > 0) text += "\n\n" + "RF_ModSyncTurnsOff".Translate(string.Join(", ", off));
            Find.WindowStack.Add(new Dialog_MessageBox(text, "RF_ModSyncButton".Translate(host.player), () => Start(host), "RF_NotNow".Translate()));
        }

        public static void ConfirmRestore() =>
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_ModSyncRestoreConfirm".Translate(), Restore));

        /// Both buttons, for the welcome screen and the mod checker.
        public static void Buttons(Listing_Standard l, Friend friend)
        {
            if (friend != null && l.ButtonText("RF_ModSyncButton".Translate(friend.player))) Start(friend);
            if (HasBackup && l.ButtonText("RF_ModSyncRestore".Translate())) ConfirmRestore();
        }
    }

    public class ModSyncWindow : Window
    {
        readonly Friend friend;
        ModSync.Plan plan;
        bool subscribed, restarting;
        float nextCheck;

        public override Vector2 InitialSize => new Vector2(560f, 520f);

        public ModSyncWindow(Friend friend)
        {
            this.friend = friend;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        static bool IsInstalled(ulong id) =>
            ((EItemState)SteamUGC.GetItemState(new PublishedFileId_t(id)) & (EItemState.k_EItemStateInstalled | EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending))
            == EItemState.k_EItemStateInstalled;

        static float Progress(ulong id) =>
            SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(id), out ulong done, out ulong total) && total > 0 ? (float)done / total : 0f;

        public override void DoWindowContents(Rect inRect)
        {
            var l = new Listing_Standard();
            l.Begin(inRect);
            Text.Font = GameFont.Medium;
            l.Label("RF_ModSyncTitle".Translate(friend.player));
            Text.Font = GameFont.Small;

            plan ??= ModSync.Make(friend);
            if (plan == null) { l.Label(friend.online ? "RF_ModsLoading".Translate() : "RF_ModsOffline".Translate(friend.player)); l.End(); return; }
            if (plan.same) { l.Label("RF_ModSyncSame".Translate()); l.End(); return; }

            if (plan.unavailable.Count > 0) { GUI.color = Color.gray; l.Label("RF_ModSyncUnavailable".Translate(string.Join(", ", plan.unavailable))); GUI.color = Color.white; }

            bool allThere = true;
            if (plan.download.Count > 0)
            {
                if (!SteamManager.Initialized) { l.Label("RF_ModSyncNoSteam".Translate()); l.End(); return; }
                if (!subscribed)
                {
                    foreach (var m in plan.download) SteamUGC.SubscribeItem(new PublishedFileId_t(m.workshop));
                    subscribed = true;
                }
                l.Label("RF_ModSyncDownloading".Translate(plan.download.Count));
                foreach (var m in plan.download)
                {
                    bool done = IsInstalled(m.workshop);
                    allThere &= done;
                    l.Label("  " + m.name + ": " + (done ? "RF_ModSyncStateDone".Translate().ToString() : "RF_ModSyncStateDown".Translate(Mathf.RoundToInt(Progress(m.workshop) * 100)).ToString()));
                }
            }

            if (allThere && plan.download.Count > 0 && Time.realtimeSinceStartup > nextCheck)
            {
                // RimWorld picks up new Workshop folders when its mod list is rebuilt.
                nextCheck = Time.realtimeSinceStartup + 2f;
                if (plan.download.Any(m => ModLister.GetModWithIdentifier(m.id, ignorePostfix: true) == null)) ModLister.RebuildModList();
            }
            bool ready = allThere && plan.order.All(id => ModLister.GetModWithIdentifier(id, ignorePostfix: true) != null);

            l.Gap();
            GUI.color = Color.gray;
            l.Label("RF_ModSyncBackupNote".Translate());
            GUI.color = Color.white;
            if (!ready) { l.End(); return; }

            l.Label("RF_ModSyncReady".Translate(friend.player));
            // Out of a game this is the one click: restart straight away. In a game, ask first so nothing unsaved is lost.
            if (Current.ProgramState != ProgramState.Playing && !restarting) { restarting = true; ModSync.Apply(plan.order); }
            else
            {
                l.Label("RF_ModSyncSaveFirst".Translate());
                if (l.ButtonText("RF_ModSyncRestartNow".Translate())) ModSync.Apply(plan.order);
            }
            l.End();
        }
    }
}
