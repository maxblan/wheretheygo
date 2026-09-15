using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // Why somebody travels. The four the mod can tell apart: the first two are read
    // out of the save (Worker/Student), the other two are what the observation sees
    // (Game.Citizens.Purpose, several of whose members are all "leisure" here).
    internal enum JourneyPurpose : byte
    {
        Work = 0,
        School = 1,
        Shopping = 2,
        Leisure = 3,
    }

    // One journey somebody wants to make, and when. Both rides are carried: the hour
    // out and the hour back, because every journey is made twice and a band that only
    // ever pointed one way would be half the truth.
    internal struct Journey
    {
        public float2Like m_Origin;
        public float2Like m_Destination;
        public float m_Weight;
        public JourneyPurpose m_Purpose;
        // Whole hours since midnight (Daytime.HourOf).
        public byte m_OutHour;
        public byte m_BackHour;

        // Share of this journey's two rides that fall in the day period (06:00–22:00).
        public readonly float DayShare => Daytime.DayShareOfHours(m_OutHour, m_BackHour);
    }

    internal static class DemandZones
    {
        // Keeps the trips that are journeys - both ends on the map, in different zones -
        // in a TOTAL order, and returns their weight so the caller can sanity-check the
        // extraction against the city's population.
        //
        // The order matters. The trips arrive from a PARALLEL job through a NativeQueue,
        // so the dequeue order depends on thread scheduling, and everything downstream
        // sums binary32 weights in list order: the pair table, the bands' hourly
        // departures, the riders per line. Float addition is not associative, so an
        // unsorted list gave every refresh slightly different last bits. Sorted on every
        // field a journey has, the whole pipeline is a function of the save.
        public static float Aggregate(
            List<Journey> trips,
            float2Like worldMin,
            int2Like zoneGrid,
            List<Journey> journeys,
            out int tripCount)
        {
            journeys.Clear();
            tripCount = 0;
            float totalWeight = 0f;
            for (int t = 0; t < trips.Count; t++)
            {
                Journey trip = trips[t];
                int origin = ZoneOf(trip.m_Origin, worldMin, zoneGrid);
                int destination = ZoneOf(trip.m_Destination, worldMin, zoneGrid);
                if (origin < 0 || destination < 0 || origin == destination)
                {
                    continue;
                }

                tripCount++;
                totalWeight += trip.m_Weight;
                journeys.Add(trip);
            }

            journeys.Sort(static (a, b) => Compare(a, b));
            return totalWeight;
        }

        private static int Compare(Journey a, Journey b)
        {
            int order = a.m_Origin.x.CompareTo(b.m_Origin.x);
            if (order == 0) { order = a.m_Origin.y.CompareTo(b.m_Origin.y); }
            if (order == 0) { order = a.m_Destination.x.CompareTo(b.m_Destination.x); }
            if (order == 0) { order = a.m_Destination.y.CompareTo(b.m_Destination.y); }
            if (order == 0) { order = ((byte)a.m_Purpose).CompareTo((byte)b.m_Purpose); }
            if (order == 0) { order = a.m_OutHour.CompareTo(b.m_OutHour); }
            if (order == 0) { order = a.m_BackHour.CompareTo(b.m_BackHour); }
            return order != 0 ? order : a.m_Weight.CompareTo(b.m_Weight);
        }

        public static int ZoneOf(float2Like position, float2Like worldMin, int2Like zoneGrid)
        {
            float2Like rel = (position - worldMin) / Assumptions.ZoneSize;
            int x = (int)Math.Floor(rel.x);
            int y = (int)Math.Floor(rel.y);
            if (x < 0 || x >= zoneGrid.x || y < 0 || y >= zoneGrid.y)
            {
                return -1;
            }

            return x + y * zoneGrid.x;
        }
    }
}
