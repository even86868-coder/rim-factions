using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// The Rim Factions window: planet status, players, goodwill, and the buttons to host, join,
    /// play together or start on a friend's planet. Opened from the main menu and the in-game bar.
    public class RimFactionsWindow : Window
    {
        string address = "";
        Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(640f, 560f);

        public RimFactionsWindow()
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            forcePause = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var l = new Listing_Standard();
            l.Begin(inRect);
            Text.Font = GameFont.Medium;
            l.Label("Rim Factions");
            Text.Font = GameFont.Small;
            l.Label(Link.Status.Translate() + (Link.HubAddress != null && Link.Role != LinkRole.Off ? $"  ({Link.HubAddress})" : ""));
            l.GapLine();

            bool playing = Current.ProgramState == ProgramState.Playing;
            if (Link.Role == LinkRole.Off)
            {
                if (l.ButtonText("RF_HostPlanet".Translate())) Link.StartHub();
                l.Label("RF_JoinAddress".Translate());
                address = l.TextEntry(address);
                if (l.ButtonText("RF_Join".Translate()) && !address.NullOrEmpty()) Link.StartClient(address);
            }
            else
            {
                if (Link.Role == LinkRole.Hub && l.ButtonText("RF_CopyAddress".Translate()))
                {
                    GUIUtility.systemCopyBuffer = Link.HubAddress;
                    Messages.Message("RF_AddressCopied".Translate(), MessageTypeDefOf.NeutralEvent, false);
                }
                if (l.ButtonText("RF_Disconnect".Translate())) Link.StopAll();
            }

            // feature:Lobby: a new game with friends, from the main menu
            if (!playing && Link.Role == LinkRole.Hub && l.ButtonText("RF_LobbyOpen".Translate()))
            {
                Close();
                if (!Lobby.IsOpen) Lobby.Open();
                Lobby.ShowWindow();
            }
            if (!playing && Link.Role == LinkRole.Client && Lobby.IsOpen && l.ButtonText("RF_LobbyShow".Translate(Roster.Get(Roster.HubId)?.player ?? "?")))
            {
                Close();
                Lobby.ShowWindow();
            }
            if (Link.Connected && Link.Role == LinkRole.Client && Roster.Together == null && !Lobby.IsOpen && Roster.Get(Roster.HubId) is Friend hostF)
            {
                if (l.ButtonText("RF_AskJoinColony".Translate(hostF.player))) { Close(); Lobby.Ask("join"); }
                if (!playing && l.ButtonText("RF_AskNewTogether".Translate(hostF.player))) { Close(); Lobby.Ask("new"); }
            }
            if (Link.Connected)
            {
                var together = Roster.Together;
                bool iHostTogether = together != null && (string)together.Attribute("host") == Link.MyId;
                var hostMismatch = together == null ? new List<string>() : Dlc.LiveMismatch(Roster.Get((string)together.Attribute("host")));
                if (together != null && !iHostTogether && !Live.InSession && hostMismatch.Count > 0)
                    l.Label("RF_LiveNeedsSameDlc".Translate(string.Join(", ", hostMismatch.Select(Dlc.Label))));
                else if (together != null && !iHostTogether && !Live.InSession)
                {
                    if (l.ButtonText("RF_JoinTogether".Translate(Roster.NameOf((string)together.Attribute("host")))))
                    {
                        if (playing) Compat.LiveCompat.ConfirmLive(() => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_LeaveGameConfirm".Translate(), Live.JoinTogether)));
                        else Compat.LiveCompat.ConfirmLive(Live.JoinTogether);
                        Close();
                    }
                }
                if (!playing && Roster.World != null && Link.Role == LinkRole.Client)
                {
                    if (l.ButtonText("RF_StartOnPlanet".Translate())) { Close(); JoinWelcome.StartOnPlanet(); }
                }
                if (playing && !Live.InSession && Rules.Active.playTogether && Find.Maps.Any(m => m.IsPlayerHome))
                {
                    if (l.ButtonText("RF_InviteTogether".Translate())) { Close(); Compat.LiveCompat.ConfirmLive(Live.OpenTogether); }
                }
                if (Live.InSession && MpBridge.Hosting && Current.Game.GetComponent<RFGameComp>().liveVisits.Any(v => !v.done))
                {
                    if (l.ButtonText("RF_EndVisits".Translate())) Live.EndVisits();
                }
                else if (Live.InSession && MpBridge.Hosting)
                {
                    if (l.ButtonText("RF_EndSession".Translate()))
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_EndSessionConfirm".Translate(), () => { Close(); MpBridge.ConvertToSingleplayer(); }));
                }
            }
            if (Live.VisitorAway) l.Label("RF_VisitorAway".Translate(Roster.NameOf(Live.VisitorFriend)));

            l.GapLine();
            l.Label("RF_Players".Translate());
            l.End();

            var listRect = new Rect(inRect.x, l.CurHeight + 4f, inRect.width, inRect.height - l.CurHeight - 4f);
            var rows = Roster.Players;
            var view = new Rect(0, 0, listRect.width - 16f, rows.Count * 52f);
            Widgets.BeginScrollView(listRect, ref scroll, view);
            float y = 0;
            foreach (var f in rows)
            {
                var r = new Rect(0, y, view.width, 48f);
                if (rows.IndexOf(f) % 2 == 0) Widgets.DrawAltRect(r);
                Widgets.DrawBoxSolid(new Rect(r.x + 4, r.y + 8, 6, 32), f.color);
                Widgets.Label(new Rect(r.x + 16, r.y + 2, r.width * 0.6f, 24), $"{f.Label}{(f.IsMe ? " " + "RF_You".Translate() : "")}");
                GUI.color = Color.gray;
                Widgets.Label(new Rect(r.x + 16, r.y + 24, r.width * 0.6f, 24),
                    (f.online ? "RF_Online".Translate() : "RF_Offline".Translate()) + $" · {f.colonists} " + "RF_Colonists".Translate() + $" · {((float)f.wealth).ToStringMoney()}");
                GUI.color = Color.white;
                if (!f.IsMe)
                {
                    int gw = Roster.Goodwill(Link.MyId, f.id);
                    Widgets.Label(new Rect(r.x + r.width * 0.55f, r.y + 2, r.width * 0.3f, 44),
                        "RF_GoodwillShort".Translate(gw.ToString("+0;-0;0"), ("RF_Kind_" + Roster.Kind(Link.MyId, f.id)).Translate()));
                    if (Widgets.ButtonText(new Rect(r.xMax - 110, r.y + 10, 104, 28), "RF_CompareMods".Translate()))
                        Find.WindowStack.Add(new ModCompareWindow(f));
                }
                y += 52f;
            }
            Widgets.EndScrollView();
        }
    }

    public class MainButtonWorker_RimFactions : MainButtonWorker
    {
        public override void Activate() => Find.WindowStack.Add(new RimFactionsWindow());
    }

    [HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
    static class MainMenuButton
    {
        static void Postfix(Rect rect)
        {
            if (Current.ProgramState != ProgramState.Entry) return;
            var r = new Rect(rect.x, rect.y + rect.height + 12f, 170f, 40f);
            if (Widgets.ButtonText(r, "Rim Factions")) Find.WindowStack.Add(new RimFactionsWindow());
        }
    }

    [StaticConstructorOnStartup]
    public static class RFTex
    {
        public static readonly Texture2D Gift = ContentFinder<Texture2D>.Get("UI/Commands/RF_Gift", false) ?? BaseContent.BadTex;
        public static readonly Texture2D Trip = ContentFinder<Texture2D>.Get("UI/Commands/RF_Trip", false) ?? Gift;
    }
}
