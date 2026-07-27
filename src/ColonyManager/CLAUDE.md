# ColonyManager Module

## Purpose
Screen-reader accessibility for the **optional** third-party mod **Colony Manager Redux**
(`ilyvion.colonymanagerredux`, source: github.com/ilyvion/colony-manager-redux). Makes its
manager window keyboard-navigable: browse the tab row, browse each tab's job list, read job
targets/status, select a job, and suspend/resume it.

This is a *soft* compatibility layer. RimWorld Access takes **no** assembly reference on
Colony Manager — everything is resolved by name via reflection. When the mod isn't loaded the
whole module is inert.

## Why not the Dialog_ModSettings widget-interception approach
The mod-settings accessibility (`src/MainMenu/ModSettings*`) shadows vanilla
`Widgets`/`Listing_Standard` primitives as a mod draws them. That does **not** work here:
Colony Manager draws its entire window with its own IMGUI framework (`ilyvion.Laboratory.UI` —
`IlyvionWidgets`, `GUIScope`, `Widgets_Section`, custom job rows, image-only tab icons, custom
threshold/area/animal-table controls). There is almost no vanilla widget to shadow.

Instead we navigate the mod's **own data model** and drive changes through its **own members**,
so the mod reacts exactly as it would to a mouse — the same high-level principle as the
mod-settings menu, just applied to a model instead of to widgets.

## Files
- **ColonyManagerReflection.cs** — reflection facade. Resolves and caches every Colony Manager
  type/member once (`AccessTools.TypeByName`/`Property`/`Method`); `Available` is false and every
  accessor a no-op when the mod is absent or its API shifted. Exposes the model:
  `Manager.For(map)` → `Manager.Tabs` (visible tabs) → per-tab jobs (`JobTracker.Jobs` filtered by
  `job.Tab`), plus tab labels/enabled/disabled-reason/`Selected`, job label/`TargetsLabel`/
  suspended/completed, `MainTabWindow_Manager.CurrentTab`/`GoTo(tab)`, and suspend toggling.
- **ColonyManagerState.cs** — windowless keyboard menu overlaid on the still-open window. Two
  levels: **Tabs** (Up/Down/Home/End + typeahead; Enter/Right enters a tab via the mod's own
  `GoTo`; Escape closes the window) and **Jobs** (Up/Down/Home/End + typeahead; Enter selects the
  job so the mod's detail panel follows; Space suspends/resumes; Left/Escape returns to Tabs).
- **ColonyManagerPatch.cs** — lifecycle. Patches the **vanilla `Window` base** methods the mod's
  window inherits (`PostOpen`/`PreClose`), gated on `ColonyManagerReflection.IsManagerWindow`, so
  no reference to the mod is needed and nothing fires when it's absent. `OnCancelKeyPressed`/
  `OnAcceptKeyPressed` prefixes block vanilla's Escape/Enter while our menu owns the window (the
  documented Window.OnCancelKeyPressed isolation pattern — see root CLAUDE.md).

## Integration
- **Opening:** the mod's bottom-bar "manager" button ships with **no** hotkey, and every low
  F-key is already a vanilla main-tab hotkey (F5 = Wildlife, etc.), so there is no free global
  key. Instead the window is opened from the **Extra Menus** list (**F12** → "Colony Manager"),
  the established RWA home for main-tab openers without a dedicated key (alongside Factions/Ideos/
  Entity Codex — see `ExtraMenusState.BuildMenuOptions`). The entry appears only when
  `ColonyManagerReflection.Available`. Closing is **Escape** at the tab level.
- **Input routing:** `UnifiedKeyboardPatch` priority **0.285** (ahead of map navigation and
  gameplay shortcuts, so the manager menu owns keys while its window is open). Also added to
  `KeyboardHelper.IsAnyAccessibilityMenuActive()` so global shortcuts don't leak while it's open.
- **Debugging:** `ColonyManagerDebug` traces lifecycle/navigation to Player.log under the
  `[RWA-CM]` prefix (grep for it); `DumpSnapshot` logs every tab+job the moment the window opens.
  `ColonyManagerDebug.Verbose` gates it (on during bring-up).
- **Typeahead:** registered in `TypeaheadConsumerRegistry` at 0.285.
- **Strings:** `Languages/*/Keyed/RimWorldAccess_ColonyManager.xml` (English + SpanishLatin). Tab
  names, job names and target labels come from the mod and are injected as placeholders; framing
  text is localized.

## Navigation model & keys
Five nested levels; Escape/Left steps back one level (Escape at Tabs closes the window):

- **Tabs** — Up/Down/Home/End + typeahead; Enter/Right enters a tab.
- **Jobs** (a tab's job list) — Up/Down/Home/End + typeahead; Enter/Right opens the job detail;
  **Space** suspend/resume; **Ctrl+N** new job; **Delete** delete job; **Ctrl+Up/Down** reorder.
- **JobDetail** (per-job settings) — Up/Down/Home/End + typeahead between settings; **Left/Right**
  adjusts the current setting (threshold ±nice-step, **Ctrl+Left/Right** = ±1; toggle off/on; area
  and mode cycle); **Enter/Space** activates (flips a toggle / opens a def-list or picker).
- **DefList** (which animals/trees/plants/minerals) — Up/Down/Home/End + typeahead; **Space/Enter**
  toggles the selected def allowed/not. Multi-select: every entry is independent.
- **Picker** (which animal to herd / what to produce) — Up/Down/Home/End + typeahead;
  **Enter/Space** confirms the one choice, Escape/Left cancels. Single-select, and confirming is
  what performs the action (creating the job, or repointing a production job at another recipe).

## Implemented
- Tabs + job-list navigation; read job target + status (suspended/target-met). Job-list rows read
  a **live "kept / current, met or short by N"** for threshold jobs and a **"N of M animals"**
  summary for Livestock (its own `TargetsLabel` is raw translation keys, unusable for speech).
- Job actions: create (Ctrl+N via the tab's own `MakeNewJob`+`JobTracker.Add`), delete
  (`JobTracker.Delete`), reorder (`ManagerTab.Increase/DecreasePriority`), suspend/resume.
- Job detail via `ColonyManagerJobSchema` (per-job-type, **best-effort** — an unresolved member is
  silently dropped, never crashes):
  - **Threshold** rows announce target + **current stock** (`Trigger_Threshold.GetCurrentCount`)
    and whether it's met (`DoesCountMeetTarget`), not the meaningless `MaxUpperThreshold`.
  - **Hunting/Forestry/Foraging/Mining:** what to hunt/chop/forage/mine (allowed-def sub-list),
    area of operation, and option toggles (invert area, lock-to-map, unforbid corpses, allow
    saplings, fully-grown-only, allow mining, haul chunks, roof/room checks, …).
  - **Livestock:** the four per-category population targets (adult/juvenile × female/male, from
    `Trigger_PawnKind.CountTargets`) as adjustable counts with live current populations, plus key
    toggles (tame-more, tame-past-targets, respect-bonds, cull trained/pregnant/bonded) and taming
    /training areas. **Writing a target must also update the tab's `_newCounts` string buffer** —
    `ManagerTab_Livestock.DoCountField` re-parses that buffer into `CountTargets` every frame it
    draws, so setting only `CountTargets` is clobbered on the next frame (see
    `ColonyManagerReflection.SyncLivestockCountBuffer`). Threshold sliders bind directly to
    `TargetCount`, so those need no such mirror.
  - **Production:** what the job makes (`Recipe`), production mode (maintain stock / consume
    surplus), threshold ("keep N"), workbench area + invert-area.
- Covered tabs: **Hunting, Forestry, Foraging, Mining, Livestock, Production** (create + edit), plus
  **Power** and any read-only job (announced as read-only status, never a silent dead-end).
- **Informational tabs** (Overview / Logs / Import & Export) announce as "information" and don't
  drop into an empty job list or offer job creation.
- **Job creation with a choice:** Livestock and Production jobs are meaningless without an animal
  or a product — and a pawn-kind-less Livestock job throws when the save is reloaded. Ctrl+N on
  those tabs opens the **Picker** level over the tab's own available list
  (`ManagerTab_Livestock._availablePawnKinds` / `ManagerTab_Production._availableRecipes`, both
  repopulated by the tab's `Refresh()`), and only creates the job once a choice is confirmed.
  Livestock passes the pawn kind straight to `MakeNewJob`; Production makes an argument-less job
  and then assigns `Recipe`, whose setter is what aims the job's threshold at that recipe's
  product. If the recipe can't be assigned the job is discarded rather than added broken.
- **Changing a production job's product** uses the mod's own `ComputeRecipeSwapCandidates` — the
  equivalents it considers valid (same product, different bench). Producing something else entirely
  is a different job, created from the Jobs level.

## Not yet (follow-up)
- **Overview / Logs / Import-Export** content (summary tables, history, import/export flows).
- **Power** has essentially nothing to tune.
- Polish worth considering: **Alt+I** info card on a selected def/job (RWA has `InfoCardState`);
  **numeric direct entry** for a threshold/target (RWA has a text-input pipeline); a **delete
  confirmation** via `WindowlessConfirmationState`; Hunting's meat-type toggles.

## Gotchas found the hard way
- `Trigger_Threshold.DoesCountMeetTarget(int count)` takes the **amount to test**. Passing the
  target into it compares the target with itself and is always true — every job then claims its
  target is met. Feed it `GetCurrentCount(false)`.
- A value edited by reflection sticks only if the mod's per-frame draw doesn't re-derive it from a
  UI buffer. `ManagerTab_Livestock.DoCountField` re-parses `_newCounts` into `CountTargets` every
  frame, so a population target must be written to both (`SyncLivestockCountBuffer`). Verify
  persistence live after a few seconds — don't trust the immediate read-back.

## DO NOT
- Do not add a hard reference to Colony Manager's assembly — keep everything reflection-based.
- Do not edit files inside the mod's own Workshop/source folder (its `CLAUDE.md`, etc.). This
  module lives entirely in RimWorld Access.
