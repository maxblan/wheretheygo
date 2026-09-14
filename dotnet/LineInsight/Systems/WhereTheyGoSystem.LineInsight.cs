using System.Collections.Generic;
using System.Globalization;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace WhereTheyGo
{
    // The existing lines: read in game time into the rolling window, collected in
    // travel order, handed the routing's riders, judged (LineWindow), and
    // re-traced on request for the improvement plan.
    public sealed partial class WhereTheyGoSystem
    {
        // Existing transit system: the lines themselves, the routable model of them,
        // and their health.
        private EntityQuery m_LineQuery;

        private readonly List<ExistingLine> m_ExistingLines = new List<ExistingLine>();

        private readonly List<float2Like> m_TransitStops = new List<float2Like>();

        private readonly Dictionary<Entity, int> m_StopIndices = new Dictionary<Entity, int>();

        // The readings the panel is looking at, captured at the refresh so a later
        // reading cannot change what it shows (LineReadings).
        private LineReadings? m_LineReadings;

        // A game day of line readings, one every ReadingIntervalFrames of simulation
        // time. Reading a line off the single sample a refresh happens to land on
        // showed a one-boat ferry as empty whenever its boat was mid-crossing; the
        // window is what the figures rest on instead.
        private readonly LineHistory m_LineHistory = new LineHistory(Assumptions.FramesPerGameDay);

        private readonly HashSet<int> m_LiveLineIds = new HashSet<int>();

        // Line id to its entity, so a line the player clicks can be found again. The
        // id is index|version, which survives the index being reused.
        private readonly Dictionary<int, Entity> m_LineEntities = new Dictionary<int, Entity>();

        // What the last routing attributed to each existing line, by line id: riders a
        // day and their split by period (RoutingResult.BaseRiders).
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

        // Reads the lines and hands them the window and the routing's riders. Cheap —
        // it walks the lines and nothing else.
        //
        // Idempotent within a simulation frame, so the demand pipeline can call it
        // without the background tick making it happen twice.
        private void RefreshLineHealth()
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

            LineReadings problem = LineWindow.Capture(m_ExistingLines, m_LineHistory);
            LineWindow.ApplyWindow(problem);
            m_LineReadings = problem;
            LogLineHealth(frame);
            UpdateDataCoverage();
        }

        // What one line carried in each hour of the day, for the section in its own
        // window. Reads the window the observation keeps; an hour with no readings
        // comes back with no samples.
        internal void ReadHourlyLoad(int lineId, float[] riders, float[] capacity, int[] samples)
        {
            m_LineHistory.HourlyLoad(lineId, riders, capacity, samples);
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
                    $"readFrom={(line.HasWindow ? "window" : "this reading only")}, " +
                    $"demand={(line.HasDemand ? $"{(line.m_RidersPerDay).ToString("F0", CultureInfo.InvariantCulture)} riders/day (day {(line.m_RidersByDay).ToString("F0", CultureInfo.InvariantCulture)}, night {(line.m_RidersByNight).ToString("F0", CultureInfo.InvariantCulture)})" : "not routed yet")}, " +
                    $"schedule={line.m_Schedule}, day {((line.m_DayUsage * 100f)).ToString("F0", CultureInfo.InvariantCulture)}% over {(line.m_DaySamples).ToString(CultureInfo.InvariantCulture)} readings, " +
                    $"night {((line.m_NightUsage * 100f)).ToString("F0", CultureInfo.InvariantCulture)}% over {(line.m_NightSamples).ToString(CultureInfo.InvariantCulture)} readings, " +
                    $"flags(require={line.m_RequireVehicles}, notEnough={line.m_NotEnoughVehicles})");
            }

            DeferredLog.Info(
                $"Line readings at frame {(frame).ToString(CultureInfo.InvariantCulture)}: " +
                $"window tracks {(m_LineHistory.TrackedLines).ToString(CultureInfo.InvariantCulture)} lines over {(LineHistory.GameHours(m_LineHistory.WindowFrames)).ToString("F0", CultureInfo.InvariantCulture)} game hours, " +
                $"a reading every {(Assumptions.ReadingIntervalFrames).ToString(CultureInfo.InvariantCulture)} frames, {(Assumptions.MinReadingsForVerdict).ToString(CultureInfo.InvariantCulture)} active readings before the window is used, " +
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

    }
}
