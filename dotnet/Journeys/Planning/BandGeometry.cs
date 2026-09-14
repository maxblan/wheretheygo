using System;

namespace WhereTheyGo
{
    // How a band is drawn: the arc it flies along and what colour it carries.
    //
    // All three are pure functions of the band, so the legend, the renderer and the
    // tests agree about them by construction rather than by comment.
    internal static class BandGeometry
    {
        // How high this band's arc flies over its middle, in metres.
        public static float ArcHeightMetres(float lengthMetres)
        {
            float height = lengthMetres * Assumptions.BandArcHeightShare;
            return height > Assumptions.BandArcMaxHeightMetres ? Assumptions.BandArcMaxHeightMetres : height;
        }

        // How far above the straight line between its two ends the arc runs at `t`.
        //
        // 4h*t*(1-t) is the vertical part of a cubic curve whose two inner control
        // points are lifted by 4h/3 - the same four thirds the game itself uses for a
        // curve that hangs (NetUtils.StraightCurve multiplies the sag by 1.3333334).
        // At t = 0.5 it comes to exactly h, which is what lets one number describe the
        // arc to the renderer, the hit test and a reader.
        public static float RiseAt(float lengthMetres, float t)
        {
            return 4f * ArcHeightMetres(lengthMetres) * t * (1f - t);
        }

        // A point on the arc: straight in plan, bowed into the air. `t` runs from 0 at
        // A to 1 at B, and the two end heights are the ground under each end.
        //
        // The arc is drawn as a chain of tubes rather than as one curve primitive, and
        // this is why: the overlay's curve is ONE flat quad whose plane is fitted from
        // the curve's own control points, and a bow that is purely vertical is exactly
        // the case that fit falls back on a horizontal plane for (OverlayRenderSystem
        // .Buffer.FitQuad - the two cross products cancel). Its length is measured in
        // the map plane as well. See docs/game-facts.md.
        public static void PointOnArc(
            float ax, float ay, float az, float bx, float by, float bz, float t,
            out float x, out float y, out float z)
        {
            float dx = bx - ax;
            float dz = bz - az;
            float length = (float)Math.Sqrt((dx * dx) + (dz * dz));
            x = ax + (dx * t);
            z = az + (dz * t);
            y = ay + ((by - ay) * t) + RiseAt(length, t);
        }

        // Which way the arc is heading at `t`, normalised, in three dimensions. The
        // arrowhead that shows an hour's direction has to sit along the arc; near the
        // ends a band climbs at better than thirty degrees, where the straight A-to-B
        // heading points into the ground.
        public static void DirectionOnArc(
            float ax, float ay, float az, float bx, float by, float bz, float t,
            out float dx, out float dy, out float dz)
        {
            float rawX = bx - ax;
            float rawZ = bz - az;
            float length = (float)Math.Sqrt((rawX * rawX) + (rawZ * rawZ));
            // The rise's own slope, 4h(1 - 2t), on top of the slope of the ground
            // between the two ends.
            float rawY = (by - ay) + (4f * ArcHeightMetres(length) * (1f - (2f * t)));
            float size = (float)Math.Sqrt((rawX * rawX) + (rawY * rawY) + (rawZ * rawZ));
            if (size <= 1e-4f)
            {
                // A band whose ends coincide on flat ground has no heading; pointing
                // along +x is arbitrary but never NaN.
                dx = 1f;
                dy = 0f;
                dz = 0f;
                return;
            }

            dx = rawX / size;
            dy = rawY / size;
            dz = rawZ / size;
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

        // How far a point lies from a straight piece of a line, squared. The hit test
        // walks the arc's own pieces with this, in SCREEN space rather than on the
        // ground: a band now flies hundreds of metres above the corridor it describes,
        // so where it lies on the map and where the player sees it are two different
        // places, and only one of them can be pointed at.
        public static float DistanceSqToSegment(float x1, float y1, float x2, float y2, float px, float py)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            float lengthSq = (dx * dx) + (dy * dy);
            float t = lengthSq <= 0f ? 0f : (((px - x1) * dx) + ((py - y1) * dy)) / lengthSq;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            float cx = x1 + (t * dx);
            float cy = y1 + (t * dy);
            return ((px - cx) * (px - cx)) + ((py - cy) * (py - cy));
        }

        // The casing colour for the dot at a band's end: the band's own hue driven
        // well down in lightness. A neutral black outline would read as a fifth colour
        // on a map that already carries a ramp; this one disappears into its own dot
        // and only does its job against the streets the dot sits among.
        public static void OutlineColour(float carriedShare, out float r, out float g, out float b)
        {
            Colour(carriedShare, out float fillR, out float fillG, out float fillB);
            r = fillR * OutlineDarkening;
            g = fillG * OutlineDarkening;
            b = fillB * OutlineDarkening;
        }

        private const float OutlineDarkening = 0.28f;

        private static float Lerp(float from, float to, float t) => from + ((to - from) * t);
    }
}
