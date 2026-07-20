using HarmonyLib;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Lifecycle patches that bind <see cref="ColonyManagerState"/> to Colony Manager Redux's
    /// <c>MainTabWindow_Manager</c> while it is open. Colony Manager is an optional mod whose
    /// assembly we cannot reference, so we patch vanilla types only and gate on
    /// <see cref="ColonyManagerReflection.IsManagerWindow"/>. When the mod is absent these
    /// guards are always false and the patches do nothing.
    ///
    /// Open is detected on <see cref="WindowStack.Add"/> (fires the instant the window is
    /// pushed, independent of the mod's PostOpen override chain, so the announcement is
    /// immediate). Close is detected on the vanilla <see cref="Window.PreClose"/> (the proven
    /// pattern already used for Dialog_ModSettings) with a belt-and-suspenders safety poll in
    /// <see cref="UnifiedKeyboardPatch"/> in case a close path ever skips it.
    ///
    /// Escape/Enter blocking follows the project's documented Window.OnCancelKeyPressed /
    /// OnAcceptKeyPressed pattern (root CLAUDE.md): the WindowStack invokes those independently
    /// of DoWindowContents, so Event.current.Use() cannot stop them. While our menu owns the
    /// window we block both — our handler steps Escape back through the menu levels (and closes
    /// the window itself at the top level), and Enter activates a control.
    /// </summary>
    [HarmonyPatch]
    public static class ColonyManagerPatch
    {
        [HarmonyPatch(typeof(WindowStack), "Add")]
        [HarmonyPostfix]
        public static void WindowStack_Add_Postfix(Window window)
        {
            try
            {
                if (!ColonyManagerReflection.IsManagerWindow(window))
                {
                    return;
                }
                if (ColonyManagerState.IsActive && ReferenceEquals(window, ColonyManagerState.BoundWindow))
                {
                    return;
                }
                ColonyManagerDebug.Log($"WindowStack.Add detected manager window: {window.GetType().FullName}");
                ColonyManagerState.Open(window);
            }
            catch (System.Exception e)
            {
                ColonyManagerDebug.Error("open error", e);
            }
        }

        [HarmonyPatch(typeof(Window), "PreClose")]
        [HarmonyPostfix]
        public static void Window_PreClose_Postfix(Window __instance)
        {
            try
            {
                if (!ColonyManagerState.IsActive)
                {
                    return;
                }
                if (!ReferenceEquals(__instance, ColonyManagerState.BoundWindow))
                {
                    return;
                }
                ColonyManagerDebug.Log("Window.PreClose detected bound manager window closing");
                ColonyManagerState.Close();
                TolkHelper.Speak("RimWorldAccess.ColonyManager.Closed".Loc());
            }
            catch (System.Exception e)
            {
                ColonyManagerDebug.Error("close error", e);
            }
        }

        [HarmonyPatch(typeof(Window), "OnCancelKeyPressed")]
        [HarmonyPrefix]
        public static bool Window_OnCancelKeyPressed_Prefix(Window __instance)
        {
            if (ColonyManagerState.IsActive && ReferenceEquals(__instance, ColonyManagerState.BoundWindow))
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Window), "OnAcceptKeyPressed")]
        [HarmonyPrefix]
        public static bool Window_OnAcceptKeyPressed_Prefix(Window __instance)
        {
            if (ColonyManagerState.IsActive && ReferenceEquals(__instance, ColonyManagerState.BoundWindow))
            {
                return false;
            }
            return true;
        }
    }
}
