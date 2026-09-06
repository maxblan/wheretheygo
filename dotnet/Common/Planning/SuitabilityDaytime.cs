using System;

namespace StationSuitabilityOverlay
{
    // When a line runs, as the game offers it on a line: all day, by day only, by
    // night only (Game.Routes.RouteOption.Day / .Night policies; the game's own
    // RouteSchedule enum has the same three members).
    public enum LineSchedule
    {
        DayAndNight = 0,
        Day = 1,
        Night = 2,
    }

    // The game's clock as it matters for transit (decompiled 2026-09-05):
    //  - a day is normalizedTime ∈ [0, 1), 0 = midnight; TransportLineSystem calls it
    //    night when normalizedTime < 0.25 or ≥ 11/12, i.e. 22:00–06:00, and a
    //    day-only line is inactive then (a night-only line the other way round);
    //  - workers leave for work at m_WorkDayStart + a per-citizen offset (±1 h) and
    //    come back at m_WorkDayEnd + the same offset, shifted by 0.33 of a day for
    //    the evening shift and 0.67 for the night shift (WorkerSystem.GetTimeToWork);
    //    students keep the day shift's hours (StudentSystem.GetTimeToStudy).
    // The per-citizen offset is not modelled: a ride is classed by its shift's
    // nominal time, and the two rides of a commute (out and back) are classed
    // separately.
    internal static class Daytime
    {

        public static bool IsNight(float timeOfDay)
        {
            float t = Frac(timeOfDay);
            return t is < Assumptions.NightEnd or >= Assumptions.NightStart;
        }

        public static float Frac(float value)
        {
            return value - (float)Math.Floor(value);
        }

        // Share of a commuter's two daily rides that fall in the day period, for the
        // shift (0 day, 1 evening, 2 night — Game.Companies.Workshift) and the city's
        // working hours (EconomyParameterData.m_WorkDayStart/End as day fractions).
        public static float CommuteDayShare(byte shift, float workDayStart, float workDayEnd)
        {
            float offset = shift == 1 ? Assumptions.EveningShiftOffset : shift == 2 ? Assumptions.NightShiftOffset : 0f;
            float outbound = Frac(workDayStart + offset);
            float homeward = Frac(workDayEnd + offset);
            return (IsNight(outbound) ? 0f : 0.5f) + (IsNight(homeward) ? 0f : 0.5f);
        }

        // Boardings over the seats a line offers during one period only: the day's
        // seats scaled by the period's share of the day.
        public static float UtilisationInPeriod(float ridersInPeriod, float headwaySeconds, float vehicleCapacity, float periodShareOfDay)
        {
            if (headwaySeconds <= 0f || vehicleCapacity <= 0f || periodShareOfDay <= 0f)
            {
                return 0f;
            }

            float boardings = ridersInPeriod * Assumptions.RidesPerJourney;
            float seats = Assumptions.MovementSecondsPerGameDay * periodShareOfDay / headwaySeconds * 2f * vehicleCapacity;
            return boardings / seats;
        }

        // The schedule a line should run: all day unless one period falls under the
        // utilisation floor while the other does not. A line under the floor in both
        // is not a schedule question and stays all day here.
        public static LineSchedule Recommend(float dayUtilisation, float nightUtilisation, float floor)
        {
            if (floor <= 0f)
            {
                return LineSchedule.DayAndNight;
            }

            bool dayOk = dayUtilisation >= floor;
            bool nightOk = nightUtilisation >= floor;
            if (dayOk && !nightOk)
            {
                return LineSchedule.Day;
            }

            if (nightOk && !dayOk)
            {
                return LineSchedule.Night;
            }

            return LineSchedule.DayAndNight;
        }

        // For an existing line judged from its readings: the same rule on the mean
        // usage of each period, asked only once both periods have enough readings and
        // the line actually ran in both (a day-only line has no night readings to
        // judge, so it is never told to run at night on this evidence).
        public static LineSchedule Advise(LineSchedule current, float dayUsage, int daySamples, float nightUsage, int nightSamples, float emptyThreshold, int minSamples)
        {
            if (current != LineSchedule.DayAndNight || daySamples < minSamples || nightSamples < minSamples || emptyThreshold <= 0f)
            {
                return current;
            }

            bool dayEmpty = dayUsage < emptyThreshold;
            bool nightEmpty = nightUsage < emptyThreshold;
            if (nightEmpty && !dayEmpty)
            {
                return LineSchedule.Day;
            }

            if (dayEmpty && !nightEmpty)
            {
                return LineSchedule.Night;
            }

            return LineSchedule.DayAndNight;
        }
    }
}
