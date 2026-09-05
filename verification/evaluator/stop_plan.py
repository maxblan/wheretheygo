"""S5 v2 stop plan (docs/formal-specification.md §4 v2), re-derived without mod code.

Objective for a chosen set S of candidates (termini and forced calls included):
  gain(S)  = Σ_ends w · max(0, H − t_e(S)),  t_e(S) = walk seconds from the end to
             the nearer of the two chosen stops bracketing its projection
             (ends before the first / after the last chosen stop see that one only)
  delay(S) = Σ_{c in S} through_c · delay
  value    = gain − delay
Feasibility: candidate 0 and the last candidate chosen; every must_call chosen;
consecutive chosen stops ≥ min_gap apart unless both are forced.
The evaluator computes the optimum exactly (rationals) — by complete enumeration
over the free candidates when there are at most ENUM_LIMIT of them, otherwise by
an independent dynamic programme over the same definition — and judges the mod's
choice: its value must equal the optimum (ties reported), within the binary32
budget of its double arithmetic.
"""

from __future__ import annotations

import itertools
import math
from fractions import Fraction

from common.canonical import bits_to_f32

ENUM_LIMIT = 14


def parse(plan: dict) -> dict:
    f = lambda key: [bits_to_f32(b) for b in plan[key]]  # noqa: E731
    p = {
        "at": f("candidate_at_b32"), "cx": f("candidate_x_b32"), "cz": f("candidate_z_b32"),
        "must": list(plan["must_call"]), "through": f("through_flow_b32"),
        "end_at": f("end_at_b32"), "ex": f("end_x_b32"), "ez": f("end_z_b32"), "ew": f("end_w_b32"),
        "gap": bits_to_f32(plan["min_gap_b32"]), "delay": bits_to_f32(plan["delay_per_stop_b32"]),
        "horizon": bits_to_f32(plan["horizon_b32"]), "speed": bits_to_f32(plan["walk_speed_b32"]),
    }
    p["m"] = len(p["at"]); p["n"] = len(p["end_at"])
    return p


def walk_seconds(p: dict, end: int, cand: int) -> Fraction:
    dx = Fraction(p["ex"][end]) - Fraction(p["cx"][cand])
    dz = Fraction(p["ez"][end]) - Fraction(p["cz"][cand])
    # exact Euclidean distance is irrational in general; the mod rounds sqrt to binary32.
    # We use the float sqrt of the exact squared distance (correctly rounded double) and
    # treat the result as the definition's walking time; the budget covers the rounding.
    d = math.sqrt(float(dx * dx + dz * dz))
    return Fraction(d) / Fraction(p["speed"])


def kernel(p: dict, walk: Fraction) -> Fraction:
    return max(Fraction(0), Fraction(p["horizon"]) - walk)


def forced(p: dict, c: int) -> bool:
    return c == 0 or c == p["m"] - 1 or bool(p["must"][c])


def feasible(p: dict, chosen: list[int]) -> bool:
    if not chosen or chosen[0] != 0 or chosen[-1] != p["m"] - 1:
        return False
    if any(p["must"][c] and c not in chosen for c in range(p["m"])):
        return False
    for i in range(1, len(chosen)):
        a, b = chosen[i - 1], chosen[i]
        if p["at"][b] - p["at"][a] < p["gap"] and not (forced(p, a) and forced(p, b)):
            return False
    return True


def value(p: dict, chosen: list[int]) -> tuple[Fraction, Fraction]:
    gain = Fraction(0)
    for e in range(p["n"]):
        after = next((i for i, c in enumerate(chosen) if p["end_at"][e] <= p["at"][c]), None)
        if after is None:
            walk = walk_seconds(p, e, chosen[-1])
        elif after == 0:
            walk = walk_seconds(p, e, chosen[0])
        else:
            walk = min(walk_seconds(p, e, chosen[after - 1]), walk_seconds(p, e, chosen[after]))
        gain += Fraction(p["ew"][e]) * kernel(p, walk)
    delay = sum((Fraction(p["through"][c]) * Fraction(p["delay"]) for c in chosen), Fraction(0))
    return gain, delay


def optimum(p: dict) -> tuple[Fraction, list[list[int]], str]:
    m = p["m"]
    free = [c for c in range(1, m - 1) if not p["must"][c]]
    if len(free) <= ENUM_LIMIT:
        best = None; sets = []
        for r in range(len(free) + 1):
            for pick in itertools.combinations(free, r):
                chosen = sorted(set(pick) | {0, m - 1} | {c for c in range(m) if p["must"][c]})
                if not feasible(p, chosen):
                    continue
                g, d = value(p, chosen); v = g - d
                if best is None or v > best:
                    best, sets = v, [chosen]
                elif v == best:
                    sets.append(chosen)
        return best, sets, "enumeration"
    # independent DP (same definition): f[k] best value with k chosen, ends assigned by projection
    order = sorted(range(p["n"]), key=lambda e: (p["end_at"][e], e))
    past = []
    cur = 0
    for c in range(m):
        while cur < p["n"] and p["end_at"][order[cur]] <= p["at"][c]:
            cur += 1
        past.append(cur)
    def between(i, k):
        return sum((Fraction(p["ew"][order[e]]) * kernel(p, min(walk_seconds(p, order[e], i), walk_seconds(p, order[e], k)))
                    for e in range(past[i], past[k])), Fraction(0))
    def beyond(c, before):
        return sum((Fraction(p["ew"][e]) * kernel(p, walk_seconds(p, e, c)) for e in range(p["n"])
                    if (p["end_at"][e] <= p["at"][c]) == before), Fraction(0))
    cost = [Fraction(p["through"][c]) * Fraction(p["delay"]) for c in range(m)]
    f = [None] * m; parent = [-1] * m
    f[0] = beyond(0, True) - cost[0]
    last_forced = -1; lf = [0] * m
    for c in range(m):
        lf[c] = last_forced
        if c == 0 or p["must"][c]:
            last_forced = c
    for k in range(1, m):
        for i in range(k - 1, max(0, lf[k]) - 1, -1):
            if f[i] is None:
                continue
            if p["at"][k] - p["at"][i] < p["gap"] and not (forced(p, i) and forced(p, k)):
                continue
            v = f[i] + between(i, k) - cost[k]
            if f[k] is None or v > f[k]:
                f[k], parent[k] = v, i
    if f[m - 1] is None:
        chosen = [0, m - 1]
        g, d = value(p, chosen)
        return g - d, [chosen], "dp-fallback"
    chosen = []; c = m - 1
    while c >= 0:
        chosen.append(c)
        if c == 0:
            break
        c = parent[c]
    chosen.reverse()
    return f[m - 1] + beyond(m - 1, False), [chosen], "dp"


def check(instance: dict, solution: dict) -> dict:
    rows = []
    all_ok = True
    for plan, sub in zip(instance["data"]["plans"], solution["plans"]):
        p = parse(plan)
        game = list(plan["chosen"]) if plan.get("chosen") is not None else None
        subject = list(sub["chosen"])
        g, d = value(p, subject)
        best, sets, method = optimum(p)
        # budget: doubles over binary32 inputs; sqrt rounding per end ~ 2^-24 relative
        scale = sum(Fraction(w) for w in p["ew"]) * Fraction(p["horizon"]) + sum(Fraction(t) for t in p["through"]) * Fraction(p["delay"])
        budget = scale * Fraction(1, 2**20)
        sub_value = g - d
        row = {
            "candidates": p["m"], "ends": p["n"], "subject_chosen": subject, "game_chosen": game,
            "subject_matches_game": game is None or subject == game,
            "subject_feasible": feasible(p, subject),
            "subject_value": str(sub_value), "optimum_value": str(best), "optimum_sets": sets[:4], "optimum_ties": len(sets),
            "method": method,
            "subject_optimal": best is not None and abs(best - sub_value) <= budget,
            "subject_value_reported": sub.get("value"),
            "reported_within_budget": abs(Fraction(float(sub["value"])) - sub_value) <= budget,
        }
        row["ok"] = row["subject_matches_game"] and row["subject_feasible"] and row["subject_optimal"] and row["reported_within_budget"]
        all_ok = all_ok and row["ok"]
        rows.append(row)
    return {"ok": all_ok, "plans": rows, "tie_affected": any(r["optimum_ties"] > 1 for r in rows)}
