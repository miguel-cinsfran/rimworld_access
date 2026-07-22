using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Keyboard navigation for the Vanilla Expanded Framework's new-faction prompt
    /// (<c>VEF.Factions.Dialog_NewFactionSpawning</c>) — the window that appears on load when a
    /// mod has added factions the world doesn't have yet.
    ///
    /// The dialog is mouse-only apart from Enter/Escape and announces nothing, so a screen-reader
    /// user could only ever press Escape — which VEF treats as "do nothing", meaning the same
    /// prompt reappears on every single load and the faction is never actually decided on.
    ///
    /// Presented as a flat list (the EntityTabState shape): a read-only information row followed by
    /// VEF's own action buttons. All user-facing text reuses the framework's own translated keys so
    /// terminology stays in sync with whatever VEF ships.
    /// </summary>
    public static class NewFactionSpawningState
    {
        public static bool IsActive { get; private set; }

        private enum ItemKind { Info, AddWithSettlements, Add, Skip, Ignore }

        private class Item
        {
            public ItemKind Kind;
            public string Label;
        }

        private static Window dialog;
        private static FactionDef factionDef;
        private static List<Item> items = new List<Item>();
        private static int selectedIndex;
        private static TypeaheadSearchHelper typeahead = new TypeaheadSearchHelper();

        /// <summary>
        /// Set once a choice has been committed for the current faction. Each prompt in VEF's chain
        /// gets its own <see cref="Open"/>, so this permits exactly one decision per faction and a
        /// repeated Enter (key repeat, or a second event in the same frame) cannot add the faction
        /// twice — which would leave duplicate factions in the world.
        /// </summary>
        private static bool actionTaken;

        public static void Open(Window window)
        {
            if (window == null || !VEFFactionsReflection.Available)
                return;

            try
            {
                dialog = window;
                factionDef = VEFFactionsReflection.GetFactionDef(window);
                if (factionDef == null)
                    return;

                IsActive = true;
                selectedIndex = 0;
                actionTaken = false;
                typeahead.ClearSearch();
                RebuildItems();

                // We drive Escape ourselves (announcing that the faction was only skipped), so stop
                // vanilla closing the window behind us. Enter is handled separately: VEF overrides
                // OnAcceptKeyPressed to add the faction outright regardless of this flag, which is
                // why NewFactionSpawningPatch also patches that override out while we are active.
                window.closeOnCancel = false;
                window.closeOnAccept = false;

                // Read the whole prompt first — what faction, from which mod, and why it matters —
                // then land on the first choice.
                TolkHelper.SpeakData(BuildFullDescription(), SpeechPriority.High);
                AnnounceCurrent(SpeechPriority.Normal);
            }
            catch (System.Exception ex)
            {
                // Never leave the user trapped in a mute, input-absorbing modal (the VREAndroid
                // lesson): announce and let vanilla Escape work.
                ModLogger.Error($"Error in NewFactionSpawningState.Open: {ex.Message}");
                Close();
                TolkHelper.Speak("RimWorldAccess.VEFFactions.OpenFailed".Loc(), SpeechPriority.High);
            }
        }

        public static void Close()
        {
            IsActive = false;
            dialog = null;
            factionDef = null;
            items.Clear();
            selectedIndex = 0;
            typeahead.ClearSearch();
        }

        private static void RebuildItems()
        {
            items.Clear();
            items.Add(new Item { Kind = ItemKind.Info, Label = "RimWorldAccess.VEFFactions.InfoRow".Loc().ToString() });

            // VEF offers exactly one of the two "add" variants — mirror its own condition.
            if (VEFFactionsReflection.CanSpawnWithSettlements(factionDef))
            {
                items.Add(new Item
                {
                    Kind = ItemKind.AddWithSettlements,
                    Label = "VanillaFactionsExpanded.FactionButtonAddFull".Translate()
                });
            }
            else
            {
                items.Add(new Item
                {
                    Kind = ItemKind.Add,
                    Label = "VanillaFactionsExpanded.FactionButtonAdd".Translate()
                });
            }

            items.Add(new Item { Kind = ItemKind.Skip, Label = "VanillaFactionsExpanded.FactionButtonSkip".Translate() });
            items.Add(new Item { Kind = ItemKind.Ignore, Label = "VanillaFactionsExpanded.FactionButtonIgnore".Translate() });
        }

        /// <summary>
        /// The dialog's full text, assembled from VEF's own keys in the same order it draws them.
        /// </summary>
        private static string BuildFullDescription()
        {
            var parts = new List<string>();

            parts.Add("VanillaFactionsExpanded.FactionTitle".Translate(
                new NamedArgument(factionDef.LabelCap, "FactionName")).ToString());

            string modName = factionDef.modContentPack != null
                ? factionDef.modContentPack.Name
                : "VanillaFactionsExpanded.AnUnknownMod".Translate().ToString();
            parts.Add("VanillaFactionsExpanded.ModInfo".Translate(
                new NamedArgument(modName, "ModName")).ToString());

            if (factionDef.hidden)
                parts.Add("VanillaFactionsExpanded.HiddenFactionInfo".Translate().ToString());

            if (VEFFactionsReflection.ForcesPlayerToAdd(dialog))
            {
                string forced = VEFFactionsReflection.GetForcedRefusalMessage(dialog, factionDef);
                if (!string.IsNullOrEmpty(forced))
                    parts.Add(forced);
            }
            else if (factionDef.hidden && factionDef.requiredCountAtGameStart > 0)
            {
                parts.Add("VanillaFactionsExpanded.RequiredFactionInfo".Translate(
                    new NamedArgument(modName, "ModName")).ToString());
            }

            if (factionDef.requiredCountAtGameStart <= 0)
                parts.Add("VanillaFactionsExpanded.NonSpawningFactionInfo".Translate().ToString());

            parts.Add("VanillaFactionsExpanded.FactionSelectOption".Translate().ToString());

            return string.Join(". ", parts.ToArray()).StripTags();
        }

        /// <summary>
        /// True when VEF would refuse this choice — it insists the faction be added.
        /// </summary>
        private static bool IsRefusalBlocked()
        {
            return VEFFactionsReflection.ForcesPlayerToAdd(dialog)
                   && !VEFFactionsReflection.GetFailedToSpawn(dialog);
        }

        private static void AnnounceCurrent(SpeechPriority priority = SpeechPriority.Low)
        {
            if (items.Count == 0)
                return;

            Item item = items[selectedIndex];
            string text = item.Label;

            // Flag the choices VEF will reject, so the user isn't left guessing why nothing happens.
            if ((item.Kind == ItemKind.Skip || item.Kind == ItemKind.Ignore) && IsRefusalBlocked())
                text = "RimWorldAccess.VEFFactions.Unavailable".Loc(text).ToString();

            TolkHelper.SpeakData(text + MenuHelper.FormatPosition(selectedIndex, items.Count), priority);
        }

        private static void Activate()
        {
            if (items.Count == 0 || dialog == null)
                return;

            Item item = items[selectedIndex];

            // Re-reading the information is always safe; every other row commits a decision and must
            // only ever fire once for this faction.
            if (item.Kind != ItemKind.Info && actionTaken)
                return;

            switch (item.Kind)
            {
                case ItemKind.Info:
                    TolkHelper.SpeakData(BuildFullDescription(), SpeechPriority.High);
                    return;

                case ItemKind.AddWithSettlements:
                    // Opens VEF's settlement-count dialog; NewFactionSettlementsState picks it up.
                    actionTaken = true;
                    VEFFactionsReflection.SpawnWithBases(dialog);
                    return;

                case ItemKind.Add:
                    actionTaken = true;
                    VEFFactionsReflection.SpawnWithoutBases(dialog);
                    return;

                case ItemKind.Skip:
                case ItemKind.Ignore:
                    if (IsRefusalBlocked())
                    {
                        // Mirror VEF: it posts a rejection message rather than acting. Our global
                        // Messages announcer speaks the mod's own text, so just don't act.
                        string msg = VEFFactionsReflection.GetForcedRefusalMessage(dialog, factionDef);
                        TolkHelper.SpeakData(string.IsNullOrEmpty(msg)
                            ? "RimWorldAccess.VEFFactions.CannotRefuse".Loc().ToString()
                            : msg.StripTags(), SpeechPriority.High);
                        return;
                    }
                    actionTaken = true;
                    if (item.Kind == ItemKind.Skip)
                        VEFFactionsReflection.Skip(dialog);
                    else
                        VEFFactionsReflection.Ignore(dialog);
                    return;
            }
        }

        public static void HandleTypeahead(char c)
        {
            if (!IsActive || items.Count == 0)
                return;

            var labels = new List<string>();
            foreach (Item i in items) labels.Add(i.Label);

            int newIndex;
            if (typeahead.ProcessCharacterInput(c, labels, out newIndex))
            {
                if (newIndex >= 0)
                {
                    selectedIndex = newIndex;
                    AnnounceCurrent();
                }
            }
            else
            {
                typeahead.SpeakNoMatches();
            }
        }

        public static bool HandleInput(Event ev)
        {
            if (!IsActive || ev.type != EventType.KeyDown || items.Count == 0)
                return false;

            // The settlements follow-up owns the keyboard while it is up.
            if (NewFactionSettlementsState.IsActive)
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

            if (key == KeyCode.Return || key == KeyCode.KeypadEnter || key == KeyCode.Space)
            {
                Activate();
                return true;
            }

            if (key == KeyCode.Backspace && typeahead.HasActiveSearch)
            {
                var labels = new List<string>();
                foreach (Item i in items) labels.Add(i.Label);
                int newIndex;
                if (typeahead.ProcessBackspace(labels, out newIndex))
                {
                    if (newIndex >= 0) selectedIndex = newIndex;
                    AnnounceCurrent();
                }
                return true;
            }

            if (key == KeyCode.Escape)
            {
                if (typeahead.HasActiveSearch)
                {
                    typeahead.ClearSearchAndAnnounce();
                    AnnounceCurrent();
                    return true;
                }
                // VEF's own Escape means "do nothing" (closeOnCancel), which silently re-queues the
                // prompt for the next load. Do exactly that, but say so — the previous behaviour left
                // the user thinking they had dismissed it for good.
                if (IsRefusalBlocked())
                {
                    string msg = VEFFactionsReflection.GetForcedRefusalMessage(dialog, factionDef);
                    TolkHelper.SpeakData(string.IsNullOrEmpty(msg)
                        ? "RimWorldAccess.VEFFactions.CannotRefuse".Loc().ToString()
                        : msg.StripTags(), SpeechPriority.High);
                    return true;
                }
                if (actionTaken)
                    return true;
                actionTaken = true;
                TolkHelper.Speak("RimWorldAccess.VEFFactions.SkippedForNow".Loc(), SpeechPriority.High);
                VEFFactionsReflection.Skip(dialog);
                return true;
            }

            // Swallow everything else: the dialog is modal and absorbs input anyway, and letting
            // game shortcuts through here would act on a world the user can't see.
            return true;
        }
    }
}
