using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // One band as the map draws it right now: how much it weighs under the filters in
    // force, and which width class that puts it in.
    internal readonly struct DrawnBand
    {
        public DrawnBand(Band band, float weight, int widthClass)
        {
            Band = band;
            Weight = weight;
            WidthClass = widthClass;
        }

        public Band Band { get; }

        // Journeys a day on this band at the chosen hour, for the chosen purposes.
        public float Weight { get; }

        // 0 for the thinnest class, BandView.ClassCount - 1 for the thickest.
        public int WidthClass { get; }
    }

    // What the map is drawing, worked out once.
    //
    // The renderer, the hit test, the panel's band count and the legend all ask the
    // same question — which bands clear the hour, the purposes and the threshold, and
    // how wide is each — and before this they each answered it themselves, slightly
    // differently. It is pure so the answer can be tested without the game.
    //
    // Width comes in CLASSES rather than as a continuous ramp. Jenny et al. (2016)
    // found every flow map in their sample uses a small number of classed widths with
    // a legend, and the reason shows on our own map: with a continuous ramp no width
    // can be read, only compared, and the eye has to hold the whole map at once to
    // compare anything. Four classes can be read one band at a time.
    internal sealed class BandView
    {
        public const int ClassCount = 4;

        private BandView(
            DrawnBand[] drawn, float heaviest, float[] breaks,
            int hiddenCount, float hiddenWeight, float shownWeight,
            float[] hourlyProfile, float[] purposeWeights)
        {
            Drawn = drawn;
            Heaviest = heaviest;
            ClassBreaks = breaks;
            HiddenCount = hiddenCount;
            HiddenWeight = hiddenWeight;
            ShownWeight = shownWeight;
            HourlyProfile = hourlyProfile;
            PurposeWeights = purposeWeights;
        }

        // Heaviest first, which is the order the bundling leaves them in.
        public DrawnBand[] Drawn { get; }

        // The heaviest band under these filters. The classes are cut against this, not
        // against the city's unfiltered maximum: with three purposes switched off,
        // scaling against the biggest commuter corridor leaves every band in the
        // thinnest class and the map goes blank.
        public float Heaviest { get; }

        // The ClassCount - 1 weights where one width class becomes the next, lowest
        // first and never above Heaviest. The legend prints them, so they are rounded
        // to numbers a player can read rather than to whatever the data happened to
        // land on. Two equal boundaries mean the class between them is empty.
        public float[] ClassBreaks { get; }

        // Bands the filters left out, and what they weigh. Said out loud in the panel:
        // a map that quietly dropped a third of the city's travel reads as a map of
        // the whole city.
        public int HiddenCount { get; }

        public float HiddenWeight { get; }

        public float ShownWeight { get; }

        // The city's day, hour by hour, under the purposes in force. Over EVERY band,
        // not only the drawn ones: it is the shape of the city's travel, and a picture
        // of the day that changed when the threshold slider moved would be describing
        // the slider instead.
        public float[] HourlyProfile { get; }

        // Journeys a day per purpose, in JourneyPurpose order, regardless of which are
        // switched on — a switch has to be able to say what it would let back in.
        public float[] PurposeWeights { get; }

        public int TotalCount => Drawn.Length + HiddenCount;

        public static BandView Of(BandSet? set, int hour, int purposeMask, float thresholdShare)
        {
            if (set is null || set.Bands.Length == 0)
            {
                return new BandView(
                    Array.Empty<DrawnBand>(), 0f, EmptyBreaks(), 0, 0f, 0f,
                    new float[Band.HoursPerDay], new float[Band.PurposeCount]);
            }

            float[] profile = set.HourlyProfile(purposeMask);
            float[] purposeWeights = set.PurposeWeights();
            float heaviest = set.HeaviestAt(hour, purposeMask);
            if (heaviest <= 0f)
            {
                return new BandView(
                    Array.Empty<DrawnBand>(), 0f, EmptyBreaks(), set.Bands.Length, 0f, 0f,
                    profile, purposeWeights);
            }

            float floor = heaviest * thresholdShare;
            var weights = new List<float>(set.Bands.Length);
            int hiddenCount = 0;
            float hiddenWeight = 0f;
            for (int i = 0; i < set.Bands.Length; i++)
            {
                float weight = set.Bands[i].WeightAtHour(hour, purposeMask);
                if (weight <= 0f || weight < floor)
                {
                    hiddenCount++;
                    hiddenWeight += weight;
                    continue;
                }

                weights.Add(weight);
            }

            float[] breaks = Breaks(weights.Count > 0 ? Lowest(weights) : heaviest, heaviest);
            var drawn = new DrawnBand[weights.Count];
            int next = 0;
            float shownWeight = 0f;
            for (int i = 0; i < set.Bands.Length; i++)
            {
                float weight = set.Bands[i].WeightAtHour(hour, purposeMask);
                if (weight <= 0f || weight < floor)
                {
                    continue;
                }

                drawn[next++] = new DrawnBand(set.Bands[i], weight, ClassOf(weight, breaks));
                shownWeight += weight;
            }

            return new BandView(
                drawn, heaviest, breaks, hiddenCount, hiddenWeight, shownWeight,
                profile, purposeWeights);
        }

        // Which class a weight falls in: the first break it does not reach.
        public static int ClassOf(float weight, float[] breaks)
        {
            for (int i = 0; i < breaks.Length; i++)
            {
                if (weight < breaks[i])
                {
                    return i;
                }
            }

            return ClassCount - 1;
        }

        // The width in metres a class is drawn at.
        public static float WidthOf(int widthClass)
        {
            float[] widths = Assumptions.BandClassWidthsMetres;
            return widths[widthClass < 0 ? 0 : widthClass >= widths.Length ? widths.Length - 1 : widthClass];
        }

        // Class boundaries between the lightest band drawn and the heaviest, spaced
        // GEOMETRICALLY rather than evenly: a city's corridors are spread over two or
        // three orders of magnitude, and even spacing puts every band but the biggest
        // handful in the bottom class.
        //
        // Each boundary is then rounded UP to a readable number (1, 2 or 5 times a
        // power of ten), because the legend prints it and "1 300 journeys" is a number
        // a player can hold while "1 287" is not. Rounding up rather than to nearest
        // keeps the classes in order even where the raw boundaries sit close together.
        private static float[] Breaks(float lowest, float heaviest)
        {
            var breaks = new float[ClassCount - 1];
            float low = lowest > 0f ? lowest : heaviest;
            if (heaviest <= low)
            {
                // Every band the same weight: one class, and the boundaries sit above
                // it so nothing is promoted out of the bottom.
                for (int i = 0; i < breaks.Length; i++)
                {
                    breaks[i] = Nice(heaviest) * (i + 2);
                }

                return breaks;
            }

            double ratio = Math.Pow(heaviest / (double)low, 1.0 / ClassCount);
            double edge = low;
            for (int i = 0; i < breaks.Length; i++)
            {
                edge *= ratio;
                breaks[i] = Nice((float)edge);
                // Rounding UP can push the top boundary past the heaviest band, which
                // leaves the widest class empty and the legend promising a width
                // nothing on the map has. Seen in a real city: the heaviest band at
                // 9736 and the legend offering "10000 and more".
                if (breaks[i] >= heaviest)
                {
                    breaks[i] = NiceBelow(heaviest);
                }

                // Rounding can also collapse two boundaries onto each other; nudging
                // the second up keeps every class reachable - unless the nudge would
                // itself pass the heaviest band, which is the case above again. Then
                // the two boundaries stay equal and that class is simply empty: the
                // legend skips a class of no width, and a band on the boundary lands in
                // the class above it, so the widest class still holds the heaviest band.
                if (i > 0 && breaks[i] <= breaks[i - 1])
                {
                    float nudged = Nice(breaks[i - 1] * 1.5f);
                    breaks[i] = nudged < heaviest ? nudged : breaks[i - 1];
                }
            }

            return breaks;
        }

        // The largest of 1, 2 or 5 times a power of ten that is strictly below `value`.
        private static float NiceBelow(float value)
        {
            float nice = Nice(value);
            if (nice < value)
            {
                return nice;
            }

            double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(nice)));
            double normalised = nice / magnitude;
            return normalised > 5.0 ? (float)(5.0 * magnitude)
                : normalised > 2.0 ? (float)(2.0 * magnitude)
                : normalised > 1.0 ? (float)magnitude
                : (float)(5.0 * magnitude / 10.0);
        }

        // The smallest of 1, 2 or 5 times a power of ten that is at least `value`.
        private static float Nice(float value)
        {
            if (value <= 1f)
            {
                return 1f;
            }

            double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(value)));
            double normalised = value / magnitude;
            double step = normalised <= 1.0 ? 1.0 : normalised <= 2.0 ? 2.0 : normalised <= 5.0 ? 5.0 : 10.0;
            return (float)(step * magnitude);
        }

        private static float Lowest(List<float> weights)
        {
            float lowest = weights[0];
            for (int i = 1; i < weights.Count; i++)
            {
                if (weights[i] < lowest)
                {
                    lowest = weights[i];
                }
            }

            return lowest;
        }

        private static float[] EmptyBreaks() => new float[ClassCount - 1];
    }
}
