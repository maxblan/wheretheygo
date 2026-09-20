using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    internal enum TransitEdgeKind
    {
        Walk = 0,
        // Stepping between the street and a vehicle. Undirected, so one line use
        // traverses it twice; see the halved cost in Build.
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

    // One line as the router needs it: the stops it calls at in travel order - the
    // first one repeated at the end where the loop closes (Lines.ToTransitLines), so a
    // rider can stay aboard round the whole loop - how long a rider waits for it, and
    // how fast it covers ground.
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

    internal static class TransitGraph
    {

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
            var forwardOnly = new List<bool>();

            int nextNode = stopCount;

            void AddEdge(int a, int b, float cost, TransitEdgeKind kind, int line)
            {
                edgeA.Add(a);
                edgeB.Add(b);
                edgeCost.Add(Math.Max(Assumptions.MinEdgeSeconds, cost));
                kinds.Add(kind);
                edgeLines.Add(line);
                // Riding is one-way: a line is a loop driven in one direction, and the
                // game's own transit edge carries EdgeFlags.Forward and nothing else.
                // Walking between stops and stepping on or off a vehicle go both ways.
                forwardOnly.Add(kind == TransitEdgeKind.Ride);
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
                    // be traversable backwards as a FREE boarding, which made
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
                Graph = CompactGraph.Build(nextNode, edgeA.ToArray(), edgeB.ToArray(), edgeCost.ToArray(), edgeA.Count, forwardOnly.ToArray()),
                EdgeKind = kinds.ToArray(),
                EdgeLine = edgeLines.ToArray(),
            };
        }

        // Joins every pair of stops within walking distance.
        //
        // Bucketed on a grid of the walk radius rather than swept exhaustively: an
        // all-pairs sweep over a city's five hundred stops is a hundred and twenty-five
        // thousand distance tests for every network built.
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
                    addEdge(a, b, distance / Assumptions.WalkSpeed, TransitEdgeKind.Walk, -1);
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

            // Count, prefix-sum, fill: the same shape the scoring job's buckets use,
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
                    int cx = Math.Min(cols - 1, Math.Max(0, (int)((stopX[i] - minX) / cellSize)));
                    int cz = Math.Min(rows - 1, Math.Max(0, (int)((stopZ[i] - minZ) / cellSize)));
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

            // Two access steps per line ridden, one on and one off.
            boardings = accessSteps / 2;
            return node == originStop;
        }

        // The k-th smallest of `values[0..length)`, by quickselect. The array is
        // REORDERED in place, which is why the caller passes a scratch copy. Used for
        // the median carried journey.
        public static float SelectKth(float[] values, int length, int k)
        {
            if (values is null || length <= 0)
            {
                return 0f;
            }

            k = Math.Min(length - 1, Math.Max(0, k));
            int lo = 0;
            int hi = length - 1;
            while (lo < hi)
            {
                float pivot = values[(lo + hi) >> 1];
                int i = lo;
                int j = hi;
                while (i <= j)
                {
                    while (values[i] < pivot)
                    {
                        i++;
                    }

                    while (values[j] > pivot)
                    {
                        j--;
                    }

                    if (i <= j)
                    {
                        float tmp = values[i];
                        values[i] = values[j];
                        values[j] = tmp;
                        i++;
                        j--;
                    }
                }

                if (k <= j)
                {
                    hi = j;
                }
                else if (k >= i)
                {
                    lo = i;
                }
                else
                {
                    return values[k];
                }
            }

            return values[k];
        }

        // The travel time past which a journey counts as not carried at all, taken from
        // the city's OWN typical transit journey rather than from a fixed hour.
        //
        // The discount asks "how much of this journey does the existing network already
        // absorb", and answering it against an absolute ceiling made the answer depend
        // on the size of the map. A ten-minute trip kept a sixth of its weight whether
        // the city was three kilometres across or thirty, so on a compact, well-served
        // city almost every journey read as fully served, at 94% of all travel weight
        // absorbed, leaving the suggestions to be driven by the remainder.
        //
        // Scaled to the median, "slow" means slow for here. This is how the mod already
        // sets its other bars: the nearly-empty threshold is a share of the city's
        // median line usage and the long-wait bar a multiple of its median interval.
        //
        // `travelTimes` is REORDERED in place. It is a scratch buffer, and must not be
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

            median = SelectKth(travelTimes, count, count / 2);
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
    }
}
