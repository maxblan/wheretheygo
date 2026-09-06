using System;

namespace StationSuitabilityOverlay
{
    // Outcome of the exact site selection. `Count` sites in `Indices`/`Scores`, ranked
    // by score descending. `Indices` are cell indices for the grid form and network
    // node indices for the network form. `Optimal` says the search closed: no feasible set of at
    // most `maxSites` candidates has a larger score sum. When the node budget ran out
    // first, `Optimal` is false and `UpperBound` is a proven ceiling on what any
    // feasible set can reach, so `UpperBound - Value` is the worst case left on the
    // table. Both values are integer-scaled scores (see SuitabilityExactSites).
    internal sealed class ExactSiteSolution
    {
        public int[] Indices = Array.Empty<int>();
        public float[] Scores = Array.Empty<float>();
        public int Count;
        public long Value;
        public long UpperBound;
        public bool Optimal;
        public bool WeightsExact;
        public long Nodes;
        public int Candidates;
        public bool CandidatesTruncated;

        // Value and UpperBound are score · 2^ScaleShift, floored per candidate.
        public int ScaleShift;
    }

    // Exact F2: choose at most K candidates so that no two conflict and the score sum
    // is maximal — a maximum-weight independent set on a conflict graph. Two forms:
    //
    //  - Grid (v1, kept for the synthetic instances): candidates are positive 3x3
    //    local maxima of a score grid, two conflict when their Chebyshev distance is
    //    below the separation.
    //  - Network (v2, register A2.1/A2.2): candidates are pedestrian-network nodes
    //    with a score, two conflict when the walking time between them is below the
    //    mode's stop spacing.
    //
    // The greedy ranking can leave score on the table (measured on a real city:
    // docs/correctness-claims.md C2.3), and the certified pipeline needs a mod result
    // it can pin against SCIP/VIPR with gap 0.
    //
    // Branch-and-bound, depth-first, include-first, seeded with the greedy solution:
    //  - Candidates are ordered by (score desc, index asc) — a total order, so the
    //    result does not depend on Array.Sort's tie handling.
    //  - Scores are converted to integers so every comparison is exact: the objective
    //    of a binary32 score is a dyadic rational, and scaling by a power of two makes
    //    the sum of K of them an exact long (`WeightsExact` reports the rare case
    //    where a score some 2^33 times smaller than the largest had to be rounded down).
    //  - The bound at a node is a clique-cover bound: partition the candidates into
    //    cliques of the conflict graph; a feasible set holds at most one per clique, so
    //    the heaviest remaining candidate of the top `slots` cliques bounds any
    //    completion. Grid cliques are m×m blocks; network cliques are balls of walking
    //    radius below half the spacing (the triangle inequality makes a ball a clique).
    //    Two covers are used and the smaller bound wins.
    //  - A node budget keeps the main thread responsive; on exhaustion the search
    //    reports the best set found and the largest bound of any unexplored subtree.
    internal static class SuitabilityExactSites
    {
        // Sums of `maxSites` weights must stay below 2^63; the largest weight is
        // scaled into [2^ScaleBits/2, 2^ScaleBits) with ScaleBits chosen from K.
        private const int MaxScaledBits = 62;

        public static ExactSiteSolution Solve(
            float[] scores,
            int width,
            int height,
            int minSeparation,
            int maxSites,
            long nodeBudget)
        {
            var solution = new ExactSiteSolution();
            if (scores is null || width <= 0 || height <= 0 || maxSites <= 0)
            {
                solution.Optimal = true;
                solution.WeightsExact = true;
                return solution;
            }

            int count = SuitabilityScoring.CollectSiteCandidates(
                scores, width, height, out int[] candidateIndices, out float[] candidateScores, out bool truncated);
            solution.CandidatesTruncated = truncated;
            if (count == 0)
            {
                solution.Optimal = true;
                solution.WeightsExact = true;
                return solution;
            }

            var cellX = new int[count];
            var cellY = new int[count];
            for (int slot = 0; slot < count; slot++)
            {
                cellX[slot] = candidateIndices[slot] % width;
                cellY[slot] = candidateIndices[slot] / width;
            }

            // Block pitch m (at least 1 so every cell is its own block when separation
            // imposes nothing); partition B is offset by half a block so a pair
            // straddling an A boundary usually shares a B block.
            int pitch = Math.Max(1, minSeparation);
            int half = pitch / 2;
            int blocksAcross = (width + half) / pitch + 2;
            var blockA = new int[count];
            var blockB = new int[count];
            for (int slot = 0; slot < count; slot++)
            {
                blockA[slot] = (cellX[slot] / pitch) + (cellY[slot] / pitch) * blocksAcross;
                blockB[slot] = ((cellX[slot] + half) / pitch) + ((cellY[slot] + half) / pitch) * blocksAcross;
            }

            bool Conflicts(int a, int b)
            {
                int dx = Math.Abs(cellX[a] - cellX[b]);
                int dy = Math.Abs(cellY[a] - cellY[b]);
                return Math.Max(dx, dy) < minSeparation;
            }

            return SolveConflicts(candidateIndices, candidateScores, count, Conflicts, new[] { blockA, blockB }, maxSites, nodeBudget, solution);
        }

        // Network form: candidates are graph nodes, `separationMs` the walking time
        // below which two of them conflict.
        public static ExactSiteSolution SolveOnNetwork(
            WalkGraph graph,
            int[] candidateNodes,
            float[] candidateScores,
            int count,
            int separationMs,
            int maxSites,
            long nodeBudget)
        {
            var solution = new ExactSiteSolution();
            if (graph is null || candidateNodes is null || candidateScores is null || count <= 0 || maxSites <= 0)
            {
                solution.Optimal = true;
                solution.WeightsExact = true;
                return solution;
            }

            int[][] neighbours = ConflictLists(graph, candidateNodes, count, separationMs);
            int[] order = OrderByScore(candidateNodes, candidateScores, count);
            var byIndex = new int[count];
            for (int i = 0; i < count; i++)
            {
                byIndex[i] = i;
            }

            Array.Sort(byIndex, (a, b) => candidateNodes[a].CompareTo(candidateNodes[b]));
            int radiusMs = Math.Max(0, (separationMs - 1) / 2);
            int[] ballsA = BallCover(graph, candidateNodes, count, order, radiusMs);
            int[] ballsB = BallCover(graph, candidateNodes, count, byIndex, radiusMs);

            bool Conflicts(int a, int b)
            {
                return Array.BinarySearch(neighbours[a], b) >= 0;
            }

            return SolveConflicts(candidateNodes, candidateScores, count, Conflicts, new[] { ballsA, ballsB }, maxSites, nodeBudget, solution);
        }

        // For each candidate, the sorted slots of the candidates within `separationMs`
        // of it (strictly below) by walking time — symmetric by construction.
        private static int[][] ConflictLists(WalkGraph graph, int[] candidateNodes, int count, int separationMs)
        {
            int[] slotOfNode = SlotOfNode(graph, candidateNodes, count);
            var dijkstra = new IntDijkstra(graph.NodeCount);
            var lists = new int[count][];
            var scratch = new System.Collections.Generic.List<int>();
            for (int slot = 0; slot < count; slot++)
            {
                scratch.Clear();
                if (separationMs > 0)
                {
                    dijkstra.Run(graph, candidateNodes[slot], 0, separationMs - 1);
                    for (int s = 0; s < dijkstra.SettledCount; s++)
                    {
                        int other = slotOfNode[dijkstra.Settled[s]];
                        if (other >= 0 && other != slot)
                        {
                            scratch.Add(other);
                        }
                    }
                }

                lists[slot] = scratch.ToArray();
                Array.Sort(lists[slot]);
            }

            return lists;
        }

        private static int[] SlotOfNode(WalkGraph graph, int[] candidateNodes, int count)
        {
            var slotOfNode = new int[graph.NodeCount];
            for (int n = 0; n < slotOfNode.Length; n++)
            {
                slotOfNode[n] = -1;
            }

            for (int slot = 0; slot < count; slot++)
            {
                if (candidateNodes[slot] >= 0 && candidateNodes[slot] < graph.NodeCount)
                {
                    slotOfNode[candidateNodes[slot]] = slot;
                }
            }

            return slotOfNode;
        }

        // Clique cover by walking-time balls: centres taken in `centreOrder`, each ball
        // claiming every still-unclaimed candidate within `radiusMs`. Two candidates in
        // one ball are within 2·radius < separation of each other, so they conflict.
        private static int[] BallCover(WalkGraph graph, int[] candidateNodes, int count, int[] centreOrder, int radiusMs)
        {
            int[] slotOfNode = SlotOfNode(graph, candidateNodes, count);
            var clique = new int[count];
            for (int slot = 0; slot < count; slot++)
            {
                clique[slot] = -1;
            }

            var dijkstra = new IntDijkstra(graph.NodeCount);
            int next = 0;
            for (int i = 0; i < count; i++)
            {
                int centre = centreOrder[i];
                if (clique[centre] >= 0)
                {
                    continue;
                }

                clique[centre] = next;
                dijkstra.Run(graph, candidateNodes[centre], 0, radiusMs);
                for (int s = 0; s < dijkstra.SettledCount; s++)
                {
                    int other = slotOfNode[dijkstra.Settled[s]];
                    if (other >= 0 && clique[other] < 0)
                    {
                        clique[other] = next;
                    }
                }

                next++;
            }

            return clique;
        }

        private static ExactSiteSolution SolveConflicts(
            int[] candidateIndices,
            float[] candidateScores,
            int count,
            Func<int, int, bool> conflicts,
            int[][] partitions,
            int maxSites,
            long nodeBudget,
            ExactSiteSolution solution)
        {
            solution.Candidates = count;
            int[] order = OrderByScore(candidateIndices, candidateScores, count);
            long[] weights = IntegerWeights(candidateScores, order, maxSites, out bool exact, out int shift);
            solution.WeightsExact = exact;
            solution.ScaleShift = shift;

            var search = new Search(weights, order, conflicts, partitions, maxSites, nodeBudget);
            search.Run();

            solution.Nodes = search.Nodes;
            solution.Optimal = !search.Exhausted;
            solution.Value = search.BestValue;
            solution.UpperBound = search.Exhausted ? Math.Max(search.BestValue, search.OpenBound) : search.BestValue;
            solution.Count = search.BestCount;
            solution.Indices = new int[search.BestCount];
            solution.Scores = new float[search.BestCount];
            for (int i = 0; i < search.BestCount; i++)
            {
                int slot = search.Best[i];
                solution.Indices[i] = candidateIndices[slot];
                solution.Scores[i] = candidateScores[slot];
            }

            RankByScore(solution);
            return solution;
        }

        // Candidate slots sorted by score descending, ties by index ascending.
        private static int[] OrderByScore(int[] indices, float[] scores, int count)
        {
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int byScore = scores[b].CompareTo(scores[a]);
                return byScore != 0 ? byScore : indices[a].CompareTo(indices[b]);
            });
            return order;
        }

        // weights[slot] = floor(score · 2^s) with s chosen so the largest score lands
        // in [2^(bits-1), 2^bits) and K of them still fit a long. Multiplying a
        // binary32 value (held exactly in a double) by a power of two is exact, so
        // the only loss is the floor, which is a no-op unless a score sits roughly
        // 33 binary orders below the largest.
        private static long[] IntegerWeights(float[] scores, int[] order, int maxSites, out bool exact, out int shift)
        {
            var weights = new long[scores.Length];
            double largest = scores[order[0]];
            shift = 0;
            if (double.IsNaN(largest) || double.IsInfinity(largest) || largest <= 0.0)
            {
                // A non-finite score cannot be ranked; the field is reported as inexact
                // and every candidate weighs nothing, so the result is an empty set.
                exact = false;
                return weights;
            }

            int budgetBits = MaxScaledBits;
            for (int k = 1; k < maxSites; k <<= 1)
            {
                budgetBits--;
            }

            double lower = Math.Pow(2.0, budgetBits - 1);
            double upper = lower * 2.0;
            double scale = 1.0;
            while (largest * scale < lower)
            {
                scale *= 2.0;
                shift++;
            }

            while (largest * scale >= upper)
            {
                scale *= 0.5;
                shift--;
            }

            exact = true;
            for (int i = 0; i < order.Length; i++)
            {
                int slot = order[i];
                double scaled = scores[slot] * scale;
                if (double.IsNaN(scaled) || double.IsInfinity(scaled))
                {
                    exact = false;
                    continue;
                }

                double floored = Math.Floor(scaled);
                if (floored != scaled)
                {
                    exact = false;
                }

                weights[slot] = (long)floored;
            }

            return weights;
        }

        private static void RankByScore(ExactSiteSolution solution)
        {
            for (int i = 1; i < solution.Count; i++)
            {
                for (int j = i; j > 0 && Outranks(solution, j, j - 1); j--)
                {
                    float score = solution.Scores[j];
                    solution.Scores[j] = solution.Scores[j - 1];
                    solution.Scores[j - 1] = score;
                    int index = solution.Indices[j];
                    solution.Indices[j] = solution.Indices[j - 1];
                    solution.Indices[j - 1] = index;
                }
            }
        }

        private static bool Outranks(ExactSiteSolution solution, int a, int b)
        {
            return solution.Scores[a] > solution.Scores[b]
                || (solution.Scores[a] == solution.Scores[b] && solution.Indices[a] < solution.Indices[b]);
        }

        private sealed class Search
        {
            private readonly long[] m_Weights;
            private readonly Func<int, int, bool> m_Conflicts;
            private readonly int[][] m_Partitions;
            private readonly int[][] m_Stamps;
            private readonly int[][] m_Lists;
            private readonly int[] m_Counts;
            private readonly int[] m_Chosen;
            private readonly int m_MaxSites;
            private readonly long m_NodeBudget;
            private int m_Stamp;

            public long Nodes;
            public bool Exhausted;
            public long OpenBound;
            public long BestValue;
            public int BestCount;
            public readonly int[] Best;

            public Search(long[] weights, int[] order, Func<int, int, bool> conflicts, int[][] partitions, int maxSites, long nodeBudget)
            {
                int count = order.Length;
                m_Weights = weights;
                m_Conflicts = conflicts;
                m_Partitions = partitions;
                m_MaxSites = maxSites;
                m_NodeBudget = nodeBudget;
                m_Stamps = new int[partitions.Length][];
                for (int p = 0; p < partitions.Length; p++)
                {
                    int cliques = 0;
                    for (int slot = 0; slot < count; slot++)
                    {
                        cliques = Math.Max(cliques, partitions[p][slot] + 1);
                    }

                    m_Stamps[p] = new int[cliques];
                }

                m_Lists = new int[maxSites + 1][];
                m_Counts = new int[maxSites + 1];
                m_Lists[0] = order;
                m_Counts[0] = count;
                for (int depth = 1; depth <= maxSites; depth++)
                {
                    m_Lists[depth] = new int[count];
                }

                m_Chosen = new int[maxSites];
                Best = new int[maxSites];
                SeedWithGreedy();
            }

            // The greedy ranking is feasible, so it is a valid incumbent: pruning
            // starts from a strong lower bound instead of from zero.
            private void SeedWithGreedy()
            {
                int[] order = m_Lists[0];
                int accepted = 0;
                long value = 0;
                for (int i = 0; i < m_Counts[0] && accepted < m_MaxSites; i++)
                {
                    int slot = order[i];
                    bool tooClose = false;
                    for (int j = 0; j < accepted; j++)
                    {
                        if (m_Conflicts(slot, Best[j]))
                        {
                            tooClose = true;
                            break;
                        }
                    }

                    if (!tooClose)
                    {
                        Best[accepted++] = slot;
                        value += m_Weights[slot];
                    }
                }

                BestCount = accepted;
                BestValue = value;
            }

            public void Run()
            {
                Explore(0, 0);
            }

            // One frame per include-depth: m_Lists[depth] holds the candidates still
            // compatible with m_Chosen[0..depth), sorted by weight descending. Walking
            // `position` forward is the exclude branch; building the next list is the
            // include branch. Returns after a prune, a budget stop or the frame's end.
            private void Explore(int depth, long value)
            {
                int[] list = m_Lists[depth];
                int count = m_Counts[depth];
                int slots = m_MaxSites - depth;
                for (int position = 0; position < count; position++)
                {
                    long bound = value + Bound(list, position, count, slots);
                    if (bound <= BestValue)
                    {
                        return;
                    }

                    if (Exhausted)
                    {
                        // Everything from here on is unexplored; its bound is at most this
                        // one because the list is sorted and the bound shrinks with position.
                        OpenBound = Math.Max(OpenBound, bound);
                        return;
                    }

                    Nodes++;
                    if (Nodes > m_NodeBudget)
                    {
                        Exhausted = true;
                        OpenBound = Math.Max(OpenBound, bound);
                        return;
                    }

                    int slot = list[position];
                    long included = value + m_Weights[slot];
                    m_Chosen[depth] = slot;
                    if (included > BestValue)
                    {
                        BestValue = included;
                        BestCount = depth + 1;
                        Array.Copy(m_Chosen, Best, depth + 1);
                    }

                    if (depth + 1 < m_MaxSites)
                    {
                        int next = Filter(list, position + 1, count, slot, m_Lists[depth + 1]);
                        m_Counts[depth + 1] = next;
                        if (next > 0)
                        {
                            Explore(depth + 1, included);
                        }
                    }
                }
            }

            private int Filter(int[] list, int from, int count, int chosen, int[] into)
            {
                int kept = 0;
                for (int i = from; i < count; i++)
                {
                    int slot = list[i];
                    if (!m_Conflicts(slot, chosen))
                    {
                        into[kept++] = slot;
                    }
                }

                return kept;
            }

            // Clique-cover bound over list[from..count): the sum of the heaviest
            // remaining candidate in each of the top `slots` distinct cliques, taken
            // under every cover; the smallest is still a valid ceiling.
            private long Bound(int[] list, int from, int count, int slots)
            {
                long bound = long.MaxValue;
                for (int p = 0; p < m_Partitions.Length; p++)
                {
                    bound = Math.Min(bound, PartitionBound(list, from, count, slots, m_Partitions[p], m_Stamps[p]));
                }

                return bound;
            }

            private long PartitionBound(int[] list, int from, int count, int slots, int[] clique, int[] stamp)
            {
                m_Stamp++;
                if (m_Stamp == int.MaxValue)
                {
                    for (int p = 0; p < m_Stamps.Length; p++)
                    {
                        Array.Clear(m_Stamps[p], 0, m_Stamps[p].Length);
                    }

                    m_Stamp = 1;
                }

                long sum = 0;
                int taken = 0;
                for (int i = from; i < count && taken < slots; i++)
                {
                    int slot = list[i];
                    int cell = clique[slot];
                    if (stamp[cell] == m_Stamp)
                    {
                        continue;
                    }

                    stamp[cell] = m_Stamp;
                    sum += m_Weights[slot];
                    taken++;
                }

                return sum;
            }
        }

        // F2's candidate set on the network (register A2.1): every siteable node whose
        // own tile is buildable and whose score for the map's mode is positive, scored
        // exactly as a tile sitting on the node would be (access 1) under the caps and
        // weights of the last combine. Grows the two arrays to the node count when they
        // are shorter; returns how many candidates were kept.
        public static int CollectNetworkCandidates(
            WalkGraph graph, WalkAccessResult result, int cls, int selfType, float[] typeWeight,
            byte[] buildable, float2Like worldMin, float tileSize, int2Like grid,
            in CombineWeights weights, float invSelf, ref int[] nodes, ref float[] scores)
        {
            if (nodes.Length < graph.NodeCount)
            {
                nodes = new int[graph.NodeCount];
                scores = new float[graph.NodeCount];
            }

            int count = 0;
            for (int node = 0; node < graph.NodeCount; node++)
            {
                int tile = TileOfNode(graph, node, worldMin, tileSize, grid);
                if (!graph.Siteable[node] || tile < 0 || tile >= buildable.Length || buildable[tile] == 0)
                {
                    continue;
                }

                SuitabilityCell cell = SuitabilityWalkAccess.NodeTerms(result, node, cls, selfType, typeWeight);
                float score = SuitabilityScoring.Combine(in cell, in weights, invSelf);
                if (score <= 0f)
                {
                    continue;
                }

                nodes[count] = node;
                scores[count] = score;
                count++;
            }

            return count;
        }

        // The tile under a network node, as a linear index into the tile grid.
        public static int TileOfNode(WalkGraph graph, int node, float2Like worldMin, float tileSize, int2Like grid)
        {
            int2Like cell = TileGrid.WorldToCell(new float2Like(graph.NodeX[node], graph.NodeZ[node]), worldMin, tileSize, grid);
            return cell.x + (cell.y * grid.x);
        }
    }
}
