"""S7 lineset: exact replay-check of the subject's greedy rounds, and the exact
set objective used by the enumeration (docs/formal-specification.md §6.3, user
decisions of 2026-09-03: credited-sum set objective, interval tie semantics)."""

from __future__ import annotations

import math
from fractions import Fraction

from common.canonical import bits_to_f32, bits_to_fraction
from evaluator import transit
from evaluator.tolerances import credit_budget


def parse(instance: dict) -> dict:
    d = instance["data"]
    return {
        "walk_radius": bits_to_f32(d["walk_radius_b32"]),
        "board_penalty": bits_to_f32(d["board_penalty_b32"]),
        "discount": Fraction(bits_to_f32(d["transfer_discount_b32"])),
        "max_travel": Fraction(bits_to_f32(d["max_travel_seconds_b32"])),
        "margin": Fraction(bits_to_f32(d["switch_margin_b32"])),
        "reach": bits_to_f32(d["zone_stop_reach_b32"]),
        "zone_x": [bits_to_f32(b) for b in d["zone_x_b32"]],
        "zone_z": [bits_to_f32(b) for b in d["zone_z_b32"]],
        "flows": [(f["origin"], f["dest"], Fraction(bits_to_f32(f["weight_b32"])))
                  for f in d["flows"]],
        "existing_x": [bits_to_f32(b) for b in d["existing_stop_x_b32"]],
        "existing_z": [bits_to_f32(b) for b in d["existing_stop_z_b32"]],
        "existing_lines": [
            transit.Line(stops=list(l["stops"]),
                         wait=bits_to_f32(l["expected_wait_b32"]),
                         speed=bits_to_f32(l["speed_b32"]))
            for l in d["existing_lines"]
        ],
        "candidates": [
            {
                "x": [bits_to_f32(b) for b in c["stop_x_b32"]],
                "z": [bits_to_f32(b) for b in c["stop_z_b32"]],
                "wait": bits_to_f32(c["expected_wait_b32"]),
                "speed": bits_to_f32(c["speed_b32"]),
                "flow": bits_to_f32(c["captured_flow_b32"]),
            }
            for c in d["candidates"]
        ],
        "max_accept": d["max_accept"],
    }


def base_zone_map(p: dict) -> tuple[list[int], list[float]]:
    incumbents = [-1] * len(p["zone_x"])
    dist_sq = [0.0] * len(p["zone_x"])
    if p["existing_x"]:
        return transit.remap_zones(p["zone_x"], p["zone_z"], incumbents, dist_sq,
                                   p["existing_x"], p["existing_z"], 0, p["reach"])
    return incumbents, dist_sq


def candidate_line(p: dict, c: dict, first_stop: int) -> transit.Line:
    return transit.Line(
        stops=list(range(first_stop, first_stop + len(c["x"]))),
        wait=c["wait"], speed=c["speed"])


def candidate_interval(p: dict, accepted: list[int], cand_index: int,
                       base_map: tuple[list[int], list[float]]
                       ) -> tuple[Fraction, Fraction, bool, int]:
    """Exact credit interval for one candidate against N_{t-1} = existing +
    accepted, mirroring the ScoreCandidates wiring."""
    existing_count = len(p["existing_x"])
    acc_x, acc_z, acc_lines = [], [], []
    for a in accepted:
        c = p["candidates"][a]
        first = existing_count + len(acc_x)
        acc_lines.append(candidate_line(p, c, first))
        acc_x.extend(c["x"])
        acc_z.extend(c["z"])
    base_stops = existing_count + len(acc_x)
    xs = p["existing_x"] + acc_x
    zs = p["existing_z"] + acc_z
    base_lines = p["existing_lines"] + acc_lines

    round_stop, round_sq = base_map
    if acc_x:
        round_stop, round_sq = transit.remap_zones(
            p["zone_x"], p["zone_z"], base_map[0], base_map[1],
            acc_x, acc_z, existing_count, p["reach"])

    # Baselines on N_{t-1} (variants over tied itineraries).
    base_net = transit.build_network(xs, zs, base_lines,
                                     p["walk_radius"], p["board_penalty"])
    dist_cache: dict = {}
    baselines = []
    for origin_zone, dest_zone, _ in p["flows"]:
        o = round_stop[origin_zone]
        dst = round_stop[dest_zone]
        access = Fraction(transit.walk_seconds(round_sq[origin_zone])) \
            + Fraction(transit.walk_seconds(round_sq[dest_zone]))
        baselines.append(transit.baseline_variants(
            base_net, o, dst, access, p["max_travel"], dist_cache))

    cand = p["candidates"][cand_index]
    xs2 = xs + cand["x"]
    zs2 = zs + cand["z"]
    lines2 = base_lines + [candidate_line(p, cand, base_stops)]
    target = len(lines2) - 1
    net = transit.build_network(xs2, zs2, lines2,
                                p["walk_radius"], p["board_penalty"])

    mapped_stop, mapped_sq = transit.remap_zones(
        p["zone_x"], p["zone_z"], round_stop, round_sq,
        cand["x"], cand["z"], base_stops, p["reach"])

    pairs = []
    for i, (origin_zone, dest_zone, weight) in enumerate(p["flows"]):
        o = mapped_stop[origin_zone]
        dst = mapped_stop[dest_zone]
        if o < 0 or dst < 0 or o == dst:
            continue
        access = Fraction(transit.walk_seconds(mapped_sq[origin_zone])) \
            + Fraction(transit.walk_seconds(mapped_sq[dest_zone]))
        pairs.append(transit.Pair(o, dst, weight, access, baselines[i]))

    lo, hi, ties = transit.credit_interval(net, pairs, target, p["discount"],
                                           p["max_travel"], p["margin"])
    return lo, hi, ties, len(pairs)


def set_objective(p: dict, subset: list[int]) -> tuple[Fraction, Fraction, bool]:
    """Credited-sum objective of a SET of candidates against the N0 baseline."""
    existing_count = len(p["existing_x"])
    base_map = base_zone_map(p)

    # N0 baselines.
    n0 = transit.build_network(p["existing_x"], p["existing_z"],
                               p["existing_lines"], p["walk_radius"],
                               p["board_penalty"])
    dist_cache: dict = {}
    baselines = []
    for origin_zone, dest_zone, _ in p["flows"]:
        o = base_map[0][origin_zone]
        dst = base_map[0][dest_zone]
        access = Fraction(transit.walk_seconds(base_map[1][origin_zone])) \
            + Fraction(transit.walk_seconds(base_map[1][dest_zone]))
        baselines.append(transit.baseline_variants(
            n0, o, dst, access, p["max_travel"], dist_cache))

    add_x, add_z, add_lines = [], [], []
    for a in subset:
        c = p["candidates"][a]
        first = existing_count + len(add_x)
        add_lines.append(candidate_line(p, c, first))
        add_x.extend(c["x"])
        add_z.extend(c["z"])
    xs = p["existing_x"] + add_x
    zs = p["existing_z"] + add_z
    lines = p["existing_lines"] + add_lines
    net = transit.build_network(xs, zs, lines, p["walk_radius"], p["board_penalty"])

    mapped_stop, mapped_sq = base_map
    if add_x:
        mapped_stop, mapped_sq = transit.remap_zones(
            p["zone_x"], p["zone_z"], base_map[0], base_map[1],
            add_x, add_z, existing_count, p["reach"])

    pairs = []
    for i, (origin_zone, dest_zone, weight) in enumerate(p["flows"]):
        o = mapped_stop[origin_zone]
        dst = mapped_stop[dest_zone]
        if o < 0 or dst < 0 or o == dst:
            continue
        access = Fraction(transit.walk_seconds(mapped_sq[origin_zone])) \
            + Fraction(transit.walk_seconds(mapped_sq[dest_zone]))
        pairs.append(transit.Pair(o, dst, weight, access, baselines[i]))

    return transit.credit_interval(net, pairs, -1, p["discount"],
                                   p["max_travel"], p["margin"])


def check(instance: dict, solution: dict) -> dict:
    p = parse(instance)
    base_map = base_zone_map(p)
    accepted: list[int] = []
    rounds_report = []
    ok = True
    ties_seen = False

    for r, round_data in enumerate(solution["rounds"]):
        credits = {int(k): bits_to_fraction(v)
                   for k, v in round_data["credits_b32"].items()}
        chosen = round_data["accepted"]
        intervals = {}
        for c in credits:
            lo, hi, tie, pairs = candidate_interval(p, accepted, c, base_map)
            eps = credit_budget(hi, pairs)
            intervals[c] = (lo, hi, eps, tie)
            ties_seen = ties_seen or tie

        in_interval = {}
        for c, value in credits.items():
            lo, hi, eps, _ = intervals[c]
            in_interval[c] = (lo - eps) <= value <= (hi + eps)
        credits_ok = all(in_interval.values())

        # Acceptance consistency: no unsettled candidate may be provably better
        # than the chosen one (its lo above the chosen's hi + budget).
        lo_c, hi_c, eps_c, _ = intervals[chosen]
        dominated = [
            c for c, (lo, hi, eps, _) in intervals.items()
            if c != chosen and lo - eps > hi_c + eps_c
        ]
        tie_with_chosen = [
            c for c, (lo, hi, eps, _) in intervals.items()
            if c != chosen and not (hi + eps < lo_c - eps_c or lo - eps > hi_c + eps_c)
        ]
        round_ok = credits_ok and not dominated
        ok = ok and round_ok
        # An acceptance-order ambiguity (another candidate's interval overlapping
        # the chosen one's) is a tie in the sense of the resolved Q3 semantics.
        ties_seen = ties_seen or bool(tie_with_chosen)
        rounds_report.append({
            "round": r,
            "chosen": chosen,
            "credits_in_interval": {str(k): v for k, v in in_interval.items()},
            "intervals": {str(c): [str(lo), str(hi)]
                          for c, (lo, hi, _, _) in intervals.items()},
            "provably_better_than_chosen": dominated,
            "overlapping_with_chosen": tie_with_chosen,
            "ok": round_ok,
        })
        accepted.append(chosen)

    greedy_lo, greedy_hi, greedy_tie = set_objective(p, accepted)
    return {
        "ok": ok,
        "rounds": rounds_report,
        "accepted": accepted,
        "greedy_set_objective": [str(greedy_lo), str(greedy_hi)],
        "tie_affected": ties_seen or greedy_tie,
    }
