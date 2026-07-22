# VEFFactions Module

## Purpose
Keyboard/screen-reader accessibility for the **Vanilla Expanded Framework**'s new-faction prompt —
the window that appears on load when a newly added mod has factions the world does not contain yet.
Because this lives in VEF itself (not in any one content mod), fixing it covers **every** Vanilla
Expanded mod that ships factions, not just the Races Expanded family that surfaced it.

This is a **soft third-party dependency**: RimWorld Access never hard-references VEF. Everything is
resolved by reflection through `VEFFactionsReflection` and no-ops when the framework is absent.

## The gap
`VEF.Factions.Dialog_NewFactionSpawning` is a modal `Window` (`forcePause`, `absorbInputAroundWindow`)
whose entire body is `Listing_Standard` labels plus three `ButtonText` calls — all mouse-only. It
announces nothing, so a screen-reader user could only press Escape. VEF's `closeOnCancel` maps Escape
to **"do nothing"**, which re-queues the faction, so the same prompt reappears on *every single load*
and the faction is never actually decided. (`OnAcceptKeyPressed` is overridden to mean "add", so Enter
did work — but silently, with no way to know it.)

## Files
- **VEFFactionsReflection.cs** — reflection facade. Resolves `Dialog_NewFactionSpawning`
  (`factionDef`, `forcedFactionData`, `failedToSpawn`; `SpawnWithBases`/`SpawnWithoutBases`/`Skip`/
  `Ignore`), `Dialog_NewFactionSpawningSettlements` (`settlementsToSpawn`/`distanceToSpawn` and their
  `*Recommended` counterparts, `Spawn`), and the optional `KCSG.CustomGenOption.canSpawnSettlements`.
  Separate `Available` / `SettlementsAvailable` gates.
- **NewFactionSpawningState.cs** — flat-list state for the main prompt.
- **NewFactionSettlementsState.cs** — flat-list state for the settlements follow-up.
- **NewFactionSpawningPatch.cs** — `Window.PostOpen`/`PostClose` lifecycle (the `FactionLandingPatch`
  pattern), so VEF's own types are never patched directly.

## Interaction model (read from VEF's `DoWindowContents`)
One faction at a time; `PostClose` advances an enumerator and reopens for the next, so the prompt
chains. Choices:

| Row | Behavior |
|-----|----------|
| Faction information | Read-only; Enter re-reads the full prompt |
| Add the faction *with settlements* | `SpawnWithBases()` → opens the settlements dialog. Offered only when `!factionDef.hidden` and the faction has no `CustomGenOption` or one that allows settlements — mirrors VEF's own condition |
| Add the faction | `SpawnWithoutBases()` — the variant shown in every other case |
| Do nothing | `Skip()` — asked again next load |
| Don't ask for this faction again | `Ignore()` — recorded in VEF's `NewFactionSpawningState` world component |

When `forcedFactionData.forcePlayerToAddFactionIfMissing` is set (and the spawn hasn't already
failed), VEF refuses the last two and blocks Escape. Those rows are announced as **unavailable**, and
activating one speaks the mod's own refusal message instead of acting — the same message VEF posts.

## Keys
- **Up/Down/Home/End** navigate; typeahead searches by label (registered at 0.225, above the psycast
  tab, since this modal absorbs everything while up).
- **Enter / Space** activate the focused row.
- **Escape** = "do nothing", *and says so* — the old silent behavior was exactly what left the user
  re-answering the same prompt forever.
- Settlements dialog: **Left/Right** adjust the focused slider (Shift = ×5 step), Enter on Spawn/Cancel.

Both states swallow unhandled keys: the dialog is modal and absorbs input anyway, and letting game
shortcuts through would act on a world the user cannot see.

## Integration points
- **UnifiedKeyboardPatch** — priority −0.225, just above the faction-landing dialog. The settlements
  state is checked *first* because it sits on top of the prompt.
- **KeyboardHelper.IsAnyAccessibilityMenuActive** — includes both states.
- **TypeaheadConsumerRegistry** — main prompt only, suppressed while the settlements dialog is up.

## Keyboard input isolation (the Spawn-did-nothing bug)
First live test: navigating both dialogs worked, but Enter on **Spawn** silently dropped back to the
faction prompt without spawning anything — no message, no exception, `failedToSpawn` still false.
Invoking `Spawn()` through the dev bridge worked perfectly, proving the reflection was fine: Enter
was never reaching our handler's action. This is the root CLAUDE.md **Keyboard Input Isolation**
case — RimWorld raises Accept/Cancel from `KeyBindingDef.KeyDownEvent`, which does **not** consult
`Event.current.Use()`, so consuming the event in our handler cannot stop it.

The two dialogs need *different* fixes, because they reach `OnAcceptKeyPressed` differently:

| Dialog | Accept handler | Fix |
|---|---|---|
| `Dialog_NewFactionSpawningSettlements` | vanilla `Window.OnAcceptKeyPressed`, which only closes when `closeOnAccept` | clear `closeOnAccept`/`closeOnCancel` in `Open()` (the `FactionLandingState` precedent) |
| `Dialog_NewFactionSpawning` | **overrides** it and adds the faction outright, ignoring `closeOnAccept` | flag-clearing is not enough — `NewFactionSpawningPatch.Dialog_OnAcceptKeyPressed_Patch` prefixes the override out while our state is active |

Without the second patch, Enter would add the faction no matter which row was selected — including
"Do nothing" and "Don't ask for this faction again". That patch resolves its target dynamically
(`Prepare()` + `TargetMethod()`), so it is skipped entirely when VEF is absent and the framework is
still never hard-referenced.

## Localization note
All faction/button/explanation text goes through the mod's own `VanillaFactionsExpanded.*` keys, so
it follows whatever VEF ships. **VEF ships English only** (`Languages/English` is its sole language
folder), so these strings read as English in every locale — the same thing a sighted player sees.
That is upstream behavior, not a RimWorld Access translation gap.

## Notes / gotchas
- `TolkHelper.Speak` takes a `Localized`; any string coming from VEF (faction text, refusal messages)
  must go through `SpeakData`.
- `MenuHelper.FormatPosition(index, total)` returns a *suffix* to append (and honours the
  AnnouncePosition setting) — there is no `FormatWithPosition`.
- Both `Open()` methods catch and, on failure, close the state **and** announce, so a failed open can
  never leave the user trapped in a mute input-absorbing modal (the VREAndroid lesson).

## Testing checklist
- [ ] Load a save with a newly added faction mod; the prompt reads itself aloud on open.
- [ ] Up/Down move through information + the three choices, with position announcements.
- [ ] "Don't ask for this faction again" stops the prompt returning on subsequent loads.
- [ ] "Add with settlements" opens the settlements dialog; Left/Right change both values audibly.
- [ ] Escape announces that the faction was skipped for now.
- [ ] A faction the mod forces announces "unavailable" on the refusal rows and cannot be escaped.
- [ ] With VEF absent, nothing breaks (states never activate).
