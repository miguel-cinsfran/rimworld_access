using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimWorldAccess
{
    /// <summary>
    /// Windowless menu for setting Drafted Auto-Combat's per-pawn switches across a whole
    /// selection at once (Alt+T). The mod already offers this as a right-click float menu on
    /// its Auto-Combat gizmo, but that menu is mouse-only and its labels describe just the pawn
    /// whose gizmo was clicked, which is misleading the moment the selection disagrees.
    ///
    /// The selection is very often mixed, so every row leads with how many pawns are currently
    /// on rather than a single on/off, and Left/Right set an explicit value for everyone
    /// instead of flipping each pawn independently (which would only deepen a mixed state).
    ///
    /// Rows also explain themselves when a setting cannot take effect, which is where the mod
    /// is silent and a screen reader user is left guessing:
    ///  - the three sub-settings only drive behaviour while Auto-Combat itself is on, so a row
    ///    says how many of the selected pawns have it off (the value is still stored, and takes
    ///    effect when they enable it);
    ///  - Long-Charge is refused by the mod while AI Control is on;
    ///  - Auto-Use All does nothing for a pawn with no ability the mod will auto-cast — notably
    ///    a Vanilla Psycasts Expanded psycaster, whose psycasts the mod never even looks at.
    /// </summary>
    public static class AutoCombatState
    {
        private static readonly AutoCombatReflection.Flag[] Rows =
        {
            AutoCombatReflection.Flag.Hunt,
            AutoCombatReflection.Flag.TakeCover,
            AutoCombatReflection.Flag.MeleeCharge,
            AutoCombatReflection.Flag.FullAIControl,
            AutoCombatReflection.Flag.AutoUseAll
        };

        private static readonly Dictionary<AutoCombatReflection.Flag, string> RowLabelKeys =
            new Dictionary<AutoCombatReflection.Flag, string>
            {
                { AutoCombatReflection.Flag.Hunt, "RimWorldAccess.AutoCombat.Row.AutoCombat" },
                { AutoCombatReflection.Flag.TakeCover, "RimWorldAccess.AutoCombat.Row.TakeCover" },
                { AutoCombatReflection.Flag.MeleeCharge, "RimWorldAccess.AutoCombat.Row.MeleeCharge" },
                { AutoCombatReflection.Flag.FullAIControl, "RimWorldAccess.AutoCombat.Row.AIControl" },
                { AutoCombatReflection.Flag.AutoUseAll, "RimWorldAccess.AutoCombat.Row.AutoUseAll" }
            };

        public static bool IsActive { get; private set; }

        private static readonly List<Pawn> pawns = new List<Pawn>();
        private static int selectedIndex;

        private static readonly TypeaheadSearchHelper typeahead = new TypeaheadSearchHelper();

        // ---------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------

        /// <summary>
        /// Opens the menu over the current selection. Announces why it can't open rather than
        /// staying silent: no mod, nothing selected, or a selection the mod doesn't track.
        /// </summary>
        public static void Open()
        {
            if (!AutoCombatReflection.Available)
            {
                TolkHelper.Speak("RimWorldAccess.AutoCombat.NotInstalled".Loc());
                return;
            }

            pawns.Clear();
            pawns.AddRange(AutoCombatReflection.GetTargetPawns());

            if (pawns.Count == 0)
            {
                TolkHelper.Speak("RimWorldAccess.AutoCombat.NoPawns".Loc());
                return;
            }

            IsActive = true;
            selectedIndex = 0;
            typeahead.ClearSearch();

            TolkHelper.SpeakData(
                "RimWorldAccess.AutoCombat.Opened".Loc(pawns.Count).ToString()
                + " " + "RimWorldAccess.AutoCombat.Hint".Loc().ToString());
            AnnounceCurrent(SpeechPriority.Normal);
        }

        public static void Close()
        {
            IsActive = false;
            pawns.Clear();
            selectedIndex = 0;
            typeahead.ClearSearch();
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

            // The selection can change underneath us (a pawn dies, the user clicks elsewhere).
            if (!RefreshPawns())
            {
                return true;
            }

            switch (key)
            {
                case KeyCode.DownArrow:
                    selectedIndex = MenuHelper.SelectNext(selectedIndex, Rows.Length);
                    ClearSearchOnMove();
                    AnnounceCurrent(SpeechPriority.Low);
                    return true;
                case KeyCode.UpArrow:
                    selectedIndex = MenuHelper.SelectPrevious(selectedIndex, Rows.Length);
                    ClearSearchOnMove();
                    AnnounceCurrent(SpeechPriority.Low);
                    return true;
                case KeyCode.Home:
                    selectedIndex = MenuHelper.JumpToFirst();
                    ClearSearchOnMove();
                    AnnounceCurrent(SpeechPriority.Low);
                    return true;
                case KeyCode.End:
                    selectedIndex = MenuHelper.JumpToLast(Rows.Length);
                    ClearSearchOnMove();
                    AnnounceCurrent(SpeechPriority.Low);
                    return true;
                case KeyCode.LeftArrow:
                    Apply(false);
                    return true;
                case KeyCode.RightArrow:
                    Apply(true);
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Space:
                    // Turn everyone off only when everyone is already on; otherwise bring the
                    // stragglers up. A plain per-pawn flip would leave a mixed selection mixed.
                    Apply(CountOn(CurrentFlag) < pawns.Count);
                    return true;
                case KeyCode.Escape:
                    if (typeahead.ClearSearchAndAnnounce())
                    {
                        return true;
                    }
                    Close();
                    TolkHelper.Speak("RimWorldAccess.AutoCombat.Closed".Loc());
                    return true;
            }
            return false;
        }

        private static AutoCombatReflection.Flag CurrentFlag => Rows[selectedIndex];

        /// <summary>
        /// Drops pawns that are no longer valid or selected. Closes the menu when nothing is
        /// left, so it can't keep acting on a stale selection.
        /// </summary>
        private static bool RefreshPawns()
        {
            var current = AutoCombatReflection.GetTargetPawns();
            if (current.Count == 0)
            {
                Close();
                TolkHelper.Speak("RimWorldAccess.AutoCombat.SelectionLost".Loc());
                return false;
            }
            pawns.Clear();
            pawns.AddRange(current);
            return true;
        }

        // ---------------------------------------------------------------
        // Applying
        // ---------------------------------------------------------------

        private static void Apply(bool value)
        {
            var flag = CurrentFlag;
            int changed = 0;
            int refused = 0;

            foreach (var pawn in pawns)
            {
                if (AutoCombatReflection.GetFlag(pawn, flag) == value)
                {
                    continue;
                }
                if (AutoCombatReflection.SetFlag(pawn, flag, value))
                {
                    changed++;
                }
                else
                {
                    refused++;
                }
            }

            if (changed > 0)
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
            else if (refused > 0)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
            }

            string label = RowLabelKeys[flag].Loc().ToString();
            string state = (value
                ? "RimWorldAccess.AutoCombat.On"
                : "RimWorldAccess.AutoCombat.Off").Loc().ToString();

            // Three outcomes, and they must not be confused: something moved, nothing moved
            // because it already matched, or nothing moved because the mod refused. Reporting a
            // refusal as "already set" would tell the user the opposite of the truth.
            string announcement;
            if (changed > 0)
            {
                announcement = "RimWorldAccess.AutoCombat.Applied".Loc(label, state, changed).ToString();
            }
            else if (refused > 0)
            {
                announcement = "RimWorldAccess.AutoCombat.CouldNotApply".Loc(label, state).ToString();
            }
            else
            {
                announcement = "RimWorldAccess.AutoCombat.NoChange".Loc(label, state).ToString();
            }

            if (refused > 0)
            {
                announcement += " " + RefusalExplanation(flag, refused);
            }

            TolkHelper.SpeakData(announcement);
            AnnounceCurrent(SpeechPriority.Normal);
        }

        /// <summary>Why the mod would not take the value, in the mod's own terms.</summary>
        private static string RefusalExplanation(AutoCombatReflection.Flag flag, int refused)
        {
            switch (flag)
            {
                case AutoCombatReflection.Flag.AutoUseAll:
                    return "RimWorldAccess.AutoCombat.RefusedNoAbilities".Loc(refused).ToString();
                case AutoCombatReflection.Flag.MeleeCharge:
                    return "RimWorldAccess.AutoCombat.RefusedAIControl".Loc(refused).ToString();
                default:
                    return "RimWorldAccess.AutoCombat.RefusedGeneric".Loc(refused).ToString();
            }
        }

        // ---------------------------------------------------------------
        // Announcements
        // ---------------------------------------------------------------

        private static int CountOn(AutoCombatReflection.Flag flag)
        {
            int n = 0;
            foreach (var pawn in pawns)
            {
                if (AutoCombatReflection.GetFlag(pawn, flag))
                {
                    n++;
                }
            }
            return n;
        }

        private static void AnnounceCurrent(SpeechPriority priority)
        {
            if (selectedIndex < 0 || selectedIndex >= Rows.Length)
            {
                return;
            }

            var flag = Rows[selectedIndex];
            string label = RowLabelKeys[flag].Loc().ToString();
            int on = CountOn(flag);

            string state;
            if (pawns.Count == 1)
            {
                state = (on == 1
                    ? "RimWorldAccess.AutoCombat.On"
                    : "RimWorldAccess.AutoCombat.Off").Loc().ToString();
            }
            else if (on == pawns.Count)
            {
                state = "RimWorldAccess.AutoCombat.OnAll".Loc().ToString();
            }
            else if (on == 0)
            {
                state = "RimWorldAccess.AutoCombat.OffAll".Loc().ToString();
            }
            else
            {
                state = "RimWorldAccess.AutoCombat.OnSome".Loc(on, pawns.Count).ToString();
            }

            string announcement = "RimWorldAccess.AutoCombat.Row".Loc(
                label, state, MenuHelper.FormatPosition(selectedIndex, Rows.Length)).ToString();

            string caveat = BuildCaveat(flag);
            if (!string.IsNullOrEmpty(caveat))
            {
                // Close the sentence first: the row ends in the position ("2 of 5") and the
                // caveat opens with its own count, so without a stop the two numbers run
                // together as "5 2".
                announcement = announcement.TrimEnd();
                announcement += (announcement.EndsWith(".") ? " " : ". ") + caveat;
            }

            TolkHelper.SpeakData(typeahead.BuildItemAnnouncement(announcement), priority);
        }

        /// <summary>
        /// The "why this may do nothing" clause for a row, or empty when everything applies.
        /// </summary>
        private static string BuildCaveat(AutoCombatReflection.Flag flag)
        {
            if (flag == AutoCombatReflection.Flag.Hunt)
            {
                return "";
            }

            // The three sub-settings are stored regardless, but only drive behaviour while the
            // pawn's own Auto-Combat is on.
            int autoCombatOff = 0;
            foreach (var pawn in pawns)
            {
                if (!AutoCombatReflection.GetFlag(pawn, AutoCombatReflection.Flag.Hunt))
                {
                    autoCombatOff++;
                }
            }

            var parts = new List<string>();
            if (autoCombatOff > 0)
            {
                parts.Add("RimWorldAccess.AutoCombat.CaveatAutoCombatOff".Loc(autoCombatOff).ToString());
            }

            if (flag == AutoCombatReflection.Flag.MeleeCharge)
            {
                int blocked = 0;
                foreach (var pawn in pawns)
                {
                    if (AutoCombatReflection.GetFlag(pawn, AutoCombatReflection.Flag.FullAIControl))
                    {
                        blocked++;
                    }
                }
                if (blocked > 0)
                {
                    parts.Add("RimWorldAccess.AutoCombat.CaveatAIControl".Loc(blocked).ToString());
                }
            }
            else if (flag == AutoCombatReflection.Flag.AutoUseAll)
            {
                int noAbilities = 0;
                foreach (var pawn in pawns)
                {
                    if (AutoCombatReflection.CompatibleAbilityCount(pawn) == 0)
                    {
                        noAbilities++;
                    }
                }
                if (noAbilities > 0)
                {
                    parts.Add("RimWorldAccess.AutoCombat.CaveatNoAbilities".Loc(noAbilities).ToString());
                }
            }

            return string.Join(" ", parts.ToArray());
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

            var labels = new List<string>();
            foreach (var flag in Rows)
            {
                labels.Add(RowLabelKeys[flag].Loc().ToString());
            }

            if (typeahead.ProcessCharacterInput(c, labels, out int newIndex))
            {
                selectedIndex = newIndex;
                AnnounceCurrent(SpeechPriority.Normal);
                return true;
            }

            typeahead.SpeakNoMatches();
            return true;
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
