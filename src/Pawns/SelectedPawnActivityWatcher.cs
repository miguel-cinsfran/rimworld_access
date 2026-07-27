using RimWorld;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Keeps saying what the <em>selected</em> pawn is doing as it changes, so a colonist does not
    /// go silent between one selection announcement and the next.
    ///
    /// Deliberately separate from <see cref="AbilityCastAnnouncer"/>, which is about deliberate
    /// orders and speaks for every player pawn on the map whether selected or not: in a fight you
    /// queue three psycasts and then need to hear what became of each. Idle chatter is the
    /// opposite — "Mila is hauling wood" only matters for the pawn you are watching, so this one
    /// follows the selection and stays quiet about everybody else.
    ///
    /// How much it says is the player's call (<see cref="ActivityUpdateVerbosity"/>), because the
    /// useful amount differs: some want only "she switched to cooking", others want the game's own
    /// "hauling wood to the stockpile", and some also want to know where she is doing it.
    /// </summary>
    public static class SelectedPawnActivityWatcher
    {
        /// <summary>How often the selected pawn is re-checked (ticks). 30 ≈ half a second at 1x.</summary>
        private const int CheckIntervalTicks = 30;

        /// <summary>Floor between two spoken updates, so a churning job can't machine-gun.</summary>
        private const int MinTicksBetweenAnnouncements = 90;

        private static Pawn watched;
        private static JobDef lastJobDef;
        private static string lastReport;
        private static string lastLocation;
        private static int lastAnnouncedTick;
        private static int nextCheckTick;

        private static ActivityUpdateVerbosity Verbosity =>
            RimWorldAccessMod_Settings.Settings?.SelectedPawnActivityUpdates ?? ActivityUpdateVerbosity.Minimal;

        public static void Tick()
        {
            if (Verbosity == ActivityUpdateVerbosity.Off) { watched = null; return; }

            int now = Find.TickManager?.TicksGame ?? 0;
            if (now < nextCheckTick) return;
            nextCheckTick = now + CheckIntervalTicks;

            Pawn pawn = SingleSelectedPawn();
            if (pawn == null) { Forget(); return; }

            // A fresh selection was just announced in full by whatever selected it; take that as
            // the baseline instead of repeating it.
            if (pawn != watched) { Remember(pawn, now); return; }

            string report = ReportFor(pawn);
            string location = Verbosity == ActivityUpdateVerbosity.Full ? LocationOf(pawn) : null;
            JobDef jobDef = pawn.CurJob?.def;

            bool changed;
            switch (Verbosity)
            {
                case ActivityUpdateVerbosity.Minimal:
                    // Only a genuinely different activity — hauling one stack after another is
                    // the same job and stays quiet.
                    changed = jobDef != lastJobDef;
                    break;
                case ActivityUpdateVerbosity.Medium:
                    changed = report != lastReport;
                    break;
                default: // Full — also follows the pawn from room to room.
                    changed = report != lastReport || location != lastLocation;
                    break;
            }

            if (!changed) return;

            // Never talk over a menu the player is reading. This one drops the update for good —
            // it adopts the new state as the baseline — so closing a menu never unleashes a
            // backlog of what the pawn did meanwhile.
            if (KeyboardHelper.IsAnyAccessibilityMenuActive() || TextInputManager.IsActive)
            {
                Commit(jobDef, report, location);
                return;
            }

            // Too soon after the last update: leave the baseline alone so this change is still
            // pending and gets spoken at the next check, rather than being swallowed.
            if (now - lastAnnouncedTick < MinTicksBetweenAnnouncements) return;

            Commit(jobDef, report, location);
            if (string.IsNullOrEmpty(report)) return;

            lastAnnouncedTick = now;
            // The game's job reports sometimes end in a period ("load steel into inventory."), and
            // the location clause is appended with a comma — trim it or it reads "inventory., in workshop".
            string text = string.IsNullOrEmpty(location)
                ? "RimWorldAccess.Pawns.Activity.Update".Loc(report).ToString()
                : "RimWorldAccess.Pawns.Activity.UpdateWithLocation".Loc(report.TrimEnd('.', ' '), location).ToString();

            // Low priority: an idle-work update must never cut off something the player asked for.
            TolkHelper.SpeakData(text, SpeechPriority.Low);
        }

        /// <summary>
        /// The activity text for the current verbosity: the terse setting keeps only the report's
        /// headline, dropping trailing detail lines such as meditation's psyfocus gain rate.
        /// </summary>
        private static string ReportFor(Pawn pawn)
        {
            return PawnHelper.GetPawnActivity(pawn, firstLineOnly: Verbosity == ActivityUpdateVerbosity.Minimal);
        }

        private static Pawn SingleSelectedPawn()
        {
            // Only a lone selection. With several pawns selected there is no single "what is he
            // doing now" to report, and five overlapping updates would be noise.
            var selector = Find.Selector;
            if (selector == null || selector.NumSelected != 1) return null;
            var pawn = selector.SingleSelectedThing as Pawn;
            return pawn != null && pawn.Spawned && pawn.Map == Find.CurrentMap ? pawn : null;
        }

        private static string LocationOf(Pawn pawn)
        {
            try { return TileInfoHelper.GetLocationContextPlain(pawn.Position, pawn.Map); }
            catch { return null; }
        }

        private static void Commit(JobDef jobDef, string report, string location)
        {
            lastJobDef = jobDef;
            lastReport = report;
            lastLocation = location;
        }

        private static void Remember(Pawn pawn, int now)
        {
            watched = pawn;
            lastJobDef = pawn.CurJob?.def;
            lastReport = ReportFor(pawn);
            lastLocation = LocationOf(pawn);
            // Whatever selected the pawn just announced all of this, so nothing is owed; but the
            // throttle starts open, or a real change one second later would be swallowed.
            lastAnnouncedTick = now - MinTicksBetweenAnnouncements;
        }

        private static void Forget()
        {
            watched = null;
            lastJobDef = null;
            lastReport = null;
            lastLocation = null;
        }

        public static void Reset() => Forget();
    }

    /// <summary>Heartbeat for <see cref="SelectedPawnActivityWatcher"/>.</summary>
    public class SelectedPawnActivityComponent : GameComponent
    {
        public SelectedPawnActivityComponent(Game game)
        {
            SelectedPawnActivityWatcher.Reset();
        }

        public override void GameComponentTick()
        {
            SelectedPawnActivityWatcher.Tick();
        }
    }
}
