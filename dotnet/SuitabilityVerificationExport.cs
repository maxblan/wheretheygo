using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Game.Simulation;
using Unity.Collections;
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
                .Add("tile_size_b32", SuitabilityExportJson.Bits(TileSize))
                .Add("node_x_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeX))
                .Add("node_z_b32", SuitabilityExportJson.BitsArray(inputs.Graph.NodeZ))
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

        // The routing instance is the pipeline's existing `lineset` kind. Null when
        // the demand pipeline has not run yet, which is the honest answer: there is
        // no transit model to export rather than an empty one to misread.
        private string? BuildLinesetInstance(Setting settings, string name)
        {
            float[]? zoneX = m_ZoneCentreX;
            float[]? zoneZ = m_ZoneCentreZ;
            if (zoneX is null || zoneZ is null || m_ZoneFlows.Count == 0)
            {
                return null;
            }

            var flows = new List<string>(m_ZoneFlows.Count);
            for (int i = 0; i < m_ZoneFlows.Count; i++)
            {
                ZoneFlow flow = m_ZoneFlows[i];
                flows.Add(new SuitabilityJsonObject()
                    .Add("origin", SuitabilityExportJson.Int(flow.m_Origin))
                    .Add("dest", SuitabilityExportJson.Int(flow.m_Destination))
                    .Add("weight_b32", SuitabilityExportJson.Bits(flow.m_Weight))
                    .Build());
            }

            var stopX = new float[m_TransitStops.Count];
            var stopZ = new float[m_TransitStops.Count];
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                stopX[i] = m_TransitStops[i].x;
                stopZ[i] = m_TransitStops[i].y;
            }

            var data = new SuitabilityJsonObject()
                .Add("walk_radius_b32", SuitabilityExportJson.Bits(TransferWalkRadius))
                .Add("board_penalty_b32",
                    SuitabilityExportJson.Bits(SuitabilityTransit.DefaultBoardPenaltySeconds))
                .Add("transfer_discount_b32", SuitabilityExportJson.Bits(settings.TransferDiscount))
                .Add("max_travel_seconds_b32", SuitabilityExportJson.Bits(MaxJourneySeconds))
                .Add("switch_margin_b32", SuitabilityExportJson.Bits(SwitchMarginSeconds))
                .Add("zone_stop_reach_b32", SuitabilityExportJson.Bits(ZoneStopReachMetres))
                .Add("zone_x_b32", SuitabilityExportJson.BitsArray(zoneX))
                .Add("zone_z_b32", SuitabilityExportJson.BitsArray(zoneZ))
                .Add("flows", SuitabilityExportJson.Array(flows))
                .Add("existing_stop_x_b32", SuitabilityExportJson.BitsArray(stopX))
                .Add("existing_stop_z_b32", SuitabilityExportJson.BitsArray(stopZ))
                .Add("existing_lines", ExistingLinesJson())
                .Add("candidates", CandidatesJson())
                .Add("max_accept", SuitabilityExportJson.Int(settings.RouteCount))
                .Build();

            return new SuitabilityJsonObject()
                .Add("kind", SuitabilityExportJson.Str("lineset"))
                .Add("name", SuitabilityExportJson.Str(name))
                .Add("comment", SuitabilityExportJson.Str(
                    "exported from a live city; acceptance gates are out of scope for "
                    + "this kind, they are covered by the mode_choice instances"))
                .Add("data", data)
                .BuildHashed(ExportSchemaVersion);
        }

        private string ExistingLinesJson()
        {
            var lines = new List<string>(m_ExistingLines.Count);
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                if (line.m_StopIndices.Count < 2)
                {
                    continue;
                }

                lines.Add(new SuitabilityJsonObject()
                    .Add("stops", SuitabilityExportJson.IntArray(line.m_StopIndices))
                    // Carried because ToTransitLines does: without the pathfound
                    // durations the verifier would price every ride geometrically and
                    // disagree with the mod about journeys it never got wrong.
                    .Add("ride_seconds_b32", SuitabilityExportJson.BitsArray(line.m_RideSeconds))
                    .Add("expected_wait_b32", SuitabilityExportJson.Bits(line.ExpectedWait))
                    .Add("speed_b32",
                        SuitabilityExportJson.Bits(TransitModes.CruiseSpeedFor(line.m_Mode)))
                    .Build());
            }

            return SuitabilityExportJson.Array(lines);
        }

        private string CandidatesJson()
        {
            var candidates = new List<string>(m_RouteCandidates.Count);
            for (int i = 0; i < m_RouteCandidates.Count; i++)
            {
                SuggestedRoute route = m_RouteCandidates[i];
                if (route.Stops.Count < 2)
                {
                    continue;
                }

                var x = new float[route.Stops.Count];
                var z = new float[route.Stops.Count];
                for (int s = 0; s < route.Stops.Count; s++)
                {
                    x[s] = route.Stops[s].x;
                    z[s] = route.Stops[s].y;
                }

                candidates.Add(new SuitabilityJsonObject()
                    .Add("stop_x_b32", SuitabilityExportJson.BitsArray(x))
                    .Add("stop_z_b32", SuitabilityExportJson.BitsArray(z))
                    .Add("expected_wait_b32", SuitabilityExportJson.Bits(SuggestedWaitFor(route.Mode)))
                    .Add("speed_b32", SuitabilityExportJson.Bits(TransitModes.CruiseSpeedFor(route.Mode)))
                    .Add("captured_flow_b32", SuitabilityExportJson.Bits(route.CapturedFlow))
                    .Build());
            }

            return SuitabilityExportJson.Array(candidates);
        }
    }
}
