using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Reflection facade for the Vanilla Expanded Framework's new-faction prompt
    /// (<c>VEF.Factions.Dialog_NewFactionSpawning</c> and its settlement-count follow-up
    /// <c>VEF.Factions.Dialog_NewFactionSpawningSettlements</c>).
    ///
    /// RimWorld Access never hard-references VEF — every type and member is resolved by name via
    /// <see cref="AccessTools"/>, cached once, and each accessor degrades to a safe default when the
    /// framework is absent (<see cref="Available"/> == false). Same soft-dependency pattern as the
    /// VREAndroid and VPEPsycasts modules.
    ///
    /// Interaction model read from VEF's own <c>DoWindowContents</c>: the dialog offers one faction
    /// at a time (its <c>PostClose</c> advances an enumerator and reopens for the next one) with
    /// three choices — add it (with settlements when the faction can have them, which opens the
    /// settlements dialog; otherwise directly), do nothing (asked again next load), or never ask
    /// again. When the faction's <c>forcedFactionData.forcePlayerToAddFactionIfMissing</c> is set,
    /// the latter two are refused and the dialog cannot be cancelled.
    /// </summary>
    public static class VEFFactionsReflection
    {
        public static bool Available { get; private set; }
        public static bool SettlementsAvailable { get; private set; }

        // ===== Types =====
        private static Type dialogType;             // VEF.Factions.Dialog_NewFactionSpawning
        private static Type settlementsDialogType;  // VEF.Factions.Dialog_NewFactionSpawningSettlements
        private static Type customGenOptionType;    // KCSG.CustomGenOption (optional)

        // ===== Dialog_NewFactionSpawning members =====
        private static FieldInfo fFactionDef, fForcedFactionData, fFailedToSpawn;
        private static MethodInfo mSpawnWithBases, mSpawnWithoutBases, mSkip, mIgnore;
        private static MethodInfo mDialogOnAcceptKeyPressed;

        /// <summary>
        /// VEF's <c>Dialog_NewFactionSpawning.OnAcceptKeyPressed</c> override, or null.
        /// Unlike the vanilla base (which merely honours <c>closeOnAccept</c>), this override adds
        /// the faction outright — so it cannot be neutralised with a flag and must be patched, or
        /// Enter would add the faction no matter which row the user has selected.
        /// </summary>
        public static MethodInfo DialogOnAcceptKeyPressed => mDialogOnAcceptKeyPressed;

        // ===== ForcedFactionData members =====
        private static FieldInfo fForcePlayerToAdd;

        // ===== Dialog_NewFactionSpawningSettlements members =====
        private static FieldInfo fSettlementsToSpawn, fSettlementsRecommended;
        private static FieldInfo fDistanceToSpawn, fDistanceRecommended;
        private static MethodInfo mSpawn;

        // ===== KCSG.CustomGenOption =====
        private static FieldInfo fCanSpawnSettlements;

        static VEFFactionsReflection()
        {
            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                Available = false;
                Log.Warning($"[RimWorld Access] VEFFactionsReflection init failed (new-faction dialog support disabled): {ex.Message}");
            }
        }

        private static void Initialize()
        {
            dialogType = AccessTools.TypeByName("VEF.Factions.Dialog_NewFactionSpawning");
            settlementsDialogType = AccessTools.TypeByName("VEF.Factions.Dialog_NewFactionSpawningSettlements");
            customGenOptionType = AccessTools.TypeByName("KCSG.CustomGenOption");

            if (dialogType != null)
            {
                fFactionDef = AccessTools.Field(dialogType, "factionDef");
                fForcedFactionData = AccessTools.Field(dialogType, "forcedFactionData");
                fFailedToSpawn = AccessTools.Field(dialogType, "failedToSpawn");
                mSpawnWithBases = AccessTools.Method(dialogType, "SpawnWithBases");
                mSpawnWithoutBases = AccessTools.Method(dialogType, "SpawnWithoutBases");
                mSkip = AccessTools.Method(dialogType, "Skip");
                mIgnore = AccessTools.Method(dialogType, "Ignore");
                mDialogOnAcceptKeyPressed = AccessTools.Method(dialogType, "OnAcceptKeyPressed");
            }

            if (settlementsDialogType != null)
            {
                fSettlementsToSpawn = AccessTools.Field(settlementsDialogType, "settlementsToSpawn");
                fSettlementsRecommended = AccessTools.Field(settlementsDialogType, "settlementsRecommended");
                fDistanceToSpawn = AccessTools.Field(settlementsDialogType, "distanceToSpawn");
                fDistanceRecommended = AccessTools.Field(settlementsDialogType, "distanceRecommended");
                mSpawn = AccessTools.Method(settlementsDialogType, "Spawn");
            }

            if (customGenOptionType != null)
                fCanSpawnSettlements = AccessTools.Field(customGenOptionType, "canSpawnSettlements");

            Available = dialogType != null && fFactionDef != null && mSpawnWithoutBases != null
                        && mSkip != null && mIgnore != null;

            SettlementsAvailable = settlementsDialogType != null && fSettlementsToSpawn != null
                                   && fDistanceToSpawn != null && mSpawn != null;

            if (dialogType != null && !Available)
                Log.Warning("[RimWorld Access] VEF new-faction dialog detected but some members failed to resolve; accessibility for it is disabled.");
        }

        // ===== Type tests =====

        public static bool IsNewFactionDialog(Window window)
            => Available && window != null && dialogType.IsInstanceOfType(window);

        public static bool IsSettlementsDialog(Window window)
            => SettlementsAvailable && window != null && settlementsDialogType.IsInstanceOfType(window);

        // ===== Dialog_NewFactionSpawning accessors =====

        public static FactionDef GetFactionDef(Window dialog)
        {
            try { return fFactionDef?.GetValue(dialog) as FactionDef; }
            catch { return null; }
        }

        public static bool GetFailedToSpawn(Window dialog)
        {
            try { return fFailedToSpawn != null && (bool)fFailedToSpawn.GetValue(dialog); }
            catch { return false; }
        }

        /// <summary>
        /// True when VEF forbids skipping/ignoring this faction (the mod declares it must exist).
        /// In that state VEF also refuses to close on Escape.
        /// </summary>
        public static bool ForcesPlayerToAdd(Window dialog)
        {
            try
            {
                object ffd = fForcedFactionData?.GetValue(dialog);
                if (ffd == null) return false;
                if (fForcePlayerToAdd == null)
                    fForcePlayerToAdd = AccessTools.Field(ffd.GetType(), "forcePlayerToAddFactionIfMissing");
                return fForcePlayerToAdd != null && (bool)fForcePlayerToAdd.GetValue(ffd);
            }
            catch { return false; }
        }

        /// <summary>
        /// The message VEF shows when the player tries to refuse a faction it insists on, or "".
        /// </summary>
        public static string GetForcedRefusalMessage(Window dialog, FactionDef factionDef)
        {
            try
            {
                object ffd = fForcedFactionData?.GetValue(dialog);
                if (ffd == null) return "";
                MethodInfo m = AccessTools.Method(ffd.GetType(), "GetFactionDiscoveryMessage");
                if (m == null) return "";
                object result = m.Invoke(ffd, new object[] { factionDef });
                return result == null ? "" : result.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// Mirrors VEF's own button choice: the "add with settlements" variant is offered only for a
        /// non-hidden faction that either has no <c>CustomGenOption</c> extension or whose extension
        /// allows settlements. Everything else gets the plain "add" button.
        /// </summary>
        public static bool CanSpawnWithSettlements(FactionDef factionDef)
        {
            if (factionDef == null || factionDef.hidden)
                return false;
            try
            {
                if (customGenOptionType == null || fCanSpawnSettlements == null)
                    return true; // No extension type at all → VEF treats it as "settlements allowed".
                object ext = factionDef.modExtensions?.Find(e => customGenOptionType.IsInstanceOfType(e));
                if (ext == null)
                    return true;
                return (bool)fCanSpawnSettlements.GetValue(ext);
            }
            catch { return true; }
        }

        public static void SpawnWithBases(Window dialog) => SafeInvoke(mSpawnWithBases, dialog, "SpawnWithBases");
        public static void SpawnWithoutBases(Window dialog) => SafeInvoke(mSpawnWithoutBases, dialog, "SpawnWithoutBases");
        public static void Skip(Window dialog) => SafeInvoke(mSkip, dialog, "Skip");
        public static void Ignore(Window dialog) => SafeInvoke(mIgnore, dialog, "Ignore");

        // ===== Dialog_NewFactionSpawningSettlements accessors =====

        public static int GetSettlementsToSpawn(Window dialog) => SafeInt(fSettlementsToSpawn, dialog);
        public static int GetSettlementsRecommended(Window dialog) => SafeInt(fSettlementsRecommended, dialog);
        public static int GetDistanceToSpawn(Window dialog) => SafeInt(fDistanceToSpawn, dialog);
        public static int GetDistanceRecommended(Window dialog) => SafeInt(fDistanceRecommended, dialog);

        public static void SetSettlementsToSpawn(Window dialog, int value) => SafeSetInt(fSettlementsToSpawn, dialog, value);
        public static void SetDistanceToSpawn(Window dialog, int value) => SafeSetInt(fDistanceToSpawn, dialog, value);

        public static void Spawn(Window dialog) => SafeInvoke(mSpawn, dialog, "Spawn");

        // ===== Plumbing =====

        private static void SafeInvoke(MethodInfo method, Window dialog, string name)
        {
            try { method?.Invoke(dialog, null); }
            catch (Exception ex) { Log.Error($"[RimWorld Access] VEF new-faction dialog: {name} failed: {ex.Message}"); }
        }

        private static int SafeInt(FieldInfo field, object instance)
        {
            try { return field != null && instance != null ? (int)field.GetValue(instance) : 0; }
            catch { return 0; }
        }

        private static void SafeSetInt(FieldInfo field, object instance, int value)
        {
            try { field?.SetValue(instance, value); }
            catch { }
        }
    }
}
