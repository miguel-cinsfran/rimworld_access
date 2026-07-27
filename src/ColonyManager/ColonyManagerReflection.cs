using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Reflection facade for the optional third-party mod <b>Colony Manager Redux</b>
    /// (packageId <c>ilyvion.colonymanagerredux</c>). RimWorld Access must not take a
    /// hard assembly reference on an optional mod, so every type and member of Colony
    /// Manager is resolved by name through <see cref="AccessTools"/> the first time it is
    /// needed and cached. When the mod is not loaded (or its API has shifted) resolution
    /// fails softly: <see cref="Available"/> stays false and every accessor is a no-op, so
    /// the accessibility patches simply do nothing.
    ///
    /// The mod draws its entire manager window with a custom IMGUI framework
    /// (ilyvion.Laboratory.UI) rather than the vanilla <c>Widgets</c>/<c>Listing_Standard</c>
    /// primitives, so — unlike the Dialog_ModSettings widget-interception approach — there
    /// is nothing useful to shadow at the widget level. Instead we navigate the mod's own
    /// data model (Manager -> Tabs -> jobs) and drive changes by calling its own public
    /// members, so the mod reacts exactly as if the user had clicked.
    /// </summary>
    public static class ColonyManagerReflection
    {
        private static bool resolved;
        private static bool available;

        // Types
        public static Type MainTabWindowType { get; private set; }
        private static Type managerType;
        private static Type tabType;
        private static Type jobType;
        private static Type jobTrackerType;
        private static Type triggerThresholdType;

        // Manager
        private static MethodInfo managerFor;              // static Manager For(Map)
        private static PropertyInfo managerTabsProp;       // IReadOnlyList<ManagerTab> Tabs
        private static PropertyInfo managerJobTrackerProp; // JobTracker JobTracker

        // MainTabWindow_Manager
        private static PropertyInfo currentTabProp;        // static ManagerTab CurrentTab
        private static MethodInfo goToMethod;              // static void GoTo(ManagerTab, ManagerJob)

        // JobTracker
        private static PropertyInfo jobTrackerJobsProp;    // IEnumerable<ManagerJob> Jobs

        // ManagerTab
        private static PropertyInfo tabLabelProp;
        private static PropertyInfo tabEnabledProp;
        private static PropertyInfo tabShowProp;
        private static PropertyInfo tabDisabledReasonProp;
        private static PropertyInfo tabSelectedProp;       // ManagerJob? Selected { get; set; }

        // ManagerJob
        private static PropertyInfo jobLabelProp;
        private static PropertyInfo jobIsSuspendedProp;    // get/set
        private static PropertyInfo jobIsCompletedProp;
        private static PropertyInfo jobIsManagedProp;
        private static PropertyInfo jobTriggerProp;
        private static PropertyInfo jobTargetsLabelProp;
        private static PropertyInfo jobTabProp;
        private static MethodInfo jobNotifyTargetsChanged;

        // Actions (best-effort — each degrades to a no-op if unresolved)
        private static MethodInfo tabMakeNewJob;           // ManagerJob MakeNewJob(params object[])
        private static MethodInfo jobTrackerAddMethod;     // void Add(ManagerJob)
        private static MethodInfo jobTrackerDeleteMethod;  // void Delete(ManagerJob, bool)
        private static MethodInfo tabIncreasePriority;     // void IncreasePriority(JobTracker, ManagerJob)
        private static MethodInfo tabDecreasePriority;     // void DecreasePriority(JobTracker, ManagerJob)

        // Trigger_Threshold (optional — not every job has one)
        private static PropertyInfo thresholdTargetCountProp;
        private static PropertyInfo thresholdTargetLabelProp;
        private static PropertyInfo thresholdMaxProp;      // int MaxUpperThreshold
        private static MethodInfo thresholdGetCurrentCount;    // int GetCurrentCount(bool)
        private static MethodInfo thresholdDoesCountMeetTarget; // bool DoesCountMeetTarget(int)

        // Livestock (ManagerJob_Livestock + Trigger_PawnKind) — per-animal population targets
        private static Type livestockJobType;
        private static Type pawnKindTriggerType;
        private static PropertyInfo livestockTriggerPawnKindProp; // Trigger_PawnKind TriggerPawnKind
        private static PropertyInfo pawnKindCountsProp;           // int[] Counts (current)
        private static FieldInfo pawnKindCountTargetsField;       // int[] CountTargets (writable)

        /// <summary>True once every required Colony Manager member has been resolved.</summary>
        public static bool Available
        {
            get
            {
                EnsureResolved();
                return available;
            }
        }

        private static void EnsureResolved()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;

            try
            {
                MainTabWindowType = AccessTools.TypeByName("ColonyManagerRedux.MainTabWindow_Manager");
                managerType = AccessTools.TypeByName("ColonyManagerRedux.Manager");
                tabType = AccessTools.TypeByName("ColonyManagerRedux.ManagerTab");
                jobType = AccessTools.TypeByName("ColonyManagerRedux.ManagerJob");
                jobTrackerType = AccessTools.TypeByName("ColonyManagerRedux.JobTracker");
                triggerThresholdType = AccessTools.TypeByName("ColonyManagerRedux.Trigger_Threshold");

                if (MainTabWindowType == null || managerType == null || tabType == null
                    || jobType == null || jobTrackerType == null)
                {
                    // Mod not present (or renamed). Stay unavailable, quietly.
                    return;
                }

                managerFor = AccessTools.Method(managerType, "For", new[] { typeof(Map) });
                managerTabsProp = AccessTools.Property(managerType, "Tabs");
                managerJobTrackerProp = AccessTools.Property(managerType, "JobTracker");

                currentTabProp = AccessTools.Property(MainTabWindowType, "CurrentTab");
                goToMethod = AccessTools.Method(MainTabWindowType, "GoTo", new[] { tabType, jobType });

                jobTrackerJobsProp = AccessTools.Property(jobTrackerType, "Jobs");

                tabLabelProp = AccessTools.Property(tabType, "Label");
                tabEnabledProp = AccessTools.Property(tabType, "Enabled");
                tabShowProp = AccessTools.Property(tabType, "Show");
                tabDisabledReasonProp = AccessTools.Property(tabType, "DisabledReason");
                tabSelectedProp = AccessTools.Property(tabType, "Selected");

                jobLabelProp = AccessTools.Property(jobType, "Label");
                jobIsSuspendedProp = AccessTools.Property(jobType, "IsSuspended");
                jobIsCompletedProp = AccessTools.Property(jobType, "IsCompleted");
                jobIsManagedProp = AccessTools.Property(jobType, "IsManaged");
                jobTriggerProp = AccessTools.Property(jobType, "Trigger");
                jobTargetsLabelProp = AccessTools.Property(jobType, "TargetsLabel");
                jobTabProp = AccessTools.Property(jobType, "Tab");

                if (triggerThresholdType != null)
                {
                    thresholdTargetCountProp = AccessTools.Property(triggerThresholdType, "TargetCount");
                    thresholdTargetLabelProp = AccessTools.Property(triggerThresholdType, "TargetLabel");
                    thresholdMaxProp = AccessTools.Property(triggerThresholdType, "MaxUpperThreshold");
                    thresholdGetCurrentCount = AccessTools.Method(triggerThresholdType, "GetCurrentCount", new[] { typeof(bool) });
                    thresholdDoesCountMeetTarget = AccessTools.Method(triggerThresholdType, "DoesCountMeetTarget", new[] { typeof(int) });
                }

                // Livestock model (optional — namespace ColonyManagerRedux.Managers). Best-effort:
                // if any member is missing the Livestock detail simply won't be offered.
                livestockJobType = AccessTools.TypeByName("ColonyManagerRedux.Managers.ManagerJob_Livestock");
                pawnKindTriggerType = AccessTools.TypeByName("ColonyManagerRedux.Managers.Trigger_PawnKind");
                if (livestockJobType != null)
                {
                    livestockTriggerPawnKindProp = AccessTools.Property(livestockJobType, "TriggerPawnKind");
                }
                if (pawnKindTriggerType != null)
                {
                    pawnKindCountsProp = AccessTools.Property(pawnKindTriggerType, "Counts");
                    pawnKindCountTargetsField = AccessTools.Field(pawnKindTriggerType, "CountTargets");
                }

                // Action members — resolved best-effort; features that need them degrade to
                // no-ops (and log) rather than blocking the whole module.
                jobNotifyTargetsChanged = AccessTools.Method(jobType, "Notify_TargetsChanged");
                tabMakeNewJob = AccessTools.Method(tabType, "MakeNewJob", new[] { typeof(object[]) });
                jobTrackerAddMethod = AccessTools.Method(jobTrackerType, "Add", new[] { jobType });
                jobTrackerDeleteMethod = AccessTools.Method(jobTrackerType, "Delete", new[] { jobType, typeof(bool) });
                tabIncreasePriority = AccessTools.Method(tabType, "IncreasePriority", new[] { jobTrackerType, jobType });
                tabDecreasePriority = AccessTools.Method(tabType, "DecreasePriority", new[] { jobTrackerType, jobType });

                available = managerFor != null
                    && managerTabsProp != null
                    && managerJobTrackerProp != null
                    && currentTabProp != null
                    && goToMethod != null
                    && jobTrackerJobsProp != null
                    && tabLabelProp != null
                    && tabSelectedProp != null
                    && jobLabelProp != null
                    && jobIsSuspendedProp != null
                    && jobTargetsLabelProp != null
                    && jobTabProp != null;

                if (available)
                {
                    Log.Message("[RimWorld Access] Colony Manager Redux detected; accessibility enabled.");
                }
                else
                {
                    Log.Warning("[RimWorld Access] Colony Manager Redux found but its API could not be fully resolved; skipping accessibility support.");
                }
            }
            catch (Exception e)
            {
                available = false;
                Log.Warning($"[RimWorld Access] Colony Manager reflection init failed: {e}");
            }
        }

        /// <summary>
        /// Toggle the Colony Manager main-tab window through its own MainButtonDef worker
        /// (opens if closed, closes if open) — the same path a mouse click on the bottom-bar
        /// "manager" button takes. The button ships with no hotkey, so this is how a keyboard
        /// user reaches it (bound to F5 in UnifiedKeyboardPatch). Returns false if the mod's
        /// button def isn't present.
        /// </summary>
        public static bool ToggleManagerWindow()
        {
            EnsureResolved();
            if (!available)
            {
                return false;
            }
            var def = DefDatabase<MainButtonDef>.GetNamedSilentFail("ColonyManagerRedux_Manager");
            if (def == null || def.Worker == null)
            {
                ColonyManagerDebug.Warn("MainButtonDef 'ColonyManagerRedux_Manager' not found; cannot open manager window.");
                return false;
            }
            def.Worker.Activate();
            return true;
        }

        /// <summary>True if the given window instance is a Colony Manager main-tab window.</summary>
        public static bool IsManagerWindow(object window)
        {
            EnsureResolved();
            return window != null && MainTabWindowType != null && MainTabWindowType.IsInstanceOfType(window);
        }

        // ---------------------------------------------------------------
        // Manager / tabs
        // ---------------------------------------------------------------

        /// <summary>The Manager MapComponent for the current map, or null.</summary>
        public static object GetManagerForCurrentMap()
        {
            EnsureResolved();
            var map = Find.CurrentMap;
            if (map == null || managerFor == null)
            {
                return null;
            }
            return managerFor.Invoke(null, new object[] { map });
        }

        /// <summary>All tabs on this manager the game currently shows (Show == true).</summary>
        public static List<object> GetVisibleTabs(object manager)
        {
            var result = new List<object>();
            if (manager == null || managerTabsProp == null)
            {
                return result;
            }
            if (managerTabsProp.GetValue(manager) is IEnumerable list)
            {
                foreach (var tab in list)
                {
                    if (tab != null && TabShow(tab))
                    {
                        result.Add(tab);
                    }
                }
            }
            return result;
        }

        /// <summary>The tab currently open in the manager window (static CurrentTab).</summary>
        public static object GetCurrentTab()
        {
            EnsureResolved();
            return currentTabProp?.GetValue(null);
        }

        /// <summary>
        /// Switch the open tab through the mod's own GoTo, which fires the tab's
        /// pre/post open/close hooks exactly as a mouse click would.
        /// </summary>
        public static void GoToTab(object tab)
        {
            if (goToMethod == null || tab == null)
            {
                return;
            }
            goToMethod.Invoke(null, new object[] { tab, null });
        }

        public static string TabLabel(object tab) => tabLabelProp?.GetValue(tab) as string ?? "";
        public static bool TabEnabled(object tab) => tabEnabledProp?.GetValue(tab) is bool b && b;
        public static bool TabShow(object tab) => tabShowProp == null || (tabShowProp.GetValue(tab) is bool b && b);
        public static string TabDisabledReason(object tab) => tabDisabledReasonProp?.GetValue(tab) as string ?? "";

        public static object GetSelectedJob(object tab) => tabSelectedProp?.GetValue(tab);
        public static void SetSelectedJob(object tab, object job) => tabSelectedProp?.SetValue(tab, job);

        // ---------------------------------------------------------------
        // Jobs
        // ---------------------------------------------------------------

        /// <summary>
        /// The jobs that belong to a given tab, in the tracker's current order. We read
        /// the manager-wide job list and keep those whose own Tab reference matches, which
        /// is exactly the set the tab draws in its job list.
        /// </summary>
        public static List<object> GetJobsForTab(object manager, object tab)
        {
            var result = new List<object>();
            if (manager == null || tab == null || managerJobTrackerProp == null || jobTrackerJobsProp == null)
            {
                return result;
            }
            var tracker = managerJobTrackerProp.GetValue(manager);
            if (tracker == null)
            {
                return result;
            }
            if (jobTrackerJobsProp.GetValue(tracker) is IEnumerable jobs)
            {
                foreach (var job in jobs)
                {
                    if (job == null)
                    {
                        continue;
                    }
                    var jobTab = jobTabProp?.GetValue(job);
                    if (ReferenceEquals(jobTab, tab))
                    {
                        result.Add(job);
                    }
                }
            }
            return result;
        }

        public static string JobLabel(object job) => jobLabelProp?.GetValue(job) as string ?? "";
        public static string JobTargetsLabel(object job) => jobTargetsLabelProp?.GetValue(job) as string ?? "";
        public static bool JobIsSuspended(object job) => jobIsSuspendedProp?.GetValue(job) is bool b && b;
        public static bool JobIsCompleted(object job) => jobIsCompletedProp?.GetValue(job) is bool b && b;
        public static bool JobIsManaged(object job) => jobIsManagedProp?.GetValue(job) is bool b && b;

        /// <summary>Toggle a job's suspended flag through its own setter. Returns the new state.</summary>
        public static bool ToggleJobSuspended(object job)
        {
            if (jobIsSuspendedProp == null || job == null)
            {
                return false;
            }
            bool current = JobIsSuspended(job);
            bool next = !current;
            if (jobIsSuspendedProp.CanWrite)
            {
                jobIsSuspendedProp.SetValue(job, next);
            }
            return next;
        }

        // ---------------------------------------------------------------
        // Job actions (create / delete / reorder)
        // ---------------------------------------------------------------

        private static object GetJobTracker(object manager)
        {
            return manager == null ? null : managerJobTrackerProp?.GetValue(manager);
        }

        /// <summary>
        /// Create a fresh job of the tab's type, mark it managed and add it to the tracker —
        /// exactly what the tab's own "Manage" button does. Returns the new job, or null.
        /// </summary>
        public static object CreateAndAddManagedJob(object manager, object tab)
        {
            if (manager == null || tab == null || tabMakeNewJob == null || jobTrackerAddMethod == null)
            {
                ColonyManagerDebug.Warn("CreateAndAddManagedJob: required members unresolved");
                return null;
            }
            try
            {
                var job = tabMakeNewJob.Invoke(tab, new object[] { new object[0] });
                if (job == null)
                {
                    return null;
                }
                if (jobIsManagedProp != null && jobIsManagedProp.CanWrite)
                {
                    jobIsManagedProp.SetValue(job, true);
                }
                var tracker = GetJobTracker(manager);
                if (tracker == null)
                {
                    return null;
                }
                jobTrackerAddMethod.Invoke(tracker, new[] { job });
                return job;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("CreateAndAddManagedJob failed", e);
                return null;
            }
        }

        /// <summary>Delete a job through the tracker (its own Delete, with cleanup).</summary>
        public static bool DeleteJob(object manager, object job)
        {
            if (manager == null || job == null || jobTrackerDeleteMethod == null)
            {
                return false;
            }
            try
            {
                var tracker = GetJobTracker(manager);
                if (tracker == null)
                {
                    return false;
                }
                jobTrackerDeleteMethod.Invoke(tracker, new[] { job, (object)true });
                return true;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("DeleteJob failed", e);
                return false;
            }
        }

        /// <summary>
        /// Move a job's priority. direction &lt; 0 raises it (toward the top / done sooner),
        /// direction &gt; 0 lowers it. Uses the tab's own priority methods.
        /// </summary>
        public static bool MoveJobPriority(object manager, object tab, object job, int direction)
        {
            var method = direction < 0 ? tabIncreasePriority : tabDecreasePriority;
            if (manager == null || tab == null || job == null || method == null)
            {
                return false;
            }
            try
            {
                var tracker = GetJobTracker(manager);
                if (tracker == null)
                {
                    return false;
                }
                method.Invoke(tab, new[] { tracker, job });
                return true;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("MoveJobPriority failed", e);
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Threshold ("how much you want") — the core knob of resource jobs
        // ---------------------------------------------------------------

        private static object GetThreshold(object job)
        {
            if (job == null || jobTriggerProp == null || triggerThresholdType == null)
            {
                return null;
            }
            var trigger = jobTriggerProp.GetValue(job);
            return (trigger != null && triggerThresholdType.IsInstanceOfType(trigger)) ? trigger : null;
        }

        /// <summary>True if this job's trigger is a threshold ("maintain N of X") trigger.</summary>
        public static bool JobHasThreshold(object job) => GetThreshold(job) != null;

        public static int GetThresholdTarget(object job)
        {
            var t = GetThreshold(job);
            if (t == null || thresholdTargetCountProp == null)
            {
                return 0;
            }
            return thresholdTargetCountProp.GetValue(t) is int i ? i : 0;
        }

        public static int GetThresholdMax(object job)
        {
            var t = GetThreshold(job);
            if (t == null || thresholdMaxProp == null)
            {
                return 0;
            }
            return thresholdMaxProp.GetValue(t) is int i ? i : 0;
        }

        public static string GetThresholdLabel(object job)
        {
            var t = GetThreshold(job);
            if (t == null || thresholdTargetLabelProp == null)
            {
                return "";
            }
            return thresholdTargetLabelProp.GetValue(t) as string ?? "";
        }

        /// <summary>
        /// The amount currently on the map counted against this job's threshold (e.g. how much
        /// wood/steel/meat you actually have right now), via the trigger's own GetCurrentCount.
        /// Returns -1 when the count can't be read (older API, or not a threshold job) so callers
        /// can omit it rather than announce a wrong 0.
        /// </summary>
        public static int GetThresholdCurrentCount(object job)
        {
            var t = GetThreshold(job);
            if (t == null || thresholdGetCurrentCount == null)
            {
                return -1;
            }
            try
            {
                return thresholdGetCurrentCount.Invoke(t, new object[] { false }) is int i ? i : -1;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("GetThresholdCurrentCount failed", e);
                return -1;
            }
        }

        /// <summary>
        /// True if the current amount already satisfies the job's target ("keep N").
        /// <c>DoesCountMeetTarget</c> takes the amount to test — feeding it the target instead
        /// compares the target against itself and is always true, so the current count is what
        /// must go in.
        /// </summary>
        public static bool ThresholdMeetsTarget(object job)
        {
            var t = GetThreshold(job);
            if (t == null || thresholdDoesCountMeetTarget == null)
            {
                return false;
            }
            try
            {
                int current = GetThresholdCurrentCount(job);
                if (current < 0)
                {
                    return false;
                }
                return thresholdDoesCountMeetTarget.Invoke(t, new object[] { current }) is bool b && b;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("ThresholdMeetsTarget failed", e);
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Livestock (per-animal population targets via Trigger_PawnKind)
        // ---------------------------------------------------------------

        /// <summary>True if this job is a Livestock job with a resolvable pawn-kind trigger.</summary>
        public static bool IsLivestockJob(object job)
        {
            return job != null
                && livestockJobType != null
                && livestockJobType.IsInstanceOfType(job)
                && livestockTriggerPawnKindProp != null
                && pawnKindCountTargetsField != null;
        }

        private static object GetPawnKindTrigger(object job)
        {
            if (!IsLivestockJob(job))
            {
                return null;
            }
            return livestockTriggerPawnKindProp.GetValue(job);
        }

        /// <summary>The four target populations [adult female, adult male, juvenile female, juvenile male].</summary>
        public static int[] GetLivestockTargets(object job)
        {
            var trig = GetPawnKindTrigger(job);
            if (trig == null)
            {
                return null;
            }
            try
            {
                return pawnKindCountTargetsField.GetValue(trig) as int[];
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("GetLivestockTargets failed", e);
                return null;
            }
        }

        /// <summary>The four current populations, aligned with <see cref="GetLivestockTargets"/>.</summary>
        public static int[] GetLivestockCurrent(object job)
        {
            var trig = GetPawnKindTrigger(job);
            if (trig == null || pawnKindCountsProp == null)
            {
                return null;
            }
            try
            {
                return pawnKindCountsProp.GetValue(trig) as int[];
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("GetLivestockCurrent failed", e);
                return null;
            }
        }

        /// <summary>Set one category's target population (clamped ≥ 0) and refresh the job's targets.</summary>
        public static int SetLivestockTarget(object job, int index, int value)
        {
            var trig = GetPawnKindTrigger(job);
            if (trig == null)
            {
                return 0;
            }
            try
            {
                var targets = pawnKindCountTargetsField.GetValue(trig) as int[];
                if (targets == null || index < 0 || index >= targets.Length)
                {
                    return 0;
                }
                if (value < 0)
                {
                    value = 0;
                }
                targets[index] = value;
                pawnKindCountTargetsField.SetValue(trig, targets);
                // The Livestock tab keeps a string buffer (_newCounts) and, every frame it draws,
                // parses it back into CountTargets (ManagerTab_Livestock.DoCountField). Writing only
                // CountTargets is therefore undone on the next frame — we must mirror the change into
                // that buffer so the mod's own per-frame sync reproduces our value.
                SyncLivestockCountBuffer(job, targets);
                jobNotifyTargetsChanged?.Invoke(job, null);
                return value;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("SetLivestockTarget failed", e);
                return 0;
            }
        }

        /// <summary>
        /// Rewrite the Livestock tab's <c>_newCounts</c> string buffer from the job's current
        /// CountTargets, so the mod's per-frame re-parse keeps (not clobbers) our edit. The buffer
        /// is only otherwise refreshed on job selection (PostSelect), so it can go stale between
        /// keystrokes; rewriting the whole array keeps every category consistent.
        /// </summary>
        private static void SyncLivestockCountBuffer(object job, int[] targets)
        {
            try
            {
                var tab = jobTabProp?.GetValue(job);
                if (tab == null || targets == null)
                {
                    return;
                }
                var f = AccessTools.Field(tab.GetType(), "_newCounts");
                if (f == null)
                {
                    return;
                }
                var buf = f.GetValue(tab) as string[];
                if (buf == null || buf.Length != targets.Length)
                {
                    buf = new string[targets.Length];
                }
                for (int i = 0; i < targets.Length; i++)
                {
                    buf[i] = targets[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                f.SetValue(tab, buf);
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("SyncLivestockCountBuffer failed", e);
            }
        }

        /// <summary>
        /// Set a threshold job's target count (clamped to [0, MaxUpperThreshold]) and refresh
        /// its cached targets label. Returns the value actually applied.
        /// </summary>
        public static int SetThresholdTarget(object job, int value)
        {
            var t = GetThreshold(job);
            if (t == null || thresholdTargetCountProp == null || !thresholdTargetCountProp.CanWrite)
            {
                return GetThresholdTarget(job);
            }
            try
            {
                int max = GetThresholdMax(job);
                if (max > 0 && value > max)
                {
                    value = max;
                }
                if (value < 0)
                {
                    value = 0;
                }
                thresholdTargetCountProp.SetValue(t, value);
                jobNotifyTargetsChanged?.Invoke(job, null);
                return value;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("SetThresholdTarget failed", e);
                return GetThresholdTarget(job);
            }
        }

        // ---------------------------------------------------------------
        // Generic per-job member access (Phase 3 detail settings). All best-effort: a
        // name that doesn't resolve makes the corresponding setting drop out, never throw.
        // ---------------------------------------------------------------

        private static FieldInfo FieldOf(object obj, string name) =>
            obj == null || name == null ? null : AccessTools.Field(obj.GetType(), name);

        private static PropertyInfo PropOf(object obj, string name) =>
            obj == null || name == null ? null : AccessTools.Property(obj.GetType(), name);

        /// <summary>True if a bool field, or a readable/writable bool property, of this name exists.</summary>
        public static bool HasBoolMember(object obj, string name)
        {
            var f = FieldOf(obj, name);
            if (f != null && f.FieldType == typeof(bool))
            {
                return true;
            }
            var p = PropOf(obj, name);
            return p != null && p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite;
        }

        public static bool GetBoolMember(object obj, string name)
        {
            try
            {
                var f = FieldOf(obj, name);
                if (f != null && f.FieldType == typeof(bool))
                {
                    return (bool)f.GetValue(obj);
                }
                var p = PropOf(obj, name);
                if (p != null && p.PropertyType == typeof(bool) && p.CanRead)
                {
                    return (bool)p.GetValue(obj);
                }
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error($"GetBoolMember '{name}'", e);
            }
            return false;
        }

        public static void SetBoolMember(object obj, string name, bool value)
        {
            try
            {
                var f = FieldOf(obj, name);
                if (f != null && f.FieldType == typeof(bool))
                {
                    f.SetValue(obj, value);
                    return;
                }
                var p = PropOf(obj, name);
                if (p != null && p.PropertyType == typeof(bool) && p.CanWrite)
                {
                    p.SetValue(obj, value);
                }
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error($"SetBoolMember '{name}'", e);
            }
        }

        /// <summary>True if a field or property of this name exists (any type — used for the Area member).</summary>
        public static bool HasMember(object obj, string name) =>
            FieldOf(obj, name) != null || PropOf(obj, name) != null;

        public static object GetObjectMember(object obj, string name)
        {
            try
            {
                var f = FieldOf(obj, name);
                if (f != null)
                {
                    return f.GetValue(obj);
                }
                var p = PropOf(obj, name);
                if (p != null && p.CanRead)
                {
                    return p.GetValue(obj);
                }
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error($"GetObjectMember '{name}'", e);
            }
            return null;
        }

        public static void SetObjectMember(object obj, string name, object value)
        {
            try
            {
                var f = FieldOf(obj, name);
                if (f != null)
                {
                    f.SetValue(obj, value);
                    return;
                }
                var p = PropOf(obj, name);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(obj, value);
                }
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error($"SetObjectMember '{name}'", e);
            }
        }

        // ---------------------------------------------------------------
        // Allowed-def lists (which animals to hunt / trees to chop / etc.)
        // ---------------------------------------------------------------

        /// <summary>True if all three members of a def-list spec resolve on this job.</summary>
        public static bool CanBuildDefList(object job, string allMember, string allowedMember, string setMethod)
        {
            return HasMember(job, allMember)
                && HasMember(job, allowedMember)
                && job != null
                && AccessTools.Method(job.GetType(), setMethod) != null;
        }

        /// <summary>The full list of options (Def objects) for a def-list.</summary>
        public static List<object> GetDefListAll(object job, string allMember)
        {
            var result = new List<object>();
            if (GetObjectMember(job, allMember) is IEnumerable list)
            {
                foreach (var def in list)
                {
                    if (def != null)
                    {
                        result.Add(def);
                    }
                }
            }
            return result;
        }

        /// <summary>True if the given def is in the job's allowed set.</summary>
        public static bool IsDefAllowed(object job, string allowedMember, object def)
        {
            if (GetObjectMember(job, allowedMember) is IEnumerable set)
            {
                foreach (var item in set)
                {
                    if (ReferenceEquals(item, def))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Allow/disallow a def through the job's own set-allowed method.</summary>
        public static bool SetDefAllowed(object job, string setMethod, object def, bool allow)
        {
            if (job == null)
            {
                return false;
            }
            var m = AccessTools.Method(job.GetType(), setMethod);
            if (m == null)
            {
                return false;
            }
            try
            {
                int paramCount = m.GetParameters().Length;
                object[] args = paramCount >= 3
                    ? new object[] { def, allow, true }
                    : new object[] { def, allow };
                m.Invoke(job, args);
                return true;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error($"SetDefAllowed '{setMethod}'", e);
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Job creation that needs an up-front choice (which animal / what to produce)
        // ---------------------------------------------------------------

        // Colony Manager's Livestock and Production tabs each have an "Available" panel the
        // player picks from before a job exists: a list of tameable animals, or of recipes
        // craftable on the map's workbenches. Both are plain Def lists the tab caches in a
        // private field and repopulates in Refresh(), so we can offer exactly the same choices
        // as a keyboard list instead of refusing to create these job types.
        private static readonly Dictionary<string, string> PickerFieldByTab =
            new Dictionary<string, string>
            {
                { "ManagerTab_Livestock", "_availablePawnKinds" },
                { "ManagerTab_Production", "_availableRecipes" },
            };

        /// <summary>
        /// True when creating a job on this tab requires choosing a Def first. Creating one
        /// blind would add an invalid job (a pawn-kind-less Livestock job even throws when the
        /// save is reloaded), so these always go through the picker.
        /// </summary>
        public static bool TabNeedsChoiceToCreate(object tab) =>
            tab != null && PickerFieldByTab.ContainsKey(tab.GetType().Name);

        /// <summary>
        /// The Defs offered for a new job on this tab (tameable animals / craftable recipes),
        /// refreshed from the map first so the list matches what the mod would draw right now.
        /// Empty when the tab needs no choice or the mod's API has shifted.
        /// </summary>
        public static List<object> GetCreationChoices(object tab)
        {
            var result = new List<object>();
            if (tab == null || !PickerFieldByTab.TryGetValue(tab.GetType().Name, out string fieldName))
            {
                return result;
            }
            try
            {
                // Refresh() is what the tab itself calls before drawing the available list.
                AccessTools.Method(tab.GetType(), "Refresh")?.Invoke(tab, null);
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("GetCreationChoices: Refresh failed", e);
            }
            if (GetObjectMember(tab, fieldName) is IEnumerable list)
            {
                foreach (var def in list)
                {
                    if (def != null)
                    {
                        result.Add(def);
                    }
                }
            }
            ColonyManagerDebug.Log($"GetCreationChoices({tab.GetType().Name}): {result.Count} option(s)");
            return result;
        }

        /// <summary>
        /// Create a job of the tab's type around the chosen Def, mark it managed and add it to
        /// the tracker. Mirrors how each tab builds the job itself: Livestock takes the pawn
        /// kind as a MakeNewJob argument, while Production makes an argument-less job and then
        /// assigns the recipe — whose setter is what wires the job's threshold to count that
        /// recipe's product. Returns the new job, or null.
        /// </summary>
        public static object CreateAndAddManagedJob(object manager, object tab, object choice)
        {
            if (manager == null || tab == null || choice == null
                || tabMakeNewJob == null || jobTrackerAddMethod == null)
            {
                ColonyManagerDebug.Warn("CreateAndAddManagedJob(choice): required members unresolved");
                return null;
            }
            try
            {
                bool passAsArgument = tab.GetType().Name == "ManagerTab_Livestock";
                var job = tabMakeNewJob.Invoke(tab, new object[]
                {
                    passAsArgument ? new[] { choice } : new object[0]
                });
                if (job == null)
                {
                    ColonyManagerDebug.Warn("CreateAndAddManagedJob(choice): MakeNewJob returned null");
                    return null;
                }

                if (!passAsArgument && !SetProductionRecipe(job, choice))
                {
                    // Without a recipe a production job is meaningless and its threshold counts
                    // nothing — drop it rather than adding a broken job to the colony.
                    ColonyManagerDebug.Warn("CreateAndAddManagedJob(choice): could not assign recipe, discarding job");
                    return null;
                }

                if (jobIsManagedProp != null && jobIsManagedProp.CanWrite)
                {
                    jobIsManagedProp.SetValue(job, true);
                }
                var tracker = GetJobTracker(manager);
                if (tracker == null)
                {
                    return null;
                }
                jobTrackerAddMethod.Invoke(tracker, new[] { job });
                return job;
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("CreateAndAddManagedJob(choice) failed", e);
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Production specifics (recipe + mode)
        // ---------------------------------------------------------------

        public static bool IsProductionJob(object job) =>
            job != null && job.GetType().Name == "ManagerJob_Production";

        /// <summary>The RecipeDef a production job makes, or null.</summary>
        public static Def GetProductionRecipe(object job) => GetObjectMember(job, "Recipe") as Def;

        /// <summary>
        /// Point a production job at a different recipe. The mod's own Recipe setter re-wires
        /// the job's threshold filter to count the new recipe's product, so this is the whole
        /// operation — nothing else needs updating.
        /// </summary>
        public static bool SetProductionRecipe(object job, object recipe)
        {
            var prop = PropOf(job, "Recipe");
            if (prop == null || !prop.CanWrite || recipe == null)
            {
                return false;
            }
            try
            {
                prop.SetValue(job, recipe);
                return ReferenceEquals(GetProductionRecipe(job), recipe);
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("SetProductionRecipe failed", e);
                return false;
            }
        }

        /// <summary>
        /// The recipes the mod itself considers valid substitutes for this job's product
        /// (its own "swap recipe" list — e.g. the same blocks made at a different bench).
        /// </summary>
        public static List<object> GetRecipeSwapCandidates(object tab, object job)
        {
            var result = new List<object>();
            if (tab == null || job == null)
            {
                return result;
            }
            var m = AccessTools.Method(tab.GetType(), "ComputeRecipeSwapCandidates");
            if (m == null)
            {
                return result;
            }
            try
            {
                if (m.Invoke(tab, new[] { job }) is IEnumerable list)
                {
                    foreach (var r in list)
                    {
                        if (r != null)
                        {
                            result.Add(r);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error("GetRecipeSwapCandidates failed", e);
            }
            return result;
        }

        // ---- Generic enum member (production mode, and any future single-choice field) ----

        /// <summary>True if the named member exists and holds an enum value.</summary>
        public static bool HasEnumMember(object obj, string name)
        {
            var f = FieldOf(obj, name);
            if (f != null)
            {
                return f.FieldType.IsEnum;
            }
            var p = PropOf(obj, name);
            return p != null && p.PropertyType.IsEnum && p.CanRead && p.CanWrite;
        }

        /// <summary>The raw enum value name of the member (e.g. "MaintainStock"), or "".</summary>
        public static string GetEnumMemberName(object obj, string name) =>
            GetObjectMember(obj, name)?.ToString() ?? "";

        /// <summary>
        /// Step the enum member to the next/previous declared value, wrapping. Returns the new
        /// value's name, or "" if the member isn't a settable enum.
        /// </summary>
        public static string CycleEnumMember(object obj, string name, int direction)
        {
            if (!HasEnumMember(obj, name))
            {
                return "";
            }
            try
            {
                var current = GetObjectMember(obj, name);
                if (current == null)
                {
                    return "";
                }
                var enumType = current.GetType();
                var values = Enum.GetValues(enumType);
                if (values.Length == 0)
                {
                    return "";
                }
                int index = Array.IndexOf(values, current);
                if (index < 0)
                {
                    index = 0;
                }
                index = ((index + direction) % values.Length + values.Length) % values.Length;
                var next = values.GetValue(index);
                SetObjectMember(obj, name, next);
                return GetEnumMemberName(obj, name);
            }
            catch (Exception e)
            {
                ColonyManagerDebug.Error($"CycleEnumMember '{name}'", e);
                return "";
            }
        }
    }
}
