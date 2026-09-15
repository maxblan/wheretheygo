using System;

namespace WhereTheyGo
{
    // How well the served network reaches the people who want to travel (register
    // A1.8/A1.9, decided 2026-09-05): the share of journeys whose BOTH ends lie within
    // a walking horizon of a served stop, and the inequality of walking times to
    // service. The share is the sufficientarian floor the line selection has to
    // reach before it may optimise efficiency; the Gini is a diagnostic — scale-free,
    // so a target it is not.
    internal sealed class CoverageReport
    {
        public int Trips;
        public int TripsCovered;
        public int TripsOffNetwork;
        // Sums of binary32 trip weights in trip order, in double.
        public double TotalWeight;
        public double CoveredWeight;
        public float Share;
        // Weighted Gini of the walking time from each journey's origin to the nearest
        // served stop, uncovered journeys counted at twice the horizon. 0 = everyone
        // equally close, 1 = one journey has all the access.
        public double GiniWalk;
    }

    internal static class Coverage
    {
        public const int NotServed = int.MaxValue;

        // Walking time from each network node to the nearest served stop, bounded by
        // `horizonMs`; NotServed beyond it. Stops are (node, access) pairs as snapped
        // by WalkAccess; one bounded integer Dijkstra per stop, in stop
        // order, taking the minimum — the order cannot change the minimum.
        public static int[] ServedWalkMs(WalkGraph graph, IntDijkstra dijkstra, int[] stopNodes, int[] stopAccessMs, int count, int horizonMs)
        {
            var served = new int[graph.NodeCount];
            for (int n = 0; n < served.Length; n++)
            {
                served[n] = NotServed;
            }

            for (int i = 0; i < count; i++)
            {
                if (stopNodes[i] < 0)
                {
                    continue;
                }

                dijkstra.Run(graph, stopNodes[i], stopAccessMs[i], horizonMs);
                for (int s = 0; s < dijkstra.SettledCount; s++)
                {
                    int node = dijkstra.Settled[s];
                    long t = dijkstra.Dist[node];
                    if (t < served[node])
                    {
                        served[node] = (int)t;
                    }
                }
            }

            return served;
        }

        // A journey end is served when its node is within the horizon of a served stop
        // and the walk from the door to the node still fits: served[node] + access ≤ T.
        public static bool EndServed(int[] served, int node, int accessMs, int horizonMs)
        {
            return node >= 0 && node < served.Length && served[node] != NotServed
                && (long)served[node] + accessMs <= horizonMs;
        }

        // Coverage of a journey list against a served-walk field. Journeys with an end
        // off the pedestrian network count as uncovered (and are counted separately,
        // because a network the citizens cannot walk to is a different defect).
        public static CoverageReport Measure(
            int[] served, int horizonMs,
            int[] originNode, int[] originAccessMs, int[] destinationNode, int[] destinationAccessMs,
            float[] weight, int count)
        {
            var report = new CoverageReport { Trips = count };
            var walk = new double[count];
            for (int i = 0; i < count; i++)
            {
                report.TotalWeight += weight[i];
                if (originNode[i] < 0 || destinationNode[i] < 0)
                {
                    report.TripsOffNetwork++;
                }

                bool covered = EndServed(served, originNode[i], originAccessMs[i], horizonMs)
                    && EndServed(served, destinationNode[i], destinationAccessMs[i], horizonMs);
                if (covered)
                {
                    report.TripsCovered++;
                    report.CoveredWeight += weight[i];
                }

                walk[i] = EndServed(served, originNode[i], originAccessMs[i], horizonMs)
                    ? served[originNode[i]] + originAccessMs[i]
                    : 2.0 * horizonMs;
            }

            report.Share = report.TotalWeight > 0.0 ? (float)(report.CoveredWeight / report.TotalWeight) : 0f;
            report.GiniWalk = Gini(walk, weight, count);
            return report;
        }

        // Weighted Gini coefficient of non-negative values: sort by (value, index), then
        // 1 − Σ w_i (S_{i−1} + S_i) / (W · S_n) with S the running weighted value sum.
        // 0 when everything is equal or the total is zero.
        public static double Gini(double[] values, float[] weights, int count)
        {
            if (count == 0)
            {
                return 0.0;
            }

            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int byValue = values[a].CompareTo(values[b]);
                return byValue != 0 ? byValue : a.CompareTo(b);
            });

            double totalWeight = 0.0;
            double totalValue = 0.0;
            for (int i = 0; i < count; i++)
            {
                totalWeight += weights[i];
                totalValue += weights[i] * values[i];
            }

            if (totalWeight <= 0.0 || totalValue <= 0.0)
            {
                return 0.0;
            }

            double running = 0.0;
            double area = 0.0;
            for (int k = 0; k < count; k++)
            {
                int i = order[k];
                double before = running;
                running += weights[i] * values[i];
                area += weights[i] * (before + running);
            }

            return 1.0 - (area / (totalWeight * totalValue));
        }

    }
}
