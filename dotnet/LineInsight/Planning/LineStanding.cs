using System.Collections.Generic;

namespace WhereTheyGo
{
    // Where one line stands among the others. "5 294 journeys" is a number with no
    // scale on it; "5 294, sixth of your 29 lines, 6 % of the city's travel" is the
    // same number with two.
    //
    // Ties share a rank (two lines at 400 journeys are both fourth), because a
    // tie-break invented here would move on its own between two refreshes.
    internal readonly struct LineStanding
    {
        private LineStanding(int rank, int lineCount, float cityShare)
        {
            Rank = rank;
            LineCount = lineCount;
            CityShare = cityShare;
        }

        // 1 for the line with the most journeys a day. Zero when nothing has been
        // routed yet, which the panel draws as no rank rather than as first place.
        public int Rank { get; }

        public int LineCount { get; }

        // This line's journeys a day as a share of the city's, 0..1. A journey that
        // rides two lines counts for both, so these do not sum to one.
        public float CityShare { get; }

        public static LineStanding Of(float riders, IReadOnlyList<float> lineRiders, float cityJourneys)
        {
            if (lineRiders is null || lineRiders.Count == 0 || riders < 0f)
            {
                return new LineStanding(0, lineRiders?.Count ?? 0, 0f);
            }

            int ahead = 0;
            for (int i = 0; i < lineRiders.Count; i++)
            {
                if (lineRiders[i] > riders)
                {
                    ahead++;
                }
            }

            float share = cityJourneys > 0f ? riders / cityJourneys : 0f;
            return new LineStanding(ahead + 1, lineRiders.Count, share);
        }
    }
}
