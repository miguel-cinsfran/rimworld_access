# AutoCombat Module

## Purpose
Screen-reader accessibility for the **optional** third-party mod **Drafted Auto-Combat**
(`RedMattis.AutoCombat`, assembly `AutoCombat`, namespace `BigAndSmall`). Adds **Alt+T**: a
windowless menu that sets the mod's four per-pawn combat switches across the **whole selection**
at once.

Like every optional-mod layer in RimWorld Access this takes **no** assembly reference —
everything resolves by name through `AccessTools` and the feature is inert when the mod is
absent.

## What was already fine, and what wasn't
The mod's gizmos are `BigAndSmall.Command_ToggleWithRClick`, which **does** derive from vanilla
`Verse.Command_Toggle`. RimWorld Access therefore already read and toggled them one pawn at a
time through the G gizmo menu — no parallel-type-hierarchy trap here (unlike VEF abilities).

Three things were missing, all confirmed live before writing any code:

1. **No batch.** The mod does have a batch path — `DraftGizmos.ShowHuntContextMenu` opens a
   float menu whose options each loop over `Find.Selector.SelectedPawns` — but it is reachable
   only by **right-clicking** the Auto-Combat gizmo, and its labels describe just the pawn whose
   gizmo was clicked, which misreports a mixed selection.
2. **"Long charge" announced as merely "disabled".** `AddChargeGizmo(pawn, data.fullAIControl)`
   sets `Disabled = fullAIControl` and never sets `disabledReason` (verified null on every
   colonist), so there was nothing to read out. The reason is simply *AI control is on*.
3. **"Auto-use all abilities" is a silent no-op for many pawns.** `ToggleAutoForAll` walks
   `Pawn.abilities.AllAbilitiesForReading` and keeps a def only if `def.aiCanUse` or the def
   carries `BigAndSmall.AutoCombatExtension` with `canMassToggle`. Two consequences: abilities
   living in another tracker are invisible to it — **Vanilla Psycasts Expanded keeps psycasts on
   a VEF comp, so a pure psycaster can never be auto-cast by this mod** — and even vanilla
   abilities are skipped unless `aiCanUse`. The gizmo isn't marked disabled, so pressing it just
   appeared to do nothing.

## Files
- **AutoCombatReflection.cs** — name-resolved facade. `Available`, `GetFlag`/`SetFlag` over the
  `Flag` enum (Hunt / TakeCover / MeleeCharge / FullAIControl / AutoUseAll), `GetTargetPawns`,
  and `CompatibleAbilityCount` which mirrors the mod's own auto-cast eligibility test.
  Writes go through the mod's `Toggle*` methods, never the backing fields, because those methods
  also call the mod's `RefreshDraft()`.
- **AutoCombatState.cs** — the windowless menu. Five rows; Up/Down/Home/End + typeahead;
  **Left** sets off for everyone, **Right** sets on, **Enter/Space** flips (bringing a mixed
  selection up rather than flipping each pawn independently), Escape closes.

## Mixed selections
Selections are usually mixed, so this is the module's main design concern:
- Rows report **"on for N of M"**, not a single on/off.
- Left/Right set an explicit value for everyone; a per-pawn flip would only deepen a mixed state.
- Rows carry a spoken caveat when a setting can't take effect: how many have Auto-Combat off
  (the value is still stored and applies once they enable it), how many are blocked by AI
  control, and how many own no compatible ability.
- Applying distinguishes **three** outcomes — changed, already matching, and *refused*. Reporting
  a refusal as "already set" tells the user the opposite of the truth.

## Integration
- **Opening:** `UnifiedKeyboardPatch` priority 6.515, **Alt+T**, gated on
  `!KeyboardHelper.IsAnyAccessibilityMenuActive()`. Alt+T is the right home because these are
  selection-scoped pawn settings, matching the Alt+H / Alt+N / Alt+B family — unlike `]`, which
  is cursor-scoped (`FloatMenuMakerMap.GetOptions` at the cursor tile) and lists one-shot orders.
  The plain-T time announcement now excludes Alt so a fall-through can't also read the clock.
- **Input routing:** priority **0.287**, ahead of map navigation.
- Registered in `KeyboardHelper.IsAnyAccessibilityMenuActive` and `TypeaheadConsumerRegistry`
  (0.287).
- **Strings:** `Languages/{English,SpanishLatin}/Keyed/RimWorldAccess_AutoCombat.xml`.

## DO NOT
- Do not add a hard assembly reference to the mod.
- Do not write the `DraftedActionData` fields directly — use the mod's `Toggle*` methods.
