using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // One journey somebody wants to make.
    internal struct Trip
    {
        public float2Like m_Origin;
        public float2Like m_Destination;
        public float m_Weight;
        // Share of this journey's rides that fall in the day period (06:00–22:00,
        // Daytime): 1 for a day-shift commute, 0.5 for one whose two rides straddle
        // the night, 0 for a shopping trip seen at 23:00.
        public float m_DayShare;
    }

    // Aggregated demand between two zones. Individual trips are collapsed into
    // these before anything expensive touches them: a city has tens of thousands of
    // trips but only a few hundred zone pairs that matter.
    internal struct ZoneFlow
    {
        public int m_Origin;
        public int m_Destination;
        public float m_Weight;
    }

    internal static class SuitabilityZones
    {

        // Collapses trips into zone-to-zone flows. Returns the total trip weight so
        // the caller can sanity-check the extraction against the city's population.
        public static float Aggregate(
            List<Trip> trips,
            float2Like worldMin,
            int2Like zoneGrid,
            List<ZoneFlow> flows,
            out int tripCount,
            List<Trip>? journeys = null)
        {
            flows.Clear();
            journeys?.Clear();
            tripCount = 0;

            var totals = new Dictionary<long, float>();
            float totalWeight = 0f;
            int zoneCount = zoneGrid.x * zoneGrid.y;

            for (int t = 0; t < trips.Count; t++)
            {
                Trip trip = trips[t];
                int origin = ZoneOf(trip.m_Origin, worldMin, zoneGrid);
                int destination = ZoneOf(trip.m_Destination, worldMin, zoneGrid);
                if (origin < 0 || destination < 0 || origin == destination)
                {
                    continue;
                }

                tripCount++;
                totalWeight += trip.m_Weight;
                // The journeys themselves, for the equity measure: zones are too coarse
                // to say whether a door is within a walk of a stop.
                journeys?.Add(trip);

                long key = (long)origin * zoneCount + destination;
                // Absent key leaves `existing` at zero, which is the wanted starting total.
                _ = totals.TryGetValue(key, out float existing);
                totals[key] = existing + trip.m_Weight;
            }

            foreach (KeyValuePair<long, float> pair in totals)
            {
                flows.Add(new ZoneFlow
                {
                    m_Origin = (int)(pair.Key / zoneCount),
                    m_Destination = (int)(pair.Key % zoneCount),
                    m_Weight = pair.Value,
                });
            }

            // A TOTAL order, not merely grouped by origin.
            //
            // Grouping is what lets the flow assignment run one search per origin zone
            // rather than one per pair. But the trips arrive from a PARALLEL job
            // through a NativeQueue, so the dequeue order depends on thread
            // scheduling; that became the insertion order of `totals`, then its
            // enumeration order, and List.Sort is not stable, so pairs sharing an
            // origin kept whatever order the dictionary happened to yield.
            //
            // Float addition is not associative, so AssignFlow then accumulated
            // slightly different edge flows on every run, and GrowCorridor seeds on a
            // strict `score > seedScore` — a last-bit difference between two
            // near-equal edges flipped which corridor was grown first, and peeling and
            // novelty decay carried that through every later suggestion. Sorting on
            // the destination too makes the whole pipeline a function of the save.
            flows.Sort(static (a, b) =>
            {
                int byOrigin = a.m_Origin.CompareTo(b.m_Origin);
                return byOrigin != 0 ? byOrigin : a.m_Destination.CompareTo(b.m_Destination);
            });
            return totalWeight;
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

        public static float2Like ZoneCentre(int zone, float2Like worldMin, int2Like zoneGrid)
        {
            int x = zone % zoneGrid.x;
            int y = zone / zoneGrid.x;
            return worldMin + new float2Like((x + 0.5f) * Assumptions.ZoneSize, (y + 0.5f) * Assumptions.ZoneSize);
        }

        // Paints straight desire lines between zone pairs into the tile raster, so
        // the demand layer shows where movement wants to happen regardless of what
        // roads exist.
        public static void RasterizeDesireLines(
            List<ZoneFlow> flows,
            float2Like worldMin,
            int2Like zoneGrid,
            int2Like tileGrid,
            float tileSize,
            float[] raster)
        {
            if (raster is null)
            {
                return;
            }

            Array.Clear(raster, 0, raster.Length);

            for (int i = 0; i < flows.Count; i++)
            {
                ZoneFlow flow = flows[i];
                float2Like from = ZoneCentre(flow.m_Origin, worldMin, zoneGrid);
                float2Like to = ZoneCentre(flow.m_Destination, worldMin, zoneGrid);

                float2Like fromTile = (from - worldMin) / tileSize;
                float2Like toTile = (to - worldMin) / tileSize;

                SuitabilityGraphMath.RasterizeSegment(
                    raster, tileGrid.x, tileGrid.y,
                    fromTile.x, fromTile.y, toTile.x, toTile.y,
                    flow.m_Weight);
            }
        }
    }
}
