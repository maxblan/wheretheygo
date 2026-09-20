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

            RoutingResult routed = JourneyRouting.Evaluate(problem);

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

            // Routing the same network twice gives the same times, bit for bit.
            RoutingResult again = JourneyRouting.Evaluate(problem);
            for (int i = 0; i < problem.PairCount; i++)
            {
                AssertTrue(again.After[i] == routed.After[i], "a second evaluation gives the same times");
            }

            // The loop closes: a journey from the trunk's LAST stop back to its first
            // rides round the seam the closing hop provides, and cannot without it.
            var seam = new RoutingProblem
            {
                PairOx = new[] { 3000f },
                PairOz = new[] { 0f },
                PairDx = new[] { 0f },
                PairDz = new[] { 0f },
                PairWeight = new[] { 1f },
                PairCount = 1,
                BaseStopX = problem.BaseStopX,
                BaseStopZ = problem.BaseStopZ,
                BaseStopCount = 3,
                WalkRadius = problem.WalkRadius,
                BoardPenaltySeconds = problem.BoardPenaltySeconds,
                MaxTravelSeconds = problem.MaxTravelSeconds,
                ZoneReachMetres = problem.ZoneReachMetres,
            };
            seam.BaseLines.Add(new TransitLine { m_Stops = new[] { 0, 1, 2 }, m_ExpectedWait = 120f, m_SpeedMetresPerSecond = 15f });
            RoutingResult open = JourneyRouting.Evaluate(seam);
            AssertTrue(open.Transit[0] == float.MaxValue, "with the loop left open the last stop cannot reach the first by riding");
            seam.BaseLines.Clear();
            seam.BaseLines.Add(new TransitLine { m_Stops = new[] { 0, 1, 2, 0 }, m_RideSeconds = new[] { 0f, 100f, 100f, 200f }, m_ExpectedWait = 120f, m_SpeedMetresPerSecond = 15f });
            RoutingResult closed = JourneyRouting.Evaluate(seam);
            AssertTrue(closed.Transit[0] < float.MaxValue && closed.After[0] < JourneyRouting.WalkOnlySeconds(seam, 0), "with the closing hop the rider stays aboard round the loop");
            AssertEqual(1f, (float)closed.BaseRiders[0], 1e-6f, "and rides the line");

            // With no line at all every journey walks.
            problem.BaseLines.Clear();
            problem.Geometry = null;
            RoutingResult walking = JourneyRouting.Evaluate(problem);
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

        // A line's own number means nothing until it is put beside the others. Ties
        // share a rank on purpose: a tie-break invented here would move between two
        // refreshes with nothing in the city having changed.
        internal static void LineStandingRanksAgainstTheOtherLines()
        {
            var riders = new List<float> { 400f, 1200f, 400f, 0f };

            LineStanding busiest = LineStanding.Of(1200f, riders, 10000f);
            AssertEqual(1, busiest.Rank, 0, "the busiest line is first");
            AssertEqual(4, busiest.LineCount, 0, "out of four lines");
            AssertEqual(0.12f, busiest.CityShare, 1e-5f, "1200 of the city's 10000 journeys");

            LineStanding tied = LineStanding.Of(400f, riders, 10000f);
            AssertEqual(2, tied.Rank, 0, "two lines at 400 are both second, not second and third");

            LineStanding empty = LineStanding.Of(0f, riders, 10000f);
            AssertEqual(4, empty.Rank, 0, "the line nobody rides is last");
            AssertEqual(0f, empty.CityShare, 0f, "and carries none of the city");

            LineStanding only = LineStanding.Of(50f, new List<float> { 50f }, 0f);
            AssertEqual(1, only.Rank, 0, "the only line is first");
            AssertEqual(0f, only.CityShare, 0f, "a city with no journeys yields no share rather than infinity");

            LineStanding unrouted = LineStanding.Of(-1f, riders, 10000f);
            AssertEqual(0, unrouted.Rank, 0, "a line the routing has not reached yet has no rank at all");
        }

        // The load chart's y axis follows the line rather than sitting at 0-100 %: a
        // metro carrying fifteen people in an 840-seat train drew as a flat line.
        internal static void LoadAxisFollowsTheLinesOwnBusiestHour()
        {
            AssertEqual(0.6f, LoadAxis.TopOf(new[] { 0.58f, 0.1f, -1f }), 1e-5f, "a peak of 58 % tops out at 60, which halves to 30");
            AssertEqual(0.04f, LoadAxis.TopOf(new[] { 0.03f, -1f, 0.01f }), 1e-5f, "a peak of 3 % tops out at 4, not at 100");
            AssertEqual(0.02f, LoadAxis.TopOf(new[] { -1f, -1f }), 1e-5f, "an unwatched line still gets the smallest axis, not a zero-high one");
            AssertEqual(0.02f, LoadAxis.TopOf(null), 1e-5f, "and so does no data at all");
            AssertEqual(1f, LoadAxis.TopOf(new[] { 0.99f }), 1e-5f, "a full line tops out at its seats");
            AssertEqual(1.14f, LoadAxis.TopOf(new[] { 1.135f }), 1e-4f, "standing room past the seats is drawn, not clipped to the top");

            float[] tops = new float[Assumptions.LoadAxisTopsPercent.Length];
            for (int i = 0; i < tops.Length; i++)
            {
                tops[i] = Assumptions.LoadAxisTopsPercent[i] / 100f;
                AssertTrue(i == 0 || tops[i] > tops[i - 1], "the ladder climbs");
                AssertEqual(tops[i], LoadAxis.TopOf(new[] { tops[i] }), 1e-5f, "a peak exactly on a rung stays on it rather than jumping to the next");
            }
        }

    }
}
