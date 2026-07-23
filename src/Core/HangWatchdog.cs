using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RimWorldAccess
{
    /// <summary>
    /// TEMPORARY diagnostic (2026-07-23) for the hard freeze under investigation.
    ///
    /// The freeze leaves no exception and no stack: Windows reports it as AppHangB1, which means the
    /// main thread stopped pumping messages while still alive. Nothing in Player.log names the code
    /// that stopped, because the code that stopped is the code that would have written the log line.
    ///
    /// So the main thread drops breadcrumbs (<see cref="Mark"/>) and a background thread — which the
    /// hang does not touch — notices when the newest breadcrumb stops aging and writes it to disk.
    /// Whatever the last breadcrumb says is where the main thread went in and never came out.
    ///
    /// The same instrumentation doubles as a stutter profiler: <see cref="End"/> reports any timed
    /// section that ran long, which is what the intermittent hitching during map navigation needs.
    ///
    /// Output: RimWorldAccess-hang.log next to Player.log. Never uses Verse.Log off the main thread
    /// (Verse.Log mutates UI-owned lists and is not thread safe); the watcher only touches the file.
    ///
    /// Delete this file and its call sites once the freeze is explained.
    /// </summary>
    public static class HangWatchdog
    {
        /// <summary>Master switch. Set false to make every entry point a no-op.</summary>
        public const bool Enabled = true;

        /// <summary>Main thread considered stalled once the newest breadcrumb is this old.</summary>
        private const int StallMs = 4000;

        /// <summary>A timed section slower than this is reported as a stutter.</summary>
        private const int SlowMs = 150;

        /// <summary>How often the watcher wakes to check the breadcrumb and flush queued lines.</summary>
        private const int PollMs = 250;

        /// <summary>While stalled, repeat the report this often so the log shows it never recovered.</summary>
        private const int RepeatMs = 5000;

        // Written by the main thread, read by the watcher. A torn read is harmless: the worst case is
        // one report naming the previous breadcrumb, and the repeat report corrects it.
        private static volatile string lastMark = "(not started)";
        private static long lastMarkStamp;

        private static readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();

        private static string logPath;
        private static Thread watcher;
        private static volatile bool running;

        /// <summary>
        /// Starts the watcher. Must be called from the main thread — it resolves the log path via
        /// RimWorld's save-data folder, which the background thread must not touch.
        /// </summary>
        /// <param name="saveDataFolder">Folder to write the diagnostic log into.</param>
        public static void Start(string saveDataFolder)
        {
            if (!Enabled || running)
                return;

            try
            {
                logPath = Path.Combine(saveDataFolder, "RimWorldAccess-hang.log");
                lastMarkStamp = clock.ElapsedMilliseconds;
                lastMark = "startup";
                running = true;

                Append($"=== session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                       $"(stall threshold {StallMs} ms, slow threshold {SlowMs} ms) ===");

                watcher = new Thread(Watch)
                {
                    Name = "RWA hang watchdog",
                    IsBackground = true
                };
                watcher.Start();
            }
            catch (Exception ex)
            {
                running = false;
                Verse.Log.Warning($"[RimWorld Access] Hang watchdog could not start: {ex.Message}");
            }
        }

        /// <summary>
        /// Drops a breadcrumb from the main thread. Deliberately trivial — a field write and a clock
        /// read — so it can sit on per-frame and per-keystroke paths without becoming the thing it is
        /// meant to measure.
        /// </summary>
        public static void Mark(string tag)
        {
            if (!Enabled || !running)
                return;

            lastMark = tag;
            lastMarkStamp = clock.ElapsedMilliseconds;
        }

        /// <summary>
        /// Opens a timed section. Pair with <see cref="End"/>. Returns the start stamp.
        /// </summary>
        public static long Begin(string tag)
        {
            if (!Enabled || !running)
                return 0;

            Mark(tag);
            return clock.ElapsedMilliseconds;
        }

        /// <summary>
        /// Closes a timed section opened by <see cref="Begin"/>, reporting it if it ran long. Safe to
        /// call from a finally block: it never throws.
        /// </summary>
        public static void End(string tag, long started)
        {
            if (!Enabled || !running || started == 0)
                return;

            long elapsed = clock.ElapsedMilliseconds - started;
            if (elapsed >= SlowMs)
                pending.Enqueue($"{Stamp()} SLOW {elapsed,6} ms  {tag}");

            Mark("(idle)");
        }

        /// <summary>
        /// Truncates and flattens text destined for a breadcrumb, so a whole announcement does not end
        /// up in the log and a newline cannot break the one-line-per-record format.
        /// </summary>
        public static string Snippet(string text, int max = 60)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var sb = new StringBuilder(Math.Min(text.Length, max) + 3);
            for (int i = 0; i < text.Length && sb.Length < max; i++)
            {
                char c = text[i];
                sb.Append(c == '\n' || c == '\r' || c == '\t' ? ' ' : c);
            }
            if (text.Length > max)
                sb.Append("...");
            return sb.ToString();
        }

        private static void Watch()
        {
            long reportedAt = -1;
            string reportedMark = null;

            while (running)
            {
                try
                {
                    Thread.Sleep(PollMs);

                    long now = clock.ElapsedMilliseconds;
                    long age = now - lastMarkStamp;
                    string mark = lastMark;

                    if (age >= StallMs)
                    {
                        bool firstReport = reportedMark == null;
                        if (firstReport || now - reportedAt >= RepeatMs)
                        {
                            pending.Enqueue(
                                $"{Stamp()} {(firstReport ? "STALLED" : "still stalled")} for {age} ms " +
                                $"— last breadcrumb: {mark}");
                            reportedAt = now;
                            reportedMark = mark;
                        }
                    }
                    else if (reportedMark != null)
                    {
                        pending.Enqueue($"{Stamp()} recovered (was stuck in: {reportedMark})");
                        reportedMark = null;
                    }

                    Flush();
                }
                catch
                {
                    // A diagnostic must never be the reason the game misbehaves. Keep watching.
                }
            }
        }

        private static void Flush()
        {
            if (pending.IsEmpty)
                return;

            var sb = new StringBuilder();
            while (pending.TryDequeue(out string line))
                sb.AppendLine(line);

            Append(sb.ToString(), addNewline: false);
        }

        private static void Append(string text, bool addNewline = true)
        {
            if (logPath == null)
                return;

            try
            {
                // Open/close per write so the file is complete on disk at all times — the interesting
                // case is a session that gets killed rather than closed, where a buffer would be lost.
                File.AppendAllText(logPath, addNewline ? text + Environment.NewLine : text);
            }
            catch
            {
                // Disk full, file locked, folder gone — nothing useful to do from here.
            }
        }

        private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");
    }
}
