using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Answers "is the RimWorld window the one the player is actually looking at?", for the
    /// "speak only while the game window is focused" setting.
    ///
    /// It does <b>not</b> use Unity's <c>Application.isFocused</c>: that stays true here even with
    /// another application in the foreground and even while RimWorld is minimised (verified live —
    /// the flag never flipped), so gating speech on it would have silently done nothing. The
    /// foreground window is asked of the OS instead.
    ///
    /// Windows-only by design, and it fails <em>open</em>: on macOS and Linux (and if the handle
    /// can't be resolved) it reports "focused", so speech is never lost to a check that cannot be
    /// answered — the setting simply has no effect there.
    /// </summary>
    public static class WindowFocusWatcher
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        // The foreground window changes at human speed; polling it every frame is pointless.
        private const int PollIntervalFrames = 10;

        private static uint ownProcessId;
        private static int frameCounter;
        private static bool lastKnown = true;
        private static bool unavailable;

        /// <summary>
        /// Refreshes the cached focus state. Called once per GUI pass; cheap, and throttled so the
        /// P/Invoke runs a few times a second rather than every frame.
        /// </summary>
        public static bool Poll()
        {
            if (unavailable || !NativeLibraryLoader.IsWindows) return true;
            if (frameCounter++ % PollIntervalFrames != 0) return lastKnown;

            try
            {
                if (ownProcessId == 0) ownProcessId = (uint)Process.GetCurrentProcess().Id;

                // Compare the foreground window's owning process, not a window handle: Unity's
                // Process.MainWindowHandle does not reliably point at the game's real window, so
                // handle comparison reported "not focused" even with the game in front (measured).
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return lastKnown;

                GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
                lastKnown = foregroundProcessId == ownProcessId;
            }
            catch (Exception ex)
            {
                // A blocked P/Invoke must never cost the player their speech: give up on the check
                // for this session and stay audible.
                unavailable = true;
                lastKnown = true;
                Log.Warning($"[RimWorld Access] Window focus check unavailable, speech will not be gated: {ex.Message}");
            }

            return lastKnown;
        }
    }
}
