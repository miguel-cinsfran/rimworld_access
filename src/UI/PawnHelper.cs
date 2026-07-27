using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Shared helper methods for pawn-related functionality.
    /// </summary>
    public static class PawnHelper
    {
        /// <summary>
        /// Gets the current activity/job for a pawn, as one speakable line.
        /// Returns null if no activity or job report fails.
        /// </summary>
        public static string GetPawnActivity(Pawn pawn)
        {
            return GetPawnActivity(pawn, firstLineOnly: false);
        }

        /// <summary>
        /// Gets the current activity/job for a pawn.
        /// <paramref name="firstLineOnly"/> keeps just the headline: some reports carry extra
        /// lines of detail (meditating adds its psyfocus gain rate), which is worth hearing at
        /// higher verbosity but is exactly the noise a terse setting is asking to avoid.
        /// </summary>
        public static string GetPawnActivity(Pawn pawn, bool firstLineOnly)
        {
            if (pawn?.CurJob == null) return null;
            try
            {
                string activity = pawn.CurJob.GetReport(pawn);
                return Flatten(activity, firstLineOnly);
            }
            catch
            {
                // Job report can sometimes fail, just return null
                return null;
            }
        }

        /// <summary>
        /// Turns a multi-line job report into a single spoken line. Screen readers read a raw
        /// newline as a hard break mid-sentence, so the lines are joined as sentences instead.
        /// </summary>
        private static string Flatten(string text, bool firstLineOnly)
        {
            if (string.IsNullOrEmpty(text)) return null;

            text = text.StripTags().Replace("\r\n", "\n").Trim();
            if (text.Length == 0) return null;

            int firstBreak = text.IndexOf('\n');
            if (firstBreak < 0) return text;

            if (firstLineOnly)
            {
                string head = text.Substring(0, firstBreak).Trim();
                return head.Length > 0 ? head : null;
            }

            var sb = new System.Text.StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                string part = line.Trim();
                if (part.Length == 0) continue;
                if (sb.Length > 0)
                {
                    char last = sb[sb.Length - 1];
                    if (last != '.' && last != '!' && last != '?' && last != ':') sb.Append('.');
                    sb.Append(' ');
                }
                sb.Append(part);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}
