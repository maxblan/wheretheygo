"""S7 complete enumeration: the exact optimum of the credited-sum set objective
over candidate subsets, evaluated in parallel with the exact evaluator.

Two regimes, and the verdict says which one applied:

  * complete — every subset of size <= K fits the budget, so the maximum is the
    global optimum of the declared problem;
  * bounded  — the pool is too large (a real city: 39 candidates at K = 5 is
    667,928 subsets at seconds each). Then every subset of size <= k' is
    enumerated for the largest k' the budget allows, and the result is the exact
    optimum FOR k' LINES, compared against the greedy's first k' acceptances at
    equal cardinality. That is a real statement about complementarity among k'
    lines; it is NOT the K-line optimum, and the verdict never calls it that.

The objective is the declared one implemented in evaluator.lineset; this module
adds subset iteration and parallelism only.
"""

from __future__ import annotations

import itertools
import os
from fractions import Fraction
from math import comb
from multiprocessing import Pool

from evaluator.lineset import parse, set_objective

ENUM_MAX_CANDIDATES = 64
DEFAULT_BUDGET = int(os.environ.get("ENUM_BUDGET", "12000"))

_PARSED = None


def _init(instance: dict) -> None:
    global _PARSED
    _PARSED = parse(instance)


def _evaluate(subset: tuple) -> tuple:
    lo, hi, tie = set_objective(_PARSED, list(subset))
    return subset, str(lo), str(hi), tie


def largest_enumerable_size(n: int, k: int, budget: int) -> int:
    total = 0
    for size in range(k + 1):
        total += comb(n, size)
        if total > budget:
            return size - 1
    return k


def enumerate_optimum(instance: dict, budget: int = DEFAULT_BUDGET,
                      workers: int | None = None) -> dict:
    p = parse(instance)
    n = len(p["candidates"])
    if n > ENUM_MAX_CANDIDATES:
        raise ValueError(f"too many candidates to enumerate: {n}")
    k = p["max_accept"]
    k_enum = largest_enumerable_size(n, k, budget)
    if k_enum < 1:
        raise ValueError("budget too small to enumerate even single lines")

    subsets = [
        s for size in range(0, k_enum + 1)
        for s in itertools.combinations(range(n), size)
    ]
    workers = workers or max(1, min(16, (os.cpu_count() or 2) - 1))
    with Pool(workers, initializer=_init, initargs=(instance,)) as pool:
        rows = pool.map(_evaluate, subsets, chunksize=8)

    best_lo = None
    best_hi = None
    best_set: list[int] = []
    out_rows = []
    # Per exact cardinality as well, so a bounded run can compare the greedy
    # prefix of equal length against the best set of that length.
    best_by_size: dict[int, dict] = {}
    for subset, lo_s, hi_s, tie in rows:
        lo = Fraction(lo_s)
        out_rows.append({"subset": list(subset), "lo": lo_s, "hi": hi_s, "tie": tie})
        if best_lo is None or lo > best_lo:
            best_lo, best_hi, best_set = lo, Fraction(hi_s), list(subset)
        size = len(subset)
        cur = best_by_size.get(size)
        if cur is None or lo > Fraction(cur["lo"]):
            best_by_size[size] = {"subset": list(subset), "lo": lo_s, "hi": hi_s}

    ambiguous = [
        r["subset"] for r in out_rows
        if r["subset"] != best_set and Fraction(r["hi"]) > best_lo
    ] if best_lo is not None else []
    return {
        "subsets_evaluated": len(out_rows),
        "declared_k": k,
        "enumerated_max_size": k_enum,
        "complete": k_enum == k,
        "workers": workers,
        "optimum_set": best_set,
        "optimum_lo": str(best_lo),
        "optimum_hi": str(best_hi),
        "best_by_size": {str(s): v for s, v in sorted(best_by_size.items())},
        "ambiguous_with_optimum": ambiguous,
        "all": out_rows,
    }
