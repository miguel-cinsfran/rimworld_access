using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimWorldAccess
{
    /// <summary>
    /// Keyboard navigation for Vanilla Psycasts Expanded's "Psycasts" pawn tab
    /// (VanillaPsycastsExpanded.UI.ITab_Pawn_Psycasts), which VPE surfaces on any player
    /// psycaster and which vanilla RimWorld Access could only dump as a read-only inspect string.
    ///
    /// The tab is a point-spend tree: the pawn's Hediff_PsycastAbilities holds spendable points,
    /// and each point buys one of — improve psycaster stats, unlock a meditation focus, unlock a
    /// path, or learn one ability inside an unlocked path (respecting ability prerequisites). This
    /// state exposes all four as a small hierarchy of flat, typeahead-searchable lists:
    ///
    ///   Root ─┬─ (status line: level / points / xp)
    ///         ├─ Paths      → per-path list; Enter unlocks a path or drills into its abilities
    ///         │    └─ Abilities → per-ability list; Enter learns an unlockable ability
    ///         ├─ Foci       → per-focus list; Enter unlocks an eligible meditation focus
    ///         └─ Improve psycaster stats (spend one point)
    ///
    /// All VPE/VEF data flows through <see cref="VPEPsycastsReflection"/> so this file compiles and
    /// no-ops when the mod is absent. Registered in <see cref="TabRegistry"/> as an Action tab,
    /// opened from <c>InspectionTreeBuilder.ExecuteCategoryAction</c>, and its keys routed by
    /// <c>BuildingInspectPatch</c> — mirroring the Anomaly <see cref="EntityTabState"/> precedent.
    /// </summary>
    public static class VPEPsycastsState
    {
        private enum View { Root, Paths, Abilities, Foci }
        private enum ItemKind { Status, GotoPaths, GotoFoci, ImproveStats, Path, Ability, Focus }

        private class Item
        {
            public ItemKind Kind;
            public Def Path;                 // Path / current-path payload
            public Def Ability;              // Ability payload
            public MeditationFocusDef Focus; // Focus payload
        }

        private static bool isActive;
        private static Pawn pawn;
        private static object hediff;   // Hediff_PsycastAbilities
        private static object comp;     // VEF CompAbilities

        private static View view;
        private static Def currentPath; // when view == Abilities
        private static List<Item> items = new List<Item>();
        private static int selectedIndex;
        private static readonly TypeaheadSearchHelper typeahead = new TypeaheadSearchHelper();

        public static bool IsActive => isActive;
        public static bool HasActiveSearch => typeahead.HasActiveSearch;

        // ===== ENTRY =====

        public static void Open(Pawn target)
        {
            if (target == null) return;
            if (!VPEPsycastsReflection.Available)
            {
                TolkHelper.Speak("RimWorldAccess.VPEPsycasts.Unavailable".Loc());
                return;
            }

            pawn = target;
            hediff = VPEPsycastsReflection.GetPsycastHediff(pawn);
            comp = VPEPsycastsReflection.GetCompAbilities(pawn);
            if (hediff == null || comp == null)
            {
                TolkHelper.Speak("RimWorldAccess.VPEPsycasts.Unavailable".Loc());
                pawn = null;
                return;
            }

            view = View.Root;
            currentPath = null;
            selectedIndex = 0;
            typeahead.ClearSearch();
            isActive = true;

            BuildItems();
            TolkHelper.SpeakData(BuildOpeningHeader(), SpeechPriority.High);
            AnnounceCurrent();
        }

        public static void Close()
        {
            isActive = false;
            pawn = null;
            hediff = null;
            comp = null;
            currentPath = null;
            items.Clear();
            typeahead.ClearSearch();
        }

        // ===== ITEM BUILDING =====

        private static void BuildItems()
        {
            items.Clear();
            switch (view)
            {
                case View.Root:
                    items.Add(new Item { Kind = ItemKind.Status });
                    items.Add(new Item { Kind = ItemKind.GotoPaths });
                    items.Add(new Item { Kind = ItemKind.GotoFoci });
                    items.Add(new Item { Kind = ItemKind.ImproveStats });
                    break;

                case View.Paths:
                    foreach (var p in VPEPsycastsReflection.GetAllPaths()
                                 .OrderByDescending(p => VPEPsycastsReflection.IsPathUnlocked(hediff, p))
                                 .ThenBy(VPEPsycastsReflection.GetPathOrder)
                                 .ThenBy(p => p.label))
                        items.Add(new Item { Kind = ItemKind.Path, Path = p });
                    break;

                case View.Abilities:
                    foreach (var a in VPEPsycastsReflection.GetPathAbilities(currentPath)
                                 .OrderBy(VPEPsycastsReflection.GetAbilityLevel)
                                 .ThenBy(VPEPsycastsReflection.GetAbilityOrder)
                                 .ThenBy(a => a.label))
                        items.Add(new Item { Kind = ItemKind.Ability, Ability = a });
                    break;

                case View.Foci:
                    foreach (var f in VPEPsycastsReflection.GetAllFoci())
                        items.Add(new Item { Kind = ItemKind.Focus, Focus = f });
                    break;
            }

            if (selectedIndex >= items.Count) selectedIndex = System.Math.Max(0, items.Count - 1);
        }

        // ===== KEYBOARD =====

        public static bool HandleInput(Event evt)
        {
            if (!isActive || evt.type != EventType.KeyDown) return false;

            KeyCode key = evt.keyCode;

            switch (key)
            {
                case KeyCode.Escape:
                    if (typeahead.HasActiveSearch)
                    {
                        typeahead.ClearSearchAndAnnounce();
                        AnnounceCurrent();
                        return true;
                    }
                    GoBackOrClose();
                    return true;

                case KeyCode.LeftArrow:
                    if (typeahead.HasActiveSearch && !typeahead.HasNoMatches) return true;
                    GoBack();
                    return true;

                case KeyCode.UpArrow:
                    if (typeahead.HasActiveSearch && !typeahead.HasNoMatches)
                        selectedIndex = typeahead.GetPreviousMatch(selectedIndex);
                    else
                        selectedIndex = MenuHelper.SelectPrevious(selectedIndex, items.Count);
                    AnnounceCurrentWithSearch();
                    return true;

                case KeyCode.DownArrow:
                    if (typeahead.HasActiveSearch && !typeahead.HasNoMatches)
                        selectedIndex = typeahead.GetNextMatch(selectedIndex);
                    else
                        selectedIndex = MenuHelper.SelectNext(selectedIndex, items.Count);
                    AnnounceCurrentWithSearch();
                    return true;

                case KeyCode.Home:
                    if (typeahead.HasActiveSearch && !typeahead.HasNoMatches)
                        selectedIndex = typeahead.GetFirstMatch();
                    else
                        selectedIndex = 0;
                    AnnounceCurrentWithSearch();
                    return true;

                case KeyCode.End:
                    if (typeahead.HasActiveSearch && !typeahead.HasNoMatches)
                        selectedIndex = typeahead.GetLastMatch();
                    else
                        selectedIndex = items.Count - 1;
                    AnnounceCurrentWithSearch();
                    return true;

                case KeyCode.Backspace:
                    HandleBackspace();
                    return true;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.RightArrow:
                case KeyCode.Space:
                    Activate();
                    return true;

                default:
                    return true; // modal: swallow everything else; typeahead arrives via TypeaheadDispatcher
            }
        }

        /// <summary>Layout-aware typeahead entry; called by <see cref="TypeaheadConsumerRegistry"/>.</summary>
        public static void HandleTypeahead(char c)
        {
            if (!isActive) return;
            var labels = items.Select(GetItemLabel).ToList();
            if (typeahead.ProcessCharacterInput(c, labels, out int newIdx) && newIdx >= 0)
            {
                selectedIndex = newIdx;
                AnnounceCurrentWithSearch();
            }
            else
            {
                typeahead.SpeakNoMatches();
            }
        }

        public static void HandleBackspace()
        {
            if (!isActive || !typeahead.HasActiveSearch) return;
            var labels = items.Select(GetItemLabel).ToList();
            if (typeahead.ProcessBackspace(labels, out int newIdx))
            {
                if (newIdx >= 0) selectedIndex = newIdx;
                AnnounceCurrentWithSearch();
            }
        }

        // ===== NAVIGATION =====

        private static void GoBackOrClose()
        {
            if (view == View.Root)
            {
                Close();
                TolkHelper.Speak("RimWorldAccess.VPEPsycasts.Closed".Loc());
                return;
            }
            GoBack();
        }

        private static void GoBack()
        {
            switch (view)
            {
                case View.Abilities:
                    view = View.Paths;
                    currentPath = null;
                    break;
                case View.Paths:
                case View.Foci:
                    view = View.Root;
                    break;
                case View.Root:
                    return;
            }
            selectedIndex = 0;
            typeahead.ClearSearch();
            BuildItems();
            TolkHelper.SpeakData(BuildViewHeader());
            AnnounceCurrent();
        }

        private static void EnterView(View target, Def path = null)
        {
            view = target;
            currentPath = path;
            selectedIndex = 0;
            typeahead.ClearSearch();
            BuildItems();
            TolkHelper.SpeakData(BuildViewHeader());
            AnnounceCurrent();
        }

        // ===== ACTIVATION =====

        private static void Activate()
        {
            if (items.Count == 0 || selectedIndex < 0 || selectedIndex >= items.Count) return;
            typeahead.ClearSearch();
            var item = items[selectedIndex];

            switch (item.Kind)
            {
                case ItemKind.Status:
                    AnnounceCurrent();
                    return;

                case ItemKind.GotoPaths:
                    EnterView(View.Paths);
                    return;

                case ItemKind.GotoFoci:
                    EnterView(View.Foci);
                    return;

                case ItemKind.ImproveStats:
                    DoImproveStats();
                    return;

                case ItemKind.Path:
                    ActivatePath(item.Path);
                    return;

                case ItemKind.Ability:
                    ActivateAbility(item.Ability);
                    return;

                case ItemKind.Focus:
                    ActivateFocus(item.Focus);
                    return;
            }
        }

        private static void DoImproveStats()
        {
            if (VPEPsycastsReflection.GetPoints(hediff) < 1)
            {
                RejectNoPoints();
                return;
            }
            VPEPsycastsReflection.ImproveStats(hediff, 1);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            BuildItems();
            TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.StatsImproved".Loc(VPEPsycastsReflection.GetPoints(hediff)).ToString());
        }

        private static void ActivatePath(Def path)
        {
            if (path == null) return;

            if (VPEPsycastsReflection.IsPathUnlocked(hediff, path))
            {
                if (VPEPsycastsReflection.PathHasAbilities(path))
                    EnterView(View.Abilities, path);
                else
                    TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.PathNoAbilities".Loc(path.LabelCap).ToString());
                return;
            }

            if (!VPEPsycastsReflection.PathCanPawnUnlock(path, pawn))
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                string reason = VPEPsycastsReflection.GetPathLockedReason(path);
                TolkHelper.SpeakData(string.IsNullOrEmpty(reason)
                    ? "RimWorldAccess.VPEPsycasts.PathLocked".Loc(path.LabelCap).ToString()
                    : "RimWorldAccess.VPEPsycasts.PathLockedReason".Loc(path.LabelCap, reason.StripTags()).ToString());
                return;
            }

            if (VPEPsycastsReflection.GetPoints(hediff) < 1)
            {
                RejectNoPoints();
                return;
            }

            VPEPsycastsReflection.UnlockPath(hediff, path);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.PathUnlocked".Loc(path.LabelCap, VPEPsycastsReflection.GetPoints(hediff)).ToString());

            // Drill straight into the freshly-unlocked path's abilities, the natural next step.
            if (VPEPsycastsReflection.PathHasAbilities(path))
                EnterView(View.Abilities, path);
            else
                BuildItems();
        }

        private static void ActivateAbility(Def ability)
        {
            if (ability == null) return;

            if (VPEPsycastsReflection.HasAbility(comp, ability))
            {
                TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.AbilityLearned".Loc(ability.LabelCap).ToString());
                return;
            }

            if (!VPEPsycastsReflection.PrereqsCompleted(comp, ability))
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                TolkHelper.SpeakData(BuildAbilityPrereqText(ability));
                return;
            }

            if (VPEPsycastsReflection.GetPoints(hediff) < 1)
            {
                RejectNoPoints();
                return;
            }

            VPEPsycastsReflection.GiveAbility(hediff, comp, ability);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            BuildItems();
            TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.AbilityUnlocked".Loc(ability.LabelCap, VPEPsycastsReflection.GetPoints(hediff)).ToString());
        }

        private static void ActivateFocus(MeditationFocusDef focus)
        {
            if (focus == null) return;

            if (VPEPsycastsReflection.FocusCanPawnUse(focus, pawn))
            {
                TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.FocusAvailable".Loc(focus.LabelCap).ToString());
                return;
            }

            if (!VPEPsycastsReflection.FocusCanUnlock(focus, pawn, out string reason))
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                TolkHelper.SpeakData(string.IsNullOrEmpty(reason)
                    ? "RimWorldAccess.VPEPsycasts.FocusLocked".Loc(focus.LabelCap).ToString()
                    : "RimWorldAccess.VPEPsycasts.FocusLockedReason".Loc(focus.LabelCap, reason.StripTags()).ToString());
                return;
            }

            if (VPEPsycastsReflection.GetPoints(hediff) < 1)
            {
                RejectNoPoints();
                return;
            }

            VPEPsycastsReflection.UnlockFocus(hediff, focus);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            BuildItems();
            TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.FocusUnlocked".Loc(focus.LabelCap, VPEPsycastsReflection.GetPoints(hediff)).ToString());
        }

        private static void RejectNoPoints()
        {
            SoundDefOf.ClickReject.PlayOneShotOnCamera();
            TolkHelper.Speak("RimWorldAccess.VPEPsycasts.NoPoints".Loc());
        }

        // ===== ANNOUNCEMENTS =====

        private static void AnnounceCurrentWithSearch()
        {
            if (typeahead.HasActiveSearch && items.Count > 0 && selectedIndex >= 0 && selectedIndex < items.Count)
                TolkHelper.SpeakData(GetItemLabel(items[selectedIndex]) + typeahead.BuildSearchContextSuffix());
            else
                AnnounceCurrent();
        }

        private static void AnnounceCurrent()
        {
            if (items.Count == 0 || selectedIndex < 0 || selectedIndex >= items.Count)
            {
                TolkHelper.SpeakData(BuildViewHeader());
                return;
            }
            string body = BuildItemAnnouncement(items[selectedIndex]);
            string position = MenuHelper.FormatPosition(selectedIndex, items.Count);
            TolkHelper.SpeakData(string.IsNullOrEmpty(position) ? body : $"{body}. {position}");
        }

        private static string BuildOpeningHeader()
        {
            string tabName = "VPE.Psycasts".Translate();
            return $"{tabName}. {pawn.LabelShortCap}. " +
                   "RimWorldAccess.VPEPsycasts.Status".Loc(
                       VPEPsycastsReflection.GetLevel(hediff),
                       VPEPsycastsReflection.GetPoints(hediff)).ToString();
        }

        private static string BuildViewHeader()
        {
            switch (view)
            {
                case View.Paths: return "RimWorldAccess.VPEPsycasts.Header.Paths".Loc().ToString();
                case View.Abilities: return "RimWorldAccess.VPEPsycasts.Header.Abilities".Loc(currentPath?.LabelCap ?? "").ToString();
                case View.Foci: return "RimWorldAccess.VPEPsycasts.Header.Foci".Loc().ToString();
                default: return "VPE.Psycasts".Translate();
            }
        }

        private static string GetItemLabel(Item item)
        {
            switch (item.Kind)
            {
                case ItemKind.Status: return "RimWorldAccess.VPEPsycasts.Item.Status".Loc().ToString();
                case ItemKind.GotoPaths: return "RimWorldAccess.VPEPsycasts.Item.Paths".Loc().ToString();
                case ItemKind.GotoFoci: return "RimWorldAccess.VPEPsycasts.Item.Foci".Loc().ToString();
                case ItemKind.ImproveStats: return "RimWorldAccess.VPEPsycasts.Item.ImproveStats".Loc().ToString();
                case ItemKind.Path: return item.Path?.LabelCap;
                case ItemKind.Ability: return item.Ability?.LabelCap;
                case ItemKind.Focus: return item.Focus?.LabelCap;
                default: return "";
            }
        }

        private static string BuildItemAnnouncement(Item item)
        {
            switch (item.Kind)
            {
                case ItemKind.Status:
                    return "RimWorldAccess.VPEPsycasts.StatusFull".Loc(
                        VPEPsycastsReflection.GetLevel(hediff),
                        VPEPsycastsReflection.GetPoints(hediff),
                        Mathf.RoundToInt(VPEPsycastsReflection.GetExperience(hediff)),
                        VPEPsycastsReflection.GetExperienceRequiredForLevel(VPEPsycastsReflection.GetLevel(hediff) + 1)).ToString();

                case ItemKind.GotoPaths:
                {
                    int total = VPEPsycastsReflection.GetAllPaths().Count;
                    int unlocked = VPEPsycastsReflection.GetUnlockedPaths(hediff)?.Count ?? 0;
                    return "RimWorldAccess.VPEPsycasts.Item.PathsCount".Loc(unlocked, total).ToString();
                }

                case ItemKind.GotoFoci:
                {
                    int total = VPEPsycastsReflection.GetAllFoci().Count;
                    int unlocked = VPEPsycastsReflection.GetUnlockedFoci(hediff)?.Count ?? 0;
                    return "RimWorldAccess.VPEPsycasts.Item.FociCount".Loc(unlocked, total).ToString();
                }

                case ItemKind.ImproveStats:
                    return "RimWorldAccess.VPEPsycasts.Item.ImproveStatsFull".Loc().ToString();

                case ItemKind.Path: return BuildPathAnnouncement(item.Path);
                case ItemKind.Ability: return BuildAbilityAnnouncement(item.Ability);
                case ItemKind.Focus: return BuildFocusAnnouncement(item.Focus);
                default: return "";
            }
        }

        private static string BuildPathAnnouncement(Def path)
        {
            string label = path.LabelCap;
            if (VPEPsycastsReflection.IsPathUnlocked(hediff, path))
            {
                var abilities = VPEPsycastsReflection.GetPathAbilities(path);
                int learned = abilities.Count(a => VPEPsycastsReflection.HasAbility(comp, a));
                return "RimWorldAccess.VPEPsycasts.Path.Unlocked".Loc(label, learned, abilities.Count).ToString();
            }
            if (!VPEPsycastsReflection.PathCanPawnUnlock(path, pawn))
            {
                string reason = VPEPsycastsReflection.GetPathLockedReason(path);
                return string.IsNullOrEmpty(reason)
                    ? "RimWorldAccess.VPEPsycasts.Path.Locked".Loc(label).ToString()
                    : "RimWorldAccess.VPEPsycasts.Path.LockedReason".Loc(label, reason.StripTags()).ToString();
            }
            return VPEPsycastsReflection.GetPoints(hediff) >= 1
                ? "RimWorldAccess.VPEPsycasts.Path.Unlockable".Loc(label).ToString()
                : "RimWorldAccess.VPEPsycasts.Path.NoPoints".Loc(label).ToString();
        }

        private static string BuildAbilityAnnouncement(Def ability)
        {
            string label = ability.LabelCap;
            int level = VPEPsycastsReflection.GetAbilityLevel(ability);
            if (VPEPsycastsReflection.HasAbility(comp, ability))
                return "RimWorldAccess.VPEPsycasts.Ability.Learned".Loc(label, level).ToString();
            if (!VPEPsycastsReflection.PrereqsCompleted(comp, ability))
                return BuildAbilityPrereqText(ability);
            return VPEPsycastsReflection.GetPoints(hediff) >= 1
                ? "RimWorldAccess.VPEPsycasts.Ability.Unlockable".Loc(label, level).ToString()
                : "RimWorldAccess.VPEPsycasts.Ability.NoPoints".Loc(label, level).ToString();
        }

        private static string BuildAbilityPrereqText(Def ability)
        {
            int level = VPEPsycastsReflection.GetAbilityLevel(ability);
            var prereqs = VPEPsycastsReflection.GetAbilityPrerequisites(ability)
                .Select(p => p.LabelCap.ToString());
            return "RimWorldAccess.VPEPsycasts.Ability.NeedsPrereq".Loc(
                ability.LabelCap, level, string.Join(", ", prereqs)).ToString();
        }

        private static string BuildFocusAnnouncement(MeditationFocusDef focus)
        {
            string label = focus.LabelCap;
            if (VPEPsycastsReflection.FocusCanPawnUse(focus, pawn))
                return "RimWorldAccess.VPEPsycasts.Focus.Available".Loc(label).ToString();
            if (!VPEPsycastsReflection.FocusCanUnlock(focus, pawn, out string reason))
                return string.IsNullOrEmpty(reason)
                    ? "RimWorldAccess.VPEPsycasts.Focus.Locked".Loc(label).ToString()
                    : "RimWorldAccess.VPEPsycasts.Focus.LockedReason".Loc(label, reason.StripTags()).ToString();
            return VPEPsycastsReflection.GetPoints(hediff) >= 1
                ? "RimWorldAccess.VPEPsycasts.Focus.Unlockable".Loc(label).ToString()
                : "RimWorldAccess.VPEPsycasts.Focus.NoPoints".Loc(label).ToString();
        }
    }
}
