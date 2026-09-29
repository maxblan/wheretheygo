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
        // DemandZones.Aggregate leaves them in: what the coverage measure snaps and the
        // routing pass copies.
        private readonly List<Journey> m_Journeys = new List<Journey>();

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

        // The whole demand pipeline: read the journeys, re-read the lines, measure the
        // coverage and hand the routing to the worker. On its own slow cadence because
        // it walks every citizen.
        private void UpdateTravelDemand(Setting settings, int2 gridSize, float2 worldMin, float2 mapSize)
        {
            // What the refresh costs the frame, phase by phase: the one part of the
            // route pipeline still on the main thread, so its budget is logged.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            m_ZoneGrid = WalkNetwork.GridDims(mapSize, Assumptions.ZoneSize);

            int tripCount;
            float totalWeight;
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
                totalWeight = TravelDemand.Aggregate(trips, worldMin, m_ZoneGrid, m_Journeys, out tripCount);
                // New journeys, new pair table: the passes build it on first use.
                m_PairTable = null;
            }
            finally
            {
                trips.Dispose();
            }

            long extractMs = clock.ElapsedMilliseconds;
            RefreshLineHealth();
            MeasureCoverage(settings);
            long modelMs = clock.ElapsedMilliseconds - extractMs;

            bool passStarted = StartRoutingPass();
            long routingMs = clock.ElapsedMilliseconds - extractMs - modelMs;
            m_LastDemandRefresh = UnityEngine.Time.realtimeSinceStartup;
            m_DemandRefreshed = true;

            DeferredLog.Info(
                $"Travel demand: trips={(tripCount).ToString(CultureInfo.InvariantCulture)} (observed shopping/leisure {(m_TripObserver.LastDemandCount).ToString(CultureInfo.InvariantCulture)} ×{(m_TripObserver.LastScale).ToString("F2", CultureInfo.InvariantCulture)} over {(LineHistory.GameHours(m_TripObserver.Window.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours), " +
                $"weight={(totalWeight).ToString("F0", CultureInfo.InvariantCulture)}, lines={(m_ExistingLines.Count).ToString(CultureInfo.InvariantCulture)}, stops={(m_TransitStops.Count).ToString(CultureInfo.InvariantCulture)}; " +
                $"main thread {(clock.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms (extract {(extractMs).ToString(CultureInfo.InvariantCulture)}, model {(modelMs).ToString(CultureInfo.InvariantCulture)}, " +
                $"handover {(routingMs).ToString(CultureInfo.InvariantCulture)}); " +
                $"routing pass {(passStarted ? "started on the worker" : "not started (one is already out)")}; " +
                $"trip observation since the last refresh: {(m_TripObserver.ScanCount).ToString(CultureInfo.InvariantCulture)} scans, " +
                $"mean {(m_TripObserver.ScanCount > 0 ? m_TripObserver.ScanMsSum / (double)m_TripObserver.ScanCount : 0.0).ToString("F1", CultureInfo.InvariantCulture)} ms, max {(m_TripObserver.ScanMsMax).ToString(CultureInfo.InvariantCulture)} ms");
            m_TripObserver.ResetScanStats();
        }
    }
}
