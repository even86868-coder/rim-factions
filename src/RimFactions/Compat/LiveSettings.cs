using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace RimFactions.Compat
{
    /// system:LiveSettings — mod settings that change how the game plays must match in a live session, or the
    /// games drift apart. While live, such settings are swapped for the mod's defaults (the same for everyone)
    /// or given fixed values; your own settings come back as soon as the session ends.
    public static class LiveSettings
    {
        static readonly List<(Action enter, Action leave)> entries = new List<(Action, Action)>();
        static bool live;

        public static void Init()
        {
            new Harmony("melty.rimfactions.livesettings").Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)),
                prefix: new HarmonyMethod(typeof(LiveSettings), nameof(Update)));
        }

        /// Swap the settings object held in these static fields for a fresh default one while live.
        public static void UseDefaults(params FieldInfo[] fields)
        {
            object mine = null;
            Add(() =>
            {
                mine = fields[0].GetValue(null);
                var defaults = Activator.CreateInstance(mine.GetType());
                foreach (var f in fields) f.SetValue(null, defaults);
            }, () => { foreach (var f in fields) f.SetValue(null, mine); });
        }

        /// Give these fields of a settings object fixed values while live (fields the mod lacks are skipped).
        public static void Fix(Func<object> settings, params (string field, object value)[] values)
        {
            var saved = new Dictionary<string, object>();
            Add(() =>
            {
                var s = settings();
                foreach (var (field, value) in values)
                {
                    var t = Traverse.Create(s).Field(field);
                    if (!t.FieldExists()) continue;   // a version of the mod without this setting
                    saved[field] = t.GetValue();
                    t.SetValue(value);
                }
            }, () =>
            {
                var s = settings();
                foreach (var kv in saved) Traverse.Create(s).Field(kv.Key).SetValue(kv.Value);
            });
        }

        public static void Add(Action enter, Action leave) => entries.Add((enter, leave));

        /// Every frame and before a game finishes loading: switch over when a live session starts or ends.
        public static void Update()
        {
            bool now = MP.enabled && MP.IsInMultiplayer;
            if (now == live) return;
            live = now;
            foreach (var (enter, leave) in entries)
            {
                try { (now ? enter : leave)(); }
                catch (Exception e) { Log.Warning("[Rim Factions] live settings: " + e.Message); }
            }
        }
    }
}
