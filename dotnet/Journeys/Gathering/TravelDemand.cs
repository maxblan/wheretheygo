using System.Collections.Generic;
using Game.Buildings;
using Game.Citizens;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace WhereTheyGo
{
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
        // EconomyParameterData.m_WorkDayStart/End, day fractions; the shift decides
        // when a commuter rides (Daytime.CommuteDayShare).
        public float WorkDayStart;
        public float WorkDayEnd;

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
                float dayShare;
                Entity destinationOwner;
                if (WorkerLookup.HasComponent(citizen))
                {
                    Worker worker = WorkerLookup[citizen];
                    destinationOwner = worker.m_Workplace;
                    weight = WorkTripWeight;
                    dayShare = Daytime.CommuteDayShare((byte)worker.m_Shift, WorkDayStart, WorkDayEnd);
                }
                else if (StudentLookup.HasComponent(citizen))
                {
                    destinationOwner = StudentLookup[citizen].m_School;
                    weight = SchoolTripWeight;
                    dayShare = Daytime.CommuteDayShare(0, WorkDayStart, WorkDayEnd);
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
                var from = new float2(origin.x, origin.z);
                var to = new float2(destination.x, destination.z);

                // Working from the building you live in is not a journey.
                if (math.distancesq(from, to) < 1f)
                {
                    continue;
                }

                Trips.Enqueue(new Trip
                {
                    m_Origin = new float2Like(from.x, from.y),
                    m_Destination = new float2Like(to.x, to.y),
                    m_Weight = weight,
                    m_DayShare = dayShare,
                });
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

    internal static class TravelDemand
    {
        // Drains the job's queue in the order it hands the trips back and aggregates
        // them (DemandZones.Aggregate). The order is thread-dependent, which is
        // why the aggregation sorts the flows totally before anything reads them.
        public static float Aggregate(
            NativeQueue<Trip> trips,
            float2 worldMin,
            int2 zoneGrid,
            List<ZoneFlow> flows,
            out int tripCount,
            List<Trip>? journeys = null)
        {
            var drained = new List<Trip>(trips.Count);
            while (trips.TryDequeue(out Trip trip))
            {
                drained.Add(trip);
            }

            return DemandZones.Aggregate(
                drained, new float2Like(worldMin.x, worldMin.y), new int2Like(zoneGrid.x, zoneGrid.y), flows, out tripCount, journeys);
        }
    }
}
