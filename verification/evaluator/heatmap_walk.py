"""S1 v2: the walking-time access terms, re-derived from docs/formal-specification.md
§7 (v2) with no mod code. Integer shortest-path times over the pedestrian graph,
straight-line snapping of points to nodes, the linear time kernel, and the
weighted sums in the order the specification fixes (sources by index, stop types
ascending). Every float operation is binary32-emulated so the result must equal
the game's — and the subject's — bit for bit.
"""

from __future__ import annotations

import heapq
import math

from common.canonical import bits_to_f32, f32_bits
from evaluator import f32

WALK_SPEED = f32.r(1.2)   # the mod's binary32 constant, widened (spec §7.2 v2)


def walk_ms(metres: float) -> int:
    """round-half-even(metres / WalkSpeed · 1000) in double arithmetic."""
    return int(round(metres / WALK_SPEED * 1000.0))


def kernel(t_ms: int, horizon_ms: int) -> float:
    """Double-precision 1 - t/T; every store rounds to binary32 once (spec §7 v2)."""
    return 1.0 - float(t_ms) / float(horizon_ms)


def add(acc: float, weight: float, k: float) -> float:
    return f32.r(acc + weight * k)


class Graph:
    def __init__(self, data: dict):
        self.x = [bits_to_f32(b) for b in data["node_x_b32"]]
        self.z = [bits_to_f32(b) for b in data["node_z_b32"]]
        self.n = len(self.x)
        # Nodes a stop could stand at (spec §7 v2 snap rule, A1.15). Instances before
        # 2026-09-05 evening carry no flags: every node was a site then.
        self.siteable = [bool(b) for b in data.get("node_siteable", [True] * self.n)]
        self.edge_a = list(data["edge_a"])
        self.edge_b = list(data["edge_b"])
        self.metres = [bits_to_f32(b) for b in data["edge_metres_b32"]]
        self.edge_ms = [max(1, walk_ms(m)) for m in self.metres]
        self.adj: list[list[tuple[int, int]]] = [[] for _ in range(self.n)]
        for a, b, ms in zip(self.edge_a, self.edge_b, self.edge_ms):
            self.adj[a].append((b, ms))
            self.adj[b].append((a, ms))

    def nearest(self, px: float, pz: float, max_metres: float,
                sites_only: bool = False) -> tuple[int, int]:
        """(node, walk_ms) or (-1, -1): smallest double squared distance, ties to the
        lower index, then the walk must fit the access horizon in whole ms. Tiles
        snap with sites_only: tunnel and bridge nodes are walked through, not stood on."""
        best, best_sq = -1, max_metres * max_metres
        for i in range(self.n):
            if sites_only and not self.siteable[i]:
                continue
            dx = self.x[i] - px
            dz = self.z[i] - pz
            sq = dx * dx + dz * dz
            if sq < best_sq or (sq == best_sq and best >= 0 and i < best):
                best, best_sq = i, sq
        if best < 0:
            return -1, -1
        return best, walk_ms(math.sqrt(best_sq))

    def times_within(self, source: int, start_ms: int, max_ms: int) -> dict[int, int]:
        dist = {source: start_ms}
        done: set[int] = set()
        heap = [(start_ms, source)]
        while heap:
            d, node = heapq.heappop(heap)
            if node in done or d != dist[node]:
                continue
            done.add(node)
            for other, ms in self.adj[node]:
                nd = d + ms
                if nd > max_ms or (other in dist and nd >= dist[other]):
                    continue
                dist[other] = nd
                heapq.heappush(heap, (nd, other))
        return {node: d for node, d in dist.items() if node in done}


def snap(graph: Graph, xs, zs, access_ms: int):
    access_metres = access_ms / 1000.0 * WALK_SPEED
    nodes, walks = [], []
    for x, z in zip(xs, zs):
        node, walk = graph.nearest(x, z, access_metres)
        if node >= 0 and walk > access_ms:
            node, walk = -1, -1
        nodes.append(node)
        walks.append(walk)
    return nodes, walks


def accumulate(graph: Graph, sources: dict, access_ms: int, classes: list[int]):
    xs = [bits_to_f32(b) for b in sources["x_b32"]]
    zs = [bits_to_f32(b) for b in sources["z_b32"]]
    ws = [bits_to_f32(b) for b in sources["w_b32"]]
    nodes, walks = snap(graph, xs, zs, access_ms)
    acc = [[0.0] * graph.n for _ in classes]
    off = 0
    for i, node in enumerate(nodes):
        if node < 0:
            off += 1
            continue
        for n, t in graph.times_within(node, walks[i], classes[-1]).items():
            for c, horizon in enumerate(classes):
                if t <= horizon:
                    acc[c][n] = add(acc[c][n], ws[i], kernel(t, horizon))
    return acc, off


def accumulate_stops(graph: Graph, data: dict, classes: list[int]):
    xs = [bits_to_f32(b) for b in data["stop_x_b32"]]
    zs = [bits_to_f32(b) for b in data["stop_z_b32"]]
    types = list(data["stop_type"])
    type_count = max(1, data["type_count"])
    transfer = data["transfer_ms"]
    nodes, walks = snap(graph, xs, zs, data["access_ms"])
    within = [[[0.0] * graph.n for _ in range(type_count)] for _ in classes]
    cross = [[[0.0] * graph.n for _ in range(type_count)] for _ in classes]
    inter = [[0.0] * graph.n for _ in range(type_count)]
    off = 0
    for i, node in enumerate(nodes):
        ty = types[i]
        if node < 0 or ty < 0 or ty >= type_count:
            off += 1 if node < 0 else 0
            continue
        for n, t in graph.times_within(node, walks[i], classes[-1]).items():
            transferable = kernel(t, transfer) if t <= transfer else 0.0
            if t <= transfer:
                inter[ty][n] = add(inter[ty][n], 1.0, transferable)
            for c, horizon in enumerate(classes):
                if t <= horizon:
                    k = kernel(t, horizon)
                    within[c][ty][n] = add(within[c][ty][n], 1.0, k)
                    cross[c][ty][n] = add(cross[c][ty][n], 1.0, k * (1.0 - transferable))
    return within, inter, cross, off


def node_terms(node: int, cls: int, self_type: int, weights: list[float],
               demand, jobs, future, within, inter, cross) -> dict:
    cell = {
        "demand": demand[cls][node], "jobs": jobs[cls][node], "future": future[cls][node],
        "coverage": 0.0, "interchange": 0.0, "cross": 0.0, "access": 1.0,
    }
    for ty in range(len(within[cls])):
        if ty == self_type:
            cell["coverage"] = add(cell["coverage"], 1.0, within[cls][ty][node])
            continue
        w = weights[ty] if ty < len(weights) else 0.0
        if w <= 0.0:
            continue
        cell["interchange"] = add(cell["interchange"], w, inter[ty][node])
        cell["cross"] = add(cell["cross"], w, cross[cls][ty][node])
    return cell


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    graph = Graph(data)
    classes = list(data["catchment_ms"])
    access_ms = data["access_ms"]
    cls = data["class"]
    self_type = data["self_type"]
    weights = [bits_to_f32(b) for b in data["type_weight_b32"]]

    demand, off_h = accumulate(graph, data["homes"], access_ms, classes)
    jobs, off_j = accumulate(graph, data["jobs"], access_ms, classes)
    future, off_f = accumulate(graph, data["future"], access_ms, classes)
    within, inter, cross, off_s = accumulate_stops(graph, data, classes)

    width = data["grid_x"]
    tile = bits_to_f32(data["tile_size_b32"])
    wx = bits_to_f32(data["world_min_x_b32"])
    wz = bits_to_f32(data["world_min_z_b32"])
    buildable = list(data["buildable"])
    access_metres = access_ms / 1000.0 * WALK_SPEED

    fields = ["demand", "jobs", "coverage", "access", "future", "interchange", "cross"]
    game = {k: list(data[f"sample_{k}_b32"]) for k in fields}
    game_node = list(data["sample_tile_node"])
    game_walk = list(data["sample_tile_walk_ms"])
    subject = {cell["index"]: cell for cell in solution.get("cells", [])}

    matched = 0
    mismatches: list[dict] = []
    for k, index in enumerate(data["sample_indices"]):
        x, y = index % width, index // width
        expected = {f: 0.0 for f in fields}
        node, walk = -1, -1
        if buildable[index] != 0:
            cx = f32.add(wx, f32.mul(f32.r(x + 0.5), tile))
            cz = f32.add(wz, f32.mul(f32.r(y + 0.5), tile))
            node, walk = graph.nearest(cx, cz, access_metres, sites_only=True)
            if node >= 0 and walk > access_ms:
                node, walk = -1, -1
            if node >= 0:
                expected = node_terms(node, cls, self_type, weights, demand, jobs, future, within, inter, cross)
                expected["access"] = f32.r(kernel(walk, access_ms))
        bad = {}
        for f in fields:
            exp_bits = f32_bits(expected[f])
            if exp_bits != game[f][k]:
                bad[f"game.{f}"] = [str(expected[f]), str(bits_to_f32(game[f][k]))]
            sub = subject.get(index)
            if sub is None or sub[f"{f}_b32"] != exp_bits:
                bad[f"subject.{f}"] = [str(expected[f]), None if sub is None else str(bits_to_f32(sub[f"{f}_b32"]))]
        if game_node[k] != node or game_walk[k] != walk:
            bad["game.snap"] = [[node, walk], [game_node[k], game_walk[k]]]
        sub = subject.get(index)
        if sub is not None and (sub["tile_node"] != node or sub["tile_walk_ms"] != walk):
            bad["subject.snap"] = [[node, walk], [sub["tile_node"], sub["tile_walk_ms"]]]
        if bad:
            if len(mismatches) < 5:
                mismatches.append({"index": index, "terms": bad})
        else:
            matched += 1

    edge_ms_ok = list(data.get("edge_ms", graph.edge_ms)) == graph.edge_ms
    subject_edge_ok = solution.get("edge_ms") is None or list(solution["edge_ms"]) == graph.edge_ms
    ok = not mismatches and edge_ms_ok and subject_edge_ok and len(data["sample_indices"]) > 0
    return {
        "ok": ok,
        "nodes": graph.n,
        "edges": len(graph.edge_ms),
        "sampled": len(data["sample_indices"]),
        "matched_three_way": matched,
        "mismatches": mismatches,
        "edge_ms_matches_spec": edge_ms_ok,
        "subject_edge_ms_matches_spec": subject_edge_ok,
        "sources_off_network": off_h + off_j + off_f + off_s,
        "subject_sources_off_network": solution.get("sources_off_network"),
        "three_way_exact": ok,
    }
