using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // One existing transit line, read out of the save: the stops it calls at in
    // travel order, how long each hop takes, how full it is, and the geometry needed
    // to draw it — plus what the rolling window and the demand routing say about it.
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
        // TransportLine.m_VehicleInterval: NOT a measured headway. The game sets it to
        // min(10 × target interval, path duration ÷ target fleet), so on a running line
        // it is the planned interval and on an inactive one (a day-only line at night,
        // no active buildings) the whole path duration. It is what the game's own
        // pathfinder charges as a wait, which is why the transit router reads it; no
        // verdict does (decompiled TransportLineSystem, 2026-09-06).
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
        // The whole loop the vehicles drive (every segment of the route), and the calls
        // they make on it (a two-way line lists each place twice, once per direction).
        public float m_LengthMetres;
        public int m_Vehicles;
        public int m_Passengers;
        public int m_Capacity;
        public bool m_RequireVehicles;
        public bool m_NotEnoughVehicles;
        // When the line runs today (Route.m_OptionMask: RouteOption.Day / .Night).
        public LineSchedule m_Schedule;
        // Mean occupancy over the window's readings in each period, and how many
        // readings with vehicles out each period has (LineHistory.TryAveragePeriod).
        public float m_DayUsage;
        public float m_NightUsage;
        public int m_DaySamples;
        public int m_NightSamples;

        // What the line looked like across the last game day, filled in from
        // LineHistory (SuitabilityLineHealth.ApplyWindow). Everything above is the
        // reading at the instant of collection; these are what a verdict is drawn
        // from once enough readings exist, because a single reading catches a one-boat
        // ferry mid-crossing at zero passengers. m_WindowSamples of 0 means there is no
        // window yet and the instantaneous reading is all there is. Only readings with
        // vehicles out count (LineAverage).
        public float m_WindowUsage;
        public float m_WindowPeakUsage;
        // The planning load: the PlanningLoadQuantile of the passengers aboard over the
        // window's active readings (register A8.4), and the single busiest reading.
        public int m_WindowPlanningLoad;
        public int m_WindowMaxAboard;
        public float m_WindowInterval;
        public int m_WindowSamples;
        public int m_WindowReadings;
        public float m_WindowGameHours;

        // What the demand routing attributes to this line on the network as it stands
        // (LineSetEvaluation.BaseRiders from the last route pass): journeys a day whose
        // fastest door-to-door itinerary boards it, and their split by period. Negative
        // until a pass has run — the verdicts then rest on the readings alone and say so.
        public float m_RidersPerDay = -1f;
        public float m_RidersByDay;
        public float m_RidersByNight;

        public bool HasDemand => m_RidersPerDay >= 0f;

        // Share of fleet capacity in use: the window mean once enough readings back
        // it, otherwise the reading taken at collection. One accessor so the city
        // median and each line's own usage are always measured the same way — the
        // "nearly empty" threshold is a fraction of that median, and mixing the two
        // would compare a windowed line against an instantaneous city.
        public float Usage => HasWindow
            ? m_WindowUsage
            : (m_Capacity > 0 ? m_Passengers / (float)m_Capacity : 0f);

        public float PeakUsage => HasWindow ? m_WindowPeakUsage : Usage;

        // The riders the fleet is sized to carry (A8.3/A8.4): the window's planning
        // load, or the count aboard right now while the window is too thin.
        public int PlanningLoad => HasWindow ? m_WindowPlanningLoad : m_Passengers;

        // The interval the router should charge, on the same footing as Usage.
        public float JudgedInterval => HasWindow ? m_WindowInterval : m_VehicleInterval;

        public bool HasWindow => m_WindowSamples >= Assumptions.MinReadingsForVerdict;

        // Seats of one vehicle as this line actually runs them; 0 when nothing is out,
        // and the prefab's largest vehicle then stands in (SuitabilityLineHealth).
        public int CapacityPerVehicle => m_Vehicles > 0 ? m_Capacity / m_Vehicles : 0;

        // What a rider turning up at random waits, in seconds — the cost the transit
        // router charges for boarding this line.
        //
        // Derived from JudgedInterval, so the routing that decides which journeys are
        // already served runs on the same windowed interval the readings hold. Stamped
        // from the instantaneous interval it inherited every spike: the game's value is
        // capped at 10x the target, so one inactive moment made the line look unusable,
        // the journeys through it looked unserved, and the suggestions moved.
        //
        // Vanilla's PathUtils.GetTransportStopSpecification takes
        // max(interval / 2, WaitingPassengers.m_AverageWaitingTime), but that second
        // term is deliberately dropped: it is the pathfinder's accumulator in game
        // units, not seconds, and one stranded rider drives it into the thousands.
        // Passing it as seconds let it beat the real headway on some lines and not
        // others, so the router's wait cost was in mixed units.
        public float ExpectedWait => SuitabilityTransit.ExpectedWait(JudgedInterval, 0f, m_StopDuration);
    }
}
