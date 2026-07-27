# Combat Module

## Purpose
Combat log viewing, targeting announcements, and spoken ability casts.

## Files
**Patches:** TargetingPatch.cs, ShellImpactPatch.cs
**States:** CombatLogState.cs
**Announcers:** AbilityCastAnnouncer.cs

## Ability casts (AbilityCastAnnouncer)
Speaks three moments of any ability cast by a player pawn: the warm-up starting, the cast landing,
and the warm-up being cut short. Patches vanilla's own funnel —
`Verb.TryStartCastOn` → `Stance_Warmup` → `Stance_Warmup.Expire` → `Verb.WarmupComplete` — so
vanilla, Royalty, Anomaly and Vanilla Expanded Framework abilities all work with no per-mod code
(VEF's `Verb_CastAbility` overrides `TryStartCastOn` but chains to the base). Weapons share that
funnel and are filtered out; an instant ability calls `WarmupComplete` inline, giving one "casts"
line and no "casting" one.

**Announced regardless of selection, always with the caster's name** — the point is queueing three
psycasts in a fight and then hearing which of them actually went off. That is the opposite of
`SelectedPawnActivityWatcher` (Pawns module), which follows the selection and reports idle work;
the two are separate features with separate settings on purpose.

Two traps, both found by driving a real cast in the live game and reading the spoken output:
- `Verb.TryStartCastOn` has **two overloads**. Matching it by name alone throws an ambiguous match,
  and one bad patch aborts `PatchAll`, silently disabling *every* patch in the mod. Spell out the
  argument types.
- On a successful cast the warm-up stance is replaced **before** `WarmupComplete` reports, so
  announcing an interruption the instant the stance drops says "stops casting" a moment before
  "casts". Interruptions resolve after a short grace, via a `GameComponent` heartbeat. A stun does
  not interrupt anything — `Stance_Warmup` just stops ticking and resumes later.

Setting: `AnnounceAbilityCasts` (RimWorld Access options → Announce Ability Casts), default on.

## Key Shortcuts
- **Alt+B** - Open combat log

## Architecture
CombatLogState provides scrollable combat history. TargetingPatch announces target acquisition.

## Dependencies
**Requires:** ScreenReader/, Input/, Pawns/ (pawn's combat log)

## Testing
- [ ] Combat log accessible
- [ ] Targeting announcements work
