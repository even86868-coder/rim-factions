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
    /// feature:ModChecker — compare mod lists with a friend: what each side is missing (with Workshop links)
    /// and how safe every mod is for live play, from the Multiplayer team's compatibility list.
    public class ModInfo
    {
        public string id, name;
        public ulong workshop;
        public bool xmlOnly;
    }

    public static class ModCompat
    {
        /// The Rim Factions stack itself and Harmony: always there, never worth comparing.
        static readonly HashSet<string> stack = new HashSet<string>
            { "zetrith.prepatcher", "rwmt.multiplayer", "rwmt.multiplayercompatibility", "melty.rimfactions", "brrainz.harmony" };

        static readonly Dictionary<string, List<ModInfo>> friendMods = new Dictionary<string, List<ModInfo>>();
        static readonly Dictionary<string, List<string>> friendOrder = new Dictionary<string, List<string>>();
        static List<ModInfo> mine;

        public static void Init()
        {
            Link.On("ModListRequest", m => Link.Send(new Msg("ModList", m.From, new XElement("mods",
                Mine.Select(i => new XElement("mod", new XAttribute("id", i.id), new XAttribute("name", i.name ?? i.id),
                    new XAttribute("ws", i.workshop), new XAttribute("xml", i.xmlOnly)))),
                // feature:ModSync — the whole active list in load order, so a friend can copy it exactly
                new XElement("order", string.Join(",", ModsConfig.ActiveModsInLoadOrder.Select(md => md.PackageIdPlayerFacing.ToLowerInvariant()))))));
            Link.On("ModList", m =>
            {
                friendMods[m.From] = m.x.Element("mods")?.Elements("mod").Select(e => new ModInfo
                {
                    id = (string)e.Attribute("id"), name = (string)e.Attribute("name"),
                    workshop = ulong.TryParse((string)e.Attribute("ws"), out var w) ? w : 0, xmlOnly = (bool?)e.Attribute("xml") ?? false,
                }).ToList() ?? new List<ModInfo>();
                friendOrder[m.From] = ((string)m.x.Element("order"))?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            });
        }

        /// This game's active mods, official content and the Rim Factions stack left out.
        public static List<ModInfo> Mine
        {
            get
            {
                if (mine != null) return mine;
                mine = new List<ModInfo>();
                foreach (var mod in ModsConfig.ActiveModsInLoadOrder.Where(md => !md.Official))
                {
                    var id = mod.PackageIdPlayerFacing?.ToLowerInvariant() ?? mod.PackageId;
                    if (stack.Contains(id)) continue;
                    bool hasCode;
                    try { hasCode = Directory.EnumerateFiles(mod.RootDir.FullName, "*.dll", SearchOption.AllDirectories).Any(); }
                    catch { hasCode = true; }
                    mine.Add(new ModInfo { id = id, name = mod.Name, workshop = mod.GetPublishedFileId().m_PublishedFileId, xmlOnly = !hasCode });
                }
                return mine;
            }
        }

        public static void Request(string friendId)
        {
            friendMods.Remove(friendId);
            friendOrder.Remove(friendId);
            Link.Send(new Msg("ModListRequest", friendId));
        }

        public static List<ModInfo> Of(string friendId) => friendMods.TryGetValue(friendId, out var l) ? l : null;

        /// The friend's full active mod list in load order (official content and the Rim Factions stack included).
        public static List<string> OrderOf(string friendId) => friendOrder.TryGetValue(friendId, out var l) ? l : null;

        // ---- live-play safety, from the bundled Multiplayer mod's own list ----

        /// 4 works, 3 minor issues, 2 major issues, 1 doesn't work, 0 unknown; -1 no code (always fine).
        public static int LiveStatus(ModInfo m)
        {
            if (m.xmlOnly) return -1;
            // Rim Factions' own list (system:LiveCompat) comes first: it knows about bundled and Rim Factions patches
            if (Compat.LiveCompat.For(m.id) is Compat.LiveCompatDef def)
                return def.status == "unsafe" ? 1 : def.status == "caution" ? 3 : 4;
            try
            {
                var mgr = AccessTools.TypeByName("Multiplayer.Client.ModCompatibilityManager");
                if (mgr == null) return 0;
                object info = null;
                if (m.workshop != 0) info = AccessTools.Method(mgr, "LookupByWorkshopId", new[] { typeof(ulong) })?.Invoke(null, new object[] { m.workshop });
                if (info == null && m.name != null) info = AccessTools.Method(mgr, "LookupByName", new[] { typeof(string) })?.Invoke(null, new object[] { m.name });
                return info == null ? 0 : Traverse.Create(info).Property("status").GetValue<int>();
            }
            catch { return 0; }
        }

        public static Color StatusColor(int s) => s switch
        {
            -1 => new Color(0.55f, 0.85f, 0.55f),
            4 => new Color(0.45f, 0.85f, 0.45f),
            3 => new Color(0.95f, 0.85f, 0.35f),
            2 => new Color(1f, 0.6f, 0.25f),
            1 => new Color(0.95f, 0.35f, 0.3f),
            _ => Color.gray,
        };

        public static string StatusLabel(int s) => ("RF_ModStatus_" + (s < 0 ? "xml" : s.ToString())).Translate();

        public static void OpenWorkshop(ulong id) =>
            Application.OpenURL($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
    }

    public class ModCompareWindow : Window
    {
        readonly Friend friend;
        Vector2 scroll;
        float viewHeight = 600f;

        public override Vector2 InitialSize => new Vector2(720f, 640f);

        public ModCompareWindow(Friend friend)
        {
            this.friend = friend;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            ModCompat.Request(friend.id);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "RF_ModsTitle".Translate(friend.Label));
            Text.Font = GameFont.Small;
            var theirs = ModCompat.Of(friend.id);
            var body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            if (theirs == null)
            {
                Widgets.Label(body, friend.online ? "RF_ModsLoading".Translate() : "RF_ModsOffline".Translate(friend.player));
                return;
            }

            var mine = ModCompat.Mine;
            var mineIds = new HashSet<string>(mine.Select(m => m.id));
            var theirIds = new HashSet<string>(theirs.Select(m => m.id));
            var theyOnly = theirs.Where(m => !mineIds.Contains(m.id)).ToList();
            var iOnly = mine.Where(m => !theirIds.Contains(m.id)).ToList();
            var all = mine.Concat(theirs.Where(m => !mineIds.Contains(m.id))).ToList();
            var risky = all.Select(m => (m, s: ModCompat.LiveStatus(m))).Where(t => t.s >= 0 && t.s < 4).OrderBy(t => t.s == 0 ? 9 : t.s).ToList();

            var view = new Rect(0, 0, body.width - 16f, viewHeight);
            Widgets.BeginScrollView(body, ref scroll, view);
            float y = 0;
            void Line(string text, Color? color = null, float h = 24f)
            {
                h = Mathf.Max(h, Text.CalcHeight(text, view.width) + 4f);
                GUI.color = color ?? Color.white;
                Widgets.Label(new Rect(0, y, view.width, h), text);
                GUI.color = Color.white;
                y += h;
            }

            if (theyOnly.Count == 0 && iOnly.Count == 0) Line("RF_ModsMatch".Translate(), new Color(0.5f, 0.9f, 0.5f), 28f);
            else Line("RF_ModsSummary".Translate(theyOnly.Count, iOnly.Count), null, 28f);
            if (!friend.IsMe && Widgets.ButtonText(new Rect(0, y, 300, 30), "RF_ModSyncButton".Translate(friend.player))) ModSync.Start(friend);
            if (ModSync.HasBackup && Widgets.ButtonText(new Rect(310, y, 220, 30), "RF_ModSyncRestore".Translate())) ModSync.ConfirmRestore();
            y += 36;
            Line("RF_ModsSoloNote".Translate(), Color.gray, 44f);

            y += 6;
            Line("RF_ModsTheyHave".Translate(friend.player, theyOnly.Count), new Color(0.75f, 0.85f, 1f), 28f);
            foreach (var m in theyOnly)
            {
                Widgets.Label(new Rect(12, y, view.width - 180, 24), m.name);
                if (m.workshop != 0 && Widgets.ButtonText(new Rect(view.width - 160, y, 150, 22), "RF_ModsGet".Translate())) ModCompat.OpenWorkshop(m.workshop);
                else if (m.workshop == 0) { GUI.color = Color.gray; Widgets.Label(new Rect(view.width - 160, y, 160, 24), "RF_ModsLocal".Translate()); GUI.color = Color.white; }
                y += 26;
            }

            y += 8;
            Line("RF_ModsYouHave".Translate(friend.player, iOnly.Count), new Color(0.75f, 0.85f, 1f), 28f);
            foreach (var m in iOnly) { Widgets.Label(new Rect(12, y, view.width - 12, 24), m.name); y += 24; }

            y += 8;
            Line("RF_ModsLive".Translate(risky.Count), new Color(0.75f, 0.85f, 1f), 28f);
            Line("RF_ModsLiveNote".Translate(), Color.gray, 44f);
            foreach (var (m, s) in risky)
            {
                var row = new Rect(12, y, view.width - 12, 24);
                var def = Compat.LiveCompat.For(m.id);
                Widgets.Label(new Rect(12, y, view.width - 200, 24), m.name);
                GUI.color = ModCompat.StatusColor(s);
                Widgets.Label(new Rect(view.width - 190, y, 190, 24), def != null ? ("RF_LCStatus_" + def.status).Translate().ToString() : ModCompat.StatusLabel(s));
                GUI.color = Color.white;
                if (def?.note != null) TooltipHandler.TipRegion(row, def.note);
                y += 24;
            }
            viewHeight = y + 10;
            Widgets.EndScrollView();
        }
    }
}
