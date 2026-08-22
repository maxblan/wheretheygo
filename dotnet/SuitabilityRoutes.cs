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
        private const float TrunkFlowMultiple = 3f;
        private const float MediumFlowMultiple = 1.5f;
        private const float TrunkLengthMetres = 4000f;
        // A road route this much longer than the straight line between its ends is
        // detouring around something; if that straight line crosses water, a boat
        // would short-circuit it.
        private const float FerryDetourRatio = 2.5f;

        // Grows up to `maxRoutes` corridors, peeling demand between each so the
        // suggestions complement rather than duplicate one another.
        public static void Build(
            SuitabilityRoadGraph roads,
            RouteObjective objective,
            int maxRoutes,
            float minFlowFraction,
            float maxRouteLength,
            List<SuggestedRoute> routes,
            System.Func<float2, float2> snapStop,
            System.Func<float2, float2, bool> crossesWater)
        {
            routes.Clear();
            if (roads?.Graph == null || roads.EdgeFlow == null || roads.EdgeCount == 0)
            {
                return;
            }

            CompactGraph graph = roads.Graph;
            // Work on a copy: peeling is destructive and the assigned flow is reused
            // by the demand layer.
            var flow = (float[])roads.EdgeFlow.Clone();
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

            for (int r = 0; r < maxRoutes; r++)
            {
                if (!SuitabilityGraphMath.GrowCorridor(graph, flow, used, novelty, noveltyWeight, flowFloor, maxRouteLength, corridor))
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
                };

                for (int i = 0; i < corridor.Nodes.Count; i++)
                {
                    int node = corridor.Nodes[i];
                    route.Path.Add(new float2(roads.NodePositionsX[node], roads.NodePositionsZ[node]));
                }

                route.Mode = ClassifyMode(corridor.CapturedFlow, corridor.Length, meanFlow, route.Path, crossesWater);
                PlaceStops(route, StopSpacingFor(route.Mode), snapStop);
                routes.Add(route);

                SuitabilityGraphMath.PeelFlow(graph, corridor, flow, used, CaptureFraction);
                SuitabilityGraphMath.DecayNovelty(graph, corridor, novelty, NoveltyHops, NoveltyFactor);
            }

            routes.Sort((a, b) => b.CapturedFlow.CompareTo(a.CapturedFlow));
        }

        // Flow volume and length decide the mode: a long, heavily loaded corridor
        // wants rail, a light or short one wants a bus.
        private static Setting.ModePreset ClassifyMode(
            float capturedFlow,
            float length,
            float meanFlow,
            List<float2> path,
            System.Func<float2, float2, bool> crossesWater)
        {
            // Water UNDER the route is a bridge, not a reason to suggest a ferry —
            // the corridor follows roads by construction. What does suggest a ferry
            // is the route taking a long way round water that a direct crossing
            // would cut out.
            if (crossesWater != null && path.Count >= 2)
            {
                float direct = math.distance(path[0], path[path.Count - 1]);
                if (direct > 1f
                    && length / direct >= FerryDetourRatio
                    && crossesWater(path[0], path[path.Count - 1]))
                {
                    return Setting.ModePreset.Ferry;
                }
            }

            float intensity = length > 0f ? capturedFlow / math.max(1f, length / 1000f) : capturedFlow;
            float trunk = meanFlow * TrunkFlowMultiple;
            float medium = meanFlow * MediumFlowMultiple;

            if (intensity >= trunk)
            {
                return length >= TrunkLengthMetres ? Setting.ModePreset.Train : Setting.ModePreset.Metro;
            }

            if (intensity >= medium)
            {
                return Setting.ModePreset.Tram;
            }

            return Setting.ModePreset.Bus;
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

        // Walks the polyline dropping a stop every `spacing` metres, then lets the
        // caller nudge each one onto the best nearby tile. The flow decides where
        // the line runs; the suitability score already computed decides exactly
        // where each stop sits.
        private static void PlaceStops(SuggestedRoute route, float spacing, System.Func<float2, float2> snapStop)
        {
            route.Stops.Clear();
            if (route.Path.Count == 0)
            {
                return;
            }

            AddStop(route, route.Path[0], snapStop);

            float travelled = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                float2 from = route.Path[i - 1];
                float2 to = route.Path[i];
                float segment = math.distance(from, to);
                if (segment <= 0f)
                {
                    continue;
                }

                float position = 0f;
                while (travelled + (segment - position) >= spacing)
                {
                    position += spacing - travelled;
                    travelled = 0f;
                    float2 point = math.lerp(from, to, math.saturate(position / segment));
                    AddStop(route, point, snapStop);
                }

                travelled += segment - position;
            }

            // The far terminus is a stop even if it falls short of a full spacing.
            float2 last = route.Path[route.Path.Count - 1];
            if (route.Stops.Count == 0 || math.distance(route.Stops[route.Stops.Count - 1], last) > spacing * 0.4f)
            {
                AddStop(route, last, snapStop);
            }
        }

        private static void AddStop(SuggestedRoute route, float2 point, System.Func<float2, float2> snapStop)
        {
            float2 placed = snapStop != null ? snapStop(point) : point;

            // Snapping can pull two stops onto the same tile; keep them distinct.
            for (int i = 0; i < route.Stops.Count; i++)
            {
                if (math.distancesq(route.Stops[i], placed) < 1f)
                {
                    return;
                }
            }

            route.Stops.Add(placed);
        }
    }
}
