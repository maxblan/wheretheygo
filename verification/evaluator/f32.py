"""Exact IEEE-754 binary32 emulation.

The mod computes in C# `float` (binary32, round-to-nearest-even; .NET on x86-64
uses SSE scalar instructions, no extended precision). Python floats are binary64.
For +, -, *, / and sqrt, computing in binary64 and rounding the result once to
binary32 is EXACTLY the correctly-rounded binary32 operation (the double has more
than 2x the significand bits; single-rounding through double is exact for these
operations — W. Kahan / Figueroa's theorem). `struct.pack('<f', x)` performs that
final rounding.

So every function here returns the bit-exact value the mod computes, as a Python
float that holds a binary32 value exactly. `frac` converts such a value to the
exact rational it denotes.

C# specifics mirrored:
- `(float)Math.Sqrt(x)` where x is float: widen exactly to double, correctly
  rounded double sqrt (== math.sqrt), then round to float — a double rounding,
  faithfully reproduced (NOT fused into a single-rounded f32 sqrt).
- `Math.Round(double, MidpointRounding.AwayFromZero)` == round-half-away.
"""

from __future__ import annotations

import math
import struct
from fractions import Fraction


def r(x: float) -> float:
    """Round a binary64 value to the nearest binary32 (ties to even)."""
    return struct.unpack("<f", struct.pack("<f", x))[0]


def add(a: float, b: float) -> float:
    return r(a + b)


def sub(a: float, b: float) -> float:
    return r(a - b)


def mul(a: float, b: float) -> float:
    return r(a * b)


def div(a: float, b: float) -> float:
    return r(a / b)


def csharp_float_sqrt(x: float) -> float:
    """(float)Math.Sqrt(x): double sqrt then rounded to float (double rounding)."""
    return r(math.sqrt(x))


def round_away(x: float) -> int:
    """Math.Round(x, MidpointRounding.AwayFromZero) for non-negative x."""
    floor = math.floor(x)
    return int(floor) + (1 if (x - floor) >= 0.5 else 0)


def frac(x: float) -> Fraction:
    """Exact rational value of a float that holds a binary32 (or binary64) value."""
    return Fraction(x)


FLOAT_MAX = struct.unpack("<f", struct.pack("<I", 0x7F7FFFFF))[0]
