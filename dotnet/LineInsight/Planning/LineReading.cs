using System.Collections.Generic;

namespace WhereTheyGo
{
    // What the section in a line's own window shows beside the two routed figures:
    // the numbers the mod has always measured and only ever logged.
    //
    // The sibling of LineContribution, and pure for the same reason: choosing between
    // the window's evidence and this instant's reading is arithmetic, and arithmetic
    // in a Systems partial is arithmetic no test can reach.
    internal readonly struct LineReading
    {
        private LineReading(
            LineStanding standing, float waitSeconds, int peakAboard,
            int capacity, float loopSeconds, float idealLoopSeconds, bool loopIsFloor, float loadAxisTop, bool fromWindow)
        {
            Standing = standing;
            WaitSeconds = waitSeconds;
            PeakAboard = peakAboard;
            Capacity = capacity;
            LoopSeconds = loopSeconds;
            IdealLoopSeconds = idealLoopSeconds;
            LoopIsFloor = loopIsFloor;
            LoadAxisTop = loadAxisTop;
            FromWindow = fromWindow;
        }

        public LineStanding Standing { get; }

        // What the router charges every rider for waiting: half the achieved interval,
        // less the time the vehicle stands at the stop anyway (TransitGraph.ExpectedWait).
        public float WaitSeconds { get; }

        // The busiest single reading in the window, against the seats the line has out.
        // Kept apart from the mean on purpose: a line packed at the peak and empty at
        // night is not a line that is uniformly half empty.
        public int PeakAboard { get; }

        public int Capacity { get; }

        // The loop as it is actually ridden against the same loop in free flow. The
        // difference is traffic, and nothing in the game says it out loud.
        public float LoopSeconds { get; }

        public float IdealLoopSeconds { get; }

        // Whether LoopSeconds is a lower bound rather than a measurement. The panel
        // must not print a floor under a caption that says "as driven".
        public bool LoopIsFloor { get; }

        public float LoadAxisTop { get; }

        // Whether the peak above rests on the window or on this one reading. The panel
        // says which, because a mean over a day and a single sample are the same number
        // on screen and mean very different things.
        public bool FromWindow { get; }

        public static LineReading Of(ExistingLine line, IReadOnlyList<float> lineRiders, float cityJourneys, float[]? hourlyLoad)
        {
            if (line is null)
            {
                return default;
            }

            return new LineReading(
                LineStanding.Of(line.m_RidersPerDay, lineRiders, cityJourneys),
                line.ExpectedWait,
                line.HasWindow ? line.m_WindowMaxAboard : line.m_Passengers,
                line.m_Capacity,
                line.m_LineDurationSeconds,
                line.m_PathDurationSeconds,
                line.m_LoopIsFloor,
                LoadAxis.TopOf(hourlyLoad),
                line.HasWindow);
        }
    }
}
