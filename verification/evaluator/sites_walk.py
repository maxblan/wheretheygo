"""S2 v2: exact site selection over pedestrian-network nodes.

Re-derived from docs/formal-specification.md §2 (v2): candidates are given nodes
with binary32 scores; two conflict when the exact integer walking time between
them (spec §7.2 v2 edge costs) is strictly below `separation_ms`; a solution is at
most `max_sites` pairwise non-conflicting candidates; the objective is the rational
score sum. Checks the mod's exact selection for feasibility, ranking and the
consistency of its integer-scaled value/bound, recomputes the greedy baseline, and
brute-forces the optimum on small instances.
"""

from __future__ import annotations

from fractions import Fraction

from common.canonical import bits_to_fraction, bits_to_f32
from evaluator.heatmap_walk import Graph

BRUTE_FORCE_MAX_CANDIDATES = 40


def conflict_pairs(graph: Graph, nodes: list[int], separation_ms: int) -> list[list[int]]:
    slot_of = {node: k for k, node in enumerate(nodes)}
    pairs = []
    for k, node in enumerate(nodes):
        if separation_ms <= 0:
            break
        for other, _ in graph.times_within(node, 0, separation_ms - 1).items():
            j = slot_of.get(other)
            if j is not None and j > k:
                pairs.append([k, j])
    return pairs


def feasible(selection: list[int], nodes: list[int], conflicts: set[tuple[int, int]],
             max_sites: int) -> tuple[bool, str]:
    slot_of = {node: k for k, node in enumerate(nodes)}
    if len(selection) > max_sites:
        return False, f"{len(selection)} sites exceed budget {max_sites}"
    if len(set(selection)) != len(selection):
        return False, "duplicate node"
    slots = []
    for node in selection:
        if node not in slot_of:
            return False, f"node {node} is not a candidate"
        slots.append(slot_of[node])
    for i in range(len(slots)):
        for j in range(i + 1, len(slots)):
            a, b = sorted((slots[i], slots[j]))
            if (a, b) in conflicts:
                return False, f"nodes {selection[i]} and {selection[j]} are within the spacing"
    return True, "ok"


def greedy(nodes: list[int], scores: list[Fraction], conflicts: set[tuple[int, int]],
           max_sites: int) -> list[int]:
    order = sorted(range(len(nodes)), key=lambda k: (-scores[k], nodes[k]))
    chosen: list[int] = []
    for k in order:
        if len(chosen) >= max_sites:
            break
        if all(tuple(sorted((k, j))) not in conflicts for j in chosen):
            chosen.append(k)
    return [nodes[k] for k in chosen]


def brute_force(nodes: list[int], scores: list[Fraction], conflicts: set[tuple[int, int]],
                max_sites: int) -> tuple[Fraction, list[int]]:
    n = len(nodes)
    best, best_set = Fraction(0), []
    suffix = [Fraction(0)] * (n + 1)
    for i in range(n - 1, -1, -1):
        suffix[i] = suffix[i + 1] + scores[i]

    def dfs(pos: int, chosen: list[int], value: Fraction) -> None:
        nonlocal best, best_set
        if value > best:
            best, best_set = value, [nodes[k] for k in chosen]
        if pos == n or len(chosen) == max_sites or value + suffix[pos] <= best:
            return
        if all(tuple(sorted((pos, j))) not in conflicts for j in chosen):
            chosen.append(pos)
            dfs(pos + 1, chosen, value + scores[pos])
            chosen.pop()
        dfs(pos + 1, chosen, value)

    dfs(0, [], Fraction(0))
    return best, best_set


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    graph = Graph(data)
    nodes = list(data["candidate_nodes"])
    scores = [bits_to_fraction(b) for b in data["candidate_scores_b32"]]
    separation = data["separation_ms"]
    max_sites = data["max_sites"]
    pairs = conflict_pairs(graph, nodes, separation)
    conflicts = {(a, b) for a, b in pairs}

    selection = list(solution["site_nodes"])
    ok, why = feasible(selection, nodes, conflicts, max_sites)
    reported = [bits_to_fraction(b) for b in solution["site_scores_b32"]]
    score_of = dict(zip(nodes, scores))
    scores_exact = len(reported) == len(selection) and all(
        reported[k] == score_of.get(node) for k, node in enumerate(selection))
    ranked = all((score_of[selection[k]], -selection[k]) >= (score_of[selection[k + 1]], -selection[k + 1])
                 for k in range(len(selection) - 1) if selection[k] in score_of and selection[k + 1] in score_of)
    value = sum((score_of.get(node, Fraction(0)) for node in selection), Fraction(0))

    expected_greedy = greedy(nodes, scores, conflicts, max_sites)
    subject_greedy = list(solution.get("greedy_nodes", []))
    greedy_value = sum((score_of[node] for node in expected_greedy), Fraction(0))

    shift = int(solution["scale_shift"])
    scale = Fraction(2) ** shift
    scaled_value = Fraction(int(solution["value_scaled"]))
    scaled_bound = Fraction(int(solution["upper_bound_scaled"]))
    weights_exact = bool(solution["weights_exact"])
    if weights_exact:
        value_consistent = scaled_value == value * scale
    else:
        value_consistent = scaled_value <= value * scale < scaled_value + len(selection)

    result = {
        "candidates": len(nodes),
        "conflict_pairs": pairs,
        "conflict_pair_count": len(pairs),
        "feasible": ok,
        "feasible_reason": why,
        "reported_scores_exact": scores_exact,
        "ranked": ranked,
        "objective_value": str(value),
        "greedy_expected": expected_greedy,
        "greedy_faithful": subject_greedy == expected_greedy,
        "greedy_value": str(greedy_value),
        "optimal_claimed": bool(solution["optimal"]),
        "weights_exact": weights_exact,
        "value_consistent": value_consistent,
        "upper_bound": str((scaled_bound + (0 if weights_exact else max_sites)) / scale),
        "search_nodes": int(solution.get("search_nodes", 0)),
    }
    if len(nodes) <= BRUTE_FORCE_MAX_CANDIDATES:
        opt, opt_set = brute_force(nodes, scores, conflicts, max_sites)
        result["enumeration_optimum"] = str(opt)
        result["enumeration_optimum_set"] = opt_set
        result["enumeration_gap"] = str(opt - value)
    return result
