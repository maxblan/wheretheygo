using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhereTheyGo
{
    // Everything a reading of the lines rests on, captured at one instant so the panel
    // and a later re-reading see exactly the same data: the lines as collected and the
    // window's readings per line.
    internal sealed class LineReadings
    {
        public List<ExistingLine> Lines = new List<ExistingLine>();
        public Dictionary<int, LineObservation[]> Samples = new Dictionary<int, LineObservation[]>();
        public uint WindowFrames;
    }

    // What a line's own readings say over the window. Pure, so the window arithmetic
    // is testable rather than eyeballed in game.
    internal static class LineWindow
    {
        // Snapshots the inputs of one pass: the samples are copied so a reading taken
        // after the judgement cannot change what the export describes.
        public static LineReadings Capture(List<ExistingLine> lines, LineHistory history)
        {
            var problem = new LineReadings
            {
                WindowFrames = history.WindowFrames,
            };
            problem.Lines.AddRange(lines);
            for (int i = 0; i < lines.Count; i++)
            {
                IReadOnlyList<LineObservation> samples = history.SamplesOf(lines[i].m_Id);
                var copy = new LineObservation[samples.Count];
                for (int s = 0; s < copy.Length; s++)
                {
                    copy[s] = samples[s];
                }

                problem.Samples[lines[i].m_Id] = copy;
            }

            return problem;
        }

        // Rebuilds the window from the captured readings — in frame order across lines,
        // as the save restore does, because Record treats an older frame as a rewound
        // clock — and hands every line its averages, whole-window and per period.
        public static void ApplyWindow(LineReadings problem)
        {
            var history = new LineHistory(problem.WindowFrames);
            var flat = new List<(int line, LineObservation sample)>();
            foreach (KeyValuePair<int, LineObservation[]> entry in problem.Samples)
            {
                for (int i = 0; i < entry.Value.Length; i++)
                {
                    flat.Add((entry.Key, entry.Value[i]));
                }
            }

            flat.Sort(static (a, b) =>
            {
                int byFrame = a.sample.m_Frame.CompareTo(b.sample.m_Frame);
                return byFrame != 0 ? byFrame : a.line.CompareTo(b.line);
            });
            for (int i = 0; i < flat.Count; i++)
            {
                history.Record(flat[i].line, flat[i].sample);
            }

            for (int i = 0; i < problem.Lines.Count; i++)
            {
                ApplyWindow(history, problem.Lines[i]);
            }
        }

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
