using System.Linq;
using System.Xml.Linq;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:JoinWelcome — a friend who arrives from a join link is greeted once with what they can do on
    /// this planet: join the host's colony live, start their own colony here, or keep playing their own save.
    public static class JoinWelcome
    {
        public static bool Pending;

        /// Called every frame; opens the window once the roster has arrived and the game can show it.
        public static void Tick()
        {
            if (!Pending || Link.Role != LinkRole.Client || !Link.Connected || Roster.HubId == null) return;
            if (Live.InSession || LongEventHandler.AnyEventNowOrWaiting) return;
            if (Current.ProgramState != ProgramState.Entry && Current.ProgramState != ProgramState.Playing) return;
            if (Find.WindowStack == null || Find.WindowStack.IsOpen<JoinWelcomeWindow>()) return;
            Pending = false;
            Find.WindowStack.Add(new JoinWelcomeWindow());
        }

        public static void StartOnPlanet()
        {
            WorldParams.usePlanet = true;
            NewsFeed.Send("JoinedPlanet", new XElement("player", Link.MyName));
            Find.WindowStack.Add(new Page_SelectScenario());
        }
    }

    public class JoinWelcomeWindow : Window
    {
        public override Vector2 InitialSize => new Vector2(560f, 500f);

        public JoinWelcomeWindow()
        {
            doCloseX = true;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var host = Roster.Get(Roster.HubId);
            bool playing = Current.ProgramState == ProgramState.Playing;
            var together = Roster.Together;

            var l = new Listing_Standard();
            l.Begin(inRect);
            Text.Font = GameFont.Medium;
            l.Label("RF_WelcomeTitle".Translate(host?.player ?? "?"));
            Text.Font = GameFont.Small;
            var others = Roster.Others.Select(f => f.Label).ToList();
            l.Label("RF_WelcomeText".Translate(others.Count > 0 ? string.Join(", ", others) : "-"));
            l.Gap();

            var liveMismatch = Dlc.LiveMismatch(host);
            if (together != null && (string)together.Attribute("host") != Link.MyId && liveMismatch.Count > 0)
            {
                l.Label("RF_LiveNeedsSameDlc".Translate(string.Join(", ", liveMismatch.Select(Dlc.Label))));
                l.Gap();
            }
            else if (together != null && (string)together.Attribute("host") != Link.MyId)
            {
                if (l.ButtonText("RF_JoinTogether".Translate(Roster.NameOf((string)together.Attribute("host")))))
                {
                    Close();
                    if (playing) Compat.LiveCompat.ConfirmLive(() => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RF_LeaveGameConfirm".Translate(), Live.JoinTogether)));
                    else Compat.LiveCompat.ConfirmLive(Live.JoinTogether);
                }
                l.Label("RF_WelcomeTogetherHint".Translate());
                l.Gap();
            }

            // nothing open on the host's side yet: ask for it (feature:AskHost)
            if (host != null && !host.IsMe && together == null && !Lobby.IsOpen)
            {
                if (l.ButtonText("RF_AskJoinColony".Translate(host.player))) { Close(); Lobby.Ask("join"); }
                if (l.ButtonText("RF_AskNewTogether".Translate(host.player))) { Close(); Lobby.Ask("new"); }
                l.Gap();
            }

            if (!playing && Lobby.IsOpen)
            {
                if (l.ButtonText("RF_LobbyShow".Translate(host?.player ?? "?"))) { Close(); Lobby.ShowWindow(); }
                l.Gap();
            }

            if (!playing && Roster.World != null)
            {
                if (l.ButtonText("RF_StartOnPlanet".Translate())) { Close(); JoinWelcome.StartOnPlanet(); }
                l.Label("RF_WelcomeStartHint".Translate());
                l.Gap();
            }

            if (playing)
            {
                if (l.ButtonText("RF_WelcomeKeepPlaying".Translate())) Close();
            }
            else if (l.ButtonText("RF_WelcomeLoad".Translate()))
            {
                Close();
                Find.WindowStack.Add(new Dialog_SaveFileList_Load());
            }
            l.Label("RF_WelcomeOwnHint".Translate());
            l.Gap();
            ModSync.Buttons(l, host);
            if (host != null && l.ButtonText("RF_CompareModsWith".Translate(host.player))) Find.WindowStack.Add(new ModCompareWindow(host));
            l.End();
        }
    }
}
