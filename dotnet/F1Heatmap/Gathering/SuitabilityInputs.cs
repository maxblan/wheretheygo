using System.Collections.Generic;
using Colossal.Entities;
using Game.Companies;
using Game.Prefabs;
using Game.Routes;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    internal static class SuitabilityInputs
    {
        // float2/int2 faces of TileGrid for the ECS side; the arithmetic lives there.
        public static int2 GridDims(float2 size, float cellSize)
        {
            int2Like dims = TileGrid.GridDims(new float2Like(size.x, size.y), cellSize);
            return new int2(dims.x, dims.y);
        }

        public static int2 WorldToCell(float2 pos, float2 worldMin, float cellSize, int2 gridSize)
        {
            int2Like cell = TileGrid.WorldToCell(
                new float2Like(pos.x, pos.y), new float2Like(worldMin.x, worldMin.y), cellSize, new int2Like(gridSize.x, gridSize.y));
            return new int2(cell.x, cell.y);
        }

        public static float2 CellCentre(int2 cell, float2 worldMin, float cellSize)
        {
            float2Like centre = TileGrid.CellCentre(new int2Like(cell.x, cell.y), new float2Like(worldMin.x, worldMin.y), cellSize);
            return new float2(centre.x, centre.y);
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
            ModePreset mode,
            System.Func<TransportType, float> weightOf,
            List<float2> allPositions,
            List<int> allTypes,
            out int orphansSkipped)
        {
            allPositions.Clear();
            allTypes.Clear();
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
                // Weight = trunk capacity relative to a bus, read from the prefabs (register
                // A1.10); zero means the save has no vehicle of that type, so the stop is
                // no transfer partner.
                if (weightOf(type) <= 0f)
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

                // Kept whole rather than split by the selected mode: the access pass
                // accumulates every stop type per node, so the map (one mode) and stop
                // placement (the SUGGESTED line's mode) read the same numbers.
                allPositions.Add(flat);
                allTypes.Add((int)type);
            }
        }

        // The pedestrian network (register A1.6): every net edge with a lane a
        // pedestrian may use — streets with pavements and stand-alone paths alike,
        // read straight from SubLane.m_PathMethods — plus the nodes those edges end
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
            out int edgesWithoutPavement)
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

        // Residents per home building: every household renting a property, weighted by
        // the citizens it holds, placed at the building. Replaces the game's 224 m
        // population raster as the demand source (register, Phase 3): on a real save
        // that raster had 51 occupied cells for 38,000 residents. Buildings are listed
        // in first-seen household order.
        public static void CollectResidents(
            EntityManager entityManager,
            EntityQuery householdQuery,
            ComponentLookup<Game.Buildings.PropertyRenter> renterLookup,
            ComponentLookup<Transform> transformLookup,
            List<float2> positions,
            List<float> residents,
            out int householdsWithoutHome)
        {
            positions.Clear();
            residents.Clear();
            householdsWithoutHome = 0;
            using var households = householdQuery.ToEntityArray(Allocator.Temp);
            var slotOf = new Dictionary<Entity, int>();
            for (int i = 0; i < households.Length; i++)
            {
                Entity household = households[i];
                if (!renterLookup.HasComponent(household))
                {
                    householdsWithoutHome++;
                    continue;
                }

                Entity home = renterLookup[household].m_Property;
                if (!transformLookup.HasComponent(home)
                    || !entityManager.TryGetBuffer(household, isReadOnly: true, out DynamicBuffer<Game.Citizens.HouseholdCitizen> members))
                {
                    householdsWithoutHome++;
                    continue;
                }

                if (!slotOf.TryGetValue(home, out int slot))
                {
                    slot = positions.Count;
                    slotOf[home] = slot;
                    float3 position = transformLookup[home].m_Position;
                    positions.Add(new float2(position.x, position.z));
                    residents.Add(0f);
                }

                residents[slot] += members.Length;
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
            return entityManager.TryGetBuffer(stop, isReadOnly: true, out DynamicBuffer<ConnectedRoute> routes)
                && routes.Length > 0;
        }

        public static bool IsStopOfMode(PrefabSystem prefabSystem, Entity prefab, ModePreset mode)
        {
            return TryGetStopType(prefabSystem, prefab, out TransportType type) && type == TransportTypeOf(mode);
        }

        public static TransportType TransportTypeOf(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return TransportType.Tram;
                case ModePreset.Metro: return TransportType.Subway;
                case ModePreset.Train: return TransportType.Train;
                case ModePreset.Ferry: return TransportType.Ferry;
                default: return TransportType.Bus;
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

            if (zoneSystem is null)
            {
                return;
            }

            using var entities = blockQuery.ToEntityArray(Allocator.Temp);
            using var blocks = blockQuery.ToComponentDataArray<Block>(Allocator.Temp);
            var zoneKinds = new Dictionary<ushort, AreaKind>();

            for (int b = 0; b < entities.Length; b++)
            {
                Block block = blocks[b];
                if (block.m_Size.x <= 0)
                {
                    continue;
                }

                if (!entityManager.TryGetBuffer(entities[b], isReadOnly: true, out DynamicBuffer<Cell> cells))
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

                    if (cell.m_Zone.m_Index == 0)
                    {
                        continue;
                    }

                    if (!zoneKinds.TryGetValue(cell.m_Zone.m_Index, out AreaKind kind))
                    {
                        kind = ClassifyZone(zoneSystem, zoneDataLookup, cell.m_Zone);
                        zoneKinds[cell.m_Zone.m_Index] = kind;
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
