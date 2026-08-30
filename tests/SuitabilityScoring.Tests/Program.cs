using System;
using System.Collections.Generic;

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
            Run("A feeder line is credited for journeys it only starts", FeederGetsCredit);
            Run("Transfer discount reduces credit per change", TransferDiscountApplies);
            Run("Walking links nearby stops into one interchange", WalkLinksStops);
            Run("Bucketed walk edges match an exhaustive sweep", WalkEdgesMatchAnExhaustiveSweep);
            Run("Vanilla wait model floors at zero", ExpectedWaitModel);
            Run("A proposed stop puts an unserved zone onto the network", RemapReachesUnservedZone);
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
            Run("The plan payload carries every field the panel reads", PlanPayloadIsComplete);

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
            AssertTrue(SuitabilityGraphMath.GrowCorridor(graph, flow, used, novelty, 0f, 2f, 1000f, corridor),
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
            AssertTrue(SuitabilityGraphMath.GrowCorridor(graph, flow, used, NewNovelty(5), 0f, 1f, 1000f, corridor),
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
            _ = SuitabilityGraphMath.GrowCorridor(graph, flow, new bool[5], NewNovelty(6), 0f, 1f, 250f, corridor);

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
            _ = SuitabilityGraphMath.GrowCorridor(graph, flow, new bool[4], NewNovelty(5), 0f, 1f, 10000f, corridor);

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
            _ = SuitabilityGraphMath.GrowCorridor(graph, flow, new bool[4], NewNovelty(5), 0f, 1f, 10000f, ungated);
            AssertTrue(ungated.Edges.Count == 4, "without the gate it runs the whole chain including empty land");

            var gated = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(graph, (float[])flow.Clone(), new bool[4], NewNovelty(5), 0f, 1f, 10000f,
                gated, demand, 0.5f);

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
            _ = SuitabilityGraphMath.GrowCorridor(graph, flow, used, NewNovelty(4), 0f, 1f, 1000f, corridor);
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
            bool grew = SuitabilityGraphMath.GrowCorridor(graph, flow, used, NewNovelty(4), 0f, 1f, 1000f, second);
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
            _ = SuitabilityGraphMath.GrowCorridor(graph, flowA, new bool[3], NewNovelty(4),
                SuitabilityGraphMath.NoveltyWeight(RouteObjective.Ridership, 70f), 0f, 1000f, ridership);

            var coverage = new Corridor();
            var flowB = new[] { 100f, 90f, 20f };
            // Node 3 is virgin territory; nodes 0-2 are already covered.
            var novelty = new[] { 0.05f, 0.05f, 0.05f, 1f };
            _ = SuitabilityGraphMath.GrowCorridor(graph, flowB, new bool[3], novelty,
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
                    SuitabilityGraphMath.GrowCorridor(graph, flow, used, novelty, weight, 0f, 250f, first,
                        seedNoveltyBias: bias),
                    "first corridor grows");
                SuitabilityGraphMath.PeelFlow(graph, first, flow, used, 0.85f);
                SuitabilityGraphMath.DecayNovelty(graph, first, novelty, 3, 0.15f);

                var second = new Corridor();
                AssertTrue(
                    SuitabilityGraphMath.GrowCorridor(graph, flow, used, novelty, weight, 0f, 250f, second,
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

        // The bug this exists for: a feeder's own corridor carries almost nobody, so
        // scoring it by direct riders made it look worthless.
        private static void FeederGetsCredit()
        {
            TransitNetwork net = BuildTwoLineNetwork(60f, out _, out _);
            var ws = new DijkstraWorkspace(net.Graph.NodeCount);

            // Everyone travels from the side stop to the far end of the trunk, so the
            // feeder is only ever one leg of the journey and never the whole thing.
            var origins = new[] { 4 };
            var dests = new[] { 3 };
            var weights = new[] { 1000f };

            float feederCredit = SuitabilityTransit.CreditLine(net, ws, origins, dests, weights, 1,
                1, 1f, 100000f, out float served);

            AssertEqual(1000f, served, 1f, "the journey is served");
            AssertTrue(feederCredit > 0f, "the feeder must be credited for a journey it only starts");
            AssertEqual(1000f, feederCredit, 1f, "with no discount it earns the full weight");

            float trunkCredit = SuitabilityTransit.CreditLine(net, ws, origins, dests, weights, 1,
                0, 1f, 100000f, out _);
            AssertTrue(trunkCredit > 0f, "the trunk is credited too — both legs enable the trip");
        }

        private static void TransferDiscountApplies()
        {
            TransitNetwork net = BuildTwoLineNetwork(60f, out _, out _);
            var ws = new DijkstraWorkspace(net.Graph.NodeCount);

            var origins = new[] { 4 };
            var dests = new[] { 3 };
            var weights = new[] { 1000f };

            // One change, so one discount factor is applied.
            float full = SuitabilityTransit.CreditLine(net, ws, origins, dests, weights, 1, 1, 1f, 100000f, out _);
            float discounted = SuitabilityTransit.CreditLine(net, ws, origins, dests, weights, 1, 1, 0.6f, 100000f, out _);

            AssertEqual(1000f, full, 1f, "no discount");
            AssertEqual(600f, discounted, 1f, "one change costs one discount factor");

            // A direct journey on the trunk keeps its full weight either way.
            float direct = SuitabilityTransit.CreditLine(net, ws, new[] { 0 }, new[] { 3 }, weights, 1, 0, 0.6f, 100000f, out _);
            AssertEqual(1000f, direct, 1f, "a direct journey is not discounted");
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
                SuitabilityGraphMath.GrowCorridor(
                    graph, flow, new bool[4], NewNovelty(5), 0f, 1f, 10000f, corridor, demand, 0.5f),
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
                graph, flow, new bool[3], NewNovelty(4), 0f, 1f, 300f, corridor, demand, 0.5f);

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
                        graph, new[] { 5f, 5f }, new bool[2], nodeNovelty: null, noveltyWeight: 0f,
                        flowFloor: 1f, maxLength: 1000f, result: corridor, nodeDemand: null, demandFloor: 0f,
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

        private static float[] NewNovelty(int nodes)
        {
            var novelty = new float[nodes];
            for (int i = 0; i < nodes; i++)
            {
                novelty[i] = 1f;
            }

            return novelty;
        }

        // A suggested line is scored by the demand it would ENABLE, which is measured
        // by routing zone-to-zone journeys over the network with the candidate added.
        // Those journeys enter as stop indices, and the mapping from zone to stop was
        // built against the EXISTING stops only — so a zone with nothing nearby stayed
        // unmapped and its journeys were invisible, no matter that the candidate put a
        // stop right in it. Every candidate therefore scored zero enabled demand, which
        // is precisely the case this mod exists to find.
        private static void RemapReachesUnservedZone()
        {
            // Three zones on a line at x = 0, 1000, 2000.
            var zoneX = new[] { 0f, 1000f, 2000f };
            var zoneZ = new[] { 0f, 0f, 0f };

            // Zone 0 already has stop 7 on top of it. Zones 1 and 2 have nothing.
            var zoneStop = new[] { 7, -1, -1 };
            var zoneDistSq = new[] { 0f, float.MaxValue, float.MaxValue };

            // The candidate proposes two stops: one in zone 1, one 400 m from zone 2.
            var newX = new[] { 1000f, 1600f };
            var newZ = new[] { 0f, 0f };
            var merged = new int[3];

            int changed = SuitabilityTransit.RemapZones(
                zoneX, zoneZ, 3, zoneStop, zoneDistSq,
                newX, newZ, 2, 20, 500f, merged);

            AssertTrue(changed == 2, "both zones the candidate reaches are remapped");
            AssertTrue(merged[0] == 7, "a zone already served by a closer existing stop keeps it");
            AssertTrue(merged[1] == 20, "the unserved zone now routes through the candidate's own stop");
            AssertTrue(merged[2] == 21, "the zone within walking distance of the far stop is picked up too");

            // A candidate stop that lands closer than the existing one wins it over:
            // that is the feeder case, where the new line is the better way in.
            var farStop = new[] { 7 };
            var farDistSq = new[] { 400f * 400f };
            var one = new int[1];
            int stolen = SuitabilityTransit.RemapZones(
                new[] { 0f }, new[] { 0f }, 1, farStop, farDistSq,
                new[] { 100f }, new[] { 0f }, 1, 20, 500f, one);

            AssertTrue(stolen == 1 && one[0] == 20, "a nearer candidate stop takes the zone from a distant existing one");
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
            _ = SuitabilityGraphMath.GrowCorridor(
                graph, flow, new bool[5], NewNovelty(6), 0f, 10f, 100000f, corridor);

            AssertTrue(corridor.Edges.Count == 2, $"only the two edges with flow are taken, got {corridor.Edges.Count}");
            AssertTrue(!corridor.Blocks.m_HitMaxLength, "it stopped for want of flow, not at the length limit");
            AssertTrue(corridor.Blocks.m_Flow > 0, "the refusal is attributed to the flow floor");
            AssertTrue(corridor.Blocks.m_Used == 0, "nothing here was spent by an earlier corridor");
            AssertTrue(corridor.Blocks.m_Demand == 0, "no demand gate was in play");

            // The same graph with flow everywhere but a tight length limit stops for
            // the other reason, and says so.
            var flowing = new[] { 50f, 50f, 50f, 50f, 50f };
            var capped = new Corridor();
            _ = SuitabilityGraphMath.GrowCorridor(
                graph, flowing, new bool[5], NewNovelty(6), 0f, 10f, 250f, capped);

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
                graph, flow, new bool[6], NewNovelty(7), 0f, 1f, 10000f, corridor, demand, 0.5f);

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
                graph, (float[])flow.Clone(), new bool[6], NewNovelty(7), 0f, 1f, 10000f,
                stopped, wide, 0.5f, seedNoveltyBias: 0f, maxLowDemandBridge: 2);

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
                graph, flow, new bool[4], NewNovelty(5), 0f, 1f, 10000f, corridor, demand, 0.5f);

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
