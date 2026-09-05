using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // One suggested line as the set objective sees it: its stops, the rider's expected
    // wait, how it covers ground, and what it costs to run (register A7.5, decided
    // 2026-09-05: the set is judged as a whole by the passenger time it saves).
    internal sealed class LineCandidate
    {
        public float[] StopX = Array.Empty<float>();
        public float[] StopZ = Array.Empty<float>();
        public float ExpectedWait;
        public float SpeedMetresPerSecond;
        public float[]? RideSeconds;
        public float HeadwaySeconds;
        public float VehicleCapacity;
    }

    internal sealed class LineSetProblem
    {
        public float[] BaseStopX = Array.Empty<float>();
        public float[] BaseStopZ = Array.Empty<float>();
        public int BaseStopCount;
        public List<TransitLine> BaseLines = new List<TransitLine>();
        public List<LineCandidate> Candidates = new List<LineCandidate>();
        // Journeys as zone-to-zone pairs: centre positions and per-day weights.
        public float[] PairOx = Array.Empty<float>();
        public float[] PairOz = Array.Empty<float>();
        public float[] PairDx = Array.Empty<float>();
        public float[] PairDz = Array.Empty<float>();
        public float[] PairWeight = Array.Empty<float>();
        public int PairCount;
        public float WalkRadius;
        public float BoardPenaltySeconds;
        public float MaxTravelSeconds;
        public float ZoneReachMetres;
        public int MaxLines;
        // Per-line utilisation floor (boardings per game day over seats offered); 0 = none.
        public float UtilisationFloor;
        public float MovementSecondsPerDay;
        // Share of a line's riders that may already have an equally fast route without
        // it before the line counts as a duplicate (register A4.3: 0.5).
        public float DuplicateShare;
        // Optional equity: served share of the journeys given the chosen candidates'
        // stops (SuitabilityEquity in the caller), and the floor it must reach first.
        public Func<int[], int, float>? CoverageOf;
        public float EquityFloorShare;
        // Derived from the pairs on first evaluation (SuitabilityLineSet.GeometryOf) and
        // reused by every evaluation since; the pair arrays are not modified after a
        // problem is built.
        internal LineSetGeometry? Geometry;
    }

    // What every evaluation of one problem shares: the distinct zone positions the
    // pairs' ends fall on (one zone node each in the transit graph), each pair's ends
    // as zone indices, the pairs grouped by origin zone, and the straight-line walk
    // time of each pair.
    internal sealed class LineSetGeometry
    {
        public float[] ZoneX = Array.Empty<float>();
        public float[] ZoneZ = Array.Empty<float>();
        public int ZoneCount;
        public int[] PairOriginZone = Array.Empty<int>();
        public int[] PairDestinationZone = Array.Empty<int>();
        // Pairs sharing an origin zone, CSR-style: pairs of zone z are
        // PairsByOrigin[OriginStart[z] .. OriginStart[z + 1]).
        public int[] OriginStart = Array.Empty<int>();
        public int[] PairsByOrigin = Array.Empty<int>();
        public float[] WalkOnly = Array.Empty<float>();
    }

    internal sealed class LineSetEvaluation
    {
        // Σ w · max(0, before − after) in seconds·journeys per day.
        public double TimeSaved;
        public float Coverage;
        // Journey weight riding each candidate (index = candidate), per day.
        public double[] Riders = Array.Empty<double>();
        // Door-to-door time of every pair under this set (float.MaxValue = not carried).
        public float[] After = Array.Empty<float>();
        // Time-weighted components over all pairs for the realism diagnostic
        // (walk 2.2, wait 2.1, ride 1 — TCQSM): what the set's journeys spend.
        public double WalkSeconds;
        public double WaitSeconds;
        public double RideSeconds;
    }

    internal sealed class LineSetSolution
    {
        public int[] Chosen = Array.Empty<int>();
        public int Count;
        public LineSetEvaluation? Evaluation;
        public double TimeSaved;
        public double UpperBoundTimeSaved;
        public float Coverage;
        public bool Optimal;
        public long Nodes;
        public int Infeasible;
        public double[] StandaloneTimeSaved = Array.Empty<double>();
    }

    // The time a set of suggested lines saves the city's journeys, and the exact best
    // set under the floors.
    //
    // Objective (A4.1, A7.2, A7.4, A3.1): for every journey, before = min(walking
    // straight there at 1.2 m/s, door-to-door on the existing network); after = the
    // same minimum with the chosen lines added; saved = w · max(0, before − after).
    // Times are the game's own — walk, wait and ride weigh 1 : 1 : 1, a transfer costs
    // exactly its walk and wait, nothing is discounted — because the citizens the game
    // simulates route that way; the realism weights are reported beside it.
    //
    // Selection: lexicographic (min(coverage, equity floor), time saved), maximised by
    // depth-first branch-and-bound over subsets of at most MaxLines candidates. Both
    // components are monotone in the set (a line can only shorten a journey or serve
    // another door), so the value of "everything still available" bounds every
    // completion. Feasibility — each line's utilisation and the duplicate rule — is
    // checked on the set itself, since riders move between lines of a set.
    internal static class SuitabilityLineSet
    {
        public const long DefaultNodeBudget = 20_000;

        // One Dijkstra per origin ZONE rather than per pair, capped at the largest
        // door-to-door time the group's pairs can still improve on. Both are exact
        // rewrites of the per-pair search, not approximations:
        //  - two pairs starting from the same zone see the same edges from the same
        //    node, so one search answers both (the zone nodes are deduplicated by
        //    position, which also removes the duplicate nodes' identical edges);
        //  - a network with lines added never lengthens a journey (every old path
        //    is still there at the same cost), so any destination further than the
        //    pair's own `before` is unreachable within it, and min(walk, transit)
        //    comes out as `before` whether or not the search finished the distance.
        // The sums stay in pair order: per-pair results are collected during the
        // group runs and folded in a final pass, so the double accumulations match
        // the per-pair evaluation bit for bit.
        public static LineSetEvaluation Evaluate(LineSetProblem problem, int[] chosen, int count, float[]? before)
        {
            LineSetGeometry geometry = GeometryOf(problem);
            AssembleStops(problem, chosen, count, out float[] stopX, out float[] stopZ, out int stopCount, out List<TransitLine> lines);
            TransitNetwork network = SuitabilityTransit.BuildWithZones(
                stopX, stopZ, stopCount, lines, problem.WalkRadius, problem.BoardPenaltySeconds,
                geometry.ZoneX, geometry.ZoneZ, geometry.ZoneCount, problem.ZoneReachMetres);
            var evaluation = new LineSetEvaluation
            {
                Riders = new double[problem.Candidates.Count],
                After = new float[problem.PairCount],
            };
            var workspace = new DijkstraWorkspace(network.Graph.NodeCount);
            int lineOffset = problem.BaseLines.Count;
            var legs = new PairLegs(problem.PairCount);
            for (int zone = 0; zone < geometry.ZoneCount; zone++)
            {
                int first = geometry.OriginStart[zone];
                int last = geometry.OriginStart[zone + 1];
                if (first == last)
                {
                    continue;
                }

                float cap = 0f;
                for (int k = first; k < last; k++)
                {
                    int pair = geometry.PairsByOrigin[k];
                    cap = Math.Max(cap, before is null ? geometry.WalkOnly[pair] : before[pair]);
                }

                int originNode = network.ZoneNodeStart + zone;
                // Doors are sinks: reached, never walked through (DijkstraWorkspace.Run).
                workspace.Run(network.Graph, originNode, Math.Min(problem.MaxTravelSeconds, cap), network.ZoneNodeStart);
                for (int k = first; k < last; k++)
                {
                    int pair = geometry.PairsByOrigin[k];
                    int destinationNode = network.ZoneNodeStart + geometry.PairDestinationZone[pair];
                    float walkOnly = geometry.WalkOnly[pair];
                    float transit = workspace.Dist[destinationNode];
                    evaluation.After[pair] = Math.Min(walkOnly, transit);
                    if (transit < walkOnly)
                    {
                        AttributeItinerary(network, workspace, originNode, destinationNode, lineOffset, count, pair, legs);
                    }
                    else
                    {
                        legs.Walk[pair] = walkOnly;
                    }
                }
            }

            for (int i = 0; i < problem.PairCount; i++)
            {
                double weight = problem.PairWeight[i];
                evaluation.WalkSeconds += weight * legs.Walk[i];
                evaluation.WaitSeconds += weight * legs.Wait[i];
                evaluation.RideSeconds += weight * legs.Ride[i];
                int[]? ridden = legs.Ridden[i];
                if (ridden is not null)
                {
                    for (int r = 0; r < ridden.Length; r++)
                    {
                        evaluation.Riders[chosen[ridden[r]]] += weight;
                    }
                }

                if (before is not null && before[i] > evaluation.After[i])
                {
                    evaluation.TimeSaved += weight * (double)(before[i] - evaluation.After[i]);
                }
            }

            evaluation.Coverage = problem.CoverageOf?.Invoke(chosen, count) ?? 0f;
            return evaluation;
        }

        // Per-pair itinerary components, held until the pair-ordered fold.
        private sealed class PairLegs
        {
            public readonly double[] Walk;
            public readonly double[] Wait;
            public readonly double[] Ride;
            public readonly int[]?[] Ridden;

            public PairLegs(int pairCount)
            {
                Walk = new double[pairCount];
                Wait = new double[pairCount];
                Ride = new double[pairCount];
                Ridden = new int[]?[pairCount];
            }
        }

        internal static LineSetGeometry GeometryOf(LineSetProblem problem)
        {
            LineSetGeometry? cached = problem.Geometry;
            if (cached is not null)
            {
                return cached;
            }

            var geometry = new LineSetGeometry
            {
                PairOriginZone = new int[problem.PairCount],
                PairDestinationZone = new int[problem.PairCount],
                WalkOnly = new float[problem.PairCount],
            };
            var zoneOf = new Dictionary<(float, float), int>();
            var zoneX = new List<float>();
            var zoneZ = new List<float>();
            int ZoneIndex(float x, float z)
            {
                if (!zoneOf.TryGetValue((x, z), out int index))
                {
                    index = zoneX.Count;
                    zoneOf.Add((x, z), index);
                    zoneX.Add(x);
                    zoneZ.Add(z);
                }

                return index;
            }

            for (int i = 0; i < problem.PairCount; i++)
            {
                geometry.PairOriginZone[i] = ZoneIndex(problem.PairOx[i], problem.PairOz[i]);
                geometry.PairDestinationZone[i] = ZoneIndex(problem.PairDx[i], problem.PairDz[i]);
                geometry.WalkOnly[i] = WalkOnlySeconds(problem, i);
            }

            geometry.ZoneCount = zoneX.Count;
            geometry.ZoneX = zoneX.ToArray();
            geometry.ZoneZ = zoneZ.ToArray();
            geometry.OriginStart = new int[geometry.ZoneCount + 1];
            geometry.PairsByOrigin = new int[problem.PairCount];
            for (int i = 0; i < problem.PairCount; i++)
            {
                geometry.OriginStart[geometry.PairOriginZone[i] + 1]++;
            }

            for (int z = 0; z < geometry.ZoneCount; z++)
            {
                geometry.OriginStart[z + 1] += geometry.OriginStart[z];
            }

            var fill = new int[geometry.ZoneCount];
            for (int i = 0; i < problem.PairCount; i++)
            {
                int zone = geometry.PairOriginZone[i];
                geometry.PairsByOrigin[geometry.OriginStart[zone] + fill[zone]++] = i;
            }

            problem.Geometry = geometry;
            return geometry;
        }

        // Straight-line walk between the two zone centres at the planning speed.
        public static float WalkOnlySeconds(LineSetProblem problem, int pair)
        {
            float dx = problem.PairDx[pair] - problem.PairOx[pair];
            float dz = problem.PairDz[pair] - problem.PairOz[pair];
            return (float)Math.Sqrt((dx * dx) + (dz * dz)) / SuitabilityTransit.WalkSpeed;
        }

        // Walks the retained shortest itinerary back from the destination, splitting its
        // cost into walk, wait and ride and noting each ridden candidate line once
        // (RidesPerJourney is applied by the utilisation formula).
        private static void AttributeItinerary(
            TransitNetwork network, DijkstraWorkspace workspace, int originNode, int destinationNode,
            int lineOffset, int count, int pair, PairLegs into)
        {
            int node = destinationNode;
            int guard = network.Graph.EdgeCount + 2;
            List<int>? ridden = null;
            while (node != originNode && guard-- > 0)
            {
                int edge = workspace.PrevEdge[node];
                if (edge < 0)
                {
                    return;
                }

                float cost = network.Graph.EdgeCost[edge];
                switch (network.EdgeKind[edge])
                {
                    case TransitEdgeKind.Walk:
                        into.Walk[pair] += cost;
                        break;
                    case TransitEdgeKind.Access:
                        into.Wait[pair] += cost;
                        int line = network.EdgeLine[edge] - lineOffset;
                        if (line >= 0 && line < count)
                        {
                            ridden ??= new List<int>();
                            if (!ridden.Contains(line))
                            {
                                ridden.Add(line);
                            }
                        }

                        break;
                    default:
                        into.Ride[pair] += cost;
                        break;
                }

                node = network.Graph.OtherEnd(edge, node);
            }

            if (ridden is not null)
            {
                into.Ridden[pair] = ridden.ToArray();
            }
        }

        private static void AssembleStops(
            LineSetProblem problem, int[] chosen, int count,
            out float[] stopX, out float[] stopZ, out int stopCount, out List<TransitLine> lines)
        {
            stopCount = problem.BaseStopCount;
            for (int k = 0; k < count; k++)
            {
                stopCount += problem.Candidates[chosen[k]].StopX.Length;
            }

            stopX = new float[stopCount];
            stopZ = new float[stopCount];
            Array.Copy(problem.BaseStopX, stopX, problem.BaseStopCount);
            Array.Copy(problem.BaseStopZ, stopZ, problem.BaseStopCount);
            lines = new List<TransitLine>(problem.BaseLines);
            int next = problem.BaseStopCount;
            for (int k = 0; k < count; k++)
            {
                LineCandidate candidate = problem.Candidates[chosen[k]];
                var stops = new int[candidate.StopX.Length];
                for (int i = 0; i < stops.Length; i++)
                {
                    stopX[next] = candidate.StopX[i];
                    stopZ[next] = candidate.StopZ[i];
                    stops[i] = next++;
                }

                lines.Add(new TransitLine
                {
                    m_Stops = stops,
                    m_ExpectedWait = candidate.ExpectedWait,
                    m_RideSeconds = candidate.RideSeconds,
                    m_SpeedMetresPerSecond = candidate.SpeedMetresPerSecond,
                });
            }
        }

        // Boardings per game day over seats offered per game day, for one candidate given
        // the journey weight riding it in a set.
        public static float Utilisation(LineSetProblem problem, int candidate, double riders)
        {
            LineCandidate line = problem.Candidates[candidate];
            if (line.HeadwaySeconds <= 0f || line.VehicleCapacity <= 0f || problem.MovementSecondsPerDay <= 0f)
            {
                return 0f;
            }

            double boardings = riders * TransitModes.RidesPerJourney;
            double seats = problem.MovementSecondsPerDay / line.HeadwaySeconds * 2.0 * line.VehicleCapacity;
            return (float)(boardings / seats);
        }

        public static LineSetSolution Solve(LineSetProblem problem, long nodeBudget)
        {
            return Solve(problem, nodeBudget, System.Threading.CancellationToken.None);
        }

        // `cancellation` is the caller's time budget: once it is requested the search
        // stops expanding, keeps the best set found and reports the open bound as the
        // ceiling — the same "best found, not proven" regime as an exhausted node
        // budget, decided by the caller's clock rather than by a count.
        public static LineSetSolution Solve(LineSetProblem problem, long nodeBudget, System.Threading.CancellationToken cancellation)
        {
            var solution = new LineSetSolution { Optimal = true };
            int n = problem.Candidates.Count;
            solution.StandaloneTimeSaved = new double[n];
            if (n == 0 || problem.MaxLines <= 0)
            {
                return solution;
            }

            float[] before = Evaluate(problem, Array.Empty<int>(), 0, before: null).After;
            var order = new int[n];
            for (int c = 0; c < n; c++)
            {
                order[c] = c;
                solution.StandaloneTimeSaved[c] = Evaluate(problem, new[] { c }, 1, before).TimeSaved;
            }

            Array.Sort(order, (a, b) =>
            {
                int bySaved = solution.StandaloneTimeSaved[b].CompareTo(solution.StandaloneTimeSaved[a]);
                return bySaved != 0 ? bySaved : a.CompareTo(b);
            });

            var search = new Search(problem, before, order, nodeBudget, cancellation);
            search.Run();
            solution.Nodes = search.Nodes;
            solution.Infeasible = search.Infeasible;
            solution.Optimal = !search.Exhausted;
            solution.Count = search.BestCount;
            solution.Chosen = new int[search.BestCount];
            Array.Copy(search.Best, solution.Chosen, search.BestCount);
            solution.TimeSaved = search.BestSaved;
            solution.Coverage = search.BestCoverage;
            solution.UpperBoundTimeSaved = search.Exhausted ? Math.Max(search.BestSaved, search.OpenBoundSaved) : search.BestSaved;
            solution.Evaluation = search.BestCount > 0 ? Evaluate(problem, solution.Chosen, solution.Count, before) : null;
            return solution;
        }

        private sealed class Search
        {
            private readonly LineSetProblem m_Problem;
            private readonly float[] m_Before;
            private readonly int[] m_Order;
            private readonly long m_Budget;
            private readonly System.Threading.CancellationToken m_Cancellation;
            private readonly int[] m_Chosen;
            // Sets of at most MaxLines lines are asked for twice — as a prefix under
            // consideration and again as "the rest" when a line of a superset is tested
            // for duplication — so their evaluations are kept. Union bounds are not: each
            // is asked for once and holds a city's worth of per-pair times.
            private readonly Dictionary<string, LineSetEvaluation> m_Evaluated = new Dictionary<string, LineSetEvaluation>(StringComparer.Ordinal);

            public long Nodes;
            public int Infeasible;
            public bool Exhausted;
            public double OpenBoundSaved;
            public readonly int[] Best;
            public int BestCount;
            public double BestSaved = -1.0;
            public float BestCoverage = -1f;

            public Search(LineSetProblem problem, float[] before, int[] order, long budget, System.Threading.CancellationToken cancellation)
            {
                m_Problem = problem;
                m_Before = before;
                m_Order = order;
                m_Budget = budget;
                m_Cancellation = cancellation;
                m_Chosen = new int[problem.MaxLines];
                Best = new int[problem.MaxLines];
            }

            public void Run()
            {
                Consider(Array.Empty<int>(), 0);
                Explore(0, 0);
            }

            // The lexicographic key: equity share capped at the floor, then time saved.
            private static int Compare(float coverageA, double savedA, float coverageB, double savedB)
            {
                int byCoverage = coverageA.CompareTo(coverageB);
                return byCoverage != 0 ? byCoverage : savedA.CompareTo(savedB);
            }

            private float Capped(float coverage)
            {
                return m_Problem.CoverageOf is null ? 0f : Math.Min(coverage, m_Problem.EquityFloorShare);
            }

            // Every set on the way down is a candidate answer (≤ MaxLines), judged on its
            // own evaluation; infeasible sets are counted, not chosen.
            private LineSetEvaluation EvaluateSet(int[] chosen, int count)
            {
                var sorted = new int[count];
                Array.Copy(chosen, sorted, count);
                Array.Sort(sorted);
                string key = string.Join(",", sorted);
                if (!m_Evaluated.TryGetValue(key, out LineSetEvaluation? evaluation))
                {
                    evaluation = Evaluate(m_Problem, chosen, count, m_Before);
                    m_Evaluated[key] = evaluation;
                }

                return evaluation;
            }

            private void Consider(int[] chosen, int count)
            {
                LineSetEvaluation evaluation = EvaluateSet(chosen, count);
                if (!Feasible(chosen, count, evaluation))
                {
                    Infeasible++;
                    return;
                }

                float capped = Capped(evaluation.Coverage);
                if (Compare(capped, evaluation.TimeSaved, BestCoverage, BestSaved) > 0)
                {
                    BestCoverage = capped;
                    BestSaved = evaluation.TimeSaved;
                    BestCount = count;
                    Array.Copy(chosen, Best, count);
                }
            }

            private void Explore(int depth, int position)
            {
                if (depth >= m_Problem.MaxLines)
                {
                    return;
                }

                for (int p = position; p < m_Order.Length; p++)
                {
                    // Bound: everything from here on, added to what is chosen.
                    int remaining = m_Order.Length - p;
                    var union = new int[depth + remaining];
                    Array.Copy(m_Chosen, union, depth);
                    Array.Copy(m_Order, p, union, depth, remaining);
                    LineSetEvaluation bound = Evaluate(m_Problem, union, union.Length, m_Before);
                    if (Compare(Capped(bound.Coverage), bound.TimeSaved, BestCoverage, BestSaved) <= 0)
                    {
                        return;
                    }

                    if (Exhausted)
                    {
                        OpenBoundSaved = Math.Max(OpenBoundSaved, bound.TimeSaved);
                        return;
                    }

                    Nodes++;
                    if (Nodes > m_Budget || m_Cancellation.IsCancellationRequested)
                    {
                        Exhausted = true;
                        OpenBoundSaved = Math.Max(OpenBoundSaved, bound.TimeSaved);
                        return;
                    }

                    m_Chosen[depth] = m_Order[p];
                    var prefix = new int[depth + 1];
                    Array.Copy(m_Chosen, prefix, depth + 1);
                    Consider(prefix, depth + 1);
                    Explore(depth + 1, p + 1);
                }
            }

            // Every chosen line must reach the utilisation floor, and none may be a
            // duplicate: a line is one when at least DuplicateShare of the journey weight
            // riding it travels no slower once the line is taken out of the set.
            private bool Feasible(int[] chosen, int count, LineSetEvaluation evaluation)
            {
                for (int k = 0; k < count; k++)
                {
                    if (m_Problem.UtilisationFloor > 0f
                        && Utilisation(m_Problem, chosen[k], evaluation.Riders[chosen[k]]) < m_Problem.UtilisationFloor)
                    {
                        return false;
                    }
                }

                if (m_Problem.DuplicateShare <= 0f || count < 2)
                {
                    return true;
                }

                for (int k = 0; k < count; k++)
                {
                    if (IsDuplicate(chosen, count, k, evaluation))
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool IsDuplicate(int[] chosen, int count, int k, LineSetEvaluation with)
            {
                var without = new int[count - 1];
                int w = 0;
                for (int j = 0; j < count; j++)
                {
                    if (j != k)
                    {
                        without[w++] = chosen[j];
                    }
                }

                LineSetEvaluation rest = EvaluateSet(without, count - 1);
                // Riders of line k are the journeys whose itinerary boards it; the
                // evaluation only keeps the weight, so the comparison is made on every
                // journey the set carries faster than walking and that k's removal would
                // slow — the complement is what k's riders lose nothing by.
                double riding = with.Riders[chosen[k]];
                if (riding <= 0.0)
                {
                    return true;
                }

                double slowed = 0.0;
                for (int i = 0; i < m_Problem.PairCount; i++)
                {
                    if (rest.After[i] > with.After[i])
                    {
                        slowed += m_Problem.PairWeight[i];
                    }
                }

                return (riding - slowed) / riding >= m_Problem.DuplicateShare;
            }
        }
    }
}
