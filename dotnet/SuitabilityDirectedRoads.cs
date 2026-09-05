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

        // Where a point sits on the street network: the undirected edge (by the arc
        // that carries it) whose chord passes nearest, and the fraction t ∈ [0, 1] along
        // that arc's From→To chord. Projection in double from binary32 coordinates;
        // ties on distance go to the lower arc index. -1 when nothing is within
        // `maxMetres`. Stops are placed along a street, not at its ends, so this is how
        // a stop becomes a point a vehicle can be timed from.
        public int NearestArc(float x, float z, double maxMetres, out double t, out double metres)
        {
            int best = -1;
            double bestSq = maxMetres * maxMetres;
            t = 0.0;
            for (int a = 0; a < ArcCount; a++)
            {
                double ax = NodeX[ArcFrom[a]];
                double az = NodeZ[ArcFrom[a]];
                double bx = NodeX[ArcTo[a]];
                double bz = NodeZ[ArcTo[a]];
                double dx = bx - ax;
                double dz = bz - az;
                double len2 = (dx * dx) + (dz * dz);
                double u = len2 > 0.0 ? Math.Max(0.0, Math.Min(1.0, ((((double)x - ax) * dx) + (((double)z - az) * dz)) / len2)) : 0.0;
                double px = ax + (u * dx) - x;
                double pz = az + (u * dz) - z;
                double sq = (px * px) + (pz * pz);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = a;
                    t = u;
                }
            }

            metres = best >= 0 ? Math.Sqrt(bestSq) : 0.0;
            return best;
        }

        // Milliseconds from an arc's tail to the point at fraction t along it.
        public int PositionMs(int arc, double t)
        {
            return (int)Math.Round(t * ArcMs[arc], MidpointRounding.ToEven);
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

            Drain(graph, maxMs);
        }

        // Like Run, but the vehicle starts ON `arc`, `startMs` from its head: the state
        // is that arc with that time, and every turn out of its head is priced.
        public void RunFromArc(DirectedRoadGraph graph, int arc, long startMs, long maxMs)
        {
            for (int i = 0; i < m_Touched.Count; i++)
            {
                Dist[m_Touched[i]] = Unreached;
                PrevArc[m_Touched[i]] = -1;
            }

            m_Touched.Clear();
            m_HeapCount = 0;
            m_Source = -1;
            if (arc < 0 || arc >= graph.ArcCount)
            {
                return;
            }

            Relax(arc, startMs, -1, maxMs);
            Drain(graph, maxMs);
        }

        private void Drain(DirectedRoadGraph graph, long maxMs)
        {
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

        // Time to a point `endMs` along `toArc` from its tail: the best settled state
        // into toArc's tail, plus the turn onto toArc, plus endMs. Also reports which
        // state was used (-1 when unreachable) so the route can be traced.
        public long TimeToPoint(DirectedRoadGraph graph, int toArc, long endMs, out int viaArc)
        {
            viaArc = -1;
            long best = Unreached;
            int tail = graph.ArcFrom[toArc];
            for (int i = 0; i < m_Touched.Count; i++)
            {
                int g = m_Touched[i];
                if (graph.ArcTo[g] != tail || Dist[g] == Unreached)
                {
                    continue;
                }

                long candidate = Dist[g] + graph.TurnMs[graph.TurnClass(g, toArc)] + endMs;
                if (candidate < best || (candidate == best && g < viaArc))
                {
                    best = candidate;
                    viaArc = g;
                }
            }

            return best;
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

    // One stop-to-stop driving leg as the directed model answers it: which arcs the
    // two points were placed on, how far along them, and the time.
    internal struct RoadLeg
    {
        public int FromArc;
        public int ToArc;
        public int StartMs;
        public int EndMs;
        public long Ms;
        public bool SameArc;
    }

    internal static class RoadLegs
    {
        // Fastest driving time from one point on the network to another. Each point
        // may lie on a two-way street, i.e. on two arcs; every pairing is tried and the
        // best kept (ties: lower from-arc, then lower to-arc). Within one pairing: the
        // vehicle starts `StartMs = ms − PositionMs(fromT)` from its arc's head, drives
        // the state graph, and finishes `EndMs = PositionMs(toT)` into the destination
        // arc — except when both points sit on the same arc in travel order, where the
        // time is simply the difference of positions. Returns Unreached when no pairing
        // has a route or a point is off the network.
        public static long PointToPointMs(
            DirectedRoadGraph graph, DirectedDijkstra dijkstra,
            float fromX, float fromZ, float toX, float toZ, double snapMetres, long maxMs, out RoadLeg leg)
        {
            leg = new RoadLeg { FromArc = -1, ToArc = -1, Ms = DirectedDijkstra.Unreached };
            int fromArc = graph.NearestArc(fromX, fromZ, snapMetres, out double fromT, out _);
            int toArc = graph.NearestArc(toX, toZ, snapMetres, out double toT, out _);
            if (fromArc < 0 || toArc < 0)
            {
                return DirectedDijkstra.Unreached;
            }

            foreach ((int fa, double ft) in ArcsOfEdge(graph, fromArc, fromT))
            {
                foreach ((int ta, double tt) in ArcsOfEdge(graph, toArc, toT))
                {
                    long ms = PairMs(graph, dijkstra, fa, ft, ta, tt, maxMs, out RoadLeg candidate);
                    if (ms < leg.Ms || (ms == leg.Ms && ms != DirectedDijkstra.Unreached && (fa < leg.FromArc || (fa == leg.FromArc && ta < leg.ToArc))))
                    {
                        leg = candidate;
                    }
                }
            }

            return leg.Ms;
        }

        // The arc found plus, if the street is two-way, its reverse arc with the
        // fraction measured from the other end. Reverse arc = the other arc sharing the
        // undirected edge index.
        private static List<(int arc, double t)> ArcsOfEdge(DirectedRoadGraph graph, int arc, double t)
        {
            var arcs = new List<(int, double)> { (arc, t) };
            int edge = graph.ArcEdge[arc];
            if (edge < 0)
            {
                return arcs;
            }

            for (int other = 0; other < graph.ArcCount; other++)
            {
                if (other != arc && graph.ArcEdge[other] == edge && graph.ArcFrom[other] == graph.ArcTo[arc] && graph.ArcTo[other] == graph.ArcFrom[arc])
                {
                    arcs.Add((other, 1.0 - t));
                    break;
                }
            }

            return arcs;
        }

        private static long PairMs(
            DirectedRoadGraph graph, DirectedDijkstra dijkstra,
            int fromArc, double fromT, int toArc, double toT, long maxMs, out RoadLeg leg)
        {
            int fromPos = graph.PositionMs(fromArc, fromT);
            int endMs = graph.PositionMs(toArc, toT);
            leg = new RoadLeg { FromArc = fromArc, ToArc = toArc, StartMs = graph.ArcMs[fromArc] - fromPos, EndMs = endMs };
            if (fromArc == toArc && toT >= fromT)
            {
                leg.SameArc = true;
                leg.Ms = endMs - fromPos;
                return leg.Ms;
            }

            dijkstra.RunFromArc(graph, fromArc, leg.StartMs, maxMs);
            leg.Ms = dijkstra.TimeToPoint(graph, toArc, endMs, out _);
            return leg.Ms;
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
