using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimWorldAccess
{
    /// <summary>
    /// Keyboard accessibility for the third-party mod Vanilla Races Expanded - Android's gene
    /// creation windows: Window_AndroidCreation, Window_AndroidModification and
    /// Window_CreateAndroidXenotype. All three extend the vanilla GeneCreationDialogBase - the
    /// same base class as vanilla Dialog_CreateXenotype (see Biotech/XenotypeEditorState) - so
    /// this state reuses the exact same tabbed-treeview architecture (Selected Genes / Gene
    /// Library / Controls) and reads/writes the shared base-class members directly (public
    /// members via the compile-time vanilla type, private fields via cached reflection). Only
    /// the mod's own additions (selectedGenes storage, requiredItems, hardware-lock gene
    /// filtering, the android name generator, and the save/load project dialogs) go through
    /// <see cref="VREAndroidReflection"/>.
    /// </summary>
    public static class AndroidCreationState
    {
        private enum Tab { Selected, Library, Controls }

        private static bool isActive;
        public static bool IsActive => isActive;

        private static readonly TextInputController renameController = new TextInputController();
        private static readonly TextFieldSpec renameSpec = new TextFieldSpec(
            labelKey: "RimWorldAccess.VREAndroid.TextInput.LabelAndroidType",
            maxLength: 40,
            minLength: 1);
        public static bool IsRenaming => TextInputManager.Active == renameController;

        public static Window BoundWindow { get; private set; }
        private static GeneCreationDialogBase dialog;
        private static Tab currentTab;

        private static TreeNavigationHelper selectedTreeNav = new TreeNavigationHelper("AndroidCreationSelected");
        private static TreeNavigationHelper libraryTreeNav = new TreeNavigationHelper("AndroidCreationLibrary");

        private static List<ControlItem> controlItems = new List<ControlItem>();
        private static int controlIdx;

        private class ControlItem
        {
            public string Label;
            public string Tooltip;
            public Action OnActivate;
        }

        // ===== Reflection cache (private fields on the vanilla GeneCreationDialogBase) =====
        private static readonly FieldInfo fi_xenotypeName = AccessTools.Field(typeof(GeneCreationDialogBase), "xenotypeName");
        private static readonly FieldInfo fi_xenotypeNameLocked = AccessTools.Field(typeof(GeneCreationDialogBase), "xenotypeNameLocked");
        private static readonly FieldInfo fi_gcx = AccessTools.Field(typeof(GeneCreationDialogBase), "gcx");
        private static readonly FieldInfo fi_met = AccessTools.Field(typeof(GeneCreationDialogBase), "met");
        private static readonly FieldInfo fi_arc = AccessTools.Field(typeof(GeneCreationDialogBase), "arc");
        private static readonly FieldInfo fi_leftChosenGroups = AccessTools.Field(typeof(GeneCreationDialogBase), "leftChosenGroups");

        // GeneCreationDialogBase declares these as protected, so - like every private field
        // above - they need reflection even though the type itself is vanilla and compile-time
        // referenceable. Invoking a base-typed MethodInfo/PropertyInfo still respects the
        // subclass's override via normal virtual dispatch.
        private static readonly MethodInfo mi_accept = AccessTools.Method(typeof(GeneCreationDialogBase), "Accept");
        private static readonly MethodInfo mi_canAccept = AccessTools.Method(typeof(GeneCreationDialogBase), "CanAccept");
        private static readonly MethodInfo mi_onGenesChanged = AccessTools.Method(typeof(GeneCreationDialogBase), "OnGenesChanged");
        private static readonly PropertyInfo pi_header = AccessTools.Property(typeof(GeneCreationDialogBase), "Header");
        private static readonly PropertyInfo pi_acceptButtonLabel = AccessTools.Property(typeof(GeneCreationDialogBase), "AcceptButtonLabel");

        private const string LevelKey = "AndroidCreation";

        static AndroidCreationState()
        {
            selectedTreeNav.FormatItemAnnouncement = FormatTreeItemAnnouncement;
            selectedTreeNav.FormatSearchAnnouncement = FormatTreeSearchAnnouncement;
            selectedTreeNav.OnActivate = item => HandleTreeEnter(item, isSelectedTab: true);
            selectedTreeNav.OnBeforeExpand = LazyLoadChildren;
            selectedTreeNav.AnnounceChildCounts = false;

            libraryTreeNav.FormatItemAnnouncement = FormatTreeItemAnnouncement;
            libraryTreeNav.FormatSearchAnnouncement = FormatTreeSearchAnnouncement;
            libraryTreeNav.OnActivate = item => HandleTreeEnter(item, isSelectedTab: false);
            libraryTreeNav.OnBeforeExpand = LazyLoadChildren;
            libraryTreeNav.AnnounceChildCounts = false;
        }

        // ===== Lifecycle =====

        public static void Open(Window dialogInstance)
        {
            try
            {
                if (!(dialogInstance is GeneCreationDialogBase gcdb))
                    return;

                BoundWindow = dialogInstance;
                dialog = gcdb;
                isActive = true;

                RebuildAllTrees();
                BuildControlItems();

                var selected = VREAndroidReflection.GetSelectedGenes(BoundWindow);
                currentTab = (selected != null && selected.Count > 0) ? Tab.Selected : Tab.Library;

                controlIdx = 0;
                MenuHelper.ResetLevel(LevelKey);

                SoundDefOf.TabOpen.PlayOneShotOnCamera();
                AnnounceOpening();
            }
            catch (Exception ex)
            {
                Log.Error($"[RimWorld Access] Error in AndroidCreationState.Open: {ex}");
                Close();
            }
        }

        public static void Close()
        {
            isActive = false;
            if (TextInputManager.Active == renameController) TextInputManager.Clear();
            BoundWindow = null;
            dialog = null;
            selectedTreeNav.Reset();
            libraryTreeNav.Reset();
            controlItems.Clear();
            controlIdx = 0;
            MenuHelper.ResetLevel(LevelKey);
        }

        // ===== Input Handling =====

        public static bool HandleInput(Event ev)
        {
            if (!isActive || ev.type != EventType.KeyDown)
                return false;

            if (WindowlessFloatMenuState.IsActive)
                return false;

            KeyCode key = ev.keyCode;
            bool shift = ev.shift;
            bool alt = ev.alt;

            if (key == KeyCode.S && alt && !ev.control && !shift)
            {
                TryAccept();
                return true;
            }

            if (key == KeyCode.I && alt && !ev.control && !shift)
            {
                OpenInfoCard();
                return true;
            }

            if (key == KeyCode.Tab && !ev.control && !alt)
            {
                SwitchTab(!shift);
                return true;
            }

            if (key == KeyCode.Escape)
                return HandleEscape();

            if (key == KeyCode.RightBracket)
                return true;

            switch (currentTab)
            {
                case Tab.Selected:
                    return HandleTreeTabInput(ev, key, alt, selectedTreeNav, true);
                case Tab.Library:
                    return HandleTreeTabInput(ev, key, alt, libraryTreeNav, false);
                case Tab.Controls:
                    return HandleControlsInput(key, alt);
            }

            return false;
        }

        private static bool HandleTreeTabInput(Event ev, KeyCode key, bool alt, TreeNavigationHelper treeNav, bool isSelectedTab)
        {
            if (treeNav.Count == 0)
                return false;

            if (key == KeyCode.Space)
            {
                var item = treeNav.SelectedItem;
                if (item != null)
                {
                    if (!HandleTreeEnter(item, isSelectedTab))
                        treeNav.ExpandOrDrillDown();
                }
                return true;
            }

            if (key == KeyCode.PageDown && !alt)
            {
                JumpToNextTopLevel(treeNav, forward: true);
                return true;
            }

            if (key == KeyCode.PageUp && !alt)
            {
                JumpToNextTopLevel(treeNav, forward: false);
                return true;
            }

            if (key == KeyCode.LeftArrow && !alt)
            {
                HandleLeftArrow(treeNav);
                return true;
            }

            return treeNav.HandleInput(ev);
        }

        private static void HandleLeftArrow(TreeNavigationHelper treeNav)
        {
            var item = treeNav.SelectedItem;
            if (item == null)
                return;

            if (item.IsExpandable && item.IsExpanded && item.Data is GeneDef && !string.IsNullOrEmpty(item.Description))
                item.Label = item.Description;

            treeNav.CollapseOrDrillUp();
        }

        private static bool HandleControlsInput(KeyCode key, bool alt)
        {
            if (controlItems.Count == 0)
                return false;

            if (key == KeyCode.UpArrow && !alt)
            {
                controlIdx = MenuHelper.SelectPrevious(controlIdx, controlItems.Count);
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                AnnounceControlItem();
                return true;
            }

            if (key == KeyCode.DownArrow && !alt)
            {
                controlIdx = MenuHelper.SelectNext(controlIdx, controlItems.Count);
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                AnnounceControlItem();
                return true;
            }

            if (key == KeyCode.Home)
            {
                controlIdx = 0;
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                AnnounceControlItem();
                return true;
            }

            if (key == KeyCode.End)
            {
                controlIdx = controlItems.Count - 1;
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                AnnounceControlItem();
                return true;
            }

            if (key == KeyCode.Return || key == KeyCode.KeypadEnter || key == KeyCode.Space)
            {
                var item = controlItems[controlIdx];
                if (item.OnActivate != null)
                    item.OnActivate();
                else
                    SoundDefOf.ClickReject.PlayOneShotOnCamera();
                return true;
            }

            return false;
        }

        // ===== Tab Switching =====

        private static void SwitchTab(bool forward)
        {
            GetCurrentTreeNav()?.Typeahead.ClearSearch();

            int tabCount = 3;
            int idx = (int)currentTab;

            if (forward)
            {
                idx++;
                if (idx >= tabCount)
                    idx = RimWorldAccessMod_Settings.Settings?.WrapNavigation == true ? 0 : tabCount - 1;
            }
            else
            {
                idx--;
                if (idx < 0)
                    idx = RimWorldAccessMod_Settings.Settings?.WrapNavigation == true ? tabCount - 1 : 0;
            }

            currentTab = (Tab)idx;
            MenuHelper.ResetLevel(LevelKey);
            SoundDefOf.Click.PlayOneShotOnCamera();
            AnnounceTabSwitch();
        }

        // ===== Escape =====

        private static bool HandleEscape()
        {
            var treeNav = GetCurrentTreeNav();
            if (treeNav != null && treeNav.HasActiveSearch)
            {
                treeNav.Typeahead.ClearSearchAndAnnounce();
                treeNav.ReannounceCurrentItem();
                return true;
            }

            CloseDialog();
            return true;
        }

        private static void CloseDialog()
        {
            var window = BoundWindow;
            Close();
            window?.Close(doCloseSound: false);
            TolkHelper.Speak("Close".Loc());
        }

        // ===== Rename =====

        private static void OnRenameCancel()
        {
            SoundDefOf.Click.PlayOneShotOnCamera();
            TolkHelper.Speak("RimWorldAccess.Biotech.XenotypeEditor.RenameCancelled".Loc());
        }

        private static void OnRenameConfirm(string newName)
        {
            if (dialog == null) return;
            fi_xenotypeName.SetValue(dialog, newName);
            fi_xenotypeNameLocked.SetValue(dialog, true);
            BuildControlItems();
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            TolkHelper.Speak("RimWorldAccess.Biotech.XenotypeEditor.Renamed".Loc(newName));
        }

        // ===== Lazy Loading =====

        private static void LazyLoadChildren(InspectionTreeItem item)
        {
            if (item.OnActivate != null && item.Children.Count == 0)
                item.OnActivate();
        }

        // ===== Enter Key Actions =====

        private static bool HandleTreeEnter(InspectionTreeItem item, bool isSelectedTab)
        {
            if (item == null)
                return true;

            if (item.Data is GeneDef gene)
            {
                ToggleGene(gene);
                return true;
            }

            if (item.Data is GeneCategoryDef)
                return false; // fall through to default expand/collapse

            if (item.IsExpandable)
                return false;

            SoundDefOf.ClickReject.PlayOneShotOnCamera();
            return true;
        }

        // ===== Page Up/Down =====

        private static void JumpToNextTopLevel(TreeNavigationHelper treeNav, bool forward)
        {
            if (treeNav.Count == 0)
                return;

            var visible = treeNav.VisibleItems;
            int idx = treeNav.SelectedIndex;
            int start = idx;
            int direction = forward ? 1 : -1;
            int current = idx + direction;

            while (current >= 0 && current < visible.Count)
            {
                if (visible[current].IndentLevel == 0)
                {
                    treeNav.SetSelectedIndex(current);
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                    treeNav.ReannounceCurrentItem();
                    return;
                }
                current += direction;
            }

            if (RimWorldAccessMod_Settings.Settings?.WrapNavigation == true)
            {
                current = forward ? 0 : visible.Count - 1;
                while (current != start)
                {
                    if (visible[current].IndentLevel == 0)
                    {
                        treeNav.SetSelectedIndex(current);
                        SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                        treeNav.ReannounceCurrentItem();
                        return;
                    }
                    current += direction;
                    if (current < 0 || current >= visible.Count)
                        break;
                }
            }

            SoundDefOf.ClickReject.PlayOneShotOnCamera();
        }

        // ===== Gene Selection Toggle =====

        private static void ToggleGene(GeneDef gene)
        {
            if (dialog == null) return;

            var selectedList = VREAndroidReflection.GetSelectedGenes(BoundWindow);
            if (selectedList == null) return;

            bool adding;
            if (selectedList.Contains(gene))
            {
                if (!VREAndroidReflection.CanToggleOffSelected(BoundWindow, gene))
                {
                    SoundDefOf.ClickReject.PlayOneShotOnCamera();
                    TolkHelper.Speak("RimWorldAccess.VREAndroid.Creation.CannotRemoveHardware".Loc(gene.LabelCap));
                    return;
                }
                selectedList.Remove(gene);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                adding = false;
            }
            else
            {
                selectedList.Add(gene);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                adding = true;
            }

            bool nameLocked = (bool)fi_xenotypeNameLocked.GetValue(dialog);
            if (!nameLocked)
            {
                string newName = VREAndroidReflection.GetAndroidTypeName(BoundWindow);
                if (!string.IsNullOrEmpty(newName))
                    fi_xenotypeName.SetValue(dialog, newName);
            }

            mi_onGenesChanged.Invoke(dialog, null);

            GeneDef cursorGene = GetCurrentGeneDef();
            RebuildAllTrees();
            BuildControlItems();
            RestoreCursor(cursorGene);

            string biostats = FormatCurrentBiostats();
            TolkHelper.Speak(adding
                ? "RimWorldAccess.Biotech.XenotypeEditor.GeneAdded".Loc(gene.LabelCap, biostats)
                : "RimWorldAccess.Biotech.XenotypeEditor.GeneRemoved".Loc(gene.LabelCap, biostats));
        }

        private static GeneDef GetCurrentGeneDef()
        {
            InspectionTreeItem item = GetCurrentItem();
            return item?.Data as GeneDef;
        }

        private static void RestoreCursor(GeneDef cursorGene)
        {
            if (cursorGene == null) return;

            switch (currentTab)
            {
                case Tab.Selected:
                    for (int i = 0; i < selectedTreeNav.VisibleItems.Count; i++)
                        if (selectedTreeNav.VisibleItems[i].Data is GeneDef g && g == cursorGene) { selectedTreeNav.SetSelectedIndex(i); return; }
                    break;
                case Tab.Library:
                    for (int i = 0; i < libraryTreeNav.VisibleItems.Count; i++)
                        if (libraryTreeNav.VisibleItems[i].Data is GeneDef g && g == cursorGene) { libraryTreeNav.SetSelectedIndex(i); return; }
                    break;
            }
        }

        // ===== Accept =====

        /// <summary>
        /// Validates and accepts via the mod's own CanAccept()/Accept() (both public overrides
        /// on the vanilla GeneCreationDialogBase, called directly with normal virtual dispatch).
        /// The mod's own validation posts Messages.Message() on rejection, which RimWorld
        /// Access's global NotificationAccessibilityPatch already speaks - no duplicate
        /// validation logic needed here.
        /// </summary>
        private static void TryAccept()
        {
            if (dialog == null) return;

            if (!(bool)mi_canAccept.Invoke(dialog, null))
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                return;
            }

            var savedDialog = BoundWindow;
            Close();
            try
            {
                mi_accept.Invoke(savedDialog, null);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimWorld Access] Error invoking Accept: {ex}");
            }
        }

        // ===== Load / Save project =====

        private static void LoadProject()
        {
            if (dialog == null) return;

            var loadDialog = VREAndroidReflection.CreateProjectListLoadDialog(ApplyLoadedProject);
            if (loadDialog == null)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                return;
            }
            Find.WindowStack.Add(loadDialog);
        }

        private static void ApplyLoadedProject(CustomXenotype xenotype)
        {
            if (dialog == null || xenotype == null) return;

            fi_xenotypeName.SetValue(dialog, xenotype.name);
            fi_xenotypeNameLocked.SetValue(dialog, true);

            var selectedList = VREAndroidReflection.GetSelectedGenes(BoundWindow);
            var mandatory = VREAndroidReflection.AndroidGenesInOrder()?
                .Where(g => !VREAndroidReflection.CanBeRemovedFromAndroid(g)).ToList() ?? new List<GeneDef>();
            selectedList.Clear();
            selectedList.AddRange(mandatory);
            selectedList.AddRange(xenotype.genes);
            var distinct = selectedList.Distinct().ToList();
            selectedList.Clear();
            selectedList.AddRange(distinct);

            mi_onGenesChanged.Invoke(dialog, null);
            RebuildAllTrees();
            BuildControlItems();

            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            TolkHelper.Speak("RimWorldAccess.Biotech.XenotypeEditor.LoadedSummaryMany".Loc(xenotype.name, selectedList.Count, FormatCurrentBiostats()));
        }

        private static void SaveProject()
        {
            if (dialog == null) return;

            var selectedList = VREAndroidReflection.GetSelectedGenes(BoundWindow);
            var customXenotype = new CustomXenotype
            {
                name = ((string)fi_xenotypeName.GetValue(dialog))?.Trim(),
                inheritable = false
            };
            customXenotype.genes.AddRange(selectedList ?? new List<GeneDef>());

            var saveDialog = VREAndroidReflection.CreateProjectListSaveDialog(customXenotype);
            if (saveDialog == null)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                return;
            }
            Find.WindowStack.Add(saveDialog);
        }

        private static void LoadPremade()
        {
            if (dialog == null) return;

            var xenotypes = DefDatabase<XenotypeDef>.AllDefs
                .Where(VREAndroidReflection.IsAndroidType)
                .OrderByDescending(x => x.displayPriority)
                .ToList();

            if (xenotypes.Count == 0)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                TolkHelper.Speak("RimWorldAccess.Biotech.XenotypeEditor.NoPremade".Loc());
                return;
            }

            var options = new List<FloatMenuOption>();
            var infoCardDefs = new List<Def>();
            foreach (var xenotype in xenotypes)
            {
                var local = xenotype;
                options.Add(new FloatMenuOption(local.LabelCap, () => ApplyPremadeXenotype(local)));
                infoCardDefs.Add(local);
            }

            WindowlessFloatMenuState.Open(options, false, infoCardDefs: infoCardDefs);
        }

        private static void ApplyPremadeXenotype(XenotypeDef xenotype)
        {
            if (dialog == null) return;

            fi_xenotypeName.SetValue(dialog, xenotype.label);
            fi_xenotypeNameLocked.SetValue(dialog, true);

            var selectedList = VREAndroidReflection.GetSelectedGenes(BoundWindow);
            selectedList.Clear();
            selectedList.AddRange(xenotype.genes.Distinct());

            mi_onGenesChanged.Invoke(dialog, null);
            RebuildAllTrees();
            BuildControlItems();

            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            TolkHelper.Speak("RimWorldAccess.Biotech.XenotypeEditor.LoadedSummaryMany".Loc(xenotype.LabelCap, selectedList.Count, FormatCurrentBiostats()));
        }

        // ===== InfoCard =====

        private static void OpenInfoCard()
        {
            InspectionTreeItem item = GetCurrentItem();
            if (item?.Data is GeneDef geneDef)
            {
                Find.WindowStack.Add(new Dialog_InfoCard(geneDef));
                SoundDefOf.Click.PlayOneShotOnCamera();
                return;
            }
            SoundDefOf.ClickReject.PlayOneShotOnCamera();
        }

        // ===== Tree Building =====

        private static void RebuildAllTrees()
        {
            var selectedList = VREAndroidReflection.GetSelectedGenes(BoundWindow);

            var selRoot = BuildSelectedTree(selectedList);
            selectedTreeNav.Initialize(selRoot);

            var libRoot = BuildLibraryTree(selectedList);
            libraryTreeNav.Initialize(libRoot);
        }

        private static InspectionTreeItem BuildSelectedTree(List<GeneDef> selectedGenes)
        {
            var root = new InspectionTreeItem
            {
                Type = InspectionTreeItem.ItemType.Object,
                Label = "Root",
                IsExpandable = true,
                IsExpanded = true,
                IndentLevel = -1
            };

            if (selectedGenes == null || selectedGenes.Count == 0)
                return root;

            foreach (var gene in selectedGenes)
            {
                var geneNode = GeneTreeBuilder.CreateGeneNode(gene, root.IndentLevel + 1, includeCategory: false);
                if (!VREAndroidReflection.CanToggleOffSelected(BoundWindow, gene))
                {
                    string mandatorySuffix = " " + (string)"RimWorldAccess.VREAndroid.Creation.MandatorySuffix".Translate();
                    geneNode.Label += mandatorySuffix;
                    geneNode.Description = (geneNode.Description ?? geneNode.Label) + mandatorySuffix;
                }
                GeneTreeBuilder.AddChild(root, geneNode);
            }

            return root;
        }

        private static InspectionTreeItem BuildLibraryTree(List<GeneDef> selectedGenes)
        {
            var root = new InspectionTreeItem
            {
                Type = InspectionTreeItem.ItemType.Object,
                Label = "Root",
                IsExpandable = true,
                IsExpanded = true,
                IndentLevel = -1
            };

            var genes = VREAndroidReflection.AndroidGenesInOrder();
            if (genes == null)
                return root;

            GeneCategoryDef currentCategory = null;
            InspectionTreeItem categoryNode = null;

            foreach (var gene in genes)
            {
                if (!VREAndroidReflection.GeneValidator(BoundWindow, gene))
                    continue;

                if (gene.displayCategory != currentCategory)
                {
                    currentCategory = gene.displayCategory;
                    string catLabel = currentCategory?.LabelCap ?? "RimWorldAccess.Biotech.XenotypeEditor.Uncategorized".Translate().ToString();

                    categoryNode = new InspectionTreeItem
                    {
                        Type = InspectionTreeItem.ItemType.SubCategory,
                        Label = catLabel,
                        Data = currentCategory,
                        IsExpandable = true,
                        IsExpanded = false,
                        IndentLevel = 0
                    };
                    GeneTreeBuilder.AddChild(root, categoryNode);
                }

                bool isSelected = selectedGenes != null && selectedGenes.Contains(gene);
                string suffix = isSelected ? $" [{((string)"StartingPawnsSelected".Translate()).ToLower()}]" : "";

                var geneNode = GeneTreeBuilder.CreateGeneNode(gene, 1, includeCategory: false);
                geneNode.Label += suffix;
                geneNode.Data = gene;

                GeneTreeBuilder.AddChild(categoryNode, geneNode);
            }

            return root;
        }

        private static void BuildControlItems()
        {
            controlItems.Clear();

            controlItems.Add(new ControlItem
            {
                Label = FormatCurrentBiostats(),
                OnActivate = () => TolkHelper.SpeakData(FormatCurrentBiostats())
            });

            controlItems.Add(new ControlItem
            {
                Label = FormatAndroidTypeName(),
                OnActivate = () =>
                {
                    if (dialog == null) return;
                    string currentName = (string)fi_xenotypeName.GetValue(dialog);
                    renameController.Begin(currentName ?? string.Empty, renameSpec, OnRenameConfirm, OnRenameCancel, replaceOnType: true);
                }
            });

            controlItems.Add(new ControlItem
            {
                Label = FormatNameLock(),
                Tooltip = ((string)"LockNameButtonDesc".Translate()).StripTags(),
                OnActivate = () =>
                {
                    if (dialog == null) return;
                    bool locked = (bool)fi_xenotypeNameLocked.GetValue(dialog);
                    fi_xenotypeNameLocked.SetValue(dialog, !locked);
                    SoundDefOf.Click.PlayOneShotOnCamera();
                    string desc = !locked
                        ? ((string)"LockNameOn".Translate()).StripTags()
                        : ((string)"LockNameOff".Translate()).StripTags();
                    TolkHelper.SpeakData(desc);
                    controlItems[controlIdx].Label = FormatNameLock();
                }
            });

            controlItems.Add(new ControlItem
            {
                Label = ((string)"RandomizeName".Translate()).StripTags(),
                OnActivate = () =>
                {
                    if (dialog == null) return;
                    string newName = VREAndroidReflection.GetAndroidTypeName(BoundWindow);
                    if (string.IsNullOrEmpty(newName)) return;
                    fi_xenotypeName.SetValue(dialog, newName);
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    TolkHelper.SpeakData(newName);
                    BuildControlItems();
                }
            });

            controlItems.Add(new ControlItem
            {
                Label = ((string)"VREA.LoadAndroidtype".Translate()).StripTags(),
                OnActivate = LoadProject
            });

            controlItems.Add(new ControlItem
            {
                Label = ((string)"VREA.SaveAndroidtype".Translate()).StripTags(),
                OnActivate = SaveProject
            });

            if (VREAndroidReflection.IsAndroidCreationXenotypeWindow(BoundWindow))
            {
                controlItems.Add(new ControlItem
                {
                    Label = ((string)"LoadPremade".Translate()).StripTags(),
                    OnActivate = LoadPremade
                });
            }

            controlItems.Add(new ControlItem
            {
                Label = ((string)pi_acceptButtonLabel.GetValue(dialog) ?? "").StripTags(),
                OnActivate = TryAccept
            });

            controlItems.Add(new ControlItem
            {
                Label = (string)"Close".Translate(),
                OnActivate = CloseDialog
            });
        }

        // ===== Announcements =====

        private static void AnnounceOpening()
        {
            string header = ((string)pi_header.GetValue(dialog) ?? "").StripTags();
            TolkHelper.Speak("RimWorldAccess.Biotech.XenotypeEditor.OpeningSummary".Loc(header, GetTabAnnouncement()));
        }

        private static void AnnounceTabSwitch()
        {
            TolkHelper.SpeakData(GetTabAnnouncement());
        }

        private static string GetTabAnnouncement()
        {
            switch (currentTab)
            {
                case Tab.Selected:
                    string selLabel = ((string)"VREA.SelectedComponents".Translate()).StripTags();
                    return selectedTreeNav.Count == 1
                        ? "RimWorldAccess.Biotech.XenotypeEditor.TabSummaryOne".Translate(selLabel)
                        : "RimWorldAccess.Biotech.XenotypeEditor.TabSummaryMany".Translate(selLabel, selectedTreeNav.Count);
                case Tab.Library:
                    string libLabel = ((string)"VREA.Components".Translate()).CapitalizeFirst().StripTags();
                    int categoryCount = libraryTreeNav.RootItem?.Children?.Count ?? 0;
                    return categoryCount == 1
                        ? "RimWorldAccess.Biotech.XenotypeEditor.TabSummaryCategoryOne".Translate(libLabel)
                        : "RimWorldAccess.Biotech.XenotypeEditor.TabSummaryCategoryMany".Translate(libLabel, categoryCount);
                case Tab.Controls:
                    return "RimWorldAccess.Biotech.XenotypeEditor.ControlsLabel".Translate();
            }
            return "";
        }

        private static string FormatTreeItemAnnouncement(InspectionTreeItem item)
        {
            string label = item.Label.StripTags().TrimEnd();

            string stateIndicator = "";
            if (item.IsExpandable)
            {
                if (!label.EndsWith(".") && !label.EndsWith("!") && !label.EndsWith("?"))
                    label += ".";
                stateIndicator = TreeNavigationHelper.FormatExpansionSpaceSuffix(item);
            }

            var treeNav = GetTreeNavForItem(item);
            var (position, total) = treeNav.GetSiblingPosition(item);
            string positionPart = MenuHelper.FormatPosition(position - 1, total);
            string levelSuffix = MenuHelper.GetLevelSuffix(LevelKey, item.IndentLevel);

            return string.IsNullOrEmpty(positionPart)
                ? $"{label}{stateIndicator}.{levelSuffix}"
                : $"{label}{stateIndicator}. {positionPart}.{levelSuffix}";
        }

        private static string FormatTreeSearchAnnouncement(InspectionTreeItem item, TypeaheadSearchHelper typeahead)
        {
            string label = item.Label.StripTags();
            string stateIndicator = TreeNavigationHelper.FormatExpansionSpaceSuffix(item);
            return typeahead.BuildItemAnnouncement($"{label}{stateIndicator}");
        }

        private static void AnnounceControlItem()
        {
            if (controlIdx < 0 || controlIdx >= controlItems.Count)
                return;

            if (controlIdx == 0) controlItems[0].Label = FormatCurrentBiostats();
            if (controlIdx == 1) controlItems[1].Label = FormatAndroidTypeName();
            if (controlIdx == 2) controlItems[2].Label = FormatNameLock();

            var item = controlItems[controlIdx];
            string positionPart = MenuHelper.FormatPosition(controlIdx, controlItems.Count);

            var sb = new System.Text.StringBuilder();
            sb.Append(item.Label);
            if (!string.IsNullOrEmpty(item.Tooltip)) { sb.Append(". "); sb.Append(item.Tooltip); }
            if (!string.IsNullOrEmpty(positionPart)) { sb.Append(". "); sb.Append(positionPart); }
            sb.Append(".");
            TolkHelper.SpeakData(sb.ToString());
        }

        // ===== Formatting =====

        private static string FormatCurrentBiostats()
        {
            if (dialog == null) return "";

            int gcx = (int)fi_gcx.GetValue(dialog);
            int met = (int)fi_met.GetValue(dialog);
            int arc = (int)fi_arc.GetValue(dialog);

            string complexityLabel = ((string)"Complexity".Translate()).CapitalizeFirst();
            string efficiencyLabel = ((string)"RimWorldAccess.VREAndroid.Creation.PowerEfficiency".Translate()).CapitalizeFirst();

            var sb = new System.Text.StringBuilder();
            sb.Append($"{complexityLabel} {gcx}");
            sb.Append($", {efficiencyLabel} {met.ToStringWithSign()}");

            if (arc > 0)
            {
                string architesLabel = ((string)"ArchitesRequired".Translate()).CapitalizeFirst();
                sb.Append($", {architesLabel} {arc}");
            }

            var leftChosenGroups = fi_leftChosenGroups.GetValue(dialog) as System.Collections.IList;
            if (leftChosenGroups != null && leftChosenGroups.Count > 0)
                sb.Append($". {((string)"GenesConflict".Translate()).StripTags()}");

            return sb.ToString();
        }

        private static string FormatAndroidTypeName()
        {
            if (dialog == null) return "";
            string name = (string)fi_xenotypeName.GetValue(dialog);
            string labelKey = VREAndroidReflection.IsAndroidCreationXenotypeWindow(BoundWindow) ? "VREA.AndroidName" : "VREA.AndroidtypeName";
            string label = ((string)labelKey.Translate()).CapitalizeFirst().StripTags();
            if (string.IsNullOrEmpty(name))
                return "RimWorldAccess.Biotech.XenotypeEditor.NameNone".Translate(label, ((string)"NoneLower".Translate()).StripTags());
            return "RimWorldAccess.Biotech.XenotypeEditor.NameWithValue".Translate(label, name);
        }

        private static string FormatNameLock()
        {
            if (dialog == null) return "";
            bool locked = (bool)fi_xenotypeNameLocked.GetValue(dialog);
            return locked
                ? ((string)"LockNameOn".Translate()).StripTags()
                : ((string)"LockNameOff".Translate()).StripTags();
        }

        // ===== Helpers =====

        private static TreeNavigationHelper GetCurrentTreeNav()
        {
            switch (currentTab)
            {
                case Tab.Selected: return selectedTreeNav;
                case Tab.Library: return libraryTreeNav;
            }
            return null;
        }

        private static TreeNavigationHelper GetTreeNavForItem(InspectionTreeItem item)
        {
            if (selectedTreeNav.VisibleItems.Contains(item))
                return selectedTreeNav;
            if (libraryTreeNav.VisibleItems.Contains(item))
                return libraryTreeNav;
            return GetCurrentTreeNav() ?? selectedTreeNav;
        }

        private static InspectionTreeItem GetCurrentItem()
        {
            switch (currentTab)
            {
                case Tab.Selected: return selectedTreeNav.SelectedItem;
                case Tab.Library: return libraryTreeNav.SelectedItem;
            }
            return null;
        }
    }
}
