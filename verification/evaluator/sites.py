"""S2 site selection: independent exact checker.

Re-derives, from the specification alone (docs/formal-specification.md §2):
  - the candidate set (positive 3x3 local maxima, plateau tie to the lowest index),
  - the greedy outcome under (score desc, index asc),
  - feasibility of the subject's output,
  - the exact rational objective value of the subject's selection,
  - for small candidate sets, the brute-force optimum (cross-check for SCIP).

No mod code, no floats in comparisons that matter: scores are exact binary32
values; ordering and sums are exact.
"""

from __future__ import annotations

import itertools
from fractions import Fraction

from common.canonical import bits_to_fraction

BRUTE_FORCE_MAX_CANDIDATES = 22


def candidates(width: int, height: int, scores: list[Fraction]) -> list[int]:
    result = []
    for y in range(height):
        for x in range(width):
            i = x + y * width
            s = scores[i]
            if s <= 0:
                continue
            is_max = True
            for dy in (-1, 0, 1):
                ny = y + dy
                if ny < 0 or ny >= height:
                    continue
                for dx in (-1, 0, 1):
                    if dx == 0 and dy == 0:
                        continue
                    nx = x + dx
                    if nx < 0 or nx >= width:
                        continue
                    j = nx + ny * width
                    if scores[j] > s or (scores[j] == s and j < i):
                        is_max = False
                        break
                if not is_max:
                    break
            if is_max:
                result.append(i)
    return result


def chebyshev(a: int, b: int, width: int) -> int:
    ax, ay = a % width, a // width
    bx, by = b % width, b // width
    return max(abs(ax - bx), abs(ay - by))


def feasible(selection: list[int], width: int, separation: int, max_sites: int,
             candidate_set: set[int]) -> tuple[bool, str]:
    if len(selection) > max_sites:
        return False, f"{len(selection)} sites > budget {max_sites}"
    for i in selection:
        if i not in candidate_set:
            return False, f"site {i} is not a candidate"
    for a, b in itertools.combinations(selection, 2):
        if chebyshev(a, b, width) < separation:
            return False, f"sites {a},{b} closer than separation {separation}"
    if len(set(selection)) != len(selection):
        return False, "duplicate site"
    return True, "ok"


def greedy(cands: list[int], scores: list[Fraction], width: int,
           separation: int, max_sites: int, tie_index_desc: bool = False) -> list[int]:
    order = sorted(
        cands,
        key=lambda i: (-scores[i], -i if tie_index_desc else i),
    )
    accepted: list[int] = []
    for i in order:
        if len(accepted) >= max_sites:
            break
        if all(chebyshev(i, j, width) >= separation for j in accepted):
            accepted.append(i)
    return accepted


def has_score_ties(cands: list[int], scores: list[Fraction]) -> bool:
    values = [scores[i] for i in cands]
    return len(values) != len(set(values))


def objective(selection: list[int], scores: list[Fraction]) -> Fraction:
    return sum((scores[i] for i in selection), Fraction(0))


def brute_force_optimum(cands: list[int], scores: list[Fraction], width: int,
                        separation: int, max_sites: int) -> tuple[Fraction, list[int]]:
    """Complete enumeration over all feasible subsets (independent of the MIP)."""
    if len(cands) > BRUTE_FORCE_MAX_CANDIDATES:
        raise ValueError(f"too many candidates for enumeration: {len(cands)}")
    best = Fraction(0)
    best_set: list[int] = []
    n = len(cands)
    # Depth-first with the candidates in index order; prune on remaining mass.
    suffix = [Fraction(0)] * (n + 1)
    for i in range(n - 1, -1, -1):
        suffix[i] = suffix[i + 1] + scores[cands[i]]

    def dfs(pos: int, chosen: list[int], value: Fraction) -> None:
        nonlocal best, best_set
        if value > best:
            best = value
            best_set = list(chosen)
        if pos == n or len(chosen) == max_sites or value + suffix[pos] <= best:
            return
        cand = cands[pos]
        if all(chebyshev(cand, j, width) >= separation for j in chosen):
            chosen.append(cand)
            dfs(pos + 1, chosen, value + scores[cand])
            chosen.pop()
        dfs(pos + 1, chosen, value)

    dfs(0, [], Fraction(0))
    return best, best_set


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    width = data["width"]
    height = data["height"]
    scores = [bits_to_fraction(b) for b in data["scores_b32"]]
    separation = data["min_separation"]
    max_sites = data["max_sites"]

    cands = candidates(width, height, scores)
    cand_set = set(cands)
    selection = list(solution["site_indices"])

    ok_feasible, why = feasible(selection, width, separation, max_sites, cand_set)

    expected = greedy(cands, scores, width, separation, max_sites)
    ties = has_score_ties(cands, scores)
    greedy_faithful = selection == expected
    tie_note = None
    if not greedy_faithful and ties:
        alt = greedy(cands, scores, width, separation, max_sites, tie_index_desc=True)
        if selection == alt:
            greedy_faithful = True
            tie_note = "matches greedy under the reversed tie order (score ties present)"

    # Reported scores must be the exact grid values of the reported indices.
    reported = [bits_to_fraction(b) for b in solution["site_scores_b32"]]
    scores_exact = all(
        i < len(scores) and reported[k] == scores[i]
        for k, i in enumerate(selection)
    ) and len(reported) == len(selection)

    greedy_value = objective(selection, scores)
    result = {
        "candidates": len(cands),
        "feasible": ok_feasible,
        "feasible_reason": why,
        "greedy_faithful": greedy_faithful,
        "greedy_expected": expected,
        "score_ties_present": ties,
        "tie_note": tie_note,
        "reported_scores_exact": scores_exact,
        "objective_value": str(greedy_value),
        "truncated": bool(solution.get("truncated", False)),
    }
    if "exact_site_indices" in solution:
        result["exact"] = check_exact(solution, scores, width, separation, max_sites,
                                      cand_set)
    if len(cands) <= BRUTE_FORCE_MAX_CANDIDATES:
        opt, opt_set = brute_force_optimum(cands, scores, width, separation, max_sites)
        result["enumeration_optimum"] = str(opt)
        result["enumeration_optimum_set"] = opt_set
        result["gap"] = str(opt - greedy_value)
        result["greedy_is_optimal"] = opt == greedy_value
        if "exact" in result:
            exact_value = Fraction(result["exact"]["objective_value"])
            result["exact"]["enumeration_gap"] = str(opt - exact_value)
            result["exact"]["enumeration_agrees"] = opt == exact_value
    return result


def check_exact(solution: dict, scores: list[Fraction], width: int, separation: int,
                max_sites: int, cand_set: set[int]) -> dict:
    """The exact selection (SuitabilityExactSites): feasibility, exact reported
    scores, ranking order, and the consistency of its own integer-scaled value and
    bound with the rational objective. Optimality itself is judged in run.py against
    the certified optimum."""
    selection = list(solution["exact_site_indices"])
    ok_feasible, why = feasible(selection, width, separation, max_sites, cand_set)
    reported = [bits_to_fraction(b) for b in solution["exact_site_scores_b32"]]
    scores_exact = len(reported) == len(selection) and all(
        i < len(scores) and reported[k] == scores[i] for k, i in enumerate(selection))
    ranked = all(
        (scores[selection[k]], -selection[k]) >= (scores[selection[k + 1]], -selection[k + 1])
        for k in range(len(selection) - 1))
    value = objective(selection, scores)
    shift = int(solution["exact_scale_shift"])
    scale = Fraction(2) ** shift
    scaled_value = Fraction(int(solution["exact_value_scaled"]))
    scaled_bound = Fraction(int(solution["exact_upper_bound_scaled"]))
    weights_exact = bool(solution["exact_weights_exact"])
    # With exact weights the scaled value IS the objective; with floored weights it
    # undershoots by less than one unit per chosen site.
    if weights_exact:
        value_consistent = scaled_value == value * scale
    else:
        value_consistent = scaled_value <= value * scale < scaled_value + len(selection)
    return {
        "site_indices": selection,
        "feasible": ok_feasible,
        "feasible_reason": why,
        "reported_scores_exact": scores_exact,
        "ranked": ranked,
        "objective_value": str(value),
        "optimal_claimed": bool(solution["exact_optimal"]),
        "weights_exact": weights_exact,
        "scale_shift": shift,
        "value_consistent": value_consistent,
        # Ceiling in rational terms, slackened by one unit per site when weights
        # were floored (each floored weight is below its score by < 1 unit).
        "upper_bound": str((scaled_bound + (0 if weights_exact else max_sites)) / scale),
        "nodes": int(solution.get("exact_nodes", 0)),
    }
