using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Keyboard navigation for <c>VEF.Factions.Dialog_NewFactionSpawningSettlements</c> — the
    /// follow-up asked after choosing "add the faction with settlements": how many settlements to
    /// place and how far from the player they must stay. Both are mouse-drag sliders in VEF, so
    /// without this the previous dialog's "add with settlements" choice led straight into a dead end.
    ///
    /// Flat list of two adjustable rows plus VEF's own Spawn/Cancel buttons. Left/Right adjust the
    /// selected value (Shift for a coarser step), mirroring the slider handling used by
    /// GizmoNavigationState and ModSettingsMenuState.
    /// </summary>
    public static class NewFactionSettlementsState
    {
        public static bool IsActive { get; private set; }

        private enum ItemKind { Settlements, Distance, Spawn, Cancel }

        private static Window dialog;
        private static List<ItemKind> items = new List<ItemKind>();
        private static int selectedIndex;

        public static void Open(Window window)
        {
            if (window == null || !VEFFactionsReflection.SettlementsAvailable)
                return;

            try
            {
                dialog = window;
                IsActive = true;
                selectedIndex = 0;

                items.Clear();
                items.Add(ItemKind.Settlements);
                items.Add(ItemKind.Distance);
                items.Add(ItemKind.Spawn);
                items.Add(ItemKind.Cancel);

                TolkHelper.Speak("RimWorldAccess.VEFFactions.SettlementsOpened".Loc(), SpeechPriority.High);
                AnnounceCurrent(SpeechPriority.Normal);
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error in NewFactionSettlementsState.Open: {ex.Message}");
                Close();
                TolkHelper.Speak("RimWorldAccess.VEFFactions.OpenFailed".Loc(), SpeechPriority.High);
            }
        }

        public static void Close()
        {
            IsActive = false;
            dialog = null;
            items.Clear();
            selectedIndex = 0;
        }

        // VEF's own slider bounds.
        private static int SettlementsMax()
            => Mathf.Max(VEFFactionsReflection.GetSettlementsRecommended(dialog) * 4, 10);

        private static int DistanceMax()
            => Mathf.Max(VEFFactionsReflection.GetDistanceRecommended(dialog) * 2, 1);

        private static string LabelFor(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Settlements:
                    return "VanillaFactionsExpanded.FactionSettlementsToSpawn".Translate(
                        VEFFactionsReflection.GetSettlementsRecommended(dialog),
                        VEFFactionsReflection.GetSettlementsToSpawn(dialog)).ToString().StripTags();

                case ItemKind.Distance:
                    return "VanillaFactionsExpanded.FactionMinDistance".Translate(
                        VEFFactionsReflection.GetDistanceRecommended(dialog),
                        VEFFactionsReflection.GetDistanceToSpawn(dialog)).ToString().StripTags();

                case ItemKind.Spawn:
                    return "VanillaFactionsExpanded.FactionButtonSpawn".Translate();

                default:
                    return "VanillaFactionsExpanded.FactionButtonCancel".Translate();
            }
        }

        private static void AnnounceCurrent(SpeechPriority priority = SpeechPriority.Low)
        {
            if (items.Count == 0) return;
            TolkHelper.SpeakData(
                LabelFor(items[selectedIndex]) + MenuHelper.FormatPosition(selectedIndex, items.Count), priority);
        }

        private static void Adjust(int direction, bool coarse)
        {
            if (items.Count == 0) return;
            ItemKind kind = items[selectedIndex];
            if (kind != ItemKind.Settlements && kind != ItemKind.Distance)
                return;

            int step = coarse ? 5 : 1;
            int current = kind == ItemKind.Settlements
                ? VEFFactionsReflection.GetSettlementsToSpawn(dialog)
                : VEFFactionsReflection.GetDistanceToSpawn(dialog);
            int max = kind == ItemKind.Settlements ? SettlementsMax() : DistanceMax();

            int updated = Mathf.Clamp(current + direction * step, 1, max);
            if (updated == current)
            {
                NumericStepperHelper.SpeakBoundary(direction);
                return;
            }

            if (kind == ItemKind.Settlements)
                VEFFactionsReflection.SetSettlementsToSpawn(dialog, updated);
            else
                VEFFactionsReflection.SetDistanceToSpawn(dialog, updated);

            // The label embeds the live value, so re-reading it announces the new setting.
            TolkHelper.SpeakData(LabelFor(kind));
        }

        public static bool HandleInput(Event ev)
        {
            if (!IsActive || ev.type != EventType.KeyDown || items.Count == 0)
                return false;

            KeyCode key = ev.keyCode;

            if (key == KeyCode.UpArrow)
            {
                selectedIndex = MenuHelper.SelectPrevious(selectedIndex, items.Count);
                AnnounceCurrent();
                return true;
            }

            if (key == KeyCode.DownArrow)
            {
                selectedIndex = MenuHelper.SelectNext(selectedIndex, items.Count);
                AnnounceCurrent();
                return true;
            }

            if (key == KeyCode.Home)
            {
                selectedIndex = MenuHelper.JumpToFirst();
                AnnounceCurrent();
                return true;
            }

            if (key == KeyCode.End)
            {
                selectedIndex = MenuHelper.JumpToLast(items.Count);
                AnnounceCurrent();
                return true;
            }

            if (key == KeyCode.LeftArrow)
            {
                Adjust(-1, ev.shift);
                return true;
            }

            if (key == KeyCode.RightArrow)
            {
                Adjust(1, ev.shift);
                return true;
            }

            if (key == KeyCode.Return || key == KeyCode.KeypadEnter || key == KeyCode.Space)
            {
                ItemKind kind = items[selectedIndex];
                if (kind == ItemKind.Spawn)
                    VEFFactionsReflection.Spawn(dialog);
                else if (kind == ItemKind.Cancel)
                    CloseDialog();
                else
                    AnnounceCurrent(SpeechPriority.Normal);
                return true;
            }

            if (key == KeyCode.Escape)
            {
                CloseDialog();
                return true;
            }

            return true;
        }

        private static void CloseDialog()
        {
            Window toClose = dialog;
            TolkHelper.Speak("RimWorldAccess.VEFFactions.SettlementsCancelled".Loc(), SpeechPriority.High);
            if (toClose != null)
                Find.WindowStack.TryRemove(toClose, doCloseSound: false);
        }
    }
}
