using System.Reflection;
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

        /// <summary>
        /// Blocks VEF's own <c>OnAcceptKeyPressed</c> override on the faction prompt while our list
        /// is driving it.
        ///
        /// This is the root CLAUDE.md "Keyboard Input Isolation" case. RimWorld raises Accept/Cancel
        /// from <c>KeyBindingDef.KeyDownEvent</c>, which does not consult <c>Event.current.Use()</c>,
        /// so consuming the event in our handler is not enough. And unlike the vanilla base method —
        /// which only closes when <c>closeOnAccept</c> is set, and so can be neutralised by clearing
        /// that flag — VEF's override ignores the flag and adds the faction outright. Left unpatched,
        /// Enter would add the faction regardless of whether the user had "Do nothing" or "Don't ask
        /// again" selected.
        ///
        /// Resolved dynamically (<see cref="Prepare"/> skips the patch entirely when VEF is absent)
        /// so RimWorld Access still never hard-references the framework.
        /// </summary>
        [HarmonyPatch]
        public static class Dialog_OnAcceptKeyPressed_Patch
        {
            public static bool Prepare() => VEFFactionsReflection.DialogOnAcceptKeyPressed != null;

            public static MethodBase TargetMethod() => VEFFactionsReflection.DialogOnAcceptKeyPressed;

            [HarmonyPrefix]
            public static bool Prefix() => !NewFactionSpawningState.IsActive;
        }
    }
}
