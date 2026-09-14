using System;

namespace WhereTheyGo
{
    // How a band is drawn: the arc it follows, how wide it is, and what colour.
    //
    // All three are pure functions of the band, so the legend, the renderer and the
    // tests agree about them by construction rather than by comment.
    internal static class BandGeometry
    {
        // The arc, as the two interior control points of a cubic curve between the
        // ends. Bowed rather than straight for one reason: two bands between the same
        // districts, or one crossing another, lie on top of each other as straight
        // lines and the map becomes a single smear.
        //
        // The bow is always to the LEFT of A→B, and A is the end with the lower zone
        // index (DesireBands), so the same two places always bow the same way however
        // the journeys happened to be recorded.
        public static void Arc(
            float ax, float az, float bx, float bz,
            out float c1x, out float c1z, out float c2x, out float c2z)
        {
            float dx = bx - ax;
            float dz = bz - az;
            float length = (float)Math.Sqrt((dx * dx) + (dz * dz));
            if (length <= 0.001f)
            {
                c1x = ax;
                c1z = az;
                c2x = bx;
                c2z = bz;
                return;
            }

            float bow = Math.Min(length * Assumptions.BandBowShare, Assumptions.BandBowMaxMetres);
            // Left of the direction of travel in the map plane (x, z).
            float leftX = -dz / length;
            float leftZ = dx / length;
            c1x = ax + (dx / 3f) + (leftX * bow);
            c1z = az + (dz / 3f) + (leftZ * bow);
            c2x = ax + (dx * 2f / 3f) + (leftX * bow);
            c2z = az + (dz * 2f / 3f) + (leftZ * bow);
        }

        // A point on that arc, for hit-testing and for the dots that show which way the
        // traffic runs. `t` from 0 at A to 1 at B.
        public static void PointOnArc(
            float ax, float az, float bx, float bz, float t,
            out float x, out float z)
        {
            Arc(ax, az, bx, bz, out float c1x, out float c1z, out float c2x, out float c2z);
            float u = 1f - t;
            float w0 = u * u * u;
            float w1 = 3f * u * u * t;
            float w2 = 3f * u * t * t;
            float w3 = t * t * t;
            x = (w0 * ax) + (w1 * c1x) + (w2 * c2x) + (w3 * bx);
            z = (w0 * az) + (w1 * c1z) + (w2 * c2z) + (w3 * bz);
        }

        // Width in metres, on the square root of the traffic.
        //
        // Not linear: the heaviest corridor is a hundred times the lightest band drawn,
        // and linearly it eats the map while everything else is a hairline. Not
        // logarithmic either, which was the first attempt and made the map unreadable
        // (2026-09-14): log puts a band of a TENTH the traffic at two thirds of the
        // full width, so nearly every band came out fat and the picture was a smear of
        // overlapping blobs. The square root is the middle: a tenth of the traffic is
        // a third of the width, which still reads as "much less" while staying visible.
        public static float Width(float weight, float heaviestWeight)
        {
            if (weight <= 0f || heaviestWeight <= 0f)
            {
                return Assumptions.BandMinWidthMetres;
            }

            double scale = Math.Sqrt(Math.Min(weight, heaviestWeight) / (double)heaviestWeight);
            return Assumptions.BandMinWidthMetres
                + ((Assumptions.BandMaxWidthMetres - Assumptions.BandMinWidthMetres) * (float)scale);
        }

        // Warm where nobody rides, cool where everybody does. The ramp runs from a
        // light warm orange to a dark cool blue, so its LIGHTNESS is monotone as well
        // as its hue: red-green colour blindness, blue-yellow colour blindness and a
        // greyscale screenshot all still read it. Same pairing rule as the building
        // ramp (InfomodePrefab), which is the warm one of the pair.
        public static void Colour(float carriedShare, out float r, out float g, out float b)
        {
            float t = carriedShare < 0f ? 0f : carriedShare > 1f ? 1f : carriedShare;
            r = Lerp(0.99f, 0.13f, t);
            g = Lerp(0.55f, 0.25f, t);
            b = Lerp(0.24f, 0.55f, t);
        }

        // How far a point lies from the band's arc, squared, in metres. Sampled along
        // the same curve the renderer draws, so what the player points at is what they
        // see — a closed-form distance to a cubic would be exact about a curve nobody
        // is looking at.
        public static float DistanceSqToArc(float ax, float az, float bx, float bz, float px, float pz, int samples)
        {
            if (samples < 1)
            {
                samples = 1;
            }

            float best = float.MaxValue;
            PointOnArc(ax, az, bx, bz, 0f, out float lastX, out float lastZ);
            for (int i = 1; i <= samples; i++)
            {
                PointOnArc(ax, az, bx, bz, i / (float)samples, out float x, out float z);
                float distance = DistanceSqToSegment(lastX, lastZ, x, z, px, pz);
                if (distance < best)
                {
                    best = distance;
                }

                lastX = x;
                lastZ = z;
            }

            return best;
        }

        private static float DistanceSqToSegment(float x1, float z1, float x2, float z2, float px, float pz)
        {
            float dx = x2 - x1;
            float dz = z2 - z1;
            float lengthSq = (dx * dx) + (dz * dz);
            float t = lengthSq <= 0f ? 0f : (((px - x1) * dx) + ((pz - z1) * dz)) / lengthSq;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            float cx = x1 + (t * dx);
            float cz = z1 + (t * dz);
            return ((px - cx) * (px - cx)) + ((pz - cz) * (pz - cz));
        }

        // Whether a band clears the panel's threshold: a share of the heaviest band,
        // so the slider means the same thing in a village and in a metropolis.
        public static bool IsVisible(float weight, float heaviestWeight, float thresholdShare)
        {
            return heaviestWeight > 0f && weight >= heaviestWeight * thresholdShare;
        }

        private static float Lerp(float from, float to, float t) => from + ((to - from) * t);
    }
}
