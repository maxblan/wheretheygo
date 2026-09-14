using System;
using System.Collections.Generic;
using System.Globalization;

namespace TransitArchitect.Tests
{
    // The served-demand discount and the panel payload contract — logic that used to
    // live in the ECS system and could not be run here.
    internal static partial class Program
    {
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
            TransitNetwork network = TransitRouting.Build(xs, zs, xs.Length, lines, Assumptions.TransferWalkRadius, Assumptions.DefaultBoardPenaltySeconds);
            var workspace = new DijkstraWorkspace(network.Graph.NodeCount);
            workspace.Run(network.Graph, 0, Assumptions.MaxJourneySeconds);
            AssertTrue(TransitRouting.Inspect(network, workspace, 0, 1, -1, out int boardings, out _, out float ride) && boardings == 1, "the line carries stop 0 to stop 1");

            AssertTrue(served.TryDiscount(network, workspace, flows, out DiscountReport report), "mapped and paired, so the discount runs");
            AssertTrue(report.ServedPairs == 2 && report.PairCount == 3, $"two of three pairs are carried: {report.ServedPairs.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(report.CeilingSeconds == Assumptions.MaxJourneySeconds, "under twenty carried pairs the ceiling is the fixed hour");
            AssertTrue(report.WeightBefore == 14f, "the carried journeys weighed 14 before");
            float direct = ride + ServedDemand.WalkSeconds(0f) + ServedDemand.WalkSeconds(0f);
            float viaWalk = ride + ServedDemand.WalkSeconds((256f * 256f) + (256f * 256f)) + ServedDemand.WalkSeconds(0f);
            AssertTrue(flows[0].m_Weight == 10f * Math.Min(1f, Math.Max(0f, direct / Assumptions.MaxJourneySeconds)), "a carried journey keeps door-to-door / ceiling of its weight");
            AssertTrue(flows[3].m_Weight == 4f * Math.Min(1f, Math.Max(0f, viaWalk / Assumptions.MaxJourneySeconds)), "the walk from the zone centre to its stop is charged");
            AssertTrue(flows[1].m_Weight == 7f && flows[2].m_Weight == 3f && flows[4].m_Weight == 2f, "journeys the network cannot carry keep their weight");
            AssertTrue(report.WeightAfter == flows[0].m_Weight + flows[3].m_Weight, "the report sums what the carried journeys kept");
            AssertTrue(ServedDemand.RemainingWeight(flows) == flows[0].m_Weight + 7f + 3f + flows[3].m_Weight + 2f, "remaining weight is the sum over every flow");
            float longest = served.LongestJourneyMetres(flows);
            AssertTrue(longest == 768f, $"the longest journey is the 768 m between zone 0's and zone 3's centres, got {longest.ToString(CultureInfo.InvariantCulture)}");

            var empty = new ServedDemand();
            AssertTrue(!empty.TryDiscount(network, workspace, flows, out _), "without a zone map there is nothing to discount");
            AssertTrue(empty.LongestJourneyMetres(flows) == 0f, "and no longest journey either");
        }



        // Routing every journey door to door over the lines that exist: the one
        // evaluation the demand refresh makes. Two zones 3 km apart with a trunk line
        // between them, and a third journey the line cannot help with.
        private static void JourneysRouteOverTheExistingNetwork()
        {
            var problem = new LineSetProblem
            {
                // A: the trunk's first stop, B: its last, C: off to the side.
                PairOx = new[] { 0f, 0f, 5000f },
                PairOz = new[] { 0f, 0f, 5000f },
                PairDx = new[] { 3000f, 3000f, 6000f },
                PairDz = new[] { 0f, 0f, 5000f },
                PairWeight = new[] { 30f, 10f, 7f },
                PairDayShare = new[] { 1f, 0f, 1f },
                PairCount = 3,
                BaseStopX = new[] { 0f, 1500f, 3000f },
                BaseStopZ = new[] { 0f, 0f, 0f },
                BaseStopCount = 3,
                WalkRadius = Assumptions.TransferWalkRadius,
                BoardPenaltySeconds = Assumptions.DefaultBoardPenaltySeconds,
                MaxTravelSeconds = Assumptions.MaxJourneySeconds,
                ZoneReachMetres = Assumptions.ZoneStopReachMetres,
            };
            problem.BaseLines.Add(new TransitLine
            {
                m_Stops = new[] { 0, 1, 2 },
                m_ExpectedWait = 120f,
                m_SpeedMetresPerSecond = 15f,
            });

            LineSetEvaluation routed = LineSet.Evaluate(problem, before: null);

            float walkOnly = LineSet.WalkOnlySeconds(problem, 0);
            AssertTrue(routed.After.Length == 3, "one door-to-door time per pair");
            AssertTrue(routed.After[0] < walkOnly, $"the trunk beats walking 3 km: {routed.After[0].ToString("F0", CultureInfo.InvariantCulture)}s against {walkOnly.ToString("F0", CultureInfo.InvariantCulture)}s");
            AssertTrue(routed.After[0] == routed.After[1], "the same two doors give the same time whatever the time of day");
            AssertTrue(routed.After[2] == LineSet.WalkOnlySeconds(problem, 2), "a journey no line reaches keeps the walk");

            // Every pair that rides the line adds its whole weight to that line, split
            // by the share of its rides in the day period.
            AssertTrue(routed.BaseRiders.Length == 1, "one figure per existing line");
            AssertEqual(40f, (float)routed.BaseRiders[0], 1e-3f, "both carried journeys ride the trunk");
            AssertEqual(30f, (float)routed.BaseRidersByDay[0], 1e-3f, "the day share of those riders");
            AssertEqual(10f, (float)routed.BaseRidersByNight[0], 1e-3f, "and the night share");

            // Nothing is saved against a baseline of itself.
            LineSetEvaluation again = LineSet.Evaluate(problem, routed.After);
            AssertTrue(again.TimeSaved == 0.0, "routing the same network twice saves nothing");
            for (int i = 0; i < problem.PairCount; i++)
            {
                AssertTrue(again.After[i] == routed.After[i], "and gives the same times");
            }

            // With no line at all every journey walks.
            problem.BaseLines.Clear();
            problem.Geometry = null;
            LineSetEvaluation walking = LineSet.Evaluate(problem, before: null);
            for (int i = 0; i < problem.PairCount; i++)
            {
                AssertTrue(walking.After[i] == LineSet.WalkOnlySeconds(problem, i), "a city with no transit walks every journey");
            }
        }

        private static void PanelPayloadRowsKeepTheirFieldOrder()
        {
            AssertTrue(PanelPayload.DataCoverageRow(1.5f, 4, 24f, 12, 3.2f) == "1.5|4|24|12|3.2", "data coverage row");
            AssertTrue(PanelPayload.EquityRow(0.8f, 10, 0.126) == "80.0|10|0.13", "coverage row");
        }
    }
}
