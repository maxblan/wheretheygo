using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Entities;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // Writes the offline verification pipeline a canonical instance of the CURRENT
    // city (verification/README.md). Read-only: nothing here changes what the mod
    // computes, and no scoring rule lives on this side — the instance carries the
    // job's own inputs and outputs so verification/ can recompute them independently.
    //
    // The inputs are captured inside StartCompute, at the moment the Burst job is
    // handed them, and the outputs are taken from the very terms that job produced.
    // Re-collecting them at export time would have been simpler and wrong: the
    // collections are rebuilt on their own timers, so an export could otherwise
    // describe a city the exported terms were never computed from.
    public sealed partial class StationSuitabilityOverlaySystem
    {
        private const int ExportSchemaVersion = 1;
        // Roughly how many cells each of the two sampling passes contributes. The
        // whole grid would be a quarter-million cells per term; the pipeline only
        // needs enough of them to catch a wrong formula, and the indices it got are
        // written into the instance so it never has to guess the rule.
        private const int ExportSampleTarget = 1024;

        private static bool s_ExportRequested;

        // The capture is a two-stage handshake across a job boundary: the request
        // arms it, StartCompute fills it, FinishComputeIfReady writes it out.
        private ExportCapture? m_ExportCapture;
        private bool m_ExportArmed;

        // Options-page button. The export runs on the next compute, not here, so the
        // instance is a consistent input/output pair rather than a mixture.
        public static void RequestVerificationExport() => s_ExportRequested = true;

        // What the access pass was given, taken at StartCompute from the very arrays
        // handed to the worker, plus the masks and the mode the map is built for.
        private sealed class ExportCapture
        {
            public WalkAccessInputs m_Inputs = new WalkAccessInputs();
            public int2 m_Grid;
            public float2 m_WorldMin;
            public byte[] m_Buildable = Array.Empty<byte>();
            public byte[] m_Land = Array.Empty<byte>();
            public ModePreset m_Mode;
            public int m_Class;
            public int m_SelfType;
            public float[] m_TypeWeight = Array.Empty<float>();
        }

        // Called from StartCompute with exactly the values handed to the worker.
        private void CaptureExportInputs(
            WalkAccessInputs inputs, int2 grid, float2 worldMin, byte[] buildable,
            ModePreset mode, int cls, int selfType, float[] typeWeight)
        {
            if (!m_ExportArmed)
            {
                return;
            }

            m_ExportCapture = new ExportCapture
            {
                m_Inputs = inputs,
                m_Grid = grid,
                m_WorldMin = worldMin,
                m_Buildable = buildable,
                m_Land = m_Land is null ? Array.Empty<byte>() : (byte[])m_Land.Clone(),
                m_Mode = mode,
                m_Class = cls,
                m_SelfType = selfType,
                m_TypeWeight = typeWeight,
            };
        }

        private void HandleExportRequest()
        {
            if (!s_ExportRequested)
            {
                return;
            }

            s_ExportRequested = false;
            m_ExportArmed = true;
            m_ExportCapture = null;
            // Force a fresh compute so the capture below has a job to ride along with.
            ScheduleRecompute(0f);
            Mod.Log.Info("Verification export requested; it will be written after the next recompute.");
        }

        // Called from FinishComputeIfReady, after the terms have landed in m_RawTerms
        // and the combine pass has produced m_Scores.
        private void WriteExportIfCaptured()
        {
            ExportCapture? capture = m_ExportCapture;
            if (!m_ExportArmed || capture is null)
            {
                return;
            }

            m_ExportArmed = false;
            m_ExportCapture = null;

            WalkAccessOutput? access = m_Access;
            var settings = Mod.Settings;
            if (access is null || settings is null)
            {
                Mod.Log.Warn("Verification export skipped: no computed terms.");
                return;
            }

            try
            {
                string folder = ExportFolder();
                _ = Directory.CreateDirectory(folder);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                string city = ExportCityName();

                WriteInstance(folder, $"real-{city}-{stamp}-heatmap",
                    BuildHeatmapInstance(capture, access, $"real-{city}-{stamp}-heatmap"));
                WriteInstance(folder, $"real-{city}-{stamp}-sites",
                    BuildSitesInstance(settings, $"real-{city}-{stamp}-sites"));
                string? lineset = BuildLinesetInstance(settings, $"real-{city}-{stamp}-lineset");
                if (lineset is not null)
                {
                    WriteInstance(folder, $"real-{city}-{stamp}-lineset", lineset);
                }

                string? roads = BuildRoadTimesInstance($"real-{city}-{stamp}-roads");
                if (roads is not null)
                {
                    WriteInstance(folder, $"real-{city}-{stamp}-roads", roads);
                }

                string? coverage = BuildCoverageInstance($"real-{city}-{stamp}-coverage");
                if (coverage is not null)
                {
                    WriteInstance(folder, $"real-{city}-{stamp}-coverage", coverage);
                }

                string? stops = BuildStopPlanInstance($"real-{city}-{stamp}-stops");
                if (stops is not null)
                {
                    WriteInstance(folder, $"real-{city}-{stamp}-stops", stops);
                }

                Mod.Log.Info(
                    $"Verification export written to {folder} " +
                    $"(grid {(capture.m_Grid.x).ToString(CultureInfo.InvariantCulture)}x{(capture.m_Grid.y).ToString(CultureInfo.InvariantCulture)}, " +
                    $"zoneFlows={(m_ZoneFlows.Count).ToString(CultureInfo.InvariantCulture)}, " +
                    $"candidates={(m_RouteCandidates.Count).ToString(CultureInfo.InvariantCulture)}). " +
                    "Copy the files into verification/instances/ and run make -C verification verify-all.");
            }
            catch (IOException e)
            {
                // Exporting is a diagnostic convenience; a full disk or a locked file
                // must never take the overlay down with it. Only the write can
                // legitimately fail — anything else is a defect and stays unhandled.
                Mod.Log.Warn($"Verification export failed: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                Mod.Log.Warn($"Verification export failed, folder not writable: {e.Message}");
            }
        }

        // Deliberately Unity's own path rather than Colossal's EnvPath.kUserDataPath:
        // the two resolve to the same folder — %LOCALAPPDATA%Low\Colossal Order\Cities
        // Skylines II, the one holding Logs and ModsData — and this one needs no
        // assembly reference the mod does not already carry.
        private static string ExportFolder()
        {
            return Path.Combine(
                UnityEngine.Application.persistentDataPath,
                "ModsData",
                nameof(StationSuitabilityOverlay),
                "verification");
        }

        private string ExportCityName()
        {
            var configuration = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
            string city = configuration?.cityName ?? "city";
            var safe = new StringBuilder(city.Length);
            for (int i = 0; i < city.Length; i++)
            {
                char c = city[i];
                bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
                _ = safe.Append(ok ? c : '-');
            }

            string result = safe.ToString().Trim('-');
            return result.Length == 0 ? "city" : result;
        }

        private static void WriteInstance(string folder, string name, string json)
        {
            string path = Path.Combine(folder, name + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // Two passes so both halves of the map are represented: a stride over every
        // cell (which catches the unbuildable gate) and a stride over the buildable
        // ones (where the terms actually have work to do).
        private static List<int> SelectSampleCells(byte[] buildable, int totalCells)
        {
            var picked = new SortedSet<int>();
            int strideAll = Math.Max(1, totalCells / ExportSampleTarget);
            for (int i = 0; i < totalCells; i += strideAll)
            {
                _ = picked.Add(i);
            }

            int buildableCount = 0;
            for (int i = 0; i < totalCells && i < buildable.Length; i++)
            {
                if (buildable[i] != 0)
                {
                    buildableCount++;
                }
            }

            int strideBuildable = Math.Max(1, buildableCount / ExportSampleTarget);
            int seen = 0;
            for (int i = 0; i < totalCells && i < buildable.Length; i++)
            {
                if (buildable[i] == 0)
                {
                    continue;
                }

                if (seen % strideBuildable == 0)
                {
                    _ = picked.Add(i);
                }

                seen++;
            }

            return new List<int>(picked);
        }

        private static string SourcesJson(WalkSources sources)
        {
            return new SuitabilityJsonObject()
                .Add("x_b32", SuitabilityExportJson.BitsArray(Prefix(sources.X, sources.Count)))
                .Add("z_b32", SuitabilityExportJson.BitsArray(Prefix(sources.Z, sources.Count)))
                .Add("w_b32", SuitabilityExportJson.BitsArray(Prefix(sources.Weight, sources.Count)))
                .Build();
        }

        private static float[] Prefix(float[] values, int count)
        {
            var prefix = new float[count];
            Array.Copy(values, prefix, count);
            return prefix;
        }

        // The `heatmap_walk` instance: the access pass's inputs verbatim, the masks,
        // and a sample of the terms it produced — so the pipeline can run the same
        // pure code on them (subject) and an independent re-derivation (evaluator) and
        // require both to reproduce the game's numbers bit for bit.
        private static string BuildHeatmapInstance(ExportCapture capture, WalkAccessOutput access, string name)
        {
            int totalCells = Math.Min(access.Terms.Length, capture.m_Buildable.Length);
            List<int> sample = SelectSampleCells(capture.m_Buildable, totalCells);
            var demand = new float[sample.Count];
            var jobs = new float[sample.Count];
            var coverage = new float[sample.Count];
            var accessTerm = new float[sample.Count];
            var future = new float[sample.Count];
            var interchange = new float[sample.Count];
            var cross = new float[sample.Count];
            var node = new int[sample.Count];
            var walk = new int[sample.Count];
            for (int i = 0; i < sample.Count; i++)
            {
                SuitabilityCell cell = access.Terms[sample[i]];
                demand[i] = cell.m_Demand;
                jobs[i] = cell.m_Jobs;
                coverage[i] = cell.m_Coverage;
                accessTerm[i] = cell.m_Access;
                future[i] = cell.m_Future;
                interchange[i] = cell.m_Interchange;
                cross[i] = cell.m_CrossCoverage;
                node[i] = access.TileNode[sample[i]];
                walk[i] = access.TileWalkMs[sample[i]];
            }

            WalkAccessInputs inputs = capture.m_Inputs;
            var data = new SuitabilityJsonObject()
                .Add("grid_x", SuitabilityExportJson.Int(capture.m_Grid.x))
                .Add("grid_y", SuitabilityExportJson.Int(capture.m_Grid.y))
                .Add("world_min_x_b32", SuitabilityExportJson.Bits(capture.m_WorldMin.x))
                .Add("world_min_z_b32", SuitabilityExportJson.Bits(capture.m_WorldMin.y))
                .Add("tile_size_b32", SuitabilityExportJson.Bits(Assumptions.TileSize))
                .Add("node_x_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeX))
                .Add("node_z_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeZ))
                .Add("node_siteable", SuitabilityExportJson.BoolArray(inputs.Graph.Siteable))
                .Add("edge_a", SuitabilityExportJson.IntArray(inputs.Graph.EdgeA))
                .Add("edge_b", SuitabilityExportJson.IntArray(inputs.Graph.EdgeB))
                .Add("edge_metres_b32", SuitabilityExportJson.BitsArray(inputs.Graph.EdgeMetres))
                .Add("edge_ms", SuitabilityExportJson.IntArray(inputs.Graph.EdgeMs))
                .Add("homes", SourcesJson(inputs.Homes))
                .Add("jobs", SourcesJson(inputs.Jobs))
                .Add("future", SourcesJson(inputs.Future))
                .Add("stop_x_b32", SuitabilityExportJson.BitsArray(Prefix(inputs.StopX, inputs.StopCount)))
                .Add("stop_z_b32", SuitabilityExportJson.BitsArray(Prefix(inputs.StopZ, inputs.StopCount)))
                .Add("stop_type", SuitabilityExportJson.IntArray(inputs.StopType))
                .Add("type_count", SuitabilityExportJson.Int(inputs.TypeCount))
                .Add("access_ms", SuitabilityExportJson.Int(inputs.AccessMs))
                .Add("transfer_ms", SuitabilityExportJson.Int(inputs.TransferMs))
                .Add("catchment_ms", SuitabilityExportJson.IntArray(inputs.CatchmentMs))
                .Add("type_weight_b32", SuitabilityExportJson.BitsArray(capture.m_TypeWeight))
                .Add("mode", SuitabilityExportJson.Str(capture.m_Mode.ToString()))
                .Add("class", SuitabilityExportJson.Int(capture.m_Class))
                .Add("self_type", SuitabilityExportJson.Int(capture.m_SelfType))
                .Add("buildable", MaskJson(capture.m_Buildable))
                .Add("land", MaskJson(capture.m_Land))
                .Add("sample_indices", SuitabilityExportJson.IntArray(sample))
                .Add("sample_demand_b32", SuitabilityExportJson.BitsArray(demand))
                .Add("sample_jobs_b32", SuitabilityExportJson.BitsArray(jobs))
                .Add("sample_coverage_b32", SuitabilityExportJson.BitsArray(coverage))
                .Add("sample_access_b32", SuitabilityExportJson.BitsArray(accessTerm))
                .Add("sample_future_b32", SuitabilityExportJson.BitsArray(future))
                .Add("sample_interchange_b32", SuitabilityExportJson.BitsArray(interchange))
                .Add("sample_cross_b32", SuitabilityExportJson.BitsArray(cross))
                .Add("sample_tile_node", SuitabilityExportJson.IntArray(node))
                .Add("sample_tile_walk_ms", SuitabilityExportJson.IntArray(walk))
                .Build();

            return new SuitabilityJsonObject()
                .Add("kind", SuitabilityExportJson.Str("heatmap_walk"))
                .Add("name", SuitabilityExportJson.Str(name))
                .Add("data", data)
                .BuildHashed(ExportSchemaVersion);
        }

        // Serves both the buildable and the land mask, which is why it is named for
        // neither.
        private static string MaskJson(byte[] mask)
        {
            var values = new int[mask.Length];
            for (int i = 0; i < mask.Length; i++)
            {
                values[i] = mask[i];
            }

            return SuitabilityExportJson.IntArray(values);
        }

        // The site-selection instance: the pedestrian graph, the candidate nodes with
        // the scores the exact selection ran on, and its spacing — the `sites_walk`
        // kind, whose certified optimum runs on the real candidate set.
        private string BuildSitesInstance(Setting settings, string name)
        {
            WalkAccessInputs? inputs = m_AccessInputs;
            WalkGraph graph = inputs?.Graph ?? WalkGraph.Build(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<float>(), 0);
            int wanted = Math.Min(settings.SiteCount, m_SiteIndices.Length);
            var nodes = new int[m_SiteCandidateCount];
            var scores = new float[m_SiteCandidateCount];
            Array.Copy(m_SiteCandidateNodes, nodes, m_SiteCandidateCount);
            Array.Copy(m_SiteCandidateScores, scores, m_SiteCandidateCount);

            var data = new SuitabilityJsonObject()
                .Add("node_x_b32", SuitabilityExportJson.BitsArray(graph.NodeX))
                .Add("node_z_b32", SuitabilityExportJson.BitsArray(graph.NodeZ))
                .Add("edge_a", SuitabilityExportJson.IntArray(graph.EdgeA))
                .Add("edge_b", SuitabilityExportJson.IntArray(graph.EdgeB))
                .Add("edge_metres_b32", SuitabilityExportJson.BitsArray(graph.EdgeMetres))
                .Add("candidate_nodes", SuitabilityExportJson.IntArray(nodes))
                .Add("candidate_scores_b32", SuitabilityExportJson.BitsArray(scores))
                .Add("separation_ms", SuitabilityExportJson.Int(m_SiteSeparationMs))
                .Add("max_sites", SuitabilityExportJson.Int(wanted))
                .Build();

            return new SuitabilityJsonObject()
                .Add("kind", SuitabilityExportJson.Str("sites_walk"))
                .Add("name", SuitabilityExportJson.Str(name))
                .Add("comment", SuitabilityExportJson.Str(
                    "exported from a live city; the MIP is large, so expect a long solve"))
                // One binary per candidate node and a constraint per close pair: the
                // pipeline's default sweep skips this unless it is asked for by name.
                .Add("heavy", SuitabilityExportJson.Bool(value: true))
                .Add("data", data)
                .BuildHashed(ExportSchemaVersion);
        }

        // The equity measure's inputs and answer — the `coverage` kind: pedestrian graph,
        // the served stops as measured, every journey of the last demand refresh, the
        // walking horizon, and the share and Gini the mod reported. Null until measured.
        private string? BuildCoverageInstance(string name)
        {
            WalkAccessInputs? inputs = m_AccessInputs;
            CoverageReport? coverage = m_Coverage;
            if (inputs is null || coverage is null)
            {
                return null;
            }

            int count = m_Journeys.Count;
            var ox = new float[count];
            var oz = new float[count];
            var dx = new float[count];
            var dz = new float[count];
            var w = new float[count];
            for (int i = 0; i < count; i++)
            {
                Trip trip = m_Journeys[i];
                ox[i] = trip.m_Origin.x;
                oz[i] = trip.m_Origin.y;
                dx[i] = trip.m_Destination.x;
                dz[i] = trip.m_Destination.y;
                w[i] = trip.m_Weight;
            }

            var sx = new float[m_TransitStops.Count];
            var sz = new float[m_TransitStops.Count];
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                sx[i] = m_TransitStops[i].x;
                sz[i] = m_TransitStops[i].y;
            }

            var data = new SuitabilityJsonObject()
                .Add("node_x_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeX))
                .Add("node_z_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeZ))
                .Add("edge_a", SuitabilityExportJson.IntArray(inputs.Graph.EdgeA))
                .Add("edge_b", SuitabilityExportJson.IntArray(inputs.Graph.EdgeB))
                .Add("edge_metres_b32", SuitabilityExportJson.BitsArray(inputs.Graph.EdgeMetres))
                .Add("stop_x_b32", SuitabilityExportJson.BitsArray(sx))
                .Add("stop_z_b32", SuitabilityExportJson.BitsArray(sz))
                .Add("trip_ox_b32", SuitabilityExportJson.BitsArray(ox))
                .Add("trip_oz_b32", SuitabilityExportJson.BitsArray(oz))
                .Add("trip_dx_b32", SuitabilityExportJson.BitsArray(dx))
                .Add("trip_dz_b32", SuitabilityExportJson.BitsArray(dz))
                .Add("trip_w_b32", SuitabilityExportJson.BitsArray(w))
                .Add("access_ms", SuitabilityExportJson.Int(inputs.AccessMs))
                .Add("horizon_ms", SuitabilityExportJson.Int(m_EquityHorizonMs))
                .Add("share_b32", SuitabilityExportJson.Bits(coverage.Share))
                .Add("covered_weight", SuitabilityExportJson.Str(coverage.CoveredWeight.ToString("R", CultureInfo.InvariantCulture)))
                .Add("total_weight", SuitabilityExportJson.Str(coverage.TotalWeight.ToString("R", CultureInfo.InvariantCulture)))
                .Add("gini_walk", SuitabilityExportJson.Str(coverage.GiniWalk.ToString("R", CultureInfo.InvariantCulture)))
                .Add("trips_covered", SuitabilityExportJson.Int(coverage.TripsCovered))
                .Add("trips_off_network", SuitabilityExportJson.Int(coverage.TripsOffNetwork))
                .Build();

            return new SuitabilityJsonObject()
                .Add("kind", SuitabilityExportJson.Str("coverage"))
                .Add("name", SuitabilityExportJson.Str(name))
                .Add("data", data)
                .BuildHashed(ExportSchemaVersion);
        }

        // The line-set problem the last route pass solved, with its answer — the
        // `lineset_time` kind: journeys at full weight, the existing network, the
        // resolved candidates with their mode's wait/speed/ride times/headway/capacity,
        // the floors, and the walking network the equity component reads. Null until
        // a route pass has run.
        private string? BuildLinesetInstance(Setting settings, string name)
        {
            LineSetProblem? problem = m_LineSetProblem;
            LineSetSolution? solution = m_LineSetSolution;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (problem is null || solution is null || inputs is null || m_ServedWalkMs is null)
            {
                return null;
            }

            var candidates = new List<string>(problem.Candidates.Count);
            for (int c = 0; c < problem.Candidates.Count; c++)
            {
                LineCandidate line = problem.Candidates[c];
                candidates.Add(new SuitabilityJsonObject()
                    .Add("stop_x_b32", SuitabilityExportJson.BitsArray(line.StopX))
                    .Add("stop_z_b32", SuitabilityExportJson.BitsArray(line.StopZ))
                    .Add("expected_wait_b32", SuitabilityExportJson.Bits(line.ExpectedWait))
                    .Add("speed_b32", SuitabilityExportJson.Bits(line.SpeedMetresPerSecond))
                    .Add("ride_seconds_b32", line.RideSeconds is null ? SuitabilityExportJson.Null() : SuitabilityExportJson.BitsArray(line.RideSeconds))
                    .Add("headway_b32", SuitabilityExportJson.Bits(line.HeadwaySeconds))
                    .Add("capacity_b32", SuitabilityExportJson.Bits(line.VehicleCapacity))
                    .Add("group", SuitabilityExportJson.Int(line.Group))
                    .Add("mode", SuitabilityExportJson.Str(m_LineSetResolved[c].Mode.ToString()))
                    .Build());
            }

            var lines = new List<string>(problem.BaseLines.Count);
            for (int i = 0; i < problem.BaseLines.Count; i++)
            {
                TransitLine line = problem.BaseLines[i];
                lines.Add(new SuitabilityJsonObject()
                    .Add("stops", SuitabilityExportJson.IntArray(line.m_Stops ?? Array.Empty<int>()))
                    .Add("ride_seconds_b32", line.m_RideSeconds is null ? SuitabilityExportJson.Null() : SuitabilityExportJson.BitsArray(line.m_RideSeconds))
                    .Add("expected_wait_b32", SuitabilityExportJson.Bits(line.m_ExpectedWait))
                    .Add("speed_b32", SuitabilityExportJson.Bits(line.m_SpeedMetresPerSecond))
                    .Build());
            }

            var data = new SuitabilityJsonObject()
                .Add("walk_radius_b32", SuitabilityExportJson.Bits(problem.WalkRadius))
                .Add("board_penalty_b32", SuitabilityExportJson.Bits(problem.BoardPenaltySeconds))
                .Add("max_travel_seconds_b32", SuitabilityExportJson.Bits(problem.MaxTravelSeconds))
                .Add("zone_reach_b32", SuitabilityExportJson.Bits(problem.ZoneReachMetres))
                .Add("pair_ox_b32", SuitabilityExportJson.BitsArray(problem.PairOx))
                .Add("pair_oz_b32", SuitabilityExportJson.BitsArray(problem.PairOz))
                .Add("pair_dx_b32", SuitabilityExportJson.BitsArray(problem.PairDx))
                .Add("pair_dz_b32", SuitabilityExportJson.BitsArray(problem.PairDz))
                .Add("pair_w_b32", SuitabilityExportJson.BitsArray(problem.PairWeight))
                .Add("pair_day_share_b32", SuitabilityExportJson.BitsArray(problem.PairDayShare))
                .Add("base_stop_x_b32", SuitabilityExportJson.BitsArray(problem.BaseStopX))
                .Add("base_stop_z_b32", SuitabilityExportJson.BitsArray(problem.BaseStopZ))
                .Add("base_lines", SuitabilityExportJson.Array(lines))
                .Add("candidates", SuitabilityExportJson.Array(candidates))
                .Add("max_lines", SuitabilityExportJson.Int(problem.MaxLines))
                .Add("utilisation_floor_b32", SuitabilityExportJson.Bits(problem.UtilisationFloor))
                .Add("utilisation_ceiling_b32", SuitabilityExportJson.Bits(problem.UtilisationCeiling))
                .Add("movement_seconds_per_day_b32", SuitabilityExportJson.Bits(problem.MovementSecondsPerDay))
                .Add("duplicate_share_b32", SuitabilityExportJson.Bits(problem.DuplicateShare))
                .Add("equity_floor_share_b32", SuitabilityExportJson.Bits(problem.EquityFloorShare))
                .Add("equity", EquityWalkJson(inputs))
                .Add("chosen", SuitabilityExportJson.IntArray(solution.Chosen))
                .Add("time_saved", SuitabilityExportJson.Str(solution.TimeSaved.ToString("R", CultureInfo.InvariantCulture)))
                .Add("upper_bound_time_saved", SuitabilityExportJson.Str(solution.UpperBoundTimeSaved.ToString("R", CultureInfo.InvariantCulture)))
                .Add("coverage_b32", SuitabilityExportJson.Bits(solution.Coverage))
                .Add("optimal", SuitabilityExportJson.Bool(solution.Optimal))
                .Add("standalone_time_saved", DoubleArray(solution.StandaloneTimeSaved))
                .Build();

            return new SuitabilityJsonObject()
                .Add("kind", SuitabilityExportJson.Str("lineset_time"))
                .Add("name", SuitabilityExportJson.Str(name))
                .Add("comment", SuitabilityExportJson.Str("exported from a live city; enumeration may be bounded"))
                .Add("heavy", SuitabilityExportJson.Bool(value: true))
                .Add("data", data)
                .BuildHashed(ExportSchemaVersion);
        }

        // The stop plans of the kept suggestions (F5 v2): each line's candidate
        // positions, forced calls, through-flow, the doors in reach, and the calls the
        // mod chose. The subject re-solves each plan, the evaluator checks the choice
        // against the objective's exact optimum.
        private string? BuildStopPlanInstance(string name)
        {
            var plans = new List<string>();
            for (int r = 0; r < m_Routes.Count; r++)
            {
                SuggestedRoute route = m_Routes[r];
                StopPlanProblem? plan = route.StopPlan;
                if (plan is null)
                {
                    continue;
                }

                plans.Add(new SuitabilityJsonObject()
                    .Add("mode", SuitabilityExportJson.Str(route.Mode.ToString()))
                    .Add("candidate_at_b32", SuitabilityExportJson.BitsArray(Prefix(plan.CandidateAt, plan.CandidateCount)))
                    .Add("candidate_x_b32", SuitabilityExportJson.BitsArray(Prefix(plan.CandidateX, plan.CandidateCount)))
                    .Add("candidate_z_b32", SuitabilityExportJson.BitsArray(Prefix(plan.CandidateZ, plan.CandidateCount)))
                    .Add("must_call", SuitabilityExportJson.BoolArray(Prefix(plan.MustCall, plan.CandidateCount)))
                    .Add("through_flow_b32", SuitabilityExportJson.BitsArray(Prefix(plan.ThroughFlow, plan.CandidateCount)))
                    .Add("end_at_b32", SuitabilityExportJson.BitsArray(Prefix(plan.EndAt, plan.EndCount)))
                    .Add("end_x_b32", SuitabilityExportJson.BitsArray(Prefix(plan.EndX, plan.EndCount)))
                    .Add("end_z_b32", SuitabilityExportJson.BitsArray(Prefix(plan.EndZ, plan.EndCount)))
                    .Add("end_w_b32", SuitabilityExportJson.BitsArray(Prefix(plan.EndWeight, plan.EndCount)))
                    .Add("min_gap_b32", SuitabilityExportJson.Bits(plan.MinGapMetres))
                    .Add("delay_per_stop_b32", SuitabilityExportJson.Bits(plan.DelaySecondsPerStop))
                    .Add("horizon_b32", SuitabilityExportJson.Bits(plan.AccessHorizonSeconds))
                    .Add("walk_speed_b32", SuitabilityExportJson.Bits(plan.WalkMetresPerSecond))
                    .Add("chosen", SuitabilityExportJson.IntArray(route.StopPlanChosen))
                    .Add("gain", SuitabilityExportJson.Str(route.StopPlanGain.ToString("R", CultureInfo.InvariantCulture)))
                    .Add("delay", SuitabilityExportJson.Str(route.StopPlanDelay.ToString("R", CultureInfo.InvariantCulture)))
                    .Build());
            }

            if (plans.Count == 0)
            {
                return null;
            }

            string data = new SuitabilityJsonObject()
                .Add("plans", SuitabilityExportJson.Array(plans))
                .Build();
            return new SuitabilityJsonObject()
                .Add("kind", SuitabilityExportJson.Str("stop_plan"))
                .Add("name", SuitabilityExportJson.Str(name))
                .Add("comment", SuitabilityExportJson.Str("stop plans of the kept suggestions, exported from a live city"))
                .Add("data", data)
                .BuildHashed(ExportSchemaVersion);
        }

        private static bool[] Prefix(bool[] values, int count)
        {
            var prefix = new bool[count];
            System.Array.Copy(values, prefix, count);
            return prefix;
        }

        // The walking network, served stops and journeys the equity share is measured
        // on (the `coverage` kind's inputs), embedded so the set objective's equity
        // component can be recomputed for any subset offline.
        private string EquityWalkJson(WalkAccessInputs inputs)
        {
            int count = m_Journeys.Count;
            var ox = new float[count];
            var oz = new float[count];
            var dx = new float[count];
            var dz = new float[count];
            var w = new float[count];
            for (int i = 0; i < count; i++)
            {
                Trip trip = m_Journeys[i];
                ox[i] = trip.m_Origin.x;
                oz[i] = trip.m_Origin.y;
                dx[i] = trip.m_Destination.x;
                dz[i] = trip.m_Destination.y;
                w[i] = trip.m_Weight;
            }

            var sx = new float[m_TransitStops.Count];
            var sz = new float[m_TransitStops.Count];
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                sx[i] = m_TransitStops[i].x;
                sz[i] = m_TransitStops[i].y;
            }

            return new SuitabilityJsonObject()
                .Add("node_x_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeX))
                .Add("node_z_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeZ))
                .Add("edge_a", SuitabilityExportJson.IntArray(inputs.Graph.EdgeA))
                .Add("edge_b", SuitabilityExportJson.IntArray(inputs.Graph.EdgeB))
                .Add("edge_metres_b32", SuitabilityExportJson.BitsArray(inputs.Graph.EdgeMetres))
                .Add("stop_x_b32", SuitabilityExportJson.BitsArray(sx))
                .Add("stop_z_b32", SuitabilityExportJson.BitsArray(sz))
                .Add("trip_ox_b32", SuitabilityExportJson.BitsArray(ox))
                .Add("trip_oz_b32", SuitabilityExportJson.BitsArray(oz))
                .Add("trip_dx_b32", SuitabilityExportJson.BitsArray(dx))
                .Add("trip_dz_b32", SuitabilityExportJson.BitsArray(dz))
                .Add("trip_w_b32", SuitabilityExportJson.BitsArray(w))
                .Add("access_ms", SuitabilityExportJson.Int(inputs.AccessMs))
                .Add("horizon_ms", SuitabilityExportJson.Int(m_EquityHorizonMs))
                .Build();
        }

        private static string DoubleArray(double[] values)
        {
            var builder = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(',');
                }

                _ = builder.Append(SuitabilityExportJson.Str(values[i].ToString("R", CultureInfo.InvariantCulture)));
            }

            return builder.Append(']').ToString();
        }

        // The directed road network with the stop-to-stop driving times the last route
        // pass asked of it — the `road_times` kind. Null before the graph exists.
        private string? BuildRoadTimesInstance(string name)
        {
            DirectedRoadGraph? directed = m_RoadGraph.Directed;
            if (directed is null || directed.ArcCount == 0)
            {
                return null;
            }

            int legs = m_RoadLegs.Count;
            var fromX = new float[legs];
            var fromZ = new float[legs];
            var toX = new float[legs];
            var toZ = new float[legs];
            var legMs = new long[legs];
            var fromArc = new int[legs];
            var toArc = new int[legs];
            var startMs = new int[legs];
            var endMs = new int[legs];
            var sameArc = new int[legs];
            for (int i = 0; i < legs; i++)
            {
                (float2Like from, float2Like to, long ms, RoadLeg leg) = m_RoadLegs[i];
                fromX[i] = from.x;
                fromZ[i] = from.y;
                toX[i] = to.x;
                toZ[i] = to.y;
                legMs[i] = ms == DirectedDijkstra.Unreached ? -1L : ms;
                fromArc[i] = leg.FromArc;
                toArc[i] = leg.ToArc;
                startMs[i] = leg.StartMs;
                endMs[i] = leg.EndMs;
                sameArc[i] = leg.SameArc ? 1 : 0;
            }

            var data = new SuitabilityJsonObject()
                .Add("node_x_b32", SuitabilityExportJson.BitsArray(directed.NodeX))
                .Add("node_z_b32", SuitabilityExportJson.BitsArray(directed.NodeZ))
                .Add("arc_from", SuitabilityExportJson.IntArray(directed.ArcFrom))
                .Add("arc_to", SuitabilityExportJson.IntArray(directed.ArcTo))
                .Add("arc_edge", SuitabilityExportJson.IntArray(directed.ArcEdge))
                .Add("arc_metres_b32", SuitabilityExportJson.BitsArray(directed.ArcMetres))
                .Add("arc_speed_b32", SuitabilityExportJson.BitsArray(directed.ArcSpeed))
                .Add("arc_ms", SuitabilityExportJson.IntArray(directed.ArcMs))
                .Add("out_dx_b32", SuitabilityExportJson.BitsArray(directed.OutDx))
                .Add("out_dz_b32", SuitabilityExportJson.BitsArray(directed.OutDz))
                .Add("in_dx_b32", SuitabilityExportJson.BitsArray(directed.InDx))
                .Add("in_dz_b32", SuitabilityExportJson.BitsArray(directed.InDz))
                .Add("turn_seconds_per_radian_b32", SuitabilityExportJson.Bits(m_RoadGraph.TurnSecondsPerRadian))
                .Add("turn_ms", SuitabilityExportJson.IntArray(directed.TurnMs))
                .Add("max_ms", SuitabilityExportJson.Int((long)Assumptions.MaxJourneySeconds * 1000L))
                .Add("snap_metres_b32", SuitabilityExportJson.Bits(Assumptions.StopNodeSnapMetres))
                .Add("leg_from_x_b32", SuitabilityExportJson.BitsArray(fromX))
                .Add("leg_from_z_b32", SuitabilityExportJson.BitsArray(fromZ))
                .Add("leg_to_x_b32", SuitabilityExportJson.BitsArray(toX))
                .Add("leg_to_z_b32", SuitabilityExportJson.BitsArray(toZ))
                .Add("leg_ms", LongArray(legMs))
                .Add("leg_from_arc", SuitabilityExportJson.IntArray(fromArc))
                .Add("leg_to_arc", SuitabilityExportJson.IntArray(toArc))
                .Add("leg_start_ms", SuitabilityExportJson.IntArray(startMs))
                .Add("leg_end_ms", SuitabilityExportJson.IntArray(endMs))
                .Add("leg_same_arc", SuitabilityExportJson.IntArray(sameArc))
                .Add("legs_dropped", SuitabilityExportJson.Int(m_RoadLegsDropped))
                .Build();

            return new SuitabilityJsonObject()
                .Add("kind", SuitabilityExportJson.Str("road_times"))
                .Add("name", SuitabilityExportJson.Str(name))
                .Add("data", data)
                .BuildHashed(ExportSchemaVersion);
        }

        private static string LongArray(long[] values)
        {
            var builder = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(',');
                }

                _ = builder.Append(SuitabilityExportJson.Int(values[i]));
            }

            return builder.Append(']').ToString();
        }

    }
}
