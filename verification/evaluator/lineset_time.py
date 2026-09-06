"""S7 v2: the passenger-time line-set objective (docs/formal-specification.md §6 v2),
re-derived without mod code.

For a set A of candidate lines: build the transit graph of the existing network
plus A (evaluator.transit rules, binary32 edge costs) with one node per journey
end joined by walk edges to every stop within reach; for every journey,
after = min(walk straight there at 1.2 m/s, exact shortest door-to-door), before
= the same with A empty; saved = Σ w · (before − after)  [the mod forms
before − after in binary32, then multiplies and sums in double, in pair order].
Feasibility: each chosen line's riders (journeys whose retained shortest
itinerary boards it) must give utilisation ≥ floor at the fleet those riders call
for within the game's span (evaluator.fleet); a line at least
`duplicate_share` of whose riders travel no slower without it is a duplicate.
Equity: the served share of the embedded walking-network journeys once the set's
stops join the served stops (evaluator.coverage rules); the key is
(min(share, floor), saved), lexicographic.

Exact shortest-path costs are rationals; the mod accumulates binary32 along the
path, so its reported time saved is checked against the exact figure within a
Higham forward bound (evaluator.tolerances) rather than bit for bit.
"""

from __future__ import annotations

import heapq
import math
import os
from fractions import Fraction

from common.canonical import bits_to_f32, bits_to_fraction
from evaluator import f32
from evaluator import transit
from evaluator.tolerances import gamma
from evaluator.heatmap_walk import Graph as WalkGraph, snap
from evaluator import coverage as ev_coverage
from evaluator import fleet as ev_fleet

WALK, ACCESS, RIDE = transit.WALK, transit.ACCESS, transit.RIDE


def parse(instance: dict) -> dict:
    d = instance["data"]
    p = {
        "walk_radius": bits_to_f32(d["walk_radius_b32"]),
        "board_penalty": bits_to_f32(d["board_penalty_b32"]),
        "max_travel": bits_to_f32(d["max_travel_seconds_b32"]),
        "reach": bits_to_f32(d["zone_reach_b32"]),
        "ox": [bits_to_f32(b) for b in d["pair_ox_b32"]],
        "oz": [bits_to_f32(b) for b in d["pair_oz_b32"]],
        "dx": [bits_to_f32(b) for b in d["pair_dx_b32"]],
        "dz": [bits_to_f32(b) for b in d["pair_dz_b32"]],
        "w": [bits_to_f32(b) for b in d["pair_w_b32"]],
        "day_share": ([bits_to_f32(b) for b in d["pair_day_share_b32"]] if d.get("pair_day_share_b32") else None),
        "base_x": [bits_to_f32(b) for b in d["base_stop_x_b32"]],
        "base_z": [bits_to_f32(b) for b in d["base_stop_z_b32"]],
        "base_lines": [transit.Line(stops=list(l["stops"]), wait=bits_to_f32(l["expected_wait_b32"]),
                                    speed=bits_to_f32(l["speed_b32"]),
                                    ride_seconds=None if l.get("ride_seconds_b32") is None
                                    else [bits_to_f32(b) for b in l["ride_seconds_b32"]])
                       for l in d["base_lines"]],
        "candidates": [{
            "x": [bits_to_f32(b) for b in c["stop_x_b32"]], "z": [bits_to_f32(b) for b in c["stop_z_b32"]],
            "wait": bits_to_f32(c["expected_wait_b32"]), "speed": bits_to_f32(c["speed_b32"]),
            "ride": None if c.get("ride_seconds_b32") is None else [bits_to_f32(b) for b in c["ride_seconds_b32"]],
            "round_trip": bits_to_f32(c["round_trip_b32"]), "capacity": bits_to_f32(c["capacity_b32"]),
            "fleet_min": int(c.get("fleet_min", 1)), "fleet_max": (None if int(c.get("fleet_max", 0)) == 0 else int(c["fleet_max"])),
            "group": int(c.get("group", -1)),
        } for c in d["candidates"]],
        "max_lines": int(d["max_lines"]),
        "utilisation_floor": bits_to_f32(d["utilisation_floor_b32"]),
        "utilisation_ceiling": bits_to_f32(d["utilisation_ceiling_b32"]) if "utilisation_ceiling_b32" in d else 0.0,
        "day": bits_to_f32(d["movement_seconds_per_day_b32"]),
        "duplicate_share": bits_to_f32(d["duplicate_share_b32"]),
        "equity_floor": bits_to_f32(d["equity_floor_share_b32"]),
        "equity": d.get("equity"),
    }
    p["pairs"] = len(p["w"])
    return p


def assemble(p: dict, chosen: list[int]):
    xs = list(p["base_x"]); zs = list(p["base_z"])
    lines = list(p["base_lines"])
    for c in chosen:
        cand = p["candidates"][c]
        first = len(xs)
        xs.extend(cand["x"]); zs.extend(cand["z"])
        lines.append(transit.Line(stops=list(range(first, first + len(cand["x"]))), wait=cand["wait"],
                                  speed=cand["speed"], ride_seconds=cand["ride"]))
    return xs, zs, lines


def network_with_zones(p: dict, chosen: list[int]) -> tuple[transit.Network, int]:
    xs, zs, lines = assemble(p, chosen)
    net = transit.build_network(xs, zs, lines, p["walk_radius"], p["board_penalty"])
    zone_start = net.node_count
    reach_sq = f32.mul(f32.r(p["reach"]), f32.r(p["reach"]))
    for i in range(p["pairs"]):
        for k, (px, pz) in enumerate(((p["ox"][i], p["oz"][i]), (p["dx"][i], p["dz"][i]))):
            node = zone_start + 2 * i + k
            for s in range(len(xs)):
                d_sq = transit.dist_sq_f32(xs[s], zs[s], px, pz)
                if d_sq <= reach_sq:
                    cost = max(f32.r(transit.MIN_EDGE_COST), f32.div(f32.r(math.sqrt(d_sq)), f32.r(transit.WALK_SPEED)))
                    net.edges.append((node, s, Fraction(cost), WALK, -1))
    net.node_count = zone_start + 2 * p["pairs"]
    return net, zone_start


def walk_only(p: dict, i: int) -> Fraction:
    dx = f32.sub(p["dx"][i], p["ox"][i]); dz = f32.sub(p["dz"][i], p["oz"][i])
    d = f32.r(math.sqrt(f32.add(f32.mul(dx, dx), f32.mul(dz, dz))))
    return Fraction(f32.div(d, f32.r(transit.WALK_SPEED)))


# The per-pair work is independent, so the check phase runs it on a process pool
# (Linux fork; the network is rebuilt once per worker from the instance). Inside an
# enumeration worker — a daemonic process that may not spawn children — it runs
# serially, as before. Same arithmetic, same fold order, same bits.
_EVAL_STATE: dict = {}


def _init_eval(p: dict, chosen: list[int], before) -> None:
    net, zone_start = network_with_zones(p, chosen)
    _EVAL_STATE.update(p=p, chosen=chosen, before=before, net=net, zone_start=zone_start)


def _eval_pair(i: int):
    st = _EVAL_STATE
    return _pair_result(st["p"], st["chosen"], st["before"], st["net"], st["zone_start"], i)


def _pair_result(p, chosen, before, net, zone_start, i):
    origin = zone_start + 2 * i; dest = origin + 1
    dist = transit.dijkstra(net, origin, Fraction(p["max_travel"]), expand_below=zone_start)
    wo = walk_only(p, i)
    t = dist[dest]
    a = wo if t is None or t >= wo else t
    saved = Fraction(0)
    if before is not None and before[i] > a:
        saved = Fraction(p["w"][i]) * (before[i] - a)
    tie = False
    used: list[int] = []
    if t is not None and t < wo:
        # Which candidate lines the journey boards, over every shortest itinerary.
        used_sets = itinerary_lines(net, dist, origin, dest, zone_start)
        tie = len(used_sets) > 1
        used = sorted(used_sets[0])
    return a, saved, tie, used


def _workers() -> int:
    import multiprocessing
    if multiprocessing.current_process().daemon:
        return 1
    return max(1, min(16, (os.cpu_count() or 2) - 1))


def evaluate(p: dict, chosen: list[int], before: list[Fraction] | None) -> dict:
    """Exact after-times, exact time saved (Σ w·(before−after) as rationals), riders per
    candidate from the retained shortest itinerary, and tie flags."""
    line_offset = len(p["base_lines"])
    workers = _workers()
    if workers > 1 and p["pairs"] >= 2000:
        from multiprocessing import Pool
        with Pool(workers, initializer=_init_eval, initargs=(p, chosen, before)) as pool:
            results = pool.map(_eval_pair, range(p["pairs"]), chunksize=max(1, p["pairs"] // (workers * 8)))
    else:
        net, zone_start = network_with_zones(p, chosen)
        results = [_pair_result(p, chosen, before, net, zone_start, i) for i in range(p["pairs"])]
    after: list[Fraction] = []
    saved = Fraction(0)
    riders = [Fraction(0)] * len(p["candidates"])
    riders_day = [Fraction(0)] * len(p["candidates"])
    riders_night = [Fraction(0)] * len(p["candidates"])
    tie_affected = False
    for i in range(p["pairs"]):
        a, s, tie, used = results[i]
        after.append(a)
        saved += s
        tie_affected = tie_affected or tie
        share = Fraction(p["day_share"][i]) if p["day_share"] is not None else Fraction(1)
        for l in used:
            # Lines after the base ones sit in `chosen` order (mod: Riders[chosen[k]]).
            k = l - line_offset
            if 0 <= k < len(chosen):
                riders[chosen[k]] += Fraction(p["w"][i])
                riders_day[chosen[k]] += Fraction(p["w"][i]) * share
                riders_night[chosen[k]] += Fraction(p["w"][i]) * (1 - share)
    return {"after": after, "saved": saved, "riders": riders, "riders_day": riders_day,
            "riders_night": riders_night, "tie_affected": tie_affected}


def itinerary_lines(net: transit.Network, dist, source: int, dest: int, zone_start: int) -> list[frozenset]:
    """Sets of lines boarded, one per cost-minimal itinerary (capped). A door other
    than the source is never a predecessor (doors are sinks)."""
    incoming: list[list[tuple[int, int, int]]] = [[] for _ in range(net.node_count)]
    for a, b, c, kind, line in net.edges:
        for u, v in ((a, b), (b, a)):
            if u >= zone_start and u != source:
                continue
            if dist[u] is not None and dist[v] is not None and dist[u] + c == dist[v]:
                incoming[v].append((u, kind, line))
    results: list[frozenset] = []
    count = [0]

    def walk(node: int, lines: frozenset) -> None:
        if count[0] > transit.MAX_ITINERARIES:
            return
        if node == source:
            count[0] += 1
            if lines not in results:
                results.append(lines)
            return
        for prev, kind, line in incoming[node]:
            walk(prev, lines | ({line} if kind == ACCESS and line >= 0 else set()))

    walk(dest, frozenset())
    return results or [frozenset()]


def fleet_plan(p: dict, c: int, ev: dict):
    """The fleet the set's riders call for within the game's span (spec §6.3 F1 v3,
    A6.8/A6.10): the mod's own double/binary32 arithmetic re-derived in
    evaluator.fleet, on the riders rounded to binary32 as the mod hands them over."""
    cand = p["candidates"][c]
    ceiling = p["utilisation_ceiling"] if p["utilisation_ceiling"] > 0 else 1.0
    return ev_fleet.plan_fleet(f32.r(float(ev["riders"][c])), f32.r(float(ev["riders_day"][c])),
                               f32.r(float(ev["riders_night"][c])), cand["round_trip"], cand["capacity"],
                               cand["fleet_min"], cand["fleet_max"], ceiling)


def utilisation(p: dict, c: int, ev: dict) -> Fraction:
    cand = p["candidates"][c]
    if cand["round_trip"] <= 0 or cand["capacity"] <= 0 or p["day"] <= 0:
        return Fraction(0)
    return Fraction(fleet_plan(p, c, ev)[2])


_COVERAGE_CACHE: dict = {}


def coverage_share(p: dict, chosen: list[int]) -> Fraction | None:
    """Served share with the chosen candidates' stops added, via evaluator.coverage."""
    eq = p["equity"]
    if eq is None:
        return None
    key = ("base",)
    if key not in _COVERAGE_CACHE:
        g = WalkGraph(eq)
        sx = [bits_to_f32(b) for b in eq["stop_x_b32"]]; sz = [bits_to_f32(b) for b in eq["stop_z_b32"]]
        sn, sa = snap(g, sx, sz, int(eq["access_ms"]))
        base_served = ev_coverage.served_walk(g, sn, sa, int(eq["horizon_ms"]))
        ox = [bits_to_f32(b) for b in eq["trip_ox_b32"]]; oz = [bits_to_f32(b) for b in eq["trip_oz_b32"]]
        dx = [bits_to_f32(b) for b in eq["trip_dx_b32"]]; dz = [bits_to_f32(b) for b in eq["trip_dz_b32"]]
        on, oa = snap(g, ox, oz, int(eq["access_ms"])); dn, da = snap(g, dx, dz, int(eq["access_ms"]))
        w = [bits_to_f32(b) for b in eq["trip_w_b32"]]
        _COVERAGE_CACHE[key] = (g, base_served, on, oa, dn, da, w)
    g, base_served, on, oa, dn, da, w = _COVERAGE_CACHE[key]
    horizon = int(eq["horizon_ms"]); access = int(eq["access_ms"])
    served = list(base_served)
    for c in chosen:
        cand = p["candidates"][c]
        cn, ca = snap(g, cand["x"], cand["z"], access)
        extra = ev_coverage.served_walk(g, cn, ca, horizon)
        for n in range(g.n):
            if extra[n] is not None and (served[n] is None or extra[n] < served[n]):
                served[n] = extra[n]
    total = 0.0; covered = 0.0
    for i in range(len(w)):
        total += w[i]
        if ev_coverage.end_served(served, on[i], oa[i], horizon) and ev_coverage.end_served(served, dn[i], da[i], horizon):
            covered += w[i]
    return Fraction(f32.r(covered / total)) if total > 0 else Fraction(0)


def key_of(p: dict, chosen: list[int], before: list[Fraction]) -> tuple:
    ev = evaluate(p, chosen, before)
    cov = coverage_share(p, chosen)
    capped = Fraction(0) if cov is None else min(cov, Fraction(p["equity_floor"]))
    return (capped, ev["saved"]), ev


def feasible(p: dict, chosen: list[int], ev: dict, before: list[Fraction]) -> tuple[bool, str]:
    groups = [p["candidates"][c]["group"] for c in chosen if p["candidates"][c]["group"] >= 0]
    if len(groups) != len(set(groups)):
        return False, "two variants of one alignment"
    for c in chosen:
        cand = p["candidates"][c]
        if cand["round_trip"] <= 0 or cand["capacity"] <= 0:
            continue
        plan = fleet_plan(p, c, ev)
        if p["utilisation_floor"] > 0 and plan[2] < p["utilisation_floor"]:
            return False, f"line {c} below the utilisation floor"
        if p["utilisation_ceiling"] > 0 and ev_fleet.overloads(plan, p["utilisation_ceiling"]):
            return False, f"line {c} above the utilisation ceiling (day or night)"
    if p["duplicate_share"] > 0 and len(chosen) >= 2:
        for c in chosen:
            rest = [x for x in chosen if x != c]
            without = evaluate(p, rest, before)
            riding = ev["riders"][c]
            if riding <= 0:
                return False, f"line {c} carries nobody"
            slowed = sum((Fraction(p["w"][i]) for i in range(p["pairs"]) if without["after"][i] > ev["after"][i]), Fraction(0))
            if (riding - slowed) / riding >= Fraction(p["duplicate_share"]):
                return False, f"line {c} duplicates the rest of the set"
    return True, "ok"


def saved_budget(p: dict, before: list[Fraction], after: list[Fraction]) -> Fraction:
    """Sound tolerance for the mod's binary32 shortest-path accumulation: per pair,
    Higham's γ_L on before and after with L = edges a path can have (node count)."""
    L = max(2, p["pairs"] * 2 + len(p["base_x"]) + sum(len(c["x"]) for c in p["candidates"]) * 2)
    g = gamma(L)
    return sum((Fraction(p["w"][i]) * g * (before[i] + after[i]) for i in range(p["pairs"])
                if before[i] < Fraction(p["max_travel"]) * 2), Fraction(0))


def check(instance: dict, solution: dict) -> dict:
    p = parse(instance)
    before = evaluate(p, [], None)["after"]
    chosen = list(solution["chosen"])
    key, ev = key_of(p, chosen, before)
    ok_feasible, why = feasible(p, chosen, ev, before)
    mod_saved = Fraction(float(solution["time_saved"]))
    budget = saved_budget(p, before, ev["after"])
    game_chosen = None if instance["data"].get("chosen") is None else list(instance["data"]["chosen"])
    game_optimal = bool(instance["data"].get("optimal", True))
    return {
        "candidates": len(p["candidates"]),
        "pairs": p["pairs"],
        "chosen": chosen,
        "game_chosen": game_chosen,
        # A budget-stopped game run is machine-dependent; its set is then judged on its
        # own feasibility and value rather than required to equal the subject's.
        "subject_matches_game": game_chosen is None or chosen == game_chosen or not game_optimal,
        "game_optimal_claimed": game_optimal,
        "game_set_feasible": None if game_chosen is None else feasible(p, game_chosen, evaluate(p, game_chosen, before), before)[0],
        "exact_time_saved": str(ev["saved"]),
        "mod_time_saved": solution["time_saved"],
        "time_saved_within_budget": abs(mod_saved - ev["saved"]) <= budget,
        "budget": str(budget),
        "coverage": None if key[0] is None else str(key[0]),
        "feasible": ok_feasible,
        "feasible_reason": why,
        "tie_affected": ev["tie_affected"],
        "riders": [str(r) for r in ev["riders"]],
        "optimal_claimed": bool(solution["optimal"]),
        "ok": ok_feasible and (game_chosen is None or chosen == game_chosen or not game_optimal) and abs(mod_saved - ev["saved"]) <= budget,
    }
