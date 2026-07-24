using HarmonyLib;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Drops a single ungated debug line spammed by the third-party "Voice Acted Colonists"
    /// (VAC) mod. Its <c>VAC.HarmonyPatches.Thing_TakeDamage_Patch.Postfix</c> opens with an
    /// unconditional <c>Log.Message("TAKEDAMAGE start")</c> that fires on <em>every</em>
    /// TakeDamage in the game — every pawn, animal, fire tick, etc. Idle that is a few per
    /// second; in combat it is a flood. Each call writes to Player.log, so on a slow or failing
    /// disk it produces visible stutter, and it buries the genuine warnings/errors a screen
    /// reader user relies on us to surface. VAC exposes no setting for it (the line uses raw
    /// Log.Message, not VAC's own gated logger), so we filter that exact string here. Everything
    /// else — including VAC's legitimate logging — passes through untouched.
    ///
    /// Harmless if VAC is absent or later fixes the line: the prefix simply never matches.
    /// </summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Message), new[] { typeof(string) })]
    public static class VacLogSpamSuppressionPatch
    {
        private const string SuppressedLine = "TAKEDAMAGE start";

        [HarmonyPrefix]
        public static bool Prefix(string text)
        {
            // Returning false skips the original Log.Message, dropping only this one message.
            return text != SuppressedLine;
        }
    }
}
