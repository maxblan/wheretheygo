using System.Collections.Generic;
using Colossal.Entities;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace WhereTheyGo
{
    // The pedestrian network read out of the streets. The graph it
    // builds is WalkGraph (pure); the arithmetic on it is WalkAccess and Coverage.
    internal static class WalkNetwork
    {
        // The float2/int2 face of TileGrid for the ECS side; the arithmetic lives there.
        public static int2 GridDims(float2 size, float cellSize)
        {
            int2Like dims = TileGrid.GridDims(new float2Like(size.x, size.y), cellSize);
            return new int2(dims.x, dims.y);
        }

        // The pedestrian network: every net edge with a lane a
        // pedestrian may use (streets with pavements and stand-alone paths alike,
        // read straight from SubLane.m_PathMethods), plus the nodes those edges end
        // on. Edge length is the game's own arc length (Curve.m_Length). Node and edge
        // order follow the query, and nodes are numbered in first-seen order, so the
        // arrays are a deterministic function of the save.
        public static void CollectWalkNetwork(
            EntityManager entityManager,
            EntityQuery nodeQuery,
            EntityQuery edgeQuery,
            out float[] nodeX,
            out float[] nodeZ,
            out int[] edgeA,
            out int[] edgeB,
            out float[] edgeMetres,
            out bool[] nodeSiteable,
            out int edgesWithoutPavement,
            out int bridged)
        {
            edgesWithoutPavement = 0;
            using var nodeEntities = nodeQuery.ToEntityArray(Allocator.Temp);
            using var nodeData = nodeQuery.ToComponentDataArray<Game.Net.Node>(Allocator.Temp);
            var nodeMap = new Dictionary<Entity, float3>(nodeEntities.Length);
            for (int i = 0; i < nodeEntities.Length; i++)
            {
                nodeMap[nodeEntities[i]] = nodeData[i].m_Position;
            }

            using var edgeEntities = edgeQuery.ToEntityArray(Allocator.Temp);
            using var edges = edgeQuery.ToComponentDataArray<Game.Net.Edge>(Allocator.Temp);
            using var curves = edgeQuery.ToComponentDataArray<Game.Net.Curve>(Allocator.Temp);
            var index = new Dictionary<Entity, int>();
            var xs = new List<float>();
            var zs = new List<float>();
            var a = new List<int>();
            var b = new List<int>();
            var metres = new List<float>();
            var onGround = new List<bool>();
            for (int i = 0; i < edgeEntities.Length; i++)
            {
                if (!HasPedestrianLane(entityManager, edgeEntities[i]))
                {
                    edgesWithoutPavement++;
                    continue;
                }

                Game.Net.Edge edge = edges[i];
                if (!TryIndexNode(edge.m_Start, nodeMap, index, xs, zs, onGround, out int start)
                    || !TryIndexNode(edge.m_End, nodeMap, index, xs, zs, onGround, out int end)
                    || start == end)
                {
                    continue;
                }

                a.Add(start);
                b.Add(end);
                metres.Add(curves[i].m_Length);
                onGround[start] = onGround[start] || EndOnGround(entityManager, edgeEntities[i], atStart: true);
                onGround[end] = onGround[end] || EndOnGround(entityManager, edgeEntities[i], atStart: false);
            }

            nodeX = xs.ToArray();
            nodeZ = zs.ToArray();

            // The graph as collected is in pieces: a segment without a pavement cuts the
            // street it belongs to, and everything past it becomes unreachable. Rejoin
            // the pieces before anyone measures a walk on it; see WalkBridging for what
            // that was costing.
            bridged = WalkBridging.Bridge(nodeX, nodeZ, a, b, metres, Assumptions.WalkBridgeMetres);

            edgeA = a.ToArray();
            edgeB = b.ToArray();
            edgeMetres = metres.ToArray();
            nodeSiteable = onGround.ToArray();
        }

        // Whether the pavement meets this node on the ground. The game's own
        // classification is used: the node composition at an edge's end carries
        // `CompositionFlags.General.Tunnel` or `.Elevated` (`NetCompositionData.m_Flags`,
        // set by `NetCompositionHelpers` from the pieces' `NetPieceRequirements`). A
        // node is a site as soon as ONE edge meets it on the ground, so a tunnel portal
        // or a bridgehead stays one; a merely raised or lowered street (`Side.Raised`/
        // `Side.Lowered`) is ground. An edge without a composition is treated as ground.
        private static bool EndOnGround(EntityManager entityManager, Entity edge, bool atStart)
        {
            if (!entityManager.HasComponent<Game.Net.Composition>(edge))
            {
                return true;
            }

            Game.Net.Composition composition = entityManager.GetComponentData<Game.Net.Composition>(edge);
            Entity nodeComposition = atStart ? composition.m_StartNode : composition.m_EndNode;
            if (!entityManager.HasComponent<NetCompositionData>(nodeComposition))
            {
                return true;
            }

            CompositionFlags.General flags = entityManager.GetComponentData<NetCompositionData>(nodeComposition).m_Flags.m_General;
            return (flags & (CompositionFlags.General.Tunnel | CompositionFlags.General.Elevated)) == 0;
        }

        private static bool TryIndexNode(
            Entity node, Dictionary<Entity, float3> nodeMap, Dictionary<Entity, int> index,
            List<float> xs, List<float> zs, List<bool> onGround, out int slot)
        {
            if (index.TryGetValue(node, out slot))
            {
                return true;
            }

            if (!nodeMap.TryGetValue(node, out float3 position))
            {
                slot = -1;
                return false;
            }

            slot = xs.Count;
            xs.Add(position.x);
            zs.Add(position.z);
            onGround.Add(false);
            index[node] = slot;
            return true;
        }

        private static bool HasPedestrianLane(EntityManager entityManager, Entity edge)
        {
            if (!entityManager.TryGetBuffer(edge, isReadOnly: true, out DynamicBuffer<Game.Net.SubLane> lanes))
            {
                return false;
            }

            for (int i = 0; i < lanes.Length; i++)
            {
                if ((lanes[i].m_PathMethods & Game.Pathfind.PathMethod.Pedestrian) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
