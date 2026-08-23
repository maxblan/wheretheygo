using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // Minimal 2D point so the geometry helpers stay free of Unity types and remain
    // unit testable; the mod converts to and from float2 at the boundary.
    internal struct float2Like
    {
        public float x;
        public float y;

        public float2Like(float x, float y)
        {
            this.x = x;
            this.y = y;
        }
    }

    // How a suggested corridor is scored while it grows.
    internal enum RouteObjective
    {
        Ridership = 0,
        Balanced = 1,
        Coverage = 2,
    }

    // A road network flattened into index arrays, so the routing algorithms never
    // touch ECS types and can be unit tested directly. Adjacency is stored
    // compressed-row style: the incident half-edges of node n live in
    // AdjEdge/AdjOther between NodeOffsets[n] and NodeOffsets[n + 1].
    internal sealed class CompactGraph
    {
        public int NodeCount;
        public int EdgeCount;
        public int[] EdgeA = Array.Empty<int>();
        public int[] EdgeB = Array.Empty<int>();
        public float[] EdgeCost = Array.Empty<float>();
        public int[] NodeOffsets = Array.Empty<int>();
        public int[] AdjEdge = Array.Empty<int>();
        public int[] AdjOther = Array.Empty<int>();

        public static CompactGraph Build(int nodeCount, int[] edgeA, int[] edgeB, float[] edgeCost, int edgeCount)
        {
            var graph = new CompactGraph
            {
                NodeCount = Math.Max(0, nodeCount),
                EdgeCount = Math.Max(0, edgeCount),
                EdgeA = edgeA,
                EdgeB = edgeB,
                EdgeCost = edgeCost,
            };

            graph.NodeOffsets = new int[graph.NodeCount + 1];
            if (graph.NodeCount == 0 || graph.EdgeCount == 0)
            {
                graph.AdjEdge = Array.Empty<int>();
                graph.AdjOther = Array.Empty<int>();
                return graph;
            }

            // Count, prefix-sum, then fill — two passes, no per-node lists.
            var counts = new int[graph.NodeCount];
            for (int e = 0; e < graph.EdgeCount; e++)
            {
                int a = edgeA[e];
                int b = edgeB[e];
                if (!Valid(a, graph.NodeCount) || !Valid(b, graph.NodeCount) || a == b)
                {
                    continue;
                }

                counts[a]++;
                counts[b]++;
            }

            int running = 0;
            for (int n = 0; n < graph.NodeCount; n++)
            {
                graph.NodeOffsets[n] = running;
                running += counts[n];
            }
            graph.NodeOffsets[graph.NodeCount] = running;

            graph.AdjEdge = new int[running];
            graph.AdjOther = new int[running];
            var cursor = new int[graph.NodeCount];
            Array.Copy(graph.NodeOffsets, cursor, graph.NodeCount);

            for (int e = 0; e < graph.EdgeCount; e++)
            {
                int a = edgeA[e];
                int b = edgeB[e];
                if (!Valid(a, graph.NodeCount) || !Valid(b, graph.NodeCount) || a == b)
                {
                    continue;
                }

                graph.AdjEdge[cursor[a]] = e;
                graph.AdjOther[cursor[a]] = b;
                cursor[a]++;

                graph.AdjEdge[cursor[b]] = e;
                graph.AdjOther[cursor[b]] = a;
                cursor[b]++;
            }

            return graph;
        }

        private static bool Valid(int node, int nodeCount)
        {
            return node >= 0 && node < nodeCount;
        }

        public int OtherEnd(int edge, int node)
        {
            return EdgeA[edge] == node ? EdgeB[edge] : EdgeA[edge];
        }
    }

    // Reusable scratch for repeated single-source searches. Kept as an object so a
    // caller running hundreds of sources allocates once.
    internal sealed class DijkstraWorkspace
    {
        public float[] Dist = Array.Empty<float>();
        public int[] PrevEdge = Array.Empty<int>();
        private int[] m_Heap = Array.Empty<int>();
        private int[] m_HeapIndex = Array.Empty<int>();
        private int m_HeapCount;
        // Touched nodes, so a search over a small neighbourhood does not pay to
        // clear a city-sized distance array.
        private int[] m_Touched = Array.Empty<int>();
        private int m_TouchedCount;

        public DijkstraWorkspace(int nodeCount)
        {
            Resize(nodeCount);
        }

        public void Resize(int nodeCount)
        {
            if (Dist is not null && Dist.Length == nodeCount)
            {
                return;
            }

            Dist = new float[nodeCount];
            PrevEdge = new int[nodeCount];
            m_Heap = new int[nodeCount + 1];
            m_HeapIndex = new int[nodeCount];
            m_Touched = new int[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                Dist[i] = float.MaxValue;
                PrevEdge[i] = -1;
                m_HeapIndex[i] = 0;
            }
        }

        // Single-source shortest paths, stopping once every reachable node within
        // maxCost is settled. One call serves every destination from that source,
        // which is why flow assignment runs one search per origin zone rather than
        // one per origin/destination pair.
        public void Run(CompactGraph graph, int source, float maxCost)
        {
            Resize(graph.NodeCount);
            ClearTouched();

            if (source < 0 || source >= graph.NodeCount)
            {
                return;
            }

            m_HeapCount = 0;
            Touch(source);
            Dist[source] = 0f;
            PrevEdge[source] = -1;
            HeapPush(source);

            while (m_HeapCount > 0)
            {
                int node = HeapPop();
                float nodeDist = Dist[node];
                if (nodeDist > maxCost)
                {
                    continue;
                }

                int start = graph.NodeOffsets[node];
                int end = graph.NodeOffsets[node + 1];
                for (int i = start; i < end; i++)
                {
                    int edge = graph.AdjEdge[i];
                    int next = graph.AdjOther[i];
                    float candidate = nodeDist + graph.EdgeCost[edge];
                    if (candidate > maxCost)
                    {
                        continue;
                    }

                    Touch(next);
                    if (candidate >= Dist[next])
                    {
                        continue;
                    }

                    Dist[next] = candidate;
                    PrevEdge[next] = edge;
                    HeapPush(next);
                }
            }
        }

        private void Touch(int node)
        {
            if (m_HeapIndex[node] != 0 || Dist[node] != float.MaxValue)
            {
                return;
            }

            m_Touched[m_TouchedCount++] = node;
        }

        private void ClearTouched()
        {
            for (int i = 0; i < m_TouchedCount; i++)
            {
                int node = m_Touched[i];
                Dist[node] = float.MaxValue;
                PrevEdge[node] = -1;
                m_HeapIndex[node] = 0;
            }

            m_TouchedCount = 0;
        }

        // Binary min-heap over node indices keyed by Dist. Lazy: a node can be
        // pushed more than once, and stale pops are harmless because Dist only ever
        // decreases and the relaxation check rejects them.
        private void HeapPush(int node)
        {
            if (m_HeapCount + 1 >= m_Heap.Length)
            {
                Array.Resize(ref m_Heap, m_Heap.Length * 2);
            }

            m_Heap[++m_HeapCount] = node;
            int slot = m_HeapCount;
            while (slot > 1)
            {
                int parent = slot >> 1;
                if (Dist[m_Heap[parent]] <= Dist[m_Heap[slot]])
                {
                    break;
                }

                Swap(parent, slot);
                slot = parent;
            }
        }

        private int HeapPop()
        {
            int top = m_Heap[1];
            m_Heap[1] = m_Heap[m_HeapCount--];
            int slot = 1;
            while (true)
            {
                int left = slot << 1;
                if (left > m_HeapCount)
                {
                    break;
                }

                int best = left;
                int right = left + 1;
                if (right <= m_HeapCount && Dist[m_Heap[right]] < Dist[m_Heap[left]])
                {
                    best = right;
                }

                if (Dist[m_Heap[slot]] <= Dist[m_Heap[best]])
                {
                    break;
                }

                Swap(slot, best);
                slot = best;
            }

            return top;
        }

        private void Swap(int a, int b)
        {
            int tmp = m_Heap[a];
            m_Heap[a] = m_Heap[b];
            m_Heap[b] = tmp;
        }
    }

    // A grown corridor: the ordered edges it runs along, plus what it captured.
    internal sealed class Corridor
    {
        public readonly List<int> Edges = new List<int>();
        public readonly List<int> Nodes = new List<int>();
        // Length-weighted mean flow along the corridor: "how many trips does this
        // carry". Deliberately NOT a sum over edges — summing counts a trip once per
        // edge it traverses, so long corridors reported flow far above the city's
        // entire demand and every suggestion was classified as a metro.
        public float CapturedFlow;
        public float Length;

        public void Clear()
        {
            Edges.Clear();
            Nodes.Clear();
            CapturedFlow = 0f;
            Length = 0f;
        }
    }

    internal static class SuitabilityGraphMath
    {
        // Adds `weight` onto every edge along the shortest path from the source the
        // workspace was last run from, back to `target`. Returns false if the target
        // was not reachable within the search radius.
        public static bool AccumulatePath(
            CompactGraph graph,
            DijkstraWorkspace workspace,
            int source,
            int target,
            float weight,
            float[] edgeFlow)
        {
            if (graph is null || workspace is null || edgeFlow is null)
            {
                return false;
            }

            if (target < 0 || target >= graph.NodeCount || source < 0 || source >= graph.NodeCount)
            {
                return false;
            }

            if (target == source)
            {
                return false;
            }

            if (workspace.Dist[target] == float.MaxValue)
            {
                return false;
            }

            int node = target;
            int guard = graph.EdgeCount + 1;
            while (node != source && guard-- > 0)
            {
                int edge = workspace.PrevEdge[node];
                if (edge < 0)
                {
                    return false;
                }

                edgeFlow[edge] += weight;
                node = graph.OtherEnd(edge, node);
            }

            return node == source;
        }

        // Novelty multiplier for the objective: how much reaching an untouched area
        // counts against raw flow. Scaled by mean flow so it is unit-free and
        // behaves the same on a small town and a metropolis.
        public static float NoveltyWeight(RouteObjective objective, float meanFlow)
        {
            switch (objective)
            {
                case RouteObjective.Ridership: return 0f;
                case RouteObjective.Coverage: return meanFlow * 4f;
                default: return meanFlow * 1f;
            }
        }

        public static float MeanPositiveFlow(float[] edgeFlow, int edgeCount)
        {
            if (edgeFlow is null || edgeCount <= 0)
            {
                return 0f;
            }

            double sum = 0.0;
            int count = 0;
            for (int e = 0; e < edgeCount; e++)
            {
                if (edgeFlow[e] > 0f)
                {
                    sum += edgeFlow[e];
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        // Grows one corridor: seed at the strongest unused edge, then repeatedly
        // extend whichever end offers the better next edge, where "better" is flow
        // plus a novelty bonus for reaching nodes no earlier corridor covered.
        //
        // Nodes are never revisited, so a corridor cannot fold back on itself, and
        // growth is deterministic: ties break on the lower edge index.
        public static bool GrowCorridor(
            CompactGraph graph,
            float[] edgeFlow,
            bool[] edgeUsed,
            float[] nodeNovelty,
            float noveltyWeight,
            float flowFloor,
            float maxLength,
            Corridor result,
            float[]? nodeDemand = null,
            float demandFloor = 0f)
        {
            result.Clear();
            if (graph is null || edgeFlow is null || edgeUsed is null || graph.EdgeCount == 0)
            {
                return false;
            }

            int seed = -1;
            float seedFlow = 0f;
            for (int e = 0; e < graph.EdgeCount; e++)
            {
                if (edgeUsed[e] || edgeFlow[e] < flowFloor)
                {
                    continue;
                }

                if (edgeFlow[e] > seedFlow)
                {
                    seedFlow = edgeFlow[e];
                    seed = e;
                }
            }

            if (seed < 0)
            {
                return false;
            }

            var visited = new HashSet<int>();
            var front = new List<int>();
            var back = new List<int>();

            int headNode = graph.EdgeA[seed];
            int tailNode = graph.EdgeB[seed];
            _ = visited.Add(headNode);
            _ = visited.Add(tailNode);
            float length = graph.EdgeCost[seed];
            float weightedFlow = edgeFlow[seed] * graph.EdgeCost[seed];

            while (length < maxLength)
            {
                int bestEdge = -1;
                int bestNext = -1;
                float bestScore = 0f;
                bool bestAtHead = true;

                FindExtension(graph, edgeFlow, edgeUsed, nodeNovelty, noveltyWeight, flowFloor, visited,
                    headNode, length, maxLength, nodeDemand, demandFloor,
                    ref bestEdge, ref bestNext, ref bestScore, ref bestAtHead, atHead: true);
                FindExtension(graph, edgeFlow, edgeUsed, nodeNovelty, noveltyWeight, flowFloor, visited,
                    tailNode, length, maxLength, nodeDemand, demandFloor,
                    ref bestEdge, ref bestNext, ref bestScore, ref bestAtHead, atHead: false);

                if (bestEdge < 0)
                {
                    break;
                }

                weightedFlow += edgeFlow[bestEdge] * graph.EdgeCost[bestEdge];
                length += graph.EdgeCost[bestEdge];
                _ = visited.Add(bestNext);

                if (bestAtHead)
                {
                    front.Add(bestEdge);
                    headNode = bestNext;
                }
                else
                {
                    back.Add(bestEdge);
                    tailNode = bestNext;
                }
            }

            // Emit front-to-back so the result is a drawable polyline.
            for (int i = front.Count - 1; i >= 0; i--)
            {
                result.Edges.Add(front[i]);
            }
            result.Edges.Add(seed);
            for (int i = 0; i < back.Count; i++)
            {
                result.Edges.Add(back[i]);
            }

            BuildNodeSequence(graph, result, headNode);

            result.CapturedFlow = length > 0f ? weightedFlow / length : 0f;
            result.Length = length;
            return true;
        }

        private static void FindExtension(
            CompactGraph graph,
            float[] edgeFlow,
            bool[] edgeUsed,
            float[] nodeNovelty,
            float noveltyWeight,
            float flowFloor,
            HashSet<int> visited,
            int fromNode,
            float length,
            float maxLength,
            float[]? nodeDemand,
            float demandFloor,
            ref int bestEdge,
            ref int bestNext,
            ref float bestScore,
            ref bool bestAtHead,
            bool atHead)
        {
            int start = graph.NodeOffsets[fromNode];
            int end = graph.NodeOffsets[fromNode + 1];
            for (int i = start; i < end; i++)
            {
                int edge = graph.AdjEdge[i];
                if (edgeUsed[edge] || edgeFlow[edge] < flowFloor)
                {
                    continue;
                }

                int next = graph.AdjOther[i];
                if (visited.Contains(next))
                {
                    continue;
                }

                if (length + graph.EdgeCost[edge] > maxLength)
                {
                    continue;
                }

                // Flow alone is not enough to justify extending: a rural through-road
                // legitimately carries assigned trips while serving nobody along it.
                // Without this the corridor happily loops out into empty land.
                if (nodeDemand is not null && next < nodeDemand.Length && nodeDemand[next] < demandFloor)
                {
                    continue;
                }

                float novelty = nodeNovelty is not null && next < nodeNovelty.Length ? nodeNovelty[next] : 1f;
                float score = edgeFlow[edge] + noveltyWeight * novelty;
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestEdge = edge;
                bestNext = next;
                bestAtHead = atHead;
            }
        }

        private static void BuildNodeSequence(CompactGraph graph, Corridor result, int startNode)
        {
            result.Nodes.Clear();
            if (result.Edges.Count == 0)
            {
                return;
            }

            int node = startNode;
            result.Nodes.Add(node);
            for (int i = 0; i < result.Edges.Count; i++)
            {
                node = graph.OtherEnd(result.Edges[i], node);
                result.Nodes.Add(node);
            }
        }

        // Removes the demand a chosen corridor satisfies. The corridor's own edges
        // lose `capture` of their flow; edges merely touching its nodes lose half
        // that, which is what stops the next suggestion being the parallel street
        // one block over.
        public static void PeelFlow(
            CompactGraph graph,
            Corridor corridor,
            float[] edgeFlow,
            bool[] edgeUsed,
            float capture)
        {
            if (graph is null || corridor is null || edgeFlow is null || edgeUsed is null)
            {
                return;
            }

            capture = SuitabilityScoring.Saturate(capture);

            for (int i = 0; i < corridor.Edges.Count; i++)
            {
                int edge = corridor.Edges[i];
                edgeFlow[edge] *= 1f - capture;
                edgeUsed[edge] = true;
            }

            float sideCapture = capture * 0.5f;
            for (int i = 0; i < corridor.Nodes.Count; i++)
            {
                int node = corridor.Nodes[i];
                int start = graph.NodeOffsets[node];
                int end = graph.NodeOffsets[node + 1];
                for (int a = start; a < end; a++)
                {
                    int edge = graph.AdjEdge[a];
                    if (edgeUsed[edge])
                    {
                        continue;
                    }

                    edgeFlow[edge] *= 1f - sideCapture;
                }
            }
        }

        // Once a corridor covers an area, further corridors get less credit for
        // going there. Spreads outward `hops` steps from the corridor's nodes.
        public static void DecayNovelty(CompactGraph graph, Corridor corridor, float[] nodeNovelty, int hops, float factor)
        {
            if (graph is null || corridor is null || nodeNovelty is null)
            {
                return;
            }

            var frontier = new List<int>(corridor.Nodes);
            var seen = new HashSet<int>(corridor.Nodes);
            float scale = factor;

            for (int hop = 0; hop <= hops; hop++)
            {
                for (int i = 0; i < frontier.Count; i++)
                {
                    int node = frontier[i];
                    if (node >= 0 && node < nodeNovelty.Length)
                    {
                        nodeNovelty[node] *= scale;
                    }
                }

                if (hop == hops)
                {
                    break;
                }

                var next = new List<int>();
                for (int i = 0; i < frontier.Count; i++)
                {
                    int node = frontier[i];
                    int start = graph.NodeOffsets[node];
                    int end = graph.NodeOffsets[node + 1];
                    for (int a = start; a < end; a++)
                    {
                        int other = graph.AdjOther[a];
                        if (seen.Add(other))
                        {
                            next.Add(other);
                        }
                    }
                }

                frontier = next;
                // Further hops are dampened less, so the penalty fades with distance.
                scale = 1f - (1f - scale) * 0.5f;
            }
        }

        // Ramer-Douglas-Peucker simplification of a polyline, in place.
        //
        // A lattice corridor is an 8-connected staircase, so drawn raw it zig-zags
        // even when the underlying route is essentially straight. Metro tunnels and
        // ferry crossings really are straight-ish, so collapsing near-collinear runs
        // is both prettier and more honest about the alignment.
        public static void SimplifyPolyline(List<float2Like> points, float tolerance)
        {
            if (points is null || points.Count < 3 || tolerance <= 0f)
            {
                return;
            }

            var keep = new bool[points.Count];
            keep[0] = true;
            keep[points.Count - 1] = true;
            SimplifyRange(points, 0, points.Count - 1, tolerance, keep);

            int write = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                {
                    points[write++] = points[i];
                }
            }

            points.RemoveRange(write, points.Count - write);
        }

        private static void SimplifyRange(List<float2Like> points, int first, int last, float tolerance, bool[] keep)
        {
            if (last <= first + 1)
            {
                return;
            }

            float worst = -1f;
            int worstIndex = -1;
            for (int i = first + 1; i < last; i++)
            {
                float distance = PerpendicularDistance(points[i], points[first], points[last]);
                if (distance > worst)
                {
                    worst = distance;
                    worstIndex = i;
                }
            }

            if (worst <= tolerance || worstIndex < 0)
            {
                return;
            }

            keep[worstIndex] = true;
            SimplifyRange(points, first, worstIndex, tolerance, keep);
            SimplifyRange(points, worstIndex, last, tolerance, keep);
        }

        private static float PerpendicularDistance(float2Like point, float2Like a, float2Like b)
        {
            float dx = b.x - a.x;
            float dy = b.y - a.y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq <= 1e-6f)
            {
                float ax = point.x - a.x;
                float ay = point.y - a.y;
                return (float)Math.Sqrt(ax * ax + ay * ay);
            }

            float t = ((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSq;
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            float projX = a.x + t * dx;
            float projY = a.y + t * dy;
            float ox = point.x - projX;
            float oy = point.y - projY;
            return (float)Math.Sqrt(ox * ox + oy * oy);
        }

        // Accumulates a straight desire line into a raster, depositing `weight` per
        // cell it crosses. Per-cell rather than per-trip, so the raster reads as
        // flow density: a long trip touches more cells but does not make any single
        // cell hotter than a short one carrying the same number of people.
        public static void RasterizeSegment(
            float[] raster,
            int width,
            int height,
            float x0,
            float y0,
            float x1,
            float y1,
            float weight)
        {
            if (raster is null || width <= 0 || height <= 0 || raster.Length < width * height)
            {
                return;
            }

            float dx = x1 - x0;
            float dy = y1 - y0;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
            int steps = (int)Math.Ceiling(distance);
            if (steps <= 0)
            {
                Deposit(raster, width, height, (int)Math.Floor(x0), (int)Math.Floor(y0), weight);
                return;
            }

            float stepX = dx / steps;
            float stepY = dy / steps;
            int lastCell = -1;
            for (int i = 0; i <= steps; i++)
            {
                int cx = (int)Math.Floor(x0 + stepX * i);
                int cy = (int)Math.Floor(y0 + stepY * i);
                if (cx < 0 || cx >= width || cy < 0 || cy >= height)
                {
                    continue;
                }

                // Sub-cell steps would otherwise deposit into the same cell twice.
                int cell = cx + cy * width;
                if (cell == lastCell)
                {
                    continue;
                }

                lastCell = cell;
                raster[cell] += weight;
            }
        }

        private static void Deposit(float[] raster, int width, int height, int x, int y, float weight)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                return;
            }

            raster[x + y * width] += weight;
        }
    }
}
