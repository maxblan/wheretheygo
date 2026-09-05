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
        self.x = [bits_to_f32(b) for b in data["node_x_b32"]]
        self.z = [bits_to_f32(b) for b in data["node_z_b32"]]
        self.frm = list(data["arc_from"])
        self.to = list(data["arc_to"])
        self.metres = [bits_to_f32(b) for b in data["arc_metres_b32"]]
        self.speed = [bits_to_f32(b) for b in data["arc_speed_b32"]]
        self.ms = [arc_ms(m, s) for m, s in zip(self.metres, self.speed)]
        self.edge = list(data["arc_edge"])
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

    def nearest_arc(self, x: float, z: float, max_metres: float) -> tuple[int, float]:
        """Spec §3.4: chord projection in double from binary32 coordinates; nearest
        arc (ties: lower index), fraction t along it; (-1, 0) when off-network."""
        best, best_sq, best_t = -1, max_metres * max_metres, 0.0
        for a in range(self.arc_count):
            ax, az = self.x[self.frm[a]], self.z[self.frm[a]]
            bx, bz = self.x[self.to[a]], self.z[self.to[a]]
            dx, dz = bx - ax, bz - az
            len2 = dx * dx + dz * dz
            u = max(0.0, min(1.0, ((x - ax) * dx + (z - az) * dz) / len2)) if len2 > 0.0 else 0.0
            px, pz = ax + u * dx - x, az + u * dz - z
            sq = px * px + pz * pz
            if sq < best_sq:
                best, best_sq, best_t = a, sq, u
        return best, best_t

    def position_ms(self, arc: int, t: float) -> int:
        return int(round(t * self.ms[arc]))

    def arcs_of_edge(self, arc: int, t: float) -> list[tuple[int, float]]:
        result = [(arc, t)]
        edge = self.edge[arc]
        if edge < 0:
            return result
        for other in range(self.arc_count):
            if (other != arc and self.edge[other] == edge and self.frm[other] == self.to[arc]
                    and self.to[other] == self.frm[arc]):
                result.append((other, 1.0 - t))
                break
        return result

    def state_graph(self, from_arc: int, start_ms: int, to_arc: int, end_ms: int,
                    same_arc_direct: int | None) -> tuple[int, int, list[tuple[int, int, int]]]:
        """Directed edges over states for one arc pairing: arcs 0..A-1, S = A, T = A+1.
        S->from_arc costs start_ms (the vehicle is start_ms from that arc's head);
        g->T costs turn(g, to_arc) + end_ms for every g ending at to_arc's tail; when
        both points lie on one arc in travel order, S->T costs their position
        difference directly."""
        S, T = self.arc_count, self.arc_count + 1
        edges: list[tuple[int, int, int]] = [(S, from_arc, start_ms)]
        tail = self.frm[to_arc]
        for a in range(self.arc_count):
            node = self.to[a]
            for b in self.out[node]:
                edges.append((a, b, self.turn_cost(a, b) + self.ms[b]))
            if node == tail:
                edges.append((a, T, self.turn_cost(a, to_arc) + end_ms))
        if same_arc_direct is not None:
            edges.append((S, T, same_arc_direct))
        return S, T, edges

    def leg(self, fx: float, fz: float, tx: float, tz: float, snap: float,
            max_ms: int) -> tuple[int | None, dict | None]:
        """Fastest time over every arc pairing of the two projected points, with the
        pairing (and its state graph and labels) that achieved it."""
        fa, ft = self.nearest_arc(fx, fz, snap)
        ta, tt = self.nearest_arc(tx, tz, snap)
        if fa < 0 or ta < 0:
            return None, None
        best, detail = None, None
        for a, at in self.arcs_of_edge(fa, ft):
            for b, bt in self.arcs_of_edge(ta, tt):
                from_pos = self.position_ms(a, at)
                start_ms = self.ms[a] - from_pos
                end_ms = self.position_ms(b, bt)
                direct = end_ms - from_pos if (a == b and bt >= at) else None
                S, T, edges = self.state_graph(a, start_ms, b, end_ms, direct)
                dist = dijkstra(self.arc_count + 2, edges, S, max_ms)
                value = dist[T]
                if value is None:
                    continue
                if best is None or value < best:
                    best = value
                    detail = {"from_arc": a, "to_arc": b, "start_ms": start_ms, "end_ms": end_ms,
                              "same_arc": direct is not None and value == direct,
                              "S": S, "T": T, "edges": edges, "dist": dist}
        return best, detail


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
    over the state graph of the pairing the exact search chose."""
    data = instance["data"]
    roads = Roads(data)
    max_ms = int(data["max_ms"])
    snap = bits_to_f32(data["snap_metres_b32"])
    report = {
        "arcs": roads.arc_count,
        "arc_ms_matches_spec": list(data["arc_ms"]) == roads.ms,
        "subject_arc_ms_matches_spec": list(solution.get("arc_ms", data["arc_ms"])) == roads.ms,
        "turn_ms_matches_spec": list(data["turn_ms"]) == roads.turn_ms,
        "subject_turn_ms_matches_spec": list(solution.get("turn_ms", data["turn_ms"])) == roads.turn_ms,
        "legs": len(data["leg_ms"]),
        "legs_dropped_by_mod": int(data.get("legs_dropped", 0)),
    }
    subject_legs = {l["leg"]: l for l in solution.get("legs", [])}
    fx = [bits_to_f32(b) for b in data["leg_from_x_b32"]]
    fz = [bits_to_f32(b) for b in data["leg_from_z_b32"]]
    tx = [bits_to_f32(b) for b in data["leg_to_x_b32"]]
    tz = [bits_to_f32(b) for b in data["leg_to_z_b32"]]
    matched = 0
    mismatches: list[dict] = []
    certificates: list[dict] = []
    for k in range(len(fx)):
        exact, detail = roads.leg(fx[k], fz[k], tx[k], tz[k], snap, max_ms)
        game = int(data["leg_ms"][k])
        game = None if game < 0 else game
        sub = subject_legs.get(k)
        sub_ms = None if sub is None or sub["ms"] < 0 else int(sub["ms"])
        ok = exact == game and exact == sub_ms
        if ok and exact is not None and detail is not None:
            # The game's and the subject's recorded pairing must be the one the exact
            # search found (ties: the search's lower-index preference mirrors the mod).
            pairing_ok = (int(data["leg_from_arc"][k]) == detail["from_arc"]
                          and int(data["leg_to_arc"][k]) == detail["to_arc"]
                          and sub is not None and sub["from_arc"] == detail["from_arc"]
                          and sub["to_arc"] == detail["to_arc"])
            ok = ok and pairing_ok
        if ok:
            matched += 1
            if exact is not None and detail is not None and sub is not None:
                if detail["same_arc"]:
                    path = [detail["S"], detail["T"]]
                else:
                    path = [detail["S"]] + list(sub["arcs"]) + [detail["T"]]
                certificates.append({
                    "directed": True,
                    "leg": k,
                    "source": detail["S"],
                    "target": detail["T"],
                    "claimed_cost": f"{exact}/1",
                    "labels": [None if d is None else f"{d}/1" for d in detail["dist"]],
                    "path": path,
                    "edges": detail["edges"],
                })
        elif len(mismatches) < 5:
            mismatches.append({"leg": k, "exact": exact, "game": game, "subject": sub_ms,
                               "exact_pairing": None if detail is None else (detail["from_arc"], detail["to_arc"]),
                               "game_pairing": (int(data["leg_from_arc"][k]), int(data["leg_to_arc"][k]))})
    report["matched_three_way"] = matched
    report["mismatches"] = mismatches
    report["ok"] = (report["arc_ms_matches_spec"] and report["subject_arc_ms_matches_spec"]
                    and report["turn_ms_matches_spec"] and report["subject_turn_ms_matches_spec"]
                    and not mismatches)
    return report, certificates
