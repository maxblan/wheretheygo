using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TransitArchitect.Verification
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
                "sites_walk" => SitesWalk(data),
                "road_times" => RoadTimes(data),
                "coverage" => Coverage(data),
                "lattice_path" => LatticePath(data),
                "stop_plan" => StopPlan(data),
                "mode_choice" => ModeChoice(data),
                "lineset_time" => LineSetTime(data),
                "line_health" => LineHealthKind(data),
                "corridor" => CorridorGrowth(data),
                "heatmap_grid" => HeatmapGrid(data),
                "heatmap_walk" => HeatmapWalk(data),
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

        // ECS-side WalkSeconds, mirrored: (float)Math.Sqrt(double) / WalkSpeed. The speed
        // is the mod's own constant, not a copy of it — a copy drifted once (1.4 after
        // the mod moved to 1.2) and made a real-city run fail for a reason that had
        // nothing to do with the mod.
        private static float WalkSeconds(float distSq)
        {
            return (float)Math.Sqrt(distSq) / Assumptions.WalkSpeed;
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

            ExactSiteSolution exact = SuitabilityExactSites.Solve(
                scores, width, height, separation, maxSites, Assumptions.SiteSearchNodeBudget);

            return new Dictionary<string, object?>
            {
                ["site_indices"] = idx,
                ["site_scores_b32"] = Bits(outScores, count),
                ["truncated"] = truncated,
                ["exact_site_indices"] = new List<int>(exact.Indices),
                ["exact_site_scores_b32"] = Bits(exact.Scores, exact.Count),
                ["exact_optimal"] = exact.Optimal,
                ["exact_weights_exact"] = exact.WeightsExact,
                ["exact_value_scaled"] = exact.Value,
                ["exact_upper_bound_scaled"] = exact.UpperBound,
                ["exact_scale_shift"] = exact.ScaleShift,
                ["exact_nodes"] = exact.Nodes,
                ["exact_candidates"] = exact.Candidates,
            };
        }

        // ---------------------------------------------------------- S3 v2 coverage

        private static Dictionary<string, object?> Coverage(JsonElement data)
        {
            float[] nodeX = F32Array(data.GetProperty("node_x_b32"));
            WalkGraph graph = WalkGraph.Build(
                nodeX, F32Array(data.GetProperty("node_z_b32")),
                IntArray(data.GetProperty("edge_a")), IntArray(data.GetProperty("edge_b")),
                F32Array(data.GetProperty("edge_metres_b32")), data.GetProperty("edge_a").GetArrayLength());
            int accessMs = data.GetProperty("access_ms").GetInt32();
            int horizonMs = data.GetProperty("horizon_ms").GetInt32();
            var index = new WalkNodeIndex(graph, Math.Max(32.0, accessMs / 1000.0 * Assumptions.WalkSpeed));

            float[] sx = F32Array(data.GetProperty("stop_x_b32"));
            float[] sz = F32Array(data.GetProperty("stop_z_b32"));
            var stopNodes = new int[sx.Length];
            var stopAccess = new int[sx.Length];
            for (int i = 0; i < sx.Length; i++)
            {
                stopNodes[i] = SuitabilityWalkAccess.SnapPoint(index, sx[i], sz[i], accessMs, out stopAccess[i]);
            }

            float[] ox = F32Array(data.GetProperty("trip_ox_b32"));
            float[] oz = F32Array(data.GetProperty("trip_oz_b32"));
            float[] dx = F32Array(data.GetProperty("trip_dx_b32"));
            float[] dz = F32Array(data.GetProperty("trip_dz_b32"));
            float[] w = F32Array(data.GetProperty("trip_w_b32"));
            int count = w.Length;
            var on = new int[count];
            var oa = new int[count];
            var dn = new int[count];
            var da = new int[count];
            for (int i = 0; i < count; i++)
            {
                on[i] = SuitabilityWalkAccess.SnapPoint(index, ox[i], oz[i], accessMs, out oa[i]);
                dn[i] = SuitabilityWalkAccess.SnapPoint(index, dx[i], dz[i], accessMs, out da[i]);
            }

            var dijkstra = new IntDijkstra(graph.NodeCount);
            int[] served = Equity.ServedWalkMs(graph, dijkstra, stopNodes, stopAccess, stopNodes.Length, horizonMs);
            CoverageReport report = Equity.Coverage(served, horizonMs, on, oa, dn, da, w, count);
            return new Dictionary<string, object?>
            {
                ["share_b32"] = B32(report.Share),
                ["covered_weight"] = report.CoveredWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ["total_weight"] = report.TotalWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ["gini_walk"] = report.GiniWalk.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ["trips_covered"] = report.TripsCovered,
                ["trips_off_network"] = report.TripsOffNetwork,
            };
        }

        // --------------------------------------------------------- S4 v2 road_times

        // Rebuilds the directed road graph from the exported streets (recomputing every
        // arc time and the turn table from their inputs) and answers the exported legs
        // with the mod's own DirectedDijkstra.
        private static Dictionary<string, object?> RoadTimes(JsonElement data)
        {
            float[] nodeX = F32Array(data.GetProperty("node_x_b32"));
            float[] nodeZ = F32Array(data.GetProperty("node_z_b32"));
            int[] from = IntArray(data.GetProperty("arc_from"));
            int[] to = IntArray(data.GetProperty("arc_to"));
            int[] edge = IntArray(data.GetProperty("arc_edge"));
            float[] metres = F32Array(data.GetProperty("arc_metres_b32"));
            float[] speed = F32Array(data.GetProperty("arc_speed_b32"));
            var ms = new int[from.Length];
            for (int a = 0; a < from.Length; a++)
            {
                ms[a] = DirectedRoadGraph.ArcMilliseconds(metres[a], speed[a]);
            }

            int[] turnMs = DirectedRoadGraph.TurnTable(F32(data.GetProperty("turn_seconds_per_radian_b32")));
            DirectedRoadGraph graph = DirectedRoadGraph.Build(
                nodeX, nodeZ, from, to, edge, ms,
                F32Array(data.GetProperty("out_dx_b32")), F32Array(data.GetProperty("out_dz_b32")),
                F32Array(data.GetProperty("in_dx_b32")), F32Array(data.GetProperty("in_dz_b32")), turnMs, from.Length);
            long maxMs = data.GetProperty("max_ms").GetInt64();

            float[] fromX = F32Array(data.GetProperty("leg_from_x_b32"));
            float[] fromZ = F32Array(data.GetProperty("leg_from_z_b32"));
            float[] toX = F32Array(data.GetProperty("leg_to_x_b32"));
            float[] toZ = F32Array(data.GetProperty("leg_to_z_b32"));
            float snap = F32(data.GetProperty("snap_metres_b32"));
            var dijkstra = new DirectedDijkstra(graph.ArcCount);
            var legs = new List<object>();
            for (int i = 0; i < fromX.Length; i++)
            {
                long time = RoadLegs.PointToPointMs(graph, dijkstra, fromX[i], fromZ[i], toX[i], toZ[i], snap, maxMs, out RoadLeg leg);
                var arcs = new List<int>();
                if (time != DirectedDijkstra.Unreached && !leg.SameArc)
                {
                    // Replay the chosen pairing to expose the arc chain for the certificate.
                    dijkstra.RunFromArc(graph, leg.FromArc, leg.StartMs, maxMs);
                    _ = dijkstra.TimeToPoint(graph, leg.ToArc, leg.EndMs, out int via);
                    while (via >= 0)
                    {
                        arcs.Add(via);
                        via = dijkstra.PrevArc[via];
                    }

                    arcs.Reverse();
                }

                legs.Add(new Dictionary<string, object?>
                {
                    ["leg"] = i,
                    ["ms"] = time == DirectedDijkstra.Unreached ? -1L : time,
                    ["from_arc"] = leg.FromArc,
                    ["to_arc"] = leg.ToArc,
                    ["start_ms"] = leg.StartMs,
                    ["end_ms"] = leg.EndMs,
                    ["same_arc"] = leg.SameArc,
                    ["arcs"] = arcs,
                });
            }

            return new Dictionary<string, object?>
            {
                ["arc_ms"] = new List<int>(ms),
                ["turn_ms"] = new List<int>(turnMs),
                ["legs"] = legs,
            };
        }

        // ------------------------------------------------------ S2 v2 sites_walk

        private static Dictionary<string, object?> SitesWalk(JsonElement data)
        {
            float[] nodeX = F32Array(data.GetProperty("node_x_b32"));
            WalkGraph graph = WalkGraph.Build(
                nodeX, F32Array(data.GetProperty("node_z_b32")),
                IntArray(data.GetProperty("edge_a")), IntArray(data.GetProperty("edge_b")),
                F32Array(data.GetProperty("edge_metres_b32")), data.GetProperty("edge_a").GetArrayLength());
            int[] candidates = IntArray(data.GetProperty("candidate_nodes"));
            float[] scores = F32Array(data.GetProperty("candidate_scores_b32"));
            int separationMs = data.GetProperty("separation_ms").GetInt32();
            int maxSites = data.GetProperty("max_sites").GetInt32();

            ExactSiteSolution exact = SuitabilityExactSites.SolveOnNetwork(
                graph, candidates, scores, candidates.Length, separationMs, maxSites, Assumptions.SiteSearchNodeBudget);
            // The greedy baseline under the same conflicts: a budget of zero search
            // nodes leaves the solver with exactly its greedy incumbent.
            ExactSiteSolution greedy = SuitabilityExactSites.SolveOnNetwork(
                graph, candidates, scores, candidates.Length, separationMs, maxSites, 0);

            return new Dictionary<string, object?>
            {
                ["site_nodes"] = new List<int>(exact.Indices),
                ["site_scores_b32"] = Bits(exact.Scores, exact.Count),
                ["optimal"] = exact.Optimal,
                ["weights_exact"] = exact.WeightsExact,
                ["value_scaled"] = exact.Value,
                ["upper_bound_scaled"] = exact.UpperBound,
                ["scale_shift"] = exact.ScaleShift,
                ["search_nodes"] = exact.Nodes,
                ["greedy_nodes"] = new List<int>(greedy.Indices),
            };
        }

        // ------------------------------------------------------- S1 v2 heatmap_walk

        // Runs the mod's own access pass (SuitabilityWalkAccess.Run — the code the game
        // executes on its worker thread) on the exported inputs and reports the terms at
        // the sampled tiles, so the pipeline can require game == subject == evaluator.
        private static Dictionary<string, object?> HeatmapWalk(JsonElement data)
        {
            float[] nodeX = F32Array(data.GetProperty("node_x_b32"));
            float[] nodeZ = F32Array(data.GetProperty("node_z_b32"));
            int[] edgeA = IntArray(data.GetProperty("edge_a"));
            int[] edgeB = IntArray(data.GetProperty("edge_b"));
            float[] edgeMetres = F32Array(data.GetProperty("edge_metres_b32"));
            // Instances before 2026-09-05 evening carry no siteable flags: every node
            // was a site then, and their tile snaps are reproduced as such.
            WalkGraph graph = data.TryGetProperty("node_siteable", out JsonElement siteable)
                ? WalkGraph.Build(nodeX, nodeZ, edgeA, edgeB, edgeMetres, edgeA.Length, BoolArray(siteable))
                : WalkGraph.Build(nodeX, nodeZ, edgeA, edgeB, edgeMetres, edgeA.Length);

            float[] stopX = F32Array(data.GetProperty("stop_x_b32"));
            var inputs = new WalkAccessInputs
            {
                Graph = graph,
                Homes = Sources(data.GetProperty("homes")),
                Jobs = Sources(data.GetProperty("jobs")),
                Future = Sources(data.GetProperty("future")),
                StopX = stopX,
                StopZ = F32Array(data.GetProperty("stop_z_b32")),
                StopType = IntArray(data.GetProperty("stop_type")),
                StopCount = stopX.Length,
                TypeCount = data.GetProperty("type_count").GetInt32(),
                AccessMs = data.GetProperty("access_ms").GetInt32(),
                TransferMs = data.GetProperty("transfer_ms").GetInt32(),
                CatchmentMs = IntArray(data.GetProperty("catchment_ms")),
            };

            int width = data.GetProperty("grid_x").GetInt32();
            int height = data.GetProperty("grid_y").GetInt32();
            int[] buildableInts = IntArray(data.GetProperty("buildable"));
            var buildable = new byte[buildableInts.Length];
            for (int i = 0; i < buildable.Length; i++)
            {
                buildable[i] = (byte)buildableInts[i];
            }

            WalkAccessOutput output = SuitabilityWalkAccess.Run(
                inputs, width, height,
                F32(data.GetProperty("world_min_x_b32")), F32(data.GetProperty("world_min_z_b32")), F32(data.GetProperty("tile_size_b32")),
                buildable, data.GetProperty("class").GetInt32(), data.GetProperty("self_type").GetInt32(),
                F32Array(data.GetProperty("type_weight_b32")));

            var cells = new List<object>();
            foreach (JsonElement cell in data.GetProperty("sample_indices").EnumerateArray())
            {
                int index = cell.GetInt32();
                SuitabilityCell terms = output.Terms[index];
                cells.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["demand_b32"] = B32(terms.m_Demand),
                    ["jobs_b32"] = B32(terms.m_Jobs),
                    ["coverage_b32"] = B32(terms.m_Coverage),
                    ["access_b32"] = B32(terms.m_Access),
                    ["future_b32"] = B32(terms.m_Future),
                    ["interchange_b32"] = B32(terms.m_Interchange),
                    ["cross_b32"] = B32(terms.m_CrossCoverage),
                    ["tile_node"] = output.TileNode[index],
                    ["tile_walk_ms"] = output.TileWalkMs[index],
                });
            }

            return new Dictionary<string, object?>
            {
                ["cells"] = cells,
                ["edge_ms"] = new List<int>(graph.EdgeMs),
                ["sources_off_network"] = output.Result.SourcesOffNetwork,
                ["tiles_on_network"] = output.TilesOnNetwork,
            };
        }

        private static WalkSources Sources(JsonElement e)
        {
            float[] x = F32Array(e.GetProperty("x_b32"));
            return new WalkSources
            {
                X = x,
                Z = F32Array(e.GetProperty("z_b32")),
                Weight = F32Array(e.GetProperty("w_b32")),
                Count = x.Length,
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

        // Re-solves every exported stop plan with the mod's own dynamic programme.
        private static Dictionary<string, object?> StopPlan(JsonElement data)
        {
            var plans = new List<object>();
            foreach (JsonElement plan in data.GetProperty("plans").EnumerateArray())
            {
                var problem = new StopPlanProblem
                {
                    CandidateAt = F32Array(plan.GetProperty("candidate_at_b32")),
                    CandidateX = F32Array(plan.GetProperty("candidate_x_b32")),
                    CandidateZ = F32Array(plan.GetProperty("candidate_z_b32")),
                    MustCall = BoolArray(plan.GetProperty("must_call")),
                    ThroughFlow = F32Array(plan.GetProperty("through_flow_b32")),
                    EndAt = F32Array(plan.GetProperty("end_at_b32")),
                    EndX = F32Array(plan.GetProperty("end_x_b32")),
                    EndZ = F32Array(plan.GetProperty("end_z_b32")),
                    EndWeight = F32Array(plan.GetProperty("end_w_b32")),
                    MinGapMetres = F32(plan.GetProperty("min_gap_b32")),
                    DelaySecondsPerStop = F32(plan.GetProperty("delay_per_stop_b32")),
                    AccessHorizonSeconds = F32(plan.GetProperty("horizon_b32")),
                    WalkMetresPerSecond = F32(plan.GetProperty("walk_speed_b32")),
                };
                problem.CandidateCount = problem.CandidateAt.Length;
                problem.EndCount = problem.EndAt.Length;
                StopPlanSolution solution = StopPlanning.Solve(problem);
                plans.Add(new Dictionary<string, object?>
                {
                    ["chosen"] = solution.Chosen,
                    ["gain"] = solution.Gain.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    ["delay"] = solution.Delay.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    ["value"] = solution.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            return new Dictionary<string, object?> { ["plans"] = plans };
        }

        // ------------------------------------------------------- S6 mode choice

        // The prefab facts and the vehicle-count policy of an instance (mode_choice and
        // line_health carry the same "facts"/"policy" objects).
        private static FleetFacts FactsOf(JsonElement data)
        {
            var byMode = new ModeFacts[TransitModes.All.Length];
            for (int m = 0; m < byMode.Length; m++)
            {
                byMode[m] = new ModeFacts();
            }

            foreach (JsonProperty p in data.GetProperty("facts").EnumerateObject())
            {
                var mode = (ModePreset)Enum.Parse(typeof(ModePreset), p.Name);
                byMode[(int)mode] = new ModeFacts
                {
                    Capacity = F32(p.Value.GetProperty("capacity_b32")),
                    PrefabIntervalSeconds = F32(p.Value.GetProperty("prefab_interval_b32")),
                    StopDurationSeconds = F32(p.Value.GetProperty("stop_duration_b32")),
                    Acceleration = p.Value.TryGetProperty("acceleration_b32", out JsonElement a) ? F32(a) : 0f,
                    Braking = p.Value.TryGetProperty("braking_b32", out JsonElement b) ? F32(b) : 0f,
                };
            }

            var policy = new VehicleCountPolicy();
            if (data.TryGetProperty("policy", out JsonElement po) && po.ValueKind == JsonValueKind.Object)
            {
                policy.Known = po.GetProperty("known").GetBoolean();
                policy.Mode = (IntervalModifierMode)Enum.Parse(typeof(IntervalModifierMode), po.GetProperty("mode").GetString() ?? "Relative");
                policy.DeltaMin = F32(po.GetProperty("delta_min_b32"));
                policy.DeltaMax = F32(po.GetProperty("delta_max_b32"));
            }

            return new FleetFacts(byMode, policy);
        }

        // The capacity ladder (S6 v3): riders per day against each mode's seats at the
        // fleet the riders call for within the game's span, from the instance's facts.
        private static Dictionary<string, object?> ModeChoice(JsonElement data)
        {
            FleetFacts facts = FactsOf(data);
            var rows = new List<object>();
            foreach (JsonElement row in data.GetProperty("rows").EnumerateArray())
            {
                var network = (RouteNetwork)Enum.Parse(typeof(RouteNetwork), row.GetProperty("network").GetString() ?? "Road");
                float roundTrip = F32(row.GetProperty("round_trip_b32"));
                float riders = F32(row.GetProperty("riders_b32"));
                // No split in the instance means no period information: only the whole
                // day binds (the period requirement of zero riders is one vehicle).
                float ridersDay = row.TryGetProperty("riders_day_b32", out JsonElement rd) ? F32(rd) : 0f;
                float ridersNight = row.TryGetProperty("riders_night_b32", out JsonElement rn) ? F32(rn) : 0f;
                bool ok = TransitModes.ChooseMode(network, riders, ridersDay, ridersNight, facts, _ => roundTrip, out ModePreset mode, out FleetPlan fleet);
                rows.Add(new Dictionary<string, object?>
                {
                    ["ok"] = ok,
                    ["mode"] = mode.ToString(),
                    ["fleet"] = fleet.Vehicles,
                    ["fleet_min"] = fleet.Min,
                    ["fleet_max"] = fleet.Max == int.MaxValue ? 0 : fleet.Max,
                    ["headway_b32"] = B32(fleet.HeadwaySeconds),
                    ["utilisation_b32"] = B32(fleet.Utilisation),
                    ["day_utilisation_b32"] = B32(fleet.DayUtilisation),
                    ["night_utilisation_b32"] = B32(fleet.NightUtilisation),
                    ["delay_per_stop_b32"] = B32(facts.DelayPerStopSeconds(mode)),
                });
            }

            return new Dictionary<string, object?> { ["rows"] = rows };
        }

        // ------------------------------------------------------- S9 line health

        // Rebuilds the captured line-health pass — the lines as collected, the window's
        // readings, the routing's riders, the facts and floors — and judges it with the
        // mod's own LineHealthRules.JudgeAll.
        private static Dictionary<string, object?> LineHealthKind(JsonElement data)
        {
            var problem = new LineHealthProblem
            {
                Facts = FactsOf(data),
                UtilisationFloor = F32(data.GetProperty("utilisation_floor_b32")),
                UtilisationCeiling = F32(data.GetProperty("utilisation_ceiling_b32")),
                TargetLoad = F32(data.GetProperty("target_load_b32")),
                WindowFrames = data.GetProperty("window_frames").GetUInt32(),
            };
            foreach (JsonElement l in data.GetProperty("lines").EnumerateArray())
            {
                var line = new ExistingLine
                {
                    m_Id = l.GetProperty("id").GetInt32(),
                    m_Name = l.GetProperty("name").GetString() ?? string.Empty,
                    m_Mode = (ModePreset)Enum.Parse(typeof(ModePreset), l.GetProperty("mode").GetString() ?? "Bus"),
                    m_LengthMetres = F32(l.GetProperty("loop_metres_b32")),
                    m_StableDurationSeconds = F32(l.GetProperty("round_trip_b32")),
                    m_TargetInterval = F32(l.GetProperty("target_interval_b32")),
                    m_VehicleInterval = F32(l.GetProperty("game_interval_b32")),
                    m_StopDuration = F32(l.GetProperty("stop_duration_b32")),
                    m_Vehicles = l.GetProperty("vehicles").GetInt32(),
                    m_Passengers = l.GetProperty("passengers").GetInt32(),
                    m_Capacity = l.GetProperty("capacity").GetInt32(),
                    m_RequireVehicles = l.GetProperty("require_vehicles").GetBoolean(),
                    m_NotEnoughVehicles = l.GetProperty("not_enough_vehicles").GetBoolean(),
                    m_Schedule = (LineSchedule)Enum.Parse(typeof(LineSchedule), l.GetProperty("schedule").GetString() ?? "DayAndNight"),
                    m_RidersPerDay = F32(l.GetProperty("riders_b32")),
                    m_RidersByDay = F32(l.GetProperty("riders_day_b32")),
                    m_RidersByNight = F32(l.GetProperty("riders_night_b32")),
                };
                int stops = l.GetProperty("stops").GetInt32();
                for (int i = 0; i < stops; i++)
                {
                    line.m_StopIndices.Add(i);
                }

                int[] passengers = IntArray(l.GetProperty("sample_passengers"));
                int[] capacity = IntArray(l.GetProperty("sample_capacity"));
                int[] vehicles = IntArray(l.GetProperty("sample_vehicles"));
                float[] intervals = F32Array(l.GetProperty("sample_interval_b32"));
                float[] clock = F32Array(l.GetProperty("sample_time_of_day_b32"));
                var frames = new List<uint>();
                foreach (JsonElement f in l.GetProperty("sample_frame").EnumerateArray())
                {
                    frames.Add((uint)f.GetInt64());
                }

                var samples = new LineObservation[frames.Count];
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = new LineObservation
                    {
                        m_Frame = frames[i],
                        m_Passengers = passengers[i],
                        m_Capacity = capacity[i],
                        m_Vehicles = vehicles[i],
                        m_IntervalSeconds = intervals[i],
                        m_TimeOfDay = clock[i],
                    };
                }

                problem.Lines.Add(line);
                problem.Samples[line.m_Id] = samples;
            }

            var health = new List<LineHealth>();
            HealthReference reference = LineHealthRules.JudgeAll(problem, health);
            var verdicts = new List<object>();
            foreach (LineHealth h in health)
            {
                verdicts.Add(new Dictionary<string, object?>
                {
                    ["id"] = h.m_Id,
                    ["verdict"] = h.m_Verdict.ToString(),
                    ["mode"] = h.m_RecommendedMode.ToString(),
                    ["fleet"] = h.m_RecommendedFleet,
                    ["fleet_min"] = h.m_FleetMin,
                    ["fleet_max"] = h.m_FleetMax == int.MaxValue ? 0 : h.m_FleetMax,
                    ["round_trip_b32"] = B32(h.m_RoundTripSeconds),
                    ["headway_b32"] = B32(h.m_HeadwaySeconds),
                    ["utilisation_b32"] = B32(h.m_Utilisation),
                    ["day_utilisation_b32"] = B32(h.m_DayUtilisation),
                    ["night_utilisation_b32"] = B32(h.m_NightUtilisation),
                    ["advice"] = h.m_ScheduleAdvice.ToString(),
                    ["planning_load"] = h.m_PlanningLoad,
                    ["usage_b32"] = B32(h.m_Usage),
                    ["peak_usage_b32"] = B32(h.m_PeakUsage),
                    ["target_vehicles"] = h.m_TargetVehicles,
                    ["window_samples"] = h.m_WindowSamples,
                });
            }

            return new Dictionary<string, object?>
            {
                ["verdicts"] = verdicts,
                ["median_usage_b32"] = B32(reference.m_MedianUsage),
                ["empty_threshold_b32"] = B32(reference.m_EmptyThreshold),
            };
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
            float meanFlow = GraphMath.MeanPositiveFlow(flow, graph.EdgeCount);
            float noveltyWeight = GraphMath.NoveltyWeight(objective, meanFlow);
            float seedBias = GraphMath.SeedNoveltyBias(objective);
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
                bool ok = GraphMath.GrowCorridor(
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
                    GraphMath.PeelFlow(graph, corridor, flow, used, 1f);
                }
                else
                {
                    GraphMath.PeelFlow(graph, corridor, flow, used, capture);
                    GraphMath.DecayNovelty(graph, corridor, novelty, hops, factor);
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

        // ------------------------------------------------------ S7 v2 lineset_time

        // Rebuilds the exported line-set problem and solves it with the mod's own
        // branch-and-bound. The equity term, when the instance carries the walking
        // network, is the coverage share of the embedded journeys with the chosen
        // candidates' stops added — the same Equity calls the system makes.
        private static Dictionary<string, object?> LineSetTime(JsonElement data)
        {
            var problem = new LineSetProblem
            {
                WalkRadius = F32(data.GetProperty("walk_radius_b32")),
                BoardPenaltySeconds = F32(data.GetProperty("board_penalty_b32")),
                MaxTravelSeconds = F32(data.GetProperty("max_travel_seconds_b32")),
                ZoneReachMetres = F32(data.GetProperty("zone_reach_b32")),
                PairOx = F32Array(data.GetProperty("pair_ox_b32")),
                PairOz = F32Array(data.GetProperty("pair_oz_b32")),
                PairDx = F32Array(data.GetProperty("pair_dx_b32")),
                PairDz = F32Array(data.GetProperty("pair_dz_b32")),
                PairWeight = F32Array(data.GetProperty("pair_w_b32")),
                BaseStopX = F32Array(data.GetProperty("base_stop_x_b32")),
                BaseStopZ = F32Array(data.GetProperty("base_stop_z_b32")),
                MaxLines = data.GetProperty("max_lines").GetInt32(),
                UtilisationFloor = F32(data.GetProperty("utilisation_floor_b32")),
                UtilisationCeiling = data.TryGetProperty("utilisation_ceiling_b32", out JsonElement ceiling) ? F32(ceiling) : 0f,
                MovementSecondsPerDay = F32(data.GetProperty("movement_seconds_per_day_b32")),
                DuplicateShare = F32(data.GetProperty("duplicate_share_b32")),
                EquityFloorShare = F32(data.GetProperty("equity_floor_share_b32")),
            };
            problem.PairCount = problem.PairWeight.Length;
            problem.BaseStopCount = problem.BaseStopX.Length;
            foreach (JsonElement line in data.GetProperty("base_lines").EnumerateArray())
            {
                problem.BaseLines.Add(new TransitLine
                {
                    m_Stops = IntArray(line.GetProperty("stops")),
                    m_ExpectedWait = F32(line.GetProperty("expected_wait_b32")),
                    m_SpeedMetresPerSecond = F32(line.GetProperty("speed_b32")),
                    m_RideSeconds = OptionalF32Array(line, "ride_seconds_b32"),
                });
            }

            foreach (JsonElement c in data.GetProperty("candidates").EnumerateArray())
            {
                problem.Candidates.Add(new LineCandidate
                {
                    StopX = F32Array(c.GetProperty("stop_x_b32")),
                    StopZ = F32Array(c.GetProperty("stop_z_b32")),
                    ExpectedWait = F32(c.GetProperty("expected_wait_b32")),
                    SpeedMetresPerSecond = F32(c.GetProperty("speed_b32")),
                    RideSeconds = OptionalF32Array(c, "ride_seconds_b32"),
                    RoundTripSeconds = F32(c.GetProperty("round_trip_b32")),
                    FleetMin = c.GetProperty("fleet_min").GetInt32(),
                    FleetMax = c.GetProperty("fleet_max").GetInt32() == 0 ? int.MaxValue : c.GetProperty("fleet_max").GetInt32(),
                    VehicleCapacity = F32(c.GetProperty("capacity_b32")),
                    Group = c.TryGetProperty("group", out JsonElement group) ? group.GetInt32() : -1,
                });
            }

            if (data.TryGetProperty("equity", out JsonElement equity) && equity.ValueKind == JsonValueKind.Object)
            {
                problem.CoverageOf = EquityCoverage(equity, problem);
            }

            // The same wall-clock regime as the mod's worker: past the budget the search
            // reports its best set and ceiling. SUBJECT_LINESET_BUDGET_SECONDS overrides
            // the mod's 90 s; 0 means unbounded.
            string? budgetText = Environment.GetEnvironmentVariable("SUBJECT_LINESET_BUDGET_SECONDS");
            double budgetSeconds = double.TryParse(budgetText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : 90.0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LineSetSolution solution;
            if (budgetSeconds > 0.0)
            {
                using var budget = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds));
                solution = LineSet.Solve(problem, Assumptions.LineSetNodeBudget, budget.Token);
            }
            else
            {
                solution = LineSet.Solve(problem, Assumptions.LineSetNodeBudget);
            }

            long elapsedMs = clock.ElapsedMilliseconds;
            var standalone = new List<string>();
            foreach (double saved in solution.StandaloneTimeSaved)
            {
                standalone.Add(saved.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }

            return new Dictionary<string, object?>
            {
                ["chosen"] = solution.Chosen,
                ["time_saved"] = solution.TimeSaved.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ["upper_bound_time_saved"] = solution.UpperBoundTimeSaved.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ["coverage_b32"] = B32(solution.Coverage),
                ["optimal"] = solution.Optimal,
                ["nodes"] = solution.Nodes,
                ["evaluations"] = solution.Evaluations,
                ["elapsed_ms"] = elapsedMs,
                ["infeasible"] = solution.Infeasible,
                ["standalone_time_saved"] = standalone,
            };
        }

        private static float[]? OptionalF32Array(JsonElement owner, string name)
        {
            return owner.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Array ? F32Array(e) : null;
        }

        // Mirrors TransitArchitectSystem.CoverageWith: snap the chosen candidates'
        // stops, merge their walking times into the served map, share of the journeys.
        private static Func<int[], int, float> EquityCoverage(JsonElement equity, LineSetProblem problem)
        {
            WalkGraph graph = WalkGraph.Build(
                F32Array(equity.GetProperty("node_x_b32")), F32Array(equity.GetProperty("node_z_b32")),
                IntArray(equity.GetProperty("edge_a")), IntArray(equity.GetProperty("edge_b")),
                F32Array(equity.GetProperty("edge_metres_b32")), equity.GetProperty("edge_a").GetArrayLength());
            int accessMs = equity.GetProperty("access_ms").GetInt32();
            int horizonMs = equity.GetProperty("horizon_ms").GetInt32();
            var index = new WalkNodeIndex(graph, Math.Max(32.0, accessMs / 1000.0 * Assumptions.WalkSpeed));
            var dijkstra = new IntDijkstra(graph.NodeCount);

            float[] sx = F32Array(equity.GetProperty("stop_x_b32"));
            float[] sz = F32Array(equity.GetProperty("stop_z_b32"));
            SnapAll(index, sx, sz, accessMs, out int[] stopNodes, out int[] stopAccess);
            int[] served = Equity.ServedWalkMs(graph, dijkstra, stopNodes, stopAccess, stopNodes.Length, horizonMs);

            float[] w = F32Array(equity.GetProperty("trip_w_b32"));
            SnapAll(index, F32Array(equity.GetProperty("trip_ox_b32")), F32Array(equity.GetProperty("trip_oz_b32")), accessMs, out int[] on, out int[] oa);
            SnapAll(index, F32Array(equity.GetProperty("trip_dx_b32")), F32Array(equity.GetProperty("trip_dz_b32")), accessMs, out int[] dn, out int[] da);

            return (chosen, count) =>
            {
                var xs = new List<float>();
                var zs = new List<float>();
                for (int k = 0; k < count; k++)
                {
                    xs.AddRange(problem.Candidates[chosen[k]].StopX);
                    zs.AddRange(problem.Candidates[chosen[k]].StopZ);
                }

                SnapAll(index, xs.ToArray(), zs.ToArray(), accessMs, out int[] nodes, out int[] access);
                int[] merged = Equity.WithStops(graph, dijkstra, served, nodes, access, nodes.Length, horizonMs);
                return Equity.Coverage(merged, horizonMs, on, oa, dn, da, w, w.Length).Share;
            };
        }

        private static void SnapAll(WalkNodeIndex index, float[] xs, float[] zs, int accessMs, out int[] nodes, out int[] access)
        {
            nodes = new int[xs.Length];
            access = new int[xs.Length];
            for (int i = 0; i < xs.Length; i++)
            {
                nodes[i] = SuitabilityWalkAccess.SnapPoint(index, xs[i], zs[i], accessMs, out access[i]);
            }
        }
    }
}
