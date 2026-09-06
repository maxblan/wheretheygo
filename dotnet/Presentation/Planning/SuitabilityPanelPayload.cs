using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationSuitabilityOverlay
{
    // The delimited-string payloads the panel module reads (ui-module.md): `|` between
    // fields, newline between rows, and anything a player typed sanitised of both
    // before it gets here. The .mjs indexes by position, so every row grows at the
    // END and the old indices never move; the harness pins the field counts.
    internal static class SuitabilityPanelPayload
    {
        // The summary when there is nothing to list: no journeys yet, no corridor, or
        // journeys that no new line would improve.
        public static string EmptyRouteSummary(int tripCount, int assignedPairs)
        {
            if (tripCount < 0)
            {
                return "No corridor was strong enough to suggest a line.";
            }

            if (tripCount == 0)
            {
                return "No journeys found yet — load a city and let it run.";
            }

            return $"{(tripCount).ToString(CultureInfo.InvariantCulture)} journeys, {(assignedPairs).ToString(CultureInfo.InvariantCulture)} routed, but no new line would improve enough of what is still unserved.";
        }

        // One route per line as "mode|km|stops|vehicles|colour|reach|key|schedule|dayUtil|nightUtil",
        // for the panel to render as a colour-keyed list. A compact string avoids
        // hand-rolling a JSON writer for what is at most a dozen rows. The colour travels
        // with the row because the panel used to carry its own copy of the mode palette,
        // kept in step with the renderer's by a comment. Fields grow at the END.
        public static string RouteRows(List<SuggestedRoute> routes, float unservedTravelWeight)
        {
            var list = new StringBuilder();
            for (int i = 0; i < routes.Count; i++)
            {
                SuggestedRoute r = routes[i];
                if (i > 0)
                {
                    _ = list.Append('\n');
                }

                _ = list.Append(r.Mode);
                _ = list.Append('|');
                _ = list.Append((r.Length / 1000f).ToString("F1", CultureInfo.InvariantCulture));
                _ = list.Append('|');
                _ = list.Append(r.Stops.Count);
                _ = list.Append('|');
                _ = list.Append(r.Vehicles);
                _ = list.Append('|');
                _ = list.Append(TransitModes.ColorCssFor(r.Mode));
                _ = list.Append('|');
                // The figure the list is ordered by. Without it "best first" is a claim
                // the panel makes and cannot show, and there is no way to tell a clear
                // leader from three suggestions of much the same worth.
                float reach = unservedTravelWeight > 0f ? r.EnabledDemand / unservedTravelWeight : 0f;
                // A decimal below ten percent. The figure the list is RANKED by was
                // rounded to whole percent, so every suggestion in a well-served city
                // read "unlocks 0%" — including the one that unlocked the most.
                _ = list.Append((reach * 100f).ToString(reach >= 0.1f ? "F0" : "F1", CultureInfo.InvariantCulture));
                _ = list.Append('|');
                // Where the line runs between, as the row's identity. Mode, length,
                // stop count and vehicles are not one: two different suggestions can
                // agree on all four, and when they did the panel keyed two rows the
                // same and handed one row's hover to the other's line. This is the
                // identity SuggestionsChanged already compares by.
                _ = list.Append(RouteKeyOf(r));
                _ = list.Append('|');
                _ = list.Append(r.Schedule);
                _ = list.Append('|');
                _ = list.Append((r.DayUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture));
                _ = list.Append('|');
                _ = list.Append((r.NightUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture));
            }
            return list.ToString();
        }

        // The one-line summary the options page shows.
        public static string RouteSummary(List<SuggestedRoute> routes)
        {
            var builder = new StringBuilder();
            _ = builder.Append(routes.Count);
            _ = builder.Append(" suggested: ");
            for (int i = 0; i < routes.Count; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append("; ");
                }

                SuggestedRoute route = routes[i];
                _ = builder.Append('#');
                _ = builder.Append(i + 1);
                _ = builder.Append(' ');
                _ = builder.Append(route.Mode);
                _ = builder.Append(' ');
                _ = builder.Append((route.Length / 1000f).ToString("F1", CultureInfo.InvariantCulture));
                _ = builder.Append("km, ");
                _ = builder.Append(route.Stops.Count);
                _ = builder.Append(" stops, ");
                _ = builder.Append(route.Vehicles);
                _ = builder.Append(" veh");
            }

            return builder.ToString();
        }

        // Two routes are the same suggestion when they run between the same places.
        public static string RouteKeyOf(SuggestedRoute route)
        {
            if (route.Stops.Count < 2)
            {
                return "empty";
            }

            float2Like from = route.Stops[0];
            float2Like to = route.Stops[route.Stops.Count - 1];
            return $"{((int)from.x).ToString(CultureInfo.InvariantCulture)},{((int)from.y).ToString(CultureInfo.InvariantCulture)}>" +
                $"{((int)to.x).ToString(CultureInfo.InvariantCulture)},{((int)to.y).ToString(CultureInfo.InvariantCulture)}";
        }

        // One health row per existing line. The .mjs indexes fields by position, so a
        // new field is appended and the old indices never move (ui-module.md).
        public static string HealthRows(List<LineHealth> lines)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                LineHealth health = lines[i];
                if (i > 0)
                {
                    _ = builder.Append('\n');
                }

                _ = builder.Append(health.m_Id);
                _ = builder.Append('|');
                // The game's own name, so this list matches the Transportation
                // Overview rather than using an invented index.
                _ = builder.Append(string.IsNullOrEmpty(health.m_Name) ? health.m_Mode.ToString() : health.m_Name);
                _ = builder.Append('|');
                _ = builder.Append(health.m_Verdict);
                _ = builder.Append('|');
                // Token plus argument, never a finished sentence: the panel is the only
                // place that knows the player's language.
                _ = builder.Append(SuitabilityLineHealth.VerdictArgument(health));
                _ = builder.Append('|');
                _ = builder.Append((health.m_Usage * 100f).ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append(health.m_Vehicles);
                _ = builder.Append('|');
                _ = builder.Append(health.m_Stops);
                _ = builder.Append('|');
                // How much evidence the verdict rests on. The percentage above is a
                // mean over the rolling window once there are enough readings, and a
                // player has no way to tell that from a single instantaneous count
                // unless the panel says so.
                _ = builder.Append(health.m_WindowSamples);
                _ = builder.Append('|');
                _ = builder.Append(health.m_WindowGameHours.ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append((health.m_PeakUsage * 100f).ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append(health.m_Schedule);
                _ = builder.Append('|');
                _ = builder.Append(health.m_ScheduleAdvice);
                _ = builder.Append('|');
                _ = builder.Append((health.m_DayUsage * 100f).ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append((health.m_NightUsage * 100f).ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append(health.m_DaySamples);
                _ = builder.Append('|');
                _ = builder.Append(health.m_NightSamples);
                _ = builder.Append('|');
                // The plan (2026-09-06): demand utilisation at the recommended fleet ("-"
                // before the first route pass), the recommended mode and fleet, the span
                // the game allows (max 0 = unbounded), the planning load and the riders.
                _ = builder.Append(health.HasDemand ? (health.m_Utilisation * 100f).ToString("F0", CultureInfo.InvariantCulture) : "-");
                _ = builder.Append('|');
                _ = builder.Append(health.m_RecommendedMode);
                _ = builder.Append('|');
                _ = builder.Append(health.m_RecommendedFleet);
                _ = builder.Append('|');
                _ = builder.Append(health.m_FleetMin);
                _ = builder.Append('|');
                _ = builder.Append(health.m_FleetMax == int.MaxValue ? 0 : health.m_FleetMax);
                _ = builder.Append('|');
                _ = builder.Append(health.m_PlanningLoad);
                _ = builder.Append('|');
                _ = builder.Append(health.HasDemand ? health.m_RidersPerDay.ToString("F0", CultureInfo.InvariantCulture) : "-");
                _ = builder.Append('|');
                _ = builder.Append((health.m_DayUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append((health.m_NightUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }


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

        // "sharePercent|walkMinutes|floorPercent|giniWalk".
        public static string EquityRow(float share, int walkMinutes, int floorPercent, double giniWalk)
        {
            return
                $"{(share * 100f).ToString("F1", CultureInfo.InvariantCulture)}|" +
                $"{walkMinutes.ToString(CultureInfo.InvariantCulture)}|" +
                $"{floorPercent.ToString(CultureInfo.InvariantCulture)}|" +
                $"{giniWalk.ToString("F2", CultureInfo.InvariantCulture)}";
        }
    }
}
