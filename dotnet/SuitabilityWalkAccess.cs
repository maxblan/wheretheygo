using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // Per-cell raw scoring terms. Kept as one struct so the access pass writes a
    // single array and the managed combine pass reads it back without re-deriving
    // units. Since v2 every term is a walking-TIME quantity over the pedestrian
    // network (register A1.1), not a Euclidean one.
    internal struct SuitabilityCell
    {
        public float m_Demand;
        public float m_Jobs;
        public float m_Coverage;
        public float m_Access;
        public float m_Future;
        // Served stops of OTHER modes close enough to transfer to, weighted by how
        // much trunk capacity they represent.
        public float m_Interchange;
        // Served stops of other modes near enough to already absorb this tile's
        // demand, but too far to transfer to.
        public float m_CrossCoverage;
    }

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
            double ms = metres / (double)SuitabilityTransit.WalkSpeed * 1000.0;
            return (int)Math.Round(ms, MidpointRounding.ToEven);
        }

        public static WalkGraph Build(float[] nodeX, float[] nodeZ, int[] edgeA, int[] edgeB, float[] edgeMetres, int edgeCount)
        {
            var graph = new WalkGraph
            {
                NodeCount = nodeX.Length,
                NodeX = nodeX,
                NodeZ = nodeZ,
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

                    ScanBucket(c + r * m_Columns, x, z, ref bestSq, ref best);
                }
            }

            metres = best >= 0 ? Math.Sqrt(bestSq) : 0.0;
            return best;
        }

        private void ScanBucket(int bucket, double x, double z, ref double bestSq, ref int best)
        {
            for (int slot = m_Offsets[bucket]; slot < m_Offsets[bucket + 1]; slot++)
            {
                int node = m_Nodes[slot];
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

    // One group of point sources with weights: homes (residents), workplaces (jobs),
    // zoned-but-unbuilt cells (future). Filled by the gathering side.
    internal sealed class WalkSources
    {
        public float[] X = Array.Empty<float>();
        public float[] Z = Array.Empty<float>();
        public float[] Weight = Array.Empty<float>();
        public int Count;
    }

    internal sealed class WalkAccessInputs
    {
        public WalkGraph Graph = WalkGraph.Build(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<float>(), 0);
        public WalkSources Homes = new WalkSources();
        public WalkSources Jobs = new WalkSources();
        public WalkSources Future = new WalkSources();
        // Served stops with their TransportType as an int in [0, TypeCount).
        public float[] StopX = Array.Empty<float>();
        public float[] StopZ = Array.Empty<float>();
        public int[] StopType = Array.Empty<int>();
        public int StopCount;
        public int TypeCount;
        // Straight-line walk from a point to its network node, beyond which the point
        // is not on the network at all; the transfer walk; the distinct catchment
        // times of the modes, ascending.
        public int AccessMs;
        public int TransferMs;
        public int[] CatchmentMs = Array.Empty<int>();
    }

    // Everything the terms need, per network node, for every catchment class and
    // stop type at once — so a single pass serves the map (one mode) and the stop
    // placement (any mode) without a second, inconsistent computation.
    internal sealed class WalkAccessResult
    {
        public int[] HomeNode = Array.Empty<int>();
        public int[] JobNode = Array.Empty<int>();
        public int[] FutureNode = Array.Empty<int>();
        public int[] StopNode = Array.Empty<int>();
        public int[] HomeAccessMs = Array.Empty<int>();
        public int[] JobAccessMs = Array.Empty<int>();
        public int[] FutureAccessMs = Array.Empty<int>();
        public int[] StopAccessMs = Array.Empty<int>();
        // [class][node]
        public float[][] Demand = Array.Empty<float[]>();
        public float[][] Jobs = Array.Empty<float[]>();
        public float[][] Future = Array.Empty<float[]>();
        // [class][type][node]: Σ K(t, T_class) over served stops of that type.
        public float[][][] StopWithin = Array.Empty<float[][]>();
        // [type][node]: Σ K(t, T_transfer) — how readily one can change here.
        public float[][] Interchange = Array.Empty<float[]>();
        // [class][type][node]: Σ K(t, T_class) · (1 − K(t, T_transfer)).
        public float[][][] CrossRaw = Array.Empty<float[][]>();
        public int SourcesOffNetwork;
        public long Relaxations;
    }

    // Everything one compute produces: the node-level accumulators for every mode,
    // the tile snap, and the seven raw terms for the mode the map was built for.
    internal sealed class WalkAccessOutput
    {
        public WalkAccessResult Result = new WalkAccessResult();
        public WalkNodeIndex? Index;
        public SuitabilityCell[] Terms = Array.Empty<SuitabilityCell>();
        public int[] TileNode = Array.Empty<int>();
        public int[] TileWalkMs = Array.Empty<int>();
        public int Class;
        public int SelfType;
        public float[] TypeWeight = Array.Empty<float>();
        public int TilesOnNetwork;
    }

    internal static class SuitabilityWalkAccess
    {
        // Ceiling on the same-mode coverage term: three fully covering stops is as
        // "already served" as a tile gets.
        public const float MaxCoveragePenalty = 1.5f;

        // One compute, start to finish, with no Unity type in sight — which is what
        // lets the game run it on a worker thread and the offline pipeline run the
        // identical code on an exported city.
        public static WalkAccessOutput Run(
            WalkAccessInputs inputs, int width, int height, float worldMinX, float worldMinZ, float tileSize,
            byte[] buildable, int cls, int selfType, float[] typeWeight)
        {
            var output = new WalkAccessOutput
            {
                Result = Compute(inputs),
                Class = cls,
                SelfType = selfType,
                TypeWeight = typeWeight,
                Terms = new SuitabilityCell[width * height],
                TileNode = new int[width * height],
                TileWalkMs = new int[width * height],
            };
            if (inputs.Graph.NodeCount == 0 || cls < 0 || cls >= inputs.CatchmentMs.Length)
            {
                for (int i = 0; i < output.TileNode.Length; i++)
                {
                    output.TileNode[i] = -1;
                    output.TileWalkMs[i] = -1;
                }

                return output;
            }

            double accessMetres = inputs.AccessMs / 1000.0 * SuitabilityTransit.WalkSpeed;
            output.Index = new WalkNodeIndex(inputs.Graph, Math.Max(32.0, accessMetres));
            TileTerms(output.Result, output.Index, width, height, worldMinX, worldMinZ, tileSize, buildable,
                inputs.AccessMs, cls, selfType, typeWeight, output.Terms, output.TileNode, output.TileWalkMs);
            for (int i = 0; i < output.TileNode.Length; i++)
            {
                if (output.TileNode[i] >= 0)
                {
                    output.TilesOnNetwork++;
                }
            }

            return output;
        }

        // K(t, T) = 1 − t/T for t ≤ T, the same linear fade the v1 kernel used over
        // distance (TCQSM and Zhao et al. both prefer a fade to a hard edge). Both
        // operands are exact in binary32 (times are below 2^24 ms).
        public static float Kernel(long timeMs, int horizonMs)
        {
            return 1f - ((float)timeMs / (float)horizonMs);
        }

        public static WalkAccessResult Compute(WalkAccessInputs inputs)
        {
            WalkGraph graph = inputs.Graph;
            int classes = inputs.CatchmentMs.Length;
            int types = Math.Max(1, inputs.TypeCount);
            var result = new WalkAccessResult
            {
                Demand = Grid(classes, graph.NodeCount),
                Jobs = Grid(classes, graph.NodeCount),
                Future = Grid(classes, graph.NodeCount),
                Interchange = Grid(types, graph.NodeCount),
                StopWithin = new float[classes][][],
                CrossRaw = new float[classes][][],
            };
            for (int c = 0; c < classes; c++)
            {
                result.StopWithin[c] = Grid(types, graph.NodeCount);
                result.CrossRaw[c] = Grid(types, graph.NodeCount);
            }

            if (graph.NodeCount == 0 || classes == 0)
            {
                result.SourcesOffNetwork = inputs.Homes.Count + inputs.Jobs.Count + inputs.Future.Count + inputs.StopCount;
                return result;
            }

            double accessMetres = inputs.AccessMs / 1000.0 * SuitabilityTransit.WalkSpeed;
            var index = new WalkNodeIndex(graph, Math.Max(32.0, accessMetres));
            var dijkstra = new IntDijkstra(graph.NodeCount);
            long horizon = inputs.CatchmentMs[classes - 1];

            Snap(index, inputs.Homes, inputs.AccessMs, out result.HomeNode, out result.HomeAccessMs, ref result.SourcesOffNetwork);
            Snap(index, inputs.Jobs, inputs.AccessMs, out result.JobNode, out result.JobAccessMs, ref result.SourcesOffNetwork);
            Snap(index, inputs.Future, inputs.AccessMs, out result.FutureNode, out result.FutureAccessMs, ref result.SourcesOffNetwork);
            SnapStops(index, inputs, out result.StopNode, out result.StopAccessMs, ref result.SourcesOffNetwork);

            Accumulate(graph, dijkstra, inputs.Homes, result.HomeNode, result.HomeAccessMs, inputs.CatchmentMs, horizon, result.Demand, result);
            Accumulate(graph, dijkstra, inputs.Jobs, result.JobNode, result.JobAccessMs, inputs.CatchmentMs, horizon, result.Jobs, result);
            Accumulate(graph, dijkstra, inputs.Future, result.FutureNode, result.FutureAccessMs, inputs.CatchmentMs, horizon, result.Future, result);
            AccumulateStops(graph, dijkstra, inputs, result, horizon);
            return result;
        }

        private static float[][] Grid(int rows, int columns)
        {
            var grid = new float[rows][];
            for (int r = 0; r < rows; r++)
            {
                grid[r] = new float[columns];
            }

            return grid;
        }

        // A point's node and the whole-millisecond walk to it; -1 when nothing lies
        // within the access walk. The straight line stands in for the unmodelled last
        // metres between a building's centre and the pavement.
        public static int SnapPoint(WalkNodeIndex index, float x, float z, int accessMs, out int walkMs)
        {
            double accessMetres = accessMs / 1000.0 * SuitabilityTransit.WalkSpeed;
            int node = index.Nearest(x, z, accessMetres, out double metres);
            walkMs = node >= 0 ? WalkGraph.WalkMilliseconds(metres) : -1;
            if (node >= 0 && walkMs > accessMs)
            {
                node = -1;
                walkMs = -1;
            }

            return node;
        }

        private static void Snap(WalkNodeIndex index, WalkSources sources, int accessMs, out int[] nodes, out int[] walkMs, ref int offNetwork)
        {
            nodes = new int[sources.Count];
            walkMs = new int[sources.Count];
            for (int i = 0; i < sources.Count; i++)
            {
                nodes[i] = SnapPoint(index, sources.X[i], sources.Z[i], accessMs, out walkMs[i]);
                if (nodes[i] < 0)
                {
                    offNetwork++;
                }
            }
        }

        private static void SnapStops(WalkNodeIndex index, WalkAccessInputs inputs, out int[] nodes, out int[] walkMs, ref int offNetwork)
        {
            nodes = new int[inputs.StopCount];
            walkMs = new int[inputs.StopCount];
            for (int i = 0; i < inputs.StopCount; i++)
            {
                nodes[i] = SnapPoint(index, inputs.StopX[i], inputs.StopZ[i], inputs.AccessMs, out walkMs[i]);
                if (nodes[i] < 0)
                {
                    offNetwork++;
                }
            }
        }

        // Sources in index order, settled nodes in settle order: the float sums this
        // produces are reproducible because that order is fixed by the input alone.
        private static void Accumulate(
            WalkGraph graph, IntDijkstra dijkstra, WalkSources sources, int[] nodes, int[] walkMs,
            int[] catchmentMs, long horizon, float[][] into, WalkAccessResult result)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                if (nodes[i] < 0)
                {
                    continue;
                }

                float weight = sources.Weight[i];
                dijkstra.Run(graph, nodes[i], walkMs[i], horizon);
                result.Relaxations += dijkstra.SettledCount;
                for (int s = 0; s < dijkstra.SettledCount; s++)
                {
                    int node = dijkstra.Settled[s];
                    long t = dijkstra.Dist[node];
                    for (int c = 0; c < catchmentMs.Length; c++)
                    {
                        if (t <= catchmentMs[c])
                        {
                            into[c][node] += weight * Kernel(t, catchmentMs[c]);
                        }
                    }
                }
            }
        }

        private static void AccumulateStops(WalkGraph graph, IntDijkstra dijkstra, WalkAccessInputs inputs, WalkAccessResult result, long horizon)
        {
            for (int i = 0; i < inputs.StopCount; i++)
            {
                int type = inputs.StopType[i];
                if (result.StopNode[i] < 0 || type < 0 || type >= result.Interchange.Length)
                {
                    continue;
                }

                dijkstra.Run(graph, result.StopNode[i], result.StopAccessMs[i], horizon);
                result.Relaxations += dijkstra.SettledCount;
                for (int s = 0; s < dijkstra.SettledCount; s++)
                {
                    int node = dijkstra.Settled[s];
                    long t = dijkstra.Dist[node];
                    float transferable = t <= inputs.TransferMs ? Kernel(t, inputs.TransferMs) : 0f;
                    if (t <= inputs.TransferMs)
                    {
                        result.Interchange[type][node] += transferable;
                    }

                    for (int c = 0; c < inputs.CatchmentMs.Length; c++)
                    {
                        if (t <= inputs.CatchmentMs[c])
                        {
                            float within = Kernel(t, inputs.CatchmentMs[c]);
                            result.StopWithin[c][type][node] += within;
                            result.CrossRaw[c][type][node] += within * (1f - transferable);
                        }
                    }
                }
            }
        }

        // The seven raw terms at a network node for one mode: catchment class `cls`,
        // own stop type `selfType`, other types weighted by `typeWeight` (capacity
        // relative to a bus, register A1.10; 0 excludes a type). Cross-mode sums run
        // over types in ascending order.
        public static SuitabilityCell NodeTerms(WalkAccessResult result, int node, int cls, int selfType, float[] typeWeight)
        {
            var cell = new SuitabilityCell
            {
                m_Demand = result.Demand[cls][node],
                m_Jobs = result.Jobs[cls][node],
                m_Future = result.Future[cls][node],
                m_Access = 1f,
            };

            float[][] within = result.StopWithin[cls];
            float[][] cross = result.CrossRaw[cls];
            for (int type = 0; type < within.Length; type++)
            {
                if (type == selfType)
                {
                    cell.m_Coverage += within[type][node];
                    continue;
                }

                float weight = type < typeWeight.Length ? typeWeight[type] : 0f;
                if (weight <= 0f)
                {
                    continue;
                }

                cell.m_Interchange += weight * result.Interchange[type][node];
                cell.m_CrossCoverage += weight * cross[type][node];
            }

            return cell;
        }

        // A tile's terms are its node's terms; only the access term is the tile's own,
        // fading from 1 at the node to 0 at the access walk. A tile with no node in
        // reach — or an unbuildable one — scores nothing.
        public static void TileTerms(
            WalkAccessResult result, WalkNodeIndex index, int width, int height, float worldMinX, float worldMinZ, float tileSize,
            byte[] buildable, int accessMs, int cls, int selfType, float[] typeWeight,
            SuitabilityCell[] terms, int[] tileNode, int[] tileWalkMs)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = x + y * width;
                    tileNode[i] = -1;
                    tileWalkMs[i] = -1;
                    terms[i] = default;
                    if (buildable[i] == 0)
                    {
                        continue;
                    }

                    float cx = worldMinX + (x + 0.5f) * tileSize;
                    float cz = worldMinZ + (y + 0.5f) * tileSize;
                    int node = SnapPoint(index, cx, cz, accessMs, out int walkMs);
                    if (node < 0)
                    {
                        continue;
                    }

                    tileNode[i] = node;
                    tileWalkMs[i] = walkMs;
                    SuitabilityCell cell = NodeTerms(result, node, cls, selfType, typeWeight);
                    cell.m_Access = Kernel(walkMs, accessMs);
                    terms[i] = cell;
                }
            }
        }

        // Which catchment class a horizon belongs to, or -1.
        public static int ClassOf(int[] catchmentMs, int horizonMs)
        {
            return Array.IndexOf(catchmentMs, horizonMs);
        }

    }
}
