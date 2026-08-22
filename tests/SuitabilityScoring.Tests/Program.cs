using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay.Tests
{
    // Minimal self-contained harness: no test framework, so it runs offline with
    // nothing to restore. Exit code is the number of failed tests.
    internal static class Program
    {
        private static int s_Failures;
        private static string s_Current = "(none)";

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
                if (intensities[i] == 255) saturated++;
                if (intensities[i] == 0) zeroed++;
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
                        if (x < 0 || x >= width || y < 0 || y >= height) continue;
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

            SuitabilityScoring.AccumulateWalkDistance(
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
            SuitabilityScoring.AccumulateWalkDistance(
                site, width, height, 10f, 60f, land, demand, jobs, 1f, 0f,
                new float[cells], new byte[cells], out float reached, out _);

            AssertEqual(0f, reached, 1e-4f, "demand across an impassable barrier must not be reached");

            // Opening a gap in the barrier must let it through again.
            land[barrierX + 4 * width] = 1;
            SuitabilityScoring.AccumulateWalkDistance(
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

            // No variance in the target leaves nothing to explain.
            var flat = new float[rows];
            for (int r = 0; r < rows; r++)
            {
                flat[r] = 5f;
            }
            AssertEqual(0f, SuitabilityScoring.RSquared(features, flat, rows, 1, new float[] { 0f }), 0f, "no variance");
        }

        // ---- harness --------------------------------------------------------

        private static int CountSaturated(byte[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == 255) count++;
            }
            return count;
        }

        private static void Run(string name, Action test)
        {
            s_Current = name;
            try
            {
                test();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                s_Failures++;
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {ex.Message}");
            }
        }

        private static void AssertTrue(bool condition, string because)
        {
            if (!condition)
            {
                throw new Exception($"{because}");
            }
        }

        private static void AssertEqual(float expected, float actual, float tolerance, string because)
        {
            if (float.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            {
                throw new Exception($"{because}: expected {expected}, got {actual}");
            }
        }

        private static void AssertEqual(int expected, int actual, int tolerance, string because)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new Exception($"{because}: expected {expected}, got {actual}");
            }
        }
    }
}
