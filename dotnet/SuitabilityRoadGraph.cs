using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using PathMethod = Game.Pathfind.PathMethod;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

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

        public CompactGraph Graph;
        public float[] NodePositionsX;
        public float[] NodePositionsZ;
        public float[] EdgeFlow;

        private readonly Dictionary<Entity, int> m_NodeIndices = new Dictionary<Entity, int>();
        private readonly List<int> m_EdgeA = new List<int>();
        private readonly List<int> m_EdgeB = new List<int>();
        private readonly List<float> m_EdgeCost = new List<float>();
        private DijkstraWorkspace m_Workspace;

        // Takes ownership of a graph built elsewhere — used for the lattice networks,
        // which are not derived from road entities at all.
        public void Adopt(CompactGraph graph, float[] nodeX, float[] nodeZ)
        {
            Graph = graph;
            NodePositionsX = nodeX;
            NodePositionsZ = nodeZ;
            EdgeFlow = new float[graph.EdgeCount];
            m_Workspace = new DijkstraWorkspace(graph.NodeCount);
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
            if (!entityManager.TryGetBuffer(edge, true, out DynamicBuffer<Game.Net.SubLane> lanes))
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
            ComponentLookup<RoadData> roadDataLookup,
            bool useTravelTime)
        {
            m_NodeIndices.Clear();
            m_EdgeA.Clear();
            m_EdgeB.Clear();
            m_EdgeCost.Clear();

            var positionsX = new List<float>();
            var positionsZ = new List<float>();

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
                float length = math.max(1f, curveLookup[edgeEntity].m_Length);
                float cost = length;

                if (useTravelTime)
                {
                    float speed = ResolveSpeed(entityManager, edgeEntity, prefabRefLookup, roadDataLookup);
                    cost = length / math.max(1f, speed);
                }

                m_EdgeA.Add(a);
                m_EdgeB.Add(b);
                m_EdgeCost.Add(cost);
            }

            NodePositionsX = positionsX.ToArray();
            NodePositionsZ = positionsZ.ToArray();
            Graph = CompactGraph.Build(
                positionsX.Count,
                m_EdgeA.ToArray(),
                m_EdgeB.ToArray(),
                m_EdgeCost.ToArray(),
                m_EdgeA.Count);

            EdgeFlow = new float[Graph.EdgeCount];
            m_Workspace = new DijkstraWorkspace(Graph.NodeCount);
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
            if (!entityManager.TryGetBuffer(edge, true, out DynamicBuffer<Game.Net.SubLane> lanes))
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

        private static float ResolveSpeed(
            EntityManager entityManager,
            Entity edge,
            ComponentLookup<PrefabRef> prefabRefLookup,
            ComponentLookup<RoadData> roadDataLookup)
        {
            if (prefabRefLookup.HasComponent(edge))
            {
                Entity prefab = prefabRefLookup[edge].m_Prefab;
                if (roadDataLookup.HasComponent(prefab))
                {
                    float speed = roadDataLookup[prefab].m_SpeedLimit;
                    if (speed > 0f)
                    {
                        return speed;
                    }
                }
            }

            return 1f;
        }

        // Highways carry traffic but cannot host stops, so a corridor that runs
        // along one is useless as a transit line even though the flow is real.
        public static bool IsHighway(
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

        // Nearest graph node to each zone centre, or -1 where the zone has no road
        // near it. Linear scan per zone: zones number in the hundreds and this only
        // reruns when the graph changes.
        public int[] MapZonesToNodes(int2 zoneGrid, float2 worldMin)
        {
            int zoneCount = zoneGrid.x * zoneGrid.y;
            var mapping = new int[zoneCount];
            float snapSq = ZoneSnapRadius * ZoneSnapRadius;

            for (int zone = 0; zone < zoneCount; zone++)
            {
                float2 centre = SuitabilityTravelDemand.ZoneCentre(zone, worldMin, zoneGrid);
                int best = -1;
                float bestSq = snapSq;

                for (int n = 0; n < NodeCount; n++)
                {
                    float dx = NodePositionsX[n] - centre.x;
                    float dz = NodePositionsZ[n] - centre.y;
                    float distSq = dx * dx + dz * dz;
                    if (distSq < bestSq)
                    {
                        bestSq = distSq;
                        best = n;
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
            if (Graph == null || EdgeFlow == null || flows.Count == 0)
            {
                return 0;
            }

            System.Array.Clear(EdgeFlow, 0, EdgeFlow.Length);
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
