"""S4 lattice paths: exact Dijkstra, feasibility, and certificate generation.

The certificate (checked by the SEPARATE checkcert_path.py) proves optimality
without trusting this module's Dijkstra: rational labels d with d[source] = 0 and
|d[a] - d[b]| <= c for every edge lower-bound every source→target path by
d[target]; a valid path of exactly that cost is therefore minimum-cost.
"""

from __future__ import annotations

import heapq
from fractions import Fraction

from common.canonical import bits_to_fraction
from evaluator.tolerances import distance_budget


def exact_dijkstra(node_count: int, edges: list[tuple[int, int, Fraction]],
                   source: int) -> list[Fraction | None]:
    adj: list[list[tuple[int, Fraction]]] = [[] for _ in range(node_count)]
    for a, b, c in edges:
        adj[a].append((b, c))
        adj[b].append((a, c))
    dist: list[Fraction | None] = [None] * node_count
    dist[source] = Fraction(0)
    heap = [(Fraction(0), source)]
    settled = [False] * node_count
    while heap:
        d, node = heapq.heappop(heap)
        if settled[node]:
            continue
        settled[node] = True
        for other, cost in adj[node]:
            nd = d + cost
            if dist[other] is None or nd < dist[other]:
                dist[other] = nd
                heapq.heappush(heap, (nd, other))
    return dist


def path_cost(path: list[int], edges: list[tuple[int, int, Fraction]]) -> Fraction | None:
    """Exact cost of the node path; None if a hop has no edge. Parallel edges
    take the cheapest, mirroring FindEdge."""
    lookup: dict[tuple[int, int], Fraction] = {}
    for a, b, c in edges:
        key = (min(a, b), max(a, b))
        if key not in lookup or c < lookup[key]:
            lookup[key] = c
    total = Fraction(0)
    for i in range(1, len(path)):
        key = (min(path[i - 1], path[i]), max(path[i - 1], path[i]))
        if key not in lookup:
            return None
        total += lookup[key]
    return total


def load_edges(data: dict) -> tuple[int, list[tuple[int, int, Fraction]]]:
    node_count = len(data["node_x_b32"])
    edges = [
        (a, b, bits_to_fraction(c))
        for a, b, c in zip(data["edge_a"], data["edge_b"], data["edge_cost_b32"])
    ]
    return node_count, edges


def check(instance: dict, solution: dict) -> tuple[dict, dict | None]:
    """Returns (report, certificate-or-None)."""
    data = instance["data"]
    node_count, edges = load_edges(data)
    source = data["from"]
    target = data["to"]
    max_cost = bits_to_fraction(data["max_cost_b32"])

    dist = exact_dijkstra(node_count, edges, source)
    exact = dist[target]

    if not solution.get("reachable", False):
        report = {
            "subject_reachable": False,
            "exact_reachable": exact is not None and exact <= max_cost,
        }
        report["ok"] = not report["exact_reachable"]
        return report, None

    path = list(solution["path"])
    cost = path_cost(path, edges)
    reported = bits_to_fraction(solution["distance_b32"])

    report = {
        "subject_reachable": True,
        "path_valid": cost is not None and path[0] == source and path[-1] == target,
        "path_cost": str(cost) if cost is not None else None,
        "exact_optimum": str(exact) if exact is not None else None,
        "path_is_optimal": cost is not None and exact is not None and cost == exact,
        "within_cap": cost is not None and cost <= max_cost,
        # The subject's float32-accumulated distance vs the exact cost of its path,
        # inside Higham's forward bound for L - 1 additions (evaluator/tolerances.py).
        "reported_distance": str(reported),
        "reported_matches_cost": cost is not None
        and abs(reported - cost) <= distance_budget(cost, len(path) - 1),
    }
    report["ok"] = all(
        report[k] for k in ("path_valid", "path_is_optimal", "within_cap",
                            "reported_matches_cost")
    )

    certificate = None
    if report["path_is_optimal"]:
        certificate = {
            "source": source,
            "target": target,
            "claimed_cost": f"{exact.numerator}/{exact.denominator}",
            "labels": [
                None if d is None else f"{d.numerator}/{d.denominator}"
                for d in dist
            ],
            "path": path,
        }
    return report, certificate
