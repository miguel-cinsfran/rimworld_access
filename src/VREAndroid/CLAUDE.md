# VREAndroid Module

## Purpose
Screen-reader accessibility for the **optional** third-party mod **Vanilla Races Expanded -
Android** (packageId `vanillaracesexpanded.android`, C# namespace `VREAndroids`, source:
github.com/Vanilla-Expanded/VanillaRacesExpanded-Android). Covers its three custom windows:

- **Gene creation/modification** - `Window_AndroidCreation` (new android at an Android Creation
  Station), `Window_AndroidModification` (re-gene an existing android at a Behaviorist Station),
  `Window_CreateAndroidXenotype` (character-creation android xenotype editor). All three extend
  `Window_CreateAndroidBase`.
- **Awakening choices** - `Dialog_AndroidAwakenedChoices`, opened from `ChoiceLetter_AndroidAwakened`
  when an android's mood extreme triggers "awakening" (pick up to N passions + one trait).
- **Saved android-type picker** - `Dialog_AndroidProjectList_Load` / `_Save`.

This is a *soft* compatibility layer: RimWorld Access takes **no** assembly reference on the mod.
Everything mod-specific is resolved by name via `VREAndroidReflection`; when the mod isn't loaded
every accessor is inert and the patches never fire.

## Why this reuses so much of Biotech/
`Window_CreateAndroidBase` extends the **vanilla** `GeneCreationDialogBase` - the exact same base
class as vanilla `Dialog_CreateXenotype` (see `Biotech/XenotypeEditorState.cs`) - without
redeclaring any of its members. That means every public member (`Header`, `AcceptButtonLabel`,
`SelectedGenes`, `Accept()`, `CanAccept()`, `OnGenesChanged()`) is called **directly** through the
compile-time vanilla type, and every private field (`xenotypeName`, `xenotypeNameLocked`, `gcx`,
`met`, `arc`, `leftChosenGroups`) is reached via the **same** `AccessTools.Field(typeof
(GeneCreationDialogBase), ...)` reflection XenotypeEditorState already uses - no mod-specific
reflection needed for any of it. Only the mod's own additions need `VREAndroidReflection`:
`selectedGenes`/`requiredItems`/`disableAndroidHardwareLimitation` fields, the `GeneValidator`
override, `Utils.AndroidGenesGenesInOrder`, and the save/load project dialog constructors.

`Dialog_AndroidProjectList` (base of `_Load`/`_Save`) similarly extends vanilla `Dialog_FileList`
unchanged - `files`, `typingName`, `ShouldDoTypeInField`, `DoFileInteraction`, `ReloadFiles`,
`interactButLabel` are all public vanilla members, reached with **zero reflection** (see
`IdeoBuilder/IdeoLoadState.cs` for the established pattern this follows, extended here to also
cover the type-in-a-name Save case).

`ChoiceLetter_AndroidAwakened` extends vanilla `ChoiceLetter`; its `Choices` property throws
`NotImplementedException` (the mod only supports opening the letter directly, not the DiaOption
button list), the same shape as `ChoiceLetter_GrowthMoment` extending `LetterWithTimeout` - see
the special case added in `Quests/NotificationMenuState.ExtractLetterButtons`.

## Files
- **VREAndroidReflection.cs** - reflection facade. Resolves and caches every mod-specific
  type/member once; `Available` is false and every accessor inert when the mod is absent.
- **AndroidCreationState.cs** - tabbed treeview (Selected Genes / Gene Library / Controls),
  architecturally identical to `Biotech/XenotypeEditorState.cs`. Gene toggling respects hardware
  lock: mandatory android-core genes (`!CanBeRemovedFromAndroid()`) cannot be removed from
  Selected and are announced with a "mandatory" suffix; `Window_CreateAndroidXenotype` additionally
  disables the awakened-hardware lock (`disableAndroidHardwareLimitation`), matched via
  `VREAndroidReflection.CanToggleOffSelected`. Accept validation is **not** duplicated - `CanAccept()`
  is called directly (virtual dispatch reaches the mod's own override) and its `Messages.Message()`
  rejections are already spoken by the existing global `NotificationAccessibilityPatch`.
- **AndroidCreationPatch.cs** - lifecycle via `WindowStack.Add`/`Window.PostClose`/
  `OnCancelKeyPressed`/`OnAcceptKeyPressed`, gated on `VREAndroidReflection.IsAndroidCreationWindow`
  (mirrors `ColonyManager/ColonyManagerPatch.cs`'s bound-window pattern; `WindowStack.Add` is used
  for open detection rather than `PostOpen` because `Window_CreateAndroidBase.PostOpen` overrides
  the vanilla method, so a patch on the base `Window.PostOpen` would never fire for it).
- **AndroidProjectListState.cs** / **AndroidProjectListPatch.cs** - saved-project picker. Patches
  `Dialog_FileList.DoWindowContents` like `IdeoBuilder/IdeoLoadPatch.cs`, filtered to the mod's own
  subclasses. Save mode gets `WindowlessSaveMenuState`'s two-focus-zone design (text field, then
  list of existing files to overwrite; Down enters the list, Up from index 0 returns to the field).
- **AndroidAwakenedState.cs** / **AndroidAwakenedPatch.cs** - awakening passion/trait picker.
  Flattened into one navigable list (intro, passion items, trait items, confirm) rather than
  `GrowthMomentState`'s tabs, since there's no separate info-tab content beyond the opening letter
  paragraph. Escape = postpone ("Later"), blocked only when `letter.ShouldAutomaticallyOpenLetter`.
  `Alt+S` = confirm (validates passion count == `passionGainsCount`, trait chosen if offered).

## Integration
- **Opening (creation windows):** the mod's own gizmos ("Create android" / "Modify android" / the
  chargen "Xenotype editor"-equivalent) - no RWA-side entry point needed, `WindowStack.Add` catches
  every path.
- **Input routing:** `UnifiedKeyboardPatch` priority **-0.204** (AndroidCreationState) and
  **-0.203** (AndroidAwakenedState), next to the Biotech gene-dialog priorities (-0.21 Xenogerm,
  -0.205 XenotypeEditor). `AndroidProjectListState` is self-routed through its `DoWindowContents`
  patch (no UnifiedKeyboardPatch entry, matching Ideology's saved-file picker).
- **Notification menu:** `ChoiceLetter_AndroidAwakened` gets a special case in
  `NotificationMenuState.ExtractLetterButtons` (before the generic `ChoiceLetter` branch, which
  would otherwise crash calling its unimplemented `Choices` property) that opens the letter via the
  vanilla `Letter.OpenLetter()` virtual method directly.
- **Strings:** `Languages/*/Keyed/RimWorldAccess_VREAndroid.xml` (English + SpanishLatin) - only for
  wording RWA itself introduces (mandatory-gene suffix, power-efficiency biostat label, awakened
  dialog framing). Everything else reuses the mod's own translated keys (`VREA.*`) directly via
  `.Translate()`, so terminology and localization stay in sync with whatever the mod ships,
  including its own Spanish translation when active.

## DO NOT
- Do not add a hard reference to the mod's assembly - keep everything reflection-based.
- Do not duplicate the mod's own Accept/CanAccept validation messages - call `CanAccept()`
  directly and rely on the global `Messages.Message()` announcer.
