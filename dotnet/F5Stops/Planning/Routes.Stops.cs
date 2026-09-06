using System;
using System.Collections.Generic;

namespace TransitArchitect
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

    // F5 — where a line calls. The alignment is fixed; this half of Routes
    // turns it into a stop plan problem (StopPlanning) and reads the answer
    // back onto the route.
    internal static partial class Routes
    {

        // Re-places stops after a mode change, since spacing, access horizon and the
        // delay a stop costs are all mode-specific.
        public static void Restop(SuggestedRoute route, ModePreset mode, StopContext stops)
        {
            route.Mode = mode;
            PlaceStops(route, mode, stops);
        }

        // Places the stops of a route by the stop plan (StopPlanning, register
        // A5.1/A5.4): candidates every Assumptions.CandidateStepMetres along the polyline, the
        // termini and every interchange within Assumptions.StationCallMetres forced, anything the
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

            StopPlanSolution plan = StopPlanning.Solve(problem);
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
            float spacing = Assumptions.StopSpacingFor(mode);
            float horizon = Assumptions.CatchmentMs(mode) / 1000f;
            float reach = horizon * Assumptions.WalkSpeed;

            // Candidate positions: every step along the line, the far end always.
            var at = new List<float>();
            for (float offset = 0f; offset < total - (Assumptions.CandidateStepMetres * 0.5f); offset += Assumptions.CandidateStepMetres)
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
                float hubSq = context.Hubs.Count > 0
                    && context.Hubs.TryNearest(point.x, point.y, Assumptions.StationCallMetres, out float distanceSq)
                    ? distanceSq
                    : float.MaxValue;
                hubDistance.Add(hubSq);

                // An existing station makes a candidate admissible on its own. The
                // station IS the demand: the ground around a terminus is often empty —
                // that is why it is a terminus — and scoring it on its neighbours alone
                // said "nothing here". Corridors were being extended onto an interchange
                // (55 of 93 ends on Valmare) and then trimmed straight back off it,
                // because the trim runs over the ADMISSIBLE candidates and the forced
                // call is only looked for between them. The line then ended a couple of
                // hundred metres short of the station it was aimed at.
                //
                // Not on water. There the score oracle is not measuring demand, it is
                // measuring whether the point is land at all (ShorelineScoreAt), and
                // overriding it would put a pier mid-crossing.
                bool atStation = hubSq < float.MaxValue && route.Network != RouteNetwork.Water;

                // Two independent reasons a candidate cannot hold a stop. The score
                // oracle rules out ground nothing could stand on (open water for a
                // ferry, unbuildable land); the path flags rule out ground the game
                // itself forbids — a motorway a line may drive along but never call on.
                // The second is not a matter of degree, so it is checked first.
                admissible.Add(CanHostStop(route, at[i])
                    && (atStation || context.ScoreAt is null || context.ScoreAt(point, mode) > 0f));
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

            float minGap = spacing * Assumptions.MinGapShareOfSpacing;
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
                WalkMetresPerSecond = Assumptions.WalkSpeed,
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
            // The unstoppable flags are indexed by segment, so they have to be trimmed
            // with the path rather than left describing the corridor it used to be. A
            // Restop after a mode change reads them again.
            List<bool> flags = route.PathCannotHostStops;
            bool hadFlags = flags.Count > 0;
            var trimmedFlags = new List<bool>();

            float travelled = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                float segment = float2Like.Distance(route.Path[i - 1], route.Path[i]);
                float at = travelled + segment;
                bool flag = hadFlags && i - 1 < flags.Count && flags[i - 1];
                if (at > from && at < to)
                {
                    trimmed.Add(route.Path[i]);
                    trimmedFlags.Add(flag);
                }
                else if (at >= to)
                {
                    // The segment the trim ends inside: its flag governs the last kept
                    // stretch, and there is exactly one of it.
                    trimmedFlags.Add(flag);
                    break;
                }

                travelled = at;
            }

            trimmed.Add(PointAlong(route.Path, to));

            route.Path.Clear();
            for (int i = 0; i < trimmed.Count; i++)
            {
                route.Path.Add(trimmed[i]);
            }

            flags.Clear();
            if (hadFlags)
            {
                // One flag per segment of the trimmed path. The loop above can fall
                // short of that when the trim starts inside a segment, so the last flag
                // is repeated rather than leaving a segment unlabelled — an unlabelled
                // segment reads as stoppable, which is the wrong way to be wrong.
                for (int i = 0; i + 1 < route.Path.Count; i++)
                {
                    flags.Add(i < trimmedFlags.Count
                        ? trimmedFlags[i]
                        : trimmedFlags.Count > 0 && trimmedFlags[trimmedFlags.Count - 1]);
                }
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
        // Whether the stretch of path at `distance` along it may hold a stop. Walks the
        // same segments PointAlong does, so the two cannot disagree about which segment
        // a candidate falls in. True when the network reported nothing, which is every
        // lattice and every water alignment: no road classes, so no motorways.
        private static bool CanHostStop(SuggestedRoute route, float distance)
        {
            List<bool> flags = route.PathCannotHostStops;
            if (flags.Count == 0)
            {
                return true;
            }

            float travelled = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                float segment = float2Like.Distance(route.Path[i - 1], route.Path[i]);
                if (segment <= 0f)
                {
                    continue;
                }

                if (travelled + segment >= distance)
                {
                    return i - 1 >= flags.Count || !flags[i - 1];
                }

                travelled += segment;
            }

            return flags.Count == 0 || !flags[flags.Count - 1];
        }

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
            float minSeparationSq = Assumptions.MinStopSeparationMetres * Assumptions.MinStopSeparationMetres;
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
