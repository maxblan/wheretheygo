using System.Collections.Generic;

namespace WhereTheyGo
{
    internal static class DoorPairs
    {
        // The journeys as door-to-door pairs: every trip at its own two positions, not
        // a zone centre; trips between the same two doors (one household's commuters to
        // one workplace) are one pair with their summed weight, in first-seen order.
        // Evaluate then searches once per distinct origin door, and the geometry cache
        // on the table is shared by every problem built from it.
        public static RoutingProblem Build(List<Journey> journeys)
        {
            var pairIndex = new Dictionary<(float, float, float, float), int>();
            var ox = new List<float>();
            var oz = new List<float>();
            var dx = new List<float>();
            var dz = new List<float>();
            var weight = new List<float>();
            var dayWeight = new List<float>();
            for (int i = 0; i < journeys.Count; i++)
            {
                Journey trip = journeys[i];
                var key = (trip.m_Origin.x, trip.m_Origin.y, trip.m_Destination.x, trip.m_Destination.y);
                if (pairIndex.TryGetValue(key, out int existing))
                {
                    weight[existing] += trip.m_Weight;
                    dayWeight[existing] += trip.m_Weight * trip.DayShare;
                    continue;
                }

                pairIndex.Add(key, ox.Count);
                ox.Add(trip.m_Origin.x);
                oz.Add(trip.m_Origin.y);
                dx.Add(trip.m_Destination.x);
                dz.Add(trip.m_Destination.y);
                weight.Add(trip.m_Weight);
                dayWeight.Add(trip.m_Weight * trip.DayShare);
            }

            var dayShare = new float[ox.Count];
            for (int i = 0; i < dayShare.Length; i++)
            {
                dayShare[i] = weight[i] > 0f ? dayWeight[i] / weight[i] : 1f;
            }

            var table = new RoutingProblem
            {
                PairCount = ox.Count,
                PairOx = ox.ToArray(),
                PairOz = oz.ToArray(),
                PairDx = dx.ToArray(),
                PairDz = dz.ToArray(),
                PairWeight = weight.ToArray(),
                PairDayShare = dayShare,
            };
            table.Geometry = JourneyRouting.GeometryOf(table);
            return table;
        }

        // The network half of a problem given the pairs it routes: the arrays are
        // shared, not copied, since neither side modifies them once built.
        public static void Attach(RoutingProblem problem, RoutingProblem pairs)
        {
            problem.PairCount = pairs.PairCount;
            problem.PairOx = pairs.PairOx;
            problem.PairOz = pairs.PairOz;
            problem.PairDx = pairs.PairDx;
            problem.PairDz = pairs.PairDz;
            problem.PairWeight = pairs.PairWeight;
            problem.PairDayShare = pairs.PairDayShare;
            problem.Geometry = pairs.Geometry;
        }
    }
}
