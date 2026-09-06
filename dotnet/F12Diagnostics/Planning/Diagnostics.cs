using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationSuitabilityOverlay
{
    // A pass whose whole job is to disagree with the rest of the mod.
    //
    // The failure that matters here is not a crash — it is a plausible wrong number
    // reaching the map, and every number below has a range it cannot leave without
    // something upstream being broken. Each check states the invariant it is
    // testing, so a warning names the defect rather than reporting a symptom.
    // Anything this reports is a bug in the mod, not a property of the city.
    internal static class SuitabilitySanity
    {

        // Returns the number of complaints; each one is handed to `complain` as it is
        // found, so the caller decides where they go.
        public static int Check(List<LineHealth> health, List<SuggestedRoute> routes, float totalZoneWeight, float longestJourneyMetres, Action<string> complain)
        {
            int complaints = 0;

            void Complain(string message)
            {
                complaints++;
                complain(message);
            }

            for (int i = 0; i < health.Count; i++)
            {
                LineHealth entry = health[i];
                string line = $"line {entry.m_Name} ({entry.m_Mode})";

                // Usage is a share of fleet capacity. Over 1 means passengers were
                // counted against the wrong capacity, or a window mixed two fleets.
                if (entry.m_Usage < 0f || entry.m_Usage > 1.5f || float.IsNaN(entry.m_Usage))
                {
                    Complain($"{line} usage {(entry.m_Usage).ToString("F2", CultureInfo.InvariantCulture)} is outside 0..1.5 — passengers are being divided by the wrong capacity");
                }

                if (entry.m_PeakUsage + 1e-3f < entry.m_Usage)
                {
                    Complain($"{line} peak usage {(entry.m_PeakUsage).ToString("F2", CultureInfo.InvariantCulture)} is below its mean {(entry.m_Usage).ToString("F2", CultureInfo.InvariantCulture)} — the window is not accumulating in order");
                }

                if (entry.m_Capacity > 0 && entry.m_Vehicles == 0)
                {
                    Complain($"{line} reports capacity {(entry.m_Capacity).ToString(CultureInfo.InvariantCulture)} with no vehicles");
                }

                // The plan clamps into the span the game allows; a fleet outside it, or an
                // interval past an hour for a line that runs, means the span or the round
                // trip came from the wrong line.
                if (entry.m_RecommendedFleet < entry.m_FleetMin || entry.m_RecommendedFleet > entry.m_FleetMax)
                {
                    Complain($"{line} recommends {(entry.m_RecommendedFleet).ToString(CultureInfo.InvariantCulture)} vehicles outside the game's span [{(entry.m_FleetMin).ToString(CultureInfo.InvariantCulture)}, {(entry.m_FleetMax).ToString(CultureInfo.InvariantCulture)}]");
                }

                if (entry.m_RoundTripSeconds > 0f && (entry.m_HeadwaySeconds is > 3600f or < 0f))
                {
                    Complain($"{line} plans an interval of {(entry.m_HeadwaySeconds).ToString("F0", CultureInfo.InvariantCulture)}s — the round trip feeding it is not this line's");
                }

                if (entry.m_WindowGameHours > 24.5f)
                {
                    Complain($"{line} window spans {(entry.m_WindowGameHours).ToString("F1", CultureInfo.InvariantCulture)} game hours, past the 24 h it is supposed to hold — eviction is not running");
                }
            }

            for (int i = 0; i < routes.Count; i++)
            {
                SuggestedRoute route = routes[i];
                string label = $"route #{(i + 1).ToString(CultureInfo.InvariantCulture)} ({route.Mode})";

                // Enabled demand is a share of the city's journeys. It cannot exceed
                // the total weight those journeys carry.
                if (totalZoneWeight > 0f && route.EnabledDemand > totalZoneWeight * 1.01f)
                {
                    Complain($"{label} enabled demand {(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)} exceeds the city's total travel weight {(totalZoneWeight).ToString("F0", CultureInfo.InvariantCulture)} — journeys are being counted more than once");
                }

                // A short line cannot be the thing that unlocks a large share of a
                // city's travel. The bound above was far too loose to catch it: a
                // 580 m, 3-stop bus stub was credited with 6853 against a city total
                // of 31856 — 21% of every journey — and passed silently.
                //
                // "Short" is relative to the city, not a fixed 2 km. Against the fixed
                // figure this fired on a 1524 m bus in Valmare that ran the length of
                // both its villages: in a city whose longest journey is 2 km, a 1.5 km
                // line genuinely does serve most of the travel, and the check called
                // the mod's own correct answer a defect. Measured against the longest
                // journey the city actually makes, the 580 m stub above is still 6% of
                // its city and still caught.
                if (totalZoneWeight > 0f
                    && longestJourneyMetres > 0f
                    && route.Length < longestJourneyMetres * Assumptions.ShortLineShareOfCity
                    && route.EnabledDemand > totalZoneWeight * Assumptions.ImplausibleDemandShare)
                {
                    Complain($"{label} is only {(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m yet is credited with enabling {((route.EnabledDemand / totalZoneWeight) * 100f).ToString("F0", CultureInfo.InvariantCulture)}% of the city's travel — a line this short cannot carry that, so the zone-to-stop remap is attaching journeys it does not serve");
                }

                if (route.Stops.Count < Assumptions.MinStops)
                {
                    Complain($"{label} has {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops, under the {(Assumptions.MinStops).ToString(CultureInfo.InvariantCulture)} a line needs — the shape gate was not applied after stop placement");
                }

                if (route.Stops.Count < 2)
                {
                    Complain($"{label} has {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops — a line needs at least two");
                }

                if (route.Vehicles <= 0)
                {
                    Complain($"{label} would run with no vehicles");
                }

                // The stop spacing the mode asked for, against what it actually got.
                // Wildly off means the stops were placed for a different mode — which
                // is what happens when a mode change skips its restop.
                if (route.Stops.Count >= 2)
                {
                    float spacing = route.Length / (route.Stops.Count - 1);
                    float want = Assumptions.StopSpacingFor(route.Mode);
                    if (spacing > want * 2.5f || spacing < want * 0.4f)
                    {
                        Complain($"{label} averages {(spacing).ToString("F0", CultureInfo.InvariantCulture)}m between stops but {route.Mode} spacing is {(want).ToString("F0", CultureInfo.InvariantCulture)}m — the stops were probably placed for another mode");
                    }
                }
            }

            return complaints;
        }
    }

    // Where the suggestions ran between at the last refresh and at the last log, so
    // churn can be measured and an unchanged list is not re-printed.
    //
    // A player cannot act on advice that changes every thirty seconds, and the churn
    // was invisible in the log: each refresh looked reasonable on its own while the
    // list went 0, 0, 0, 1, 1, 2 routes with entirely different termini each time.
    internal sealed class SuggestionChurn
    {

        // Where last refresh's suggestions ran between, so churn can be measured.
        private readonly List<RouteEnds> m_PreviousRouteEnds = new List<RouteEnds>();
        // What was last logged, so an unchanged list is not re-printed every refresh.
        // Separate from m_PreviousRouteEnds, which measures churn between refreshes
        // and is reset by that measurement.
        private readonly List<RouteEnds> m_LoggedRouteEnds = new List<RouteEnds>();

        public int RememberedCount => m_PreviousRouteEnds.Count;

        // How many of `routes` run between (nearly) the same ends as one of the
        // suggestions remembered last refresh.
        public int CountHeld(List<SuggestedRoute> routes)
        {
            int held = 0;
            for (int i = 0; i < routes.Count; i++)
            {
                SuggestedRoute route = routes[i];
                if (route.Stops.Count < 2)
                {
                    continue;
                }

                float2Like from = route.Stops[0];
                float2Like to = route.Stops[route.Stops.Count - 1];
                for (int j = 0; j < m_PreviousRouteEnds.Count; j++)
                {
                    RouteEnds previous = m_PreviousRouteEnds[j];
                    // Same corridor if both ends land near where they were. The stops
                    // themselves shift a little between refreshes as scores move.
                    if (float2Like.DistanceSq(previous.m_From, from) <= Assumptions.ChurnSameEndsRadiusSq
                        && float2Like.DistanceSq(previous.m_To, to) <= Assumptions.ChurnSameEndsRadiusSq)
                    {
                        held++;
                        break;
                    }
                }
            }

            return held;
        }

        // Remembers where this refresh's suggestions run between, for the next count.
        public void Remember(List<SuggestedRoute> routes)
        {
            m_PreviousRouteEnds.Clear();
            for (int i = 0; i < routes.Count; i++)
            {
                SuggestedRoute route = routes[i];
                if (route.Stops.Count >= 2)
                {
                    m_PreviousRouteEnds.Add(new RouteEnds
                    {
                        m_From = route.Stops[0],
                        m_To = route.Stops[route.Stops.Count - 1],
                    });
                }
            }
        }

        // Whether the suggestion list differs from the one last logged, by mode and by
        // where each line runs between.
        public bool ChangedSinceLogged(List<SuggestedRoute> routes)
        {
            if (m_LoggedRouteEnds.Count != routes.Count)
            {
                Refresh();
                return true;
            }

            for (int i = 0; i < routes.Count; i++)
            {
                SuggestedRoute route = routes[i];
                RouteEnds logged = m_LoggedRouteEnds[i];
                if (route.Stops.Count < 2
                    || float2Like.DistanceSq(logged.m_From, route.Stops[0]) > Assumptions.ChurnSameEndsRadiusSq
                    || float2Like.DistanceSq(logged.m_To, route.Stops[route.Stops.Count - 1]) > Assumptions.ChurnSameEndsRadiusSq)
                {
                    Refresh();
                    return true;
                }
            }

            return false;

            void Refresh()
            {
                m_LoggedRouteEnds.Clear();
                for (int i = 0; i < routes.Count; i++)
                {
                    SuggestedRoute route = routes[i];
                    m_LoggedRouteEnds.Add(new RouteEnds
                    {
                        m_From = route.Stops.Count > 0 ? route.Stops[0] : float2Like.Zero,
                        m_To = route.Stops.Count > 0 ? route.Stops[route.Stops.Count - 1] : float2Like.Zero,
                    });
                }
            }
        }

        // The two ends of a suggested line, kept only to compare one refresh's list
        // against the last.
        private struct RouteEnds
        {
            public float2Like m_From;
            public float2Like m_To;
        }
    }
}
