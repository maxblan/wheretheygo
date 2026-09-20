using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // One reading of a line, stamped with the simulation frame it was taken on.
    //
    // Passengers and capacity are counts at that instant; interval is the game's
    // TransportLine.m_VehicleInterval (see ExistingLine.m_VehicleInterval for what that
    // is and is not). Nothing here is an average yet.
    internal struct LineObservation
    {
        public uint m_Frame;
        public int m_Passengers;
        public int m_Capacity;
        public float m_IntervalSeconds;
        public int m_Vehicles;
        // The game clock at the reading, as a day fraction (Daytime.IsNight).
        public float m_TimeOfDay;

        // A reading with vehicles out. An inactive line (a day-only line at night, no
        // active buildings) reads capacity 0 and says nothing about demand, so it is
        // left out of every mean (register A8.4, decided 2026-09-06).
        public readonly bool Active => m_Capacity > 0;
    }

    // What a line looked like across the window, rather than at the instant the
    // refresh happened to land, taken over the ACTIVE readings.
    internal struct LineAverage
    {
        public float m_Passengers;
        public float m_Capacity;
        public float m_IntervalSeconds;
        public float m_Vehicles;
        // Usage computed per sample and then averaged, which is not the same as
        // averaged passengers over averaged capacity once the fleet changes size
        // during the window.
        public float m_Usage;
        // Highest usage any single sample in the window reached. A line that is
        // packed at the peak and empty at night is not the same as one that is
        // uniformly half empty, and the mean alone cannot tell them apart.
        public float m_PeakUsage;
        // The planning load: the PlanningLoadQuantile (nearest rank) of the passengers
        // aboard, and the most anyone counted. Riders, not shares, so a fleet can be
        // sized from it without dividing a windowed share by an instantaneous capacity.
        public int m_PlanningLoad;
        public int m_MaxAboard;
        // Active readings the means rest on, and every reading the window holds.
        public int m_Samples;
        public int m_Readings;
        // Frames between the oldest and newest reading. Says how much of the window
        // is actually covered, so a verdict drawn from ten minutes of game time is
        // not reported as if it came from a full day.
        public uint m_SpanFrames;
    }

    // A rolling window of line readings, kept over GAME time.
    //
    // Every quantity the game exposes about a running line (passengers aboard, the
    // vehicle interval, the fleet actually in service) is an instantaneous count.
    // Judging a line on one reading condemns a ferry whose only boat happens to be
    // mid-crossing as "nearly empty", and calls a bus packed for the morning peak
    // healthy at midnight. The window is what turns those readings into something a
    // verdict can rest on.
    //
    // Measured in simulation frames rather than real seconds on purpose: frames stop
    // advancing when the game is paused and advance faster when the player fast
    // forwards, so a window of 24 game hours stays 24 game hours either way. A wall
    // clock would drain the window while the player sat in the pause menu. Readings
    // are taken every ReadingIntervalFrames for the same reason (A8.5).
    internal sealed class LineHistory
    {
        private readonly Dictionary<int, List<LineObservation>> m_ByLine =
            new Dictionary<int, List<LineObservation>>();
        private readonly uint m_WindowFrames;
        private uint m_NewestFrame;
        private int m_Evicted;
        private int m_Dropped;

        public LineHistory(uint windowFrames)
        {
            m_WindowFrames = windowFrames == 0u ? Assumptions.FramesPerGameDay : windowFrames;
        }

        public uint WindowFrames => m_WindowFrames;

        public uint NewestFrame => m_NewestFrame;

        // The oldest reading still held, over every line: how far back the window
        // actually reaches, as against how far it is allowed to. Reported after a
        // restore, because "the data basis was not saved" and "the window has moved on
        // since the save" look identical from the panel and are not the same thing.
        public uint SpanFrames
        {
            get
            {
                uint oldest = m_NewestFrame;
                foreach (KeyValuePair<int, List<LineObservation>> line in m_ByLine)
                {
                    if (line.Value.Count > 0 && line.Value[0].m_Frame < oldest)
                    {
                        oldest = line.Value[0].m_Frame;
                    }
                }

                return m_NewestFrame - oldest;
            }
        }

        // Samples pushed out of the far end of the window, and samples discarded at
        // the per-line cap, since the last time the counters were read. Logged so a
        // window that is quietly truncating says so.
        public int EvictedSinceLastReport => m_Evicted;

        public int DroppedAtCapSinceLastReport => m_Dropped;

        public void ClearCounters()
        {
            m_Evicted = 0;
            m_Dropped = 0;
        }

        public int TrackedLines => m_ByLine.Count;

        // The window's contents for the save file: every tracked line and its samples in
        // recording order. Restore by re-recording the samples in frame order
        // (Record clears the window on a frame older than the newest it has seen).
        public IEnumerable<int> LineIds => m_ByLine.Keys;

        public IReadOnlyList<LineObservation> SamplesOf(int lineId)
        {
            return m_ByLine.TryGetValue(lineId, out List<LineObservation>? samples) ? samples : Array.Empty<LineObservation>();
        }

        // Wipes the history. Loading a different save rewinds the frame counter, and
        // readings from the previous city must not be averaged into this one.
        // What one line carried in each hour of the day, over every reading in the
        // window: the mean riders aboard and the mean seats offered. Hours the window
        // never saw come back with no samples, which the panel draws as a gap rather
        // than as an empty hour: a line nobody has watched at 03:00 is not a line
        // nobody rides at 03:00.
        //
        // Only readings with vehicles out count (LineObservation.Active): a reading
        // taken while the line stood still says nothing about its load.
        public void HourlyLoad(int lineId, float[] riders, float[] capacity, int[] samples)
        {
            Array.Clear(riders, 0, riders.Length);
            Array.Clear(capacity, 0, capacity.Length);
            Array.Clear(samples, 0, samples.Length);
            IReadOnlyList<LineObservation> observations = SamplesOf(lineId);
            for (int i = 0; i < observations.Count; i++)
            {
                LineObservation observation = observations[i];
                if (!observation.Active)
                {
                    continue;
                }

                int hour = Daytime.HourOf(observation.m_TimeOfDay);
                if (hour < 0 || hour >= samples.Length)
                {
                    continue;
                }

                riders[hour] += observation.m_Passengers;
                capacity[hour] += observation.m_Capacity;
                samples[hour]++;
            }

            for (int hour = 0; hour < samples.Length; hour++)
            {
                if (samples[hour] > 0)
                {
                    riders[hour] /= samples[hour];
                    capacity[hour] /= samples[hour];
                }
            }
        }

        public void Clear()
        {
            m_ByLine.Clear();
            m_NewestFrame = 0u;
            ClearCounters();
        }

        // Whether a reading taken at `frame` is due: the first ever, or one reading
        // interval since the newest (A8.5, 96 a game day). A rewound clock is due too,
        // and Record then restarts the window.
        public bool ReadingDue(uint frame)
        {
            return m_NewestFrame == 0u || frame < m_NewestFrame || frame - m_NewestFrame >= Assumptions.ReadingIntervalFrames;
        }

        public void Record(int lineId, LineObservation observation)
        {
            // The simulation frame only ever counts up within one city. Going
            // backwards means a different save was loaded, and mixing the two
            // cities' readings would be worse than starting the window again.
            if (observation.m_Frame < m_NewestFrame)
            {
                Clear();
            }

            if (observation.m_Frame > m_NewestFrame)
            {
                m_NewestFrame = observation.m_Frame;
            }

            if (!m_ByLine.TryGetValue(lineId, out List<LineObservation>? samples))
            {
                samples = new List<LineObservation>();
                m_ByLine[lineId] = samples;
            }

            samples.Add(observation);
            if (samples.Count > Assumptions.LineReadingsCap)
            {
                samples.RemoveAt(0);
                m_Dropped++;
            }

            // Only this line's list. Every live line is recorded on every refresh, so
            // each one is trimmed as it is written; sweeping all of them on every
            // Record made one refresh of N lines cost N squared list scans.
            Evict(samples);
        }

        // Forgets lines that no longer exist, so a city where the player keeps
        // deleting and rebuilding does not accumulate history for ever.
        public void RetainOnly(HashSet<int> liveLineIds)
        {
            if (liveLineIds is null || m_ByLine.Count == 0)
            {
                return;
            }

            List<int>? stale = null;
            foreach (KeyValuePair<int, List<LineObservation>> entry in m_ByLine)
            {
                if (!liveLineIds.Contains(entry.Key))
                {
                    stale ??= new List<int>();
                    stale.Add(entry.Key);
                }
            }

            if (stale is null)
            {
                return;
            }

            for (int i = 0; i < stale.Count; i++)
            {
                _ = m_ByLine.Remove(stale[i]);
            }
        }

        public bool TryAverage(int lineId, out LineAverage average)
        {
            average = default;
            return m_ByLine.TryGetValue(lineId, out List<LineObservation>? samples) && Average(samples, out average);
        }

        // The same averages over the readings of one period only, where night is the
        // game's 22:00–06:00.
        public bool TryAveragePeriod(int lineId, bool night, out LineAverage average)
        {
            average = default;
            if (!m_ByLine.TryGetValue(lineId, out List<LineObservation>? all))
            {
                return false;
            }

            var samples = new List<LineObservation>();
            for (int i = 0; i < all.Count; i++)
            {
                if (Daytime.IsNight(all[i].m_TimeOfDay) == night)
                {
                    samples.Add(all[i]);
                }
            }

            return Average(samples, out average);
        }

        // Means over the active readings, in recording order; false when the window
        // holds no active reading. The span covers every reading, active or not.
        private static bool Average(List<LineObservation> samples, out LineAverage average)
        {
            average = default;
            if (samples.Count == 0)
            {
                return false;
            }

            float passengers = 0f;
            float capacity = 0f;
            float interval = 0f;
            float vehicles = 0f;
            float usage = 0f;
            float peakUsage = 0f;
            int maxAboard = 0;
            uint oldest = samples[0].m_Frame;
            uint newest = samples[0].m_Frame;
            var aboard = new List<int>(samples.Count);

            for (int i = 0; i < samples.Count; i++)
            {
                LineObservation sample = samples[i];
                if (sample.m_Frame < oldest)
                {
                    oldest = sample.m_Frame;
                }

                if (sample.m_Frame > newest)
                {
                    newest = sample.m_Frame;
                }

                if (!sample.Active)
                {
                    continue;
                }

                passengers += sample.m_Passengers;
                capacity += sample.m_Capacity;
                interval += sample.m_IntervalSeconds;
                vehicles += sample.m_Vehicles;
                aboard.Add(sample.m_Passengers);
                maxAboard = Math.Max(maxAboard, sample.m_Passengers);

                // Per sample, because the fleet can change size mid-window and a
                // ratio of sums would then silently weight the busiest hour by
                // however many vehicles were running in it.
                float sampleUsage = sample.m_Passengers / (float)sample.m_Capacity;
                usage += sampleUsage;
                if (sampleUsage > peakUsage)
                {
                    peakUsage = sampleUsage;
                }
            }

            if (aboard.Count == 0)
            {
                return false;
            }

            float count = aboard.Count;
            average.m_Passengers = passengers / count;
            average.m_Capacity = capacity / count;
            average.m_IntervalSeconds = interval / count;
            average.m_Vehicles = vehicles / count;
            average.m_Usage = usage / count;
            average.m_PeakUsage = peakUsage;
            average.m_PlanningLoad = Quantile(aboard, Assumptions.PlanningLoadQuantile);
            average.m_MaxAboard = maxAboard;
            average.m_Samples = aboard.Count;
            average.m_Readings = samples.Count;
            average.m_SpanFrames = newest - oldest;
            return true;
        }

        // Nearest-rank quantile (A8.4): the value at position ⌈q·n⌉ of the sorted
        // values, no interpolation, so the result is always a count someone recorded.
        public static int Quantile(List<int> values, float q)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            var sorted = new List<int>(values);
            sorted.Sort();
            int rank = (int)Math.Ceiling(q * sorted.Count);
            rank = Math.Max(1, Math.Min(sorted.Count, rank));
            return sorted[rank - 1];
        }

        // Fraction of a game day the window actually holds, so a caller can say "6 h
        // of 24 h" rather than implying a full day of evidence.
        public static float GameHours(uint frames)
        {
            return frames / (float)Assumptions.FramesPerGameDay * 24f;
        }

        private void Evict(List<LineObservation> samples)
        {
            // Subtracting on unsigned would wrap in the opening frames of a city,
            // where the newest frame is smaller than the window itself.
            if (m_NewestFrame <= m_WindowFrames)
            {
                return;
            }

            uint cutoff = m_NewestFrame - m_WindowFrames;
            int keepFrom = 0;
            while (keepFrom < samples.Count && samples[keepFrom].m_Frame < cutoff)
            {
                keepFrom++;
            }

            if (keepFrom > 0)
            {
                samples.RemoveRange(0, keepFrom);
                m_Evicted += keepFrom;
            }
        }
    }
}
