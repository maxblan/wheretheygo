using System.Globalization;
using Colossal.Collections;
using Unity.Entities;
using Game.Simulation;
using Unity.Jobs;
using Unity.Mathematics;

namespace WhereTheyGo
{
    // The pedestrian network and the tile grid everything else is measured on: the
    // walk graph read out of the streets, and every tile's nearest pavement with the
    // walk to it. The snap runs on a worker task because it is one nearest-node query
    // per 32 m tile — some 200 000 of them on a full map.
    public sealed partial class WhereTheyGoSystem
    {
        private EntityQuery m_AllEdgeQuery;

        private WalkGraph? m_WalkGraph;

        private int m_EdgesWithoutPavement;

        // How many gaps the pedestrian network had to be stitched across (WalkBridging).
        private int m_WalkBridges;

        private bool m_RoadCacheDirty = true;

        // Where each tile attaches to the network. Null until the first pass lands.
        private TileSnap? m_TileSnap;

        private int2 m_IntensityGrid;

        private float2 m_ScoreWorldMin;

        private bool m_JobPending;

        private System.Threading.Tasks.Task? m_PendingSnap;

        private SnapBox? m_PendingBox;

        private int2 m_PendingGrid;

        private float2 m_PendingWorldMin;

        private int2 m_PlayableGridAtCompute;


        // The worker writes here and the main thread reads it once the task is done;
        // a field rather than the task's result so nothing has to be awaited.
        private sealed class SnapBox
        {
            public TileSnap? m_Snap;
        }

        private bool StartCompute()
        {
            if (m_JobPending || m_PopulationSystem is null || m_TerrainSystem is null)
            {
                return false;
            }

            Dependency.Complete();

            // The population map is what fixes the map's extent and grid, so the tile
            // grid stays aligned with everything else that samples the world.
            CellMapData<PopulationCell> popData = m_PopulationSystem.GetData(readOnly: true, out JobHandle popDeps);
            if (popData.m_CellSize.x <= 0f || popData.m_CellSize.y <= 0f ||
                popData.m_TextureSize.x <= 0 || popData.m_TextureSize.y <= 0)
            {
                return false;
            }

            float2 mapSize = popData.m_CellSize * new float2(popData.m_TextureSize.x, popData.m_TextureSize.y);
            if (mapSize.x <= 0f || mapSize.y <= 0f)
            {
                return false;
            }

            popDeps.Complete();
            float2 worldMin = -mapSize * 0.5f;
            int2 gridSize = WalkNetwork.GridDims(mapSize, Assumptions.TileSize);

            EnsureWalkNetwork();
            WalkGraph? graph = m_WalkGraph;
            if (graph is null)
            {
                return false;
            }

            int width = gridSize.x;
            int height = gridSize.y;
            float minX = worldMin.x;
            float minZ = worldMin.y;

            // Everything the task touches is captured here; nothing on the worker
            // reads ECS state.
            var box = new SnapBox();
            m_PendingBox = box;
            m_PendingSnap = System.Threading.Tasks.Task.Run(
                () => box.m_Snap = WalkAccess.SnapTiles(
                    graph, width, height, minX, minZ, Assumptions.TileSize, Assumptions.AccessWalkMs),
                System.Threading.CancellationToken.None);

            m_PendingGrid = gridSize;
            m_PendingWorldMin = worldMin;
            m_JobPending = true;
            m_PlayableGridAtCompute = GetGridSize();
            return true;
        }

        // Rebuilds the walk graph only when change detection says the streets moved:
        // it walks every edge in the world on the main thread.
        private void EnsureWalkNetwork()
        {
            if (!m_RoadCacheDirty && m_WalkGraph is not null)
            {
                return;
            }

            WalkNetwork.CollectWalkNetwork(
                EntityManager, m_NodeQuery, m_AllEdgeQuery,
                out float[] nodeX, out float[] nodeZ, out int[] edgeA, out int[] edgeB, out float[] edgeMetres,
                out bool[] siteable, out int edgesWithoutPavement, out int bridges);
            m_EdgesWithoutPavement = edgesWithoutPavement;
            m_WalkBridges = bridges;
            m_WalkGraph = WalkGraph.Build(nodeX, nodeZ, edgeA, edgeB, edgeMetres, edgeA.Length, siteable);
            int offGround = 0;
            for (int n = 0; n < siteable.Length; n++)
            {
                offGround += siteable[n] ? 0 : 1;
            }

            Mod.Log.Info(
                $"Pedestrian network: {(nodeX.Length).ToString(CultureInfo.InvariantCulture)} nodes, " +
                $"{(edgeA.Length).ToString(CultureInfo.InvariantCulture)} edges with a pavement, " +
                $"{(offGround).ToString(CultureInfo.InvariantCulture)} nodes in tunnels or on bridges (walkable, not sites)");
            m_RoadCacheDirty = false;
        }

        private void FinishComputeIfReady()
        {
            System.Threading.Tasks.Task? pending = m_PendingSnap;
            TileSnap? snap = m_PendingBox?.m_Snap;
            if (!m_JobPending || pending is null || !pending.IsCompleted)
            {
                return;
            }

            m_JobPending = false;
            m_PendingSnap = null;
            m_PendingBox = null;
            if (pending.IsFaulted || pending.IsCanceled || snap is null)
            {
                // A fault here is a defect in the pure core, not a game condition, so
                // it is logged in full rather than swallowed; the previous snap stays.
                DeferredLog.Error($"Tile snap failed: {pending.Exception}");
                return;
            }

            m_TileSnap = snap;
            m_IntensityGrid = m_PendingGrid;
            m_ScoreWorldMin = m_PendingWorldMin;
            DeferredLog.Info(
                $"Tile snap: grid {(m_PendingGrid.x).ToString(CultureInfo.InvariantCulture)}x{(m_PendingGrid.y).ToString(CultureInfo.InvariantCulture)}, " +
                $"walk network {(m_WalkGraph?.NodeCount ?? 0).ToString(CultureInfo.InvariantCulture)} nodes " +
                $"({(m_EdgesWithoutPavement).ToString(CultureInfo.InvariantCulture)} edges without a pedestrian lane skipped, " +
                $"{(m_WalkBridges).ToString(CultureInfo.InvariantCulture)} gaps bridged at up to {(Assumptions.WalkBridgeMetres).ToString("F0", CultureInfo.InvariantCulture)} m), " +
                $"{(snap.TilesOnNetwork).ToString(CultureInfo.InvariantCulture)} tiles within {(Assumptions.AccessWalkMs / 1000).ToString(CultureInfo.InvariantCulture)} s of a node");
        }

        private void DiscardPendingCompute()
        {
            if (!m_JobPending)
            {
                return;
            }

            // The task holds only its own copies, so it can be left to finish into a
            // box nobody reads.
            m_JobPending = false;
            m_PendingSnap = null;
            m_PendingBox = null;
        }

    }
}
