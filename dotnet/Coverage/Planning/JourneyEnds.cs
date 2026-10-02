using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // Points already snapped to one pedestrian network, by their exact position. A city
    // of a hundred thousand makes a hundred and fifty thousand journeys between a few
    // tens of thousands of doors, and every demand refresh used to snap both ends of
    // every journey afresh: one nearest-node search per end, the same answer for the
    // same door each time. Remembered, a refresh searches only for the doors it has
    // not seen, and the rest is a lookup.
    //
    // Bound to ONE index: a snapped node only means anything in the numbering of the
    // graph it was snapped to (TileSnap.Graph), so a new snap needs a new memo, which
    // For decides. Not thread-safe; one demand stage at a time owns it.
    internal sealed class SnapMemo
    {
        private readonly Dictionary<(float X, float Z), (int Node, int WalkMs)> m_Known =
            new Dictionary<(float X, float Z), (int Node, int WalkMs)>();

        private readonly int m_Capacity;

        public SnapMemo(WalkNodeIndex index, int accessMs, int capacity)
        {
            Index = index;
            AccessMs = accessMs;
            m_Capacity = Math.Max(1, capacity);
        }

        public WalkNodeIndex Index { get; }

        public int AccessMs { get; }

        // The memo to snap with on `index`: the one already held when it was made for
        // that very index and access walk, otherwise a fresh one. Another index is
        // another graph's numbering, and an answer remembered from it would point at a
        // node of a network that no longer exists.
        public static SnapMemo For(SnapMemo? held, WalkNodeIndex index, int accessMs, int capacity)
        {
            return held is not null && ReferenceEquals(held.Index, index) && held.AccessMs == accessMs
                ? held
                : new SnapMemo(index, accessMs, capacity);
        }

        public int Count => m_Known.Count;

        // Searches since the memo was made or last emptied: the doors it had not seen.
        public int Searches { get; private set; }

        // WalkAccess.SnapPoint, remembered. The same position gives the same node and
        // walk the search would, because it IS the search's answer, kept.
        public int Snap(float x, float z, out int walkMs)
        {
            if (m_Known.TryGetValue((x, z), out (int Node, int WalkMs) known))
            {
                walkMs = known.WalkMs;
                return known.Node;
            }

            // Bounded by the doors of one city, but a long session on one network keeps
            // meeting new ones as buildings are replaced; past the cap it starts over
            // rather than growing.
            if (m_Known.Count >= m_Capacity)
            {
                m_Known.Clear();
            }

            int node = WalkAccess.SnapPoint(Index, x, z, AccessMs, out walkMs);
            m_Known.Add((x, z), (node, walkMs));
            Searches++;
            return node;
        }

        public void ResetSearches() => Searches = 0;
    }

    // Every journey's two ends snapped to the pedestrian network, index-aligned with
    // the journey list: what the coverage measure reads. The arrays are kept from one
    // refresh to the next and only grow, so Count, not their length, is how many
    // journeys they hold.
    internal sealed class JourneyEnds
    {
        public int[] OriginNode = Array.Empty<int>();
        public int[] OriginAccessMs = Array.Empty<int>();
        public int[] DestinationNode = Array.Empty<int>();
        public int[] DestinationAccessMs = Array.Empty<int>();
        public float[] Weight = Array.Empty<float>();
        public int Count;

        // Snaps into `reuse` when it is given, growing its arrays only when the
        // journeys outnumber them: a hundred and fifty thousand journeys are five
        // arrays of several hundred kilobytes each, and the Mono collector stops every
        // thread to reclaim them.
        public static JourneyEnds Snap(List<Journey> journeys, SnapMemo memo, JourneyEnds? reuse)
        {
            int count = journeys.Count;
            JourneyEnds ends = reuse ?? new JourneyEnds();
            if (ends.Weight.Length < count)
            {
                ends.OriginNode = new int[count];
                ends.OriginAccessMs = new int[count];
                ends.DestinationNode = new int[count];
                ends.DestinationAccessMs = new int[count];
                ends.Weight = new float[count];
            }

            ends.Count = count;
            for (int i = 0; i < count; i++)
            {
                Journey trip = journeys[i];
                ends.OriginNode[i] = memo.Snap(trip.m_Origin.x, trip.m_Origin.y, out ends.OriginAccessMs[i]);
                ends.DestinationNode[i] = memo.Snap(trip.m_Destination.x, trip.m_Destination.y, out ends.DestinationAccessMs[i]);
                ends.Weight[i] = trip.m_Weight;
            }

            return ends;
        }
    }
}
