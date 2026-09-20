using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
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

        // Per edge: true where the edge may only be walked from its A end to its B end.
        // Empty means every edge is two-way, which is what the walk graph wants.
        //
        // It exists for one thing: riding a transit line. A line in this game is a
        // closed loop driven in one direction, and the game's own pathfinder builds
        // its transit edge with EdgeFlags.Forward alone
        // (PathUtils.GetTransportLineSpecification). Two-way ride edges let a rider
        // step one stop BACKWARDS along a loop for the price of the forward hop, where
        // the real answer is to ride the whole loop round.
        public bool[] EdgeForwardOnly = Array.Empty<bool>();

        public static CompactGraph Build(int nodeCount, int[] edgeA, int[] edgeB, float[] edgeCost, int edgeCount)
        {
            return Build(nodeCount, edgeA, edgeB, edgeCost, edgeCount, forwardOnly: null);
        }

        public static CompactGraph Build(int nodeCount, int[] edgeA, int[] edgeB, float[] edgeCost, int edgeCount, bool[]? forwardOnly)
        {
            var graph = new CompactGraph
            {
                NodeCount = Math.Max(0, nodeCount),
                EdgeCount = Math.Max(0, edgeCount),
                EdgeA = edgeA,
                EdgeB = edgeB,
                EdgeCost = edgeCost,
                EdgeForwardOnly = forwardOnly ?? Array.Empty<bool>(),
            };

            graph.NodeOffsets = new int[graph.NodeCount + 1];
            if (graph.NodeCount == 0 || graph.EdgeCount == 0)
            {
                graph.AdjEdge = Array.Empty<int>();
                graph.AdjOther = Array.Empty<int>();
                return graph;
            }

            // Count, prefix-sum, then fill: two passes, no per-node lists.
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
            m_Touched = new int[nodeCount];

            // The touched list addresses the arrays that were just replaced, so its
            // count must go with them: carrying it across a reallocation made the
            // next ClearTouched read past the end of a shorter m_Touched.
            m_TouchedCount = 0;
            m_HeapCount = 0;

            for (int i = 0; i < nodeCount; i++)
            {
                Dist[i] = float.MaxValue;
                PrevEdge[i] = -1;
            }
        }

        // Single-source shortest paths, stopping once every reachable node within
        // maxCost is settled. One call serves every destination from that source.
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
            Settle(graph, maxCost);
        }

        // Several starting nodes at once, each with its own starting cost: the stops a
        // journey's door can walk to, at the walking time to each. Everything else as
        // Run: a node's distance is the least over all starts.
        public void RunFromMany(CompactGraph graph, int[] sources, float[] startCosts, int sourceCount, float maxCost)
        {
            Resize(graph.NodeCount);
            ClearTouched();
            m_HeapCount = 0;
            for (int i = 0; i < sourceCount; i++)
            {
                int source = sources[i];
                if (source < 0 || source >= graph.NodeCount || startCosts[i] > maxCost)
                {
                    continue;
                }

                Touch(source);
                if (startCosts[i] < Dist[source])
                {
                    Dist[source] = startCosts[i];
                    PrevEdge[source] = -1;
                    HeapPush(source);
                }
            }

            Settle(graph, maxCost);
        }

        // The main loop shared by every entry point: pops the heap and relaxes edges
        // until nothing within maxCost is left.
        private void Settle(CompactGraph graph, float maxCost)
        {
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
                    // A one-way edge is in both nodes' adjacency, because the
                    // adjacency is what makes the walk graph cheap to build; the
                    // direction is enforced here, on the one traversal that matters.
                    if (edge < graph.EdgeForwardOnly.Length && graph.EdgeForwardOnly[edge] && graph.EdgeA[edge] != node)
                    {
                        continue;
                    }

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

        // A node enters the touched list exactly once, on the relaxation that first
        // gives it a finite distance. Every caller of this sets Dist[node] to a
        // finite value immediately afterwards, which is what keeps that true.
        private void Touch(int node)
        {
            if (Dist[node] != float.MaxValue)
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

}
