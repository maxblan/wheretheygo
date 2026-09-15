namespace WhereTheyGo
{
    // What a line's own readings say over the window. Pure, so the window arithmetic
    // is testable rather than eyeballed in game.
    internal static class LineWindow
    {
        // Hands the line its averages, whole-window and per period, from the history's
        // readings of it.
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
    }
}
