using System.Collections.Generic;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // A suggested transit line: the polyline it runs along, where its stops go, and
    // which mode it should be.
    internal sealed class SuggestedRoute
    {
        public readonly List<float2> Path = new List<float2>();
        public readonly List<float2> Stops = new List<float2>();
        public Setting.ModePreset Mode;
        public float CapturedFlow;
        public float Length;
        // Which network traced this alignment, and therefore which modes could
        // actually run on it. A tunnel path cannot host a bus.
        public RouteNetwork Network;
        // Fleet the line would need to hold its assumed headway.
        public int Vehicles;

        public void Clear()
        {
            Path.Clear();
            Stops.Clear();
            CapturedFlow = 0f;
            Length = 0f;
        }
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

        // Mode thresholds. Flow is in trips per aggregation window, so these are
        // relative rather than absolute passenger counts — calibrated against the
        // mean corridor flow so they hold on a town and a metropolis alike.
        private const float MediumFlowMultiple = 1.5f;
        // Corners closer than this to the straight line between their neighbours are
        // lattice artefacts rather than real alignment.
        private const float SimplifyTolerance = 120f;
        // How far along the line a stop may be nudged to find a better score,
        // as a fraction of the spacing. It never leaves the line.
        private const float StopSearchFraction = 0.35f;

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
            float[] nodeDemand,
            float demandFloor,
            Setting.ModePreset? forcedMode,
            List<SuggestedRoute> output,
            System.Func<float2, float> scoreAt,
            out int grown,
            out int tooShort)
        {
            grown = 0;
            tooShort = 0;
            if (network?.Graph == null || network.EdgeFlow == null || network.EdgeCount == 0)
            {
                return;
            }

            CompactGraph graph = network.Graph;
            // Work on a copy: peeling is destructive and the assigned flow is reused
            // by the demand layer.
            var flow = (float[])network.EdgeFlow.Clone();
            var used = new bool[graph.EdgeCount];
            var novelty = new float[graph.NodeCount];
            for (int n = 0; n < graph.NodeCount; n++)
            {
                novelty[n] = 1f;
            }

            float meanFlow = SuitabilityGraphMath.MeanPositiveFlow(flow, graph.EdgeCount);
            if (meanFlow <= 0f)
            {
                return;
            }

            float noveltyWeight = SuitabilityGraphMath.NoveltyWeight(objective, meanFlow);
            float flowFloor = meanFlow * minFlowFraction;
            var corridor = new Corridor();

            // Grow more corridors than asked for: many are discarded for being too
            // short or too light, and stopping at maxRoutes attempts left the merged
            // set far thinner than the requested count.
            int attempts = maxRoutes * 4;
            for (int r = 0; r < attempts && output.Count < maxRoutes * 2; r++)
            {
                if (!SuitabilityGraphMath.GrowCorridor(graph, flow, used, novelty, noveltyWeight,
                        flowFloor, maxRouteLength, corridor, nodeDemand, demandFloor))
                {
                    break;
                }

                // A single edge is not a line.
                if (corridor.Edges.Count < 2)
                {
                    SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, 1f);
                    continue;
                }

                var route = new SuggestedRoute
                {
                    CapturedFlow = corridor.CapturedFlow,
                    Length = corridor.Length,
                    Network = network.Network,
                };

                for (int i = 0; i < corridor.Nodes.Count; i++)
                {
                    int node = corridor.Nodes[i];
                    route.Path.Add(new float2(network.NodePositionsX[node], network.NodePositionsZ[node]));
                }

                route.Mode = forcedMode ?? ClassifyStreetMode(corridor.CapturedFlow, meanFlow);

                // Lattice corridors are 8-connected staircases; straighten them
                // before measuring or drawing so a tunnel does not zig-zag.
                if (route.Mode != Setting.ModePreset.Bus && route.Mode != Setting.ModePreset.Tram)
                {
                    Simplify(route.Path, SimplifyTolerance);
                }

                // Rail and water suggestions are only worth making at a scale that
                // justifies the infrastructure; a 900 m metro line is nonsense.
                grown++;
                if (route.Length < MinLengthFor(route.Mode))
                {
                    tooShort++;
                }
                else
                {
                    PlaceStops(route, StopSpacingFor(route.Mode), scoreAt);
                    if (route.Stops.Count >= 2)
                    {
                        output.Add(route);
                    }
                }

                SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, CaptureFraction);
                SuitabilityGraphMath.DecayNovelty(graph, corridor, novelty, NoveltyHops, NoveltyFactor);
            }
        }

        // On the street network the only choice is how heavy the corridor is.
        private static Setting.ModePreset ClassifyStreetMode(float corridorFlow, float meanFlow)
        {
            return corridorFlow >= meanFlow * MediumFlowMultiple
                ? Setting.ModePreset.Tram
                : Setting.ModePreset.Bus;
        }

        // Modes a given alignment can carry, best capacity first. Choosing among these
        // is what lets an under-used rail corridor come back as something feasible
        // instead of being dropped for not justifying a metro.
        public static Setting.ModePreset[] ModesFor(RouteNetwork network)
        {
            switch (network)
            {
                case RouteNetwork.Rail:
                    return new[] { Setting.ModePreset.Train, Setting.ModePreset.Metro };
                case RouteNetwork.Water:
                    return new[] { Setting.ModePreset.Ferry };
                default:
                    // Streets can host either, and a bus has no capacity floor, so a
                    // road corridor always yields a usable suggestion.
                    return new[] { Setting.ModePreset.Tram, Setting.ModePreset.Bus };
            }
        }

        // Highest-capacity mode whose demand floor and minimum length this corridor
        // actually meets. Returns false when nothing on this alignment is justified.
        public static bool ChooseMode(
            RouteNetwork network,
            float flow,
            float length,
            float referenceFlow,
            out Setting.ModePreset mode)
        {
            Setting.ModePreset[] options = ModesFor(network);
            for (int i = 0; i < options.Length; i++)
            {
                Setting.ModePreset option = options[i];
                float floor = referenceFlow * MinFlowMultipleFor(option);
                if (flow >= floor && length >= MinLengthFor(option))
                {
                    mode = option;
                    return true;
                }
            }

            mode = options[options.Length - 1];
            return false;
        }

        // Fleet needed to hold the mode's assumed headway around the whole line.
        public static int EstimateVehicles(Setting.ModePreset mode, float lengthMetres, int stops, float headwaySeconds)
        {
            float speed = CruiseSpeedFor(mode);
            float dwell = 15f;
            float roundTrip = (lengthMetres * 2f) / math.max(1f, speed) + stops * 2 * dwell;
            return math.max(1, (int)math.round(roundTrip / math.max(30f, headwaySeconds)));
        }

        public static float CruiseSpeedFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 12f;
                case Setting.ModePreset.Metro: return 18f;
                case Setting.ModePreset.Train: return 28f;
                case Setting.ModePreset.Ferry: return 10f;
                default: return 9f;
            }
        }

        // Re-traces a corridor on the ROAD network between the same endpoints.
        //
        // Needed because falling back across mode families changes what the alignment
        // may be: a metro corridor is a tunnel path, and a bus cannot drive it. So the
        // route is genuinely recalculated along streets rather than merely relabelled.
        public static SuggestedRoute RetraceOnRoad(
            SuitabilityRoadGraph roads,
            float2 from,
            float2 to,
            float referenceFlow,
            System.Func<float2, float> scoreAt,
            List<int> scratch)
        {
            if (roads?.Graph == null)
            {
                return null;
            }

            int fromNode = roads.NearestNode(from, 600f);
            int toNode = roads.NearestNode(to, 600f);
            if (fromNode < 0 || toNode < 0 || !roads.TracePath(fromNode, toNode, 30000f, scratch))
            {
                return null;
            }

            var route = new SuggestedRoute { Network = RouteNetwork.Road };
            for (int i = 0; i < scratch.Count; i++)
            {
                int node = scratch[i];
                route.Path.Add(new float2(roads.NodePositionsX[node], roads.NodePositionsZ[node]));
            }

            float length = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                length += math.distance(route.Path[i - 1], route.Path[i]);
            }

            route.Length = length;
            route.CapturedFlow = roads.FlowAlong(scratch);

            if (!ChooseMode(RouteNetwork.Road, route.CapturedFlow, route.Length, referenceFlow, out Setting.ModePreset mode))
            {
                return null;
            }

            Restop(route, mode, scoreAt);
            return route.Stops.Count >= 2 ? route : null;
        }

        // Re-places stops after a mode change, since spacing is mode-specific.
        public static void Restop(SuggestedRoute route, Setting.ModePreset mode, System.Func<float2, float> scoreAt)
        {
            route.Mode = mode;
            PlaceStops(route, StopSpacingFor(mode), scoreAt);
        }

        // True when this candidate essentially retraces a line that already exists.
        // Without this a suggestion survived being built: the player laid the tram the
        // mod asked for and the same suggestion kept being offered.
        public static bool DuplicatesExisting(SuggestedRoute route, List<ExistingLine> existing, List<float2> stopPositions, float matchRadius)
        {
            if (route.Stops.Count == 0 || existing == null)
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
                if (matched >= (int)math.ceil(route.Stops.Count * 0.6f))
                {
                    return true;
                }
            }

            return false;
        }

        // Below these lengths the mode is not worth building, whatever the demand.
        public static float MinLengthFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 1500f;
                case Setting.ModePreset.Metro: return 2500f;
                case Setting.ModePreset.Train: return 5000f;
                case Setting.ModePreset.Ferry: return 1500f;
                default: return 800f;
            }
        }

        public static float StopSpacingFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 450f;
                case Setting.ModePreset.Metro: return 800f;
                case Setting.ModePreset.Train: return 2000f;
                case Setting.ModePreset.Ferry: return 1200f;
                default: return 350f;
            }
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
            for (float target = 0f; target <= total + 1f; target += spacing)
            {
                float at = math.min(target, total);
                float best = at;

                if (scoreAt != null && search > 0f)
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

                AddStop(route, PointAlong(route.Path, best));

                if (at >= total)
                {
                    break;
                }
            }
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

        private static void AddStop(SuggestedRoute route, float2 placed)
        {
            // The along-line search can land two stops on nearly the same spot.
            for (int i = 0; i < route.Stops.Count; i++)
            {
                if (math.distancesq(route.Stops[i], placed) < 400f)
                {
                    return;
                }
            }

            route.Stops.Add(placed);
        }

        // Absolute capacity floors, expressed against a city-wide reference flow so
        // they hold on any size of city. Each network hands out its own mode, but a
        // corridor only justifies that mode if it actually carries enough: a metro
        // built for 361 trips while a tram carries 1633 is the wrong way round.
        public static float MinFlowMultipleFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 1.5f;
                case Setting.ModePreset.Metro: return 5f;
                case Setting.ModePreset.Train: return 8f;
                case Setting.ModePreset.Ferry: return 1f;
                default: return 0f;
            }
        }
    }
}
