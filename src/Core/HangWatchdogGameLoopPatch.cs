using HarmonyLib;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// TEMPORARY diagnostic (2026-07-23), companion to <see cref="HangWatchdog"/>.
    ///
    /// The first breadcrumb pass only covered RimWorld Access's own OnGUI handler, which was enough
    /// to clear that handler — the live capture showed the main thread spinning a full core with the
    /// last breadcrumb reading "(idle)", i.e. the loop is somewhere the mod never runs. "Somewhere
    /// else" is too big to act on, so these marks split the game's own main loop into coarse stages:
    /// simulation ticking, per-map update, and UI.
    ///
    /// Whichever stage the stall report names is the stage the runaway loop lives in — which decides
    /// whether the next thing to look at is a ticking/AI/damage mod or a UI/drawing one.
    ///
    /// Postfixes deliberately re-mark the enclosing stage rather than "(idle)": leaving a stale inner
    /// mark behind would misattribute a hang that happens between stages.
    /// </summary>
    public static class HangWatchdogGameLoopPatch
    {
        [HarmonyPatch(typeof(Game), "UpdatePlay")]
        public static class GameUpdatePlayPatch
        {
            public static bool Prepare() => HangWatchdog.Enabled;

            [HarmonyPrefix]
            public static void Prefix() => HangWatchdog.Mark("game loop: starting play update");
        }

        [HarmonyPatch(typeof(TickManager), "DoSingleTick")]
        public static class DoSingleTickPatch
        {
            public static bool Prepare() => HangWatchdog.Enabled;

            [HarmonyPrefix]
            public static void Prefix() => HangWatchdog.Mark("game loop: simulation tick");

            [HarmonyPostfix]
            public static void Postfix() => HangWatchdog.Mark("game loop: between ticks");
        }

        [HarmonyPatch(typeof(Map), "MapUpdate")]
        public static class MapUpdatePatch
        {
            public static bool Prepare() => HangWatchdog.Enabled;

            [HarmonyPrefix]
            public static void Prefix() => HangWatchdog.Mark("game loop: map update (drawing)");

            [HarmonyPostfix]
            public static void Postfix() => HangWatchdog.Mark("game loop: after map update");
        }
    }
}
