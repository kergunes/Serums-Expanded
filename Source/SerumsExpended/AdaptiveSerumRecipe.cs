using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace SerumsExpended;

// XML order is production priority: refined dose, vanilla dose, then raw inputs.
public sealed class AdaptiveSerumRecipe : DefModExtension
{
    public List<SerumRoute> routes = new List<SerumRoute>();
}

public sealed class SerumRoute
{
    public float work;
    public List<ThingDefCountClass> ingredients = new List<ThingDefCountClass>();
    private List<IngredientCount> searchIngredients;

    public List<IngredientCount> SearchIngredients
    {
        get
        {
            if (searchIngredients == null)
            {
                searchIngredients = new List<IngredientCount>();
                foreach (ThingDefCountClass input in ingredients)
                {
                    IngredientCount count = new IngredientCount();
                    count.filter.SetAllow(input.thingDef, true);
                    count.SetBaseCount(input.count);
                    searchIngredients.Add(count);
                }
            }
            return searchIngredients;
        }
    }
}

[StaticConstructorOnStartup]
public static class Startup
{
    static Startup()
    {
        new Harmony("kergunes.serumsexpended.adaptivecrafting").PatchAll();
    }
}

internal static class AdaptiveProduction
{
    // Collect once before choosing a route. Vanilla's ingredient helper batches
    // nearby regions and stops at a complete recipe; that search strategy does
    // not provide a complete common pool for strict cross-route priorities.
    // Do not mutate shared RecipeDefs/Bills or reuse vanilla's static scratch lists.
    internal static List<Thing> GatherIngredients(Bill bill, Pawn pawn, Thing giver,
        AdaptiveSerumRecipe extension)
    {
        HashSet<ThingDef> allowedDefs = new HashSet<ThingDef>(
            extension.routes.SelectMany(route => route.ingredients).Select(input => input.thingDef));
        List<Thing> available = new List<Thing>();
        HashSet<Thing> seen = new HashSet<Thing>();
        float radiusSquared = bill.ingredientSearchRadius * bill.ingredientSearchRadius;

        void Consider(Thing thing)
        {
            if (thing == null || !seen.Add(thing) || thing.Destroyed
                || !allowedDefs.Contains(thing.def) || !bill.ingredientFilter.Allows(thing)) return;
            Thing access = thing.Spawned ? thing : thing.SpawnedParentOrMe;
            if (access == null || !access.Spawned || access.Map != pawn.Map
                || (access.Position - giver.Position).LengthHorizontalSquared >= radiusSquared
                || thing.IsForbidden(pawn) || access.IsForbidden(pawn)
                || !pawn.CanReserve(thing)
                || !pawn.CanReach(access, PathEndMode.ClosestTouch, pawn.NormalMaxDanger())) return;
            available.Add(thing);
        }

        // Only the few ingredient defs are listed, instead of every haulable on the map.
        foreach (ThingDef def in allowedDefs)
            foreach (Thing thing in pawn.Map.listerThings.ThingsOfDef(def).ToList())
                Consider(thing);

        // Match the vanilla crafting sources, including enabled storage
        // containers. Other pawns' inventory is not an ingredient source.
        foreach (IHaulSource source in pawn.Map.haulDestinationManager.AllHaulSourcesListForReading)
        {
            if (!source.HaulSourceEnabled || !(source is Thing container)
                || !container.Spawned || container.IsForbidden(pawn)
                || (container.Position - giver.Position).LengthHorizontalSquared >= radiusSquared) continue;
            List<Thing> contents = new List<Thing>();
            ThingOwnerUtility.GetAllThingsRecursively(source, contents);
            foreach (Thing thing in contents) Consider(thing);
        }
        return available;
    }

    internal static bool TryChoose(List<Thing> available, SerumRoute route,
        List<ThingCount> chosen, Thing giver)
    {
        chosen.Clear();
        // All inputs have exact Defs. A route is accepted only as a complete bundle.
        foreach (ThingDefCountClass input in route.ingredients)
        {
            int remaining = input.count;
            foreach (Thing thing in available.Where(t => t.def == input.thingDef)
                .OrderBy(t => t.PositionHeld.DistanceToSquared(giver.Position)))
            {
                int used = ThingCountUtility.CountOf(chosen, thing);
                int take = Math.Min(remaining, thing.stackCount - used);
                if (take <= 0) continue;
                ThingCountUtility.AddToList(chosen, thing, take);
                remaining -= take;
                if (remaining == 0) break;
            }
            if (remaining > 0)
            {
                chosen.Clear();
                return false;
            }
        }
        return true;
    }

    internal static List<IngredientCount> MissingForRoute(SerumRoute route, List<Thing> available)
    {
        List<IngredientCount> missing = new List<IngredientCount>();
        foreach (ThingDefCountClass input in route.ingredients)
        {
            int found = available.Where(thing => thing.def == input.thingDef)
                .Sum(thing => thing.stackCount);
            int shortage = input.count - found;
            if (shortage <= 0) continue;
            IngredientCount count = new IngredientCount();
            count.filter.SetAllow(input.thingDef, true);
            count.SetBaseCount(shortage);
            missing.Add(count);
        }
        return missing;
    }

    // The hauled ingredient counts are saved by the vanilla job. Infer the route
    // from those counts rather than retaining a volatile bill-to-route cache.
    internal static SerumRoute RouteForJob(Job job)
    {
        AdaptiveSerumRecipe extension = job?.RecipeDef?.GetModExtension<AdaptiveSerumRecipe>();
        if (extension == null || job.placedThings == null) return null;
        Dictionary<ThingDef, int> placed = new Dictionary<ThingDef, int>();
        foreach (ThingCountClass entry in job.placedThings)
        {
            if (entry.thing == null || entry.thing.Destroyed || entry.Count <= 0) continue;
            placed.TryGetValue(entry.thing.def, out int previous);
            placed[entry.thing.def] = previous + entry.Count;
        }
        foreach (SerumRoute route in extension.routes)
        {
            if (placed.Count == route.ingredients.Count && route.ingredients.All(
                input => placed.TryGetValue(input.thingDef, out int count) && count == input.count))
                return route;
        }
        return null;
    }
}

[HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients")]
internal static class SelectSerumIngredients
{
    private static bool Prefix(Bill bill, Pawn pawn, Thing billGiver,
        List<ThingCount> chosen, List<IngredientCount> missingIngredients, ref bool __result)
    {
        AdaptiveSerumRecipe extension = bill.recipe.GetModExtension<AdaptiveSerumRecipe>();
        if (extension == null) return true;

        chosen.Clear();
        missingIngredients?.Clear();
        List<Thing> available = AdaptiveProduction.GatherIngredients(bill, pawn, billGiver, extension);
        List<IngredientCount> closestMissing = null;
        void ConsiderMissing(List<IngredientCount> candidate)
        {
            // Prefer an alternative missing fewer ingredient types, then fewer
            // items. Exact ties retain the normal higher-tier route priority.
            if (closestMissing == null || candidate.Count < closestMissing.Count
                || (candidate.Count == closestMissing.Count
                    && candidate.Sum(input => input.GetBaseCount())
                        < closestMissing.Sum(input => input.GetBaseCount())))
                closestMissing = candidate;
        }
        foreach (SerumRoute route in extension.routes)
        {
            // Every route sees the entire same pool. Proximity sorts stacks
            // within a route, never raw materials ahead of a usable serum route.
            if (AdaptiveProduction.TryChoose(available, route, chosen, billGiver))
            {
                __result = true;
                return false;
            }
            if (missingIngredients != null)
                ConsiderMissing(AdaptiveProduction.MissingForRoute(route, available));
        }
        chosen.Clear();
        // Report actual shortages for the closest alternative, rather than the
        // complete raw recipe when an upgrade serum is already available.
        if (missingIngredients != null && closestMissing != null)
            missingIngredients.AddRange(closestMissing);
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Toils_Recipe), nameof(Toils_Recipe.DoRecipeWork))]
internal static class SetSerumWork
{
    private static void Postfix(Toil __result)
    {
        Toil toil = __result;
        Action vanillaInit = toil.initAction;
        toil.initAction = () =>
        {
            Job job = toil.actor.CurJob;
            if (job?.RecipeDef?.GetModExtension<AdaptiveSerumRecipe>() == null)
            {
                vanillaInit?.Invoke();
                return;
            }
            SerumRoute route = AdaptiveProduction.RouteForJob(job);
            if (route == null)
            {
                // Never complete an adaptive recipe with a partial input bundle.
                toil.actor.jobs.EndCurrentJob(JobCondition.Incompletable);
                return;
            }
            vanillaInit?.Invoke();
            JobDriver_DoBill driver = toil.actor.jobs.curDriver as JobDriver_DoBill;
            if (driver != null)
            {
                // Preserve vanilla bill work multipliers, discounting only stages
                // already embodied in the consumed serum.
                driver.workLeft *= route.work / job.RecipeDef.workAmount;
            }
        };
    }
}

[HarmonyPatch(typeof(ToilEffects), nameof(ToilEffects.WithProgressBar))]
internal static class SerumProgress
{
    private static void Prefix(Toil toil, ref Func<float> progressGetter)
    {
        if (toil.debugName != "DoRecipeWork") return;
        Func<float> vanillaProgress = progressGetter;
        progressGetter = () =>
        {
            Job job = toil.actor?.CurJob;
            SerumRoute route = AdaptiveProduction.RouteForJob(job);
            if (route == null) return vanillaProgress();
            float vanillaWork = job.bill.GetWorkAmount(job.GetTarget(TargetIndex.B).Thing);
            float total = vanillaWork * route.work / job.RecipeDef.workAmount;
            return total > 0f
                ? 1f - ((JobDriver_DoBill)toil.actor.jobs.curDriver).workLeft / total
                : 1f;
        };
    }
}
