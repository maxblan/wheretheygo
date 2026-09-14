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

        // Whole hours since midnight, 0..23. The hour is what a journey carries and
        // what the time-of-day slider selects: a finer clock would pretend to a
        // precision the shift model does not have.
        //
        // The epsilon is not decoration. 23/24 is not representable, so a time of
        // exactly 23:00 arrives as 0.95833331f and floors to 22 — an hour early, for
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
        // (0 day, 1 evening, 2 night — Game.Companies.Workshift), the city's working
        // hours, and that citizen's OWN offset in days.
        //
        // The rounding is the GAME's, not a tidy-up of ours: WorkerSystem.GetTimeToWork
        // computes frac(RoundToInt(24 · (workDayStart + offset)) / 24), so the evening
        // shift's 0.33 of a day lands on a whole 17:00 rather than on 16:92. Dropping
        // the rounding put every evening commute an hour early.
        //
        // `citizenOffsetDays` is WorkerSystem.GetWorkOffset: ±1 h, drawn from the
        // citizen's own pseudo-random seed. It used to be left out as a detail, and the
        // hour strip in the panel is what showed that it is not one — without it every
        // commuter in the city leaves in the same single hour, and the day has one
        // spike where the game has a rush hour three hours wide.
        public static void WorkHours(byte shift, float workDayStart, float workDayEnd, float citizenOffsetDays, out byte outHour, out byte backHour)
        {
            float offset = citizenOffsetDays
                + (shift == 1 ? Assumptions.EveningShiftOffset : shift == 2 ? Assumptions.NightShiftOffset : 0f);
            outHour = WholeHour(workDayStart + offset);
            backHour = WholeHour(workDayEnd + offset);
        }

        // The same for a student. Students carry the same ±1 h offset — StudentSystem
        // reads it from the same CitizenPseudoRandom.WorkOffset draw — but their times
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

        // Boardings over the seats a line offers during one period only: the seats of a
        // vehicle scaled by the period's share of the day (the day's runs happen in
        // both periods in proportion), through the one utilisation formula.
        public static float UtilisationInPeriod(float ridersInPeriod, float headwaySeconds, float vehicleCapacity, float periodShareOfDay)
        {
            if (periodShareOfDay <= 0f)
            {
                return 0f;
            }

            return Coverage.Utilisation(ridersInPeriod, headwaySeconds, (double)vehicleCapacity * periodShareOfDay);
        }

        // The schedule a line should run, suggested or existing (register A8, decided
        // 2026-09-06: one rule for both, on the boardings the routing attributes to the
        // line in each period): all day unless one period falls under the utilisation
        // floor while the other does not. A line under the floor in both is not a
        // schedule question and stays all day here.
        // The same rule asked of an EXISTING line: when both periods are under the floor
        // (or no floor is set) there is no schedule question and the line keeps the
        // schedule it runs — a day-only line with no demand in either period is not told
        // to extend into the night (found on Valmare 2026-09-06: three trains with zero
        // routed riders were advised "run it all day").
        public static LineSchedule Advise(LineSchedule current, float dayUtilisation, float nightUtilisation, float floor)
        {
            if (floor <= 0f || (dayUtilisation < floor && nightUtilisation < floor))
            {
                return current;
            }

            return Recommend(dayUtilisation, nightUtilisation, floor);
        }

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
    }
}
