using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace SerumsExpended;

// Every adaptive route specifies physical item counts, including stuff such as
// bioferrite. The vanilla volume getter would apply stuff-volume conversions.
public sealed class IngredientValueGetter_SerumCount : IngredientValueGetter
{
    public override float ValuePerUnitOf(ThingDef thing) => 1f;

    public override string BillRequirementsDescription(RecipeDef recipe, IngredientCount ingredient)
        => ingredient.GetBaseCount() + "x " + ingredient.filter.Summary;
}

public sealed class CompSerumInformation : ThingComp { }

public sealed class CompProperties_SerumInformation : CompProperties
{
    public CompProperties_SerumInformation()
    {
        compClass = typeof(CompSerumInformation);
    }

    private static StatDrawEntry Entry(string key, string value, int priority)
        => new StatDrawEntry(StatCategoryDefOf.Serum, ("SE_Info_" + key).Translate(),
            value, ("SE_Info_" + key + "_Desc").Translate(), priority);

    public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest request)
    {
        foreach (StatDrawEntry entry in base.SpecialDisplayStats(request)) yield return entry;
        ThingDef item = request.Def as ThingDef;
        HediffDef effect = item?.GetCompProperties<CompProperties_UseEffectAddHediff>()?.hediffDef;
        if (effect?.stages == null || effect.stages.Count == 0) yield break;
        HediffStage stage = effect.stages[0];
        RegenesisTimerProperties regenesis = effect.CompProps<RegenesisTimerProperties>();
        NeuroplasticTimerProperties neuro = effect.CompProps<NeuroplasticTimerProperties>();
        if (neuro != null)
        {
            yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                "StatsReport_SerumDuration".Translate(),
                neuro.disappearsAfterTicks.min.ToStringTicksToPeriod(),
                "StatsReport_SerumDuration_Desc".Translate(), 1000);
            yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                "SE_Neuro_Result".Translate(),
                (neuro.chooseSkill ? "SE_Neuro_AdvancedResult" : "SE_Neuro_NormalResult").Translate(),
                "SE_Neuro_ResultDesc".Translate(), 990);
        }
        if (regenesis != null)
        {
            yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                "StatsReport_SerumDuration".Translate(),
                regenesis.disappearsAfterTicks.min.ToStringTicksToPeriod(),
                "StatsReport_SerumDuration_Desc".Translate(), 1000);
            yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                "SE_Regenesis_Result".Translate(),
                (regenesis.restoreMissingParts ? "SE_Regenesis_AdvancedResult" : "SE_Regenesis_NormalResult").Translate(),
                "SE_Regenesis_ResultDesc".Translate(), 990);
        }

        // Reuse vanilla formatting and translated stat names for the actual
        // configured effects; numbers are never duplicated in flavor text.
        foreach (StatDrawEntry entry in stage.SpecialDisplayStats())
        {
            // Native capacity formatting rounds fractional offsets to integers.
            // Add these separately below to preserve Mind-numb's small penalty.
            if (stage.capMods != null && stage.capMods.Any(
                cap => entry.LabelCap == cap.capacity.GetLabelFor().CapitalizeFirst())) continue;
            yield return entry;
        }
        if (stage.capMods != null)
        {
            foreach (PawnCapacityModifier cap in stage.capMods)
            {
                if (cap.offset != 0f)
                    yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                        cap.capacity.GetLabelFor().CapitalizeFirst(),
                        (cap.offset * 100f).ToString("+0.##;-0.##;0") + "%",
                        cap.capacity.description, 940);
                if (cap.SetMaxDefined)
                    yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                        cap.capacity.GetLabelFor().CapitalizeFirst(),
                        "max".Translate().CapitalizeFirst() + " " + cap.setMax.ToStringPercent(),
                        cap.capacity.description, 940);
            }
        }

        ThoughtDef thought = DefDatabase<ThoughtDef>.GetNamedSilentFail(effect.defName);
        if (thought?.stages != null && thought.stages.Count > 0)
            yield return Entry("Mood", thought.stages[0].baseMoodEffect.ToString("+0.##;-0.##;0"), 930);
        if (stage.blocksMentalBreaks) yield return Entry("MentalBreaks", "Yes".Translate(), 920);
        if (stage.blocksInspirations) yield return Entry("Inspirations", "Yes".Translate(), 910);
        if (stage.pctAllThoughtNullification > 0f)
            yield return Entry("ThoughtSuppression", stage.pctAllThoughtNullification.ToStringPercent(), 900);
        if (stage.overrideMoodBase >= 0f)
            yield return Entry("MoodBase", stage.overrideMoodBase.ToStringPercent(), 890);
        if (regenesis != null || neuro != null)
            yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                "SE_Info_Stacking".Translate(), "No".Translate(),
                (regenesis != null ? "SE_Regenesis_AlreadyInComa" : "SE_Neuro_AlreadyInComa").Translate(), 880);
        else yield return Entry("Stacking", "No".Translate(), 880);

        RecipeDef recipe = DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(
            r => r.GetModExtension<AdaptiveSerumRecipe>() != null
                && r.recipeUsers != null && r.recipeUsers.Count > 0
                && r.products.Any(product => product.thingDef == item));
        AdaptiveSerumRecipe production = recipe?.GetModExtension<AdaptiveSerumRecipe>();
        if (production != null)
        {
            string report = string.Join("\n\n", production.routes.Select(route =>
                string.Join(" + ", route.ingredients.Select(input =>
                    input.count + "x " + input.thingDef.LabelCap))
                + "\n" + "SE_Info_Work".Translate() + ": " + route.work.ToString("0.##")));
            yield return new StatDrawEntry(StatCategoryDefOf.Serum,
                "SE_Info_Production".Translate(),
                "SE_Info_AlternativeCount".Translate(production.routes.Count),
                "SE_Info_Production_Desc".Translate() + "\n\n" + report, 870);
        }
    }
}
