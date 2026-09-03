"""C1.2: SelectKth and PositivePercentile against the sorted reference, by
complete enumeration of the bounded instance family (see the generator). The
reference is `sorted()`; the subject runs the mod's Hoare quickselect. Values
compare with `==` (an order statistic is an element, no arithmetic; ±0.0 are
order-equal)."""

from __future__ import annotations

import math

from common.canonical import bits_to_f32


def clamp(v: int, lo: int, hi: int) -> int:
    return lo if v < lo else (hi if v > hi else v)


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    select_ok = 0
    select_bad = []
    for i, case in enumerate(data["select_cases"]):
        values = [bits_to_f32(b) for b in case["values_b32"]]
        k = clamp(case["k"], 0, len(values) - 1)
        expected = sorted(values)[k]
        got = bits_to_f32(solution["select_results_b32"][i])
        if got == expected:
            select_ok += 1
        else:
            select_bad.append({"case": i, "expected": expected, "got": got})

    pct_ok = 0
    pct_bad = []
    for i, case in enumerate(data["percentile_cases"]):
        values = [bits_to_f32(b) for b in case["values_b32"]]
        p = bits_to_f32(case["percentile_b32"])
        positives = sorted(v for v in values if v > 0.0)
        if not positives:
            expected = 0.0
        else:
            k = clamp(int(math.floor(len(positives) * p)), 0, len(positives) - 1)
            expected = positives[k]
        got = bits_to_f32(solution["percentile_results_b32"][i])
        if got == expected:
            pct_ok += 1
        else:
            pct_bad.append({"case": i, "expected": expected, "got": got})

    return {
        "ok": not select_bad and not pct_bad,
        "select_cases": len(data["select_cases"]),
        "select_matched": select_ok,
        "select_mismatches": select_bad[:10],
        "percentile_cases": len(data["percentile_cases"]),
        "percentile_matched": pct_ok,
        "percentile_mismatches": pct_bad[:10],
    }
