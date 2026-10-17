using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:CallForHelp — while your home is under attack an alert offers to call every online friend;
    /// each gets a letter whose one click sends their best fighters as an AI help trip.
    public static class HelpCall
    {
        static int lastCallTick = -999999;

        public static Map ThreatenedHome =>
            Find.Maps.FirstOrDefault(m => m.IsPlayerHome && GenHostility.AnyHostileActiveThreatToPlayer(m, countDormantPawnsAsHostile: false));

        public static bool CanCall =>
            Gifts.Usable && Roster.OthersWithColony.Any(f => f.online) &&
            Find.TickManager.TicksGame - lastCallTick > RFSettings.HelpCallCooldownTicks;

        public static void Call(Map map)
        {
            lastCallTick = Find.TickManager.TicksGame;
            Link.Send(new Msg("HelpCall", "*", new XElement("from", Link.MyId), new XElement("colony", map.Parent?.LabelCap.ToString() ?? Faction.OfPlayer.Name)));
            Messages.Message("RF_HelpCalled".Translate(), MessageTypeDefOf.NeutralEvent, false);
        }

        public static void Init() => Link.On("HelpCall", OnCall);

        static void OnCall(Msg m)
        {
            var from = m.Get("from");
            if (from == Link.MyId || !Inbox.CanHandleNow || !Rules.Active.callForHelp) return;
            var letter = (HelpCallLetter)LetterMaker.MakeLetter(DefDatabase<LetterDef>.GetNamed("RF_HelpCall"));
            letter.friendId = from;
            letter.Label = "RF_HelpCallLabel".Translate();
            letter.title = "RF_HelpCallLabel".Translate();
            letter.Text = "RF_HelpCallText".Translate(Roster.NameOf(from));
            letter.StartTimeout(RFSettings.HelpCallTimeoutTicks);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// The fighters a colony would send: free, healthy, best shooters/brawlers first, never the last colonist.
        public static List<Pawn> PickDefenders(int count)
        {
            var map = Arrival.HomeMap;
            if (map == null) return new List<Pawn>();
            var free = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.InMentalState && !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            int keep = Mathf.Max(1, map.mapPawns.FreeColonistsSpawnedCount - count);
            int n = Mathf.Min(count, map.mapPawns.FreeColonistsSpawnedCount - keep);
            return free.OrderByDescending(p => Mathf.Max(p.skills.GetSkill(SkillDefOf.Shooting).Level, p.skills.GetSkill(SkillDefOf.Melee).Level)).Take(n).ToList();
        }
    }

    public class HelpCallLetter : ChoiceLetter
    {
        public string friendId;

        public override bool CanDismissWithRightClick => true;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly) { yield return Option_Close; yield break; }
                var friend = Roster.Get(friendId);
                var send = new DiaOption("RF_HelpCallSend".Translate(RFSettings.HelpCallSendCount))
                {
                    action = () =>
                    {
                        var pawns = HelpCall.PickDefenders(RFSettings.HelpCallSendCount);
                        if (friend != null && pawns.Count > 0) Trips.Send(pawns, friend, "help", "ai");
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true
                };
                var defenders = HelpCall.PickDefenders(RFSettings.HelpCallSendCount);
                if (friend == null || friend.colony.NullOrEmpty()) send.Disable("RF_FriendGone".Translate());
                else if (defenders.Count == 0) send.Disable("RF_NoDefenders".Translate());
                else if (Dlc.Missing(friend, defenders.Select(PawnData.Capture)) is var missing && missing.Count > 0) send.Disable(Dlc.Reason(friend, missing));
                yield return send;
                yield return Option_Reject;
                yield return Option_Postpone;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref friendId, "friendId");
        }
    }

    public class Alert_RFCallForHelp : Alert
    {
        public Alert_RFCallForHelp()
        {
            defaultLabel = "RF_AlertCallLabel".Translate();
            defaultExplanation = "RF_AlertCallDesc".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override AlertReport GetReport()
        {
            if (Live.InSession || !Rules.Active.callForHelp || !HelpCall.CanCall) return false;
            return HelpCall.ThreatenedHome != null;
        }

        protected override void OnClick()
        {
            var map = HelpCall.ThreatenedHome;
            if (map != null && HelpCall.CanCall) HelpCall.Call(map);
        }
    }
}
