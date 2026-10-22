using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimFactions
{
    /// system:BlueprintMenuCheck — diagnostics. When right-clicking a blueprint or frame with one colonist selected
    /// gives no menu at all, the log says why each construction job turned it down (forbidden, reserved, unreachable,
    /// another faction's, Rim Factions' work rules...). Only reads; logs at most once every few seconds.
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    static class BlueprintMenuCheck
    {
        static float lastLog;

        [HarmonyPriority(Priority.Last)]
        static void Postfix(List<Pawn> selectedPawns, Vector3 clickPos, List<FloatMenuOption> __result)
        {
            try
            {
                if (__result == null || __result.Count > 0 || selectedPawns == null || selectedPawns.Count != 1) return;
                if (Time.realtimeSinceStartup - lastLog < 5f) return;
                var pawn = selectedPawns[0];
                var map = pawn?.Map;
                if (map == null) return;
                var cell = IntVec3.FromVector3(clickPos);
                if (!cell.InBounds(map)) return;
                var things = cell.GetThingList(map).Where(t => t is Blueprint || t is Frame).ToList();
                if (things.Count == 0) return;
                lastLog = Time.realtimeSinceStartup;
                foreach (var t in things) Log.Message("[Rim Factions] no menu on " + Describe(pawn, t));
            }
            catch (System.Exception e) { Log.Warning("[Rim Factions] blueprint menu check: " + e.Message); }
        }

        static string Describe(Pawn pawn, Thing t)
        {
            var map = pawn.Map;
            var comp = Current.Game?.GetComponent<RFGameComp>();
            var parts = new List<string>
            {
                $"{t.LabelCap} ({t.GetType().Name}, faction {t.Faction?.Name ?? "none"}) for {pawn.LabelShortCap} (faction {pawn.Faction?.Name ?? "none"})",
                $"player-controlled {pawn.IsColonistPlayerControlled}, drafted {pawn.Drafted}, construction disabled {pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)}, priority {pawn.workSettings?.GetPriority(WorkTypeDefOf.Construction)}",
                $"forbidden {t.IsForbidden(pawn)}, reachable {pawn.CanReach(t, PathEndMode.Touch, Danger.Deadly)}, reserved by {map.reservationManager.FirstRespectedReserver(t, pawn)?.LabelShort ?? "nobody"}",
                $"owner {comp?.OwnerOf(pawn) ?? "-"}, me {MpBridge.MyUsername ?? "-"}, ordered by {(comp != null && comp.orderOwners.TryGetValue(PersonalWork.ThingKey(t), out var who) ? who : "-")}, blocked colonist {OwnColonists.Blocked(pawn)}, blocked thing {PersonalWork.BlockedThing(t, pawn)}",
            };
            JobFailReason.Clear();
            bool can = GenConstruct.CanConstruct(t, pawn, true, true, null);
            parts.Add($"can construct {can}{(JobFailReason.HaveReason ? " (" + JobFailReason.Reason + ")" : "")}");
            foreach (var wg in DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(d => d.workType == WorkTypeDefOf.Construction && d.Worker is WorkGiver_Scanner))
            {
                var s = (WorkGiver_Scanner)wg.Worker;
                try
                {
                    JobFailReason.Clear();
                    bool skip = s.ShouldSkip(pawn, true);
                    bool has = !skip && s.HasJobOnThing(pawn, t, true);
                    if (has || JobFailReason.HaveReason || s.PotentialWorkThingRequest.Accepts(t))
                        parts.Add($"{wg.defName}: {(skip ? "skipped" : has ? "has job" : "no job")}{(JobFailReason.HaveReason ? " (" + JobFailReason.Reason + ")" : "")}, direct order {wg.directOrderable}");
                }
                catch (System.Exception e) { parts.Add($"{wg.defName}: error {e.GetType().Name}: {e.Message}"); }
            }
            JobFailReason.Clear();
            return string.Join("; ", parts);
        }
    }
}
