using HarmonyLib;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Lifecycle patches binding <see cref="AndroidAwakenedState"/> to the android mod's
    /// awakening choice dialog. Open is detected on <see cref="WindowStack.Add"/> (fires for
    /// both the automatic letter open and our own re-open from the notification menu - see
    /// NotificationMenuState's ChoiceLetter_AndroidAwakened branch); the dialog's private
    /// `letter` field is read via <see cref="VREAndroidReflection"/>.
    /// </summary>
    [HarmonyPatch]
    public static class AndroidAwakenedPatch
    {
        [HarmonyPatch(typeof(WindowStack), "Add")]
        [HarmonyPostfix]
        public static void WindowStack_Add_Postfix(Window window)
        {
            try
            {
                if (!VREAndroidReflection.IsAndroidAwakenedDialog(window))
                    return;
                if (AndroidAwakenedState.IsActive && ReferenceEquals(window, AndroidAwakenedState.BoundWindow))
                    return;

                ChoiceLetter letter = VREAndroidReflection.GetAwakenedDialogLetter(window);
                if (letter == null)
                {
                    Log.Warning("[RimWorld Access] Android awakened dialog opened without a resolvable letter field");
                    return;
                }

                AndroidAwakenedState.Open(window, letter);
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RimWorld Access] Error in AndroidAwakenedPatch.WindowStack_Add_Postfix: {ex}");
            }
        }

        [HarmonyPatch(typeof(Window), "PostClose")]
        [HarmonyPostfix]
        public static void Window_PostClose_Postfix(Window __instance)
        {
            if (AndroidAwakenedState.IsActive && ReferenceEquals(__instance, AndroidAwakenedState.BoundWindow))
                AndroidAwakenedState.Close();
        }

        [HarmonyPatch(typeof(Window), "OnCancelKeyPressed")]
        [HarmonyPrefix]
        public static bool Window_OnCancelKeyPressed_Prefix(Window __instance)
        {
            if (AndroidAwakenedState.IsActive && ReferenceEquals(__instance, AndroidAwakenedState.BoundWindow))
                return false;
            return true;
        }

        [HarmonyPatch(typeof(Window), "OnAcceptKeyPressed")]
        [HarmonyPrefix]
        public static bool Window_OnAcceptKeyPressed_Prefix(Window __instance)
        {
            if (AndroidAwakenedState.IsActive && ReferenceEquals(__instance, AndroidAwakenedState.BoundWindow))
                return false;
            return true;
        }
    }
}
