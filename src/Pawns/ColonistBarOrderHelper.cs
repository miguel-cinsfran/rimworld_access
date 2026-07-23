using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimWorldAccess
{
    /// <summary>
    /// Produces colonists in colonist-bar display order without making the game rebuild the bar.
    ///
    /// The obvious way to get this list is <c>Find.ColonistBar.GetColonistsInOrder()</c>, and that is
    /// what this mod used to call. It is a trap. That property routes through
    /// <c>ColonistBar.Entries</c>, which calls <c>CheckRecacheEntries</c>, which finishes by running
    /// <c>ColonistBarDrawLocsFinder.CalculateDrawLocs</c> — a layout search that shrinks a scale
    /// factor in a loop with no iteration cap. Once that search fails to converge it spins the main
    /// thread forever: no exception, no log line, just a frozen game that Windows reports as
    /// AppHangB1. A live capture with the hang watchdog caught it in the act, with the last
    /// breadcrumb sitting on the call into the colonist bar.
    ///
    /// Vanilla rarely hits this because <c>ColonistBarOnGUI</c> checks <c>Visible</c> and returns
    /// before ever touching <c>Entries</c> — so when the bar is not on screen the game simply never
    /// recomputes the layout. This mod's callers had no such guard: they asked for the order from a
    /// keystroke handler and forced the recache the game was deliberately avoiding.
    ///
    /// So the order is rebuilt here from the same inputs vanilla feeds its entries — the map's free
    /// colonists plus player-controllable subhumans, ordered by
    /// <see cref="PlayerPawnsDisplayOrderUtility"/> — which is the same sort that decides bar order
    /// and the same thing <c>ColonistBar.Reorder</c> writes to. Same list, none of the layout work.
    ///
    /// Deliberately not covered: the reordering paths in <c>ColonistBarState</c>, which read
    /// <c>Entries</c> because they genuinely operate on the bar's groups. They stay as they are.
    /// </summary>
    public static class ColonistBarOrderHelper
    {
        /// <summary>
        /// Colonists on <paramref name="map"/> in bar display order. Never null; empty when the map
        /// is null. Callers add their own filters (spawned, selectable, has skills, ...).
        /// </summary>
        public static List<Pawn> GetColonistsInBarOrder(Map map)
        {
            var colonists = new List<Pawn>();
            if (map?.mapPawns == null)
                return colonists;

            colonists.AddRange(map.mapPawns.FreeColonists);
            colonists.AddRange(map.mapPawns.ColonySubhumansControllable);

            // The same sort vanilla applies when it builds bar entries, so a player who has dragged
            // their colonists into a preferred order still gets that order here.
            PlayerPawnsDisplayOrderUtility.Sort(colonists);

            return colonists;
        }
    }
}
