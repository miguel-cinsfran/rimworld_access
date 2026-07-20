using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimWorldAccess
{
    /// <summary>
    /// Keyboard accessibility for the android mod's awakening choice dialog
    /// (Dialog_AndroidAwakenedChoices / ChoiceLetter_AndroidAwakened): pick up to N skill
    /// passions and exactly one trait for a colonist android whose mood extreme has triggered
    /// "awakening". Conceptually the same letter-driven passion/trait picker as vanilla's
    /// Dialog_GrowthMomentChoices (see Biotech/GrowthMomentState) but flattened into a single
    /// navigable list (intro, passions, traits, confirm) instead of tabs, since there is no
    /// separate info-tab content beyond the opening paragraph. Both dialog and letter are the
    /// mod's own types (VREAndroids namespace) so all mod-specific data flows through
    /// <see cref="VREAndroidReflection"/>; the letter itself is typed as the vanilla Letter base
    /// for members it doesn't override (Text, ShouldAutomaticallyOpenLetter, OpenLetter).
    /// </summary>
    public static class AndroidAwakenedState
    {
        private enum ItemKind { Intro, Passion, Trait, Confirm }

        private class Item
        {
            public ItemKind Kind;
            public SkillDef Skill;
            public Trait Trait;
        }

        public static bool IsActive { get; private set; }
        public static Window BoundWindow { get; private set; }

        private static ChoiceLetter letter;
        private static bool archiveView;
        private static List<SkillDef> passionChoices;
        private static List<Trait> traitChoices;
        private static int passionGainsCount;

        private static List<SkillDef> chosenPassions = new List<SkillDef>();
        private static Trait chosenTrait;

        private static List<Item> items = new List<Item>();
        private static int selectedIndex;

        public static void Open(Window dialogInstance, ChoiceLetter letterInstance)
        {
            try
            {
                if (dialogInstance == null || letterInstance == null)
                    return;

                BoundWindow = dialogInstance;
                letter = letterInstance;
                archiveView = VREAndroidReflection.GetLetterArchiveView(letter);
                passionChoices = VREAndroidReflection.GetLetterPassionChoices(letter) ?? new List<SkillDef>();
                traitChoices = VREAndroidReflection.GetLetterTraitChoices(letter) ?? new List<Trait>();
                passionGainsCount = VREAndroidReflection.GetLetterPassionGainsCount(letter);

                chosenPassions = new List<SkillDef>();
                chosenTrait = null;
                selectedIndex = 0;
                IsActive = true;

                BuildItems();
                AnnounceOpening();
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RimWorld Access] Error in AndroidAwakenedState.Open: {ex}");
                Close();
            }
        }

        public static void Close()
        {
            IsActive = false;
            BoundWindow = null;
            letter = null;
            items.Clear();
            chosenPassions = new List<SkillDef>();
            chosenTrait = null;
            selectedIndex = 0;
        }

        private static void BuildItems()
        {
            items.Clear();
            items.Add(new Item { Kind = ItemKind.Intro });

            if (!archiveView)
            {
                foreach (var skill in passionChoices)
                    items.Add(new Item { Kind = ItemKind.Passion, Skill = skill });

                foreach (var trait in traitChoices)
                    items.Add(new Item { Kind = ItemKind.Trait, Trait = trait });

                if (passionChoices.Count > 0 || traitChoices.Count > 0)
                    items.Add(new Item { Kind = ItemKind.Confirm });
            }

            if (selectedIndex >= items.Count)
                selectedIndex = items.Count - 1;
        }

        #region Input

        public static bool HandleInput(Event ev)
        {
            if (!IsActive || ev.type != EventType.KeyDown)
                return false;

            if (WindowlessConfirmationState.IsActive || WindowlessDialogState.IsActive)
                return false;

            KeyCode key = ev.keyCode;
            bool alt = KeyboardHelper.IsAltHeld;

            if (key == KeyCode.S && alt)
            {
                TryConfirm();
                return true;
            }

            if (key == KeyCode.Escape)
            {
                TryPostpone();
                return true;
            }

            if (items.Count == 0)
                return true;

            if (key == KeyCode.UpArrow) { Move(-1); return true; }
            if (key == KeyCode.DownArrow) { Move(1); return true; }
            if (key == KeyCode.Home) { selectedIndex = 0; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); AnnounceCurrent(); return true; }
            if (key == KeyCode.End) { selectedIndex = items.Count - 1; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); AnnounceCurrent(); return true; }

            if (key == KeyCode.Return || key == KeyCode.KeypadEnter || key == KeyCode.Space)
            {
                Activate();
                return true;
            }

            return true;
        }

        private static void Move(int delta)
        {
            selectedIndex = delta > 0
                ? MenuHelper.SelectNext(selectedIndex, items.Count)
                : MenuHelper.SelectPrevious(selectedIndex, items.Count);
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            AnnounceCurrent();
        }

        private static void Activate()
        {
            if (selectedIndex < 0 || selectedIndex >= items.Count)
                return;

            var item = items[selectedIndex];
            switch (item.Kind)
            {
                case ItemKind.Intro:
                    TolkHelper.SpeakData(((string)letter.Text).StripTags());
                    return;

                case ItemKind.Passion:
                    TogglePassion(item.Skill);
                    return;

                case ItemKind.Trait:
                    SelectTrait(item.Trait);
                    return;

                case ItemKind.Confirm:
                    TryConfirm();
                    return;
            }
        }

        private static void TogglePassion(SkillDef skill)
        {
            bool checkboxMode = passionGainsCount > 1;

            if (checkboxMode)
            {
                if (chosenPassions.Contains(skill))
                {
                    chosenPassions.Remove(skill);
                    SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                }
                else
                {
                    if (chosenPassions.Count >= passionGainsCount)
                    {
                        SoundDefOf.ClickReject.PlayOneShotOnCamera();
                        TolkHelper.Speak("RimWorldAccess.VREAndroid.Awakened.PassionLimitReached".Loc(passionGainsCount));
                        return;
                    }
                    chosenPassions.Add(skill);
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }
            }
            else
            {
                chosenPassions.Clear();
                chosenPassions.Add(skill);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }

            AnnounceCurrent();
        }

        private static void SelectTrait(Trait trait)
        {
            chosenTrait = trait;
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            AnnounceCurrent();
        }

        #endregion

        #region Confirm / Postpone

        private static void TryConfirm()
        {
            if (letter == null || archiveView)
                return;

            if (passionChoices.Count > 0 && chosenPassions.Count != passionGainsCount)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                TolkHelper.Speak(passionGainsCount == 1
                    ? "SelectPassionSingular".Loc()
                    : "SelectPassionsPlural".Loc(passionGainsCount));
                return;
            }

            if (traitChoices.Count > 0 && chosenTrait == null)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                TolkHelper.Speak("SelectATrait".Loc());
                return;
            }

            VREAndroidReflection.MakeChoices(letter, chosenPassions, chosenTrait);
            var confirmedLetter = letter;
            var window = BoundWindow;
            Close();
            window?.Close(doCloseSound: false);
            Find.LetterStack.RemoveLetter(confirmedLetter);
            TolkHelper.Speak("RimWorldAccess.VREAndroid.Awakened.Confirmed".Loc());
        }

        private static void TryPostpone()
        {
            if (letter == null)
                return;

            if (archiveView)
            {
                var window = BoundWindow;
                Close();
                window?.Close(doCloseSound: false);
                TolkHelper.Speak("Close".Loc());
                return;
            }

            if (letter.ShouldAutomaticallyOpenLetter)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                Pawn pawn = VREAndroidReflection.GetLetterPawn(letter);
                Messages.Message("MessageCannotPostponeGrowthMoment".Translate(pawn.Named("PAWN")), null, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            var w = BoundWindow;
            Close();
            w?.Close(doCloseSound: false);
            TolkHelper.Speak("RimWorldAccess.VREAndroid.Awakened.Postponed".Loc());
        }

        #endregion

        #region Announcements

        private static void AnnounceOpening()
        {
            Pawn pawn = VREAndroidReflection.GetLetterPawn(letter);
            string opener = ((string)"RimWorldAccess.VREAndroid.Awakened.Opening".Translate(pawn?.LabelShortCap ?? "")).StripTags();
            TolkHelper.SpeakData($"{opener} {((string)letter.Text).StripTags()}", SpeechPriority.High);
        }

        private static void AnnounceCurrent()
        {
            TolkHelper.SpeakData(BuildAnnouncement());
        }

        private static string BuildAnnouncement()
        {
            if (selectedIndex < 0 || selectedIndex >= items.Count)
                return "";

            var item = items[selectedIndex];
            string position = MenuHelper.FormatPosition(selectedIndex, items.Count);

            string body;
            switch (item.Kind)
            {
                case ItemKind.Intro:
                    body = ((string)letter.Text).StripTags();
                    break;
                case ItemKind.Passion:
                    bool selected = chosenPassions.Contains(item.Skill);
                    string state = selected
                        ? ((string)"RimWorldAccess.VREAndroid.Awakened.Selected".Translate()).StripTags()
                        : ((string)"RimWorldAccess.VREAndroid.Awakened.NotSelected".Translate()).StripTags();
                    body = $"{item.Skill.LabelCap}. {state}";
                    break;
                case ItemKind.Trait:
                    bool traitSelected = chosenTrait == item.Trait;
                    string tstate = traitSelected
                        ? ((string)"RimWorldAccess.VREAndroid.Awakened.Selected".Translate()).StripTags()
                        : ((string)"RimWorldAccess.VREAndroid.Awakened.NotSelected".Translate()).StripTags();
                    body = $"{item.Trait.LabelCap}. {tstate}";
                    break;
                case ItemKind.Confirm:
                    body = ((string)"OK".Translate()).StripTags();
                    break;
                default:
                    body = "";
                    break;
            }

            return string.IsNullOrEmpty(position) ? body : $"{body}. {position}";
        }

        #endregion
    }
}
