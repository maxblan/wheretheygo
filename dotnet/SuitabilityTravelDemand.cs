using System.Collections.Generic;
using Game.Buildings;
using Game.Citizens;
using Game.Objects;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // One journey somebody wants to make.
    internal struct Trip
    {
        public float2 m_Origin;
        public float2 m_Destination;
        public float m_Weight;
    }

    // Aggregated demand between two zones. Individual trips are collapsed into
    // these before anything expensive touches them: a city has tens of thousands of
    // trips but only a few hundred zone pairs that matter.
    internal struct ZoneFlow
    {
        public int m_Origin;
        public int m_Destination;
        public float m_Weight;
    }

    // Extracts real origin-destination pairs from the citizens in the save.
    //
    // This is what lets the mod answer "where do people want to go" without a
    // gravity model: home comes from the citizen's household property, and the
    // destination from Worker.m_Workplace or Student.m_School. Commuters and the
    // homeless fall out naturally because their households carry no PropertyRenter,
    // and tourists are excluded explicitly.
    [BurstCompile]
    internal struct ExtractTripsJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle EntityType;
        [ReadOnly] public ComponentTypeHandle<Citizen> CitizenType;
        [ReadOnly] public ComponentTypeHandle<HouseholdMember> HouseholdMemberType;

        [ReadOnly] public ComponentLookup<Worker> WorkerLookup;
        [ReadOnly] public ComponentLookup<Game.Citizens.Student> StudentLookup;
        [ReadOnly] public ComponentLookup<PropertyRenter> PropertyRenterLookup;
        [ReadOnly] public ComponentLookup<TouristHousehold> TouristLookup;
        [ReadOnly] public ComponentLookup<Transform> TransformLookup;

        public float WorkTripWeight;
        public float SchoolTripWeight;

        public NativeQueue<Trip>.ParallelWriter Trips;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            NativeArray<Entity> entities = chunk.GetNativeArray(EntityType);
            NativeArray<Citizen> citizens = chunk.GetNativeArray(ref CitizenType);
            NativeArray<HouseholdMember> members = chunk.GetNativeArray(ref HouseholdMemberType);
            if (citizens.Length == 0 || members.Length == 0)
            {
                return;
            }

            for (int i = 0; i < citizens.Length; i++)
            {
                // Everyone who travels inside the map counts, tourists included
                // (assumptions register A0.2). The only exclusion is structural: a
                // citizen without a rented property has no fixed origin to plan from —
                // the homeless and outside commuters both land here.
                Entity household = members[i].m_Household;
                if (!PropertyRenterLookup.HasComponent(household))
                {
                    continue;
                }

                Entity home = PropertyRenterLookup[household].m_Property;
                if (!TransformLookup.HasComponent(home))
                {
                    continue;
                }

                Entity citizen = entities[i];
                float weight;
                Entity destinationOwner;
                if (WorkerLookup.HasComponent(citizen))
                {
                    destinationOwner = WorkerLookup[citizen].m_Workplace;
                    weight = WorkTripWeight;
                }
                else if (StudentLookup.HasComponent(citizen))
                {
                    destinationOwner = StudentLookup[citizen].m_School;
                    weight = SchoolTripWeight;
                }
                else
                {
                    continue;
                }

                if (!TryResolvePosition(destinationOwner, out float3 destination))
                {
                    continue;
                }

                float3 origin = TransformLookup[home].m_Position;
                var trip = new Trip
                {
                    m_Origin = new float2(origin.x, origin.z),
                    m_Destination = new float2(destination.x, destination.z),
                    m_Weight = weight,
                };

                // Working from the building you live in is not a journey.
                if (math.distancesq(trip.m_Origin, trip.m_Destination) < 1f)
                {
                    continue;
                }

                Trips.Enqueue(trip);
            }
        }

        // Workplaces and schools are the same shape: either the entity carries a
        // Transform itself (city service building, outside connection) or it rents a
        // property whose building does.
        private bool TryResolvePosition(Entity owner, out float3 position)
        {
            if (owner != Entity.Null)
            {
                if (TransformLookup.HasComponent(owner))
                {
                    position = TransformLookup[owner].m_Position;
                    return true;
                }

                if (PropertyRenterLookup.HasComponent(owner))
                {
                    Entity property = PropertyRenterLookup[owner].m_Property;
                    if (TransformLookup.HasComponent(property))
                    {
                        position = TransformLookup[property].m_Position;
                        return true;
                    }
                }
            }

            position = default;
            return false;
        }
    }

    internal static class SuitabilityTravelDemand
    {
        // Zones are much coarser than the score grid: they exist to make the flow
        // assignment tractable, not to be looked at.
        public const float ZoneSize = 256f;

        // Collapses trips into zone-to-zone flows. Returns the total trip weight so
        // the caller can sanity-check the extraction against the city's population.
        public static float Aggregate(
            NativeQueue<Trip> trips,
            float2 worldMin,
            int2 zoneGrid,
            List<ZoneFlow> flows,
            out int tripCount)
        {
            flows.Clear();
            tripCount = 0;

            var totals = new Dictionary<long, float>();
            float totalWeight = 0f;
            int zoneCount = zoneGrid.x * zoneGrid.y;

            while (trips.TryDequeue(out Trip trip))
            {
                int origin = ZoneOf(trip.m_Origin, worldMin, zoneGrid);
                int destination = ZoneOf(trip.m_Destination, worldMin, zoneGrid);
                if (origin < 0 || destination < 0 || origin == destination)
                {
                    continue;
                }

                tripCount++;
                totalWeight += trip.m_Weight;

                long key = (long)origin * zoneCount + destination;
                // Absent key leaves `existing` at zero, which is the wanted starting total.
                _ = totals.TryGetValue(key, out float existing);
                totals[key] = existing + trip.m_Weight;
            }

            foreach (KeyValuePair<long, float> pair in totals)
            {
                flows.Add(new ZoneFlow
                {
                    m_Origin = (int)(pair.Key / zoneCount),
                    m_Destination = (int)(pair.Key % zoneCount),
                    m_Weight = pair.Value,
                });
            }

            // A TOTAL order, not merely grouped by origin.
            //
            // Grouping is what lets the flow assignment run one search per origin zone
            // rather than one per pair. But the trips arrive from a PARALLEL job
            // through a NativeQueue, so the dequeue order depends on thread
            // scheduling; that became the insertion order of `totals`, then its
            // enumeration order, and List.Sort is not stable, so pairs sharing an
            // origin kept whatever order the dictionary happened to yield.
            //
            // Float addition is not associative, so AssignFlow then accumulated
            // slightly different edge flows on every run, and GrowCorridor seeds on a
            // strict `score > seedScore` — a last-bit difference between two
            // near-equal edges flipped which corridor was grown first, and peeling and
            // novelty decay carried that through every later suggestion. Sorting on
            // the destination too makes the whole pipeline a function of the save.
            flows.Sort(static (a, b) =>
            {
                int byOrigin = a.m_Origin.CompareTo(b.m_Origin);
                return byOrigin != 0 ? byOrigin : a.m_Destination.CompareTo(b.m_Destination);
            });
            return totalWeight;
        }

        public static int ZoneOf(float2 position, float2 worldMin, int2 zoneGrid)
        {
            float2 rel = (position - worldMin) / ZoneSize;
            int x = (int)math.floor(rel.x);
            int y = (int)math.floor(rel.y);
            if (x < 0 || x >= zoneGrid.x || y < 0 || y >= zoneGrid.y)
            {
                return -1;
            }

            return x + y * zoneGrid.x;
        }

        public static float2 ZoneCentre(int zone, float2 worldMin, int2 zoneGrid)
        {
            int x = zone % zoneGrid.x;
            int y = zone / zoneGrid.x;
            return worldMin + new float2((x + 0.5f) * ZoneSize, (y + 0.5f) * ZoneSize);
        }

        // Paints straight desire lines between zone pairs into the tile raster, so
        // the demand layer shows where movement wants to happen regardless of what
        // roads exist.
        public static void RasterizeDesireLines(
            List<ZoneFlow> flows,
            float2 worldMin,
            int2 zoneGrid,
            int2 tileGrid,
            float tileSize,
            float[] raster)
        {
            if (raster is null)
            {
                return;
            }

            System.Array.Clear(raster, 0, raster.Length);

            for (int i = 0; i < flows.Count; i++)
            {
                ZoneFlow flow = flows[i];
                float2 from = ZoneCentre(flow.m_Origin, worldMin, zoneGrid);
                float2 to = ZoneCentre(flow.m_Destination, worldMin, zoneGrid);

                float2 fromTile = (from - worldMin) / tileSize;
                float2 toTile = (to - worldMin) / tileSize;

                SuitabilityGraphMath.RasterizeSegment(
                    raster, tileGrid.x, tileGrid.y,
                    fromTile.x, fromTile.y, toTile.x, toTile.y,
                    flow.m_Weight);
            }
        }
    }
}
