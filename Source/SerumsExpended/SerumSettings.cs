using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SerumsExpended;

public sealed class SerumSettings : ModSettings
{
    public Dictionary<string, float> values = new Dictionary<string, float>();
    public override void ExposeData()
    {
        Scribe_Collections.Look(ref values, "serumValues", LookMode.Value, LookMode.Value);
        if (values == null) values = new Dictionary<string, float>();
    }
}

public sealed class SerumsExpendedMod : Mod
{
    internal readonly SerumSettings Settings;
    private Vector2 scroll;
    private string search = "";
    internal static readonly Color Accent = new Color(.65f, .46f, .92f);
    public SerumsExpendedMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<SerumSettings>();
    }
    public override string SettingsCategory() => "Serums Expanded";

    public override void DoSettingsWindowContents(Rect rect)
    {
        SerumSettingsCatalog.EnsureReady();
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(rect.x, rect.y, rect.width, 35f), "SE_Settings_Title".Translate());
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(rect.x, rect.y + 40f, rect.width, 45f), "SE_Settings_Intro".Translate());
        search = Widgets.TextField(new Rect(rect.x, rect.y + 90f, rect.width - 190f, 32f), search);
        TooltipHandler.TipRegion(new Rect(rect.x, rect.y + 90f, rect.width - 190f, 32f), "SE_Settings_Search".Translate());
        if (Widgets.ButtonText(new Rect(rect.xMax - 180f, rect.y + 90f, 180f, 32f), "SE_Settings_ResetAll".Translate()))
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("SE_Settings_ResetConfirm".Translate(), () =>
            {
                Settings.values.Clear();
                SerumSettingsCatalog.Apply(Settings);
                WriteSettings();
            }));

        List<SerumProfile> profiles = SerumSettingsCatalog.Profiles.Where(p =>
            p.Item.label.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        Rect viewport = new Rect(rect.x, rect.y + 136f, rect.width, Math.Max(0f, rect.height - 136f));
        int columns = rect.width >= 700f ? 3 : 2;
        float width = (viewport.width - 20f - (columns - 1) * 12f) / columns;
        Rect view = new Rect(0f, 0f, viewport.width - 20f, Mathf.CeilToInt(profiles.Count / (float)columns) * 136f);
        Widgets.BeginScrollView(viewport, ref scroll, view);
        for (int i = 0; i < profiles.Count; i++)
        {
            SerumProfile profile = profiles[i];
            Rect card = new Rect((i % columns) * (width + 12f), (i / columns) * 136f, width, 124f);
            Widgets.DrawBoxSolid(card, new Color(.105f, .12f, .15f));
            Widgets.DrawBoxSolid(new Rect(card.x, card.y, 4f, card.height), Accent);
            Widgets.DrawHighlightIfMouseover(card);
            Widgets.DefIcon(new Rect(card.x + 14f, card.y + 12f, 48f, 48f), profile.Item);
            Widgets.Label(new Rect(card.x + 72f, card.y + 12f, width - 82f, 55f), profile.Item.LabelCap);
            Text.Font = GameFont.Tiny;
            GUI.color = profile.Fields.Any(f => Settings.values.ContainsKey(f.Key)) ? Accent : Color.gray;
            Widgets.Label(new Rect(card.x + 14f, card.y + 76f, width - 24f, 40f),
                (profile.Fields.Any(f => Settings.values.ContainsKey(f.Key)) ? "SE_Settings_Custom" : "SE_Settings_Default").Translate()
                + "  ·  " + "SE_Settings_FieldCount".Translate(profile.Fields.Count));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (Widgets.ButtonInvisible(card)) Find.WindowStack.Add(new Dialog_SerumTuning(this, profile));
        }
        Widgets.EndScrollView();
        if (profiles.Count == 0) Widgets.Label(viewport.ContractedBy(12f), "SE_Settings_NoResults".Translate());
    }
}

internal sealed class SerumField
{
    internal string Key, Label, Help, Unit;
    internal float Default, Min, Max;
    internal bool Toggle, SideEffect;
    internal Action<float> Set;
    internal float Clamp(float value) => float.IsNaN(value) || float.IsInfinity(value) ? Default : Mathf.Clamp(value, Min, Max);
}

internal sealed class SerumProfile
{
    internal ThingDef Item;
    internal readonly List<SerumField> Fields = new List<SerumField>();
    internal void Add(string key, string label, float initial, float min, float max,
        Action<float> setter, string unit = "", bool sideEffect = false, bool toggle = false, string help = "")
    {
        Fields.Add(new SerumField { Key = Item.defName + "/" + key, Label = label, Default = initial,
            Min = min, Max = max, Set = setter, Unit = unit, SideEffect = sideEffect, Toggle = toggle, Help = help });
    }
}

// Capture XML defaults once, after all Def patches, before applying saved overrides.
[StaticConstructorOnStartup]
internal static class SerumSettingsCatalog
{
    internal static readonly List<SerumProfile> Profiles = new List<SerumProfile>();
    static SerumSettingsCatalog()
    {
        foreach (ThingDef item in DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => !d.IsCorpse && d.GetCompProperties<CompProperties_SerumInformation>() != null)
            .OrderBy(d => d.defName))
        {
            HediffDef effect = item.GetCompProperties<CompProperties_UseEffectAddHediff>()?.hediffDef;
            if (effect?.stages == null || effect.stages.Count == 0) continue;
            SerumProfile profile = new SerumProfile { Item = item };
            HediffStage stage = effect.stages[0];
            HediffCompProperties_Disappears timer = effect.CompProps<HediffCompProperties_Disappears>();
            if (timer != null)
                profile.Add("duration", "SE_Settings_Duration", timer.disappearsAfterTicks.min / 60000f, .01f, 120f,
                    v => timer.disappearsAfterTicks = new IntRange(Mathf.RoundToInt(v * 60000f)), "SE_Settings_Days",
                    help: "SE_Settings_DurationHelp");
            if (stage.statFactors != null)
                foreach (StatModifier modifier in stage.statFactors)
                {
                    StatModifier m = modifier;
                    bool lowerIsBetter = m.stat.defName == "IncomingDamageFactor" || m.stat.defName == "AimingDelayFactor";
                    profile.Add("factor/" + m.stat.defName, m.stat.LabelCap, m.value, 0f, 10f, v => m.value = v,
                        "SE_Settings_Multiplier", lowerIsBetter ? m.value > 1f : m.value < 1f, help: m.stat.description);
                }
            if (stage.statOffsets != null)
                foreach (StatModifier modifier in stage.statOffsets)
                {
                    StatModifier m = modifier;
                    profile.Add("offset/" + m.stat.defName, m.stat.LabelCap, m.value, -10f, 10f, v => m.value = v,
                        "SE_Settings_Offset", m.value < 0f, help: m.stat.description);
                }
            if (stage.damageFactors != null)
                foreach (DamageFactor modifier in stage.damageFactors)
                {
                    DamageFactor m = modifier;
                    profile.Add("damage/" + m.damageDef.defName, "SE_Settings_Damage".Translate(m.damageDef.LabelCap),
                        m.factor, 0f, 10f, v => m.factor = v, "SE_Settings_Multiplier", m.factor > 1f,
                        help: "SE_Settings_DamageHelp");
                }
            if (stage.capMods != null)
                foreach (PawnCapacityModifier modifier in stage.capMods)
                {
                    PawnCapacityModifier m = modifier;
                    if (m.offset != 0f)
                        profile.Add("capacityOffset/" + m.capacity.defName, m.capacity.GetLabelFor().CapitalizeFirst(),
                            m.offset * 100f, -99f, 100f, v => m.offset = v / 100f,
                            "SE_Settings_Points", m.offset < 0f, help: m.capacity.description);
                    if (m.SetMaxDefined)
                        profile.Add("capacityMax/" + m.capacity.defName, "SE_Settings_Cap".Translate(m.capacity.GetLabelFor()),
                            m.setMax * 100f, 1f, 100f, v => m.setMax = v / 100f,
                            "SE_Settings_Percent", true, help: "SE_Settings_CapHelp");
                }
            ThoughtDef thought = DefDatabase<ThoughtDef>.GetNamedSilentFail(effect.defName);
            if (thought?.stages != null && thought.stages.Count > 0)
                profile.Add("mood", "SE_Info_Mood", thought.stages[0].baseMoodEffect, -100f, 100f,
                    v => thought.stages[0].baseMoodEffect = v, "SE_Settings_Points", thought.stages[0].baseMoodEffect < 0f);
            if (stage.blocksMentalBreaks)
                profile.Add("mentalBreaks", "SE_Info_MentalBreaks", 1f, 0f, 1f,
                    v => stage.blocksMentalBreaks = v >= .5f, toggle: true);
            if (stage.blocksInspirations)
                profile.Add("inspirations", "SE_Info_Inspirations", 1f, 0f, 1f,
                    v => stage.blocksInspirations = v >= .5f, sideEffect: true, toggle: true);
            if (stage.pctAllThoughtNullification > 0f)
                profile.Add("thoughtSuppression", "SE_Info_ThoughtSuppression", stage.pctAllThoughtNullification * 100f,
                    0f, 100f, v => stage.pctAllThoughtNullification = v / 100f, "SE_Settings_Percent");
            if (stage.overrideMoodBase >= 0f)
                profile.Add("moodBase", "SE_Info_MoodBase", stage.overrideMoodBase * 100f,
                    0f, 100f, v => stage.overrideMoodBase = v / 100f, "SE_Settings_Percent");
            Profiles.Add(profile);
        }
        Apply(LoadedModManager.GetMod<SerumsExpendedMod>().Settings);
    }
    internal static void EnsureReady() { }
    internal static void Apply(SerumSettings settings)
    {
        foreach (SerumField field in Profiles.SelectMany(p => p.Fields))
            field.Set(settings.values.TryGetValue(field.Key, out float value) ? field.Clamp(value) : field.Default);
        // Recalculate capacity-driven health immediately; existing duration counters are untouched.
        if (Current.Game != null)
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.ToList())
            {
                if (pawn.health == null || !pawn.health.hediffSet.hediffs.Any(h => Profiles.Any(p =>
                    p.Item.GetCompProperties<CompProperties_UseEffectAddHediff>()?.hediffDef == h.def))) continue;
                pawn.health.hediffSet.DirtyCache();
                pawn.needs?.mood?.thoughts?.situational?.Notify_SituationalThoughtsDirty();
            }
    }
}

internal sealed class Dialog_SerumTuning : Window
{
    private readonly SerumsExpendedMod owner;
    private readonly SerumProfile profile;
    private readonly Dictionary<string, float> draft = new Dictionary<string, float>();
    private readonly Dictionary<string, string> buffers = new Dictionary<string, string>();
    private Vector2 scroll;
    public override Vector2 InitialSize => new Vector2(900f, 760f);
    public Dialog_SerumTuning(SerumsExpendedMod owner, SerumProfile profile)
    {
        this.owner = owner;
        this.profile = profile;
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
        foreach (SerumField field in profile.Fields)
            draft[field.Key] = owner.Settings.values.TryGetValue(field.Key, out float value) ? field.Clamp(value) : field.Default;
    }
    public override void DoWindowContents(Rect rect)
    {
        Widgets.DrawBoxSolid(new Rect(0f, 0f, rect.width, 3f), SerumsExpendedMod.Accent);
        Widgets.DefIcon(new Rect(8f, 14f, 68f, 68f), profile.Item);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(90f, 16f, rect.width - 135f, 55f), profile.Item.LabelCap);
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(90f, 72f, rect.width - 110f, 28f), "SE_Settings_Draft".Translate());
        Text.Font = GameFont.Small;
        string description = profile.Item.description;
        float descriptionHeight = Text.CalcHeight(description, rect.width - 32f);
        float contentHeight = descriptionHeight + 24f + profile.Fields.Count * 94f + 2f * 42f;
        Rect viewport = new Rect(0f, 110f, rect.width, Math.Max(0f, rect.height - 214f));
        Rect view = new Rect(0f, 0f, rect.width - 20f, contentHeight);
        Widgets.BeginScrollView(viewport, ref scroll, view);
        GUI.color = new Color(.75f, .78f, .82f);
        Widgets.Label(new Rect(8f, 0f, view.width - 16f, descriptionHeight), description);
        GUI.color = Color.white;
        float y = descriptionHeight + 24f;
        foreach (bool sideEffect in new[] { false, true })
        {
            GUI.color = sideEffect ? new Color(1f, .65f, .52f) : new Color(.6f, .85f, .75f);
            Widgets.Label(new Rect(8f, y, view.width, 32f),
                (sideEffect ? "SE_Settings_SideEffects" : "SE_Settings_Effects").Translate());
            GUI.color = Color.white;
            y += 42f;
            foreach (SerumField field in profile.Fields.Where(f => f.SideEffect == sideEffect))
            {
                DrawField(new Rect(0f, y, view.width, 84f), field);
                y += 94f;
            }
        }
        Widgets.EndScrollView();
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, rect.height - 95f, rect.width, 42f), "SE_Settings_ApplyHelp".Translate());
        Text.Font = GameFont.Small;
        float buttonWidth = (rect.width - 24f) / 3f;
        if (Widgets.ButtonText(new Rect(0f, rect.height - 42f, buttonWidth, 38f), "SE_Settings_Reset".Translate()))
        {
            foreach (SerumField field in profile.Fields) draft[field.Key] = field.Default;
            buffers.Clear();
        }
        if (Widgets.ButtonText(new Rect(buttonWidth + 12f, rect.height - 42f, buttonWidth, 38f), "CancelButton".Translate())) Close();
        if (Widgets.ButtonText(new Rect(2f * (buttonWidth + 12f), rect.height - 42f, buttonWidth, 38f), "SE_Settings_Apply".Translate()))
        {
            // Reject incomplete numeric input instead of silently applying an older value.
            foreach (SerumField field in profile.Fields.Where(f => !f.Toggle))
                if (buffers.TryGetValue(field.Key, out string input)
                    && (!float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                        || float.IsNaN(number) || float.IsInfinity(number) || number < field.Min || number > field.Max))
                {
                    Messages.Message("SE_Settings_Invalid".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }
            foreach (SerumField field in profile.Fields)
            {
                float value = field.Clamp(draft[field.Key]);
                if (Mathf.Approximately(value, field.Default)) owner.Settings.values.Remove(field.Key);
                else owner.Settings.values[field.Key] = value;
            }
            SerumSettingsCatalog.Apply(owner.Settings);
            owner.WriteSettings();
            Close();
        }
    }
    private void DrawField(Rect rect, SerumField field)
    {
        Widgets.DrawBoxSolid(rect, new Color(.105f, .12f, .15f));
        string label = field.Label.StartsWith("SE_") ? field.Label.Translate().ToString() : field.Label;
        Widgets.Label(new Rect(rect.x + 12f, rect.y + 7f, rect.width - 180f, 28f), label);
        string help = field.Help.StartsWith("SE_") ? field.Help.Translate().ToString() : field.Help;
        if (!field.Toggle) help += "\n\n" + field.Min.ToString("0.####", CultureInfo.InvariantCulture)
            + " … " + field.Max.ToString("0.####", CultureInfo.InvariantCulture) + " " + field.Unit.Translate();
        TooltipHandler.TipRegion(rect, help);
        float value = draft[field.Key];
        if (field.Toggle)
        {
            bool enabled = value >= .5f;
            Widgets.CheckboxLabeled(new Rect(rect.x + 12f, rect.y + 42f, rect.width - 24f, 28f),
                (enabled ? "Yes" : "No").Translate(), ref enabled);
            draft[field.Key] = enabled ? 1f : 0f;
            return;
        }
        string input = buffers.TryGetValue(field.Key, out string stored) ? stored : value.ToString("0.####", CultureInfo.InvariantCulture);
        string edited = Widgets.TextField(new Rect(rect.xMax - 154f, rect.y + 7f, 100f, 28f), input);
        buffers[field.Key] = edited;
        bool valid = float.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && !float.IsNaN(parsed) && !float.IsInfinity(parsed) && parsed >= field.Min && parsed <= field.Max;
        if (valid) draft[field.Key] = value = parsed;
        else Widgets.DrawBoxSolid(new Rect(rect.xMax - 154f, rect.y + 36f, 100f, 2f), Color.red);
        if (Widgets.ButtonText(new Rect(rect.xMax - 46f, rect.y + 7f, 34f, 28f), "↺"))
        {
            draft[field.Key] = value = field.Default;
            buffers.Remove(field.Key);
        }
        float slider = Widgets.HorizontalSlider(new Rect(rect.x + 12f, rect.y + 43f, rect.width - 190f, 24f), value, field.Min, field.Max);
        if (!Mathf.Approximately(slider, value))
        {
            draft[field.Key] = (float)Math.Round(slider, 3);
            buffers.Remove(field.Key);
        }
        Text.Font = GameFont.Tiny;
        GUI.color = Color.gray;
        Widgets.Label(new Rect(rect.xMax - 170f, rect.y + 44f, 160f, 32f),
            field.Unit.Translate() + " · " + "SE_Settings_Base".Translate(field.Default.ToString("0.####", CultureInfo.InvariantCulture)));
        GUI.color = Color.white;
        Text.Font = GameFont.Small;
    }
}
