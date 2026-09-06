using System.Collections.Generic;
using System.Globalization;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // The existing lines: read in travel order, folded into the rolling window, judged,
    // and re-traced on request for the improvement plan.
    public sealed partial class StationSuitabilityOverlaySystem
    {
        // Existing transit system: the lines themselves, the routable model of them,
        // and their health.
        private EntityQuery m_LineQuery;

        private readonly List<ExistingLine> m_ExistingLines = new List<ExistingLine>();

        private readonly List<float2Like> m_TransitStops = new List<float2Like>();

        private readonly Dictionary<Entity, int> m_StopIndices = new Dictionary<Entity, int>();

        private readonly List<LineHealth> m_LineHealth = new List<LineHealth>();

        private SuggestedRoute? m_ImprovedRoute;

        // A game day of line readings. Judging a line on the single reading a refresh
        // happens to land on condemned a one-boat ferry as empty whenever its boat was
        // mid-crossing; the window is what a verdict rests on instead.
        private readonly LineHistory m_LineHistory = new LineHistory(LineHistory.FramesPerGameDay);

        private readonly HashSet<int> m_LiveLineIds = new HashSet<int>();

        private uint m_LastHistoryFrame;

        private uint m_LastLineRefreshFrame;

        private float m_LastLineSample;

        // Expected rider wait in seconds at a stop position, taken from the best line
        // that actually calls there — the same windowed headway the verdicts and the
        // transit router use. Zero when no collected line has a stop within reach,
        // which tells the calibration there is nothing honest to record here.
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

                    if (float2Like.DistanceSq(m_TransitStops[index], new float2Like(position.x, position.y)) > StopMatchRadiusSq)
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

        // Reads the lines, folds this reading into the rolling window and re-judges
        // them. Cheap next to the route pipeline — it walks the lines and nothing else
        // — which is what lets it run on its own regardless of what is on screen.
        //
        // Idempotent within a simulation frame, so the route pipeline can call it
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
            SuitabilityLines.Collect(EntityManager, m_LineQuery, m_PrefabSystem, m_NameSystem,
                m_ExistingLines, m_TransitStops, m_StopIndices);
            RecordLineWindow();
            SuitabilityLines.Judge(m_ExistingLines, m_LineHealth);
            UpdateLineHealthText();
        }

        // Folds this collection's readings into the rolling window and hands each line
        // back its own averages.
        //
        // Stamped with SimulationSystem.frameIndex rather than the wall clock, because
        // the window is 24 GAME hours: frames stop while the game is paused and run
        // faster when the player fast forwards, and a real-time window would drain
        // itself in the pause menu and cover a quarter of a day at 4x.
        private void RecordLineWindow()
        {
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation is null)
            {
                return;
            }

            uint frame = simulation.frameIndex;

            // A paused game keeps handing back the same frame. Recording it repeatedly
            // would stack identical readings and let a pause decide the average.
            bool advanced = frame != m_LastHistoryFrame;
            if (advanced)
            {
                m_LastHistoryFrame = frame;
                m_LiveLineIds.Clear();
                for (int i = 0; i < m_ExistingLines.Count; i++)
                {
                    ExistingLine line = m_ExistingLines[i];
                    _ = m_LiveLineIds.Add(line.m_Id);
                    m_LineHistory.Record(line.m_Id, new LineObservation
                    {
                        m_Frame = frame,
                        m_Passengers = line.m_Passengers,
                        m_Capacity = line.m_Capacity,
                        m_IntervalSeconds = line.m_VehicleInterval,
                        m_Vehicles = line.m_Vehicles,
                        m_TimeOfDay = TimeOfDay,
                    });
                }

                m_LineHistory.RetainOnly(m_LiveLineIds);
            }

            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                if (!m_LineHistory.TryAverage(line.m_Id, out LineAverage average))
                {
                    line.m_WindowSamples = 0;
                    continue;
                }

                line.m_WindowUsage = average.m_Usage;
                line.m_WindowPeakUsage = average.m_PeakUsage;
                line.m_WindowInterval = average.m_IntervalSeconds;
                line.m_WindowSamples = average.m_Samples;
                line.m_WindowGameHours = LineHistory.GameHours(average.m_SpanFrames);
                line.m_DayUsage = m_LineHistory.TryAveragePeriod(line.m_Id, night: false, out LineAverage day) ? day.m_Usage : 0f;
                line.m_DaySamples = day.m_Samples;
                line.m_NightUsage = m_LineHistory.TryAveragePeriod(line.m_Id, night: true, out LineAverage nightAverage) ? nightAverage.m_Usage : 0f;
                line.m_NightSamples = nightAverage.m_Samples;
            }

            // The widest coverage any line has, which is what the oldest reading in the
            // window buys us. Lines added later have less and say so on their own row.
            float coveredHours = 0f;
            int readings = 0;
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                coveredHours = math.max(coveredHours, line.m_WindowGameHours);
                readings = math.max(readings, line.m_WindowSamples);
            }

            s_DataCoverage = SuitabilityPanelPayload.DataCoverageRow(
                coveredHours, readings, LineHistory.GameHours(m_LineHistory.WindowFrames),
                m_TripObserver.Window.Count, LineHistory.GameHours(m_TripObserver.Window.SpanFrames));

            DeferredLog.Info(
                $"Line window: frame={(frame).ToString(CultureInfo.InvariantCulture)} (advanced={advanced}), " +
                $"tracking {(m_LineHistory.TrackedLines).ToString(CultureInfo.InvariantCulture)} lines over " +
                $"{(LineHistory.GameHours(m_LineHistory.WindowFrames)).ToString("F0", CultureInfo.InvariantCulture)} game hours, " +
                $"{(LineHistory.MinSamplesForVerdict).ToString(CultureInfo.InvariantCulture)} readings needed before a verdict uses it, " +
                $"evicted={(m_LineHistory.EvictedSinceLastReport).ToString(CultureInfo.InvariantCulture)}, " +
                $"droppedAtCap={(m_LineHistory.DroppedAtCapSinceLastReport).ToString(CultureInfo.InvariantCulture)}");
            m_LineHistory.ClearCounters();
        }

        // Builds the improvement plan for whichever line the panel asked about. Run
        // here rather than in the binding so it uses the same measurements the list
        // was built from.
        // Answers the panel's request immediately. It reads only the cached health and
        // line data, so there is no reason to make the player wait for the next demand
        // refresh — which is what made the button feel broken.
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
                int perVehicle = health.m_Vehicles > 0 ? health.m_Capacity / health.m_Vehicles : health.m_Capacity;

                // Aim to fill about 70%: full enough to justify the service, with room
                // for the peaks the averages hide.
                SuitabilityLineHealth.ImprovePlan plan = SuitabilityLineHealth.Plan(
                    health, line.m_LengthMetres, line.m_StableDurationSeconds, perVehicle, 0.7f);
                s_ImprovePlan = SuitabilityLineHealth.PlanPayload(plan);
                s_ImprovedLine = health.m_Id;

                BuildImprovedRoute(health, line);

                DeferredLog.Info(
                    $"Improvement for \"{health.m_Name}\" ({health.m_Mode}): " +
                    $"{SuitabilityLineHealth.Improve(health, line.m_LengthMetres, line.m_StableDurationSeconds, perVehicle, 0.7f)} " +
                    $"[measured: {(health.m_Passengers).ToString(CultureInfo.InvariantCulture)}/{(health.m_Capacity).ToString(CultureInfo.InvariantCulture)} aboard, {(health.m_Vehicles).ToString(CultureInfo.InvariantCulture)}/{(health.m_TargetVehicles).ToString(CultureInfo.InvariantCulture)} veh, " +
                    $"typicalWait {(health.m_TypicalWait).ToString("F0", CultureInfo.InvariantCulture)}, {(health.m_Stops).ToString(CultureInfo.InvariantCulture)} stops, {(health.m_LengthKm).ToString("F1", CultureInfo.InvariantCulture)} km, " +
                    $"perVehicle {(perVehicle).ToString(CultureInfo.InvariantCulture)}, roundTrip {(line.m_StableDurationSeconds).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                    $"targetInterval {(line.m_TargetInterval).ToString("F0", CultureInfo.InvariantCulture)}s]");
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

            ModePreset mode = health.m_Verdict == LineVerdict.AtModeCapacity
                ? TransitModes.NextModeUp(health.m_Mode)
                : health.m_Verdict == LineVerdict.NearlyEmpty
                    ? TransitModes.NextModeDown(health.m_Mode)
                    : health.m_Mode;

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
            int from = graph.NearestNode(m_TransitStops[firstStop], ReplanSnapMetres);
            int to = graph.NearestNode(m_TransitStops[lastStop], ReplanSnapMetres);
            if (from < 0 || to < 0 || !graph.TracePath(from, to, ReplanMaxPathMetres, scratch))
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
            FleetFacts facts = ReadFleetFacts();
            SuitabilityRoutes.Restop(route, mode, BuildStopContext());
            route.Vehicles = RoadVehicles(route, facts.HeadwayFor(mode), facts.DelayPerStopSeconds(mode));

            m_ImprovedRoute = route.Stops.Count >= 2 ? route : null;
            s_ImprovedRouteDrawn = m_ImprovedRoute is not null;

            DeferredLog.Info(
                $"Improved route for \"{health.m_Name}\": {mode}, {(length / 1000f).ToString("F2", CultureInfo.InvariantCulture)} km " +
                $"(was {(health.m_LengthKm).ToString("F2", CultureInfo.InvariantCulture)}), {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops (was {(health.m_Stops).ToString(CultureInfo.InvariantCulture)}), " +
                $"{(route.Vehicles).ToString(CultureInfo.InvariantCulture)} vehicles (was {(health.m_Vehicles).ToString(CultureInfo.InvariantCulture)}), corridorFlow={(route.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}");
        }

        // How far an existing line's endpoint may be from a node when re-planning it,
        // and how long the replacement path may be.
        private const float ReplanSnapMetres = 600f;

        private const float ReplanMaxPathMetres = 60000f;

        // Candidates transfer-scored per requested route. Four networks each grow up
        // to RouteCount * 4, so this covers all of them rather than only the network
        // whose corridors happen to carry the most flow per edge.
        //
        // It has to rise with that budget. Candidates enter scoring sorted by corridor
        // flow, and enabled demand does not follow flow at all — the best candidate in
        // one refresh carried a corridor flow of 63 and unlocked more journeys than
        // anything else in the run. A scoring window narrower than the candidate set
        // would drop exactly that kind of line, unscored, to the bottom of a ranking
        // led by the number it never got.
        // How close a sampled stop entity has to be to a collected line's stop to be
        // the same stop. Generous, because the two come from different game components
        // and their positions need not agree exactly.
        private const float StopMatchRadiusSq = 40f * 40f;
    }
}
