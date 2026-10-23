using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimFactions.Compat
{
    /// fix:BrokenTrades — a caravan met on the world map offers Trade / Move on / Attack. In a live game the choice can
    /// run more than once (both players had the window), so "Trade" could run after "Move on" or "Attack" had already
    /// dissolved the other caravan. Multiplayer then started a trade session with a caravan that no longer exists:
    /// building the trade list failed halfway, the broken session stayed, the trade window stayed blank and the game was
    /// stuck. Now no trade opens with a caravan that is gone, a session that fails while being made is removed, and a
    /// broken one that already exists (in a save) is closed for everyone.
    public static class BrokenTrades
    {
        static Type sessionType;
        static FieldInfo traderField;
        static float nextCheck;

        public static void Init(Harmony h)
        {
            sessionType = AccessTools.TypeByName("Multiplayer.Client.MpTradeSession");
            if (sessionType == null) return;
            traderField = AccessTools.Field(sessionType, "trader");
            h.Patch(AccessTools.Method(sessionType, "TryCreate"),
                prefix: new HarmonyMethod(typeof(BrokenTrades), nameof(NotWithGoneCaravan)),
                finalizer: new HarmonyMethod(typeof(BrokenTrades), nameof(DropIfFailed)));
            h.Patch(AccessTools.Method(sessionType, "Tick"), prefix: new HarmonyMethod(typeof(BrokenTrades), nameof(TickDropsBroken)));
        }

        /// A trader that can't be traded with any more: a world-map caravan that has been dissolved or emptied.
        public static bool Gone(ITrader trader)
        {
            if (!(trader is Caravan c)) return false;
            try { return c.Destroyed || c.pawns == null || c.pawns.Count == 0 || c.TraderKind == null; }
            catch { return true; }
        }

        static bool NotWithGoneCaravan(ITrader trader, ref object __result)
        {
            if (!Gone(trader)) return true;
            __result = null;
            Messages.Message("RF_TradeGone".Translate(), MessageTypeDefOf.RejectInput, false);
            return false;
        }

        static Exception DropIfFailed(Exception __exception, ITrader trader, ref object __result)
        {
            if (__exception == null) return null;
            Log.Warning("[Rim Factions] a trade couldn't be set up and was cancelled: " + __exception.Message);
            RemoveWhere(t => t == trader);
            __result = null;
            return null;
        }

        static bool TickDropsBroken(object __instance)
        {
            if (!Gone(traderField.GetValue(__instance) as ITrader)) return true;
            Remove(__instance);
            return false;
        }

        /// Called every frame: the host closes any broken trade session for everyone (needed when the game is
        /// waiting on it and nothing ticks).
        public static void Update()
        {
            if (sessionType == null || !MP.IsInMultiplayer || !MpBridge.Hosting || Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 3f;
            if (Sessions()?.Cast<object>().Any(s => Gone(traderField.GetValue(s) as ITrader)) == true) CloseBrokenTrades();
        }

        [SyncMethod]
        public static void CloseBrokenTrades()
        {
            if (RemoveWhere(Gone) > 0) Messages.Message("RF_TradeGone".Translate(), MessageTypeDefOf.NeutralEvent, false);
        }

        static object WorldComp => AccessTools.Property(AccessTools.TypeByName("Multiplayer.Client.Multiplayer"), "WorldComp")?.GetValue(null);

        static IList Sessions() => WorldComp == null ? null : Traverse.Create(WorldComp).Field("trading").GetValue() as IList;

        static int RemoveWhere(Func<ITrader, bool> match)
        {
            var list = Sessions();
            if (list == null) return 0;
            int n = 0;
            foreach (var s in list.Cast<object>().ToList())
                if (match(traderField.GetValue(s) as ITrader)) { Remove(s); n++; }
            return n;
        }

        static void Remove(object session)
        {
            try
            {
                var manager = Traverse.Create(WorldComp).Field("sessionManager").GetValue();
                AccessTools.Method(manager.GetType(), "RemoveSession").Invoke(manager, new[] { session });
                Find.WindowStack?.TryRemove(AccessTools.TypeByName("Multiplayer.Client.TradingWindow"), false);
            }
            catch (Exception e) { Log.Warning("[Rim Factions] couldn't close a broken trade: " + e.Message); }
        }
    }
}
