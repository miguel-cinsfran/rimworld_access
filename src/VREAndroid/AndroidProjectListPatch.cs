using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Patches for the android mod's saved-project picker (Dialog_AndroidProjectList_Load/_Save).
    /// DoWindowContents is declared on the vanilla Dialog_FileList (not on the mod's subclasses),
    /// so we patch the base method and filter to android's own subclasses via
    /// <see cref="VREAndroidReflection.IsAndroidProjectListWindow"/> - harmless for every other
    /// Dialog_FileList subclass (vanilla save/load, Ideology's picker). Mirrors IdeoLoadPatch.
    /// </summary>
    [HarmonyPatch(typeof(Dialog_FileList), "DoWindowContents")]
    public static class AndroidProjectListPatch
    {
        static bool Prefix(Window __instance)
        {
            try
            {
                if (!(__instance is Dialog_FileList fileList) || !VREAndroidReflection.IsAndroidProjectListWindow(fileList))
                    return true;

                AndroidProjectListState.EnsureOpen(fileList);

                if (WindowlessDialogState.IsActive || WindowlessConfirmationState.IsActive)
                    return true;

                if (Event.current.type == EventType.KeyDown)
                {
                    if (AndroidProjectListState.HandleInput(Event.current))
                        Event.current.Use();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimWorld Access] Error in AndroidProjectListPatch.Prefix: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Window), "OnCancelKeyPressed")]
    public static class AndroidProjectListPatch_OnCancel
    {
        [HarmonyPrefix]
        static bool Prefix(Window __instance)
        {
            if (VREAndroidReflection.IsAndroidProjectListWindow(__instance) && AndroidProjectListState.IsActive && AndroidProjectListState.HasActiveSearch)
                return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(Window), "PostOpen")]
    public static class AndroidProjectListPatch_PostOpen
    {
        [HarmonyPostfix]
        static void Postfix(Window __instance)
        {
            if (VREAndroidReflection.IsAndroidProjectListWindow(__instance))
                Find.WindowStack.Notify_ManuallySetFocus(__instance);
        }
    }

    [HarmonyPatch(typeof(Window), "PostClose")]
    public static class AndroidProjectListPatch_PostClose
    {
        [HarmonyPostfix]
        static void Postfix(Window __instance)
        {
            if (VREAndroidReflection.IsAndroidProjectListWindow(__instance))
                AndroidProjectListState.Close();
        }
    }
}
