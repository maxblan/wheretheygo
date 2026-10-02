using System.Collections.Generic;
using System.Globalization;
using Game.Citizens;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WhereTheyGo
{
    // Travel demand: the home-work/school journeys read from the save joined with the
    // observed window, handed to the coverage measure and the routing pass.
    public sealed partial class WhereTheyGoSystem
    {
        private EntityQuery m_EconomyQuery;

        // Travel demand and route suggestions.
        private EntityQuery m_CitizenQuery;

        private ComponentLookup<Worker> m_WorkerLookup;

        private ComponentLookup<Game.Citizens.Student> m_StudentLookup;

        // Every journey of the last demand refresh, in the total order
        // DemandZones.Aggregate leaves them in: what a selection pass routes and the
        // pair table was built from. Adopted from the worker with the stage that
        // ordered it, and never written in place.
        private List<Journey> m_Journeys = new List<Journey>();

        // The buffers a demand stage fills, kept between refreshes so a city of a
        // hundred thousand does not hand the collector several megabytes every thirty
        // seconds: the drained trips, which the stage empties, and the journey list
        // m_Journeys held before the last adoption, which nothing reads any more.
        private List<Journey>? m_TripBuffer;

        private List<Journey>? m_SpareJourneys;

        private int2 m_ZoneGrid;

        private float m_LastDemandRefresh;

        // Whether a demand refresh has run at all in this city. Until one has, the
        // refresh is tried every frame the snap allows rather than on the cadence.
        private bool m_DemandRefreshed;

        // The city's working hours as day fractions (EconomyParameterData); the game's
        // shifts sit on them (Daytime). Logged once so the classification is auditable.
        private bool m_WorkDayLogged;

        private void ReadWorkDay(out float start, out float end)
        {
            start = Assumptions.WorkDayStartDefault;
            end = Assumptions.WorkDayEndDefault;
            if (!m_EconomyQuery.IsEmptyIgnoreFilter)
            {
                EconomyParameterData economy = m_EconomyQuery.GetSingleton<EconomyParameterData>();
                start = economy.m_WorkDayStart;
                end = economy.m_WorkDayEnd;
            }

            if (!m_WorkDayLogged)
            {
                m_WorkDayLogged = true;
                DeferredLog.Info(
                    $"Work day from EconomyParameterData: {(start * 24f).ToString("F1", CultureInfo.InvariantCulture)}h to {(end * 24f).ToString("F1", CultureInfo.InvariantCulture)}h; " +
                    $"evening shift +{(Assumptions.EveningShiftOffset * 24f).ToString("F1", CultureInfo.InvariantCulture)}h, night shift +{(Assumptions.NightShiftOffset * 24f).ToString("F1", CultureInfo.InvariantCulture)}h; " +
                    $"night is {(Assumptions.NightStart * 24f).ToString("F0", CultureInfo.InvariantCulture)}:00–{(Assumptions.NightEnd * 24f).ToString("F0", CultureInfo.InvariantCulture)}:00 (TransportLineSystem); " +
                    $"commute hours by shift: {ShiftHours(0, start, end)}, evening {ShiftHours(1, start, end)}, night {ShiftHours(2, start, end)}");
            }
        }

        // "07:00 out, 16:00 back (100 % by day)" for one shift at no offset, so the log
        // says what the MIDDLE of that shift's hour spread is. Each citizen's own
        // ±1 h moves them off it (Daytime.WorkHours).
        private static string ShiftHours(byte shift, float start, float end)
        {
            Daytime.WorkHours(shift, start, end, 0f, out byte outHour, out byte backHour);
            float dayShare = Daytime.DayShareOfHours(outHour, backHour);
            return $"{(outHour).ToString("00", CultureInfo.InvariantCulture)}:00 out, " +
                $"{(backHour).ToString("00", CultureInfo.InvariantCulture)}:00 back " +
                $"({(dayShare * 100f).ToString("F0", CultureInfo.InvariantCulture)} % by day)";
        }

        // The demand pipeline's main-thread half: read the journeys, re-read the lines,
        // bring the served-walk field up to date and hand everything else to the
        // worker (DemandStage). On its own slow cadence because it walks every citizen.
        private void UpdateTravelDemand(Setting settings, float2 mapSize)
        {
            // What the refresh costs the frame, phase by phase, so its budget is logged.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            m_ZoneGrid = WalkNetwork.GridDims(mapSize, Assumptions.ZoneSize);

            var demand = new DemandStage
            {
                Trips = m_TripBuffer ??= new List<Journey>(),
                Journeys = m_SpareJourneys ?? new List<Journey>(),
            };
            m_SpareJourneys = null;
            var trips = new NativeQueue<Journey>(Allocator.TempJob);
            try
            {
                m_WorkerLookup.Update(this);
                m_StudentLookup.Update(this);
                m_PropertyRenterLookup.Update(this);
                m_TransformLookup.Update(this);
                ReadWorkDay(out float workDayStart, out float workDayEnd);

                var job = new ExtractTripsJob
                {
                    EntityType = GetEntityTypeHandle(),
                    CitizenType = GetComponentTypeHandle<Citizen>(isReadOnly: true),
                    HouseholdMemberType = GetComponentTypeHandle<HouseholdMember>(isReadOnly: true),
                    WorkerLookup = m_WorkerLookup,
                    StudentLookup = m_StudentLookup,
                    PropertyRenterLookup = m_PropertyRenterLookup,
                    TransformLookup = m_TransformLookup,
                    WorkTripWeight = 1f,
                    WorkDayStart = workDayStart,
                    WorkDayEnd = workDayEnd,
                    // School trips are real transit demand but shorter and less
                    // peaked than commutes.
                    // Every purpose weighs the same.
                    SchoolTripWeight = 1f,
                    Trips = trips.AsParallelWriter(),
                };

                job.ScheduleParallel(m_CitizenQuery, Dependency).Complete();
                m_TripObserver.Drain(trips);
                TravelDemand.Drain(trips, demand.Trips);
            }
            finally
            {
                trips.Dispose();
            }

            int gathered = demand.Trips.Count;
            long extractMs = clock.ElapsedMilliseconds;
            RefreshLineHealth();
            PrepareCoverage(settings, demand);
            long modelMs = clock.ElapsedMilliseconds - extractMs;

            // The gate in MaybeUpdateTravelDemand keeps a pass from being out here, so
            // this cannot fail today; if it ever does, the refresh is not counted as
            // done and the next frame tries again, rather than losing its journeys for
            // half a minute.
            if (!StartRoutingPass(demand))
            {
                m_SpareJourneys = demand.Journeys;
                demand.Trips.Clear();
                DeferredLog.Warn("Travel demand: a routing pass was already out, so this refresh was not handed over; retrying");
                return;
            }

            long routingMs = clock.ElapsedMilliseconds - extractMs - modelMs;
            m_LastDemandRefresh = UnityEngine.Time.realtimeSinceStartup;
            m_DemandRefreshed = true;

            DeferredLog.Info(
                $"Travel demand: {(gathered).ToString(CultureInfo.InvariantCulture)} trips gathered (observed shopping/leisure {(m_TripObserver.LastDemandCount).ToString(CultureInfo.InvariantCulture)} ×{(m_TripObserver.LastScale).ToString("F2", CultureInfo.InvariantCulture)} over {(LineHistory.GameHours(m_TripObserver.Window.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours), " +
                $"lines={(m_ExistingLines.Count).ToString(CultureInfo.InvariantCulture)}, stops={(m_TransitStops.Count).ToString(CultureInfo.InvariantCulture)}; " +
                $"main thread {(clock.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms (extract {(extractMs).ToString(CultureInfo.InvariantCulture)}, lines and coverage inputs {(modelMs).ToString(CultureInfo.InvariantCulture)}, " +
                $"handover {(routingMs).ToString(CultureInfo.InvariantCulture)}); " +
                "ordering, snapping, coverage and routing started on the worker; " +
                $"trip observation since the last refresh: {(m_TripObserver.ScanCount).ToString(CultureInfo.InvariantCulture)} scans, " +
                $"mean {(m_TripObserver.ScanCount > 0 ? m_TripObserver.ScanMsSum / (double)m_TripObserver.ScanCount : 0.0).ToString("F1", CultureInfo.InvariantCulture)} ms, max {(m_TripObserver.ScanMsMax).ToString(CultureInfo.InvariantCulture)} ms");
            m_TripObserver.ResetScanStats();
        }

        // The worker's demand stage, adopted: the ordered journeys and the pair table
        // built from them become the city's, the coverage measured on them is
        // published, and the buffers come back for the next refresh. The journey list
        // being replaced becomes the spare: a selection pass reads m_Journeys only
        // while it is out, and none can be while a demand stage is.
        private void AdoptDemand(DemandStage demand)
        {
            m_SpareJourneys = m_Journeys;
            m_Journeys = demand.Journeys;
            m_PairTable = demand.Pairs;
            DeferredLog.Info(
                $"Journeys on the worker: trips={(demand.TripCount).ToString(CultureInfo.InvariantCulture)}, weight={(demand.TotalWeight).ToString("F0", CultureInfo.InvariantCulture)}; " +
                $"ordered in {(demand.SortMs).ToString(CultureInfo.InvariantCulture)} ms, " +
                $"ends snapped in {(demand.SnapMs).ToString(CultureInfo.InvariantCulture)} ms ({(demand.Memo?.Searches ?? 0).ToString(CultureInfo.InvariantCulture)} doors searched, {(demand.Memo?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} remembered), " +
                $"coverage measured in {(demand.MeasureMs).ToString(CultureInfo.InvariantCulture)} ms, " +
                $"{(demand.Pairs.PairCount).ToString(CultureInfo.InvariantCulture)} door pairs in {(demand.PairMs).ToString(CultureInfo.InvariantCulture)} ms");
            AdoptCoverage(demand);
        }
    }
}
