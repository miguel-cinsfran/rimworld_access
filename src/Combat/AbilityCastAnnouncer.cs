using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Speaks the life cycle of an ability cast — psycasts, royal-title abilities, Anomaly powers
    /// and any Vanilla Expanded Framework ability — for player pawns.
    ///
    /// Sighted players read a cast off the aim pie and the charge-up sound; in a firefight that
    /// sound is buried under gunfire, and nothing says whether the psycast actually went off or
    /// fizzled because the order was cancelled or the target dropped. Three moments are announced:
    /// the warm-up starting, the cast landing, and the warm-up being cut short.
    ///
    /// All three ride vanilla's own funnel, so no ability mod needs special-casing:
    ///   Verb.TryStartCastOn → Stance_Warmup → Stance_Warmup.Expire → Verb.WarmupComplete
    /// An instant ability (warmupTime 0) skips the stance and calls WarmupComplete inline, so it
    /// produces exactly one "casts" announcement and no "casting" one. VEF's own Verb_CastAbility
    /// overrides TryStartCastOn but chains to the base, and neither it nor vanilla's overrides
    /// WarmupComplete — verified against both assemblies — so patching the two base methods covers
    /// modded abilities too.
    /// </summary>
    public static class AbilityCastAnnouncer
    {
        private class PendingCast
        {
            public Verb Verb;
            public string Label;
            public int ExpiryTick;     // when we stop caring, so a despawned caster can't leak
            public int StanceLostTick; // 0 while the warm-up stance is still held
        }

        /// <summary>
        /// Ticks to wait after the warm-up stance disappears before calling a cast interrupted.
        /// A successful cast drops the stance and reports WarmupComplete in the same breath, and
        /// live testing showed the stance change arriving *first* — announcing on the spot said
        /// "stops casting" a moment before "casts". Anything still unresolved after this grace
        /// really was cut short.
        /// </summary>
        private const int InterruptGraceTicks = 5;

        private static readonly Dictionary<Pawn, PendingCast> pending = new Dictionary<Pawn, PendingCast>();

        private static System.Type vefAbilityVerbType;
        private static bool vefTypeResolved;

        private static bool Enabled => RimWorldAccessMod_Settings.Settings?.AnnounceAbilityCasts ?? true;

        // ===== Events =====

        /// <summary>A warm-up just began — the pawn is charging the ability but it has not fired.</summary>
        public static void Notify_WarmupStarted(Verb verb)
        {
            if (!Enabled || verb == null) return;
            Pawn pawn = verb.CasterPawn;
            if (!ShouldAnnounceFor(pawn) || !IsAbilityVerb(verb)) return;

            // WarmupStance is null for an instant ability: it already fired inside TryStartCastOn
            // and WarmupComplete announced it, so there is nothing to preview here.
            var stance = verb.WarmupStance;
            if (stance == null) return;

            string label = GetAbilityLabel(verb);
            if (string.IsNullOrEmpty(label)) return;

            Prune();
            pending[pawn] = new PendingCast
            {
                Verb = verb,
                Label = label,
                ExpiryTick = CurrentTick() + stance.ticksLeft + 600
            };
            TolkHelper.SpeakData(
                "RimWorldAccess.Combat.Cast.Started".Loc(pawn.LabelShortCap, label).ToString(),
                SpeechPriority.Low);
        }

        /// <summary>The ability actually went off (after its warm-up, or immediately).</summary>
        public static void Notify_CastCompleted(Verb verb)
        {
            if (!Enabled || verb == null) return;
            Pawn pawn = verb.CasterPawn;
            if (!ShouldAnnounceFor(pawn) || !IsAbilityVerb(verb)) return;

            string label = pending.TryGetValue(pawn, out var cast) && cast.Verb == verb
                ? cast.Label
                : GetAbilityLabel(verb);
            pending.Remove(pawn);
            if (string.IsNullOrEmpty(label)) return;

            TolkHelper.SpeakData(
                "RimWorldAccess.Combat.Cast.Completed".Loc(pawn.LabelShortCap, label).ToString());
        }

        /// <summary>
        /// The pawn left its warm-up stance. That happens both on success and on a genuine
        /// interruption, so only the clock starts here — <see cref="Tick"/> decides.
        /// </summary>
        public static void Notify_StanceReplaced(Pawn pawn, Stance oldStance)
        {
            if (!Enabled || pawn == null) return;
            if (!(oldStance is Stance_Warmup warmup)) return;
            if (!pending.TryGetValue(pawn, out var cast) || cast.Verb != warmup.verb) return;

            if (cast.StanceLostTick == 0) cast.StanceLostTick = CurrentTick();
        }

        /// <summary>
        /// Resolves warm-ups that ended without the ability firing — the order was cancelled, the
        /// target dropped or moved out of reach. (A stun does not qualify: Stance_Warmup simply
        /// stops ticking while stunned and resumes afterwards — checked live.) Silence there is
        /// the worst case: the player waits for a psycast that is never coming. Driven by
        /// <see cref="AbilityCastAnnouncerComponent"/>.
        /// </summary>
        public static void Tick()
        {
            if (pending.Count == 0) return;
            int now = CurrentTick();
            List<Pawn> resolved = null;

            foreach (var kv in pending)
            {
                var cast = kv.Value;
                bool lost = cast.StanceLostTick > 0 && now - cast.StanceLostTick >= InterruptGraceTicks;
                bool expired = cast.ExpiryTick < now;
                if (!lost && !expired) continue;

                (resolved ?? (resolved = new List<Pawn>())).Add(kv.Key);
                if (lost && Enabled && ShouldAnnounceFor(kv.Key))
                    TolkHelper.SpeakData(
                        "RimWorldAccess.Combat.Cast.Interrupted".Loc(kv.Key.LabelShortCap, cast.Label).ToString());
            }

            if (resolved == null) return;
            foreach (var pawn in resolved) pending.Remove(pawn);
        }

        // ===== Helpers =====

        private static bool ShouldAnnounceFor(Pawn pawn)
        {
            return pawn != null
                   && pawn.Faction != null
                   && pawn.Faction.IsPlayer
                   && pawn.Map == Find.CurrentMap;
        }

        /// <summary>
        /// True for ability verbs only. Weapons run through the same warm-up funnel, and
        /// announcing every shot would bury the map in speech.
        /// </summary>
        private static bool IsAbilityVerb(Verb verb)
        {
            if (verb is Verb_CastAbility || verb is IAbilityVerb) return true;

            // VEF's Verb_CastAbility extends Verse.Verb directly — a parallel hierarchy that no
            // vanilla type check matches. Resolved by name so VEF stays an optional dependency.
            if (!vefTypeResolved)
            {
                vefTypeResolved = true;
                vefAbilityVerbType = AccessTools.TypeByName("VEF.Abilities.Verb_CastAbility");
            }
            return vefAbilityVerbType != null && vefAbilityVerbType.IsInstanceOfType(verb);
        }

        /// <summary>The ability's own name — <c>ReportLabel</c> is overridden to it by both hierarchies.</summary>
        private static string GetAbilityLabel(Verb verb)
        {
            try
            {
                string label = verb.ReportLabel;
                if (!string.IsNullOrEmpty(label)) return label.CapitalizeFirst();
            }
            catch { /* fall through to the verb's own label */ }
            return verb.verbProps?.label?.CapitalizeFirst();
        }

        private static int CurrentTick() => Find.TickManager?.TicksGame ?? 0;

        /// <summary>Drops entries whose caster died or despawned mid-warm-up, so nothing leaks.</summary>
        private static void Prune()
        {
            if (pending.Count == 0) return;
            int now = CurrentTick();
            var stale = pending.Where(kv => kv.Key == null || !kv.Key.Spawned || kv.Value.ExpiryTick < now)
                               .Select(kv => kv.Key).ToList();
            foreach (var pawn in stale) pending.Remove(pawn);
        }

        /// <summary>Clears tracking between games so a stale pawn reference can't survive a reload.</summary>
        public static void Reset() => pending.Clear();
    }

    /// <summary>
    /// Drives <see cref="AbilityCastAnnouncer.Tick"/>. A cast that is cut short leaves no event of
    /// its own — the absence of WarmupComplete is the signal — so it needs a heartbeat to notice.
    /// </summary>
    public class AbilityCastAnnouncerComponent : GameComponent
    {
        public AbilityCastAnnouncerComponent(Game game)
        {
            AbilityCastAnnouncer.Reset();
        }

        public override void GameComponentTick()
        {
            AbilityCastAnnouncer.Tick();
        }
    }

    /// <summary>
    /// Verb has two TryStartCastOn overloads and the shorter one just forwards to this virtual
    /// six-argument version, so the argument types must be spelled out — a bare name match is
    /// ambiguous, and an ambiguous patch aborts Harmony's whole PatchAll, silently disabling
    /// every other accessibility patch in the mod.
    /// </summary>
    [HarmonyPatch(typeof(Verb), nameof(Verb.TryStartCastOn), new[]
    {
        typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(bool), typeof(bool), typeof(bool), typeof(bool)
    })]
    public static class Verb_TryStartCastOn_CastAnnouncePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Verb __instance, bool __result)
        {
            if (__result) AbilityCastAnnouncer.Notify_WarmupStarted(__instance);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.WarmupComplete))]
    public static class Verb_WarmupComplete_CastAnnouncePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Verb __instance)
        {
            AbilityCastAnnouncer.Notify_CastCompleted(__instance);
        }
    }

    /// <summary>
    /// Catches a warm-up ending without the ability firing. Stance_Warmup.Expire calls
    /// WarmupComplete *before* handing the pawn back to Stance_Mobile, so a completed cast has
    /// already cleared its tracking entry by the time this runs and stays silent here.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.SetStance))]
    public static class Pawn_StanceTracker_SetStance_CastAnnouncePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn_StanceTracker __instance)
        {
            AbilityCastAnnouncer.Notify_StanceReplaced(__instance.pawn, __instance.curStance);
        }
    }
}
