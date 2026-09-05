using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // The road network as a road VEHICLE sees it (register A0.7/A0.8): one arc per
    // drivable direction of a street, timed by its speed limit, with a turn cost at
    // every intersection that depends on how sharply the vehicle has to turn. The
    // undirected CompactGraph the corridor search grows along stays as it is — a
    // street exists in both directions as a place to put a line — but everything that
    // asks "how long does a bus take from here to there" or "which way does the
    // demand actually travel" asks this graph.
    //
    // Costs are whole milliseconds and turns fall into five classes decided by the
    // dot product of unit headings, so an offline checker reproduces every shortest
    // time exactly without a single transcendental function.
    internal sealed class DirectedRoadGraph
    {
        public const int TurnClasses = 5;
        public const int Straight = 0;
        public const int Gentle = 1;
        public const int Turn = 2;
        public const int Sharp = 3;
        public const int UTurn = 4;

        // cos 15°, cos 45°, cos 120°, cos 165°: the class boundaries, as the exact
        // double literals the specification names.
        public const double CosGentle = 0.9659258262890683;
        public const double CosTurn = 0.7071067811865476;
        public const double CosSharp = -0.5;
        public const double CosUTurn = -0.9659258262890683;

        public int NodeCount;
        public float[] NodeX = Array.Empty<float>();
        public float[] NodeZ = Array.Empty<float>();
        public int ArcCount;
        public int[] ArcFrom = Array.Empty<int>();
        public int[] ArcTo = Array.Empty<int>();
        // The undirected edge (CompactGraph index) an arc runs along; -1 if none.
        public int[] ArcEdge = Array.Empty<int>();
        public int[] ArcMs = Array.Empty<int>();
        // What the milliseconds were derived from, kept for the export: the street's
        // arc length and the direction's speed limit (both binary32, from the game).
        public float[] ArcMetres = Array.Empty<float>();
        public float[] ArcSpeed = Array.Empty<float>();
        // Unit headings (binary32) leaving From and arriving at To.
        public float[] OutDx = Array.Empty<float>();
        public float[] OutDz = Array.Empty<float>();
        public float[] InDx = Array.Empty<float>();
        public float[] InDz = Array.Empty<float>();
        public int[] OutOffsets = Array.Empty<int>();
        public int[] OutArcs = Array.Empty<int>();
        // Milliseconds per turn class.
        public int[] TurnMs = new int[TurnClasses];

        public static DirectedRoadGraph Build(
            float[] nodeX, float[] nodeZ,
            int[] arcFrom, int[] arcTo, int[] arcEdge, int[] arcMs,
            float[] outDx, float[] outDz, float[] inDx, float[] inDz,
            int[] turnMs, int arcCount)
        {
            var graph = new DirectedRoadGraph
            {
                NodeCount = nodeX.Length,
                NodeX = nodeX,
                NodeZ = nodeZ,
                ArcCount = arcCount,
                ArcFrom = arcFrom,
                ArcTo = arcTo,
                ArcEdge = arcEdge,
                ArcMs = arcMs,
                OutDx = outDx,
                OutDz = outDz,
                InDx = inDx,
                InDz = inDz,
            };
            Array.Copy(turnMs, graph.TurnMs, Math.Min(TurnClasses, turnMs.Length));

            var offsets = new int[nodeX.Length + 1];
            for (int a = 0; a < arcCount; a++)
            {
                offsets[arcFrom[a] + 1]++;
            }

            for (int n = 0; n < nodeX.Length; n++)
            {
                offsets[n + 1] += offsets[n];
            }

            graph.OutOffsets = offsets;
            graph.OutArcs = new int[arcCount];
            var fill = new int[nodeX.Length];
            for (int a = 0; a < arcCount; a++)
            {
                graph.OutArcs[offsets[arcFrom[a]] + fill[arcFrom[a]]++] = a;
            }

            return graph;
        }

        // Which turn it is to arrive along `into` and leave along `outOf`: the dot
        // product of two binary32 unit headings is exact in double, and the class is
        // read off the fixed cosine boundaries.
        public int TurnClass(int into, int outOf)
        {
            double dot = ((double)InDx[into] * OutDx[outOf]) + ((double)InDz[into] * OutDz[outOf]);
            return TurnClassOf(dot);
        }

        public static int TurnClassOf(double dot)
        {
            if (dot >= CosGentle)
            {
                return Straight;
            }

            if (dot >= CosTurn)
            {
                return Gentle;
            }

            if (dot >= CosSharp)
            {
                return Turn;
            }

            return dot >= CosUTurn ? Sharp : UTurn;
        }

        // Travel time of an arc from the game's arc length and the lane's speed limit:
        // round-half-even(length / speed · 1000) in double, at least 1 ms.
        public static int ArcMilliseconds(float lengthMetres, float speedMetresPerSecond)
        {
            double speed = Math.Max(0.1, (double)speedMetresPerSecond);
            return Math.Max(1, (int)Math.Round((double)lengthMetres / speed * 1000.0, MidpointRounding.ToEven));
        }

        // Turn cost per class from the game's curve-angle time cost (PathfindCarData
        // m_CurveAngleCost.x, seconds per radian) at each class's representative
        // angle: 0°, 30°, 90°, 140°, 180°.
        public static int[] TurnTable(float secondsPerRadian)
        {
            double[] angles = { 0.0, Math.PI / 6.0, Math.PI / 2.0, Math.PI * 7.0 / 9.0, Math.PI };
            var table = new int[TurnClasses];
            for (int c = 0; c < TurnClasses; c++)
            {
                table[c] = (int)Math.Round((double)secondsPerRadian * angles[c] * 1000.0, MidpointRounding.ToEven);
            }

            return table;
        }
    }

    // Shortest driving times on a DirectedRoadGraph. The search state is the arc the
    // vehicle is on, not the node it is at, because the cost of leaving a node depends
    // on how it was entered. Reusable across sources with a touched-list reset.
    internal sealed class DirectedDijkstra
    {
        public const long Unreached = long.MaxValue;

        public long[] Dist;
        public int[] PrevArc;
        private long[] m_HeapKey;
        private int[] m_HeapArc;
        private int m_HeapCount;
        private readonly List<int> m_Touched = new List<int>();
        private int m_Source = -1;

        public DirectedDijkstra(int arcCount)
        {
            Dist = new long[arcCount];
            PrevArc = new int[arcCount];
            m_HeapKey = new long[Math.Max(16, arcCount)];
            m_HeapArc = new int[m_HeapKey.Length];
            for (int i = 0; i < arcCount; i++)
            {
                Dist[i] = Unreached;
                PrevArc[i] = -1;
            }
        }

        public int Source => m_Source;

        // Times to be at the END of every arc reachable from `source` within `maxMs`,
        // having driven that arc last. Ties settle the lower arc index first.
        public void Run(DirectedRoadGraph graph, int source, long maxMs)
        {
            for (int i = 0; i < m_Touched.Count; i++)
            {
                Dist[m_Touched[i]] = Unreached;
                PrevArc[m_Touched[i]] = -1;
            }

            m_Touched.Clear();
            m_HeapCount = 0;
            m_Source = source;
            if (source < 0 || source >= graph.NodeCount)
            {
                return;
            }

            for (int slot = graph.OutOffsets[source]; slot < graph.OutOffsets[source + 1]; slot++)
            {
                int arc = graph.OutArcs[slot];
                Relax(arc, graph.ArcMs[arc], -1, maxMs);
            }

            while (m_HeapCount > 0)
            {
                Pop(out long d, out int arc);
                if (d != Dist[arc])
                {
                    continue;
                }

                int node = graph.ArcTo[arc];
                for (int slot = graph.OutOffsets[node]; slot < graph.OutOffsets[node + 1]; slot++)
                {
                    int next = graph.OutArcs[slot];
                    long nd = d + graph.TurnMs[graph.TurnClass(arc, next)] + graph.ArcMs[next];
                    Relax(next, nd, arc, maxMs);
                }
            }
        }

        private void Relax(int arc, long candidate, int previous, long maxMs)
        {
            if (candidate > maxMs || candidate >= Dist[arc])
            {
                return;
            }

            if (Dist[arc] == Unreached)
            {
                m_Touched.Add(arc);
            }

            Dist[arc] = candidate;
            PrevArc[arc] = previous;
            Push(candidate, arc);
        }

        // The arc that reaches `target` soonest (ties: lower arc index), or -1.
        public int BestArcInto(DirectedRoadGraph graph, int target)
        {
            int best = -1;
            long bestMs = Unreached;
            for (int i = 0; i < m_Touched.Count; i++)
            {
                int arc = m_Touched[i];
                if (graph.ArcTo[arc] != target || Dist[arc] == Unreached)
                {
                    continue;
                }

                if (Dist[arc] < bestMs || (Dist[arc] == bestMs && arc < best))
                {
                    bestMs = Dist[arc];
                    best = arc;
                }
            }

            return best;
        }

        public long TimeTo(DirectedRoadGraph graph, int target)
        {
            int arc = BestArcInto(graph, target);
            return arc < 0 ? Unreached : Dist[arc];
        }

        // Arcs of the best route into `target`, source-first. Empty when unreachable.
        public bool TraceArcs(DirectedRoadGraph graph, int target, List<int> arcs)
        {
            arcs.Clear();
            int arc = BestArcInto(graph, target);
            if (arc < 0)
            {
                return false;
            }

            int guard = graph.ArcCount + 2;
            while (arc >= 0 && guard-- > 0)
            {
                arcs.Add(arc);
                arc = PrevArc[arc];
            }

            arcs.Reverse();
            return true;
        }

        private void Push(long key, int arc)
        {
            if (m_HeapCount == m_HeapKey.Length)
            {
                Array.Resize(ref m_HeapKey, m_HeapKey.Length * 2);
                Array.Resize(ref m_HeapArc, m_HeapKey.Length);
            }

            int i = m_HeapCount++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Less(key, arc, m_HeapKey[parent], m_HeapArc[parent]))
                {
                    break;
                }

                m_HeapKey[i] = m_HeapKey[parent];
                m_HeapArc[i] = m_HeapArc[parent];
                i = parent;
            }

            m_HeapKey[i] = key;
            m_HeapArc[i] = arc;
        }

        private void Pop(out long key, out int arc)
        {
            key = m_HeapKey[0];
            arc = m_HeapArc[0];
            m_HeapCount--;
            if (m_HeapCount == 0)
            {
                return;
            }

            long lastKey = m_HeapKey[m_HeapCount];
            int lastArc = m_HeapArc[m_HeapCount];
            int i = 0;
            while (true)
            {
                int child = (i << 1) + 1;
                if (child >= m_HeapCount)
                {
                    break;
                }

                int right = child + 1;
                if (right < m_HeapCount && Less(m_HeapKey[right], m_HeapArc[right], m_HeapKey[child], m_HeapArc[child]))
                {
                    child = right;
                }

                if (!Less(m_HeapKey[child], m_HeapArc[child], lastKey, lastArc))
                {
                    break;
                }

                m_HeapKey[i] = m_HeapKey[child];
                m_HeapArc[i] = m_HeapArc[child];
                i = child;
            }

            m_HeapKey[i] = lastKey;
            m_HeapArc[i] = lastArc;
        }

        private static bool Less(long keyA, int arcA, long keyB, int arcB)
        {
            return keyA < keyB || (keyA == keyB && arcA < arcB);
        }
    }

    internal static class SuitabilityDirectedRoads
    {
        // Assigns each flow to the fastest DIRECTED route from its origin node to its
        // destination node, adding its weight to every undirected edge the route runs
        // along — so the corridor search still sees one flow figure per street while
        // a one-way street only ever carries the direction it admits. Flows arrive
        // sorted by origin; one search serves every destination of an origin.
        public static int AssignFlow(
            DirectedRoadGraph graph, DirectedDijkstra dijkstra, List<ZoneFlowLike> flows, int[] zoneNodes,
            long maxMs, float[] edgeFlow, float[]? arcFlow, out float assignedWeight)
        {
            assignedWeight = 0f;
            int assigned = 0;
            int currentOrigin = -1;
            for (int i = 0; i < flows.Count; i++)
            {
                ZoneFlowLike flow = flows[i];
                int originNode = zoneNodes[flow.Origin];
                int destinationNode = zoneNodes[flow.Destination];
                if (originNode < 0 || destinationNode < 0 || originNode == destinationNode)
                {
                    continue;
                }

                if (flow.Origin != currentOrigin)
                {
                    currentOrigin = flow.Origin;
                    dijkstra.Run(graph, originNode, maxMs);
                }

                int arc = dijkstra.BestArcInto(graph, destinationNode);
                if (arc < 0)
                {
                    continue;
                }

                while (arc >= 0)
                {
                    if (arcFlow is not null)
                    {
                        arcFlow[arc] += flow.Weight;
                    }

                    int edge = graph.ArcEdge[arc];
                    if (edge >= 0 && edge < edgeFlow.Length)
                    {
                        edgeFlow[edge] += flow.Weight;
                    }

                    arc = dijkstra.PrevArc[arc];
                }

                assigned++;
                assignedWeight += flow.Weight;
            }

            return assigned;
        }
    }

    // A zone-to-zone demand figure as the pure assignment sees it; the ECS side's
    // ZoneFlow carries the same three numbers.
    internal struct ZoneFlowLike
    {
        public int Origin;
        public int Destination;
        public float Weight;
    }
}
