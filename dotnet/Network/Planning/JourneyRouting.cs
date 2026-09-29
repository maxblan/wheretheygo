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
        // A pair whose walk the whole way is within this many seconds is WALKED
        // (RoutingResult.Walked): a walk, not a transit question. The system sets it
        // from the player's coverage horizon, the walk they accept to reach a stop: a
        // journey shorter than that walk is a walk. At 0, which a problem built without
        // a horizon has, nothing is walked, since every pair runs between two distinct
        // doors and so takes some time on foot.
        public float WalkedHorizonSeconds;
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
        // Journey weight riding each EXISTING line (index = position in BaseLines):
        // what the network as it stands carries, which is what the line verdicts read
        // as a line's demand. Only CARRIED pairs are credited, not every pair that
        // merely beats walking: the line window, the rank, the "carries" share and the
        // highlighted bands then count the same journeys the headline and the band
        // colour do, and "carried" has one definition wherever it is read.
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
        // Whether each pair is a walk (JourneyRouting.MarkWalked): its walk the whole
        // way is within RoutingProblem.WalkedHorizonSeconds. Decided before Carried and
        // taking precedence over it: a walked pair is never carried, never credited to
        // a line and never a sample for the served-ceiling median.
        public bool[] Walked = Array.Empty<bool>();
        // Whether the network carries each pair (JourneyRouting.MarkCarried).
        public bool[] Carried = Array.Empty<bool>();
        // What Walked and Carried add up to: the ceiling the second was judged by, and
        // the shares of the city's travel each comes to.
        public CarriedReport Report;
        // Whether each pair is carried AND its itinerary rides RoutingProblem.TargetLine.
        public bool[] RidesTarget = Array.Empty<bool>();
    }

    // What "the network carries this journey" means, and how much of the city's travel
    // it adds up to. Two conditions, both nameable in a
    // tooltip:
    //
    //   1. the transit itinerary is faster than walking the whole way, and
    //   2. it stays under the ceiling, a multiple of this city's own median carried
    //      journey, so a 20-minute city and a 60-minute one are judged by their own
    //      standard rather than by a number chosen here.
    //
    // A journey within the walking horizon is a WALK and is left
    // out of both sides: it is neither carried nor a failure of the network, because no
    // itinerary with an access walk at each end and a wait could ever carry it. A 300 m
    // corridor used to draw as a fat warm band, "68 % travel without transit", for
    // people who were walking.
    //
    // The median is taken over the journeys that pass condition 1 and are not walks,
    // which is why this is two passes and not one. It is the median JOURNEY, weighted
    // by how many make each pair: a pair two hundred people make
    // sets the city's typical journey two hundred times as firmly as a pair one person
    // makes.
    internal readonly struct CarriedReport
    {
        public CarriedReport(
            float ceilingSeconds, float medianSeconds, int carriedPairs, double carriedWeight, double totalWeight,
            int walkedPairs, double walkedWeight)
        {
            CeilingSeconds = ceilingSeconds;
            MedianSeconds = medianSeconds;
            CarriedPairs = carriedPairs;
            CarriedWeight = carriedWeight;
            TotalWeight = totalWeight;
            WalkedPairs = walkedPairs;
            WalkedWeight = walkedWeight;
        }

        public float CeilingSeconds { get; }

        public float MedianSeconds { get; }

        public int CarriedPairs { get; }

        public double CarriedWeight { get; }

        public double TotalWeight { get; }

        public int WalkedPairs { get; }

        public double WalkedWeight { get; }

        // The share of the journeys transit COULD serve that the network carries: the
        // headline figure. The walks are taken out of the denominator, since counting
        // them as failures held the figure down for journeys no network can carry.
        public float Share
        {
            get
            {
                double servable = TotalWeight - WalkedWeight;
                return servable > 0.0 ? (float)(CarriedWeight / servable) : 0f;
            }
        }

        // The share of ALL the city's travel that is a walk, against the whole because
        // that is the whole it is being left out of.
        public float WalkedShare => TotalWeight > 0.0 ? (float)(WalkedWeight / TotalWeight) : 0f;
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

        // Carried journeys a day that ride this line.
        public double RiderWeight { get; }

        // Passenger-seconds a day those journeys save by it, against the best they
        // could do without it, which is the rest of the network, or walking.
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

        // One search per origin DOOR rather than per pair, capped at the longest walk
        // among the door's pairs - past it transit cannot beat walking, so the answer is
        // the walk either way. Doors are not nodes of the transit graph: a search starts
        // at every stop within reach of the origin door (at the walking time to it) and
        // a destination's time is the least over the stops within reach of its door -
        // a door is reached, never walked through. The sums stay in pair order: per-pair
        // results are collected during the searches and folded afterwards, so one
        // thread or sixteen give the same bits.
        public static RoutingResult Evaluate(RoutingProblem problem)
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
            int lineCount = problem.BaseLines.Count;
            var legs = new PairLegs(problem.PairCount);
            int nodeCount = network.Graph.NodeCount;
            // The origins are independent (each writes only its own pairs' legs), so
            // they are spread over half the cores, leaving the other half to the game.
            //
            // The pool threads doing the work have no DeferredLog buffer of their own,
            // and the game's logger must not be written from them (DeferredLog). Each
            // thread is therefore given a buffer for the duration and its lines are
            // handed on to the calling thread's afterwards.
            var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
            var pooledLines = new List<DeferredLogLine>();
            object gate = new object();
            _ = System.Threading.Tasks.Parallel.For(
                0,
                geometry.ZoneCount,
                options,
                () => new SearchThread(nodeCount),
                (zone, _, thread) =>
                {
                    SearchOrigin(problem, geometry, network, access, lineCount, zone, thread.Workspace, legs);
                    return thread;
                },
                thread =>
                {
                    _ = DeferredLog.Bind(thread.PreviousBuffer);
                    lock (gate)
                    {
                        pooledLines.AddRange(thread.Lines);
                    }
                });
            for (int i = 0; i < pooledLines.Count; i++)
            {
                DeferredLog.Write(pooledLines[i].Level, pooledLines[i].Text);
            }

            for (int i = 0; i < problem.PairCount; i++)
            {
                evaluation.After[i] = legs.After[i];
                evaluation.Transit[i] = legs.Transit[i];
            }

            // Walked and carried first, riders second: a line is credited only with the
            // journeys the network carries, and whether a journey is carried is decided
            // against the whole city's times, so it cannot be known inside the searches.
            evaluation.Report = MarkCarried(problem, evaluation, new float[problem.PairCount], new float[problem.PairCount]);
            CreditRiders(problem, evaluation, legs);
            return evaluation;
        }

        // Folds each carried pair's weight onto the lines its itinerary rides, split by
        // the share of its rides in the day period, and notes which pairs ride the
        // target line. A pair that beats walking but is not carried (over the ceiling)
        // credits nothing: it used to, and a line could then be shown
        // "carrying" journeys the headline and the band colour had refused as too slow
        // for this city.
        private static void CreditRiders(RoutingProblem problem, RoutingResult evaluation, PairLegs legs)
        {
            bool hasShares = problem.PairDayShare.Length >= problem.PairCount;
            for (int i = 0; i < problem.PairCount; i++)
            {
                int[]? ridden = legs.Ridden[i];
                if (ridden is null || !evaluation.Carried[i])
                {
                    continue;
                }

                double weight = problem.PairWeight[i];
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
        }

        // One pool thread's scratch for the searches and the log lines it writes while
        // it holds them.
        private sealed class SearchThread
        {
            public readonly DijkstraWorkspace Workspace;
            public readonly List<DeferredLogLine> Lines = new List<DeferredLogLine>();
            public readonly List<DeferredLogLine>? PreviousBuffer;

            public SearchThread(int nodeCount)
            {
                Workspace = new DijkstraWorkspace(nodeCount);
                PreviousBuffer = DeferredLog.Bind(Lines);
            }
        }

        // Compares the network as it stands with the same network minus one line.
        //
        // `with` is the full network's door-to-door times and which pairs ride the
        // line; `without` is the times when it is taken out. Removing a line can never
        // make a journey faster, so the difference is what the line is worth. A
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

        // Fills `result.Walked` and `result.Carried` and reports what they add up to.
        // Evaluate runs it before crediting riders; it is public so a synthetic result
        // can be judged without routing. `scratchTimes` and `scratchWeights`, one slot
        // per pair, are reordered in place by the median (TransitGraph.WeightedMedian),
        // so they are the caller's buffers and never the times or the weights themselves.
        public static CarriedReport MarkCarried(RoutingProblem problem, RoutingResult result, float[] scratchTimes, float[] scratchWeights)
        {
            int count = problem.PairCount;
            double walkedWeight = MarkWalked(problem, result, out int walkedPairs);
            result.Carried = new bool[count];
            int faster = 0;
            for (int i = 0; i < count; i++)
            {
                if (!result.Walked[i] && result.Transit[i] < result.WalkOnly[i])
                {
                    scratchTimes[faster] = result.Transit[i];
                    scratchWeights[faster] = problem.PairWeight[i];
                    faster++;
                }
            }

            float ceiling = TransitGraph.ServedCeiling(
                scratchTimes, scratchWeights, faster, Assumptions.ServedCeilingMultiple, Assumptions.MaxJourneySeconds,
                Assumptions.MinPairsForServedMedian, out float median);

            int carriedPairs = 0;
            double carriedWeight = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < count; i++)
            {
                double weight = problem.PairWeight[i];
                totalWeight += weight;
                if (!result.Walked[i] && result.Transit[i] < result.WalkOnly[i] && result.Transit[i] <= ceiling)
                {
                    result.Carried[i] = true;
                    carriedPairs++;
                    carriedWeight += weight;
                }
            }

            return new CarriedReport(ceiling, median, carriedPairs, carriedWeight, totalWeight, walkedPairs, walkedWeight);
        }

        // Fills `result.Walked`: a pair is a walk when walking it the whole way fits
        // the horizon. Per DOOR pair, which is where the
        // walk time lives; a band folds in zone pairs up to a kilometre apart, so it
        // cannot be decided there. Returns the walked weight.
        private static double MarkWalked(RoutingProblem problem, RoutingResult result, out int walkedPairs)
        {
            int count = problem.PairCount;
            float horizon = problem.WalkedHorizonSeconds;
            result.Walked = new bool[count];
            walkedPairs = 0;
            double walkedWeight = 0.0;
            for (int i = 0; i < count; i++)
            {
                if (result.WalkOnly[i] <= horizon)
                {
                    result.Walked[i] = true;
                    walkedPairs++;
                    walkedWeight += problem.PairWeight[i];
                }
            }

            return walkedWeight;
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

                // Bucketed on a grid of the reach, the way the walk edges between stops
                // are: an all-pairs sweep here was every door against every stop, and
                // it ran twice per pass with a line selected. The grid hands back the
                // same stops in the same ascending order, so the lists are the ones the
                // sweep built and the routing's bits do not move.
                TransitGraph.StopGrid? grid = stopCount > 0 && reach > 0f
                    ? TransitGraph.StopGrid.Build(stopX, stopZ, stopCount, reach)
                    : null;
                var near = new List<int>();
                for (int zone = 0; zone < geometry.ZoneCount; zone++)
                {
                    access.Start[zone] = stops.Count;
                    if (grid is null)
                    {
                        continue;
                    }

                    float zx = geometry.ZoneX[zone];
                    float zz = geometry.ZoneZ[zone];
                    near.Clear();
                    grid.Value.CollectWithin(zx, zz, stopX, stopZ, reachSq, near);
                    for (int n = 0; n < near.Count; n++)
                    {
                        int stop = near[n];
                        float dx = stopX[stop] - zx;
                        float dz = stopZ[stop] - zz;
                        float distSq = (dx * dx) + (dz * dz);
                        stops.Add(stop);
                        costs.Add(Math.Max(Assumptions.MinEdgeSeconds, (float)Math.Sqrt(distSq) / Assumptions.WalkSpeed));
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
            RoutingProblem problem, RoutingGeometry geometry, TransitNetwork network, DoorAccess access,
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
                cap = Math.Max(cap, geometry.WalkOnly[pair]);
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
                    }
                }

                legs.After[pair] = Math.Min(walkOnly, transit);
                legs.Transit[pair] = transit;
                if (transit < walkOnly)
                {
                    legs.Ridden[pair] = RiddenLines(network, workspace, alight, lineCount);
                }
            }
        }

        // Per-pair results, held until the pair-ordered fold.
        private sealed class PairLegs
        {
            public readonly float[] After;
            // The best transit itinerary's own time, float.MaxValue where there is
            // none, unlike After, which is the better of transit and walking.
            public readonly float[] Transit;
            public readonly int[]?[] Ridden;

            public PairLegs(int pairCount)
            {
                After = new float[pairCount];
                Transit = new float[pairCount];
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
        // stop the journey started at and notes each ridden line once, by its index in
        // the transit network. Null when no line was boarded.
        private static int[]? RiddenLines(TransitNetwork network, DijkstraWorkspace workspace, int alight, int lineCount)
        {
            int node = alight;
            int guard = network.Graph.EdgeCount + 2;
            List<int>? ridden = null;
            while (guard-- > 0)
            {
                int edge = workspace.PrevEdge[node];
                if (edge < 0)
                {
                    break;
                }

                if (network.EdgeKind[edge] == TransitEdgeKind.Access)
                {
                    int line = network.EdgeLine[edge];
                    if (line >= 0 && line < lineCount)
                    {
                        ridden ??= new List<int>();
                        if (!ridden.Contains(line))
                        {
                            ridden.Add(line);
                        }
                    }
                }

                node = network.Graph.OtherEnd(edge, node);
            }

            return ridden?.ToArray();
        }

    }
}
