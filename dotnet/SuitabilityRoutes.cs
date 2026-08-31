using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // A suggested transit line: the polyline it runs along, where its stops go, and
    // which mode it should be.
    internal sealed class SuggestedRoute
    {
        public readonly List<float2> Path = new List<float2>();
        public readonly List<float2> Stops = new List<float2>();
        public ModePreset Mode;
        // Length-weighted MEAN edge flow along the corridor (SuitabilityGraphMath's
        // GrowCorridor). Comparable to the network's mean positive edge flow, which is
        // what the mode floors are a multiple of.
        public float CapturedFlow;
        // Total journey weight this line would put onto the network, summed over every
        // zone pair it makes routable. A city-wide SUM, so it is one to two orders of
        // magnitude larger than CapturedFlow and the two must never be compared,
        // combined, or substituted for one another.
        public float EnabledDemand;
        // Whether EnabledDemand was ever measured. A candidate past the transfer
        // scoring window is left at zero without being routed, and a zero that was
        // never measured is not evidence of anything — it must not be read as "this
        // line would improve nothing", which is how every unscored candidate came to be
        // dropped by a bar that was meant to reject only measured zeroes.
        public bool DemandScored;
        public float Length;
        // Which network traced this alignment, and therefore which modes could
        // actually run on it. A tunnel path cannot host a bus.
        public RouteNetwork Network;
        // The mode whose lattice traced this alignment. A lattice path is only valid
        // for the cost model that produced it, so this is the only mode it may be
        // suggested as — see TransitModes.ModesForTraced. Meaningless for a road
        // corridor, where growth carries no mode-specific cost.
        public ModePreset TracedMode;
        // Fleet the line would need to hold its assumed headway.
        public int Vehicles;
    }

    internal static class SuitabilityRoutes
    {
        // Fraction of a corridor's demand a single line is taken to satisfy. Below 1
        // so a genuinely enormous corridor can still justify a second line.
        private const float CaptureFraction = 0.85f;
        // How far novelty suppression spreads once a corridor is chosen, in graph
        // hops, and how hard it bites at the corridor itself.
        private const int NoveltyHops = 3;
        private const float NoveltyFactor = 0.15f;

        // Corners closer than this to the straight line between their neighbours are
        // lattice artefacts rather than real alignment.
        private const float SimplifyTolerance = 120f;
        // How far along the line a stop may be nudged to find a better score,
        // as a fraction of the spacing. It never leaves the line.
        private const float StopSearchFraction = 0.35f;

        // Two consecutive calls may never end up closer than this share of the line's
        // own interval. Adjacent windows can each nudge by StopSearchFraction, so
        // without a floor they can close to 30% of the interval — a tram calling twice
        // within sight of itself, which is what the player saw.
        private const float MinStopGapShare = 0.6f;

        // A line passing this close to an existing served stop calls AT it rather than
        // beside it. Any player would put the stop at the station; doing otherwise
        // leaves two stops a short walk apart and no reason for either.
        private const float StationCallMetres = 150f;
        // Two stops closer than this are the same stop: the along-line search can land
        // consecutive placements on nearly the same spot.
        private const float MinStopSeparationMetres = 20f;
        // How far a line's endpoint may be from a road node when re-tracing it, and how
        // long the re-traced path may be.
        private const float RetraceSnapMetres = 600f;
        private const float RetraceMaxPathMetres = 30000f;
        // Dwell at each stop when estimating a fleet, in seconds, and the shortest
        // headway worth planning around.
        private const float StopDwellSeconds = 15f;
        private const float MinPlannedHeadwaySeconds = 30f;
        // Share of a candidate's stops that must already have service on the same
        // alignment before it counts as a line the player has already built.
        private const float DuplicateStopShare = 0.75f;

        // Grows corridors on ONE network and appends them as candidates. Each
        // network carries its own mode family, because the alignment a mode can use
        // is what decides where its routes may run: buses and trams are stuck with
        // streets, metro tunnels and trains lay their own, ferries need water.
        public static void BuildForNetwork(
            SuitabilityRoadGraph network,
            RouteObjective objective,
            int maxRoutes,
            float minFlowFraction,
            float maxRouteLength,
            float[]? nodeDemand,
            float demandFloor,
            ModePreset? forcedMode,
            List<SuggestedRoute> output,
            System.Func<float2, ModePreset, float> scoreAt,
            InterchangeMap hubs,
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
                    network, corridor, meanFlow, forcedMode, scoreAt, hubs,
                    out bool shorterThanAnyMode, out int aimedAtInterchange);
                if (candidate is not null)
                {
                    output.Add(candidate);
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

                SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, CaptureFraction);
                SuitabilityGraphMath.DecayNovelty(graph, corridor, novelty, NoveltyHops, NoveltyFactor);
            }

            // Why the corridors on this network came out the length they did. A route
            // that ran out of demand and one hemmed in by corridors grown before it
            // both arrive as a small number of metres, and the two call for opposite
            // responses — the first means there is nothing there, the second means the
            // growth order is eating its own network.
            Mod.Log.Info(
                $"Corridor growth on {network.Network}: {(lengthCount).ToString(CultureInfo.InvariantCulture)} grown, " +
                $"mean length {((lengthCount > 0 ? lengthSum / lengthCount : 0f)).ToString("F0", CultureInfo.InvariantCulture)}m, " +
                $"{(hitMaxLength).ToString(CultureInfo.InvariantCulture)} reached the {(maxRouteLength).ToString("F0", CultureInfo.InvariantCulture)}m limit; " +
                $"meanEdgeFlow={(meanFlow).ToString("F0", CultureInfo.InvariantCulture)}, flowFloor={(flowFloor).ToString("F0", CultureInfo.InvariantCulture)}, " +
                $"{(unstoppable).ToString(CultureInfo.InvariantCulture)} edge(s) excluded as unable to host a stop, " +
                $"{(wandered).ToString(CultureInfo.InvariantCulture)} discarded for coming back on themselves " +
                $"(ends must be {(SuitabilityGraphMath.MinDirectness * 100f).ToString("F0", CultureInfo.InvariantCulture)}% of the length apart); " +
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
            SuitabilityRoadGraph network,
            List<ZoneFlow> flows,
            int[] zoneNodes,
            int maxRoutes,
            float maxRouteLength,
            ModePreset forcedMode,
            List<SuggestedRoute> output,
            System.Func<float2, ModePreset, float> scoreAt,
            InterchangeMap hubs,
            out int considered,
            out int tooShort,
            out int atInterchange)
        {
            considered = 0;
            tooShort = 0;
            atInterchange = 0;
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
            var takenFrom = new List<float2>();
            var takenTo = new List<float2>();

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

                var fromPoint = new float2(network.NodePositionsX[from], network.NodePositionsZ[from]);
                var toPoint = new float2(network.NodePositionsX[to], network.NodePositionsZ[to]);

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

                var route = new SuggestedRoute
                {
                    Network = network.Network,
                    Mode = forcedMode,
                    TracedMode = forcedMode,
                };
                network.MaterialisePath(scratch, route.Path);

                // A shortest path on a uniform grid is a minimal staircase; straightening
                // it leaves the near-straight alignment a tunnel or a crossing actually
                // takes.
                Simplify(route.Path, SimplifyTolerance);
                route.Length = PathLength(route.Path);
                route.CapturedFlow = network.FlowAlong(scratch);

                // The traced mode's OWN minimum, not the shortest any mode on this
                // network could use. Since a lattice alignment may only be suggested as
                // the mode that traced it (TransitModes.ModesForTraced), measuring it
                // against the other rail mode's floor only carried paths forward that
                // ChooseMode was then certain to reject: 1320 of 1329 train paths in one
                // cycle were under the 3200 m metro floor, and every one of the nine
                // survivors was under the train's own 10000 m.
                if (route.Length < TransitModes.MinLengthFor(forcedMode))
                {
                    tooShort++;
                    continue;
                }

                PlaceStops(route, forcedMode, scoreAt, hubs);
                if (route.Stops.Count < 2)
                {
                    continue;
                }

                takenFrom.Add(fromPoint);
                takenTo.Add(toPoint);
                output.Add(route);
                added++;
                atInterchange += aimedAtInterchange;
            }
        }

        // Moves a terminus onto the network node nearest a usable interchange, when one
        // is within a walk of it. The search radius is the transfer walk radius, so the
        // zone the line was drawn for is still served from the moved end — this buys a
        // change of vehicle without giving up the demand that justified the line.
        private static int SnapToInterchange(
            SuitabilityRoadGraph network, InterchangeMap hubs, ModePreset mode, int node, ref float2 point)
        {
            if (hubs.Count == 0
                || !hubs.TryFindNear(mode, point.x, point.y, hubs.Radius, out float hubX, out float hubZ))
            {
                return node;
            }

            // The lattice has a 128 m pitch, so the node nearest a station is not the
            // station; anything further off than a transfer walk is not an interchange.
            int snapped = network.NearestNode(new float2(hubX, hubZ), hubs.Radius);
            if (snapped < 0)
            {
                return node;
            }

            point = new float2(network.NodePositionsX[snapped], network.NodePositionsZ[snapped]);
            return snapped;
        }

        // Whether a line already proposed on this network runs between the same two
        // places. Zones are 256 m across, so anything inside that is the same pair.
        private static bool AlreadyConnecting(List<float2> froms, List<float2> tos, float2 from, float2 to)
        {
            float sameSq = SuitabilityTravelDemand.ZoneSize * SuitabilityTravelDemand.ZoneSize;
            for (int i = 0; i < froms.Count; i++)
            {
                bool sameWay = math.distancesq(froms[i], from) <= sameSq && math.distancesq(tos[i], to) <= sameSq;
                bool otherWay = math.distancesq(froms[i], to) <= sameSq && math.distancesq(tos[i], from) <= sameSq;
                if (sameWay || otherWay)
                {
                    return true;
                }
            }

            return false;
        }

        private static float PathLength(List<float2> path)
        {
            float length = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                length += math.distance(path[i - 1], path[i]);
            }

            return length;
        }

        // Edges a line could not call at start out spent. They stay in the graph so
        // journeys still route over them and their flow still counts towards the
        // network's mean, but no corridor may be seeded on or extended along one — a
        // bus route down a motorway serves nobody. Returns how many were excluded.
        private static int MarkUnstoppable(SuitabilityRoadGraph network, bool[] used)
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
        private static float EndToEnd(List<float2> path)
        {
            return path.Count < 2 ? 0f : math.distance(path[0], path[path.Count - 1]);
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
            SuitabilityRoadGraph network,
            Corridor corridor,
            float meanFlow,
            ModePreset? forcedMode,
            System.Func<float2, ModePreset, float> scoreAt,
            InterchangeMap hubs,
            out bool shorterThanAnyMode,
            out int aimedAtInterchange)
        {
            shorterThanAnyMode = false;
            ModePreset mode = forcedMode ?? ClassifyStreetMode(corridor.CapturedFlow, meanFlow);
            var route = new SuggestedRoute
            {
                CapturedFlow = corridor.CapturedFlow,
                Length = corridor.Length,
                Network = network.Network,
                Mode = mode,
                TracedMode = mode,
            };

            // A terminus is where every rider must finish or change vehicle, so it is
            // the most valuable point on the line to put within a walk of another mode.
            // The lattices have aimed their ends at a hub since SnapToInterchange was
            // written; a grown corridor's ends were wherever growth happened to stop,
            // because they are an OUTPUT of flow peeling rather than an input.
            int extended = AimEndsAtInterchange(network, hubs, route.Mode, corridor.Nodes);
            aimedAtInterchange = extended;


            // Follow each edge's real centreline where there is one, so a street
            // route stays on the street instead of cutting every corner.
            network.MaterialisePath(corridor.Nodes, route.Path);

            // Only when the ends actually moved: corridor.Length is the graph's own
            // edge total, and re-measuring the materialised polyline for every
            // candidate would quietly change every length floor at once.
            if (extended > 0)
            {
                route.Length = PathLength(route.Path);
            }

            // Lattice corridors are 8-connected staircases; straighten them before
            // measuring or drawing so a tunnel does not zig-zag.
            if (route.Mode is not (ModePreset.Bus or ModePreset.Tram))
            {
                Simplify(route.Path, SimplifyTolerance);
            }

            if (route.Length < TransitModes.ShortestModeLength(network.Network))
            {
                shorterThanAnyMode = true;
                return null;
            }

            // A line that comes back on itself is a ring, not a route. Growth prefers
            // to head away from where it started, but on a corridor with nowhere else
            // to go it still curls round — a metro was proposed as a box around an
            // empty field, and another as a ring about the whole city.
            if (!SuitabilityGraphMath.IsDirectEnough(EndToEnd(route.Path), route.Length))
            {
                return null;
            }

            PlaceStops(route, route.Mode, scoreAt, hubs);
            return route.Stops.Count >= 2 ? route : null;
        }

        // How far along the streets a corridor's end may be extended to reach a hub.
        // The hub itself is within one transfer walk, but the road route to the node
        // beside it can go round a block, so the allowance is wider than the walk —
        // and still short enough that the extension is local rather than a second leg.
        private const float InterchangeReachMetres = 500f;

        // Extends a grown corridor's ends onto the road node nearest a usable
        // interchange, when one is within reach. Returns how many ends moved.
        //
        // Extending the NODE LIST rather than nudging the drawn polyline: a bus has to
        // follow the streets, so the added stretch must be real road edges. That is
        // also why this cannot reuse SnapToInterchange, which relocates a lattice
        // terminus by moving a point — on a lattice any two nodes are joined, on the
        // street network they are not.
        private static int AimEndsAtInterchange(
            SuitabilityRoadGraph network, InterchangeMap hubs, ModePreset mode, List<int> nodes)
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
            SuitabilityRoadGraph network,
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
            if (!hubs.TryFindNear(mode, x, z, hubs.Radius, out float hubX, out float hubZ))
            {
                return false;
            }

            int target = network.NearestNode(new float2(hubX, hubZ), hubs.Radius);
            if (target < 0 || target == terminus || nodes.Contains(target))
            {
                return false;
            }

            if (!network.TracePath(terminus, target, InterchangeReachMetres, trace) || trace.Count < 2)
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

        // On the street network the only choice is how heavy the corridor is, and the
        // bar is the tram's own capacity floor rather than a second copy of it.
        private static ModePreset ClassifyStreetMode(float corridorFlow, float meanFlow)
        {
            return corridorFlow >= meanFlow * TransitModes.MinFlowMultipleFor(ModePreset.Tram)
                ? ModePreset.Tram
                : ModePreset.Bus;
        }

        // Fleet needed to hold the mode's assumed headway around the whole line.
        public static int EstimateVehicles(ModePreset mode, float lengthMetres, int stops, float headwaySeconds)
        {
            float speed = TransitModes.CruiseSpeedFor(mode);
            // Out and back, dwelling at every stop in each direction.
            float roundTrip = ((lengthMetres * 2f) / math.max(1f, speed)) + (stops * 2 * StopDwellSeconds);
            return math.max(1, (int)math.round(roundTrip / math.max(MinPlannedHeadwaySeconds, headwaySeconds)));
        }

        // Re-traces a corridor on the ROAD network between the same endpoints.
        //
        // Needed because falling back across mode families changes what the alignment
        // may be: a metro corridor is a tunnel path, and a bus cannot drive it. So the
        // route is genuinely recalculated along streets rather than merely relabelled.
        public static SuggestedRoute? RetraceOnRoad(
            SuitabilityRoadGraph roads,
            float2 from,
            float2 to,
            float referenceFlow,
            System.Func<float2, ModePreset, float> scoreAt,
            InterchangeMap hubs,
            List<int> scratch)
        {
            if (roads?.Graph is null)
            {
                return null;
            }

            int fromNode = roads.NearestNode(from, RetraceSnapMetres);
            int toNode = roads.NearestNode(to, RetraceSnapMetres);
            if (fromNode < 0 || toNode < 0 || !roads.TracePath(fromNode, toNode, RetraceMaxPathMetres, scratch))
            {
                return null;
            }

            var route = new SuggestedRoute { Network = RouteNetwork.Road, TracedMode = ModePreset.Bus };
            roads.MaterialisePath(scratch, route.Path);

            float length = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                length += math.distance(route.Path[i - 1], route.Path[i]);
            }

            route.Length = length;
            route.CapturedFlow = roads.FlowAlong(scratch);

            // No track share and no enabled demand: a road re-trace is judged on flow,
            // which is the only evidence a street corridor ever had.
            var evidence = new CorridorEvidence(route.CapturedFlow, route.Length, 0f, 0f, 0f);
            if (!TransitModes.ChooseMode(RouteNetwork.Road, route.TracedMode, evidence, referenceFlow,
                    out ModePreset mode, out ModeRejection why))
            {
                Mod.Log.Info(
                    $"Re-trace on road found a {(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m path " +
                    $"carrying {(route.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)} against a reference of " +
                    $"{(referenceFlow).ToString("F0", CultureInfo.InvariantCulture)}, but no road mode is justified: " +
                    $"{(why == ModeRejection.TooShort ? "too short for any road mode" : "below every demand floor")}.");
                return null;
            }

            Restop(route, mode, scoreAt, hubs);
            return KeepsItsFloor(route) ? route : null;
        }

        // A route still clears the floor its mode was chosen against.
        //
        // ChooseMode is necessarily asked BEFORE the stops exist — the mode is what
        // decides their spacing — but PlaceStops then trims the polyline back to its
        // end stops, and that can pull a route under the very floor that approved it.
        // That is how a 350 m corridor reached the map as a bus line against a 500 m
        // minimum. Re-verify once the stops are placed.
        public static bool KeepsItsFloor(SuggestedRoute route)
        {
            return route is not null
                && route.Stops.Count >= 2
                && route.Length >= TransitModes.MinLengthFor(route.Mode);
        }

        // Re-places stops after a mode change, since spacing is mode-specific.
        public static void Restop(
            SuggestedRoute route, ModePreset mode, System.Func<float2, ModePreset, float> scoreAt, InterchangeMap hubs)
        {
            route.Mode = mode;
            PlaceStops(route, mode, scoreAt, hubs);
        }

        // True when this candidate essentially retraces a line that already exists.
        // Without this a suggestion survived being built: the player laid the tram the
        // mod asked for and the same suggestion kept being offered.
        public static bool DuplicatesExisting(SuggestedRoute route, List<ExistingLine> existing, List<float2> stopPositions, float matchRadius)
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

                        if (math.distancesq(route.Stops[s], stopPositions[index]) <= radiusSq)
                        {
                            matched++;
                            break;
                        }
                    }
                }

                // Most of the suggestion already has service on the same alignment.
                if (matched >= (int)math.ceil(route.Stops.Count * DuplicateStopShare))
                {
                    return true;
                }
            }

            return false;
        }

        // A window with nothing worth stopping at is skipped rather than served. The
        // line still crosses the ground, it just does not call there — which is what a
        // real metro does under a park. The bar is a share of the route's OWN median
        // window, not an absolute score: a line through uniformly thin land keeps its
        // stops, and only a genuine outlier is dropped. That is the case the player
        // reported — a suggested station standing on a solar power plant, where the
        // line had to cross open industrial ground and the spacing called for a stop
        // regardless of there being nobody to serve.
        private const float StopScoreFloorShare = 0.35f;

        // Walks the polyline dropping a stop every `spacing` metres. At each one it
        // searches a short way forwards and backwards ALONG the line for the
        // best-scoring position — so the flow still decides where the line runs and
        // the suitability score still decides exactly where a stop sits, but a stop
        // can never end up beside its own route.
        internal static void PlaceStops(
            SuggestedRoute route, ModePreset mode, System.Func<float2, ModePreset, float> scoreAt, InterchangeMap hubs)
        {
            route.Stops.Clear();
            if (route.Path.Count < 2)
            {
                return;
            }

            float total = PathLength(route.Path);
            if (total <= 0f)
            {
                return;
            }

            float spacing = TransitModes.StopSpacingFor(mode);
            int windows = (int)(total / spacing) + 2;
            var offsets = new float[windows];
            var scores = new float[windows];
            var mustCall = new bool[windows];
            int count = ScanStopWindows(route.Path, total, mode, scoreAt, hubs, offsets, scores, mustCall);

            var keep = new bool[count];
            SuitabilityScoring.SelectCallingPoints(
                scores, count, StopScoreFloorShare, new float[count], mustCall, keep);

            float firstAt = -1f;
            float lastAt = -1f;
            for (int i = 0; i < count; i++)
            {
                if (!keep[i] || !AddStop(route, PointAlong(route.Path, offsets[i])))
                {
                    continue;
                }

                if (firstAt < 0f)
                {
                    firstAt = offsets[i];
                }

                lastAt = offsets[i];
            }

            // The line is drawn between its termini. The nudge search can pull the end
            // stops inward, and a rejected near-duplicate can drop the final one
            // altogether, both of which left the polyline running on past the last stop
            // marker with nothing to serve out there.
            if (firstAt >= 0f && lastAt > firstAt)
            {
                TrimPath(route, firstAt, lastAt);
            }
        }

        // One candidate position per interval, with the score it was chosen for.
        // Deciding which of them are worth a stop is the caller's job.
        //
        // The intervals are EVEN, not a fixed grid with a clamped tail. At 450 m
        // spacing a 2150 m tram put its last two windows 2150 mod 450 = 350 m apart and
        // the nudge closed that to 190 m; five even intervals of 430 m carry the same
        // six stops with none of them bunched.
        private static int ScanStopWindows(
            List<float2> path,
            float total,
            ModePreset mode,
            System.Func<float2, ModePreset, float> scoreAt,
            InterchangeMap hubs,
            float[] offsets,
            float[] scores,
            bool[] mustCall)
        {
            float spacing = TransitModes.StopSpacingFor(mode);
            var planned = new float[offsets.Length];
            int windows = SuitabilityScoring.PlanCallingPoints(total, spacing, planned);
            if (windows < 2)
            {
                return 0;
            }

            float step = total / (windows - 1);
            float search = step * StopSearchFraction;
            float minGap = step * MinStopGapShare;
            int count = 0;
            float previous = float.NegativeInfinity;

            for (int window = 0; window < windows; window++)
            {
                float at = planned[window];
                bool terminus = window == 0 || window == windows - 1;

                // A terminus is pinned where the alignment ends, and only moves if it
                // cannot be used where it is.
                //
                // Pinning matters because the nudge window is clamped to the polyline:
                // at distance 0 it can only search FORWARD and at the far end only
                // BACKWARD, so the end stops could only ever move inward, and TrimPath
                // then cut the line back to them. Every route lost up to two nudge
                // windows of length (245 m for a bus) and died against the very length
                // floor that had just approved it — corridors that cleared the 500 m
                // bus minimum came out at 309 m, 228 m and 391 m and were dropped,
                // leaving whole refreshes with nothing to suggest. The ends are also
                // where the search is least welcome: growth chose them because that is
                // where the demand is.
                //
                // But pinning alone put a stop wherever the corridor happened to stop,
                // and on a free-form lattice that is not necessarily a place a stop can
                // exist: a suggested ferry ended in open water, hundreds of metres from
                // any shore, because its terminus scored zero and was pinned there
                // anyway. So a terminus that scores nothing searches for the nearest
                // position along the line that scores at all.
                float lower = math.max(at - search, 0f);
                float upper = math.min(at + search, total);
                if (!terminus && count > 0)
                {
                    lower = math.max(lower, previous + minGap);
                    if (lower > upper)
                    {
                        // No position in this window is far enough from the last call.
                        // Skipping it is the point: the line runs on to the next one.
                        continue;
                    }
                }

                float best = math.clamp(at, lower, upper);
                float bestScore = scoreAt is null ? 0f : scoreAt(PointAlong(path, best), mode);
                bool pinned = terminus && bestScore > 0f;
                bool atStation = false;

                if (scoreAt is not null && upper > lower && !pinned)
                {
                    atStation = ChooseInWindow(path, mode, scoreAt, hubs, lower, upper, ref best, ref bestScore);
                }

                offsets[count] = best;
                scores[count] = bestScore;
                mustCall[count] = atStation;
                previous = best;
                count++;

                if (at >= total)
                {
                    break;
                }
            }

            return count;
        }

        // The position within one window a stop belongs at. Normally the best-scoring
        // one; but where the line passes an existing served stop, the position nearest
        // that stop wins — a call at the station is worth more than a slightly better
        // tile beside it, because it is what turns two lines into a network.
        // Returns true when the chosen position is at an existing served stop, which
        // the caller records so the score floor cannot drop the call afterwards.
        private static bool ChooseInWindow(
            List<float2> path,
            ModePreset mode,
            System.Func<float2, ModePreset, float> scoreAt,
            InterchangeMap hubs,
            float lower,
            float upper,
            ref float best,
            ref float bestScore)
        {
            float stationBest = 0f;
            float stationDistSq = StationCallMetres * StationCallMetres;
            float stationScore = 0f;
            bool atStation = false;

            // Sample a handful of positions in the window; more would not change the
            // outcome at 32 m tile resolution.
            const int Samples = 6;
            for (int step = 0; step <= Samples; step++)
            {
                float candidate = lower + ((upper - lower) * step / Samples);
                float2 point = PointAlong(path, candidate);
                float score = scoreAt(point, mode);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }

                // A stop over ground nothing can use is not a call, however close the
                // station: the terminus repair above exists for exactly that reason.
                if (score <= 0f || hubs.Count == 0
                    || !hubs.TryNearest(point.x, point.y, StationCallMetres, out float distanceSq))
                {
                    continue;
                }

                if (distanceSq < stationDistSq)
                {
                    atStation = true;
                    stationDistSq = distanceSq;
                    stationBest = candidate;
                    stationScore = score;
                }
            }

            if (atStation)
            {
                best = stationBest;
                bestScore = stationScore;
            }

            return atStation;
        }

        // Keeps only the stretch of the polyline between two distances along it,
        // inserting exact endpoints so the drawn line starts and ends on a stop.
        private static void TrimPath(SuggestedRoute route, float from, float to)
        {
            var trimmed = new List<float2> { PointAlong(route.Path, from) };

            float travelled = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                float segment = math.distance(route.Path[i - 1], route.Path[i]);
                float at = travelled + segment;
                if (at > from && at < to)
                {
                    trimmed.Add(route.Path[i]);
                }

                travelled = at;
            }

            trimmed.Add(PointAlong(route.Path, to));

            route.Path.Clear();
            for (int i = 0; i < trimmed.Count; i++)
            {
                route.Path.Add(trimmed[i]);
            }

            // Length is quoted to the player and used by the mode floors, so it has to
            // follow the trim rather than keep describing the untrimmed corridor.
            float length = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                length += math.distance(route.Path[i - 1], route.Path[i]);
            }

            route.Length = length;
        }

        // Position at `distance` along the polyline.
        private static float2 PointAlong(List<float2> path, float distance)
        {
            float travelled = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                float segment = math.distance(path[i - 1], path[i]);
                if (segment <= 0f)
                {
                    continue;
                }

                if (travelled + segment >= distance)
                {
                    float t = (distance - travelled) / segment;
                    return math.lerp(path[i - 1], path[i], math.saturate(t));
                }

                travelled += segment;
            }

            return path[path.Count - 1];
        }

        private static void Simplify(List<float2> path, float tolerance)
        {
            var points = new List<float2Like>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                points.Add(new float2Like(path[i].x, path[i].y));
            }

            SuitabilityGraphMath.SimplifyPolyline(points, tolerance);

            path.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                path.Add(new float2(points[i].x, points[i].y));
            }
        }

        private static bool AddStop(SuggestedRoute route, float2 placed)
        {
            float minSeparationSq = MinStopSeparationMetres * MinStopSeparationMetres;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                if (math.distancesq(route.Stops[i], placed) < minSeparationSq)
                {
                    return false;
                }
            }

            route.Stops.Add(placed);
            return true;
        }

    }
}
