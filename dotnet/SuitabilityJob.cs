using Game.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // Emits the five raw score terms per cell. The managed combine pass normalizes
    // and weights them, so changing a weight never re-runs this job.
    //
    // Catchment sums are gated on the barrier component label: a cell only
    // accumulates demand from sources standing in the same walkable landmass, which
    // is what stops a hotspot from drawing population across a river it has no
    // crossing for. Labels are precomputed on the main thread (see
    // SuitabilityMasks) because flood filling is inherently sequential.
    [BurstCompile]
    internal struct SuitabilityJob : Unity.Jobs.IJobParallelFor
    {
        // Per-feature access contributions, tuned so a normal street grid saturates
        // the access term at the reference radius.
        private const float EdgeAccessCoefficient = 0.06f;
        private const float NodeAccessCoefficient = 0.15f;
        // Radius the access coefficients were tuned at. Scaling by
        // (reference/actual)^2 keeps the saturation density constant, so the
        // configurable radius changes which roads count, not how quickly the term
        // maxes out.
        private const float ReferenceAccessRadius = 120f;
        public const float MaxPenalty = 1.5f;

        public int2 GridSize;
        public int2 BucketGridSize;
        public float2 WorldMin;
        public float TileSize;
        public float BucketSize;
        public float CatchmentRadius;
        public float AccessRadius;

        [ReadOnly] public NativeArray<PopulationCell> PopulationMap;
        public float2 PopulationCellSize;
        public int2 PopulationTextureSize;

        [ReadOnly] public NativeArray<float2> StopPositions;
        [ReadOnly] public NativeArray<float> StopWeights;
        [ReadOnly] public NativeArray<int> StopOffsets;
        [ReadOnly] public NativeArray<int> StopCounts;

        [ReadOnly] public NativeArray<float2> NodePositions;
        [ReadOnly] public NativeArray<float> NodeWeights;
        [ReadOnly] public NativeArray<int> NodeOffsets;
        [ReadOnly] public NativeArray<int> NodeCounts;

        [ReadOnly] public NativeArray<float2> EdgePositions;
        [ReadOnly] public NativeArray<float> EdgeWeights;
        [ReadOnly] public NativeArray<int> EdgeOffsets;
        [ReadOnly] public NativeArray<int> EdgeCounts;

        [ReadOnly] public NativeArray<float2> JobPositions;
        [ReadOnly] public NativeArray<float> JobWeights;
        [ReadOnly] public NativeArray<int> JobOffsets;
        [ReadOnly] public NativeArray<int> JobCounts;

        [ReadOnly] public NativeArray<float2> FutureHomePositions;
        [ReadOnly] public NativeArray<float> FutureHomeWeights;
        [ReadOnly] public NativeArray<int> FutureHomeOffsets;
        [ReadOnly] public NativeArray<int> FutureHomeCounts;

        [ReadOnly] public NativeArray<float2> FutureJobPositions;
        [ReadOnly] public NativeArray<float> FutureJobWeights;
        [ReadOnly] public NativeArray<int> FutureJobOffsets;
        [ReadOnly] public NativeArray<int> FutureJobCounts;

        // Barrier component label per tile; 0 means unbuildable/no component.
        [ReadOnly] public NativeArray<int> Components;
        // Non-zero where a stop could actually be placed.
        [ReadOnly] public NativeArray<byte> Buildable;

        public NativeArray<SuitabilityCell> Terms;

        public void Execute(int index)
        {
            if (Buildable[index] == 0)
            {
                Terms[index] = default;
                return;
            }

            int x = index % GridSize.x;
            int y = index / GridSize.x;
            float2 center = WorldMin + new float2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);
            int component = Components[index];

            var cell = new SuitabilityCell
            {
                m_Demand = SumPopulation(center, CatchmentRadius, component),
                m_Jobs = SumPoints(JobPositions, JobWeights, JobOffsets, JobCounts, center, CatchmentRadius, component),
                m_Coverage = SumCoverage(center),
                m_Access = ComputeAccessibility(center),
                m_Future = SumPoints(FutureHomePositions, FutureHomeWeights, FutureHomeOffsets, FutureHomeCounts, center, CatchmentRadius, component)
                    + SumPoints(FutureJobPositions, FutureJobWeights, FutureJobOffsets, FutureJobCounts, center, CatchmentRadius, component),
            };

            Terms[index] = cell;
        }

        // Triangular kernel: contributions fade linearly with distance, so the
        // coarse source cells cannot imprint rectangular plateaus. Callers must
        // ensure dist <= radius.
        private static float TriangularWeight(float dist, float radius)
        {
            return 1f - dist / radius;
        }

        private int ComponentAt(float2 position)
        {
            int2 tile = SuitabilityInputs.WorldToCell(position, WorldMin, TileSize, GridSize);
            return Components[tile.x + tile.y * GridSize.x];
        }

        private float SumPopulation(float2 center, float radius, int component)
        {
            float2 mapSize = PopulationCellSize * new float2(PopulationTextureSize.x, PopulationTextureSize.y);
            float2 mapMin = -mapSize * 0.5f;
            int2 minCell = ClampToTexture(center - radius, mapMin);
            int2 maxCell = ClampToTexture(center + radius, mapMin);

            float sum = 0f;
            for (int cy = minCell.y; cy <= maxCell.y; cy++)
            {
                for (int cx = minCell.x; cx <= maxCell.x; cx++)
                {
                    float2 cellCenter = mapMin + new float2(
                        (cx + 0.5f) * PopulationCellSize.x,
                        (cy + 0.5f) * PopulationCellSize.y);
                    float dist = math.distance(cellCenter, center);
                    if (dist > radius)
                    {
                        continue;
                    }

                    // Population is sampled on a much coarser grid than the score,
                    // so gate on the component of the cell's own centre.
                    if (ComponentAt(cellCenter) != component)
                    {
                        continue;
                    }

                    int idx = cx + cy * PopulationTextureSize.x;
                    sum += PopulationMap[idx].m_Population * TriangularWeight(dist, radius);
                }
            }

            return sum;
        }

        private int2 ClampToTexture(float2 position, float2 mapMin)
        {
            float2 rel = (position - mapMin) / PopulationCellSize;
            int2 cell = new int2((int)math.floor(rel.x), (int)math.floor(rel.y));
            cell.x = math.clamp(cell.x, 0, PopulationTextureSize.x - 1);
            cell.y = math.clamp(cell.y, 0, PopulationTextureSize.y - 1);
            return cell;
        }

        private float SumPoints(
            NativeArray<float2> positions,
            NativeArray<float> weights,
            NativeArray<int> offsets,
            NativeArray<int> counts,
            float2 center,
            float radius,
            int component)
        {
            int radiusTiles = (int)math.ceil(radius / BucketSize);
            int2 baseCell = SuitabilityInputs.WorldToCell(center, WorldMin, BucketSize, BucketGridSize);
            float sum = 0f;

            for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
            {
                int cy = baseCell.y + dy;
                if (cy < 0 || cy >= BucketGridSize.y)
                {
                    continue;
                }

                for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                {
                    int cx = baseCell.x + dx;
                    if (cx < 0 || cx >= BucketGridSize.x)
                    {
                        continue;
                    }

                    int bucket = cx + cy * BucketGridSize.x;
                    int count = counts[bucket];
                    int start = offsets[bucket];
                    for (int i = 0; i < count; i++)
                    {
                        float2 pos = positions[start + i];
                        float dist = math.distance(pos, center);
                        if (dist > radius)
                        {
                            continue;
                        }

                        if (ComponentAt(pos) != component)
                        {
                            continue;
                        }

                        sum += weights[start + i] * TriangularWeight(dist, radius);
                    }
                }
            }

            return sum;
        }

        // Existing coverage is deliberately NOT component-gated: a stop across a
        // river still competes for the same riders on lines that cross it, and the
        // penalty is a redundancy preference rather than a walk-access estimate.
        private float SumCoverage(float2 center)
        {
            int radiusTiles = (int)math.ceil(CatchmentRadius / BucketSize);
            int2 baseCell = SuitabilityInputs.WorldToCell(center, WorldMin, BucketSize, BucketGridSize);
            float penalty = 0f;

            for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
            {
                int cy = baseCell.y + dy;
                if (cy < 0 || cy >= BucketGridSize.y)
                {
                    continue;
                }

                for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                {
                    int cx = baseCell.x + dx;
                    if (cx < 0 || cx >= BucketGridSize.x)
                    {
                        continue;
                    }

                    int bucket = cx + cy * BucketGridSize.x;
                    int count = StopCounts[bucket];
                    int start = StopOffsets[bucket];
                    for (int i = 0; i < count; i++)
                    {
                        float dist = math.distance(StopPositions[start + i], center);
                        if (dist > CatchmentRadius)
                        {
                            continue;
                        }

                        penalty += StopWeights[start + i] * TriangularWeight(dist, CatchmentRadius);
                    }
                }
            }

            return math.clamp(penalty, 0f, MaxPenalty);
        }

        private float ComputeAccessibility(float2 center)
        {
            int radiusTiles = (int)math.ceil(AccessRadius / BucketSize);
            int2 baseCell = SuitabilityInputs.WorldToCell(center, WorldMin, BucketSize, BucketGridSize);
            float nodeWeight = 0f;
            float edgeWeight = 0f;

            for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
            {
                int cy = baseCell.y + dy;
                if (cy < 0 || cy >= BucketGridSize.y)
                {
                    continue;
                }

                for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                {
                    int cx = baseCell.x + dx;
                    if (cx < 0 || cx >= BucketGridSize.x)
                    {
                        continue;
                    }

                    int bucket = cx + cy * BucketGridSize.x;

                    int nodeCount = NodeCounts[bucket];
                    int nodeStart = NodeOffsets[bucket];
                    for (int i = 0; i < nodeCount; i++)
                    {
                        float dist = math.distance(NodePositions[nodeStart + i], center);
                        if (dist <= AccessRadius)
                        {
                            nodeWeight += TriangularWeight(dist, AccessRadius);
                        }
                    }

                    int edgeCount = EdgeCounts[bucket];
                    int edgeStart = EdgeOffsets[bucket];
                    for (int i = 0; i < edgeCount; i++)
                    {
                        float dist = math.distance(EdgePositions[edgeStart + i], center);
                        if (dist <= AccessRadius)
                        {
                            edgeWeight += TriangularWeight(dist, AccessRadius);
                        }
                    }
                }
            }

            float radiusScale = (ReferenceAccessRadius * ReferenceAccessRadius) / (AccessRadius * AccessRadius);
            float access = ((edgeWeight * EdgeAccessCoefficient) + (nodeWeight * NodeAccessCoefficient)) * radiusScale;
            return math.saturate(access);
        }
    }
}
