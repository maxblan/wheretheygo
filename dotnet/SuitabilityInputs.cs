using System.Collections.Generic;
using Colossal.Entities;
using Game.Companies;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // A weighted point set indexed by a coarse bucket grid, so the job can gather
    // nearby points without scanning everything. Weights are always allocated
    // (uniform 1 for unweighted sets) — a conditionally-created NativeArray inside
    // a Burst job is not safe to branch around.
    internal struct PointBuckets
    {
        public NativeArray<float2> m_Positions;
        public NativeArray<float> m_Weights;
        public NativeArray<int> m_Offsets;
        public NativeArray<int> m_Counts;

        public int Count => m_Positions.IsCreated ? m_Positions.Length : 0;

        public void Dispose(Unity.Jobs.JobHandle handle)
        {
            if (m_Positions.IsCreated) m_Positions.Dispose(handle);
            if (m_Weights.IsCreated) m_Weights.Dispose(handle);
            if (m_Offsets.IsCreated) m_Offsets.Dispose(handle);
            if (m_Counts.IsCreated) m_Counts.Dispose(handle);
        }
    }

    // Per-cell raw scoring terms. Kept as one struct so the job writes a single
    // array and the managed combine pass reads it back without re-deriving units.
    internal struct SuitabilityCell
    {
        public float m_Demand;
        public float m_Jobs;
        public float m_Coverage;
        public float m_Access;
        public float m_Future;
        // Served stops of OTHER modes close enough to transfer to, weighted by how
        // much trunk capacity they represent.
        public float m_Interchange;
        // Served stops of other modes near enough to already absorb this tile's
        // demand, but too far to transfer to.
        public float m_CrossCoverage;
    }

    internal static class SuitabilityInputs
    {
        public static int2 GridDims(float2 size, float cellSize)
        {
            return new int2(
                math.max(1, (int)math.ceil(size.x / cellSize)),
                math.max(1, (int)math.ceil(size.y / cellSize)));
        }

        public static int2 WorldToCell(float2 pos, float2 worldMin, float cellSize, int2 gridSize)
        {
            float2 rel = (pos - worldMin) / cellSize;
            int2 cell = new int2((int)math.floor(rel.x), (int)math.floor(rel.y));
            cell.x = math.clamp(cell.x, 0, gridSize.x - 1);
            cell.y = math.clamp(cell.y, 0, gridSize.y - 1);
            return cell;
        }

        public static PointBuckets BuildBuckets(
            List<float2> positions,
            List<float> weights,
            int2 gridSize,
            float2 worldMin,
            float cellSize)
        {
            int bucketCount = gridSize.x * gridSize.y;
            var result = new PointBuckets
            {
                m_Counts = new NativeArray<int>(bucketCount, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                m_Offsets = new NativeArray<int>(bucketCount, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                m_Positions = new NativeArray<float2>(positions.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                m_Weights = new NativeArray<float>(positions.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory),
            };

            for (int i = 0; i < positions.Count; i++)
            {
                int2 cell = WorldToCell(positions[i], worldMin, cellSize, gridSize);
                result.m_Counts[cell.x + cell.y * gridSize.x] += 1;
            }

            int running = 0;
            for (int i = 0; i < bucketCount; i++)
            {
                result.m_Offsets[i] = running;
                running += result.m_Counts[i];
            }

            var write = new NativeArray<int>(bucketCount, Allocator.Temp);
            NativeArray<int>.Copy(result.m_Offsets, write);
            for (int i = 0; i < positions.Count; i++)
            {
                int2 cell = WorldToCell(positions[i], worldMin, cellSize, gridSize);
                int index = cell.x + cell.y * gridSize.x;
                int writeIndex = write[index]++;
                result.m_Positions[writeIndex] = positions[i];
                result.m_Weights[writeIndex] = weights != null ? weights[i] : 1f;
            }

            write.Dispose();
            return result;
        }

        // Every passenger stop, split into the selected mode (which drives the
        // coverage penalty) and all other modes (which drive the interchange bonus
        // and the cross-mode redundancy penalty).
        //
        // Unserved stops are excluded from both: a stop no line calls at provides
        // neither service to duplicate nor a transfer to make. That also means a
        // co-located pair of served stops of different modes always represents a
        // genuine transfer opportunity, since different modes necessarily run
        // different lines — so no line-identity comparison is needed.
        public static void CollectStops(
            EntityManager entityManager,
            PrefabSystem prefabSystem,
            EntityQuery stopQuery,
            Setting.ModePreset mode,
            List<float2> samePositions,
            List<float2> otherPositions,
            List<float> otherWeights,
            out int orphansSkipped)
        {
            samePositions.Clear();
            otherPositions.Clear();
            otherWeights.Clear();
            orphansSkipped = 0;

            TransportType selected = TransportTypeOf(mode);

            using var entities = stopQuery.ToEntityArray(Allocator.Temp);
            using var transforms = stopQuery.ToComponentDataArray<Transform>(Allocator.Temp);
            using var prefabs = stopQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (!TryGetStopType(prefabSystem, prefabs[i].m_Prefab, out TransportType type))
                {
                    continue;
                }

                bool sameMode = type == selected;
                float otherWeight = sameMode ? 0f : ModeWeight(type);
                if (!sameMode && otherWeight <= 0f)
                {
                    // Not a mode anyone would transfer between (taxi, cargo, ...).
                    continue;
                }

                if (!IsServedByLine(entityManager, entities[i]))
                {
                    if (sameMode)
                    {
                        orphansSkipped++;
                    }

                    continue;
                }

                float3 pos = transforms[i].m_Position;
                var flat = new float2(pos.x, pos.z);
                if (sameMode)
                {
                    samePositions.Add(flat);
                }
                else
                {
                    otherPositions.Add(flat);
                    otherWeights.Add(otherWeight);
                }
            }
        }

        // Roughly how much trunk capacity each mode represents. Used to scale the
        // interchange bonus, so feeding a metro station counts for more than
        // standing next to another bus stop. Zero means "never a transfer partner".
        public static float ModeWeight(TransportType type)
        {
            switch (type)
            {
                case TransportType.Bus: return 1f;
                case TransportType.Helicopter: return 1f;
                case TransportType.Ferry: return 1.2f;
                case TransportType.Tram: return 1.5f;
                case TransportType.Ship: return 1.5f;
                case TransportType.Subway: return 2.5f;
                case TransportType.Train: return 3f;
                case TransportType.Airplane: return 3f;
                default: return 0f;
            }
        }

        private static bool TryGetStopType(PrefabSystem prefabSystem, Entity prefab, out TransportType type)
        {
            type = TransportType.None;
            PrefabBase prefabBase = prefabSystem.GetPrefab<PrefabBase>(prefab);
            if (prefabBase == null || !prefabSystem.TryGetComponentData(prefabBase, out TransportStopData stopData))
            {
                return false;
            }

            if (!stopData.m_PassengerTransport)
            {
                return false;
            }

            type = stopData.m_TransportType;
            return true;
        }

        public static bool IsServedByLine(EntityManager entityManager, Entity stop)
        {
            return entityManager.TryGetBuffer(stop, true, out DynamicBuffer<ConnectedRoute> routes)
                && routes.Length > 0;
        }

        public static bool IsStopOfMode(PrefabSystem prefabSystem, Entity prefab, Setting.ModePreset mode)
        {
            return TryGetStopType(prefabSystem, prefab, out TransportType type) && type == TransportTypeOf(mode);
        }

        public static TransportType TransportTypeOf(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return TransportType.Tram;
                case Setting.ModePreset.Metro: return TransportType.Subway;
                case Setting.ModePreset.Train: return TransportType.Train;
                case Setting.ModePreset.Ferry: return TransportType.Ferry;
                default: return TransportType.Bus;
            }
        }

        // Accessibility only considers the road network: pipes, power lines and rail
        // would otherwise inflate the score where pedestrians cannot reach. The node
        // map spans every net node because road edges reference endpoints by entity;
        // only road endpoints end up in nodePositions.
        public static void CollectRoadNetwork(
            EntityQuery nodeQuery,
            EntityQuery roadEdgeQuery,
            List<float2> nodePositions,
            List<float2> edgePositions)
        {
            nodePositions.Clear();
            edgePositions.Clear();

            using var nodeEntities = nodeQuery.ToEntityArray(Allocator.Temp);
            using var nodeData = nodeQuery.ToComponentDataArray<Game.Net.Node>(Allocator.Temp);
            var nodeMap = new Dictionary<Entity, float3>(nodeEntities.Length);
            for (int i = 0; i < nodeEntities.Length; i++)
            {
                nodeMap[nodeEntities[i]] = nodeData[i].m_Position;
            }

            using var edges = roadEdgeQuery.ToComponentDataArray<Game.Net.Edge>(Allocator.Temp);
            var roadNodes = new HashSet<Entity>();
            for (int i = 0; i < edges.Length; i++)
            {
                Game.Net.Edge edge = edges[i];
                if (!nodeMap.TryGetValue(edge.m_Start, out float3 start) || !nodeMap.TryGetValue(edge.m_End, out float3 end))
                {
                    continue;
                }

                float3 mid = (start + end) * 0.5f;
                edgePositions.Add(new float2(mid.x, mid.z));
                roadNodes.Add(edge.m_Start);
                roadNodes.Add(edge.m_End);
            }

            foreach (Entity node in roadNodes)
            {
                float3 pos = nodeMap[node];
                nodePositions.Add(new float2(pos.x, pos.z));
            }
        }

        // Actual workplace capacity, positioned at the building. Companies rent a
        // building (PropertyRenter); city service buildings carry WorkProvider and a
        // Transform themselves. The vanilla availability cell map is NOT usable
        // here: its "workplaces" channel is a road-network reachability ratio rather
        // than a job count, which is why office and industry areas never registered.
        public static void CollectWorkplaces(
            EntityQuery workplaceQuery,
            ComponentLookup<Transform> transformLookup,
            ComponentLookup<Game.Buildings.PropertyRenter> renterLookup,
            List<float2> positions,
            List<float> workers)
        {
            positions.Clear();
            workers.Clear();

            using var entities = workplaceQuery.ToEntityArray(Allocator.Temp);
            using var providers = workplaceQuery.ToComponentDataArray<WorkProvider>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                int maxWorkers = providers[i].m_MaxWorkers;
                if (maxWorkers <= 0)
                {
                    continue;
                }

                Entity entity = entities[i];
                float3 pos;
                if (transformLookup.HasComponent(entity))
                {
                    pos = transformLookup[entity].m_Position;
                }
                else if (renterLookup.HasComponent(entity))
                {
                    Entity property = renterLookup[entity].m_Property;
                    if (property == Entity.Null || !transformLookup.HasComponent(property))
                    {
                        continue;
                    }

                    pos = transformLookup[property].m_Position;
                }
                else
                {
                    continue;
                }

                positions.Add(new float2(pos.x, pos.z));
                workers.Add(maxWorkers);
            }
        }

        // Zoned-but-unbuilt cells, so a stop can be placed ahead of a district
        // filling in. Residential capacity feeds future demand; commercial,
        // industrial and office capacity feeds future jobs.
        //
        // The vacancy test mirrors Game.Zones.LotSizeJobs, and cell coordinates come
        // from the linear buffer index via the block's row width, then through
        // ZoneUtils.GetCellPosition for a world position.
        public static void CollectZonedCells(
            EntityManager entityManager,
            EntityQuery blockQuery,
            Game.Prefabs.ZoneSystem zoneSystem,
            ComponentLookup<ZoneData> zoneDataLookup,
            List<float2> residentialPositions,
            List<float> residentialWeights,
            List<float2> jobPositions,
            List<float> jobWeights)
        {
            residentialPositions.Clear();
            residentialWeights.Clear();
            jobPositions.Clear();
            jobWeights.Clear();

            if (zoneSystem == null)
            {
                return;
            }

            using var entities = blockQuery.ToEntityArray(Allocator.Temp);
            using var blocks = blockQuery.ToComponentDataArray<Block>(Allocator.Temp);
            var zoneKinds = new Dictionary<ZoneType, AreaKind>();

            for (int b = 0; b < entities.Length; b++)
            {
                Block block = blocks[b];
                if (block.m_Size.x <= 0)
                {
                    continue;
                }

                if (!entityManager.TryGetBuffer(entities[b], true, out DynamicBuffer<Cell> cells))
                {
                    continue;
                }

                for (int i = 0; i < cells.Length; i++)
                {
                    Cell cell = cells[i];
                    if ((cell.m_State & (CellFlags.Blocked | CellFlags.Occupied | CellFlags.Shared | CellFlags.Redundant)) != 0)
                    {
                        continue;
                    }

                    if (cell.m_Zone.Equals(ZoneType.None))
                    {
                        continue;
                    }

                    if (!zoneKinds.TryGetValue(cell.m_Zone, out AreaKind kind))
                    {
                        kind = ClassifyZone(zoneSystem, zoneDataLookup, cell.m_Zone);
                        zoneKinds[cell.m_Zone] = kind;
                    }

                    if (kind == AreaKind.Other)
                    {
                        continue;
                    }

                    int2 cellIndex = new int2(i % block.m_Size.x, i / block.m_Size.x);
                    float3 pos = ZoneUtils.GetCellPosition(block, cellIndex);
                    var flat = new float2(pos.x, pos.z);

                    if (kind == AreaKind.Residential)
                    {
                        residentialPositions.Add(flat);
                        residentialWeights.Add(ZoneUtils.CELL_AREA);
                    }
                    else
                    {
                        jobPositions.Add(flat);
                        jobWeights.Add(ZoneUtils.CELL_AREA);
                    }
                }
            }
        }

        private enum AreaKind
        {
            Other,
            Residential,
            Working,
        }

        private static AreaKind ClassifyZone(
            Game.Prefabs.ZoneSystem zoneSystem,
            ComponentLookup<ZoneData> zoneDataLookup,
            ZoneType zoneType)
        {
            Entity prefab = zoneSystem.GetPrefab(zoneType);
            if (prefab == Entity.Null || !zoneDataLookup.HasComponent(prefab))
            {
                return AreaKind.Other;
            }

            ZoneData data = zoneDataLookup[prefab];
            switch (data.m_AreaType)
            {
                case AreaType.Residential:
                    return AreaKind.Residential;
                case AreaType.Commercial:
                case AreaType.Industrial:
                    return AreaKind.Working;
                default:
                    return AreaKind.Other;
            }
        }
    }
}
