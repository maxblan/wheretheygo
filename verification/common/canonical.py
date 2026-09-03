"""Canonical instance/solution IO shared by every pipeline component.

This module deliberately contains NO computation logic — only serialization,
hashing and lossless float32 <-> rational conversion. The reference model and
the evaluator must stay independent implementations; what they may share is the
definition of the wire format, which is this file.

Float policy: every float travels as its IEEE-754 binary32 bit pattern (uint32).
A binary32 value is exactly a rational number; `bits_to_fraction` returns it.
"""

from __future__ import annotations

import hashlib
import json
import struct
from fractions import Fraction

SCHEMA_VERSION = 1


def f32_bits(value: float) -> int:
    """Bit pattern of the binary32 nearest to `value` (round-to-nearest-even)."""
    return struct.unpack("<I", struct.pack("<f", value))[0]


def bits_to_f32(bits: int) -> float:
    """The float64 exactly equal to the binary32 the bits denote."""
    return struct.unpack("<f", struct.pack("<I", bits))[0]


def bits_to_fraction(bits: int) -> Fraction:
    """The exact rational value of the binary32 the bits denote.

    Raises on NaN/Inf: the instance format forbids them.
    """
    value = bits_to_f32(bits)
    if value != value or value in (float("inf"), float("-inf")):
        raise ValueError(f"non-finite float32 bits 0x{bits:08x}")
    # float64 holding a binary32 value converts exactly.
    return Fraction(value)


def canonical_bytes(obj: object) -> bytes:
    """Canonical JSON encoding: sorted keys, no whitespace, ASCII."""
    return json.dumps(
        obj, sort_keys=True, separators=(",", ":"), ensure_ascii=True
    ).encode("ascii")


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def instance_hash(instance: dict) -> str:
    """Hash over the canonical form, excluding any embedded hash field."""
    body = {k: v for k, v in instance.items() if k != "hash"}
    return sha256_hex(canonical_bytes(body))


def write_instance(path: str, instance: dict) -> None:
    instance = dict(instance)
    instance["schema_version"] = SCHEMA_VERSION
    instance["hash"] = instance_hash(instance)
    with open(path, "w", encoding="ascii") as f:
        json.dump(instance, f, sort_keys=True, indent=1)
        f.write("\n")


def load_instance(path: str) -> dict:
    with open(path, "r", encoding="ascii") as f:
        instance = json.load(f)
    if instance.get("schema_version") != SCHEMA_VERSION:
        raise ValueError(f"{path}: unsupported schema_version")
    expected = instance.get("hash")
    actual = instance_hash(instance)
    if expected != actual:
        raise ValueError(f"{path}: hash mismatch ({expected} != {actual})")
    return instance
