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
        private enum View { Root, Paths, Abilities, Foci, Psysets, PsysetEdit }
        private enum ItemKind
        {
            Status, GotoPaths, GotoFoci, GotoPsysets, ImproveStats,
            Path, Ability, Focus, Psyset, PsysetCreate, PsysetAbility
        }

        private class Item
        {
            public ItemKind Kind;
            public Def Path;                 // Path / current-path payload
            public Def Ability;              // Ability / psyset-ability payload
            public MeditationFocusDef Focus; // Focus payload
            public object Psyset;            // Psyset payload (VPE PsySet instance)
        }

        private static bool isActive;
        private static Pawn pawn;
        private static object hediff;   // Hediff_PsycastAbilities
        private static object comp;     // VEF CompAbilities

        private static View view;
        private static Def currentPath;    // when view == Abilities
        private static object currentPsyset; // when view == PsysetEdit
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
            currentPsyset = null;
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
                    if (VPEPsycastsReflection.PsysetsAvailable)
                        items.Add(new Item { Kind = ItemKind.GotoPsysets });
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
                    foreach (var a in OrderAbilitiesLogically(currentPath))
                        items.Add(new Item { Kind = ItemKind.Ability, Ability = a });
                    break;

                case View.Foci:
                    // Already-usable foci first, then by name — a predictable, stable order.
                    foreach (var f in VPEPsycastsReflection.GetAllFoci()
                                 .OrderByDescending(f => VPEPsycastsReflection.FocusCanPawnUse(f, pawn))
                                 .ThenBy(f => f.label))
                        items.Add(new Item { Kind = ItemKind.Focus, Focus = f });
                    break;

                case View.Psysets:
                    var psysets = VPEPsycastsReflection.GetPsysets(hediff);
                    if (psysets != null)
                        foreach (var ps in psysets)
                            items.Add(new Item { Kind = ItemKind.Psyset, Psyset = ps });
                    items.Add(new Item { Kind = ItemKind.PsysetCreate });
                    break;

                case View.PsysetEdit:
                    foreach (var a in VPEPsycastsReflection.GetLearnedPsycastAbilities(comp)
                                 .OrderBy(a => a.label))
                        items.Add(new Item { Kind = ItemKind.PsysetAbility, Ability = a });
                    break;
            }

            if (selectedIndex >= items.Count) selectedIndex = System.Math.Max(0, items.Count - 1);
        }

        // ===== KEYBOARD =====

        public static bool HandleInput(Event evt)
        {
            if (!isActive || evt.type != EventType.KeyDown) return false;

            // Defer entirely while a modal text session owns input (our psyset rename dialog):
            // let UnifiedKeyboardPatch's -1.6 text dispatch handle every key instead of stealing them.
            if (TextInputManager.IsActive) return false;

            KeyCode key = evt.keyCode;

            // Alt+D speaks the focused element's description; Alt+C speaks its numeric data (cast
            // cost, neural heat, level, range, AOE, or the pawn's psycaster stats). VPE's own info
            // card carries no numeric data for its ability defs, so terse on-demand spoken shortcuts
            // beat a heavy Dialog_InfoCard here. Checked before typeahead — Alt+letter never reaches it.
            if (KeyboardHelper.IsAltHeld && key == KeyCode.D)
            {
                AnnounceDescription();
                return true;
            }
            if (KeyboardHelper.IsAltHeld && key == KeyCode.C)
            {
                AnnounceData();
                return true;
            }

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

                case KeyCode.RightArrow:
                    // Expand only — never commits an action. Drills into an unlocked path's
                    // abilities; inert on anything else. Enter is the only key that spends points.
                    if (typeahead.HasActiveSearch && !typeahead.HasNoMatches) return true;
                    Expand();
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
                case KeyCode.Space:
                    Activate();
                    return true;

                case KeyCode.Delete:
                    DeleteCurrentPsyset();
                    return true;

                case KeyCode.F2:
                    RenameCurrentPsyset();
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
                case View.PsysetEdit:
                    view = View.Psysets;
                    currentPsyset = null;
                    break;
                case View.Paths:
                case View.Foci:
                case View.Psysets:
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

                case ItemKind.GotoPsysets:
                    EnterView(View.Psysets);
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

                case ItemKind.Psyset:
                    currentPsyset = item.Psyset;
                    EnterView(View.PsysetEdit);
                    return;

                case ItemKind.PsysetCreate:
                    CreatePsyset();
                    return;

                case ItemKind.PsysetAbility:
                    TogglePsysetAbility(item.Ability);
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
            string result = "RimWorldAccess.VPEPsycasts.StatsImproved".Loc(VPEPsycastsReflection.GetPoints(hediff)).ToString();
            TolkHelper.SpeakData($"{result}. {BuildStatsDetails()}");
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

        // ===== Psyset actions =====

        private static void CreatePsyset()
        {
            int n = (VPEPsycastsReflection.GetPsysets(hediff)?.Count ?? 0) + 1;
            string name = "RimWorldAccess.VPEPsycasts.Psyset.DefaultName".Loc(n).ToString();
            var ps = VPEPsycastsReflection.CreatePsyset(hediff, name);
            if (ps == null)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                return;
            }
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            currentPsyset = ps;
            EnterView(View.PsysetEdit);
            TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.Psyset.Created".Loc(name).ToString(), SpeechPriority.High);
        }

        private static void TogglePsysetAbility(Def ability)
        {
            if (currentPsyset == null || ability == null) return;
            bool nowIn = VPEPsycastsReflection.PsysetToggle(currentPsyset, ability);
            (nowIn ? SoundDefOf.Tick_High : SoundDefOf.Tick_Low).PlayOneShotOnCamera();
            TolkHelper.SpeakData((nowIn
                ? "RimWorldAccess.VPEPsycasts.Psyset.AbilityAdded".Loc(ability.LabelCap)
                : "RimWorldAccess.VPEPsycasts.Psyset.AbilityRemoved".Loc(ability.LabelCap)).ToString());
        }

        private static void DeleteCurrentPsyset()
        {
            if (view != View.Psysets || items.Count == 0 || selectedIndex < 0 || selectedIndex >= items.Count)
                return;
            var item = items[selectedIndex];
            if (item.Kind != ItemKind.Psyset) { AnnounceCurrent(); return; }

            string name = VPEPsycastsReflection.GetPsysetName(item.Psyset);
            VPEPsycastsReflection.RemovePsyset(hediff, item.Psyset);
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            BuildItems();
            if (selectedIndex >= items.Count) selectedIndex = System.Math.Max(0, items.Count - 1);
            TolkHelper.SpeakData("RimWorldAccess.VPEPsycasts.Psyset.Deleted".Loc(name ?? "").ToString(), SpeechPriority.High);
        }

        private static void RenameCurrentPsyset()
        {
            if (view != View.Psysets || items.Count == 0 || selectedIndex < 0 || selectedIndex >= items.Count)
                return;
            var item = items[selectedIndex];
            if (item.Kind != ItemKind.Psyset) return;
            // Opens VPE's Dialog_RenamePsyset (a vanilla Dialog_Rename<PsySet>), which RWA's text-input
            // pipeline makes accessible; our HandleInput defers while that session is active.
            VPEPsycastsReflection.OpenRenamePsysetDialog(item.Psyset);
        }

        /// <summary>
        /// Right-arrow: expand/drill in only, never commits a point-spend. Enters a submenu from
        /// the root, or an unlocked path's ability list; inert elsewhere (re-announces so silence
        /// isn't mistaken for a missed keypress).
        /// </summary>
        private static void Expand()
        {
            if (items.Count == 0 || selectedIndex < 0 || selectedIndex >= items.Count) return;
            var item = items[selectedIndex];
            switch (item.Kind)
            {
                case ItemKind.GotoPaths:
                    typeahead.ClearSearch();
                    EnterView(View.Paths);
                    return;
                case ItemKind.GotoFoci:
                    typeahead.ClearSearch();
                    EnterView(View.Foci);
                    return;
                case ItemKind.GotoPsysets:
                    typeahead.ClearSearch();
                    EnterView(View.Psysets);
                    return;
                case ItemKind.Psyset:
                    typeahead.ClearSearch();
                    currentPsyset = item.Psyset;
                    EnterView(View.PsysetEdit);
                    return;
                case ItemKind.Path:
                    if (VPEPsycastsReflection.IsPathUnlocked(hediff, item.Path) &&
                        VPEPsycastsReflection.PathHasAbilities(item.Path))
                    {
                        typeahead.ClearSearch();
                        EnterView(View.Abilities, item.Path);
                        return;
                    }
                    break;
            }
            AnnounceCurrent();
        }

        /// <summary>
        /// Orders a path's abilities so prerequisites always precede the abilities that depend on
        /// them (a topological sort), tie-broken by level, then VPE's own order, then name. This
        /// fixes the confusing raw order where e.g. "Word of Serenity" preceded its own
        /// prerequisite "Word of Love".
        /// </summary>
        private static List<Def> OrderAbilitiesLogically(Def path)
        {
            var abilities = VPEPsycastsReflection.GetPathAbilities(path);
            var set = new HashSet<Def>(abilities);
            var prereqs = new Dictionary<Def, List<Def>>();
            foreach (var a in abilities)
                prereqs[a] = VPEPsycastsReflection.GetAbilityPrerequisites(a).Where(set.Contains).ToList();

            var result = new List<Def>();
            var placed = new HashSet<Def>();
            var remaining = new List<Def>(abilities);
            while (remaining.Count > 0)
            {
                var ready = remaining
                    .Where(a => prereqs[a].All(placed.Contains))
                    .OrderBy(VPEPsycastsReflection.GetAbilityLevel)
                    .ThenBy(VPEPsycastsReflection.GetAbilityOrder)
                    .ThenBy(a => a.label)
                    .ToList();
                if (ready.Count == 0)
                {
                    // Prerequisite cycle (shouldn't happen with real data) — append the rest
                    // deterministically so nothing is dropped.
                    foreach (var a in remaining
                                 .OrderBy(VPEPsycastsReflection.GetAbilityLevel)
                                 .ThenBy(VPEPsycastsReflection.GetAbilityOrder)
                                 .ThenBy(a => a.label))
                        result.Add(a);
                    break;
                }
                var next = ready[0];
                result.Add(next);
                placed.Add(next);
                remaining.Remove(next);
            }
            return result;
        }

        // ===== ON-DEMAND INFO (Alt+D description / Alt+C data) =====

        /// <summary>Def under the current row that carries a description / cast data.</summary>
        private static Def CurrentDef()
        {
            if (items.Count == 0 || selectedIndex < 0 || selectedIndex >= items.Count) return null;
            var item = items[selectedIndex];
            switch (item.Kind)
            {
                case ItemKind.Ability:
                case ItemKind.PsysetAbility: return item.Ability;
                case ItemKind.Path: return item.Path;
                case ItemKind.Focus: return item.Focus;
                default: return null;
            }
        }

        /// <summary>Alt+D — speak the focused element's description.</summary>
        private static void AnnounceDescription()
        {
            var def = CurrentDef();
            if (def == null) { AnnounceCurrent(); return; }
            string desc = SanitizeText(def.description);
            TolkHelper.SpeakData(string.IsNullOrEmpty(desc)
                ? "RimWorldAccess.VPEPsycasts.NoDescription".Loc().ToString()
                : $"{def.LabelCap}. {desc}", SpeechPriority.High);
        }

        /// <summary>Alt+C — speak the focused element's numeric data (cast cost, level, range, AOE).</summary>
        private static void AnnounceData()
        {
            if (items.Count == 0 || selectedIndex < 0 || selectedIndex >= items.Count) return;
            var item = items[selectedIndex];
            switch (item.Kind)
            {
                case ItemKind.Ability:
                case ItemKind.PsysetAbility:
                    TolkHelper.SpeakData(BuildAbilityData(item.Ability), SpeechPriority.High);
                    return;
                case ItemKind.Status:
                case ItemKind.ImproveStats:
                    TolkHelper.SpeakData(BuildStatsDetails(), SpeechPriority.High);
                    return;
                default:
                    TolkHelper.Speak("RimWorldAccess.VPEPsycasts.NoData".Loc());
                    return;
            }
        }

        private static string BuildAbilityData(Def ability)
        {
            if (ability == null) return "RimWorldAccess.VPEPsycasts.NoData".Loc().ToString();
            var parts = new List<string>();
            float psy = VPEPsycastsReflection.GetAbilityPsyfocusCost(ability, pawn);
            if (psy > 0.0001f) parts.Add("RimWorldAccess.VPEPsycasts.Data.Psyfocus".Loc(psy.ToStringPercent()).ToString());
            float heat = VPEPsycastsReflection.GetAbilityNeuralHeat(ability, pawn);
            if (heat > 0.0001f) parts.Add("RimWorldAccess.VPEPsycasts.Data.NeuralHeat".Loc(heat.ToString("0.#")).ToString());
            parts.Add("RimWorldAccess.VPEPsycasts.Data.Level".Loc(VPEPsycastsReflection.GetAbilityLevel(ability)).ToString());
            float range = VPEPsycastsReflection.GetAbilityRange(ability);
            if (range > 0f && range < 1000f) parts.Add("RimWorldAccess.VPEPsycasts.Data.Range".Loc(range.ToString("0")).ToString());
            float radius = VPEPsycastsReflection.GetAbilityRadius(ability);
            if (radius > 0f) parts.Add("RimWorldAccess.VPEPsycasts.Data.Radius".Loc(radius.ToString("0")).ToString());
            return "RimWorldAccess.VPEPsycasts.Data.Prefix".Loc(ability.LabelCap, string.Join(". ", parts)).ToString();
        }

        private static string SanitizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.StripTags().Replace("\n\n", ". ").Replace("\n", " ").Trim();
        }

        private static string BuildStatsDetails()
        {
            // The four psycaster stats a sighted user reads next to the Upgrade button.
            var parts = new List<string>();
            AppendStat(parts, StatDefOf.PsychicEntropyMax);
            AppendStat(parts, StatDefOf.PsychicEntropyRecoveryRate);
            AppendStat(parts, StatDefOf.PsychicSensitivity);
            return "RimWorldAccess.VPEPsycasts.CurrentStats".Loc(string.Join(". ", parts)).ToString();
        }

        private static void AppendStat(List<string> parts, StatDef stat)
        {
            try { parts.Add($"{stat.LabelCap}: {stat.ValueToString(pawn.GetStatValue(stat))}"); }
            catch { /* stat unavailable — skip */ }
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
                       VPEPsycastsReflection.GetPoints(hediff)).ToString() +
                   $" {"RimWorldAccess.VPEPsycasts.DetailsHint".Loc()}";
        }

        private static string BuildViewHeader()
        {
            switch (view)
            {
                case View.Paths: return "RimWorldAccess.VPEPsycasts.Header.Paths".Loc().ToString();
                case View.Abilities: return "RimWorldAccess.VPEPsycasts.Header.Abilities".Loc(currentPath?.LabelCap ?? "").ToString();
                case View.Foci: return "RimWorldAccess.VPEPsycasts.Header.Foci".Loc().ToString();
                case View.Psysets: return "RimWorldAccess.VPEPsycasts.Header.Psysets".Loc().ToString();
                case View.PsysetEdit: return "RimWorldAccess.VPEPsycasts.Header.PsysetEdit".Loc(VPEPsycastsReflection.GetPsysetName(currentPsyset) ?? "").ToString();
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
                case ItemKind.GotoPsysets: return "RimWorldAccess.VPEPsycasts.Item.Psysets".Loc().ToString();
                case ItemKind.ImproveStats: return "RimWorldAccess.VPEPsycasts.Item.ImproveStats".Loc().ToString();
                case ItemKind.Path: return item.Path?.LabelCap;
                case ItemKind.Ability: return item.Ability?.LabelCap;
                case ItemKind.Focus: return item.Focus?.LabelCap;
                case ItemKind.Psyset: return VPEPsycastsReflection.GetPsysetName(item.Psyset);
                case ItemKind.PsysetCreate: return "RimWorldAccess.VPEPsycasts.Item.PsysetCreate".Loc().ToString();
                case ItemKind.PsysetAbility: return item.Ability?.LabelCap;
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

                case ItemKind.GotoPsysets:
                    return "RimWorldAccess.VPEPsycasts.Item.PsysetsCount".Loc(
                        VPEPsycastsReflection.GetPsysets(hediff)?.Count ?? 0).ToString();

                case ItemKind.ImproveStats:
                    return "RimWorldAccess.VPEPsycasts.Item.ImproveStatsFull".Loc().ToString();

                case ItemKind.Path: return BuildPathAnnouncement(item.Path);
                case ItemKind.Ability: return BuildAbilityAnnouncement(item.Ability);
                case ItemKind.Focus: return BuildFocusAnnouncement(item.Focus);

                case ItemKind.Psyset:
                    return "RimWorldAccess.VPEPsycasts.Psyset.Row".Loc(
                        VPEPsycastsReflection.GetPsysetName(item.Psyset) ?? "",
                        VPEPsycastsReflection.GetPsysetAbilityCount(item.Psyset)).ToString();

                case ItemKind.PsysetCreate:
                    return "RimWorldAccess.VPEPsycasts.Item.PsysetCreateFull".Loc().ToString();

                case ItemKind.PsysetAbility:
                {
                    string state = (VPEPsycastsReflection.PsysetContains(currentPsyset, item.Ability)
                        ? "RimWorldAccess.VPEPsycasts.Psyset.InSet".Loc()
                        : "RimWorldAccess.VPEPsycasts.Psyset.NotInSet".Loc()).ToString();
                    return "RimWorldAccess.VPEPsycasts.PsysetAbility".Loc(item.Ability.LabelCap, state).ToString();
                }

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
