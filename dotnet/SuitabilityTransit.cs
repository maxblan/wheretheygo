using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    internal enum TransitEdgeKind
    {
        Walk = 0,
        // Stepping between the street and a vehicle. Undirected, so one line use
        // traverses it twice — see the halved cost in Build.
        Access = 1,
        Ride = 2,
    }

    // A routable model of the transit system itself, as opposed to the road, rail and
    // water networks used to trace where a line could physically run.
    //
    // The point of the line-stop nodes is that boarding has to cost something. Without
    // them a rider could glide between lines for free and a three-change itinerary
    // would look as good as a direct one, which is precisely the mistake that made
    // feeder lines score as worthless.
    //
    // Node layout: [0, StopCount) are stop nodes; the rest are line-stop nodes, one
    // per (line, stop-along-that-line) pair.
    internal sealed class TransitNetwork
    {
        public int StopCount;
        public CompactGraph Graph = new CompactGraph();
        // Per edge, parallel to Graph's edge arrays.
        public TransitEdgeKind[] EdgeKind = Array.Empty<TransitEdgeKind>();
        public int[] EdgeLine = Array.Empty<int>();
    }

    // One line as the router needs it: the stops it calls at in travel order, how long
    // a rider waits for it, and how fast it covers ground.
    internal struct TransitLine
    {
        public int[]? m_Stops;
        // Expected wait in seconds, computed the way the game's own pathfinder does:
        // max(vehicleInterval / 2, observedAverageWait) - stopDwell, floored at zero.
        // m_VehicleInterval converges to lineDuration / fleetSize, so it already IS
        // the mean headway and half of it is the standard random-arrival wait.
        public float m_ExpectedWait;
        // Seconds from the previous stop to this one, taken from the route segment's
        // PathInformation.m_Duration. Index i is the ride into m_Stops[i], so index 0
        // is unused. Null falls back to distance over m_SpeedMetresPerSecond.
        public float[]? m_RideSeconds;
        public float m_SpeedMetresPerSecond;
    }

    // The served stops of the existing network, each carrying the set of modes a
    // rider can reach on foot from it. Built by
    // SuitabilityTransit.BuildInterchangeMap, which is where the union is explained.
    internal readonly struct InterchangeMap
    {
        private readonly float[] m_StopX;
        private readonly float[] m_StopZ;
        private readonly int[] m_Reachable;
        private readonly int m_Count;
        private readonly float m_Radius;

        public InterchangeMap(float[] stopX, float[] stopZ, int[] reachable, int count, float radius)
        {
            m_StopX = stopX;
            m_StopZ = stopZ;
            m_Reachable = reachable;
            m_Count = count;
            m_Radius = radius;
        }

        public int Count => m_Count;

        // How far a rider will walk to change vehicle. The one owner of that distance:
        // the walk edges in the transit graph, this map and anything aiming a line at
        // an interchange all have to agree on it or a suggestion promises a transfer
        // the routing will not credit.
        public float Radius => m_Radius;

        // Squared distance to the nearest served stop within `radius`, or false when
        // there is none. Squared because the caller only ever compares it with another
        // distance, and a square root per sampled position buys nothing.
        public bool TryNearest(float x, float z, float radius, out float distanceSq)
        {
            distanceSq = 0f;
            if (m_StopX is null || m_StopZ is null)
            {
                return false;
            }

            float radiusSq = radius * radius;
            bool found = false;
            for (int i = 0; i < m_Count; i++)
            {
                float dx = m_StopX[i] - x;
                float dz = m_StopZ[i] - z;
                float candidate = (dx * dx) + (dz * dz);
                if (candidate > radiusSq || (found && candidate >= distanceSq))
                {
                    continue;
                }

                found = true;
                distanceSq = candidate;
            }

            return found;
        }

        // The best place near (x, z) for a line of `ownMode` to call at: the one
        // offering the most OTHER modes, nearest among equals. False when there is no
        // served stop within the walk radius at all.
        //
        // Any served stop counts, its own mode included. A SUGGESTION is never the line
        // that is already there, so a new tram calling at an existing tram station lets
        // riders change between tram lines — the heatmap's interchange term excludes the
        // same mode only because, scoring a bare tile, it cannot know which line would
        // run there. Mode still decides the RANKING, so a place where three modes meet
        // beats a lone stop; it no longer decides whether the place counts at all.
        public bool TryFindNear(ModePreset ownMode, float x, float z, float radius, out float hubX, out float hubZ)
        {
            hubX = 0f;
            hubZ = 0f;
            if (m_StopX is null || m_StopZ is null || m_Reachable is null)
            {
                return false;
            }

            int ownBit = TransitModes.ModeBit(ownMode);
            float radiusSq = radius * radius;
            int bestModes = 0;
            float bestSq = 0f;
            bool found = false;

            for (int i = 0; i < m_Count; i++)
            {
                float dx = m_StopX[i] - x;
                float dz = m_StopZ[i] - z;
                float distanceSq = (dx * dx) + (dz * dz);
                if (distanceSq > radiusSq)
                {
                    continue;
                }

                int modes = TransitModes.ModeCount(m_Reachable[i] & ~ownBit);
                if (!found || modes > bestModes || (modes == bestModes && distanceSq < bestSq))
                {
                    found = true;
                    bestModes = modes;
                    bestSq = distanceSq;
                    hubX = m_StopX[i];
                    hubZ = m_StopZ[i];
                }
            }

            return found;
        }
    }

    internal static class SuitabilityTransit
    {
        // Walking is slow enough that a long connection is worse than a detour by
        // vehicle, which is what keeps interchanges local.
        public const float WalkSpeed = 1.4f;

        // Flat cost of boarding, on top of the wait. Matches TransportPathfind's
        // m_StartingCost time component (5), so a change of vehicle costs what the
        // game itself charges for one. There is no separate transfer penalty in
        // vanilla — a transfer is simply a second boarding — so modelling boardings
        // is modelling transfers.
        public const float DefaultBoardPenaltySeconds = 5f;

        // Vanilla's rider wait, from PathUtils.GetTransportStopSpecification.
        public static float ExpectedWait(float vehicleInterval, float observedAverageWait, float stopDwell)
        {
            return Math.Max(0f, Math.Max(vehicleInterval * 0.5f, observedAverageWait) - stopDwell);
        }

        public static TransitNetwork Build(
            float[] stopX,
            float[] stopZ,
            int stopCount,
            List<TransitLine> lines,
            float walkRadius,
            float boardPenaltySeconds)
        {
            var edgeA = new List<int>();
            var edgeB = new List<int>();
            var edgeCost = new List<float>();
            var kinds = new List<TransitEdgeKind>();
            var edgeLines = new List<int>();

            int nextNode = stopCount;

            void AddEdge(int a, int b, float cost, TransitEdgeKind kind, int line)
            {
                edgeA.Add(a);
                edgeB.Add(b);
                edgeCost.Add(Math.Max(0.01f, cost));
                kinds.Add(kind);
                edgeLines.Add(line);
            }

            // Walking between nearby stops. This is what turns a bus stop beside a
            // metro entrance into one interchange rather than two unrelated stops.
            AddWalkEdges(stopX, stopZ, stopCount, walkRadius, AddEdge);

            for (int l = 0; l < lines.Count; l++)
            {
                TransitLine line = lines[l];
                if (line.m_Stops is null || line.m_Stops.Length < 2)
                {
                    continue;
                }

                int first = nextNode;
                nextNode += line.m_Stops.Length;

                for (int i = 0; i < line.m_Stops.Length; i++)
                {
                    int stop = line.m_Stops[i];
                    if (stop < 0 || stop >= stopCount)
                    {
                        continue;
                    }

                    int aboard = first + i;

                    // CompactGraph is undirected, so a zero-cost "alight" edge would
                    // be traversable backwards as a FREE boarding — which made
                    // transfers cost nothing at all. Instead there is one access edge
                    // carrying half the boarding cost: using a line traverses it twice
                    // (on and off), so each line used pays the full cost exactly once,
                    // and every change of vehicle pays it again.
                    AddEdge(stop, aboard, (line.m_ExpectedWait + boardPenaltySeconds) * 0.5f, TransitEdgeKind.Access, l);

                    if (i > 0)
                    {
                        int previousStop = line.m_Stops[i - 1];
                        if (previousStop >= 0 && previousStop < stopCount)
                        {
                            // Prefer the real pathfound duration the game keeps per
                            // route segment; fall back to geometry only if absent.
                            float ride;
                            if (line.m_RideSeconds is not null && i < line.m_RideSeconds.Length && line.m_RideSeconds[i] > 0f)
                            {
                                ride = line.m_RideSeconds[i];
                            }
                            else
                            {
                                float dx = stopX[stop] - stopX[previousStop];
                                float dz = stopZ[stop] - stopZ[previousStop];
                                float distance = (float)Math.Sqrt(dx * dx + dz * dz);
                                ride = distance / Math.Max(1f, line.m_SpeedMetresPerSecond);
                            }

                            AddEdge(first + i - 1, aboard, ride, TransitEdgeKind.Ride, l);
                        }
                    }
                }
            }

            return new TransitNetwork
            {
                StopCount = stopCount,
                Graph = CompactGraph.Build(nextNode, edgeA.ToArray(), edgeB.ToArray(), edgeCost.ToArray(), edgeA.Count),
                EdgeKind = kinds.ToArray(),
                EdgeLine = edgeLines.ToArray(),
            };
        }

        // Joins every pair of stops within walking distance.
        //
        // Bucketed on a grid of the walk radius rather than swept exhaustively. Route
        // scoring rebuilds this network once per candidate line — up to ninety-six
        // times per refresh — and an all-pairs sweep over a city's five hundred stops
        // is a hundred and twenty-five thousand distance tests each time, on the main
        // thread.
        //
        // Pairs are still emitted in ascending (a, b) order, so the edge list is
        // identical to the sweep it replaces and nothing downstream can tell them
        // apart.
        private static void AddWalkEdges(
            float[] stopX,
            float[] stopZ,
            int stopCount,
            float walkRadius,
            Action<int, int, float, TransitEdgeKind, int> addEdge)
        {
            if (stopCount <= 1 || walkRadius <= 0f)
            {
                return;
            }

            StopGrid grid = StopGrid.Build(stopX, stopZ, stopCount, walkRadius);
            float walkRadiusSq = walkRadius * walkRadius;
            var neighbours = new List<int>();
            for (int a = 0; a < stopCount; a++)
            {
                neighbours.Clear();
                grid.CollectNeighboursAfter(a, stopX, stopZ, walkRadiusSq, neighbours);

                // Ascending, so the edge list is the one the exhaustive sweep produced.
                neighbours.Sort();
                for (int n = 0; n < neighbours.Count; n++)
                {
                    int b = neighbours[n];
                    float dx = stopX[a] - stopX[b];
                    float dz = stopZ[a] - stopZ[b];
                    float distance = (float)Math.Sqrt((dx * dx) + (dz * dz));
                    addEdge(a, b, distance / WalkSpeed, TransitEdgeKind.Walk, -1);
                }
            }
        }

        // Stops bucketed onto a grid of the walk radius, so a stop only has to be
        // compared with the stops in its own cell and the eight around it.
        private readonly struct StopGrid
        {
            private readonly int m_Cols;
            private readonly int m_Rows;
            private readonly int[] m_CellOf;
            private readonly int[] m_Offsets;
            private readonly int[] m_ByCell;

            private StopGrid(int cols, int rows, int[] cellOf, int[] offsets, int[] byCell)
            {
                m_Cols = cols;
                m_Rows = rows;
                m_CellOf = cellOf;
                m_Offsets = offsets;
                m_ByCell = byCell;
            }

            // Count, prefix-sum, fill — the same shape the scoring job's buckets use,
            // and no per-cell list to allocate.
            public static StopGrid Build(float[] stopX, float[] stopZ, int stopCount, float cellSize)
            {
                float minX = stopX[0];
                float minZ = stopZ[0];
                float maxX = minX;
                float maxZ = minZ;
                for (int i = 1; i < stopCount; i++)
                {
                    minX = Math.Min(minX, stopX[i]);
                    minZ = Math.Min(minZ, stopZ[i]);
                    maxX = Math.Max(maxX, stopX[i]);
                    maxZ = Math.Max(maxZ, stopZ[i]);
                }

                int cols = Math.Max(1, (int)((maxX - minX) / cellSize) + 1);
                int rows = Math.Max(1, (int)((maxZ - minZ) / cellSize) + 1);

                var counts = new int[cols * rows];
                var cellOf = new int[stopCount];
                for (int i = 0; i < stopCount; i++)
                {
                    int cx = SuitabilityScoring.ClampInt((int)((stopX[i] - minX) / cellSize), 0, cols - 1);
                    int cz = SuitabilityScoring.ClampInt((int)((stopZ[i] - minZ) / cellSize), 0, rows - 1);
                    cellOf[i] = cx + cz * cols;
                    counts[cellOf[i]]++;
                }

                var offsets = new int[counts.Length + 1];
                int running = 0;
                for (int c = 0; c < counts.Length; c++)
                {
                    offsets[c] = running;
                    running += counts[c];
                }
                offsets[counts.Length] = running;

                var cursor = new int[counts.Length];
                Array.Copy(offsets, cursor, counts.Length);
                var byCell = new int[stopCount];
                for (int i = 0; i < stopCount; i++)
                {
                    byCell[cursor[cellOf[i]]++] = i;
                }

                return new StopGrid(cols, rows, cellOf, offsets, byCell);
            }

            // Every stop after `stop` in index order that is within the radius of it.
            public void CollectNeighboursAfter(
                int stop,
                float[] stopX,
                float[] stopZ,
                float radiusSq,
                List<int> into)
            {
                int ax = m_CellOf[stop] % m_Cols;
                int az = m_CellOf[stop] / m_Cols;
                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = az + dz;
                    if (nz < 0 || nz >= m_Rows)
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = ax + dx;
                        if (nx < 0 || nx >= m_Cols)
                        {
                            continue;
                        }

                        int cell = nx + (nz * m_Cols);
                        for (int k = m_Offsets[cell]; k < m_Offsets[cell + 1]; k++)
                        {
                            int other = m_ByCell[k];
                            if (other <= stop)
                            {
                                continue;
                            }

                            float sx = stopX[stop] - stopX[other];
                            float sz = stopZ[stop] - stopZ[other];
                            if ((sx * sx) + (sz * sz) <= radiusSq)
                            {
                                into.Add(other);
                            }
                        }
                    }
                }
            }
        }

        // Where a rider could change vehicle and carry on. Built from the served stops
        // of the existing network, so a suggested line can be aimed at one.
        //
        // A hub in Cities: Skylines II is several stop entities a few metres apart —
        // the train platform, the metro entrance below it, the bus stand out front —
        // so what is on offer at one PLACE is only visible as a union over the stops
        // within walking distance of it. Reading a single stop's own mode would call
        // the city's biggest interchange a train station and nothing more.
        public static InterchangeMap BuildInterchangeMap(
            float[] stopX, float[] stopZ, int[] stopModes, int stopCount, float radius)
        {
            var reachable = new int[stopCount];
            if (stopCount <= 0 || radius <= 0f)
            {
                return new InterchangeMap(stopX, stopZ, reachable, 0, radius);
            }

            Array.Copy(stopModes, reachable, stopCount);
            if (stopCount > 1)
            {
                StopGrid grid = StopGrid.Build(stopX, stopZ, stopCount, radius);
                float radiusSq = radius * radius;
                var neighbours = new List<int>();
                for (int a = 0; a < stopCount; a++)
                {
                    neighbours.Clear();
                    grid.CollectNeighboursAfter(a, stopX, stopZ, radiusSq, neighbours);

                    // CollectNeighboursAfter reports each pair once, so both directions
                    // have to be unioned here or the lower-indexed stop of every pair
                    // would never learn about the higher one.
                    for (int n = 0; n < neighbours.Count; n++)
                    {
                        int b = neighbours[n];
                        reachable[a] |= stopModes[b];
                        reachable[b] |= stopModes[a];
                    }
                }
            }

            return new InterchangeMap(stopX, stopZ, reachable, stopCount, radius);
        }

        // Walks the shortest itinerary back from `destStop`, reporting how many times
        // the rider boarded and whether `targetLine` was one of them.
        //
        // Board edges are the countable event: one boarding for the first vehicle and
        // one more for each change, so transfers = boardings - 1.
        public static bool Inspect(
            TransitNetwork network,
            DijkstraWorkspace workspace,
            int originStop,
            int destStop,
            int targetLine,
            out int boardings,
            out bool usesTarget,
            out float travelTime)
        {
            boardings = 0;
            usesTarget = false;
            travelTime = 0f;
            int accessSteps = 0;

            if (network?.Graph is null || originStop == destStop)
            {
                return false;
            }

            if (destStop < 0 || destStop >= network.Graph.NodeCount)
            {
                return false;
            }

            if (workspace.Dist[destStop] == float.MaxValue)
            {
                return false;
            }

            travelTime = workspace.Dist[destStop];

            int node = destStop;
            int guard = network.Graph.EdgeCount + 2;
            while (node != originStop && guard-- > 0)
            {
                int edge = workspace.PrevEdge[node];
                if (edge < 0)
                {
                    return false;
                }

                if (network.EdgeKind[edge] == TransitEdgeKind.Access)
                {
                    accessSteps++;
                    if (targetLine >= 0 && network.EdgeLine[edge] == targetLine)
                    {
                        usesTarget = true;
                    }
                }

                node = network.Graph.OtherEnd(edge, node);
            }

            // Two access steps — on and off — per line ridden.
            boardings = accessSteps / 2;
            return node == originStop;
        }

        // Demand credited to one line, and the total demand the network can carry at
        // all. A journey counts in full when it rides the line directly and less for
        // each change it needs, so a direct service still outranks a three-leg
        // itinerary carrying the same people.
        public static float CreditLine(
            TransitNetwork network,
            DijkstraWorkspace workspace,
            int[] originStops,
            int[] destStops,
            float[] weights,
            float[] accessSeconds,
            float[] baselineSeconds,
            int pairCount,
            int targetLine,
            float transferDiscount,
            float maxTravelTime,
            float switchMarginSeconds,
            out float servedWeight)
        {
            servedWeight = 0f;
            float credited = 0f;
            if (network?.Graph is null || pairCount <= 0)
            {
                return 0f;
            }

            int currentOrigin = -1;
            for (int i = 0; i < pairCount; i++)
            {
                int origin = originStops[i];
                int destination = destStops[i];
                if (origin < 0 || destination < 0 || origin == destination)
                {
                    continue;
                }

                // Pairs arrive grouped by origin, so one search serves a run of them.
                if (origin != currentOrigin)
                {
                    currentOrigin = origin;
                    workspace.Run(network.Graph, origin, maxTravelTime);
                }

                if (!Inspect(network, workspace, origin, destination, targetLine,
                        out int boardings, out bool usesTarget, out float travelTime))
                {
                    continue;
                }

                float doorToDoor = travelTime + accessSeconds[i];
                if (doorToDoor > maxTravelTime)
                {
                    continue;
                }

                servedWeight += weights[i];

                if (!usesTarget || boardings <= 0)
                {
                    continue;
                }

                // Nobody changes how they travel for nothing. The margin is what makes
                // it worth the bother rather than a rounding difference.
                if (doorToDoor >= baselineSeconds[i] - switchMarginSeconds)
                {
                    continue;
                }

                int transfers = Math.Max(0, boardings - 1);
                float discount = (float)Math.Pow(transferDiscount, transfers);
                credited += weights[i] * discount;
            }

            return credited;
        }

        // The travel time past which a journey counts as not carried at all, taken from
        // the city's OWN typical transit journey rather than from a fixed hour.
        //
        // The discount asks "how much of this journey does the existing network already
        // absorb", and answering it against an absolute ceiling made the answer depend
        // on the size of the map. A ten-minute trip kept a sixth of its weight whether
        // the city was three kilometres across or thirty, so on a compact, well-served
        // city almost every journey read as fully served — 94% of all travel weight
        // absorbed, leaving the suggestions to be driven by the remainder.
        //
        // Scaled to the median, "slow" means slow for here. This is how the mod already
        // sets its other bars: the nearly-empty threshold is a share of the city's
        // median line usage and the long-wait bar a multiple of its median interval.
        //
        // `travelTimes` is REORDERED in place — it is a scratch buffer, and must not be
        // the array the caller still needs in journey order. `median` is handed back so
        // the caller can report the figure the ceiling came from without running the
        // selection again over a buffer that is now shuffled.
        //
        // Falls back when too few journeys are carried for a median to mean anything,
        // which is exactly the risk this approach carries on a thin network.
        public static float ServedCeiling(
            float[] travelTimes,
            int count,
            float multiple,
            float fallback,
            int minSamples,
            out float median)
        {
            median = 0f;
            if (travelTimes is null || count < minSamples || count <= 0 || multiple <= 0f)
            {
                return fallback;
            }

            median = SuitabilityScoring.SelectKth(travelTimes, count, count / 2);
            if (median <= 0f)
            {
                return fallback;
            }

            // Never past the point where a journey stops being a transit trip at all:
            // the router itself refuses to look further, so a larger ceiling would only
            // mean nothing is ever counted as fully carried.
            float ceiling = median * multiple;
            return ceiling > fallback ? fallback : ceiling;
        }

        // Re-assigns zones to the nearest stop once a PROPOSED line's stops are added
        // to the network.
        //
        // The base mapping is built against the stops that exist today, so a zone with
        // nothing within walking distance has no stop at all and its journeys cannot be
        // routed. Scoring a candidate against that mapping asks "how much of the demand
        // your existing network already reaches would this line carry" — which is zero
        // for exactly the lines worth building, the ones going somewhere unserved.
        //
        // `baseIndex` is where the candidate's stops start in the combined stop array,
        // so the indices written here address the same array the routing graph was
        // built from. Returns how many zones the candidate captured.
        public static int RemapZones(
            float[] zoneX,
            float[] zoneZ,
            int zoneCount,
            int[] zoneStop,
            float[] zoneStopDistSq,
            float[] newStopX,
            float[] newStopZ,
            int newStopCount,
            int baseIndex,
            float maxDistance,
            int[] outZoneStop,
            float[] outZoneStopDistSq)
        {
            if (zoneX is null || zoneZ is null || zoneStop is null || zoneStopDistSq is null
                || newStopX is null || newStopZ is null || outZoneStop is null || outZoneStopDistSq is null)
            {
                return 0;
            }

            float maxSq = maxDistance * maxDistance;
            int captured = 0;
            for (int zone = 0; zone < zoneCount; zone++)
            {
                int best = zoneStop[zone];
                // An unmapped zone has no distance to beat, so start from the radius.
                float bestSq = best >= 0 ? zoneStopDistSq[zone] : maxSq;

                float zx = zoneX[zone];
                float zz = zoneZ[zone];
                for (int i = 0; i < newStopCount; i++)
                {
                    float dx = newStopX[i] - zx;
                    float dz = newStopZ[i] - zz;
                    float distSq = (dx * dx) + (dz * dz);
                    if (distSq < bestSq)
                    {
                        bestSq = distSq;
                        best = baseIndex + i;
                    }
                }

                if (best != zoneStop[zone])
                {
                    captured++;
                }

                outZoneStop[zone] = best;
                // The walk to it, so the caller can charge for it. A stop the zone only
                // just reaches is not the same offer as one on its doorstep.
                outZoneStopDistSq[zone] = best >= 0 ? bestSq : 0f;
            }

            return captured;
        }

    }
}
