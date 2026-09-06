using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using PathMethod = Game.Pathfind.PathMethod;

namespace StationSuitabilityOverlay
{
    // Reads the streets out of the save into an AlignmentNetwork: which edges a
    // transit vehicle may drive, their arc lengths and sampled centrelines, which are
    // highways, and one directed arc per admitted driving direction with its speed
    // limit and end tangents — plus the track segments the rail lattices are priced
    // by. Everything numeric happens on the network; this file only gathers.
    internal static class SuitabilityRoads
    {
        // Bow below which an edge is drawn as a straight chord.
        private const float StraightEnough = 3f;
        private const int MaxCurveSamples = 8;

        // Walks road edges and their endpoint nodes into index arrays and hands them
        // to the network (AlignmentNetwork.AdoptRoads). Node adjacency comes from the
        // game's own ConnectedEdge buffers, so no adjacency has to be inferred — but a
        // compact copy is built so the routing never touches ECS memory.
        public static void Build(
            AlignmentNetwork into,
            EntityManager entityManager,
            EntityQuery roadEdgeQuery,
            ComponentLookup<Node> nodeLookup,
            ComponentLookup<Curve> curveLookup,
            ComponentLookup<PrefabRef> prefabRefLookup,
            ComponentLookup<RoadData> roadDataLookup)
        {
            var nodeIndices = new Dictionary<Entity, int>();
            var edgeA = new List<int>();
            var edgeB = new List<int>();
            var edgeCost = new List<float>();
            var positionsX = new List<float>();
            var positionsZ = new List<float>();
            var shapeX = new List<float>();
            var shapeZ = new List<float>();
            var shapeStart = new List<int>();
            var shapeCount = new List<int>();
            var noStops = new List<bool>();
            var arcs = new AlignmentNetwork.ArcBuilder();
            int oneWayEdges = 0;
            int edgesWithoutCarLane = 0;
            float turnSecondsPerRadian = 0f;

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
                if (!TryAddNode(edge.m_Start, nodeLookup, nodeIndices, positionsX, positionsZ, out int a) ||
                    !TryAddNode(edge.m_End, nodeLookup, nodeIndices, positionsX, positionsZ, out int b))
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

                edgeA.Add(a);
                edgeB.Add(b);
                edgeCost.Add(length);
                noStops.Add(IsHighway(edgeEntity, prefabRefLookup, roadDataLookup));

                shapeStart.Add(shapeX.Count);
                shapeCount.Add(SampleCurve(curve, shapeX, shapeZ));
                AddArcs(entityManager, edgeEntity, edgeA.Count - 1, a, b, curve, length, arcs,
                    ref turnSecondsPerRadian, ref oneWayEdges, ref edgesWithoutCarLane);
            }

            into.AdoptRoads(
                positionsX.ToArray(), positionsZ.ToArray(),
                edgeA.ToArray(), edgeB.ToArray(), edgeCost.ToArray(), noStops.ToArray(),
                shapeX.ToArray(), shapeZ.ToArray(), shapeStart.ToArray(), shapeCount.ToArray(),
                arcs, turnSecondsPerRadian, oneWayEdges, edgesWithoutCarLane);
        }

        // Existing track by kind, so each rail lattice can tell where alignment of ITS
        // kind already exists: an edge with a train-track lane goes to the train lists,
        // one with a metro-track lane to the metro lists (both, if it carries both).
        // Tram track is street-bound and no lattice concern.
        public static void CollectTrackSegments(
            EntityManager entityManager,
            EntityQuery trackEdgeQuery,
            ComponentLookup<Node> nodeLookup,
            List<float2Like> trainStarts,
            List<float2Like> trainEnds,
            List<float2Like> metroStarts,
            List<float2Like> metroEnds)
        {
            trainStarts.Clear();
            trainEnds.Clear();
            metroStarts.Clear();
            metroEnds.Clear();

            using var entities = trackEdgeQuery.ToEntityArray(Allocator.Temp);
            using var edges = trackEdgeQuery.ToComponentDataArray<Edge>(Allocator.Temp);
            for (int i = 0; i < edges.Length; i++)
            {
                // The query covers every net edge, so filter to the ones carrying a
                // track lane — that is what "rail already exists here" means.
                Game.Net.TrackTypes types = TrackTypesOf(entityManager, entities[i]);
                if (types == Game.Net.TrackTypes.None)
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
                if ((types & Game.Net.TrackTypes.Train) != 0)
                {
                    trainStarts.Add(new float2Like(a.x, a.z));
                    trainEnds.Add(new float2Like(b.x, b.z));
                }

                if ((types & Game.Net.TrackTypes.Subway) != 0)
                {
                    metroStarts.Add(new float2Like(a.x, a.z));
                    metroEnds.Add(new float2Like(b.x, b.z));
                }
            }
        }

        // The kinds of track on an edge, read off its lanes' prefabs
        // (Game.Prefabs.TrackLaneData.m_TrackTypes: Train, Tram, Subway).
        private static Game.Net.TrackTypes TrackTypesOf(EntityManager entityManager, Entity edge)
        {
            var types = Game.Net.TrackTypes.None;
            if (!entityManager.TryGetBuffer(edge, isReadOnly: true, out DynamicBuffer<Game.Net.SubLane> lanes))
            {
                return types;
            }

            for (int i = 0; i < lanes.Length; i++)
            {
                if ((lanes[i].m_PathMethods & PathMethod.Track) == 0
                    || !entityManager.TryGetComponent(lanes[i].m_SubLane, out PrefabRef prefabRef)
                    || !entityManager.TryGetComponent(prefabRef.m_Prefab, out TrackLaneData laneData))
                {
                    continue;
                }

                types |= laneData.m_TrackTypes;
            }

            return types;
        }

        // The directions a road vehicle may drive this edge, and how fast. Each car
        // lane (SubLane with PathMethod.Road, carrying CarLane + EdgeLane) runs from the
        // edge's start to its end when EdgeLane.m_EdgeDelta.x < m_EdgeDelta.y, else the
        // other way — the comparison decompiled Game.Pathfind.LaneDataSystem makes; a
        // Twoway lane admits both. The direction's speed is the fastest of its lanes:
        // a bus takes the lane it likes. Headings come from the edge curve's end
        // tangents. The turn cost rate is read once from the first car lane's pathfind
        // prefab (NetLaneData.m_PathfindPrefab -> PathfindCarData.m_CurveAngleCost.x).
        private static void AddArcs(
            EntityManager entityManager, Entity edgeEntity, int edge, int a, int b, Curve curve, float length,
            AlignmentNetwork.ArcBuilder arcs, ref float turnSecondsPerRadian, ref int oneWayEdges, ref int edgesWithoutCarLane)
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
                    ReadTurnRate(entityManager, lane, ref turnSecondsPerRadian);
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
                edgesWithoutCarLane++;
                return;
            }

            float2 startTangent = math.normalizesafe(new float2(curve.m_Bezier.b.x - curve.m_Bezier.a.x, curve.m_Bezier.b.z - curve.m_Bezier.a.z));
            float2 endTangent = math.normalizesafe(new float2(curve.m_Bezier.d.x - curve.m_Bezier.c.x, curve.m_Bezier.d.z - curve.m_Bezier.c.z));
            if (forwardSpeed > 0f)
            {
                arcs.Add(a, b, edge, length, forwardSpeed, startTangent.x, startTangent.y, endTangent.x, endTangent.y);
            }

            if (backwardSpeed > 0f)
            {
                arcs.Add(b, a, edge, length, backwardSpeed, -endTangent.x, -endTangent.y, -startTangent.x, -startTangent.y);
            }

            if ((forwardSpeed > 0f) != (backwardSpeed > 0f))
            {
                oneWayEdges++;
            }
        }

        private static void ReadTurnRate(EntityManager entityManager, Entity lane, ref float turnSecondsPerRadian)
        {
            if (turnSecondsPerRadian > 0f
                || !entityManager.TryGetComponent(lane, out PrefabRef prefabRef)
                || !entityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData)
                || !entityManager.TryGetComponent(laneData.m_PathfindPrefab, out PathfindCarData costs))
            {
                return;
            }

            turnSecondsPerRadian = costs.m_CurveAngleCost.m_Value.x;
        }

        private static bool TryAddNode(
            Entity node,
            ComponentLookup<Node> nodeLookup,
            Dictionary<Entity, int> nodeIndices,
            List<float> positionsX,
            List<float> positionsZ,
            out int index)
        {
            if (nodeIndices.TryGetValue(node, out index))
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
            nodeIndices[node] = index;
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
    }
}
