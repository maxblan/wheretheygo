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
        public float Length;
        // Which network traced this alignment, and therefore which modes could
        // actually run on it. A tunnel path cannot host a bus.
        public RouteNetwork Network;
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
            System.Func<float2, float> scoreAt,
            out int grown,
            out int tooShort)
        {
            grown = 0;
            tooShort = 0;
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
                    network, corridor, meanFlow, forcedMode, scoreAt, out bool shorterThanAnyMode);
                if (candidate is not null)
                {
                    output.Add(candidate);
                    added++;
                }
                else if (shorterThanAnyMode)
                {
                    tooShort++;
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
                $"{(unstoppable).ToString(CultureInfo.InvariantCulture)} edge(s) excluded as unable to host a stop; " +
                $"extensions refused — alreadyUsed={(blocked.m_Used).ToString(CultureInfo.InvariantCulture)}, " +
                $"belowFlowFloor={(blocked.m_Flow).ToString(CultureInfo.InvariantCulture)}, " +
                $"wouldRevisit={(blocked.m_Visited).ToString(CultureInfo.InvariantCulture)}, " +
                $"pastMaxLength={(blocked.m_Length).ToString(CultureInfo.InvariantCulture)}, " +
                $"noDemandBeside={(blocked.m_Demand).ToString(CultureInfo.InvariantCulture)}" +
                $"{(added >= budget ? $"; STOPPED at this network's budget of {budget.ToString(CultureInfo.InvariantCulture)} candidates — there may be more worth having" : string.Empty)}");
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
            System.Func<float2, float> scoreAt,
            out bool shorterThanAnyMode)
        {
            shorterThanAnyMode = false;
            var route = new SuggestedRoute
            {
                CapturedFlow = corridor.CapturedFlow,
                Length = corridor.Length,
                Network = network.Network,
            };

            // Follow each edge's real centreline where there is one, so a street
            // route stays on the street instead of cutting every corner.
            network.MaterialisePath(corridor.Nodes, route.Path);

            route.Mode = forcedMode ?? ClassifyStreetMode(corridor.CapturedFlow, meanFlow);

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

            PlaceStops(route, TransitModes.StopSpacingFor(route.Mode), scoreAt);
            return route.Stops.Count >= 2 ? route : null;
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
            System.Func<float2, float> scoreAt,
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

            var route = new SuggestedRoute { Network = RouteNetwork.Road };
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
            var evidence = new CorridorEvidence(route.CapturedFlow, route.Length, 0f, 0f);
            if (!TransitModes.ChooseMode(RouteNetwork.Road, evidence, referenceFlow,
                    out ModePreset mode, out ModeRejection why))
            {
                Mod.Log.Info(
                    $"Re-trace on road found a {(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m path " +
                    $"carrying {(route.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)} against a reference of " +
                    $"{(referenceFlow).ToString("F0", CultureInfo.InvariantCulture)}, but no road mode is justified: " +
                    $"{(why == ModeRejection.TooShort ? "too short for any road mode" : "below every demand floor")}.");
                return null;
            }

            Restop(route, mode, scoreAt);
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
        public static void Restop(SuggestedRoute route, ModePreset mode, System.Func<float2, float> scoreAt)
        {
            route.Mode = mode;
            PlaceStops(route, TransitModes.StopSpacingFor(mode), scoreAt);
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

        // Walks the polyline dropping a stop every `spacing` metres. At each one it
        // searches a short way forwards and backwards ALONG the line for the
        // best-scoring position — so the flow still decides where the line runs and
        // the suitability score still decides exactly where a stop sits, but a stop
        // can never end up beside its own route.
        internal static void PlaceStops(SuggestedRoute route, float spacing, System.Func<float2, float> scoreAt)
        {
            route.Stops.Clear();
            if (route.Path.Count < 2)
            {
                return;
            }

            float total = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                total += math.distance(route.Path[i - 1], route.Path[i]);
            }

            if (total <= 0f)
            {
                return;
            }

            float search = spacing * StopSearchFraction;
            float firstAt = -1f;
            float lastAt = -1f;
            for (float target = 0f; target <= total + 1f; target += spacing)
            {
                float at = math.min(target, total);
                float best = at;

                // A terminus is pinned where the corridor ends, and only moves if it
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
                // position along the line that scores at all. That is a validity
                // repair, not an optimisation — it moves only when staying is not an
                // option, which is why it cannot bring back the systematic shortening.
                bool terminus = at <= 0f || at >= total;
                bool usable = scoreAt is null || scoreAt(PointAlong(route.Path, at)) > 0f;

                if (scoreAt is not null && search > 0f && (!terminus || !usable))
                {
                    float bestScore = float.MinValue;
                    // Sample a handful of positions in the window; more would not
                    // change the outcome at 32 m tile resolution.
                    for (int step = -3; step <= 3; step++)
                    {
                        float candidate = math.clamp(at + search * step / 3f, 0f, total);
                        float score = scoreAt(PointAlong(route.Path, candidate));
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }

                if (AddStop(route, PointAlong(route.Path, best)))
                {
                    if (firstAt < 0f)
                    {
                        firstAt = best;
                    }

                    lastAt = best;
                }

                if (at >= total)
                {
                    break;
                }
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
