using HarmonyLib;
using RimWorld;
using Verse;

namespace SerumsExpended;

// Serums are drugs (Anomaly's SerumBase has a drug category), so vanilla's
// JobGiver_DropUnusedInventory drops them from a colonist standing in the home
// area unless the drug policy allows carrying them. Nothing lets players set
// that for a one-off serum, and taking one into inventory by hand (for example
// with Combat Extended's pick-up option) made the pawn drop it again at once.
// Serums are never part of a scheduled drug routine, so keep them.
[HarmonyPatch(typeof(JobGiver_DropUnusedInventory), nameof(JobGiver_DropUnusedInventory.ShouldKeepDrugInInventory))]
internal static class KeepSerumsInInventory
{
    private static void Postfix(Thing drug, ref bool __result)
    {
        if (!__result && drug?.def.GetCompProperties<CompProperties_Serum>() != null)
            __result = true;
    }
}
