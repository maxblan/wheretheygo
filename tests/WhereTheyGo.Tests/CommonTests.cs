using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhereTheyGo.Tests
{
    // The pure pieces every feature rests on: the map-plane vector that keeps the core
    // Unity-free, the tile grid, and the zone aggregation the journeys are summed into.
    internal static partial class Program
    {
        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

        private static void AssertBits(uint expected, float actual, string because)
        {
            if (Bits(actual) != unchecked((int)expected))
            {
                throw new TestFailedException(
                    $"{because}: expected bits {expected.ToString("X8", CultureInfo.InvariantCulture)} ({BitConverter.Int32BitsToSingle(unchecked((int)expected)).ToString("R", CultureInfo.InvariantCulture)}), " +
                    $"got {Bits(actual).ToString("X8", CultureInfo.InvariantCulture)} ({actual.ToString("R", CultureInfo.InvariantCulture)})");
            }
        }

        private static void AssertBits(ulong expected, double actual, string because)
        {
            if (BitConverter.DoubleToInt64Bits(actual) != unchecked((long)expected))
            {
                throw new TestFailedException(
                    $"{because}: expected bits {expected.ToString("X16", CultureInfo.InvariantCulture)}, got {BitConverter.DoubleToInt64Bits(actual).ToString("X16", CultureInfo.InvariantCulture)} ({actual.ToString("R", CultureInfo.InvariantCulture)})");
            }
        }

        private static void Float2LikeMatchesUnityFormulas()
        {
            // Values from Unity.Mathematics.dll run on the same inputs.
            var a = new float2Like(1.5f, 2.25f);
            var b = new float2Like(-3.1f, 7.7f);
            AssertBits(unchecked((uint)Bits(7.1317945f)), float2Like.Distance(a, b), "distance");
            float2Like lerp = float2Like.Lerp(a, b, 0.3f);
            AssertBits(unchecked((uint)Bits(0.120000005f)), lerp.x, "lerp.x");
            AssertBits(unchecked((uint)Bits(3.885f)), lerp.y, "lerp.y");
            AssertTrue(float2Like.DistanceSq(a, b) == float2Like.LengthSq(b - a), "distancesq is lengthsq of the difference");
            AssertTrue(float2Like.Dot(a, b) == ((1.5f * -3.1f) + (2.25f * 7.7f)), "dot is the left-to-right sum");
            AssertTrue((a * 2f) == (2f * a) && (a * 2f).x == 3f && (a / 2f).y == 1.125f, "scalar operators");
            AssertTrue((-a).x == -1.5f && (a + b - b) == a, "unary minus, add and subtract");
        }

        private static void TileGridClampsAndInverts()
        {
            var worldMin = new float2Like(-2048f, -2048f);
            int2Like dims = TileGrid.GridDims(new float2Like(4096f, 4000f), 32f);
            AssertTrue(dims.x == 128 && dims.y == 125, "dims round up");
            AssertTrue(TileGrid.GridDims(new float2Like(0f, 0f), 32f) == new int2Like(1, 1), "a degenerate size still has one cell");
            int2Like inside = TileGrid.WorldToCell(new float2Like(-2047f, 1000f), worldMin, 32f, dims);
            AssertTrue(inside.x == 0 && inside.y == 95, "cell of a point inside");
            int2Like outside = TileGrid.WorldToCell(new float2Like(9000f, -9000f), worldMin, 32f, dims);
            AssertTrue(outside.x == 127 && outside.y == 0, "points off the map clamp to the edge cells");
            float2Like centre = TileGrid.CellCentre(inside, worldMin, 32f);
            AssertTrue(TileGrid.WorldToCell(centre, worldMin, 32f, dims) == inside, "a cell's centre maps back to the cell");
            AssertTrue(centre.x == -2032f && centre.y == 1008f, "centre is half a cell in");
        }

        private static void ZonesAggregate()
        {
            var worldMin = new float2Like(0f, 0f);
            var grid = new int2Like(4, 4);
            var trips = new List<Trip>
            {
                new Trip { m_Origin = new float2Like(100f, 100f), m_Destination = new float2Like(900f, 100f), m_Weight = 2f, m_DayShare = 1f },
                new Trip { m_Origin = new float2Like(900f, 900f), m_Destination = new float2Like(100f, 100f), m_Weight = 1f, m_DayShare = 0f },
                new Trip { m_Origin = new float2Like(120f, 120f), m_Destination = new float2Like(910f, 110f), m_Weight = 3f, m_DayShare = 1f },
                new Trip { m_Origin = new float2Like(100f, 100f), m_Destination = new float2Like(200f, 200f), m_Weight = 5f, m_DayShare = 1f },
                new Trip { m_Origin = new float2Like(-5f, 100f), m_Destination = new float2Like(900f, 100f), m_Weight = 7f, m_DayShare = 1f },
            };
            var flows = new List<ZoneFlow>();
            var journeys = new List<Trip>();
            float total = DemandZones.Aggregate(trips, worldMin, grid, flows, out int count, journeys);
            AssertTrue(count == 3 && total == 6f, $"three trips survive (self-zone and off-map dropped): count {count.ToString(CultureInfo.InvariantCulture)}, weight {total.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(journeys.Count == 3, "the surviving trips are handed back as journeys");
            AssertTrue(flows.Count == 2, "two zone pairs");
            AssertTrue(flows[0].m_Origin == 0 && flows[0].m_Destination == 3 && flows[0].m_Weight == 5f, "pair 0->3 sums both trips' weights");
            AssertTrue(flows[1].m_Origin == 15 && flows[1].m_Destination == 0 && flows[1].m_Weight == 1f, "pairs come out in (origin, destination) order");
            AssertTrue(DemandZones.ZoneOf(new float2Like(1023f, 1023f), worldMin, grid) == 15 && DemandZones.ZoneOf(new float2Like(1024f, 0f), worldMin, grid) == -1, "zone index and the open upper edge");
            float2Like centre = DemandZones.ZoneCentre(5, worldMin, grid);
            AssertTrue(centre.x == 384f && centre.y == 384f, "zone centre");

        }
    }
}
