using System;
using System.IO;
using System.Threading;
using HarmonyLib;
using Multiplayer.API;
using RimFactions.Net;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace RimFactions
{
    /// Values from design/sheets.json "settings".
    public static class RFSettings
    {
        public const int MaxPlayers = 4;
        public const int LinkSteamChannel = 7;
        public const int LinkTcpPort = 30602;
        // Tunable ones come from the planet's rules (system:PlanetRules): the host's while linked, else yours.
        static RimFactionsSettings R => Rules.Active;
        public static float GoodwillPerSilver => 1f / Mathf.Max(1, R.silverPerGoodwill);
        public static int GiftGoodwillCap => R.giftGoodwillCap;
        public static int ColonistGiftGoodwill => R.colonistGiftGoodwill;
        public static int HelpTripGoodwill => R.helpTripGoodwill;
        public static int RaidGoodwill => R.raidGoodwill;
        public static int HostileAt => R.hostileAt;
        public static int AllyAt => R.allyAt;
        public static int HelpStayTicks => R.helpStayHours * 2500;
        public const int HelpCalmTicks = 5000;
        public const int RaidMaxTicks = 90000;
        public static int TogetherStartPawns => R.togetherStartPawns;
        public static int HelpCallCooldownTicks => R.helpCallCooldownHours * 2500;
        public const int HelpCallTimeoutTicks = 15000;
        public static int HelpCallSendCount => R.helpCallSendCount;
        public const int TradeStockMaxStacks = 250;
        public static int TradeGoodwill => R.tradeGoodwill;
        public static int PlanetEventFirstTicks => R.assaultFirstDays * 60000;
        public static int PlanetEventEveryTicks => R.assaultEveryDays * 60000;
        public const int PlanetEventDurationTicks = 60000;
        public const int PlanetEventMinPlayers = 2;
        public static int PlanetEventRewardSilver => R.assaultRewardSilver;
        public static float PlanetEventPointsFactor => R.assaultStrengthPercent / 100f;
        public const string HostLogPrefix = "Hosting at ";
        public const string LogFile = "{localappdata}/RimFactions/logs/latest.log";
    }

    public class RimFactionsMod : Mod
    {
        public override string SettingsCategory() => "Rim Factions";

        /// This game's Rim Factions version (About.xml modVersion).
        public static string Version = "?";

        public override void DoSettingsWindowContents(Rect inRect) => Rules.Draw(inRect);

        public RimFactionsMod(ModContentPack content) : base(content)
        {
            Rules.Local = GetSettings<RimFactionsSettings>();
            Version = content?.ModMetaData?.ModVersion ?? "?";
            new Harmony("melty.rimfactions").PatchAll();
            HostPatches.Apply(new Harmony("melty.rimfactions.mp"));
            if (MP.enabled) MP.RegisterAll();
            HostAddressLog.Write("Rim Factions started");
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                // each part starts on its own: one that fails is logged and the rest still start
                foreach (var (name, init) in new (string, Action)[]
                {
                    ("Roster", () => Roster.Init()),
                    ("HelpCall", () => HelpCall.Init()),
                    ("Trade", () => Trade.Init()),
                    ("ModCompat", () => ModCompat.Init()),
                    ("Compat.LiveCompat", () => Compat.LiveCompat.Init()),
                    ("Compat.LiveSettings", () => Compat.LiveSettings.Init()),
                    ("Compat.HostConfigs", () => Compat.HostConfigs.Init()),
                    ("Lobby", () => Lobby.Init()),
                    ("Inbox", () => Inbox.Init()),
                    ("Live", () => Live.Init()),
                    ("Link", () => Link.Init()),
                    ("SharedSaves", () => SharedSaves.Init()),
                    ("Colonists", () => EarlyColonistsDone.Init(new Harmony("melty.rimfactions.colonists"))),
                    ("PersonalWork", () => PersonalWork.Init(new Harmony("melty.rimfactions.personalwork"))),
                    ("DesyncReport", () => DesyncReport.Init(new Harmony("melty.rimfactions.desyncreport"))),
                    ("SyncCheck", () => SyncCheck.Init(new Harmony("melty.rimfactions.synccheck"))),
                    ("TestHarness", () => TestHarness.Init()),
                    ("MeltyTest", () => MeltyTest.Init()),
                    ("Driver", () => { new GameObject("RimFactions.Driver").AddComponent<Driver>(); }),
                })
                {
                    try { init(); }
                    catch (Exception e) { Log.Error($"[Rim Factions] {name} failed to start: {e}"); }
                }
            });
            Log.Message("[Rim Factions] loaded");
        }
    }

    class Driver : MonoBehaviour
    {
        void Awake() => DontDestroyOnLoad(gameObject);

        static void Run(int part, Action step)
        {
            try { step(); }
            catch (Exception e)
            {
                var real = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                Log.ErrorOnce($"[Rim Factions] {step.Method.DeclaringType?.Name}.{step.Method.Name}: {real}", 0x52464631 + part);
            }
        }
        void Update()
        {
            // each part on its own, so one failing doesn't stop the others; each error is logged once, in full
            Run(0, Compat.LiveSettings.Update); Run(1, Live.Update); Run(2, ReloadSave.Update); Run(3, ModSync.MenuCheck);
            Run(4, Lobby.Update); Run(5, Starters.Update); Run(6, JoinWelcome.Tick); Run(7, Trade.Tick); Run(8, DesyncReport.Update);
        }
    }

    /// hook:HostWindowDefaults — live sessions opened by hand default to Steam, 4 players, one shared faction.
    public static class HostPatches
    {
        public static void Apply(Harmony harmony)
        {
            var hostWindow = AccessTools.TypeByName("Multiplayer.Client.HostWindow");
            if (hostWindow == null) { Log.Warning("[Rim Factions] Multiplayer HostWindow not found"); return; }
            foreach (var ctor in AccessTools.GetDeclaredConstructors(hostWindow))
                harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(HostPatches), nameof(HostWindowDefaults)));

            // Multiplayer renames the host's faction to "<user>'s faction" when hosting; keep the colony's own name.
            var setup = AccessTools.Method(AccessTools.TypeByName("Multiplayer.Client.HostUtil"), "SetupGameFromSingleplayer");
            if (setup != null)
                harmony.Patch(setup, prefix: new HarmonyMethod(typeof(HostPatches), nameof(KeepNamePrefix)), postfix: new HarmonyMethod(typeof(HostPatches), nameof(KeepNamePostfix)));
        }

        static void KeepNamePrefix(out string __state) => __state = RimWorld.Faction.OfPlayer?.Name;
        static void KeepNamePostfix(string __state) { if (__state != null) RimWorld.Faction.OfPlayer.Name = __state; }

        static void HostWindowDefaults(object __instance)
        {
            try
            {
                var settings = Traverse.Create(__instance).Field("serverSettings");
                settings.Field("maxPlayers").SetValue(RFSettings.MaxPlayers);
                if (SteamManager.Initialized) settings.Field("steam").SetValue(true);
                MpBridge.DevModeSynced(settings);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] host defaults failed: " + e.Message); }
        }
    }

    /// system:HostAddressLog — Melty reads "Hosting at <address>" from setting:LogFile to give joiners a link.
    public static class HostAddressLog
    {
        static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimFactions", "logs", "latest.log");

        public static void Write(string line)
        {
            var text = line + Environment.NewLine;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (path)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.AppendAllText(path, text);
                    }
                }
                catch { }
            });
        }
    }
}
