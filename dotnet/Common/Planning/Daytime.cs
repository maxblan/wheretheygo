using System;

namespace WhereTheyGo
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

    // The game's clock as it matters for transit (verified against the game):
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

        // Whole hours since midnight, 0..23. The hour is what a journey carries and
        // what the time-of-day slider selects: a finer clock would pretend to a
        // precision the shift model does not have.
        //
        // The epsilon is not decoration. 23/24 is not representable, so a time of
        // exactly 23:00 arrives as 0.95833331f and floors to 22, an hour early, for
        // every journey seen on the hour.
        public static byte HourOf(float timeOfDay)
        {
            int hour = (int)Math.Floor((Frac(timeOfDay) * 24.0) + HourEpsilon);
            return (byte)(hour < 0 ? 0 : hour > 23 ? 23 : hour);
        }

        private const double HourEpsilon = 1e-6;

        public static bool IsNightHour(byte hour)
        {
            return IsNight(hour / 24f);
        }

        // The hour a worker leaves home and the hour they leave work, for the shift
        // (0 day, 1 evening, 2 night, per Game.Companies.Workshift), the city's working
        // hours, and that citizen's OWN offset in days.
        //
        // The rounding is the GAME's, not a tidy-up of ours: WorkerSystem.GetTimeToWork
        // computes frac(RoundToInt(24 · (workDayStart + offset)) / 24), so the evening
        // shift's 0.33 of a day lands on a whole 17:00 rather than on 16:92. Dropping
        // the rounding put every evening commute an hour early.
        //
        // `citizenOffsetDays` is WorkerSystem.GetWorkOffset: ±1 h, drawn from the
        // citizen's own pseudo-random seed. It used to be left out as a detail, and the
        // hour strip in the panel is what showed that it is not one: without it every
        // commuter in the city leaves in the same single hour, and the day has one
        // spike where the game has a rush hour three hours wide.
        public static void WorkHours(byte shift, float workDayStart, float workDayEnd, float citizenOffsetDays, out byte outHour, out byte backHour)
        {
            float offset = citizenOffsetDays
                + (shift == 1 ? Assumptions.EveningShiftOffset : shift == 2 ? Assumptions.NightShiftOffset : 0f);
            outHour = WholeHour(workDayStart + offset);
            backHour = WholeHour(workDayEnd + offset);
        }

        // The same for a student. Students carry the same ±1 h offset, since StudentSystem
        // reads it from the same CitizenPseudoRandom.WorkOffset draw, but their times
        // are NOT rounded to the hour: GetTimeToStudy is frac(workDayStart + offset)
        // with no RoundToInt. So the hour is simply the one the time falls in.
        public static void StudyHours(float workDayStart, float workDayEnd, float citizenOffsetDays, out byte outHour, out byte backHour)
        {
            outHour = HourContaining(workDayStart + citizenOffsetDays);
            backHour = HourContaining(workDayEnd + citizenOffsetDays);
        }

        // The game's own hour: 24 · t rounded to the nearest whole hour, then wrapped.
        private static byte WholeHour(float timeOfDay)
        {
            int hour = (int)Math.Round(24.0 * timeOfDay, MidpointRounding.ToEven);
            hour %= 24;
            return (byte)(hour < 0 ? hour + 24 : hour);
        }

        // The hour a time of day falls inside, wrapped: 07:40 is the seven o'clock hour.
        private static byte HourContaining(float timeOfDay)
        {
            int hour = (int)Math.Floor(24.0 * timeOfDay);
            hour %= 24;
            return (byte)(hour < 0 ? hour + 24 : hour);
        }

        // The share of a journey's two rides that fall in the day period, from the two
        // hours it is made at. Same rule as CommuteDayShare, over hours rather than
        // day fractions, so a journey carries one fact about its timing and not two.
        public static float DayShareOfHours(byte outHour, byte backHour)
        {
            return (IsNightHour(outHour) ? 0f : 0.5f) + (IsNightHour(backHour) ? 0f : 0.5f);
        }

    }
}
