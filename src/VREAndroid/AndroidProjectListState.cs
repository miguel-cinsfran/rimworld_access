using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimWorldAccess
{
    /// <summary>
    /// Keyboard-accessible state for the android mod's saved-project picker
    /// (Dialog_AndroidProjectList_Load / _Save). Both extend the vanilla Dialog_FileList
    /// unchanged, so every member used here (files, typingName, ShouldDoTypeInField,
    /// DoFileInteraction, ReloadFiles, interactButLabel) is reached through the compile-time
    /// vanilla type - Dialog_FileList declares them all `protected`, so (like IdeoLoadState)
    /// this still needs reflection, just against the vanilla base type rather than a mod type.
    /// Extends IdeoBuilder/IdeoLoadState's pattern to also cover the type-in-a-name Save case
    /// (WindowlessSaveMenuState's dual-focus-zone design: TextField then List, Down enters the
    /// list, Up from index 0 returns to the field).
    /// </summary>
    public static class AndroidProjectListState
    {
        private enum Focus { TextField, List }

        public static bool IsActive { get; private set; }
        public static bool HasActiveSearch => !IsInTextField && typeahead.HasActiveSearch;

        private static readonly PropertyInfo pi_shouldDoTypeInField = AccessTools.Property(typeof(Dialog_FileList), "ShouldDoTypeInField");
        private static readonly FieldInfo fi_typingName = AccessTools.Field(typeof(Dialog_FileList), "typingName");
        private static readonly FieldInfo fi_files = AccessTools.Field(typeof(Dialog_FileList), "files");
        private static readonly FieldInfo fi_interactButLabel = AccessTools.Field(typeof(Dialog_FileList), "interactButLabel");
        private static readonly MethodInfo mi_doFileInteraction = AccessTools.Method(typeof(Dialog_FileList), "DoFileInteraction");
        private static readonly MethodInfo mi_reloadFiles = AccessTools.Method(typeof(Dialog_FileList), "ReloadFiles");

        private static bool GetShouldDoTypeInField(Dialog_FileList d) => (bool)pi_shouldDoTypeInField.GetValue(d);
        private static string GetTypingName(Dialog_FileList d) => fi_typingName.GetValue(d) as string;
        private static void SetTypingName(Dialog_FileList d, string value) => fi_typingName.SetValue(d, value);
        private static List<SaveFileInfo> GetFiles(Dialog_FileList d) => fi_files.GetValue(d) as List<SaveFileInfo>;
        private static string GetInteractButLabel(Dialog_FileList d) => fi_interactButLabel.GetValue(d) as string;
        private static void DoFileInteraction(Dialog_FileList d, string fileName) => mi_doFileInteraction.Invoke(d, new object[] { fileName });
        private static void ReloadFiles(Dialog_FileList d) => mi_reloadFiles.Invoke(d, null);

        private static Dialog_FileList dialog;
        private static List<SaveFileInfo> files = new List<SaveFileInfo>();
        private static int selectedIndex;
        private static Focus focus;
        private static bool isSaveMode;
        private static TypeaheadSearchHelper typeahead = new TypeaheadSearchHelper();

        private static readonly TextInputController nameController = new TextInputController();
        private static readonly TextFieldSpec nameSpec = new TextFieldSpec(
            labelKey: "RimWorldAccess.TextInput.LabelFilename",
            maxLength: 64,
            minLength: 1,
            mustBeFilename: true);

        public static bool IsInTextField => IsActive && isSaveMode && focus == Focus.TextField;

        public static void EnsureOpen(Dialog_FileList d)
        {
            if (System.Object.ReferenceEquals(dialog, d))
                return;

            dialog = d;
            IsActive = true;
            isSaveMode = GetShouldDoTypeInField(d);
            selectedIndex = 0;
            typeahead.ClearSearch();
            RebuildFromDialog();

            if (isSaveMode)
            {
                focus = Focus.TextField;
                nameController.Begin(GetTypingName(d) ?? "", nameSpec, _ => { }, null, replaceOnType: true, modal: false);
            }
            else
            {
                focus = Focus.List;
                nameController.Cancel();
            }

            AnnounceOpening();
        }

        public static void Close()
        {
            IsActive = false;
            files.Clear();
            typeahead.ClearSearch();
            nameController.Cancel();
            // Keep `dialog` set so EnsureOpen's reference check detects the post-close
            // snapshot re-render, same as IdeoLoadState.
        }

        private static void RebuildFromDialog()
        {
            files = GetFiles(dialog) ?? new List<SaveFileInfo>();
            if (selectedIndex >= files.Count)
                selectedIndex = System.Math.Max(0, files.Count - 1);
        }

        private static List<string> Labels() => files.Select(f => Path.GetFileNameWithoutExtension(f.FileName)).ToList();

        #region Input

        public static bool HandleInput(Event ev)
        {
            if (ev.type != EventType.KeyDown) return false;

            KeyCode key = ev.keyCode;
            bool alt = KeyboardHelper.IsAltHeld;
            bool ctrl = ev.control;

            if (key == KeyCode.Escape && !alt && !ctrl)
            {
                if (!IsInTextField && typeahead.HasActiveSearch) { typeahead.ClearSearchAndAnnounce(); AnnounceCurrent(); return true; }
                dialog.Close(doCloseSound: false);
                return true;
            }

            if (key == KeyCode.DownArrow) { MoveDown(); return true; }
            if (key == KeyCode.UpArrow) { MoveUp(); return true; }

            if (IsInTextField)
            {
                if (key == KeyCode.Return || key == KeyCode.KeypadEnter) { ExecuteSave(); return true; }
                if (key == KeyCode.Backspace) { BackspaceInField(); return true; }
                char fc = ev.character;
                if (!alt && !ctrl && fc != '\0' && fc >= ' ')
                {
                    AppendChar(fc);
                    return true;
                }
                return true;
            }

            if (files.Count == 0)
                return true;

            if (key == KeyCode.Home)
            {
                if (typeahead.HasActiveSearch && !typeahead.HasNoMatches) selectedIndex = typeahead.GetFirstMatch();
                else { typeahead.ClearSearch(); selectedIndex = 0; }
                AnnounceCurrent();
                return true;
            }
            if (key == KeyCode.End)
            {
                if (typeahead.HasActiveSearch && !typeahead.HasNoMatches) selectedIndex = typeahead.GetLastMatch();
                else { typeahead.ClearSearch(); selectedIndex = files.Count - 1; }
                AnnounceCurrent();
                return true;
            }

            if (key == KeyCode.Return || key == KeyCode.KeypadEnter)
            {
                if (isSaveMode) ExecuteSave();
                else LoadSelected();
                return true;
            }

            if (key == KeyCode.Delete)
            {
                DeleteSelected();
                return true;
            }

            if (key == KeyCode.Backspace)
            {
                if (typeahead.HasActiveSearch && typeahead.ProcessBackspace(Labels(), out int ni))
                {
                    if (ni >= 0) selectedIndex = ni;
                    AnnounceCurrent();
                }
                return true;
            }

            char c = ev.character;
            if (!alt && !ctrl && c != '\0' && char.IsLetterOrDigit(c))
            {
                if (typeahead.ProcessCharacterInput(c, Labels(), out int ni))
                {
                    selectedIndex = ni;
                    AnnounceCurrent();
                }
                else
                {
                    SoundDefOf.ClickReject.PlayOneShotOnCamera();
                    typeahead.SpeakNoMatches();
                }
                return true;
            }

            return true;
        }

        /// <summary>Character input forwarded when the save-name field has focus.</summary>
        public static void AppendChar(char c)
        {
            if (!IsInTextField) return;
            nameController.HandleCharacter(c);
            SyncTypingNameToDialog();
        }

        public static void BackspaceInField()
        {
            if (!IsInTextField) return;
            nameController.HandleBackspace();
            SyncTypingNameToDialog();
        }

        /// <summary>Mirrors our controller's text into the dialog's own field so the vanilla
        /// visual rendering stays in sync for sighted co-op use.</summary>
        private static void SyncTypingNameToDialog()
        {
            if (dialog != null)
                SetTypingName(dialog, nameController.CurrentText);
        }

        private static void MoveDown()
        {
            if (IsInTextField)
            {
                if (files.Count == 0) { TolkHelper.Speak("RimWorldAccess.UI.Save.NoExistingSaves".Loc()); return; }
                focus = Focus.List;
                selectedIndex = 0;
                AnnounceCurrent();
                return;
            }
            if (files.Count == 0) return;
            if (typeahead.HasActiveSearch && !typeahead.HasNoMatches)
                selectedIndex = typeahead.GetNextMatch(selectedIndex);
            else
                selectedIndex = MenuHelper.SelectNext(selectedIndex, files.Count);
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            AnnounceCurrent();
        }

        private static void MoveUp()
        {
            if (IsInTextField)
                return;
            if (isSaveMode && selectedIndex == 0)
            {
                focus = Focus.TextField;
                AnnounceCurrent();
                return;
            }
            if (files.Count == 0) return;
            if (typeahead.HasActiveSearch && !typeahead.HasNoMatches)
                selectedIndex = typeahead.GetPreviousMatch(selectedIndex);
            else
                selectedIndex = MenuHelper.SelectPrevious(selectedIndex, files.Count);
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            AnnounceCurrent();
        }

        private static void LoadSelected()
        {
            if (selectedIndex < 0 || selectedIndex >= files.Count) return;
            string fileName = Path.GetFileNameWithoutExtension(files[selectedIndex].FileName);
            DoFileInteraction(dialog, fileName);
        }

        private static void ExecuteSave()
        {
            string saveName = IsInTextField
                ? nameController.CurrentText
                : (selectedIndex >= 0 && selectedIndex < files.Count ? Path.GetFileNameWithoutExtension(files[selectedIndex].FileName) : null);

            if (string.IsNullOrEmpty(saveName))
            {
                TolkHelper.Speak("RimWorldAccess.UI.Save.NeedName".Loc());
                return;
            }

            DoFileInteraction(dialog, saveName);
        }

        private static void DeleteSelected()
        {
            if (files.Count == 0 || selectedIndex < 0 || selectedIndex >= files.Count) return;

            var fileInfo = files[selectedIndex].FileInfo;
            var localDialog = dialog;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "ConfirmDelete".Translate(fileInfo.Name),
                delegate
                {
                    fileInfo.Delete();
                    ReloadFiles(localDialog);
                    RebuildFromDialog();
                    if (isSaveMode && files.Count == 0)
                        focus = Focus.TextField;
                    TolkHelper.SpeakData($"{fileInfo.Name}, {(string)"RimWorldAccess.Ideology.Builder.Status.Removed".Translate()}");
                    AnnounceCurrent();
                },
                destructive: true));
        }

        #endregion

        #region Announcements

        private static void AnnounceOpening()
        {
            var sb = new StringBuilder();
            sb.Append(GetInteractButLabel(dialog));
            sb.Append(". ").Append(files.Count);
            sb.Append(". ").Append(BuildCurrentText());
            TolkHelper.SpeakData(sb.ToString(), SpeechPriority.High);
        }

        private static void AnnounceCurrent()
        {
            string text = BuildCurrentText();
            if (!string.IsNullOrEmpty(text))
                TolkHelper.SpeakData(text);
        }

        private static string BuildCurrentText()
        {
            if (IsInTextField)
                return "RimWorldAccess.UI.Save.CreateNewRow".Loc(
                    nameController.CurrentText, "RimWorldAccess.UI.Save.CreateNewInstructions".Loc().ToString()).ToString();

            if (files.Count == 0) return "NoneLower".Translate();
            if (selectedIndex < 0 || selectedIndex >= files.Count) selectedIndex = 0;

            var file = files[selectedIndex];
            var sb = new StringBuilder();
            sb.Append(Path.GetFileNameWithoutExtension(file.FileName));
            string position = MenuHelper.FormatPosition(selectedIndex, files.Count);
            if (!string.IsNullOrEmpty(position))
                sb.Append(". ").Append(position);
            return sb.ToString();
        }

        #endregion
    }
}
