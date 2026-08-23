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

        public bool IsStopNode(int node) => node >= 0 && node < StopCount;
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
            float walkRadiusSq = walkRadius * walkRadius;
            for (int a = 0; a < stopCount; a++)
            {
                for (int b = a + 1; b < stopCount; b++)
                {
                    float dx = stopX[a] - stopX[b];
                    float dz = stopZ[a] - stopZ[b];
                    float distSq = dx * dx + dz * dz;
                    if (distSq > walkRadiusSq)
                    {
                        continue;
                    }

                    float distance = (float)Math.Sqrt(distSq);
                    AddEdge(a, b, distance / WalkSpeed, TransitEdgeKind.Walk, -1);
                }
            }

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
            int pairCount,
            int targetLine,
            float transferDiscount,
            float maxTravelTime,
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

                if (travelTime > maxTravelTime)
                {
                    continue;
                }

                servedWeight += weights[i];

                if (!usesTarget || boardings <= 0)
                {
                    continue;
                }

                int transfers = Math.Max(0, boardings - 1);
                float discount = (float)Math.Pow(transferDiscount, transfers);
                credited += weights[i] * discount;
            }

            return credited;
        }
    }
}
