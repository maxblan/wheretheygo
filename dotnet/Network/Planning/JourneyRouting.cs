using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    internal sealed class RoutingProblem
    {
        public float[] BaseStopX = Array.Empty<float>();
        public float[] BaseStopZ = Array.Empty<float>();
        public int BaseStopCount;
        public List<TransitLine> BaseLines = new List<TransitLine>();
        // Journeys as zone-to-zone pairs: centre positions and per-day weights.
        public float[] PairOx = Array.Empty<float>();
        public float[] PairOz = Array.Empty<float>();
        public float[] PairDx = Array.Empty<float>();
        public float[] PairDz = Array.Empty<float>();
        public float[] PairWeight = Array.Empty<float>();
        // Share of each pair's rides in the day period (Daytime); empty = all day.
        public float[] PairDayShare = Array.Empty<float>();
        public int PairCount;
        // One line to report on, as its index in BaseLines, or -1. Set when the player
        // has a line selected: Evaluate then records which pairs ride it, which is the
        // one thing the fold cannot reconstruct afterwards.
        public int TargetLine = -1;
        public float WalkRadius;
        public float BoardPenaltySeconds;
        public float MaxTravelSeconds;
        public float ZoneReachMetres;
        // Derived from the pairs on first evaluation (JourneyRouting.GeometryOf) and
        // reused by every evaluation since; the pair arrays are not modified after a
        // problem is built.
        internal RoutingGeometry? Geometry;
    }

    // What every evaluation of one problem shares: the distinct zone positions the
    // pairs' ends fall on (one zone node each in the transit graph), each pair's ends
    // as zone indices, the pairs grouped by origin zone, and the straight-line walk
    // time of each pair.
    internal sealed class RoutingGeometry
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

    internal sealed class RoutingResult
    {
        // Σ w · max(0, before − after) in seconds·journeys per day.
        public double TimeSaved;
        // Journey weight riding each EXISTING line (index = position in BaseLines):
        // what the network as it stands carries, which is what the line verdicts read
        // as a line's demand (register A8.2, decided 2026-09-06).
        public double[] BaseRiders = Array.Empty<double>();
        public double[] BaseRidersByDay = Array.Empty<double>();
        public double[] BaseRidersByNight = Array.Empty<double>();
        // Door-to-door time of every pair: the better of transit and walking.
        public float[] After = Array.Empty<float>();
        // The transit itinerary's own time per pair, float.MaxValue where the network
        // offers none. MarkCarried reads it; After cannot, because a journey faster on
        // foot has the walk in it.
        public float[] Transit = Array.Empty<float>();
        // Straight-line walking time per pair, the alternative every journey always has.
        public float[] WalkOnly = Array.Empty<float>();
        // Whether the network carries each pair (JourneyRouting.MarkCarried).
        public bool[] Carried = Array.Empty<bool>();
        // Whether each pair's fastest itinerary rides RoutingProblem.TargetLine.
        public bool[] RidesTarget = Array.Empty<bool>();
        // Time-weighted components over all pairs for the realism diagnostic
        // (walk 2.2, wait 2.1, ride 1 — TCQSM): what the set's journeys spend.
        public double WalkSeconds;
        public double WaitSeconds;
        public double RideSeconds;
    }

    // What "the network carries this journey" means, and how much of the city's travel
    // it adds up to (author's decision 2026-09-14). Two conditions, both nameable in a
    // tooltip:
    //
    //   1. the transit itinerary is faster than walking the whole way, and
    //   2. it stays under the ceiling — a multiple of this city's own median carried
    //      journey, so a 20-minute city and a 60-minute one are judged by their own
    //      standard rather than by a number chosen here.
    //
    // The median is taken over the journeys that pass condition 1, which is why this
    // is two passes and not one.
    internal readonly struct CarriedReport
    {
        public CarriedReport(float ceilingSeconds, float medianSeconds, int carriedPairs, double carriedWeight, double totalWeight)
        {
            CeilingSeconds = ceilingSeconds;
            MedianSeconds = medianSeconds;
            CarriedPairs = carriedPairs;
            CarriedWeight = carriedWeight;
            TotalWeight = totalWeight;
        }

        public float CeilingSeconds { get; }

        public float MedianSeconds { get; }

        public int CarriedPairs { get; }

        public double CarriedWeight { get; }

        public double TotalWeight { get; }

        // The share of the city's travel the network carries: the headline figure.
        public float Share => TotalWeight > 0.0 ? (float)(CarriedWeight / TotalWeight) : 0f;
    }

    // What one line does for the journeys that ride it, measured by taking it away.
    // The reading the player gets when they click a line (product plan 3.5): not a
    // verdict, not a recommendation, just the two numbers a line's value rests on.
    internal readonly struct LineContribution
    {
        public LineContribution(double riderWeight, double secondsSaved, double noSlowerWeight)
        {
            RiderWeight = riderWeight;
            SecondsSaved = secondsSaved;
            NoSlowerWeight = noSlowerWeight;
        }

        // Journeys a day whose fastest route rides this line.
        public double RiderWeight { get; }

        // Passenger-seconds a day those journeys save by it, against the best they
        // could do without it — which is the rest of the network, or walking.
        public double SecondsSaved { get; }

        // How much of RiderWeight would be no slower without the line at all. High
        // means the line runs beside something that already carries those journeys:
        // the answer to "why is my line empty".
        public double NoSlowerWeight { get; }

        public float DuplicateShare => RiderWeight > 0.0 ? (float)(NoSlowerWeight / RiderWeight) : 0f;

        public double MinutesSaved => SecondsSaved / 60.0;
    }

    internal static class JourneyRouting
    {

        // One search per origin DOOR rather than per pair, capped at the largest
        // door-to-door time the door's pairs can still improve on. Doors are not nodes
        // of the transit graph: a search starts at every stop within reach of the
        // origin door (at the walking time to it) and a destination's time is the least
        // over the stops within reach of its door — the same distances the door-node
        // graph of the specification gives (a door is reached, never walked through),
        // at a fraction of the edges. Both the per-door grouping and the cap are exact
        // rewrites of the per-pair search: pairs from one door see the same stops, and
        // a network with lines added never lengthens a journey, so a destination
        // further than a pair's own `before` yields `before` whether or not the search
        // finished the distance. The sums stay in pair order: per-pair results are
        // collected during the searches and folded afterwards, so one thread or sixteen
        // give the same bits.
        public static RoutingResult Evaluate(RoutingProblem problem, float[]? before)
        {
            RoutingGeometry geometry = GeometryOf(problem);
            TransitNetwork network = TransitGraph.Build(
                problem.BaseStopX, problem.BaseStopZ, problem.BaseStopCount, problem.BaseLines,
                problem.WalkRadius, problem.BoardPenaltySeconds);
            DoorAccess access = DoorAccess.Build(geometry, problem.BaseStopX, problem.BaseStopZ, problem.BaseStopCount, problem.ZoneReachMetres);
            var evaluation = new RoutingResult
            {
                BaseRiders = new double[problem.BaseLines.Count],
                BaseRidersByDay = new double[problem.BaseLines.Count],
                BaseRidersByNight = new double[problem.BaseLines.Count],
                After = new float[problem.PairCount],
                Transit = new float[problem.PairCount],
                WalkOnly = geometry.WalkOnly,
                RidesTarget = problem.TargetLine >= 0 ? new bool[problem.PairCount] : Array.Empty<bool>(),
            };
            bool hasShares = problem.PairDayShare.Length >= problem.PairCount;
            int lineCount = problem.BaseLines.Count;
            var legs = new PairLegs(problem.PairCount);
            int nodeCount = network.Graph.NodeCount;
            // The origins are independent (each writes only its own pairs' legs), so
            // they are spread over half the cores — the other half stays the game's.
            var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
            _ = System.Threading.Tasks.Parallel.For(
                0,
                geometry.ZoneCount,
                options,
                () => new DijkstraWorkspace(nodeCount),
                (zone, _, workspace) =>
                {
                    SearchOrigin(problem, geometry, network, access, before, lineCount, zone, workspace, legs);
                    return workspace;
                },
                static _ => { });
            for (int i = 0; i < problem.PairCount; i++)
            {
                evaluation.After[i] = legs.After[i];
                evaluation.Transit[i] = legs.Transit[i];
                double weight = problem.PairWeight[i];
                evaluation.WalkSeconds += weight * legs.Walk[i];
                evaluation.WaitSeconds += weight * legs.Wait[i];
                evaluation.RideSeconds += weight * legs.Ride[i];
                int[]? ridden = legs.Ridden[i];
                if (ridden is not null)
                {
                    double dayShare = hasShares ? problem.PairDayShare[i] : 1.0;
                    for (int r = 0; r < ridden.Length; r++)
                    {
                        int line = ridden[r];
                        evaluation.BaseRiders[line] += weight;
                        evaluation.BaseRidersByDay[line] += weight * dayShare;
                        evaluation.BaseRidersByNight[line] += weight * (1.0 - dayShare);
                        if (line == problem.TargetLine)
                        {
                            evaluation.RidesTarget[i] = true;
                        }
                    }
                }

                if (before is not null && before[i] > evaluation.After[i])
                {
                    evaluation.TimeSaved += weight * (double)(before[i] - evaluation.After[i]);
                }
            }

            return evaluation;
        }

        // Compares the network as it stands with the same network minus one line.
        //
        // `with` is the full network's door-to-door times and which pairs ride the
        // line; `without` is the times when it is taken out. Removing a line can never
        // make a journey faster, so the difference is what the line is worth — and a
        // journey that rides it while losing nothing by its removal is riding a line
        // that duplicates something else.
        public static LineContribution Measure(RoutingProblem problem, RoutingResult with, RoutingResult without)
        {
            double riders = 0.0;
            double seconds = 0.0;
            double noSlower = 0.0;
            for (int i = 0; i < problem.PairCount; i++)
            {
                if (i >= with.RidesTarget.Length || !with.RidesTarget[i])
                {
                    continue;
                }

                double weight = problem.PairWeight[i];
                riders += weight;
                float lost = without.After[i] - with.After[i];
                if (lost > Assumptions.NoSlowerSeconds)
                {
                    seconds += weight * lost;
                }
                else
                {
                    noSlower += weight;
                }
            }

            return new LineContribution(riders, seconds, noSlower);
        }

        // Fills `result.Carried` and reports what it adds up to. `scratch` is reordered
        // in place by the median (SelectKth), so it is the caller's buffer and never
        // the times themselves.
        public static CarriedReport MarkCarried(RoutingProblem problem, RoutingResult result, float[] scratch)
        {
            int count = problem.PairCount;
            result.Carried = new bool[count];
            int faster = 0;
            for (int i = 0; i < count; i++)
            {
                if (result.Transit[i] < result.WalkOnly[i])
                {
                    scratch[faster++] = result.Transit[i];
                }
            }

            float ceiling = TransitGraph.ServedCeiling(
                scratch, faster, Assumptions.ServedCeilingMultiple, Assumptions.MaxJourneySeconds,
                Assumptions.MinPairsForServedMedian, out float median);

            int carriedPairs = 0;
            double carriedWeight = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < count; i++)
            {
                double weight = problem.PairWeight[i];
                totalWeight += weight;
                if (result.Transit[i] < result.WalkOnly[i] && result.Transit[i] <= ceiling)
                {
                    result.Carried[i] = true;
                    carriedPairs++;
                    carriedWeight += weight;
                }
            }

            return new CarriedReport(ceiling, median, carriedPairs, carriedWeight, totalWeight);
        }

        // The stops each door can walk to, with the walking time as the specification's
        // door edge costs it: straight line over the planning speed, floored at 0.01 s.
        private sealed class DoorAccess
        {
            public int[] Start = Array.Empty<int>();
            public int[] Stop = Array.Empty<int>();
            public float[] Cost = Array.Empty<float>();

            public static DoorAccess Build(RoutingGeometry geometry, float[] stopX, float[] stopZ, int stopCount, float reach)
            {
                var access = new DoorAccess { Start = new int[geometry.ZoneCount + 1] };
                var stops = new List<int>();
                var costs = new List<float>();
                float reachSq = reach * reach;
                for (int zone = 0; zone < geometry.ZoneCount; zone++)
                {
                    access.Start[zone] = stops.Count;
                    float zx = geometry.ZoneX[zone];
                    float zz = geometry.ZoneZ[zone];
                    for (int stop = 0; stop < stopCount; stop++)
                    {
                        float dx = stopX[stop] - zx;
                        float dz = stopZ[stop] - zz;
                        float distSq = (dx * dx) + (dz * dz);
                        if (distSq <= reachSq)
                        {
                            stops.Add(stop);
                            costs.Add(Math.Max(0.01f, (float)Math.Sqrt(distSq) / Assumptions.WalkSpeed));
                        }
                    }
                }

                access.Start[geometry.ZoneCount] = stops.Count;
                access.Stop = stops.ToArray();
                access.Cost = costs.ToArray();
                return access;
            }
        }

        // The door-to-door times of every pair leaving one origin door: one search from
        // the stops the door reaches, then the least stop-plus-walk for each destination.
        private static void SearchOrigin(
            RoutingProblem problem, RoutingGeometry geometry, TransitNetwork network, DoorAccess access, float[]? before,
            int lineCount, int zone, DijkstraWorkspace workspace, PairLegs legs)
        {
            int first = geometry.OriginStart[zone];
            int last = geometry.OriginStart[zone + 1];
            if (first == last)
            {
                return;
            }

            float cap = 0f;
            for (int k = first; k < last; k++)
            {
                int pair = geometry.PairsByOrigin[k];
                cap = Math.Max(cap, before is null ? geometry.WalkOnly[pair] : before[pair]);
            }

            cap = Math.Min(problem.MaxTravelSeconds, cap);
            int startAt = access.Start[zone];
            int startCount = access.Start[zone + 1] - startAt;
            var sources = new int[startCount];
            var costs = new float[startCount];
            Array.Copy(access.Stop, startAt, sources, 0, startCount);
            Array.Copy(access.Cost, startAt, costs, 0, startCount);
            workspace.RunFromMany(network.Graph, sources, costs, startCount, cap);

            for (int k = first; k < last; k++)
            {
                int pair = geometry.PairsByOrigin[k];
                int destination = geometry.PairDestinationZone[pair];
                float walkOnly = geometry.WalkOnly[pair];
                float transit = float.MaxValue;
                int alight = -1;
                float alightWalk = 0f;
                for (int d = access.Start[destination]; d < access.Start[destination + 1]; d++)
                {
                    int stop = access.Stop[d];
                    float atStop = workspace.Dist[stop];
                    if (atStop == float.MaxValue)
                    {
                        continue;
                    }

                    float total = atStop + access.Cost[d];
                    if (total <= cap && total < transit)
                    {
                        transit = total;
                        alight = stop;
                        alightWalk = access.Cost[d];
                    }
                }

                legs.After[pair] = Math.Min(walkOnly, transit);
                legs.Transit[pair] = transit;
                if (transit < walkOnly)
                {
                    legs.Walk[pair] += alightWalk;
                    AttributeItinerary(network, workspace, alight, lineCount, pair, legs);
                }
                else
                {
                    legs.Walk[pair] = walkOnly;
                }
            }
        }

        // Per-pair itinerary components, held until the pair-ordered fold.
        private sealed class PairLegs
        {
            public readonly float[] After;
            // The best transit itinerary's own time, float.MaxValue where there is
            // none — apart from After, which is the better of transit and walking.
            public readonly float[] Transit;
            public readonly double[] Walk;
            public readonly double[] Wait;
            public readonly double[] Ride;
            public readonly int[]?[] Ridden;

            public PairLegs(int pairCount)
            {
                After = new float[pairCount];
                Transit = new float[pairCount];
                Walk = new double[pairCount];
                Wait = new double[pairCount];
                Ride = new double[pairCount];
                Ridden = new int[]?[pairCount];
            }
        }

        internal static RoutingGeometry GeometryOf(RoutingProblem problem)
        {
            RoutingGeometry? cached = problem.Geometry;
            if (cached is not null)
            {
                return cached;
            }

            var geometry = new RoutingGeometry
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
        public static float WalkOnlySeconds(RoutingProblem problem, int pair)
        {
            float dx = problem.PairDx[pair] - problem.PairOx[pair];
            float dz = problem.PairDz[pair] - problem.PairOz[pair];
            return (float)Math.Sqrt((dx * dx) + (dz * dz)) / Assumptions.WalkSpeed;
        }

        // Walks the retained shortest itinerary back from the alighting stop to the
        // stop the journey started at, splitting its cost into walk, wait and ride and
        // noting each ridden line once, by its index in the transit network. The
        // starting stop's distance is the origin's access walk.
        private static void AttributeItinerary(
            TransitNetwork network, DijkstraWorkspace workspace, int alight,
            int lineCount, int pair, PairLegs into)
        {
            int node = alight;
            int guard = network.Graph.EdgeCount + 2;
            List<int>? ridden = null;
            while (guard-- > 0)
            {
                int edge = workspace.PrevEdge[node];
                if (edge < 0)
                {
                    into.Walk[pair] += workspace.Dist[node];
                    break;
                }

                float cost = network.Graph.EdgeCost[edge];
                switch (network.EdgeKind[edge])
                {
                    case TransitEdgeKind.Walk:
                        into.Walk[pair] += cost;
                        break;
                    case TransitEdgeKind.Access:
                        into.Wait[pair] += cost;
                        int line = network.EdgeLine[edge];
                        if (line >= 0 && line < lineCount)
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

    }
}
