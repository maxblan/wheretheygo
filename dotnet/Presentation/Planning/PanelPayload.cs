using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TransitArchitect
{
    // The delimited-string payloads the panel module reads (ui-module.md): `|` between
    // fields, newline between rows, and anything a player typed sanitised of both
    // before it gets here. The .mjs indexes by position, so every row grows at the
    // END and the old indices never move; the harness pins the field counts.
    internal static class PanelPayload
    {
        // "coveredHours|readings|windowHours|observedJourneys|observedHours" — how much
        // observed history the verdicts and the served-demand discount rest on. Shown
        // because a mean over twenty minutes and a mean over a full day are the same
        // number on screen and mean very different things.
        public static string DataCoverageRow(float coveredHours, int readings, float windowHours, int observedJourneys, float observedHours)
        {
            return
                $"{coveredHours.ToString("F1", CultureInfo.InvariantCulture)}|" +
                $"{readings.ToString(CultureInfo.InvariantCulture)}|" +
                $"{windowHours.ToString("F0", CultureInfo.InvariantCulture)}|" +
                $"{observedJourneys.ToString(CultureInfo.InvariantCulture)}|" +
                $"{observedHours.ToString("F1", CultureInfo.InvariantCulture)}";
        }

        // "sharePercent|walkMinutes|giniWalk".
        public static string EquityRow(float share, int walkMinutes, double giniWalk)
        {
            return
                $"{(share * 100f).ToString("F1", CultureInfo.InvariantCulture)}|" +
                $"{walkMinutes.ToString(CultureInfo.InvariantCulture)}|" +
                $"{giniWalk.ToString("F2", CultureInfo.InvariantCulture)}";
        }
    }
}
