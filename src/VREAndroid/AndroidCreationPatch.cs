using HarmonyLib;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Lifecycle patches binding <see cref="AndroidCreationState"/> to the android mod's gene
    /// creation windows. Open is detected on <see cref="WindowStack.Add"/> (fires unconditionally,
    /// independent of whether the mod's own PostOpen override chain calls base.PostOpen -
    /// Window_CreateAndroidBase.PostOpen does call it, but WindowStack.Add is still the more
    /// robust signal and matches the pattern already used for Colony Manager). Close/Escape/Enter
    /// use the vanilla Window base methods, which none of the mod's windows override.
    /// </summary>
    [HarmonyPatch]
    public static class AndroidCreationPatch
    {
        [HarmonyPatch(typeof(WindowStack), "Add")]
        [HarmonyPostfix]
        public static void WindowStack_Add_Postfix(Window window)
        {
            try
            {
                if (!VREAndroidReflection.IsAndroidCreationWindow(window))
                    return;
                if (AndroidCreationState.IsActive && ReferenceEquals(window, AndroidCreationState.BoundWindow))
                    return;

                AndroidCreationState.Open(window);
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RimWorld Access] Error in AndroidCreationPatch.WindowStack_Add_Postfix: {ex}");
            }
        }

        [HarmonyPatch(typeof(Window), "PostClose")]
        [HarmonyPostfix]
        public static void Window_PostClose_Postfix(Window __instance)
        {
            if (AndroidCreationState.IsActive && ReferenceEquals(__instance, AndroidCreationState.BoundWindow))
                AndroidCreationState.Close();
        }

        [HarmonyPatch(typeof(Window), "OnCancelKeyPressed")]
        [HarmonyPrefix]
        public static bool Window_OnCancelKeyPressed_Prefix(Window __instance)
        {
            if (AndroidCreationState.IsActive && ReferenceEquals(__instance, AndroidCreationState.BoundWindow))
                return false;
            return true;
        }

        [HarmonyPatch(typeof(Window), "OnAcceptKeyPressed")]
        [HarmonyPrefix]
        public static bool Window_OnAcceptKeyPressed_Prefix(Window __instance)
        {
            if (AndroidCreationState.IsActive && ReferenceEquals(__instance, AndroidCreationState.BoundWindow))
                return false;
            return true;
        }
    }
}
