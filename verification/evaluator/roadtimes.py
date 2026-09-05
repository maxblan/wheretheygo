"""S4 v2: driving times on the directed road graph (docs/formal-specification.md
§3.4), re-derived without mod code.

The graph: one arc per admitted direction of a street with an integer time
`max(1, round_half_even(metres / speed · 1000))`; a turn from arc a into arc b
costs the class read off the double dot product of a's arriving and b's departing
unit headings against the fixed cosines (15°, 45°, 120°, 165°), each class priced
at `round_half_even(rate · angle · 1000)` for angles 0, π/6, π/2, 7π/9, π.

Because the cost of leaving a node depends on how it was entered, shortest times
live on a STATE graph whose nodes are arcs (plus one source and one target node);
this module builds that state graph explicitly — it is what the directed
certificate and the Lean checker see — and runs an exact integer Dijkstra on it.
"""

from __future__ import annotations

import heapq
import math

from common.canonical import bits_to_f32

COS_GENTLE = 0.9659258262890683
COS_TURN = 0.7071067811865476
COS_SHARP = -0.5
COS_UTURN = -0.9659258262890683
ANGLES = [0.0, math.pi / 6.0, math.pi / 2.0, math.pi * 7.0 / 9.0, math.pi]


def arc_ms(metres: float, speed: float) -> int:
    return max(1, int(round(metres / max(0.1, speed) * 1000.0)))


def turn_table(rate: float) -> list[int]:
    return [int(round(rate * a * 1000.0)) for a in ANGLES]


def turn_class(dot: float) -> int:
    if dot >= COS_GENTLE:
        return 0
    if dot >= COS_TURN:
        return 1
    if dot >= COS_SHARP:
        return 2
    return 3 if dot >= COS_UTURN else 4


class Roads:
    def __init__(self, data: dict):
        self.n = len(data["node_x_b32"])
        self.frm = list(data["arc_from"])
        self.to = list(data["arc_to"])
        self.metres = [bits_to_f32(b) for b in data["arc_metres_b32"]]
        self.speed = [bits_to_f32(b) for b in data["arc_speed_b32"]]
        self.ms = [arc_ms(m, s) for m, s in zip(self.metres, self.speed)]
        self.odx = [bits_to_f32(b) for b in data["out_dx_b32"]]
        self.odz = [bits_to_f32(b) for b in data["out_dz_b32"]]
        self.idx = [bits_to_f32(b) for b in data["in_dx_b32"]]
        self.idz = [bits_to_f32(b) for b in data["in_dz_b32"]]
        self.turn_ms = turn_table(bits_to_f32(data["turn_seconds_per_radian_b32"]))
        self.out: list[list[int]] = [[] for _ in range(self.n)]
        for a, f in enumerate(self.frm):
            self.out[f].append(a)
        self.arc_count = len(self.frm)

    def turn_cost(self, into: int, out_of: int) -> int:
        dot = self.idx[into] * self.odx[out_of] + self.idz[into] * self.odz[out_of]
        return self.turn_ms[turn_class(dot)]

    def state_graph(self, source: int, target: int) -> tuple[int, int, list[tuple[int, int, int]]]:
        """Directed edges over states: arcs 0..A-1, S = A, T = A+1."""
        S, T = self.arc_count, self.arc_count + 1
        edges: list[tuple[int, int, int]] = []
        for a in self.out[source]:
            edges.append((S, a, self.ms[a]))
        for a in range(self.arc_count):
            node = self.to[a]
            for b in self.out[node]:
                edges.append((a, b, self.turn_cost(a, b) + self.ms[b]))
            if node == target:
                edges.append((a, T, 0))
        return S, T, edges


def dijkstra(node_count: int, edges: list[tuple[int, int, int]], source: int,
             max_cost: int) -> list[int | None]:
    adj: list[list[tuple[int, int]]] = [[] for _ in range(node_count)]
    for a, b, c in edges:
        adj[a].append((b, c))
    dist: list[int | None] = [None] * node_count
    dist[source] = 0
    heap = [(0, source)]
    done = [False] * node_count
    while heap:
        d, u = heapq.heappop(heap)
        if done[u]:
            continue
        done[u] = True
        for v, c in adj[u]:
            nd = d + c
            if nd > max_cost:
                continue
            if dist[v] is None or nd < dist[v]:
                dist[v] = nd
                heapq.heappush(heap, (nd, v))
    return dist


def check(instance: dict, solution: dict) -> tuple[dict, list[dict]]:
    """Returns (report, certificates) — one directed certificate per reachable leg
    over its state graph."""
    data = instance["data"]
    roads = Roads(data)
    max_ms = int(data["max_ms"])
    report = {
        "arcs": roads.arc_count,
        "arc_ms_matches_spec": list(data["arc_ms"]) == roads.ms,
        "subject_arc_ms_matches_spec": list(solution.get("arc_ms", data["arc_ms"])) == roads.ms,
        "turn_ms_matches_spec": list(data["turn_ms"]) == roads.turn_ms,
        "subject_turn_ms_matches_spec": list(solution.get("turn_ms", data["turn_ms"])) == roads.turn_ms,
        "legs": len(data["leg_from"]),
        "legs_dropped_by_mod": int(data.get("legs_dropped", 0)),
    }
    subject_legs = {(l["from"], l["to"]): l for l in solution.get("legs", [])}
    matched = 0
    mismatches: list[dict] = []
    certificates: list[dict] = []
    for k, (f, t) in enumerate(zip(data["leg_from"], data["leg_to"])):
        S, T, edges = roads.state_graph(f, t)
        dist = dijkstra(roads.arc_count + 2, edges, S, max_ms)
        exact = dist[T]
        game = int(data["leg_ms"][k])
        game = None if game < 0 else game
        sub = subject_legs.get((f, t))
        sub_ms = None if sub is None or sub["ms"] < 0 else int(sub["ms"])
        ok = exact == game and exact == sub_ms
        if ok:
            matched += 1
            if exact is not None and sub is not None:
                # State path: S, then the subject's arcs, then T.
                path = [S] + list(sub["arcs"]) + [T]
                certificates.append({
                    "directed": True,
                    "leg": k,
                    "source": S,
                    "target": T,
                    "claimed_cost": f"{exact}/1",
                    "labels": [None if d is None else f"{d}/1" for d in dist],
                    "path": path,
                    "edges": edges,
                })
        elif len(mismatches) < 5:
            mismatches.append({"leg": k, "from": f, "to": t, "exact": exact, "game": game,
                               "subject": sub_ms})
    report["matched_three_way"] = matched
    report["mismatches"] = mismatches
    report["ok"] = (report["arc_ms_matches_spec"] and report["subject_arc_ms_matches_spec"]
                    and report["turn_ms_matches_spec"] and report["subject_turn_ms_matches_spec"]
                    and not mismatches)
    return report, certificates
