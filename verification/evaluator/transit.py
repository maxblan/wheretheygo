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


def dijkstra(net: Network, source: int, max_cost: Fraction,
             expand_below: int | None = None) -> list[Fraction | None]:
    """Exact Dijkstra. Nodes >= expand_below (journey doors) are sinks: reached, never
    expanded, except the source itself."""
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
        if expand_below is not None and node >= expand_below and node != source:
            continue
        for other, cost in adj[node]:
            nd = d + cost
            if nd > max_cost:
                continue
            if dist[other] is None or nd < dist[other]:
                dist[other] = nd
                heapq.heappush(heap, (nd, other))
    return dist
