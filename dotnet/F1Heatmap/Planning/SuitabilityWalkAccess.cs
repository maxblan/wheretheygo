using System;
using System.Collections.Generic;

namespace TransitArchitect
{
    // The pedestrian network as the planning model sees it: one node per net node,
    // one undirected edge per net edge that carries a pedestrian lane, cost = walking
    // time in whole milliseconds. Integer costs are the point: every shortest-path
    // time is then an exact integer that an offline checker reproduces without any
    // float-summation argument, and the (few) float operations that remain are the
    // kernel and the weighted sums, whose order this file pins.
    internal sealed class WalkGraph
    {
        public int NodeCount;
        public float[] NodeX = Array.Empty<float>();
        public float[] NodeZ = Array.Empty<float>();
        // Whether a stop could stand at the node: false where every pavement meeting
        // it is in a tunnel or on a bridge (register A1.15). Such a node still carries
        // walking — a home or a journey may snap to it — but no tile and no site
        // candidate does.
        public bool[] Siteable = Array.Empty<bool>();
        public int[] EdgeA = Array.Empty<int>();
        public int[] EdgeB = Array.Empty<int>();
        public int[] EdgeMs = Array.Empty<int>();
        // The game's arc lengths the milliseconds were derived from, kept so an export
        // can carry the derivation's input rather than only its output.
        public float[] EdgeMetres = Array.Empty<float>();
        public int[] Offsets = Array.Empty<int>();
        public int[] AdjOther = Array.Empty<int>();
        public int[] AdjMs = Array.Empty<int>();

        // ms = round(length / WalkSpeed · 1000) in double arithmetic, with WalkSpeed the
        // binary32 constant widened (1.2f, not 1.2 — the offline checker widens the
        // same bits). Rounding is to-even. Edges additionally get a 1 ms floor in
        // Build so no edge is free; an access walk of 0 m stays 0 ms.
        public static int WalkMilliseconds(double metres)
        {
            double ms = metres / (double)Assumptions.WalkSpeed * 1000.0;
            return (int)Math.Round(ms, MidpointRounding.ToEven);
        }

        public static WalkGraph Build(float[] nodeX, float[] nodeZ, int[] edgeA, int[] edgeB, float[] edgeMetres, int edgeCount)
        {
            var siteable = new bool[nodeX.Length];
            for (int n = 0; n < siteable.Length; n++)
            {
                siteable[n] = true;
            }

            return Build(nodeX, nodeZ, edgeA, edgeB, edgeMetres, edgeCount, siteable);
        }

        public static WalkGraph Build(float[] nodeX, float[] nodeZ, int[] edgeA, int[] edgeB, float[] edgeMetres, int edgeCount, bool[] siteable)
        {
            var graph = new WalkGraph
            {
                NodeCount = nodeX.Length,
                NodeX = nodeX,
                NodeZ = nodeZ,
                Siteable = siteable,
                EdgeA = new int[edgeCount],
                EdgeB = new int[edgeCount],
                EdgeMs = new int[edgeCount],
                EdgeMetres = new float[edgeCount],
            };

            var degree = new int[nodeX.Length + 1];
            for (int e = 0; e < edgeCount; e++)
            {
                graph.EdgeA[e] = edgeA[e];
                graph.EdgeB[e] = edgeB[e];
                graph.EdgeMetres[e] = edgeMetres[e];
                graph.EdgeMs[e] = Math.Max(1, WalkMilliseconds(edgeMetres[e]));
                degree[edgeA[e] + 1]++;
                degree[edgeB[e] + 1]++;
            }

            for (int n = 0; n < nodeX.Length; n++)
            {
                degree[n + 1] += degree[n];
            }

            graph.Offsets = degree;
            graph.AdjOther = new int[edgeCount * 2];
            graph.AdjMs = new int[edgeCount * 2];
            var fill = new int[nodeX.Length];
            for (int e = 0; e < edgeCount; e++)
            {
                int a = graph.EdgeA[e];
                int b = graph.EdgeB[e];
                int slotA = graph.Offsets[a] + fill[a]++;
                int slotB = graph.Offsets[b] + fill[b]++;
                graph.AdjOther[slotA] = b;
                graph.AdjMs[slotA] = graph.EdgeMs[e];
                graph.AdjOther[slotB] = a;
                graph.AdjMs[slotB] = graph.EdgeMs[e];
            }

            return graph;
        }
    }

    // Bounded integer Dijkstra with a touched-list reset, so thousands of runs over
    // one graph cost O(settled) each rather than O(nodes). After Run, Settled[0..
    // SettledCount) lists the nodes within the bound in settle order and Dist holds
    // their exact times; every other node reads Unreached.
    internal sealed class IntDijkstra
    {
        public const long Unreached = long.MaxValue;

        public long[] Dist;
        public int[] Settled;
        public int SettledCount;
        private long[] m_HeapKey;
        private int[] m_HeapNode;
        private int m_HeapCount;
        private readonly List<int> m_Touched = new List<int>();

        public IntDijkstra(int nodeCount)
        {
            Dist = new long[nodeCount];
            Settled = new int[nodeCount];
            m_HeapKey = new long[Math.Max(16, nodeCount)];
            m_HeapNode = new int[m_HeapKey.Length];
            for (int i = 0; i < nodeCount; i++)
            {
                Dist[i] = Unreached;
            }
        }

        // Shortest times from `source`, starting at `startMs` (the walk from the real
        // origin to its node), settling nothing beyond `maxMs`.
        public void Run(WalkGraph graph, int source, long startMs, long maxMs)
        {
            for (int i = 0; i < m_Touched.Count; i++)
            {
                Dist[m_Touched[i]] = Unreached;
            }

            m_Touched.Clear();
            SettledCount = 0;
            m_HeapCount = 0;
            if (source < 0 || source >= graph.NodeCount || startMs > maxMs)
            {
                return;
            }

            Dist[source] = startMs;
            m_Touched.Add(source);
            Push(startMs, source);
            while (m_HeapCount > 0)
            {
                Pop(out long d, out int node);
                if (d != Dist[node])
                {
                    continue;
                }

                Settled[SettledCount++] = node;
                int end = graph.Offsets[node + 1];
                for (int slot = graph.Offsets[node]; slot < end; slot++)
                {
                    int other = graph.AdjOther[slot];
                    long nd = d + graph.AdjMs[slot];
                    if (nd > maxMs || nd >= Dist[other])
                    {
                        continue;
                    }

                    if (Dist[other] == Unreached)
                    {
                        m_Touched.Add(other);
                    }

                    Dist[other] = nd;
                    Push(nd, other);
                }
            }
        }

        private void Push(long key, int node)
        {
            if (m_HeapCount == m_HeapKey.Length)
            {
                Array.Resize(ref m_HeapKey, m_HeapKey.Length * 2);
                Array.Resize(ref m_HeapNode, m_HeapKey.Length);
            }

            int i = m_HeapCount++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Less(key, node, m_HeapKey[parent], m_HeapNode[parent]))
                {
                    break;
                }

                m_HeapKey[i] = m_HeapKey[parent];
                m_HeapNode[i] = m_HeapNode[parent];
                i = parent;
            }

            m_HeapKey[i] = key;
            m_HeapNode[i] = node;
        }

        private void Pop(out long key, out int node)
        {
            key = m_HeapKey[0];
            node = m_HeapNode[0];
            m_HeapCount--;
            if (m_HeapCount == 0)
            {
                return;
            }

            long lastKey = m_HeapKey[m_HeapCount];
            int lastNode = m_HeapNode[m_HeapCount];
            int i = 0;
            while (true)
            {
                int child = (i << 1) + 1;
                if (child >= m_HeapCount)
                {
                    break;
                }

                int right = child + 1;
                if (right < m_HeapCount && Less(m_HeapKey[right], m_HeapNode[right], m_HeapKey[child], m_HeapNode[child]))
                {
                    child = right;
                }

                if (!Less(m_HeapKey[child], m_HeapNode[child], lastKey, lastNode))
                {
                    break;
                }

                m_HeapKey[i] = m_HeapKey[child];
                m_HeapNode[i] = m_HeapNode[child];
                i = child;
            }

            m_HeapKey[i] = lastKey;
            m_HeapNode[i] = lastNode;
        }

        // Ties on time settle the lower node index first, so the settle order — and
        // with it the accumulation order downstream — is a function of the input.
        private static bool Less(long keyA, int nodeA, long keyB, int nodeB)
        {
            return keyA < keyB || (keyA == keyB && nodeA < nodeB);
        }
    }

    // Nearest graph node to a point, by straight-line distance, via a uniform grid of
    // buckets. Ties (equal squared distance in double) go to the lower node index.
    internal sealed class WalkNodeIndex
    {
        private readonly WalkGraph m_Graph;
        private readonly double m_MinX;
        private readonly double m_MinZ;
        private readonly double m_Cell;
        private readonly int m_Columns;
        private readonly int m_Rows;
        private readonly int[] m_Offsets;
        private readonly int[] m_Nodes;

        public WalkNodeIndex(WalkGraph graph, double cellMetres)
        {
            m_Graph = graph;
            m_Cell = Math.Max(1.0, cellMetres);
            double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
            for (int n = 0; n < graph.NodeCount; n++)
            {
                minX = Math.Min(minX, graph.NodeX[n]);
                minZ = Math.Min(minZ, graph.NodeZ[n]);
                maxX = Math.Max(maxX, graph.NodeX[n]);
                maxZ = Math.Max(maxZ, graph.NodeZ[n]);
            }

            if (graph.NodeCount == 0)
            {
                minX = minZ = 0.0;
                maxX = maxZ = 0.0;
            }

            m_MinX = minX;
            m_MinZ = minZ;
            m_Columns = Math.Max(1, (int)Math.Floor((maxX - minX) / m_Cell) + 1);
            m_Rows = Math.Max(1, (int)Math.Floor((maxZ - minZ) / m_Cell) + 1);
            m_Offsets = new int[m_Columns * m_Rows + 1];
            for (int n = 0; n < graph.NodeCount; n++)
            {
                m_Offsets[BucketOf(graph.NodeX[n], graph.NodeZ[n]) + 1]++;
            }

            for (int b = 0; b < m_Columns * m_Rows; b++)
            {
                m_Offsets[b + 1] += m_Offsets[b];
            }

            m_Nodes = new int[graph.NodeCount];
            var fill = new int[m_Columns * m_Rows];
            for (int n = 0; n < graph.NodeCount; n++)
            {
                int bucket = BucketOf(graph.NodeX[n], graph.NodeZ[n]);
                m_Nodes[m_Offsets[bucket] + fill[bucket]++] = n;
            }
        }

        private int BucketOf(double x, double z)
        {
            int column = Math.Min(m_Columns - 1, Math.Max(0, (int)Math.Floor((x - m_MinX) / m_Cell)));
            int row = Math.Min(m_Rows - 1, Math.Max(0, (int)Math.Floor((z - m_MinZ) / m_Cell)));
            return column + row * m_Columns;
        }

        // Nearest node within `maxMetres`, or -1. `metres` is the straight-line
        // distance to it, computed as sqrt of the double squared distance.
        public int Nearest(float x, float z, double maxMetres, out double metres)
        {
            return Nearest(x, z, maxMetres, sitesOnly: false, out metres);
        }

        // Nearest node a stop could stand at (WalkGraph.Siteable), or -1: the snap
        // for tiles and site candidates, which skips tunnel and bridge nodes.
        public int NearestSite(float x, float z, double maxMetres, out double metres)
        {
            return Nearest(x, z, maxMetres, sitesOnly: true, out metres);
        }

        private int Nearest(float x, float z, double maxMetres, bool sitesOnly, out double metres)
        {
            metres = 0.0;
            if (m_Graph.NodeCount == 0)
            {
                return -1;
            }

            int reach = (int)Math.Ceiling(maxMetres / m_Cell);
            int column = (int)Math.Floor((x - m_MinX) / m_Cell);
            int row = (int)Math.Floor((z - m_MinZ) / m_Cell);
            double bestSq = maxMetres * maxMetres;
            int best = -1;
            for (int dr = -reach; dr <= reach; dr++)
            {
                int r = row + dr;
                if (r < 0 || r >= m_Rows)
                {
                    continue;
                }

                for (int dc = -reach; dc <= reach; dc++)
                {
                    int c = column + dc;
                    if (c < 0 || c >= m_Columns)
                    {
                        continue;
                    }

                    ScanBucket(c + r * m_Columns, x, z, sitesOnly, ref bestSq, ref best);
                }
            }

            metres = best >= 0 ? Math.Sqrt(bestSq) : 0.0;
            return best;
        }

        private void ScanBucket(int bucket, double x, double z, bool sitesOnly, ref double bestSq, ref int best)
        {
            for (int slot = m_Offsets[bucket]; slot < m_Offsets[bucket + 1]; slot++)
            {
                int node = m_Nodes[slot];
                if (sitesOnly && !m_Graph.Siteable[node])
                {
                    continue;
                }

                double dx = m_Graph.NodeX[node] - x;
                double dz = m_Graph.NodeZ[node] - z;
                double sq = dx * dx + dz * dz;
                if (sq < bestSq || (sq == bestSq && best >= 0 && node < best))
                {
                    bestSq = sq;
                    best = node;
                }
            }
        }
    }

    // The pedestrian network as every tile of the map sees it: which node each tile
    // attaches to and how long the walk to it is, plus the spatial index that found
    // them. -1 means no pavement within the access walk.
    internal sealed class TileSnap
    {
        public WalkNodeIndex? Index;
        public int[] TileNode = Array.Empty<int>();
        public int[] TileWalkMs = Array.Empty<int>();
        public int TilesOnNetwork;
    }

    internal static class SuitabilityWalkAccess
    {
        // A point's node and the whole-millisecond walk to it; -1 when nothing lies
        // within the access walk. The straight line stands in for the unmodelled last
        // metres between a building's centre and the pavement.
        public static int SnapPoint(WalkNodeIndex index, float x, float z, int accessMs, out int walkMs)
        {
            double accessMetres = accessMs / 1000.0 * Assumptions.WalkSpeed;
            int node = index.Nearest(x, z, accessMetres, out double metres);
            return WithinAccess(node, metres, accessMs, out walkMs);
        }

        // The snap for a place a stop would stand — a tile, a site candidate: the
        // nearest node on the ground (WalkGraph.Siteable), under the same access rule.
        public static int SnapSite(WalkNodeIndex index, float x, float z, int accessMs, out int walkMs)
        {
            double accessMetres = accessMs / 1000.0 * Assumptions.WalkSpeed;
            int node = index.NearestSite(x, z, accessMetres, out double metres);
            return WithinAccess(node, metres, accessMs, out walkMs);
        }

        private static int WithinAccess(int node, double metres, int accessMs, out int walkMs)
        {
            walkMs = node >= 0 ? WalkGraph.WalkMilliseconds(metres) : -1;
            if (node >= 0 && walkMs > accessMs)
            {
                node = -1;
                walkMs = -1;
            }

            return node;
        }

        // Every tile's node and the walk to it, for the tile grid the access field is
        // laid out on. The node is the nearest one ON THE GROUND (WalkGraph.Siteable):
        // a tile over a tunnel reads the pavement above it, not the one beneath.
        public static TileSnap SnapTiles(
            WalkGraph graph, int width, int height, float worldMinX, float worldMinZ, float tileSize, int accessMs)
        {
            var snap = new TileSnap
            {
                TileNode = new int[width * height],
                TileWalkMs = new int[width * height],
            };
            if (graph.NodeCount == 0 || width <= 0 || height <= 0)
            {
                for (int i = 0; i < snap.TileNode.Length; i++)
                {
                    snap.TileNode[i] = -1;
                    snap.TileWalkMs[i] = -1;
                }

                return snap;
            }

            double accessMetres = accessMs / 1000.0 * Assumptions.WalkSpeed;
            snap.Index = new WalkNodeIndex(graph, Math.Max(32.0, accessMetres));
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = x + (y * width);
                    float cx = worldMinX + ((x + 0.5f) * tileSize);
                    float cz = worldMinZ + ((y + 0.5f) * tileSize);
                    snap.TileNode[i] = SnapSite(snap.Index, cx, cz, accessMs, out int walkMs);
                    snap.TileWalkMs[i] = walkMs;
                    if (snap.TileNode[i] >= 0)
                    {
                        snap.TilesOnNetwork++;
                    }
                }
            }

            return snap;
        }
    }
}
