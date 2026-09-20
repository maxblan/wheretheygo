using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // One journey seen starting in the live city: where it began, where it was
    // headed, why, and the simulation frame it was first observed on.
    internal struct ObservedTrip
    {
        public uint m_Frame;
        public float m_OriginX;
        public float m_OriginZ;
        public float m_DestinationX;
        public float m_DestinationZ;
        public byte m_Purpose;
        // The game clock when the journey was seen, as a day fraction (Daytime).
        public float m_TimeOfDay;
    }

    // A rolling window of observed shopping and leisure journeys (register A0.1).
    //
    // The save stores no shopping or leisure destination on a citizen, only the
    // journey currently under way, so this demand has to be WATCHED over time, the
    // way the ridership sampler watches stops. Home-to-work and home-to-school
    // journeys are read whole from the save as one journey per citizen per day; to
    // sit beside them on equal footing (A0.1: every purpose weighs the same), the
    // observed journeys are kept for Assumptions.ObservationWindowDays game days and
    // scaled to a per-day rate - down when the window holds several days, up when it
    // is not yet a day long.
    //
    // Frames, not wall-clock seconds, for the same reason as LineHistory: the window
    // must not drain while the game is paused nor shrink when it is fast-forwarded.
    internal sealed class ObservedTripWindow
    {

        private readonly uint m_WindowFrames;
        private readonly List<ObservedTrip> m_Trips = new List<ObservedTrip>();
        private readonly int[] m_ByPurpose = new int[256];
        private int m_Evicted;
        private int m_Dropped;

        public ObservedTripWindow(uint windowFrames)
        {
            m_WindowFrames = windowFrames == 0u ? Assumptions.FramesPerGameDay : windowFrames;
        }

        public int Count => m_Trips.Count;

        // The window's contents for the save file, oldest first; restore with Record.
        public IReadOnlyList<ObservedTrip> Trips => m_Trips;
        public int EvictedSinceLastReport => m_Evicted;
        public int DroppedAtCapSinceLastReport => m_Dropped;

        public int CountOf(byte purpose)
        {
            return m_ByPurpose[purpose];
        }

        // Frames between the oldest and the newest trip held; 0 with fewer than two.
        public uint SpanFrames => m_Trips.Count < 2 ? 0u : m_Trips[m_Trips.Count - 1].m_Frame - m_Trips[0].m_Frame;

        public ObservedTrip this[int index] => m_Trips[index];

        public void ClearCounters()
        {
            m_Evicted = 0;
            m_Dropped = 0;
        }

        // Loading a different save rewinds the frame counter; its journeys must go.
        public void Clear()
        {
            m_Trips.Clear();
            Array.Clear(m_ByPurpose, 0, m_ByPurpose.Length);
        }

        // Frames only count up within one city; a frame below the newest held means
        // a different save, and the window starts over rather than mixing two cities.
        public void Record(ObservedTrip trip)
        {
            if (m_Trips.Count > 0 && trip.m_Frame < m_Trips[m_Trips.Count - 1].m_Frame)
            {
                Clear();
            }

            if (m_Trips.Count >= Assumptions.ObservedTripCapacity)
            {
                m_Dropped++;
                return;
            }

            m_Trips.Add(trip);
            m_ByPurpose[trip.m_Purpose]++;
        }

        // Drops everything older than the window as of `frame`. Trips are held in
        // recording order, which is frame order, so this is a prefix removal.
        public void Prune(uint frame)
        {
            uint oldest = frame > m_WindowFrames ? frame - m_WindowFrames : 0u;
            int keep = 0;
            while (keep < m_Trips.Count && m_Trips[keep].m_Frame < oldest)
            {
                m_ByPurpose[m_Trips[keep].m_Purpose]--;
                keep++;
            }

            if (keep > 0)
            {
                m_Trips.RemoveRange(0, keep);
                m_Evicted += keep;
            }
        }

        // Weight per observed trip so the window reads as ONE day's demand: day/span,
        // so three days of observations weigh a third each and half a day's weigh two,
        // capped at Assumptions.ObservedTripMaxDayScale so a few minutes of readings are
        // not multiplied into a full day. With fewer than two trips there is no span, so 1.
        //
        // No floor at 1: the window was widened to three days on 2026-09-14 and a floor
        // then made every shopping and leisure journey count three times against the
        // commutes read from the save.
        public float ScaleFor(uint dayFrames)
        {
            uint span = SpanFrames;
            if (span == 0u || dayFrames == 0u)
            {
                return 1f;
            }

            double scale = (double)dayFrames / span;
            return (float)Math.Min(Assumptions.ObservedTripMaxDayScale, scale);
        }
    }
}
