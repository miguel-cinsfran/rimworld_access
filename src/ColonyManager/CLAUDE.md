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
Four nested levels; Escape/Left steps back one level (Escape at Tabs closes the window):

- **Tabs** — Up/Down/Home/End + typeahead; Enter/Right enters a tab.
- **Jobs** (a tab's job list) — Up/Down/Home/End + typeahead; Enter/Right opens the job detail;
  **Space** suspend/resume; **Ctrl+N** new job; **Delete** delete job; **Ctrl+Up/Down** reorder.
- **JobDetail** (per-job settings) — Up/Down/Home/End + typeahead between settings; **Left/Right**
  adjusts the current setting (threshold ±nice-step, **Ctrl+Left/Right** = ±1; toggle off/on; area
  cycles); **Enter/Space** activates (flips a toggle / opens a def-list).
- **DefList** (which animals/trees/plants/minerals) — Up/Down/Home/End + typeahead; **Space/Enter**
  toggles the selected def allowed/not.

## Implemented (Phases 1–3)
- Tabs + job-list navigation; read job target + status (suspended/target-met).
- Job actions: create (Ctrl+N via the tab's own `MakeNewJob`+`JobTracker.Add`), delete
  (`JobTracker.Delete`), reorder (`ManagerTab.Increase/DecreasePriority`), suspend/resume.
- Job detail via `ColonyManagerJobSchema` (per-job-type, **best-effort** — an unresolved member is
  silently dropped, never crashes): threshold target (all resource jobs) + per-tab **what** to
  hunt/chop/forage/mine (allowed-def sub-list), **where** (area of operation, cycles the map's
  areas), and option toggles (invert area, lock-to-map, unforbid corpses, allow saplings,
  fully-grown-only, allow mining, haul chunks, roof/room checks, …).
- Covered tabs: **Hunting, Forestry, Foraging, Mining** (the threshold-based resource jobs).

## Not yet (follow-up PRs)
- **Livestock** (per-animal target populations, training toggles, tame/butcher — a table, not a
  threshold), **Power** (no threshold), **Production** (recipe picker, maintain/consume modes).
- **Overview / Logs / Import-Export** tabs (mostly read-only tables + import/export flows).
- Polish worth considering for naturalness: **Alt+I** info card on a selected def/job (RWA has
  `InfoCardState`); **numeric direct entry** for the threshold target (type a number, RWA has a
  text-input pipeline); a **delete confirmation** via `WindowlessConfirmationState`; Hunting's
  meat-type toggles (allow humanlike/insect/twisted — skipped, side effects + DLC-gated).

## DO NOT
- Do not add a hard reference to Colony Manager's assembly — keep everything reflection-based.
- Do not edit files inside the mod's own Workshop/source folder (its `CLAUDE.md`, etc.). This
  module lives entirely in RimWorld Access.
