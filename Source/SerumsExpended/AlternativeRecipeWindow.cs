using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SerumsExpended;

// Keep the vanilla controls, adding a self-contained explanation beneath them.
[HarmonyPatch(typeof(Bill_Production), "GetBillDialog")]
internal static class AdaptiveBillDialogPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Bill_Production __instance, ref Window __result)
    {
        // Leave another mod's specialized bill dialog intact. Alternatives also
        // remain available in the serum information card in that case.
        if (__result?.GetType() == typeof(Dialog_BillConfig)
            && __instance.recipe.GetModExtension<AdaptiveSerumRecipe>() != null
            && __instance.billStack?.billGiver is Thing giver)
            __result = new Dialog_AdaptiveBillConfig(__instance, giver.Position);
    }
}

public sealed class Dialog_AdaptiveBillConfig : Dialog_BillConfig
{
    private int index;
    private float nextSwitch;
    private const float PanelHeight = 170f;
    public override Vector2 InitialSize => base.InitialSize
        + new Vector2(0f, PanelHeight + 8f + CloseButSize.y);

    public Dialog_AdaptiveBillConfig(Bill_Production bill, IntVec3 position) : base(bill, position) { }

    public override void DoWindowContents(Rect inRect)
    {
        Rect vanilla = new Rect(inRect.x, inRect.y, inRect.width,
            inRect.height - PanelHeight - 8f - CloseButSize.y);
        base.DoWindowContents(vanilla);
        AdaptiveSerumRecipe extension = bill.recipe.GetModExtension<AdaptiveSerumRecipe>();
        if (extension == null || extension.routes.Count == 0) return;
        Rect panel = new Rect(inRect.x, vanilla.yMax + 8f, inRect.width, PanelHeight - 6f);
        Widgets.DrawMenuSection(panel);
        Rect inner = panel.ContractedBy(10f);
        bool hovering = Mouse.IsOver(panel);
        float now = Time.realtimeSinceStartup;
        if (hovering || nextSwitch == 0f) nextSwitch = now + 3f;
        else if (now >= nextSwitch)
        {
            index = (index + 1) % extension.routes.Count;
            nextSwitch = now + 3f;
        }
        index %= extension.routes.Count;
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 195f, 26f),
            "SE_UI_Alternatives".Translate(index + 1, extension.routes.Count));
        if (Widgets.ButtonText(new Rect(inner.xMax - 190f, inner.y, 34f, 25f), ">"))
        {
            index = (index + 1) % extension.routes.Count;
            nextSwitch = now + 3f;
        }
        if (Widgets.ButtonText(new Rect(inner.xMax - 150f, inner.y, 150f, 25f), "SE_UI_ShowAll".Translate()))
            Find.WindowStack.Add(new Dialog_SerumAlternatives(extension, bill.LabelCap));
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 28f, inner.width, 30f), "SE_UI_Priority".Translate());
        Text.Font = GameFont.Small;
        AlternativeRows.Draw(new Rect(inner.x, inner.y + 60f, inner.width, 84f), extension.routes[index]);
    }
}

internal static class AlternativeRows
{
    internal static string Name(SerumRoute route)
    {
        ThingDef serum = route.ingredients.Select(input => input.thingDef)
            .FirstOrDefault(def => def.GetCompProperties<CompProperties_Serum>() != null);
        return serum == null ? "SE_UI_FromRaw".Translate()
            : "SE_UI_FromSerum".Translate(serum.LabelCap);
    }

    internal static void Draw(Rect rect, SerumRoute route)
    {
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(rect.x, rect.y, rect.width - 150f, 25f), Name(route));
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(new Rect(rect.xMax - 150f, rect.y, 150f, 25f),
            "WorkAmount".Translate() + ": " + route.work.ToStringWorkAmount());
        Text.Anchor = TextAnchor.UpperLeft;
        float x = rect.x;
        foreach (ThingDefCountClass input in route.ingredients)
        {
            Rect icon = new Rect(x, rect.y + 30f, 36f, 36f);
            Widgets.DefIcon(icon, input.thingDef);
            Widgets.Label(new Rect(x + 37f, icon.y + 8f, 34f, 26f), "×" + input.count);
            TooltipHandler.TipRegion(new Rect(x, icon.y, 72f, 38f),
                input.thingDef.LabelCap + " ×" + input.count + "\n\n" + input.thingDef.description);
            x += 76f;
            if (input != route.ingredients.Last())
            {
                Widgets.Label(new Rect(x, icon.y + 8f, 16f, 26f), "+");
                x += 19f;
            }
        }
    }
}

public sealed class Dialog_SerumAlternatives : Window
{
    private readonly AdaptiveSerumRecipe extension;
    private readonly string title;
    private Vector2 scroll;
    public override Vector2 InitialSize => new Vector2(740f, 500f);

    public Dialog_SerumAlternatives(AdaptiveSerumRecipe extension, string title)
    {
        this.extension = extension;
        this.title = title;
        forcePause = true;
        doCloseX = true;
        doCloseButton = true;
        absorbInputAroundWindow = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width - 30f, 36f), title);
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0f, 42f, inRect.width, 50f), "SE_UI_Priority".Translate());
        Rect viewport = new Rect(0f, 98f, inRect.width, inRect.height - 148f);
        Rect content = new Rect(0f, 0f, viewport.width - 18f, extension.routes.Count * 104f);
        Widgets.BeginScrollView(viewport, ref scroll, content);
        try
        {
            for (int i = 0; i < extension.routes.Count; i++)
            {
                Rect row = new Rect(0f, i * 104f, content.width, 96f);
                Widgets.DrawMenuSection(row);
                AlternativeRows.Draw(row.ContractedBy(8f), extension.routes[i]);
            }
        }
        finally { Widgets.EndScrollView(); }
    }
}
