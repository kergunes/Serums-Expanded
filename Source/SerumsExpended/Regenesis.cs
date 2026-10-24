using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace SerumsExpended;

public sealed class RegenesisTimerProperties : HediffCompProperties_Disappears
{
    public bool restoreMissingParts;
    public RegenesisTimerProperties() { compClass = typeof(RegenesisTimer); }
}

// Reuse the vanilla saved countdown; resolve only natural expiry, never early
// removal, death, resurrection or unloading the map. No regeneration Harmony.
public sealed class RegenesisTimer : HediffComp_Disappears
{
    private bool completed;
    private RegenesisTimerProperties Settings => (RegenesisTimerProperties)props;

    public override void CompExposeData()
    {
        base.CompExposeData();
        Scribe_Values.Look(ref completed, "regenesisCompleted", false);
    }

    public override void CompPostTick(ref float severityAdjustment)
    {
        base.CompPostTick(ref severityAdjustment);
        if (ticksToDisappear > 0 || completed) return;
        completed = true;
        if (Pawn.Dead || Pawn.Destroyed || !VanillaExpandedCompatibility.IsBiological(Pawn)) return;
        if (Settings.restoreMissingParts && RestoreMissingPart()) return;
        HealPermanentInjury();
    }

    private void HealPermanentInjury()
    {
        List<Hediff_Injury> injuries = Pawn.health.hediffSet.hediffs.OfType<Hediff_Injury>()
            .Where(injury => injury.Part != null && injury.IsPermanent()
                && Pawn.RaceProps.body.AllParts.Contains(injury.Part)
                && !Pawn.health.hediffSet.PartIsMissing(injury.Part)
                && !Pawn.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(injury.Part))
            .ToList();
        if (injuries.TryRandomElement(out Hediff_Injury injury)) Pawn.health.RemoveHediff(injury);
    }

    private bool RestoreMissingPart()
    {
        List<Hediff_MissingPart> candidates = Pawn.health.hediffSet.GetMissingPartsCommonAncestors()
            .Where(missing => CanRestore(missing.Part)).ToList();
        if (!candidates.TryRandomElement(out Hediff_MissingPart chosen)) return false;
        // Vanilla restoration reconstructs descendants and refreshes health and
        // rendering caches, unlike removing a MissingPart Hediff by hand.
        Pawn.health.RestorePart(chosen.Part);
        return true;
    }

    private bool CanRestore(BodyPartRecord part)
    {
        if (part == null || part.parent == null || part == Pawn.RaceProps.body.corePart
            || !Pawn.RaceProps.body.AllParts.Contains(part)
            || part.def == BodyPartDefOf.Head || part.def.defName == "Brain"
            || part.def.defName == "Torso"
            || Pawn.health.hediffSet.PartIsMissing(part.parent)
            || Pawn.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(part)) return false;
        HashSet<BodyPartRecord> subtree = new HashSet<BodyPartRecord>(part.GetPartAndAllChildParts());
        if (subtree.Any(child => child.def.defName == "Brain"
            || child.def.tags?.Any(tag => tag.defName == "ConsciousnessSource") == true)) return false;
        // RestorePart clears the restored subtree. Exclude a root if that would
        // overwrite an implant/prosthetic or erase an unrelated health condition.
        return !Pawn.health.hediffSet.hediffs.Any(hediff => hediff.Part != null
            && subtree.Contains(hediff.Part)
            && (hediff is Hediff_AddedPart || hediff.def.countsAsAddedPartOrImplant
                || (!(hediff is Hediff_MissingPart) && !(hediff is Hediff_Injury))));
    }
}

public sealed class CompProperties_UseRegenesis : CompProperties_UseEffectAddHediff
{
    public CompProperties_UseRegenesis() { compClass = typeof(CompUseEffect_Regenesis); }
}

public sealed class CompUseEffect_Regenesis : CompUseEffect_AddHediff
{
    public override AcceptanceReport CanBeUsedBy(Pawn pawn)
    {
        if (pawn.Dead || !VanillaExpandedCompatibility.IsBiological(pawn)) return "SE_Regenesis_InvalidPatient".Translate();
        if (SerumComa.IsInComa(pawn))
            return "SE_Regenesis_AlreadyInComa".Translate();
        return base.CanBeUsedBy(pawn);
    }
}
