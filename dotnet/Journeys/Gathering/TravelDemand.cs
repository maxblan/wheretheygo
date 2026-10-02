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
        [ReadOnly] public ComponentLookup<Transform> TransformLookup;

        public float WorkTripWeight;
        public float SchoolTripWeight;
        // EconomyParameterData.m_WorkDayStart/End, day fractions; the shift and the
        // citizen's own offset decide when a commuter rides (Daytime.WorkHours).
        public float WorkDayStart;
        public float WorkDayEnd;

        public NativeQueue<Journey>.ParallelWriter Trips;

        // The citizen's own shift of the working day, in days: ±1 h, mirrored from
        // WorkerSystem.GetWorkOffset (StudentSystem.GetStudyOffset is the same draw).
        // Deterministic from the citizen's own pseudo-random seed, so two runs over the
        // same city stagger the same people the same way, and Burst-safe, because
        // Unity.Mathematics.Random is plain integer arithmetic.
        private static float WorkOffsetDays(Citizen citizen)
        {
            return (-10922 + citizen.GetPseudoRandom(CitizenPseudoRandom.WorkOffset).NextInt(21845)) / 262144f;
        }

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
                // Everyone who travels inside the map counts, tourists included.
                // The only exclusion is structural: a
                // citizen without a rented property has no fixed origin to plan from.
                // The homeless and outside commuters both land here.
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
                JourneyPurpose purpose;
                byte outHour;
                byte backHour;
                Entity destinationOwner;
                if (WorkerLookup.HasComponent(citizen))
                {
                    Worker worker = WorkerLookup[citizen];
                    destinationOwner = worker.m_Workplace;
                    weight = WorkTripWeight;
                    purpose = JourneyPurpose.Work;
                    Daytime.WorkHours((byte)worker.m_Shift, WorkDayStart, WorkDayEnd, WorkOffsetDays(citizens[i]), out outHour, out backHour);
                }
                else if (StudentLookup.HasComponent(citizen))
                {
                    destinationOwner = StudentLookup[citizen].m_School;
                    weight = SchoolTripWeight;
                    purpose = JourneyPurpose.School;
                    // Students keep the day shift's hours and the same offset, but the
                    // game does not round theirs (StudentSystem.GetTimeToStudy).
                    Daytime.StudyHours(WorkDayStart, WorkDayEnd, WorkOffsetDays(citizens[i]), out outHour, out backHour);
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
                if (math.distancesq(from, to) < Assumptions.MinJourneyDistanceSq)
                {
                    continue;
                }

                Trips.Enqueue(new Journey
                {
                    m_Origin = new float2Like(from.x, from.y),
                    m_Destination = new float2Like(to.x, to.y),
                    m_Weight = weight,
                    m_Purpose = purpose,
                    m_OutHour = outHour,
                    m_BackHour = backHour,
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
        // Drains the job's queue into `drained`, emptied first, in the order the queue
        // hands the trips back. That order is thread-dependent, which is why
        // DemandZones.Aggregate sorts the journeys totally (on the worker, DemandStage)
        // before anything reads them.
        public static void Drain(NativeQueue<Journey> trips, List<Journey> drained)
        {
            drained.Clear();
            drained.Capacity = System.Math.Max(drained.Capacity, trips.Count);
            while (trips.TryDequeue(out Journey trip))
            {
                drained.Add(trip);
            }
        }
    }
}
