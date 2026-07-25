#if DEBUG
using HarmonyLib;
using Verse;

namespace RimWorldAccess.DevBridge
{
    /// <summary>
    /// Drives the dev bridge from RimWorld's main-thread render loop. UIRootOnGUI runs every
    /// frame at both the main menu and in-game, so this both lazily starts the server and
    /// drains any eval work the HTTP thread has queued. Debug builds only.
    /// </summary>
    [HarmonyPatch(typeof(UIRoot), "UIRootOnGUI")]
    internal static class DevBridgeDrainPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            // Opt-in only. The dev bridge is a debug eval server for live inspection; it must NOT
            // run during normal play. Even in a DEBUG build it now stays completely dormant unless
            // the RWA_DEVBRIDGE=1 environment variable is set, because its per-frame main-thread
            // queue drain (and Roslyn eval execution) can stall the game hard. Set the env var
            // deliberately in the launch environment only when live diagnostics are needed.
            if (System.Environment.GetEnvironmentVariable("RWA_DEVBRIDGE") != "1")
                return;
            DevBridgeServer.EnsureStarted();
            MainThreadDispatcher.DrainPending();
        }
    }
}
#endif
