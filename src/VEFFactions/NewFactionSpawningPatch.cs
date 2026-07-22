using HarmonyLib;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Harmony patches wiring the Vanilla Expanded Framework's new-faction prompt into RimWorld
    /// Access. Uses the base <c>Window.PostOpen</c>/<c>PostClose</c> lifecycle (the FactionLandingPatch
    /// pattern) rather than patching VEF's own types, so the mod is never hard-referenced and this
    /// no-ops when VEF is absent.
    ///
    /// Both VEF dialogs chain: <c>Dialog_NewFactionSpawning.PostClose</c> reopens itself for the next
    /// faction in the queue, and "add with settlements" pushes the settlements dialog on top. Because
    /// these are lifecycle hooks, each new window in the chain is picked up automatically.
    /// </summary>
    public static class NewFactionSpawningPatch
    {
        [HarmonyPatch(typeof(Window), "PostOpen")]
        public static class Window_PostOpen_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Window __instance)
            {
                if (VEFFactionsReflection.IsNewFactionDialog(__instance))
                    NewFactionSpawningState.Open(__instance);
                else if (VEFFactionsReflection.IsSettlementsDialog(__instance))
                    NewFactionSettlementsState.Open(__instance);
            }
        }

        [HarmonyPatch(typeof(Window), "PostClose")]
        public static class Window_PostClose_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Window __instance)
            {
                // Order matters: the settlements dialog closes first and hands control back to the
                // faction prompt underneath, which must stay active.
                if (VEFFactionsReflection.IsSettlementsDialog(__instance) && NewFactionSettlementsState.IsActive)
                    NewFactionSettlementsState.Close();
                else if (VEFFactionsReflection.IsNewFactionDialog(__instance) && NewFactionSpawningState.IsActive)
                    NewFactionSpawningState.Close();
            }
        }
    }
}
