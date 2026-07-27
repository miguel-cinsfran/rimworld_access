using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Windowless keyboard menu overlaid on Colony Manager Redux's still-open
    /// <c>MainTabWindow_Manager</c>. The window keeps drawing underneath; we never take it
    /// over, we just navigate the mod's own data model and drive changes through its own
    /// members (see <see cref="ColonyManagerReflection"/>) so it reacts exactly as it would
    /// to a mouse.
    ///
    /// Five nested levels, each stepping back into the previous one with Left or Escape:
    ///  - <see cref="Level.Tabs"/>: the row of tab icons (Overview, Hunting, Forestry,
    ///    Livestock, Foraging, Mining, Power, Production, Logs, Import/Export, plus any
    ///    third-party tabs). These are icon-only in the vanilla UI, so a sighted-only user
    ///    relies on tooltips; here each announces its label, enabled state and job count.
    ///  - <see cref="Level.Jobs"/>: the selected tab's list of manager jobs, each announcing
    ///    its label, target and status. Enter opens a job, Space suspends/resumes it,
    ///    Ctrl+N creates one, Delete removes it, Ctrl+Up/Down reorders it.
    ///  - <see cref="Level.JobDetail"/>: that job's settings — how much to keep, where to
    ///    work, and the per-job-type toggles from <see cref="ColonyManagerJobSchema"/>.
    ///  - <see cref="Level.DefList"/>: the multi-select behind a settings row (which animals
    ///    to hunt, which trees to chop), where every entry toggles independently.
    ///  - <see cref="Level.Picker"/>: a single-choice list. Livestock and Production jobs are
    ///    meaningless until they know which animal or product they are for, so creating one
    ///    asks here first, using the mod's own list of available animals/recipes.
    /// </summary>
    public static class ColonyManagerState
    {
        private enum Level
        {
            Tabs,
            Jobs,
            JobDetail,
            DefList,
            Picker
        }

        private enum SettingKind
        {
            Threshold,
            Toggle,
            Area,
            DefList,
            /// <summary>A single Def chosen from a list (a production job's product).</summary>
            Choice,
            /// <summary>One of a fixed set of named modes, stepped with Left/Right.</summary>
            Cycle
        }

        /// <summary>What confirming a choice in the picker level does.</summary>
        private enum PickerPurpose
        {
            /// <summary>Create a new job of the current tab's type around the chosen Def.</summary>
            CreateJob,
            /// <summary>Repoint the open production job at the chosen recipe.</summary>
            ChangeRecipe
        }

        /// <summary>An editable setting shown in a job's detail level.</summary>
        private sealed class DetailSetting
        {
            public SettingKind Kind;
            public string Label;

            // Threshold / count (int, Left/Right adjusts). Also used for Livestock per-category
            // population targets, which share the "keep N, currently M" shape.
            public System.Func<int> GetInt;
            public System.Func<int, int> SetInt;   // returns the value actually applied
            public int Min;
            public int Max;
            public int Step;
            // Optional context for count settings: current stock/population and whether the
            // target is already met. Null when unavailable (older API) so it's simply omitted.
            public System.Func<int> GetCurrent;
            public System.Func<bool> GetMeets;

            // Toggle (bool)
            public System.Func<bool> GetBool;
            public System.Action<bool> SetBool;

            // Area (cycle through the map's areas + unrestricted)
            public System.Func<string> GetAreaLabel;
            public System.Action<int> CycleArea;   // direction -1 / +1

            // DefList (opens the def sub-level)
            public ColonyManagerJobSchema.Spec DefListSpec;
            public System.Func<int> GetAllowedCount;
            public System.Func<int> GetTotalCount;

            // Choice (a single Def, changed through the picker level)
            public System.Func<string> GetChoiceLabel;
            public System.Action OpenChoicePicker;

            // Cycle (a named mode stepped with Left/Right)
            public System.Func<string> GetCycleLabel;
            public System.Action<int> CycleValue;
        }

        public static bool IsActive { get; private set; }

        // The MainTabWindow_Manager instance we are bound to (held as object — its type
        // lives in the optional mod's assembly). Also usable as a Verse.Window for closing.
        private static object window;
        private static object manager;

        private static Level level;

        private static readonly List<object> tabs = new List<object>();
        private static int tabIndex;

        private static readonly List<object> jobs = new List<object>();
        private static int jobIndex;

        // Job detail level
        private static object detailJob;
        private static readonly List<DetailSetting> detailSettings = new List<DetailSetting>();
        private static int detailIndex;

        // Def-list sub-level (which animals to hunt / trees to chop / etc.)
        private static ColonyManagerJobSchema.Spec defListSpec;
        private static readonly List<object> defListItems = new List<object>();
        private static int defListIndex;

        // Picker sub-level: choose one Def (which animal to herd / what to produce)
        private static readonly List<object> pickerItems = new List<object>();
        private static int pickerIndex;
        private static PickerPurpose pickerPurpose;
        private static Level pickerReturnLevel;

        private static readonly TypeaheadSearchHelper typeahead = new TypeaheadSearchHelper();

        /// <summary>The window this menu is bound to, so the lifecycle patch can match it.</summary>
        public static object BoundWindow => window;

        /// <summary>True while the bound window is still on the window stack.</summary>
        public static bool BoundWindowStillOpen()
        {
            var w = window as Window;
            return w != null && Find.WindowStack != null && Find.WindowStack.IsOpen(w);
        }

        // ---------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------

        public static void Open(object managerWindow)
        {
            if (!ColonyManagerReflection.Available)
            {
                return;
            }

            window = managerWindow;
            manager = ColonyManagerReflection.GetManagerForCurrentMap();
            IsActive = true;
            typeahead.ClearSearch();

            ColonyManagerDebug.Log($"Open(): manager={(manager != null ? "found" : "NULL")} currentMap={(Find.CurrentMap != null ? Find.CurrentMap.ToString() : "null")}");
            ColonyManagerDebug.DumpSnapshot(manager);

            RebuildTabs();

            // Start on the tab the window currently has open.
            level = Level.Tabs;
            var current = ColonyManagerReflection.GetCurrentTab();
            tabIndex = current != null ? tabs.IndexOf(current) : 0;
            if (tabIndex < 0)
            {
                tabIndex = 0;
            }

            ColonyManagerDebug.Log($"Open(): {tabs.Count} visible tabs, starting at index {tabIndex}");

            TolkHelper.Speak("RimWorldAccess.ColonyManager.WindowOpened".Loc());
            AnnounceCurrentTab(SpeechPriority.Normal);
        }

        public static void Close()
        {
            IsActive = false;
            window = null;
            manager = null;
            tabs.Clear();
            jobs.Clear();
            detailSettings.Clear();
            detailJob = null;
            defListItems.Clear();
            defListSpec = null;
            pickerItems.Clear();
            tabIndex = 0;
            jobIndex = 0;
            detailIndex = 0;
            defListIndex = 0;
            pickerIndex = 0;
            typeahead.ClearSearch();
        }

        private static void RebuildTabs()
        {
            tabs.Clear();
            if (manager != null)
            {
                tabs.AddRange(ColonyManagerReflection.GetVisibleTabs(manager));
            }
        }

        private static void RebuildJobs(object tab)
        {
            jobs.Clear();
            if (manager != null && tab != null)
            {
                jobs.AddRange(ColonyManagerReflection.GetJobsForTab(manager, tab));
            }
            jobIndex = 0;
        }

        // ---------------------------------------------------------------
        // Input
        // ---------------------------------------------------------------

        public static bool HandleInput(KeyCode key, bool shift, bool ctrl, bool alt)
        {
            if (!IsActive)
            {
                return false;
            }

            switch (level)
            {
                case Level.Tabs:
                    return HandleTabsInput(key, shift, ctrl, alt);
                case Level.Jobs:
                    return HandleJobsInput(key, shift, ctrl, alt);
                case Level.JobDetail:
                    return HandleDetailInput(key, shift, ctrl, alt);
                case Level.Picker:
                    return HandlePickerInput(key, shift, ctrl, alt);
                default:
                    return HandleDefListInput(key, shift, ctrl, alt);
            }
        }

        private static bool HandleTabsInput(KeyCode key, bool shift, bool ctrl, bool alt)
        {
            switch (key)
            {
                case KeyCode.DownArrow:
                    tabIndex = MenuHelper.SelectNext(tabIndex, tabs.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentTab(SpeechPriority.Low);
                    return true;
                case KeyCode.UpArrow:
                    tabIndex = MenuHelper.SelectPrevious(tabIndex, tabs.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentTab(SpeechPriority.Low);
                    return true;
                case KeyCode.Home:
                    tabIndex = MenuHelper.JumpToFirst();
                    ClearSearchOnMove();
                    AnnounceCurrentTab(SpeechPriority.Low);
                    return true;
                case KeyCode.End:
                    tabIndex = MenuHelper.JumpToLast(tabs.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentTab(SpeechPriority.Low);
                    return true;
                case KeyCode.RightArrow:
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    EnterSelectedTab();
                    return true;
                case KeyCode.Escape:
                    if (typeahead.ClearSearchAndAnnounce())
                    {
                        return true;
                    }
                    CloseWindow();
                    return true;
            }
            return false;
        }

        private static bool HandleJobsInput(KeyCode key, bool shift, bool ctrl, bool alt)
        {
            // Reorder priority: Ctrl+Up raises (done sooner), Ctrl+Down lowers.
            if (ctrl && (key == KeyCode.UpArrow || key == KeyCode.DownArrow))
            {
                ReorderCurrentJob(key == KeyCode.UpArrow ? -1 : 1);
                return true;
            }

            // Create a new managed job of this tab's type (Ctrl+N — kept off plain letters so it
            // doesn't collide with typeahead search).
            if (ctrl && key == KeyCode.N)
            {
                CreateNewJob();
                return true;
            }

            switch (key)
            {
                case KeyCode.DownArrow:
                    if (jobs.Count == 0)
                    {
                        return true;
                    }
                    jobIndex = MenuHelper.SelectNext(jobIndex, jobs.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentJob(SpeechPriority.Low);
                    return true;
                case KeyCode.UpArrow:
                    if (jobs.Count == 0)
                    {
                        return true;
                    }
                    jobIndex = MenuHelper.SelectPrevious(jobIndex, jobs.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentJob(SpeechPriority.Low);
                    return true;
                case KeyCode.Home:
                    if (jobs.Count == 0)
                    {
                        return true;
                    }
                    jobIndex = MenuHelper.JumpToFirst();
                    ClearSearchOnMove();
                    AnnounceCurrentJob(SpeechPriority.Low);
                    return true;
                case KeyCode.End:
                    if (jobs.Count == 0)
                    {
                        return true;
                    }
                    jobIndex = MenuHelper.JumpToLast(jobs.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentJob(SpeechPriority.Low);
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.RightArrow:
                    OpenJobDetail();
                    return true;
                case KeyCode.Space:
                    ToggleCurrentJobSuspended();
                    return true;
                case KeyCode.Delete:
                    DeleteCurrentJob();
                    return true;
                case KeyCode.LeftArrow:
                    GoBackToTabs();
                    return true;
                case KeyCode.Escape:
                    if (typeahead.ClearSearchAndAnnounce())
                    {
                        return true;
                    }
                    GoBackToTabs();
                    return true;
            }
            return false;
        }

        private static bool HandleDetailInput(KeyCode key, bool shift, bool ctrl, bool alt)
        {
            if (detailSettings.Count == 0)
            {
                if (key == KeyCode.Escape || key == KeyCode.LeftArrow)
                {
                    GoBackToJobs();
                }
                return true;
            }

            switch (key)
            {
                case KeyCode.DownArrow:
                    detailIndex = MenuHelper.SelectNext(detailIndex, detailSettings.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentDetail(SpeechPriority.Low);
                    return true;
                case KeyCode.UpArrow:
                    detailIndex = MenuHelper.SelectPrevious(detailIndex, detailSettings.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentDetail(SpeechPriority.Low);
                    return true;
                case KeyCode.Home:
                    detailIndex = MenuHelper.JumpToFirst();
                    ClearSearchOnMove();
                    AnnounceCurrentDetail(SpeechPriority.Low);
                    return true;
                case KeyCode.End:
                    detailIndex = MenuHelper.JumpToLast(detailSettings.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentDetail(SpeechPriority.Low);
                    return true;
                case KeyCode.RightArrow:
                    AdjustDetail(1, ctrl);
                    return true;
                case KeyCode.LeftArrow:
                    AdjustDetail(-1, ctrl);
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Space:
                    ActivateDetail();
                    return true;
                case KeyCode.Escape:
                    if (typeahead.ClearSearchAndAnnounce())
                    {
                        return true;
                    }
                    GoBackToJobs();
                    return true;
            }
            return false;
        }

        private static bool HandleDefListInput(KeyCode key, bool shift, bool ctrl, bool alt)
        {
            if (defListItems.Count == 0)
            {
                if (key == KeyCode.Escape || key == KeyCode.LeftArrow)
                {
                    GoBackToDetail();
                }
                return true;
            }

            switch (key)
            {
                case KeyCode.DownArrow:
                    defListIndex = MenuHelper.SelectNext(defListIndex, defListItems.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentDef(SpeechPriority.Low);
                    return true;
                case KeyCode.UpArrow:
                    defListIndex = MenuHelper.SelectPrevious(defListIndex, defListItems.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentDef(SpeechPriority.Low);
                    return true;
                case KeyCode.Home:
                    defListIndex = MenuHelper.JumpToFirst();
                    ClearSearchOnMove();
                    AnnounceCurrentDef(SpeechPriority.Low);
                    return true;
                case KeyCode.End:
                    defListIndex = MenuHelper.JumpToLast(defListItems.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentDef(SpeechPriority.Low);
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Space:
                    ToggleCurrentDef();
                    return true;
                case KeyCode.LeftArrow:
                case KeyCode.Escape:
                    if (key == KeyCode.Escape && typeahead.ClearSearchAndAnnounce())
                    {
                        return true;
                    }
                    GoBackToDetail();
                    return true;
            }
            return false;
        }

        private static bool HandlePickerInput(KeyCode key, bool shift, bool ctrl, bool alt)
        {
            if (pickerItems.Count == 0)
            {
                if (key == KeyCode.Escape || key == KeyCode.LeftArrow)
                {
                    CancelPicker();
                }
                return true;
            }

            switch (key)
            {
                case KeyCode.DownArrow:
                    pickerIndex = MenuHelper.SelectNext(pickerIndex, pickerItems.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentPickerItem(SpeechPriority.Low);
                    return true;
                case KeyCode.UpArrow:
                    pickerIndex = MenuHelper.SelectPrevious(pickerIndex, pickerItems.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentPickerItem(SpeechPriority.Low);
                    return true;
                case KeyCode.Home:
                    pickerIndex = MenuHelper.JumpToFirst();
                    ClearSearchOnMove();
                    AnnounceCurrentPickerItem(SpeechPriority.Low);
                    return true;
                case KeyCode.End:
                    pickerIndex = MenuHelper.JumpToLast(pickerItems.Count);
                    ClearSearchOnMove();
                    AnnounceCurrentPickerItem(SpeechPriority.Low);
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Space:
                    ConfirmPicker();
                    return true;
                case KeyCode.LeftArrow:
                case KeyCode.Escape:
                    if (key == KeyCode.Escape && typeahead.ClearSearchAndAnnounce())
                    {
                        return true;
                    }
                    CancelPicker();
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Actions
        // ---------------------------------------------------------------

        private static void EnterSelectedTab()
        {
            if (tabIndex < 0 || tabIndex >= tabs.Count)
            {
                return;
            }
            var tab = tabs[tabIndex];

            if (!ColonyManagerReflection.TabEnabled(tab))
            {
                string reason = ColonyManagerReflection.TabDisabledReason(tab);
                string label = ColonyManagerReflection.TabLabel(tab);
                if (!string.IsNullOrEmpty(reason))
                {
                    TolkHelper.SpeakData(
                        "RimWorldAccess.ColonyManager.TabDisabled".Loc(label, reason).ToString());
                }
                else
                {
                    TolkHelper.SpeakData(
                        "RimWorldAccess.ColonyManager.TabDisabledNoReason".Loc(label).ToString());
                }
                return;
            }

            ColonyManagerDebug.Log($"EnterSelectedTab: '{ColonyManagerReflection.TabLabel(tab)}'");
            ColonyManagerReflection.GoToTab(tab);

            // Informational tabs (Overview / Logs / Import & Export) have no job list to browse
            // and nothing our menu can create. Switch the visible tab so a sighted helper sees it,
            // but stay at the tab row and say so, instead of dropping into an empty "0 jobs" list.
            if (IsInformationalTab(tab))
            {
                TolkHelper.SpeakData(
                    "RimWorldAccess.ColonyManager.TabInformational".Loc(
                        ColonyManagerReflection.TabLabel(tab)).ToString());
                return;
            }

            RebuildJobs(tab);
            level = Level.Jobs;
            typeahead.ClearSearch();
            ColonyManagerDebug.Log($"EnterSelectedTab: loaded {jobs.Count} jobs");

            string newJobHint = " " + "RimWorldAccess.ColonyManager.HintNewJob".Loc().ToString();

            if (jobs.Count == 0)
            {
                TolkHelper.SpeakData(
                    "RimWorldAccess.ColonyManager.EnteredTabEmpty".Loc(
                        ColonyManagerReflection.TabLabel(tab)).ToString() + newJobHint);
                return;
            }

            jobIndex = 0;
            TolkHelper.SpeakData(
                "RimWorldAccess.ColonyManager.EnteredTab".Loc(
                    ColonyManagerReflection.TabLabel(tab), jobs.Count).ToString() + newJobHint);
            AnnounceCurrentJob(SpeechPriority.Normal);
        }

        private static void GoBackToTabs()
        {
            level = Level.Tabs;
            typeahead.ClearSearch();
            TolkHelper.Speak("RimWorldAccess.ColonyManager.BackToTabs".Loc());
            AnnounceCurrentTab(SpeechPriority.Normal);
        }

        private static object CurrentTab => (tabIndex >= 0 && tabIndex < tabs.Count) ? tabs[tabIndex] : null;
        private static object CurrentJob => (jobIndex >= 0 && jobIndex < jobs.Count) ? jobs[jobIndex] : null;

        // Tabs that don't hold a manager-job list (a summary/history/transfer UI instead). We
        // don't descend into an empty job list or offer "create job" for these — they're read
        // only from our menu's point of view.
        private static readonly System.Collections.Generic.HashSet<string> InformationalTabTypes =
            new System.Collections.Generic.HashSet<string>
            {
                "ManagerTab_Overview",
                "ManagerTab_Logs",
                "ManagerTab_ImportExport",
            };

        private static bool IsInformationalTab(object tab) =>
            tab != null && InformationalTabTypes.Contains(tab.GetType().Name);

        private static void OpenJobDetail()
        {
            var job = CurrentJob;
            if (job == null)
            {
                return;
            }

            // Keep the mod's own detail panel in sync with our selection.
            ColonyManagerReflection.SetSelectedJob(CurrentTab, job);

            BuildDetailSettings(job);
            ColonyManagerDebug.Log($"OpenJobDetail: '{ColonyManagerReflection.JobLabel(job)}' -> {detailSettings.Count} editable setting(s)");

            if (detailSettings.Count == 0)
            {
                // No knob our menu can drive for this job type (e.g. Power is essentially a
                // read-only monitor). Don't dead-end silently — read what status we can so the
                // user hears where they are and that there's nothing to adjust here.
                string jl = ColonyManagerReflection.JobLabel(job);
                string tl = ColonyManagerReflection.JobTargetsLabel(job);
                string status = "";
                if (ColonyManagerReflection.JobIsSuspended(job))
                {
                    status = ", " + "RimWorldAccess.ColonyManager.StatusSuspended".Loc().ToString();
                }
                else if (ColonyManagerReflection.JobIsCompleted(job))
                {
                    status = ", " + "RimWorldAccess.ColonyManager.StatusCompleted".Loc().ToString();
                }

                string body = (string.IsNullOrEmpty(tl) || tl == "None")
                    ? "RimWorldAccess.ColonyManager.JobNoSettingsReadonly".Loc(jl).ToString()
                    : "RimWorldAccess.ColonyManager.JobNoSettingsReadonlyWithTargets".Loc(jl, tl).ToString();
                TolkHelper.SpeakData(body + status);
                return;
            }

            detailJob = job;
            detailIndex = 0;
            level = Level.JobDetail;
            typeahead.ClearSearch();
            TolkHelper.SpeakData(
                "RimWorldAccess.ColonyManager.EnteredJob".Loc(
                    ColonyManagerReflection.JobLabel(job)).ToString()
                + " " + "RimWorldAccess.ColonyManager.HintDetail".Loc().ToString());
            AnnounceCurrentDetail(SpeechPriority.Normal);
        }

        private static void GoBackToJobs()
        {
            level = Level.Jobs;
            detailJob = null;
            detailSettings.Clear();
            typeahead.ClearSearch();
            TolkHelper.Speak("RimWorldAccess.ColonyManager.BackToJobs".Loc());
            AnnounceCurrentJob(SpeechPriority.Normal);
        }

        private static void CreateNewJob()
        {
            var tab = CurrentTab;
            if (tab == null)
            {
                return;
            }

            // Livestock and Production jobs are meaningless until they know which animal to herd
            // or what to produce — and a pawn-kind-less Livestock job throws when the save is
            // reloaded. For those, ask first: the picker offers the mod's own "available" list.
            if (ColonyManagerReflection.TabNeedsChoiceToCreate(tab))
            {
                OpenPicker(
                    ColonyManagerReflection.GetCreationChoices(tab),
                    PickerPurpose.CreateJob,
                    Level.Jobs,
                    "RimWorldAccess.ColonyManager.PickerCreateTitle");
                return;
            }

            var job = ColonyManagerReflection.CreateAndAddManagedJob(manager, tab);
            if (job == null)
            {
                TolkHelper.Speak("RimWorldAccess.ColonyManager.NewJobFailed".Loc());
                return;
            }
            SelectAndAnnounceNewJob(tab, job);
        }

        /// <summary>Refresh the job list around a freshly created job and announce it.</summary>
        private static void SelectAndAnnounceNewJob(object tab, object job)
        {
            RebuildJobs(tab);
            jobIndex = jobs.IndexOf(job);
            if (jobIndex < 0)
            {
                jobIndex = jobs.Count - 1;
            }
            if (jobIndex < 0)
            {
                jobIndex = 0;
            }
            ColonyManagerDebug.Log($"CreateNewJob: created '{ColonyManagerReflection.JobLabel(job)}', now {jobs.Count} jobs");
            TolkHelper.SpeakData(
                "RimWorldAccess.ColonyManager.NewJobCreated".Loc(
                    ColonyManagerReflection.JobLabel(job)).ToString());
            AnnounceCurrentJob(SpeechPriority.Normal);
        }

        private static void DeleteCurrentJob()
        {
            var job = CurrentJob;
            var tab = CurrentTab;
            if (job == null || tab == null)
            {
                return;
            }
            string label = ColonyManagerReflection.JobLabel(job);
            if (!ColonyManagerReflection.DeleteJob(manager, job))
            {
                return;
            }
            RebuildJobs(tab);
            if (jobIndex >= jobs.Count)
            {
                jobIndex = jobs.Count - 1;
            }
            if (jobIndex < 0)
            {
                jobIndex = 0;
            }
            ColonyManagerDebug.Log($"DeleteCurrentJob: deleted '{label}', now {jobs.Count} jobs");
            TolkHelper.SpeakData(
                "RimWorldAccess.ColonyManager.JobDeleted".Loc(label).ToString());
            if (jobs.Count == 0)
            {
                TolkHelper.Speak("RimWorldAccess.ColonyManager.NoJobs".Loc());
            }
            else
            {
                AnnounceCurrentJob(SpeechPriority.Normal);
            }
        }

        private static void ReorderCurrentJob(int direction)
        {
            var job = CurrentJob;
            var tab = CurrentTab;
            if (job == null || tab == null)
            {
                return;
            }
            if (!ColonyManagerReflection.MoveJobPriority(manager, tab, job, direction))
            {
                MenuHelper.SpeakAlreadyAtEdge(direction < 0 ? MenuHelper.EdgeDirection.Top : MenuHelper.EdgeDirection.Bottom);
                return;
            }
            RebuildJobs(tab);
            jobIndex = jobs.IndexOf(job);
            if (jobIndex < 0)
            {
                jobIndex = 0;
            }
            ColonyManagerDebug.Log($"ReorderCurrentJob: '{ColonyManagerReflection.JobLabel(job)}' dir={direction}, new index {jobIndex}");
            AnnounceCurrentJob(SpeechPriority.Normal);
        }

        // ---------------------------------------------------------------
        // Job detail settings
        // ---------------------------------------------------------------

        private static void BuildDetailSettings(object job)
        {
            detailSettings.Clear();
            if (job == null)
            {
                return;
            }

            // 1) Threshold target ("how much you want") — generic across resource jobs.
            if (ColonyManagerReflection.JobHasThreshold(job))
            {
                int max = ColonyManagerReflection.GetThresholdMax(job);
                detailSettings.Add(new DetailSetting
                {
                    Kind = SettingKind.Threshold,
                    Label = "RimWorldAccess.ColonyManager.SettingTarget".Loc().ToString(),
                    GetInt = () => ColonyManagerReflection.GetThresholdTarget(job),
                    SetInt = v => ColonyManagerReflection.SetThresholdTarget(job, v),
                    Min = 0,
                    Max = max,
                    Step = NiceStep(max),
                    GetCurrent = () => ColonyManagerReflection.GetThresholdCurrentCount(job),
                    GetMeets = () => ColonyManagerReflection.ThresholdMeetsTarget(job)
                });
            }

            // 1b) Livestock: four per-category population targets (adult/juvenile × female/male).
            //     Structurally different from a single threshold — driven by Trigger_PawnKind.
            if (ColonyManagerReflection.IsLivestockJob(job))
            {
                AddLivestockTargets(job);
            }

            // 1c) Production: what the job makes, and whether it keeps stock up or works surplus
            //     down. The product defines the whole job, so it leads the settings list.
            if (ColonyManagerReflection.IsProductionJob(job))
            {
                AddProductionSettings(job);
            }

            // 2) Per-job-type settings (what / where / options) from the schema. Best-effort:
            //    any spec whose members don't resolve on this job is skipped.
            foreach (var spec in ColonyManagerJobSchema.For(job.GetType().Name))
            {
                var built = BuildSchemaSetting(job, spec);
                if (built != null)
                {
                    detailSettings.Add(built);
                }
                else
                {
                    ColonyManagerDebug.Log($"BuildDetailSettings: skipped unresolved spec {spec.Kind} '{spec.LabelKey}'");
                }
            }
        }

        private static DetailSetting BuildSchemaSetting(object job, ColonyManagerJobSchema.Spec spec)
        {
            string label = spec.LabelKey.Loc().ToString();

            switch (spec.Kind)
            {
                case ColonyManagerJobSchema.Kind.Toggle:
                    if (!ColonyManagerReflection.HasBoolMember(job, spec.Member))
                    {
                        return null;
                    }
                    return new DetailSetting
                    {
                        Kind = SettingKind.Toggle,
                        Label = label,
                        GetBool = () => ColonyManagerReflection.GetBoolMember(job, spec.Member),
                        SetBool = v => ColonyManagerReflection.SetBoolMember(job, spec.Member, v)
                    };

                case ColonyManagerJobSchema.Kind.Area:
                    if (!ColonyManagerReflection.HasMember(job, spec.Member))
                    {
                        return null;
                    }
                    return new DetailSetting
                    {
                        Kind = SettingKind.Area,
                        Label = label,
                        GetAreaLabel = () => CurrentAreaLabel(job, spec.Member),
                        CycleArea = dir => CycleJobArea(job, spec.Member, dir)
                    };

                case ColonyManagerJobSchema.Kind.DefList:
                    if (!ColonyManagerReflection.CanBuildDefList(job, spec.AllMember, spec.AllowedMember, spec.SetMethod))
                    {
                        return null;
                    }
                    return new DetailSetting
                    {
                        Kind = SettingKind.DefList,
                        Label = label,
                        DefListSpec = spec,
                        GetTotalCount = () => ColonyManagerReflection.GetDefListAll(job, spec.AllMember).Count,
                        GetAllowedCount = () => CountAllowed(job, spec)
                    };
            }
            return null;
        }

        // The four Trigger_PawnKind categories, in the order CM stores them
        // (Counts / CountTargets are int[4]).
        private static readonly string[] LivestockCategoryKeys =
        {
            "RimWorldAccess.ColonyManager.LivestockAdultFemale",
            "RimWorldAccess.ColonyManager.LivestockAdultMale",
            "RimWorldAccess.ColonyManager.LivestockJuvenileFemale",
            "RimWorldAccess.ColonyManager.LivestockJuvenileMale",
        };

        private static void AddLivestockTargets(object job)
        {
            var targets = ColonyManagerReflection.GetLivestockTargets(job);
            if (targets == null || targets.Length == 0)
            {
                return;
            }
            int count = System.Math.Min(targets.Length, LivestockCategoryKeys.Length);
            for (int i = 0; i < count; i++)
            {
                int idx = i; // capture per-iteration
                detailSettings.Add(new DetailSetting
                {
                    Kind = SettingKind.Threshold,
                    Label = LivestockCategoryKeys[idx].Loc().ToString(),
                    GetInt = () =>
                    {
                        var t = ColonyManagerReflection.GetLivestockTargets(job);
                        return (t != null && idx < t.Length) ? t[idx] : 0;
                    },
                    SetInt = v => ColonyManagerReflection.SetLivestockTarget(job, idx, v),
                    Min = 0,
                    Max = 0,     // no meaningful upper bound — don't announce a max
                    Step = 1,
                    GetCurrent = () =>
                    {
                        var c = ColonyManagerReflection.GetLivestockCurrent(job);
                        return (c != null && idx < c.Length) ? c[idx] : -1;
                    }
                });
            }
        }

        /// <summary>
        /// Production-only rows: the recipe the job makes, and its production mode. The recipe
        /// can be swapped only for what the mod itself offers as an equivalent
        /// (<c>ComputeRecipeSwapCandidates</c> — e.g. the same blocks cut at a different bench);
        /// producing something else entirely is a different job, created from the Jobs level.
        /// </summary>
        private static void AddProductionSettings(object job)
        {
            if (ColonyManagerReflection.GetProductionRecipe(job) != null)
            {
                detailSettings.Add(new DetailSetting
                {
                    Kind = SettingKind.Choice,
                    Label = "RimWorldAccess.ColonyManager.SettingProduct".Loc().ToString(),
                    GetChoiceLabel = () => DefLabel(ColonyManagerReflection.GetProductionRecipe(job)),
                    OpenChoicePicker = () => OpenRecipeSwapPicker(job)
                });
            }

            if (ColonyManagerReflection.HasEnumMember(job, "Mode"))
            {
                detailSettings.Add(new DetailSetting
                {
                    Kind = SettingKind.Cycle,
                    Label = "RimWorldAccess.ColonyManager.SettingProductionMode".Loc().ToString(),
                    GetCycleLabel = () => ProductionModeLabel(
                        ColonyManagerReflection.GetEnumMemberName(job, "Mode")),
                    CycleValue = dir => ColonyManagerReflection.CycleEnumMember(job, "Mode", dir)
                });
            }
        }

        private static void OpenRecipeSwapPicker(object job)
        {
            var candidates = ColonyManagerReflection.GetRecipeSwapCandidates(
                ColonyManagerReflection.GetObjectMember(job, "Tab"), job);
            if (candidates.Count == 0)
            {
                TolkHelper.Speak("RimWorldAccess.ColonyManager.RecipeNoAlternatives".Loc());
                return;
            }
            OpenPicker(candidates, PickerPurpose.ChangeRecipe, Level.JobDetail,
                "RimWorldAccess.ColonyManager.PickerRecipeTitle");
        }

        /// <summary>
        /// Speakable name for a production mode. The mod stores these as bare enum values, so we
        /// translate the ones we know and fall back to the raw name for anything new.
        /// </summary>
        private static string ProductionModeLabel(string enumName)
        {
            switch (enumName)
            {
                case "MaintainStock":
                    return "RimWorldAccess.ColonyManager.ProductionModeMaintainStock".Loc().ToString();
                case "ConsumeSurplus":
                    return "RimWorldAccess.ColonyManager.ProductionModeConsumeSurplus".Loc().ToString();
                default:
                    return enumName;
            }
        }

        private static int CountAllowed(object job, ColonyManagerJobSchema.Spec spec)
        {
            int n = 0;
            foreach (var def in ColonyManagerReflection.GetDefListAll(job, spec.AllMember))
            {
                if (ColonyManagerReflection.IsDefAllowed(job, spec.AllowedMember, def))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>A "nice" adjustment step ~5% of the range, floored at 1.</summary>
        private static int NiceStep(int max)
        {
            if (max <= 20)
            {
                return 1;
            }
            int step = max / 20;
            return step < 1 ? 1 : step;
        }

        // ---- Area cycling (vanilla Area type — reflection only for the job's field) ----

        private static string CurrentAreaLabel(object job, string member)
        {
            var area = ColonyManagerReflection.GetObjectMember(job, member) as Area;
            return area == null
                ? "RimWorldAccess.ColonyManager.AreaUnrestricted".Loc().ToString()
                : area.Label;
        }

        private static void CycleJobArea(object job, string member, int direction)
        {
            var options = new List<Area> { null };
            var all = Find.CurrentMap?.areaManager?.AllAreas;
            if (all != null)
            {
                options.AddRange(all);
            }
            var current = ColonyManagerReflection.GetObjectMember(job, member) as Area;
            int idx = options.FindIndex(a => ReferenceEquals(a, current));
            if (idx < 0)
            {
                idx = 0;
            }
            idx = ((idx + direction) % options.Count + options.Count) % options.Count;
            ColonyManagerReflection.SetObjectMember(job, member, options[idx]);
        }

        // ---- Detail adjust / activate / announce ----

        private static void AdjustDetail(int direction, bool fine)
        {
            if (detailIndex < 0 || detailIndex >= detailSettings.Count)
            {
                return;
            }
            var setting = detailSettings[detailIndex];
            switch (setting.Kind)
            {
                case SettingKind.Threshold:
                    int step = fine ? 1 : setting.Step;
                    int current = setting.GetInt();
                    int target = current + (direction * step);
                    if (target < setting.Min)
                    {
                        target = setting.Min;
                    }
                    if (setting.Max > 0 && target > setting.Max)
                    {
                        target = setting.Max;
                    }
                    setting.SetInt(target);
                    AnnounceCurrentDetail(SpeechPriority.Normal);
                    break;
                case SettingKind.Toggle:
                    setting.SetBool(direction > 0);
                    AnnounceCurrentDetail(SpeechPriority.Normal);
                    break;
                case SettingKind.Area:
                    setting.CycleArea(direction);
                    AnnounceCurrentDetail(SpeechPriority.Normal);
                    break;
                case SettingKind.Cycle:
                    setting.CycleValue(direction);
                    AnnounceCurrentDetail(SpeechPriority.Normal);
                    break;
                case SettingKind.Choice:
                    if (direction > 0)
                    {
                        setting.OpenChoicePicker();
                    }
                    break;
                case SettingKind.DefList:
                    if (direction > 0)
                    {
                        OpenDefList(setting);
                    }
                    break;
            }
        }

        private static void ActivateDetail()
        {
            if (detailIndex < 0 || detailIndex >= detailSettings.Count)
            {
                return;
            }
            var setting = detailSettings[detailIndex];
            switch (setting.Kind)
            {
                case SettingKind.Toggle:
                    setting.SetBool(!setting.GetBool());
                    AnnounceCurrentDetail(SpeechPriority.Normal);
                    break;
                case SettingKind.DefList:
                    OpenDefList(setting);
                    break;
                case SettingKind.Choice:
                    setting.OpenChoicePicker();
                    break;
                case SettingKind.Cycle:
                    setting.CycleValue(1);
                    AnnounceCurrentDetail(SpeechPriority.Normal);
                    break;
                default:
                    AnnounceCurrentDetail(SpeechPriority.Normal);
                    break;
            }
        }

        private static void AnnounceCurrentDetail(SpeechPriority priority)
        {
            if (detailIndex < 0 || detailIndex >= detailSettings.Count)
            {
                return;
            }
            var setting = detailSettings[detailIndex];
            string position = MenuHelper.FormatPosition(detailIndex, detailSettings.Count);
            string announcement;

            switch (setting.Kind)
            {
                case SettingKind.Threshold:
                    int value = setting.GetInt();
                    int current = setting.GetCurrent != null ? setting.GetCurrent() : -1;
                    if (current >= 0)
                    {
                        // "target N, currently M, met / short by K"
                        string statusPart;
                        if (setting.GetMeets != null)
                        {
                            statusPart = setting.GetMeets()
                                ? "RimWorldAccess.ColonyManager.TargetMet".Loc().ToString()
                                : "RimWorldAccess.ColonyManager.TargetShort".Loc(System.Math.Max(0, value - current)).ToString();
                        }
                        else
                        {
                            statusPart = current >= value
                                ? "RimWorldAccess.ColonyManager.TargetMet".Loc().ToString()
                                : "RimWorldAccess.ColonyManager.TargetShort".Loc(System.Math.Max(0, value - current)).ToString();
                        }
                        announcement = "RimWorldAccess.ColonyManager.SettingCount".Loc(
                            setting.Label, value, current, statusPart, position).ToString();
                    }
                    else
                    {
                        announcement = setting.Max > 0
                            ? "RimWorldAccess.ColonyManager.SettingIntWithMax".Loc(setting.Label, value, setting.Max, position).ToString()
                            : "RimWorldAccess.ColonyManager.SettingInt".Loc(setting.Label, value, position).ToString();
                    }
                    break;
                case SettingKind.Toggle:
                    string onOff = (setting.GetBool()
                        ? "RimWorldAccess.ColonyManager.On"
                        : "RimWorldAccess.ColonyManager.Off").Loc().ToString();
                    announcement = "RimWorldAccess.ColonyManager.SettingToggle".Loc(setting.Label, onOff, position).ToString();
                    break;
                case SettingKind.Area:
                    announcement = "RimWorldAccess.ColonyManager.SettingArea2".Loc(setting.Label, setting.GetAreaLabel(), position).ToString();
                    break;
                case SettingKind.Choice:
                    announcement = "RimWorldAccess.ColonyManager.SettingChoice".Loc(setting.Label, setting.GetChoiceLabel(), position).ToString();
                    break;
                case SettingKind.Cycle:
                    announcement = "RimWorldAccess.ColonyManager.SettingChoice".Loc(setting.Label, setting.GetCycleLabel(), position).ToString();
                    break;
                default: // DefList
                    announcement = "RimWorldAccess.ColonyManager.SettingDefList".Loc(setting.Label, setting.GetAllowedCount(), setting.GetTotalCount(), position).ToString();
                    break;
            }

            ColonyManagerDebug.Log($"announce detail: {announcement}");
            TolkHelper.SpeakData(typeahead.BuildItemAnnouncement(announcement), priority);
        }

        // ---- Def-list sub-level (which animals to hunt / trees to chop / etc.) ----

        private static void OpenDefList(DetailSetting setting)
        {
            if (detailJob == null || setting.DefListSpec == null)
            {
                return;
            }
            defListSpec = setting.DefListSpec;
            defListItems.Clear();
            defListItems.AddRange(ColonyManagerReflection.GetDefListAll(detailJob, defListSpec.AllMember));
            defListIndex = 0;
            level = Level.DefList;
            typeahead.ClearSearch();
            ColonyManagerDebug.Log($"OpenDefList: '{setting.Label}' -> {defListItems.Count} options");
            TolkHelper.SpeakData(
                "RimWorldAccess.ColonyManager.EnteredDefList".Loc(setting.Label, defListItems.Count).ToString());
            if (defListItems.Count > 0)
            {
                AnnounceCurrentDef(SpeechPriority.Normal);
            }
        }

        private static void GoBackToDetail()
        {
            level = Level.JobDetail;
            defListItems.Clear();
            defListSpec = null;
            typeahead.ClearSearch();
            // Rebuild so the def-list row's allowed-count reflects any changes.
            BuildDetailSettings(detailJob);
            ClampDetailIndex();
            TolkHelper.Speak("RimWorldAccess.ColonyManager.BackToSettings".Loc());
            AnnounceCurrentDetail(SpeechPriority.Normal);
        }

        private static void ToggleCurrentDef()
        {
            if (defListSpec == null || defListIndex < 0 || defListIndex >= defListItems.Count)
            {
                return;
            }
            var def = defListItems[defListIndex];
            bool nowAllowed = !ColonyManagerReflection.IsDefAllowed(detailJob, defListSpec.AllowedMember, def);
            ColonyManagerReflection.SetDefAllowed(detailJob, defListSpec.SetMethod, def, nowAllowed);
            ColonyManagerDebug.Log($"ToggleCurrentDef: '{DefLabel(def)}' -> allowed={nowAllowed}");
            AnnounceCurrentDef(SpeechPriority.Normal);
        }

        private static void AnnounceCurrentDef(SpeechPriority priority)
        {
            if (defListIndex < 0 || defListIndex >= defListItems.Count)
            {
                return;
            }
            var def = defListItems[defListIndex];
            bool allowed = ColonyManagerReflection.IsDefAllowed(detailJob, defListSpec.AllowedMember, def);
            string state = (allowed
                ? "RimWorldAccess.ColonyManager.Allowed"
                : "RimWorldAccess.ColonyManager.NotAllowed").Loc().ToString();
            string position = MenuHelper.FormatPosition(defListIndex, defListItems.Count);
            string announcement = "RimWorldAccess.ColonyManager.DefItem".Loc(DefLabel(def), state, position).ToString();
            ColonyManagerDebug.Log($"announce def: {announcement}");
            TolkHelper.SpeakData(typeahead.BuildItemAnnouncement(announcement), priority);
        }

        private static string DefLabel(object def)
        {
            return (def as Def)?.LabelCap.ToString() ?? (def as Def)?.defName ?? def?.ToString() ?? "";
        }

        // ---- Picker sub-level (which animal to herd / what to produce) ----

        /// <summary>
        /// Enter the picker over a list of Defs. Unlike the def-list level — where every entry is
        /// independently allowed or disallowed — exactly one entry is chosen here, and confirming
        /// it either creates a job or repoints the open one.
        /// </summary>
        private static void OpenPicker(List<object> choices, PickerPurpose purpose, Level returnLevel, string titleKey)
        {
            if (choices == null || choices.Count == 0)
            {
                TolkHelper.Speak("RimWorldAccess.ColonyManager.PickerEmpty".Loc());
                return;
            }

            pickerItems.Clear();
            pickerItems.AddRange(choices);
            pickerPurpose = purpose;
            pickerReturnLevel = returnLevel;
            pickerIndex = 0;
            level = Level.Picker;
            typeahead.ClearSearch();

            ColonyManagerDebug.Log($"OpenPicker: purpose={purpose}, {pickerItems.Count} option(s)");
            TolkHelper.SpeakData(
                titleKey.Loc(pickerItems.Count).ToString()
                + " " + "RimWorldAccess.ColonyManager.PickerHint".Loc().ToString());
            AnnounceCurrentPickerItem(SpeechPriority.Normal);
        }

        private static void CancelPicker()
        {
            var back = pickerReturnLevel;
            pickerItems.Clear();
            pickerIndex = 0;
            typeahead.ClearSearch();
            level = back;
            TolkHelper.Speak("RimWorldAccess.ColonyManager.PickerCancelled".Loc());
            if (back == Level.JobDetail)
            {
                AnnounceCurrentDetail(SpeechPriority.Normal);
            }
            else
            {
                AnnounceCurrentJob(SpeechPriority.Normal);
            }
        }

        private static void ConfirmPicker()
        {
            if (pickerIndex < 0 || pickerIndex >= pickerItems.Count)
            {
                return;
            }
            var choice = pickerItems[pickerIndex];
            var purpose = pickerPurpose;

            pickerItems.Clear();
            pickerIndex = 0;
            typeahead.ClearSearch();

            if (purpose == PickerPurpose.CreateJob)
            {
                level = Level.Jobs;
                var tab = CurrentTab;
                var job = ColonyManagerReflection.CreateAndAddManagedJob(manager, tab, choice);
                if (job == null)
                {
                    TolkHelper.Speak("RimWorldAccess.ColonyManager.NewJobFailed".Loc());
                    AnnounceCurrentJob(SpeechPriority.Normal);
                    return;
                }
                SelectAndAnnounceNewJob(tab, job);
                return;
            }

            // ChangeRecipe: repoint the open production job. The mod's Recipe setter also re-aims
            // the job's threshold at the new product, so the detail rows are rebuilt afterwards.
            level = Level.JobDetail;
            if (!ColonyManagerReflection.SetProductionRecipe(detailJob, choice))
            {
                TolkHelper.Speak("RimWorldAccess.ColonyManager.RecipeChangeFailed".Loc());
                AnnounceCurrentDetail(SpeechPriority.Normal);
                return;
            }
            BuildDetailSettings(detailJob);
            ClampDetailIndex();
            ColonyManagerDebug.Log($"ConfirmPicker: recipe -> '{DefLabel(choice)}'");
            TolkHelper.SpeakData(
                "RimWorldAccess.ColonyManager.RecipeChanged".Loc(DefLabel(choice)).ToString());
            AnnounceCurrentDetail(SpeechPriority.Normal);
        }

        private static void AnnounceCurrentPickerItem(SpeechPriority priority)
        {
            if (pickerIndex < 0 || pickerIndex >= pickerItems.Count)
            {
                return;
            }
            string position = MenuHelper.FormatPosition(pickerIndex, pickerItems.Count);
            string announcement = "RimWorldAccess.ColonyManager.PickerItem".Loc(
                DefLabel(pickerItems[pickerIndex]), position).ToString();
            ColonyManagerDebug.Log($"announce picker: {announcement}");
            TolkHelper.SpeakData(typeahead.BuildItemAnnouncement(announcement), priority);
        }

        private static void ClampDetailIndex()
        {
            if (detailIndex >= detailSettings.Count)
            {
                detailIndex = detailSettings.Count - 1;
            }
            if (detailIndex < 0)
            {
                detailIndex = 0;
            }
        }

        /// <summary>
        /// A spoken summary of a Livestock job's herd ("7 of 20 animals"), or null if the job
        /// isn't a Livestock job. The mod's own TargetsLabel for Livestock is a bundle of raw
        /// translation keys, unsuitable for speech.
        /// </summary>
        private static string LivestockJobSummary(object job)
        {
            if (!ColonyManagerReflection.IsLivestockJob(job))
            {
                return null;
            }
            var current = ColonyManagerReflection.GetLivestockCurrent(job);
            var targets = ColonyManagerReflection.GetLivestockTargets(job);
            if (current == null || targets == null)
            {
                return null;
            }
            int have = 0, want = 0;
            foreach (int c in current) have += c;
            foreach (int t in targets) want += t;
            return "RimWorldAccess.ColonyManager.JobLivestockSummary".Loc(have, want).ToString();
        }

        private static void ToggleCurrentJobSuspended()
        {
            if (jobIndex < 0 || jobIndex >= jobs.Count)
            {
                return;
            }
            var job = jobs[jobIndex];
            bool nowSuspended = ColonyManagerReflection.ToggleJobSuspended(job);
            string label = ColonyManagerReflection.JobLabel(job);
            ColonyManagerDebug.Log($"ToggleJobSuspended: '{label}' -> suspended={nowSuspended}");
            TolkHelper.SpeakData(
                (nowSuspended
                    ? "RimWorldAccess.ColonyManager.JobSuspendedNow".Loc(label)
                    : "RimWorldAccess.ColonyManager.JobResumedNow".Loc(label)).ToString());
        }

        private static void CloseWindow()
        {
            var w = window as Window;
            if (w != null)
            {
                Find.WindowStack.TryRemove(w, doCloseSound: false);
            }
            // The lifecycle patch (Window.PreClose) fires Close() + the closed announcement.
        }

        // ---------------------------------------------------------------
        // Announcements
        // ---------------------------------------------------------------

        private static void AnnounceCurrentTab(SpeechPriority priority)
        {
            if (tabIndex < 0 || tabIndex >= tabs.Count)
            {
                return;
            }
            var tab = tabs[tabIndex];
            string label = ColonyManagerReflection.TabLabel(tab);
            string position = MenuHelper.FormatPosition(tabIndex, tabs.Count);

            int jobCount = manager != null
                ? ColonyManagerReflection.GetJobsForTab(manager, tab).Count
                : 0;

            string announcement;
            if (!ColonyManagerReflection.TabEnabled(tab))
            {
                announcement = "RimWorldAccess.ColonyManager.TabItemDisabled".Loc(
                    label, position).ToString();
            }
            else if (IsInformationalTab(tab))
            {
                announcement = "RimWorldAccess.ColonyManager.TabItemInformational".Loc(
                    label, position).ToString();
            }
            else
            {
                announcement = "RimWorldAccess.ColonyManager.TabItem".Loc(
                    label, jobCount, position).ToString();
            }

            ColonyManagerDebug.Log($"announce tab: {announcement}");
            TolkHelper.SpeakData(typeahead.BuildItemAnnouncement(announcement), priority);
        }

        private static void AnnounceCurrentJob(SpeechPriority priority)
        {
            if (jobs.Count == 0)
            {
                TolkHelper.Speak("RimWorldAccess.ColonyManager.NoJobs".Loc(), priority);
                return;
            }
            if (jobIndex < 0 || jobIndex >= jobs.Count)
            {
                return;
            }
            var job = jobs[jobIndex];
            string label = ColonyManagerReflection.JobLabel(job);
            string position = MenuHelper.FormatPosition(jobIndex, jobs.Count);

            string status = "";
            if (ColonyManagerReflection.JobIsSuspended(job))
            {
                status = ", " + "RimWorldAccess.ColonyManager.StatusSuspended".Loc().ToString();
            }
            else if (ColonyManagerReflection.JobIsCompleted(job))
            {
                status = ", " + "RimWorldAccess.ColonyManager.StatusCompleted".Loc().ToString();
            }

            // Targets text. Livestock's TargetsLabel is a set of raw translation keys, so build a
            // plain "current of target animals" summary instead. Jobs with no meaningful targets
            // (e.g. Power reports "None") drop the targets clause entirely.
            string targets = LivestockJobSummary(job) ?? ColonyManagerReflection.JobTargetsLabel(job);
            string announcement = (string.IsNullOrEmpty(targets) || targets == "None")
                ? "RimWorldAccess.ColonyManager.JobItemNoTargets".Loc(label, position).ToString() + status
                : "RimWorldAccess.ColonyManager.JobItem".Loc(label, targets, position).ToString() + status;

            ColonyManagerDebug.Log($"announce job: {announcement}");
            TolkHelper.SpeakData(typeahead.BuildItemAnnouncement(announcement), priority);
        }

        // ---------------------------------------------------------------
        // Typeahead
        // ---------------------------------------------------------------

        public static bool IsTypeaheadActive() => IsActive;

        public static bool HandleCharacterInput(char c)
        {
            if (!IsActive)
            {
                return false;
            }

            var labels = CurrentLabels();
            if (labels.Count == 0)
            {
                return false;
            }

            if (typeahead.ProcessCharacterInput(c, labels, out int newIndex))
            {
                switch (level)
                {
                    case Level.Tabs:
                        tabIndex = newIndex;
                        AnnounceCurrentTab(SpeechPriority.Normal);
                        break;
                    case Level.Jobs:
                        jobIndex = newIndex;
                        AnnounceCurrentJob(SpeechPriority.Normal);
                        break;
                    case Level.JobDetail:
                        detailIndex = newIndex;
                        AnnounceCurrentDetail(SpeechPriority.Normal);
                        break;
                    case Level.Picker:
                        pickerIndex = newIndex;
                        AnnounceCurrentPickerItem(SpeechPriority.Normal);
                        break;
                    default:
                        defListIndex = newIndex;
                        AnnounceCurrentDef(SpeechPriority.Normal);
                        break;
                }
                return true;
            }

            typeahead.SpeakNoMatches();
            return true;
        }

        private static List<string> CurrentLabels()
        {
            var labels = new List<string>();
            switch (level)
            {
                case Level.Tabs:
                    foreach (var tab in tabs)
                    {
                        labels.Add(ColonyManagerReflection.TabLabel(tab));
                    }
                    break;
                case Level.Jobs:
                    foreach (var job in jobs)
                    {
                        labels.Add(ColonyManagerReflection.JobLabel(job));
                    }
                    break;
                case Level.JobDetail:
                    foreach (var setting in detailSettings)
                    {
                        labels.Add(setting.Label);
                    }
                    break;
                case Level.Picker:
                    foreach (var choice in pickerItems)
                    {
                        labels.Add(DefLabel(choice));
                    }
                    break;
                default:
                    foreach (var def in defListItems)
                    {
                        labels.Add(DefLabel(def));
                    }
                    break;
            }
            return labels;
        }

        private static void ClearSearchOnMove()
        {
            if (typeahead.HasActiveSearch)
            {
                typeahead.ClearSearch();
            }
        }
    }
}
