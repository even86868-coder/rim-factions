using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// ItemData (design/sheets.json "data"): an item that travels between games.
    public static class ItemData
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static XElement Capture(Thing t)
        {
            var inner = t.GetInnerIfMinified();
            var x = new XElement("item",
                new XAttribute("def", inner.def.defName),
                new XAttribute("count", t.stackCount),
                new XAttribute("hp", inner.HitPoints),
                new XAttribute("value", (t.MarketValue * t.stackCount).ToString(Inv)));
            if (inner != t) x.Add(new XAttribute("minified", true));
            if (inner.Stuff != null) x.Add(new XAttribute("stuff", inner.Stuff.defName));
            if (inner.TryGetQuality(out var q)) x.Add(new XAttribute("quality", (int)q));
            return x;
        }

        /// Unknown defs (a mod the receiver doesn't have) arrive as silver of the same value.
        public static Thing Make(XElement x)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail((string)x.Attribute("def"));
            int count = (int?)x.Attribute("count") ?? 1;
            if (def == null)
            {
                float value = float.Parse((string)x.Attribute("value") ?? "0", Inv);
                var silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                silver.stackCount = Mathf.Max(1, Mathf.RoundToInt(value));
                return silver;
            }
            var stuff = (string)x.Attribute("stuff") is string s ? DefDatabase<ThingDef>.GetNamedSilentFail(s) : null;
            if (def.MadeFromStuff && stuff == null) stuff = GenStuff.DefaultStuffFor(def);
            if (!def.MadeFromStuff) stuff = null;
            var thing = ThingMaker.MakeThing(def, stuff);
            if ((int?)x.Attribute("quality") is int q) thing.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)q, ArtGenerationContext.Outsider);
            if ((int?)x.Attribute("hp") is int hp && thing.def.useHitPoints) thing.HitPoints = Mathf.Clamp(hp, 1, thing.MaxHitPoints);
            thing.stackCount = Mathf.Clamp(count, 1, Mathf.Max(1, def.stackLimit));
            if ((bool?)x.Attribute("minified") == true && def.Minifiable) return thing.MakeMinified();
            return thing;
        }

        /// Splits stacks larger than a def's stack limit.
        public static IEnumerable<Thing> MakeAll(XElement x)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail((string)x.Attribute("def"));
            int count = (int?)x.Attribute("count") ?? 1;
            if (def == null || count <= def.stackLimit) { yield return Make(x); yield break; }
            while (count > 0)
            {
                var part = new XElement(x);
                int n = Mathf.Min(count, def.stackLimit);
                part.SetAttributeValue("count", n);
                count -= n;
                yield return Make(part);
            }
        }
    }

    /// PawnData (design/sheets.json "data"): a colonist that travels between games.
    public static class PawnData
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static XElement Capture(Pawn p)
        {
            var name = p.Name as NameTriple;
            var x = new XElement("pawn",
                new XAttribute("id", p.ThingID),
                new XAttribute("kind", p.kindDef.defName),
                new XAttribute("gender", (int)p.gender),
                new XAttribute("ageBio", p.ageTracker.AgeBiologicalYearsFloat.ToString(Inv)),
                new XAttribute("ageChrono", p.ageTracker.AgeChronologicalYearsFloat.ToString(Inv)),
                new XAttribute("first", name?.First ?? p.LabelShort),
                new XAttribute("nick", name?.Nick ?? p.LabelShort),
                new XAttribute("last", name?.Last ?? ""));
            var st = p.story;
            if (st != null)
            {
                x.Add(new XAttribute("childhood", st.Childhood?.defName ?? ""), new XAttribute("adulthood", st.Adulthood?.defName ?? ""),
                    new XAttribute("head", st.headType?.defName ?? ""), new XAttribute("body", st.bodyType?.defName ?? ""),
                    new XAttribute("hair", st.hairDef?.defName ?? ""), new XAttribute("hairColor", ColorUtility.ToHtmlStringRGBA(st.HairColor)),
                    new XAttribute("skinColor", ColorUtility.ToHtmlStringRGBA(st.SkinColor)));
                foreach (var t in st.traits.allTraits.Where(t => t.sourceGene == null))
                    x.Add(new XElement("trait", new XAttribute("def", t.def.defName), new XAttribute("degree", t.Degree)));
            }
            if (p.style?.beardDef != null) x.Add(new XAttribute("beard", p.style.beardDef.defName));
            if (ModsConfig.BiotechActive && p.genes?.Xenotype != null && p.genes.Xenotype != XenotypeDefOf.Baseliner) x.Add(new XAttribute("xenotype", p.genes.Xenotype.defName));
            if (p.skills != null)
                foreach (var s in p.skills.skills)
                    x.Add(new XElement("skill", new XAttribute("def", s.def.defName), new XAttribute("level", s.levelInt),
                        new XAttribute("passion", (int)s.passion), new XAttribute("xp", s.xpSinceLastLevel.ToString(Inv))));
            x.Add(CaptureHealth(p));
            if (p.apparel != null) foreach (var a in p.apparel.WornApparel) x.Add(new XElement("apparel", ItemData.Capture(a)));
            if (p.equipment != null) foreach (var e in p.equipment.AllEquipmentListForReading) x.Add(new XElement("equipment", ItemData.Capture(e)));
            if (p.inventory != null) foreach (var i in p.inventory.innerContainer) x.Add(new XElement("inventory", ItemData.Capture(i)));
            CaptureDlcParts(p, x);
            return x;
        }

        // ---- expansion content (system:DlcCheck makes sure the receiver has these) ----

        static void CaptureDlcParts(Pawn p, XElement x)
        {
            // Royalty: psycasts and titles (psylink level rides along in health)
            if (p.abilities != null)
                foreach (var a in p.abilities.abilities.Where(a => a.sourcePrecept == null)) // ideology role powers stay with the role
                    x.Add(new XElement("ability", new XAttribute("def", a.def.defName)));
            if (p.royalty != null)
                foreach (var t in p.royalty.AllTitlesForReading.Where(t => t.faction != null))
                    x.Add(new XElement("title", new XAttribute("def", t.def.defName), new XAttribute("faction", t.faction.def.defName),
                        new XAttribute("favor", p.royalty.GetFavor(t.faction))));
            // Ideology: tattoos
            if (p.style?.FaceTattoo != null) x.Add(new XAttribute("faceTattoo", p.style.FaceTattoo.defName));
            if (p.style?.BodyTattoo != null) x.Add(new XAttribute("bodyTattoo", p.style.BodyTattoo.defName));
            // Biotech: the exact genes, also for custom xenotypes
            // (skin and hair colour genes are looks only and stay out, so a plain baseliner needs no Biotech)
            var realEndo = p.genes?.Endogenes.Where(g => g.def.endogeneCategory == EndogeneCategory.None).ToList();
            if (p.genes != null && (realEndo.Count > 0 || p.genes.Xenogenes.Count > 0))
            {
                var g = new XElement("genes");
                if (!p.genes.xenotypeName.NullOrEmpty()) g.Add(new XAttribute("name", p.genes.xenotypeName));
                if (p.genes.iconDef != null) g.Add(new XAttribute("icon", p.genes.iconDef.defName));
                foreach (var gene in realEndo) g.Add(new XElement("endo", new XAttribute("def", gene.def.defName)));
                foreach (var gene in p.genes.Xenogenes) g.Add(new XElement("xeno", new XAttribute("def", gene.def.defName)));
                x.Add(g);
            }
            // Anomaly: ghouls and other mutants
            if (p.mutant?.Def != null) x.Add(new XAttribute("mutant", p.mutant.Def.defName));
        }

        static void ApplyGenes(Pawn p, XElement x)
        {
            var g = x.Element("genes");
            if (g == null || p.genes == null || !ModsConfig.BiotechActive) return;
            try
            {
                foreach (var gene in p.genes.Endogenes.Where(gn => gn.def.endogeneCategory == EndogeneCategory.None).ToList()) p.genes.RemoveGene(gene);
                p.genes.ClearXenogenes();
                foreach (var e in g.Elements("endo"))
                    if (DefDatabase<GeneDef>.GetNamedSilentFail((string)e.Attribute("def")) is GeneDef d) p.genes.AddGene(d, false);
                foreach (var e in g.Elements("xeno"))
                    if (DefDatabase<GeneDef>.GetNamedSilentFail((string)e.Attribute("def")) is GeneDef d) p.genes.AddGene(d, true);
                p.genes.xenotypeName = (string)g.Attribute("name");
                if ((string)g.Attribute("icon") is string icon) p.genes.iconDef = DefDatabase<XenotypeIconDef>.GetNamedSilentFail(icon);
            }
            catch (Exception e) { Log.Warning($"[Rim Factions] genes on {p}: {e.Message}"); }
        }

        static void ApplyDlcParts(Pawn p, XElement x)
        {
            try
            {
                if (p.abilities != null)
                    foreach (var a in x.Elements("ability"))
                        if (DefDatabase<AbilityDef>.GetNamedSilentFail((string)a.Attribute("def")) is AbilityDef ad && p.abilities.GetAbility(ad) == null)
                            p.abilities.GainAbility(ad);
                if (p.royalty != null && ModsConfig.RoyaltyActive)
                    foreach (var t in x.Elements("title"))
                    {
                        var def = DefDatabase<RoyalTitleDef>.GetNamedSilentFail((string)t.Attribute("def"));
                        var facDef = DefDatabase<FactionDef>.GetNamedSilentFail((string)t.Attribute("faction"));
                        var fac = facDef == null ? null : Find.FactionManager.FirstFactionOfDef(facDef);
                        if (def == null || fac == null) continue;
                        p.royalty.SetTitle(fac, def, grantRewards: false, rewardsOnlyForNewestTitle: false, sendLetter: false);
                        p.royalty.SetFavor(fac, (int?)t.Attribute("favor") ?? 0, notifyOnFavorChanged: false);
                    }
                if (p.style != null && ModsConfig.IdeologyActive)
                {
                    if (Def<TattooDef>(x, "faceTattoo") is TattooDef ft) p.style.FaceTattoo = ft;
                    if (Def<TattooDef>(x, "bodyTattoo") is TattooDef bt) p.style.BodyTattoo = bt;
                }
                if (ModsConfig.AnomalyActive && Def<MutantDef>(x, "mutant") is MutantDef md && p.mutant == null)
                    MutantUtility.SetPawnAsMutantInstantly(p, md);
            }
            catch (Exception e) { Log.Warning($"[Rim Factions] expansion parts on {p}: {e.Message}"); }
        }

        /// Biotech: a mechanitor's mech on a trip, as a stand-in of this faction.
        static Pawn MakeMech(XElement x, PawnKindDef kind, Faction faction)
        {
            var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                canGeneratePawnRelations: false));
            p.health.RemoveAllHediffs();
            foreach (var hx in x.Element("health")?.Elements("h") ?? Enumerable.Empty<XElement>()) AddHediff(p, hx);
            SetGear(p, x);
            return p;
        }

        static XElement CaptureHealth(Pawn p)
        {
            var health = new XElement("health");
            var parts = p.RaceProps.body.AllParts;
            foreach (var h in p.health.hediffSet.hediffs)
            {
                var hx = new XElement("h", new XAttribute("def", h.def.defName), new XAttribute("sev", h.Severity.ToString(Inv)));
                if (h.Part != null)
                    hx.Add(new XAttribute("part", h.Part.def.defName), new XAttribute("idx", parts.Where(bp => bp.def == h.Part.def).ToList().IndexOf(h.Part)));
                if (h.TryGetComp<HediffComp_GetsPermanent>()?.IsPermanent == true) hx.Add(new XAttribute("perm", true));
                if (h is Hediff_Level hl) hx.Add(new XAttribute("level", hl.level)); // Royalty: psylink level
                health.Add(hx);
            }
            return health;
        }

        static BodyPartRecord FindPart(Pawn p, XElement hx)
        {
            var partDef = (string)hx.Attribute("part");
            if (partDef == null) return null;
            var list = p.RaceProps.body.AllParts.Where(bp => bp.def.defName == partDef).ToList();
            int idx = (int?)hx.Attribute("idx") ?? 0;
            return idx >= 0 && idx < list.Count ? list[idx] : list.FirstOrDefault();
        }

        static void AddHediff(Pawn p, XElement hx)
        {
            try
            {
                var def = DefDatabase<HediffDef>.GetNamedSilentFail((string)hx.Attribute("def"));
                if (def == null) return;
                var part = FindPart(p, hx);
                if ((string)hx.Attribute("part") != null && part == null) return;
                if (part != null && p.health.hediffSet.PartIsMissing(part)) return;
                var h = HediffMaker.MakeHediff(def, p, part);
                h.Severity = float.Parse((string)hx.Attribute("sev"), Inv);
                if ((bool?)hx.Attribute("perm") == true && h.TryGetComp<HediffComp_GetsPermanent>() is HediffComp_GetsPermanent perm) perm.IsPermanent = true;
                p.health.AddHediff(h, part);
                if (h is Hediff_Level hl && (int?)hx.Attribute("level") is int lv && lv > hl.level) hl.SetLevelTo(lv);
            }
            catch (Exception e) { Log.Warning($"[Rim Factions] skipped a hediff on {p}: {e.Message}"); }
        }

        /// Builds a stand-in for a colonist from another game, in this game's faction.
        public static Pawn Make(XElement x, Faction faction)
        {
            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail((string)x.Attribute("kind")) ?? PawnKindDefOf.Colonist;
            if (kind.RaceProps?.IsMechanoid == true) return MakeMech(x, kind, faction);
            if (kind.RaceProps?.Humanlike != true) kind = PawnKindDefOf.Colonist;
            var xeno = (string)x.Attribute("xenotype") is string xs && ModsConfig.BiotechActive ? DefDatabase<XenotypeDef>.GetNamedSilentFail(xs) : null;
            var req = new PawnGenerationRequest(kind, faction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                canGeneratePawnRelations: false, allowFood: false, allowAddictions: false,
                fixedBiologicalAge: float.Parse((string)x.Attribute("ageBio"), Inv),
                fixedChronologicalAge: float.Parse((string)x.Attribute("ageChrono"), Inv),
                fixedGender: (Gender)(int)x.Attribute("gender"),
                forcedXenotype: xeno);
            var p = PawnGenerator.GeneratePawn(req);

            p.Name = new NameTriple((string)x.Attribute("first"), (string)x.Attribute("nick"), (string)x.Attribute("last"));
            var st = p.story;
            if (st != null)
            {
                if (Def<BackstoryDef>(x, "childhood") is BackstoryDef ch) st.Childhood = ch;
                st.Adulthood = Def<BackstoryDef>(x, "adulthood");
                if (Def<HeadTypeDef>(x, "head") is HeadTypeDef head) st.headType = head;
                if (Def<BodyTypeDef>(x, "body") is BodyTypeDef body) st.bodyType = body;
                if (Def<HairDef>(x, "hair") is HairDef hair) st.hairDef = hair;
                if (ColorUtility.TryParseHtmlString("#" + (string)x.Attribute("hairColor"), out var hc)) st.HairColor = hc;
                if (ColorUtility.TryParseHtmlString("#" + (string)x.Attribute("skinColor"), out var sc)) st.skinColorOverride = sc;
                var traits = x.Elements("trait").ToList();
                if (traits.Count > 0)
                {
                    foreach (var t in st.traits.allTraits.Where(t => t.sourceGene == null).ToList()) st.traits.RemoveTrait(t);
                    foreach (var tx in traits)
                        if (DefDatabase<TraitDef>.GetNamedSilentFail((string)tx.Attribute("def")) is TraitDef td && !st.traits.HasTrait(td))
                            st.traits.GainTrait(new Trait(td, (int)tx.Attribute("degree"), forced: true));
                }
            }
            if (p.style != null && Def<BeardDef>(x, "beard") is BeardDef beard) p.style.beardDef = beard;
            if (p.skills != null)
                foreach (var sx in x.Elements("skill"))
                    if (DefDatabase<SkillDef>.GetNamedSilentFail((string)sx.Attribute("def")) is SkillDef sd)
                    {
                        var rec = p.skills.GetSkill(sd);
                        rec.levelInt = (int)sx.Attribute("level");
                        rec.passion = (Passion)(int)sx.Attribute("passion");
                        rec.xpSinceLastLevel = float.Parse((string)sx.Attribute("xp"), Inv);
                    }

            ApplyGenes(p, x);
            p.health.RemoveAllHediffs();
            foreach (var hx in x.Element("health")?.Elements("h") ?? Enumerable.Empty<XElement>()) AddHediff(p, hx);
            ApplyDlcParts(p, x);
            SetGear(p, x);
            p.Notify_DisabledWorkTypesChanged();
            p.Drawer?.renderer?.SetAllGraphicsDirty();
            return p;
        }

        /// Puts a returning colonist's trip results (wounds, illnesses, gear) onto the original pawn.
        public static void ApplyReturn(Pawn original, XElement x)
        {
            var hs = original.health.hediffSet;
            foreach (var h in hs.hediffs.Where(h => h is Hediff_Injury || (h is Hediff_MissingPart && !hs.PartOrAnyAncestorHasDirectlyAddedParts(h.Part))).ToList())
                original.health.RemoveHediff(h);
            var have = new HashSet<HediffDef>(hs.hediffs.Select(h => h.def));
            foreach (var hx in x.Element("health")?.Elements("h") ?? Enumerable.Empty<XElement>())
            {
                var def = DefDatabase<HediffDef>.GetNamedSilentFail((string)hx.Attribute("def"));
                if (def == null) continue;
                bool injuryLike = typeof(Hediff_Injury).IsAssignableFrom(def.hediffClass) || typeof(Hediff_MissingPart).IsAssignableFrom(def.hediffClass);
                if (injuryLike || !have.Contains(def)) AddHediff(original, hx);
            }
            SetGear(original, x);
        }

        static void SetGear(Pawn p, XElement x)
        {
            p.apparel?.DestroyAll();
            p.equipment?.DestroyAllEquipment();
            p.inventory?.DestroyAll();
            foreach (var ax in x.Elements("apparel"))
                if (ItemData.Make(ax.Element("item")) is Apparel a && p.apparel != null && ApparelUtility.HasPartsToWear(p, a.def))
                    p.apparel.Wear(a, dropReplacedApparel: false);
            foreach (var ex in x.Elements("equipment"))
                if (ItemData.Make(ex.Element("item")) is ThingWithComps eq && p.equipment != null && eq.def.equipmentType == EquipmentType.Primary && p.equipment.Primary == null)
                    p.equipment.AddEquipment(eq);
            foreach (var ix in x.Elements("inventory"))
                foreach (var t in ItemData.MakeAll(ix.Element("item")))
                    p.inventory?.innerContainer.TryAdd(t);
        }

        static T Def<T>(XElement x, string attr) where T : Def =>
            (string)x.Attribute(attr) is string s && s.Length > 0 ? DefDatabase<T>.GetNamedSilentFail(s) : null;

        public static string NameOf(XElement x) => (string)x.Attribute("nick") ?? (string)x.Attribute("first") ?? "?";
    }
}
