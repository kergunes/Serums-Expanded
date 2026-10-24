using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace SerumsExpended;

internal static class SerumComa
{
    // One regenerative or neuroplastic coma at a time.
    internal static bool IsInComa(Pawn pawn)
        => pawn.health.hediffSet.hediffs.Any(hediff =>
            hediff.TryGetComp<RegenesisTimer>() != null || hediff.TryGetComp<NeuroplasticTimer>() != null);
}

public sealed class NeuroplasticTimerProperties : HediffCompProperties_Disappears
{
    public bool chooseSkill;
    public NeuroplasticTimerProperties() { compClass = typeof(NeuroplasticTimer); }
}

// Reuses the vanilla saved countdown; resolves only natural expiry, like Regenesis.
public sealed class NeuroplasticTimer : HediffComp_Disappears
{
    private bool completed;
    internal SkillDef chosenSkill;
    private NeuroplasticTimerProperties Settings => (NeuroplasticTimerProperties)props;

    public override void CompExposeData()
    {
        base.CompExposeData();
        Scribe_Values.Look(ref completed, "neuroCompleted", false);
        Scribe_Defs.Look(ref chosenSkill, "neuroChosenSkill");
    }

    public override void CompPostTick(ref float severityAdjustment)
    {
        base.CompPostTick(ref severityAdjustment);
        if (ticksToDisappear > 0 || completed) return;
        completed = true;
        if (Pawn.Dead || Pawn.Destroyed || Pawn.skills == null || !VanillaExpandedCompatibility.IsBiological(Pawn)) return;
        SkillRecord skill = Settings.chooseSkill ? ChosenRecord() : RandomRecord();
        bool notify = Pawn.Faction == Faction.OfPlayer;
        if (skill == null || !VanillaExpandedCompatibility.TryGrantMajor(Pawn, skill))
        {
            if (notify)
                Messages.Message("SE_Neuro_Wasted".Translate(Pawn.Named("PAWN")), Pawn,
                    MessageTypeDefOf.NeutralEvent);
            return;
        }
        if (notify)
            Messages.Message("SE_Neuro_Gained".Translate(Pawn.Named("PAWN"), skill.def.skillLabel),
                Pawn, MessageTypeDefOf.PositiveEvent);
    }

    private SkillRecord RandomRecord()
    {
        // Prefer no passion (or VSE Apathy); then other eligible lesser passions.
        List<SkillRecord> eligible = Neuroplastic.Eligible(Pawn).ToList();
        List<SkillRecord> none = eligible.Where(VanillaExpandedCompatibility.PrefersTreatment).ToList();
        return (none.Count > 0 ? none : eligible).TryRandomElement(out SkillRecord skill) ? skill : null;
    }

    private SkillRecord ChosenRecord()
        => Neuroplastic.Eligible(Pawn).FirstOrDefault(skill => skill.def == chosenSkill);
}

internal static class Neuroplastic
{
    internal static IEnumerable<SkillRecord> Eligible(Pawn pawn)
        => pawn.skills == null
            ? Enumerable.Empty<SkillRecord>()
            : pawn.skills.skills.Where(skill => VanillaExpandedCompatibility.CanGainMajor(pawn, skill));
}

// Remembers the skill picked in the float menu until the use job applies the serum.
public sealed class NeuroplasticChoices : GameComponent
{
    private Dictionary<Pawn, SkillDef> pending = new Dictionary<Pawn, SkillDef>();

    public NeuroplasticChoices(Game game) { }

    internal static NeuroplasticChoices Current => Verse.Current.Game?.GetComponent<NeuroplasticChoices>();

    internal void Set(Pawn pawn, SkillDef skill) => pending[pawn] = skill;

    internal SkillDef Take(Pawn pawn)
    {
        if (!pending.TryGetValue(pawn, out SkillDef skill)) return null;
        pending.Remove(pawn);
        return skill;
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref pending, "neuroPending", LookMode.Reference, LookMode.Def);
        if (pending == null) pending = new Dictionary<Pawn, SkillDef>();
        pending.RemoveAll(entry => entry.Key == null || entry.Value == null);
    }
}

public sealed class CompProperties_UseNeuroplastic : CompProperties_UseEffectAddHediff
{
    public bool chooseSkill;
    public CompProperties_UseNeuroplastic() { compClass = typeof(CompUseEffect_Neuroplastic); }
}

public sealed class CompUseEffect_Neuroplastic : CompUseEffect_AddHediff
{
    private new CompProperties_UseNeuroplastic Props => (CompProperties_UseNeuroplastic)props;

    public override AcceptanceReport CanBeUsedBy(Pawn pawn)
    {
        if (pawn.Dead || pawn.skills == null || !VanillaExpandedCompatibility.IsBiological(pawn))
            return "SE_Neuro_InvalidPatient".Translate();
        if (SerumComa.IsInComa(pawn)) return "SE_Neuro_AlreadyInComa".Translate();
        // The basic serum may be wasted on purpose; the advanced serum needs a choice.
        if (Props.chooseSkill && !Neuroplastic.Eligible(pawn).Any())
            return "SE_Neuro_NoEligible".Translate();
        return base.CanBeUsedBy(pawn);
    }

    public override bool SelectedUseOption(Pawn pawn)
    {
        if (!Props.chooseSkill) return false;
        List<FloatMenuOption> options = new List<FloatMenuOption>();
        foreach (SkillRecord skill in Neuroplastic.Eligible(pawn))
        {
            SkillDef def = skill.def;
            string passion = VanillaExpandedCompatibility.PassionLabel(skill);
            options.Add(new FloatMenuOption(
                "SE_Neuro_SkillOption".Translate(def.skillLabel.CapitalizeFirst(), skill.Level, passion),
                () =>
                {
                    NeuroplasticChoices.Current?.Set(pawn, def);
                    CompUsable usable = parent.GetComp<CompUsable>();
                    usable?.TryStartUseJob(pawn, usable.GetExtraTarget(pawn), usable.Props.ignoreOtherReservations);
                }));
        }
        if (options.Count == 0) return false;
        Find.WindowStack.Add(new FloatMenu(options));
        return true;
    }

    public override void DoEffect(Pawn user)
    {
        base.DoEffect(user);
        if (!Props.chooseSkill) return;
        NeuroplasticTimer timer = user.health.hediffSet.GetFirstHediffOfDef(Props.hediffDef)
            ?.TryGetComp<NeuroplasticTimer>();
        if (timer == null) return;
        // A lost pending choice (for example a job queued before reloading) falls back to a random skill.
        timer.chosenSkill = NeuroplasticChoices.Current?.Take(user)
            ?? Neuroplastic.Eligible(user).RandomElementWithFallback()?.def;
    }
}
