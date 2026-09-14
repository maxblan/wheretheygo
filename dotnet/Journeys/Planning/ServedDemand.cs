using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // F3, steps 3 and 4 (formal-specification.md §7b): which served stop each 256 m
    // zone reaches, the routable stop pairs the zone flows make, and the discount of
    // the journeys the existing network already carries. Holds the zone-indexed and
    // pair arrays between a demand refresh's model build and its discount; the
    // system logs what TryDiscount reports.
    internal sealed class ServedDemand
    {

        private int[]? m_ZoneStops;
        private float[]? m_ZoneStopDistSq;
        private float[]? m_ZoneCentreX;
        private float[]? m_ZoneCentreZ;
        // Which zone flow each pair came from, so a measured journey time can be
        // written back against the flow it belongs to.
        private int[]? m_PairOrigins;

        private int[]? m_PairDests;

        // Which zone flow each pair came from, so the served-demand discount can write
        // back to it without re-deriving the mapping the pass above already did.
        private int[]? m_PairFlow;

        private int m_PairCount;

        // Carried journeys from the discount's first pass: which pair, and how long the
        // existing network takes over it. Held so the ceiling can be derived from the
        // whole set before any weight is touched.
        private int[]? m_ServedPairs;

        private float[]? m_ServedSeconds;

        // A copy for the median, because selecting it reorders what it is given and the
        // second pass still needs the travel times in journey order.
        private float[]? m_ServedScratch;


        public int PairCount => m_PairCount;

        // The stop each zone was mapped to; -1 where a zone has no stop in reach. Read
        // by the harness, which checks the mapping through this rather than through
        // the discount it feeds.
        public int[]? ZoneStops => m_ZoneStops;

        // Nearest stop to each zone centre, within walking distance. A zone with no
        // stop nearby simply cannot use the network.
        public void MapZonesToStops(float[] xs, float[] zs, float2Like worldMin, int2Like zoneGrid)
        {
            int zoneCount = zoneGrid.x * zoneGrid.y;
            // Every co-allocated array is in the condition, or flow analysis only
            // trusts the one that was tested.
            if (m_ZoneStops is null || m_ZoneStopDistSq is null || m_ZoneCentreX is null
                || m_ZoneCentreZ is null || m_ZoneStops.Length != zoneCount)
            {
                m_ZoneStops = new int[zoneCount];
                m_ZoneStopDistSq = new float[zoneCount];
                m_ZoneCentreX = new float[zoneCount];
                m_ZoneCentreZ = new float[zoneCount];
            }

            float radiusSq = Assumptions.ZoneStopReachMetres * Assumptions.ZoneStopReachMetres;
            for (int zone = 0; zone < zoneCount; zone++)
            {
                float2Like centre = DemandZones.ZoneCentre(zone, worldMin, zoneGrid);
                int best = -1;
                float bestSq = radiusSq;
                for (int i = 0; i < xs.Length; i++)
                {
                    float dx = xs[i] - centre.x;
                    float dz = zs[i] - centre.y;
                    float distSq = dx * dx + dz * dz;
                    if (distSq < bestSq)
                    {
                        bestSq = distSq;
                        best = i;
                    }
                }

                m_ZoneStops[zone] = best;
                // Kept so a candidate's own stops can be judged against the incumbent
                // rather than only filling in zones that had nothing, and so the walk
                // to the stop can be charged rather than given away.
                m_ZoneStopDistSq[zone] = best >= 0 ? bestSq : 0f;
                m_ZoneCentreX[zone] = centre.x;
                m_ZoneCentreZ[zone] = centre.y;
            }
        }

        // Flattens the zone flows into the stop-indexed arrays the transit router
        // takes, keeping them grouped by origin so one search serves a run of pairs.
        public void BuildPairs(List<ZoneFlow> flows)
        {
            if (m_ZoneStops is null)
            {
                return;
            }

            int[] zoneStops = m_ZoneStops;
            int count = flows.Count;
            if (m_PairOrigins is null || m_PairDests is null || m_PairFlow is null
                || m_PairOrigins.Length < count)
            {
                m_PairOrigins = new int[count];
                m_PairDests = new int[count];
                m_PairFlow = new int[count];
            }

            m_PairCount = 0;
            for (int i = 0; i < count; i++)
            {
                ZoneFlow flow = flows[i];
                int origin = zoneStops[flow.m_Origin];
                int destination = zoneStops[flow.m_Destination];
                if (origin < 0 || destination < 0 || origin == destination)
                {
                    continue;
                }

                m_PairOrigins[m_PairCount] = origin;
                m_PairDests[m_PairCount] = destination;
                m_PairFlow[m_PairCount] = i;
                m_PairCount++;
            }
        }

        // Unserved demand, decided by ROUTING each journey over the existing network
        // rather than by how close its ends are to a stop. A journey the network can
        // already carry within a reasonable time is discounted in place; one it cannot
        // is left at full weight to drive a suggestion.
        //
        // This replaces an endpoint-coverage approximation that under-discounted trips
        // between two well-served places that no single service connects.
        // False until MapZonesToStops and BuildPairs have run; the caller logs the report.
        public bool TryDiscount(TransitNetwork network, DijkstraWorkspace workspace, List<ZoneFlow> flows, out DiscountReport report)
        {
            report = default;
            if (m_ZoneStopDistSq is null || m_PairOrigins is null || m_PairDests is null || m_PairFlow is null)
            {
                return false;
            }

            // Every journey starts unreachable; the pass below fills in the ones the
            // network can carry. A candidate is later credited only for beating this.
            float[] zoneStopDistSq = m_ZoneStopDistSq;
            int[] pairOrigins = m_PairOrigins;
            int[] pairDests = m_PairDests;
            int[] pairFlow = m_PairFlow;

            if (m_ServedPairs is null || m_ServedSeconds is null || m_ServedScratch is null
                || m_ServedPairs.Length < m_PairCount)
            {
                m_ServedPairs = new int[m_PairCount];
                m_ServedSeconds = new float[m_PairCount];
                m_ServedScratch = new float[m_PairCount];
            }

            int[] servedPairIndex = m_ServedPairs;
            float[] servedSeconds = m_ServedSeconds;
            float[] medianScratch = m_ServedScratch;

            // First pass: find out which journeys the network carries, and how long it
            // takes over each. No weight is touched yet, because the ceiling the
            // discount is measured against comes from this whole set.
            int servedPairs = 0;
            int currentOrigin = -1;
            for (int i = 0; i < m_PairCount; i++)
            {
                int origin = pairOrigins[i];
                int destination = pairDests[i];

                // Pairs arrive grouped by origin, so one search serves a run of them.
                if (origin != currentOrigin)
                {
                    currentOrigin = origin;
                    workspace.Run(network.Graph, origin, Assumptions.MaxJourneySeconds);
                }

                if (!TransitGraph.Inspect(network, workspace, origin, destination,
                        -1, out int boardings, out bool _, out float travelTime))
                {
                    continue;
                }

                if (boardings <= 0)
                {
                    continue;
                }

                // Door to door: the walk to the stop and from it are part of the
                // journey, and used to be free — so a stop 490 m away looked exactly as
                // good as one on the doorstep.
                ZoneFlow served = flows[pairFlow[i]];
                float doorToDoor = travelTime
                    + WalkSeconds(zoneStopDistSq[served.m_Origin])
                    + WalkSeconds(zoneStopDistSq[served.m_Destination]);

                servedPairIndex[servedPairs] = pairFlow[i];
                servedSeconds[servedPairs] = doorToDoor;
                servedPairs++;
            }

            Array.Copy(servedSeconds, medianScratch, servedPairs);
            float ceiling = TransitGraph.ServedCeiling(
                medianScratch, servedPairs, Assumptions.ServedCeilingMultiple,
                Assumptions.MaxJourneySeconds, Assumptions.MinPairsForServedMedian, out float medianSeconds);

            // Second pass: how much of each carried journey the network absorbs. One it
            // handles in a fraction of the city's typical transit journey drops out
            // almost entirely; one taking three times that keeps its whole weight,
            // because it still deserves a better option.
            float weightBefore = 0f;
            float weightAfter = 0f;
            for (int i = 0; i < servedPairs; i++)
            {
                ZoneFlow flow = flows[servedPairIndex[i]];
                weightBefore += flow.m_Weight;
                flow.m_Weight *= Math.Min(1f, Math.Max(0f, servedSeconds[i] / ceiling));
                weightAfter += flow.m_Weight;
                flows[servedPairIndex[i]] = flow;
            }

            report = new DiscountReport
            {
                ServedPairs = servedPairs,
                PairCount = m_PairCount,
                MedianSeconds = medianSeconds,
                CeilingSeconds = ceiling,
                WeightBefore = weightBefore,
                WeightAfter = weightAfter,
            };
            return true;
        }

        // What the zone flows still carry after the served-demand discount. Summed
        // rather than tracked, because the discount touches only the pairs the network
        // can route and leaves the rest at full weight.
        public static float RemainingWeight(List<ZoneFlow> flows)
        {
            float remaining = 0f;
            for (int i = 0; i < flows.Count; i++)
            {
                remaining += flows[i].m_Weight;
            }

            return remaining;
        }

        // The longest journey the city actually makes, end to end in metres. Stands in
        // for the city's own scale: a map is mostly empty, and the span of the built
        // area is what decides whether a given line counts as short.
        //
        // Over the flows rather than over every pair of zones — one distance each, and
        // a zone nobody travels to says nothing about how far people go.
        public float LongestJourneyMetres(List<ZoneFlow> flows)
        {
            if (m_ZoneCentreX is null || m_ZoneCentreZ is null)
            {
                return 0f;
            }

            float[] centreX = m_ZoneCentreX;
            float[] centreZ = m_ZoneCentreZ;
            float longest = 0f;
            for (int i = 0; i < flows.Count; i++)
            {
                ZoneFlow flow = flows[i];
                if (flow.m_Origin < 0 || flow.m_Origin >= centreX.Length
                    || flow.m_Destination < 0 || flow.m_Destination >= centreX.Length)
                {
                    continue;
                }

                float dx = centreX[flow.m_Origin] - centreX[flow.m_Destination];
                float dz = centreZ[flow.m_Origin] - centreZ[flow.m_Destination];
                longest = Math.Max(longest, (float)Math.Sqrt((dx * dx) + (dz * dz)));
            }

            return longest;
        }

        // Seconds spent walking to a stop that far away. The model's own walking speed,
        // the one it already uses between stops.
        public static float WalkSeconds(float distanceSq)
        {
            return (float)Math.Sqrt(distanceSq) / Assumptions.WalkSpeed;
        }
    }

    // What one discount pass did, for the log.
    internal struct DiscountReport
    {
        public int ServedPairs;
        public int PairCount;
        public float MedianSeconds;
        public float CeilingSeconds;
        public float WeightBefore;
        public float WeightAfter;
    }
}
