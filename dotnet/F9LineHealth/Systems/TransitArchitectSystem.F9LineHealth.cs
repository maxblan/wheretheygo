using System.Collections.Generic;
using System.Globalization;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // The existing lines: read in game time into the rolling window, collected in
    // travel order, handed the routing's riders, judged (LineHealthRules), and
    // re-traced on request for the improvement plan.
    public sealed partial class TransitArchitectSystem
    {
        // Existing transit system: the lines themselves, the routable model of them,
        // and their health.
        private EntityQuery m_LineQuery;

        private readonly List<ExistingLine> m_ExistingLines = new List<ExistingLine>();

        private readonly List<float2Like> m_TransitStops = new List<float2Like>();

        private readonly Dictionary<Entity, int> m_StopIndices = new Dictionary<Entity, int>();

        private readonly List<LineHealth> m_LineHealth = new List<LineHealth>();

        // The inputs the verdicts were drawn from, captured at the judgement so the
        // export describes exactly that pass (LineHealthProblem), and the city-wide bar.
        private LineHealthProblem? m_HealthProblem;

        private HealthReference m_HealthReference;

        private SuggestedRoute? m_ImprovedRoute;

        // A game day of line readings, one every ReadingIntervalFrames of simulation
        // time. Judging a line on the single reading a refresh happens to land on
        // condemned a one-boat ferry as empty whenever its boat was mid-crossing; the
        // window is what a verdict rests on instead.
        private readonly LineHistory m_LineHistory = new LineHistory(Assumptions.FramesPerGameDay);

        private readonly HashSet<int> m_LiveLineIds = new HashSet<int>();

        // What the last route pass's baseline attributed to each existing line, by line
        // id: riders a day and their split by period (LineSetEvaluation.BaseRiders).
        private readonly Dictionary<int, (float riders, float day, float night)> m_ExistingLineRiders =
            new Dictionary<int, (float riders, float day, float night)>();

        private uint m_LastLineRefreshFrame;

        private float m_LastLineSample;

        // Expected rider wait in seconds at a stop position, taken from the best line
        // that actually calls there — the same windowed interval the transit router
        // uses. Zero when no collected line has a stop within reach, which tells the
        // calibration there is nothing honest to record here.
        private float ExpectedWaitAt(float2 position)
        {
            float best = 0f;
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                float wait = line.ExpectedWait;
                if (wait <= 0f)
                {
                    continue;
                }

                for (int j = 0; j < line.m_StopIndices.Count; j++)
                {
                    int index = line.m_StopIndices[j];
                    if (index < 0 || index >= m_TransitStops.Count)
                    {
                        continue;
                    }

                    if (float2Like.DistanceSq(m_TransitStops[index], new float2Like(position.x, position.y)) > Assumptions.StopMatchRadiusSq)
                    {
                        continue;
                    }

                    // The shortest wait wins: a rider at an interchange takes whichever
                    // service turns up first.
                    if (best <= 0f || wait < best)
                    {
                        best = wait;
                    }
                }
            }

            return best;
        }

        // One reading of every line into the window when one is due (register A8.5:
        // every ReadingIntervalFrames of SIMULATION time, so the sample is the same at
        // every speed and a paused game adds nothing). Reads counts only, never the
        // collection the route worker holds, so it runs whether or not a pass is out.
        private void ObserveLines()
        {
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation is null)
            {
                return;
            }

            uint frame = simulation.frameIndex;
            if (frame == 0u || !m_LineHistory.ReadingDue(frame))
            {
                return;
            }

            Lines.Observe(EntityManager, m_LineQuery, frame, TimeOfDay, m_LineHistory, m_LiveLineIds);
        }

        // Reads the lines, hands them the window and the routing's riders, and judges
        // them. Cheap next to the route pipeline — it walks the lines and nothing else.
        //
        // Idempotent within a simulation frame, so the route pipeline can call it
        // without the background tick making it happen twice.
        private void RefreshLineHealth(Setting settings)
        {
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = simulation?.frameIndex ?? 0u;
            if (m_ExistingLines.Count > 0 && frame == m_LastLineRefreshFrame)
            {
                return;
            }

            m_LastLineRefreshFrame = frame;
            Lines.Collect(EntityManager, m_LineQuery, m_PrefabSystem, m_NameSystem,
                m_ExistingLines, m_TransitStops, m_StopIndices, m_LineEntities);
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                if (m_ExistingLineRiders.TryGetValue(line.m_Id, out (float riders, float day, float night) demand))
                {
                    line.m_RidersPerDay = demand.riders;
                    line.m_RidersByDay = demand.day;
                    line.m_RidersByNight = demand.night;
                }
            }

            LineHealthProblem problem = LineHealthRules.Capture(
                m_ExistingLines, m_LineHistory, ReadFleetFacts(),
                settings.UtilisationFloorPercent / 100f, Assumptions.MaxPlannedUtilisation, Assumptions.TargetLoad);
            m_HealthReference = LineHealthRules.JudgeAll(problem, m_LineHealth);
            m_HealthProblem = problem;
            RefreshLineNotifications();
            LogLineHealth(frame);
            UpdateDataCoverage();
            UpdateOverviewRows();
        }

        private void LogLineHealth(uint frame)
        {
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                DeferredLog.Info(
                    $"Line {(i + 1).ToString(CultureInfo.InvariantCulture)} \"{line.m_Name}\" inputs: mode={line.m_Mode}, stops={line.m_StopIndices.Count}, " +
                    $"loop={(line.m_LengthMetres).ToString("F0", CultureInfo.InvariantCulture)}m, roundTrip={(line.m_StableDurationSeconds).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                    $"gameInterval={(line.m_VehicleInterval).ToString("F0", CultureInfo.InvariantCulture)}s (planned, not measured; target {(line.m_TargetInterval).ToString("F0", CultureInfo.InvariantCulture)}s), " +
                    $"routerWait={(line.ExpectedWait).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                    $"vehicles={(line.m_Vehicles).ToString(CultureInfo.InvariantCulture)} ({(line.CapacityPerVehicle).ToString(CultureInfo.InvariantCulture)} seats each), " +
                    $"aboard={(line.m_Passengers).ToString(CultureInfo.InvariantCulture)}/{(line.m_Capacity).ToString(CultureInfo.InvariantCulture)}, " +
                    $"window({(line.m_WindowSamples).ToString(CultureInfo.InvariantCulture)} active of {(line.m_WindowReadings).ToString(CultureInfo.InvariantCulture)} readings over " +
                    $"{(line.m_WindowGameHours).ToString("F1", CultureInfo.InvariantCulture)}h: mean {((line.m_WindowUsage * 100f)).ToString("F0", CultureInfo.InvariantCulture)}%, " +
                    $"peak {((line.m_WindowPeakUsage * 100f)).ToString("F0", CultureInfo.InvariantCulture)}%, planning load {(line.m_WindowPlanningLoad).ToString(CultureInfo.InvariantCulture)} riders, max {(line.m_WindowMaxAboard).ToString(CultureInfo.InvariantCulture)}), " +
                    $"judgedOn={(line.HasWindow ? "window" : "this reading only")}, " +
                    $"demand={(line.HasDemand ? $"{(line.m_RidersPerDay).ToString("F0", CultureInfo.InvariantCulture)} riders/day (day {(line.m_RidersByDay).ToString("F0", CultureInfo.InvariantCulture)}, night {(line.m_RidersByNight).ToString("F0", CultureInfo.InvariantCulture)})" : "no route pass yet")}, " +
                    $"schedule={line.m_Schedule}, day {((line.m_DayUsage * 100f)).ToString("F0", CultureInfo.InvariantCulture)}% over {(line.m_DaySamples).ToString(CultureInfo.InvariantCulture)} readings, " +
                    $"night {((line.m_NightUsage * 100f)).ToString("F0", CultureInfo.InvariantCulture)}% over {(line.m_NightSamples).ToString(CultureInfo.InvariantCulture)} readings, " +
                    $"flags(require={line.m_RequireVehicles}, notEnough={line.m_NotEnoughVehicles})");
            }

            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                LineHealth entry = m_LineHealth[i];
                DeferredLog.Info(
                    $"Line \"{entry.m_Name}\" ({entry.m_Mode}): {LineHealthRules.Describe(entry)} — " +
                    $"plan {entry.m_RecommendedMode} × {(entry.m_RecommendedFleet).ToString(CultureInfo.InvariantCulture)} of [{(entry.m_FleetMin).ToString(CultureInfo.InvariantCulture)}, {(entry.m_FleetMax == int.MaxValue ? "?" : entry.m_FleetMax.ToString(CultureInfo.InvariantCulture))}] " +
                    $"(round trip {(entry.m_RoundTripSeconds).ToString("F0", CultureInfo.InvariantCulture)}s -> interval {(entry.m_HeadwaySeconds).ToString("F0", CultureInfo.InvariantCulture)}s), " +
                    $"game target {(entry.m_TargetVehicles).ToString(CultureInfo.InvariantCulture)} vehicles, " +
                    $"utilisation {(entry.HasDemand ? (entry.m_Utilisation * 100f).ToString("F1", CultureInfo.InvariantCulture) + " %" : "n/a")} " +
                    $"(day {(entry.m_DayUtilisation * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, night {(entry.m_NightUtilisation * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, advice {entry.m_ScheduleAdvice}), " +
                    $"occupancy mean {(entry.m_Usage * 100f).ToString("F1", CultureInfo.InvariantCulture)} % peak {(entry.m_PeakUsage * 100f).ToString("F1", CultureInfo.InvariantCulture)} %");
            }

            DeferredLog.Info(
                $"Line health reference at frame {(frame).ToString(CultureInfo.InvariantCulture)}: medianOccupancy={(m_HealthReference.m_MedianUsage * 100f).ToString("F1", CultureInfo.InvariantCulture)}%, " +
                $"emptyBelow={(m_HealthReference.m_EmptyThreshold * 100f).ToString("F1", CultureInfo.InvariantCulture)}% (peak under {(m_HealthReference.m_EmptyThreshold * Assumptions.EmptyPeakAllowance * 100f).ToString("F1", CultureInfo.InvariantCulture)}%); " +
                $"window tracks {(m_LineHistory.TrackedLines).ToString(CultureInfo.InvariantCulture)} lines over {(LineHistory.GameHours(m_LineHistory.WindowFrames)).ToString("F0", CultureInfo.InvariantCulture)} game hours, " +
                $"a reading every {(Assumptions.ReadingIntervalFrames).ToString(CultureInfo.InvariantCulture)} frames, {(Assumptions.MinReadingsForVerdict).ToString(CultureInfo.InvariantCulture)} active readings before a verdict uses it, " +
                $"evicted={(m_LineHistory.EvictedSinceLastReport).ToString(CultureInfo.InvariantCulture)}, droppedAtCap={(m_LineHistory.DroppedAtCapSinceLastReport).ToString(CultureInfo.InvariantCulture)}");
            m_LineHistory.ClearCounters();
        }

        // The widest coverage any line has, which is what the oldest reading in the
        // window buys us. Lines added later have less and say so on their own row.
        private void UpdateDataCoverage()
        {
            float coveredHours = 0f;
            int readings = 0;
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                coveredHours = math.max(coveredHours, line.m_WindowGameHours);
                readings = math.max(readings, line.m_WindowReadings);
            }

            s_DataCoverage = PanelPayload.DataCoverageRow(
                coveredHours, readings, LineHistory.GameHours(m_LineHistory.WindowFrames),
                m_TripObserver.Window.Count, LineHistory.GameHours(m_TripObserver.Window.SpanFrames));
        }

        // Answers the panel's request immediately from the cached health: the plan is
        // the verdict's own numbers, so there is nothing to wait for.
        private void HandleImprovementRequest()
        {
            if (s_ImproveRequest < 0 || m_LineHealth.Count == 0)
            {
                return;
            }

            int requested = s_ImproveRequest;
            s_ImproveRequest = -1;

            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                LineHealth health = m_LineHealth[i];
                if (health.m_Id != requested)
                {
                    continue;
                }

                // Matched by identity, not by position. The query returns lines in
                // whatever chunk order the ECS happens to have them in, so a positional
                // lookup silently pointed at a different line after a refresh — which
                // is why an improvement plan could end up sitting under the wrong name.
                ExistingLine? line = null;
                for (int k = 0; k < m_ExistingLines.Count; k++)
                {
                    if (m_ExistingLines[k].m_Id == health.m_Id)
                    {
                        line = m_ExistingLines[k];
                        break;
                    }
                }

                if (line is null)
                {
                    DeferredLog.Info($"Improvement requested for line id {(requested).ToString(CultureInfo.InvariantCulture)}, which no longer exists.");
                    return;
                }

                LineHealthRules.ImprovePlan plan = LineHealthRules.Plan(health);
                s_ImprovePlan = LineHealthRules.PlanPayload(plan);
                s_ImprovedLine = health.m_Id;

                BuildImprovedRoute(health, line);

                DeferredLog.Info(
                    $"Improvement for \"{health.m_Name}\" ({health.m_Mode}): {LineHealthRules.Improve(health)} " +
                    $"[measured: {(health.m_Passengers).ToString(CultureInfo.InvariantCulture)}/{(health.m_Capacity).ToString(CultureInfo.InvariantCulture)} aboard, {(health.m_Vehicles).ToString(CultureInfo.InvariantCulture)}/{(health.m_TargetVehicles).ToString(CultureInfo.InvariantCulture)} veh, " +
                    $"planning load {(health.m_PlanningLoad).ToString(CultureInfo.InvariantCulture)} riders, {(health.m_Stops).ToString(CultureInfo.InvariantCulture)} stops, {(health.m_LengthKm).ToString("F1", CultureInfo.InvariantCulture)} km, " +
                    $"roundTrip {(line.m_StableDurationSeconds).ToString("F0", CultureInfo.InvariantCulture)}s, targetInterval {(line.m_TargetInterval).ToString("F0", CultureInfo.InvariantCulture)}s]");
                return;
            }
        }

        // Re-traces the line between its own endpoints using current demand, then
        // re-spaces its stops for the recommended mode. This is the improved routing
        // the plan talks about, made visible rather than merely described.
        private void BuildImprovedRoute(LineHealth health, ExistingLine line)
        {
            m_ImprovedRoute = null;
            s_ImprovedRouteDrawn = false;
            if (line.m_StopIndices.Count < 2)
            {
                return;
            }

            int firstStop = line.m_StopIndices[0];
            int lastStop = line.m_StopIndices[line.m_StopIndices.Count - 1];
            // Both ends of the range, as every other stop-index guard in this file
            // checks: a negative index reads off the front of the list just as surely.
            if (firstStop < 0 || lastStop < 0
                || firstStop >= m_TransitStops.Count || lastStop >= m_TransitStops.Count)
            {
                return;
            }

            ModePreset mode = health.m_RecommendedMode;

            // Re-trace on the network the recommended mode can actually use. Tracing a
            // train on the road graph reported "no road path between its endpoints" and
            // produced nothing at all.
            AlignmentNetwork graph = NetworkForMode(mode);
            if (graph?.Graph is null)
            {
                DeferredLog.Info($"Improved route for \"{health.m_Name}\": no {mode} network is built yet.");
                return;
            }

            var scratch = new List<int>();
            int from = graph.NearestNode(m_TransitStops[firstStop], Assumptions.ReplanSnapMetres);
            int to = graph.NearestNode(m_TransitStops[lastStop], Assumptions.ReplanSnapMetres);
            if (from < 0 || to < 0 || !graph.TracePath(from, to, Assumptions.ReplanMaxPathMetres, scratch))
            {
                DeferredLog.Info(
                    $"Improved route for \"{health.m_Name}\": no {graph.Network} path between its endpoints " +
                    $"(fromNode={(from).ToString(CultureInfo.InvariantCulture)}, toNode={(to).ToString(CultureInfo.InvariantCulture)}).");
                return;
            }

            var route = new SuggestedRoute { Network = graph.Network, Mode = mode, Source = graph };
            route.Nodes.AddRange(scratch);
            graph.MaterialisePath(scratch, route.Path);

            float length = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                length += float2Like.Distance(route.Path[i - 1], route.Path[i]);
            }

            route.Length = length;
            route.CapturedFlow = graph.FlowAlong(scratch);
            Routes.Restop(route, mode, BuildStopContext());
            route.Vehicles = health.m_RecommendedFleet;
            route.HeadwaySeconds = health.m_HeadwaySeconds;
            route.FleetMin = health.m_FleetMin;
            route.FleetMax = health.m_FleetMax;
            route.RoundTripSeconds = health.m_RoundTripSeconds;

            m_ImprovedRoute = route.Stops.Count >= 2 ? route : null;
            s_ImprovedRouteDrawn = m_ImprovedRoute is not null;

            DeferredLog.Info(
                $"Improved route for \"{health.m_Name}\": {mode}, {(length / 1000f).ToString("F2", CultureInfo.InvariantCulture)} km " +
                $"(was {(health.m_LengthKm).ToString("F2", CultureInfo.InvariantCulture)}), {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops (was {(health.m_Stops).ToString(CultureInfo.InvariantCulture)}), " +
                $"{(route.Vehicles).ToString(CultureInfo.InvariantCulture)} vehicles (was {(health.m_Vehicles).ToString(CultureInfo.InvariantCulture)}), corridorFlow={(route.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}");
        }
    }
}
