using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Colossal.Entities;
using Game.Citizens;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace WhereTheyGo
{
    // Observed shopping and leisure journeys (register A0.1; formal-specification.md
    // §7b step 1 v2): the once-a-second scan of citizens queued inside buildings and
    // travelling, keyed per (citizen, purpose) so one journey is recorded once, the
    // window they are held in, and the hand-over into the demand refresh's queue. The
    // overlay system owns the queries and the lookups and calls Scan on its cadence.
    internal sealed class TripObserver
    {
        private readonly EntityManager m_EntityManager;
        private readonly EntityQuery m_QueuedQuery;
        private readonly EntityQuery m_TravellingQuery;
        private readonly EntityQuery m_InsideQuery;
        // Set per Scan from the system's lookups; the system keeps them current.
        private ComponentLookup<Transform> m_Transforms;
        private ComponentLookup<Game.Buildings.PropertyRenter> m_Renters;
        private float m_TimeOfDay;

        public TripObserver(EntityManager entityManager, EntityQuery queuedQuery, EntityQuery travellingQuery, EntityQuery insideQuery)
        {
            m_EntityManager = entityManager;
            m_QueuedQuery = queuedQuery;
            m_TravellingQuery = travellingQuery;
            m_InsideQuery = insideQuery;
        }

        // The window of journeys seen; the save state writes and restores it.
        public ObservedTripWindow Window { get; } = new ObservedTripWindow(Assumptions.FramesPerGameDay);

        // What the last Drain handed over, and how the scans since have cost the frame.
        public int LastDemandCount => m_ObservedLastDemand;

        public float LastScale => m_ObservedScaleLastDemand;

        public int ScanCount => m_ObserveCount;

        public long ScanMsSum => m_ObserveMsSum;

        public long ScanMsMax => m_ObserveMsMax;

        public void ResetScanStats()
        {
            m_ObserveCount = 0;
            m_ObserveMsSum = 0;
            m_ObserveMsMax = 0;
        }

        // Leaving a city forgets its journeys and citizens.
        public void Clear()
        {
            Window.Clear();
            m_CurrentJourney.Clear();
            m_LastBuilding.Clear();
        }

        // Observed shopping/leisure demand (register A0.1): the window of journeys
        // seen, each citizen's current journey (so one journey is recorded once),
        // and the building each citizen was last seen inside (a journey's origin).

        private readonly Dictionary<Entity, byte> m_CurrentJourney = new Dictionary<Entity, byte>();

        private readonly Dictionary<Entity, Entity> m_LastBuilding = new Dictionary<Entity, Entity>();


        private int m_ObservedWithoutOrigin;

        // Diagnostics for the scan itself, logged with every demand refresh: how many
        // citizens the travelling query matched, how many carried a watched purpose,
        // how many of those had a queued destination, and which purposes were seen.
        private int m_ObservedQueued;

        private int m_ObservedTravelling;

        private int m_ObservedTravellingWithBuilding;

        private int m_ObservedTargetMissing;

        private int m_ObservedTargetVehicle;

        private int m_ObservedTargetOther;

        private readonly int[] m_ObservedPurposeHistogram = new int[256];

        private int m_ObservedLastDemand;

        private float m_ObservedScaleLastDemand;

        // Purposes that count as shopping or leisure (register A0.1). Working,
        // studying and going home are covered by the save's own home-work/school pairs;
        // service trips (hospital, mail, garbage, crime) are not passenger demand.
        private static bool IsShoppingOrLeisure(Purpose purpose)
        {
            return purpose is Purpose.Shopping or Purpose.Leisure or Purpose.Relaxing
                or Purpose.Sightseeing or Purpose.VisitAttractions;
        }

        // One scan of the live city, in two stages, both keyed per (citizen, purpose)
        // so one journey is recorded once however often it is seen:
        //
        //  A. Queued: a citizen still inside a building with a TripNeeded entry of a
        //     watched purpose. Origin (CurrentBuilding) and destination (the entry's
        //     m_TargetAgent) are both exact. Visible only until TripNeededSystem
        //     dispatches the trip — it runs every 16 frames over update-frame groups,
        //     so a second's sampling sees most but not all departures.
        //  B. Travelling: a citizen carrying TravelPurpose with a CurrentTransport. The
        //     TripNeeded entry is gone by then (the first live scan proved it: 700
        //     shopping travellers, none with an entry), so the destination is read from
        //     the travelling creature's Target, which decompiled ResidentAISystem
        //     rewrites only while boarding a vehicle that is itself the target and while
        //     diverted — hence the building-only filter and the per-purpose key. Origin
        //     = the building the citizen was last seen inside. Visible for the whole
        //     journey, so nothing stage A missed is lost.
        // The once-a-second scan's cost, folded into the next "Travel demand" log line
        // rather than logged sixty times a minute.
        private int m_ObserveCount;

        private long m_ObserveMsSum;

        private long m_ObserveMsMax;

        // One scan. The lookups and type handles come from the overlay system, which
        // updates them against its own state; the clock is the frame's day fraction.
        public void Scan(
            uint frame,
            float timeOfDay,
            ComponentLookup<Transform> transforms,
            ComponentLookup<Game.Buildings.PropertyRenter> renters,
            EntityTypeHandle entityType,
            BufferTypeHandle<TripNeeded> tripType,
            ComponentTypeHandle<CurrentBuilding> buildingType)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            m_Transforms = transforms;
            m_Renters = renters;
            m_TimeOfDay = timeOfDay;
            RememberBuildings();

            var seen = new HashSet<Entity>();
            ObserveQueued(frame, seen, entityType, tripType, buildingType);
            ObserveTravelling(frame, seen);

            // Journeys no longer under way: forget them so the next one is new.
            var ended = new List<Entity>();
            foreach (Entity citizen in m_CurrentJourney.Keys)
            {
                if (!seen.Contains(citizen))
                {
                    ended.Add(citizen);
                }
            }

            for (int i = 0; i < ended.Count; i++)
            {
                _ = m_CurrentJourney.Remove(ended[i]);
            }

            Window.Prune(frame);

            m_ObserveCount++;
            m_ObserveMsSum += clock.ElapsedMilliseconds;
            m_ObserveMsMax = Math.Max(m_ObserveMsMax, clock.ElapsedMilliseconds);
        }

        private void RememberBuildings()
        {
            using var inside = m_InsideQuery.ToEntityArray(Allocator.Temp);
            using var buildings = m_InsideQuery.ToComponentDataArray<CurrentBuilding>(Allocator.Temp);
            for (int i = 0; i < inside.Length; i++)
            {
                m_LastBuilding[inside[i]] = buildings[i].m_CurrentBuilding;
            }
        }

        private void ObserveQueued(uint frame, HashSet<Entity> seen, EntityTypeHandle entityType, BufferTypeHandle<TripNeeded> tripType, ComponentTypeHandle<CurrentBuilding> buildingType)
        {
            m_ObservedQueued = 0;
            Array.Clear(m_ObservedPurposeHistogram, 0, m_ObservedPurposeHistogram.Length);
            using var chunks = m_QueuedQuery.ToArchetypeChunkArray(Allocator.Temp);
            for (int c = 0; c < chunks.Length; c++)
            {
                ArchetypeChunk chunk = chunks[c];
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                NativeArray<CurrentBuilding> buildings = chunk.GetNativeArray(ref buildingType);
                BufferAccessor<TripNeeded> trips = chunk.GetBufferAccessor(ref tripType);
                for (int i = 0; i < entities.Length; i++)
                {
                    DynamicBuffer<TripNeeded> queue = trips[i];
                    if (queue.Length == 0)
                    {
                        continue;
                    }

                    TripNeeded trip = queue[0];
                    if (!IsShoppingOrLeisure(trip.m_Purpose) || trip.m_TargetAgent == Entity.Null)
                    {
                        continue;
                    }

                    m_ObservedQueued++;
                    Entity citizen = entities[i];
                    _ = seen.Add(citizen);
                    if (m_CurrentJourney.TryGetValue(citizen, out byte current) && current == (byte)trip.m_Purpose)
                    {
                        continue;
                    }

                    m_CurrentJourney[citizen] = (byte)trip.m_Purpose;
                    RecordObservedTrip(buildings[i].m_CurrentBuilding, trip.m_TargetAgent, (byte)trip.m_Purpose, frame);
                }
            }
        }

        private void ObserveTravelling(uint frame, HashSet<Entity> seen)
        {
            m_ObservedTravelling = 0;
            m_ObservedTravellingWithBuilding = 0;
            m_ObservedTargetMissing = 0;
            m_ObservedTargetVehicle = 0;
            m_ObservedTargetOther = 0;
            using var travellers = m_TravellingQuery.ToEntityArray(Allocator.Temp);
            using var purposes = m_TravellingQuery.ToComponentDataArray<TravelPurpose>(Allocator.Temp);
            using var transports = m_TravellingQuery.ToComponentDataArray<CurrentTransport>(Allocator.Temp);
            for (int i = 0; i < travellers.Length; i++)
            {
                Purpose purpose = purposes[i].m_Purpose;
                m_ObservedPurposeHistogram[(byte)purpose]++;
                if (!IsShoppingOrLeisure(purpose))
                {
                    continue;
                }

                m_ObservedTravelling++;
                Entity citizen = travellers[i];
                _ = seen.Add(citizen);
                if (m_CurrentJourney.TryGetValue(citizen, out byte current) && current == (byte)purpose)
                {
                    continue;
                }

                if (!TryCreatureDestination(transports[i].m_CurrentTransport, out Entity target))
                {
                    continue;
                }

                m_ObservedTravellingWithBuilding++;
                m_CurrentJourney[citizen] = (byte)purpose;
                if (!m_LastBuilding.TryGetValue(citizen, out Entity origin))
                {
                    m_ObservedWithoutOrigin++;
                    continue;
                }

                RecordObservedTrip(origin, target, (byte)purpose, frame);
            }
        }

        // The travelling creature's Target, accepted only when it is a building or a
        // company renting one. A creature walking to its parked car carries the CAR as
        // its target (ResidentAISystem hands it to TryEnterVehicle); the car's own
        // Target is then the destination, so one hop through a vehicle is followed.
        // Anything else — a vehicle with no building target, a lane, nothing — is
        // counted by kind so the log says what the scan could not read.
        private bool TryCreatureDestination(Entity creature, out Entity target)
        {
            target = Entity.Null;
            if (creature == Entity.Null || !m_EntityManager.TryGetComponent(creature, out Game.Common.Target creatureTarget)
                || creatureTarget.m_Target == Entity.Null)
            {
                m_ObservedTargetMissing++;
                return false;
            }

            Entity candidate = creatureTarget.m_Target;
            if (IsBuildingLike(candidate))
            {
                target = candidate;
                return true;
            }

            if (m_EntityManager.HasComponent<Game.Vehicles.Vehicle>(candidate))
            {
                if (m_EntityManager.TryGetComponent(candidate, out Game.Common.Target vehicleTarget) && IsBuildingLike(vehicleTarget.m_Target))
                {
                    target = vehicleTarget.m_Target;
                    return true;
                }

                m_ObservedTargetVehicle++;
                return false;
            }

            m_ObservedTargetOther++;
            return false;
        }

        private bool IsBuildingLike(Entity entity)
        {
            return entity != Entity.Null
                && (m_EntityManager.HasComponent<Game.Buildings.Building>(entity) || m_Renters.HasComponent(entity));
        }

        private void RecordObservedTrip(Entity origin, Entity target, byte purpose, uint frame)
        {
            if (!TryResolveBuildingPosition(origin, out float3 from) || !TryResolveBuildingPosition(target, out float3 to))
            {
                m_ObservedWithoutOrigin++;
                return;
            }

            // First seen already at the destination (the purpose stays on the citizen
            // while shopping): no journey to record.
            if (math.distancesq(new float2(from.x, from.z), new float2(to.x, to.z)) < 1f)
            {
                return;
            }

            Window.Record(new ObservedTrip
            {
                m_Frame = frame,
                m_OriginX = from.x,
                m_OriginZ = from.z,
                m_DestinationX = to.x,
                m_DestinationZ = to.z,
                m_Purpose = purpose,
                m_TimeOfDay = m_TimeOfDay,
            });
        }

        private string PurposeHistogram()
        {
            var builder = new StringBuilder();
            for (int p = 0; p < m_ObservedPurposeHistogram.Length; p++)
            {
                if (m_ObservedPurposeHistogram[p] == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    _ = builder.Append(", ");
                }

                _ = builder.Append(((Purpose)p).ToString()).Append('=').Append(m_ObservedPurposeHistogram[p].ToString(CultureInfo.InvariantCulture));
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        // The same shape as ExtractTripsJob.TryResolvePosition: a building carries a
        // Transform itself, or a company rents one that does.
        private bool TryResolveBuildingPosition(Entity owner, out float3 position)
        {
            position = default;
            if (owner == Entity.Null)
            {
                return false;
            }

            if (m_Transforms.HasComponent(owner))
            {
                position = m_Transforms[owner].m_Position;
                return true;
            }

            if (m_Renters.HasComponent(owner))
            {
                Entity property = m_Renters[owner].m_Property;
                if (m_Transforms.HasComponent(property))
                {
                    position = m_Transforms[property].m_Position;
                    return true;
                }
            }

            return false;
        }

        // The observed window joins the save's home-work/school journeys in the same
        // queue, each observed journey weighted so the window reads as one day.
        public void Drain(NativeQueue<Trip> trips)
        {
            float scale = Window.ScaleFor(Assumptions.FramesPerGameDay);
            m_ObservedLastDemand = Window.Count;
            m_ObservedScaleLastDemand = scale;
            for (int i = 0; i < Window.Count; i++)
            {
                ObservedTrip observed = Window[i];
                var trip = new Trip
                {
                    m_DayShare = Daytime.IsNight(observed.m_TimeOfDay) ? 0f : 1f,
                    m_Origin = new float2Like(observed.m_OriginX, observed.m_OriginZ),
                    m_Destination = new float2Like(observed.m_DestinationX, observed.m_DestinationZ),
                    m_Weight = scale,
                };
                if (float2Like.DistanceSq(trip.m_Origin, trip.m_Destination) < 1f)
                {
                    continue;
                }

                trips.Enqueue(trip);
            }

            DeferredLog.Info(
                $"Journey scan: {(m_ObservedQueued).ToString(CultureInfo.InvariantCulture)} shopping/leisure trips queued inside buildings, " +
                $"{(m_ObservedTravelling).ToString(CultureInfo.InvariantCulture)} under way ({(m_ObservedTravellingWithBuilding).ToString(CultureInfo.InvariantCulture)} newly recorded from the creature's building target; " +
                $"targets unreadable this scan: none={(m_ObservedTargetMissing).ToString(CultureInfo.InvariantCulture)}, vehicle without building target={(m_ObservedTargetVehicle).ToString(CultureInfo.InvariantCulture)}, other={(m_ObservedTargetOther).ToString(CultureInfo.InvariantCulture)}); " +
                $"buildings remembered for {(m_LastBuilding.Count).ToString(CultureInfo.InvariantCulture)} citizens; purposes under way: {PurposeHistogram()}");
            if (Window.EvictedSinceLastReport > 0 || Window.DroppedAtCapSinceLastReport > 0 || m_ObservedWithoutOrigin > 0)
            {
                DeferredLog.Info(
                    $"Observed journeys: {(Window.Count).ToString(CultureInfo.InvariantCulture)} held " +
                    $"(shopping {(Window.CountOf((byte)Purpose.Shopping)).ToString(CultureInfo.InvariantCulture)}, " +
                    $"leisure {(Window.CountOf((byte)Purpose.Leisure) + Window.CountOf((byte)Purpose.Relaxing)).ToString(CultureInfo.InvariantCulture)}, " +
                    $"sightseeing {(Window.CountOf((byte)Purpose.Sightseeing) + Window.CountOf((byte)Purpose.VisitAttractions)).ToString(CultureInfo.InvariantCulture)}), " +
                    $"evicted={(Window.EvictedSinceLastReport).ToString(CultureInfo.InvariantCulture)}, " +
                    $"droppedAtCap={(Window.DroppedAtCapSinceLastReport).ToString(CultureInfo.InvariantCulture)}, " +
                    $"withoutKnownOrigin={(m_ObservedWithoutOrigin).ToString(CultureInfo.InvariantCulture)} (journeys seen before the citizen was ever seen inside a building)");
                Window.ClearCounters();
                m_ObservedWithoutOrigin = 0;
            }
        }
    }
}
