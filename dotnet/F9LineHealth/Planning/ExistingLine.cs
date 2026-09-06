using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // One existing transit line, read out of the save: the stops it calls at in
    // travel order, how long each hop takes, how full it is, and the geometry needed
    // to draw it.
    internal sealed class ExistingLine
    {
        public ModePreset m_Mode;
        // The name the game shows for this line, so the panel and the Transportation
        // Overview agree instead of the mod inventing its own numbering.
        public string m_Name = string.Empty;
        public readonly List<int> m_StopIndices = new List<int>();
        public readonly List<float> m_RideSeconds = new List<float>();
        // Dwell at each stop, from TransportLineData.m_StopDuration. Kept because the
        // rider's wait is derived from it rather than stamped at collection.
        public float m_StopDuration;
        // The line's own headway, as the game maintains it.
        public float m_VehicleInterval;
        public float m_LineDurationSeconds;
        // The round trip the way TransportLineSystem measures it for fleet sizing:
        // path durations PLUS the dwell at every stop. Leaving the dwell out made this
        // roughly 2.3x too small, which produced fleet targets of 1 against fleets of 9.
        public float m_StableDurationSeconds;
        // The interval the player has asked for (prefab default plus the line's own
        // modifier). The game derives the fleet from this, so it is what a fleet
        // recommendation has to be measured against.
        public float m_TargetInterval;
        // Stable identity: the position in a worst-first list is not one, and using it
        // meant "Suggest improvement" pointed at whichever line had drifted into that
        // slot when the list was last sorted. See SuitabilityLines.IdentityOf for why
        // it is not simply the entity's index either.
        public int m_Id;
        public float m_LengthMetres;
        public int m_Vehicles;
        public int m_Passengers;
        public int m_Capacity;
        // WaitingPassengers.m_AverageWaitingTime averaged over the line's stops. This
        // is the game's pathfinder accumulator, NOT a wait in seconds — see LongWait.
        public float m_WaitAccumulator;
        public bool m_RequireVehicles;
        public bool m_NotEnoughVehicles;
        // When the line runs today (Route.m_OptionMask: RouteOption.Day / .Night).
        public LineSchedule m_Schedule;
        // Mean usage over the window's readings in each period, and how many readings
        // with vehicles out each period has (LineHistory.TryAveragePeriod).
        public float m_DayUsage;
        public float m_NightUsage;
        public int m_DaySamples;
        public int m_NightSamples;

        // What the line looked like across the last game day, filled in from
        // LineHistory. Everything above is the reading at the instant of collection;
        // these are what a verdict is drawn from once enough readings exist, because a
        // single reading catches a one-boat ferry mid-crossing at zero passengers.
        // m_WindowSamples of 0 means there is no window yet and the instantaneous
        // reading is all there is.
        public float m_WindowUsage;
        public float m_WindowPeakUsage;
        public float m_WindowInterval;
        public int m_WindowSamples;
        public float m_WindowGameHours;

        // Share of fleet capacity in use: the window mean once enough readings back
        // it, otherwise the reading taken at collection. One accessor so the city
        // median and each line's own usage are always measured the same way — the
        // "nearly empty" threshold is a fraction of that median, and mixing the two
        // would compare a windowed line against an instantaneous city.
        public float Usage => m_WindowSamples >= Assumptions.MinReadingsForVerdict
            ? m_WindowUsage
            : (m_Capacity > 0 ? m_Passengers / (float)m_Capacity : 0f);

        // The headway a verdict should judge, on the same footing as Usage.
        public float JudgedInterval => m_WindowSamples >= Assumptions.MinReadingsForVerdict
            ? m_WindowInterval
            : m_VehicleInterval;

        public bool HasWindow => m_WindowSamples >= Assumptions.MinReadingsForVerdict;

        // What a rider turning up at random waits, in seconds — the cost the transit
        // router charges for boarding this line.
        //
        // Derived from JudgedInterval, so the routing that decides which journeys are
        // already served runs on the same windowed headway the verdicts do. Stamped
        // from the instantaneous interval it inherited every bunching spike: the
        // achieved interval is capped at 10x the target, so one bad moment made the
        // line look unusable, the journeys through it looked unserved, and the
        // suggestions moved.
        //
        // Vanilla's PathUtils.GetTransportStopSpecification takes
        // max(interval / 2, WaitingPassengers.m_AverageWaitingTime), but that second
        // term is deliberately dropped: it is the pathfinder's accumulator in game
        // units, not seconds, and one stranded rider drives it into the thousands —
        // the same reason Assumptions.LongWait refuses to read it. Passing it
        // as seconds let it beat the real headway on some lines and not others, so the
        // router's wait cost was in mixed units.
        public float ExpectedWait => SuitabilityTransit.ExpectedWait(JudgedInterval, 0f, m_StopDuration);
    }
}
