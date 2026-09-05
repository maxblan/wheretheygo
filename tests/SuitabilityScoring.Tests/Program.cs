using System;
using System.Collections.Generic;
using System.Linq;

namespace StationSuitabilityOverlay.Tests
{
    // Minimal self-contained harness: no test framework, so it runs offline with
    // nothing to restore. Exit code is the number of failed tests.
    // Distinguishes an assertion failure from a genuine crash in the code under test.
    internal sealed class TestFailedException : Exception
    {
        public TestFailedException()
        {
        }

        public TestFailedException(string message)
            : base(message)
        {
        }

        public TestFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static class Program
    {
        private static int s_Failures;

        private static int Main()
        {
            Run("SelectKth matches a sorted reference", SelectKthMatchesSortedReference);
            Run("SelectKth respects a length shorter than the array", SelectKthRespectsLength);
            Run("PositivePercentile ignores zero and negative cells", PositivePercentileIgnoresNonPositive);
            Run("PositivePercentile returns zero when nothing is positive", PositivePercentileAllNonPositive);
            Run("Sparse city with negative cells does not saturate", SparseCityDoesNotSaturate);
            Run("Highlight share controls how much reaches the top", HighlightShareControlsTop);
            Run("Normalization clears output when no positive scores", NormalizationClearsWhenEmpty);
            Run("Gamma lifts the low end without changing the cap", GammaLiftsLowEnd);
            Run("Normalization refuses an output shorter than the score field", NormalizationRejectsShortOutput);
            Run("FindTopSites respects separation and count", TopSitesRespectSeparationAndCount);
            Run("FindTopSites returns descending scores", TopSitesDescending);
            Run("FindTopSites ignores non-positive fields", TopSitesIgnoresEmptyField);
            Run("Exact sites beat greedy on the two-fives counterexample", ExactSitesBeatGreedyCounterexample);
            Run("Exact sites match brute force on random grids", ExactSitesMatchBruteForce);
            Run("Exact sites report a sound bound when the budget runs out", ExactSitesBoundWhenExhausted);
            Run("Exact sites rank ties by index and handle empty fields", ExactSitesRankingAndEmptyField);
            Run("Exact sites scale wide-ranging scores without loss", ExactSitesWeightScaling);
            Run("Network sites conflict by walking time and match brute force", NetworkSitesMatchBruteForce);
            Run("Network sites beat greedy where the middle node blocks both ends", NetworkSitesBeatGreedy);
            Run("Walk graph converts metres to whole milliseconds at the planning speed", WalkGraphMilliseconds);
            Run("Integer Dijkstra is exact, bounded and reusable", IntDijkstraExactBoundedReusable);
            Run("Nearest node breaks ties by index and respects the access walk", NearestNodeTiesAndReach);
            Run("Walk access accumulates the linear time kernel per class", WalkAccessAccumulatesKernel);
            Run("Walk access splits stops into coverage, interchange and cross terms", WalkAccessStopTerms);
            Run("Tile terms follow the node and fade with the access walk", WalkAccessTileTerms);
            Run("Catchment classes are exactly the modes' horizons", CatchmentClassesMatchModes);
            Run("Observed trips are held for a game day and scaled to a day's rate", ObservedTripWindowHoldsADay);
            Run("Observed trips restart on a rewound clock and stop at the cap", ObservedTripWindowRestartsAndCaps);
            Run("Directed roads: turn classes, arc times and the game's turn table", DirectedTurnClassesAndTimes);
            Run("Directed roads: a one-way street is drivable one way only", DirectedOneWayStreet);
            Run("Directed roads: turn costs steer between a short turning and a long straight route", DirectedTurnCostsSteer);
            Run("Directed roads: fastest times match an exhaustive path enumeration", DirectedMatchesEnumeration);
            Run("Directed roads: flow follows admitted directions and sums per street", DirectedFlowAssignment);
            Run("Directed roads: stops mid-street are timed from their point on the arc", DirectedPointLegs);
            Run("Equity: coverage counts journeys served at both ends within the horizon", EquityCoverage);
            Run("Equity: weighted Gini is 0 for equal access, rises with concentration, and utilisation follows the peak model", EquityGiniAndUtilisation);
            Run("Line set: time saved is monotone and rewards a trunk-and-feeder pair", LineSetTrunkAndFeeder);
            Run("Line set: the exact selection matches brute force under utilisation and duplicate rules", LineSetMatchesBruteForce);
            Run("Line set: the equity floor ranks coverage before time saved", LineSetEquityFirst);
            Run("Line set: one capped search per origin zone equals one search per pair", LineSetGroupedEqualsPerPair);
            Run("Walk distance counts each tile exactly once", WalkDistanceCountsOnce);
            Run("Walk distance is deterministic across repeats", WalkDistanceIsDeterministic);
            Run("Walk distance respects the radius", WalkDistanceRespectsRadius);
            Run("Walk distance is blocked by water", WalkDistanceBlockedByWater);
            Run("FitNonNegativeLeastSquares recovers known weights", FitRecoversKnownWeights);
            Run("FitNonNegativeLeastSquares clamps negative coefficients", FitClampsNegativeWeights);
            Run("FitNonNegativeLeastSquares rejects underdetermined input", FitRejectsUnderdetermined);
            Run("RSquared is 1 for an exact fit and 0 for no variance", RSquaredBounds);

            Run("Graph adjacency covers both directions", GraphAdjacencyBothDirections);
            Run("Dijkstra finds the cheapest path, not the fewest hops", DijkstraPrefersCheapPath);
            Run("Dijkstra respects the cost limit", DijkstraRespectsMaxCost);
            Run("Dijkstra workspace reuse gives identical results", DijkstraWorkspaceReuse);
            Run("Path accumulation loads flow onto the route taken", FlowFollowsShortestPath);
            Run("Unreachable targets accumulate nothing", FlowIgnoresUnreachable);
            Run("Corridor grows along the strongest flow", CorridorFollowsFlow);
            Run("Corridor is a connected polyline", CorridorIsConnected);
            Run("Corridor respects the length limit", CorridorRespectsMaxLength);
            Run("Corridor flow is a mean, not a sum", CorridorFlowIsMean);
            Run("Demand gate keeps corridors out of empty land", DemandGateBlocksEmptyLand);
            Run("Peeling reduces flow and blocks reuse", PeelingReducesFlow);
            Run("Coverage objective diverts from the busiest corridor", ObjectiveChangesRoutes);
            Run("Coverage seeds the next corridor away from the last one", ObjectiveChangesSeeding);
            Run("Desire lines deposit once per cell crossed", RasterizeDepositsPerCell);
            Run("Polyline simplification removes staircase corners", SimplifyRemovesStaircase);
            Run("Growth records why a corridor stopped", CorridorRecordsWhyItStopped);
            Run("A corridor crosses a quiet gap between busy districts", CorridorBridgesQuietGap);
            Run("A corridor never ends in the quiet gap it crossed", CorridorDoesNotEndInEmptiness);
            Run("A discarded head-side bridge leaves a connected node walk", CorridorNodeWalkSurvivesDiscardedBridge);
            Run("Hitting the length limit is reported after a bridge is handed back", CorridorReportsItsLimitAfterDiscard);
            Run("Growth works without a novelty array on every objective", CorridorGrowsWithoutNovelty);
            Run("Side capture decays a chord once, not once per end", PeelingDecaysAChordOnce);
            Run("Workspace reuse survives the graph changing size", DijkstraWorkspaceResize);

            Run("Direct service beats an equal-time transfer", DirectBeatsTransfer);
            Run("Each change of vehicle costs a boarding", TransfersCostBoardings);
            Run("Walking links nearby stops into one interchange", WalkLinksStops);
            Run("Bucketed walk edges match an exhaustive sweep", WalkEdgesMatchAnExhaustiveSweep);
            Run("Vanilla wait model floors at zero", ExpectedWaitModel);
            Run("A line is judged over the window, not one reading", WindowAveragesLineReadings);
            Run("Readings older than the window are evicted", WindowEvictsPastADay);
            Run("Usage is averaged per sample, not as a ratio of sums", WindowUsageIsPerSample);
            Run("Loading another save restarts the window", WindowResetsWhenFramesRewind);
            Run("A deleted line stops being tracked", WindowForgetsDeletedLines);

            Run("The game asking for vehicles is taken at its word", VerdictFollowsTheGamesFlags);
            Run("A full line already at its fleet target is at mode capacity", VerdictAtModeCapacity);
            Run("A line that fills up at its peak is not \"nearly empty\"", VerdictEmptyNeedsALowPeakToo);
            Run("Long waits are judged against the line's own target", VerdictLongWaitsAreRelative);
            Run("A plan never asks for fewer vehicles than the game already wants", PlanRespectsTheGamesTarget);
            Run("A plan is sized from the peak, not from one reading", PlanSizesFromThePeak);
            Run("A plan quotes the interval that yields its fleet", PlanQuotesAnInterval);
            Run("Growth carries straight on unless a turn is worth it", GrowthPrefersToCarryStraightOn);
            Run("Growth heads away from where it started", GrowthHeadsAwayFromItsOtherEnd);
            Run("A detour to an interchange is worth only so much", ADetourIsWorthOnlySoMuch);
            Run("A corridor that comes back on itself is a ring", RingsAreNotRoutes);
            Run("The served ceiling follows the city's own median journey", ServedCeilingScalesToTheCity);
            Run("A thin network falls back to the fixed hour", ServedCeilingFallsBack);
            Run("A hub is every mode within a walk, not one stop's own", AHubIsTheUnionOverAWalk);
            Run("The bigger interchange beats the nearer one", TheBiggerInterchangeWins);
            Run("The plan payload carries every field the panel reads", PlanPayloadIsComplete);
            Run("Mode: the ladder climbs by the game's capacities until the riders fit", ModeClimbsTheLadderByCapacity);
            Run("Mode: one stop costs the dwell plus braking and acceleration losses, and ride limits hold", StopDelayAndRideLimits);
            Run("Stop plan: a call is made where boarders outweigh the through-riders' delay", StopPlanWeighsBoardersAgainstThrough);
            Run("Stop plan: termini and interchanges are forced, the gap floor holds", StopPlanForcesTerminiAndHubs);
            Run("Stop plan: the dynamic programme matches brute force", StopPlanMatchesBruteForce);

            Run("Interchange weight is capacity relative to a bus, zero when unknown", CapacityWeightIsRelativeToBus);
            Run("Export JSON matches the canonical form byte for byte", ExportJsonIsCanonical);
            Run("Export JSON escapes exactly as ensure_ascii does", ExportJsonEscapes);
            Run("Export float bits round-trip", ExportJsonBitsRoundTrip);
            Run("Export instances carry the digest of their own body", ExportJsonHashesBody);

            Console.WriteLine();
            if (s_Failures == 0)
            {
                Console.WriteLine("All tests passed.");
                return 0;
            }

            Console.WriteLine($"{s_Failures} test(s) FAILED.");
            return s_Failures;
        }

        // ---- tests ----------------------------------------------------------

        private static void SelectKthMatchesSortedReference()
        {
            var source = new float[] { 5f, -2f, 0f, 17.5f, 3f, 3f, -9f, 42f, 1f };
            var reference = (float[])source.Clone();
            Array.Sort(reference);

            for (int k = 0; k < source.Length; k++)
            {
                var working = (float[])source.Clone();
                float actual = SuitabilityScoring.SelectKth(working, working.Length, k);
                AssertEqual(reference[k], actual, 0f, $"k={k}");
            }
        }

        private static void SelectKthRespectsLength()
        {
            // Entries past `length` are stale scratch and must not be selected.
            var values = new float[] { 3f, 1f, 2f, 999f, 999f };
            float median = SuitabilityScoring.SelectKth(values, 3, 1);
            AssertEqual(2f, median, 0f, "median of first three");

            float max = SuitabilityScoring.SelectKth(values, 3, 2);
            AssertEqual(3f, max, 0f, "max of first three");
        }

        private static void PositivePercentileIgnoresNonPositive()
        {
            // Only 1..4 are positive; the 0th percentile of those is 1.
            var source = new float[] { -5f, 0f, 1f, 2f, 3f, 4f, 0f, -1f };
            var scratch = new float[source.Length];

            AssertEqual(1f, SuitabilityScoring.PositivePercentile(source, source.Length, 0f, scratch), 0f, "min positive");
            AssertEqual(4f, SuitabilityScoring.PositivePercentile(source, source.Length, 1f, scratch), 0f, "max positive");
        }

        private static void PositivePercentileAllNonPositive()
        {
            var source = new float[] { 0f, -1f, -2f, 0f };
            var scratch = new float[source.Length];
            AssertEqual(0f, SuitabilityScoring.PositivePercentile(source, source.Length, 0.95f, scratch), 0f, "no positives");
        }

        // The regression test for the bug that turned the whole map red: a city
        // occupies a few percent of the grid, so most cells are exactly zero, and
        // coverage penalties push some cells negative. A percentile taken over ALL
        // cells collapses to ~0 and every cell at or above zero saturates.
        private static void SparseCityDoesNotSaturate()
        {
            const int cells = 10000;
            var scores = new float[cells];
            var scratch = new float[cells];
            var intensities = new byte[cells];

            // 2% built-up with a spread of scores, 0.5% negative (served by a stop
            // in a low-demand spot), the rest empty land at exactly zero.
            for (int i = 0; i < 200; i++)
            {
                scores[i] = 1f + i * 0.05f;
            }
            for (int i = 200; i < 250; i++)
            {
                scores[i] = -1.5f;
            }

            SuitabilityScoring.NormalizeIntensities(scores, cells, 0.05f, 1f, intensities, scratch);

            int saturated = 0;
            int zeroed = 0;
            for (int i = 0; i < cells; i++)
            {
                if (intensities[i] == 255)
                {
                    saturated++;
                }
                if (intensities[i] == 0)
                {
                    zeroed++;
                }
            }

            // Empty land and negative cells must stay at zero intensity...
            AssertTrue(zeroed >= cells - 200, $"expected >= {cells - 200} zero cells, got {zeroed}");
            // ...and only a small share of the BUILT-UP tiles may top out. The bug
            // produced saturated == cells here.
            AssertTrue(saturated > 0, "expected some cells to reach the top of the gradient");
            AssertTrue(saturated <= 40, $"expected at most 40 saturated cells, got {saturated}");

            for (int i = 200; i < 250; i++)
            {
                AssertEqual(0, intensities[i], 0, $"negative cell {i} must not tint");
            }
        }

        private static void HighlightShareControlsTop()
        {
            const int cells = 1000;
            var scratch = new float[cells];
            var scores = new float[cells];
            for (int i = 0; i < cells; i++)
            {
                scores[i] = i + 1;
            }

            var narrow = new byte[cells];
            var wide = new byte[cells];
            SuitabilityScoring.NormalizeIntensities(scores, cells, 0.01f, 1f, narrow, scratch);
            SuitabilityScoring.NormalizeIntensities(scores, cells, 0.20f, 1f, wide, scratch);

            int narrowTop = CountSaturated(narrow);
            int wideTop = CountSaturated(wide);
            AssertTrue(wideTop > narrowTop, $"a wider highlight share must saturate more cells ({wideTop} vs {narrowTop})");
        }

        private static void NormalizationClearsWhenEmpty()
        {
            var scores = new float[] { 0f, -1f, -2f };
            var scratch = new float[scores.Length];
            var intensities = new byte[] { 200, 200, 200 };

            SuitabilityScoring.NormalizeIntensities(scores, scores.Length, 0.05f, 0.6f, intensities, scratch);

            for (int i = 0; i < intensities.Length; i++)
            {
                AssertEqual(0, intensities[i], 0, $"stale intensity at {i} must be cleared");
            }
        }

        private static void GammaLiftsLowEnd()
        {
            var scores = new float[] { 1f, 5f, 10f };
            var scratch = new float[scores.Length];
            var linear = new byte[scores.Length];
            var lifted = new byte[scores.Length];

            SuitabilityScoring.NormalizeIntensities(scores, scores.Length, 0.34f, 1f, linear, scratch);
            SuitabilityScoring.NormalizeIntensities(scores, scores.Length, 0.34f, 0.6f, lifted, scratch);

            AssertTrue(lifted[0] > linear[0], $"gamma < 1 must lift the low end ({lifted[0]} vs {linear[0]})");
            AssertEqual(linear[2], lifted[2], 0, "the top of the gradient must be unaffected by gamma");
        }

        // The write loop addresses the output up to `length`, so a shorter array is a
        // caller error and not something to half-fill: clamping only the clear made it
        // look handled and then indexed past the end.
        private static void NormalizationRejectsShortOutput()
        {
            var scores = new[] { 1f, 2f, 3f, 4f };
            var tooSmall = new byte[2];
            SuitabilityScoring.NormalizeIntensities(scores, 4, 0.25f, 0.6f, tooSmall, new float[4]);
            AssertEqual(0, tooSmall[0] + tooSmall[1], 0, "a short output must be left alone, not partly written");

            // The same call with a correctly sized output still works.
            var sized = new byte[4];
            SuitabilityScoring.NormalizeIntensities(scores, 4, 0.25f, 0.6f, sized, new float[4]);
            AssertTrue(sized[3] > 0, "a correctly sized output is still filled");
        }

        private static void TopSitesRespectSeparationAndCount()
        {
            // Four clear peaks on a 20x20 grid, each surrounded by a soft halo so
            // the local-maximum filter has something to reject.
            const int width = 20;
            const int height = 20;
            var scores = new float[width * height];
            var peaks = new (int x, int y, float v)[]
            {
                (2, 2, 10f), (17, 2, 9f), (2, 17, 8f), (17, 17, 7f),
            };

            foreach (var peak in peaks)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = peak.x + dx;
                        int y = peak.y + dy;
                        if (x < 0 || x >= width || y < 0 || y >= height)
                        {
                            continue;
                        }
                        float value = (dx == 0 && dy == 0) ? peak.v : peak.v * 0.5f;
                        scores[x + y * width] = Math.Max(scores[x + y * width], value);
                    }
                }
            }

            var indices = new int[8];
            var siteScores = new float[8];
            int found = SuitabilityScoring.FindTopSites(scores, width, height, 5, 8, indices, siteScores, out _);
            AssertEqual(4, found, 0, "should find exactly the four peaks");

            // Every accepted pair must be at least `separation` apart.
            for (int i = 0; i < found; i++)
            {
                for (int j = i + 1; j < found; j++)
                {
                    int dx = Math.Abs((indices[i] % width) - (indices[j] % width));
                    int dy = Math.Abs((indices[i] / width) - (indices[j] / width));
                    AssertTrue(Math.Max(dx, dy) >= 5, $"sites {i} and {j} are closer than the separation");
                }
            }

            // A max-count lower than the number of peaks must be honoured.
            int capped = SuitabilityScoring.FindTopSites(scores, width, height, 5, 2, indices, siteScores, out _);
            AssertEqual(2, capped, 0, "max site count must cap the result");

            // A separation wide enough to span the grid admits exactly one site.
            int single = SuitabilityScoring.FindTopSites(scores, width, height, 100, 8, indices, siteScores, out _);
            AssertEqual(1, single, 0, "a huge separation must collapse to one site");
        }

        private static void TopSitesDescending()
        {
            const int width = 30;
            var scores = new float[width * 3];
            scores[1] = 3f;
            scores[10] = 9f;
            scores[20] = 6f;

            var indices = new int[4];
            var siteScores = new float[4];
            int found = SuitabilityScoring.FindTopSites(scores, width, 3, 2, 4, indices, siteScores, out _);
            AssertEqual(3, found, 0, "three isolated peaks");
            AssertEqual(9f, siteScores[0], 0f, "best first");
            AssertEqual(6f, siteScores[1], 0f, "second");
            AssertEqual(3f, siteScores[2], 0f, "third");
        }

        private static void TopSitesIgnoresEmptyField()
        {
            var scores = new float[100];
            var indices = new int[4];
            var siteScores = new float[4];
            AssertEqual(0, SuitabilityScoring.FindTopSites(scores, 10, 10, 2, 4, indices, siteScores, out _), 0, "all-zero field");

            for (int i = 0; i < scores.Length; i++)
            {
                scores[i] = -1f;
            }
            AssertEqual(0, SuitabilityScoring.FindTopSites(scores, 10, 10, 2, 4, indices, siteScores, out _), 0, "all-negative field");
        }

        private static void ExactSitesBeatGreedyCounterexample()
        {
            // 5 _ _ 8 _ _ 5 on a 9x3 grid, separation 4: greedy takes the 8 and can
            // add nothing; the two 5s are mutually feasible and sum to 10.
            const int width = 9;
            const int height = 3;
            var scores = new float[width * height];
            scores[1 + width] = 5f;
            scores[4 + width] = 8f;
            scores[7 + width] = 5f;

            var greedyIndices = new int[2];
            var greedyScores = new float[2];
            int greedy = SuitabilityScoring.FindTopSites(scores, width, height, 4, 2, greedyIndices, greedyScores, out _);
            AssertEqual(1, greedy, 0, "greedy is stuck with the single 8");

            ExactSiteSolution exact = SuitabilityExactSites.Solve(scores, width, height, 4, 2, SuitabilityExactSites.DefaultNodeBudget);
            AssertTrue(exact.Optimal, "search closes on a three-candidate field");
            AssertTrue(exact.WeightsExact, "integer weights lose nothing on small integers");
            AssertEqual(2, exact.Count, 0, "both fives are chosen");
            AssertEqual(1 + width, exact.Indices[0], 0, "ranked by score then index: left five first");
            AssertEqual(7 + width, exact.Indices[1], 0, "right five second");
            AssertEqual(10f, exact.Scores[0] + exact.Scores[1], 0f, "objective is 10");
            AssertEqual(3, exact.Candidates, 0, "three local maxima");
        }

        private static void ExactSitesMatchBruteForce()
        {
            // Small random fields with integer scores, so a brute-force sum over every
            // feasible subset is exact and comparable to the solver's integer objective.
            uint state = 20260904u;
            for (int trial = 0; trial < 40; trial++)
            {
                int width = 6 + trial % 5;
                int height = 5 + trial % 4;
                int separation = 2 + trial % 3;
                int maxSites = 1 + trial % 5;
                var scores = new float[width * height];
                for (int i = 0; i < scores.Length; i++)
                {
                    state = unchecked(state * 1664525u + 1013904223u);
                    scores[i] = (state >> 24) < 96 ? (int)((state >> 8) % 50) : 0f;
                }

                ExactSiteSolution exact = SuitabilityExactSites.Solve(scores, width, height, separation, maxSites, SuitabilityExactSites.DefaultNodeBudget);
                AssertTrue(exact.Optimal, "small fields close within the budget");
                AssertTrue(exact.WeightsExact, "integer scores scale exactly");
                AssertTrue(exact.Count <= maxSites, "never more than K sites");
                AssertSitesFeasible(exact.Indices, exact.Count, width, separation);

                int count = SuitabilityScoring.CollectSiteCandidates(scores, width, height, out int[] candidates, out float[] candidateScores, out _);
                long best = BruteForceSites(candidates, candidateScores, count, width, separation, maxSites);
                long value = 0;
                for (int i = 0; i < exact.Count; i++)
                {
                    value += (long)exact.Scores[i];
                    AssertEqual(scores[exact.Indices[i]], exact.Scores[i], 0f, "reported score is the grid value");
                }

                AssertEqual((int)best, (int)value, 0, $"trial {trial}: brute force {best} vs solver {value}");
            }
        }

        private static long BruteForceSites(int[] candidates, float[] candidateScores, int count, int width, int separation, int maxSites)
        {
            long best = 0;
            var chosen = new int[Math.Max(1, maxSites)];
            Recurse(0, 0, 0);
            return best;

            void Recurse(int from, int depth, long value)
            {
                if (value > best)
                {
                    best = value;
                }

                if (depth == maxSites)
                {
                    return;
                }

                for (int i = from; i < count; i++)
                {
                    bool ok = true;
                    for (int j = 0; j < depth && ok; j++)
                    {
                        int dx = Math.Abs((candidates[i] % width) - (chosen[j] % width));
                        int dy = Math.Abs((candidates[i] / width) - (chosen[j] / width));
                        ok = Math.Max(dx, dy) >= separation;
                    }

                    if (ok)
                    {
                        chosen[depth] = candidates[i];
                        Recurse(i + 1, depth + 1, value + (long)candidateScores[i]);
                    }
                }
            }
        }

        private static void AssertSitesFeasible(int[] indices, int count, int width, int separation)
        {
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    int dx = Math.Abs((indices[i] % width) - (indices[j] % width));
                    int dy = Math.Abs((indices[i] / width) - (indices[j] / width));
                    AssertTrue(Math.Max(dx, dy) >= separation, "chosen sites respect the separation");
                }
            }
        }

        private static void ExactSitesBoundWhenExhausted()
        {
            // The two-fives field with a budget of one node: the root bound (13) beats
            // the greedy incumbent (8), so the search starts, expands the 8, and is
            // stopped before it can try the fives. It must then report the incumbent
            // with the unexplored branch's bound (10) as ceiling — and the closed run's
            // optimum has to sit inside that interval.
            const int width = 9;
            const int height = 3;
            var scores = new float[width * height];
            scores[1 + width] = 5f;
            scores[4 + width] = 8f;
            scores[7 + width] = 5f;

            ExactSiteSolution starved = SuitabilityExactSites.Solve(scores, width, height, 4, 2, 1);
            AssertTrue(!starved.Optimal, "one node cannot close the counterexample");
            AssertEqual(1, starved.Count, 0, "falls back to the greedy incumbent");
            AssertEqual(8f, starved.Scores[0], 0f, "the incumbent is the 8");
            AssertTrue(starved.UpperBound > starved.Value, "the ceiling admits a better set");

            ExactSiteSolution closed = SuitabilityExactSites.Solve(scores, width, height, 4, 2, SuitabilityExactSites.DefaultNodeBudget);
            AssertTrue(closed.Optimal, "closes with the full budget");
            AssertEqual(starved.ScaleShift, closed.ScaleShift, 0, "same field, same scaling");
            AssertTrue(closed.Value <= starved.UpperBound, "the true optimum sits under the starved run's ceiling");
            AssertTrue(closed.Value > starved.Value, "the true optimum beats the incumbent here");
            AssertTrue(closed.Value == starved.UpperBound, "on this field the open bound is tight");

            // A dense 40x40 field closes within the default budget, and whatever a
            // starved run reports must bracket that optimum and beat plain greedy.
            const int denseWidth = 40;
            const int denseHeight = 40;
            var dense = new float[denseWidth * denseHeight];
            uint state = 7u;
            for (int i = 0; i < dense.Length; i++)
            {
                state = unchecked(state * 1664525u + 1013904223u);
                dense[i] = 1 + (int)((state >> 8) % 1000);
            }

            ExactSiteSolution denseStarved = SuitabilityExactSites.Solve(dense, denseWidth, denseHeight, 3, 8, 1);
            ExactSiteSolution denseClosed = SuitabilityExactSites.Solve(dense, denseWidth, denseHeight, 3, 8, SuitabilityExactSites.DefaultNodeBudget);
            AssertTrue(denseClosed.Optimal, "the full budget closes a 40x40 field");
            AssertSitesFeasible(denseClosed.Indices, denseClosed.Count, denseWidth, 3);
            AssertSitesFeasible(denseStarved.Indices, denseStarved.Count, denseWidth, 3);
            AssertTrue(denseClosed.Value <= denseStarved.UpperBound, "starved ceiling holds the optimum");
            AssertTrue(denseClosed.Value >= denseStarved.Value, "starved incumbent never beats the optimum");

            var greedyIndices = new int[8];
            var greedyScores = new float[8];
            int greedy = SuitabilityScoring.FindTopSites(dense, denseWidth, denseHeight, 3, 8, greedyIndices, greedyScores, out _);
            float greedySum = 0f;
            for (int i = 0; i < greedy; i++)
            {
                greedySum += greedyScores[i];
            }

            float starvedSum = 0f;
            for (int i = 0; i < denseStarved.Count; i++)
            {
                starvedSum += denseStarved.Scores[i];
            }

            AssertTrue(starvedSum >= greedySum, "the incumbent is at least the greedy ranking");
        }

        private static void ExactSitesRankingAndEmptyField()
        {
            const int width = 12;
            const int height = 7;
            var scores = new float[width * height];
            // Three equal peaks and one lesser four rows down: ranking is score desc,
            // then index asc.
            scores[10 + width] = 4f;
            scores[1 + width] = 4f;
            scores[5 + width] = 4f;
            scores[5 + 5 * width] = 1f;
            ExactSiteSolution exact = SuitabilityExactSites.Solve(scores, width, height, 3, 4, SuitabilityExactSites.DefaultNodeBudget);
            AssertTrue(exact.Optimal, "closes");
            AssertEqual(4, exact.Count, 0, "all four peaks fit");
            AssertEqual(1 + width, exact.Indices[0], 0, "lowest index among equal scores first");
            AssertEqual(5 + width, exact.Indices[1], 0, "then the next index");
            AssertEqual(10 + width, exact.Indices[2], 0, "then the last equal score");
            AssertEqual(5 + 5 * width, exact.Indices[3], 0, "the lesser peak last");

            ExactSiteSolution empty = SuitabilityExactSites.Solve(new float[width * height], width, height, 3, 4, SuitabilityExactSites.DefaultNodeBudget);
            AssertEqual(0, empty.Count, 0, "nothing to choose from");
            AssertTrue(empty.Optimal, "an empty field is trivially solved");
            AssertEqual(0, (int)empty.Nodes, 0, "no search on an empty field");
        }

        private static void ExactSitesWeightScaling()
        {
            // Scores spanning 2^20 stay exact after scaling; a spread beyond the long's
            // headroom is reported as inexact rather than silently rounded.
            const int width = 9;
            const int height = 3;
            var scores = new float[width * height];
            scores[1 + width] = 1048576f;
            scores[4 + width] = 0.75f;
            scores[7 + width] = 1.0f;
            ExactSiteSolution exact = SuitabilityExactSites.Solve(scores, width, height, 3, 3, SuitabilityExactSites.DefaultNodeBudget);
            AssertTrue(exact.WeightsExact, "a 2^20 spread is exact");
            AssertEqual(3, exact.Count, 0, "all three are compatible at separation 3");
            AssertEqual(1048576f, exact.Scores[0], 0f, "ranked by score");
            AssertEqual(1.0f, exact.Scores[1], 0f, "then 1.0");
            AssertEqual(0.75f, exact.Scores[2], 0f, "then 0.75");

            scores[4 + width] = 1e-20f;
            ExactSiteSolution wide = SuitabilityExactSites.Solve(scores, width, height, 3, 3, SuitabilityExactSites.DefaultNodeBudget);
            AssertTrue(!wide.WeightsExact, "a 1e26 spread floors the tiny score");
            AssertTrue(wide.Optimal, "still closes");
            AssertSitesFeasible(wide.Indices, wide.Count, width, 3);
        }

        private static void NetworkSitesBeatGreedy()
        {
            // Nodes 0-1-2 one minute apart, scores 5 / 8 / 5, spacing 90 s: the 8 in the
            // middle conflicts with both fives (60 s < 90 s) while the fives are 120 s
            // apart and compatible. Greedy takes the 8; the optimum is the two fives.
            WalkGraph graph = LineGraph(3, 72f);
            var nodes = new[] { 0, 1, 2 };
            var scores = new[] { 5f, 8f, 5f };
            ExactSiteSolution exact = SuitabilityExactSites.SolveOnNetwork(graph, nodes, scores, 3, 90000, 2, SuitabilityExactSites.DefaultNodeBudget);
            AssertTrue(exact.Optimal, "closes");
            AssertEqual(2, exact.Count, 0, "both fives");
            AssertEqual(0, exact.Indices[0], 0, "ranked by score then node index");
            AssertEqual(2, exact.Indices[1], 0, "the other five");

            // Spacing 60 s: the middle node is exactly a minute away, which does NOT
            // conflict (strictly below), so all three fit.
            ExactSiteSolution loose = SuitabilityExactSites.SolveOnNetwork(graph, nodes, scores, 3, 60000, 3, SuitabilityExactSites.DefaultNodeBudget);
            AssertEqual(3, loose.Count, 0, "a walk equal to the spacing is allowed");

            // Budget of one node: greedy incumbent with a ceiling that holds the optimum.
            ExactSiteSolution starved = SuitabilityExactSites.SolveOnNetwork(graph, nodes, scores, 3, 90000, 2, 1);
            AssertTrue(!starved.Optimal, "one node cannot close it");
            AssertEqual(8f, starved.Scores[0], 0f, "falls back to the greedy 8");
            AssertTrue(exact.Value <= starved.UpperBound, "ceiling holds the optimum");
        }

        private static void NetworkSitesMatchBruteForce()
        {
            // Random small graphs: a line with a few random shortcuts, random scores,
            // random spacing. Conflicts are recomputed here by a plain all-pairs
            // Floyd-Warshall so the solver's Dijkstra-based lists are cross-checked too.
            uint state = 99u;
            for (int trial = 0; trial < 30; trial++)
            {
                int n = 6 + trial % 6;
                var x = new float[n];
                var z = new float[n];
                var a = new List<int>();
                var b = new List<int>();
                var len = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    x[i] = i * 60f;
                    if (i > 0)
                    {
                        a.Add(i - 1);
                        b.Add(i);
                        state = unchecked(state * 1664525u + 1013904223u);
                        len.Add(30f + (state >> 8) % 90);
                    }
                }

                for (int extra = 0; extra < 2; extra++)
                {
                    state = unchecked(state * 1664525u + 1013904223u);
                    int u = (int)((state >> 8) % n);
                    state = unchecked(state * 1664525u + 1013904223u);
                    int v = (int)((state >> 8) % n);
                    if (u != v)
                    {
                        a.Add(u);
                        b.Add(v);
                        len.Add(40f + (state >> 20) % 200);
                    }
                }

                WalkGraph graph = WalkGraph.Build(x, z, a.ToArray(), b.ToArray(), len.ToArray(), a.Count);
                var candidates = new int[n];
                var scores = new float[n];
                for (int i = 0; i < n; i++)
                {
                    candidates[i] = i;
                    state = unchecked(state * 1664525u + 1013904223u);
                    scores[i] = 1 + (int)((state >> 8) % 40);
                }

                state = unchecked(state * 1664525u + 1013904223u);
                int separation = 40000 + (int)((state >> 8) % 120000);
                int maxSites = 1 + trial % 4;
                long[][] dist = AllPairsMs(graph);

                long best = 0;
                for (int mask = 0; mask < (1 << n); mask++)
                {
                    int bits = 0;
                    long value = 0;
                    bool ok = true;
                    for (int i = 0; i < n && ok; i++)
                    {
                        if ((mask & (1 << i)) == 0)
                        {
                            continue;
                        }

                        bits++;
                        value += (long)scores[i];
                        for (int j = i + 1; j < n; j++)
                        {
                            if ((mask & (1 << j)) != 0 && dist[i][j] < separation)
                            {
                                ok = false;
                                break;
                            }
                        }
                    }

                    if (ok && bits <= maxSites)
                    {
                        best = Math.Max(best, value);
                    }
                }

                ExactSiteSolution exact = SuitabilityExactSites.SolveOnNetwork(graph, candidates, scores, n, separation, maxSites, SuitabilityExactSites.DefaultNodeBudget);
                AssertTrue(exact.Optimal, "small graphs close");
                long value2 = 0;
                for (int i = 0; i < exact.Count; i++)
                {
                    value2 += (long)exact.Scores[i];
                    for (int j = i + 1; j < exact.Count; j++)
                    {
                        AssertTrue(dist[exact.Indices[i]][exact.Indices[j]] >= separation, $"trial {trial}: chosen nodes respect the spacing");
                    }
                }

                AssertTrue(exact.Count <= maxSites, "never more than K");
                AssertEqual((int)best, (int)value2, 0, $"trial {trial}: brute force {best} vs solver {value2}");
            }
        }

        private static long[][] AllPairsMs(WalkGraph graph)
        {
            int n = graph.NodeCount;
            var dist = new long[n][];
            for (int i = 0; i < n; i++)
            {
                dist[i] = new long[n];
                for (int j = 0; j < n; j++)
                {
                    dist[i][j] = i == j ? 0 : long.MaxValue / 4;
                }
            }

            for (int e = 0; e < graph.EdgeMs.Length; e++)
            {
                int u = graph.EdgeA[e];
                int v = graph.EdgeB[e];
                dist[u][v] = Math.Min(dist[u][v], graph.EdgeMs[e]);
                dist[v][u] = Math.Min(dist[v][u], graph.EdgeMs[e]);
            }

            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        dist[i][j] = Math.Min(dist[i][j], dist[i][k] + dist[k][j]);
                    }
                }
            }

            return dist;
        }

        // Nodes on a plane; arcs given as (from, to, metres, speed); headings from the
        // straight chord so tests can reason about angles directly.
        private static DirectedRoadGraph DirectedGraph(float[] x, float[] z, (int from, int to, float metres, float speed)[] arcs, int[]? edgeOf = null, float turnSecondsPerRadian = 2f)
        {
            int n = arcs.Length;
            var from = new int[n];
            var to = new int[n];
            var edge = new int[n];
            var ms = new int[n];
            var odx = new float[n];
            var odz = new float[n];
            for (int a = 0; a < n; a++)
            {
                from[a] = arcs[a].from;
                to[a] = arcs[a].to;
                edge[a] = edgeOf is null ? a : edgeOf[a];
                ms[a] = DirectedRoadGraph.ArcMilliseconds(arcs[a].metres, arcs[a].speed);
                float dx = x[arcs[a].to] - x[arcs[a].from];
                float dz = z[arcs[a].to] - z[arcs[a].from];
                float len = (float)Math.Sqrt((dx * dx) + (dz * dz));
                odx[a] = dx / len;
                odz[a] = dz / len;
            }

            return DirectedRoadGraph.Build(x, z, from, to, edge, ms, odx, odz, odx, odz, DirectedRoadGraph.TurnTable(turnSecondsPerRadian), n);
        }

        private static void DirectedTurnClassesAndTimes()
        {
            AssertEqual(DirectedRoadGraph.Straight, DirectedRoadGraph.TurnClassOf(1.0), 0, "dead ahead");
            AssertEqual(DirectedRoadGraph.Straight, DirectedRoadGraph.TurnClassOf(DirectedRoadGraph.CosGentle), 0, "15° is still straight (boundary inclusive)");
            AssertEqual(DirectedRoadGraph.Gentle, DirectedRoadGraph.TurnClassOf(0.9), 0, "25° is gentle");
            AssertEqual(DirectedRoadGraph.Turn, DirectedRoadGraph.TurnClassOf(0.0), 0, "90° is a turn");
            AssertEqual(DirectedRoadGraph.Sharp, DirectedRoadGraph.TurnClassOf(-0.8), 0, "143° is sharp");
            AssertEqual(DirectedRoadGraph.UTurn, DirectedRoadGraph.TurnClassOf(-1.0), 0, "reversal is a U-turn");

            AssertEqual(10000, DirectedRoadGraph.ArcMilliseconds(139f, 13.9f), 0, "139 m at 13.9 m/s is 10 s");
            AssertEqual(1, DirectedRoadGraph.ArcMilliseconds(0f, 13.9f), 0, "a zero-length arc still costs a millisecond");
            AssertEqual(20000, DirectedRoadGraph.ArcMilliseconds(2f, 0f), 0, "a zero speed limit is floored at 0.1 m/s");

            int[] table = DirectedRoadGraph.TurnTable(2f);
            AssertEqual(0, table[0], 0, "straight costs nothing");
            AssertEqual(1047, table[1], 0, "2 s/rad · 30°");
            AssertEqual(3142, table[2], 0, "2 s/rad · 90°");
            AssertEqual(4887, table[3], 0, "2 s/rad · 140°");
            AssertEqual(6283, table[4], 0, "2 s/rad · 180°");
        }

        private static void DirectedOneWayStreet()
        {
            // 0 -> 1 -> 2 with the return 2 -> 1 only: node 0 is unreachable from 2.
            var x = new[] { 0f, 100f, 200f };
            var z = new float[3];
            DirectedRoadGraph graph = DirectedGraph(x, z, new[] { (0, 1, 100f, 10f), (1, 2, 100f, 10f), (2, 1, 100f, 10f) });
            var dijkstra = new DirectedDijkstra(graph.ArcCount);
            dijkstra.Run(graph, 0, 1_000_000);
            AssertTrue(dijkstra.TimeTo(graph, 2) == 20000, "0 to 2 is two ten-second arcs going straight on");
            dijkstra.Run(graph, 2, 1_000_000);
            AssertTrue(dijkstra.TimeTo(graph, 1) == 10000, "2 to 1 is allowed");
            AssertTrue(dijkstra.TimeTo(graph, 0) == DirectedDijkstra.Unreached, "2 to 0 would need the missing 1 -> 0 arc");

            var fresh = new DirectedDijkstra(graph.ArcCount);
            fresh.Run(graph, 2, 1_000_000);
            for (int a = 0; a < graph.ArcCount; a++)
            {
                AssertTrue(fresh.Dist[a] == dijkstra.Dist[a], "reused workspace equals a fresh one");
            }
        }

        private static void DirectedTurnCostsSteer()
        {
            // From S=(0,0) to T=(100,100): the fast route goes east then north (200 m at
            // 10 m/s, one 90° turn); the slow route bends gently through P=(30,70)
            // (152.3 m at 5 m/s, one gentle bend). Cheap turns favour the fast route,
            // dear turns the gentle one — which is what a bus planner has to weigh
            // between an avenue and a side street.
            var x = new[] { 0f, 100f, 100f, 30f };
            var z = new[] { 0f, 0f, 100f, 70f };
            float leg = (float)Math.Sqrt((30f * 30f) + (70f * 70f));
            var arcs = new[]
            {
                (0, 1, 100f, 10f),   // east
                (1, 2, 100f, 10f),   // north: 90° turn
                (0, 3, leg, 5f),     // gently north-north-east
                (3, 2, leg, 5f),     // gently east-north-east: bend of about 46°... see below
            };
            DirectedRoadGraph cheap = DirectedGraph(x, z, arcs, turnSecondsPerRadian: 2f);
            int bend = cheap.TurnClass(2, 3);
            AssertEqual(DirectedRoadGraph.Gentle, bend, 0, "the slow route's bend is gentle (headings 23° either side of the diagonal)");
            var dijkstra = new DirectedDijkstra(cheap.ArcCount);
            dijkstra.Run(cheap, 0, 1_000_000);
            long fast = 20000 + cheap.TurnMs[DirectedRoadGraph.Turn];
            long slow = (2L * DirectedRoadGraph.ArcMilliseconds(leg, 5f)) + cheap.TurnMs[DirectedRoadGraph.Gentle];
            AssertTrue(fast < slow, "with cheap turns the fast route is quicker");
            AssertTrue(dijkstra.TimeTo(cheap, 2) == fast, "and is what the search returns");
            var trace = new List<int>();
            AssertTrue(dijkstra.TraceArcs(cheap, 2, trace) && trace.Count == 2 && trace[0] == 0 && trace[1] == 1, "trace follows arcs 0 then 1");

            DirectedRoadGraph dear = DirectedGraph(x, z, arcs, turnSecondsPerRadian: 20f);
            dijkstra = new DirectedDijkstra(dear.ArcCount);
            dijkstra.Run(dear, 0, 1_000_000);
            long fastDear = 20000 + dear.TurnMs[DirectedRoadGraph.Turn];
            long slowDear = (2L * DirectedRoadGraph.ArcMilliseconds(leg, 5f)) + dear.TurnMs[DirectedRoadGraph.Gentle];
            AssertTrue(slowDear < fastDear, "with dear turns the gentle route is quicker");
            AssertTrue(dijkstra.TimeTo(dear, 2) == slowDear, "and the search switches to it");
        }

        private static void DirectedMatchesEnumeration()
        {
            uint state = 4242u;
            for (int trial = 0; trial < 25; trial++)
            {
                int n = 4 + trial % 3;
                var x = new float[n];
                var z = new float[n];
                for (int i = 0; i < n; i++)
                {
                    state = unchecked(state * 1664525u + 1013904223u);
                    x[i] = (state >> 8) % 500;
                    state = unchecked(state * 1664525u + 1013904223u);
                    z[i] = (state >> 8) % 500;
                }

                var arcs = new List<(int, int, float, float)>();
                for (int a = 0; a < n; a++)
                {
                    for (int b = 0; b < n; b++)
                    {
                        state = unchecked(state * 1664525u + 1013904223u);
                        if (a != b && (state >> 8) % 3 == 0 && (x[a] != x[b] || z[a] != z[b]))
                        {
                            float metres = (float)Math.Sqrt(((x[a] - x[b]) * (x[a] - x[b])) + ((z[a] - z[b]) * (z[a] - z[b])));
                            arcs.Add((a, b, metres, 8f + ((state >> 12) % 10)));
                        }
                    }
                }

                if (arcs.Count == 0)
                {
                    continue;
                }

                DirectedRoadGraph graph = DirectedGraph(x, z, arcs.ToArray(), turnSecondsPerRadian: 1.5f);
                var dijkstra = new DirectedDijkstra(graph.ArcCount);
                for (int source = 0; source < n; source++)
                {
                    dijkstra.Run(graph, source, long.MaxValue / 4);
                    for (int target = 0; target < n; target++)
                    {
                        if (target == source)
                        {
                            continue;
                        }

                        long best = EnumerateBest(graph, source, target);
                        AssertTrue(dijkstra.TimeTo(graph, target) == best, $"trial {trial}: {source}->{target} dijkstra {dijkstra.TimeTo(graph, target)} vs enumeration {best}");
                    }
                }
            }
        }

        // Every simple arc path from source to target, with turn costs.
        private static long EnumerateBest(DirectedRoadGraph graph, int source, int target)
        {
            long best = DirectedDijkstra.Unreached;
            var visited = new bool[graph.NodeCount];
            visited[source] = true;
            Recurse(source, -1, 0);
            return best;

            void Recurse(int node, int lastArc, long cost)
            {
                if (node == target)
                {
                    best = Math.Min(best, cost);
                    return;
                }

                for (int slot = graph.OutOffsets[node]; slot < graph.OutOffsets[node + 1]; slot++)
                {
                    int arc = graph.OutArcs[slot];
                    int next = graph.ArcTo[arc];
                    if (visited[next])
                    {
                        continue;
                    }

                    long step = graph.ArcMs[arc] + (lastArc < 0 ? 0 : graph.TurnMs[graph.TurnClass(lastArc, arc)]);
                    visited[next] = true;
                    Recurse(next, arc, cost + step);
                    visited[next] = false;
                }
            }
        }

        private static void DirectedFlowAssignment()
        {
            // Square 0-1-2-3 with a one-way loop 0->1->2->3->0 plus a two-way spur 0<->4.
            // Undirected edge ids: 0:(0,1) 1:(1,2) 2:(2,3) 3:(3,0) 4:(0,4).
            var x = new[] { 0f, 100f, 100f, 0f, -100f };
            var z = new[] { 0f, 0f, 100f, 100f, 0f };
            var arcs = new[] { (0, 1, 100f, 10f), (1, 2, 100f, 10f), (2, 3, 100f, 10f), (3, 0, 100f, 10f), (0, 4, 100f, 10f), (4, 0, 100f, 10f) };
            DirectedRoadGraph graph = DirectedGraph(x, z, arcs, edgeOf: new[] { 0, 1, 2, 3, 4, 4 });
            var dijkstra = new DirectedDijkstra(graph.ArcCount);
            var flows = new List<ZoneFlowLike>
            {
                new ZoneFlowLike { Origin = 0, Destination = 1, Weight = 5f },   // 0->1 direct
                new ZoneFlowLike { Origin = 1, Destination = 0, Weight = 7f },   // must loop 1->2->3->0
                new ZoneFlowLike { Origin = 4, Destination = 0, Weight = 1f },
            };
            var zoneNodes = new[] { 0, 1, 2, 3, 4 };
            var edgeFlow = new float[5];
            var arcFlow = new float[graph.ArcCount];
            int assigned = SuitabilityDirectedRoads.AssignFlow(graph, dijkstra, flows, zoneNodes, long.MaxValue / 4, edgeFlow, arcFlow, out float weight);
            AssertEqual(3, assigned, 0, "all three flows have a directed route");
            AssertEqual(13f, weight, 0f, "assigned weight");
            AssertEqual(5f, edgeFlow[0], 0f, "street 0-1 carries only the 0->1 flow");
            AssertEqual(7f, edgeFlow[1], 0f, "the return loop carries the 1->0 flow");
            AssertEqual(7f, edgeFlow[2], 0f, "...along 2-3");
            AssertEqual(7f, edgeFlow[3], 0f, "...and 3-0");
            AssertEqual(1f, edgeFlow[4], 0f, "the spur carries the 4->0 flow");
            AssertEqual(0f, arcFlow[4], 0f, "arc 0->4 is unused");
            AssertEqual(1f, arcFlow[5], 0f, "arc 4->0 carries it");
        }

        private static void DirectedPointLegs()
        {
            // A straight two-way street 0 -> 1 -> 2 (100 m each at 10 m/s) with a one-way
            // side street 1 -> 3 north. Stops sit mid-block.
            var x = new[] { 0f, 100f, 200f, 100f };
            var z = new[] { 0f, 0f, 0f, 100f };
            var arcs = new[] { (0, 1, 100f, 10f), (1, 0, 100f, 10f), (1, 2, 100f, 10f), (2, 1, 100f, 10f), (1, 3, 100f, 10f) };
            DirectedRoadGraph graph = DirectedGraph(x, z, arcs, edgeOf: new[] { 0, 0, 1, 1, 2 });
            var dijkstra = new DirectedDijkstra(graph.ArcCount);

            int arc = graph.NearestArc(25f, 3f, 20.0, out double t, out double metres);
            AssertEqual(0, arc, 0, "the point projects onto the first street (lowest arc index of the pair)");
            AssertEqual(0.25f, (float)t, 1e-6f, "a quarter of the way along");
            AssertEqual(3f, (float)metres, 1e-5f, "three metres off the chord");
            AssertEqual(-1, graph.NearestArc(25f, 50f, 20.0, out _, out _), 0, "fifty metres off is off the network");
            AssertEqual(2500, graph.PositionMs(0, 0.25), 0, "a quarter of ten seconds");

            // Same street, in travel order: 25 m -> 75 m is five seconds, no turns.
            long same = RoadLegs.PointToPointMs(graph, dijkstra, 25f, 0f, 75f, 0f, 20.0, 1_000_000, out RoadLeg leg);
            AssertTrue(same == 5000, $"same-arc leg is the position difference, got {same}");
            AssertTrue(leg.SameArc && leg.FromArc == 0 && leg.ToArc == 0, "recorded as a same-arc leg");

            // Backwards along the two-way street: 75 m -> 25 m uses the reverse arc (1 -> 0).
            long back = RoadLegs.PointToPointMs(graph, dijkstra, 75f, 0f, 25f, 0f, 20.0, 1_000_000, out leg);
            AssertTrue(back == 5000 && leg.FromArc == 1 && leg.ToArc == 1, $"the reverse arc carries the return, got {back} via {leg.FromArc}");

            // Mid-block to the side street: 50 m along 0->1 (5 s to the node), 90° turn,
            // 30 m up 1->3 (3 s).
            long turn = RoadLegs.PointToPointMs(graph, dijkstra, 50f, 0f, 100f, 30f, 20.0, 1_000_000, out leg);
            long expected = 5000 + graph.TurnMs[DirectedRoadGraph.Turn] + 3000;
            AssertTrue(turn == expected, $"mid-block start, turn, mid-block end: {turn} vs {expected}");
            AssertTrue(leg.FromArc == 0 && leg.ToArc == 4 && leg.StartMs == 5000 && leg.EndMs == 3000, "leg detail is recorded");

            // From the one-way side street back down to the main street: 1 -> 3 has no
            // reverse, so a point on it can only be a destination, never an origin.
            long stuck = RoadLegs.PointToPointMs(graph, dijkstra, 100f, 30f, 50f, 0f, 20.0, 1_000_000, out _);
            AssertTrue(stuck == DirectedDijkstra.Unreached, "no arc leads away from the one-way side street's point");
        }

        private static void EquityCoverage()
        {
            // Line 0..7 one minute apart; one served stop at node 1 (access 0), horizon 10 min.
            WalkGraph graph = LineGraph(8, 72f);
            var dijkstra = new IntDijkstra(graph.NodeCount);
            int[] served = SuitabilityEquity.ServedWalkMs(graph, dijkstra, new[] { 1 }, new[] { 0 }, 1, 600000);
            AssertEqual(0, served[1], 0, "the stop's node is served at once");
            AssertEqual(60000, served[0], 0, "one minute to node 0");
            AssertEqual(360000, served[7], 0, "six minutes to node 7");

            // Trips: 0->7 (both within 10 min), 0->7 with 5 min access at the destination
            // (6+5 = 11 min: not served), one with an off-network end, weights 2/1/1.
            var origin = new[] { 0, 0, 0 };
            var originAccess = new[] { 0, 0, 0 };
            var dest = new[] { 7, 7, -1 };
            var destAccess = new[] { 0, 300000, 0 };
            var weight = new[] { 2f, 1f, 1f };
            CoverageReport report = SuitabilityEquity.Coverage(served, 600000, origin, originAccess, dest, destAccess, weight, 3);
            AssertEqual(1, report.TripsCovered, 0, "only the first journey is served at both ends");
            AssertEqual(1, report.TripsOffNetwork, 0, "one journey has an end off the network");
            AssertEqual(0.5f, report.Share, 0f, "2 of 4 weight covered");
            AssertTrue(SuitabilityEquity.EndServed(served, 7, 240000, 600000), "6 + 4 minutes fits the horizon exactly");
            AssertTrue(!SuitabilityEquity.EndServed(served, 7, 240001, 600000), "one millisecond over does not");

            // Adding a stop at node 7 covers the second journey as well (5 min access ≤ 10).
            int[] merged = SuitabilityEquity.WithStops(graph, dijkstra, served, new[] { 7 }, new[] { 0 }, 1, 600000);
            AssertEqual(0, merged[7], 0, "node 7 is now a stop");
            AssertEqual(0, served[7] == 360000 ? 0 : 1, 0, "the original field is untouched");
            CoverageReport after = SuitabilityEquity.Coverage(merged, 600000, origin, originAccess, dest, destAccess, weight, 3);
            AssertEqual(0.75f, after.Share, 0f, "3 of 4 weight covered with the new stop");
        }

        private static void EquityGiniAndUtilisation()
        {
            AssertEqual(0f, (float)SuitabilityEquity.Gini(new[] { 3.0, 3.0, 3.0 }, new[] { 1f, 1f, 1f }, 3), 0f, "equal values: Gini 0");
            AssertEqual(0f, (float)SuitabilityEquity.Gini(new[] { 0.0, 0.0 }, new[] { 1f, 1f }, 2), 0f, "all zero: Gini 0 by convention");
            double concentrated = SuitabilityEquity.Gini(new[] { 0.0, 0.0, 0.0, 10.0 }, new[] { 1f, 1f, 1f, 1f }, 4);
            AssertEqual(0.75f, (float)concentrated, 1e-6f, "one of four holds everything: (n-1)/n");
            double weighted = SuitabilityEquity.Gini(new[] { 1.0, 2.0 }, new[] { 3f, 1f }, 2);
            double unweighted = SuitabilityEquity.Gini(new[] { 1.0, 1.0, 1.0, 2.0 }, new[] { 1f, 1f, 1f, 1f }, 4);
            AssertEqual((float)unweighted, (float)weighted, 1e-9f, "weights behave like repeated observations");

            // 179 journeys a game day on a 400 s headway with 80-seat buses: 358
            // boardings against (4369.07 / 400) runs × 2 directions × 80 seats.
            float day = 262144f / 60f;
            AssertEqual(day, SuitabilityEquity.MovementSecondsPerGameDay, 1e-3f, "a game day is 262144 ticks at 60 per second");
            float utilisation = SuitabilityEquity.Utilisation(179f, 400f, 80f);
            AssertEqual(358f / (day / 400f * 2f * 80f), utilisation, 1e-6f, "boardings over seats offered in the day");
            AssertTrue(utilisation is > 0.20f and < 0.21f, "Valmare's best candidate sits near 20 %");
            AssertEqual(0f, SuitabilityEquity.Utilisation(1000f, 0f, 70f), 0f, "no headway, no utilisation");
        }

        private static LineCandidate Line(float wait, float speed, params (float x, float z)[] stops)
        {
            var line = new LineCandidate { StopX = new float[stops.Length], StopZ = new float[stops.Length], ExpectedWait = wait, SpeedMetresPerSecond = speed, HeadwaySeconds = wait * 2f, VehicleCapacity = 80f };
            for (int i = 0; i < stops.Length; i++)
            {
                line.StopX[i] = stops[i].x;
                line.StopZ[i] = stops[i].z;
            }

            return line;
        }

        private static LineSetProblem FeederProblem()
        {
            // Zones A (0,0), B (6000,0), C (0,3000). Trunk A-B is fast; feeder C-A brings
            // C's journeys to the trunk. Journeys: A->B 40/day, C->B 40/day, C->A 10/day.
            var problem = new LineSetProblem
            {
                PairOx = new[] { 0f, 0f, 0f },
                PairOz = new[] { 0f, 3000f, 3000f },
                PairDx = new[] { 6000f, 6000f, 0f },
                PairDz = new[] { 0f, 0f, 0f },
                PairWeight = new[] { 40f, 40f, 10f },
                PairCount = 3,
                WalkRadius = 216f,
                BoardPenaltySeconds = 5f,
                MaxTravelSeconds = 3600f,
                ZoneReachMetres = 432f,
                MaxLines = 2,
                MovementSecondsPerDay = 262144f / 60f,
                DuplicateShare = 0.5f,
            };
            problem.Candidates.Add(Line(150f, 20f, (0f, 0f), (6000f, 0f)));          // trunk
            problem.Candidates.Add(Line(200f, 10f, (0f, 3000f), (0f, 0f)));          // feeder
            problem.Candidates.Add(Line(200f, 10f, (0f, 3000f), (0f, 2000f)));       // a stub going nowhere useful
            return problem;
        }

        private static void LineSetTrunkAndFeeder()
        {
            LineSetProblem problem = FeederProblem();
            float[] before = SuitabilityLineSet.Evaluate(problem, Array.Empty<int>(), 0, null).After;
            AssertEqual(6000f / 1.2f, before[0], 1e-3f, "with no lines, A->B is a walk");
            double trunk = SuitabilityLineSet.Evaluate(problem, new[] { 0 }, 1, before).TimeSaved;
            double feeder = SuitabilityLineSet.Evaluate(problem, new[] { 1 }, 1, before).TimeSaved;
            double both = SuitabilityLineSet.Evaluate(problem, new[] { 0, 1 }, 2, before).TimeSaved;
            AssertTrue(trunk > 0.0, "the trunk saves A->B riders time");
            AssertTrue(both > trunk && both > feeder, "adding a line never loses time (monotone)");
            AssertTrue(both > trunk + feeder, "trunk and feeder together save more than apart: C->B needs both");
            LineSetEvaluation withBoth = SuitabilityLineSet.Evaluate(problem, new[] { 0, 1 }, 2, before);
            AssertEqual(80f, (float)withBoth.Riders[0], 1e-3f, "A->B and C->B ride the trunk");
            AssertEqual(50f, (float)withBoth.Riders[1], 1e-3f, "C->B and C->A ride the feeder");
            AssertTrue(withBoth.WaitSeconds > 0.0 && withBoth.RideSeconds > 0.0, "the realism breakdown is filled");

            LineSetSolution solution = SuitabilityLineSet.Solve(problem, SuitabilityLineSet.DefaultNodeBudget);
            AssertTrue(solution.Optimal, "three candidates close at once");
            AssertEqual(2, solution.Count, 0, "two lines chosen");
            AssertTrue((solution.Chosen[0] == 0 && solution.Chosen[1] == 1) || (solution.Chosen[0] == 1 && solution.Chosen[1] == 0), "trunk and feeder are the pair");
            AssertEqual((float)both, (float)solution.TimeSaved, 1e-3f, "the set's value is reported");
        }

        // Evaluate runs one Dijkstra per origin zone, capped at the group's largest
        // `before`; the reference here is the definition — one uncapped search per pair
        // from its own zone node. Seeded random cities with overlapping origins, stops
        // close enough to walk between and lines that cross: after-times, riders and
        // the saved sum must agree bit for bit.
        private static void LineSetGroupedEqualsPerPair()
        {
            uint state = 20260905u;
            float Next(float max)
            {
                state = unchecked((state * 1664525u) + 1013904223u);
                return (state >> 8) / 16777216f * max;
            }

            for (int trial = 0; trial < 6; trial++)
            {
                const int zones = 6;
                var zx = new float[zones];
                var zz = new float[zones];
                for (int z = 0; z < zones; z++)
                {
                    zx[z] = (float)Math.Round(Next(4000f) / 250f) * 250f;
                    zz[z] = (float)Math.Round(Next(4000f) / 250f) * 250f;
                }

                const int pairs = 18;
                var problem = new LineSetProblem
                {
                    PairCount = pairs,
                    PairOx = new float[pairs],
                    PairOz = new float[pairs],
                    PairDx = new float[pairs],
                    PairDz = new float[pairs],
                    PairWeight = new float[pairs],
                    WalkRadius = 300f,
                    BoardPenaltySeconds = 5f,
                    MaxTravelSeconds = 3600f,
                    ZoneReachMetres = 500f,
                    MaxLines = 2,
                };
                for (int i = 0; i < pairs; i++)
                {
                    int o = (int)Next(zones);
                    int d = (int)Next(zones);
                    problem.PairOx[i] = zx[o];
                    problem.PairOz[i] = zz[o];
                    problem.PairDx[i] = zx[d];
                    problem.PairDz[i] = zz[d];
                    problem.PairWeight[i] = 1f + (float)Math.Floor(Next(40f));
                }

                for (int c = 0; c < 3; c++)
                {
                    int stops = 2 + (int)Next(3f);
                    var line = new LineCandidate { StopX = new float[stops], StopZ = new float[stops], ExpectedWait = 100f + Next(200f), SpeedMetresPerSecond = 8f + Next(10f), HeadwaySeconds = 300f, VehicleCapacity = 60f };
                    for (int k = 0; k < stops; k++)
                    {
                        int z = (int)Next(zones);
                        line.StopX[k] = zx[z] + Next(200f) - 100f;
                        line.StopZ[k] = zz[z] + Next(200f) - 100f;
                    }

                    problem.Candidates.Add(line);
                }

                int[][] sets = { Array.Empty<int>(), new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 0, 1 }, new[] { 1, 2 }, new[] { 0, 1, 2 } };
                float[]? before = null;
                float[]? referenceBefore = null;
                foreach (int[] set in sets)
                {
                    LineSetEvaluation grouped = SuitabilityLineSet.Evaluate(problem, set, set.Length, before);
                    PerPairReference(problem, set, referenceBefore, out float[] after, out double[] riders, out double saved);
                    for (int i = 0; i < pairs; i++)
                    {
                        AssertTrue(grouped.After[i] == after[i], $"trial {trial} set [{string.Join(",", set)}] pair {i}: after {grouped.After[i]} vs per-pair {after[i]}");
                    }

                    for (int c = 0; c < problem.Candidates.Count; c++)
                    {
                        AssertTrue(grouped.Riders[c] == riders[c], $"trial {trial} set [{string.Join(",", set)}] line {c}: riders {grouped.Riders[c]} vs {riders[c]}");
                    }

                    if (before is not null)
                    {
                        AssertTrue(grouped.TimeSaved == saved, $"trial {trial} set [{string.Join(",", set)}]: saved {grouped.TimeSaved} vs {saved}");
                    }

                    if (set.Length == 0)
                    {
                        before = grouped.After;
                        referenceBefore = after;
                    }
                }
            }
        }

        // The definition of the objective: two zone nodes per pair, one uncapped search
        // each (SuitabilityLineSet.Evaluate as first written).
        private static void PerPairReference(LineSetProblem problem, int[] chosen, float[]? before, out float[] after, out double[] riders, out double saved)
        {
            int stopCount = problem.BaseStopCount;
            foreach (int c in chosen)
            {
                stopCount += problem.Candidates[c].StopX.Length;
            }

            var stopX = new float[stopCount];
            var stopZ = new float[stopCount];
            Array.Copy(problem.BaseStopX, stopX, problem.BaseStopCount);
            Array.Copy(problem.BaseStopZ, stopZ, problem.BaseStopCount);
            var lines = new List<TransitLine>(problem.BaseLines);
            int next = problem.BaseStopCount;
            foreach (int c in chosen)
            {
                LineCandidate candidate = problem.Candidates[c];
                var stops = new int[candidate.StopX.Length];
                for (int i = 0; i < stops.Length; i++)
                {
                    stopX[next] = candidate.StopX[i];
                    stopZ[next] = candidate.StopZ[i];
                    stops[i] = next++;
                }

                lines.Add(new TransitLine { m_Stops = stops, m_ExpectedWait = candidate.ExpectedWait, m_RideSeconds = candidate.RideSeconds, m_SpeedMetresPerSecond = candidate.SpeedMetresPerSecond });
            }

            var zoneX = new float[problem.PairCount * 2];
            var zoneZ = new float[problem.PairCount * 2];
            for (int i = 0; i < problem.PairCount; i++)
            {
                zoneX[2 * i] = problem.PairOx[i];
                zoneZ[2 * i] = problem.PairOz[i];
                zoneX[(2 * i) + 1] = problem.PairDx[i];
                zoneZ[(2 * i) + 1] = problem.PairDz[i];
            }

            TransitNetwork network = SuitabilityTransit.BuildWithZones(stopX, stopZ, stopCount, lines, problem.WalkRadius, problem.BoardPenaltySeconds, zoneX, zoneZ, problem.PairCount * 2, problem.ZoneReachMetres);
            var workspace = new DijkstraWorkspace(network.Graph.NodeCount);
            after = new float[problem.PairCount];
            riders = new double[problem.Candidates.Count];
            saved = 0.0;
            int lineOffset = problem.BaseLines.Count;
            for (int i = 0; i < problem.PairCount; i++)
            {
                int origin = network.ZoneNodeStart + (2 * i);
                int destination = origin + 1;
                workspace.Run(network.Graph, origin, problem.MaxTravelSeconds);
                float walkOnly = SuitabilityLineSet.WalkOnlySeconds(problem, i);
                float transit = workspace.Dist[destination];
                after[i] = Math.Min(walkOnly, transit);
                if (transit < walkOnly)
                {
                    var ridden = new HashSet<int>();
                    int node = destination;
                    while (node != origin)
                    {
                        int edge = workspace.PrevEdge[node];
                        if (network.EdgeKind[edge] == TransitEdgeKind.Access)
                        {
                            int line = network.EdgeLine[edge] - lineOffset;
                            if (line >= 0 && line < chosen.Length && ridden.Add(line))
                            {
                                riders[chosen[line]] += problem.PairWeight[i];
                            }
                        }

                        node = network.Graph.OtherEnd(edge, node);
                    }
                }

                if (before is not null && before[i] > after[i])
                {
                    saved += problem.PairWeight[i] * (double)(before[i] - after[i]);
                }
            }
        }

        private static void LineSetMatchesBruteForce()
        {
            LineSetProblem problem = FeederProblem();
            problem.Candidates.Add(Line(150f, 20f, (0f, 0f), (6000f, 0f)));   // an exact copy of the trunk: a duplicate
            problem.MaxLines = 3;
            problem.UtilisationFloor = 0.01f;
            float[] before = SuitabilityLineSet.Evaluate(problem, Array.Empty<int>(), 0, null).After;

            // Brute force over every subset of size <= 3 with the same feasibility rules.
            int n = problem.Candidates.Count;
            double best = -1.0;
            for (int mask = 0; mask < (1 << n); mask++)
            {
                var chosen = new List<int>();
                for (int c = 0; c < n; c++)
                {
                    if ((mask & (1 << c)) != 0)
                    {
                        chosen.Add(c);
                    }
                }

                if (chosen.Count > problem.MaxLines)
                {
                    continue;
                }

                LineSetEvaluation evaluation = SuitabilityLineSet.Evaluate(problem, chosen.ToArray(), chosen.Count, before);
                bool feasible = true;
                foreach (int c in chosen)
                {
                    feasible &= SuitabilityLineSet.Utilisation(problem, c, evaluation.Riders[c]) >= problem.UtilisationFloor;
                }

                if (feasible && chosen.Count >= 2)
                {
                    // Duplicate rule by hand: a line is redundant if half its riders lose nothing without it.
                    foreach (int c in chosen)
                    {
                        var rest = chosen.Where(x => x != c).ToArray();
                        LineSetEvaluation without = SuitabilityLineSet.Evaluate(problem, rest, rest.Length, before);
                        double slowed = 0.0;
                        for (int i = 0; i < problem.PairCount; i++)
                        {
                            if (without.After[i] > evaluation.After[i])
                            {
                                slowed += problem.PairWeight[i];
                            }
                        }

                        double riding = evaluation.Riders[c];
                        if (riding <= 0.0 || (riding - slowed) / riding >= problem.DuplicateShare)
                        {
                            feasible = false;
                        }
                    }
                }

                if (feasible)
                {
                    best = Math.Max(best, evaluation.TimeSaved);
                }
            }

            LineSetSolution solution = SuitabilityLineSet.Solve(problem, SuitabilityLineSet.DefaultNodeBudget);
            AssertTrue(solution.Optimal, "four candidates close");
            AssertEqual((float)best, (float)solution.TimeSaved, 1e-3f, "solver equals brute force");
            AssertTrue(solution.Count == 2, "the duplicate trunk cannot join its twin and the stub carries nobody");
            AssertTrue(solution.Infeasible > 0, "infeasible sets were met and counted");

            problem.UtilisationFloor = 10f;
            LineSetSolution starved = SuitabilityLineSet.Solve(problem, SuitabilityLineSet.DefaultNodeBudget);
            AssertEqual(0, starved.Count, 0, "an unreachable utilisation floor leaves the empty set");
        }

        private static void LineSetEquityFirst()
        {
            LineSetProblem problem = FeederProblem();
            problem.MaxLines = 1;
            // Pretend coverage: the stub (candidate 2) serves the most doors, the trunk none.
            problem.CoverageOf = (chosen, count) =>
            {
                float coverage = 0.1f;
                for (int k = 0; k < count; k++)
                {
                    coverage += chosen[k] == 2 ? 0.5f : (chosen[k] == 1 ? 0.2f : 0f);
                }

                return coverage;
            };
            problem.EquityFloorShare = 0.8f;
            LineSetSolution solution = SuitabilityLineSet.Solve(problem, SuitabilityLineSet.DefaultNodeBudget);
            AssertEqual(1, solution.Count, 0, "one line");
            AssertEqual(2, solution.Chosen[0], 0, "below the floor the line that serves the most doors wins, whatever it saves");

            problem.EquityFloorShare = 0.05f;   // already met by everyone: time saved decides
            solution = SuitabilityLineSet.Solve(problem, SuitabilityLineSet.DefaultNodeBudget);
            AssertEqual(0, solution.Chosen[0], 0, "with the floor met the trunk's time saving wins");
        }

        private static ObservedTrip TripAt(uint frame, byte purpose)
        {
            return new ObservedTrip { m_Frame = frame, m_OriginX = 1f, m_OriginZ = 2f, m_DestinationX = 3f, m_DestinationZ = 4f, m_Purpose = purpose };
        }

        private static void ObservedTripWindowHoldsADay()
        {
            uint day = LineHistory.FramesPerGameDay;
            var window = new ObservedTripWindow(day);
            AssertEqual(1f, window.ScaleFor(day), 0f, "an empty window scales by 1");
            window.Record(TripAt(1000u, 1));
            window.Record(TripAt(1000u + day / 4, 2));
            window.Record(TripAt(1000u + day / 2, 1));
            AssertEqual(3, window.Count, 0, "three trips held");
            AssertEqual(2, window.CountOf(1), 0, "two shopping");
            AssertEqual(1, window.CountOf(2), 0, "one leisure");
            AssertEqual(day / 2, (int)window.SpanFrames, 0, "span is half a day");
            AssertEqual(2f, window.ScaleFor(day), 0f, "half a day of readings scales by 2");

            window.Prune(1000u + day / 4 + day);
            AssertEqual(2, window.Count, 0, "the first trip fell out of the window");
            AssertEqual(1, window.CountOf(1), 0, "per-purpose count follows");
            AssertEqual(1, window.EvictedSinceLastReport, 0, "one eviction reported");
            AssertEqual(1000u + day / 4, (int)window[0].m_Frame, 0, "the oldest is now the second");

            var brief = new ObservedTripWindow(day);
            brief.Record(TripAt(10u, 1));
            brief.Record(TripAt(20u, 1));
            AssertEqual(ObservedTripWindow.MaxDayScale, brief.ScaleFor(day), 0f, "ten frames of readings cannot be scaled past the cap");
        }

        private static void ObservedTripWindowRestartsAndCaps()
        {
            var window = new ObservedTripWindow(1000u);
            window.Record(TripAt(500u, 1));
            window.Record(TripAt(600u, 1));
            window.Record(TripAt(100u, 2));
            AssertEqual(1, window.Count, 0, "a frame below the newest means another save: the window restarts");
            AssertEqual(0, window.CountOf(1), 0, "old purposes cleared");
            AssertEqual(1, window.CountOf(2), 0, "the new trip is kept");

            var full = new ObservedTripWindow(uint.MaxValue);
            for (int i = 0; i < ObservedTripWindow.Capacity + 5; i++)
            {
                full.Record(TripAt((uint)i, 1));
            }

            AssertEqual(ObservedTripWindow.Capacity, full.Count, 0, "the cap holds");
            AssertEqual(5, full.DroppedAtCapSinceLastReport, 0, "drops are counted, not hidden");
            full.ClearCounters();
            AssertEqual(0, full.DroppedAtCapSinceLastReport, 0, "counters reset on report");
        }

        private static WalkGraph LineGraph(int nodes, float metres)
        {
            var x = new float[nodes];
            var z = new float[nodes];
            var a = new int[Math.Max(0, nodes - 1)];
            var b = new int[a.Length];
            var len = new float[a.Length];
            for (int i = 0; i < nodes; i++)
            {
                x[i] = i * metres;
            }

            for (int e = 0; e < a.Length; e++)
            {
                a[e] = e;
                b[e] = e + 1;
                len[e] = metres;
            }

            return WalkGraph.Build(x, z, a, b, len, a.Length);
        }

        private static void WalkGraphMilliseconds()
        {
            // 1.2f widened is 1.2000000476837158, so 1.2 m is fractionally under a
            // second and rounds to 1000 ms; 0 m is never free.
            AssertEqual(1000, WalkGraph.WalkMilliseconds(1.2), 0, "1.2 m at 1.2 m/s");
            AssertEqual(0, WalkGraph.WalkMilliseconds(0.0), 0, "a zero walk is zero milliseconds");
            AssertEqual(100000, WalkGraph.WalkMilliseconds(120.0), 0, "120 m is 100 s");
            WalkGraph graph = LineGraph(3, 72f);
            AssertEqual(60000, graph.EdgeMs[0], 0, "72 m is one minute");
            WalkGraph degenerate = WalkGraph.Build(new float[2], new float[2], new[] { 0 }, new[] { 1 }, new[] { 0f }, 1);
            AssertEqual(1, degenerate.EdgeMs[0], 0, "a zero-length edge still costs a millisecond");
            AssertEqual(2, graph.Offsets[2] - graph.Offsets[1], 0, "middle node has two adjacencies");
        }

        private static void IntDijkstraExactBoundedReusable()
        {
            // 0-1-2-3-4 at one minute per edge, plus a 0-4 shortcut of 150 s: the
            // shortcut wins for node 4 (150 < 240) and settles before node 3 (180).
            WalkGraph line = LineGraph(5, 72f);
            var a = new List<int>(line.EdgeA) { 0 };
            var b = new List<int>(line.EdgeB) { 4 };
            var len = new List<float> { 72f, 72f, 72f, 72f, 180f };
            WalkGraph graph = WalkGraph.Build(line.NodeX, line.NodeZ, a.ToArray(), b.ToArray(), len.ToArray(), 5);

            var dijkstra = new IntDijkstra(graph.NodeCount);
            dijkstra.Run(graph, 0, 0, 1_000_000);
            AssertEqual(5, dijkstra.SettledCount, 0, "everything is within the bound");
            AssertTrue(dijkstra.Dist[4] == 150000, "shortcut time is exact");
            AssertTrue(dijkstra.Dist[3] == 180000, "line time is exact");
            AssertEqual(4, dijkstra.Settled[3], 0, "node 4 settles before node 3");

            dijkstra.Run(graph, 2, 30000, 90000);
            AssertEqual(3, dijkstra.SettledCount, 0, "start 30 s + one minute reaches the neighbours only");
            AssertTrue(dijkstra.Dist[2] == 30000, "start cost is the source's time");
            AssertTrue(dijkstra.Dist[0] == IntDijkstra.Unreached, "beyond the bound is unreached");
            AssertTrue(dijkstra.Dist[4] == IntDijkstra.Unreached, "the previous run's labels are gone");

            var fresh = new IntDijkstra(graph.NodeCount);
            fresh.Run(graph, 2, 30000, 90000);
            for (int i = 0; i < graph.NodeCount; i++)
            {
                AssertTrue(fresh.Dist[i] == dijkstra.Dist[i], "reused workspace equals a fresh one");
            }
        }

        private static void NearestNodeTiesAndReach()
        {
            var x = new float[] { 0f, 100f, 100f, 400f };
            var z = new float[] { 0f, 0f, 0f, 0f };
            WalkGraph graph = WalkGraph.Build(x, z, new[] { 0, 1, 2 }, new[] { 1, 2, 3 }, new[] { 100f, 1f, 300f }, 3);
            var index = new WalkNodeIndex(graph, 144.0);

            int node = index.Nearest(60f, 0f, 144.0, out double metres);
            AssertEqual(1, node, 0, "co-located nodes 1 and 2: the lower index wins");
            AssertEqual(40f, (float)metres, 0f, "distance to it");
            AssertEqual(-1, index.Nearest(250f, 0f, 144.0, out _), 0, "nothing within reach");
            AssertEqual(1, index.Nearest(250f, 0f, 200.0, out _), 0, "150 m to both sides: the lower index wins");
            AssertEqual(3, index.Nearest(260f, 0f, 200.0, out _), 0, "wider reach finds the nearer node 3");

            int snapped = SuitabilityWalkAccess.SnapPoint(index, 0f, 60f, TransitModes.AccessWalkMs, out int walkMs);
            AssertEqual(0, snapped, 0, "60 m off node 0");
            AssertEqual(50000, walkMs, 0, "60 m is 50 s");
        }

        private static WalkAccessInputs LineInputs(int nodes)
        {
            return new WalkAccessInputs
            {
                Graph = LineGraph(nodes, 72f),
                TypeCount = 14,
                AccessMs = TransitModes.AccessWalkMs,
                TransferMs = TransitModes.TransferWalkMs,
                CatchmentMs = TransitModes.CatchmentClassesMs,
            };
        }

        private static WalkSources Sources(params (float x, float w)[] points)
        {
            var sources = new WalkSources { Count = points.Length, X = new float[points.Length], Z = new float[points.Length], Weight = new float[points.Length] };
            for (int i = 0; i < points.Length; i++)
            {
                sources.X[i] = points[i].x;
                sources.Weight[i] = points[i].w;
            }

            return sources;
        }

        private static void WalkAccessAccumulatesKernel()
        {
            // Ten residents at node 0 (access 0) and ten more 36 m off node 0 (30 s
            // access). Class 0 is six minutes; node k is k minutes down the line.
            WalkAccessInputs inputs = LineInputs(12);
            inputs.Homes = Sources((0f, 10f), (0f, 10f));
            inputs.Homes.Z[1] = 36f;
            WalkAccessResult result = SuitabilityWalkAccess.Compute(inputs);

            AssertEqual(0, result.SourcesOffNetwork, 0, "both homes are on the network");
            AssertEqual(30000, result.HomeAccessMs[1], 0, "36 m is 30 s");
            double k0 = SuitabilityWalkAccess.Kernel(0, 360000);
            double k30 = SuitabilityWalkAccess.Kernel(30000, 360000);
            AssertEqual((float)((float)(10.0 * k0) + (10.0 * k30)), result.Demand[0][0], 0f, "node 0 sums both homes in index order, rounding once per store");
            double k60 = SuitabilityWalkAccess.Kernel(60000, 360000);
            double k90 = SuitabilityWalkAccess.Kernel(90000, 360000);
            AssertEqual((float)((float)(10.0 * k60) + (10.0 * k90)), result.Demand[0][1], 0f, "node 1 is a minute further for both");
            AssertEqual(0f, result.Demand[0][6], 0f, "six minutes out the kernel reaches zero");
            AssertEqual(0f, result.Demand[0][7], 0f, "beyond the class horizon nothing arrives");
            AssertTrue(result.Demand[2][7] > 0f, "the 16-minute class still sees node 7");
            AssertEqual(0f, result.Jobs[0][0], 0f, "no jobs given");

            // Off-network home is dropped and counted.
            inputs.Homes = Sources((0f, 5f), (1200f, 5f));
            result = SuitabilityWalkAccess.Compute(inputs);
            AssertEqual(1, result.SourcesOffNetwork, 0, "the far home has no node within the access walk");
            AssertEqual((float)(5.0 * k0), result.Demand[0][0], 0f, "only the near home counts");
        }

        private static void WalkAccessStopTerms()
        {
            // A bus stop (type 0) at node 0 and a train station (type 1) at node 2,
            // two minutes apart. Transfer horizon is three minutes, class 0 six.
            WalkAccessInputs inputs = LineInputs(8);
            inputs.StopCount = 2;
            inputs.StopX = new[] { 0f, 144f };
            inputs.StopZ = new[] { 0f, 0f };
            inputs.StopType = new[] { 0, 1 };
            WalkAccessResult result = SuitabilityWalkAccess.Compute(inputs);

            double within120 = SuitabilityWalkAccess.Kernel(120000, 360000);
            double transferable120 = SuitabilityWalkAccess.Kernel(120000, 180000);
            AssertEqual(1f, result.StopWithin[0][0][0], 0f, "the bus stop covers its own node fully");
            AssertEqual((float)within120, result.StopWithin[0][1][0], 0f, "the station is two minutes from node 0");
            AssertEqual((float)transferable120, result.Interchange[1][0], 0f, "transferable at two of three minutes");
            AssertEqual((float)(within120 * (1.0 - transferable120)), result.CrossRaw[0][1][0], 0f, "cross fades by transferability");
            AssertEqual(0f, result.Interchange[1][6], 0f, "six minutes away is not a transfer");
            AssertTrue(result.StopWithin[0][1][5] > 0f && result.Interchange[1][5] == 0f, "node 5: inside the catchment, outside the transfer walk");

            // Terms for a bus (type 0) vs a train (type 1) at node 0, train weight 3.
            var weights = new float[14];
            weights[0] = 1f;
            weights[1] = 3f;
            SuitabilityCell bus = SuitabilityWalkAccess.NodeTerms(result, 0, 0, 0, weights);
            AssertEqual(1f, bus.m_Coverage, 0f, "own bus stop is coverage");
            AssertEqual((float)(3.0 * result.Interchange[1][0]), bus.m_Interchange, 0f, "the station is a weighted transfer partner");
            AssertEqual((float)(3.0 * result.CrossRaw[0][1][0]), bus.m_CrossCoverage, 0f, "and a weighted competitor");
            SuitabilityCell train = SuitabilityWalkAccess.NodeTerms(result, 0, 0, 1, weights);
            AssertEqual((float)within120, train.m_Coverage, 0f, "for a train the station is coverage");
            AssertEqual((float)SuitabilityWalkAccess.Kernel(0, 180000), train.m_Interchange, 0f, "the bus stop is its transfer partner");
            weights[0] = 0f;
            SuitabilityCell trainNoBus = SuitabilityWalkAccess.NodeTerms(result, 0, 0, 1, weights);
            AssertEqual(0f, trainNoBus.m_Interchange, 0f, "a type with weight 0 is excluded");
        }

        private static void WalkAccessTileTerms()
        {
            WalkAccessInputs inputs = LineInputs(4);
            inputs.Homes = Sources((0f, 10f));
            WalkAccessResult result = SuitabilityWalkAccess.Compute(inputs);
            var index = new WalkNodeIndex(inputs.Graph, 144.0);

            // Three 32 m tiles in a row starting at x = -16: centres 0, 32, 64.
            const int width = 3;
            var buildable = new byte[] { 1, 0, 1 };
            var terms = new SuitabilityCell[width];
            var tileNode = new int[width];
            var tileWalk = new int[width];
            SuitabilityWalkAccess.TileTerms(result, index, width, 1, -16f, -16f, 32f, buildable, inputs.AccessMs, 0, 0, new float[14], terms, tileNode, tileWalk);

            AssertEqual(0, tileNode[0], 0, "tile 0 sits on node 0");
            AssertEqual(1f, terms[0].m_Access, 0f, "on the node access is 1");
            AssertEqual(result.Demand[0][0], terms[0].m_Demand, 0f, "tile demand is the node's");
            AssertEqual(-1, tileNode[1], 0, "unbuildable tiles are not snapped");
            AssertEqual(0f, terms[1].m_Demand, 0f, "and score nothing");
            AssertEqual(1, tileNode[2], 0, "tile 2 at x=64 is nearer node 1 (72) than node 0");
            int walk = WalkGraph.WalkMilliseconds(8.0);
            AssertEqual(walk, tileWalk[2], 0, "8 m off the node");
            AssertEqual((float)SuitabilityWalkAccess.Kernel(walk, inputs.AccessMs), terms[2].m_Access, 0f, "access fades with the walk");
            AssertEqual(result.Demand[0][1], terms[2].m_Demand, 0f, "terms are node 1's");
        }

        private static void CatchmentClassesMatchModes()
        {
            var expected = new SortedSet<int>();
            foreach (ModePreset mode in Enum.GetValues<ModePreset>())
            {
                _ = expected.Add(TransitModes.CatchmentMs(mode));
            }

            int[] classes = TransitModes.CatchmentClassesMs;
            AssertEqual(expected.Count, classes.Length, 0, "one class per distinct horizon");
            int k = 0;
            foreach (int horizon in expected)
            {
                AssertEqual(horizon, classes[k++], 0, "ascending and complete");
                AssertTrue(SuitabilityWalkAccess.ClassOf(classes, horizon) >= 0, "every mode finds its class");
            }
        }

        // The regression test for the double-counting bug: an open grid gives every
        // tile many shortest-path predecessors, so each tile enters the frontier
        // repeatedly. Without settling a tile once, its density is added once per
        // pop and the total inflates unpredictably — observed in game as the same
        // site scoring 344 and then 2936 on consecutive recomputes.
        private static void WalkDistanceCountsOnce()
        {
            const int width = 21;
            const int height = 21;
            int cells = width * height;
            var land = new byte[cells];
            var demand = new float[cells];
            var jobs = new float[cells];
            for (int i = 0; i < cells; i++)
            {
                land[i] = 1;
                demand[i] = 1f;
            }

            int site = 10 + 10 * width;
            // A radius of exactly one tile step reaches the site plus its eight
            // neighbours, and the neighbours sit at weight 0 (orthogonal) or are out
            // of range (diagonal), so only the centre contributes: 1 * 1.0.
            float total = SuitabilityScoring.AccumulateWalkDistance(
                site, width, height, 1f, 1f, land, demand, jobs, 1f, 0f,
                new float[cells], new byte[cells], out float reachedDemand, out _);

            AssertEqual(1f, reachedDemand, 1e-4f, "only the centre tile is fully weighted");
            AssertEqual(1f, total, 1e-4f, "weighted total");

            // With a wider radius the sum must still be bounded by the number of
            // tiles in range, which double counting would blow past.
            float wide = SuitabilityScoring.AccumulateWalkDistance(
                site, width, height, 1f, 5f, land, demand, jobs, 1f, 0f,
                new float[cells], new byte[cells], out float wideDemand, out _);

            // 11x11 tiles are within 5 units of Chebyshev reach at most; every tile
            // contributes strictly less than 1, so the sum cannot reach that count.
            AssertTrue(wideDemand < 121f, $"reached demand {wideDemand} must be under the tile count in range");
            AssertTrue(wideDemand > 20f, $"reached demand {wideDemand} should still cover a real neighbourhood");
            AssertEqual(wide, wideDemand, 1e-4f, "jobs weight zero leaves the demand total");
        }

        private static void WalkDistanceIsDeterministic()
        {
            const int width = 25;
            const int height = 25;
            int cells = width * height;
            var land = new byte[cells];
            var demand = new float[cells];
            var jobs = new float[cells];
            var random = new Random(7);
            for (int i = 0; i < cells; i++)
            {
                land[i] = (byte)(random.NextDouble() < 0.85 ? 1 : 0);
                demand[i] = (float)random.NextDouble() * 100f;
                jobs[i] = (float)random.NextDouble() * 50f;
            }

            int site = 12 + 12 * width;
            land[site] = 1;

            var distance = new float[cells];
            var visited = new byte[cells];
            float first = SuitabilityScoring.AccumulateWalkDistance(
                site, width, height, 32f, 300f, land, demand, jobs, 1f, 0.5f, distance, visited, out _, out _);

            // Reusing the same scratch buffers must not change the answer, which is
            // exactly the condition the in-game repeats violated.
            for (int repeat = 0; repeat < 5; repeat++)
            {
                float again = SuitabilityScoring.AccumulateWalkDistance(
                    site, width, height, 32f, 300f, land, demand, jobs, 1f, 0.5f, distance, visited, out _, out _);
                AssertEqual(first, again, 1e-3f, $"repeat {repeat} must match the first result");
            }
        }

        private static void WalkDistanceRespectsRadius()
        {
            const int width = 31;
            int height = 31;
            int cells = width * height;
            var land = new byte[cells];
            var demand = new float[cells];
            var jobs = new float[cells];
            for (int i = 0; i < cells; i++)
            {
                land[i] = 1;
            }

            int site = 15 + 15 * width;
            // Demand far outside the radius must not be reached at all.
            demand[0] = 1000f;

            _ = SuitabilityScoring.AccumulateWalkDistance(
                site, width, height, 10f, 30f, land, demand, jobs, 1f, 0f,
                new float[cells], new byte[cells], out float reached, out _);

            AssertEqual(0f, reached, 1e-4f, "demand beyond the radius must not be counted");
        }

        private static void WalkDistanceBlockedByWater()
        {
            const int width = 21;
            const int height = 9;
            int cells = width * height;
            var land = new byte[cells];
            var demand = new float[cells];
            var jobs = new float[cells];
            for (int i = 0; i < cells; i++)
            {
                land[i] = 1;
            }

            // A full-height water column splits the grid in two.
            int barrierX = 10;
            for (int y = 0; y < height; y++)
            {
                land[barrierX + y * width] = 0;
            }

            // Demand sits just across the barrier, well within straight-line range.
            demand[(barrierX + 1) + 4 * width] = 500f;

            int site = (barrierX - 1) + 4 * width;
            _ = SuitabilityScoring.AccumulateWalkDistance(
                site, width, height, 10f, 60f, land, demand, jobs, 1f, 0f,
                new float[cells], new byte[cells], out float reached, out _);

            AssertEqual(0f, reached, 1e-4f, "demand across an impassable barrier must not be reached");

            // Opening a gap in the barrier must let it through again.
            land[barrierX + 4 * width] = 1;
            _ = SuitabilityScoring.AccumulateWalkDistance(
                site, width, height, 10f, 60f, land, demand, jobs, 1f, 0f,
                new float[cells], new byte[cells], out float throughGap, out _);

            AssertTrue(throughGap > 0f, "a gap in the barrier must make the demand reachable");
        }

        private static void FitRecoversKnownWeights()
        {
            // Synthetic observations generated from known weights; the fit must
            // recover them.
            var truth = new float[] { 1.2f, 0.8f, 0.35f, 0.15f };
            int rows = 40;
            int cols = truth.Length;
            var features = new float[rows, cols];
            var target = new float[rows];
            var random = new Random(1234);

            for (int r = 0; r < rows; r++)
            {
                float expected = 0f;
                for (int c = 0; c < cols; c++)
                {
                    float value = (float)random.NextDouble();
                    features[r, c] = value;
                    expected += value * truth[c];
                }
                target[r] = expected;
            }

            var weights = new float[cols];
            AssertTrue(SuitabilityScoring.FitNonNegativeLeastSquares(features, target, rows, cols, weights), "fit should succeed");
            for (int c = 0; c < cols; c++)
            {
                AssertEqual(truth[c], weights[c], 1e-3f, $"weight {c}");
            }

            AssertEqual(1f, SuitabilityScoring.RSquared(features, target, rows, cols, weights), 1e-4f, "exact fit");
        }

        private static void FitClampsNegativeWeights()
        {
            // Column 1 genuinely anti-correlates with the target, so an
            // unconstrained fit would want a negative coefficient. It must be
            // pinned to zero instead, and no coefficient may come back negative.
            int rows = 30;
            int cols = 2;
            var features = new float[rows, cols];
            var target = new float[rows];
            var random = new Random(99);

            for (int r = 0; r < rows; r++)
            {
                float a = (float)random.NextDouble();
                float b = (float)random.NextDouble();
                features[r, 0] = a;
                features[r, 1] = b;
                target[r] = a * 1.5f - b * 0.9f;
            }

            var weights = new float[cols];
            bool ok = SuitabilityScoring.FitNonNegativeLeastSquares(features, target, rows, cols, weights);
            AssertTrue(ok, "constrained fit should still succeed");
            for (int c = 0; c < cols; c++)
            {
                AssertTrue(weights[c] >= 0f, $"weight {c} must not be negative (was {weights[c]})");
            }
            AssertEqual(0f, weights[1], 0f, "the anti-correlated column must be pinned to zero");
            AssertTrue(weights[0] > 0f, "the correlated column must stay positive");
        }

        private static void FitRejectsUnderdetermined()
        {
            var features = new float[2, 4];
            var target = new float[2];
            var weights = new float[4];
            AssertTrue(!SuitabilityScoring.FitNonNegativeLeastSquares(features, target, 2, 4, weights), "fewer rows than columns must be rejected");

            // All-zero features are singular and must be rejected rather than
            // producing NaN weights.
            var zeros = new float[20, 3];
            var zeroTarget = new float[20];
            var zeroWeights = new float[3];
            AssertTrue(!SuitabilityScoring.FitNonNegativeLeastSquares(zeros, zeroTarget, 20, 3, zeroWeights), "singular system must be rejected");
        }

        private static void RSquaredBounds()
        {
            int rows = 10;
            var features = new float[rows, 1];
            var target = new float[rows];
            for (int r = 0; r < rows; r++)
            {
                features[r, 0] = r;
                target[r] = r * 2f;
            }

            AssertEqual(1f, SuitabilityScoring.RSquared(features, target, rows, 1, new float[] { 2f }), 1e-5f, "exact");

            // An all-zero target leaves nothing to explain.
            AssertEqual(0f, SuitabilityScoring.RSquared(features, new float[rows], rows, 1, new float[] { 0f }), 0f, "nothing to explain");

            // The fit has no intercept column, so the measure is taken about zero
            // rather than about the mean: all-weights-zero predicts zero, which is the
            // worst the fit can do, and that pins the floor at 0. Measured about the
            // mean instead, this same case returned a large negative that reached the
            // player as a "model quality".
            var flat = new float[rows];
            for (int r = 0; r < rows; r++)
            {
                flat[r] = 5f;
            }
            AssertEqual(0f, SuitabilityScoring.RSquared(features, flat, rows, 1, new float[] { 0f }), 1e-5f, "no weight explains nothing");

            // The floor holds for the weights the FIT produces, which is the only way
            // the player ever sees this number: all-weights-zero is always available
            // to a non-negative least squares, so it can never do worse than zero.
            var fitted = new float[1];
            AssertTrue(SuitabilityScoring.FitNonNegativeLeastSquares(features, flat, rows, 1, fitted), "fit a poorly explained target");
            float quality = SuitabilityScoring.RSquared(features, flat, rows, 1, fitted);
            AssertTrue(quality is >= 0f and <= 1f,
                $"a fitted model quality must land in 0..1, got {quality}");
        }

        // ---- graph / routing tests ------------------------------------------

        // A 6-node chain 0-1-2-3-4-5 plus a long shortcut edge 0-5.
        private static CompactGraph BuildChain(out float[] flow)
        {
            var a = new[] { 0, 1, 2, 3, 4, 0 };
            var b = new[] { 1, 2, 3, 4, 5, 5 };
            var cost = new[] { 1f, 1f, 1f, 1f, 1f, 100f };
            flow = new float[a.Length];
            return CompactGraph.Build(6, a, b, cost, a.Length);
        }

        private static void GraphAdjacencyBothDirections()
        {
            CompactGraph graph = BuildChain(out _);

            // Node 2 sits mid-chain, so it must see the edges on both sides.
            int start = graph.NodeOffsets[2];
            int end = graph.NodeOffsets[2 + 1];
            AssertEqual(2, end - start, 0, "node 2 degree");

            var seen = new List<int>();
            for (int i = start; i < end; i++)
            {
                seen.Add(graph.AdjOther[i]);
            }
            AssertTrue(seen.Contains(1) && seen.Contains(3), "node 2 must reach both neighbours");

            // Node 0 has the chain edge plus the shortcut.
            AssertEqual(2, graph.NodeOffsets[1] - graph.NodeOffsets[0], 0, "node 0 degree");
            AssertEqual(3, graph.OtherEnd(2, 2), 0, "OtherEnd resolves the far end");
            AssertEqual(2, graph.OtherEnd(2, 3), 0, "OtherEnd is symmetric");
        }

        private static void DijkstraPrefersCheapPath()
        {
            CompactGraph graph = BuildChain(out _);
            var ws = new DijkstraWorkspace(graph.NodeCount);
            ws.Run(graph, 0, 1000f);

            // Five hops of cost 1 beat one hop of cost 100.
            AssertEqual(5f, ws.Dist[5], 1e-4f, "distance to node 5");
            AssertEqual(2f, ws.Dist[2], 1e-4f, "distance to node 2");
            AssertEqual(0f, ws.Dist[0], 0f, "source distance");
        }

        private static void DijkstraRespectsMaxCost()
        {
            CompactGraph graph = BuildChain(out _);
            var ws = new DijkstraWorkspace(graph.NodeCount);
            ws.Run(graph, 0, 2f);

            AssertEqual(2f, ws.Dist[2], 1e-4f, "node 2 is within the limit");
            AssertTrue(ws.Dist[4] == float.MaxValue, "node 4 is beyond the limit and must stay unreached");
        }

        private static void DijkstraWorkspaceReuse()
        {
            CompactGraph graph = BuildChain(out _);
            var ws = new DijkstraWorkspace(graph.NodeCount);

            ws.Run(graph, 0, 1000f);
            float first = ws.Dist[5];

            // A different source, then back again: stale state from the previous
            // search must not leak into the result.
            ws.Run(graph, 3, 1000f);
            ws.Run(graph, 0, 1000f);
            AssertEqual(first, ws.Dist[5], 0f, "repeat search must match");

            ws.Run(graph, 5, 1000f);
            AssertEqual(5f, ws.Dist[0], 1e-4f, "reverse direction is symmetric");
        }

        private static void FlowFollowsShortestPath()
        {
            CompactGraph graph = BuildChain(out float[] flow);
            var ws = new DijkstraWorkspace(graph.NodeCount);
            ws.Run(graph, 0, 1000f);

            AssertTrue(SuitabilityGraphMath.AccumulatePath(graph, ws, 0, 5, 10f, flow), "path should be found");

            // Chain edges 0..4 carry the flow; the expensive shortcut (edge 5) does not.
            for (int e = 0; e < 5; e++)
            {
                AssertEqual(10f, flow[e], 1e-4f, $"chain edge {e}");
            }
            AssertEqual(0f, flow[5], 0f, "the expensive shortcut must carry nothing");
        }

        private static void FlowIgnoresUnreachable()
        {
            // Two disconnected components: 0-1 and 2-3.
            var a = new[] { 0, 2 };
            var b = new[] { 1, 3 };
            var cost = new[] { 1f, 1f };
            CompactGraph graph = CompactGraph.Build(4, a, b, cost, 2);
            var flow = new float[2];
            var ws = new DijkstraWorkspace(graph.NodeCount);
            ws.Run(graph, 0, 1000f);

            AssertTrue(!SuitabilityGraphMath.AccumulatePath(graph, ws, 0, 3, 5f, flow), "must report failure");
            AssertEqual(0f, flow[0], 0f, "no flow on the reachable component");
            AssertEqual(0f, flow[1], 0f, "no flow on the unreachable component");
        }

        private static void CorridorFollowsFlow()
        {
            // Chain 0-1-2-3-4-5 with heavy flow on the middle, plus a dead-end spur
            // off node 2 carrying almost nothing.
            var a = new[] { 0, 1, 2, 3, 4, 2 };
            var b = new[] { 1, 2, 3, 4, 5, 6 };
            var cost = new[] { 100f, 100f, 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(7, a, b, cost, a.Length);
            var flow = new[] { 5f, 50f, 60f, 40f, 3f, 1f };
            var used = new bool[a.Length];
            var novelty = NewNovelty(7);

            var corridor = new Corridor();
            AssertTrue(SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, used, novelty), 0f, 2f, 1000f, corridor),
                "corridor should grow");

            // Seeded on edge 2 (the strongest) and extended over the other strong
            // edges; the near-empty spur must be left out.
            AssertTrue(corridor.Edges.Contains(2), "must include the strongest edge");
            AssertTrue(corridor.Edges.Contains(1), "must include the second strongest");
            AssertTrue(!corridor.Edges.Contains(5), "must not take the near-empty spur");
            // CapturedFlow is a length-weighted MEAN, so it must sit inside the range
            // of the edge flows it covers — never their sum. Summing counted a trip
            // once per edge it traversed, which inflated long corridors past the
            // city's entire demand and made everything look like a metro.
            AssertTrue(corridor.CapturedFlow is > 20f and < 61f,
                $"mean flow {corridor.CapturedFlow} must lie within the per-edge range, not be a sum");
        }

        private static void CorridorIsConnected()
        {
            var a = new[] { 0, 1, 2, 3 };
            var b = new[] { 1, 2, 3, 4 };
            var cost = new[] { 10f, 10f, 10f, 10f };
            CompactGraph graph = CompactGraph.Build(5, a, b, cost, 4);
            var flow = new[] { 8f, 9f, 7f, 6f };
            var used = new bool[4];

            var corridor = new Corridor();
            AssertTrue(SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, used, NewNovelty(5)), 0f, 1f, 1000f, corridor),
                "corridor should grow");

            // Nodes must form an unbroken walk: each consecutive pair is joined by
            // the corresponding edge, which is what makes it drawable as a polyline.
            AssertEqual(corridor.Edges.Count + 1, corridor.Nodes.Count, 0, "node count must be edge count + 1");
            for (int i = 0; i < corridor.Edges.Count; i++)
            {
                int edge = corridor.Edges[i];
                int from = corridor.Nodes[i];
                int to = corridor.Nodes[i + 1];
                bool joins = (graph.EdgeA[edge] == from && graph.EdgeB[edge] == to)
                    || (graph.EdgeB[edge] == from && graph.EdgeA[edge] == to);
                AssertTrue(joins, $"edge {edge} must join nodes {from} and {to}");
            }
        }

        private static void CorridorRespectsMaxLength()
        {
            var a = new[] { 0, 1, 2, 3, 4 };
            var b = new[] { 1, 2, 3, 4, 5 };
            var cost = new[] { 100f, 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(6, a, b, cost, 5);
            var flow = new[] { 10f, 10f, 10f, 10f, 10f };

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, new bool[5], NewNovelty(6)), 0f, 1f, 250f, corridor);

            AssertTrue(corridor.Length <= 250f, $"length {corridor.Length} must respect the limit");
            AssertTrue(corridor.Edges.Count <= 2, $"only 2 edges of 100 fit under 250, got {corridor.Edges.Count}");
        }

        // A corridor of N identical edges must report the flow of ONE edge, not N
        // times it. With the old sum this scaled with length without bound.
        private static void CorridorFlowIsMean()
        {
            var a = new[] { 0, 1, 2, 3 };
            var b = new[] { 1, 2, 3, 4 };
            var cost = new[] { 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(5, a, b, cost, 4);
            var flow = new[] { 40f, 40f, 40f, 40f };

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, new bool[4], NewNovelty(5)), 0f, 1f, 10000f, corridor);

            AssertTrue(corridor.Edges.Count >= 3, $"should grow along the chain, got {corridor.Edges.Count} edges");
            AssertEqual(40f, corridor.CapturedFlow, 1e-3f, "uniform flow means the mean equals that flow");
            AssertTrue(corridor.Length >= 300f, "length still accumulates");
        }

        // The bug this guards: a rural through-road carries real assigned flow while
        // serving nobody, so flow alone let corridors loop out into empty land.
        private static void DemandGateBlocksEmptyLand()
        {
            // Chain 0-1-2 through populated nodes, then 2-3-4 out into emptiness.
            var a = new[] { 0, 1, 2, 3 };
            var b = new[] { 1, 2, 3, 4 };
            var cost = new[] { 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(5, a, b, cost, 4);
            var flow = new[] { 50f, 50f, 50f, 50f };
            // Nodes 3 and 4 have nobody living or working near them.
            var demand = new[] { 1f, 1f, 1f, 0f, 0f };

            var ungated = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, new bool[4], NewNovelty(5)), 0f, 1f, 10000f, ungated);
            AssertTrue(ungated.Edges.Count == 4, "without the gate it runs the whole chain including empty land");

            var gated = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, (float[])flow.Clone(), new bool[4], NewNovelty(5), demand), 0f, 1f, 10000f, gated, 0.5f);

            AssertTrue(gated.Nodes.Contains(0) && gated.Nodes.Contains(2), "populated stretch is kept");
            AssertTrue(!gated.Nodes.Contains(3) && !gated.Nodes.Contains(4), "empty nodes must be refused");
            AssertTrue(gated.Length < ungated.Length, "the gated corridor must be shorter");
        }

        private static void PeelingReducesFlow()
        {
            var a = new[] { 0, 1, 2 };
            var b = new[] { 1, 2, 3 };
            var cost = new[] { 10f, 10f, 10f };
            CompactGraph graph = CompactGraph.Build(4, a, b, cost, 3);
            var flow = new[] { 100f, 100f, 100f };
            var used = new bool[3];

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, used, NewNovelty(4)), 0f, 1f, 1000f, corridor);
            float before = 0f;
            for (int e = 0; e < 3; e++)
            {
                before += flow[e];
            }

            SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, 0.8f);

            float after = 0f;
            for (int e = 0; e < 3; e++)
            {
                after += flow[e];
            }
            AssertTrue(after < before, $"peeling must reduce total flow ({after} vs {before})");

            for (int i = 0; i < corridor.Edges.Count; i++)
            {
                AssertTrue(used[corridor.Edges[i]], "corridor edges must be marked used");
            }

            // A second growth must not re-select the same corridor.
            var second = new Corridor();
            bool grew = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, used, NewNovelty(4)), 0f, 1f, 1000f, second);
            if (grew)
            {
                for (int i = 0; i < second.Edges.Count; i++)
                {
                    AssertTrue(!corridor.Edges.Contains(second.Edges[i]), "must not reuse a peeled edge");
                }
            }
        }

        private static void ObjectiveChangesRoutes()
        {
            // A busy trunk 0-1-2 and a quiet branch off node 1 to fresh territory.
            var a = new[] { 0, 1, 1 };
            var b = new[] { 1, 2, 3 };
            var cost = new[] { 10f, 10f, 10f };
            CompactGraph graph = CompactGraph.Build(4, a, b, cost, 3);

            var ridership = new Corridor();
            var flowA = new[] { 100f, 90f, 20f };
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flowA, new bool[3], NewNovelty(4)),
                SuitabilityGraphMath.NoveltyWeight(RouteObjective.Ridership, 70f), 0f, 1000f, ridership);

            var coverage = new Corridor();
            var flowB = new[] { 100f, 90f, 20f };
            // Node 3 is virgin territory; nodes 0-2 are already covered.
            var novelty = new[] { 0.05f, 0.05f, 0.05f, 1f };
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flowB, new bool[3], novelty),
                SuitabilityGraphMath.NoveltyWeight(RouteObjective.Coverage, 70f), 0f, 1000f, coverage);

            AssertTrue(ridership.Edges.Contains(1), "ridership objective should take the busy trunk");
            AssertTrue(!ridership.Edges.Contains(2), "ridership objective should skip the quiet branch");
            AssertTrue(coverage.Edges.Contains(2), "coverage objective should reach the untouched branch");
        }

        // The objective must survive the SECOND corridor, which is the one the player
        // actually sees differ. Novelty is uniform when the first corridor is grown, so
        // an objective that only tips the extension choice cannot change anything at
        // all until something has already been chosen — and seeding ignored novelty
        // entirely, so in a real city all three objectives produced identical output.
        private static void ObjectiveChangesSeeding()
        {
            // Cluster A (0-1-2) is busiest and is taken first. Edge 6-7 hangs two hops
            // off it: still busier than anywhere else, but in ground the first corridor
            // has already come near. Cluster B (3-4-5) is quieter and untouched.
            //
            // Two hops matters: PeelFlow already halves the flow on edges TOUCHING the
            // corridor's own nodes, so the interesting case is the one it does not
            // reach — where only novelty can tell the two apart.
            var a = new[] { 0, 1, 1, 3, 4, 6 };
            var b = new[] { 1, 2, 6, 4, 5, 7 };
            var cost = new[] { 100f, 100f, 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(8, a, b, cost, 6);
            var baseFlow = new[] { 100f, 90f, 20f, 60f, 55f, 70f };

            Corridor RunSecondSeed(RouteObjective objective)
            {
                var flow = (float[])baseFlow.Clone();
                var used = new bool[graph.EdgeCount];
                float[] novelty = NewNovelty(8);
                float weight = SuitabilityGraphMath.NoveltyWeight(objective, 77f);
                float bias = SuitabilityGraphMath.SeedNoveltyBias(objective);

                var first = new Corridor();
                AssertTrue(
                    SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, used, novelty), weight, 0f, 250f, first,
                        seedNoveltyBias: bias),
                    "first corridor grows");
                SuitabilityGraphMath.PeelFlow(graph, first, flow, used, 0.85f);
                SuitabilityGraphMath.DecayNovelty(graph, first, novelty, 3, 0.15f);

                var second = new Corridor();
                AssertTrue(
                    SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, used, novelty), weight, 0f, 250f, second,
                        seedNoveltyBias: bias),
                    "second corridor grows");
                return second;
            }

            // Edge 5 is the busier stub near the first corridor; edges 3/4 are the
            // untouched cluster. Assert on what the corridor COVERS rather than on its
            // first edge: the edge list is emitted front-to-back, so the seed is not
            // necessarily at index 0.
            Corridor ridership = RunSecondSeed(RouteObjective.Ridership);
            AssertTrue(ridership.Edges.Contains(5),
                "ridership takes the busiest remaining edge even though it is next to the last corridor");
            AssertTrue(!ridership.Edges.Contains(3),
                "ridership does not go looking for the quieter untouched cluster");

            Corridor coverage = RunSecondSeed(RouteObjective.Coverage);
            AssertTrue(coverage.Edges.Contains(3),
                "coverage seeds the next corridor in territory the first one did not touch");
            AssertTrue(!coverage.Edges.Contains(5),
                "coverage leaves the busy stub beside the last corridor alone");
        }

        private static void RasterizeDepositsPerCell()
        {
            const int width = 10;
            const int height = 10;
            var raster = new float[width * height];

            // A horizontal line across row 5 from x=0.5 to x=9.5.
            SuitabilityGraphMath.RasterizeSegment(raster, width, height, 0.5f, 5.5f, 9.5f, 5.5f, 2f);

            for (int x = 0; x < width; x++)
            {
                AssertEqual(2f, raster[x + 5 * width], 1e-4f, $"cell ({x},5) gets the weight exactly once");
            }
            AssertEqual(0f, raster[0], 0f, "other rows untouched");

            // Out-of-bounds endpoints must be clipped, not wrapped or crash.
            var clipped = new float[width * height];
            SuitabilityGraphMath.RasterizeSegment(clipped, width, height, -50f, 2.5f, 50f, 2.5f, 1f);
            for (int x = 0; x < width; x++)
            {
                AssertEqual(1f, clipped[x + 2 * width], 1e-4f, $"clipped line still fills ({x},2)");
            }
        }

        // ---- transit graph tests --------------------------------------------

        // Two stops joined by a straight line of lattice-style corners.
        private static void SimplifyRemovesStaircase()
        {
            var points = new List<float2Like>();
            for (int i = 0; i <= 10; i++)
            {
                // A staircase that hugs the diagonal: every corner is within a metre
                // of the straight line, so all of them are artefacts.
                points.Add(new float2Like(i * 100f, i * 100f));
                points.Add(new float2Like((i + 1) * 100f, i * 100f));
            }

            int before = points.Count;
            SuitabilityGraphMath.SimplifyPolyline(points, 120f);

            AssertTrue(points.Count < before, $"simplification must drop corners ({points.Count} vs {before})");
            AssertTrue(points.Count >= 2, "endpoints must survive");
            AssertEqual(0f, points[0].x, 0f, "first point kept");
            AssertEqual(1100f, points[points.Count - 1].x, 1f, "last point kept");

            // A genuine right-angle detour must NOT be flattened away.
            var corner = new List<float2Like>
            {
                new float2Like(0f, 0f), new float2Like(0f, 1000f), new float2Like(1000f, 1000f),
            };
            SuitabilityGraphMath.SimplifyPolyline(corner, 120f);
            AssertEqual(3, corner.Count, 0, "a real corner must be preserved");
        }

        // Stops 0..3 in a line, plus stop 4 off to the side.
        private static TransitNetwork BuildTwoLineNetwork(float wait, out float[] xs, out float[] zs)
        {
            xs = new[] { 0f, 1000f, 2000f, 3000f, 1000f };
            zs = new[] { 0f, 0f, 0f, 0f, 500f };

            var lines = new List<TransitLine>
            {
                // Line 0: the trunk, all four stops in a row.
                new TransitLine { m_Stops = new[] { 0, 1, 2, 3 }, m_ExpectedWait = wait, m_SpeedMetresPerSecond = 10f },
                // Line 1: a feeder from the side stop into the trunk at stop 1.
                new TransitLine { m_Stops = new[] { 4, 1 }, m_ExpectedWait = wait, m_SpeedMetresPerSecond = 10f },
            };

            return SuitabilityTransit.Build(xs, zs, 5, lines, 100f, SuitabilityTransit.DefaultBoardPenaltySeconds);
        }

        private static void DirectBeatsTransfer()
        {
            TransitNetwork net = BuildTwoLineNetwork(60f, out _, out _);
            var ws = new DijkstraWorkspace(net.Graph.NodeCount);

            // 0 -> 3 is a single ride on the trunk: one boarding.
            ws.Run(net.Graph, 0, 100000f);
            AssertTrue(SuitabilityTransit.Inspect(net, ws, 0, 3, -1, out int boardings, out _, out float direct),
                "trunk journey should be routable");
            AssertEqual(1, boardings, 0, "riding one line is one boarding");

            // 4 -> 3 needs the feeder then the trunk: two boardings, and must cost
            // more than the direct trip even though the ride distance is shorter.
            ws.Run(net.Graph, 4, 100000f);
            AssertTrue(SuitabilityTransit.Inspect(net, ws, 4, 3, -1, out int viaFeeder, out _, out float changed),
                "feeder journey should be routable");
            AssertEqual(2, viaFeeder, 0, "changing vehicle is a second boarding");
            AssertTrue(changed > direct, $"a change must cost extra ({changed} vs {direct})");
        }

        private static void TransfersCostBoardings()
        {
            // With a big wait, the second boarding should dominate the cost.
            TransitNetwork cheap = BuildTwoLineNetwork(10f, out _, out _);
            TransitNetwork dear = BuildTwoLineNetwork(600f, out _, out _);

            var wsCheap = new DijkstraWorkspace(cheap.Graph.NodeCount);
            wsCheap.Run(cheap.Graph, 4, 100000f);
            _ = SuitabilityTransit.Inspect(cheap, wsCheap, 4, 3, -1, out _, out _, out float cheapTime);

            var wsDear = new DijkstraWorkspace(dear.Graph.NodeCount);
            wsDear.Run(dear.Graph, 4, 100000f);
            _ = SuitabilityTransit.Inspect(dear, wsDear, 4, 3, -1, out _, out _, out float dearTime);

            // Two boardings, each paying the extra wait: the gap is about 2x.
            AssertTrue(dearTime > cheapTime + 1000f, $"longer headways must cost more ({dearTime} vs {cheapTime})");
        }

        private static void WalkLinksStops()
        {
            // Two stops 80 m apart on different lines: within the walk radius they are
            // one interchange, beyond it the journey cannot be made at all.
            var xs = new[] { 0f, 1000f, 1080f, 2000f };
            var zs = new[] { 0f, 0f, 0f, 0f };
            var lines = new List<TransitLine>
            {
                new TransitLine { m_Stops = new[] { 0, 1 }, m_ExpectedWait = 30f, m_SpeedMetresPerSecond = 10f },
                new TransitLine { m_Stops = new[] { 2, 3 }, m_ExpectedWait = 30f, m_SpeedMetresPerSecond = 10f },
            };

            TransitNetwork linked = SuitabilityTransit.Build(xs, zs, 4, lines, 200f, 5f);
            var ws = new DijkstraWorkspace(linked.Graph.NodeCount);
            ws.Run(linked.Graph, 0, 100000f);
            AssertTrue(SuitabilityTransit.Inspect(linked, ws, 0, 3, -1, out int boardings, out _, out _),
                "a short walk must join the two lines");
            AssertEqual(2, boardings, 0, "one boarding per line");

            TransitNetwork split = SuitabilityTransit.Build(xs, zs, 4, lines, 50f, 5f);
            var ws2 = new DijkstraWorkspace(split.Graph.NodeCount);
            ws2.Run(split.Graph, 0, 100000f);
            AssertTrue(!SuitabilityTransit.Inspect(split, ws2, 0, 3, -1, out _, out _, out _),
                "too far to walk means no itinerary");
        }

        private static void ExpectedWaitModel()
        {
            // max(interval/2, observed) - dwell, floored at zero.
            AssertEqual(25f, SuitabilityTransit.ExpectedWait(60f, 0f, 5f), 1e-4f, "half the headway less dwell");
            AssertEqual(85f, SuitabilityTransit.ExpectedWait(60f, 90f, 5f), 1e-4f, "observed wait dominates when longer");
            AssertEqual(0f, SuitabilityTransit.ExpectedWait(10f, 0f, 100f), 0f, "never negative");
        }

        // The corridor's own head node is where GROWTH continues from, which while it
        // is out on an unproven crossing is not a node on the finished line. Emitting
        // the node walk from there put a discarded bridge's far end at the front of
        // the polyline and dropped the real terminus off the back, so the drawn route
        // began out in the empty land the discard exists to cut off and reached it by
        // a chord across the gap.
        private static void CorridorNodeWalkSurvivesDiscardedBridge()
        {
            // Chain 0-1-2-3-4. The seed is e2, the strongest edge; growing towards
            // node 0 crosses two demand-free nodes and never comes out, so that run is
            // handed back and the line is 2-3-4.
            var a = new[] { 0, 1, 2, 3 };
            var b = new[] { 1, 2, 3, 4 };
            var cost = new[] { 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(5, a, b, cost, 4);
            var flow = new[] { 10f, 10f, 100f, 10f };
            var demand = new[] { 0f, 0f, 1f, 1f, 1f };

            var corridor = new Corridor();
            AssertTrue(
                SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, new bool[4], NewNovelty(5), demand), 0f, 1f, 10000f, corridor, 0.5f),
                "corridor should grow");

            AssertTrue(!corridor.Nodes.Contains(0) && !corridor.Nodes.Contains(1),
                $"the handed-back crossing must not appear in the node walk, got [{string.Join(",", corridor.Nodes)}]");
            AssertTrue(corridor.Nodes.Contains(4),
                $"the real terminus must be in the node walk, got [{string.Join(",", corridor.Nodes)}]");
            AssertNodeWalkMatchesEdges(graph, corridor);
        }

        // Growth stops when the length limit is reached; handing a crossing back
        // afterwards shortens the corridor but does not change why it stopped.
        private static void CorridorReportsItsLimitAfterDiscard()
        {
            // Chain 0-1-2-3 at 100 m an edge against a 300 m limit. Growth seeds on
            // e0, crosses two demand-free nodes, and stops because it has spent its
            // whole allowance; the crossing is then handed back, leaving 100 m.
            var a = new[] { 0, 1, 2 };
            var b = new[] { 1, 2, 3 };
            var cost = new[] { 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(4, a, b, cost, 3);
            var flow = new[] { 50f, 50f, 50f };
            var demand = new[] { 1f, 1f, 0f, 0f };

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, flow, new bool[3], NewNovelty(4), demand), 0f, 1f, 300f, corridor, 0.5f);

            AssertEqual(100f, corridor.Length, 1e-3f, "the two crossing edges are handed back");
            AssertTrue(corridor.Blocks.m_HitMaxLength,
                "growth stopped at the length limit, and the discard must not rewrite that as running out of edges");
        }

        // Ridership passes no novelty bias and Coverage passes a full one, so a null
        // novelty array used to be harmless on one objective and fatal on the others.
        private static void CorridorGrowsWithoutNovelty()
        {
            var a = new[] { 0, 1 };
            var b = new[] { 1, 2 };
            var cost = new[] { 10f, 10f };
            CompactGraph graph = CompactGraph.Build(3, a, b, cost, 2);

            foreach (RouteObjective objective in new[] { RouteObjective.Ridership, RouteObjective.Balanced, RouteObjective.Coverage })
            {
                var corridor = new Corridor();
                AssertTrue(
                    SuitabilityGraphMath.GrowCorridor(
                        new CorridorNetwork(graph, new[] { 5f, 5f }, new bool[2]), noveltyWeight: 0f,
                        flowFloor: 1f, maxLength: 1000f, result: corridor, demandFloor: 0f,
                        seedNoveltyBias: SuitabilityGraphMath.SeedNoveltyBias(objective)),
                    $"{objective} must grow a corridor without a novelty array");
                AssertNodeWalkMatchesEdges(graph, corridor);
            }
        }

        // Side capture exists to suppress the parallel street one block over. An edge
        // joining two of the corridor's OWN nodes is incident to both, and decaying it
        // as each node was visited compounded the capture to (1 - s)^2.
        private static void PeelingDecaysAChordOnce()
        {
            // Corridor 0-1-2, plus a chord 0-2 that is not part of it.
            var a = new[] { 0, 1, 0 };
            var b = new[] { 1, 2, 2 };
            var cost = new[] { 10f, 10f, 25f };
            CompactGraph graph = CompactGraph.Build(3, a, b, cost, 3);

            var corridor = new Corridor();
            corridor.Edges.Add(0);
            corridor.Edges.Add(1);
            corridor.Nodes.Add(0);
            corridor.Nodes.Add(1);
            corridor.Nodes.Add(2);

            var flow = new[] { 100f, 100f, 100f };
            SuitabilityGraphMath.PeelFlow(graph, corridor, flow, new bool[3], 0.85f);

            // sideCapture is half the capture, so one application leaves 57.5.
            AssertEqual(57.5f, flow[2], 1e-3f, "the chord must lose exactly one side capture");
        }

        // Resize replaces every array the touched list addresses, so its count has to
        // go with them; carrying it over made the next search index past the new ones.
        private static void DijkstraWorkspaceResize()
        {
            CompactGraph big = BuildChain(out _);
            var ws = new DijkstraWorkspace(big.NodeCount);
            ws.Run(big, 0, 1000f);

            var a = new[] { 0, 1 };
            var b = new[] { 1, 2 };
            var cost = new[] { 3f, 4f };
            CompactGraph small = CompactGraph.Build(3, a, b, cost, 2);
            ws.Run(small, 0, 1000f);
            AssertEqual(7f, ws.Dist[2], 1e-4f, "a reused workspace must give the fresh answer on a smaller graph");

            var fresh = new DijkstraWorkspace(small.NodeCount);
            fresh.Run(small, 0, 1000f);
            AssertEqual(fresh.Dist[2], ws.Dist[2], 0f, "reuse must match a fresh instance");

            // And back up again, including the degenerate empty graph.
            ws.Run(new CompactGraph(), 0, 1000f);
            ws.Run(big, 0, 1000f);
            AssertEqual(5f, ws.Dist[5], 1e-4f, "the workspace must still be usable after an empty graph");
        }

        // Every node in the walk must be joined to the next by the edge in that slot,
        // which is what makes the corridor drawable as a polyline.
        private static void AssertNodeWalkMatchesEdges(CompactGraph graph, Corridor corridor)
        {
            AssertEqual(corridor.Edges.Count + 1, corridor.Nodes.Count, 0, "node count must be edge count + 1");
            for (int i = 0; i < corridor.Edges.Count; i++)
            {
                int edge = corridor.Edges[i];
                int from = corridor.Nodes[i];
                int to = corridor.Nodes[i + 1];
                bool joins = (graph.EdgeA[edge] == from && graph.EdgeB[edge] == to)
                    || (graph.EdgeB[edge] == from && graph.EdgeA[edge] == to);
                AssertTrue(joins, $"edge {edge} must join nodes {from} and {to}");
            }
        }

        // Judging a line: the thresholds below were all moved off fixed values after
        // they flagged most of a healthy network, so each test pins the relative rule
        // rather than the number it happens to produce.
        private static LineHealth Line(
            float usage,
            float peakUsage,
            int vehicles,
            int targetVehicles,
            int capacity = 100,
            ModePreset mode = ModePreset.Bus)
        {
            return new LineHealth
            {
                m_Mode = mode,
                m_Vehicles = vehicles,
                m_TargetVehicles = targetVehicles,
                m_Capacity = capacity,
                m_Passengers = (int)(usage * capacity),
                m_Usage = usage,
                m_PeakUsage = peakUsage,
                m_Stops = 10,
            };
        }

        // TransportLineFlags already says when the game wants more vehicles, so that
        // signal is read rather than re-derived.
        private static void VerdictFollowsTheGamesFlags()
        {
            LineVerdict verdict = SuitabilityLineHealth.Judge(
                usage: 0.4f, peakUsage: 0.5f, achievedInterval: 60f, targetInterval: 60f,
                longWaitMultiple: 2f, vehicles: 3, targetVehicles: 6,
                requireVehicles: false, notEnoughVehicles: true, emptyThreshold: 0.05f,
                out int addVehicles);

            AssertTrue(verdict == LineVerdict.Overcrowded, $"expected Overcrowded, got {verdict}");
            AssertEqual(3, addVehicles, 0, "the shortfall against the game's own target");
        }

        // Full AND already running the fleet the interval calls for: more vehicles are
        // not available, so the mode itself is the ceiling.
        private static void VerdictAtModeCapacity()
        {
            LineVerdict atCapacity = SuitabilityLineHealth.Judge(
                usage: 0.9f, peakUsage: 0.95f, achievedInterval: 60f, targetInterval: 60f,
                longWaitMultiple: 2f, vehicles: 6, targetVehicles: 6,
                requireVehicles: false, notEnoughVehicles: false, emptyThreshold: 0.05f,
                out _);
            AssertTrue(atCapacity == LineVerdict.AtModeCapacity, $"expected AtModeCapacity, got {atCapacity}");

            // The same load with room in the fleet is a service problem, not a mode one.
            LineVerdict crowded = SuitabilityLineHealth.Judge(
                usage: 0.9f, peakUsage: 0.95f, achievedInterval: 60f, targetInterval: 60f,
                longWaitMultiple: 2f, vehicles: 4, targetVehicles: 6,
                requireVehicles: false, notEnoughVehicles: false, emptyThreshold: 0.05f,
                out int addVehicles);
            AssertTrue(crowded == LineVerdict.Overcrowded, $"expected Overcrowded, got {crowded}");
            AssertEqual(2, addVehicles, 0, "vehicles to add");
        }

        // "Reroute or remove" is the most destructive advice the mod gives. A line
        // that fills twice a day and idles the rest averages out looking dead, so the
        // peak has to be low too.
        private static void VerdictEmptyNeedsALowPeakToo()
        {
            LineVerdict peaky = SuitabilityLineHealth.Judge(
                usage: 0.03f, peakUsage: 0.30f, achievedInterval: 60f, targetInterval: 60f,
                longWaitMultiple: 2f, vehicles: 4, targetVehicles: 4,
                requireVehicles: false, notEnoughVehicles: false, emptyThreshold: 0.05f,
                out _);
            AssertTrue(peaky != LineVerdict.NearlyEmpty,
                $"a line that fills at its peak must not be called empty, got {peaky}");

            LineVerdict dead = SuitabilityLineHealth.Judge(
                usage: 0.03f, peakUsage: 0.04f, achievedInterval: 60f, targetInterval: 60f,
                longWaitMultiple: 2f, vehicles: 4, targetVehicles: 4,
                requireVehicles: false, notEnoughVehicles: false, emptyThreshold: 0.05f,
                out _);
            AssertTrue(dead == LineVerdict.NearlyEmpty, $"expected NearlyEmpty, got {dead}");
        }

        // A 42 s bus and a 180 s train cannot share one stopwatch: the bar is a
        // multiple of the line's OWN target, and a wait too short to notice is never
        // worth reporting however badly the target is missed.
        private static void VerdictLongWaitsAreRelative()
        {
            LineVerdict late = SuitabilityLineHealth.Judge(
                usage: 0.4f, peakUsage: 0.5f, achievedInterval: 400f, targetInterval: 120f,
                longWaitMultiple: 2f, vehicles: 4, targetVehicles: 4,
                requireVehicles: false, notEnoughVehicles: false, emptyThreshold: 0.05f,
                out _);
            AssertTrue(late == LineVerdict.LongWaits, $"expected LongWaits, got {late}");

            // Three times a 20 s target is still only a 30 s wait.
            LineVerdict brisk = SuitabilityLineHealth.Judge(
                usage: 0.4f, peakUsage: 0.5f, achievedInterval: 60f, targetInterval: 20f,
                longWaitMultiple: 2f, vehicles: 4, targetVehicles: 4,
                requireVehicles: false, notEnoughVehicles: false, emptyThreshold: 0.05f,
                out _);
            AssertTrue(brisk == LineVerdict.Healthy,
                $"doubling a short headway is not worth reporting, got {brisk}");

            // A line missing a target the game never set has nothing to be late against.
            LineVerdict noTarget = SuitabilityLineHealth.Judge(
                usage: 0.4f, peakUsage: 0.5f, achievedInterval: 900f, targetInterval: 0f,
                longWaitMultiple: 2f, vehicles: 4, targetVehicles: 4,
                requireVehicles: false, notEnoughVehicles: false, emptyThreshold: 0.05f,
                out _);
            AssertTrue(noTarget == LineVerdict.Healthy, $"expected Healthy, got {noTarget}");
        }

        // Recommending fewer vehicles than the game is already asking for produced
        // "overcrowded — add 1 vehicle" beside "run it with 1 vehicle (-3)".
        private static void PlanRespectsTheGamesTarget()
        {
            LineHealth health = Line(usage: 0.9f, peakUsage: 0.95f, vehicles: 2, targetVehicles: 9);
            health.m_Verdict = LineVerdict.AtModeCapacity;

            SuitabilityLineHealth.ImprovePlan plan = SuitabilityLineHealth.Plan(
                health, lengthMetres: 6000f, roundTripSeconds: 1800f, capacityPerVehicle: 50, targetLoad: 0.7f);

            AssertTrue(plan.m_Vehicles >= 9, $"plan asks for {plan.m_Vehicles}, below the game's target of 9");
            AssertTrue(plan.m_VehicleDelta > 0, "a line short of vehicles is not told to shrink");
        }

        // Sizing from the instantaneous count condemned a ferry whose only boat was
        // mid-crossing to a fleet for nobody, even though the window said it was full.
        private static void PlanSizesFromThePeak()
        {
            LineHealth health = Line(usage: 0.8f, peakUsage: 0.9f, vehicles: 2, targetVehicles: 2, capacity: 200);
            health.m_Passengers = 0;   // caught between arrivals
            health.m_Verdict = LineVerdict.Overcrowded;

            SuitabilityLineHealth.ImprovePlan plan = SuitabilityLineHealth.Plan(
                health, lengthMetres: 4000f, roundTripSeconds: 600f, capacityPerVehicle: 100, targetLoad: 0.7f);

            // 0.9 * 200 = 180 riders at a 70% fill over 100-seat vehicles is 3.
            AssertTrue(plan.m_Vehicles >= 3,
                $"plan must carry the window's peak load, got {plan.m_Vehicles} vehicles");
        }

        // The player cannot set a vehicle count in Cities: Skylines II, so the
        // actionable number is the interval that produces the fleet.
        private static void PlanQuotesAnInterval()
        {
            LineHealth health = Line(usage: 0.5f, peakUsage: 0.6f, vehicles: 4, targetVehicles: 4);
            SuitabilityLineHealth.ImprovePlan plan = SuitabilityLineHealth.Plan(
                health, lengthMetres: 5000f, roundTripSeconds: 1200f, capacityPerVehicle: 60, targetLoad: 0.7f);

            AssertTrue(plan.m_IntervalSeconds > 0, "an interval must be quoted when the round trip is known");
            AssertEqual(1200f / plan.m_Vehicles, plan.m_IntervalSeconds, 1f,
                "the interval must be the one that yields the recommended fleet");

            // With no round trip there is no honest number to quote.
            SuitabilityLineHealth.ImprovePlan unknown = SuitabilityLineHealth.Plan(
                health, lengthMetres: 5000f, roundTripSeconds: 0f, capacityPerVehicle: 60, targetLoad: 0.7f);
            AssertTrue(unknown.m_IntervalSeconds < 0, "an unknown round trip is reported as such, not as zero");
        }

        // The panel assembles its sentence from these fields, so a payload short of
        // them renders "NaN" and "undefined" into the player's language.
        private static void PlanPayloadIsComplete()
        {
            LineHealth health = Line(usage: 0.5f, peakUsage: 0.6f, vehicles: 4, targetVehicles: 4);
            health.m_Stops = 30;
            SuitabilityLineHealth.ImprovePlan plan = SuitabilityLineHealth.Plan(
                health, lengthMetres: 3000f, roundTripSeconds: 900f, capacityPerVehicle: 60, targetLoad: 0.7f);

            string[] parts = SuitabilityLineHealth.PlanPayload(plan).Split('|');
            AssertEqual(7, parts.Length, 0, "the panel reads mode|vehicles|delta|interval|shape|value|spacing");
            AssertTrue(parts[0] == plan.m_Mode.ToString(), "field 0 is the mode token");
            AssertTrue(parts[4] == plan.m_Shape.ToString(), "field 4 is the shape token");
            for (int i = 0; i < parts.Length; i++)
            {
                AssertTrue(parts[i].Length > 0, $"field {i} must not be empty");
            }
        }

        // The walk pass is bucketed because route scoring rebuilds this network once per
        // candidate. A bucket that drops a pair silently un-links an interchange, so
        // the result is checked against the exhaustive sweep it replaced.
        private static void WalkEdgesMatchAnExhaustiveSweep()
        {
            var random = new Random(11);
            const int stops = 120;
            const float radius = 250f;
            var xs = new float[stops];
            var zs = new float[stops];
            for (int i = 0; i < stops; i++)
            {
                xs[i] = (float)(random.NextDouble() * 3000.0);
                zs[i] = (float)(random.NextDouble() * 3000.0);
            }

            TransitNetwork net = SuitabilityTransit.Build(
                xs, zs, stops, new List<TransitLine>(), radius, SuitabilityTransit.DefaultBoardPenaltySeconds);

            var built = new HashSet<long>();
            for (int e = 0; e < net.Graph.EdgeCount; e++)
            {
                if (net.EdgeKind[e] != TransitEdgeKind.Walk)
                {
                    continue;
                }

                int a = Math.Min(net.Graph.EdgeA[e], net.Graph.EdgeB[e]);
                int b = Math.Max(net.Graph.EdgeA[e], net.Graph.EdgeB[e]);
                AssertTrue(built.Add(((long)a << 32) | (uint)b), $"pair {a}-{b} must be joined once");
            }

            int expected = 0;
            for (int a = 0; a < stops; a++)
            {
                for (int b = a + 1; b < stops; b++)
                {
                    float dx = xs[a] - xs[b];
                    float dz = zs[a] - zs[b];
                    if ((dx * dx) + (dz * dz) > radius * radius)
                    {
                        AssertTrue(!built.Contains(((long)a << 32) | (uint)b), $"pair {a}-{b} is out of range");
                        continue;
                    }

                    expected++;
                    AssertTrue(built.Contains(((long)a << 32) | (uint)b), $"pair {a}-{b} is within range and must be joined");
                }
            }

            AssertEqual(expected, built.Count, 0, "exactly the pairs within the radius");
            AssertTrue(expected > 20, $"the fixture must actually produce interchanges, got {expected}");
        }

        private static FleetFacts RealFacts()
        {
            // Cities: Skylines II's own vehicle capacities and default intervals as the
            // mod reads them off the loaded prefabs; written down here only so the
            // harness has something to feed ChooseMode.
            var byMode = new ModeFacts[TransitModes.All.Length];
            byMode[(int)ModePreset.Bus] = new ModeFacts { Capacity = 80f, HeadwaySeconds = 300f, StopDurationSeconds = 15f, Acceleration = 1.5f, Braking = 1.5f };
            byMode[(int)ModePreset.Tram] = new ModeFacts { Capacity = 240f, HeadwaySeconds = 240f, StopDurationSeconds = 15f, Acceleration = 1.2f, Braking = 1.2f };
            byMode[(int)ModePreset.Metro] = new ModeFacts { Capacity = 540f, HeadwaySeconds = 200f, StopDurationSeconds = 20f, Acceleration = 1.2f, Braking = 1.2f };
            byMode[(int)ModePreset.Train] = new ModeFacts { Capacity = 1680f, HeadwaySeconds = 480f, StopDurationSeconds = 30f, Acceleration = 1f, Braking = 1f };
            byMode[(int)ModePreset.Ferry] = new ModeFacts { Capacity = 400f, HeadwaySeconds = 600f, StopDurationSeconds = 40f, Acceleration = 0.5f, Braking = 0.5f };
            return new FleetFacts(byMode);
        }

        private static void ModeClimbsTheLadderByCapacity()
        {
            FleetFacts facts = RealFacts();
            AssertTrue(TransitModes.ChooseMode(RouteNetwork.Road, 100f, facts, out ModePreset mode, out float utilisation) && mode == ModePreset.Bus,
                "a hundred riders a day fit a bus");
            AssertTrue(utilisation is > 0f and < 0.1f, $"bus utilisation {utilisation}");
            // 2000 riders: 4000 boardings against 4369/300·2·80 = 2330 bus seats a day.
            AssertTrue(TransitModes.ChooseMode(RouteNetwork.Road, 2000f, facts, out mode, out utilisation) && mode == ModePreset.Tram,
                "two thousand riders overload a bus and take the tram");
            AssertTrue(utilisation is > 0.4f and < 0.5f, $"tram utilisation {utilisation}");
            AssertTrue(TransitModes.ChooseMode(RouteNetwork.Road, 100000f, facts, out mode, out _) && mode == ModePreset.Tram,
                "when every road mode is overloaded the largest is still named");
            AssertTrue(TransitModes.ChooseMode(RouteNetwork.Rail, 5000f, facts, out mode, out _) && mode == ModePreset.Metro,
                "the rail ladder starts at the metro");
            AssertTrue(TransitModes.ChooseMode(RouteNetwork.Rail, 30000f, facts, out mode, out _) && mode == ModePreset.Train,
                "thirty thousand riders a day overload a metro and take the train");
            AssertTrue(TransitModes.ChooseMode(RouteNetwork.Water, 10f, facts, out mode, out _) && mode == ModePreset.Ferry, "water carries ferries only");

            var none = new FleetFacts(new ModeFacts[TransitModes.All.Length]);
            AssertTrue(!TransitModes.ChooseMode(RouteNetwork.Road, 100f, none, out _, out _), "no vehicle installed, no mode");
            AssertEqual(TransitModes.TargetHeadwayFor(ModePreset.Bus), none.HeadwayFor(ModePreset.Bus), 0f, "a missing line prefab falls back to the table headway");
        }

        private static void StopDelayAndRideLimits()
        {
            FleetFacts facts = RealFacts();
            // Bus: dwell 15 s, cruise 9 m/s, 1.5 m/s² each way: 15 + 3 + 3.
            AssertEqual(21f, facts.DelayPerStopSeconds(ModePreset.Bus), 1e-4f, "bus stop delay");
            AssertEqual(30f * 60f, TransitModes.MaxRideSecondsFor(ModePreset.Bus), 0f, "bus ride limit");
            AssertEqual(60f * 60f, TransitModes.MaxRideSecondsFor(ModePreset.Train), 0f, "train ride limit");
            // 9 km bus with 5 stops: 1000 s driving + 3 × 21 s.
            AssertEqual(1063f, TransitModes.RideSeconds(9000f, 5, 9f, 21f), 1e-3f, "ride seconds count intermediate stops only");
            AssertEqual(35f * 60f * 12f, TransitModes.MaxAlignmentMetresFor(RouteNetwork.Road), 1e-2f, "the road alignment bound is the tram's limit at cruise");
        }

        private static StopPlanProblem StraightLine(float length, float step, float minGap, float delay, float horizon)
        {
            int count = (int)Math.Round(length / step) + 1;
            var problem = new StopPlanProblem
            {
                CandidateCount = count,
                CandidateAt = new float[count],
                CandidateX = new float[count],
                CandidateZ = new float[count],
                MustCall = new bool[count],
                ThroughFlow = new float[count],
                MinGapMetres = minGap,
                DelaySecondsPerStop = delay,
                AccessHorizonSeconds = horizon,
                WalkMetresPerSecond = 1.2f,
            };
            for (int c = 0; c < count; c++)
            {
                problem.CandidateAt[c] = c * step;
                problem.CandidateX[c] = c * step;
            }

            return problem;
        }

        private static void AddEnd(StopPlanProblem problem, float x, float z, float weight)
        {
            int n = problem.EndCount;
            Array.Resize(ref problem.EndAt, n + 1);
            Array.Resize(ref problem.EndX, n + 1);
            Array.Resize(ref problem.EndZ, n + 1);
            Array.Resize(ref problem.EndWeight, n + 1);
            problem.EndAt[n] = x;
            problem.EndX[n] = x;
            problem.EndZ[n] = z;
            problem.EndWeight[n] = weight;
            problem.EndCount = n + 1;
        }

        private static void StopPlanWeighsBoardersAgainstThrough()
        {
            // 3 km line, candidates every 100 m, 200 through-riders everywhere, 20 s a stop
            // → a stop costs 4000 s·journeys. A door 60 m off the line at 1000 m with 50
            // journeys gains 50 × (360 − 50) = 15 500 with a stop there, against walking
            // ~1000 m to a terminus (nothing within the 6 min horizon): worth calling.
            // Ten journeys at 2000 m gain 10 × 310 = 3100: not worth 4000. Termini stay.
            StopPlanProblem problem = StraightLine(3000f, 100f, 175f, 20f, 360f);
            for (int c = 0; c < problem.CandidateCount; c++)
            {
                problem.ThroughFlow[c] = 200f;
            }

            AddEnd(problem, 1000f, 60f, 50f);
            AddEnd(problem, 2000f, 60f, 10f);
            StopPlanSolution plan = SuitabilityStopPlan.Solve(problem);
            AssertEqual(3, plan.Count, 0, $"three calls, got {string.Join(",", plan.Chosen)}");
            AssertTrue(plan.Chosen[0] == 0 && plan.Chosen[2] == problem.CandidateCount - 1, "termini are the ends");
            AssertEqual(10, plan.Chosen[1], 0, "the heavy door gets its stop at 1000 m");
            AssertTrue(Math.Abs(plan.Gain - (50.0 * 310.0)) < 1e-2, $"gain is the heavy door's saving ({plan.Gain}); the light door walks beyond the horizon");
            AssertTrue(Math.Abs(plan.Delay - (3 * 4000.0)) < 1e-6, $"three stops' delay ({plan.Delay})");

            // Make the through-riders scarce and the light door earns its stop too.
            for (int c = 0; c < problem.CandidateCount; c++)
            {
                problem.ThroughFlow[c] = 100f;
            }

            plan = SuitabilityStopPlan.Solve(problem);
            AssertEqual(4, plan.Count, 0, "with half the through-riders the light door is worth a call");
        }

        private static void StopPlanForcesTerminiAndHubs()
        {
            StopPlanProblem problem = StraightLine(2000f, 100f, 400f, 30f, 360f);
            for (int c = 0; c < problem.CandidateCount; c++)
            {
                problem.ThroughFlow[c] = 1000f;
            }

            problem.MustCall[7] = true;   // an interchange at 700 m, nobody boarding
            AddEnd(problem, 900f, 10f, 80f);    // 200 m past the hub: closer than the 400 m gap
            StopPlanSolution plan = SuitabilityStopPlan.Solve(problem);
            AssertTrue(Array.IndexOf(plan.Chosen, 7) >= 0, "the interchange is called at although nobody boards there");
            AssertTrue(Array.IndexOf(plan.Chosen, 9) < 0, "a stop 200 m past a forced call would break the gap floor");
            for (int i = 1; i < plan.Count; i++)
            {
                bool forcedPair = (plan.Chosen[i - 1] == 0 || problem.MustCall[plan.Chosen[i - 1]]) && (plan.Chosen[i] == problem.CandidateCount - 1 || problem.MustCall[plan.Chosen[i]]);
                AssertTrue(forcedPair || problem.CandidateAt[plan.Chosen[i]] - problem.CandidateAt[plan.Chosen[i - 1]] >= 400f, "consecutive calls keep the gap");
            }

            // A line shorter than the gap still has its two termini.
            StopPlanProblem stub = StraightLine(300f, 100f, 400f, 30f, 360f);
            StopPlanSolution stubPlan = SuitabilityStopPlan.Solve(stub);
            AssertEqual(2, stubPlan.Count, 0, "a stub keeps both ends");
        }

        private static void StopPlanMatchesBruteForce()
        {
            uint state = 7u;
            float Next(float max)
            {
                state = unchecked((state * 1664525u) + 1013904223u);
                return (state >> 8) / 16777216f * max;
            }

            for (int trial = 0; trial < 12; trial++)
            {
                int count = 5 + (int)Next(7f);
                StopPlanProblem problem = StraightLine((count - 1) * 100f, 100f, 150f + Next(200f), 5f + Next(30f), 300f + Next(200f));
                for (int c = 0; c < count; c++)
                {
                    problem.ThroughFlow[c] = Next(300f);
                    problem.MustCall[c] = c > 0 && c < count - 1 && Next(1f) < 0.15f;
                }

                int ends = 3 + (int)Next(10f);
                for (int e = 0; e < ends; e++)
                {
                    AddEnd(problem, Next((count - 1) * 100f), Next(300f) - 150f, 1f + Next(60f));
                }

                StopPlanSolution plan = SuitabilityStopPlan.Solve(problem);
                double best = double.NegativeInfinity;
                for (int mask = 0; mask < (1 << count); mask++)
                {
                    if ((mask & 1) == 0 || (mask & (1 << (count - 1))) == 0)
                    {
                        continue;
                    }

                    var chosen = new List<int>();
                    bool ok = true;
                    for (int c = 0; c < count; c++)
                    {
                        bool inSet = (mask & (1 << c)) != 0;
                        ok &= inSet || !problem.MustCall[c];
                        if (inSet)
                        {
                            chosen.Add(c);
                        }
                    }

                    for (int i = 1; i < chosen.Count && ok; i++)
                    {
                        bool forcedPair = (chosen[i - 1] == 0 || problem.MustCall[chosen[i - 1]]) && (chosen[i] == count - 1 || problem.MustCall[chosen[i]]);
                        ok &= forcedPair || problem.CandidateAt[chosen[i]] - problem.CandidateAt[chosen[i - 1]] >= problem.MinGapMetres;
                    }

                    if (!ok)
                    {
                        continue;
                    }

                    best = Math.Max(best, PlanValue(problem, chosen));
                }

                AssertTrue(Math.Abs(plan.Value - best) <= 1e-6 * Math.Max(1.0, Math.Abs(best)), $"trial {trial}: DP {plan.Value} vs brute force {best}");
                AssertTrue(Math.Abs(PlanValue(problem, new List<int>(plan.Chosen)) - plan.Value) <= 1e-6 * Math.Max(1.0, Math.Abs(best)), $"trial {trial}: the reported value is the plan's own");
            }
        }

        // The objective by definition: each end walks to the nearer of the two chosen
        // stops bracketing its projection.
        private static double PlanValue(StopPlanProblem problem, List<int> chosen)
        {
            double gain = 0.0;
            for (int e = 0; e < problem.EndCount; e++)
            {
                int after = -1;
                for (int i = 0; i < chosen.Count; i++)
                {
                    if (problem.EndAt[e] <= problem.CandidateAt[chosen[i]])
                    {
                        after = i;
                        break;
                    }
                }

                float walk;
                if (after < 0)
                {
                    walk = SuitabilityStopPlan.WalkSeconds(problem, e, chosen[chosen.Count - 1]);
                }
                else if (after == 0)
                {
                    walk = SuitabilityStopPlan.WalkSeconds(problem, e, chosen[0]);
                }
                else
                {
                    walk = Math.Min(SuitabilityStopPlan.WalkSeconds(problem, e, chosen[after - 1]), SuitabilityStopPlan.WalkSeconds(problem, e, chosen[after]));
                }

                gain += problem.EndWeight[e] * SuitabilityStopPlan.Kernel(problem, walk);
            }

            double delay = 0.0;
            foreach (int c in chosen)
            {
                delay += problem.ThroughFlow[c] * (double)problem.DelaySecondsPerStop;
            }

            return gain - delay;
        }

        // A hub in this game is several stop entities a few metres apart — the train
        // platform, the metro entrance below it, the bus stand out front. Reading one
        // stop's own mode calls the city's biggest interchange a train station.
        private static void AHubIsTheUnionOverAWalk()
        {
            // A train platform and a metro entrance 40 m apart, plus two lone bus stops
            // far from everything.
            var x = new float[] { 0f, 40f, 2000f, 0f };
            var z = new float[] { 0f, 0f, 0f, 3000f };
            var modes = new int[]
            {
                TransitModes.ModeBit(ModePreset.Train),
                TransitModes.ModeBit(ModePreset.Metro),
                TransitModes.ModeBit(ModePreset.Bus),
                TransitModes.ModeBit(ModePreset.Bus),
            };

            InterchangeMap map = SuitabilityTransit.BuildInterchangeMap(x, z, modes, x.Length, 250f);
            AssertTrue(map.Count == 4, $"every served stop is in the map, got {map.Count}");

            AssertTrue(map.TryFindNear(ModePreset.Bus, 10f, 0f, 250f, out float hubX, out float hubZ, out _),
                "a new bus line beside the station can change to something");
            AssertTrue(hubX == 0f && hubZ == 0f, $"and it is aimed at the nearest of the pair, got ({hubX}, {hubZ})");

            // Read on its own, the metro entrance is a metro stop and offers a new
            // metro line nothing. The union is what makes it a place where that line's
            // riders can reach a train — and the entrance itself is the nearest such
            // place, which is exactly where the terminus should go.
            AssertTrue(map.TryFindNear(ModePreset.Metro, 40f, 0f, 250f, out float trainX, out _, out _),
                "a new metro line there can change to the train beside it");
            AssertTrue(trainX == 40f, $"aimed at the nearest place the train is reachable from, got {trainX}");

            // A new bus line calling at an existing bus stop lets riders change to
            // whatever line already runs there — a suggestion is never the line that is
            // already present. Mode decides the ranking, not whether it counts.
            AssertTrue(map.TryFindNear(ModePreset.Bus, 2000f, 0f, 250f, out float sameX, out _, out _),
                "a new bus line can still change lines at an existing bus stop");
            AssertTrue(sameX == 2000f, $"and it is the stop that is there, got {sameX}");

            AssertTrue(!map.TryFindNear(ModePreset.Bus, 5000f, 5000f, 250f, out _, out _, out _),
                "and nothing is in reach out in the fields");
        }

        // Ranking a hub against a lone stop. Nearest-wins would take the tram stop
        // 40 m away over the interchange 180 m away that reaches three modes.
        private static void TheBiggerInterchangeWins()
        {
            // The lone tram has to sit further than a walk from every hub member, or
            // it is not lone — it joins the hub, which is the map working correctly.
            var x = new float[] { 0f, 30f, 60f, 400f };
            var z = new float[] { 0f, 0f, 0f, 0f };
            var modes = new int[]
            {
                TransitModes.ModeBit(ModePreset.Train),
                TransitModes.ModeBit(ModePreset.Metro),
                TransitModes.ModeBit(ModePreset.Tram),
                TransitModes.ModeBit(ModePreset.Tram),
            };

            InterchangeMap map = SuitabilityTransit.BuildInterchangeMap(x, z, modes, x.Length, 250f);
            AssertTrue(map.TryFindNear(ModePreset.Bus, 250f, 0f, 250f, out float hubX, out _, out _),
                "there is somewhere to change within a walk");
            AssertTrue(hubX == 60f,
                $"the three-mode interchange 190 m off wins over the lone tram stop 150 m off, got {hubX}");

            // Among equals, distance decides.
            var pairX = new float[] { 0f, 30f, 400f, 430f };
            var pairZ = new float[] { 0f, 0f, 0f, 0f };
            var pairModes = new int[]
            {
                TransitModes.ModeBit(ModePreset.Train),
                TransitModes.ModeBit(ModePreset.Metro),
                TransitModes.ModeBit(ModePreset.Train),
                TransitModes.ModeBit(ModePreset.Metro),
            };

            InterchangeMap pairs = SuitabilityTransit.BuildInterchangeMap(pairX, pairZ, pairModes, 4, 250f);
            AssertTrue(pairs.TryFindNear(ModePreset.Bus, 380f, 0f, 250f, out float nearX, out _, out _),
                "both interchanges offer the same two modes");
            AssertTrue(nearX == 400f, $"so the nearer one is chosen, got {nearX}");
        }

        // "Already served" has to mean slow FOR HERE. Against a fixed hour a ten-minute
        // trip kept a sixth of its weight whether the city was three kilometres across
        // or thirty, so a compact well-served city absorbed 94% of all its travel.
        private static void ServedCeilingScalesToTheCity()
        {
            // A brisk city: journeys carried in about three minutes.
            var brisk = new float[40];
            for (int i = 0; i < brisk.Length; i++)
            {
                brisk[i] = 180f + i;
            }

            float ceiling = SuitabilityTransit.ServedCeiling(
                brisk, brisk.Length, multiple: 3f, fallback: 3600f, minSamples: 20, out float median);
            AssertEqual(200f, median, 20f, "the median of the carried journeys");
            AssertEqual(median * 3f, ceiling, 1e-3f, "the ceiling is a multiple of it");
            AssertTrue(ceiling < 3600f, "and well inside the fixed hour");

            // The same journey is discounted far less here than against the fixed hour.
            float keptNow = 180f / ceiling;
            float keptBefore = 180f / 3600f;
            AssertTrue(keptNow > keptBefore * 3f,
                $"a typical journey must keep meaningfully more of its weight ({keptNow} vs {keptBefore})");

            // A slow city's ceiling is capped at the point a trip stops being transit.
            var slow = new float[40];
            for (int i = 0; i < slow.Length; i++)
            {
                slow[i] = 2000f;
            }

            float slowCeiling = SuitabilityTransit.ServedCeiling(
                slow, slow.Length, multiple: 3f, fallback: 3600f, minSamples: 20, out _);
            AssertEqual(3600f, slowCeiling, 1e-3f, "never past the router's own horizon");
        }

        // A median over a handful of journeys is noise, which is the risk this approach
        // carries on a thin network.
        private static void ServedCeilingFallsBack()
        {
            var few = new[] { 100f, 120f, 140f };
            AssertEqual(
                3600f,
                SuitabilityTransit.ServedCeiling(few, few.Length, 3f, 3600f, minSamples: 20, out float median),
                1e-3f,
                "too few carried journeys falls back to the fixed hour");
            AssertEqual(0f, median, 0f, "and reports no median, rather than a misleading one");

            AssertEqual(
                3600f,
                SuitabilityTransit.ServedCeiling(Array.Empty<float>(), 0, 3f, 3600f, 20, out _),
                1e-3f,
                "a network carrying nothing falls back too");
        }

        // Growth picks the best adjacent edge on flow and novelty alone, and on a
        // lattice — a 128 m grid where flow is thin and nearly uniform — the tiniest
        // difference between two edges steers it. Corridors staircased across the whole
        // city, which is not an alignment anyone would build and not something the
        // polyline simplifier can repair afterwards: the corridor really did go there.
        private static void GrowthPrefersToCarryStraightOn()
        {
            // 0-1-2-3 runs east; 1-4 branches due north and carries MORE.
            var a = new[] { 0, 1, 2, 1 };
            var b = new[] { 1, 2, 3, 4 };
            var cost = new[] { 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(5, a, b, cost, 4);
            var flow = new[] { 20f, 10f, 10f, 12f };
            var x = new[] { 0f, 100f, 200f, 300f, 100f };
            var z = new[] { 0f, 0f, 0f, 0f, 100f };

            // With no positions there is no direction to prefer, and the busier branch
            // wins — the behaviour every caller had before positions existed.
            var blind = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, (float[])flow.Clone(), new bool[4], NewNovelty(5)),
                0f, 1f, 10000f, blind);
            AssertTrue(blind.Nodes.Contains(4), "without positions the busier turn is taken");

            // With them, a right-angle turn has to be worth appreciably more.
            var straight = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, (float[])flow.Clone(), new bool[4], NewNovelty(5), null, x, z),
                0f, 1f, 10000f, straight);
            AssertTrue(!straight.Nodes.Contains(4),
                $"a 20% busier right-angle turn is not worth leaving the alignment for, got [{string.Join(",", straight.Nodes)}]");
            AssertTrue(straight.Nodes.Contains(3), "and the corridor carries on east instead");

            // A turn that is genuinely much busier is still taken: this is a
            // preference, not a constraint.
            var worthIt = new[] { 20f, 10f, 10f, 60f };
            var turned = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, worthIt, new bool[4], NewNovelty(5), null, x, z),
                0f, 1f, 10000f, turned);
            AssertTrue(turned.Nodes.Contains(4), "a far busier direction still wins");
        }

        // The turn penalty is local: it stops a staircase but says nothing about the
        // shape overall, and a corridor can carry smoothly round a long arc back to
        // where it started. On a lattice — a uniform grid where the shortest path
        // between two zones is degenerate and the flow ridges are an artifact of the
        // grid rather than of where anyone travels — that is exactly what happened: a
        // metro proposed as a box around an empty field.
        private static void GrowthHeadsAwayFromItsOtherEnd()
        {
            // The corridor runs east from (0,0) to (200,0), then north to (200,200).
            // From there both options are 45-degree turns, so the turn penalty scores
            // them IDENTICALLY — only which way they go relative to the far end can
            // tell them apart. Node 3 carries on outward; node 4 curls back towards it.
            var a = new[] { 0, 1, 2, 2 };
            var b = new[] { 1, 2, 3, 4 };
            var cost = new[] { 200f, 200f, 141f, 141f };
            CompactGraph graph = CompactGraph.Build(5, a, b, cost, 4);
            var x = new[] { 0f, 200f, 200f, 300f, 100f };
            var z = new[] { 0f, 0f, 200f, 300f, 300f };

            // The curl carries a fifth more than the continuation.
            var flow = new[] { 30f, 20f, 10f, 12f };

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, (float[])flow.Clone(), new bool[4], NewNovelty(5), null, x, z),
                0f, 1f, 10000f, corridor);
            AssertTrue(corridor.Nodes.Contains(3),
                $"the corridor must head onward, got [{string.Join(",", corridor.Nodes)}]");
            AssertTrue(!corridor.Nodes.Contains(4),
                "and must not curl back towards the end it started from");

            // Without positions there is nothing to measure and the busier curl wins,
            // which is the behaviour every caller had before positions existed.
            var blind = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, (float[])flow.Clone(), new bool[4], NewNovelty(5)),
                0f, 1f, 10000f, blind);
            AssertTrue(blind.Nodes.Contains(4), "without positions the busier curl is taken");
        }

        // Where growth had no alternative it still comes round, so the shape is checked
        // once it is finished. A line whose ends nearly meet is a ring, not a route.
        // A metro passing two kilometres from the train station was never diverted to
        // reach it: an alignment was traced end to end and only its STOPS were ever
        // adjusted, so a line that missed the interchange missed the network. Bending
        // it through the hub is worth doing — but only while the people already on
        // board can stand the extra minutes, which is what this bound is.
        private static void ADetourIsWorthOnlySoMuch()
        {
            const float direct = 10000f;
            const float maxLength = 20000f;

            AssertTrue(
                SuitabilityGraphMath.IsDetourWorthwhile(direct, direct, maxLength),
                "a via that costs nothing extra is always worth taking");
            AssertTrue(
                SuitabilityGraphMath.IsDetourWorthwhile(direct, direct * 1.1f, maxLength),
                "a tenth further to reach an interchange is worth it");

            AssertTrue(
                SuitabilityGraphMath.IsDetourWorthwhile(direct, direct * SuitabilityGraphMath.MaxViaDetour, maxLength),
                "the bound itself is allowed");
            AssertTrue(
                !SuitabilityGraphMath.IsDetourWorthwhile(direct, direct * (SuitabilityGraphMath.MaxViaDetour + 0.01f), maxLength),
                "and a line that goes noticeably out of its way is not");

            // A hub two kilometres to one side costs roughly four kilometres of extra
            // running. That pays on a long line and not on a short one, which is what
            // lets the reach radius stay a single generous constant.
            AssertTrue(
                SuitabilityGraphMath.IsDetourWorthwhile(20000f, 24000f, 60000f),
                "reaching it off a twenty-kilometre line is worth four kilometres");
            AssertTrue(
                !SuitabilityGraphMath.IsDetourWorthwhile(4000f, 8000f, 60000f),
                "doubling a four-kilometre line for the same hub is not");

            // The mode's own ceiling still applies: a line it cannot hold a headway
            // around is no use however modest the detour.
            AssertTrue(
                !SuitabilityGraphMath.IsDetourWorthwhile(direct, direct * 1.05f, 9000f),
                "past the mode's maximum length nothing is worthwhile");

            // Degenerate input is not a licence to wander.
            AssertTrue(!SuitabilityGraphMath.IsDetourWorthwhile(0f, 5000f, maxLength), "no direct path, no detour");
            AssertTrue(!SuitabilityGraphMath.IsDetourWorthwhile(direct, 0f, maxLength), "a via of no length is not a path");
        }

        private static void RingsAreNotRoutes()
        {
            // A 4 km line that gets 3.6 km away from where it began is a route.
            AssertTrue(SuitabilityGraphMath.IsDirectEnough(3600f, 4000f), "a line that gets somewhere");

            // The same 4 km spent going round a block is not.
            AssertTrue(!SuitabilityGraphMath.IsDirectEnough(800f, 4000f), "a loop around a field is not a route");
            AssertTrue(!SuitabilityGraphMath.IsDirectEnough(0f, 4000f), "and a closed ring least of all");

            // Exactly at the bar counts, and a zero-length corridor is nothing to judge.
            AssertTrue(
                SuitabilityGraphMath.IsDirectEnough(4000f * SuitabilityGraphMath.MinDirectness, 4000f),
                "the bar itself passes");
            AssertTrue(SuitabilityGraphMath.IsDirectEnough(0f, 0f), "nothing to judge");
        }

        private static float[] NewNovelty(int nodes)
        {
            var novelty = new float[nodes];
            for (int i = 0; i < nodes; i++)
            {
                novelty[i] = 1f;
            }

            return novelty;
        }

        // A ferry with one boat reads zero passengers whenever that boat is mid
        // crossing. Judged on the reading the refresh happened to land on, the line
        // is condemned as "nearly empty — reroute or remove"; judged over a day it is
        // simply a small line that is busy some of the time.
        private static void WindowAveragesLineReadings()
        {
            var history = new LineHistory(LineHistory.FramesPerGameDay);

            // Six readings a few game hours apart: full, empty, full, empty, ...
            int[] aboard = { 80, 0, 60, 0, 40, 0 };
            for (int i = 0; i < aboard.Length; i++)
            {
                history.Record(7, new LineObservation
                {
                    m_Frame = (uint)(i * 10000),
                    m_Passengers = aboard[i],
                    m_Capacity = 100,
                    m_IntervalSeconds = 300f,
                    m_Vehicles = 1,
                });
            }

            AssertTrue(history.TryAverage(7, out LineAverage average), "the line has history");
            AssertTrue(average.m_Samples == 6, "every reading inside the window counts");
            AssertEqual(30f, average.m_Passengers, 1e-3f, "mean passengers over the window");
            AssertEqual(0.3f, average.m_Usage, 1e-3f, "mean usage, not the reading it landed on");
            AssertEqual(0.8f, average.m_PeakUsage, 1e-3f, "the busiest sample is kept alongside the mean");
            AssertEqual(300f, average.m_IntervalSeconds, 1e-3f, "interval averages too");

            // The verdict must know how much of a day it is actually looking at.
            AssertTrue(average.m_SpanFrames == 50000u, "the span covered is reported");
            AssertTrue(LineHistory.GameHours(average.m_SpanFrames) < 24f, "under a full day of coverage");

            AssertTrue(!history.TryAverage(99, out LineAverage _), "a line never seen has no history");
        }

        private static void WindowEvictsPastADay()
        {
            var history = new LineHistory(1000u);

            // Three readings, then one a full window later: only the last survives
            // together with anything inside the window behind it.
            history.Record(1, Reading(0u, 10));
            history.Record(1, Reading(700u, 20));
            history.Record(1, Reading(900u, 30));
            AssertTrue(history.TryAverage(1, out LineAverage before), "history exists");
            AssertTrue(before.m_Samples == 3, "nothing evicted while inside the window");

            // Window 1000, newest 1600, so the cutoff is 600 and only the reading at
            // frame 0 falls out.
            history.Record(1, Reading(1600u, 40));
            AssertTrue(history.TryAverage(1, out LineAverage after), "history survives eviction");
            AssertTrue(after.m_Samples == 3, "the reading older than the window is gone");
            AssertEqual(30f, after.m_Passengers, 1e-3f, "the evicted reading no longer weighs on the mean");
            AssertTrue(history.EvictedSinceLastReport == 1, "the eviction is counted so the log can say so");

            // A window that has nothing left in it must not report a stale average.
            history.Record(1, Reading(100000u, 5));
            AssertTrue(history.TryAverage(1, out LineAverage far), "the newest reading is kept");
            AssertTrue(far.m_Samples == 1, "everything a window older than the newest is dropped");
            AssertEqual(5f, far.m_Passengers, 1e-3f, "only the surviving reading counts");
        }

        // A line whose fleet doubles mid-window: 10/100 then 90/200. A ratio of sums
        // says 100/300 = 33%; the honest answer is the mean of 10% and 45%.
        private static void WindowUsageIsPerSample()
        {
            var history = new LineHistory(LineHistory.FramesPerGameDay);
            history.Record(3, new LineObservation
            {
                m_Frame = 0u,
                m_Passengers = 10,
                m_Capacity = 100,
                m_IntervalSeconds = 120f,
                m_Vehicles = 1,
            });
            history.Record(3, new LineObservation
            {
                m_Frame = 5000u,
                m_Passengers = 90,
                m_Capacity = 200,
                m_IntervalSeconds = 60f,
                m_Vehicles = 2,
            });

            AssertTrue(history.TryAverage(3, out LineAverage average), "history exists");
            AssertEqual(0.275f, average.m_Usage, 1e-4f, "mean of the per-sample usages");
            AssertEqual(0.45f, average.m_PeakUsage, 1e-4f, "the fuller sample is the peak");
            AssertEqual(1.5f, average.m_Vehicles, 1e-4f, "fleet size averages across the change");
        }

        // The simulation frame counts up within one city and rewinds when another
        // save is loaded. Averaging the previous city's lines into this one would be
        // worse than starting over.
        private static void WindowResetsWhenFramesRewind()
        {
            var history = new LineHistory(LineHistory.FramesPerGameDay);
            history.Record(2, Reading(500000u, 80));
            history.Record(2, Reading(500100u, 80));
            AssertTrue(history.TryAverage(2, out LineAverage loaded), "the first city has history");
            AssertEqual(80f, loaded.m_Passengers, 1e-3f, "from the first city");

            history.Record(2, Reading(120u, 4));
            AssertTrue(history.TryAverage(2, out LineAverage fresh), "the new city starts recording");
            AssertTrue(fresh.m_Samples == 1, "the previous city's readings are gone");
            AssertEqual(4f, fresh.m_Passengers, 1e-3f, "only this city's reading counts");
        }

        // Lines the player has deleted stop being tracked, so the history is bounded
        // by the network that exists rather than by everything ever built.
        private static void WindowForgetsDeletedLines()
        {
            var history = new LineHistory(LineHistory.FramesPerGameDay);
            history.Record(1, Reading(0u, 10));
            history.Record(2, Reading(0u, 20));
            AssertTrue(history.TrackedLines == 2, "both lines tracked");

            history.RetainOnly(new System.Collections.Generic.HashSet<int> { 2 });
            AssertTrue(history.TrackedLines == 1, "the deleted line is forgotten");
            AssertTrue(!history.TryAverage(1, out LineAverage _), "and has no history left");
            AssertTrue(history.TryAverage(2, out LineAverage _), "the surviving line keeps its history");
        }

        private static LineObservation Reading(uint frame, int passengers)
        {
            return new LineObservation
            {
                m_Frame = frame,
                m_Passengers = passengers,
                m_Capacity = 100,
                m_IntervalSeconds = 120f,
                m_Vehicles = 1,
            };
        }

        // A short corridor and a long one arrive as nothing but a number of metres,
        // and "ran out of demand" calls for the opposite response to "hemmed in by the
        // corridor grown before it". Growth therefore records which test refused the
        // last extension it looked for.
        private static void CorridorRecordsWhyItStopped()
        {
            // A chain of five 100 m edges. Only the first two carry any flow, so growth
            // must stop at the flow floor rather than for want of a graph.
            var a = new[] { 0, 1, 2, 3, 4 };
            var b = new[] { 1, 2, 3, 4, 5 };
            var cost = new[] { 100f, 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(6, a, b, cost, 5);
            var flow = new[] { 50f, 50f, 0f, 0f, 0f };

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flow, new bool[5], NewNovelty(6)), 0f, 10f, 100000f, corridor);

            AssertTrue(corridor.Edges.Count == 2, $"only the two edges with flow are taken, got {corridor.Edges.Count}");
            AssertTrue(!corridor.Blocks.m_HitMaxLength, "it stopped for want of flow, not at the length limit");
            AssertTrue(corridor.Blocks.m_Flow > 0, "the refusal is attributed to the flow floor");
            AssertTrue(corridor.Blocks.m_Used == 0, "nothing here was spent by an earlier corridor");
            AssertTrue(corridor.Blocks.m_Demand == 0, "no demand gate was in play");

            // The same graph with flow everywhere but a tight length limit stops for
            // the other reason, and says so.
            var flowing = new[] { 50f, 50f, 50f, 50f, 50f };
            var capped = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(new CorridorNetwork(graph, flowing, new bool[5], NewNovelty(6)), 0f, 10f, 250f, capped);

            AssertTrue(capped.Blocks.m_Length > 0 || capped.Blocks.m_HitMaxLength,
                "a corridor stopped by its length limit reports the length, not the flow");
            AssertTrue(capped.Blocks.m_Flow == 0, "nothing here was below the flow floor");
        }

        // Two busy districts with a park between them are one bus route, not two.
        // The demand gate was a per-node veto, so a corridor stopped dead at the first
        // quiet junction: on a real street grid it refused more extensions than the
        // flow floor and the used-edge test put together, and no corridor came near
        // its length limit. Everything then died against the minimum length for a
        // mode, which is why whole refreshes suggested nothing at all.
        private static void CorridorBridgesQuietGap()
        {
            // 0-1-2 busy, 2-3 and 3-4 cross an empty park, 4-5-6 busy again.
            var a = new[] { 0, 1, 2, 3, 4, 5 };
            var b = new[] { 1, 2, 3, 4, 5, 6 };
            var cost = new[] { 100f, 100f, 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(7, a, b, cost, 6);
            var flow = new[] { 50f, 50f, 50f, 50f, 50f, 50f };
            var demand = new[] { 1f, 1f, 1f, 0f, 0f, 1f, 1f };

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, flow, new bool[6], NewNovelty(7), demand), 0f, 1f, 10000f, corridor, 0.5f);

            AssertTrue(corridor.Nodes.Contains(0), "the first district is served");
            AssertTrue(corridor.Nodes.Contains(6), "the district across the gap is reached");
            AssertTrue(corridor.Nodes.Contains(3) && corridor.Nodes.Contains(4),
                "the quiet nodes are crossed rather than ending the line");
            AssertTrue(corridor.Edges.Count == 6, $"the whole chain is one corridor, got {corridor.Edges.Count} edges");

            // A gap wider than the bridge allowance is still refused: this is a
            // crossing, not a licence to strike out into open country.
            var wide = new[] { 1f, 1f, 1f, 0f, 0f, 0f, 0f };
            var stopped = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, (float[])flow.Clone(), new bool[6], NewNovelty(7), wide), 0f, 1f, 10000f,
                stopped, 0.5f, seedNoveltyBias: 0f, maxLowDemandBridge: 2);

            AssertTrue(!stopped.Nodes.Contains(6), "a gap wider than the allowance is not crossed");
        }

        // Crossing emptiness is only justified by what lies beyond it. A corridor that
        // walks into a park and never comes out must hand back those edges, or the
        // line ends at a stop in the middle of a field.
        private static void CorridorDoesNotEndInEmptiness()
        {
            var a = new[] { 0, 1, 2, 3 };
            var b = new[] { 1, 2, 3, 4 };
            var cost = new[] { 100f, 100f, 100f, 100f };
            CompactGraph graph = CompactGraph.Build(5, a, b, cost, 4);
            var flow = new[] { 50f, 50f, 50f, 50f };
            // Busy to node 2, then nothing at all beyond it.
            var demand = new[] { 1f, 1f, 1f, 0f, 0f };

            var corridor = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                new CorridorNetwork(graph, flow, new bool[4], NewNovelty(5), demand), 0f, 1f, 10000f, corridor, 0.5f);

            AssertTrue(!corridor.Nodes.Contains(3) && !corridor.Nodes.Contains(4),
                "a crossing that leads nowhere is handed back");
            AssertEqual(200f, corridor.Length, 1e-3f, "and its length with it");
        }

        // ---- harness --------------------------------------------------------

        private static int CountSaturated(byte[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == 255)
                {
                    count++;
                }
            }
            return count;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A test runner must report any failure and continue; narrowing this " +
                "would let one unexpected exception abort the whole suite.")]
        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (TestFailedException failure)
            {
                s_Failures++;
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {failure.Message}");
            }
            catch (Exception ex)
            {
                // Distinguished from an assertion failure on purpose: a crash in the
                // code under test is a different problem from a property not holding,
                // and printing them identically hid which one had happened.
                s_Failures++;
                Console.WriteLine($"  CRASH {name}");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AssertTrue(bool condition, string because)
        {
            if (!condition)
            {
                throw new TestFailedException(because);
            }
        }

        // GOLDEN VECTOR, produced by the other side of the contract:
        //   json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        // then sha256 of its ASCII bytes (verification/common/canonical.py). If this
        // test fails, every instance the mod exports becomes unloadable — the pipeline
        // recomputes this digest on load and refuses a file it cannot reproduce.
        private const string GoldenCanonical =
            "{\"data\":{\"label\":\"Gr\\u00fc\\u00dfe \\\"x\\\" \\\\ y\",\"values_b32\":"
            + "[1069547520,2147483648]},\"kind\":\"demo\",\"name\":\"golden\",\"schema_version\":1}";

        private const string GoldenHash =
            "ed592da4e2e64bf1a8dde36156ecdacd83812eb864ca1a1b75fdc9a802c453db";

        private static SuitabilityJsonObject GoldenBody()
        {
            string data = new SuitabilityJsonObject()
                // Added out of order on purpose: the writer sorts, so the canonical
                // form must not depend on the order the export code gathers things in.
                .Add("values_b32", SuitabilityExportJson.BitsArray(new[] { 1.5f, -0.0f }))
                .Add("label", SuitabilityExportJson.Str("Grüße \"x\" \\ y"))
                .Build();

            return new SuitabilityJsonObject()
                .Add("name", SuitabilityExportJson.Str("golden"))
                .Add("kind", SuitabilityExportJson.Str("demo"))
                .Add("data", data);
        }

        private static void CapacityWeightIsRelativeToBus()
        {
            // CS2's base subway (1080 seats) against its bus (80): 13.5 buses' worth.
            AssertEqual(13.5f, TransitModes.CapacityWeight(1080f, 80f), 1e-6f, "subway vs bus");
            AssertEqual(1f, TransitModes.CapacityWeight(80f, 80f), 1e-6f, "a bus is one bus");
            AssertEqual(0f, TransitModes.CapacityWeight(0f, 80f), 0f, "unknown mode is no partner");
            AssertEqual(0f, TransitModes.CapacityWeight(1080f, 0f), 0f, "no bus to compare against");
        }

        private static void ExportJsonIsCanonical()
        {
            SuitabilityJsonObject body = GoldenBody();
            _ = body.Add("schema_version", SuitabilityExportJson.Int(1));
            string canonical = body.Build();
            if (!string.Equals(canonical, GoldenCanonical, StringComparison.Ordinal))
            {
                throw new TestFailedException(
                    $"canonical form drifted from canonical.py:\n  got      {canonical}\n  expected {GoldenCanonical}");
            }

            string hash = SuitabilityExportJson.Sha256Hex(canonical);
            if (!string.Equals(hash, GoldenHash, StringComparison.Ordinal))
            {
                throw new TestFailedException($"digest drifted: {hash} != {GoldenHash}");
            }
        }

        private static void ExportJsonEscapes()
        {
            // Control characters, the short forms, a surrogate pair, and lowercase hex
            // — all four are what ensure_ascii=True produces.
            AssertJson("\"\\u0000\\b\\t\\n\\f\\r\"", SuitabilityExportJson.Str("\0\b\t\n\f\r"));
            AssertJson("\"\\ud83d\\ude00\"", SuitabilityExportJson.Str("\U0001F600"));
            AssertJson("\"~\"", SuitabilityExportJson.Str("~"));
            AssertJson("null", SuitabilityExportJson.Str(null));
        }

        private static void ExportJsonBitsRoundTrip()
        {
            float[] values = { 0f, -0f, 1f, -1.5f, 3.4028235e38f, 1.4e-45f, 128f * 1.41421356f };
            foreach (float value in values)
            {
                uint bits = SuitabilityExportJson.ToBits(value);
                byte[] bytes = BitConverter.GetBytes(bits);
                float back = BitConverter.ToSingle(bytes, 0);
                if (!back.Equals(value))
                {
                    throw new TestFailedException($"bit pattern for {value} did not round-trip");
                }
            }
        }

        private static void ExportJsonHashesBody()
        {
            string file = GoldenBody().BuildHashed(1);
            // The digest is OVER THE BODY, so it must be the golden hash, and the file
            // must still contain the body verbatim after the inserted member.
            if (!file.Contains(GoldenHash, StringComparison.Ordinal))
            {
                throw new TestFailedException("the written instance does not carry the body's digest");
            }

            if (!file.StartsWith("{\"hash\":", StringComparison.Ordinal)
                || !file.EndsWith(GoldenCanonical.Substring(1), StringComparison.Ordinal))
            {
                throw new TestFailedException($"the written instance is not body-plus-digest: {file}");
            }
        }

        private static void AssertJson(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new TestFailedException($"expected {expected}, got {actual}");
            }
        }

        private static void AssertEqual(float expected, float actual, float tolerance, string because)
        {
            if (float.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            {
                throw new TestFailedException($"{because}: expected {expected}, got {actual}");
            }
        }

        private static void AssertEqual(int expected, int actual, int tolerance, string because)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new TestFailedException($"{because}: expected {expected}, got {actual}");
            }
        }
    }
}
