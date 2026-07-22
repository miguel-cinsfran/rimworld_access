using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Reflection facade for Vanilla Psycasts Expanded (VPE) and its Vanilla Expanded Framework
    /// (VEF) dependency. RimWorld Access never hard-references either optional assembly — every
    /// type and member is resolved by name via <see cref="AccessTools"/>, cached once, and each
    /// accessor degrades to a safe default when the mod is absent (<see cref="Available"/> == false).
    ///
    /// This mirrors the soft-dependency pattern used for the VREAndroid and Colony Manager Redux
    /// modules: the mod's own types (Hediff_PsycastAbilities, PsycasterPathDef, CompAbilities, …)
    /// flow through this facade as <see cref="object"/>/<see cref="Def"/>, while the vanilla types
    /// they touch (Pawn, MeditationFocusDef) are referenced directly.
    ///
    /// Interaction model reflected from VPE's <c>ITab_Pawn_Psycasts.FillTab</c>: a psycaster's
    /// <c>Hediff_PsycastAbilities</c> holds spendable <c>points</c>; each point buys one of
    /// (a) a psycaster stat upgrade (<c>ImproveStats</c>), (b) unlocking a meditation focus,
    /// (c) unlocking a path, or (d) learning one ability inside an unlocked path (gated on its
    /// prerequisites). Every spend goes through <c>SpentPoints</c> first.
    /// </summary>
    public static class VPEPsycastsReflection
    {
        public static bool Available { get; private set; }

        // ===== Types =====
        private static Type hediffType;         // VanillaPsycastsExpanded.Hediff_PsycastAbilities
        private static Type pathDefType;        // VanillaPsycastsExpanded.PsycasterPathDef
        private static Type psycastExtType;     // VanillaPsycastsExpanded.AbilityExtension_Psycast
        private static Type compAbilitiesType;  // VEF.Abilities.CompAbilities
        private static Type meditationUtilsType;// VanillaPsycastsExpanded.MeditationUtilities (CanUnlock ext)

        // ===== Hediff_PsycastAbilities members =====
        private static FieldInfo fPoints, fExperience, fUnlockedPaths, fUnlockedFoci, fLevel;
        private static MethodInfo mSpentPoints, mImproveStats, mUnlockPath, mUnlockFocus, mExpRequired;

        // ===== PsycasterPathDef members =====
        private static FieldInfo fAbilities, fLockedReason, fPathOrder, fHasAbilities;
        private static MethodInfo mCanPawnUnlock;

        // ===== AbilityExtension_Psycast members =====
        private static FieldInfo fPrereqs, fAbLevel, fAbOrder;
        private static MethodInfo mGetPsyfocusUsed, mGetEntropyUsed;

        // ===== CompAbilities members =====
        private static MethodInfo mHasAbility, mGiveAbility;

        // ===== MeditationFocusDef (vanilla) + VPE CanUnlock extension =====
        private static MethodInfo mCanPawnUse, mCanUnlock;

        // ===== VEF ability instances (gizmo cost/cooldown readout) — gated on VefAbilitiesAvailable =====
        // Independent of VPE: any Vanilla Expanded Framework ability mod uses these, so they are
        // resolved before the VPE-specific bail-out below.
        private static Type vefAbilityType, vefCommandAbilityType;
        private static FieldInfo fVefCmdAbility, fVefAbilityDef, fVefAbilityCooldown, fVefAbilityPawn;
        public static bool VefAbilitiesAvailable { get; private set; }

        // ===== Psysets (loadout management) — optional; gated on PsysetsAvailable =====
        private static Type psySetType, dialogRenamePsysetType;
        private static FieldInfo fPsysets, fPsysetName, fPsysetAbilities;
        private static MethodInfo mRemovePsySet;
        private static MethodInfo mSetAdd, mSetRemove, mSetContains; // HashSet<AbilityDef> members
        public static bool PsysetsAvailable { get; private set; }

        // Path defs are immutable after load; cache the list on first request.
        private static List<Def> allPathsCache;

        static VPEPsycastsReflection()
        {
            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                Available = false;
                Log.Warning($"[RimWorld Access] VPEPsycastsReflection init failed (VPE support disabled): {ex.Message}");
            }
        }

        private static void Initialize()
        {
            hediffType = AccessTools.TypeByName("VanillaPsycastsExpanded.Hediff_PsycastAbilities");
            pathDefType = AccessTools.TypeByName("VanillaPsycastsExpanded.PsycasterPathDef");
            psycastExtType = AccessTools.TypeByName("VanillaPsycastsExpanded.AbilityExtension_Psycast");
            compAbilitiesType = AccessTools.TypeByName("VEF.Abilities.CompAbilities");
            meditationUtilsType = AccessTools.TypeByName("VanillaPsycastsExpanded.MeditationUtilities");

            // VEF ability instances. Resolved first and gated separately, because these power the
            // gizmo cost/cooldown readout for ANY VEF-based ability mod — VPE itself may be absent.
            vefAbilityType = AccessTools.TypeByName("VEF.Abilities.Ability");
            vefCommandAbilityType = AccessTools.TypeByName("VEF.Abilities.Command_Ability");
            if (vefAbilityType != null)
            {
                fVefAbilityDef = AccessTools.Field(vefAbilityType, "def");
                fVefAbilityCooldown = AccessTools.Field(vefAbilityType, "cooldown");
                fVefAbilityPawn = AccessTools.Field(vefAbilityType, "pawn");
            }
            if (vefCommandAbilityType != null)
                fVefCmdAbility = AccessTools.Field(vefCommandAbilityType, "ability");
            VefAbilitiesAvailable = vefCommandAbilityType != null && fVefCmdAbility != null
                                    && fVefAbilityDef != null && fVefAbilityCooldown != null;

            // Core types absent → mod not installed; stay unavailable and no-op everywhere.
            if (hediffType == null || pathDefType == null || psycastExtType == null || compAbilitiesType == null)
                return;

            fPoints = AccessTools.Field(hediffType, "points");
            fExperience = AccessTools.Field(hediffType, "experience");
            fUnlockedPaths = AccessTools.Field(hediffType, "unlockedPaths");
            fUnlockedFoci = AccessTools.Field(hediffType, "unlockedMeditationFoci");
            fLevel = AccessTools.Field(hediffType, "level"); // inherited from Hediff_Level
            mSpentPoints = AccessTools.Method(hediffType, "SpentPoints", new[] { typeof(int) });
            mImproveStats = AccessTools.Method(hediffType, "ImproveStats", new[] { typeof(int) });
            mUnlockPath = AccessTools.Method(hediffType, "UnlockPath", new[] { pathDefType });
            mUnlockFocus = AccessTools.Method(hediffType, "UnlockMeditationFocus", new[] { typeof(MeditationFocusDef) });
            mExpRequired = AccessTools.Method(hediffType, "ExperienceRequiredForLevel", new[] { typeof(int) });

            fAbilities = AccessTools.Field(pathDefType, "abilities");
            fLockedReason = AccessTools.Field(pathDefType, "lockedReason");
            fPathOrder = AccessTools.Field(pathDefType, "order");
            fHasAbilities = AccessTools.Field(pathDefType, "HasAbilities");
            mCanPawnUnlock = AccessTools.Method(pathDefType, "CanPawnUnlock", new[] { typeof(Pawn) });

            fPrereqs = AccessTools.Field(psycastExtType, "prerequisites");
            fAbLevel = AccessTools.Field(psycastExtType, "level");
            fAbOrder = AccessTools.Field(psycastExtType, "order");
            mGetPsyfocusUsed = AccessTools.Method(psycastExtType, "GetPsyfocusUsedByPawn", new[] { typeof(Pawn) });
            mGetEntropyUsed = AccessTools.Method(psycastExtType, "GetEntropyUsedByPawn", new[] { typeof(Pawn) });

            // Name-only lookups: single overloads in VEF, and the AbilityDef param is VEF.Abilities.AbilityDef.
            mHasAbility = AccessTools.Method(compAbilitiesType, "HasAbility");
            mGiveAbility = AccessTools.Method(compAbilitiesType, "GiveAbility");

            // CanPawnUse is a vanilla MeditationFocusDef method; CanUnlock is a VPE static extension
            // (MeditationUtilities.CanUnlock(this MeditationFocusDef, Pawn, out string)). Both optional.
            mCanPawnUse = AccessTools.Method(typeof(MeditationFocusDef), "CanPawnUse", new[] { typeof(Pawn) });
            if (meditationUtilsType != null)
                mCanUnlock = AccessTools.Method(meditationUtilsType, "CanUnlock");

            // Psysets (optional — a mod update could drop them without breaking the rest).
            psySetType = AccessTools.TypeByName("VanillaPsycastsExpanded.PsySet");
            dialogRenamePsysetType = AccessTools.TypeByName("VanillaPsycastsExpanded.UI.Dialog_RenamePsyset");
            fPsysets = AccessTools.Field(hediffType, "psysets");
            if (psySetType != null)
            {
                fPsysetName = AccessTools.Field(psySetType, "Name");
                fPsysetAbilities = AccessTools.Field(psySetType, "Abilities");
                mRemovePsySet = AccessTools.Method(hediffType, "RemovePsySet", new[] { psySetType });
            }
            PsysetsAvailable = psySetType != null && fPsysets != null && fPsysetName != null &&
                               fPsysetAbilities != null && mRemovePsySet != null;

            Available =
                fPoints != null && fLevel != null && fUnlockedPaths != null &&
                mSpentPoints != null && mImproveStats != null && mUnlockPath != null &&
                fAbilities != null && mCanPawnUnlock != null &&
                fPrereqs != null && fAbLevel != null &&
                mHasAbility != null && mGiveAbility != null;

            if (!Available)
                Log.Warning("[RimWorld Access] VPE detected but some members failed to resolve; psycast tab support disabled.");
        }

        // ===== Pawn-level resolution =====

        /// <summary>Returns the pawn's Hediff_PsycastAbilities instance, or null.</summary>
        public static object GetPsycastHediff(Pawn pawn)
        {
            if (!Available || pawn?.health?.hediffSet == null) return null;
            try
            {
                foreach (var h in pawn.health.hediffSet.hediffs)
                    if (hediffType.IsInstanceOfType(h)) return h;
            }
            catch (Exception ex) { LogOnce("GetPsycastHediff", ex); }
            return null;
        }

        // ===== VEF ability instances (gizmo cost/cooldown readout) =====

        /// <summary>
        /// Returns the <c>VEF.Abilities.Ability</c> carried by a VEF ability gizmo
        /// (<c>VEF.Abilities.Command_Ability</c>), or null for any other gizmo. VEF's command type
        /// is a parallel hierarchy to vanilla's (it extends <c>Command_Action</c>, not
        /// <c>RimWorld.Command_Ability</c>), so vanilla ability handling never matches it.
        /// </summary>
        public static object GetVefAbilityFromGizmo(Gizmo gizmo)
        {
            if (!VefAbilitiesAvailable || gizmo == null) return null;
            try
            {
                return vefCommandAbilityType.IsInstanceOfType(gizmo) ? fVefCmdAbility.GetValue(gizmo) : null;
            }
            catch (Exception ex) { LogOnce("GetVefAbilityFromGizmo", ex); return null; }
        }

        /// <summary>The ability's def (a <c>VEF.Abilities.AbilityDef</c>), or null.</summary>
        public static Def GetVefAbilityDef(object ability)
        {
            try { return ability != null ? fVefAbilityDef?.GetValue(ability) as Def : null; }
            catch { return null; }
        }

        /// <summary>The pawn casting the ability, or null.</summary>
        public static Pawn GetVefAbilityPawn(object ability)
        {
            try { return ability != null ? fVefAbilityPawn?.GetValue(ability) as Pawn : null; }
            catch { return null; }
        }

        /// <summary>
        /// Cooldown ticks still remaining, or 0 when the ability is ready. VEF stores
        /// <c>cooldown</c> as the absolute game tick the cooldown ends on.
        /// </summary>
        public static int GetVefAbilityCooldownTicksRemaining(object ability)
        {
            try
            {
                if (ability == null || fVefAbilityCooldown == null) return 0;
                int endTick = (int)fVefAbilityCooldown.GetValue(ability);
                int now = Find.TickManager?.TicksGame ?? 0;
                return endTick > now ? endTick - now : 0;
            }
            catch { return 0; }
        }

        /// <summary>Returns the pawn's VEF CompAbilities, or null.</summary>
        public static object GetCompAbilities(Pawn pawn)
        {
            if (!Available || pawn == null) return null;
            try
            {
                foreach (var c in pawn.AllComps)
                    if (compAbilitiesType.IsInstanceOfType(c)) return c;
            }
            catch (Exception ex) { LogOnce("GetCompAbilities", ex); }
            return null;
        }

        // ===== Hediff accessors =====

        public static int GetPoints(object hediff) => SafeInt(fPoints, hediff);
        public static int GetLevel(object hediff) => SafeInt(fLevel, hediff);
        public static float GetExperience(object hediff)
        {
            try { return hediff != null && fExperience != null ? (float)fExperience.GetValue(hediff) : 0f; }
            catch { return 0f; }
        }

        public static int GetExperienceRequiredForLevel(int level)
        {
            try { return mExpRequired != null ? (int)mExpRequired.Invoke(null, new object[] { level }) : 0; }
            catch { return 0; }
        }

        public static IList GetUnlockedPaths(object hediff)
        {
            try { return fUnlockedPaths?.GetValue(hediff) as IList; }
            catch { return null; }
        }

        public static bool IsPathUnlocked(object hediff, Def path)
        {
            var list = GetUnlockedPaths(hediff);
            if (list == null) return false;
            foreach (var p in list) if (ReferenceEquals(p, path)) return true;
            return false;
        }

        public static IList GetUnlockedFoci(object hediff)
        {
            try { return fUnlockedFoci?.GetValue(hediff) as IList; }
            catch { return null; }
        }

        /// <summary>Spends <paramref name="count"/> points and applies a stat upgrade.</summary>
        public static void ImproveStats(object hediff, int count = 1)
        {
            try
            {
                mSpentPoints.Invoke(hediff, new object[] { count });
                mImproveStats.Invoke(hediff, new object[] { count });
            }
            catch (Exception ex) { LogOnce("ImproveStats", ex); }
        }

        /// <summary>Spends one point and unlocks the path.</summary>
        public static void UnlockPath(object hediff, Def path)
        {
            try
            {
                mSpentPoints.Invoke(hediff, new object[] { 1 });
                mUnlockPath.Invoke(hediff, new object[] { path });
            }
            catch (Exception ex) { LogOnce("UnlockPath", ex); }
        }

        /// <summary>Spends one point and unlocks the meditation focus.</summary>
        public static void UnlockFocus(object hediff, MeditationFocusDef focus)
        {
            try
            {
                mSpentPoints.Invoke(hediff, new object[] { 1 });
                mUnlockFocus.Invoke(hediff, new object[] { focus });
            }
            catch (Exception ex) { LogOnce("UnlockFocus", ex); }
        }

        // ===== Path accessors =====

        public static List<Def> GetAllPaths()
        {
            if (allPathsCache != null) return allPathsCache;
            allPathsCache = new List<Def>();
            if (!Available) return allPathsCache;
            try
            {
                var dbType = typeof(DefDatabase<>).MakeGenericType(pathDefType);
                var prop = AccessTools.Property(dbType, "AllDefsListForReading");
                if (prop?.GetValue(null) is IEnumerable defs)
                    foreach (var d in defs) if (d is Def def) allPathsCache.Add(def);
            }
            catch (Exception ex) { LogOnce("GetAllPaths", ex); }
            return allPathsCache;
        }

        /// <summary>Ability defs belonging to a path (elements are VEF.Abilities.AbilityDef, exposed as Def).</summary>
        public static List<Def> GetPathAbilities(Def path)
        {
            var result = new List<Def>();
            try
            {
                if (fAbilities?.GetValue(path) is IList list)
                    foreach (var a in list) if (a is Def d) result.Add(d);
            }
            catch (Exception ex) { LogOnce("GetPathAbilities", ex); }
            return result;
        }

        public static bool PathCanPawnUnlock(Def path, Pawn pawn)
        {
            try { return mCanPawnUnlock != null && (bool)mCanPawnUnlock.Invoke(path, new object[] { pawn }); }
            catch (Exception ex) { LogOnce("PathCanPawnUnlock", ex); return false; }
        }

        public static bool PathHasAbilities(Def path)
        {
            try { return fHasAbilities != null && (bool)fHasAbilities.GetValue(path); }
            catch { return true; }
        }

        public static string GetPathLockedReason(Def path)
        {
            try { return fLockedReason?.GetValue(path) as string; }
            catch { return null; }
        }

        public static int GetPathOrder(Def path)
        {
            try { return fPathOrder != null ? (int)fPathOrder.GetValue(path) : 0; }
            catch { return 0; }
        }

        // ===== Ability accessors =====

        private static object GetPsycastExt(Def abilityDef)
        {
            try
            {
                var exts = abilityDef?.modExtensions;
                if (exts == null) return null;
                foreach (var e in exts) if (psycastExtType.IsInstanceOfType(e)) return e;
            }
            catch (Exception ex) { LogOnce("GetPsycastExt", ex); }
            return null;
        }

        public static int GetAbilityLevel(Def abilityDef)
        {
            try
            {
                var ext = GetPsycastExt(abilityDef);
                return ext != null && fAbLevel != null ? (int)fAbLevel.GetValue(ext) : 0;
            }
            catch { return 0; }
        }

        public static int GetAbilityOrder(Def abilityDef)
        {
            try
            {
                var ext = GetPsycastExt(abilityDef);
                return ext != null && fAbOrder != null ? (int)fAbOrder.GetValue(ext) : 0;
            }
            catch { return 0; }
        }

        /// <summary>Psyfocus fraction this ability costs the pawn to cast (stat-scaled).</summary>
        public static float GetAbilityPsyfocusCost(Def abilityDef, Pawn pawn)
        {
            try
            {
                var ext = GetPsycastExt(abilityDef);
                return ext != null && mGetPsyfocusUsed != null ? (float)mGetPsyfocusUsed.Invoke(ext, new object[] { pawn }) : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>Neural heat (entropy) this ability generates when cast (stat-scaled).</summary>
        public static float GetAbilityNeuralHeat(Def abilityDef, Pawn pawn)
        {
            try
            {
                var ext = GetPsycastExt(abilityDef);
                return ext != null && mGetEntropyUsed != null ? (float)mGetEntropyUsed.Invoke(ext, new object[] { pawn }) : 0f;
            }
            catch { return 0f; }
        }

        public static float GetAbilityRange(Def abilityDef) => GetDefFloat(abilityDef, "range");
        public static float GetAbilityRadius(Def abilityDef) => GetDefFloat(abilityDef, "radius");

        private static float GetDefFloat(Def def, string name)
        {
            try
            {
                var f = AccessTools.Field(def.GetType(), name);
                return f != null && f.GetValue(def) is float v ? v : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>Prerequisite ability defs for an ability (may be empty).</summary>
        public static List<Def> GetAbilityPrerequisites(Def abilityDef)
        {
            var result = new List<Def>();
            try
            {
                var ext = GetPsycastExt(abilityDef);
                if (ext != null && fPrereqs?.GetValue(ext) is IList list)
                    foreach (var p in list) if (p is Def d) result.Add(d);
            }
            catch (Exception ex) { LogOnce("GetAbilityPrerequisites", ex); }
            return result;
        }

        // ===== CompAbilities accessors =====

        public static bool HasAbility(object comp, Def abilityDef)
        {
            try { return mHasAbility != null && (bool)mHasAbility.Invoke(comp, new object[] { abilityDef }); }
            catch (Exception ex) { LogOnce("HasAbility", ex); return false; }
        }

        public static void GiveAbility(object hediff, object comp, Def abilityDef)
        {
            try
            {
                mSpentPoints.Invoke(hediff, new object[] { 1 });
                mGiveAbility.Invoke(comp, new object[] { abilityDef });
            }
            catch (Exception ex) { LogOnce("GiveAbility", ex); }
        }

        /// <summary>
        /// Mirrors VPE's AbilityExtension_Psycast.PrereqsCompleted: true when the ability has no
        /// prerequisites, or the pawn already learned at least one of them.
        /// </summary>
        public static bool PrereqsCompleted(object comp, Def abilityDef)
        {
            var prereqs = GetAbilityPrerequisites(abilityDef);
            if (prereqs.Count == 0) return true;
            foreach (var p in prereqs) if (HasAbility(comp, p)) return true;
            return false;
        }

        // ===== Meditation focus accessors =====

        public static List<MeditationFocusDef> GetAllFoci()
        {
            var result = new List<MeditationFocusDef>();
            if (!Available) return result;
            try { result.AddRange(DefDatabase<MeditationFocusDef>.AllDefsListForReading); }
            catch (Exception ex) { LogOnce("GetAllFoci", ex); }
            return result;
        }

        public static bool FocusCanPawnUse(MeditationFocusDef focus, Pawn pawn)
        {
            try { return mCanPawnUse != null && (bool)mCanPawnUse.Invoke(focus, new object[] { pawn }); }
            catch (Exception ex) { LogOnce("FocusCanPawnUse", ex); return false; }
        }

        public static bool FocusCanUnlock(MeditationFocusDef focus, Pawn pawn, out string reason)
        {
            reason = null;
            if (mCanUnlock == null) return true; // no gate resolvable → let VPE's own UnlockMeditationFocus decide
            try
            {
                var args = new object[] { focus, pawn, null };
                bool result = (bool)mCanUnlock.Invoke(null, args);
                reason = args[2] as string;
                return result;
            }
            catch (Exception ex) { LogOnce("FocusCanUnlock", ex); return false; }
        }

        // ===== Psyset accessors =====

        public static IList GetPsysets(object hediff)
        {
            try { return fPsysets?.GetValue(hediff) as IList; }
            catch { return null; }
        }

        public static string GetPsysetName(object psyset)
        {
            try { return fPsysetName?.GetValue(psyset) as string; }
            catch { return null; }
        }

        public static int GetPsysetAbilityCount(object psyset)
        {
            try
            {
                if (fPsysetAbilities?.GetValue(psyset) is IEnumerable set)
                {
                    int n = 0;
                    foreach (var _ in set) n++;
                    return n;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Learned psycast abilities that a psyset may contain — path abilities the pawn owns.</summary>
        public static List<Def> GetLearnedPsycastAbilities(object comp)
        {
            var result = new List<Def>();
            try
            {
                foreach (var path in GetAllPaths())
                    foreach (var ab in GetPathAbilities(path))
                        if (HasAbility(comp, ab)) result.Add(ab);
            }
            catch (Exception ex) { LogOnce("GetLearnedPsycastAbilities", ex); }
            return result;
        }

        public static bool PsysetContains(object psyset, Def abilityDef)
        {
            try
            {
                var set = fPsysetAbilities?.GetValue(psyset);
                if (set == null) return false;
                EnsureSetMethods(set);
                return mSetContains != null && (bool)mSetContains.Invoke(set, new object[] { abilityDef });
            }
            catch (Exception ex) { LogOnce("PsysetContains", ex); return false; }
        }

        /// <summary>Toggles an ability def in/out of the psyset. Returns the new membership state.</summary>
        public static bool PsysetToggle(object psyset, Def abilityDef)
        {
            try
            {
                var set = fPsysetAbilities?.GetValue(psyset);
                if (set == null) return false;
                EnsureSetMethods(set);
                bool has = mSetContains != null && (bool)mSetContains.Invoke(set, new object[] { abilityDef });
                if (has) mSetRemove?.Invoke(set, new object[] { abilityDef });
                else mSetAdd?.Invoke(set, new object[] { abilityDef });
                return !has;
            }
            catch (Exception ex) { LogOnce("PsysetToggle", ex); return false; }
        }

        public static object CreatePsyset(object hediff, string name)
        {
            try
            {
                var ps = Activator.CreateInstance(psySetType);
                fPsysetName.SetValue(ps, name);
                (fPsysets.GetValue(hediff) as IList)?.Add(ps);
                return ps;
            }
            catch (Exception ex) { LogOnce("CreatePsyset", ex); return null; }
        }

        public static void RemovePsyset(object hediff, object psyset)
        {
            try { mRemovePsySet.Invoke(hediff, new[] { psyset }); }
            catch (Exception ex) { LogOnce("RemovePsyset", ex); }
        }

        /// <summary>Opens VPE's rename dialog (a vanilla Dialog_Rename&lt;T&gt;, already made accessible by RWA).</summary>
        public static void OpenRenamePsysetDialog(object psyset)
        {
            try
            {
                if (dialogRenamePsysetType == null) return;
                if (Activator.CreateInstance(dialogRenamePsysetType, psyset) is Window w)
                    Find.WindowStack.Add(w);
            }
            catch (Exception ex) { LogOnce("OpenRenamePsysetDialog", ex); }
        }

        private static void EnsureSetMethods(object set)
        {
            if (mSetAdd != null || set == null) return;
            var t = set.GetType();
            mSetAdd = AccessTools.Method(t, "Add");
            mSetRemove = AccessTools.Method(t, "Remove");
            mSetContains = AccessTools.Method(t, "Contains");
        }

        // ===== Internals =====

        private static int SafeInt(FieldInfo field, object instance)
        {
            try { return instance != null && field != null ? (int)field.GetValue(instance) : 0; }
            catch { return 0; }
        }

        private static readonly HashSet<string> loggedErrors = new HashSet<string>();
        private static void LogOnce(string where, Exception ex)
        {
            if (loggedErrors.Add(where))
                Log.Warning($"[RimWorld Access] VPEPsycastsReflection.{where} failed: {ex.Message}");
        }
    }
}
