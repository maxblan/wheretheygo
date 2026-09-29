using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhereTheyGo.Tests
{
    // The served-demand discount and the panel payload contract: logic that used to
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

            // Carried, in the sense that transit beats walking, but four times slower
            // than this city's normal journey.
            transit[fast] = 9000f;
            walk[fast] = 12000f;
            weight[fast] = 1f;

            var result = new RoutingResult { Transit = transit, WalkOnly = walk };
            var problem = new RoutingProblem { PairCount = fast + 1, PairWeight = weight };

            CarriedReport report = JourneyRouting.MarkCarried(problem, result, new float[fast + 1], new float[fast + 1]);

            AssertEqual(720f, report.MedianSeconds, 0f, "the median is taken over the journeys transit already helps, weighted by how many make each");
            AssertEqual(2160f, report.CeilingSeconds, 0f, "the ceiling is that median times the multiple");
            AssertTrue(report.CarriedPairs == fast, $"the outlier is the only one outside the ceiling, got {report.CarriedPairs.ToString(CultureInfo.InvariantCulture)} carried");
            AssertTrue(!result.Carried[fast], "a journey four times the city's normal length is not carried by it");
            AssertEqual(fast / (float)(fast + 1), report.Share, 1e-6f, "the share is by weight");

            // Walking faster than the bus is the other way out, whatever the ceiling.
            transit[0] = 3999f;
            walk[0] = 3000f;
            _ = JourneyRouting.MarkCarried(problem, result, new float[fast + 1], new float[fast + 1]);
            AssertTrue(!result.Carried[0], "a journey quicker on foot is not carried, however short the ride");

            // A city whose network carries almost nothing has no median to speak of, so
            // the ceiling falls back to the fixed hour rather than to noise.
            var thin = new RoutingResult
            {
                Transit = new[] { 100f, float.MaxValue },
                WalkOnly = new[] { 4000f, 4000f },
            };
            var thinProblem = new RoutingProblem { PairCount = 2, PairWeight = new[] { 1f, 3f } };
            CarriedReport thinReport = JourneyRouting.MarkCarried(thinProblem, thin, new float[2], new float[2]);
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

            // Three pairs are too few for a median, so the ceiling is the fixed hour and
            // both trunk journeys, minutes long, are carried; the third walks.
            AssertTrue(routed.Carried[0] && routed.Carried[1] && !routed.Carried[2], "the two trunk journeys are carried and the third is not");
            AssertEqual(Assumptions.MaxJourneySeconds, routed.Report.CeilingSeconds, 0f, "too few pairs for a median: the ceiling is the fixed hour");
            AssertEqual(40f / 47f, routed.Report.Share, 1e-6f, "the report on the result is the share of weight carried");

            // Every CARRIED pair that rides the line adds its whole weight to that line,
            // split by the share of its rides in the day period.
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

        // A city in which the served ceiling bites: one line along x with stops every
        // 300 m, twenty short pairs riding one or two hops, and one pair riding the
        // whole line. The long ride beats walking by far, but takes more than three
        // times the city's typical carried journey, so it is faster by transit yet not
        // carried. Weights: the short pairs three each, the long one five, so the
        // weighted median stays with the short journeys. Fits a 4 km band grid.
        private const int CeilingFixtureShortPairs = 20;

        private static RoutingProblem CeilingBitesProblem()
        {
            const int stops = 14;
            const float spacing = 300f;
            int pairs = CeilingFixtureShortPairs + 1;
            var problem = new RoutingProblem
            {
                PairOx = new float[pairs],
                PairOz = new float[pairs],
                PairDx = new float[pairs],
                PairDz = new float[pairs],
                PairWeight = new float[pairs],
                PairDayShare = new float[pairs],
                PairCount = pairs,
                BaseStopX = new float[stops],
                BaseStopZ = new float[stops],
                BaseStopCount = stops,
                TargetLine = 0,
                WalkRadius = Assumptions.TransferWalkRadius,
                BoardPenaltySeconds = Assumptions.DefaultBoardPenaltySeconds,
                MaxTravelSeconds = Assumptions.MaxJourneySeconds,
                ZoneReachMetres = Assumptions.ZoneStopReachMetres,
                // No walks here, on purpose. The hops are 300 and 600 m so the whole
                // fixture fits a 4 km band grid, and under the player's default horizon
                // (WalkedJourneysAreAWalkNotATransitQuestion) the 300 m hops would be
                // walks, leaving too little for the ceiling to bite on. This fixture is
                // about the ceiling, so walking is switched off and every hop is a
                // transit question.
                WalkedHorizonSeconds = 0f,
            };
            var lineStops = new int[stops];
            for (int k = 0; k < stops; k++)
            {
                problem.BaseStopX[k] = 100f + (k * spacing);
                problem.BaseStopZ[k] = 100f;
                lineStops[k] = k;
            }

            // Thirteen one-hop pairs, then seven two-hop pairs.
            for (int i = 0; i < CeilingFixtureShortPairs; i++)
            {
                int from = i < stops - 1 ? i : i - (stops - 1);
                int hops = i < stops - 1 ? 1 : 2;
                problem.PairOx[i] = problem.BaseStopX[from];
                problem.PairOz[i] = 100f;
                problem.PairDx[i] = problem.BaseStopX[from + hops];
                problem.PairDz[i] = 100f;
                problem.PairWeight[i] = 3f;
                problem.PairDayShare[i] = 1f;
            }

            int last = CeilingFixtureShortPairs;
            problem.PairOx[last] = problem.BaseStopX[0];
            problem.PairOz[last] = 100f;
            problem.PairDx[last] = problem.BaseStopX[stops - 1];
            problem.PairDz[last] = 100f;
            problem.PairWeight[last] = 5f;
            problem.PairDayShare[last] = 0.5f;
            problem.BaseLines.Add(new TransitLine { m_Stops = lineStops, m_ExpectedWait = 60f, m_SpeedMetresPerSecond = 15f });
            return problem;
        }

        // A line is credited only with the journeys the network CARRIES. A journey the
        // line makes faster than walking but not fast enough for this city adds nothing
        // to the line's riders and does not ride the target, so the line window, the
        // rank and the band highlight agree with the headline.
        private static void RiderCreditFollowsCarried()
        {
            RoutingProblem problem = CeilingBitesProblem();
            int last = CeilingFixtureShortPairs;

            RoutingResult routed = JourneyRouting.Evaluate(problem);

            // The fixture must actually be the case it claims to be: the long ride beats
            // walking, so the old rule would have credited it, and it is over the ceiling.
            AssertTrue(routed.Transit[last] < routed.WalkOnly[last], $"the long ride beats walking: {routed.Transit[last].ToString("F0", CultureInfo.InvariantCulture)}s against {routed.WalkOnly[last].ToString("F0", CultureInfo.InvariantCulture)}s");
            AssertTrue(routed.Report.CeilingSeconds < Assumptions.MaxJourneySeconds, "enough pairs are carried for the ceiling to come from the city's own median");
            AssertTrue(routed.Transit[last] > routed.Report.CeilingSeconds, $"and it is over the ceiling: {routed.Transit[last].ToString("F0", CultureInfo.InvariantCulture)}s against {routed.Report.CeilingSeconds.ToString("F0", CultureInfo.InvariantCulture)}s");
            AssertTrue(!routed.Carried[last], "so it is not carried");
            AssertEqual(CeilingFixtureShortPairs, routed.Report.CarriedPairs, 0, "every short pair is");
            for (int i = 0; i < CeilingFixtureShortPairs; i++)
            {
                AssertTrue(routed.Carried[i] && routed.RidesTarget[i], "a carried pair that rides the selected line rides the target");
            }

            AssertEqual(3f * CeilingFixtureShortPairs, (float)routed.BaseRiders[0], 1e-3f, "the line is credited with the carried journeys and nothing else");
            AssertEqual(3f * CeilingFixtureShortPairs, (float)routed.BaseRidersByDay[0], 1e-3f, "all of them by day");
            AssertEqual(0f, (float)routed.BaseRidersByNight[0], 1e-3f, "the uncarried pair's night half is not there either");
            AssertTrue(!routed.RidesTarget[last], "the uncarried pair does not ride the target line, whatever its itinerary boarded");

            // Taking the line out measures only what its carried riders lose.
            var reduced = new RoutingProblem
            {
                PairCount = problem.PairCount,
                PairOx = problem.PairOx,
                PairOz = problem.PairOz,
                PairDx = problem.PairDx,
                PairDz = problem.PairDz,
                PairWeight = problem.PairWeight,
                PairDayShare = problem.PairDayShare,
                Geometry = problem.Geometry,
                BaseStopCount = problem.BaseStopCount,
                BaseStopX = problem.BaseStopX,
                BaseStopZ = problem.BaseStopZ,
                WalkRadius = problem.WalkRadius,
                BoardPenaltySeconds = problem.BoardPenaltySeconds,
                MaxTravelSeconds = problem.MaxTravelSeconds,
                ZoneReachMetres = problem.ZoneReachMetres,
            };
            LineContribution contribution = JourneyRouting.Measure(problem, routed, JourneyRouting.Evaluate(reduced));
            AssertEqual(3f * CeilingFixtureShortPairs, (float)contribution.RiderWeight, 1e-3f, "the line window's riders are the carried ones");
        }

        // The served ceiling comes from the median JOURNEY, not the median zone pair:
        // a pair a hundred people make sets the city's typical
        // journey a hundred times as firmly as a pair one person makes. With every
        // pair weighing one, the weighted median is the plain one.
        private static void ServedCeilingMedianIsWeightedByJourneys()
        {
            // Twenty light pairs at 100..119 s and one heavy pair at 1000 s.
            const int light = 20;
            var transit = new float[light + 1];
            var walk = new float[light + 1];
            var weight = new float[light + 1];
            for (int i = 0; i < light; i++)
            {
                transit[i] = 100f + i;
                walk[i] = 4000f;
                weight[i] = 1f;
            }

            transit[light] = 1000f;
            walk[light] = 4000f;
            weight[light] = 100f;

            var result = new RoutingResult { Transit = transit, WalkOnly = walk };
            var problem = new RoutingProblem { PairCount = light + 1, PairWeight = weight };
            CarriedReport heavy = JourneyRouting.MarkCarried(problem, result, new float[light + 1], new float[light + 1]);
            AssertEqual(1000f, heavy.MedianSeconds, 0f, "a hundred people making the slow journey make it the city's typical one");
            AssertEqual(3000f, heavy.CeilingSeconds, 0f, "and the ceiling follows it");
            AssertTrue(result.Carried[light], "so the heavy pair is carried");

            // The same times with every pair weighing one: the slow journey is one in
            // twenty-one and the median is the plain one.
            for (int i = 0; i <= light; i++)
            {
                weight[i] = 1f;
            }

            CarriedReport plain = JourneyRouting.MarkCarried(problem, result, new float[light + 1], new float[light + 1]);
            AssertEqual(110f, plain.MedianSeconds, 0f, "with unit weights the median is the k-th smallest at k = count / 2");
            AssertTrue(!result.Carried[light], "and the slow journey is over the ceiling");

            // The rule at the seam: the smallest time whose cumulative weight EXCEEDS half
            // the total, so an exact half does not stop early and ties with the plain
            // median are resolved the way SelectKth resolved them.
            AssertEqual(200f, TransitGraph.ServedCeiling(new[] { 100f, 200f }, new[] { 1f, 1f }, 2, 1f, 3600f, 1, out _), 0f, "two equal weights: the upper one, as the plain median of two");
            AssertEqual(100f, TransitGraph.ServedCeiling(new[] { 200f, 100f }, new[] { 2f, 3f }, 2, 1f, 3600f, 1, out _), 0f, "the heavier of two is the median");
            AssertEqual(300f, TransitGraph.ServedCeiling(new[] { 300f, 100f, 200f }, new[] { 2f, 1f, 1f }, 3, 1f, 3600f, 1, out _), 0f, "exactly half is not past half");
            AssertEqual(300f, TransitGraph.ServedCeiling(new[] { 100f, 200f, 300f }, new[] { 1f, 1f, 2f }, 3, 1f, 3600f, 1, out float median), 0f, "however the input is ordered");
            AssertEqual(300f, median, 0f, "and the median reported is the one the ceiling came from");
            AssertEqual(3600f, TransitGraph.ServedCeiling(new[] { 100f, 200f }, new[] { 0f, 0f }, 2, 3f, 3600f, 1, out _), 0f, "pairs nobody makes give no median, and the fixed hour");
            AssertEqual(100f, TransitGraph.ServedCeiling(new[] { 50f, 100f }, new[] { 0f, 1f }, 2, 1f, 3600f, 1, out _), 0f, "a pair nobody makes does not pull the median");
        }

        // A journey within the walking horizon is a WALK: a third
        // state beside carried and not carried, and the one that wins. It is not
        // carried however fast the bus, it credits no line, it is no sample for the
        // ceiling's median, and it is out of the headline share on both sides.
        private static void WalkedJourneysAreAWalkNotATransitQuestion()
        {
            // The ceiling fixture: twenty-four pairs transit carries in 600..830 s, one
            // outlier at 9000 s. Then thirty short pairs, each walkable in 300 s and
            // ridable in 100 s. Left in, they would be thirty of the fifty-five samples
            // and drag the median down to 100 s, a ceiling of 300 s, and every real
            // transit journey in the city over it.
            const int fast = 24;
            const int walks = 30;
            int count = fast + 1 + walks;
            var transit = new float[count];
            var walk = new float[count];
            var weight = new float[count];
            for (int i = 0; i < fast; i++)
            {
                transit[i] = 600f + (i * 10f);
                walk[i] = 4000f;
                weight[i] = 1f;
            }

            transit[fast] = 9000f;
            walk[fast] = 12000f;
            weight[fast] = 1f;
            for (int i = fast + 1; i < count; i++)
            {
                transit[i] = 100f;
                walk[i] = 300f;
                weight[i] = 1f;
            }

            var result = new RoutingResult { Transit = transit, WalkOnly = walk };
            var problem = new RoutingProblem
            {
                PairCount = count,
                PairWeight = weight,
                WalkedHorizonSeconds = Assumptions.CoverageWalkMinutesDefault * 60f,
            };
            CarriedReport report = JourneyRouting.MarkCarried(problem, result, new float[count], new float[count]);

            for (int i = fast + 1; i < count; i++)
            {
                AssertTrue(result.Walked[i], "a 300 s walk is within the default horizon (five minutes, and the horizon itself counts)");
                AssertTrue(!result.Carried[i], "and is not carried, although the bus would be three times as fast");
            }

            AssertTrue(!result.Walked[0] && !result.Walked[fast], "a 4000 s walk is not");
            AssertEqual(walks, report.WalkedPairs, 0, "the walks are counted");
            AssertEqual((float)walks, (float)report.WalkedWeight, 0f, "and weighed");
            AssertEqual(720f, report.MedianSeconds, 0f, "the median is the same as without them: a walk is no sample for the city's typical transit journey");
            AssertEqual(2160f, report.CeilingSeconds, 0f, "so the ceiling stands");
            AssertEqual(fast, report.CarriedPairs, 0, "and every real transit journey is still carried");
            AssertEqual(fast / (float)(fast + 1), report.Share, 1e-6f, "the share is carried over what transit could serve: the walks are out of the denominator");
            AssertEqual(walks / (float)count, report.WalkedShare, 1e-6f, "the walked share is against the whole city, which is what it is left out of");

            // The seam: exactly the horizon is a walk, a second over is not.
            var edge = new RoutingResult { Transit = new[] { 100f, 100f }, WalkOnly = new[] { 600f, 601f } };
            var edgeProblem = new RoutingProblem { PairCount = 2, PairWeight = new[] { 1f, 1f }, WalkedHorizonSeconds = 600f };
            _ = JourneyRouting.MarkCarried(edgeProblem, edge, new float[2], new float[2]);
            AssertTrue(edge.Walked[0] && !edge.Walked[1], "the horizon itself is still a walk; one second past it is a journey");

            // A problem built without a horizon walks nothing, which is what every
            // fixture in this file that does not name one relies on.
            var none = new RoutingResult { Transit = new[] { 100f }, WalkOnly = new[] { 1f } };
            _ = JourneyRouting.MarkCarried(new RoutingProblem { PairCount = 1, PairWeight = new[] { 1f } }, none, new float[1], new float[1]);
            AssertTrue(!none.Walked[0], "no horizon, no walks");

            // Through the routing itself: the trunk city with a fourth pair riding one
            // 300 m hop. The bus beats the walk, so the line used to be
            // credited with it and it rode the target; now it is a walk and does neither.
            var city = new RoutingProblem
            {
                PairOx = new[] { 0f, 0f, 5000f, 0f },
                PairOz = new[] { 0f, 0f, 5000f, 0f },
                PairDx = new[] { 3000f, 3000f, 6000f, 300f },
                PairDz = new[] { 0f, 0f, 5000f, 0f },
                PairWeight = new[] { 30f, 10f, 7f, 12f },
                PairDayShare = new[] { 1f, 0f, 1f, 1f },
                PairCount = 4,
                BaseStopX = new[] { 0f, 300f, 3000f },
                BaseStopZ = new[] { 0f, 0f, 0f },
                BaseStopCount = 3,
                TargetLine = 0,
                WalkRadius = Assumptions.TransferWalkRadius,
                BoardPenaltySeconds = Assumptions.DefaultBoardPenaltySeconds,
                MaxTravelSeconds = Assumptions.MaxJourneySeconds,
                ZoneReachMetres = Assumptions.ZoneStopReachMetres,
                WalkedHorizonSeconds = Assumptions.CoverageWalkMinutesDefault * 60f,
            };
            city.BaseLines.Add(new TransitLine { m_Stops = new[] { 0, 1, 2 }, m_ExpectedWait = 60f, m_SpeedMetresPerSecond = 15f });
            RoutingResult routed = JourneyRouting.Evaluate(city);
            AssertTrue(routed.Transit[3] < routed.WalkOnly[3], $"the fixture's hop is quicker by bus: {routed.Transit[3].ToString("F0", CultureInfo.InvariantCulture)}s against {routed.WalkOnly[3].ToString("F0", CultureInfo.InvariantCulture)}s");
            AssertTrue(routed.Walked[3] && !routed.Carried[3] && !routed.RidesTarget[3], "and it is a walk: not carried, not riding the selected line");
            AssertTrue(!routed.Walked[0] && routed.Carried[0] && routed.Carried[1], "the 3 km pairs are transit journeys and carried");
            AssertEqual(40f, (float)routed.BaseRiders[0], 1e-3f, "the line is credited with the carried journeys and not with the walk");
            AssertEqual(40f / 47f, routed.Report.Share, 1e-6f, "the share is over the three pairs transit could serve");
            AssertEqual(12f / 59f, routed.Report.WalkedShare, 1e-6f, "and the walked share over all four");
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

        // The load a line's own readings show, hour by hour, and the difference
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
