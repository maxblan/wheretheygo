using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhereTheyGo.Tests
{
    // The bands the map draws: how journeys are bundled into them, and how one is
    // drawn once it exists.
    internal static partial class Program
    {
        private static readonly float2Like BandWorldMin = new float2Like(0f, 0f);

        // A 16x16 grid of 256 m zones: 4 km square, enough for corridors that are
        // genuinely apart and corridors that are only nearly the same.
        private static readonly int2Like BandGrid = new int2Like(16, 16);

        private static Journey Commute(float ox, float oz, float dx, float dz, float weight, byte outHour, byte backHour, JourneyPurpose purpose = JourneyPurpose.Work)
        {
            return new Journey
            {
                m_Origin = new float2Like(ox, oz),
                m_Destination = new float2Like(dx, dz),
                m_Weight = weight,
                m_Purpose = purpose,
                m_OutHour = outHour,
                m_BackHour = backHour,
            };
        }

        private static BandSet BundleOf(List<Journey> journeys, bool[]? carried, float mergeMetres, int maxBands)
        {
            int n = journeys.Count;
            var ox = new float[n];
            var oz = new float[n];
            var dx = new float[n];
            var dz = new float[n];
            var weight = new float[n];
            for (int i = 0; i < n; i++)
            {
                ox[i] = journeys[i].m_Origin.x;
                oz[i] = journeys[i].m_Origin.y;
                dx[i] = journeys[i].m_Destination.x;
                dz[i] = journeys[i].m_Destination.y;
                weight[i] = journeys[i].m_Weight;
            }

            var pairs = new RoutingProblem
            {
                PairCount = n,
                PairOx = ox,
                PairOz = oz,
                PairDx = dx,
                PairDz = dz,
                PairWeight = weight,
            };
            var routed = new RoutingResult { Carried = carried ?? new bool[n] };
            return DesireBands.Build(journeys, pairs, routed, BandWorldMin, BandGrid, mergeMetres, maxBands);
        }

        private static void BandsBundleNeighbouringCorridors()
        {
            // Two corridors 100 m apart between the same two districts, plus the same
            // corridor travelled the other way, plus one somewhere else entirely.
            var journeys = new List<Journey>
            {
                Commute(100f, 100f, 3000f, 100f, 40f, 8, 17),
                Commute(180f, 160f, 3080f, 160f, 20f, 9, 18),
                Commute(3000f, 100f, 100f, 100f, 10f, 7, 16),
                Commute(100f, 3000f, 3000f, 3000f, 5f, 8, 17, JourneyPurpose.Shopping),
            };
            var carried = new[] { true, false, false, true };

            BandSet set = BundleOf(journeys, carried, Assumptions.BandMergeMetres, Assumptions.MaxBands);

            AssertTrue(set.Bands.Length == 2, $"two corridors, not four: {set.Bands.Length.ToString(CultureInfo.InvariantCulture)} bands");
            Band heavy = set.Bands[0];
            AssertEqual(70f, heavy.Weight, 1e-3f, "the three journeys along one corridor are one band");
            // The reversed journey shares its zone pair with the first, so the corridor
            // is two zone pairs and one of them is folded into the other.
            AssertTrue(set.MergedPairs == 1, $"the neighbouring zone pair was folded in, got {set.MergedPairs.ToString(CultureInfo.InvariantCulture)}");
            AssertEqual(40f, heavy.CarriedWeight, 1e-3f, "only the carried journeys colour it");
            AssertTrue(Math.Abs(heavy.CarriedShare - (40f / 70f)) < 1e-6f, "and the colour is their share");
            AssertEqual(70f, set.HeaviestWeight, 1e-3f, "the heaviest band is what the widths and the threshold scale against");

            // The reversed journey belongs to the same band but to the other direction:
            // it leaves the far end at 07:00 and comes back at 16:00.
            AssertEqual(60f, heavy.HourlyAtoB[8] + heavy.HourlyAtoB[9], 1e-3f, "the two morning departures leave A for B");
            AssertEqual(10f, heavy.HourlyAtoB[16], 1e-3f, "the reversed journey's RETURN leaves A at 16:00");
            AssertEqual(10f, heavy.HourlyBtoA[7], 1e-3f, "and its outbound leaves B at 07:00");
            AssertEqual(60f, heavy.HourlyBtoA[17] + heavy.HourlyBtoA[18], 1e-3f, "the evening flow runs the other way");
            AssertTrue(heavy.PeakHour is 17 or 8, $"the peak hour is one of the two commuting hours, got {heavy.PeakHour.ToString(CultureInfo.InvariantCulture)}");
            // Every journey is made twice a day, so the hours hold twice the band's
            // weight — once leaving, once coming back. That is the whole reason the
            // map can breathe with the clock.
            float hourly = 0f;
            for (int h = 0; h < Band.HoursPerDay; h++)
            {
                hourly += heavy.HourlyAtoB[h] + heavy.HourlyBtoA[h];
            }

            AssertEqual(2f * heavy.Weight, hourly, 1e-3f, "the hours hold both rides of every journey");

            AssertTrue((heavy.PurposeMask & (1 << (int)JourneyPurpose.Work)) != 0, "the band knows work travels here");
            AssertTrue((heavy.PurposeMask & (1 << (int)JourneyPurpose.Shopping)) == 0, "and that shopping does not");
            AssertTrue((set.Bands[1].PurposeMask & (1 << (int)JourneyPurpose.Shopping)) != 0, "the other band is the shopping one");

            // The ends follow the traffic rather than the grid: the heavy corridor's A
            // end sits between the two origins, nearer the heavier one.
            AssertTrue(heavy.Ax is > 100f and < 180f, $"the end is the weighted mean of the doors, got {heavy.Ax.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        private static void BandsAreDeterministicAndBounded()
        {
            var journeys = new List<Journey>();
            var random = new Random(7);
            for (int i = 0; i < 400; i++)
            {
                float ox = (float)(random.NextDouble() * 4000.0);
                float oz = (float)(random.NextDouble() * 4000.0);
                float dx = (float)(random.NextDouble() * 4000.0);
                float dz = (float)(random.NextDouble() * 4000.0);
                journeys.Add(Commute(ox, oz, dx, dz, 1f + (i % 5), (byte)(6 + (i % 12)), (byte)(15 + (i % 8))));
            }

            BandSet first = BundleOf(journeys, null, Assumptions.BandMergeMetres, Assumptions.MaxBands);
            BandSet second = BundleOf(journeys, null, Assumptions.BandMergeMetres, Assumptions.MaxBands);
            AssertTrue(first.Bands.Length == second.Bands.Length, "two runs over the same city give the same bands");
            for (int i = 0; i < first.Bands.Length; i++)
            {
                AssertTrue(Bits(first.Bands[i].Ax) == Bits(second.Bands[i].Ax)
                    && Bits(first.Bands[i].Weight) == Bits(second.Bands[i].Weight),
                    $"band {i.ToString(CultureInfo.InvariantCulture)} is bit-identical between runs");
            }

            // Heaviest first is what makes the cap honest: what gets left out is the
            // light end, and it is counted.
            for (int i = 1; i < first.Bands.Length; i++)
            {
                AssertTrue(first.Bands[i - 1].Weight >= first.Bands[i].Weight - 1e-3f, "bands come out heaviest first");
            }

            BandSet capped = BundleOf(journeys, null, Assumptions.BandMergeMetres, 5);
            AssertTrue(capped.Bands.Length == 5, $"the cap holds, got {capped.Bands.Length.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(capped.HiddenPairs > 0 && capped.HiddenWeight > 0f, "and what it left out is counted, not silently dropped");

            // Nothing evaporates: every journey is either on a band or in the hidden
            // count. (The heaviest FINAL band need not survive the cap — a band that
            // starts light can absorb its way past the ones accepted before it, and
            // the cap is applied as the buckets arrive.)
            float shownWeight = 0f;
            for (int i = 0; i < capped.Bands.Length; i++)
            {
                shownWeight += capped.Bands[i].Weight;
            }

            float allWeight = 0f;
            for (int i = 0; i < first.Bands.Length; i++)
            {
                allWeight += first.Bands[i].Weight;
            }

            AssertEqual(allWeight, shownWeight + capped.HiddenWeight, 1e-2f, "what is drawn plus what was left out is every journey");
        }

        private static void BandsDropWhatIsNotAJourney()
        {
            var journeys = new List<Journey>
            {
                // Both ends in the same 256 m zone: not a corridor.
                Commute(100f, 100f, 200f, 200f, 9f, 8, 17),
                // Off the map.
                Commute(-500f, 100f, 3000f, 100f, 9f, 8, 17),
            };
            BandSet set = BundleOf(journeys, null, Assumptions.BandMergeMetres, Assumptions.MaxBands);
            AssertTrue(set.Bands.Length == 0, "a journey inside one zone and one off the map are neither of them bands");
            AssertEqual(0f, set.HeaviestWeight, 0f, "and nothing is the heaviest of nothing");

            BandSet empty = BundleOf(new List<Journey>(), null, Assumptions.BandMergeMetres, Assumptions.MaxBands);
            AssertTrue(empty.Bands.Length == 0 && empty.HiddenPairs == 0, "a city with no journeys has no bands");
        }

        // The direction the map shows at an hour, and why there is none over a day.
        private static void BandDirectionFollowsTheHour()
        {
            var journeys = new List<Journey>
            {
                // A commute: out at 08:00, back at 17:00.
                Commute(100f, 100f, 3000f, 100f, 40f, 8, 17),
            };
            BandSet set = BundleOf(journeys, null, Assumptions.BandMergeMetres, Assumptions.MaxBands);
            Band band = set.Bands[0];

            AssertTrue(band.DirectionAtHour(8) == 1, "at eight the traffic leaves A for B");
            AssertTrue(band.DirectionAtHour(17) == -1, "at five it comes back");
            AssertTrue(band.DirectionAtHour(12) == 0, "an hour with no traffic has no direction");
            AssertTrue(band.DirectionAtHour(-1) == 0, "and neither has the whole day, which is the point");

            // An hour that is nearly even must not invent a rush: half in each
            // direction is no direction at all.
            var even = new List<Journey>
            {
                Commute(100f, 100f, 3000f, 100f, 10f, 8, 9),
                Commute(3000f, 100f, 100f, 100f, 10f, 8, 9),
            };
            Band mixed = BundleOf(even, null, Assumptions.BandMergeMetres, Assumptions.MaxBands).Bands[0];
            AssertTrue(mixed.DirectionAtHour(8) == 0, "an evenly split hour has no direction");

            // The whole day always balances, whatever the city does, because every
            // journey is made twice.
            var random = new Random(3);
            var many = new List<Journey>();
            for (int i = 0; i < 50; i++)
            {
                many.Add(Commute(100f, 100f, 3000f, 100f, 1f + (i % 3), (byte)random.Next(0, 24), (byte)random.Next(0, 24)));
            }

            Band day = BundleOf(many, null, Assumptions.BandMergeMetres, Assumptions.MaxBands).Bands[0];
            float atoB = 0f;
            float btoA = 0f;
            for (int hour = 0; hour < Band.HoursPerDay; hour++)
            {
                atoB += day.HourlyAtoB[hour];
                btoA += day.HourlyBtoA[hour];
            }

            AssertEqual(atoB, btoA, 1e-3f, "over a day the two directions carry the same traffic");
        }

        private static void BandGeometryBowsLeftAndScalesByLog()
        {
            // A band running due east. Left of travel in the map plane is +z, which is
            // north in the game's world.
            BandGeometry.Arc(0f, 0f, 1000f, 0f, out float c1x, out float c1z, out float c2x, out float c2z);
            float bow = Math.Min(1000f * Assumptions.BandBowShare, Assumptions.BandBowMaxMetres);
            AssertEqual(bow, c1z, 1e-3f, "the arc bows left of the direction of travel");
            AssertEqual(bow, c2z, 1e-3f, "at both control points");
            AssertTrue(c1x < c2x, "which are a third and two thirds along");

            BandGeometry.PointOnArc(0f, 0f, 1000f, 0f, 0f, out float sx, out float sz);
            BandGeometry.PointOnArc(0f, 0f, 1000f, 0f, 1f, out float ex, out float ez);
            AssertTrue(sx == 0f && sz == 0f && Math.Abs(ex - 1000f) < 1e-3f && Math.Abs(ez) < 1e-3f, "the arc starts at A and ends at B");
            BandGeometry.PointOnArc(0f, 0f, 1000f, 0f, 0.5f, out float mx, out float mz);
            AssertTrue(Math.Abs(mx - 500f) < 1e-3f && mz > 1f, $"and its middle is off the straight line, at z={mz.ToString("F1", CultureInfo.InvariantCulture)}");

            // A very long band's bow is capped, or it would swing out over the sea.
            BandGeometry.Arc(0f, 0f, 40000f, 0f, out _, out float longBow, out _, out _);
            AssertEqual(Assumptions.BandBowMaxMetres, longBow, 1e-3f, "the bow is capped in metres");

            // Degenerate: a band with no length has no arc to speak of and must not
            // divide by zero.
            BandGeometry.Arc(5f, 5f, 5f, 5f, out float dx, out float dz, out _, out _);
            AssertTrue(dx == 5f && dz == 5f, "a band of no length is its own control points");

            AssertEqual(Assumptions.BandMinWidthMetres, BandGeometry.Width(0f, 100f), 1e-4f, "no weight is the minimum width");
            AssertEqual(Assumptions.BandMaxWidthMetres, BandGeometry.Width(100f, 100f), 1e-3f, "the heaviest band is the maximum width");
            AssertEqual(Assumptions.BandMinWidthMetres, BandGeometry.Width(10f, 0f), 1e-4f, "and a city with no traffic does not divide by zero");
            float half = BandGeometry.Width(50f, 100f);
            float tenth = BandGeometry.Width(10f, 100f);
            AssertTrue(half < Assumptions.BandMaxWidthMetres && half > tenth, "widths grow with weight");
            // The point of the log scale: a band of a tenth the traffic is far more
            // than a tenth as wide, so it is still visible beside the trunk.
            float linearTenth = Assumptions.BandMinWidthMetres + ((Assumptions.BandMaxWidthMetres - Assumptions.BandMinWidthMetres) * 0.1f);
            AssertTrue(tenth > linearTenth, $"a tenth of the traffic is wider than a tenth of the width: {tenth.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        private static void BandColourRunsWarmToCoolAndMonotone()
        {
            BandGeometry.Colour(0f, out float r0, out float g0, out float b0);
            BandGeometry.Colour(1f, out float r1, out _, out float b1);
            AssertTrue(r0 > b0, "nobody carried reads warm");
            AssertTrue(b1 > r1, "everybody carried reads cool");

            // Monotone in every channel, so the ramp is also monotone in lightness and
            // survives colour blindness and a greyscale screenshot.
            float lastR = r0;
            float lastB = b0;
            float lastLight = (0.299f * r0) + (0.587f * g0) + (0.114f * b0);
            for (int step = 1; step <= 10; step++)
            {
                BandGeometry.Colour(step / 10f, out float r, out float g, out float b);
                AssertTrue(r <= lastR + 1e-6f, "red falls all the way");
                AssertTrue(b >= lastB - 1e-6f, "blue rises all the way");
                float light = (0.299f * r) + (0.587f * g) + (0.114f * b);
                AssertTrue(light <= lastLight + 1e-6f, $"and lightness falls all the way: {light.ToString("F3", CultureInfo.InvariantCulture)}");
                lastR = r;
                lastB = b;
                lastLight = light;
            }

            BandGeometry.Colour(-1f, out float rLow, out _, out _);
            BandGeometry.Colour(2f, out _, out _, out float bHigh);
            AssertTrue(rLow == r0 && bHigh == b1, "a share outside 0..1 clamps rather than running off the ramp");

            AssertTrue(BandGeometry.IsVisible(10f, 100f, 0.02f) && !BandGeometry.IsVisible(1f, 100f, 0.02f),
                "the threshold is a share of the heaviest band, so it means the same in any city");
            AssertTrue(!BandGeometry.IsVisible(10f, 0f, 0.02f), "and nothing is visible in a city with no traffic");
        }
    }
}
