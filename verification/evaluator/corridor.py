"""S4 corridor growth: independent bit-exact replay of the specified transition
rule (docs/formal-specification.md §3.2), C4.3.

This module re-implements GrowCorridor / PeelFlow / DecayNovelty / the
BuildForNetwork round loop from the specification, in exactly the mod's mixed
float32/float64 semantics (see the per-operation inventory below), and compares
the subject's rounds bit-for-bit — corridors, blocks, and the full flow and
novelty arrays after every round. On top of the replay it checks structural
invariants that would catch a bug shared by both implementations.

Float semantics mirrored from the C# (file: GraphMath.cs):
  - flow/length/score accumulation: float32 ops;
  - Continuity: float32 subs/muls/adds, then DOUBLE sqrt and double divisions,
    result rounded to float32 once ((cosine+1.0)*0.5 in double, cast);
  - Spread: (double)dx*dx — exact double products of float32 values, double
    sqrt/sub/div/clamp, one cast to float32;
  - MeanPositiveFlow: DOUBLE accumulation in edge order, cast to float32;
  - DecayNovelty scale update and PeelFlow multipliers: float32.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

from common.canonical import bits_to_f32
from evaluator import f32

TURN_PENALTY = f32.r(0.6)
SPREAD_PENALTY = f32.r(0.7)
LOW_DEMAND_BRIDGE_PENALTY = f32.r(0.05)


def build_adjacency(node_count: int, edge_a: list[int], edge_b: list[int]
                    ) -> list[list[tuple[int, int]]]:
    """CompactGraph adjacency: per node, (edge, other) in ascending edge order —
    the count/prefix/fill construction preserves exactly that order."""
    adj: list[list[tuple[int, int]]] = [[] for _ in range(node_count)]
    for e in range(len(edge_a)):
        a, b = edge_a[e], edge_b[e]
        if a < 0 or a >= node_count or b < 0 or b >= node_count or a == b:
            continue
        adj[a].append((e, b))
        adj[b].append((e, a))
    return adj


def mean_positive_flow(flow: list[float]) -> float:
    total = 0.0  # double accumulation, mirrored
    count = 0
    for value in flow:
        if value > 0.0:
            total += value
            count += 1
    return 0.0 if count == 0 else f32.r(total / count)


def novelty_weight(objective: str, mean_flow: float) -> float:
    if objective == "Ridership":
        return 0.0
    if objective == "Coverage":
        return f32.mul(mean_flow, f32.r(4.0))
    return f32.mul(mean_flow, f32.r(1.0))


def seed_novelty_bias(objective: str) -> float:
    if objective == "Ridership":
        return 0.0
    if objective == "Coverage":
        return 1.0
    return 0.5


@dataclass
class Blocks:
    used: int = 0
    flow: int = 0
    visited: int = 0
    length: int = 0
    demand: int = 0
    hit_max_length: bool = False

    def as_dict(self) -> dict:
        return {"used": self.used, "flow": self.flow, "visited": self.visited,
                "length": self.length, "demand": self.demand,
                "hit_max_length": self.hit_max_length}


@dataclass
class GrownCorridor:
    edges: list[int] = field(default_factory=list)
    nodes: list[int] = field(default_factory=list)
    length: float = 0.0
    captured_flow: float = 0.0
    blocks: Blocks = field(default_factory=Blocks)


class Growth:
    def __init__(self, data: dict):
        self.edge_a = list(data["edge_a"])
        self.edge_b = list(data["edge_b"])
        self.cost = [bits_to_f32(b) for b in data["edge_cost_b32"]]
        self.node_x = [bits_to_f32(b) for b in data["node_x_b32"]]
        self.node_z = [bits_to_f32(b) for b in data["node_z_b32"]]
        self.demand = (None if data["node_demand_b32"] is None
                       else [bits_to_f32(b) for b in data["node_demand_b32"]])
        self.adj = build_adjacency(len(self.node_x), self.edge_a, self.edge_b)
        self.flow = [bits_to_f32(b) for b in data["edge_flow_b32"]]
        self.used = [False] * len(self.edge_a)
        if data.get("edge_cannot_host"):
            for e, cannot in enumerate(data["edge_cannot_host"]):
                if e < len(self.used) and cannot:
                    self.used[e] = True
        self.novelty = [1.0] * len(self.node_x)

    def other_end(self, edge: int, node: int) -> int:
        return self.edge_b[edge] if self.edge_a[edge] == node else self.edge_a[edge]

    def continuity(self, frm: int, at: int, to: int) -> float:
        x, z = self.node_x, self.node_z
        n = len(x)
        if frm < 0 or at < 0 or to < 0 or frm >= n or at >= n or to >= n:
            return 1.0
        in_x = f32.sub(x[at], x[frm])
        in_z = f32.sub(z[at], z[frm])
        out_x = f32.sub(x[to], x[at])
        out_z = f32.sub(z[to], z[at])
        in_len = math.sqrt(f32.add(f32.mul(in_x, in_x), f32.mul(in_z, in_z)))
        out_len = math.sqrt(f32.add(f32.mul(out_x, out_x), f32.mul(out_z, out_z)))
        if in_len <= 0.0 or out_len <= 0.0:
            return 1.0
        numerator = f32.add(f32.mul(in_x, out_x), f32.mul(in_z, out_z))
        cosine = (numerator / in_len) / out_len          # double divisions
        straightness = f32.r((cosine + 1.0) * 0.5)
        return f32.sub(1.0, f32.mul(TURN_PENALTY, f32.sub(1.0, straightness)))

    @staticmethod
    def _separation(dx: float, dz: float) -> float:
        # Math.Sqrt(((double)dx * dx) + ((double)dz * dz)) — exact double
        # products of float32 values, double sum and sqrt.
        return math.sqrt(dx * dx + dz * dz)

    def spread(self, this_end: int, other_end: int, nxt: int, edge_cost: float) -> float:
        x, z = self.node_x, self.node_z
        n = len(x)
        if edge_cost <= 0.0 or this_end < 0 or other_end < 0 or nxt < 0 \
                or this_end >= n or other_end >= n or nxt >= n:
            return 1.0
        before = self._separation(f32.sub(x[this_end], x[other_end]),
                                  f32.sub(z[this_end], z[other_end]))
        after = self._separation(f32.sub(x[nxt], x[other_end]),
                                 f32.sub(z[nxt], z[other_end]))
        gain = (after - before) / edge_cost
        outward = f32.r((max(-1.0, min(1.0, gain)) + 1.0) * 0.5)
        return f32.sub(1.0, f32.mul(SPREAD_PENALTY, f32.sub(1.0, outward)))

    def select_seed(self, flow_floor: float, seed_bias: float) -> int:
        seed = -1
        seed_score = 0.0
        for e in range(len(self.edge_a)):
            if self.used[e] or self.flow[e] < flow_floor:
                continue
            score = self.flow[e]
            if seed_bias > 0.0:
                ends = min(self.novelty[self.edge_a[e]], self.novelty[self.edge_b[e]])
                score = f32.mul(score, f32.add(f32.sub(1.0, seed_bias),
                                               f32.mul(seed_bias, ends)))
            if score > seed_score:
                seed_score = score
                seed = e
        return seed

    def _find_extension(self, from_node: int, came_from: int, other_end: int,
                        length: float, max_length: float, flow_floor: float,
                        novelty_w: float, demand_floor: float,
                        visited: set[int], blocks: Blocks,
                        low_run: int, max_bridge: int, at_head: bool,
                        best: list) -> None:
        for edge, nxt in self.adj[from_node]:
            if self.used[edge]:
                blocks.used += 1
                continue
            if self.flow[edge] < flow_floor:
                blocks.flow += 1
                continue
            if nxt in visited:
                blocks.visited += 1
                continue
            if f32.add(length, self.cost[edge]) > max_length:
                blocks.length += 1
                continue
            low_demand = (self.demand is not None and nxt < len(self.demand)
                          and self.demand[nxt] < demand_floor)
            if low_demand and low_run >= max_bridge:
                blocks.demand += 1
                continue
            score = f32.add(self.flow[edge],
                            f32.mul(novelty_w, self.novelty[nxt]
                                    if 0 <= nxt < len(self.novelty) else 1.0))
            score = f32.mul(score, self.continuity(came_from, from_node, nxt))
            score = f32.mul(score, self.spread(from_node, other_end, nxt,
                                               self.cost[edge]))
            if low_demand:
                score = f32.mul(score, LOW_DEMAND_BRIDGE_PENALTY)
            if score <= best[2]:
                continue
            best[0] = edge
            best[1] = nxt
            best[2] = score
            best[3] = at_head

    def grow(self, novelty_w: float, flow_floor: float, max_length: float,
             demand_floor: float, seed_bias: float, max_bridge: int
             ) -> GrownCorridor | None:
        seed = self.select_seed(flow_floor, seed_bias)
        if seed < 0:
            return None
        visited = {self.edge_a[seed], self.edge_b[seed]}
        front: list[int] = []
        back: list[int] = []
        front_bridge: list[int] = []
        back_bridge: list[int] = []
        head = self.edge_a[seed]
        tail = self.edge_b[seed]
        front_terminus = head
        head_from = tail
        tail_from = head
        length = self.cost[seed]
        weighted = f32.mul(self.flow[seed], self.cost[seed])
        blocks = Blocks()

        while length < max_length:
            best = [-1, -1, 0.0, True]
            blocks = Blocks()
            self._find_extension(head, head_from, tail, length, max_length,
                                 flow_floor, novelty_w, demand_floor, visited,
                                 blocks, len(front_bridge), max_bridge, True, best)
            self._find_extension(tail, tail_from, head, length, max_length,
                                 flow_floor, novelty_w, demand_floor, visited,
                                 blocks, len(back_bridge), max_bridge, False, best)
            edge, nxt, _score, at_head = best
            if edge < 0:
                break
            weighted = f32.add(weighted, f32.mul(self.flow[edge], self.cost[edge]))
            length = f32.add(length, self.cost[edge])
            visited.add(nxt)
            crossing = (self.demand is not None and nxt < len(self.demand)
                        and self.demand[nxt] < demand_floor)
            if at_head:
                if crossing:
                    front_bridge.append(edge)
                else:
                    front.extend(front_bridge)
                    front_bridge.clear()
                    front.append(edge)
                    front_terminus = nxt
                head_from = head
                head = nxt
            else:
                if crossing:
                    back_bridge.append(edge)
                else:
                    back.extend(back_bridge)
                    back_bridge.clear()
                    back.append(edge)
                tail_from = tail
                tail = nxt

        hit_max = length >= max_length

        # Discard unredeemed bridges (float semantics of DiscardBridge: per-edge
        # f32 subtraction from weighted, one f32 subtraction of the removed sum
        # from length).
        for bridge in (front_bridge, back_bridge):
            removed = 0.0
            for edge in bridge:
                removed = f32.add(removed, self.cost[edge])
                weighted = f32.sub(weighted, f32.mul(self.flow[edge], self.cost[edge]))
            length = f32.sub(length, removed)
            bridge.clear()

        result = GrownCorridor()
        result.blocks = blocks
        result.blocks.hit_max_length = hit_max
        for e in reversed(front):
            result.edges.append(e)
        result.edges.append(seed)
        result.edges.extend(back)

        node = front_terminus
        result.nodes.append(node)
        for e in result.edges:
            node = self.other_end(e, node)
            result.nodes.append(node)

        result.length = length
        result.captured_flow = f32.div(weighted, length) if length > 0.0 else 0.0
        return result

    def peel(self, corridor: GrownCorridor, capture: float) -> None:
        capture = min(max(capture, 0.0), 1.0)
        for edge in corridor.edges:
            self.flow[edge] = f32.mul(self.flow[edge], f32.sub(1.0, capture))
            self.used[edge] = True
        processed: set[int] = set()
        side = f32.mul(capture, f32.r(0.5))
        for node in corridor.nodes:
            if node in processed:
                continue
            processed.add(node)
            for edge, other in self.adj[node]:
                if self.used[edge] or other in processed:
                    continue
                self.flow[edge] = f32.mul(self.flow[edge], f32.sub(1.0, side))

    def decay_novelty(self, corridor: GrownCorridor, hops: int, factor: float) -> None:
        frontier = list(corridor.nodes)
        seen = set(corridor.nodes)
        scale = factor
        for hop in range(hops + 1):
            for node in frontier:
                if 0 <= node < len(self.novelty):
                    self.novelty[node] = f32.mul(self.novelty[node], scale)
            if hop == hops:
                break
            nxt: list[int] = []
            for node in frontier:
                for _edge, other in self.adj[node]:
                    if other not in seen:
                        seen.add(other)
                        nxt.append(other)
            frontier = nxt
            scale = f32.sub(1.0, f32.mul(f32.sub(1.0, scale), f32.r(0.5)))


def structural_invariants(g: Growth, corridor: GrownCorridor,
                          max_length: float) -> list[str]:
    problems = []
    if len(set(corridor.nodes)) != len(corridor.nodes):
        problems.append("node sequence revisits a node")
    if len(corridor.nodes) != len(corridor.edges) + 1:
        problems.append("node/edge count mismatch")
    for i, edge in enumerate(corridor.edges):
        a, b = g.edge_a[edge], g.edge_b[edge]
        if {corridor.nodes[i], corridor.nodes[i + 1]} != {a, b}:
            problems.append(f"edge {edge} does not join nodes at position {i}")
    if corridor.length > max_length:
        problems.append("length exceeds the maximum")
    return problems


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    g = Growth(data)
    mean = mean_positive_flow(g.flow)
    objective = data["objective"]
    nov_w = novelty_weight(objective, mean)
    bias = seed_novelty_bias(objective)
    floor = f32.mul(mean, bits_to_f32(data["min_flow_fraction_b32"]))
    max_length = bits_to_f32(data["max_route_length_b32"])
    demand_floor = bits_to_f32(data["demand_floor_b32"])
    capture = bits_to_f32(data["capture_b32"])
    hops = data["novelty_hops"]
    factor = bits_to_f32(data["novelty_factor_b32"])
    max_bridge = data["max_low_demand_bridge"]

    from common.canonical import f32_bits
    report = {
        "params_match": (
            f32_bits(mean) == solution["mean_flow_b32"]
            and f32_bits(floor) == solution["flow_floor_b32"]
            and f32_bits(nov_w) == solution["novelty_weight_b32"]
            and f32_bits(bias) == solution["seed_bias_b32"]),
        "rounds": [],
    }
    ok = report["params_match"]
    demand = g.demand

    for r, subject_round in enumerate(solution["rounds"]):
        grown = g.grow(nov_w, floor, max_length, demand_floor, bias, max_bridge)
        if grown is None or not subject_round.get("ok", False):
            match = (grown is None) == (not subject_round.get("ok", False))
            report["rounds"].append({"round": r, "both_stopped": match})
            ok = ok and match
            break

        problems = structural_invariants(g, grown, max_length)
        exact = (
            grown.edges == list(subject_round["edges"])
            and grown.nodes == list(subject_round["nodes"])
            and f32_bits(grown.length) == subject_round["length_b32"]
            and f32_bits(grown.captured_flow) == subject_round["captured_flow_b32"]
            and grown.blocks.as_dict() == dict(subject_round["blocks"]))

        if len(grown.edges) < 2:
            g.peel(grown, 1.0)
        else:
            g.peel(grown, capture)
            g.decay_novelty(grown, hops, factor)

        arrays_exact = (
            [f32_bits(v) for v in g.flow] == list(subject_round["flow_after_b32"])
            and [f32_bits(v) for v in g.novelty]
            == list(subject_round["novelty_after_b32"]))

        round_report = {
            "round": r,
            "replay_exact": exact,
            "arrays_exact": arrays_exact,
            "invariants": problems,
            "nodes": grown.nodes,
            "hit_max_length": grown.blocks.hit_max_length,
        }
        if demand is not None and grown.nodes:
            floor_d = demand_floor
            round_report["termini_have_demand"] = (
                demand[grown.nodes[0]] >= floor_d
                and demand[grown.nodes[-1]] >= floor_d)
        report["rounds"].append(round_report)
        ok = ok and exact and arrays_exact and not problems

    report["ok"] = ok
    return report
