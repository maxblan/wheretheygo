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

        public static LineSetEvaluation Evaluate(LineSetProblem problem, int[] chosen, int count, float[]? before)
        {
            AssembleStops(problem, chosen, count, out float[] stopX, out float[] stopZ, out int stopCount, out List<TransitLine> lines);
            TransitNetwork network = SuitabilityTransit.BuildWithZones(
                stopX, stopZ, stopCount, lines, problem.WalkRadius, problem.BoardPenaltySeconds,
                ZoneX(problem), ZoneZ(problem), problem.PairCount * 2, problem.ZoneReachMetres);
            var evaluation = new LineSetEvaluation
            {
                Riders = new double[problem.Candidates.Count],
                After = new float[problem.PairCount],
            };
            var workspace = new DijkstraWorkspace(network.Graph.NodeCount);
            int lineOffset = problem.BaseLines.Count;
            for (int i = 0; i < problem.PairCount; i++)
            {
                int originNode = network.ZoneNodeStart + (2 * i);
                int destinationNode = originNode + 1;
                workspace.Run(network.Graph, originNode, problem.MaxTravelSeconds);
                float walkOnly = WalkOnlySeconds(problem, i);
                float transit = workspace.Dist[destinationNode];
                float after = Math.Min(walkOnly, transit);
                evaluation.After[i] = after;
                if (transit < walkOnly)
                {
                    AttributeItinerary(network, workspace, originNode, destinationNode, problem.PairWeight[i], lineOffset, chosen, count, evaluation);
                }
                else
                {
                    evaluation.WalkSeconds += problem.PairWeight[i] * (double)walkOnly;
                }

                if (before is not null && before[i] > after)
                {
                    evaluation.TimeSaved += problem.PairWeight[i] * (double)(before[i] - after);
                }
            }

            evaluation.Coverage = problem.CoverageOf?.Invoke(chosen, count) ?? 0f;
            return evaluation;
        }

        // Straight-line walk between the two zone centres at the planning speed.
        public static float WalkOnlySeconds(LineSetProblem problem, int pair)
        {
            float dx = problem.PairDx[pair] - problem.PairOx[pair];
            float dz = problem.PairDz[pair] - problem.PairOz[pair];
            return (float)Math.Sqrt((dx * dx) + (dz * dz)) / SuitabilityTransit.WalkSpeed;
        }

        // Walks the retained shortest itinerary back from the destination, splitting its
        // cost into walk, wait and ride and crediting each ridden candidate line once with
        // the journey's weight (RidesPerJourney is applied by the utilisation formula).
        private static void AttributeItinerary(
            TransitNetwork network, DijkstraWorkspace workspace, int originNode, int destinationNode,
            float weight, int lineOffset, int[] chosen, int count, LineSetEvaluation into)
        {
            int node = destinationNode;
            int guard = network.Graph.EdgeCount + 2;
            var ridden = new HashSet<int>();
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
                        into.WalkSeconds += weight * (double)cost;
                        break;
                    case TransitEdgeKind.Access:
                        into.WaitSeconds += weight * (double)cost;
                        int line = network.EdgeLine[edge] - lineOffset;
                        if (line >= 0 && line < count && ridden.Add(line))
                        {
                            into.Riders[chosen[line]] += weight;
                        }

                        break;
                    default:
                        into.RideSeconds += weight * (double)cost;
                        break;
                }

                node = network.Graph.OtherEnd(edge, node);
            }
        }

        private static float[] ZoneX(LineSetProblem problem)
        {
            var zones = new float[problem.PairCount * 2];
            for (int i = 0; i < problem.PairCount; i++)
            {
                zones[2 * i] = problem.PairOx[i];
                zones[(2 * i) + 1] = problem.PairDx[i];
            }

            return zones;
        }

        private static float[] ZoneZ(LineSetProblem problem)
        {
            var zones = new float[problem.PairCount * 2];
            for (int i = 0; i < problem.PairCount; i++)
            {
                zones[2 * i] = problem.PairOz[i];
                zones[(2 * i) + 1] = problem.PairDz[i];
            }

            return zones;
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

            var search = new Search(problem, before, order, nodeBudget);
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
            private readonly int[] m_Chosen;

            public long Nodes;
            public int Infeasible;
            public bool Exhausted;
            public double OpenBoundSaved;
            public readonly int[] Best;
            public int BestCount;
            public double BestSaved = -1.0;
            public float BestCoverage = -1f;

            public Search(LineSetProblem problem, float[] before, int[] order, long budget)
            {
                m_Problem = problem;
                m_Before = before;
                m_Order = order;
                m_Budget = budget;
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
            private void Consider(int[] chosen, int count)
            {
                LineSetEvaluation evaluation = Evaluate(m_Problem, chosen, count, m_Before);
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
                    if (Nodes > m_Budget)
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

                LineSetEvaluation rest = Evaluate(m_Problem, without, count - 1, m_Before);
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
