using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // What every stop plan of one route pass shares: the journeys' doors (two ends per
    // journey, each with the journey's weight), the game's fleet facts, the score
    // oracle that says whether a point can hold a stop at all, and the interchanges.
    internal sealed class StopContext
    {
        public float2Like[] Ends = Array.Empty<float2Like>();
        public float[] EndWeights = Array.Empty<float>();
        public FleetFacts Facts;
        public Func<float2Like, ModePreset, float>? ScoreAt;
        public InterchangeMap Hubs;
        private DoorIndex? m_Index;

        // The doors bucketed on a grid, built on first use: a pass plans stops for
        // thousands of alignments against tens of thousands of doors, and projecting
        // every door onto every alignment was minutes of work.
        public DoorIndex Index => m_Index ??= new DoorIndex(Ends);
    }

    // Doors on a square grid, answering "which doors could lie within `reach` of this
    // polyline" with a superset cheap enough to project exactly afterwards.
    internal sealed class DoorIndex
    {
        private const float CellMetres = 256f;
        private readonly Dictionary<long, List<int>> m_Cells = new Dictionary<long, List<int>>();
        private readonly int[] m_Stamp;
        private int m_Generation;

        public DoorIndex(float2Like[] doors)
        {
            m_Stamp = new int[doors.Length];
            for (int i = 0; i < doors.Length; i++)
            {
                long key = Key(Cell(doors[i].x), Cell(doors[i].y));
                if (!m_Cells.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>();
                    m_Cells.Add(key, list);
                }

                list.Add(i);
            }
        }

        private static int Cell(float v) => (int)Math.Floor(v / CellMetres);

        // The low word is the z cell's bit pattern; a negative cell wraps on purpose,
        // and the checked builds (tests, verification subject) need that said.
        private static long Key(int cx, int cz) => ((long)cx << 32) ^ unchecked((uint)cz);

        // Every door in a cell touched by a path segment's box grown by `reach`, once.
        public void Collect(List<float2Like> path, float reach, List<int> into)
        {
            into.Clear();
            m_Generation++;
            for (int i = 1; i < path.Count; i++)
            {
                float2Like a = path[i - 1];
                float2Like b = path[i];
                int x0 = Cell(Math.Min(a.x, b.x) - reach);
                int x1 = Cell(Math.Max(a.x, b.x) + reach);
                int z0 = Cell(Math.Min(a.y, b.y) - reach);
                int z1 = Cell(Math.Max(a.y, b.y) + reach);
                for (int cx = x0; cx <= x1; cx++)
                {
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        if (!m_Cells.TryGetValue(Key(cx, cz), out List<int>? list))
                        {
                            continue;
                        }

                        for (int k = 0; k < list.Count; k++)
                        {
                            int door = list[k];
                            if (m_Stamp[door] != m_Generation)
                            {
                                m_Stamp[door] = m_Generation;
                                into.Add(door);
                            }
                        }
                    }
                }
            }
        }
    }

    // F5 — where a line calls. The alignment is fixed; this half of SuitabilityRoutes
    // turns it into a stop plan problem (SuitabilityStopPlan) and reads the answer
    // back onto the route.
    internal static partial class SuitabilityRoutes
    {
        // A line passing this close to an existing served stop calls AT it rather than
        // beside it. Any player would put the stop at the station; doing otherwise
        // leaves two stops a short walk apart and no reason for either.
        private const float StationCallMetres = 150f;

        // Candidate stop positions along an alignment are this far apart; the stop plan
        // decides which of them are called at. Fine enough that a door is never more
        // than half a step from the position that would serve it best.
        private const float CandidateStepMetres = 50f;

        // Hard floor between consecutive stops: half the mode's nominal spacing
        // (register A5.1, decided 2026-09-05).
        private const float MinGapShareOfSpacing = 0.5f;
        // Two stops closer than this are the same stop: the along-line search can land
        // consecutive placements on nearly the same spot.
        private const float MinStopSeparationMetres = 20f;
        // Re-places stops after a mode change, since spacing, access horizon and the
        // delay a stop costs are all mode-specific.
        public static void Restop(SuggestedRoute route, ModePreset mode, StopContext stops)
        {
            route.Mode = mode;
            PlaceStops(route, mode, stops);
        }

        // Places the stops of a route by the stop plan (SuitabilityStopPlan, register
        // A5.1/A5.4): candidates every CandidateStepMetres along the polyline, the
        // termini and every interchange within StationCallMetres forced, anything the
        // score oracle says cannot hold a stop (open water for a ferry, unbuildable
        // ground) excluded; every journey door within the mode's access horizon is a
        // potential boarder, the corridor flow at a candidate its through-riders. The
        // path is then trimmed to the first and last call.
        internal static void PlaceStops(SuggestedRoute route, ModePreset mode, StopContext context)
        {
            route.Stops.Clear();
            route.StopPlan = null;
            route.StopPlanChosen = Array.Empty<int>();
            if (route.Path.Count < 2)
            {
                return;
            }

            float total = PathLength(route.Path);
            if (total <= 0f)
            {
                return;
            }

            StopPlanProblem? problem = BuildStopPlan(route, mode, total, context);
            if (problem is null)
            {
                return;
            }

            StopPlanSolution plan = SuitabilityStopPlan.Solve(problem);
            route.StopPlan = problem;
            route.StopPlanChosen = plan.Chosen;
            route.StopPlanGain = plan.Gain;
            route.StopPlanDelay = plan.Delay;

            float firstAt = -1f;
            float lastAt = -1f;
            for (int i = 0; i < plan.Count; i++)
            {
                int c = plan.Chosen[i];
                if (!AddStop(route, new float2Like(problem.CandidateX[c], problem.CandidateZ[c])))
                {
                    continue;
                }

                if (firstAt < 0f)
                {
                    firstAt = problem.CandidateAt[c] + problem.PathOffset;
                }

                lastAt = problem.CandidateAt[c] + problem.PathOffset;
            }

            // The line is drawn between its termini, not on past the last call.
            if (firstAt >= 0f && lastAt > firstAt)
            {
                TrimPath(route, firstAt, lastAt);
            }
        }

        private static StopPlanProblem? BuildStopPlan(SuggestedRoute route, ModePreset mode, float total, StopContext context)
        {
            float spacing = TransitModes.StopSpacingFor(mode);
            float horizon = TransitModes.CatchmentMs(mode) / 1000f;
            float reach = horizon * SuitabilityTransit.WalkSpeed;

            // Candidate positions: every step along the line, the far end always.
            var at = new List<float>();
            for (float offset = 0f; offset < total - (CandidateStepMetres * 0.5f); offset += CandidateStepMetres)
            {
                at.Add(offset);
            }

            at.Add(total);

            var points = new List<float2Like>(at.Count);
            var admissible = new List<bool>(at.Count);
            var hubDistance = new List<float>(at.Count);
            for (int i = 0; i < at.Count; i++)
            {
                float2Like point = PointAlong(route.Path, at[i]);
                points.Add(point);
                admissible.Add(context.ScoreAt is null || context.ScoreAt(point, mode) > 0f);
                hubDistance.Add(context.Hubs.Count > 0 && context.Hubs.TryNearest(point.x, point.y, StationCallMetres, out float distanceSq)
                    ? distanceSq
                    : float.MaxValue);
            }

            // Termini: the first and last positions that can hold a stop.
            int first = admissible.IndexOf(true);
            int last = admissible.LastIndexOf(true);
            if (first < 0 || last <= first)
            {
                return null;
            }

            // One forced call per interchange: within a run of candidates that see a
            // station, the nearest one. Forced calls keep the gap floor among
            // themselves; a second station inside the gap is served by the first.
            var mustCall = new bool[last - first + 1];
            int runBest = -1;
            for (int i = first; i <= last + 1; i++)
            {
                bool seesHub = i <= last && hubDistance[i] < float.MaxValue;
                if (seesHub)
                {
                    if (runBest < 0 || hubDistance[i] < hubDistance[runBest])
                    {
                        runBest = i;
                    }

                    continue;
                }

                if (runBest >= 0)
                {
                    mustCall[runBest - first] = true;
                    runBest = -1;
                }
            }

            float minGap = spacing * MinGapShareOfSpacing;
            float previousForced = float.NegativeInfinity;
            for (int i = 0; i < mustCall.Length; i++)
            {
                if (!mustCall[i])
                {
                    continue;
                }

                if (at[first + i] - previousForced < minGap)
                {
                    mustCall[i] = false;
                    continue;
                }

                previousForced = at[first + i];
            }

            // Positions that cannot hold a stop stay out of the plan; arc lengths are
            // kept exact, measured from the first terminus.
            var keep = new List<int>();
            for (int i = first; i <= last; i++)
            {
                if (admissible[i] || mustCall[i - first])
                {
                    keep.Add(i);
                }
            }

            var problem = new StopPlanProblem
            {
                CandidateCount = keep.Count,
                CandidateAt = new float[keep.Count],
                CandidateX = new float[keep.Count],
                CandidateZ = new float[keep.Count],
                MustCall = new bool[keep.Count],
                ThroughFlow = new float[keep.Count],
                MinGapMetres = minGap,
                DelaySecondsPerStop = context.Facts.DelayPerStopSeconds(mode),
                AccessHorizonSeconds = horizon,
                WalkMetresPerSecond = SuitabilityTransit.WalkSpeed,
                PathOffset = at[first],
            };
            for (int k = 0; k < keep.Count; k++)
            {
                int i = keep[k];
                problem.CandidateAt[k] = at[i] - at[first];
                problem.CandidateX[k] = points[i].x;
                problem.CandidateZ[k] = points[i].y;
                problem.MustCall[k] = mustCall[i - first];
                problem.ThroughFlow[k] = route.Source?.FlowNear(route.Nodes, points[i]) ?? route.CapturedFlow;
            }

            CollectEnds(route.Path, at[first], at[last], reach, context, problem);
            return problem;
        }

        // Every journey door within `reach` of the polyline between the two termini,
        // with the arc length of its projection (relative to the first terminus).
        private static void CollectEnds(List<float2Like> path, float fromAt, float toAt, float reach, StopContext context, StopPlanProblem into)
        {
            var endAt = new List<float>();
            var endX = new List<float>();
            var endZ = new List<float>();
            var endWeight = new List<float>();
            float reachSq = reach * reach;
            var nearby = new List<int>();
            context.Index.Collect(path, reach, nearby);
            for (int n = 0; n < nearby.Count; n++)
            {
                int e = nearby[n];
                float2Like door = context.Ends[e];
                float bestSq = float.MaxValue;
                float bestAt = 0f;
                float travelled = 0f;
                for (int i = 1; i < path.Count; i++)
                {
                    float2Like a = path[i - 1];
                    float2Like b = path[i];
                    float segment = float2Like.Distance(a, b);
                    if (segment > 0f)
                    {
                        float t = SuitabilityScoring.Saturate(float2Like.Dot(door - a, b - a) / (segment * segment));
                        float distSq = float2Like.DistanceSq(door, float2Like.Lerp(a, b, t));
                        if (distSq < bestSq)
                        {
                            bestSq = distSq;
                            bestAt = travelled + (t * segment);
                        }
                    }

                    travelled += segment;
                }

                if (bestSq > reachSq || bestAt < fromAt || bestAt > toAt)
                {
                    continue;
                }

                endAt.Add(bestAt - fromAt);
                endX.Add(door.x);
                endZ.Add(door.y);
                endWeight.Add(context.EndWeights[e]);
            }

            into.EndCount = endAt.Count;
            into.EndAt = endAt.ToArray();
            into.EndX = endX.ToArray();
            into.EndZ = endZ.ToArray();
            into.EndWeight = endWeight.ToArray();
        }

        private static void TrimPath(SuggestedRoute route, float from, float to)
        {
            var trimmed = new List<float2Like> { PointAlong(route.Path, from) };

            float travelled = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                float segment = float2Like.Distance(route.Path[i - 1], route.Path[i]);
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
                length += float2Like.Distance(route.Path[i - 1], route.Path[i]);
            }

            route.Length = length;
        }

        // Position at `distance` along the polyline.
        private static float2Like PointAlong(List<float2Like> path, float distance)
        {
            float travelled = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                float segment = float2Like.Distance(path[i - 1], path[i]);
                if (segment <= 0f)
                {
                    continue;
                }

                if (travelled + segment >= distance)
                {
                    float t = (distance - travelled) / segment;
                    return float2Like.Lerp(path[i - 1], path[i], SuitabilityScoring.Saturate(t));
                }

                travelled += segment;
            }

            return path[path.Count - 1];
        }

        private static bool AddStop(SuggestedRoute route, float2Like placed)
        {
            float minSeparationSq = MinStopSeparationMetres * MinStopSeparationMetres;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                if (float2Like.DistanceSq(route.Stops[i], placed) < minSeparationSq)
                {
                    return false;
                }
            }

            route.Stops.Add(placed);
            return true;
        }
    }
}
