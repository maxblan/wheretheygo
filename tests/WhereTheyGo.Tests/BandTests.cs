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
            AssertEqual(60f, heavy.AtoB((int)JourneyPurpose.Work, 8) + heavy.AtoB((int)JourneyPurpose.Work, 9), 1e-3f, "the two morning departures leave A for B");
            AssertEqual(10f, heavy.AtoB((int)JourneyPurpose.Work, 16), 1e-3f, "the reversed journey's RETURN leaves A at 16:00");
            AssertEqual(10f, heavy.BtoA((int)JourneyPurpose.Work, 7), 1e-3f, "and its outbound leaves B at 07:00");
            AssertEqual(60f, heavy.BtoA((int)JourneyPurpose.Work, 17) + heavy.BtoA((int)JourneyPurpose.Work, 18), 1e-3f, "the evening flow runs the other way");
            AssertTrue(heavy.PeakHour(Band.AllPurposes) is 17 or 8, $"the peak hour is one of the two commuting hours, got {heavy.PeakHour(Band.AllPurposes).ToString(CultureInfo.InvariantCulture)}");
            // Every journey is made twice a day, so the hours hold twice the band's
            // weight — once leaving, once coming back. That is the whole reason the
            // map can breathe with the clock.
            float hourly = 0f;
            for (int h = 0; h < Band.HoursPerDay; h++)
            {
                hourly += heavy.WeightAtHour(h, Band.AllPurposes);
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

            AssertTrue(band.DirectionAtHour(8, Band.AllPurposes) == 1, "at eight the traffic leaves A for B");
            AssertTrue(band.DirectionAtHour(17, Band.AllPurposes) == -1, "at five it comes back");
            AssertTrue(band.DirectionAtHour(12, Band.AllPurposes) == 0, "an hour with no traffic has no direction");
            AssertTrue(band.DirectionAtHour(-1, Band.AllPurposes) == 0, "and neither has the whole day, which is the point");

            // An hour that is nearly even must not invent a rush: half in each
            // direction is no direction at all.
            var even = new List<Journey>
            {
                Commute(100f, 100f, 3000f, 100f, 10f, 8, 9),
                Commute(3000f, 100f, 100f, 100f, 10f, 8, 9),
            };
            Band mixed = BundleOf(even, null, Assumptions.BandMergeMetres, Assumptions.MaxBands).Bands[0];
            AssertTrue(mixed.DirectionAtHour(8, Band.AllPurposes) == 0, "an evenly split hour has no direction");

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
                atoB += day.AtoB((int)JourneyPurpose.Work, hour);
                btoA += day.BtoA((int)JourneyPurpose.Work, hour);
            }

            AssertEqual(atoB, btoA, 1e-3f, "over a day the two directions carry the same traffic");
        }

        private static void BandGeometryBowsLeftAndCasesTheFill()
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

            // The tangent an arrowhead is turned by. On a bowed band the straight A-to-B
            // heading is visibly wrong near the ends, which is the whole reason this
            // exists rather than reusing the chord.
            BandGeometry.DirectionOnArc(0f, 0f, 1000f, 0f, 0.5f, out float mdx, out float mdz);
            AssertTrue(Math.Abs(mdx - 1f) < 1e-3f && Math.Abs(mdz) < 1e-3f, "at the middle of a symmetric bow the heading is the chord's");
            BandGeometry.DirectionOnArc(0f, 0f, 1000f, 0f, 0f, out float sdx, out float sdz);
            AssertTrue(sdx > 0f && sdz > 0f, "leaving A it already heads into the bow, which is +z on an eastward band");
            AssertTrue(Math.Abs((sdx * sdx) + (sdz * sdz) - 1f) < 1e-4f, "and the heading is a unit vector, or the arrow would be scaled by it");
            BandGeometry.DirectionOnArc(5f, 5f, 5f, 5f, 0.5f, out float ddx, out float ddz);
            AssertTrue(!float.IsNaN(ddx) && !float.IsNaN(ddz), "a band of no length still has a finite heading");

            // The casing is the band's own colour driven down, not black: a neutral
            // outline would read as a fifth colour on a map that already carries a ramp.
            BandGeometry.Colour(0.5f, out float fr, out float fg, out float fb);
            BandGeometry.OutlineColour(0.5f, out float or_, out float og, out float ob);
            AssertTrue(or_ < fr && og < fg && ob < fb, "the casing is darker than the fill in every channel");
            AssertTrue(or_ > 0f || og > 0f || ob > 0f, "but it is not black");
        }

        // What the map actually draws, under the three filters that decide it.
        private static void BandViewFiltersExactlyAndClassesWidths()
        {
            var journeys = new List<Journey>
            {
                // One corridor carrying both commuters and shoppers, so the purpose
                // filter has something to be exact about.
                Commute(100f, 100f, 3000f, 100f, 100f, 8, 17),
                Commute(100f, 100f, 3000f, 100f, 25f, 10, 12, JourneyPurpose.Shopping),
                // A second, lighter corridor elsewhere.
                Commute(100f, 3000f, 3000f, 3000f, 8f, 8, 17),
                // And a hair, to be cut by the threshold.
                Commute(100f, 2000f, 3000f, 2000f, 1f, 8, 17),
            };

            BandSet set = BundleOf(journeys, carried: null, Assumptions.BandMergeMetres, Assumptions.MaxBands);
            AssertTrue(set.Bands.Length == 3, $"three corridors, got {set.Bands.Length.ToString(CultureInfo.InvariantCulture)}");

            // Switching shopping off must take its 25 journeys OFF the heavy band, not
            // merely leave the band in place at its full weight. The band is drawn at a
            // number, and the number has to be the one the filter describes.
            Band heavy = set.Bands[0];
            AssertEqual(125f, heavy.DayWeight(Band.AllPurposes), 1e-3f, "all purposes is every journey on the band");
            AssertEqual(100f, heavy.DayWeight(1 << (int)JourneyPurpose.Work), 1e-3f, "work alone is the commuters");
            AssertEqual(25f, heavy.DayWeight(1 << (int)JourneyPurpose.Shopping), 1e-3f, "shopping alone is the shoppers");
            AssertEqual(125f, heavy.Weight, 1e-3f, "and the two together are what the routing weighed");

            // The hour and the purpose compose: at ten in the morning only the shoppers
            // are out on this band.
            AssertEqual(25f, heavy.WeightAtHour(10, Band.AllPurposes), 1e-3f, "ten in the morning is the shopping trip");
            AssertEqual(0f, heavy.WeightAtHour(10, 1 << (int)JourneyPurpose.Work), 1e-3f, "and none of it is work");

            // The threshold is a share of the heaviest band UNDER THE SAME FILTERS.
            BandView all = BandView.Of(set, hour: -1, Band.AllPurposes, thresholdShare: 0.05f);
            AssertTrue(all.Drawn.Length == 2, $"the hair is under five percent of 125, got {all.Drawn.Length.ToString(CultureInfo.InvariantCulture)} drawn");
            AssertTrue(all.HiddenCount == 1, "and it is counted rather than forgotten");
            AssertEqual(1f, all.HiddenWeight, 1e-3f, "with the journeys it stands for");
            AssertTrue(all.TotalCount == 3, "drawn plus hidden is every band there is");
            AssertEqual(125f, all.Heaviest, 1e-3f, "the scale is the heaviest band under these filters");

            // Heaviest first out of the view, which is the order the renderer relies on
            // to put light bands on top of heavy ones.
            AssertTrue(all.Drawn[0].Weight >= all.Drawn[1].Weight, "the view is heaviest first");

            // With only shopping ticked the scale follows: the heavy band is now 25
            // journeys, and the other corridors carry no shopping at all.
            BandView shopping = BandView.Of(set, hour: -1, 1 << (int)JourneyPurpose.Shopping, thresholdShare: 0.05f);
            AssertTrue(shopping.Drawn.Length == 1, $"only the corridor with shoppers on it, got {shopping.Drawn.Length.ToString(CultureInfo.InvariantCulture)}");
            AssertEqual(25f, shopping.Heaviest, 1e-3f, "and the scale is 25, not the city's 125");
            AssertEqual(25f, shopping.Drawn[0].Weight, 1e-3f, "the band is drawn at what shopping weighs on it");

            // The day's shape, which the panel's hour strip draws. It must sum to both
            // rides of every journey the purposes let through, and it must NOT follow
            // the threshold: a picture of the day that changed when the slider moved
            // would be describing the slider.
            float profileTotal = 0f;
            foreach (float value in all.HourlyProfile)
            {
                profileTotal += value;
            }

            AssertEqual(2f * (100f + 25f + 8f + 1f), profileTotal, 1e-3f, "the strip holds both rides of every journey, hidden bands included");
            BandView harsh = BandView.Of(set, hour: -1, Band.AllPurposes, thresholdShare: 0.5f);
            float harshTotal = 0f;
            foreach (float value in harsh.HourlyProfile)
            {
                harshTotal += value;
            }

            AssertEqual(profileTotal, harshTotal, 1e-3f, "and raising the threshold does not reshape the day");

            // The four switches carry their weight whether they are on or off, or a
            // switch could not say what it would let back in.
            AssertEqual(100f + 8f + 1f, shopping.PurposeWeights[(int)JourneyPurpose.Work], 1e-3f, "work still weighs what it weighs with only shopping ticked");
            AssertEqual(25f, shopping.PurposeWeights[(int)JourneyPurpose.Shopping], 1e-3f, "and so does shopping");

            // Degenerate: nothing at all must not divide by zero or throw.
            BandView empty = BandView.Of(null, hour: -1, Band.AllPurposes, thresholdShare: 0.05f);
            AssertTrue(empty.Drawn.Length == 0 && empty.Heaviest == 0f, "no bands is an empty view");
            BandView nobody = BandView.Of(set, hour: 3, Band.AllPurposes, thresholdShare: 0.05f);
            AssertTrue(nobody.Drawn.Length == 0, "an hour nobody travels in draws nothing");
        }

        // Widths come in classes with a legend, which is what every flow map in the
        // cartographic survey does and what a continuous ramp cannot offer: a width
        // that can be read rather than only compared.
        private static void BandWidthClassesAreOrderedAndReadable()
        {
            var journeys = new List<Journey>();
            // Weights spread over two orders of magnitude, each its own corridor.
            float[] weights = { 1000f, 300f, 90f, 30f, 12f };
            for (int i = 0; i < weights.Length; i++)
            {
                journeys.Add(Commute(100f, 100f + (i * 600f), 3000f, 100f + (i * 600f), weights[i], 8, 17));
            }

            BandSet set = BundleOf(journeys, carried: null, Assumptions.BandMergeMetres, Assumptions.MaxBands);
            BandView view = BandView.Of(set, hour: -1, Band.AllPurposes, thresholdShare: 0f);
            AssertTrue(view.Drawn.Length == weights.Length, "every corridor is drawn when nothing is thresholded away");

            // The class boundaries are readable numbers, strictly increasing, and they
            // separate the corridors rather than lumping them together.
            float[] breaks = view.ClassBreaks;
            AssertTrue(breaks.Length == BandView.ClassCount - 1, "one boundary fewer than there are classes");
            for (int i = 1; i < breaks.Length; i++)
            {
                AssertTrue(breaks[i] > breaks[i - 1], $"the boundaries increase: {breaks[i - 1].ToString("F0", CultureInfo.InvariantCulture)} then {breaks[i].ToString("F0", CultureInfo.InvariantCulture)}");
            }

            foreach (float edge in breaks)
            {
                AssertTrue(IsReadableNumber(edge), $"a legend prints {edge.ToString("F0", CultureInfo.InvariantCulture)}, which has to be a number a player can hold");
            }

            // The heaviest corridor is in the top class, the lightest in the bottom,
            // and the classes never run backwards against weight.
            AssertTrue(view.Drawn[0].WidthClass == BandView.ClassCount - 1, "the city's biggest corridor is in the widest class");
            AssertTrue(view.Drawn[^1].WidthClass == 0, "and the lightest band drawn is in the thinnest");
            for (int i = 1; i < view.Drawn.Length; i++)
            {
                AssertTrue(view.Drawn[i].WidthClass <= view.Drawn[i - 1].WidthClass, "a lighter band is never in a wider class");
            }

            // Widths themselves increase with the class, or the classes would say
            // nothing on the map.
            for (int i = 1; i < BandView.ClassCount; i++)
            {
                AssertTrue(BandView.WidthOf(i) > BandView.WidthOf(i - 1), "each class is wider than the one below");
            }

            AssertEqual(BandView.WidthOf(0), BandView.WidthOf(-3), 1e-6f, "a class below the first clamps rather than throwing");
            AssertEqual(BandView.WidthOf(BandView.ClassCount - 1), BandView.WidthOf(99), 1e-6f, "and one above the last");

            // Degenerate: every band the same weight. One class is the honest answer,
            // and nothing may be promoted out of it.
            var flat = new List<Journey>
            {
                Commute(100f, 100f, 3000f, 100f, 50f, 8, 17),
                Commute(100f, 1000f, 3000f, 1000f, 50f, 8, 17),
            };
            BandView same = BandView.Of(BundleOf(flat, carried: null, Assumptions.BandMergeMetres, Assumptions.MaxBands), hour: -1, Band.AllPurposes, thresholdShare: 0f);
            AssertTrue(same.Drawn[0].WidthClass == same.Drawn[1].WidthClass, "two bands of equal weight are drawn the same width");
        }

        // 1, 2 or 5 times a power of ten: the numbers a legend can print.
        private static bool IsReadableNumber(float value)
        {
            if (value <= 0f)
            {
                return false;
            }

            double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(value)));
            double normalised = value / magnitude;
            return Math.Abs(normalised - 1.0) < 1e-6
                || Math.Abs(normalised - 2.0) < 1e-6
                || Math.Abs(normalised - 5.0) < 1e-6;
        }

        // Pointing at a band. The hit test runs on the same arc the renderer draws, so
        // what the player grabs is what they see.
        private static void PointingAtABandFindsTheArcNotTheChord()
        {
            // A band running due east bows to the left (+z), so its middle is NOT on
            // the straight line between its ends.
            const float ax = 0f;
            const float az = 0f;
            const float bx = 1000f;
            const float bz = 0f;

            BandGeometry.PointOnArc(ax, az, bx, bz, 0.5f, out float mx, out float mz);
            float onArc = BandGeometry.DistanceSqToArc(ax, az, bx, bz, mx, mz, 20);
            AssertTrue(onArc < 1f, $"a point on the arc is on the arc, got {Math.Sqrt(onArc).ToString("F1", CultureInfo.InvariantCulture)} m away");

            // The midpoint of the CHORD is a good way off it — which is the whole
            // reason the test is here: a hit test against the chord would grab the
            // wrong band wherever two bands cross.
            float onChord = BandGeometry.DistanceSqToArc(ax, az, bx, bz, 500f, 0f, 20);
            AssertTrue(Math.Sqrt(onChord) > 20f, $"the chord's middle is off the arc by {Math.Sqrt(onChord).ToString("F1", CultureInfo.InvariantCulture)} m");

            // The ends are always on it, and a point far away is far away.
            AssertTrue(BandGeometry.DistanceSqToArc(ax, az, bx, bz, ax, az, 20) < 1f, "the A end is on the arc");
            AssertTrue(BandGeometry.DistanceSqToArc(ax, az, bx, bz, bx, bz, 20) < 1f, "and so is the B end");
            float far = BandGeometry.DistanceSqToArc(ax, az, bx, bz, 500f, -400f, 20);
            AssertTrue(Math.Sqrt(far) > 350f, "a point off the band is off the band");

            // A band of no length still answers rather than dividing by zero.
            AssertTrue(BandGeometry.DistanceSqToArc(5f, 5f, 5f, 5f, 5f, 5f, 20) < 1f, "a band with no length is its own position");
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

        }
    }
}
