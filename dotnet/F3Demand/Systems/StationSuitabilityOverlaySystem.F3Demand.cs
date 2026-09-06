using System.Collections.Generic;
using System.Globalization;
using Game.Citizens;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // Travel demand: the home-work/school journeys read from the save joined with the
    // observed window, the routable model of the existing network, the served-demand
    // discount, the equity measure and the desire-line layer.
    public sealed partial class StationSuitabilityOverlaySystem
    {
        private EntityQuery m_EconomyQuery;

        // Travel demand and route suggestions.
        private EntityQuery m_CitizenQuery;

        private ComponentLookup<Worker> m_WorkerLookup;

        private ComponentLookup<Game.Citizens.Student> m_StudentLookup;

        private ComponentLookup<TouristHousehold> m_TouristLookup;

        // Where a rider of a suggested line could change vehicle. Derived from the
        // existing network and rebuilt by BuildTransitModel with everything else that
        // depends on it — there is no separate invalidation to get wrong.
        private InterchangeMap m_Interchanges;

        private TransitNetwork? m_TransitNetwork;

        private DijkstraWorkspace? m_TransitWorkspace;





        private readonly List<ZoneFlow> m_ZoneFlows = new List<ZoneFlow>();
        // Zone-to-stop reach, the routable pairs and the served-demand discount (F3
        // steps 3 and 4), rebuilt with the transit model on every demand refresh.
        private readonly ServedDemand m_ServedDemand = new ServedDemand();

        // The equity measure (register A1.8/A1.9): every journey of the last demand
        // refresh with its ends snapped to the pedestrian network, the walk from each
        // network node to the nearest served stop, and the coverage that gives.
        private readonly List<Trip> m_Journeys = new List<Trip>();

        private float[]? m_DemandRaster;

        private int2 m_ZoneGrid;

        private float m_LastDemandRefresh;

        // Journey weight still looking for a service once the existing network has
        // taken its share — the pool every candidate is scored against, and therefore
        // the only honest denominator for a candidate's reach.
        //
        // This used to be the PRE-discount total while the credit in the numerator was
        // drawn from the post-discount weights, so a reach of "7% of city travel" was
        // really 17% of the demand it was competing for. Two different pools, one
        // ratio.
        private float m_UnservedTravelWeight;

        private RouteGoal m_LastObjective;

        private int m_LastRouteCount;

        // The city's working hours as day fractions (EconomyParameterData); the game's
        // shifts sit on them (Daytime). Logged once so the classification is auditable.
        private bool m_WorkDayLogged;

        private void ReadWorkDay(out float start, out float end)
        {
            start = 0.25f;
            end = 0.7083f;
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
                    $"day-shift rides by day {(Daytime.CommuteDayShare(0, start, end) * 100f).ToString("F0", CultureInfo.InvariantCulture)} %, evening {(Daytime.CommuteDayShare(1, start, end) * 100f).ToString("F0", CultureInfo.InvariantCulture)} %, night {(Daytime.CommuteDayShare(2, start, end) * 100f).ToString("F0", CultureInfo.InvariantCulture)} %");
            }
        }

        // The whole demand pipeline: extract real journeys, aggregate them, load
        // them onto the road network, and grow route suggestions from the result.
        // Runs on its own slow cadence because it is far heavier than the per-tile
        // scoring — it walks every citizen and runs a shortest-path search per
        // origin zone.
        private void UpdateTravelDemand(Setting settings, int2 gridSize, float2 worldMin, float2 mapSize, bool objectiveChanged)
        {
            // What the refresh costs the frame, phase by phase: the one part of the
            // route pipeline still on the main thread, so its budget is logged.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            m_ZoneGrid = SuitabilityInputs.GridDims(mapSize, Assumptions.ZoneSize);

            int tripCount;
            float totalWeight;
            var trips = new NativeQueue<Trip>(Allocator.TempJob);
            try
            {
                m_WorkerLookup.Update(this);
                m_StudentLookup.Update(this);
                m_TouristLookup.Update(this);
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
                    TouristLookup = m_TouristLookup,
                    TransformLookup = m_TransformLookup,
                    WorkTripWeight = 1f,
                    WorkDayStart = workDayStart,
                    WorkDayEnd = workDayEnd,
                    // School trips are real transit demand but shorter and less
                    // peaked than commutes.
                    // Register A0.3: every purpose weighs the same.
                    SchoolTripWeight = 1f,
                    Trips = trips.AsParallelWriter(),
                };

                job.ScheduleParallel(m_CitizenQuery, Dependency).Complete();
                m_TripObserver.Drain(trips);
                totalWeight = SuitabilityTravelDemand.Aggregate(trips, worldMin, m_ZoneGrid, m_ZoneFlows, out tripCount, m_Journeys);
            }
            finally
            {
                trips.Dispose();
            }

            long extractMs = clock.ElapsedMilliseconds;
            BuildTransitModel(gridSize);
            MeasureEquity(settings);
            DiscountServedDemand();
            m_UnservedTravelWeight = ServedDemand.RemainingWeight(m_ZoneFlows);
            BuildDemandLayer(settings, gridSize, worldMin);
            long modelMs = clock.ElapsedMilliseconds - extractMs;

            long networksMs = 0;
            if (m_GraphDirty || m_RoadGraph.Graph is null)
            {
                BuildNetworks(gridSize, worldMin);
                networksMs = clock.ElapsedMilliseconds - extractMs - modelMs;
            }

            bool passStarted = false;
            bool passDue = m_Routes.Count == 0 || objectiveChanged
                || UnityEngine.Time.realtimeSinceStartup - m_LastRoutePassStart >= Assumptions.RoutePassIntervalSeconds;
            if (m_RoadGraph.Graph is not null && m_ZoneNodes is not null && passDue)
            {

                // The flow assignment runs on the worker with the rest of the route
                // pass (it only feeds corridor growth); the main thread hands over the
                // journeys and networks it built.
                passStarted = StartRoutePass(settings, gridSize, worldMin, tripCount, totalWeight);
            }

            m_LastDemandRefresh = UnityEngine.Time.realtimeSinceStartup;
            if (!passStarted)
            {
                UpdateRouteSummary(tripCount, 0);
            }

            DeferredLog.Info(
                $"Travel demand: trips={(tripCount).ToString(CultureInfo.InvariantCulture)} (observed shopping/leisure {(m_TripObserver.LastDemandCount).ToString(CultureInfo.InvariantCulture)} ×{(m_TripObserver.LastScale).ToString("F2", CultureInfo.InvariantCulture)} over {(LineHistory.GameHours(m_TripObserver.Window.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours), " +
                $"weight={(totalWeight).ToString("F0", CultureInfo.InvariantCulture)}, zonePairs={m_ZoneFlows.Count}, " +
                $"route pass {(passStarted ? "started on the worker" : passDue ? "not started (no network)" : $"not due (every {Assumptions.RoutePassIntervalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s)")}; " +
                $"main thread {(clock.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms (extract {(extractMs).ToString(CultureInfo.InvariantCulture)}, model {(modelMs).ToString(CultureInfo.InvariantCulture)}, " +
                $"networks {(networksMs).ToString(CultureInfo.InvariantCulture)}, assign {(clock.ElapsedMilliseconds - extractMs - modelMs - networksMs).ToString(CultureInfo.InvariantCulture)}); " +
                $"trip observation since the last refresh: {(m_TripObserver.ScanCount).ToString(CultureInfo.InvariantCulture)} scans, " +
                $"mean {(m_TripObserver.ScanCount > 0 ? m_TripObserver.ScanMsSum / (double)m_TripObserver.ScanCount : 0.0).ToString("F1", CultureInfo.InvariantCulture)} ms, max {(m_TripObserver.ScanMsMax).ToString(CultureInfo.InvariantCulture)} ms");
            m_TripObserver.ResetScanStats();
            if (!passStarted)
            {
                LogSanityChecks(totalWeight);
            }
        }

        // Reads the existing transit system and turns it into a routable model, so a
        // journey can be tested against the network that actually exists rather than
        // against how close its ends happen to be to some stop.
        private void BuildTransitModel(int2 gridSize)
        {
            RefreshLineHealth();

            // A city with NO transit at all still gets a model, empty though it is.
            // Returning early here left m_TransitNetwork and m_BaselineSeconds null,
            // which is what ScoreCandidates guards on — so the transfer-scoring pass
            // never ran, every candidate kept an enabled demand of zero, and the
            // MinEnabledDemandShare gate then dropped the lot with "would improve only
            // 0.00% of unserved travel". The mod could not suggest a first line until
            // the player had already built one.
            //
            // The empty case is routable, not special: no stops means no walk edges and
            // a graph of zero nodes, every zone maps to no stop, no pair is routable,
            // and so every journey's baseline stays at "unreachable" — which is exactly
            // the baseline a first line has to be credited against.
            var xs = new float[m_TransitStops.Count];
            var zs = new float[m_TransitStops.Count];
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                xs[i] = m_TransitStops[i].x;
                zs[i] = m_TransitStops[i].y;
            }

            // Which modes serve each stop. A stop is in m_TransitStops only because a
            // line calls there, so every entry here is service a rider can actually use.
            var stopModes = new int[m_TransitStops.Count];
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                int bit = TransitModes.ModeBit(line.m_Mode);
                for (int k = 0; k < line.m_StopIndices.Count; k++)
                {
                    int stop = line.m_StopIndices[k];
                    if (stop >= 0 && stop < stopModes.Length)
                    {
                        stopModes[stop] |= bit;
                    }
                }
            }

            m_Interchanges = SuitabilityTransit.BuildInterchangeMap(
                xs, zs, stopModes, m_TransitStops.Count, Assumptions.TransferWalkRadius);

            List<TransitLine> transitLines = SuitabilityLines.ToTransitLines(m_ExistingLines);
            m_TransitNetwork = SuitabilityTransit.Build(xs, zs, m_TransitStops.Count, transitLines,
                Assumptions.TransferWalkRadius, Assumptions.DefaultBoardPenaltySeconds);
            m_TransitWorkspace ??= new DijkstraWorkspace(0);
            m_TransitWorkspace.Resize(m_TransitNetwork.Graph.NodeCount);

            m_ServedDemand.MapZonesToStops(xs, zs, new float2Like(m_ScoreWorldMin.x, m_ScoreWorldMin.y), new int2Like(m_ZoneGrid.x, m_ZoneGrid.y));
            m_ServedDemand.BuildPairs(m_ZoneFlows);

            int problems = 0;
            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                if (m_LineHealth[i].Severity > 0)
                {
                    problems++;
                }
            }

            DeferredLog.Info(
                $"Transit model: lines={m_ExistingLines.Count}, stops={m_TransitStops.Count}, " +
                $"graphNodes={(m_TransitNetwork.Graph.NodeCount).ToString(CultureInfo.InvariantCulture)}, routablePairs={(m_ServedDemand.PairCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"linesNeedingAttention={(problems).ToString(CultureInfo.InvariantCulture)}");

            for (int i = 0; i < m_LineHealth.Count && i < 12; i++)
            {
                LineHealth entry = m_LineHealth[i];
                DeferredLog.Info(
                    $"Line {(entry.m_Index).ToString(CultureInfo.InvariantCulture)} ({entry.m_Mode}): {SuitabilityLineHealth.Describe(entry)} — " +
                    $"{(entry.m_Passengers).ToString(CultureInfo.InvariantCulture)}/{(entry.m_Capacity).ToString(CultureInfo.InvariantCulture)} aboard right now, " +
                    $"judged at {(entry.m_Usage * 100f).ToString("F1", CultureInfo.InvariantCulture)}% " +
                    $"(peak {(entry.m_PeakUsage * 100f).ToString("F1", CultureInfo.InvariantCulture)}%) " +
                    $"{(entry.m_WindowSamples > 0 ? $"from {entry.m_WindowSamples.ToString(CultureInfo.InvariantCulture)} readings over {entry.m_WindowGameHours.ToString("F1", CultureInfo.InvariantCulture)}h" : "from this reading alone")}, " +
                    $"{(entry.m_Vehicles).ToString(CultureInfo.InvariantCulture)}/{(entry.m_TargetVehicles).ToString(CultureInfo.InvariantCulture)} vehicles, typicalWait {(entry.m_TypicalWait).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                    $"{(entry.m_Stops).ToString(CultureInfo.InvariantCulture)} stops, {(entry.m_LengthKm).ToString("F1", CultureInfo.InvariantCulture)} km");
            }
        }

        // Unserved demand, decided by ROUTING each journey over the existing network
        // rather than by how close its ends are to a stop (ServedDemand.TryDiscount). A
        // journey the network can already carry within a reasonable time is
        // discounted; one it cannot is left at full weight to drive a suggestion.
        private void DiscountServedDemand()
        {
            if (m_RawTerms is null || m_TransitNetwork is null || m_TransitWorkspace is null)
            {
                return;
            }

            if (!m_ServedDemand.TryDiscount(m_TransitNetwork, m_TransitWorkspace, m_ZoneFlows, out DiscountReport report))
            {
                return;
            }

            DeferredLog.Info(
                $"Served-demand discount: {(report.ServedPairs).ToString(CultureInfo.InvariantCulture)} of {(report.PairCount).ToString(CultureInfo.InvariantCulture)} routable pairs already carried, " +
                $"median carried journey {(report.MedianSeconds).ToString("F0", CultureInfo.InvariantCulture)}s -> ceiling {(report.CeilingSeconds).ToString("F0", CultureInfo.InvariantCulture)}s " +
                $"({(report.CeilingSeconds >= Assumptions.MaxJourneySeconds ? "the fixed hour: too few carried journeys for a median, or a slow network" : $"{Assumptions.ServedCeilingMultiple.ToString("F0", CultureInfo.InvariantCulture)}x this city's median")}); " +
                $"weight {(report.WeightBefore).ToString("F0", CultureInfo.InvariantCulture)} -> {(report.WeightAfter).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"({((report.WeightBefore > 0f ? (1f - report.WeightAfter / report.WeightBefore) * 100f : 0f)).ToString("F0", CultureInfo.InvariantCulture)}% absorbed by existing lines)");
        }

        private void BuildDemandLayer(Setting settings, int2 gridSize, float2 worldMin)
        {
            // Rasterising and normalising a layer no infomode can select is a pass over
            // every cell on the map for nothing.
            if (TermLayer(SuitabilityLayer.TravelDemand) is null)
            {
                return;
            }

            int cells = gridSize.x * gridSize.y;
            if (m_DemandRaster is null || m_DemandRaster.Length != cells)
            {
                m_DemandRaster = new float[cells];
            }

            SuitabilityZones.RasterizeDesireLines(
                m_ZoneFlows, new float2Like(worldMin.x, worldMin.y), new int2Like(m_ZoneGrid.x, m_ZoneGrid.y),
                new int2Like(gridSize.x, gridSize.y), Assumptions.TileSize, m_DemandRaster);

            byte[] layer = m_LayerIntensities[(int)SuitabilityLayer.TravelDemand];
            if (layer is not null && layer.Length == cells && m_ScoreScratch is not null && m_ScoreScratch.Length >= cells)
            {
                SuitabilityScoring.NormalizeIntensities(
                    m_DemandRaster, cells, settings.HighlightShare / 100f, Assumptions.IntensityGamma, layer, m_ScoreScratch);
                m_Infoview.InvalidateExpandedCache();
            }
        }
    }
}
