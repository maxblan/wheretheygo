using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationSuitabilityOverlay
{
    // F4 — alignments: corridors grown along street flow, direct traces on the
    // lattices, the interchange aiming and bending, re-tracing on streets and the
    // shape and duplicate gates. Stop placement is the F5 half of this class.
    internal static partial class SuitabilityRoutes
    {

        // Grows corridors on ONE network and appends them as candidates. Each
        // network carries its own mode family, because the alignment a mode can use
        // is what decides where its routes may run: buses and trams are stuck with
        // streets, metro tunnels and trains lay their own, ferries need water.
        public static void BuildForNetwork(
            AlignmentNetwork network,
            RouteObjective objective,
            int maxRoutes,
            float minFlowFraction,
            float maxRouteLength,
            float[]? nodeDemand,
            float demandFloor,
            ModePreset? forcedMode,
            List<SuggestedRoute> output,
            StopContext stops,
            out int grown,
            out int tooShort,
            out int atInterchange)
        {
            grown = 0;
            tooShort = 0;
            atInterchange = 0;
            int wandered = 0;
            if (network?.Graph is null || network.EdgeFlow is null || network.EdgeCount == 0)
            {
                return;
            }

            CompactGraph graph = network.Graph;
            // Work on a copy: peeling is destructive and the assigned flow is reused
            // by the demand layer.
            var flow = (float[])network.EdgeFlow.Clone();

            var used = new bool[graph.EdgeCount];
            int unstoppable = MarkUnstoppable(network, used);
            var novelty = new float[graph.NodeCount];
            for (int n = 0; n < graph.NodeCount; n++)
            {
                novelty[n] = 1f;
            }

            // Node positions come along so growth knows which way it is heading: without
            // them a corridor staircases across the map, which is what a lattice route
            // did before there was any preference for carrying straight on.
            var growthNetwork = new CorridorNetwork(
                graph, flow, used, novelty, nodeDemand, network.NodePositionsX, network.NodePositionsZ);

            float meanFlow = SuitabilityGraphMath.MeanPositiveFlow(flow, graph.EdgeCount);
            if (meanFlow <= 0f)
            {
                return;
            }

            float noveltyWeight = SuitabilityGraphMath.NoveltyWeight(objective, meanFlow);
            // The objective has to reach SEEDING, not just the extension tie-break:
            // novelty is uniform while the first corridor grows, so a weight that only
            // tips extensions leaves every objective producing the same suggestions.
            float seedNoveltyBias = SuitabilityGraphMath.SeedNoveltyBias(objective);
            float flowFloor = meanFlow * minFlowFraction;
            var corridor = new Corridor();
            var blocked = default(CorridorBlocks);
            int hitMaxLength = 0;
            float lengthSum = 0f;
            int lengthCount = 0;

            // Grow more corridors than asked for: many are discarded for being too
            // short or too light, and stopping at maxRoutes attempts left the merged
            // set far thinner than the requested count.
            //
            // Four per requested route, not two. On a real city the road network — the
            // one every surviving suggestion came from — hit the old budget every
            // refresh and said so, while the lattices never came close to theirs.
            //
            // The budget is counted PER NETWORK, not against the shared output list.
            // Testing output.Count made one shared allowance that whichever network ran
            // first consumed: roads run first, and once their corridors grew long
            // enough to survive the length floor they filled all ten slots, so train,
            // metro and ferry growth was skipped entirely — not one attempt, not one
            // rejected extension, no trains ever suggested however much demand there
            // was for one.
            int attempts = maxRoutes * 8;
            int added = 0;
            int budget = maxRoutes * 4;
            for (int r = 0; r < attempts && added < budget; r++)
            {
                if (!SuitabilityGraphMath.GrowCorridor(in growthNetwork, noveltyWeight,
                        flowFloor, maxRouteLength, corridor, demandFloor, seedNoveltyBias))
                {
                    break;
                }

                blocked.m_Used += corridor.Blocks.m_Used;
                blocked.m_Flow += corridor.Blocks.m_Flow;
                blocked.m_Visited += corridor.Blocks.m_Visited;
                blocked.m_Length += corridor.Blocks.m_Length;
                blocked.m_Demand += corridor.Blocks.m_Demand;
                if (corridor.Blocks.m_HitMaxLength)
                {
                    hitMaxLength++;
                }

                lengthSum += corridor.Length;
                lengthCount++;

                // A single edge is not a line.
                if (corridor.Edges.Count < 2)
                {
                    SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, 1f);
                    continue;
                }

                grown++;
                SuggestedRoute? candidate = BuildCandidate(
                    network, corridor, forcedMode, stops,
                    out bool shorterThanAnyMode, out int aimedAtInterchange);
                if (candidate is not null)
                {
                    OfferGrown(candidate, output);
                    added++;
                    // Counted only once the corridor becomes a candidate, so the figure
                    // cannot exceed the number of candidates and read as a bug in the
                    // aiming rather than as corridors being discarded afterwards.
                    atInterchange += aimedAtInterchange;
                }
                else if (shorterThanAnyMode)
                {
                    tooShort++;
                }
                else if (corridor.Length > 0f)
                {
                    wandered++;
                }

                SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, Assumptions.CaptureFraction);
                SuitabilityGraphMath.DecayNovelty(graph, corridor, novelty, Assumptions.NoveltyHops, Assumptions.NoveltyFactor);
            }

            // Why the corridors on this network came out the length they did. A route
            // that ran out of demand and one hemmed in by corridors grown before it
            // both arrive as a small number of metres, and the two call for opposite
            // responses — the first means there is nothing there, the second means the
            // growth order is eating its own network.
            DeferredLog.Info(
                $"Corridor growth on {network.Network}: {(lengthCount).ToString(CultureInfo.InvariantCulture)} grown, " +
                $"mean length {((lengthCount > 0 ? lengthSum / lengthCount : 0f)).ToString("F0", CultureInfo.InvariantCulture)}m, " +
                $"{(hitMaxLength).ToString(CultureInfo.InvariantCulture)} reached the {(maxRouteLength).ToString("F0", CultureInfo.InvariantCulture)}m limit; " +
                $"meanEdgeFlow={(meanFlow).ToString("F0", CultureInfo.InvariantCulture)}, flowFloor={(flowFloor).ToString("F0", CultureInfo.InvariantCulture)}, " +
                $"{(unstoppable).ToString(CultureInfo.InvariantCulture)} edge(s) excluded as unable to host a stop, " +
                $"{(wandered).ToString(CultureInfo.InvariantCulture)} discarded for coming back on themselves " +
                $"(ends must be {(Assumptions.MinDirectness * 100f).ToString("F0", CultureInfo.InvariantCulture)}% of the length apart); " +
                $"extensions refused — alreadyUsed={(blocked.m_Used).ToString(CultureInfo.InvariantCulture)}, " +
                $"belowFlowFloor={(blocked.m_Flow).ToString(CultureInfo.InvariantCulture)}, " +
                $"wouldRevisit={(blocked.m_Visited).ToString(CultureInfo.InvariantCulture)}, " +
                $"pastMaxLength={(blocked.m_Length).ToString(CultureInfo.InvariantCulture)}, " +
                $"noDemandBeside={(blocked.m_Demand).ToString(CultureInfo.InvariantCulture)}" +
                $"{(added >= budget ? $"; STOPPED at this network's budget of {budget.ToString(CultureInfo.InvariantCulture)} candidates — there may be more worth having" : string.Empty)}");
        }

        // Candidates for a network whose alignment is FREE — metro, train, ferry.
        //
        // These are not grown along flow, and deliberately so. A lattice is a uniform
        // grid, so the shortest path between two zones is degenerate: hundreds of
        // staircases cost the same and which one Dijkstra picks falls out of the order
        // its edges were added. The flow those paths accumulate is an artifact of the
        // grid rather than a travel pattern, and following it produced corridors that
        // wandered across the city and averaged 358 m — three tries at biasing the
        // growth made them wander less without making them right.
        //
        // A metro is not a corridor that emerges from a flow field. It is a decision to
        // connect two places. So take the heaviest demand the network still does not
        // serve, and go straight there: the alignment is free, which is the whole reason
        // this network exists.
        //
        // Roads keep growth. Street flow is real, and a bus has to follow the streets.
        public static void BuildDirectForNetwork(
            AlignmentNetwork network,
            List<ZoneFlow> flows,
            int[] zoneNodes,
            int maxRoutes,
            float maxRouteLength,
            ModePreset forcedMode,
            List<SuggestedRoute> output,
            StopContext stops,
            out int considered,
            out int tooShort,
            out int atInterchange)
        {
            considered = 0;
            tooShort = 0;
            atInterchange = 0;
            int bent = 0;
            InterchangeMap hubs = stops.Hubs;
            if (network?.Graph is null || zoneNodes is null || flows.Count == 0)
            {
                return;
            }

            // Heaviest unserved demand first. The weights have already had the existing
            // network's share taken out of them, so this is what is going begging.
            var order = new List<int>(flows.Count);
            for (int i = 0; i < flows.Count; i++)
            {
                order.Add(i);
            }

            order.Sort((left, right) => flows[right].m_Weight.CompareTo(flows[left].m_Weight));

            var scratch = new List<int>();
            var takenFrom = new List<float2Like>();
            var takenTo = new List<float2Like>();

            // Counted PER NETWORK, in a local, not against the shared output list —
            // exactly as BuildForNetwork does and says. Testing output.Count made one
            // shared allowance that whichever network ran first consumed: BuildRoutes
            // calls road, train, metro then water into the same list, train filled the
            // budget, and metro and ferry found it spent before their first iteration.
            // The log said so on every cycle — `metro pairs tried=0 ferry pairs
            // tried=0` beside a populated water lattice and 38 cross-water journeys —
            // and no metro or ferry could be suggested however much demand there was.
            int budget = maxRoutes * 4;
            int added = 0;

            for (int slot = 0; slot < order.Count && added < budget; slot++)
            {
                ZoneFlow flow = flows[order[slot]];
                if (flow.m_Weight <= 0f)
                {
                    break;
                }

                int from = zoneNodes[flow.m_Origin];
                int to = zoneNodes[flow.m_Destination];
                if (from < 0 || to < 0 || from == to)
                {
                    continue;
                }

                var fromPoint = new float2Like(network.NodePositionsX[from], network.NodePositionsZ[from]);
                var toPoint = new float2Like(network.NodePositionsX[to], network.NodePositionsZ[to]);

                // Aim each end at an interchange if one is in reach. A terminus is
                // where every rider must either finish or change vehicle, so it is the
                // most valuable point on the line to put within walking distance of
                // another mode — and CreditLine already pays a candidate for the
                // journeys a transfer unlocks, it just had no way of being offered one.
                int fromHub = SnapToInterchange(network, hubs, forcedMode, from, ref fromPoint);
                int toHub = SnapToInterchange(network, hubs, forcedMode, to, ref toPoint);
                // Counted only once the pair actually becomes a candidate. Counting at
                // the snap made the figure larger than the number of pairs tried, which
                // reads as a bug in the snapping rather than as pairs being discarded.
                int aimedAtInterchange = (fromHub != from ? 1 : 0) + (toHub != to ? 1 : 0);
                from = fromHub;
                to = toHub;

                // Both ends can snap to the same station when the two zones sit either
                // side of one, which is a stop rather than a line.
                if (from == to || AlreadyConnecting(takenFrom, takenTo, fromPoint, toPoint))
                {
                    continue;
                }

                considered++;
                if (!network.TracePath(from, to, maxRouteLength, scratch))
                {
                    continue;
                }

                // The direct alignment, and — where an interchange is worth bending
                // towards — the same journey traced through that hub as a SECOND
                // candidate (register A4.7). Whether the detour pays is not a question
                // a length ratio can answer: a metro bent 700 m out to a ferry pier and
                // back stayed inside the ratio and was drawn as a spur into the fields.
                // Both go to the set selection, which routes every journey over each
                // and keeps the one that saves more time; they cannot both be chosen,
                // because the second duplicates the first.
                SuggestedRoute? direct = TraceCandidate(network, forcedMode, scratch, bentThroughHub: false, stops, ref tooShort);
                var bentPath = new List<int>(scratch);
                SuggestedRoute? viaHub = BendThroughInterchange(network, hubs, forcedMode, from, to, maxRouteLength, bentPath)
                    ? TraceCandidate(network, forcedMode, bentPath, bentThroughHub: true, stops, ref tooShort)
                    : null;
                if (viaHub is not null)
                {
                    bent++;
                }

                if (direct is null && viaHub is null)
                {
                    continue;
                }

                takenFrom.Add(fromPoint);
                takenTo.Add(toPoint);
                OfferVariants(direct, viaHub, output);
                added++;
                atInterchange += aimedAtInterchange;
            }

            if (bent > 0)
            {
                DeferredLog.Info(
                    $"  {forcedMode} alignments also offered bent through an interchange: {(bent).ToString(CultureInfo.InvariantCulture)} " +
                    $"of {(considered).ToString(CultureInfo.InvariantCulture)} traced, within " +
                    $"{(Assumptions.MaxViaDetour).ToString("F2", CultureInfo.InvariantCulture)}x the direct alignment; the set selection decides between the two");
            }
        }

        // A grown road corridor is an alignment of its own: one group, one variant so far
        // (the mode ladder may add the next mode up later).
        private static void OfferGrown(SuggestedRoute candidate, List<SuggestedRoute> output)
        {
            candidate.Group = output.Count;
            output.Add(candidate);
        }

        // Both alignments of one journey pair join the pool as one group: alternatives
        // the set selection chooses between, never both.
        private static void OfferVariants(SuggestedRoute? direct, SuggestedRoute? viaHub, List<SuggestedRoute> output)
        {
            int group = output.Count;
            if (direct is not null)
            {
                direct.Group = group;
                output.Add(direct);
            }

            if (viaHub is not null)
            {
                viaHub.Group = group;
                output.Add(viaHub);
            }
        }

        // One lattice alignment made into a candidate: materialised, straightened and
        // given its stops. Null when it cannot hold a line's worth of stops.
        private static SuggestedRoute? TraceCandidate(
            AlignmentNetwork network,
            ModePreset mode,
            List<int> nodes,
            bool bentThroughHub,
            StopContext stops,
            ref int tooShort)
        {
            var route = new SuggestedRoute
            {
                Network = network.Network,
                Mode = mode,
                BentThroughHub = bentThroughHub,
                Source = network,
            };
            route.Nodes.AddRange(nodes);
            network.MaterialisePath(nodes, route.Path);

            // A shortest path on a uniform grid is a minimal staircase; straightening
            // it leaves the near-straight alignment a tunnel or a crossing actually
            // takes.
            Simplify(route.Path, Assumptions.SimplifyTolerance);
            route.Length = PathLength(route.Path);
            route.CapturedFlow = network.FlowAlong(nodes);

            PlaceStops(route, mode, stops);
            if (route.Stops.Count < Assumptions.MinStops)
            {
                tooShort++;
                return null;
            }

            return route;
        }

        // Bends an alignment through an interchange it passes near, by re-tracing it as
        // two legs through that hub.
        //
        // The last piece of "take transport hubs into account". Termini have been aimed
        // at a hub since SnapToInterchange, and stops call at any station within 150 m
        // of where the line already runs — but a metro passing two kilometres from the
        // train station was never diverted to reach it, because an alignment was traced
        // end to end and only its STOPS were ever adjusted. A line that misses the
        // interchange misses the network.
        //
        // Only the lattices. A road corridor's shape is a measurement — grown along
        // real street flow and peeled — and re-tracing its middle would throw that
        // measurement away to buy a transfer. Its ends are aimed at a hub instead.
        //
        // Returns true when `path` was replaced with the bent alignment.
        private static bool BendThroughInterchange(
            AlignmentNetwork network,
            InterchangeMap hubs,
            ModePreset mode,
            int from,
            int to,
            float maxRouteLength,
            List<int> path)
        {
            if (hubs.Count == 0 || path.Count < 2)
            {
                return false;
            }

            // The best hub anywhere along the line: most other modes first, and among
            // equals the one that asks for the least sideways travel.
            float bestX = 0f;
            float bestZ = 0f;
            int bestModes = 0;
            float bestOffsetSq = float.MaxValue;
            bool found = false;

            for (int i = 0; i < path.Count; i += Assumptions.ViaSampleStride)
            {
                int node = path[i];
                if (node < 0 || node >= network.NodePositionsX.Length)
                {
                    continue;
                }

                float x = network.NodePositionsX[node];
                float z = network.NodePositionsZ[node];
                if (!hubs.TryFindNear(mode, x, z, Assumptions.ViaReachMetres, out float hubX, out float hubZ, out int modes)
                    || modes <= 0)
                {
                    continue;
                }

                float dx = hubX - x;
                float dz = hubZ - z;
                float offsetSq = (dx * dx) + (dz * dz);
                if (found && (modes < bestModes || (modes == bestModes && offsetSq >= bestOffsetSq)))
                {
                    continue;
                }

                found = true;
                bestModes = modes;
                bestOffsetSq = offsetSq;
                bestX = hubX;
                bestZ = hubZ;
            }

            if (!found)
            {
                return false;
            }

            int via = network.NearestNode(new float2Like(bestX, bestZ), Assumptions.ViaSnapMetres);
            if (via < 0 || via == from || via == to || path.Contains(via))
            {
                // Already on the line, or no node near enough to stand for the hub.
                return false;
            }

            float direct = NodePathLength(network, path);
            var head = new List<int>();
            var tail = new List<int>();
            if (!network.TracePath(from, via, maxRouteLength, head)
                || !network.TracePath(via, to, maxRouteLength, tail))
            {
                return false;
            }

            float bentLength = NodePathLength(network, head) + NodePathLength(network, tail);
            if (!SuitabilityGraphMath.IsDetourWorthwhile(direct, bentLength, maxRouteLength))
            {
                return false;
            }

            // Two shortest paths sharing anything but the via point means the line
            // doubles back through it — a hub reached by going out and coming home is
            // not on the way to anywhere.
            for (int i = 0; i < head.Count - 1; i++)
            {
                if (tail.Contains(head[i]))
                {
                    return false;
                }
            }

            // And the bend must not be a hairpin. Sharing no node is not the same as
            // not turning back: a hub square off to one side gives two legs that meet
            // at a sharp V, which the length bound alone permits on a long enough line
            // — 1.25x the direct distance buys a wide detour or a narrow spike equally.
            // The same directness test that catches a grown corridor curling into a
            // ring catches this.
            float endToEnd = 0f;
            if (head.Count > 0 && tail.Count > 0)
            {
                int first = head[0];
                int last = tail[tail.Count - 1];
                if (first >= 0 && last >= 0
                    && first < network.NodePositionsX.Length && last < network.NodePositionsX.Length)
                {
                    float ex = network.NodePositionsX[first] - network.NodePositionsX[last];
                    float ez = network.NodePositionsZ[first] - network.NodePositionsZ[last];
                    endToEnd = (float)Math.Sqrt((ex * ex) + (ez * ez));
                }
            }

            if (!SuitabilityGraphMath.IsDirectEnough(endToEnd, bentLength))
            {
                return false;
            }

            path.Clear();
            path.AddRange(head);
            // head ends at the via, which tail begins with.
            for (int i = 1; i < tail.Count; i++)
            {
                path.Add(tail[i]);
            }

            return true;
        }

        // Geometric length of a node path. NOT the sum of edge costs: a lattice scales
        // its costs to express "prefer existing track", so a cost total is a preference
        // score rather than a distance, and the detour bound is about distance.
        private static float NodePathLength(AlignmentNetwork network, List<int> nodes)
        {
            float length = 0f;
            for (int i = 1; i < nodes.Count; i++)
            {
                int a = nodes[i - 1];
                int b = nodes[i];
                if (a < 0 || b < 0 || a >= network.NodePositionsX.Length || b >= network.NodePositionsX.Length)
                {
                    continue;
                }

                float dx = network.NodePositionsX[a] - network.NodePositionsX[b];
                float dz = network.NodePositionsZ[a] - network.NodePositionsZ[b];
                length += (float)Math.Sqrt((dx * dx) + (dz * dz));
            }

            return length;
        }

        // Moves a terminus onto the network node nearest a usable interchange, when one
        // is within a walk of it. The search radius is the transfer walk radius, so the
        // zone the line was drawn for is still served from the moved end — this buys a
        // change of vehicle without giving up the demand that justified the line.
        private static int SnapToInterchange(
            AlignmentNetwork network, InterchangeMap hubs, ModePreset mode, int node, ref float2Like point)
        {
            if (hubs.Count == 0
                || !hubs.TryFindNear(mode, point.x, point.y, hubs.Radius, out float hubX, out float hubZ, out _))
            {
                return node;
            }

            // The lattice has a 128 m pitch, so the node nearest a station is not the
            // station; anything further off than a transfer walk is not an interchange.
            int snapped = network.NearestNode(new float2Like(hubX, hubZ), hubs.Radius);
            if (snapped < 0)
            {
                return node;
            }

            point = new float2Like(network.NodePositionsX[snapped], network.NodePositionsZ[snapped]);
            return snapped;
        }

        // Whether a line already proposed on this network runs between the same two
        // places. Zones are 256 m across, so anything inside that is the same pair.
        private static bool AlreadyConnecting(List<float2Like> froms, List<float2Like> tos, float2Like from, float2Like to)
        {
            float sameSq = Assumptions.ZoneSize * Assumptions.ZoneSize;
            for (int i = 0; i < froms.Count; i++)
            {
                bool sameWay = float2Like.DistanceSq(froms[i], from) <= sameSq && float2Like.DistanceSq(tos[i], to) <= sameSq;
                bool otherWay = float2Like.DistanceSq(froms[i], to) <= sameSq && float2Like.DistanceSq(tos[i], from) <= sameSq;
                if (sameWay || otherWay)
                {
                    return true;
                }
            }

            return false;
        }

        private static float PathLength(List<float2Like> path)
        {
            float length = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                length += float2Like.Distance(path[i - 1], path[i]);
            }

            return length;
        }

        // Edges a line could not call at start out spent. They stay in the graph so
        // journeys still route over them and their flow still counts towards the
        // network's mean, but no corridor may be seeded on or extended along one — a
        // bus route down a motorway serves nobody. Returns how many were excluded.
        private static int MarkUnstoppable(AlignmentNetwork network, bool[] used)
        {
            bool[]? noStopEdges = network.EdgeCannotHostStops;
            if (noStopEdges is null)
            {
                return 0;
            }

            int unstoppable = 0;
            for (int e = 0; e < used.Length && e < noStopEdges.Length; e++)
            {
                if (noStopEdges[e])
                {
                    used[e] = true;
                    unstoppable++;
                }
            }

            return unstoppable;
        }

        // Straight-line distance between a polyline's two ends.
        private static float EndToEnd(List<float2Like> path)
        {
            return path.Count < 2 ? 0f : float2Like.Distance(path[0], path[path.Count - 1]);
        }

        // Turns one grown corridor into a candidate line: its drawn alignment, the mode
        // its flow suggests, and its stops.
        //
        // Returns null when the corridor cannot become a line, with
        // `shorterThanAnyMode` telling the two rejections apart for the diagnostics.
        // Length is judged against the SHORTEST mode this alignment can host, not the
        // mode first guessed from flow: a busy 800 m street classified Tram failed the
        // 1200 m tram floor and was thrown away when it was a perfectly good bus
        // route, which is why road candidates kept coming out as "grown=8 tooShort=8".
        private static SuggestedRoute? BuildCandidate(
            AlignmentNetwork network,
            Corridor corridor,
            ModePreset? forcedMode,
            StopContext stops,
            out bool shorterThanAnyMode,
            out int aimedAtInterchange)
        {
            shorterThanAnyMode = false;
            // The smallest mode the network carries; the riders decide the real one
            // once the candidate has been weighed (TransitModes.ChooseMode).
            ModePreset mode = forcedMode ?? TransitModes.ModesFor(network.Network)[0];
            var route = new SuggestedRoute
            {
                CapturedFlow = corridor.CapturedFlow,
                Length = corridor.Length,
                Network = network.Network,
                Mode = mode,
                Source = network,
            };

            int extended = AimEndsAtInterchange(network, stops.Hubs, route.Mode, corridor.Nodes);
            aimedAtInterchange = extended;
            route.Nodes.AddRange(corridor.Nodes);

            network.MaterialisePath(corridor.Nodes, route.Path);

            if (extended > 0)
            {
                route.Length = PathLength(route.Path);
            }

            if (route.Mode is not (ModePreset.Bus or ModePreset.Tram))
            {
                Simplify(route.Path, Assumptions.SimplifyTolerance);
            }

            if (!SuitabilityGraphMath.IsDirectEnough(EndToEnd(route.Path), route.Length))
            {
                return null;
            }

            PlaceStops(route, route.Mode, stops);
            if (route.Stops.Count < Assumptions.MinStops)
            {
                shorterThanAnyMode = true;
                return null;
            }

            return route;
        }

        // Extends a grown corridor's ends onto the road node nearest a usable
        // interchange, when one is within reach. Returns how many ends moved.
        //
        // Extending the NODE LIST rather than nudging the drawn polyline: a bus has to
        // follow the streets, so the added stretch must be real road edges. That is
        // also why this cannot reuse SnapToInterchange, which relocates a lattice
        // terminus by moving a point — on a lattice any two nodes are joined, on the
        // street network they are not.
        private static int AimEndsAtInterchange(
            AlignmentNetwork network, InterchangeMap hubs, ModePreset mode, List<int> nodes)
        {
            if (hubs.Count == 0 || nodes.Count < 2)
            {
                return 0;
            }

            var trace = new List<int>();
            int moved = 0;

            // Far end first. Extending the near end shifts every later index, and
            // taking them in this order keeps nodes[0] meaning what it did.
            if (ExtendEnd(network, hubs, mode, nodes, nodes[nodes.Count - 1], trace, atStart: false))
            {
                moved++;
            }

            if (ExtendEnd(network, hubs, mode, nodes, nodes[0], trace, atStart: true))
            {
                moved++;
            }

            return moved;
        }

        private static bool ExtendEnd(
            AlignmentNetwork network,
            InterchangeMap hubs,
            ModePreset mode,
            List<int> nodes,
            int terminus,
            List<int> trace,
            bool atStart)
        {
            if (terminus < 0 || terminus >= network.NodePositionsX.Length)
            {
                return false;
            }

            float x = network.NodePositionsX[terminus];
            float z = network.NodePositionsZ[terminus];
            if (!hubs.TryFindNear(mode, x, z, hubs.Radius, out float hubX, out float hubZ, out _))
            {
                return false;
            }

            int target = network.NearestNode(new float2Like(hubX, hubZ), hubs.Radius);
            if (target < 0 || target == terminus || nodes.Contains(target))
            {
                return false;
            }

            if (!network.TracePath(terminus, target, Assumptions.InterchangeReachMetres, trace) || trace.Count < 2)
            {
                return false;
            }

            // A corridor that walks back over itself to reach a hub is a worse line
            // than one that stops short of it.
            for (int i = 1; i < trace.Count; i++)
            {
                if (nodes.Contains(trace[i]))
                {
                    return false;
                }
            }

            if (atStart)
            {
                // trace runs terminus -> target, so it goes on the front reversed and
                // without its first entry, which is the terminus already in `nodes`.
                for (int i = 1; i < trace.Count; i++)
                {
                    nodes.Insert(0, trace[i]);
                }
            }
            else
            {
                for (int i = 1; i < trace.Count; i++)
                {
                    nodes.Add(trace[i]);
                }
            }

            return true;
        }

        // Fleet needed to hold the mode's assumed headway around the whole line, from
        // its length at cruise speed — the estimate for alignments without streets.
        public static int EstimateVehicles(ModePreset mode, float lengthMetres, int stops, float headwaySeconds, float delayPerStopSeconds)
        {
            float speed = Assumptions.CruiseSpeedFor(mode);
            // Out and back.
            return EstimateVehiclesFromRoundTrip((lengthMetres * 2f) / Math.Max(1f, speed), stops, headwaySeconds, delayPerStopSeconds);
        }

        // The same fleet arithmetic from a measured out-and-back driving time, dwelling
        // at every stop in each direction.
        public static int EstimateVehiclesFromRoundTrip(float drivingSeconds, int stops, float headwaySeconds, float delayPerStopSeconds)
        {
            float roundTrip = drivingSeconds + (stops * 2 * delayPerStopSeconds);
            // ToEven is what Unity's math.round (Math.Round without an argument) did here.
            return Math.Max(1, (int)Math.Round(roundTrip / Math.Max(Assumptions.MinPlannedHeadwaySeconds, headwaySeconds), MidpointRounding.ToEven));
        }

        // Re-traces a corridor on the ROAD network between the same endpoints.
        //
        // Needed because falling back across mode families changes what the alignment
        // may be: a metro corridor is a tunnel path, and a bus cannot drive it. So the
        // route is genuinely recalculated along streets rather than merely relabelled.
        // The same journey traced along streets, as the smallest road mode: what a
        // lattice alignment falls back to when its riders would not fill a rail vehicle.
        public static SuggestedRoute? RetraceOnRoad(
            AlignmentNetwork roads,
            float2Like from,
            float2Like to,
            StopContext stops,
            List<int> scratch)
        {
            if (roads?.Graph is null)
            {
                return null;
            }

            int fromNode = roads.NearestNode(from, Assumptions.RetraceSnapMetres);
            int toNode = roads.NearestNode(to, Assumptions.RetraceSnapMetres);
            if (fromNode < 0 || toNode < 0 || !roads.TracePath(fromNode, toNode, Assumptions.RetraceMaxPathMetres, scratch))
            {
                return null;
            }

            var route = new SuggestedRoute { Network = RouteNetwork.Road, Mode = TransitModes.ModesFor(RouteNetwork.Road)[0], Source = roads };
            route.Nodes.AddRange(scratch);
            roads.MaterialisePath(scratch, route.Path);
            route.Length = PathLength(route.Path);
            route.CapturedFlow = roads.FlowAlong(scratch);
            PlaceStops(route, route.Mode, stops);
            return route.Stops.Count >= Assumptions.MinStops ? route : null;
        }

        // A line's shape after its stops are placed: at least MinStops calls, and an
        // end-to-end ride within the mode's limit (register A4.6/A6.1). `rideSeconds`
        // is the caller's — the directed driving time where streets exist, cruise
        // speed otherwise — so the same figure the rider is quoted is the one judged.
        public static bool KeepsItsShape(SuggestedRoute route, float rideSeconds)
        {
            return route is not null
                && route.Stops.Count >= Assumptions.MinStops
                && rideSeconds <= Assumptions.MaxRideSecondsFor(route.Mode);
        }

        // True when this candidate essentially retraces a line that already exists.
        // Without this a suggestion survived being built: the player laid the tram the
        // mod asked for and the same suggestion kept being offered.
        public static bool DuplicatesExisting(SuggestedRoute route, List<ExistingLine> existing, List<float2Like> stopPositions, float matchRadius)
        {
            // Below three stops a candidate trivially matches most of something on a
            // dense network and was thrown away as "already built" when it was not.
            if (existing is null || route.Stops.Count < 3)
            {
                return false;
            }

            float radiusSq = matchRadius * matchRadius;
            for (int l = 0; l < existing.Count; l++)
            {
                ExistingLine line = existing[l];
                int matched = 0;
                for (int s = 0; s < route.Stops.Count; s++)
                {
                    for (int i = 0; i < line.m_StopIndices.Count; i++)
                    {
                        int index = line.m_StopIndices[i];
                        if (index < 0 || index >= stopPositions.Count)
                        {
                            continue;
                        }

                        if (float2Like.DistanceSq(route.Stops[s], stopPositions[index]) <= radiusSq)
                        {
                            matched++;
                            break;
                        }
                    }
                }

                // Most of the suggestion already has service on the same alignment.
                if (matched >= (int)Math.Ceiling(route.Stops.Count * Assumptions.DuplicateStopShare))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Simplify(List<float2Like> path, float tolerance)
        {
            SuitabilityGraphMath.SimplifyPolyline(path, tolerance);
        }
    }
}
