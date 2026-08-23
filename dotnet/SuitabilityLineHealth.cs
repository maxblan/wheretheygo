using System;

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
        public Setting.ModePreset m_Mode;
        public int m_Vehicles;
        public int m_TargetVehicles;
        public int m_Passengers;
        public int m_Capacity;
        public float m_Usage;
        // Half the achieved headway: what a rider turning up at random waits.
        public float m_TypicalWait;
        public float m_LengthKm;
        public int m_Stops;
        public LineVerdict m_Verdict;
        public int m_AddVehicles;

        public int Severity
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
        // Above this share of capacity a line is effectively full.
        public const float FullUsage = 0.85f;
        // Below this it is not carrying enough to justify itself. Usage is an
        // INSTANTANEOUS snapshot of passengers against fleet capacity, and a healthy
        // line sits well under half full most of the time — at 0.15 this flagged 18 of
        // 19 lines on a working city, which is noise rather than advice.
        public const float EmptyUsage = 0.06f;
        // A line is only "empty" if it is far below what this city's lines normally
        // carry. Without the relative test a fixed threshold flags most of a healthy
        // network, because usage is an instantaneous snapshot.
        public const float EmptyShareOfMedian = 0.35f;
        // Wait past which service genuinely feels sparse, in the same units as the
        // line's round trip. Measured as half the achieved headway.
        //
        // NOT from WaitingPassengers.m_TypicalWaitingTime: that is an accumulator the
        // game keeps for its pathfinder — max(ongoing/waiting, concluded/boarded),
        // quantised to 5 — so a single stranded rider drives it to thousands. Read as
        // seconds it reported 45-minute waits on a line running every two minutes, and
        // flagged 8 of 18 lines on that basis.
        public const float LongWait = 150f;

        // `requireVehicles` and `notEnoughVehicles` come straight from
        // TransportLineFlags — the game already decides when a line is short of
        // vehicles, so that signal is read rather than re-derived.
        public static LineVerdict Judge(
            float usage,
            float achievedInterval,
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

            if (usage >= FullUsage)
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
            if (usage <= emptyThreshold)
            {
                return LineVerdict.NearlyEmpty;
            }

            // Long waits with room to spare means the service is too infrequent for
            // the demand pattern rather than too small. Only meaningful on a line that
            // is actually carrying people.
            if (achievedInterval * 0.5f >= LongWait)
            {
                return LineVerdict.LongWaits;
            }

            return LineVerdict.Healthy;
        }

        // The mode a struggling line should grow into. Ordered by capacity, so a bus
        // becomes a tram before it becomes a metro — suggesting the largest possible
        // jump would rarely be actionable.
        public static Setting.ModePreset NextModeUp(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Bus: return Setting.ModePreset.Tram;
                case Setting.ModePreset.Tram: return Setting.ModePreset.Metro;
                case Setting.ModePreset.Metro: return Setting.ModePreset.Train;
                default: return mode;
            }
        }

        // A concrete improvement for one line: what to run it with, how many, and what
        // to do about its shape. Every number is derived from the line's own
        // measurements so the player can check the reasoning.
        // `roundTripSeconds` is the game's stableDuration: every hop plus the dwell at
        // every stop. The fleet follows from it exactly as TransportLineSystem derives
        // it, so the recommendation is one the game can actually produce.
        public static string Improve(
            LineHealth health,
            float lengthMetres,
            float roundTripSeconds,
            int capacityPerVehicle,
            float targetLoad)
        {
            var parts = new System.Text.StringBuilder();

            // Mode: grow when the vehicles themselves are the ceiling, shrink when the
            // line cannot fill what it already runs.
            Setting.ModePreset mode = health.m_Mode;
            if (health.m_Verdict == LineVerdict.AtModeCapacity)
            {
                mode = NextModeUp(health.m_Mode);
            }
            else if (health.m_Verdict == LineVerdict.NearlyEmpty)
            {
                mode = NextModeDown(health.m_Mode);
            }

            parts.Append("run it as ");
            parts.Append(mode);

            // Fleet: enough capacity to carry the observed load at a comfortable fill,
            // and enough vehicles to hold a sensible headway around the line.
            int perVehicle = Math.Max(1, capacityPerVehicle);
            int forLoad = (int)Math.Ceiling(health.m_Passengers / Math.Max(0.2f, targetLoad) / perVehicle);
            int forHeadway = roundTripSeconds > 0f
                ? (int)Math.Round(roundTripSeconds / TargetHeadwayFor(mode))
                : health.m_Vehicles;
            int vehicles = Math.Max(1, Math.Max(forLoad, forHeadway));

            // The game sizes the fleet from the line's interval and is already asking
            // for m_TargetVehicles. Recommending fewer than that on a line flagged as
            // short of vehicles produced "overcrowded — add 1 vehicle" next to a plan
            // saying "run it with 1 vehicle (-3)".
            if (health.m_Verdict == LineVerdict.Overcrowded || health.m_Verdict == LineVerdict.AtModeCapacity)
            {
                vehicles = Math.Max(vehicles, health.m_TargetVehicles);
            }

            parts.Append(" with ");
            parts.Append(vehicles);
            parts.Append(" vehicle(s)");
            if (vehicles != health.m_Vehicles)
            {
                parts.Append(vehicles > health.m_Vehicles ? " (+" : " (");
                parts.Append(vehicles - health.m_Vehicles);
                parts.Append(')');
            }

            // The player cannot set a vehicle count in Cities: Skylines II — the game
            // derives it from the line's interval. So the actionable number is the
            // interval that yields this fleet, which is how it is quoted here.
            if (roundTripSeconds > 0f)
            {
                parts.Append(", i.e. an interval of about ");
                parts.Append((roundTripSeconds / vehicles).ToString("F0"));
                parts.Append(" s");
            }

            // Shape: the two failures worth calling out are a line too long to keep a
            // headway, and one making far more stops than its mode wants.
            float maxLength = MaxSensibleLength(mode);
            float spacing = health.m_Stops > 1 ? lengthMetres / (health.m_Stops - 1) : lengthMetres;
            float wantedSpacing = TargetSpacingFor(mode);

            if (lengthMetres > maxLength)
            {
                parts.Append("; split it — ");
                parts.Append((lengthMetres / 1000f).ToString("F1"));
                parts.Append(" km is beyond what one ");
                parts.Append(mode);
                parts.Append(" line can keep to time");
            }
            else if (spacing < wantedSpacing * 0.6f && health.m_Stops > 4)
            {
                int keep = Math.Max(2, (int)Math.Round(lengthMetres / wantedSpacing) + 1);
                parts.Append("; thin the stops to about ");
                parts.Append(keep);
                parts.Append(" — they average ");
                parts.Append(spacing.ToString("F0"));
                parts.Append(" m apart, close for a ");
                parts.Append(mode);
            }
            else if (health.m_Verdict == LineVerdict.NearlyEmpty)
            {
                parts.Append("; or reroute it through denser ground — the suggestions list shows where demand is unserved");
            }
            else
            {
                parts.Append("; the route shape looks reasonable");
            }

            return parts.ToString();
        }

        public static Setting.ModePreset NextModeDown(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Train: return Setting.ModePreset.Metro;
                case Setting.ModePreset.Metro: return Setting.ModePreset.Tram;
                case Setting.ModePreset.Tram: return Setting.ModePreset.Bus;
                default: return mode;
            }
        }

        private static float TargetHeadwayFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 240f;
                case Setting.ModePreset.Metro: return 200f;
                case Setting.ModePreset.Train: return 480f;
                case Setting.ModePreset.Ferry: return 600f;
                default: return 300f;
            }
        }

        private static float TargetSpacingFor(Setting.ModePreset mode)
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

        private static float MaxSensibleLength(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 12000f;
                case Setting.ModePreset.Metro: return 20000f;
                case Setting.ModePreset.Train: return 60000f;
                case Setting.ModePreset.Ferry: return 20000f;
                default: return 9000f;
            }
        }

        public static string Describe(LineHealth health)
        {
            switch (health.m_Verdict)
            {
                case LineVerdict.Overcrowded:
                    return health.m_AddVehicles > 0
                        ? $"overcrowded — add {health.m_AddVehicles} vehicle(s)"
                        : "overcrowded — increase service";
                case LineVerdict.AtModeCapacity:
                    Setting.ModePreset next = NextModeUp(health.m_Mode);
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
