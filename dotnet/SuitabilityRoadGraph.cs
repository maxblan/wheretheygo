using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using PathMethod = Game.Pathfind.PathMethod;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Colossal.Mathematics;
using Unity.Mathematics;
using System;

namespace StationSuitabilityOverlay
{
    // A routable network with travel demand loaded onto it. Built either from the
    // real road entities (buses, trams) or adopted from a free-form lattice (metro
    // and train alignment, ferry water crossings).
    //
    // The game's own pathfinder is not usable here: it is agent-shaped (origins and
    // destinations are entities, not coordinates), asynchronous and frame-spread for
    // a live population, and built on unsafe internals. For a bulk origin-destination
    // matrix computed offline, a plain Dijkstra over our own graph is both simpler
    // and far faster, and keeps the cost model under our control.
    internal sealed class SuitabilityRoadGraph
    {
        // Nodes further apart than this from a zone centre are not considered that
        // zone's access point — a zone with no road near its middle simply does not
        // participate in the assignment.
        private const float ZoneSnapRadius = SuitabilityTravelDemand.ZoneSize;

        // Bow below which an edge is drawn as a straight chord.
        private const float StraightEnough = 3f;
        private const int MaxCurveSamples = 8;

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
        private int2 m_ZoneNodeGrid;
        private float2 m_ZoneNodeWorldMin;

        private readonly Dictionary<Entity, int> m_NodeIndices = new Dictionary<Entity, int>();
        private readonly List<int> m_EdgeA = new List<int>();
        private readonly List<int> m_EdgeB = new List<int>();
        private readonly List<float> m_EdgeCost = new List<float>();
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

        // Collects the endpoints of every track edge, so the rail lattice can tell
        // where alignment already exists.
        public static void CollectTrackSegments(
            EntityManager entityManager,
            EntityQuery trackEdgeQuery,
            ComponentLookup<Node> nodeLookup,
            List<float2> starts,
            List<float2> ends)
        {
            starts.Clear();
            ends.Clear();

            using var entities = trackEdgeQuery.ToEntityArray(Allocator.Temp);
            using var edges = trackEdgeQuery.ToComponentDataArray<Edge>(Allocator.Temp);
            for (int i = 0; i < edges.Length; i++)
            {
                // The query covers every net edge, so filter to the ones carrying a
                // track lane — that is what "rail already exists here" means.
                if (!HasTrackLane(entityManager, entities[i]))
                {
                    continue;
                }

                Edge edge = edges[i];
                if (!nodeLookup.HasComponent(edge.m_Start) || !nodeLookup.HasComponent(edge.m_End))
                {
                    continue;
                }

                float3 a = nodeLookup[edge.m_Start].m_Position;
                float3 b = nodeLookup[edge.m_End].m_Position;
                starts.Add(new float2(a.x, a.z));
                ends.Add(new float2(b.x, b.z));
            }
        }

        private static bool HasTrackLane(EntityManager entityManager, Entity edge)
        {
            if (!entityManager.TryGetBuffer(edge, isReadOnly: true, out DynamicBuffer<Game.Net.SubLane> lanes))
            {
                return false;
            }

            for (int i = 0; i < lanes.Length; i++)
            {
                if ((lanes[i].m_PathMethods & PathMethod.Track) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        public int NodeCount => Graph?.NodeCount ?? 0;
        public int EdgeCount => Graph?.EdgeCount ?? 0;

        // Walks road edges and their endpoint nodes into index arrays. Node
        // adjacency comes from the game's own ConnectedEdge buffers, so no adjacency
        // has to be inferred — but we still rebuild a compact copy so the routing
        // never touches ECS memory.
        public void Build(
            EntityManager entityManager,
            EntityQuery roadEdgeQuery,
            ComponentLookup<Node> nodeLookup,
            ComponentLookup<Curve> curveLookup,
            ComponentLookup<PrefabRef> prefabRefLookup,
            ComponentLookup<RoadData> roadDataLookup)
        {
            Network = RouteNetwork.Road;
            m_ZoneNodes = null;
            m_NodeIndices.Clear();
            m_EdgeA.Clear();
            m_EdgeB.Clear();
            m_EdgeCost.Clear();

            var positionsX = new List<float>();
            var positionsZ = new List<float>();
            var shapeX = new List<float>();
            var shapeZ = new List<float>();
            var shapeStart = new List<int>();
            var shapeCount = new List<int>();
            var noStops = new List<bool>();
            var arcs = new ArcBuilder();
            OneWayEdges = 0;
            EdgesWithoutCarLane = 0;
            TurnSecondsPerRadian = 0f;

            using var edges = roadEdgeQuery.ToEntityArray(Allocator.Temp);
            using var edgeData = roadEdgeQuery.ToComponentDataArray<Edge>(Allocator.Temp);

            for (int i = 0; i < edges.Length; i++)
            {
                Entity edgeEntity = edges[i];
                if (!curveLookup.HasComponent(edgeEntity))
                {
                    continue;
                }

                if (!IsTransitDrivable(entityManager, edgeEntity))
                {
                    continue;
                }

                Edge edge = edgeData[i];
                if (!TryAddNode(edge.m_Start, nodeLookup, positionsX, positionsZ, out int a) ||
                    !TryAddNode(edge.m_End, nodeLookup, positionsX, positionsZ, out int b))
                {
                    continue;
                }

                if (a == b)
                {
                    continue;
                }

                // Curve.m_Length is a precomputed arc length on the edge, so the
                // distance cost is exact rather than a midpoint approximation.
                Curve curve = curveLookup[edgeEntity];
                float length = math.max(1f, curve.m_Length);

                m_EdgeA.Add(a);
                m_EdgeB.Add(b);
                m_EdgeCost.Add(length);
                noStops.Add(IsHighway(edgeEntity, prefabRefLookup, roadDataLookup));

                shapeStart.Add(shapeX.Count);
                shapeCount.Add(SampleCurve(curve, shapeX, shapeZ));
                AddArcs(entityManager, edgeEntity, m_EdgeA.Count - 1, a, b, curve, length, arcs);
            }

            NodePositionsX = positionsX.ToArray();
            NodePositionsZ = positionsZ.ToArray();
            EdgeCannotHostStops = noStops.ToArray();
            EdgeShapeX = shapeX.ToArray();
            EdgeShapeZ = shapeZ.ToArray();
            EdgeShapeStart = shapeStart.ToArray();
            EdgeShapeCount = shapeCount.ToArray();
            Graph = CompactGraph.Build(
                positionsX.Count,
                m_EdgeA.ToArray(),
                m_EdgeB.ToArray(),
                m_EdgeCost.ToArray(),
                m_EdgeA.Count);

            EdgeFlow = new float[Graph.EdgeCount];
            m_Workspace = new DijkstraWorkspace(Graph.NodeCount);

            if (TurnSecondsPerRadian <= 0f)
            {
                // The CarPathfind prefab's own default (decompiled Game.Prefabs.CarPathfind:
                // m_CurveAngleCost time 2), used only if no car lane named a pathfind prefab.
                TurnSecondsPerRadian = 2f;
            }

            Directed = arcs.Build(NodePositionsX, NodePositionsZ, DirectedRoadGraph.TurnTable(TurnSecondsPerRadian));
            ArcFlow = new float[Directed.ArcCount];
            m_DirectedWorkspace = new DirectedDijkstra(Directed.ArcCount);
        }

        // Plain-array accumulator for the directed arcs of the streets being read.
        private sealed class ArcBuilder
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

            public void Add(int from, int to, int edge, float metres, float speed, float2 outHeading, float2 inHeading)
            {
                From.Add(from);
                To.Add(to);
                Edge.Add(edge);
                Ms.Add(DirectedRoadGraph.ArcMilliseconds(metres, speed));
                Metres.Add(metres);
                Speed.Add(speed);
                OutDx.Add(outHeading.x);
                OutDz.Add(outHeading.y);
                InDx.Add(inHeading.x);
                InDz.Add(inHeading.y);
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

        // The directions a road vehicle may drive this edge, and how fast. Each car
        // lane (SubLane with PathMethod.Road, carrying CarLane + EdgeLane) runs from the
        // edge's start to its end when EdgeLane.m_EdgeDelta.x < m_EdgeDelta.y, else the
        // other way — the comparison decompiled Game.Pathfind.LaneDataSystem makes; a
        // Twoway lane admits both. The direction's speed is the fastest of its lanes:
        // a bus takes the lane it likes. Headings come from the edge curve's end
        // tangents. The turn cost rate is read once from the first car lane's pathfind
        // prefab (NetLaneData.m_PathfindPrefab -> PathfindCarData.m_CurveAngleCost.x).
        private void AddArcs(EntityManager entityManager, Entity edgeEntity, int edge, int a, int b, Curve curve, float length, ArcBuilder arcs)
        {
            float forwardSpeed = 0f;
            float backwardSpeed = 0f;
            bool anyCarLane = false;
            if (entityManager.TryGetBuffer(edgeEntity, isReadOnly: true, out DynamicBuffer<Game.Net.SubLane> lanes))
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    if ((lanes[i].m_PathMethods & (PathMethod.Road | PathMethod.PublicTransportDay)) == 0)
                    {
                        continue;
                    }

                    Entity lane = lanes[i].m_SubLane;
                    if (!entityManager.TryGetComponent(lane, out Game.Net.CarLane carLane)
                        || !entityManager.TryGetComponent(lane, out EdgeLane edgeLane))
                    {
                        continue;
                    }

                    anyCarLane = true;
                    ReadTurnRate(entityManager, lane);
                    bool forward = edgeLane.m_EdgeDelta.x < edgeLane.m_EdgeDelta.y;
                    bool twoWay = (carLane.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0;
                    if (forward || twoWay)
                    {
                        forwardSpeed = math.max(forwardSpeed, carLane.m_SpeedLimit);
                    }

                    if (!forward || twoWay)
                    {
                        backwardSpeed = math.max(backwardSpeed, carLane.m_SpeedLimit);
                    }
                }
            }

            if (!anyCarLane)
            {
                EdgesWithoutCarLane++;
                return;
            }

            float2 startTangent = math.normalizesafe(new float2(curve.m_Bezier.b.x - curve.m_Bezier.a.x, curve.m_Bezier.b.z - curve.m_Bezier.a.z));
            float2 endTangent = math.normalizesafe(new float2(curve.m_Bezier.d.x - curve.m_Bezier.c.x, curve.m_Bezier.d.z - curve.m_Bezier.c.z));
            if (forwardSpeed > 0f)
            {
                arcs.Add(a, b, edge, length, forwardSpeed, startTangent, endTangent);
            }

            if (backwardSpeed > 0f)
            {
                arcs.Add(b, a, edge, length, backwardSpeed, -endTangent, -startTangent);
            }

            if ((forwardSpeed > 0f) != (backwardSpeed > 0f))
            {
                OneWayEdges++;
            }
        }

        private void ReadTurnRate(EntityManager entityManager, Entity lane)
        {
            if (TurnSecondsPerRadian > 0f
                || !entityManager.TryGetComponent(lane, out PrefabRef prefabRef)
                || !entityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData)
                || !entityManager.TryGetComponent(laneData.m_PathfindPrefab, out PathfindCarData costs))
            {
                return;
            }

            TurnSecondsPerRadian = costs.m_CurveAngleCost.m_Value.x;
        }

        // Fastest driving time between two POINTS on the streets (RoadLegs), or
        // long.MaxValue when either is off the network or no directed route exists.
        public long PointLegMs(float2 from, float2 to, float snapMetres, long maxMs, out RoadLeg leg)
        {
            leg = new RoadLeg { FromArc = -1, ToArc = -1, Ms = DirectedDijkstra.Unreached };
            if (Directed is null || m_DirectedWorkspace is null)
            {
                return DirectedDijkstra.Unreached;
            }

            return RoadLegs.PointToPointMs(Directed, m_DirectedWorkspace, from.x, from.y, to.x, to.y, snapMetres, maxMs, out leg);
        }

        // Fastest driving time between two nodes on the directed graph, or long.MaxValue.
        public long DirectedTimeMs(int fromNode, int toNode, long maxMs)
        {
            if (Directed is null || m_DirectedWorkspace is null || fromNode < 0 || toNode < 0)
            {
                return DirectedDijkstra.Unreached;
            }

            if (fromNode == toNode)
            {
                return 0;
            }

            if (m_DirectedWorkspace.Source != fromNode)
            {
                m_DirectedWorkspace.Run(Directed, fromNode, maxMs);
            }

            return m_DirectedWorkspace.TimeTo(Directed, toNode);
        }

        private bool TryAddNode(
            Entity node,
            ComponentLookup<Node> nodeLookup,
            List<float> positionsX,
            List<float> positionsZ,
            out int index)
        {
            if (m_NodeIndices.TryGetValue(node, out index))
            {
                return true;
            }

            if (!nodeLookup.HasComponent(node))
            {
                index = -1;
                return false;
            }

            float3 position = nodeLookup[node].m_Position;
            index = positionsX.Count;
            positionsX.Add(position.x);
            positionsZ.Add(position.z);
            m_NodeIndices[node] = index;
            return true;
        }

        // An edge is usable by a transit vehicle when at least one of its sub-lanes
        // admits road traffic. SubLane.m_PathMethods states this directly, which is
        // far more reliable than inferring it from the prefab.
        private static bool IsTransitDrivable(EntityManager entityManager, Entity edge)
        {
            if (!entityManager.TryGetBuffer(edge, isReadOnly: true, out DynamicBuffer<Game.Net.SubLane> lanes))
            {
                return false;
            }

            for (int i = 0; i < lanes.Length; i++)
            {
                PathMethod methods = lanes[i].m_PathMethods;
                if ((methods & (PathMethod.Road | PathMethod.PublicTransportDay)) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Highways carry traffic but cannot host stops, so a corridor that runs
        // along one is useless as a transit line even though the flow is real.
        private static bool IsHighway(
            Entity edge,
            ComponentLookup<PrefabRef> prefabRefLookup,
            ComponentLookup<RoadData> roadDataLookup)
        {
            if (!prefabRefLookup.HasComponent(edge))
            {
                return false;
            }

            Entity prefab = prefabRefLookup[edge].m_Prefab;
            if (!roadDataLookup.HasComponent(prefab))
            {
                return false;
            }

            return (roadDataLookup[prefab].m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
        }

        // Nearest node to a world position. Linear, but only called a handful of times
        // when a corridor is re-traced or an existing line is re-planned.
        public int NearestNode(float2 position, float maxDistance)
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

        // Interior samples of one edge's centreline, appended in A->B order. Straight
        // edges get none: the chord already IS the street, and every extra vertex
        // costs a draw call and a joint dot.
        private static int SampleCurve(Curve curve, List<float> shapeX, List<float> shapeZ)
        {
            float3 start = curve.m_Bezier.a;
            float3 end = curve.m_Bezier.d;
            float3 middle = MathUtils.Position(curve.m_Bezier, 0.5f);
            float2 chordMid = new float2((start.x + end.x) * 0.5f, (start.z + end.z) * 0.5f);
            float bow = math.distance(new float2(middle.x, middle.z), chordMid);

            if (bow < StraightEnough)
            {
                return 0;
            }

            // One sample per ~8 m of bow, so a gentle bend gets a couple of points and
            // a hairpin gets enough to read as a curve.
            int count = math.clamp((int)math.round(bow / 8f), 1, MaxCurveSamples);
            for (int i = 1; i <= count; i++)
            {
                float3 point = MathUtils.Position(curve.m_Bezier, i / (float)(count + 1));
                shapeX.Add(point.x);
                shapeZ.Add(point.z);
            }

            return count;
        }

        // Turns a node path into the polyline a vehicle would actually drive,
        // following each edge's centreline rather than cutting the corner.
        public void MaterialisePath(List<int> nodes, List<float2> path)
        {
            path.Clear();
            if (Graph is null || nodes is null || nodes.Count == 0)
            {
                return;
            }

            path.Add(new float2(NodePositionsX[nodes[0]], NodePositionsZ[nodes[0]]));

            for (int i = 1; i < nodes.Count; i++)
            {
                int from = nodes[i - 1];
                int to = nodes[i];

                int edge = FindEdge(from, to);
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
                        path.Add(new float2(EdgeShapeX[at], EdgeShapeZ[at]));
                    }
                }

                path.Add(new float2(NodePositionsX[to], NodePositionsZ[to]));
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
        public int[] MapZonesToNodes(int2 zoneGrid, float2 worldMin)
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
                var position = new float2(NodePositionsX[n], NodePositionsZ[n]);
                int zone = SuitabilityTravelDemand.ZoneOf(position, worldMin, zoneGrid);
                if (zone < 0)
                {
                    continue;
                }

                (buckets[zone] ?? (buckets[zone] = new List<int>())).Add(n);
            }

            float snapSq = ZoneSnapRadius * ZoneSnapRadius;
            for (int zone = 0; zone < zoneCount; zone++)
            {
                float2 centre = SuitabilityTravelDemand.ZoneCentre(zone, worldMin, zoneGrid);
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
        // `flows` must be sorted by origin, which SuitabilityTravelDemand.Aggregate
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
                long maxMs = (long)(maxCost / TransitModes.CruiseSpeedFor(ModePreset.Bus) * 1000f);
                return SuitabilityDirectedRoads.AssignFlow(Directed, m_DirectedWorkspace, directedFlows, zoneNodes, maxMs, EdgeFlow, ArcFlow, out assignedWeight);
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

                if (SuitabilityGraphMath.AccumulatePath(Graph, m_Workspace, originNode, destinationNode, flow.m_Weight, EdgeFlow))
                {
                    assignedPairs++;
                    assignedWeight += flow.m_Weight;
                }
            }

            return assignedPairs;
        }
    }
}
