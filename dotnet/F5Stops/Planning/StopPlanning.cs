using System;
using System.Collections.Generic;

namespace TransitArchitect
{
    // Where along a fixed alignment a line calls (register A5.1, A5.2, A5.4, decided
    // 2026-09-05): a stop is made exactly where the walking time it saves the
    // journeys boarding there outweighs the delay it costs everyone riding through.
    //
    // Candidates are positions along the alignment (arc length, ascending); the two
    // termini and every interchange position are forced. Journey ENDS near the line
    // are the boarders: each end is assigned, by the arc length of its projection, to
    // the interval between the two chosen stops that bracket it, and walks to the
    // nearer of the two; it gains max(0, H − walk seconds) with H the mode's access
    // horizon. Through-riders at a candidate are the corridor flow there, and every
    // intermediate stop costs them DelaySecondsPerStop each. Consecutive stops keep at
    // least MinGapMetres apart. The objective is exactly
    //   Σ_ends w · max(0, H − t_e(S)) − Σ_{stops} through · delay
    // and the plan below is its maximum — a dynamic programme over candidates in arc
    // order, exact because an end's gain depends only on the two stops around it.
    internal sealed class StopPlanProblem
    {
        public float[] CandidateAt = Array.Empty<float>();
        public float[] CandidateX = Array.Empty<float>();
        public float[] CandidateZ = Array.Empty<float>();
        public bool[] MustCall = Array.Empty<bool>();
        public float[] ThroughFlow = Array.Empty<float>();
        public int CandidateCount;
        public float[] EndAt = Array.Empty<float>();
        public float[] EndX = Array.Empty<float>();
        public float[] EndZ = Array.Empty<float>();
        public float[] EndWeight = Array.Empty<float>();
        public int EndCount;
        public float MinGapMetres;
        public float DelaySecondsPerStop;
        public float AccessHorizonSeconds;
        public float WalkMetresPerSecond;
        // Arc length of candidate 0 on the alignment the candidates were cut from; the
        // plan itself measures from candidate 0. Carried for the caller's trimming.
        public float PathOffset;
    }

    internal sealed class StopPlanSolution
    {
        public int[] Chosen = Array.Empty<int>();
        public int Count;
        // Σ w · max(0, H − t) over all ends, seconds·journeys per day.
        public double Gain;
        // Σ through · delay over the chosen stops, same unit.
        public double Delay;
        public double Value => Gain - Delay;
    }

    internal static class StopPlanning
    {
        public static StopPlanSolution Solve(StopPlanProblem problem)
        {
            int m = problem.CandidateCount;
            var solution = new StopPlanSolution();
            if (m < 2)
            {
                solution.Chosen = m == 1 ? new[] { 0 } : Array.Empty<int>();
                solution.Count = m;
                solution.Delay = m == 1 ? Cost(problem, 0) : 0.0;
                solution.Gain = m == 1 ? GainBeyond(problem, 0, before: true) + GainBeyond(problem, 0, before: false) : 0.0;
                return solution;
            }

            // Ends sorted by arc length, with the first end index past each candidate.
            int[] order = EndOrder(problem);
            var pastCandidate = new int[m];
            int cursor = 0;
            for (int c = 0; c < m; c++)
            {
                while (cursor < problem.EndCount && problem.EndAt[order[cursor]] <= problem.CandidateAt[c])
                {
                    cursor++;
                }

                pastCandidate[c] = cursor;
            }

            // The nearest forced candidate before each position: a transition may not
            // jump over one.
            var lastForced = new int[m];
            int forced = -1;
            for (int c = 0; c < m; c++)
            {
                lastForced[c] = forced;
                if (problem.MustCall[c] || c == 0)
                {
                    forced = c;
                }
            }

            var best = new double[m];
            var parent = new int[m];
            for (int c = 0; c < m; c++)
            {
                best[c] = double.NegativeInfinity;
                parent[c] = -1;
            }

            best[0] = GainBeyond(problem, 0, before: true) - Cost(problem, 0);
            for (int k = 1; k < m; k++)
            {
                double cost = Cost(problem, k);
                int lowest = Math.Max(0, lastForced[k]);
                for (int i = k - 1; i >= lowest; i--)
                {
                    if (best[i] == double.NegativeInfinity)
                    {
                        continue;
                    }

                    bool bothForced = (i == 0 || problem.MustCall[i]) && (k == m - 1 || problem.MustCall[k]);
                    if (problem.CandidateAt[k] - problem.CandidateAt[i] < problem.MinGapMetres && !bothForced)
                    {
                        // Too close to call at both. Two forced calls (termini,
                        // interchanges) are exempt: the caller pinned them, the plan
                        // does not get to drop one.
                        continue;
                    }

                    double value = best[i] + GainBetween(problem, order, pastCandidate, i, k) - cost;
                    if (value > best[k])
                    {
                        best[k] = value;
                        parent[k] = i;
                    }
                }
            }

            int last = m - 1;
            if (best[last] == double.NegativeInfinity)
            {
                // No feasible plan keeps the gap between forced calls: fall back to the
                // termini alone, which is always a plan.
                solution.Chosen = new[] { 0, last };
                solution.Count = 2;
                solution.Delay = Cost(problem, 0) + Cost(problem, last);
                solution.Gain = GainBeyond(problem, 0, before: true) + GainBetween(problem, order, pastCandidate, 0, last) + GainBeyond(problem, last, before: false);
                return solution;
            }

            Backtrack(problem, best[last], parent, last, solution);
            return solution;
        }

        private static void Backtrack(StopPlanProblem problem, double bestValue, int[] parent, int last, StopPlanSolution into)
        {
            var chosen = new List<int>();
            for (int c = last; c >= 0; c = parent[c])
            {
                chosen.Add(c);
                if (c == 0)
                {
                    break;
                }
            }

            chosen.Reverse();
            into.Chosen = chosen.ToArray();
            into.Count = chosen.Count;
            double delay = 0.0;
            for (int i = 0; i < chosen.Count; i++)
            {
                delay += Cost(problem, chosen[i]);
            }

            into.Delay = delay;
            into.Gain = bestValue + delay + GainBeyond(problem, last, before: false);
        }

        // Walking seconds from an end to a candidate, straight-line at the planning speed.
        public static float WalkSeconds(StopPlanProblem problem, int end, int candidate)
        {
            double dx = problem.EndX[end] - problem.CandidateX[candidate];
            double dz = problem.EndZ[end] - problem.CandidateZ[candidate];
            return (float)(Math.Sqrt((dx * dx) + (dz * dz)) / problem.WalkMetresPerSecond);
        }

        public static double Kernel(StopPlanProblem problem, float walkSeconds)
        {
            return Math.Max(0.0, problem.AccessHorizonSeconds - (double)walkSeconds);
        }

        private static double Cost(StopPlanProblem problem, int candidate)
        {
            return problem.ThroughFlow[candidate] * (double)problem.DelaySecondsPerStop;
        }

        // Ends whose projection lies in (at[i], at[k]] walk to the nearer of i and k.
        private static double GainBetween(StopPlanProblem problem, int[] order, int[] pastCandidate, int i, int k)
        {
            double gain = 0.0;
            for (int e = pastCandidate[i]; e < pastCandidate[k]; e++)
            {
                int end = order[e];
                float walk = Math.Min(WalkSeconds(problem, end, i), WalkSeconds(problem, end, k));
                gain += problem.EndWeight[end] * Kernel(problem, walk);
            }

            return gain;
        }

        // Ends projecting before the first candidate (or after the last) have only it.
        private static double GainBeyond(StopPlanProblem problem, int candidate, bool before)
        {
            double gain = 0.0;
            for (int end = 0; end < problem.EndCount; end++)
            {
                bool outside = before ? problem.EndAt[end] <= problem.CandidateAt[candidate] : problem.EndAt[end] > problem.CandidateAt[candidate];
                if (outside)
                {
                    gain += problem.EndWeight[end] * Kernel(problem, WalkSeconds(problem, end, candidate));
                }
            }

            return gain;
        }

        private static int[] EndOrder(StopPlanProblem problem)
        {
            var order = new int[problem.EndCount];
            for (int e = 0; e < order.Length; e++)
            {
                order[e] = e;
            }

            float[] at = problem.EndAt;
            Array.Sort(order, (a, b) =>
            {
                int byAt = at[a].CompareTo(at[b]);
                return byAt != 0 ? byAt : a.CompareTo(b);
            });
            return order;
        }
    }
}
