using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RimFactions.Net;
using RimWorld;
using Verse;

namespace RimFactions
{
    /// system:DlcCheck — which RimWorld expansions each game has, and what a gift, trip or trade needs.
    /// Rule (agreed with the creator): anything a friend's game can't load is blocked at the sender, with
    /// the missing expansion named.
    public static class Dlc
    {
        const string CorePackage = "ludeon.rimworld";

        /// Official expansions active in this game, as lowercase package ids.
        public static List<string> Mine =>
            ModLister.AllExpansions.Select(e => e.linkedMod?.ToLowerInvariant()).Where(id => id != null && id != CorePackage && ModsConfig.IsActive(id)).ToList();

        public static string Label(string packageId) =>
            ModLister.AllExpansions.FirstOrDefault(e => e.linkedMod?.ToLowerInvariant() == packageId)?.label?.CapitalizeFirst()
            ?? packageId.Replace("ludeon.rimworld.", "").CapitalizeFirst();

        static string DlcOf(Def d)
        {
            var id = d?.modContentPack?.PackageId?.ToLowerInvariant();
            return id != null && id.StartsWith(CorePackage + ".") ? id : null;
        }

        static readonly Func<string, Def>[] lookups =
        {
            s => DefDatabase<ThingDef>.GetNamedSilentFail(s), s => DefDatabase<PawnKindDef>.GetNamedSilentFail(s),
            s => DefDatabase<HediffDef>.GetNamedSilentFail(s), s => DefDatabase<TraitDef>.GetNamedSilentFail(s),
            s => DefDatabase<BackstoryDef>.GetNamedSilentFail(s), s => DefDatabase<HairDef>.GetNamedSilentFail(s),
            s => DefDatabase<BeardDef>.GetNamedSilentFail(s), s => DefDatabase<HeadTypeDef>.GetNamedSilentFail(s),
            s => DefDatabase<BodyTypeDef>.GetNamedSilentFail(s), s => DefDatabase<XenotypeDef>.GetNamedSilentFail(s),
            s => DefDatabase<GeneDef>.GetNamedSilentFail(s), s => DefDatabase<AbilityDef>.GetNamedSilentFail(s),
            s => DefDatabase<RoyalTitleDef>.GetNamedSilentFail(s), s => DefDatabase<TattooDef>.GetNamedSilentFail(s),
            s => DefDatabase<MutantDef>.GetNamedSilentFail(s), s => DefDatabase<XenotypeIconDef>.GetNamedSilentFail(s),
        };

        /// Looks only: a friend without the expansion just gets their own default for these.
        static readonly HashSet<string> cosmetic = new HashSet<string>
            { "hair", "beard", "head", "body", "childhood", "adulthood", "faceTattoo", "bodyTattoo", "icon", "name", "first", "nick", "last", "id", "thing" };

        /// Expansions the defs named in a payload come from (looked up in this game).
        public static HashSet<string> Required(IEnumerable<XElement> payload)
        {
            var need = new HashSet<string>();
            foreach (var root in payload)
                foreach (var a in root.DescendantsAndSelf().SelectMany(e => e.Attributes()))
                {
                    if (cosmetic.Contains(a.Name.LocalName)) continue;
                    var v = a.Value;
                    // a person's pawn kind only matters for mechs; people are rebuilt as colonists anyway
                    if (a.Name.LocalName == "kind" && DefDatabase<PawnKindDef>.GetNamedSilentFail(v)?.RaceProps?.IsMechanoid != true) continue;
                    if (v.Length < 2 || char.IsDigit(v[0])) continue;
                    foreach (var look in lookups)
                        if (DlcOf(look(v)) is string id) { need.Add(id); break; }
                }
            return need;
        }

        /// Expansions the friend lacks for this payload; empty when it can go (or the friend's game is too
        /// old to say what it has).
        public static List<string> Missing(Friend f, IEnumerable<XElement> payload)
        {
            if (f?.dlcs == null) return new List<string>();
            return Required(payload).Where(id => !f.dlcs.Contains(id)).ToList();
        }

        public static string Reason(Friend f, List<string> missing) =>
            "RF_DlcMissing".Translate(f.player, string.Join(", ", missing.Select(Label)));

        /// Live sessions run one game for everyone, so they need the same expansions on both sides.
        public static List<string> LiveMismatch(Friend f)
        {
            if (f?.dlcs == null) return new List<string>();
            var mine = Mine;
            return mine.Except(f.dlcs).Concat(f.dlcs.Except(mine)).Distinct().ToList();
        }

        public static XElement HelloElement() => new XElement("dlcs", string.Join(",", Mine));
    }
}
