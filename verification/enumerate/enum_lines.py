"""S7 complete enumeration: the exact optimum of the credited-sum set objective
over every candidate subset of size <= K. This IS the proof artifact for bounded
instances — no solver involved; the objective is the declared one implemented in
evaluator.lineset (the subset iteration here is the only added logic).
"""

from __future__ import annotations

import itertools
from fractions import Fraction

from evaluator.lineset import parse, set_objective

ENUM_MAX_CANDIDATES = 12


def enumerate_optimum(instance: dict) -> dict:
    p = parse(instance)
    n = len(p["candidates"])
    if n > ENUM_MAX_CANDIDATES:
        raise ValueError(f"too many candidates to enumerate: {n}")
    k = p["max_accept"]
    best_lo = None
    best_set: list[int] = []
    best_hi = None
    rows = []
    total = 0
    for size in range(0, k + 1):
        for subset in itertools.combinations(range(n), size):
            lo, hi, tie = set_objective(p, list(subset))
            total += 1
            rows.append({"subset": list(subset), "lo": str(lo), "hi": str(hi),
                         "tie": tie})
            if best_lo is None or lo > best_lo:
                best_lo, best_hi, best_set = lo, hi, list(subset)
    # Ambiguity: another subset whose hi exceeds the best lo.
    ambiguous = [
        r["subset"] for r in rows
        if r["subset"] != best_set and Fraction(r["hi"]) > best_lo
    ] if best_lo is not None else []
    return {
        "subsets_evaluated": total,
        "complete": True,
        "optimum_set": best_set,
        "optimum_lo": str(best_lo),
        "optimum_hi": str(best_hi),
        "ambiguous_with_optimum": ambiguous,
        "all": rows,
    }
