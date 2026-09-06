using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // F7 — the line-set selection on the game side: every candidate weighed alone,
    // the set problem built from the journeys and the existing network, the exact
    // search run under its time budget, and the chosen set adopted with its riders,
    // utilisation and schedule. The optimisation itself is SuitabilityLineSet.
    public sealed partial class StationSuitabilityOverlaySystem
    {

        // The walk to and from the candidate's own stops, and what each journey costs
        // on the network as it stands. A line earns credit for a journey only by being
        // better than what the rider already has.
        // Door-to-door seconds per zone flow on the existing network, or MaxValue where
        // it cannot carry the journey at all.
        // The suggestions accepted so far this refresh, treated as though the player
        // had built them. Every later candidate is scored against a network that
        // already contains them, so two lines cannot both be credited with the same
        // journeys — which is how four near-parallel metros came to be suggested at
        // once, each one claiming the riders of the others.
        private readonly List<float2Like> m_AcceptedStops = new List<float2Like>();

        private readonly List<TransitLine> m_AcceptedLines = new List<TransitLine>();

        private LineSetSolution? m_LineSetSolution;

        private readonly List<SuggestedRoute> m_LineSetResolved = new List<SuggestedRoute>();

        private LineSetProblem? m_LineSetProblem;

        // The suggestions as a SET (register A7.5, decided 2026-09-05): every candidate is
        // first weighed alone — its riders feed the mode decision and the log — then the
        // resolved candidates are handed to the exact line-set search, which maximises
        // (equity share up to the floor, then passenger time saved) over sets of at most
        // RouteCount lines under the utilisation and duplicate rules. Nothing is taken
        // greedily: a feeder that pays only next to its trunk is found with it.
        private void SelectRoutes(Setting settings, int2 gridSize, int grownTotal, int shortTotal, RoutePass pass)
        {
            var tally = new RejectionTally();
            var scratch = new List<int>();
            FleetFacts facts = ReadFleetFacts();
            StopContext stops = BuildStopContext();

            var phases = System.Diagnostics.Stopwatch.StartNew();
            EnsureBaseline(settings, pass);
            WeighCandidatesAlone(settings, pass.Candidates, facts, pass);
            pass.WeighMs = phases.ElapsedMilliseconds;
            phases.Restart();
            List<SuggestedRoute> resolved = pass.Resolved;
            for (int i = 0; i < pass.Candidates.Count; i++)
            {
                ResolveCandidate(settings, pass.Candidates[i], i, facts, stops, scratch, tally, resolved, pass);
            }

            pass.ResolveMs = phases.ElapsedMilliseconds;
            int dropped = DropIdenticalCandidates(resolved);
            if (dropped > 0)
            {
                DeferredLog.Info($"  {(dropped).ToString(CultureInfo.InvariantCulture)} candidate(s) dropped as identical to another (same mode, same stops)");
            }

            if (resolved.Count > 0)
            {
                LineSetProblem problem = BuildLineSetProblem(settings, resolved, settings.RouteCount, pass);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                LineSetSolution solution;
                using (var budget = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(Assumptions.LineSetTimeBudgetSeconds)))
                {
                    solution = SuitabilityLineSet.Solve(problem, Assumptions.LineSetNodeBudget, budget.Token);
                }

                stopwatch.Stop();
                pass.SolveMs = stopwatch.ElapsedMilliseconds;
                pass.Solution = solution;
                pass.Problem = problem;
                AdoptLineSet(settings, resolved, problem, solution, stopwatch.ElapsedMilliseconds, pass);
            }

            DeferredLog.Info(
                $"Route suggestions: grown={(grownTotal).ToString(CultureInfo.InvariantCulture)}, tooShort={(shortTotal).ToString(CultureInfo.InvariantCulture)}, " +
                $"candidates={pass.Candidates.Count}, resolved={(resolved.Count).ToString(CultureInfo.InvariantCulture)}, unjustified={(tally.Unjustified).ToString(CultureInfo.InvariantCulture)}, " +
                $"retracedOnRoad={(tally.Retraced).ToString(CultureInfo.InvariantCulture)}, alreadyBuilt={(tally.AlreadyBuilt).ToString(CultureInfo.InvariantCulture)}, " +
                $"kept={pass.Routes.Count}; assigned {(pass.AssignedPairs).ToString(CultureInfo.InvariantCulture)} pairs / {(pass.AssignedWeight).ToString("F0", CultureInfo.InvariantCulture)} weight; " +
                $"worker phases: flow assignment {(pass.AssignMs).ToString(CultureInfo.InvariantCulture)} ms, alignments+stops {(pass.AlignmentMs - pass.AssignMs).ToString(CultureInfo.InvariantCulture)} ms, " +
                $"weighing {(pass.WeighMs).ToString(CultureInfo.InvariantCulture)} ms, resolving {(pass.ResolveMs).ToString(CultureInfo.InvariantCulture)} ms, " +
                $"set search {(pass.SolveMs).ToString(CultureInfo.InvariantCulture)} ms");
        }

        // The network as it stands, routed once per pass: every pair's door-to-door time
        // (the `before` of the objective) and the riders each EXISTING line carries,
        // which the line verdicts read as its demand (register A8.2).
        private void EnsureBaseline(Setting settings, RoutePass pass)
        {
            if (pass.BaselineEvaluation is not null)
            {
                return;
            }

            LineSetProblem probe = BuildLineSetProblem(settings, new List<SuggestedRoute>(), 0, pass);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            LineSetEvaluation baseline = SuitabilityLineSet.Evaluate(probe, Array.Empty<int>(), 0, before: null);
            pass.BaselineEvaluation = baseline;
            pass.Baseline = baseline.After;
            DeferredLog.Info(
                $"Baseline door-to-door times for {(probe.PairCount).ToString(CultureInfo.InvariantCulture)} pairs from " +
                $"{(SuitabilityLineSet.GeometryOf(probe).ZoneCount).ToString(CultureInfo.InvariantCulture)} zones over {(probe.BaseLines.Count).ToString(CultureInfo.InvariantCulture)} existing lines in {(stopwatch.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms");
            for (int i = 0; i < probe.BaseLines.Count && i < m_ExistingLines.Count; i++)
            {
                DeferredLog.Info(
                    $"  existing line \"{m_ExistingLines[i].m_Name}\" ({m_ExistingLines[i].m_Mode}): riders/day={(baseline.BaseRiders[i]).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(day {(baseline.BaseRidersByDay[i]).ToString("F0", CultureInfo.InvariantCulture)}, night {(baseline.BaseRidersByNight[i]).ToString("F0", CultureInfo.InvariantCulture)})");
            }
        }

        // Every candidate evaluated on its own against the existing network: the journey
        // weight that would ride it becomes EnabledDemand (what the mode decision and the
        // panel's reach figure read), and its standalone time saving is logged. Each
        // candidate is first given the fleet a player would build it with (PrepareFleet)
        // so the probe has a wait to charge.
        private void WeighCandidatesAlone(Setting settings, List<SuggestedRoute> candidates, FleetFacts facts, RoutePass pass)
        {
            var usable = new List<SuggestedRoute>();
            for (int i = 0; i < candidates.Count; i++)
            {
                candidates[i].EnabledDemand = 0f;
                candidates[i].DemandScored = false;
                if (candidates[i].Stops.Count >= 2)
                {
                    PrepareFleet(candidates[i], facts);
                    usable.Add(candidates[i]);
                }
            }

            if (usable.Count == 0)
            {
                return;
            }

            LineSetProblem probe = BuildLineSetProblem(settings, usable, 1, pass);
            float[] before = pass.Baseline ?? Array.Empty<float>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int c = 0; c < usable.Count; c++)
            {
                stopwatch.Restart();
                LineSetEvaluation alone = SuitabilityLineSet.Evaluate(probe, new[] { c }, 1, before);
                usable[c].EnabledDemand = (float)alone.Riders[c];
                usable[c].DemandScored = true;
                DeferredLog.Info(
                    $"  candidate alone: {usable[c].Network} {usable[c].Mode}, {usable[c].Stops.Count} stops, " +
                    $"corridorFlow={(usable[c].CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"riders/day={(alone.Riders[c]).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"timeSaved={(alone.TimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} passenger-hours/day, {(stopwatch.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms");
            }
        }

        // The set problem over `candidates`: existing served stops and lines as the base
        // network, every journey at its full weight, each candidate with the wait, speed,
        // ride times, headway and capacity of its resolved mode.
        private LineSetProblem BuildLineSetProblem(Setting settings, List<SuggestedRoute> candidates, int maxLines, RoutePass pass)
        {
            LineSetProblem pairs = pass.PairTable ??= BuildPairTable();
            var problem = new LineSetProblem
            {
                PairCount = pairs.PairCount,
                PairOx = pairs.PairOx,
                PairOz = pairs.PairOz,
                PairDx = pairs.PairDx,
                PairDz = pairs.PairDz,
                PairWeight = pairs.PairWeight,
                PairDayShare = pairs.PairDayShare,
                Geometry = pairs.Geometry,
                BaseStopCount = m_TransitStops.Count,
                BaseStopX = new float[m_TransitStops.Count],
                BaseStopZ = new float[m_TransitStops.Count],
                BaseLines = SuitabilityLines.ToTransitLines(m_ExistingLines),
                WalkRadius = Assumptions.TransferWalkRadius,
                BoardPenaltySeconds = Assumptions.DefaultBoardPenaltySeconds,
                MaxTravelSeconds = Assumptions.MaxJourneySeconds,
                ZoneReachMetres = Assumptions.ZoneStopReachMetres,
                MaxLines = maxLines,
                UtilisationFloor = settings.UtilisationFloorPercent / 100f,
                UtilisationCeiling = Assumptions.MaxPlannedUtilisation,
                MovementSecondsPerDay = Assumptions.MovementSecondsPerGameDay,
                DuplicateShare = Assumptions.DuplicateRiderShare,
                EquityFloorShare = settings.EquityFloorPercent / 100f,
            };
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                problem.BaseStopX[i] = m_TransitStops[i].x;
                problem.BaseStopZ[i] = m_TransitStops[i].y;
            }

            FleetFacts facts = ReadFleetFacts();
            for (int c = 0; c < candidates.Count; c++)
            {
                SuggestedRoute route = candidates[c];
                // The wait the router charges is half the interval of the fleet the route
                // carries (PrepareFleet / SettleMode); the set's own riders re-size the
                // fleet for feasibility (SuitabilityLineSet.FleetFor), not the wait.
                var line = new LineCandidate
                {
                    StopX = new float[route.Stops.Count],
                    StopZ = new float[route.Stops.Count],
                    ExpectedWait = route.HeadwaySeconds * 0.5f,
                    SpeedMetresPerSecond = Assumptions.CruiseSpeedFor(route.Mode),
                    RideSeconds = RoadRideSeconds(route),
                    RoundTripSeconds = route.RoundTripSeconds,
                    VehicleCapacity = facts.CapacityFor(route.Mode),
                    FleetMin = route.FleetMin,
                    FleetMax = route.FleetMax,
                    Group = route.Group,
                };
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    line.StopX[i] = route.Stops[i].x;
                    line.StopZ[i] = route.Stops[i].y;
                }

                problem.Candidates.Add(line);
            }

            WalkAccessInputs? accessInputs = m_AccessInputs;
            if (m_ServedWalkMs is not null && m_Access?.Index is not null && accessInputs is not null)
            {
                // Its own workspace: the pass runs off the main thread, which keeps the
                // equity measure's for itself.
                var dijkstra = new IntDijkstra(accessInputs.Graph.NodeCount);
                problem.CoverageOf = (chosen, count) => CoverageWith(candidates, chosen, count, dijkstra);
            }

            return problem;
        }

        // The journeys as door-to-door pairs (register A0.5): every trip at its own two
        // positions, not a zone centre; trips between the same two doors (one
        // household's commuters to one workplace) are one pair with their summed
        // weight. Built once per pass; Evaluate then searches once per distinct origin
        // door, and the geometry cache on the table is shared by every problem.
        private LineSetProblem BuildPairTable()
        {
            var pairIndex = new Dictionary<(float, float, float, float), int>();
            var ox = new List<float>();
            var oz = new List<float>();
            var dx = new List<float>();
            var dz = new List<float>();
            var weight = new List<float>();
            var dayWeight = new List<float>();
            for (int i = 0; i < m_Journeys.Count; i++)
            {
                Trip trip = m_Journeys[i];
                var key = (trip.m_Origin.x, trip.m_Origin.y, trip.m_Destination.x, trip.m_Destination.y);
                if (pairIndex.TryGetValue(key, out int existing))
                {
                    weight[existing] += trip.m_Weight;
                    dayWeight[existing] += trip.m_Weight * trip.m_DayShare;
                    continue;
                }

                pairIndex.Add(key, ox.Count);
                ox.Add(trip.m_Origin.x);
                oz.Add(trip.m_Origin.y);
                dx.Add(trip.m_Destination.x);
                dz.Add(trip.m_Destination.y);
                weight.Add(trip.m_Weight);
                dayWeight.Add(trip.m_Weight * trip.m_DayShare);
            }

            var dayShare = new float[ox.Count];
            for (int i = 0; i < dayShare.Length; i++)
            {
                dayShare[i] = weight[i] > 0f ? dayWeight[i] / weight[i] : 1f;
            }

            var table = new LineSetProblem
            {
                PairCount = ox.Count,
                PairOx = ox.ToArray(),
                PairOz = oz.ToArray(),
                PairDx = dx.ToArray(),
                PairDz = dz.ToArray(),
                PairWeight = weight.ToArray(),
                PairDayShare = dayShare,
            };
            table.Geometry = SuitabilityLineSet.GeometryOf(table);
            return table;
        }

        // Share of journeys served at both ends once the chosen candidates' stops join the
        // served network — the equity component of the set objective.
        private float CoverageWith(List<SuggestedRoute> candidates, int[] chosen, int count, IntDijkstra dijkstra)
        {
            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (m_ServedWalkMs is null || access?.Index is null || inputs is null)
            {
                return 0f;
            }

            var stops = new List<float2Like>();
            for (int k = 0; k < count; k++)
            {
                stops.AddRange(candidates[chosen[k]].Stops);
            }

            SnapStops(stops, access.Index, inputs.AccessMs, out int[] nodes, out int[] stopAccess);
            int[] merged = SuitabilityEquity.WithStops(inputs.Graph, dijkstra, m_ServedWalkMs, nodes, stopAccess, nodes.Length, m_EquityHorizonMs);
            return SuitabilityEquity.Coverage(
                merged, m_EquityHorizonMs,
                m_JourneyOriginNode, m_JourneyOriginAccess, m_JourneyDestinationNode, m_JourneyDestinationAccess,
                m_JourneyWeight, m_Journeys.Count).Share;
        }

        private void AdoptLineSet(Setting settings, List<SuggestedRoute> resolved, LineSetProblem problem, LineSetSolution solution, long elapsedMs, RoutePass pass)
        {
            var order = new List<int>();
            for (int k = 0; k < solution.Count; k++)
            {
                order.Add(solution.Chosen[k]);
            }

            order.Sort((a, b) => solution.StandaloneTimeSaved[b].CompareTo(solution.StandaloneTimeSaved[a]));
            LineSetEvaluation? evaluation = solution.Evaluation;
            for (int k = 0; k < order.Count; k++)
            {
                SuggestedRoute route = resolved[order[k]];
                if (evaluation is not null)
                {
                    // The fleet the set's own riders call for, within the game's span;
                    // the interval it yields prices the periods.
                    route.EnabledDemand = (float)evaluation.Riders[order[k]];
                    LineCandidate line = problem.Candidates[order[k]];
                    FleetPlan fleet = SuitabilityLineSet.FleetFor(problem, order[k], evaluation.Riders[order[k]]);
                    route.Vehicles = fleet.Vehicles;
                    route.HeadwaySeconds = fleet.HeadwaySeconds;
                    route.DayUtilisation = Daytime.UtilisationInPeriod((float)evaluation.RidersByDay[order[k]], fleet.HeadwaySeconds, line.VehicleCapacity, Assumptions.DayShareOfDay);
                    route.NightUtilisation = Daytime.UtilisationInPeriod((float)evaluation.RidersByNight[order[k]], fleet.HeadwaySeconds, line.VehicleCapacity, 1f - Assumptions.DayShareOfDay);
                    route.Schedule = Daytime.Recommend(route.DayUtilisation, route.NightUtilisation, problem.UtilisationFloor);
                }

                pass.Routes.Add(route);
                AcceptIntoNetwork(route, pass);
                DeferredLog.Info(
                    $"  KEPT #{(k + 1).ToString(CultureInfo.InvariantCulture)}: {route.Network} {route.Mode}{(route.BentThroughHub ? " via an interchange" : string.Empty)}, {route.Stops.Count} stops " +
                    $"(plan: {(route.StopPlan?.CandidateCount ?? 0).ToString(CultureInfo.InvariantCulture)} candidates, {(route.StopPlan?.EndCount ?? 0).ToString(CultureInfo.InvariantCulture)} doors in reach, " +
                    $"gain {(route.StopPlanGain / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} h vs delay {(route.StopPlanDelay / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} h a day), " +
                    $"len={(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m, riders/day in the set={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"utilisation={(SuitabilityLineSet.Utilisation(problem, order[k], route.EnabledDemand) * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, " +
                    $"alone={(solution.StandaloneTimeSaved[order[k]] / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} passenger-hours/day, {(route.Vehicles).ToString(CultureInfo.InvariantCulture)} veh of [{(route.FleetMin).ToString(CultureInfo.InvariantCulture)}, {(route.FleetMax == int.MaxValue ? "?" : route.FleetMax.ToString(CultureInfo.InvariantCulture))}] at {(route.HeadwaySeconds).ToString("F0", CultureInfo.InvariantCulture)} s, " +
                    $"schedule {route.Schedule} (day {(route.DayUtilisation * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, night {(route.NightUtilisation * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of the period's seats)");
            }

            string realism = evaluation is null
                ? string.Empty
                : $"; the set's journeys spend walk {(evaluation.WalkSeconds / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} h, wait {(evaluation.WaitSeconds / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} h, ride {(evaluation.RideSeconds / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} h a day " +
                  $"(realism-weighted 2.2/2.1/1: {((evaluation.WalkSeconds * 2.2 + evaluation.WaitSeconds * 2.1 + evaluation.RideSeconds) / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} weighted hours — shown, not planned with; register A7.2)";
            DeferredLog.Info(
                $"Line set: {(solution.Count).ToString(CultureInfo.InvariantCulture)} of {(resolved.Count).ToString(CultureInfo.InvariantCulture)} resolved candidates chosen, " +
                $"time saved {(solution.TimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} passenger-hours/day " +
                $"{(solution.Optimal ? "(proven optimal" : $"(best found, NOT proven optimal; ceiling {(solution.UpperBoundTimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)}")} " +
                $"under equity floor {settings.EquityFloorPercent.ToString(CultureInfo.InvariantCulture)} % (set reaches {(solution.Coverage * 100f).ToString("F1", CultureInfo.InvariantCulture)} %), " +
                $"utilisation floor {settings.UtilisationFloorPercent.ToString(CultureInfo.InvariantCulture)} % and ceiling {(Assumptions.MaxPlannedUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture)} %, duplicate share {(Assumptions.DuplicateRiderShare * 100f).ToString("F0", CultureInfo.InvariantCulture)} %), " +
                $"greedy {(solution.GreedyTimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} h, after swaps {(solution.LocalSearchTimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} h, " +
                $"{(solution.Nodes).ToString(CultureInfo.InvariantCulture)} search nodes, {(solution.Evaluations).ToString(CultureInfo.InvariantCulture)} set evaluations, {(solution.Infeasible).ToString(CultureInfo.InvariantCulture)} infeasible sets met, " +
                $"{(elapsedMs).ToString(CultureInfo.InvariantCulture)} ms of a {(Assumptions.LineSetTimeBudgetSeconds).ToString(CultureInfo.InvariantCulture)} s budget{realism}");
        }

        // Treats an accepted suggestion as though the player had built it, so the next
        // round measures every remaining candidate against a network containing it.
        private void AcceptIntoNetwork(SuggestedRoute route, RoutePass pass)
        {
            var stops = new int[route.Stops.Count];
            for (int i = 0; i < route.Stops.Count; i++)
            {
                stops[i] = m_TransitStops.Count + pass.AcceptedStops.Count;
                pass.AcceptedStops.Add(route.Stops[i]);
            }

            pass.AcceptedLines.Add(new TransitLine
            {
                m_Stops = stops,
                m_ExpectedWait = route.HeadwaySeconds * 0.5f,
                m_RideSeconds = RoadRideSeconds(route),
                m_SpeedMetresPerSecond = Assumptions.CruiseSpeedFor(route.Mode),
            });
        }
    }
}
