"""S5 calling points: independent exact re-implementation.

PlanCallingPoints and SelectCallingPoints are pure float32 procedures; with exact
binary32 emulation the evaluator's expected output must match the subject's
byte-for-byte — no tolerance. Additionally the structural invariants (monotone
offsets, endpoints pinned, termini/must-call always kept) are asserted on the
result itself, so a bug shared by both implementations would still have to pass
the invariants.
"""

from __future__ import annotations

import math
from fractions import Fraction

from common.canonical import bits_to_f32, bits_to_fraction, f32_bits
from evaluator import f32


def plan_calling_points(length: float, spacing: float, buffer: int) -> list[float]:
    if buffer <= 0 or length <= 0.0 or spacing <= 0.0:
        return []
    # (int)Math.Round(length / spacing, AwayFromZero): f32 division, then
    # round-half-away on its (exact) double value.
    intervals = max(1, f32.round_away(f32.div(length, spacing)))
    count = min(intervals + 1, buffer)
    out = []
    for i in range(count):
        if i == intervals:
            out.append(length)
        else:
            # length * i / intervals with C# float semantics: f32 mul, f32 div.
            out.append(f32.div(f32.mul(length, float(i)), float(intervals)))
    return out


def positive_percentile(values: list[float], percentile: float) -> float:
    positives = sorted(v for v in values if v > 0.0)
    if not positives:
        return 0.0
    # k = ClampInt((int)Math.Floor(count * (double)percentile), 0, count-1);
    k = min(max(int(math.floor(len(positives) * percentile)), 0), len(positives) - 1)
    return positives[k]


def select_calling_points(scores: list[float], floor_share: float,
                          must_call: list[bool]) -> list[bool]:
    count = len(scores)
    if count <= 0:
        return []
    floor = f32.mul(positive_percentile(scores, 0.5), floor_share)
    keep = []
    for i in range(count):
        pinned = i < len(must_call) and must_call[i]
        keep.append(i == 0 or i == count - 1 or pinned or scores[i] >= floor)
    return keep


def plan_invariants(offsets: list[float], length: float) -> tuple[bool, str]:
    if not offsets:
        return True, "empty plan"
    if offsets[0] != 0.0:
        return False, "first offset not 0"
    for i in range(1, len(offsets)):
        if not offsets[i] > offsets[i - 1]:
            return False, f"offsets not strictly increasing at {i}"
    exact = [Fraction(o) for o in offsets]
    if exact[-1] > Fraction(length):
        return False, "offset beyond the line"
    return True, "ok"


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    plans = []
    all_ok = True
    for spec, subject in zip(data["plans"], solution["plans"]):
        length = bits_to_f32(spec["length_b32"])
        spacing = bits_to_f32(spec["spacing_b32"])
        expected = plan_calling_points(length, spacing, spec["buffer"])
        got = [bits_to_f32(b) for b in subject["offsets_b32"]]
        exact_match = [f32_bits(v) for v in expected] == list(subject["offsets_b32"])
        inv_ok, inv_why = plan_invariants(got, length)
        # The last offset is pinned to `length` only when the plan was not cut
        # short by the buffer.
        full = subject["count"] < spec["buffer"] or subject["count"] == 0
        end_pinned = (not full) or (not got) or got[-1] == length
        plans.append({
            "expected_count": len(expected),
            "subject_count": subject["count"],
            "exact_match": exact_match,
            "invariants": inv_ok,
            "invariant_reason": inv_why,
            "end_pinned": end_pinned,
        })
        all_ok = all_ok and exact_match and inv_ok and end_pinned

    selections = []
    for spec, subject in zip(data["selections"], solution["selections"]):
        scores = [bits_to_f32(b) for b in spec["scores_b32"]]
        floor_share = bits_to_f32(spec["floor_share_b32"])
        must_call = list(spec["must_call"])
        expected = select_calling_points(scores, floor_share, must_call)
        got = list(subject["keep"])
        exact_match = expected == got
        termini_kept = (not got) or (got[0] and got[-1])
        must_call_kept = all(got[i] for i in range(len(got))
                             if i < len(must_call) and must_call[i])
        selections.append({
            "exact_match": exact_match,
            "termini_kept": termini_kept,
            "must_call_kept": must_call_kept,
        })
        all_ok = all_ok and exact_match and termini_kept and must_call_kept

    return {"ok": all_ok, "plans": plans, "selections": selections}
