using System;
using System.Globalization;

namespace StationSuitabilityOverlay
{
    internal enum LineVerdict
    {
        Healthy = 0,
        // Demand exceeds what the current fleet carries, and more vehicles would help.
        Overcrowded = 1,
        // Full even with the fleet the interval allows: the mode itself is the limit.
        AtModeCapacity = 2,
        // People wait a long time although there is spare room aboard.
        LongWaits = 3,
        // Barely used.
        NearlyEmpty = 4,
    }

    // The measurements behind one verdict. Kept together so the panel can show the
    // numbers next to the recommendation — a remedy the player cannot check is not
    // worth much.
    internal struct LineHealth
    {
        public int m_Index;
        // Stable per-line identity, unaffected by the list being re-sorted worst-first.
        public int m_Id;
        // The game's own display name for the line.
        public string m_Name;
        public ModePreset m_Mode;
        public int m_Vehicles;
        public int m_TargetVehicles;
        public int m_Passengers;
        public int m_Capacity;
        // Share of fleet capacity in use: the mean over the rolling window once enough
        // readings back it, otherwise the reading at collection. m_Passengers and
        // m_Capacity stay the instantaneous counts, so the two do not divide into each
        // other and anything showing both must say which is which.
        public float m_Usage;
        // How many readings the verdict rests on and how much game time they span.
        // Zero samples means the verdict came from a single reading.
        public int m_WindowSamples;
        public float m_WindowGameHours;
        public float m_PeakUsage;
        // Half the achieved headway: what a rider turning up at random waits.
        public float m_TypicalWait;
        public float m_LengthKm;
        public int m_Stops;
        public LineVerdict m_Verdict;
        public int m_AddVehicles;
        // The schedule the line runs today and the one its readings argue for (the
        // same when nothing argues; Daytime.Advise), with the evidence.
        public LineSchedule m_Schedule;
        public LineSchedule m_ScheduleAdvice;
        public float m_DayUsage;
        public float m_NightUsage;
        public int m_DaySamples;
        public int m_NightSamples;

        public readonly int Severity
        {
            get
            {
                switch (m_Verdict)
                {
                    case LineVerdict.AtModeCapacity: return 3;
                    case LineVerdict.Overcrowded: return 2;
                    case LineVerdict.LongWaits: return 1;
                    case LineVerdict.NearlyEmpty: return 1;
                    default: return 0;
                }
            }
        }
    }

    // Judging a line's health. Pure so the thresholds can be tested rather than
    // eyeballed in game.
    internal static class SuitabilityLineHealth
    {

        // `requireVehicles` and `notEnoughVehicles` come straight from
        // TransportLineFlags — the game already decides when a line is short of
        // vehicles, so that signal is read rather than re-derived.
        public static LineVerdict Judge(
            float usage,
            float peakUsage,
            float achievedInterval,
            float targetInterval,
            float longWaitMultiple,
            int vehicles,
            int targetVehicles,
            bool requireVehicles,
            bool notEnoughVehicles,
            float emptyThreshold,
            out int addVehicles)
        {
            addVehicles = 0;

            // The game itself is asking for more vehicles on this line.
            if (notEnoughVehicles || (requireVehicles && vehicles < targetVehicles))
            {
                addVehicles = Math.Max(1, targetVehicles - vehicles);
                return LineVerdict.Overcrowded;
            }

            if (usage >= Assumptions.FullUsage)
            {
                // Already running the fleet the interval calls for, so the vehicles
                // themselves are the ceiling: a bigger mode is the only way up.
                if (vehicles >= targetVehicles)
                {
                    return LineVerdict.AtModeCapacity;
                }

                addVehicles = Math.Max(1, targetVehicles - vehicles);
                return LineVerdict.Overcrowded;
            }

            // Emptiness is checked BEFORE waits: a barely-used line is lightly used,
            // not badly timetabled, and reporting it as "long waits" told the player
            // to run more buses down an empty street.
            //
            // Judged on the busiest moment as well as the average. "Reroute or remove"
            // is the most destructive advice this mod gives, and a line that fills up
            // twice a day and sits idle the rest of the time averages out looking dead
            // — a ferry carrying twelve people at that moment, peaking at 12% against a
            // city median of 7%, was being recommended for removal on a mean of 3%.
            // A line is only empty if it is empty even at its best.
            if (usage <= emptyThreshold && peakUsage <= emptyThreshold * Assumptions.EmptyPeakAllowance)
            {
                return LineVerdict.NearlyEmpty;
            }

            // Long waits with room to spare means the service is too infrequent for
            // the demand pattern rather than too small. Only meaningful on a line that
            // is actually carrying people.
            //
            // Judged against the line's own target, so the verdict says "this line is
            // running at less than half the frequency it is set to" rather than
            // holding every mode to one stopwatch. A target of zero means the game has
            // not given us one, and then there is nothing to be late against.
            bool missesItsTarget = targetInterval > 0f
                && achievedInterval >= targetInterval * Math.Max(Assumptions.LongWaitMultipleOfTarget, longWaitMultiple);
            if (missesItsTarget && achievedInterval * 0.5f >= Assumptions.LongWait)
            {
                return LineVerdict.LongWaits;
            }

            return LineVerdict.Healthy;
        }

        // A concrete improvement for one line: what to run it with, how many, and what
        // to do about its shape. Every number is derived from the line's own
        // measurements so the player can check the reasoning.
        // `roundTripSeconds` is the game's stableDuration: every hop plus the dwell at
        // every stop. The fleet follows from it exactly as TransportLineSystem derives
        // it, so the recommendation is one the game can actually produce.
        // What is wrong with a line's SHAPE, as a token the panel can translate.
        internal enum PlanShape
        {
            Fine = 0,
            Split = 1,
            ThinStops = 2,
            Reroute = 3,
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
            // Interval that yields this fleet. Negative when the round trip is unknown,
            // which is the only case where there is no actionable number to quote.
            public int m_IntervalSeconds;
            public PlanShape m_Shape;
            // Shape arguments: km for Split, stop count for ThinStops.
            public float m_ShapeValue;
            // Present spacing in metres, for ThinStops only.
            public float m_ShapeSpacing;
        }

        public static ImprovePlan Plan(
            LineHealth health,
            float lengthMetres,
            float roundTripSeconds,
            int capacityPerVehicle,
            float targetLoad)
        {

            // Mode: grow when the vehicles themselves are the ceiling, shrink when the
            // line cannot fill what it already runs.
            ModePreset mode = health.m_Mode;
            if (health.m_Verdict == LineVerdict.AtModeCapacity)
            {
                mode = TransitModes.NextModeUp(health.m_Mode);
            }
            else if (health.m_Verdict == LineVerdict.NearlyEmpty)
            {
                mode = TransitModes.NextModeDown(health.m_Mode);
            }

            // Fleet: enough capacity to carry the observed load at a comfortable fill,
            // and enough vehicles to hold a sensible headway around the line.
            //
            // The load comes from the BUSIEST reading in the window, not the count at
            // the instant of collection. The verdict that sent us here was drawn from
            // the window, and sizing its remedy from one reading handed a ferry caught
            // mid-crossing a fleet for nobody. m_Passengers and m_Capacity are
            // deliberately kept as instantaneous counts that do not divide into a
            // windowed share, so the peak share is multiplied back up by capacity to
            // get a rider count on the same footing as the verdict.
            int perVehicle = Math.Max(1, capacityPerVehicle);
            float peakRiders = health.m_PeakUsage * health.m_Capacity;
            int forLoad = (int)Math.Ceiling(peakRiders / Math.Max(0.2f, targetLoad) / perVehicle);
            int forHeadway = roundTripSeconds > 0f
                ? (int)Math.Round(roundTripSeconds / TransitModes.TargetHeadwayFor(mode), MidpointRounding.AwayFromZero)
                : health.m_Vehicles;
            int vehicles = Math.Max(1, Math.Max(forLoad, forHeadway));

            // The game sizes the fleet from the line's interval and is already asking
            // for m_TargetVehicles. Recommending fewer than that on a line flagged as
            // short of vehicles produced "overcrowded — add 1 vehicle" next to a plan
            // saying "run it with 1 vehicle (-3)".
            if (health.m_Verdict is LineVerdict.Overcrowded or LineVerdict.AtModeCapacity)
            {
                vehicles = Math.Max(vehicles, health.m_TargetVehicles);
            }

            var plan = new ImprovePlan
            {
                m_Mode = mode,
                m_Vehicles = vehicles,
                m_VehicleDelta = vehicles - health.m_Vehicles,
                // The player cannot set a vehicle count in Cities: Skylines II — the
                // game derives it from the line's interval. So the actionable number is
                // the interval that yields this fleet.
                m_IntervalSeconds = roundTripSeconds > 0f
                    ? (int)Math.Round(roundTripSeconds / vehicles, MidpointRounding.AwayFromZero)
                    : -1,
            };

            // Shape: the two failures worth calling out are a line too long to keep a
            // headway, and one making far more stops than its mode wants.
            float maxLength = TransitModes.MaxSensibleLength(mode);
            float spacing = health.m_Stops > 1 ? lengthMetres / (health.m_Stops - 1) : lengthMetres;
            float wantedSpacing = Assumptions.StopSpacingFor(mode);

            if (lengthMetres > maxLength)
            {
                plan.m_Shape = PlanShape.Split;
                plan.m_ShapeValue = lengthMetres / 1000f;
            }
            else if (spacing < wantedSpacing * 0.6f && health.m_Stops > 4)
            {
                plan.m_Shape = PlanShape.ThinStops;
                plan.m_ShapeValue = Math.Max(2, (int)Math.Round(lengthMetres / wantedSpacing, MidpointRounding.AwayFromZero) + 1);
                plan.m_ShapeSpacing = spacing;
            }
            else if (health.m_Verdict == LineVerdict.NearlyEmpty)
            {
                plan.m_Shape = PlanShape.Reroute;
            }
            else
            {
                plan.m_Shape = PlanShape.Fine;
            }

            return plan;
        }

        // English rendering of a plan, for the log. The panel builds its own from the
        // same ImprovePlan, so the two cannot disagree about the numbers.
        public static string Improve(
            LineHealth health,
            float lengthMetres,
            float roundTripSeconds,
            int capacityPerVehicle,
            float targetLoad)
        {
            ImprovePlan plan = Plan(health, lengthMetres, roundTripSeconds, capacityPerVehicle, targetLoad);
            var parts = new System.Text.StringBuilder();
            _ = parts.Append("run it as ");
            _ = parts.Append(plan.m_Mode);
            _ = parts.Append(" with ");
            _ = parts.Append(plan.m_Vehicles);
            _ = parts.Append(" vehicle(s)");
            if (plan.m_VehicleDelta != 0)
            {
                _ = parts.Append(plan.m_VehicleDelta > 0 ? " (+" : " (");
                _ = parts.Append(plan.m_VehicleDelta);
                _ = parts.Append(')');
            }

            if (plan.m_IntervalSeconds >= 0)
            {
                _ = parts.Append(", i.e. an interval of about ");
                _ = parts.Append(plan.m_IntervalSeconds.ToString(CultureInfo.InvariantCulture));
                _ = parts.Append(" s");
            }

            switch (plan.m_Shape)
            {
                case PlanShape.Split:
                    _ = parts.Append("; split it — ");
                    _ = parts.Append(plan.m_ShapeValue.ToString("F1", CultureInfo.InvariantCulture));
                    _ = parts.Append(" km is beyond what one ");
                    _ = parts.Append(plan.m_Mode);
                    _ = parts.Append(" line can keep to time");
                    break;
                case PlanShape.ThinStops:
                    _ = parts.Append("; thin the stops to about ");
                    _ = parts.Append(plan.m_ShapeValue.ToString("F0", CultureInfo.InvariantCulture));
                    _ = parts.Append(" — they average ");
                    _ = parts.Append(plan.m_ShapeSpacing.ToString("F0", CultureInfo.InvariantCulture));
                    _ = parts.Append(" m apart, close for a ");
                    _ = parts.Append(plan.m_Mode);
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

        // The same plan as the delimited payload the panel renders.
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
                plan.m_ShapeSpacing.ToString("F0", CultureInfo.InvariantCulture),
            });
        }

        // The one variable piece of a verdict: how many vehicles to add, or the mode
        // to upgrade to. Empty when the verdict takes no argument. Kept apart from
        // Describe so the panel can substitute it into a TRANSLATED sentence instead
        // of receiving English prose it cannot localise.
        public static string VerdictArgument(LineHealth health)
        {
            switch (health.m_Verdict)
            {
                case LineVerdict.Overcrowded:
                    return health.m_AddVehicles > 0
                        ? health.m_AddVehicles.ToString(CultureInfo.InvariantCulture)
                        : string.Empty;
                case LineVerdict.AtModeCapacity:
                    ModePreset upgrade = TransitModes.NextModeUp(health.m_Mode);
                    return upgrade != health.m_Mode ? upgrade.ToString() : string.Empty;
                default:
                    return string.Empty;
            }
        }

        // English prose for the LOG. The panel builds its own text from the verdict
        // token and VerdictArgument above.
        public static string Describe(LineHealth health)
        {
            switch (health.m_Verdict)
            {
                case LineVerdict.Overcrowded:
                    return health.m_AddVehicles > 0
                        ? $"overcrowded — add {(health.m_AddVehicles).ToString(CultureInfo.InvariantCulture)} vehicle(s)"
                        : "overcrowded — increase service";
                case LineVerdict.AtModeCapacity:
                    ModePreset next = TransitModes.NextModeUp(health.m_Mode);
                    return next != health.m_Mode
                        ? $"at capacity — upgrade to {next}"
                        : "at capacity — split the route";
                case LineVerdict.LongWaits:
                    return "long waits with spare room — shorten the route or run more often";
                case LineVerdict.NearlyEmpty:
                    return "nearly empty — reroute or remove";
                default:
                    return "healthy";
            }
        }
    }
}
