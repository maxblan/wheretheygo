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
        // Below this it is not carrying enough to justify itself.
        public const float EmptyUsage = 0.15f;
        // Average wait, in the game's own units, past which service feels sparse.
        public const float LongWait = 45f;

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

            // Long waits with room to spare means the service is too infrequent for
            // the demand pattern rather than too small.
            if (averageWait >= LongWait && usage < FullUsage)
            {
                return LineVerdict.LongWaits;
            }

            if (usage > 0f && usage <= EmptyUsage)
            {
                return LineVerdict.NearlyEmpty;
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
