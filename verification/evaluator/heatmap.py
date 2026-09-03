"""S1 stop-derived heatmap terms (C1.1, offline-reachable part): independent
exact binary32 re-implementation of AccumulateStop + ModeTerms (spec §7.2
T3/T6/T7 and §7.4), compared bit-exact against the mod code, plus the kernel
boundary properties (a stop at exactly the catchment or transfer radius, and
one beyond the catchment).

The gathered terms T1/T2/T4/T5 live in the Burst job and cannot be executed
offline; their reference formulas are specified in §7.2 and remain paired with
the future real-save export (claims table C1.1).
"""

from __future__ import annotations

import math

from common.canonical import bits_to_f32, f32_bits
from evaluator import f32


def saturate(v: float) -> float:
    return 0.0 if v < 0.0 else (1.0 if v > 1.0 else v)


def accumulate_stop(distance: float, weight: float, same_mode: bool,
                    catchment: float, interchange_radius: float,
                    coverage: float, interchange: float, cross: float
                    ) -> tuple[float, float, float]:
    if catchment <= 0.0 or distance > catchment:
        return coverage, interchange, cross
    within = f32.sub(1.0, f32.div(distance, catchment))
    if same_mode:
        return f32.add(coverage, f32.mul(weight, within)), interchange, cross
    transferable = (f32.sub(1.0, f32.div(distance, interchange_radius))
                    if interchange_radius > 0.0 and distance <= interchange_radius
                    else 0.0)
    interchange = f32.add(interchange, f32.mul(weight, transferable))
    cross = f32.add(cross, f32.mul(f32.mul(weight, within),
                                   f32.sub(1.0, transferable)))
    return coverage, interchange, cross


def mode_terms(coverage_share: float, interchange: float, cross: float,
               inv_self: float, w3: float, w6: float, w7: float) -> float:
    bonus = f32.mul(w6, saturate(f32.mul(interchange, inv_self)))
    cov = f32.mul(w3, coverage_share)
    dup = f32.mul(w7, saturate(f32.mul(cross, inv_self)))
    return f32.sub(f32.sub(bonus, cov), dup)


def distance_f32(ax: float, az: float, bx: float, bz: float) -> float:
    dx = f32.sub(ax, bx)
    dz = f32.sub(az, bz)
    # MathF.Sqrt of a float32 sum: double sqrt rounded once to float32 is the
    # correctly rounded float32 sqrt (2p+2 <= 53).
    return f32.r(math.sqrt(f32.add(f32.mul(dx, dx), f32.mul(dz, dz))))


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    catchment = bits_to_f32(data["catchment_b32"])
    interchange_radius = bits_to_f32(data["interchange_b32"])
    inv_self = bits_to_f32(data["inv_self_b32"])
    w3 = bits_to_f32(data["coverage_weight_b32"])
    w6 = bits_to_f32(data["interchange_weight_b32"])
    w7 = bits_to_f32(data["cross_weight_b32"])
    stops = [(bits_to_f32(s["x_b32"]), bits_to_f32(s["z_b32"]),
              bits_to_f32(s["weight_b32"]), s["same_mode"])
             for s in data["stops"]]

    queries = []
    all_exact = True
    for qi, q in enumerate(data["queries"]):
        qx = bits_to_f32(q["x_b32"])
        qz = bits_to_f32(q["z_b32"])
        cov = inter = cross = 0.0
        contributions = []
        for (sx, sz, w, same) in stops:
            d = distance_f32(sx, sz, qx, qz)
            c0, i0, x0 = cov, inter, cross
            cov, inter, cross = accumulate_stop(
                d, w, same, catchment, interchange_radius, cov, inter, cross)
            contributions.append((f32.sub(cov, c0), f32.sub(inter, i0),
                                  f32.sub(cross, x0)))
        share = f32.div(min(cov, f32.r(1.5)), f32.r(1.5))
        terms = mode_terms(share, inter, cross, inv_self, w3, w6, w7)
        subject = solution["queries"][qi]
        exact = (f32_bits(cov) == subject["coverage_b32"]
                 and f32_bits(inter) == subject["interchange_b32"]
                 and f32_bits(cross) == subject["cross_b32"]
                 and f32_bits(terms) == subject["mode_terms_b32"])
        all_exact = all_exact and exact
        queries.append({"exact": exact, "contributions": [
            [str(a), str(b), str(c)] for a, b, c in contributions]})

    # Kernel boundary properties at query 0 (origin), per the generator layout:
    # stop 3 sits at exactly the transfer radius, stop 4 at exactly the
    # catchment, stop 5 just beyond it.
    props = {}
    if queries:
        c3, i3, x3 = [float(v) for v in queries[0]["contributions"][3]]
        c4, i4, x4 = [float(v) for v in queries[0]["contributions"][4]]
        c5, i5, x5 = [float(v) for v in queries[0]["contributions"][5]]
        props = {
            "at_transfer_radius_no_interchange": i3 == 0.0 and x3 > 0.0,
            "at_catchment_edge_contributes_zero": (c4, i4, x4) == (0.0, 0.0, 0.0),
            "beyond_catchment_contributes_zero": (c5, i5, x5) == (0.0, 0.0, 0.0),
        }

    ok = all_exact and all(props.values())
    return {"ok": ok, "all_exact": all_exact, "properties": props,
            "queries": [{"exact": q["exact"]} for q in queries]}
