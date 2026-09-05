"""S3 v2 equity measure (docs/formal-specification.md §7c), re-derived without mod
code: snap every served stop and every journey end to the pedestrian network
(heatmap_walk rules), compute the integer walking time from each node to the
nearest served stop within the horizon, count a journey as served when both ends
satisfy served[node] + access <= horizon, sum binary32 weights in trip order in
double, share = fl32(covered / total), and the weighted Gini of the origin access
walk (uncovered journeys at twice the horizon). Game = subject = evaluator, exactly.
"""

from __future__ import annotations

import heapq

from common.canonical import bits_to_f32, f32_bits
from evaluator import f32
from evaluator.heatmap_walk import Graph, snap


def served_walk(graph: Graph, stop_nodes: list[int], stop_access: list[int], horizon: int) -> list[int | None]:
    served: list[int | None] = [None] * graph.n
    for node, access in zip(stop_nodes, stop_access):
        if node < 0:
            continue
        for n, t in graph.times_within(node, access, horizon).items():
            if served[n] is None or t < served[n]:
                served[n] = t
    return served


def end_served(served, node: int, access: int, horizon: int) -> bool:
    return node >= 0 and served[node] is not None and served[node] + access <= horizon


def gini(values: list[float], weights: list[float]) -> float:
    n = len(values)
    if n == 0:
        return 0.0
    order = sorted(range(n), key=lambda i: (values[i], i))
    total_w = 0.0
    total_v = 0.0
    for i in range(n):
        total_w += weights[i]
        total_v += weights[i] * values[i]
    if total_w <= 0.0 or total_v <= 0.0:
        return 0.0
    running = 0.0
    area = 0.0
    for i in order:
        before = running
        running += weights[i] * values[i]
        area += weights[i] * (before + running)
    return 1.0 - area / (total_w * total_v)


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    graph = Graph(data)
    access_ms = int(data["access_ms"])
    horizon = int(data["horizon_ms"])
    sx = [bits_to_f32(b) for b in data["stop_x_b32"]]
    sz = [bits_to_f32(b) for b in data["stop_z_b32"]]
    stop_nodes, stop_access = snap(graph, sx, sz, access_ms)
    served = served_walk(graph, stop_nodes, stop_access, horizon)

    ox = [bits_to_f32(b) for b in data["trip_ox_b32"]]
    oz = [bits_to_f32(b) for b in data["trip_oz_b32"]]
    dx = [bits_to_f32(b) for b in data["trip_dx_b32"]]
    dz = [bits_to_f32(b) for b in data["trip_dz_b32"]]
    w = [bits_to_f32(b) for b in data["trip_w_b32"]]
    on, oa = snap(graph, ox, oz, access_ms)
    dn, da = snap(graph, dx, dz, access_ms)

    total = 0.0
    covered = 0.0
    trips_covered = 0
    off = 0
    walk = []
    for i in range(len(w)):
        total += w[i]
        if on[i] < 0 or dn[i] < 0:
            off += 1
        origin_ok = end_served(served, on[i], oa[i], horizon)
        if origin_ok and end_served(served, dn[i], da[i], horizon):
            trips_covered += 1
            covered += w[i]
        walk.append(float(served[on[i]] + oa[i]) if origin_ok else 2.0 * horizon)
    share = f32.r(covered / total) if total > 0.0 else 0.0
    g = gini(walk, w)

    exact = {
        "share_b32": f32_bits(share), "covered_weight": repr(covered), "total_weight": repr(total),
        "gini_walk": repr(g), "trips_covered": trips_covered, "trips_off_network": off,
    }
    def same(key, a, b):
        if key in ("covered_weight", "total_weight", "gini_walk"):
            return float(a) == float(b)
        return a == b
    game = {k: data[k] for k in exact}
    subject = {k: solution.get(k) for k in exact}
    game_ok = {k: same(k, exact[k], game[k]) for k in exact}
    subject_ok = {k: same(k, exact[k], subject[k]) for k in exact}
    return {
        "trips": len(w),
        "stops": len(sx),
        "exact": exact,
        "game": game,
        "subject": subject,
        "game_matches": game_ok,
        "subject_matches": subject_ok,
        "three_way_exact": all(game_ok.values()) and all(subject_ok.values()),
        "ok": all(game_ok.values()) and all(subject_ok.values()),
    }
