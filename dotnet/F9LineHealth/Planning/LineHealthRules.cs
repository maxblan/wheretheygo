using System;
using System.Collections.Generic;
using System.Globalization;

namespace TransitArchitect
{
    // What is wrong with an existing line, as the recommendation names it (register
    // A8, decided 2026-09-06). One verdict per line, the first that applies in this
    // order; the plan behind it carries every number (LineHealth).
    internal enum LineVerdict
    {
        Healthy = 0,
        // The game is short of the fleet the player asked for: vehicles requested and
        // not delivered (TransportLineFlags.NotEnoughVehicles / RequireVehicles). A
        // supply problem — depot, money — not a statement about demand.
        FleetShort = 1,
        // The load does not fit the largest fleet the game allows this mode: the next
        // mode up the network's ladder is the answer.
        ModeUp = 2,
        // Not even the largest mode of the ladder carries the load at its maximum
        // fleet: the line has to be split.
        SplitRoute = 3,
        // Empty by the readings AND below the utilisation floor even as the smallest
        // mode at the fewest vehicles the game allows: reroute or remove.
        Remove = 4,
        // A smaller mode carries the load within its own span.
        ModeDown = 5,
        // More vehicles than today, within the span.
        FleetUp = 6,
        // Fewer vehicles than today: the load fits a smaller fleet.
        FleetDown = 7,
        // The fleet is right, but one period falls under the floor while the other
        // does not: run the line by day or by night only (or all day again).
        Schedule = 8,
    }

    // The measurements behind one verdict and the plan they lead to. Kept together so
    // the panel can show the numbers next to the recommendation — a remedy the player
    // cannot check is not worth much.
    internal struct LineHealth
    {
        public int m_Index;
        // Stable per-line identity, unaffected by the list being re-sorted worst-first.
        public int m_Id;
        // The line entity's ECS index, for joining onto the vanilla overview's rows.
        public int m_EntityIndex;
        // The game's own display name for the line.
        public string m_Name;
        public ModePreset m_Mode;
        public int m_Vehicles;
        // The fleet the game itself is aiming at (its formula on the line's target
        // interval and round trip), and how many are missing when the game says so.
        public int m_TargetVehicles;
        public int m_MissingVehicles;
        public int m_Passengers;
        public int m_Capacity;
        // Mean occupancy over the window's active readings once enough back it,
        // otherwise the reading at collection; the busiest reading beside it; and the
        // planning load in riders (the window's quantile, A8.4). m_Passengers and
        // m_Capacity stay the instantaneous counts.
        public float m_Usage;
        public float m_PeakUsage;
        public int m_PlanningLoad;
        // How many readings the verdict rests on and how much game time they span.
        // Zero samples means the verdict came from a single reading.
        public int m_WindowSamples;
        public float m_WindowGameHours;
        public float m_LengthKm;
        public int m_Stops;
        public LineVerdict m_Verdict;
        // The plan: the mode the ladder settles on, the fleet within the game's span
        // for that mode's round trip, the interval that fleet yields.
        public ModePreset m_RecommendedMode;
        public int m_RecommendedFleet;
        public int m_FleetMin;
        public int m_FleetMax;
        public float m_RoundTripSeconds;
        public float m_HeadwaySeconds;
        // The demand the routing attributes to the line (negative = no pass yet) and
        // its utilisation at the recommended fleet.
        public float m_RidersPerDay;
        public float m_Utilisation;
        // The schedule the line runs today and the one its demand argues for (the
        // same when nothing argues; Daytime.Recommend on the period utilisations at
        // the recommended fleet), with the measured period occupancies as evidence.
        public LineSchedule m_Schedule;
        public LineSchedule m_ScheduleAdvice;
        public float m_DayUtilisation;
        public float m_NightUtilisation;
        public float m_DayUsage;
        public float m_NightUsage;
        public int m_DaySamples;
        public int m_NightSamples;

        public readonly bool HasDemand => m_RidersPerDay >= 0f;

        public readonly int Severity
        {
            get
            {
                switch (m_Verdict)
                {
                    case LineVerdict.ModeUp:
                    case LineVerdict.SplitRoute:
                        return 4;
                    case LineVerdict.FleetShort:
                    case LineVerdict.FleetUp:
                        return 3;
                    case LineVerdict.Remove:
                        return 2;
                    case LineVerdict.ModeDown:
                    case LineVerdict.FleetDown:
                    case LineVerdict.Schedule:
                        return 1;
                    default:
                        return 0;
                }
            }
        }
    }

    // Everything a line-health pass reads, captured at one instant so the export and
    // the offline subject see exactly what the verdicts were drawn from: the lines as
    // collected (with the routing's riders attached), the window's readings per line,
    // the prefab facts and slider policy, and the floors.
    internal sealed class LineHealthProblem
    {
        public List<ExistingLine> Lines = new List<ExistingLine>();
        public Dictionary<int, LineObservation[]> Samples = new Dictionary<int, LineObservation[]>();
        public FleetFacts Facts;
        public float UtilisationFloor;
        public float UtilisationCeiling;
        public float TargetLoad;
        public uint WindowFrames;
    }

    // The city-wide reference the relative "empty" bar is measured against.
    internal struct HealthReference
    {
        public float m_MedianUsage;
        public float m_EmptyThreshold;
    }

    // Judging a line's health and planning its remedy (formal-specification.md §7e,
    // register A8.1–A8.6). Pure so the thresholds and the plan can be tested and
    // verified rather than eyeballed in game.
    internal static class LineHealthRules
    {
        // Snapshots the inputs of one pass: the samples are copied so a reading taken
        // after the judgement cannot change what the export describes.
        public static LineHealthProblem Capture(List<ExistingLine> lines, LineHistory history, FleetFacts facts, float utilisationFloor, float utilisationCeiling, float targetLoad)
        {
            var problem = new LineHealthProblem
            {
                Facts = facts,
                UtilisationFloor = utilisationFloor,
                UtilisationCeiling = utilisationCeiling,
                TargetLoad = targetLoad,
                WindowFrames = history.WindowFrames,
            };
            problem.Lines.AddRange(lines);
            for (int i = 0; i < lines.Count; i++)
            {
                IReadOnlyList<LineObservation> samples = history.SamplesOf(lines[i].m_Id);
                var copy = new LineObservation[samples.Count];
                for (int s = 0; s < copy.Length; s++)
                {
                    copy[s] = samples[s];
                }

                problem.Samples[lines[i].m_Id] = copy;
            }

            return problem;
        }

        // Rebuilds the window from the captured readings — in frame order across lines,
        // as the save restore does, because Record treats an older frame as a rewound
        // clock — and hands every line its averages, whole-window and per period.
        public static void ApplyWindow(LineHealthProblem problem)
        {
            var history = new LineHistory(problem.WindowFrames);
            var flat = new List<(int line, LineObservation sample)>();
            foreach (KeyValuePair<int, LineObservation[]> entry in problem.Samples)
            {
                for (int i = 0; i < entry.Value.Length; i++)
                {
                    flat.Add((entry.Key, entry.Value[i]));
                }
            }

            flat.Sort(static (a, b) =>
            {
                int byFrame = a.sample.m_Frame.CompareTo(b.sample.m_Frame);
                return byFrame != 0 ? byFrame : a.line.CompareTo(b.line);
            });
            for (int i = 0; i < flat.Count; i++)
            {
                history.Record(flat[i].line, flat[i].sample);
            }

            for (int i = 0; i < problem.Lines.Count; i++)
            {
                ApplyWindow(history, problem.Lines[i]);
            }
        }

        public static void ApplyWindow(LineHistory history, ExistingLine line)
        {
            if (!history.TryAverage(line.m_Id, out LineAverage average))
            {
                line.m_WindowSamples = 0;
                line.m_WindowReadings = 0;
                line.m_WindowGameHours = 0f;
                line.m_DaySamples = 0;
                line.m_NightSamples = 0;
                return;
            }

            line.m_WindowUsage = average.m_Usage;
            line.m_WindowPeakUsage = average.m_PeakUsage;
            line.m_WindowPlanningLoad = average.m_PlanningLoad;
            line.m_WindowMaxAboard = average.m_MaxAboard;
            line.m_WindowInterval = average.m_IntervalSeconds;
            line.m_WindowSamples = average.m_Samples;
            line.m_WindowReadings = average.m_Readings;
            line.m_WindowGameHours = LineHistory.GameHours(average.m_SpanFrames);
            line.m_DayUsage = history.TryAveragePeriod(line.m_Id, night: false, out LineAverage day) ? day.m_Usage : 0f;
            line.m_DaySamples = day.m_Samples;
            line.m_NightUsage = history.TryAveragePeriod(line.m_Id, night: true, out LineAverage night) ? night.m_Usage : 0f;
            line.m_NightSamples = night.m_Samples;
        }

        // The upper median: the value at index n/2 of the sorted list, so with an even
        // count the larger of the two middle values. At least ⌈n/2⌉ lines are at or
        // above it, which is what bounds the relative bars (Lean: Verify.LineHealth).
        public static float UpperMedian(List<float> values)
        {
            if (values.Count == 0)
            {
                return 0f;
            }

            var sorted = new List<float>(values);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        // "Nearly empty" cannot be an absolute share of capacity (A8.1): occupancy is a
        // count of riders aboard against total fleet capacity, and on a working city
        // almost every healthy line sits between 1 % and 6 % of that. The city's own
        // median is the reference: a line is empty relative to how busy this city's
        // transit actually runs, and by construction at most half of them can be.
        public static HealthReference ReferenceOf(List<ExistingLine> lines)
        {
            var usages = new List<float>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                usages.Add(lines[i].Usage);
            }

            float median = UpperMedian(usages);
            return new HealthReference
            {
                m_MedianUsage = median,
                m_EmptyThreshold = Math.Min(Assumptions.EmptyUsage, median * Assumptions.EmptyShareOfMedian),
            };
        }

        // Every line judged and planned, worst first.
        public static HealthReference JudgeAll(LineHealthProblem problem, List<LineHealth> health)
        {
            ApplyWindow(problem);
            HealthReference reference = ReferenceOf(problem.Lines);
            health.Clear();
            for (int i = 0; i < problem.Lines.Count; i++)
            {
                health.Add(Assess(problem.Lines[i], i + 1, problem, reference));
            }

            health.Sort(static (a, b) =>
            {
                int bySeverity = b.Severity.CompareTo(a.Severity);
                return bySeverity != 0 ? bySeverity : b.m_Usage.CompareTo(a.m_Usage);
            });
            return reference;
        }

        // One rung of the ladder as it would run this line: the seats of its vehicle,
        // the round trip, the span the game allows, and the fleet the load and the
        // demand require of it.
        internal struct Rung
        {
            public ModePreset Mode;
            public float Capacity;
            public float RoundTripSeconds;
            public int Min;
            public int Max;
            public int Required;
            // Whether a vehicle of the mode is installed at all.
            public bool Available;
        }

        // The seats of one vehicle for a rung: what the line actually runs for its own
        // mode, the prefab's largest vehicle for any other (and for its own when nothing
        // is out).
        public static float CapacityFor(ExistingLine line, ModePreset mode, FleetFacts facts)
        {
            return mode == line.m_Mode && line.CapacityPerVehicle > 0 ? line.CapacityPerVehicle : facts.CapacityFor(mode);
        }

        // The round trip a rung would take: the game's own stable duration for the
        // line's mode, the loop at the mode's cruise speed with its dwell otherwise.
        public static float RoundTripFor(ExistingLine line, ModePreset mode, FleetFacts facts)
        {
            return mode == line.m_Mode && line.m_StableDurationSeconds > 0f
                ? line.m_StableDurationSeconds
                : TransitModes.RoundTripSeconds(line.m_LengthMetres, line.m_StopIndices.Count, Assumptions.CruiseSpeedFor(mode), facts.DelayPerStopSeconds(mode));
        }

        public static Rung RungFor(ExistingLine line, ModePreset mode, LineHealthProblem problem)
        {
            var rung = new Rung { Mode = mode, Capacity = CapacityFor(line, mode, problem.Facts) };
            rung.Available = rung.Capacity > 0f;
            if (!rung.Available)
            {
                return rung;
            }

            rung.RoundTripSeconds = RoundTripFor(line, mode, problem.Facts);
            problem.Facts.FleetSpanFor(mode, rung.RoundTripSeconds, out rung.Min, out rung.Max);
            int forLoad = TransitModes.FleetForLoad(line.PlanningLoad, rung.Capacity, problem.TargetLoad);
            int forDemand = line.HasDemand
                ? TransitModes.FleetForDemandByPeriod(line.m_RidersPerDay, line.m_RidersByDay, line.m_RidersByNight, rung.RoundTripSeconds, rung.Capacity, problem.UtilisationCeiling)
                : 1;
            rung.Required = Math.Max(forLoad, forDemand);
            return rung;
        }

        // Climbs the network's ladder (A6.x for existing lines too, A8.2): the first
        // mode whose largest allowed fleet carries what the line needs; the largest
        // installed mode when none does (`split` then), the line's own mode when the
        // ladder has no vehicle at all. A rung BELOW the line's mode only qualifies when
        // it needs no more vehicles than run today (A6.9, user decision 2026-09-06
        // question 1a): five trams were being told to become six to twelve buses because
        // the bus span reached that far. `smallest` is the first installed rung, the one
        // the utilisation floor is asked of.
        public static Rung ClimbLadder(ExistingLine line, LineHealthProblem problem, out Rung smallest, out bool split, out int currentIndex, out int chosenIndex)
        {
            ModePreset[] ladder = TransitModes.ModesFor(TransitModes.NetworkOf(line.m_Mode));
            currentIndex = Math.Max(0, Array.IndexOf(ladder, line.m_Mode));
            Rung chosen = default;
            smallest = default;
            bool any = false;
            split = false;
            chosenIndex = currentIndex;
            for (int i = 0; i < ladder.Length; i++)
            {
                Rung rung = RungFor(line, ladder[i], problem);
                if (!rung.Available)
                {
                    continue;
                }

                if (!any)
                {
                    smallest = rung;
                    any = true;
                }

                chosen = rung;
                chosenIndex = i;
                bool noMoreVehiclesThanToday = i >= currentIndex || rung.Required <= line.m_Vehicles;
                if (rung.Required <= rung.Max && noMoreVehiclesThanToday)
                {
                    return chosen;
                }
            }

            if (!any)
            {
                // No vehicle prefab of the ladder is loaded: judge the line as it runs.
                chosen = RungFor(line, line.m_Mode, problem);
                chosen.Available = true;
                chosen.Capacity = line.CapacityPerVehicle;
                chosen.Min = 1;
                chosen.Max = int.MaxValue;
                chosen.Required = Math.Max(1, line.m_Vehicles);
                smallest = chosen;
                chosenIndex = currentIndex;
                return chosen;
            }

            split = true;
            return chosen;
        }

        // The verdict and plan for one line.
        public static LineHealth Assess(ExistingLine line, int index, LineHealthProblem problem, HealthReference reference)
        {
            Rung rung = ClimbLadder(line, problem, out Rung smallest, out bool split, out int currentIndex, out int chosenIndex);
            int fleet = TransitModes.Clamp(rung.Required, rung.Min, rung.Max);
            float headway = TransitModes.GameInterval(rung.RoundTripSeconds, fleet);
            int gameTarget = line.m_StableDurationSeconds > 0f && line.m_TargetInterval > 0f
                ? TransitModes.GameFleet(line.m_TargetInterval, line.m_StableDurationSeconds)
                : Math.Max(1, line.m_Vehicles);

            var health = new LineHealth
            {
                m_Index = index,
                m_Id = line.m_Id,
                m_EntityIndex = line.m_EntityIndex,
                m_Name = line.m_Name,
                m_Mode = line.m_Mode,
                m_Vehicles = line.m_Vehicles,
                m_TargetVehicles = gameTarget,
                m_MissingVehicles = Math.Max(0, gameTarget - line.m_Vehicles),
                m_Passengers = line.m_Passengers,
                m_Capacity = line.m_Capacity,
                m_Usage = line.Usage,
                m_PeakUsage = line.PeakUsage,
                m_PlanningLoad = line.PlanningLoad,
                m_WindowSamples = line.HasWindow ? line.m_WindowSamples : 0,
                m_WindowGameHours = line.m_WindowGameHours,
                m_LengthKm = line.m_LengthMetres / 1000f,
                m_Stops = line.m_StopIndices.Count,
                m_RecommendedMode = rung.Mode,
                m_RecommendedFleet = fleet,
                m_FleetMin = rung.Min,
                m_FleetMax = rung.Max,
                m_RoundTripSeconds = rung.RoundTripSeconds,
                m_HeadwaySeconds = headway,
                m_RidersPerDay = line.m_RidersPerDay,
                m_Utilisation = line.HasDemand ? Equity.Utilisation(line.m_RidersPerDay, headway, rung.Capacity) : -1f,
                m_Schedule = line.m_Schedule,
                m_ScheduleAdvice = line.m_Schedule,
                m_DayUsage = line.m_DayUsage,
                m_NightUsage = line.m_NightUsage,
                m_DaySamples = line.m_DaySamples,
                m_NightSamples = line.m_NightSamples,
            };
            if (line.HasDemand)
            {
                health.m_DayUtilisation = Daytime.UtilisationInPeriod(line.m_RidersByDay, headway, rung.Capacity, Assumptions.DayShareOfDay);
                health.m_NightUtilisation = Daytime.UtilisationInPeriod(line.m_RidersByNight, headway, rung.Capacity, 1f - Assumptions.DayShareOfDay);
                health.m_ScheduleAdvice = Daytime.Advise(line.m_Schedule, health.m_DayUtilisation, health.m_NightUtilisation, problem.UtilisationFloor);
            }

            health.m_Verdict = VerdictOf(line, health, problem, reference, smallest, split, currentIndex, chosenIndex);
            return health;
        }

        // The first rule that applies, in the order LineVerdict lists them.
        private static LineVerdict VerdictOf(ExistingLine line, LineHealth health, LineHealthProblem problem, HealthReference reference, Rung smallest, bool split, int currentIndex, int chosenIndex)
        {
            if (line.m_NotEnoughVehicles || (line.m_RequireVehicles && line.m_Vehicles < health.m_TargetVehicles))
            {
                return LineVerdict.FleetShort;
            }

            if (chosenIndex > currentIndex)
            {
                return LineVerdict.ModeUp;
            }

            if (split)
            {
                return LineVerdict.SplitRoute;
            }

            // "Reroute or remove" is the most destructive advice this mod gives, so it
            // takes two independent signals (A8.6): the readings say empty — mean under
            // the relative bar AND the busiest reading under three times it — and the
            // routed demand would not reach the floor even as the smallest installed
            // mode at the fewest vehicles the game allows it.
            bool measuredEmpty = health.m_Usage <= reference.m_EmptyThreshold
                && health.m_PeakUsage <= reference.m_EmptyThreshold * Assumptions.EmptyPeakAllowance;
            if (measuredEmpty && line.HasDemand && smallest.Available)
            {
                float atFewest = Equity.Utilisation(line.m_RidersPerDay, TransitModes.GameInterval(smallest.RoundTripSeconds, smallest.Min), smallest.Capacity);
                if (atFewest < problem.UtilisationFloor)
                {
                    return LineVerdict.Remove;
                }
            }

            if (chosenIndex < currentIndex)
            {
                return LineVerdict.ModeDown;
            }

            if (health.m_RecommendedFleet > line.m_Vehicles)
            {
                return LineVerdict.FleetUp;
            }

            if (health.m_RecommendedFleet < line.m_Vehicles)
            {
                return LineVerdict.FleetDown;
            }

            return health.m_ScheduleAdvice != line.m_Schedule ? LineVerdict.Schedule : LineVerdict.Healthy;
        }

        // What is wrong with a line's SHAPE, as a token the panel can translate.
        internal enum PlanShape
        {
            Fine = 0,
            Split = 1,
            Reroute = 2,
        }

        // A remedy, as numbers and tokens rather than a sentence. Improve() renders the
        // English version for the log; the panel renders the player's language from the
        // same values, so the two can never drift.
        internal struct ImprovePlan
        {
            public ModePreset m_Mode;
            public int m_Vehicles;
            // Signed change against the fleet running today; 0 means leave it alone.
            public int m_VehicleDelta;
            // Interval the recommended fleet yields, as the game would derive it.
            // Negative when the round trip is unknown, which is the only case where
            // there is no number to quote.
            public int m_IntervalSeconds;
            public PlanShape m_Shape;
            // Shape argument: km for Split.
            public float m_ShapeValue;
            // The span the game allows the recommended mode on this line.
            public int m_FleetMin;
            public int m_FleetMax;
        }

        // The plan is the verdict's own numbers: the ladder's mode, the fleet within the
        // span, the interval that fleet yields, and the shape token.
        public static ImprovePlan Plan(LineHealth health)
        {
            return new ImprovePlan
            {
                m_Mode = health.m_RecommendedMode,
                m_Vehicles = health.m_RecommendedFleet,
                m_VehicleDelta = health.m_RecommendedFleet - health.m_Vehicles,
                m_IntervalSeconds = health.m_RoundTripSeconds > 0f
                    ? (int)Math.Round(health.m_HeadwaySeconds, MidpointRounding.AwayFromZero)
                    : -1,
                m_Shape = health.m_Verdict == LineVerdict.SplitRoute ? PlanShape.Split
                    : health.m_Verdict == LineVerdict.Remove ? PlanShape.Reroute
                    : PlanShape.Fine,
                m_ShapeValue = health.m_LengthKm,
                m_FleetMin = health.m_FleetMin,
                m_FleetMax = health.m_FleetMax,
            };
        }

        // English rendering of a plan, for the log. The panel builds its own from the
        // same ImprovePlan, so the two cannot disagree about the numbers.
        public static string Improve(LineHealth health)
        {
            ImprovePlan plan = Plan(health);
            var parts = new System.Text.StringBuilder();
            _ = parts.Append("run it as ").Append(plan.m_Mode).Append(" with ").Append(plan.m_Vehicles).Append(" vehicle(s)");
            if (plan.m_VehicleDelta != 0)
            {
                _ = parts.Append(plan.m_VehicleDelta > 0 ? " (+" : " (").Append(plan.m_VehicleDelta).Append(')');
            }

            _ = parts.Append(" of the ").Append(plan.m_FleetMin.ToString(CultureInfo.InvariantCulture)).Append("..")
                .Append(plan.m_FleetMax == int.MaxValue ? "?" : plan.m_FleetMax.ToString(CultureInfo.InvariantCulture)).Append(" the game allows");
            if (plan.m_IntervalSeconds >= 0)
            {
                _ = parts.Append(", i.e. an interval of about ").Append(plan.m_IntervalSeconds.ToString(CultureInfo.InvariantCulture)).Append(" s");
            }

            switch (plan.m_Shape)
            {
                case PlanShape.Split:
                    _ = parts.Append("; split it — ").Append(plan.m_ShapeValue.ToString("F1", CultureInfo.InvariantCulture))
                        .Append(" km is more than the largest ").Append(plan.m_Mode).Append(" fleet can carry");
                    break;
                case PlanShape.Reroute:
                    _ = parts.Append("; or reroute it through denser ground — the suggestions list shows where demand is unserved");
                    break;
                default:
                    _ = parts.Append("; the route shape looks reasonable");
                    break;
            }

            return parts.ToString();
        }

        // The same plan as the delimited payload the panel renders:
        // mode|vehicles|delta|interval|shape|value|fleetMin|fleetMax.
        public static string PlanPayload(ImprovePlan plan)
        {
            return string.Join("|", new[]
            {
                plan.m_Mode.ToString(),
                plan.m_Vehicles.ToString(CultureInfo.InvariantCulture),
                plan.m_VehicleDelta.ToString(CultureInfo.InvariantCulture),
                plan.m_IntervalSeconds.ToString(CultureInfo.InvariantCulture),
                plan.m_Shape.ToString(),
                plan.m_ShapeValue.ToString("F1", CultureInfo.InvariantCulture),
                plan.m_FleetMin.ToString(CultureInfo.InvariantCulture),
                plan.m_FleetMax == int.MaxValue ? "0" : plan.m_FleetMax.ToString(CultureInfo.InvariantCulture),
            });
        }

        // The one variable piece of a verdict: vehicles missing or to add, the mode to
        // change to, the schedule to switch to. Empty when the verdict takes no
        // argument. Kept apart from Describe so the panel can substitute it into a
        // TRANSLATED sentence instead of receiving English prose it cannot localise.
        public static string VerdictArgument(LineHealth health)
        {
            switch (health.m_Verdict)
            {
                case LineVerdict.FleetShort:
                    return health.m_MissingVehicles.ToString(CultureInfo.InvariantCulture);
                case LineVerdict.ModeUp:
                case LineVerdict.ModeDown:
                    return health.m_RecommendedMode.ToString();
                case LineVerdict.FleetUp:
                case LineVerdict.FleetDown:
                    return (health.m_RecommendedFleet - health.m_Vehicles).ToString(CultureInfo.InvariantCulture);
                case LineVerdict.Schedule:
                    return health.m_ScheduleAdvice.ToString();
                default:
                    return string.Empty;
            }
        }

        // English prose for the LOG. The panel builds its own text from the verdict
        // token and VerdictArgument above.
        public static string Describe(LineHealth health)
        {
            string argument = VerdictArgument(health);
            switch (health.m_Verdict)
            {
                case LineVerdict.FleetShort:
                    return $"fleet short — the game wants {argument} more vehicle(s) than it can supply";
                case LineVerdict.ModeUp:
                    return $"too big for its mode — upgrade to {argument}";
                case LineVerdict.SplitRoute:
                    return "beyond the largest fleet of any mode — split the route";
                case LineVerdict.Remove:
                    return "empty and unjustified even as the smallest service — reroute or remove";
                case LineVerdict.ModeDown:
                    return $"a smaller vehicle would do — run it as {argument}";
                case LineVerdict.FleetUp:
                    return $"add {argument} vehicle(s)";
                case LineVerdict.FleetDown:
                    return $"remove {(-(health.m_RecommendedFleet - health.m_Vehicles)).ToString(CultureInfo.InvariantCulture)} vehicle(s)";
                case LineVerdict.Schedule:
                    return $"run it {argument}";
                default:
                    return "healthy";
            }
        }
    }
}
