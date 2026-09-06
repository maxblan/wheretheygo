using System.Collections.Generic;
using System;

namespace TransitArchitect
{
    // A network an alignment is traced on, with travel demand loaded onto it: the
    // streets as Roads reads them (buses, trams) or a free-form lattice
    // (metro and train alignment, ferry water crossings). Pure: the ECS reading is
    // Roads' job, this class only ever sees plain arrays.
    //
    // The game's own pathfinder is not usable here: it is agent-shaped (origins and
    // destinations are entities, not coordinates), asynchronous and frame-spread for
    // a live population, and built on unsafe internals. For a bulk origin-destination
    // matrix computed offline, a plain Dijkstra over our own graph is both simpler
    // and far faster, and keeps the cost model under our control.
    internal sealed class AlignmentNetwork
    {

        public RouteNetwork Network;
        public CompactGraph? Graph;
        // The same streets as a road vehicle drives them: one arc per admitted
        // direction, timed by speed limit and turn class (register A0.7/A0.8). Null on
        // a lattice. Built beside Graph so an undirected edge index maps to its arcs.
        public DirectedRoadGraph? Directed;
        public float[] ArcFlow = Array.Empty<float>();
        public float TurnSecondsPerRadian;
        public int OneWayEdges;
        public int EdgesWithoutCarLane;
        public float[] NodePositionsX = Array.Empty<float>();
        public float[] NodePositionsZ = Array.Empty<float>();
        public float[] EdgeFlow = Array.Empty<float>();

        // Per edge: true where a transit line could not call at a stop. Highways carry
        // real traffic and belong in the graph so journeys route over them, but a
        // corridor grown ALONG one is useless as a line — and highways are by
        // construction the heaviest-flow edges on the road graph, so they are exactly
        // what corridor growth reaches for first. Null on a lattice, which has no
        // road class at all.
        public bool[]? EdgeCannotHostStops;

        // Interior points of each edge's actual centreline, in A->B order. Without
        // these a traced route is a chord between intersections, which visibly leaves
        // the street on anything curved — the reason suggested bus and tram lines
        // looked like they cut across blocks.
        public float[]? EdgeShapeX;
        public float[]? EdgeShapeZ;
        public int[]? EdgeShapeStart;
        public int[]? EdgeShapeCount;

        // Zone -> nearest node, cached because it is a bucketed sweep over every node
        // and only the graph itself can change the answer. Build and Adopt are the two
        // things that do, and both drop it.
        private int[]? m_ZoneNodes;
        private int2Like m_ZoneNodeGrid;
        private float2Like m_ZoneNodeWorldMin;
        private DijkstraWorkspace? m_Workspace;
        private DirectedDijkstra? m_DirectedWorkspace;

        // Takes ownership of a graph built elsewhere — used for the lattice networks,
        // which are not derived from road entities at all.
        public void Adopt(CompactGraph graph, float[] nodeX, float[] nodeZ, RouteNetwork network)
        {
            // Which network this is MUST be recorded: the mode-fallback path uses it to
            // decide that a lattice alignment has to be re-traced on streets before a
            // bus may run it. Left at the enum default every graph claimed to be a
            // road, so ferry and metro alignments were relabelled as buses and drawn
            // straight over water and through buildings.
            Network = network;
            Graph = graph;
            Directed = null;
            m_DirectedWorkspace = null;
            ArcFlow = Array.Empty<float>();
            m_ZoneNodes = null;
            NodePositionsX = nodeX;
            NodePositionsZ = nodeZ;
            EdgeFlow = new float[graph.EdgeCount];
            m_Workspace = new DijkstraWorkspace(graph.NodeCount);
            // A lattice has no real centreline to follow, and no road class either.
            EdgeCannotHostStops = null;
            EdgeShapeX = null;
            EdgeShapeZ = null;
            EdgeShapeStart = null;
            EdgeShapeCount = null;
        }

        // Takes the streets as Roads read them: node positions, undirected
        // edges with the arc length as cost, which edges cannot host a stop, the sampled
        // centreline points per edge, and the directed arcs with the game's turn cost
        // rate (0 when no car lane named a pathfind prefab).
        public void AdoptRoads(
            float[] nodeX,
            float[] nodeZ,
            int[] edgeA,
            int[] edgeB,
            float[] edgeCost,
            bool[] edgeCannotHostStops,
            float[] shapeX,
            float[] shapeZ,
            int[] shapeStart,
            int[] shapeCount,
            ArcBuilder arcs,
            float turnSecondsPerRadian,
            int oneWayEdges,
            int edgesWithoutCarLane)
        {
            Network = RouteNetwork.Road;
            m_ZoneNodes = null;
            OneWayEdges = oneWayEdges;
            EdgesWithoutCarLane = edgesWithoutCarLane;
            NodePositionsX = nodeX;
            NodePositionsZ = nodeZ;
            EdgeCannotHostStops = edgeCannotHostStops;
            EdgeShapeX = shapeX;
            EdgeShapeZ = shapeZ;
            EdgeShapeStart = shapeStart;
            EdgeShapeCount = shapeCount;
            Graph = CompactGraph.Build(nodeX.Length, edgeA, edgeB, edgeCost, edgeA.Length);

            EdgeFlow = new float[Graph.EdgeCount];
            m_Workspace = new DijkstraWorkspace(Graph.NodeCount);

            // The CarPathfind prefab's own default (decompiled Game.Prefabs.CarPathfind:
            // m_CurveAngleCost time 2), used only if no car lane named a pathfind prefab.
            TurnSecondsPerRadian = turnSecondsPerRadian > 0f ? turnSecondsPerRadian : 2f;

            Directed = arcs.Build(NodePositionsX, NodePositionsZ, DirectedRoadGraph.TurnTable(TurnSecondsPerRadian));
            ArcFlow = new float[Directed.ArcCount];
            m_DirectedWorkspace = new DirectedDijkstra(Directed.ArcCount);
        }

        public int NodeCount => Graph?.NodeCount ?? 0;
        public int EdgeCount => Graph?.EdgeCount ?? 0;

        // Plain-array accumulator for the directed arcs of the streets being read, filled
        // by Roads and handed to AdoptRoads.
        public sealed class ArcBuilder
        {
            public readonly List<int> From = new List<int>();
            public readonly List<int> To = new List<int>();
            public readonly List<int> Edge = new List<int>();
            public readonly List<int> Ms = new List<int>();
            public readonly List<float> Metres = new List<float>();
            public readonly List<float> Speed = new List<float>();
            public readonly List<float> OutDx = new List<float>();
            public readonly List<float> OutDz = new List<float>();
            public readonly List<float> InDx = new List<float>();
            public readonly List<float> InDz = new List<float>();

            public void Add(int from, int to, int edge, float metres, float speed, float outDx, float outDz, float inDx, float inDz)
            {
                From.Add(from);
                To.Add(to);
                Edge.Add(edge);
                Ms.Add(DirectedRoadGraph.ArcMilliseconds(metres, speed));
                Metres.Add(metres);
                Speed.Add(speed);
                OutDx.Add(outDx);
                OutDz.Add(outDz);
                InDx.Add(inDx);
                InDz.Add(inDz);
            }

            public DirectedRoadGraph Build(float[] nodeX, float[] nodeZ, int[] turnMs)
            {
                DirectedRoadGraph graph = DirectedRoadGraph.Build(
                    nodeX, nodeZ, From.ToArray(), To.ToArray(), Edge.ToArray(), Ms.ToArray(),
                    OutDx.ToArray(), OutDz.ToArray(), InDx.ToArray(), InDz.ToArray(), turnMs, From.Count);
                graph.ArcMetres = Metres.ToArray();
                graph.ArcSpeed = Speed.ToArray();
                return graph;
            }
        }

        // Fastest driving time between two POINTS on the streets (RoadLegs), or
        // long.MaxValue when either is off the network or no directed route exists.
        public long PointLegMs(float2Like from, float2Like to, float snapMetres, long maxMs, out RoadLeg leg)
        {
            leg = new RoadLeg { FromArc = -1, ToArc = -1, Ms = DirectedDijkstra.Unreached };
            if (Directed is null || m_DirectedWorkspace is null)
            {
                return DirectedDijkstra.Unreached;
            }

            return RoadLegs.PointToPointMs(Directed, m_DirectedWorkspace, from.x, from.y, to.x, to.y, snapMetres, maxMs, out leg);
        }

        // Nearest node to a world position. Linear, but only called a handful of times
        // when a corridor is re-traced or an existing line is re-planned.
        public int NearestNode(float2Like position, float maxDistance)
        {
            int best = -1;
            float bestSq = maxDistance * maxDistance;
            for (int n = 0; n < NodeCount; n++)
            {
                float dx = NodePositionsX[n] - position.x;
                float dz = NodePositionsZ[n] - position.y;
                float distSq = dx * dx + dz * dz;
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    best = n;
                }
            }

            return best;
        }

        // Turns a node path into the polyline a vehicle would actually drive,
        // following each edge's centreline rather than cutting the corner.
        public void MaterialisePath(List<int> nodes, List<float2Like> path)
        {
            MaterialisePath(nodes, path, segmentCannotHostStops: null);
        }

        // With `segmentCannotHostStops`, also reports which SEGMENTS of the drawn path
        // lie on an edge no line may call at — one entry per segment, so segment i runs
        // from path[i] to path[i+1]. A path may legitimately cross a motorway to get
        // somewhere; what it may not do is stop on it.
        public void MaterialisePath(List<int> nodes, List<float2Like> path, List<bool>? segmentCannotHostStops)
        {
            path.Clear();
            segmentCannotHostStops?.Clear();
            if (Graph is null || nodes is null || nodes.Count == 0)
            {
                return;
            }

            path.Add(new float2Like(NodePositionsX[nodes[0]], NodePositionsZ[nodes[0]]));

            for (int i = 1; i < nodes.Count; i++)
            {
                int from = nodes[i - 1];
                int to = nodes[i];

                int edge = FindEdge(from, to);
                bool noStops = edge >= 0 && EdgeCannotHostStops is not null
                    && edge < EdgeCannotHostStops.Length && EdgeCannotHostStops[edge];

                // All four shape arrays are written together in Build and left null by
                // Adopt, so they are all present or all absent.
                if (edge >= 0 && EdgeShapeCount is not null && EdgeShapeStart is not null
                    && EdgeShapeX is not null && EdgeShapeZ is not null && EdgeShapeCount[edge] > 0)
                {
                    int start = EdgeShapeStart[edge];
                    int count = EdgeShapeCount[edge];
                    // Samples are stored A->B; this hop may run the other way.
                    bool forward = Graph.EdgeA[edge] == from;
                    for (int k = 0; k < count; k++)
                    {
                        int at = forward ? start + k : start + count - 1 - k;
                        path.Add(new float2Like(EdgeShapeX[at], EdgeShapeZ[at]));
                        segmentCannotHostStops?.Add(noStops);
                    }
                }

                path.Add(new float2Like(NodePositionsX[to], NodePositionsZ[to]));
                segmentCannotHostStops?.Add(noStops);
            }
        }

        // Cheapest edge joining two adjacent nodes, or -1 if they are not adjacent.
        private int FindEdge(int from, int to)
        {
            if (Graph is null)
            {
                return -1;
            }

            int best = -1;
            float bestCost = float.MaxValue;
            for (int a = Graph.NodeOffsets[from]; a < Graph.NodeOffsets[from + 1]; a++)
            {
                if (Graph.AdjOther[a] != to)
                {
                    continue;
                }

                int edge = Graph.AdjEdge[a];
                if (Graph.EdgeCost[edge] < bestCost)
                {
                    bestCost = Graph.EdgeCost[edge];
                    best = edge;
                }
            }

            return best;
        }

        // Shortest path between two nodes as a list of node indices, for re-tracing a
        // corridor on a different network.
        public bool TracePath(int fromNode, int toNode, float maxCost, List<int> nodes)
        {
            nodes.Clear();
            if (Graph is null || m_Workspace is null || fromNode < 0 || toNode < 0 || fromNode == toNode)
            {
                return false;
            }

            m_Workspace.Run(Graph, fromNode, maxCost);
            if (m_Workspace.Dist[toNode] == float.MaxValue)
            {
                return false;
            }

            int node = toNode;
            int guard = Graph.EdgeCount + 2;
            while (node != fromNode && guard-- > 0)
            {
                nodes.Add(node);
                int edge = m_Workspace.PrevEdge[node];
                if (edge < 0)
                {
                    return false;
                }

                node = Graph.OtherEnd(edge, node);
            }

            nodes.Add(fromNode);
            nodes.Reverse();
            return true;
        }

        // Mean flow along a traced path, matching how corridor flow is measured.
        // The assigned flow of the path edge nearest to `point`: what rides through
        // that point of the line. Zero off the path or without flow.
        public float FlowNear(List<int> nodes, float2Like point)
        {
            if (Graph is null || EdgeFlow is null || nodes.Count < 2)
            {
                return 0f;
            }

            float bestSq = float.MaxValue;
            int bestEdge = -1;
            for (int i = 1; i < nodes.Count; i++)
            {
                int a = nodes[i - 1];
                int b = nodes[i];
                if (a < 0 || b < 0 || a >= NodeCount || b >= NodeCount)
                {
                    continue;
                }

                var from = new float2Like(NodePositionsX[a], NodePositionsZ[a]);
                var to = new float2Like(NodePositionsX[b], NodePositionsZ[b]);
                float lengthSq = float2Like.DistanceSq(from, to);
                float t = lengthSq > 0f ? SuitabilityScoring.Saturate(float2Like.Dot(point - from, to - from) / lengthSq) : 0f;
                float distSq = float2Like.DistanceSq(point, float2Like.Lerp(from, to, t));
                if (distSq >= bestSq)
                {
                    continue;
                }

                int start = Graph.NodeOffsets[a];
                int end = Graph.NodeOffsets[a + 1];
                for (int k = start; k < end; k++)
                {
                    if (Graph.AdjOther[k] == b)
                    {
                        bestSq = distSq;
                        bestEdge = Graph.AdjEdge[k];
                        break;
                    }
                }
            }

            return bestEdge >= 0 && bestEdge < EdgeFlow.Length ? EdgeFlow[bestEdge] : 0f;
        }

        public float FlowAlong(List<int> nodes)
        {
            if (Graph is null || EdgeFlow is null || nodes.Count < 2)
            {
                return 0f;
            }

            float weighted = 0f;
            float length = 0f;
            for (int i = 1; i < nodes.Count; i++)
            {
                int a = nodes[i - 1];
                int b = nodes[i];
                int start = Graph.NodeOffsets[a];
                int end = Graph.NodeOffsets[a + 1];
                for (int k = start; k < end; k++)
                {
                    if (Graph.AdjOther[k] != b)
                    {
                        continue;
                    }

                    int edge = Graph.AdjEdge[k];
                    weighted += EdgeFlow[edge] * Graph.EdgeCost[edge];
                    length += Graph.EdgeCost[edge];
                    break;
                }
            }

            return length > 0f ? weighted / length : 0f;
        }

        // Nearest graph node to each zone centre, or -1 where the zone has no node
        // near it.
        //
        // Bucketed rather than scanned: a linear sweep is zones x nodes, which on a
        // lattice network is tens of millions of distance tests per rebuild and was
        // the visible stall when the overlay opened.
        public int[] MapZonesToNodes(int2Like zoneGrid, float2Like worldMin)
        {
            if (m_ZoneNodes is not null && m_ZoneNodeGrid.Equals(zoneGrid) && m_ZoneNodeWorldMin.Equals(worldMin))
            {
                return m_ZoneNodes;
            }

            int zoneCount = zoneGrid.x * zoneGrid.y;
            var mapping = new int[zoneCount];
            for (int i = 0; i < zoneCount; i++)
            {
                mapping[i] = -1;
            }

            m_ZoneNodes = mapping;
            m_ZoneNodeGrid = zoneGrid;
            m_ZoneNodeWorldMin = worldMin;
            if (NodeCount == 0)
            {
                return mapping;
            }

            // One bucket per zone cell, so a zone only examines the nodes in itself
            // and its eight neighbours.
            var buckets = new List<int>[zoneCount];
            for (int n = 0; n < NodeCount; n++)
            {
                var position = new float2Like(NodePositionsX[n], NodePositionsZ[n]);
                int zone = DemandZones.ZoneOf(position, worldMin, zoneGrid);
                if (zone < 0)
                {
                    continue;
                }

                (buckets[zone] ?? (buckets[zone] = new List<int>())).Add(n);
            }

            float snapSq = Assumptions.ZoneSnapRadius * Assumptions.ZoneSnapRadius;
            for (int zone = 0; zone < zoneCount; zone++)
            {
                float2Like centre = DemandZones.ZoneCentre(zone, worldMin, zoneGrid);
                int zx = zone % zoneGrid.x;
                int zy = zone / zoneGrid.x;
                int best = -1;
                float bestSq = snapSq;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = zy + dy;
                    if (ny < 0 || ny >= zoneGrid.y)
                    {
                        continue;
                    }
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = zx + dx;
                        if (nx < 0 || nx >= zoneGrid.x)
                        {
                            continue;
                        }

                        List<int> bucket = buckets[nx + ny * zoneGrid.x];
                        if (bucket is null)
                        {
                            continue;
                        }

                        for (int i = 0; i < bucket.Count; i++)
                        {
                            int node = bucket[i];
                            float ndx = NodePositionsX[node] - centre.x;
                            float ndz = NodePositionsZ[node] - centre.y;
                            float distSq = ndx * ndx + ndz * ndz;
                            if (distSq < bestSq)
                            {
                                bestSq = distSq;
                                best = node;
                            }
                        }
                    }
                }

                mapping[zone] = best;
            }

            return mapping;
        }

        // Loads zone-to-zone demand onto the graph. One Dijkstra per origin zone
        // serves every destination from it, so the cost is (origin zones x graph)
        // rather than (pairs x graph).
        //
        // `flows` must be sorted by origin, which DemandZones.Aggregate
        // guarantees.
        public int AssignFlow(List<ZoneFlow> flows, int[] zoneNodes, float maxCost, out float assignedWeight)
        {
            assignedWeight = 0f;
            if (Graph is null || m_Workspace is null || EdgeFlow is null || flows.Count == 0)
            {
                return 0;
            }

            System.Array.Clear(EdgeFlow, 0, EdgeFlow.Length);
            if (Directed is not null && m_DirectedWorkspace is not null)
            {
                System.Array.Clear(ArcFlow, 0, ArcFlow.Length);
                var directedFlows = new List<ZoneFlowLike>(flows.Count);
                for (int i = 0; i < flows.Count; i++)
                {
                    directedFlows.Add(new ZoneFlowLike { Origin = flows[i].m_Origin, Destination = flows[i].m_Destination, Weight = flows[i].m_Weight });
                }

                // Metres of cap become milliseconds at the planning cruise speed of a
                // bus, so the ceiling keeps its meaning of "not one line's journey".
                long maxMs = (long)(maxCost / Assumptions.CruiseSpeedFor(ModePreset.Bus) * 1000f);
                return DirectedRoads.AssignFlow(Directed, m_DirectedWorkspace, directedFlows, zoneNodes, maxMs, EdgeFlow, ArcFlow, out assignedWeight);
            }

            int assignedPairs = 0;
            int currentOrigin = -1;

            for (int i = 0; i < flows.Count; i++)
            {
                ZoneFlow flow = flows[i];
                int originNode = zoneNodes[flow.m_Origin];
                int destinationNode = zoneNodes[flow.m_Destination];
                if (originNode < 0 || destinationNode < 0 || originNode == destinationNode)
                {
                    continue;
                }

                // flows is grouped by origin, so the search is reused across the run
                // of pairs sharing it.
                if (flow.m_Origin != currentOrigin)
                {
                    currentOrigin = flow.m_Origin;
                    m_Workspace.Run(Graph, originNode, maxCost);
                }

                if (GraphMath.AccumulatePath(Graph, m_Workspace, originNode, destinationNode, flow.m_Weight, EdgeFlow))
                {
                    assignedPairs++;
                    assignedWeight += flow.m_Weight;
                }
            }

            return assignedPairs;
        }
    }
}
