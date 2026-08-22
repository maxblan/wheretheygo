using System.Collections.Generic;
using Game.Simulation;
using Unity.Collections;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // Terrain-derived masks: where a stop could physically go, and which tiles are
    // mutually reachable on foot without crossing water or a cliff.
    //
    // These run on the main thread rather than in a Burst job. The sampling itself
    // is Burst-safe, but the flood fill that consumes it is inherently sequential,
    // and terrain changes so rarely that the result is cached and reused across many
    // recomputes. Cost is paid on grid resize and the slow refresh only.
    internal static class SuitabilityMasks
    {
        // A tile counts as water when the surface is deeper than this. Shallow
        // puddles and shoreline wash should not carve up the walkable landmass.
        private const float WaterDepthThreshold = 0.5f;
        // Ferry stops want the shoreline, so they accept shallow water and require
        // proximity to it; this is how far a land tile may be from water and still
        // count as shoreline.
        private const float FerryShorelineDepth = 0.1f;

        // Fills `buildable` (1 where a stop could go) and `components` (a label per
        // tile, 0 for unbuildable) for a grid of gridSize tiles of tileSize metres
        // starting at worldMin.
        public static void Build(
            TerrainSystem terrainSystem,
            WaterSystem waterSystem,
            Setting.ModePreset mode,
            float maxSlopeDegrees,
            int2 gridSize,
            float2 worldMin,
            float tileSize,
            NativeArray<byte> buildable,
            NativeArray<int> components,
            NativeArray<byte> land,
            out int componentCount)
        {
            componentCount = 0;
            int cells = gridSize.x * gridSize.y;
            if (cells <= 0 || !buildable.IsCreated || !components.IsCreated || !land.IsCreated)
            {
                return;
            }

            TerrainHeightData heightData = terrainSystem.GetHeightData(true);
            WaterSurfaceData<SurfaceWater> waterData = waterSystem.GetSurfaceData(out Unity.Jobs.JobHandle waterDeps);
            // Sampling on the main thread, so the producing jobs must be finished.
            waterDeps.Complete();

            bool ferry = mode == Setting.ModePreset.Ferry;
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
                    buildable[index] = (isLand && gentle) ? (byte)1 : (byte)0;

                    if (ferry)
                    {
                        // Ferries need water access, so keep shallow water and defer
                        // the shoreline test to pass 2 (it needs neighbours).
                        buildable[index] = (isLand && gentle) || depth <= FerryShorelineDepth + WaterDepthThreshold
                            ? (byte)1
                            : (byte)0;
                    }
                }
            }

            if (ferry)
            {
                RestrictToShoreline(gridSize, land, buildable);
            }

            // Pass 2: label connected landmasses over 4-connectivity. Connectivity
            // follows `land`, not `buildable`: a steep hillside still connects the
            // valleys either side of it for the purpose of "same landmass", whereas
            // water genuinely separates them.
            LabelComponents(gridSize, land, components, out componentCount);
        }

        // A ferry stop is only sensible where land meets water, so drop any tile
        // that has no water neighbour.
        private static void RestrictToShoreline(int2 gridSize, NativeArray<byte> land, NativeArray<byte> buildable)
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
                        if (ny < 0 || ny >= gridSize.y) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= gridSize.x) continue;
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

        // Iterative flood fill with an explicit stack — recursion would blow the
        // stack on a 448x448 landmass.
        private static void LabelComponents(int2 gridSize, NativeArray<byte> land, NativeArray<int> components, out int componentCount)
        {
            int cells = gridSize.x * gridSize.y;
            for (int i = 0; i < cells; i++)
            {
                components[i] = 0;
            }

            int label = 0;
            var stack = new Stack<int>();

            for (int start = 0; start < cells; start++)
            {
                if (land[start] == 0 || components[start] != 0)
                {
                    continue;
                }

                label++;
                components[start] = label;
                stack.Push(start);

                while (stack.Count > 0)
                {
                    int current = stack.Pop();
                    int cx = current % gridSize.x;
                    int cy = current / gridSize.x;

                    PushNeighbour(stack, land, components, gridSize, cx - 1, cy, label);
                    PushNeighbour(stack, land, components, gridSize, cx + 1, cy, label);
                    PushNeighbour(stack, land, components, gridSize, cx, cy - 1, label);
                    PushNeighbour(stack, land, components, gridSize, cx, cy + 1, label);
                }
            }

            componentCount = label;
        }

        private static void PushNeighbour(
            Stack<int> stack,
            NativeArray<byte> land,
            NativeArray<int> components,
            int2 gridSize,
            int x,
            int y,
            int label)
        {
            if (x < 0 || x >= gridSize.x || y < 0 || y >= gridSize.y)
            {
                return;
            }

            int index = x + y * gridSize.x;
            if (land[index] == 0 || components[index] != 0)
            {
                return;
            }

            components[index] = label;
            stack.Push(index);
        }

        // Walk-distance refinement for a single candidate site: Dijkstra over the
        // walkable tiles (8-connected, diagonal cost sqrt(2)) out to `radius`
        // metres of NETWORK distance, summing the per-tile demand and jobs
        // densities it can actually reach.
        //
        // This is the honest version of the catchment: the heatmap uses Euclidean
        // distance gated by landmass, which is cheap enough for every tile, whereas
        // this is only affordable for the handful of reported sites.
        public static float RefineSite(
            int siteIndex,
            int2 gridSize,
            float tileSize,
            float radius,
            NativeArray<byte> land,
            float[] tileDemand,
            float[] tileJobs,
            float demandWeight,
            float jobsWeight,
            float[] distanceScratch,
            out float reachedDemand,
            out float reachedJobs)
        {
            reachedDemand = 0f;
            reachedJobs = 0f;

            int cells = gridSize.x * gridSize.y;
            if (siteIndex < 0 || siteIndex >= cells || distanceScratch == null || distanceScratch.Length < cells)
            {
                return 0f;
            }

            int span = (int)math.ceil(radius / tileSize) + 1;
            int sx = siteIndex % gridSize.x;
            int sy = siteIndex / gridSize.x;

            // Only the window the radius can reach is touched, so the scratch does
            // not need clearing globally between sites.
            int minX = math.max(0, sx - span);
            int maxX = math.min(gridSize.x - 1, sx + span);
            int minY = math.max(0, sy - span);
            int maxY = math.min(gridSize.y - 1, sy + span);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    distanceScratch[x + y * gridSize.x] = float.MaxValue;
                }
            }

            // Simple binary-heap-free Dijkstra: the frontier stays small because the
            // radius is bounded, so a linear scan of a pending list is fine.
            var frontier = new List<int>(64);
            distanceScratch[siteIndex] = 0f;
            frontier.Add(siteIndex);

            float diagonal = math.sqrt(2f) * tileSize;

            while (frontier.Count > 0)
            {
                // Pop the nearest pending tile.
                int bestSlot = 0;
                float bestDistance = distanceScratch[frontier[0]];
                for (int i = 1; i < frontier.Count; i++)
                {
                    float candidate = distanceScratch[frontier[i]];
                    if (candidate < bestDistance)
                    {
                        bestDistance = candidate;
                        bestSlot = i;
                    }
                }

                int current = frontier[bestSlot];
                frontier.RemoveAt(bestSlot);

                if (bestDistance > radius)
                {
                    continue;
                }

                float weight = 1f - bestDistance / radius;
                reachedDemand += tileDemand[current] * weight;
                reachedJobs += tileJobs[current] * weight;

                int cx = current % gridSize.x;
                int cy = current / gridSize.x;

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny < minY || ny > maxY) continue;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx;
                        if (nx < minX || nx > maxX) continue;

                        int neighbour = nx + ny * gridSize.x;
                        if (land[neighbour] == 0)
                        {
                            continue;
                        }

                        float step = (dx != 0 && dy != 0) ? diagonal : tileSize;
                        float candidate = bestDistance + step;
                        if (candidate > radius || candidate >= distanceScratch[neighbour])
                        {
                            continue;
                        }

                        distanceScratch[neighbour] = candidate;
                        frontier.Add(neighbour);
                    }
                }
            }

            return (reachedDemand * demandWeight) + (reachedJobs * jobsWeight);
        }
    }
}
