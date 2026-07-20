# VPEPsycasts Module

## Purpose
Keyboard/screen-reader accessibility for **Vanilla Psycasts Expanded (VPE)**'s psycast tab
(`VanillaPsycastsExpanded.UI.ITab_Pawn_Psycasts`, shown as "Psíquico" / "Psycasts"). Without this
module the tab fell through `TabRegistry` to the read-only `BasicInspectString` fallback, so a
screen-reader user could not spend psycast points at all.

This is a **soft third-party dependency**: RimWorld Access never hard-references the VPE or VEF
assemblies. Everything is resolved by reflection and no-ops when the mod is absent.

## Files
- **VPEPsycastsReflection.cs** — Reflection facade (`AccessTools`). Resolves the VPE/VEF types and
  members once, gates on `Available`, and exposes typed accessors that degrade to safe defaults.
- **VPEPsycastsState.cs** — Dedicated interactive overlay state (the `EntityTabState` pattern).

## Interaction model (from VPE's `ITab_Pawn_Psycasts.FillTab`)
A psycaster's `Hediff_PsycastAbilities` holds spendable `points`. Each point buys one of:
1. **Improve psycaster stats** — `SpentPoints(n)` + `ImproveStats(n)`.
2. **Unlock a meditation focus** — `SpentPoints(1)` + `UnlockMeditationFocus(def)` (gated on the
   vanilla `MeditationFocusDef.CanPawnUse` and VPE's `MeditationUtilities.CanUnlock`).
3. **Unlock a path** — `SpentPoints(1)` + `UnlockPath(def)` (gated on `PsycasterPathDef.CanPawnUnlock`).
4. **Learn an ability** inside an unlocked path — `SpentPoints(1)` + `CompAbilities.GiveAbility(def)`,
   gated on prerequisites (`AbilityExtension_Psycast.PrereqsCompleted` — the pawn must already have
   **any one** of the ability's prerequisites; abilities with no prereqs are always learnable).

Note the ability defs are `VEF.Abilities.AbilityDef` (a VEF subclass of `RimWorld.AbilityDef`), and
`CompAbilities` lives in `VEF.Abilities` — both reached only via reflection.

## Navigation (VPEPsycastsState)
A small hierarchy of flat, typeahead-searchable lists:

```
Root ─┬─ Status (level / points / experience — read-only)
      ├─ Psycast paths      → per-path list; Enter unlocks a locked path or drills into abilities
      │     └─ Abilities    → per-ability list; Enter learns an unlockable ability
      ├─ Meditation focus types → per-focus list; Enter unlocks an eligible focus
      └─ Improve psycaster stats (Enter spends one point)
```

Keys:
- **Up/Down/Home/End** navigate; typeahead searches by name.
- **Enter / Space** = the only keys that commit an action (unlock path/focus, learn ability, improve
  stats) or enter a submenu / drill into an unlocked path. Right-arrow deliberately does NOT commit —
  a learner accidentally spent a point on it during testing.
- **Right** = expand only (enter a submenu, or drill into an unlocked path's abilities); inert on
  anything that can't be expanded. Mirrors the inventory tree (Right expands, Enter is the action).
- **Left** = collapse / go back one level. **Escape** = back one level, and closes from Root.
- **Alt+I** = read the current item's description/details on demand (ability/path/focus description,
  or the current psycaster stat values on "Improve stats"). Keeps fast navigation terse.
- **Backspace** edits the active search.

Ability lists are **topologically ordered** (prerequisites always precede their dependents), and all
point-spends re-announce the remaining point count and rebuild the list so newly-eligible siblings
appear immediately.

## Integration points
- **TabRegistry.cs** — `ITab_Pawn_Psycasts` registered as `TabHandlerType.Action`; dispatch token
  `"Psycasts"` (display name comes free from the tab's `labelKey` "VPE.Psycasts").
- **InspectionTreeBuilder.ExecuteCategoryAction** — `category == "Psycasts"` → `VPEPsycastsState.Open(pawn)`.
- **BuildingInspectPatch.Prefix** — routes keys to `VPEPsycastsState.HandleInput` (VeryHigh prefix),
  same as `EntityTabState`.
- **KeyboardHelper.IsAnyAccessibilityMenuActive** — includes `VPEPsycastsState.IsActive`.
- **TypeaheadConsumerRegistry** — registered at 4.615 (below the inspection tree at 4.806 so its
  typeahead wins while open).

## Scope / follow-ups
Implemented: stat upgrades, foci, paths, and ability learning — everything that consumes points.
**Deferred:** psyset (ability loadout) management from the same tab — those are casting-time
loadouts for the psychic-status gizmo, only useful after abilities are learned, so a natural
second pass.

## Testing checklist
- [ ] "Psíquico" appears in a player psycaster's inspection tree; Enter opens the overlay.
- [ ] Root announces level + points; navigating lists announces status suffixes.
- [ ] Unlock a path with points → point count drops, drills into abilities.
- [ ] Learn a level-1 ability (no prereqs) → learned; sibling with that prereq becomes learnable.
- [ ] Ability with unmet prereqs announces the requirement and does not spend a point.
- [ ] Improve stats spends one point; rejects at 0 points.
- [ ] Escape/Left step back a level; Escape at Root returns to the inspection tree.
- [ ] With VPE uninstalled, nothing breaks (tab simply never appears).
