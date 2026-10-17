using System.Linq;
using System.Xml.Linq;
using RimFactions.Net;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:SettingsMenu / system:PlanetRules — the planet's rules. Each player sets their own in Mod
    /// settings; while linked, the host's rules (sent in the roster) apply to everyone on the planet.
    public class RimFactionsSettings : ModSettings
    {
        // features
        public bool callForHelp = true, tradeCaravans = true, planetAssaults = true, playerRaids = true, liveVisits = true, playTogether = true;
        // goodwill
        public int silverPerGoodwill = 40, giftGoodwillCap = 25, colonistGiftGoodwill = 15, helpTripGoodwill = 10, raidGoodwill = -40, tradeGoodwill = 3;
        public int hostileAt = -75, allyAt = 75;
        // helping
        public int helpStayHours = 24, helpCallCooldownHours = 12, helpCallSendCount = 3;
        // assaults
        public int assaultFirstDays = 2, assaultEveryDays = 5, assaultRewardSilver = 300, assaultStrengthPercent = 80;
        // together
        public int togetherStartPawns = 3;
        public bool ownColonistsOnly = true;   // feature:OwnColonists
        // just yours, never the host's rules (feature:PlayerColors, feature:NameTags, feature:SharedSaves)
        public string playerColor = "";
        public bool nameTags = true;
        public string cloudFolder = "";

        public override void ExposeData()
        {
            Scribe_Values.Look(ref callForHelp, "callForHelp", true);
            Scribe_Values.Look(ref tradeCaravans, "tradeCaravans", true);
            Scribe_Values.Look(ref planetAssaults, "planetAssaults", true);
            Scribe_Values.Look(ref playerRaids, "playerRaids", true);
            Scribe_Values.Look(ref liveVisits, "liveVisits", true);
            Scribe_Values.Look(ref playTogether, "playTogether", true);
            Scribe_Values.Look(ref silverPerGoodwill, "silverPerGoodwill", 40);
            Scribe_Values.Look(ref giftGoodwillCap, "giftGoodwillCap", 25);
            Scribe_Values.Look(ref colonistGiftGoodwill, "colonistGiftGoodwill", 15);
            Scribe_Values.Look(ref helpTripGoodwill, "helpTripGoodwill", 10);
            Scribe_Values.Look(ref raidGoodwill, "raidGoodwill", -40);
            Scribe_Values.Look(ref tradeGoodwill, "tradeGoodwill", 3);
            Scribe_Values.Look(ref hostileAt, "hostileAt", -75);
            Scribe_Values.Look(ref allyAt, "allyAt", 75);
            Scribe_Values.Look(ref helpStayHours, "helpStayHours", 24);
            Scribe_Values.Look(ref helpCallCooldownHours, "helpCallCooldownHours", 12);
            Scribe_Values.Look(ref helpCallSendCount, "helpCallSendCount", 3);
            Scribe_Values.Look(ref assaultFirstDays, "assaultFirstDays", 2);
            Scribe_Values.Look(ref assaultEveryDays, "assaultEveryDays", 5);
            Scribe_Values.Look(ref assaultRewardSilver, "assaultRewardSilver", 300);
            Scribe_Values.Look(ref assaultStrengthPercent, "assaultStrengthPercent", 80);
            Scribe_Values.Look(ref togetherStartPawns, "togetherStartPawns", 3);
            Scribe_Values.Look(ref ownColonistsOnly, "ownColonistsOnly", true);
            Scribe_Values.Look(ref playerColor, "playerColor", "");
            Scribe_Values.Look(ref nameTags, "nameTags", true);
            Scribe_Values.Look(ref cloudFolder, "cloudFolder", "");
        }

        static readonly string[] fields =
        {
            "callForHelp", "tradeCaravans", "planetAssaults", "playerRaids", "liveVisits", "playTogether", "silverPerGoodwill", "giftGoodwillCap",
            "colonistGiftGoodwill", "helpTripGoodwill", "raidGoodwill", "tradeGoodwill", "hostileAt", "allyAt", "helpStayHours", "helpCallCooldownHours",
            "helpCallSendCount", "assaultFirstDays", "assaultEveryDays", "assaultRewardSilver", "assaultStrengthPercent", "togetherStartPawns", "ownColonistsOnly",
        };

        public XElement ToXml() =>
            new XElement("rules", fields.Select(f => new XAttribute(f, HarmonyLib.Traverse.Create(this).Field(f).GetValue().ToString())));

        public static RimFactionsSettings FromXml(XElement x)
        {
            var s = new RimFactionsSettings();
            var t = HarmonyLib.Traverse.Create(s);
            foreach (var f in fields)
            {
                var v = (string)x.Attribute(f);
                if (v == null) continue;
                var field = t.Field(f);
                if (field.GetValueType() == typeof(bool) && bool.TryParse(v, out var b)) field.SetValue(b);
                else if (int.TryParse(v, out var i)) field.SetValue(i);
            }
            return s;
        }

        public void Reset()
        {
            var d = new RimFactionsSettings();
            var src = HarmonyLib.Traverse.Create(d);
            var dst = HarmonyLib.Traverse.Create(this);
            foreach (var f in fields) dst.Field(f).SetValue(src.Field(f).GetValue());
        }
    }

    public static class Rules
    {
        public static RimFactionsSettings Local;
        public static RimFactionsSettings FromHost;
        public static string HostName;

        /// The rules in force: the host's while linked to someone else's planet, otherwise your own.
        public static RimFactionsSettings Active => Link.Role == LinkRole.Client && FromHost != null ? FromHost : Local ?? (Local = new RimFactionsSettings());

        public static bool HostSets => Link.Role == LinkRole.Client && FromHost != null;

        // ---- the Mod settings page ----

        public static void Draw(Rect rect)
        {
            var s = Local;
            var bannerText = HostSets ? "RF_SetHostRules".Translate(HostName ?? "?").ToString() : "RF_SetYourRules".Translate().ToString();
            float bannerH = Text.CalcHeight(bannerText, rect.width) + 6f;
            GUI.color = HostSets ? new Color(1f, 0.85f, 0.4f) : Color.white;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, bannerH), bannerText);
            GUI.color = Color.white;

            // two fixed columns, one line per setting
            float top = rect.y + bannerH + 4f, colW = (rect.width - 24f) / 2f;
            var l = new Listing_Standard();
            l.Begin(new Rect(rect.x, top, colW, rect.height - bannerH - 4f));
            Header(l, "RF_SetFeatures");
            l.CheckboxLabeled("RF_SetCallForHelp".Translate(), ref s.callForHelp);
            l.CheckboxLabeled("RF_SetTrade".Translate(), ref s.tradeCaravans);
            l.CheckboxLabeled("RF_SetAssaults".Translate(), ref s.planetAssaults);
            l.CheckboxLabeled("RF_SetRaids".Translate(), ref s.playerRaids);
            l.CheckboxLabeled("RF_SetLive".Translate(), ref s.liveVisits);
            l.CheckboxLabeled("RF_SetTogether".Translate(), ref s.playTogether);

            Header(l, "RF_SetGoodwill");
            Int(l, "RF_SetSilverPerGoodwill", ref s.silverPerGoodwill, 5, 200);
            Int(l, "RF_SetGiftCap", ref s.giftGoodwillCap, 0, 100);
            Int(l, "RF_SetColonistGift", ref s.colonistGiftGoodwill, 0, 50);
            Int(l, "RF_SetHelpGoodwill", ref s.helpTripGoodwill, 0, 50);
            Int(l, "RF_SetRaidGoodwill", ref s.raidGoodwill, -100, 0);
            Int(l, "RF_SetTradeGoodwill", ref s.tradeGoodwill, 0, 20);
            Int(l, "RF_SetHostileAt", ref s.hostileAt, -100, -10);
            Int(l, "RF_SetAllyAt", ref s.allyAt, 10, 100);
            l.End();

            l = new Listing_Standard();
            l.Begin(new Rect(rect.x + colW + 24f, top, colW, rect.height - bannerH - 4f));
            Header(l, "RF_SetHelping");
            Int(l, "RF_SetHelpStay", ref s.helpStayHours, 2, 72);
            Int(l, "RF_SetCallCooldown", ref s.helpCallCooldownHours, 1, 48);
            Int(l, "RF_SetCallCount", ref s.helpCallSendCount, 1, 8);

            Header(l, "RF_SetAssaultsHeader");
            Int(l, "RF_SetAssaultFirst", ref s.assaultFirstDays, 1, 30);
            Int(l, "RF_SetAssaultEvery", ref s.assaultEveryDays, 1, 30);
            Int(l, "RF_SetAssaultStrength", ref s.assaultStrengthPercent, 20, 300);
            Int(l, "RF_SetAssaultReward", ref s.assaultRewardSilver, 0, 2000);

            Header(l, "RF_SetTogetherHeader");
            Int(l, "RF_SetTogetherPawns", ref s.togetherStartPawns, 1, 6);
            l.CheckboxLabeled("RF_SetOwnColonists".Translate(), ref s.ownColonistsOnly);

            Header(l, "RF_SetYouHeader");   // always your own, whoever hosts
            l.Label("RF_LobbyMyColor".Translate());
            Lobby.ColorPicker(l.GetRect(30f));
            l.CheckboxLabeled("RF_SetNameTags".Translate(), ref s.nameTags);
            SharedSaves.DrawSettings(l);

            l.Gap(16f);
            if (l.ButtonText("RF_SetReset".Translate())) s.Reset();
            l.End();
        }

        static void Header(Listing_Standard l, string key)
        {
            l.Gap(6f);
            Text.Font = GameFont.Medium;
            l.Label(key.Translate());
            Text.Font = GameFont.Small;
        }

        static void Int(Listing_Standard l, string key, ref int value, int min, int max)
        {
            // label on the left, slider on the right, one line each
            var row = l.GetRect(Text.LineHeight + 8f);
            Widgets.Label(new Rect(row.x, row.y, row.width * 0.6f, row.height), key.Translate(value));
            value = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(row.x + row.width * 0.62f, row.y + 6f, row.width * 0.38f, 16f), value, min, max));
            TooltipHandler.TipRegion(row, key.Translate(value));
        }
    }
}
