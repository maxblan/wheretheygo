"""S7 transit routing and credit: independent exact re-implementation.

Everything here is derived from docs/formal-specification.md §6/§7b, not from mod
code. Edge costs are derived with exact binary32 emulation (they are float data in
the mod); routing and credit are then computed over those exact rationals.

Tie semantics (user decision 2026-09-03): shortest-path distances are unique
rationals, but the itinerary among equal-cost paths is not — boardings and
line-usage are therefore evaluated over ALL cost-minimal itineraries, giving a
per-pair credit interval [lo, hi]. A subject value passes iff it lies within the
summed interval plus the float budget; instances where the interval is not a
point are reported as tie-affected.
"""

from __future__ import annotations

import heapq
import math
from dataclasses import dataclass, field
from fractions import Fraction

from evaluator import f32

WALK_SPEED = 1.2   # planning walking speed (spec §6.1, v2: TCQSM/FHWA value; was 1.4)
MIN_EDGE_COST = 0.01
MAX_ITINERARIES = 512

WALK, ACCESS, RIDE = 0, 1, 2


@dataclass
class Line:
    stops: list[int]
    wait: float
    speed: float
    ride_seconds: list[float] | None = None


@dataclass
class Network:
    stop_count: int
    node_count: int
    # (a, b, cost_fraction, kind, line)
    edges: list[tuple[int, int, Fraction, int, int]] = field(default_factory=list)


def dist_sq_f32(ax: float, az: float, bx: float, bz: float) -> float:
    dx = f32.sub(ax, bx)
    dz = f32.sub(az, bz)
    return f32.add(f32.mul(dx, dx), f32.mul(dz, dz))


def walk_seconds(dist_sq: float) -> float:
    """ECS WalkSeconds: (float)Math.Sqrt(double distSq) / WalkSpeed (1.2f since v2)."""
    return f32.div(f32.r(math.sqrt(dist_sq)), f32.r(WALK_SPEED))


def build_network(stop_x: list[float], stop_z: list[float], lines: list[Line],
                  walk_radius: float, board_penalty: float) -> Network:
    stop_count = len(stop_x)
    net = Network(stop_count=stop_count, node_count=stop_count)

    def add_edge(a: int, b: int, cost: float, kind: int, line: int) -> None:
        cost = max(f32.r(MIN_EDGE_COST), cost)
        net.edges.append((a, b, Fraction(cost), kind, line))

    # Walk edges: every pair within the radius (f32 squared-distance test), in
    # ascending (a, b) order — the bucketed sweep is defined to be equivalent.
    radius_sq = f32.mul(f32.r(walk_radius), f32.r(walk_radius))
    if walk_radius > 0.0:
        for a in range(stop_count):
            for b in range(a + 1, stop_count):
                d_sq = dist_sq_f32(stop_x[a], stop_z[a], stop_x[b], stop_z[b])
                if d_sq <= radius_sq:
                    # distance = (float)Math.Sqrt(f32 sums); cost = distance / WalkSpeed
                    distance = f32.r(math.sqrt(d_sq))
                    add_edge(a, b, f32.div(distance, f32.r(WALK_SPEED)), WALK, -1)

    next_node = stop_count
    for l, line in enumerate(lines):
        if line.stops is None or len(line.stops) < 2:
            continue
        first = next_node
        next_node += len(line.stops)
        for i, stop in enumerate(line.stops):
            if stop < 0 or stop >= stop_count:
                continue
            aboard = first + i
            access = f32.mul(f32.add(line.wait, f32.r(board_penalty)), f32.r(0.5))
            add_edge(stop, aboard, access, ACCESS, l)
            if i > 0:
                prev = line.stops[i - 1]
                if 0 <= prev < stop_count:
                    if (line.ride_seconds is not None
                            and i < len(line.ride_seconds)
                            and line.ride_seconds[i] > 0.0):
                        ride = line.ride_seconds[i]
                    else:
                        d_sq = dist_sq_f32(stop_x[stop], stop_z[stop],
                                           stop_x[prev], stop_z[prev])
                        distance = f32.r(math.sqrt(d_sq))
                        ride = f32.div(distance, max(f32.r(1.0), line.speed))
                    add_edge(first + i - 1, aboard, ride, RIDE, l)
    net.node_count = next_node
    return net


def dijkstra(net: Network, source: int, max_cost: Fraction) -> list[Fraction | None]:
    adj: list[list[tuple[int, Fraction]]] = [[] for _ in range(net.node_count)]
    for a, b, c, _, _ in net.edges:
        adj[a].append((b, c))
        adj[b].append((a, c))
    dist: list[Fraction | None] = [None] * net.node_count
    if source < 0 or source >= net.node_count:
        return dist
    dist[source] = Fraction(0)
    heap = [(Fraction(0), source)]
    done = [False] * net.node_count
    while heap:
        d, node = heapq.heappop(heap)
        if done[node]:
            continue
        done[node] = True
        if d > max_cost:
            continue
        for other, cost in adj[node]:
            nd = d + cost
            if nd > max_cost:
                continue
            if dist[other] is None or nd < dist[other]:
                dist[other] = nd
                heapq.heappush(heap, (nd, other))
    return dist


def itinerary_variants(net: Network, dist: list[Fraction | None], source: int,
                       dest: int, target_line: int) -> tuple[set[tuple[int, bool]], bool]:
    """All (boardings, uses_target) over cost-minimal itineraries source->dest.
    Returns (variants, truncated)."""
    if dest < 0 or dest >= net.node_count or dist[dest] is None:
        return set(), False
    # Tight incoming edges per node: d[prev] + c == d[node].
    incoming: list[list[tuple[int, int, int]]] = [[] for _ in range(net.node_count)]
    for a, b, c, kind, line in net.edges:
        for u, v in ((a, b), (b, a)):
            if dist[u] is not None and dist[v] is not None and dist[u] + c == dist[v]:
                incoming[v].append((u, kind, line))
    variants: set[tuple[int, bool]] = set()
    truncated = False
    count = 0

    def walk(node: int, access_steps: int, uses: bool) -> None:
        nonlocal truncated, count
        if truncated:
            return
        if node == source:
            count += 1
            if count > MAX_ITINERARIES:
                truncated = True
                return
            variants.add((access_steps // 2, uses))
            return
        for prev, kind, line in incoming[node]:
            walk(prev,
                 access_steps + (1 if kind == ACCESS else 0),
                 uses or (kind == ACCESS and target_line >= 0 and line == target_line))

    walk(dest, 0, False)
    return variants, truncated


def remap_zones(zone_x: list[float], zone_z: list[float],
                incumbent_stop: list[int], incumbent_dist_sq: list[float],
                new_x: list[float], new_z: list[float], base_index: int,
                reach: float) -> tuple[list[int], list[float]]:
    """Spec §6 / SuitabilityTransit.RemapZones semantics, re-derived: nearest new
    stop beats the incumbent under strict <; unmapped zones start at reach²."""
    max_sq = f32.mul(f32.r(reach), f32.r(reach))
    out_stop, out_sq = [], []
    for z in range(len(zone_x)):
        best = incumbent_stop[z]
        best_sq = incumbent_dist_sq[z] if best >= 0 else max_sq
        for i in range(len(new_x)):
            d_sq = dist_sq_f32(new_x[i], new_z[i], zone_x[z], zone_z[z])
            if d_sq < best_sq:
                best_sq = d_sq
                best = base_index + i
        out_stop.append(best)
        out_sq.append(best_sq if best >= 0 else 0.0)
    return out_stop, out_sq


@dataclass
class Pair:
    origin: int
    dest: int
    weight: Fraction
    access: Fraction
    # Baseline variants: possible values of the mod-side baseline for this pair
    # (math.inf stands for float.MaxValue = unreachable).
    baselines: set


def credit_interval(net: Network, pairs: list[Pair], target_line: int,
                    discount: Fraction, max_travel: Fraction,
                    margin: Fraction) -> tuple[Fraction, Fraction, bool]:
    """[lo, hi] of the credited sum over all tie choices; bool = ties affected it."""
    lo = Fraction(0)
    hi = Fraction(0)
    tie_affected = False
    by_origin: dict[int, list[Fraction | None]] = {}
    for pair in pairs:
        if pair.origin < 0 or pair.dest < 0 or pair.origin == pair.dest:
            continue
        if pair.origin not in by_origin:
            by_origin[pair.origin] = dijkstra(net, pair.origin, max_travel)
        dist = by_origin[pair.origin]
        if pair.dest >= len(dist) or dist[pair.dest] is None:
            continue
        travel = dist[pair.dest]
        door = travel + pair.access
        if door > max_travel:
            continue
        variants, truncated = itinerary_variants(net, dist, pair.origin, pair.dest,
                                                 target_line)
        if truncated:
            # Sound over-approximation: anything from 0 to full weight.
            lo += Fraction(0)
            hi += pair.weight
            tie_affected = True
            continue
        credits = set()
        for boardings, uses in variants:
            uses_ok = uses if target_line >= 0 else True
            for baseline in pair.baselines:
                improves = (baseline == math.inf) or (
                    door < Fraction(baseline) - margin)
                if uses_ok and boardings > 0 and improves:
                    transfers = max(0, boardings - 1)
                    credits.add(pair.weight * discount ** transfers)
                else:
                    credits.add(Fraction(0))
        if not credits:
            continue
        pair_lo, pair_hi = min(credits), max(credits)
        lo += pair_lo
        hi += pair_hi
        if pair_lo != pair_hi:
            tie_affected = True
    return lo, hi, tie_affected


def baseline_variants(net: Network, origin: int, dest: int, access: Fraction,
                      max_travel: Fraction,
                      dist_cache: dict[int, list[Fraction | None]]) -> set:
    """Possible mod-side baseline values for one pair on the base network:
    travel + access when the retained itinerary has boardings > 0, else MaxValue.
    Over all cost-minimal itineraries that is a set of at most two values."""
    if origin < 0 or dest < 0 or origin == dest:
        return {math.inf}
    if origin not in dist_cache:
        dist_cache[origin] = dijkstra(net, origin, max_travel)
    dist = dist_cache[origin]
    if dest >= len(dist) or dist[dest] is None:
        return {math.inf}
    variants, truncated = itinerary_variants(net, dist, origin, dest, -1)
    values = set()
    if truncated:
        return {math.inf, dist[dest] + access}
    for boardings, _ in variants:
        values.add(dist[dest] + access if boardings > 0 else math.inf)
    return values or {math.inf}
