using System;

namespace TransitArchitect
{
    // A point or vector on the map plane, with Unity's float2 shape: `x` is the world
    // x and `y` the world z, so code moved off float2 reads the same and the mod
    // converts at its boundary with a two-field copy. Every operation here is the
    // exact formula Unity.Mathematics uses (decompiled from Unity.Mathematics.dll),
    // so a value computed on this side is bit-identical to the same expression on
    // float2 — which is what lets the alignment stage run offline and be checked
    // against the game's numbers.
    internal struct float2Like : IEquatable<float2Like>
    {
        public float x;
        public float y;

        public float2Like(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static float2Like Zero => default;

        public static float2Like operator +(float2Like a, float2Like b) => new float2Like(a.x + b.x, a.y + b.y);

        public static float2Like operator -(float2Like a, float2Like b) => new float2Like(a.x - b.x, a.y - b.y);

        public static float2Like operator -(float2Like a) => new float2Like(-a.x, -a.y);

        public static float2Like operator *(float2Like a, float s) => new float2Like(a.x * s, a.y * s);

        public static float2Like operator *(float s, float2Like a) => new float2Like(s * a.x, s * a.y);

        public static float2Like operator /(float2Like a, float s) => new float2Like(a.x / s, a.y / s);

        public static bool operator ==(float2Like a, float2Like b) => a.x == b.x && a.y == b.y;

        public static bool operator !=(float2Like a, float2Like b) => !(a == b);

        // math.dot: x.x * y.x + x.y * y.y, left to right.
        public static float Dot(float2Like a, float2Like b) => (a.x * b.x) + (a.y * b.y);

        public static float LengthSq(float2Like v) => Dot(v, v);

        // math.length: sqrt(dot(x, x)) with Unity's sqrt being (float)Math.Sqrt.
        public static float Length(float2Like v) => (float)Math.Sqrt(Dot(v, v));

        // math.distance(x, y) is length(y - x): the subtraction order is kept.
        public static float Distance(float2Like a, float2Like b) => Length(b - a);

        public static float DistanceSq(float2Like a, float2Like b) => LengthSq(b - a);

        // math.lerp: start + t * (end - start).
        public static float2Like Lerp(float2Like a, float2Like b, float t) => a + (t * (b - a));

        public readonly bool Equals(float2Like other) => x == other.x && y == other.y;

        public readonly override bool Equals(object? obj) => obj is float2Like other && Equals(other);

        public readonly override int GetHashCode()
        {
            unchecked
            {
                return (x.GetHashCode() * 397) ^ y.GetHashCode();
            }
        }
    }

    // Integer pair with int2's shape, for grid sizes and cell coordinates.
    internal struct int2Like : IEquatable<int2Like>
    {
        public int x;
        public int y;

        public int2Like(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public static bool operator ==(int2Like a, int2Like b) => a.x == b.x && a.y == b.y;

        public static bool operator !=(int2Like a, int2Like b) => !(a == b);

        public readonly bool Equals(int2Like other) => x == other.x && y == other.y;

        public readonly override bool Equals(object? obj) => obj is int2Like other && Equals(other);

        public readonly override int GetHashCode()
        {
            unchecked
            {
                return (x * 397) ^ y;
            }
        }
    }
}
