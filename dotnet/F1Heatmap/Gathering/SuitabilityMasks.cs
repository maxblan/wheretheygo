using Game.Simulation;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // Terrain-derived masks: where a stop could physically go, and which tiles are
    // land rather than water.
    //
    // These run on the main thread rather than in a Burst job: terrain changes so
    // rarely that the result is cached and reused across many recomputes, so the cost
    // is paid on grid resize and the slow refresh only. (The v1 landmass labelling
    // that used to follow the sampling was never read once the walk-distance site
    // refinement went; it is gone with it.)
    internal static class SuitabilityMasks
    {
        // A tile counts as water when the surface is deeper than this. Shallow
        // puddles and shoreline wash should not carve up the walkable landmass.
        private const float WaterDepthThreshold = 0.5f;
        // Ferry stops want the shoreline, so they accept shallow water and require
        // proximity to it; this is how far a land tile may be from water and still
        // count as shoreline.
        private const float FerryShorelineDepth = 0.1f;

        // Fills `buildable` (1 where a stop could go) and `land` (1 where the tile is
        // not water) for a grid of gridSize tiles of tileSize metres starting at worldMin.
        public static void Build(
            TerrainSystem terrainSystem,
            WaterSystem waterSystem,
            ModePreset mode,
            float maxSlopeDegrees,
            int2 gridSize,
            float2 worldMin,
            float tileSize,
            byte[] buildable,
            byte[] land)
        {
            int cells = gridSize.x * gridSize.y;
            if (cells <= 0 || buildable is null || buildable.Length < cells || land is null || land.Length < cells)
            {
                return;
            }

            TerrainHeightData heightData = terrainSystem.GetHeightData(waitForPending: true);
            WaterSurfaceData<SurfaceWater> waterData = waterSystem.GetSurfaceData(out Unity.Jobs.JobHandle waterDeps);
            // Sampling on the main thread, so the producing jobs must be finished.
            waterDeps.Complete();

            bool ferry = mode == ModePreset.Ferry;
            float minNormalY = math.cos(math.radians(math.clamp(maxSlopeDegrees, 1f, 89f)));

            // Pass 1: classify each tile as land (for connectivity) and buildable
            // (for candidate placement). Those differ for ferries, which want to sit
            // on the shoreline while still belonging to the land they serve.
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int x = 0; x < gridSize.x; x++)
                {
                    int index = x + y * gridSize.x;
                    float2 flat = worldMin + new float2((x + 0.5f) * tileSize, (y + 0.5f) * tileSize);
                    var probe = new float3(flat.x, 0f, flat.y);

                    float height = TerrainUtils.SampleHeight(ref heightData, probe, out float3 normal);
                    probe.y = height;
                    float depth = waterData.isCreated ? WaterUtils.SampleDepth(ref waterData, probe) : 0f;

                    bool isLand = depth <= WaterDepthThreshold;
                    bool gentle = normal.y >= minNormalY;

                    land[index] = isLand ? (byte)1 : (byte)0;

                    // Ferries need water access, so they also keep shallow water; the
                    // shoreline test itself needs neighbours and waits for pass 2.
                    bool placeable = ferry
                        ? (isLand && gentle) || depth <= FerryShorelineDepth + WaterDepthThreshold
                        : isLand && gentle;
                    buildable[index] = placeable ? (byte)1 : (byte)0;
                }
            }

            if (ferry)
            {
                RestrictToShoreline(gridSize, land, buildable);
            }
        }

        // A ferry stop is only sensible where land meets water, so drop any tile
        // that has no water neighbour.
        private static void RestrictToShoreline(int2 gridSize, byte[] land, byte[] buildable)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int x = 0; x < gridSize.x; x++)
                {
                    int index = x + y * gridSize.x;
                    if (buildable[index] == 0)
                    {
                        continue;
                    }

                    bool nearWater = false;
                    for (int dy = -1; dy <= 1 && !nearWater; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= gridSize.y)
                        {
                            continue;
                        }
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= gridSize.x)
                            {
                                continue;
                            }
                            if (land[nx + ny * gridSize.x] == 0)
                            {
                                nearWater = true;
                                break;
                            }
                        }
                    }

                    if (!nearWater)
                    {
                        buildable[index] = 0;
                    }
                }
            }
        }
    }
}
