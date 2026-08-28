using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // One reading of a line, stamped with the simulation frame it was taken on.
    //
    // Passengers and capacity are counts at that instant; interval is the achieved
    // headway in seconds. Nothing here is an average yet.
    internal struct LineObservation
    {
        public uint m_Frame;
        public int m_Passengers;
        public int m_Capacity;
        public float m_IntervalSeconds;
        public int m_Vehicles;
    }

    // What a line looked like across the window, rather than at the instant the
    // refresh happened to land.
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
        public int m_Samples;
        // Frames between the oldest and newest sample. Says how much of the window
        // is actually covered, so a verdict drawn from ten minutes of game time is
        // not reported as if it came from a full day.
        public uint m_SpanFrames;
    }

    // A rolling window of line readings, kept over GAME time.
    //
    // Every quantity the game exposes about a running line — passengers aboard, the
    // achieved vehicle interval, the fleet actually in service — is an instantaneous
    // count. Judging a line on one reading condemns a ferry whose only boat happens
    // to be mid-crossing as "nearly empty", and calls a bus packed for the morning
    // peak healthy at midnight. The window is what turns those readings into
    // something a verdict can rest on.
    //
    // Measured in simulation frames rather than real seconds on purpose: frames stop
    // advancing when the game is paused and advance faster when the player fast
    // forwards, so a window of 24 game hours stays 24 game hours either way. A wall
    // clock would drain the window while the player sat in the pause menu.
    internal sealed class LineHistory
    {
        // Game.Simulation.TimeSystem.kTicksPerDay. One simulation frame is one tick,
        // so a game day is this many frames whatever speed the player is running at.
        public const uint FramesPerGameDay = 262144u;

        // Enough readings that one outlier cannot carry a verdict on its own. Below
        // this the caller is told to fall back to the instantaneous reading rather
        // than be handed a mean of two samples dressed up as a day's evidence.
        public const int MinSamplesForVerdict = 4;

        // Per line, so a player who leaves the game running overnight at high speed
        // cannot grow these without bound. At the default sampling cadence this is
        // far more than a day holds; it is a backstop, not a tuning knob.
        private const int MaxSamplesPerLine = 512;

        private readonly Dictionary<int, List<LineObservation>> m_ByLine =
            new Dictionary<int, List<LineObservation>>();
        private readonly uint m_WindowFrames;
        private uint m_NewestFrame;
        private int m_Evicted;
        private int m_Dropped;

        public LineHistory(uint windowFrames)
        {
            m_WindowFrames = windowFrames == 0u ? FramesPerGameDay : windowFrames;
        }

        public uint WindowFrames => m_WindowFrames;

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

        // Wipes the history. Loading a different save rewinds the frame counter, and
        // readings from the previous city must not be averaged into this one.
        public void Clear()
        {
            m_ByLine.Clear();
            m_NewestFrame = 0u;
            ClearCounters();
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
            if (samples.Count > MaxSamplesPerLine)
            {
                samples.RemoveAt(0);
                m_Dropped++;
            }

            Evict();
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
            if (!m_ByLine.TryGetValue(lineId, out List<LineObservation>? samples) || samples.Count == 0)
            {
                return false;
            }

            float passengers = 0f;
            float capacity = 0f;
            float interval = 0f;
            float vehicles = 0f;
            float usage = 0f;
            float peakUsage = 0f;
            uint oldest = samples[0].m_Frame;
            uint newest = samples[0].m_Frame;

            for (int i = 0; i < samples.Count; i++)
            {
                LineObservation sample = samples[i];
                passengers += sample.m_Passengers;
                capacity += sample.m_Capacity;
                interval += sample.m_IntervalSeconds;
                vehicles += sample.m_Vehicles;

                // Per sample, because the fleet can change size mid-window and a
                // ratio of sums would then silently weight the busiest hour by
                // however many vehicles were running in it.
                float sampleUsage = sample.m_Capacity > 0
                    ? sample.m_Passengers / (float)sample.m_Capacity
                    : 0f;
                usage += sampleUsage;
                if (sampleUsage > peakUsage)
                {
                    peakUsage = sampleUsage;
                }

                if (sample.m_Frame < oldest)
                {
                    oldest = sample.m_Frame;
                }

                if (sample.m_Frame > newest)
                {
                    newest = sample.m_Frame;
                }
            }

            float count = samples.Count;
            average.m_Passengers = passengers / count;
            average.m_Capacity = capacity / count;
            average.m_IntervalSeconds = interval / count;
            average.m_Vehicles = vehicles / count;
            average.m_Usage = usage / count;
            average.m_PeakUsage = peakUsage;
            average.m_Samples = samples.Count;
            average.m_SpanFrames = newest - oldest;
            return true;
        }

        // Fraction of a game day the window actually holds, so a caller can say "6 h
        // of 24 h" rather than implying a full day of evidence.
        public static float GameHours(uint frames)
        {
            return frames / (float)FramesPerGameDay * 24f;
        }

        private void Evict()
        {
            // Subtracting on unsigned would wrap in the opening frames of a city,
            // where the newest frame is smaller than the window itself.
            if (m_NewestFrame <= m_WindowFrames)
            {
                return;
            }

            uint cutoff = m_NewestFrame - m_WindowFrames;
            foreach (KeyValuePair<int, List<LineObservation>> entry in m_ByLine)
            {
                List<LineObservation> samples = entry.Value;
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
}
