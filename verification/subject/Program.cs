using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace StationSuitabilityOverlay.Verification
{
    // The SYSTEM UNDER TEST driver. It links the mod's pure files unchanged and
    // wires them exactly as the ECS half does (wiring documented per call in
    // docs/formal-specification.md). Everything here is IO and glue; any glue that
    // mirrors ECS-side orchestration (zone mapping order, greedy rounds) is part of
    // the trusted-to-be-faithful boundary listed in docs/verification-architecture.md.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: Subject <instance.json> <solution.json>");
                return 2;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(args[0]));
            JsonElement root = doc.RootElement;
            string kind = root.GetProperty("kind").GetString() ?? "";
            JsonElement data = root.GetProperty("data");

            Dictionary<string, object?> result = kind switch
            {
                "sites" => Sites(data),
                "lattice_path" => LatticePath(data),
                "calling_points" => CallingPoints(data),
                "mode_choice" => ModeChoice(data),
                "lineset" => LineSet(data),
                "corridor" => CorridorGrowth(data),
                "heatmap_point" => HeatmapPoint(data),
                "heatmap_grid" => HeatmapGrid(data),
                "order_stats" => OrderStats(data),
                _ => throw new InvalidOperationException($"unknown kind '{kind}'"),
            };
            result["kind"] = kind;
            result["instance_hash"] = root.TryGetProperty("hash", out JsonElement h) ? h.GetString() : null;

            File.WriteAllText(args[1], JsonSerializer.Serialize(
                result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // ------------------------------------------------------------- helpers

        private static float F32(JsonElement e) => BitConverter.UInt32BitsToSingle(e.GetUInt32());

        private static uint B32(float f) => BitConverter.SingleToUInt32Bits(f);

        private static float[] F32Array(JsonElement e)
        {
            var result = new float[e.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in e.EnumerateArray())
            {
                result[i++] = BitConverter.UInt32BitsToSingle(item.GetUInt32());
            }
            return result;
        }

        private static int[] IntArray(JsonElement e)
        {
            var result = new int[e.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in e.EnumerateArray())
            {
                result[i++] = item.GetInt32();
            }
            return result;
        }

        private static bool[] BoolArray(JsonElement e)
        {
            var result = new bool[e.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in e.EnumerateArray())
            {
                result[i++] = item.GetBoolean();
            }
            return result;
        }

        private static List<uint> Bits(IReadOnlyList<float> values, int count)
        {
            var bits = new List<uint>(count);
            for (int i = 0; i < count; i++)
            {
                bits.Add(B32(values[i]));
            }
            return bits;
        }

        // ECS-side WalkSeconds, mirrored: (float)Math.Sqrt(double)/1.4f
        // (StationSuitabilityOverlaySystem.cs:3138-3141).
        private static float WalkSeconds(float distSq)
        {
            return (float)Math.Sqrt(distSq) / 1.4f;
        }

        // ------------------------------------------------------------- S2 sites

        private static Dictionary<string, object?> Sites(JsonElement data)
        {
            int width = data.GetProperty("width").GetInt32();
            int height = data.GetProperty("height").GetInt32();
            float[] scores = F32Array(data.GetProperty("scores_b32"));
            int separation = data.GetProperty("min_separation").GetInt32();
            int maxSites = data.GetProperty("max_sites").GetInt32();

            var indices = new int[maxSites];
            var outScores = new float[maxSites];
            int count = SuitabilityScoring.FindTopSites(
                scores, width, height, separation, maxSites, indices, outScores, out bool truncated);

            var idx = new List<int>();
            for (int i = 0; i < count; i++)
            {
                idx.Add(indices[i]);
            }

            return new Dictionary<string, object?>
            {
                ["site_indices"] = idx,
                ["site_scores_b32"] = Bits(outScores, count),
                ["truncated"] = truncated,
            };
        }

        // ------------------------------------------------------ S4 lattice path

        private static Dictionary<string, object?> LatticePath(JsonElement data)
        {
            int[] edgeA = IntArray(data.GetProperty("edge_a"));
            int[] edgeB = IntArray(data.GetProperty("edge_b"));
            float[] edgeCost = F32Array(data.GetProperty("edge_cost_b32"));
            int nodeCount = F32Array(data.GetProperty("node_x_b32")).Length;
            int from = data.GetProperty("from").GetInt32();
            int to = data.GetProperty("to").GetInt32();
            float maxCost = F32(data.GetProperty("max_cost_b32"));

            CompactGraph graph = CompactGraph.Build(nodeCount, edgeA, edgeB, edgeCost, edgeA.Length);
            var workspace = new DijkstraWorkspace(nodeCount);
            workspace.Run(graph, from, maxCost);

            if (workspace.Dist[to] == float.MaxValue)
            {
                return new Dictionary<string, object?> { ["reachable"] = false };
            }

            // Back-walk mirroring SuitabilityRoadGraph.TracePath (guarded, reversed).
            var path = new List<int> { to };
            int node = to;
            int guard = edgeA.Length + 2;
            while (node != from && guard-- > 0)
            {
                int edge = workspace.PrevEdge[node];
                if (edge < 0)
                {
                    return new Dictionary<string, object?> { ["reachable"] = false };
                }
                node = graph.OtherEnd(edge, node);
                path.Add(node);
            }
            path.Reverse();

            return new Dictionary<string, object?>
            {
                ["reachable"] = true,
                ["path"] = path,
                ["distance_b32"] = B32(workspace.Dist[to]),
            };
        }

        // --------------------------------------------------- S5 calling points

        private static Dictionary<string, object?> CallingPoints(JsonElement data)
        {
            var plans = new List<object>();
            foreach (JsonElement plan in data.GetProperty("plans").EnumerateArray())
            {
                float length = F32(plan.GetProperty("length_b32"));
                float spacing = F32(plan.GetProperty("spacing_b32"));
                int buffer = plan.GetProperty("buffer").GetInt32();
                var into = new float[buffer];
                int count = SuitabilityScoring.PlanCallingPoints(length, spacing, into);
                plans.Add(new Dictionary<string, object?>
                {
                    ["count"] = count,
                    ["offsets_b32"] = Bits(into, count),
                });
            }

            var selections = new List<object>();
            foreach (JsonElement sel in data.GetProperty("selections").EnumerateArray())
            {
                float[] scores = F32Array(sel.GetProperty("scores_b32"));
                float floorShare = F32(sel.GetProperty("floor_share_b32"));
                bool[] mustCall = BoolArray(sel.GetProperty("must_call"));
                var keep = new bool[scores.Length];
                SuitabilityScoring.SelectCallingPoints(
                    scores, scores.Length, floorShare, new float[scores.Length], mustCall, keep);
                selections.Add(new Dictionary<string, object?> { ["keep"] = new List<bool>(keep) });
            }

            return new Dictionary<string, object?>
            {
                ["plans"] = plans,
                ["selections"] = selections,
            };
        }

        // ------------------------------------------------------- S6 mode choice

        private static Dictionary<string, object?> ModeChoice(JsonElement data)
        {
            JsonElement caps = data.GetProperty("capacities_b32");
            var byMode = new float[5];
            foreach (JsonProperty p in caps.EnumerateObject())
            {
                var mode = (ModePreset)Enum.Parse(typeof(ModePreset), p.Name);
                byMode[(int)mode] = BitConverter.UInt32BitsToSingle(p.Value.GetUInt32());
            }
            var capacities = new FleetCapacity(byMode);

            var rows = new List<object>();
            foreach (JsonElement row in data.GetProperty("rows").EnumerateArray())
            {
                var network = (RouteNetwork)Enum.Parse(typeof(RouteNetwork), row.GetProperty("network").GetString() ?? "Road");
                var traced = (ModePreset)Enum.Parse(typeof(ModePreset), row.GetProperty("traced_mode").GetString() ?? "Bus");
                var evidence = new CorridorEvidence(
                    F32(row.GetProperty("flow_b32")),
                    F32(row.GetProperty("length_b32")),
                    F32(row.GetProperty("enabled_demand_b32")),
                    F32(row.GetProperty("city_travel_weight_b32")),
                    F32(row.GetProperty("track_share_b32")),
                    row.GetProperty("demand_scored").GetBoolean());
                bool ok = TransitModes.ChooseMode(
                    network, traced, evidence, F32(row.GetProperty("reference_flow_b32")),
                    capacities, out ModePreset mode, out ModeRejection why);
                rows.Add(new Dictionary<string, object?>
                {
                    ["ok"] = ok,
                    ["mode"] = mode.ToString(),
                    ["rejection"] = why.ToString(),
                });
            }

            return new Dictionary<string, object?> { ["rows"] = rows };
        }

        // -------------------------------------------------- S4 corridor growth

        // Mirrors the BuildForNetwork core loop (spec §6.3 pool construction):
        // flow copy, unstoppable edges pre-used, novelty 1, mean/floor derived,
        // Grow -> Peel (capture 1 for single-edge corridors, else the instance
        // capture) -> DecayNovelty per round. Everything numeric is mod code.
        private static Dictionary<string, object?> CorridorGrowth(JsonElement data)
        {
            int[] edgeA = IntArray(data.GetProperty("edge_a"));
            int[] edgeB = IntArray(data.GetProperty("edge_b"));
            float[] cost = F32Array(data.GetProperty("edge_cost_b32"));
            float[] flow = F32Array(data.GetProperty("edge_flow_b32"));
            float[] nodeX = F32Array(data.GetProperty("node_x_b32"));
            float[] nodeZ = F32Array(data.GetProperty("node_z_b32"));
            float[]? nodeDemand = data.GetProperty("node_demand_b32").ValueKind == JsonValueKind.Null
                ? null
                : F32Array(data.GetProperty("node_demand_b32"));

            CompactGraph graph = CompactGraph.Build(nodeX.Length, edgeA, edgeB, cost, edgeA.Length);
            var used = new bool[graph.EdgeCount];
            if (data.GetProperty("edge_cannot_host").ValueKind != JsonValueKind.Null)
            {
                bool[] cannot = BoolArray(data.GetProperty("edge_cannot_host"));
                for (int e = 0; e < used.Length && e < cannot.Length; e++)
                {
                    used[e] = cannot[e];
                }
            }
            var novelty = new float[graph.NodeCount];
            for (int n = 0; n < novelty.Length; n++)
            {
                novelty[n] = 1f;
            }

            var network = new CorridorNetwork(graph, flow, used, novelty, nodeDemand, nodeX, nodeZ);
            var objective = (RouteObjective)Enum.Parse(
                typeof(RouteObjective), data.GetProperty("objective").GetString() ?? "Ridership");
            float meanFlow = SuitabilityGraphMath.MeanPositiveFlow(flow, graph.EdgeCount);
            float noveltyWeight = SuitabilityGraphMath.NoveltyWeight(objective, meanFlow);
            float seedBias = SuitabilityGraphMath.SeedNoveltyBias(objective);
            float flowFloor = meanFlow * F32(data.GetProperty("min_flow_fraction_b32"));
            float maxLength = F32(data.GetProperty("max_route_length_b32"));
            float demandFloor = F32(data.GetProperty("demand_floor_b32"));
            float capture = F32(data.GetProperty("capture_b32"));
            int hops = data.GetProperty("novelty_hops").GetInt32();
            float factor = F32(data.GetProperty("novelty_factor_b32"));
            int maxBridge = data.GetProperty("max_low_demand_bridge").GetInt32();
            int roundCount = data.GetProperty("rounds").GetInt32();

            var corridor = new Corridor();
            var rounds = new List<object>();
            for (int r = 0; r < roundCount; r++)
            {
                bool ok = SuitabilityGraphMath.GrowCorridor(
                    in network, noveltyWeight, flowFloor, maxLength, corridor,
                    demandFloor, seedBias, maxBridge);
                if (!ok)
                {
                    rounds.Add(new Dictionary<string, object?> { ["ok"] = false });
                    break;
                }

                var round = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["edges"] = new List<int>(corridor.Edges),
                    ["nodes"] = new List<int>(corridor.Nodes),
                    ["length_b32"] = B32(corridor.Length),
                    ["captured_flow_b32"] = B32(corridor.CapturedFlow),
                    ["blocks"] = new Dictionary<string, object?>
                    {
                        ["used"] = corridor.Blocks.m_Used,
                        ["flow"] = corridor.Blocks.m_Flow,
                        ["visited"] = corridor.Blocks.m_Visited,
                        ["length"] = corridor.Blocks.m_Length,
                        ["demand"] = corridor.Blocks.m_Demand,
                        ["hit_max_length"] = corridor.Blocks.m_HitMaxLength,
                    },
                };

                if (corridor.Edges.Count < 2)
                {
                    SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, 1f);
                }
                else
                {
                    SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, capture);
                    SuitabilityGraphMath.DecayNovelty(graph, corridor, novelty, hops, factor);
                }

                round["flow_after_b32"] = Bits(flow, flow.Length);
                round["novelty_after_b32"] = Bits(novelty, novelty.Length);
                rounds.Add(round);
            }

            return new Dictionary<string, object?>
            {
                ["mean_flow_b32"] = B32(meanFlow),
                ["flow_floor_b32"] = B32(flowFloor),
                ["novelty_weight_b32"] = B32(noveltyWeight),
                ["seed_bias_b32"] = B32(seedBias),
                ["rounds"] = rounds,
            };
        }

        // ------------------------------------------------ S1 stop-derived terms

        private static Dictionary<string, object?> HeatmapPoint(JsonElement data)
        {
            float catchment = F32(data.GetProperty("catchment_b32"));
            float interchangeRadius = F32(data.GetProperty("interchange_b32"));
            float invSelf = F32(data.GetProperty("inv_self_b32"));
            float w3 = F32(data.GetProperty("coverage_weight_b32"));
            float w6 = F32(data.GetProperty("interchange_weight_b32"));
            float w7 = F32(data.GetProperty("cross_weight_b32"));

            var stops = new List<(float X, float Z, float W, bool Same)>();
            foreach (JsonElement s in data.GetProperty("stops").EnumerateArray())
            {
                stops.Add((F32(s.GetProperty("x_b32")), F32(s.GetProperty("z_b32")),
                    F32(s.GetProperty("weight_b32")), s.GetProperty("same_mode").GetBoolean()));
            }

            var queries = new List<object>();
            foreach (JsonElement q in data.GetProperty("queries").EnumerateArray())
            {
                float qx = F32(q.GetProperty("x_b32"));
                float qz = F32(q.GetProperty("z_b32"));
                float coverage = 0f;
                float interchange = 0f;
                float cross = 0f;
                foreach ((float x, float z, float w, bool same) in stops)
                {
                    // Flat scan, distance as the ECS swap computes it
                    // (float32 sqrt of a float32 squared sum).
                    float dx = x - qx;
                    float dz = z - qz;
                    float distance = MathF.Sqrt((dx * dx) + (dz * dz));
                    SuitabilityScoring.AccumulateStop(distance, w, same, catchment,
                        interchangeRadius, ref coverage, ref interchange, ref cross);
                }
                float share = MathF.Min(coverage, 1.5f) / 1.5f;
                float terms = SuitabilityScoring.ModeTerms(
                    share, interchange, cross, invSelf, w3, w6, w7);
                queries.Add(new Dictionary<string, object?>
                {
                    ["coverage_b32"] = B32(coverage),
                    ["interchange_b32"] = B32(interchange),
                    ["cross_b32"] = B32(cross),
                    ["mode_terms_b32"] = B32(terms),
                });
            }

            return new Dictionary<string, object?> { ["queries"] = queries };
        }

        // ----------------------------------- S1 gathered terms from a real save
        //
        // A `heatmap_grid` instance carries the Burst job's own inputs and a sample of
        // the terms it produced. The job itself cannot run here (Unity), and this
        // runner does NOT reimplement it: the subject's answer for this kind IS the
        // exported sample. The evaluator recomputes the same cells from the same
        // inputs, so the comparison is still mod-against-independent-implementation —
        // it just crosses a file rather than a function call.
        private static Dictionary<string, object?> HeatmapGrid(JsonElement data)
        {
            var terms = new List<object>();
            JsonElement indices = data.GetProperty("sample_indices");
            string[] fields =
            {
                "sample_demand_b32", "sample_jobs_b32", "sample_coverage_b32",
                "sample_access_b32", "sample_future_b32", "sample_interchange_b32",
                "sample_cross_b32",
            };
            var columns = new List<uint[]>();
            foreach (string field in fields)
            {
                JsonElement column = data.GetProperty(field);
                var values = new uint[column.GetArrayLength()];
                int i = 0;
                foreach (JsonElement item in column.EnumerateArray())
                {
                    values[i++] = item.GetUInt32();
                }
                columns.Add(values);
            }

            int index = 0;
            foreach (JsonElement cell in indices.EnumerateArray())
            {
                terms.Add(new Dictionary<string, object?>
                {
                    ["index"] = cell.GetInt32(),
                    ["demand_b32"] = columns[0][index],
                    ["jobs_b32"] = columns[1][index],
                    ["coverage_b32"] = columns[2][index],
                    ["access_b32"] = columns[3][index],
                    ["future_b32"] = columns[4][index],
                    ["interchange_b32"] = columns[5][index],
                    ["cross_b32"] = columns[6][index],
                });
                index++;
            }

            return new Dictionary<string, object?> { ["cells"] = terms };
        }

        // ----------------------------------------------- S1/S5 order statistics

        private static Dictionary<string, object?> OrderStats(JsonElement data)
        {
            var selects = new List<object>();
            foreach (JsonElement c in data.GetProperty("select_cases").EnumerateArray())
            {
                float[] values = F32Array(c.GetProperty("values_b32"));
                int k = c.GetProperty("k").GetInt32();
                var copy = (float[])values.Clone();
                float result = SuitabilityScoring.SelectKth(copy, copy.Length, k);
                selects.Add(B32(result));
            }

            var percentiles = new List<object>();
            foreach (JsonElement c in data.GetProperty("percentile_cases").EnumerateArray())
            {
                float[] values = F32Array(c.GetProperty("values_b32"));
                float p = F32(c.GetProperty("percentile_b32"));
                var scratch = new float[values.Length];
                float result = SuitabilityScoring.PositivePercentile(
                    (float[])values.Clone(), values.Length, p, scratch);
                percentiles.Add(B32(result));
            }

            return new Dictionary<string, object?>
            {
                ["select_results_b32"] = selects,
                ["percentile_results_b32"] = percentiles,
            };
        }

        // ----------------------------------------------------------- S7 lineset

        private sealed class Candidate
        {
            public float[] StopX = Array.Empty<float>();
            public float[] StopZ = Array.Empty<float>();
            public float Wait;
            public float Speed;
            public float CapturedFlow;
        }

        private static Dictionary<string, object?> LineSet(JsonElement data)
        {
            float walkRadius = F32(data.GetProperty("walk_radius_b32"));
            float boardPenalty = F32(data.GetProperty("board_penalty_b32"));
            float discount = F32(data.GetProperty("transfer_discount_b32"));
            float maxTravel = F32(data.GetProperty("max_travel_seconds_b32"));
            float margin = F32(data.GetProperty("switch_margin_b32"));
            float reach = F32(data.GetProperty("zone_stop_reach_b32"));

            float[] zoneX = F32Array(data.GetProperty("zone_x_b32"));
            float[] zoneZ = F32Array(data.GetProperty("zone_z_b32"));
            int zoneCount = zoneX.Length;

            var flowOrigin = new List<int>();
            var flowDest = new List<int>();
            var flowWeight = new List<float>();
            foreach (JsonElement f in data.GetProperty("flows").EnumerateArray())
            {
                flowOrigin.Add(f.GetProperty("origin").GetInt32());
                flowDest.Add(f.GetProperty("dest").GetInt32());
                flowWeight.Add(F32(f.GetProperty("weight_b32")));
            }
            int flowCount = flowOrigin.Count;

            float[] existingX = F32Array(data.GetProperty("existing_stop_x_b32"));
            float[] existingZ = F32Array(data.GetProperty("existing_stop_z_b32"));
            int existingCount = existingX.Length;

            var existingLines = new List<TransitLine>();
            foreach (JsonElement l in data.GetProperty("existing_lines").EnumerateArray())
            {
                existingLines.Add(new TransitLine
                {
                    m_Stops = IntArray(l.GetProperty("stops")),
                    m_ExpectedWait = F32(l.GetProperty("expected_wait_b32")),
                    m_SpeedMetresPerSecond = F32(l.GetProperty("speed_b32")),
                    // Present on exported instances, absent on synthetic ones, where
                    // the geometric fallback is what the instance intends.
                    m_RideSeconds = l.TryGetProperty("ride_seconds_b32", out JsonElement rides)
                        ? F32Array(rides)
                        : null,
                });
            }

            var candidates = new List<Candidate>();
            foreach (JsonElement c in data.GetProperty("candidates").EnumerateArray())
            {
                candidates.Add(new Candidate
                {
                    StopX = F32Array(c.GetProperty("stop_x_b32")),
                    StopZ = F32Array(c.GetProperty("stop_z_b32")),
                    Wait = F32(c.GetProperty("expected_wait_b32")),
                    Speed = F32(c.GetProperty("speed_b32")),
                    CapturedFlow = F32(c.GetProperty("captured_flow_b32")),
                });
            }
            int maxAccept = data.GetProperty("max_accept").GetInt32();

            // Base zone -> stop mapping over EXISTING stops (mirrors MapZonesToStops:
            // nearest within reach, strict <, ties to the lower index). RemapZones with
            // -1 incumbents produces exactly that.
            var baseZoneStop = new int[zoneCount];
            var baseZoneDist = new float[zoneCount];
            for (int z = 0; z < zoneCount; z++)
            {
                baseZoneStop[z] = -1;
                baseZoneDist[z] = 0f;
            }
            if (existingCount > 0)
            {
                var mappedStop = new int[zoneCount];
                var mappedDist = new float[zoneCount];
                SuitabilityTransit.RemapZones(zoneX, zoneZ, zoneCount, baseZoneStop, baseZoneDist,
                    existingX, existingZ, existingCount, 0, reach, mappedStop, mappedDist);
                baseZoneStop = mappedStop;
                baseZoneDist = mappedDist;
            }

            var acceptedX = new List<float>();
            var acceptedZ = new List<float>();
            var acceptedLines = new List<TransitLine>();
            var acceptedOrder = new List<int>();
            var settled = new bool[candidates.Count];
            var rounds = new List<object>();

            for (int round = 0; round < maxAccept; round++)
            {
                int baseStops = existingCount + acceptedX.Count;
                var xs = new float[baseStops];
                var zs = new float[baseStops];
                Array.Copy(existingX, xs, existingCount);
                Array.Copy(existingZ, zs, existingCount);
                for (int i = 0; i < acceptedX.Count; i++)
                {
                    xs[existingCount + i] = acceptedX[i];
                    zs[existingCount + i] = acceptedZ[i];
                }

                var baseLines = new List<TransitLine>(existingLines);
                baseLines.AddRange(acceptedLines);

                // Round mapping: base mapping remapped by all accepted stops
                // (mirrors the BuildPairsWith side effect in ScoreCandidates).
                var roundStop = (int[])baseZoneStop.Clone();
                var roundDist = (float[])baseZoneDist.Clone();
                if (acceptedX.Count > 0)
                {
                    var mappedStop = new int[zoneCount];
                    var mappedDist = new float[zoneCount];
                    SuitabilityTransit.RemapZones(zoneX, zoneZ, zoneCount, baseZoneStop, baseZoneDist,
                        acceptedX.ToArray(), acceptedZ.ToArray(), acceptedX.Count,
                        existingCount, reach, mappedStop, mappedDist);
                    roundStop = mappedStop;
                    roundDist = mappedDist;
                }

                // MeasureBaseline against the round network WITHOUT any candidate.
                var baselines = new float[flowCount];
                for (int i = 0; i < flowCount; i++)
                {
                    baselines[i] = float.MaxValue;
                }
                if (baseStops > 0 && baseLines.Count > 0)
                {
                    TransitNetwork baseNetwork = SuitabilityTransit.Build(
                        xs, zs, baseStops, baseLines, walkRadius, boardPenalty);
                    var ws = new DijkstraWorkspace(baseNetwork.Graph.NodeCount);
                    int currentOrigin = -1;
                    for (int i = 0; i < flowCount; i++)
                    {
                        int origin = roundStop[flowOrigin[i]];
                        int dest = roundStop[flowDest[i]];
                        if (origin < 0 || dest < 0 || origin == dest)
                        {
                            continue;
                        }
                        if (origin != currentOrigin)
                        {
                            currentOrigin = origin;
                            ws.Run(baseNetwork.Graph, origin, maxTravel);
                        }
                        if (SuitabilityTransit.Inspect(baseNetwork, ws, origin, dest, -1,
                                out int boardings, out _, out float travelTime)
                            && boardings > 0)
                        {
                            baselines[i] = travelTime
                                + WalkSeconds(roundDist[flowOrigin[i]])
                                + WalkSeconds(roundDist[flowDest[i]]);
                        }
                    }
                }

                // Score every unsettled candidate with transfers.
                var credits = new float?[candidates.Count];
                for (int c = 0; c < candidates.Count; c++)
                {
                    if (settled[c])
                    {
                        continue;
                    }
                    Candidate cand = candidates[c];
                    int candStops = cand.StopX.Length;
                    int total = baseStops + candStops;
                    var xs2 = new float[total];
                    var zs2 = new float[total];
                    Array.Copy(xs, xs2, baseStops);
                    Array.Copy(zs, zs2, baseStops);
                    Array.Copy(cand.StopX, 0, xs2, baseStops, candStops);
                    Array.Copy(cand.StopZ, 0, zs2, baseStops, candStops);

                    var lines2 = new List<TransitLine>(baseLines);
                    var candStopIndices = new int[candStops];
                    for (int i = 0; i < candStops; i++)
                    {
                        candStopIndices[i] = baseStops + i;
                    }
                    lines2.Add(new TransitLine
                    {
                        m_Stops = candStopIndices,
                        m_ExpectedWait = cand.Wait,
                        m_SpeedMetresPerSecond = cand.Speed,
                    });
                    int candidateLine = lines2.Count - 1;

                    TransitNetwork network = SuitabilityTransit.Build(
                        xs2, zs2, total, lines2, walkRadius, boardPenalty);

                    var mappedStop = new int[zoneCount];
                    var mappedDist = new float[zoneCount];
                    SuitabilityTransit.RemapZones(zoneX, zoneZ, zoneCount, roundStop, roundDist,
                        cand.StopX, cand.StopZ, candStops, baseStops, reach, mappedStop, mappedDist);

                    var origins = new int[flowCount];
                    var dests = new int[flowCount];
                    var weights = new float[flowCount];
                    var access = new float[flowCount];
                    var pairBaseline = new float[flowCount];
                    int pairs = 0;
                    for (int i = 0; i < flowCount; i++)
                    {
                        int o = mappedStop[flowOrigin[i]];
                        int d = mappedStop[flowDest[i]];
                        if (o < 0 || d < 0 || o == d)
                        {
                            continue;
                        }
                        origins[pairs] = o;
                        dests[pairs] = d;
                        weights[pairs] = flowWeight[i];
                        access[pairs] = WalkSeconds(mappedDist[flowOrigin[i]])
                            + WalkSeconds(mappedDist[flowDest[i]]);
                        pairBaseline[pairs] = baselines[i];
                        pairs++;
                    }

                    var ws2 = new DijkstraWorkspace(network.Graph.NodeCount);
                    float credit = pairs > 0
                        ? SuitabilityTransit.CreditLine(network, ws2, origins, dests, weights,
                            access, pairBaseline, pairs, candidateLine, discount, maxTravel,
                            margin, out _)
                        : 0f;
                    credits[c] = credit;
                }

                // Accept the best: EnabledDemand desc, ties CapturedFlow desc (the mod's
                // OrderCandidates; its List.Sort tie order is unspecified — the evaluator
                // treats equal keys as a reported tie). Gates are exercised separately by
                // the mode_choice instances; lineset instances declare none.
                int best = -1;
                foreach (int c in Order(credits, candidates))
                {
                    best = c;
                    break;
                }
                if (best < 0)
                {
                    break;
                }

                var roundCredits = new Dictionary<string, object?>();
                for (int c = 0; c < candidates.Count; c++)
                {
                    if (credits[c] is float value)
                    {
                        roundCredits[c.ToString(System.Globalization.CultureInfo.InvariantCulture)] = B32(value);
                    }
                }
                rounds.Add(new Dictionary<string, object?>
                {
                    ["credits_b32"] = roundCredits,
                    ["accepted"] = best,
                });

                settled[best] = true;
                acceptedOrder.Add(best);
                Candidate chosen = candidates[best];
                var stops = new int[chosen.StopX.Length];
                for (int i = 0; i < chosen.StopX.Length; i++)
                {
                    stops[i] = existingCount + acceptedX.Count + i;
                    // Index allocated BEFORE appending, mirroring AcceptIntoNetwork.
                }
                // AcceptIntoNetwork allocates indices existing+acceptedSoFar upward.
                int firstIndex = existingCount + acceptedX.Count;
                for (int i = 0; i < stops.Length; i++)
                {
                    stops[i] = firstIndex + i;
                }
                for (int i = 0; i < chosen.StopX.Length; i++)
                {
                    acceptedX.Add(chosen.StopX[i]);
                    acceptedZ.Add(chosen.StopZ[i]);
                }
                acceptedLines.Add(new TransitLine
                {
                    m_Stops = stops,
                    m_ExpectedWait = chosen.Wait,
                    m_SpeedMetresPerSecond = chosen.Speed,
                });
            }

            return new Dictionary<string, object?>
            {
                ["accepted"] = acceptedOrder,
                ["rounds"] = rounds,
            };
        }

        private static IEnumerable<int> Order(float?[] credits, List<Candidate> candidates)
        {
            var order = new List<int>();
            for (int c = 0; c < credits.Length; c++)
            {
                if (credits[c] is not null)
                {
                    order.Add(c);
                }
            }
            order.Sort((left, right) =>
            {
                float lc = credits[left] ?? 0f;
                float rc = credits[right] ?? 0f;
                if (lc != rc)
                {
                    return rc.CompareTo(lc);
                }
                float lf = candidates[left].CapturedFlow;
                float rf = candidates[right].CapturedFlow;
                if (lf != rf)
                {
                    return rf.CompareTo(lf);
                }
                return left.CompareTo(right);
            });
            return order;
        }
    }
}
