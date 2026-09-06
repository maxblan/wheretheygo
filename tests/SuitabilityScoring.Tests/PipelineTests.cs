using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationSuitabilityOverlay.Tests
{
    // F3 steps 3–4, the panel payload contract and the log's disagreement pass —
    // logic that used to live in the ECS system and could not be run here.
    internal static partial class Program
    {
        private static SuggestedRoute MakeRoute(ModePreset mode, params float2Like[] stops)
        {
            var route = new SuggestedRoute { Mode = mode, Network = RouteNetwork.Road, Vehicles = 2 };
            route.Stops.AddRange(stops);
            route.Path.AddRange(stops);
            float length = 0f;
            for (int i = 1; i < stops.Length; i++)
            {
                length += float2Like.Distance(stops[i - 1], stops[i]);
            }

            route.Length = length;
            return route;
        }

        private static void ServedDemandMapsPairsAndDiscounts()
        {
            var worldMin = new float2Like(0f, 0f);
            var grid = new int2Like(4, 4);
            // Stops: two at zone centres joined by a line, one nearer zone 1's centre than
            // stop 0 is, and an equidistant pair either side of zone 10's centre.
            float[] xs = { 128f, 896f, 250f, 540f, 740f };
            float[] zs = { 128f, 128f, 20f, 896f, 896f };
            var served = new ServedDemand();
            served.MapZonesToStops(xs, zs, worldMin, grid);
            int[]? zoneStops = served.ZoneStops;
            AssertTrue(zoneStops is not null && zoneStops.Length == 16, "one entry per zone");
            if (zoneStops is null)
            {
                return;
            }

            AssertTrue(zoneStops[0] == 0 && zoneStops[3] == 1, "zone centres on a stop map to it");
            AssertTrue(zoneStops[1] == 2, "the nearer stop wins even when another stop is also in reach");
            AssertTrue(zoneStops[5] == 0, "a stop 362 m away is within the 432 m zone reach");
            AssertTrue(zoneStops[10] == 3, "an exact tie keeps the lower stop index");
            AssertTrue(zoneStops[8] == -1, "a zone with no stop within reach maps to none");

            var flows = new List<ZoneFlow>
            {
                new ZoneFlow { m_Origin = 0, m_Destination = 3, m_Weight = 10f },
                new ZoneFlow { m_Origin = 0, m_Destination = 5, m_Weight = 7f },
                new ZoneFlow { m_Origin = 0, m_Destination = 8, m_Weight = 3f },
                new ZoneFlow { m_Origin = 5, m_Destination = 3, m_Weight = 4f },
                new ZoneFlow { m_Origin = 10, m_Destination = 3, m_Weight = 2f },
            };
            served.BuildPairs(flows);
            AssertTrue(served.PairCount == 3, $"same-stop and unmapped pairs drop: {served.PairCount.ToString(CultureInfo.InvariantCulture)} pairs");

            var lines = new List<TransitLine>
            {
                new TransitLine { m_Stops = new[] { 0, 1 }, m_RideSeconds = new[] { 0f, 300f }, m_ExpectedWait = 60f, m_SpeedMetresPerSecond = 10f },
            };
            TransitNetwork network = SuitabilityTransit.Build(xs, zs, xs.Length, lines, Assumptions.TransferWalkRadius, Assumptions.DefaultBoardPenaltySeconds);
            var workspace = new DijkstraWorkspace(network.Graph.NodeCount);
            workspace.Run(network.Graph, 0, Assumptions.MaxJourneySeconds);
            AssertTrue(SuitabilityTransit.Inspect(network, workspace, 0, 1, -1, out int boardings, out _, out float ride) && boardings == 1, "the line carries stop 0 to stop 1");

            AssertTrue(served.TryDiscount(network, workspace, flows, out DiscountReport report), "mapped and paired, so the discount runs");
            AssertTrue(report.ServedPairs == 2 && report.PairCount == 3, $"two of three pairs are carried: {report.ServedPairs.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(report.CeilingSeconds == Assumptions.MaxJourneySeconds, "under twenty carried pairs the ceiling is the fixed hour");
            AssertTrue(report.WeightBefore == 14f, "the carried journeys weighed 14 before");
            float direct = ride + ServedDemand.WalkSeconds(0f) + ServedDemand.WalkSeconds(0f);
            float viaWalk = ride + ServedDemand.WalkSeconds((256f * 256f) + (256f * 256f)) + ServedDemand.WalkSeconds(0f);
            AssertTrue(flows[0].m_Weight == 10f * SuitabilityScoring.Saturate(direct / Assumptions.MaxJourneySeconds), "a carried journey keeps door-to-door / ceiling of its weight");
            AssertTrue(flows[3].m_Weight == 4f * SuitabilityScoring.Saturate(viaWalk / Assumptions.MaxJourneySeconds), "the walk from the zone centre to its stop is charged");
            AssertTrue(flows[1].m_Weight == 7f && flows[2].m_Weight == 3f && flows[4].m_Weight == 2f, "journeys the network cannot carry keep their weight");
            AssertTrue(report.WeightAfter == flows[0].m_Weight + flows[3].m_Weight, "the report sums what the carried journeys kept");
            AssertTrue(ServedDemand.RemainingWeight(flows) == flows[0].m_Weight + 7f + 3f + flows[3].m_Weight + 2f, "remaining weight is the sum over every flow");
            float longest = served.LongestJourneyMetres(flows);
            AssertTrue(longest == 768f, $"the longest journey is the 768 m between zone 0's and zone 3's centres, got {longest.ToString(CultureInfo.InvariantCulture)}");

            var empty = new ServedDemand();
            AssertTrue(!empty.TryDiscount(network, workspace, flows, out _), "without a zone map there is nothing to discount");
            AssertTrue(empty.LongestJourneyMetres(flows) == 0f, "and no longest journey either");
        }

        private static void ChurnCountsHeldEndsAndGatesTheLog()
        {
            var churn = new SuggestionChurn();
            var routes = new List<SuggestedRoute>
            {
                MakeRoute(ModePreset.Bus, new float2Like(0f, 0f), new float2Like(500f, 0f), new float2Like(1000f, 0f)),
                MakeRoute(ModePreset.Tram, new float2Like(0f, 2000f), new float2Like(600f, 2000f), new float2Like(1200f, 2000f)),
            };
            AssertTrue(churn.CountHeld(routes) == 0 && churn.RememberedCount == 0, "nothing remembered yet");
            churn.Remember(routes);
            AssertTrue(churn.RememberedCount == 2, "both remembered");
            var next = new List<SuggestedRoute>
            {
                MakeRoute(ModePreset.Bus, new float2Like(150f, 0f), new float2Like(500f, 0f), new float2Like(1100f, 100f)),
                MakeRoute(ModePreset.Tram, new float2Like(0f, 2000f), new float2Like(600f, 2000f), new float2Like(1500f, 2000f)),
                MakeRoute(ModePreset.Bus, new float2Like(5f, 5f)),
            };
            AssertTrue(churn.CountHeld(next) == 1, "ends within 200 m are the same corridor; a terminus 300 m off is not; a one-stop route never counts");

            AssertTrue(churn.ChangedSinceLogged(routes), "the first list is a change");
            AssertTrue(!churn.ChangedSinceLogged(routes), "the same list again is not");
            var moved = new List<SuggestedRoute> { next[0], next[1] };
            AssertTrue(churn.ChangedSinceLogged(moved), "a moved terminus is");
            AssertTrue(!churn.ChangedSinceLogged(moved), "and the moved list is then the logged one");
            AssertTrue(churn.ChangedSinceLogged(next) && churn.ChangedSinceLogged(next), "a route without two stops reads as a change every time");
            AssertTrue(churn.ChangedSinceLogged(new List<SuggestedRoute>()), "an emptied list is a change");
        }

        private static void SanityChecksNameEachDefect()
        {
            var complaints = new List<string>();
            var healthy = new LineHealth { m_Name = "1", m_Mode = ModePreset.Bus, m_Usage = 0.3f, m_PeakUsage = 0.5f, m_Capacity = 100, m_Vehicles = 2, m_RecommendedFleet = 2, m_FleetMin = 1, m_FleetMax = 10, m_RoundTripSeconds = 1200f, m_HeadwaySeconds = 600f, m_WindowGameHours = 12f };
            var route = MakeRoute(ModePreset.Bus, new float2Like(0f, 0f), new float2Like(400f, 0f), new float2Like(800f, 0f), new float2Like(1200f, 0f));
            route.EnabledDemand = 100f;
            int clear = SuitabilitySanity.Check(new List<LineHealth> { healthy }, new List<SuggestedRoute> { route }, 5000f, 8000f, complaints.Add);
            AssertTrue(clear == 0 && complaints.Count == 0, "a plausible line and route raise nothing");

            var broken = new LineHealth { m_Name = "2", m_Mode = ModePreset.Tram, m_Usage = 2f, m_PeakUsage = 1f, m_Capacity = 100, m_Vehicles = 0, m_RecommendedFleet = 0, m_FleetMin = 1, m_FleetMax = 5, m_RoundTripSeconds = 1000f, m_HeadwaySeconds = 4000f, m_WindowGameHours = 30f };
            int lineComplaints = SuitabilitySanity.Check(new List<LineHealth> { broken }, new List<SuggestedRoute>(), 0f, 0f, complaints.Add);
            AssertTrue(lineComplaints == 6 && complaints.Count == 6, $"usage, peak below mean, capacity without vehicles, fleet outside the span, interval and window each complain once: {lineComplaints.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(complaints[0].Contains("outside 0..1.5", StringComparison.Ordinal) && complaints[3].Contains("outside the game's span", StringComparison.Ordinal), "the messages name the invariant");

            complaints.Clear();
            var stub = MakeRoute(ModePreset.Bus, new float2Like(0f, 0f), new float2Like(100f, 0f));
            stub.EnabledDemand = 900f;
            stub.Vehicles = 0;
            int routeComplaints = SuitabilitySanity.Check(new List<LineHealth>(), new List<SuggestedRoute> { stub }, 1000f, 8000f, complaints.Add);
            // Fewer than three stops, a 100 m stub credited with 90 % of the city, no
            // vehicles, and a stop spacing under 40 % of the bus's.
            AssertTrue(routeComplaints == 4, $"a two-stop stub raises four complaints, got {routeComplaints.ToString(CultureInfo.InvariantCulture)}: {string.Join(" / ", complaints)}");

            complaints.Clear();
            var greedy = MakeRoute(ModePreset.Bus, new float2Like(0f, 0f), new float2Like(400f, 0f), new float2Like(800f, 0f));
            greedy.EnabledDemand = 2000f;
            AssertTrue(SuitabilitySanity.Check(new List<LineHealth>(), new List<SuggestedRoute> { greedy }, 1000f, 8000f, complaints.Add) == 2, "demand above the city's total, and a short line unlocking most of it");
        }

        private static void PanelPayloadRowsKeepTheirFieldOrder()
        {
            var route = MakeRoute(ModePreset.Bus, new float2Like(100.7f, 200.2f), new float2Like(300f, 400f), new float2Like(500.9f, 600.1f));
            route.EnabledDemand = 50f;
            route.Schedule = LineSchedule.Day;
            route.DayUtilisation = 0.25f;
            route.NightUtilisation = 0.104f;
            string rows = SuitabilityPanelPayload.RouteRows(new List<SuggestedRoute> { route, route }, 1000f);
            string[] lines = rows.Split('\n');
            AssertTrue(lines.Length == 2, "one row per route");
            string[] fields = lines[0].Split('|');
            AssertTrue(fields.Length == 10, $"the route row has ten fields, got {fields.Length.ToString(CultureInfo.InvariantCulture)}: {lines[0]}");
            AssertTrue(fields[0] == "Bus" && fields[1] == (route.Length / 1000f).ToString("F1", CultureInfo.InvariantCulture) && fields[2] == "3" && fields[3] == "2", "mode, km, stops, vehicles");
            AssertTrue(fields[4] == TransitModes.ColorCssFor(ModePreset.Bus), "the colour travels with the row");
            AssertTrue(fields[5] == "5.0", "a reach under ten percent keeps one decimal");
            AssertTrue(fields[6] == "100,200>500,600" && fields[6] == SuitabilityPanelPayload.RouteKeyOf(route), "the row's identity is where the line runs between");
            AssertTrue(fields[7] == "Day" && fields[8] == "25" && fields[9] == "10", "schedule and the two period utilisations");
            route.EnabledDemand = 500f;
            AssertTrue(SuitabilityPanelPayload.RouteRows(new List<SuggestedRoute> { route }, 1000f).Split('|')[5] == "50", "a reach of ten percent or more is a whole number");
            AssertTrue(SuitabilityPanelPayload.RouteRows(new List<SuggestedRoute> { route }, 0f).Split('|')[5] == "0.0", "no unserved travel means no reach");
            AssertTrue(SuitabilityPanelPayload.RouteKeyOf(MakeRoute(ModePreset.Bus, new float2Like(1f, 1f))) == "empty", "a route without two stops has no key");

            AssertTrue(SuitabilityPanelPayload.RouteSummary(new List<SuggestedRoute> { route }) == "1 suggested: #1 Bus " + (route.Length / 1000f).ToString("F1", CultureInfo.InvariantCulture) + "km, 3 stops, 2 veh", "the options-page summary");
            AssertTrue(SuitabilityPanelPayload.EmptyRouteSummary(-1, -1) == "No corridor was strong enough to suggest a line."
                && SuitabilityPanelPayload.EmptyRouteSummary(0, 0) == "No journeys found yet — load a city and let it run."
                && SuitabilityPanelPayload.EmptyRouteSummary(12, 7) == "12 journeys, 7 routed, but no new line would improve enough of what is still unserved.", "the three empty summaries");

            var health = new LineHealth
            {
                m_Id = 77,
                m_Name = "Line 3",
                m_Mode = ModePreset.Tram,
                m_Verdict = LineVerdict.Healthy,
                m_Usage = 0.456f,
                m_Vehicles = 4,
                m_Stops = 9,
                m_WindowSamples = 12,
                m_WindowGameHours = 6.4f,
                m_PeakUsage = 0.9f,
                m_Schedule = LineSchedule.DayAndNight,
                m_ScheduleAdvice = LineSchedule.Day,
                m_DayUsage = 0.5f,
                m_NightUsage = 0.05f,
                m_DaySamples = 8,
                m_NightSamples = 4,
                m_RidersPerDay = 812f,
                m_Utilisation = 0.234f,
                m_RecommendedMode = ModePreset.Tram,
                m_RecommendedFleet = 5,
                m_FleetMin = 1,
                m_FleetMax = 13,
                m_PlanningLoad = 96,
                m_DayUtilisation = 0.3f,
                m_NightUtilisation = 0.1f,
            };
            string[] healthFields = SuitabilityPanelPayload.HealthRows(new List<LineHealth> { health }).Split('|');
            AssertTrue(healthFields.Length == 25, $"the health row has twenty-five fields, got {healthFields.Length.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(healthFields[16] == "23" && healthFields[17] == "Tram" && healthFields[18] == "5" && healthFields[19] == "1" && healthFields[20] == "13", "demand utilisation, plan mode, fleet and span");
            AssertTrue(healthFields[21] == "96" && healthFields[22] == "812" && healthFields[23] == "30" && healthFields[24] == "10", "planning load, riders and the period utilisations come last");
            health.m_RidersPerDay = -1f;
            health.m_FleetMax = int.MaxValue;
            string[] noDemand = SuitabilityPanelPayload.HealthRows(new List<LineHealth> { health }).Split('|');
            AssertTrue(noDemand[16] == "-" && noDemand[22] == "-" && noDemand[20] == "0", "no demand reads as a dash, an open span as 0");
            AssertTrue(healthFields[0] == "77" && healthFields[1] == "Line 3" && healthFields[2] == "Healthy" && healthFields[4] == "46" && healthFields[5] == "4" && healthFields[6] == "9", "id, name, verdict, usage, vehicles, stops");
            AssertTrue(healthFields[7] == "12" && healthFields[8] == "6" && healthFields[9] == "90" && healthFields[10] == "DayAndNight" && healthFields[11] == "Day", "evidence, peak, schedule and advice");
            AssertTrue(healthFields[12] == "50" && healthFields[13] == "5" && healthFields[14] == "8" && healthFields[15] == "4", "the period usages and their sample counts come last");
            health.m_Name = string.Empty;
            AssertTrue(SuitabilityPanelPayload.HealthRows(new List<LineHealth> { health }).Split('|')[1] == "Tram", "an unnamed line shows its mode");

            AssertTrue(SuitabilityPanelPayload.DataCoverageRow(1.5f, 4, 24f, 12, 3.2f) == "1.5|4|24|12|3.2", "data coverage row");
            AssertTrue(SuitabilityPanelPayload.EquityRow(0.8f, 10, 80, 0.126) == "80.0|10|80|0.13", "equity row");
        }
    }
}
