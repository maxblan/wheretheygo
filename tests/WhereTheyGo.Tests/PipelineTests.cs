using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhereTheyGo.Tests
{
    // The served-demand discount and the panel payload contract — logic that used to
    // live in the ECS system and could not be run here.
    internal static partial class Program
    {
        // "The network carries this journey" is two conditions, and this pins both: the
        // transit itinerary must beat walking, and it must stay under the ceiling the
        // city's own median sets. Enough pairs that the median is allowed to speak
        // (Assumptions.MinPairsForServedMedian).
        private static void CarriedIsFasterThanWalkingAndUnderTheCeiling()
        {
            const int fast = 24;
            var transit = new float[fast + 1];
            var walk = new float[fast + 1];
            var weight = new float[fast + 1];
            for (int i = 0; i < fast; i++)
            {
                transit[i] = 600f + (i * 10f);
                walk[i] = 4000f;
                weight[i] = 1f;
            }

            // Carried, in the sense that transit beats walking — but four times slower
            // than this city's normal journey.
            transit[fast] = 9000f;
            walk[fast] = 12000f;
            weight[fast] = 1f;

            var result = new RoutingResult { Transit = transit, WalkOnly = walk };
            var problem = new RoutingProblem { PairCount = fast + 1, PairWeight = weight };

            CarriedReport report = JourneyRouting.MarkCarried(problem, result, new float[fast + 1]);

            AssertEqual(720f, report.MedianSeconds, 0f, "the median is taken over the journeys transit already helps");
            AssertEqual(2160f, report.CeilingSeconds, 0f, "the ceiling is that median times the multiple");
            AssertTrue(report.CarriedPairs == fast, $"the outlier is the only one outside the ceiling, got {report.CarriedPairs.ToString(CultureInfo.InvariantCulture)} carried");
            AssertTrue(!result.Carried[fast], "a journey four times the city's normal length is not carried by it");
            AssertEqual(fast / (float)(fast + 1), report.Share, 1e-6f, "the share is by weight");

            // Walking faster than the bus is the other way out, whatever the ceiling.
            transit[0] = 3999f;
            walk[0] = 3000f;
            _ = JourneyRouting.MarkCarried(problem, result, new float[fast + 1]);
            AssertTrue(!result.Carried[0], "a journey quicker on foot is not carried, however short the ride");

            // A city whose network carries almost nothing has no median to speak of, so
            // the ceiling falls back to the fixed hour rather than to noise.
            var thin = new RoutingResult
            {
                Transit = new[] { 100f, float.MaxValue },
                WalkOnly = new[] { 4000f, 4000f },
            };
            var thinProblem = new RoutingProblem { PairCount = 2, PairWeight = new[] { 1f, 3f } };
            CarriedReport thinReport = JourneyRouting.MarkCarried(thinProblem, thin, new float[2]);
            AssertEqual(Assumptions.MaxJourneySeconds, thinReport.CeilingSeconds, 0f, "too few carried journeys: the fixed hour");
            AssertTrue(thin.Carried[0] && !thin.Carried[1], "the one journey it does carry still counts");
            AssertEqual(0.25f, thinReport.Share, 1e-6f, "and the share weighs it against everything else");
        }

        // Routing every journey door to door over the lines that exist: the one
        // evaluation the demand refresh makes. Two zones 3 km apart with a trunk line
        // between them, and a third journey the line cannot help with.
        private static void JourneysRouteOverTheExistingNetwork()
        {
            var problem = new RoutingProblem
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

            RoutingResult routed = JourneyRouting.Evaluate(problem, before: null);

            float walkOnly = JourneyRouting.WalkOnlySeconds(problem, 0);
            AssertTrue(routed.After.Length == 3, "one door-to-door time per pair");
            AssertTrue(routed.After[0] < walkOnly, $"the trunk beats walking 3 km: {routed.After[0].ToString("F0", CultureInfo.InvariantCulture)}s against {walkOnly.ToString("F0", CultureInfo.InvariantCulture)}s");
            AssertTrue(routed.After[0] == routed.After[1], "the same two doors give the same time whatever the time of day");
            AssertTrue(routed.After[2] == JourneyRouting.WalkOnlySeconds(problem, 2), "a journey no line reaches keeps the walk");

            // Every pair that rides the line adds its whole weight to that line, split
            // by the share of its rides in the day period.
            AssertTrue(routed.BaseRiders.Length == 1, "one figure per existing line");
            AssertEqual(40f, (float)routed.BaseRiders[0], 1e-3f, "both carried journeys ride the trunk");
            AssertEqual(30f, (float)routed.BaseRidersByDay[0], 1e-3f, "the day share of those riders");
            AssertEqual(10f, (float)routed.BaseRidersByNight[0], 1e-3f, "and the night share");

            // Nothing is saved against a baseline of itself.
            RoutingResult again = JourneyRouting.Evaluate(problem, routed.After);
            AssertTrue(again.TimeSaved == 0.0, "routing the same network twice saves nothing");
            for (int i = 0; i < problem.PairCount; i++)
            {
                AssertTrue(again.After[i] == routed.After[i], "and gives the same times");
            }

            // With no line at all every journey walks.
            problem.BaseLines.Clear();
            problem.Geometry = null;
            RoutingResult walking = JourneyRouting.Evaluate(problem, before: null);
            for (int i = 0; i < problem.PairCount; i++)
            {
                AssertTrue(walking.After[i] == JourneyRouting.WalkOnlySeconds(problem, i), "a city with no transit walks every journey");
            }
        }

        // What a line is worth, measured by taking it away. The two numbers the
        // section shows rest entirely on this.
        private static void LineContributionComesFromTakingTheLineOut()
        {
            // Three journeys ride the line. One saves ten minutes by it, one saves
            // nothing (something else is just as fast), one is not on it at all.
            var problem = new RoutingProblem
            {
                PairCount = 4,
                PairWeight = new[] { 10f, 5f, 3f, 100f },
            };
            var with = new RoutingResult
            {
                After = new[] { 600f, 800f, 900f, 100f },
                RidesTarget = new[] { true, true, true, false },
            };
            var without = new RoutingResult
            {
                // The first loses ten minutes, the second is one second worse (which is
                // "no slower"), the third cannot be carried at all any more.
                After = new[] { 1200f, 801f, 3600f, 100f },
            };

            LineContribution contribution = JourneyRouting.Measure(problem, with, without);

            AssertEqual(18f, (float)contribution.RiderWeight, 1e-3f, "only the journeys that ride the line count as its riders");
            AssertEqual((10f * 600f) + (3f * 2700f), (float)contribution.SecondsSaved, 1e-1f, "each rider saves what it would lose without the line");
            AssertEqual(5f, (float)contribution.NoSlowerWeight, 1e-3f, "a rider that loses a second by its removal is no slower without it");
            AssertTrue(Math.Abs(contribution.DuplicateShare - (5f / 18f)) < 1e-5f, "the duplicate share is that weight over the riders");
            AssertEqual((float)((10f * 600f) + (3f * 2700f)) / 60f, (float)contribution.MinutesSaved, 1e-1f, "and the panel reads it in minutes");

            // A line nobody rides is not a division by zero.
            var nobody = new RoutingResult { After = new[] { 600f }, RidesTarget = new[] { false } };
            LineContribution empty = JourneyRouting.Measure(
                new RoutingProblem { PairCount = 1, PairWeight = new[] { 9f } },
                nobody,
                new RoutingResult { After = new[] { 600f } });
            AssertTrue(empty.RiderWeight == 0.0 && empty.DuplicateShare == 0f && empty.MinutesSaved == 0.0, "a line nobody rides reads as nothing, not as a crash");
        }

        // The load a line's own readings show, hour by hour — and the difference
        // between an hour nobody watched and an hour nobody rode.
        private static void HourlyLoadSeparatesQuietHoursFromUnwatchedOnes()
        {
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            // Two readings at 08:00 with 40 and 60 aboard of 100 seats, one at 03:00
            // with nobody aboard, and one at 12:00 while the line stood still.
            history.Record(7, new LineObservation { m_Frame = 10, m_Passengers = 40, m_Capacity = 100, m_Vehicles = 2, m_TimeOfDay = 8f / 24f });
            history.Record(7, new LineObservation { m_Frame = 20, m_Passengers = 60, m_Capacity = 100, m_Vehicles = 2, m_TimeOfDay = 8f / 24f });
            history.Record(7, new LineObservation { m_Frame = 30, m_Passengers = 0, m_Capacity = 100, m_Vehicles = 2, m_TimeOfDay = 3f / 24f });
            history.Record(7, new LineObservation { m_Frame = 40, m_Passengers = 0, m_Capacity = 0, m_Vehicles = 0, m_TimeOfDay = 12f / 24f });

            var riders = new float[Band.HoursPerDay];
            var capacity = new float[Band.HoursPerDay];
            var samples = new int[Band.HoursPerDay];
            history.HourlyLoad(7, riders, capacity, samples);

            AssertTrue(samples[8] == 2 && riders[8] == 50f && capacity[8] == 100f, "an hour averages its readings");
            AssertTrue(samples[3] == 1 && riders[3] == 0f, "an hour watched with nobody aboard is a measured zero");
            AssertTrue(samples[12] == 0, "a reading taken while the line stood still says nothing about its load");
            AssertTrue(samples[9] == 0 && riders[9] == 0f, "an hour nobody watched has no samples, which the panel draws as a gap");

            // Reused buffers must not carry the last line's numbers into this one.
            history.HourlyLoad(99, riders, capacity, samples);
            for (int hour = 0; hour < Band.HoursPerDay; hour++)
            {
                AssertTrue(samples[hour] == 0 && riders[hour] == 0f && capacity[hour] == 0f, "a line with no readings clears the buffers it was handed");
            }
        }

    }
}
