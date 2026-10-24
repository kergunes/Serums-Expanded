using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace SerumsExpended;

// Optional integrations: no VE assembly is referenced or shipped with the mod.
// Bind only when the corresponding mod is active. Unknown passion kinds are
// preserved rather than guessed from their numeric enum value.
internal static class VanillaExpandedCompatibility
{
    private static readonly bool SkillsActive = ModsConfig.IsActive("vanillaexpanded.skills");
    private static readonly bool AndroidsActive = ModsConfig.IsActive("vanillaracesexpanded.android");
    private static readonly Type PassionManager = SkillsActive ? AccessTools.TypeByName("VSE.Passions.PassionManager") : null;
    private static readonly MethodInfo PassionToDef = PassionManager == null ? null
        : AccessTools.Method(PassionManager, "PassionToDef", new[] { typeof(Passion) });
    private static readonly MethodInfo ClearLearningCache = SkillsActive
        ? OptionalMethod("VSE.Passions.LearnRateFactorCache", "ClearCache", Type.EmptyTypes) : null;
    private static readonly MethodInfo AndroidCheck = AndroidsActive
        ? OptionalMethod("VREAndroids.Utils", "IsAndroid", new[] { typeof(Pawn) }) : null;
    private static bool warned;

    private static MethodInfo OptionalMethod(string owner, string name, Type[] parameters)
    {
        Type type = AccessTools.TypeByName(owner);
        return type == null ? null : AccessTools.Method(type, name, parameters);
    }

    internal static bool IsBiological(Pawn pawn)
    {
        if (pawn == null || !pawn.RaceProps.IsFlesh) return false;
        if (!AndroidsActive) return true;
        try
        {
            if (AndroidCheck != null) return !(bool)AndroidCheck.Invoke(null, new object[] { pawn });
        }
        catch (Exception error) { Warn(error); }
        // Conservative fallback for a changed Android API. This is its actual
        // synthetic-body gene, not a race-name or xenotype-name heuristic.
        return pawn.genes?.GenesListForReading.Any(g => g.def.defName == "VREA_SyntheticBody") != true;
    }

    private static T Field<T>(Def def, string name, T fallback = default(T))
        => def != null && AccessTools.Field(def.GetType(), name)?.GetValue(def) is T value ? value : fallback;

    private static Def PassionDef(Passion passion)
        => PassionToDef?.Invoke(null, new object[] { passion }) as Def;

    internal static bool CanGainMajor(Pawn pawn, SkillRecord skill)
    {
        if (skill.TotallyDisabled || skill.passion == Passion.Major) return false;
        if (pawn.story?.traits?.allTraits.Any(t => !t.Suppressed && t.def.ConflictsWithPassion(skill.def)) == true
            || pawn.genes?.GenesListForReading.Any(g => g.Active && g.def.passionMod?.skill == skill.def
                && g.def.passionMod.modType == PassionMod.PassionModType.DropAll) == true) return false;
        if (!SkillsActive) return skill.passion == Passion.None || skill.passion == Passion.Minor;
        // A broken/changed optional API must never turn an unknown passion into
        // an upgrade candidate or leave VSE's learning cache stale.
        if (PassionToDef == null || ClearLearningCache == null) return false;
        try
        {
            Def current = PassionDef(skill.passion);
            Def major = PassionDef(Passion.Major);
            if (current == null || major == null || Field(current, "isTriggered", false)
                || Field<HediffDef>(current, "hediffToAdd") != null
                || Field(current, "learnRateFactorOther", 1f) != 1f) return false;
            // Natural and Critical both learn faster than Major in VSE. Keep
            // those and any equivalent stronger custom passion intact.
            if (Field(current, "learnRateFactor", float.MaxValue) >= Field(major, "learnRateFactor", 0f)) return false;
            return AllowsMajor(major, pawn, skill);
        }
        catch (Exception error) { Warn(error); return false; }
    }

    private static bool AllowsMajor(Def def, Pawn pawn, SkillRecord skill)
    {
        SkillDef onlySkill = Field<SkillDef>(def, "onlyForSkill");
        if (onlySkill != null && onlySkill != skill.def || Field(def, "isTriggered", false)) return false;
        int minAge = Field(def, "minAge", -1), maxAge = Field(def, "maxAge", -1);
        int age = pawn.ageTracker.AgeBiologicalYears;
        if (minAge != -1 && age <= minAge || maxAge != -1 && age >= maxAge) return false;
        bool HasTrait(TraitDef trait) => pawn.story?.traits?.HasTrait(trait) == true;
        bool HasGene(GeneDef gene) => pawn.genes?.GenesListForReading.Any(g => g.def == gene) == true;
        bool HasPrecept(PreceptDef precept) => pawn.Faction?.ideos?.PrimaryIdeo?.PreceptsListForReading.Any(p => p.def == precept) == true;
        return !Field(def, "blockingTraits", new List<TraitDef>()).Any(HasTrait)
            && Field(def, "requiredTraits", new List<TraitDef>()).All(HasTrait)
            && !Field(def, "blockingGenes", new List<GeneDef>()).Any(HasGene)
            && Field(def, "requiredGenes", new List<GeneDef>()).All(HasGene)
            && !Field(def, "blockingPrecepts", new List<PreceptDef>()).Any(HasPrecept)
            && Field(def, "requiredPrecepts", new List<PreceptDef>()).All(HasPrecept)
            && !Field(def, "blockingTraitsWithDegree", new List<TraitRequirement>()).Any(t => t.HasTrait(pawn))
            && Field(def, "requiredTraitsWithDegree", new List<TraitRequirement>()).All(t => t.HasTrait(pawn));
    }

    internal static bool PrefersTreatment(SkillRecord skill)
    {
        if (!SkillsActive || PassionToDef == null) return skill.passion == Passion.None;
        try { return Field(PassionDef(skill.passion), "isBad", false); }
        catch (Exception error) { Warn(error); return skill.passion == Passion.None; }
    }

    internal static string PassionLabel(SkillRecord skill)
    {
        if (SkillsActive && PassionToDef != null)
            try
            {
                Def def = PassionDef(skill.passion);
                if (def != null) return def.LabelCap;
            }
            catch (Exception error) { Warn(error); }
        return (skill.passion == Passion.Minor ? "SE_Neuro_PassionMinor" : "SE_Neuro_PassionNone").Translate();
    }

    internal static bool TryGrantMajor(Pawn pawn, SkillRecord skill)
    {
        if (!CanGainMajor(pawn, skill)) return false;
        Passion previous = skill.passion;
        try
        {
            skill.passion = Passion.Major;
            if (SkillsActive)
            {
                ClearLearningCache.Invoke(null, null);
                HediffDef hediff = Field<HediffDef>(PassionDef(Passion.Major), "hediffToAdd");
                if (hediff != null && !pawn.health.hediffSet.HasHediff(hediff)) pawn.health.AddHediff(hediff);
            }
            pawn.needs?.mood?.thoughts?.situational?.Notify_SituationalThoughtsDirty();
            return true;
        }
        catch (Exception error)
        {
            skill.passion = previous;
            if (SkillsActive)
                try { ClearLearningCache?.Invoke(null, null); }
                catch (Exception cacheError) { Warn(cacheError); }
            Warn(error);
            return false;
        }
    }

    private static void Warn(Exception error)
    {
        if (warned) return;
        warned = true;
        Log.Warning("[Serums Expended] Optional Vanilla Expanded integration could not complete; custom passions are protected. " + error);
    }
}
