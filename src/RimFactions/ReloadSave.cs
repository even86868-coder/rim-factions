using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:LoadSaveTogether — the host picks a save from inside a game and everyone moves to it: friends drop back to
    /// the main menu, the host loads the save and hosts it, and friends rejoin by themselves. Works with RimWorld saves
    /// and with Multiplayer's own saves (autosaves included).
    public static class ReloadSave
    {
        static string pendingSave, pendingReplay;
        static bool friendLeaving;

        public static bool CanUse => Current.ProgramState == ProgramState.Playing && (MpBridge.Hosting || (!Live.InSession && Link.Role == LinkRole.Hub));

        public static bool Reloading => Roster.Together != null && (string)Roster.Together.Attribute("reloading") == "true";

        /// host: everyone moves to this save.
        public static void Start(FileInfo file, bool multiplayerSave)
        {
            Log.Message("[Rim Factions] loading " + file.Name + " for everyone");
            Live.ForgetSession();   // ending this session must not tell friends it closed: we're coming back
            Link.Send(new Msg("Together", "hub", new XElement("session", new XAttribute("open", true), new XAttribute("host", Link.MyId),
                new XAttribute("reloading", true), new XAttribute("steam", ""), new XAttribute("lan", ""), new XAttribute("name", Path.GetFileNameWithoutExtension(file.Name)))));
            if (Live.InSession) MpBridge.Stop();
            if (multiplayerSave) { pendingReplay = file.FullName; GenScene.GoToMainMenu(); }
            else { pendingSave = Path.GetFileNameWithoutExtension(file.Name); GameDataSaveLoader.LoadGame(pendingSave); }
        }

        public static void Update()
        {
            if (LongEventHandler.AnyEventNowOrWaiting) return;

            // host: a Multiplayer save loads from the main menu, then hosts
            if (pendingReplay != null && Current.ProgramState == ProgramState.Entry)
            {
                var path = pendingReplay;
                pendingReplay = null;
                if (!MpBridge.HostReplay(path, Find.World?.info?.name ?? Path.GetFileNameWithoutExtension(path)))
                    Messages.Message("RF_HostFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                else Live.AnnounceWhenHosting();
            }

            // host: a RimWorld save finished loading: host it
            if (pendingSave != null && Current.ProgramState == ProgramState.Playing && !Live.InSession)
            {
                pendingSave = null;
                Live.OpenTogether();
            }

            // friend: the host is loading another save: leave, and rejoin when it's up
            if (Reloading && Live.InSession && !MpBridge.Hosting && !friendLeaving)
            {
                friendLeaving = true;
                Lobby.FollowHost();
                Messages.Message("RF_ReloadFriend".Translate(Roster.NameOf((string)Roster.Together.Attribute("host"))), MessageTypeDefOf.NeutralEvent, false);
                MpBridge.Stop();
                GenScene.GoToMainMenu();
            }
            if (!Reloading) friendLeaving = false;
        }

        public static IEnumerable<(FileInfo file, bool mp)> Saves()
        {
            var list = new List<(FileInfo, bool)>();
            try { list.AddRange(GenFilePaths.AllSavedGameFiles.Select(f => (f, false))); } catch { }
            try
            {
                var dir = Traverse.Create(AccessTools.TypeByName("Multiplayer.Client.Multiplayer")).Property("ReplaysDir").GetValue<string>();
                if (dir != null && Directory.Exists(dir))
                    list.AddRange(new DirectoryInfo(dir).GetFiles("*.zip").Select(f => (f, true)));
            }
            catch { }
            return list.OrderByDescending(t => t.Item1.LastWriteTime);
        }
    }

    public class ReloadSaveWindow : Window
    {
        Vector2 scroll;
        readonly List<(FileInfo file, bool mp)> saves;

        public override Vector2 InitialSize => new Vector2(620f, 640f);

        public ReloadSaveWindow()
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            saves = ReloadSave.Saves().ToList();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), "RF_ReloadTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, 44f), "RF_ReloadHint".Translate());
            var outer = new Rect(inRect.x, inRect.y + 84f, inRect.width, inRect.height - 84f);
            var view = new Rect(0, 0, outer.width - 16f, saves.Count * 34f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            for (int i = 0; i < saves.Count; i++)
            {
                var (file, mp) = saves[i];
                var r = new Rect(0, i * 34f, view.width, 32f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(r);
                Widgets.DrawHighlightIfMouseover(r);
                Widgets.Label(new Rect(r.x + 6, r.y + 6, r.width * 0.55f, 24f), Path.GetFileNameWithoutExtension(file.Name).Truncate(r.width * 0.55f));
                GUI.color = Color.gray;
                Widgets.Label(new Rect(r.x + r.width * 0.57f, r.y + 6, r.width * 0.2f, 24f), mp ? "RF_ReloadMp".Translate() : "RF_ReloadSp".Translate());
                Widgets.Label(new Rect(r.x + r.width * 0.75f, r.y + 6, r.width * 0.25f, 24f), file.LastWriteTime.ToString("g"));
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(r))
                {
                    var pick = saves[i];
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_ReloadConfirm".Translate(Path.GetFileNameWithoutExtension(pick.file.Name)),
                        () => { Close(); ReloadSave.Start(pick.file, pick.mp); }, destructive: true));
                }
            }
            Widgets.EndScrollView();
        }
    }
}
