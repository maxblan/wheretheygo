using System;

namespace WhereTheyGo
{
    // The top of the load chart's y axis. The chart used to run 0-100 % whatever the
    // line did, which drew a metro carrying 15 people in an 840-seat train as a flat
    // line along the floor: the shape of its day was there and unreadable.
    //
    // The axis therefore follows the line's own busiest hour, off a short ladder of
    // tops (Assumptions.LoadAxisTopsPercent) rather than off the peak itself, so the
    // chart marks a number a player can hold and the mark at half the top is readable
    // too. The caption says the axis is the line's own, because a chart whose scale
    // moves must say so.
    internal static class LoadAxis
    {
        // `shares` are loads 0..1 with a negative for an hour nobody watched.
        public static float TopOf(float[]? shares)
        {
            float peak = 0f;
            for (int i = 0; shares is not null && i < shares.Length; i++)
            {
                if (shares[i] > peak)
                {
                    peak = shares[i];
                }
            }

            // Compared as SHARES, not as percentages: `peak * 100f` turns a load of
            // exactly 0.6 into 60.000004 and sends the axis a rung too high, because
            // 0.6 has no binary32 spelling. Dividing the rung the same way the caller
            // divided its own value keeps a peak that sits on a rung on that rung.
            int[] tops = Assumptions.LoadAxisTopsPercent;
            for (int i = 0; i < tops.Length; i++)
            {
                float top = tops[i] / 100f;
                if (peak <= top)
                {
                    return top;
                }
            }

            // Above the last rung the axis is the peak itself, rounded up to a whole
            // percent: a line cannot exceed its seats by much, but standing room and a
            // fleet that shrank mid-window both can put it there, and a bar drawn past
            // the top would read as the top.
            return (float)Math.Ceiling(peak * 100f) / 100f;
        }
    }
}
