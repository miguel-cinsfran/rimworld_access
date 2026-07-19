using System;
using System.Text;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Diagnostic tracing for the Colony Manager accessibility bring-up. Everything is
    /// prefixed with <c>[RWA-CM]</c> so it can be grepped straight out of Player.log, and
    /// gated on <see cref="Verbose"/> so it can be silenced once the feature is stable.
    ///
    /// The point of <see cref="DumpSnapshot"/> is to record the full state of the player's
    /// manager (every tab, every job, its target and status) the moment the window opens, so
    /// the exact situation being tested is understood from the log alone.
    /// </summary>
    public static class ColonyManagerDebug
    {
        /// <summary>Verbose lifecycle/navigation tracing. Off by default — flip to true only
        /// when diagnosing Colony Manager navigation; it logs on every move.</summary>
        public static bool Verbose = false;

        public static void Log(string msg)
        {
            if (Verbose)
            {
                Verse.Log.Message("[RWA-CM] " + msg);
            }
        }

        public static void Warn(string msg)
        {
            Verse.Log.Warning("[RWA-CM] " + msg);
        }

        public static void Error(string msg, Exception e = null)
        {
            Verse.Log.Error("[RWA-CM] " + msg + (e != null ? "\n" + e : ""));
        }

        /// <summary>
        /// Logs a complete snapshot of the manager's tabs and jobs. Best-effort: any single
        /// member read that throws is caught and noted, so a snapshot never breaks the game.
        /// </summary>
        public static void DumpSnapshot(object manager)
        {
            if (!Verbose)
            {
                return;
            }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[RWA-CM] ===== Colony Manager snapshot =====");
                sb.AppendLine($"  Reflection available: {ColonyManagerReflection.Available}");
                sb.AppendLine($"  Manager instance: {(manager != null ? manager.GetType().FullName : "null")}");

                if (manager == null)
                {
                    Verse.Log.Message(sb.ToString());
                    return;
                }

                var tabs = ColonyManagerReflection.GetVisibleTabs(manager);
                object currentTab = ColonyManagerReflection.GetCurrentTab();
                sb.AppendLine($"  Visible tabs: {tabs.Count}");
                sb.AppendLine($"  CurrentTab: {(currentTab != null ? ColonyManagerReflection.TabLabel(currentTab) : "null")}");

                for (int i = 0; i < tabs.Count; i++)
                {
                    var tab = tabs[i];
                    string label = Safe(() => ColonyManagerReflection.TabLabel(tab));
                    bool enabled = Safe(() => ColonyManagerReflection.TabEnabled(tab));
                    string disabledReason = Safe(() => ColonyManagerReflection.TabDisabledReason(tab));
                    var jobs = Safe(() => ColonyManagerReflection.GetJobsForTab(manager, tab));
                    int jobCount = jobs != null ? jobs.Count : -1;
                    bool isCurrent = ReferenceEquals(tab, currentTab);

                    sb.AppendLine($"  [{i}] tab '{label}' type={tab.GetType().Name} enabled={enabled}"
                        + (string.IsNullOrEmpty(disabledReason) ? "" : $" disabledReason='{disabledReason}'")
                        + $" jobs={jobCount}{(isCurrent ? " (current)" : "")}");

                    if (jobs != null)
                    {
                        for (int j = 0; j < jobs.Count; j++)
                        {
                            var job = jobs[j];
                            string jl = Safe(() => ColonyManagerReflection.JobLabel(job));
                            string jt = Safe(() => ColonyManagerReflection.JobTargetsLabel(job));
                            bool sus = Safe(() => ColonyManagerReflection.JobIsSuspended(job));
                            bool comp = Safe(() => ColonyManagerReflection.JobIsCompleted(job));
                            bool man = Safe(() => ColonyManagerReflection.JobIsManaged(job));
                            sb.AppendLine($"       - job[{j}] '{jl}' targets='{jt}' suspended={sus} completed={comp} managed={man} ({job.GetType().Name})");
                        }
                    }
                }

                sb.AppendLine("[RWA-CM] ===== end snapshot =====");
                Verse.Log.Message(sb.ToString());
            }
            catch (Exception e)
            {
                Error("DumpSnapshot failed", e);
            }
        }

        private static T Safe<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch
            {
                return default;
            }
        }
    }
}
