"""S7 v2 complete enumeration: the exact optimum of the lexicographic set key
(min(equity share, floor), passenger time saved) over feasible candidate
subsets, evaluated in parallel with evaluator.lineset_time. Two regimes:
complete (every subset of size ≤ K fits the budget) and bounded (exact only for
subsets of size ≤ k′, compared at equal cardinality). The verdict says which.
"""

from __future__ import annotations

import itertools
import os
from fractions import Fraction
from math import comb
from multiprocessing import Pool

from evaluator.lineset_time import parse, evaluate, key_of, feasible

ENUM_MAX_CANDIDATES = 64
DEFAULT_BUDGET = int(os.environ.get("ENUM_BUDGET", "12000"))

_P = None
_BEFORE = None


def _init(instance: dict) -> None:
    global _P, _BEFORE
    _P = parse(instance)
    _BEFORE = evaluate(_P, [], None)["after"]


def _evaluate(subset: tuple) -> tuple:
    chosen = list(subset)
    key, ev = key_of(_P, chosen, _BEFORE)
    ok, _ = feasible(_P, chosen, ev, _BEFORE)
    return subset, str(key[0]), str(key[1]), ok, ev["tie_affected"]


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
    k = p["max_lines"]
    k_enum = largest_enumerable_size(n, k, budget)
    if k_enum < 0:
        raise ValueError("budget too small")
    subsets = [s for size in range(0, k_enum + 1) for s in itertools.combinations(range(n), size)]
    workers = workers or max(1, min(16, (os.cpu_count() or 2) - 1))
    with Pool(workers, initializer=_init, initargs=(instance,)) as pool:
        rows = pool.map(_evaluate, subsets, chunksize=8)
    best = None
    best_set: list[int] = []
    best_by_size: dict[int, dict] = {}
    infeasible = 0
    tie_any = False
    optimum_count = 0
    for subset, cov, saved, ok, tie in rows:
        tie_any = tie_any or tie
        if not ok:
            infeasible += 1
            continue
        key = (Fraction(cov), Fraction(saved))
        if best is None or key > best:
            best, best_set = key, list(subset)
            optimum_count = 1
        elif key == best:
            optimum_count += 1
        size = len(subset)
        if size not in best_by_size or key > best_by_size[size]["key"]:
            best_by_size[size] = {"key": key, "set": list(subset)}
    return {
        "complete": k_enum >= k,
        "declared_k": k,
        "enumerated_max_size": k_enum,
        "subsets_evaluated": len(subsets),
        "infeasible_subsets": infeasible,
        "optimum_set": best_set,
        "optimum_coverage": None if best is None else str(best[0]),
        "optimum_saved": None if best is None else str(best[1]),
        "best_by_size": {str(s): {"coverage": str(v["key"][0]), "saved": str(v["key"][1]), "set": v["set"]}
                         for s, v in best_by_size.items()},
        "optimum_ties": optimum_count,
        "tie_affected": tie_any or optimum_count > 1,
    }
