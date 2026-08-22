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

            Run("Graph adjacency covers both directions", GraphAdjacencyBothDirections);
            Run("Dijkstra finds the cheapest path, not the fewest hops", DijkstraPrefersCheapPath);
            Run("Dijkstra respects the cost limit", DijkstraRespectsMaxCost);
            Run("Dijkstra workspace reuse gives identical results", DijkstraWorkspaceReuse);
            Run("Path accumulation loads flow onto the route taken", FlowFollowsShortestPath);
            Run("Unreachable targets accumulate nothing", FlowIgnoresUnreachable);
            Run("Corridor grows along the strongest flow", CorridorFollowsFlow);
            Run("Corridor is a connected polyline", CorridorIsConnected);
            Run("Corridor respects the length limit", CorridorRespectsMaxLength);
            Run("Peeling reduces flow and blocks reuse", PeelingReducesFlow);
            Run("Coverage objective diverts from the busiest corridor", ObjectiveChangesRoutes);
            Run("Desire lines deposit once per cell crossed", RasterizeDepositsPerCell);

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
            AssertTrue(corridor.CapturedFlow > 100f, $"captured flow {corridor.CapturedFlow} should exceed 100");
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
            SuitabilityGraphMath.GrowCorridor(graph, flow, new bool[5], NewNovelty(6), 0f, 1f, 250f, corridor);

            AssertTrue(corridor.Length <= 250f, $"length {corridor.Length} must respect the limit");
            AssertTrue(corridor.Edges.Count <= 2, $"only 2 edges of 100 fit under 250, got {corridor.Edges.Count}");
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
            SuitabilityGraphMath.GrowCorridor(graph, flow, used, NewNovelty(4), 0f, 1f, 1000f, corridor);
            float before = 0f;
            for (int e = 0; e < 3; e++) before += flow[e];

            SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, 0.8f);

            float after = 0f;
            for (int e = 0; e < 3; e++) after += flow[e];
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
            SuitabilityGraphMath.GrowCorridor(graph, flowA, new bool[3], NewNovelty(4),
                SuitabilityGraphMath.NoveltyWeight(RouteObjective.Ridership, 70f), 0f, 1000f, ridership);

            var coverage = new Corridor();
            var flowB = new[] { 100f, 90f, 20f };
            // Node 3 is virgin territory; nodes 0-2 are already covered.
            var novelty = new[] { 0.05f, 0.05f, 0.05f, 1f };
            SuitabilityGraphMath.GrowCorridor(graph, flowB, new bool[3], novelty,
                SuitabilityGraphMath.NoveltyWeight(RouteObjective.Coverage, 70f), 0f, 1000f, coverage);

            AssertTrue(ridership.Edges.Contains(1), "ridership objective should take the busy trunk");
            AssertTrue(!ridership.Edges.Contains(2), "ridership objective should skip the quiet branch");
            AssertTrue(coverage.Edges.Contains(2), "coverage objective should reach the untouched branch");
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

        private static float[] NewNovelty(int nodes)
        {
            var novelty = new float[nodes];
            for (int i = 0; i < nodes; i++)
            {
                novelty[i] = 1f;
            }

            return novelty;
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
