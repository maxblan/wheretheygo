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
        // The game's own display name for the line.
        public string m_Name;
        public Setting.ModePreset m_Mode;
        public int m_Vehicles;
        public int m_TargetVehicles;
        public int m_Passengers;
        public int m_Capacity;
        public float m_Usage;
        public float m_AverageWait;
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
        // Average wait past which service genuinely feels sparse. Observed waits on a
        // working city run 20-100 for healthy lines, so 45 flagged almost everything.
        public const float LongWait = 150f;

        // `requireVehicles` and `notEnoughVehicles` come straight from
        // TransportLineFlags — the game already decides when a line is short of
        // vehicles, so that signal is read rather than re-derived.
        public static LineVerdict Judge(
            float usage,
            float averageWait,
            int vehicles,
            int targetVehicles,
            bool requireVehicles,
            bool notEnoughVehicles,
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
            if (usage <= EmptyUsage)
            {
                return LineVerdict.NearlyEmpty;
            }

            // Long waits with room to spare means the service is too infrequent for
            // the demand pattern rather than too small. Only meaningful on a line that
            // is actually carrying people.
            if (averageWait >= LongWait)
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
        public static string Improve(
            LineHealth health,
            float lengthMetres,
            float lineDurationSeconds,
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
            int forHeadway = lineDurationSeconds > 0f
                ? (int)Math.Round(lineDurationSeconds / TargetHeadwayFor(mode))
                : health.m_Vehicles;
            int vehicles = Math.Max(1, Math.Max(forLoad, forHeadway));

            parts.Append(" with ");
            parts.Append(vehicles);
            parts.Append(" vehicle(s)");
            if (vehicles != health.m_Vehicles)
            {
                parts.Append(vehicles > health.m_Vehicles ? " (+" : " (");
                parts.Append(vehicles - health.m_Vehicles);
                parts.Append(')');
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
