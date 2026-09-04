"""Deterministic instance generators.

Every instance is written in canonical form with an embedded SHA-256 hash. The
generated files are committed; re-running this script must reproduce them
byte-for-byte (seeded PRNG, no time, no environment reads).

Instance kinds and what they verify (see docs/formal-specification.md):
  sites          S2  FindTopSites: feasibility, greedy faithfulness, exact gap
                     to the certified max-sum optimum (SCIP exact + VIPR).
  lattice_path   S4  Dijkstra path optimality (exact distance-label certificate).
  calling_points S5  PlanCallingPoints / SelectCallingPoints invariants.
  mode_choice    S6  ChooseMode gate cascade re-evaluation.
  lineset        S7  transit graph + CreditLine semantics, greedy rounds vs the
                     enumerated optimum of the credited-sum set objective.
"""

from __future__ import annotations

import math
import os
import random
import struct
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from common.canonical import f32_bits, write_instance  # noqa: E402

OUT = os.path.join(os.path.dirname(__file__), "..", "instances")


def f32_list(values):
    return [f32_bits(v) for v in values]


# ---------------------------------------------------------------- sites (S2)

def sites_greedy_gap():
    """Greedy takes the single 8 in the middle; the two 5s at the ends are
    mutually feasible and sum to 10. Chebyshev separation 4 blocks 8+5 pairs
    (distance 3) but allows 5..5 (distance 6). Every scored cell is a 3x3 local
    maximum (neighbours are 0)."""
    width, height = 9, 3
    scores = [0.0] * (width * height)
    scores[1 + 1 * width] = 5.0
    scores[4 + 1 * width] = 8.0
    scores[7 + 1 * width] = 5.0
    return {
        "kind": "sites",
        "name": "sites-greedy-gap",
        "comment": "minimal counterexample: greedy sum 8 < optimal sum 10",
        "expect": {"greedy_is_optimal": False},
        "data": {
            "width": width,
            "height": height,
            "scores_b32": f32_list(scores),
            "min_separation": 4,
            "max_sites": 2,
        },
    }


def sites_random(name, width, height, seed, min_separation, max_sites):
    rng = random.Random(seed)
    scores = []
    for _ in range(width * height):
        # ~60% empty land, the rest scored in (0, 10).
        scores.append(0.0 if rng.random() < 0.6 else rng.random() * 10.0)
    return {
        "kind": "sites",
        "name": name,
        "seed": seed,
        "data": {
            "width": width,
            "height": height,
            "scores_b32": f32_list(scores),
            "min_separation": min_separation,
            "max_sites": max_sites,
        },
    }


def sites_plateau():
    """A 2x2 plateau of equal scores: exactly one plateau cell (lowest linear
    index) may be a candidate under the mod's tie rule."""
    width, height = 8, 8
    scores = [0.0] * (width * height)
    for x, y in ((3, 3), (4, 3), (3, 4), (4, 4)):
        scores[x + y * width] = 7.0
    scores[0] = 2.0  # a lone secondary site
    return {
        "kind": "sites",
        "name": "sites-plateau",
        "data": {
            "width": width,
            "height": height,
            "scores_b32": f32_list(scores),
            "min_separation": 2,
            "max_sites": 4,
        },
    }


# --------------------------------------------------------- lattice_path (S4)

def grid_graph(cols, rows, blocked, scale_fn, seed=None):
    """8-connected lattice like SuitabilityLattice.Build: orthogonal cost 128,
    diagonal 128*1.41421356 (the mod's 7-digit constant), times a per-edge
    scale. Costs are rounded through binary32 like the mod's float pipeline."""
    def rf32(x):
        return struct.unpack("<f", struct.pack("<f", x))[0]

    index = {}
    xs, zs = [], []
    for gy in range(rows):
        for gx in range(cols):
            if (gx, gy) in blocked:
                continue
            index[(gx, gy)] = len(xs)
            xs.append((gx + 0.5) * 128.0)
            zs.append((gy + 0.5) * 128.0)
    diag = rf32(128.0 * 1.41421356)
    edges = []
    for gy in range(rows):
        for gx in range(cols):
            if (gx, gy) not in index:
                continue
            a = index[(gx, gy)]
            for dx, dy, base in ((1, 0, 128.0), (0, 1, 128.0),
                                 (1, 1, diag), (1, -1, diag)):
                nb = (gx + dx, gy + dy)
                if nb not in index:
                    continue
                cost = rf32(base * scale_fn(gx, gy, nb[0], nb[1]))
                edges.append((a, index[nb], cost))
    return xs, zs, edges, index


def lattice_path_rail():
    """12x9 land lattice with a cheap 'existing track' row (scale 0.35) and
    expensive fresh ground (1.6), one water hole. Unique optimum by design."""
    blocked = {(5, 3), (5, 4), (6, 3), (6, 4)}

    def scale(x0, y0, x1, y1):
        on_track = y0 == 1 and y1 == 1
        return 0.35 if on_track else 1.6

    xs, zs, edges, index = grid_graph(12, 9, blocked, scale)
    return {
        "kind": "lattice_path",
        "name": "path-rail",
        "data": {
            "node_x_b32": f32_list(xs),
            "node_z_b32": f32_list(zs),
            "edge_a": [e[0] for e in edges],
            "edge_b": [e[1] for e in edges],
            "edge_cost_b32": f32_list([e[2] for e in edges]),
            "from": index[(0, 7)],
            "to": index[(11, 7)],
            "max_cost_b32": f32_bits(30000.0),
        },
    }


def lattice_path_tie():
    """Two exactly equal-cost routes (integer costs, symmetric grid): the
    verifier must accept either and report the tie."""
    xs = [0.0, 100.0, 100.0, 200.0]
    zs = [0.0, 100.0, -100.0, 0.0]
    edges = [(0, 1, 100.0), (0, 2, 100.0), (1, 3, 100.0), (2, 3, 100.0)]
    return {
        "kind": "lattice_path",
        "name": "path-tie",
        "expect": {"tie": True},
        "data": {
            "node_x_b32": f32_list(xs),
            "node_z_b32": f32_list(zs),
            "edge_a": [e[0] for e in edges],
            "edge_b": [e[1] for e in edges],
            "edge_cost_b32": f32_list([e[2] for e in edges]),
            "from": 0,
            "to": 3,
            "max_cost_b32": f32_bits(30000.0),
        },
    }


# ------------------------------------------------------- calling_points (S5)

def calling_points_cases():
    """PlanCallingPoints length/spacing cases incl. the README's 2150/450
    example, plus SelectCallingPoints score profiles."""
    return {
        "kind": "calling_points",
        "name": "calling-points",
        "data": {
            "plans": [
                {"length_b32": f32_bits(2150.0), "spacing_b32": f32_bits(450.0), "buffer": 32},
                {"length_b32": f32_bits(6000.0), "spacing_b32": f32_bits(800.0), "buffer": 32},
                {"length_b32": f32_bits(100.0), "spacing_b32": f32_bits(450.0), "buffer": 32},
                {"length_b32": f32_bits(2150.0), "spacing_b32": f32_bits(450.0), "buffer": 3},
            ],
            "selections": [
                {
                    "scores_b32": f32_list([1.0, 0.0, 4.0, 0.1, 3.0, 0.0, 2.0]),
                    "floor_share_b32": f32_bits(0.35),
                    "must_call": [False, False, False, False, False, True, False],
                },
                {
                    "scores_b32": f32_list([0.0, 0.0, 0.0, 0.0]),
                    "floor_share_b32": f32_bits(0.35),
                    "must_call": [False, False, False, False],
                },
            ],
        },
    }


# ------------------------------------------------------- heatmap point (S1)

def heatmap_point():
    """The stop-derived heatmap terms (T3/T6/T7) at a point: the mod's
    AccumulateStop + ModeTerms (linked pure code) against an independent exact
    binary32 re-implementation. Stops sit at engineered distances including
    EXACTLY the interchange radius (250) and EXACTLY the catchment (350), where
    the >-vs->= semantics of the kernel gates show. The remaining terms
    (T1/T2/T4/T5) live in the Burst job and are unreachable offline — see
    correctness-claims C1.1."""
    catchment = 350.0
    interchange = 250.0

    def stop(x, z, weight, same):
        return {"x_b32": f32_bits(x), "z_b32": f32_bits(z),
                "weight_b32": f32_bits(weight), "same_mode": same}

    stops = [
        stop(0.0, 0.0, 1.0, True),        # same mode on top of the query
        stop(100.0, 0.0, 1.0, True),      # same mode inside
        stop(0.0, 100.0, 2.5, False),     # metro nearby: strong interchange
        stop(250.0, 0.0, 3.0, False),     # train at EXACTLY the transfer radius
        stop(0.0, 350.0, 1.5, False),     # tram at EXACTLY the catchment edge
        stop(0.0, 351.0, 3.0, False),     # just outside: contributes nothing
        stop(-200.0, 0.0, 1.2, False),    # ferry inside transfer range
        stop(300.0, 0.0, 1.5, False),     # tram between transfer and catchment
    ]
    queries = [
        {"x_b32": f32_bits(0.0), "z_b32": f32_bits(0.0)},
        {"x_b32": f32_bits(50.0), "z_b32": f32_bits(50.0)},
        {"x_b32": f32_bits(-400.0), "z_b32": f32_bits(0.0)},
    ]
    return {
        "kind": "heatmap_point",
        "name": "heatmap-point",
        "data": {
            "catchment_b32": f32_bits(catchment),
            "interchange_b32": f32_bits(interchange),
            "inv_self_b32": f32_bits(1.0),          # placing a bus (weight 1)
            "coverage_weight_b32": f32_bits(1.2),   # W3 (bus defaults)
            "interchange_weight_b32": f32_bits(0.9),  # W6
            "cross_weight_b32": f32_bits(0.4),      # W7
            "stops": stops,
            "queries": queries,
        },
    }


# -------------------------------------------------------- heatmap grid (S1)

def _f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


def _dist(ax, az, bx, bz):
    dx = _f32(ax - bx)
    dz = _f32(az - bz)
    return _f32(math.sqrt(_f32(_f32(dx * dx) + _f32(dz * dz))))


def _tri(d, r):
    return _f32(1.0 - _f32(d / r))


def heatmap_grid_plumbing():
    """A hand-sized `heatmap_grid` instance whose expected terms are computed
    HERE, directly over the handful of sources, with no bucket sweep and no
    component-lookup machinery — the two things the evaluator does differently.
    A 4x4 map at a 128 m bucket pitch has exactly one bucket, so the evaluator's
    sweep must reduce to this straight-line sum.

    This exercises the evaluator's plumbing; it does NOT test the Burst job,
    which cannot run offline. Only a real export closes C1.1b, and the claims
    table says so.

    Layout: tiles 32 m, world_min (-64,-64), so tile centres sit at -48, -16,
    16, 48. Components split the map down the middle (x<=1 -> 1, x>=2 -> 2), so
    a sample cell on the left must gate out every source on the right.
    """
    grid = 4
    tile = 32.0
    world_min = -64.0
    catchment = 350.0
    access_radius = 120.0
    interchange_radius = 250.0

    def centre(i):
        x, y = i % grid, i // grid
        return (_f32(world_min + _f32(_f32(x + 0.5) * tile)),
                _f32(world_min + _f32(_f32(y + 0.5) * tile)))

    components = [1 if (i % grid) <= 1 else 2 for i in range(grid * grid)]
    buildable = [1] * (grid * grid)
    # Two unbuildable tiles, of the two kinds the masks actually produce, so the
    # job's gate (all seven terms zero) is covered along with the relationship the
    # first real export corrected:
    #   * water — not land, so unlabelled;
    #   * steep land — unbuildable but STILL LABELLED, because connectivity floods
    #     over land rather than over buildable (SuitabilityMasks pass 2). A checker
    #     that demands buildable <=> labelled fails on real terrain, and did.
    buildable[15] = 0
    components[15] = 0          # water
    buildable[9] = 0            # steep land, keeps component 1
    land = [1 if component > 0 else 0 for component in components]

    # Population raster shares the tile grid, so a population cell centre IS a
    # tile centre — which keeps the expected values readable.
    population = [0.0] * (grid * grid)
    population[0] = 100.0          # (0,0), component 1
    population[15] = 500.0         # (3,3), component 2 -> gated out from the left

    def pt(i, w):
        cx, cz = centre(i)
        return (cx, cz, w)

    jobs = [pt(4, 50.0)]                     # (0,1) component 1
    future_homes = [pt(1, 25.0)]             # (1,0) component 1
    future_jobs = [pt(5, 35.0), pt(7, 60.0)]  # (1,1) c1 and (3,1) c2
    nodes = [pt(0, 1.0), pt(1, 1.0)]
    edges = [pt(4, 1.0)]
    stops = [pt(1, 1.0)]                     # same mode
    other_stops = [pt(4, 2.5), pt(2, 3.0)]   # metro left, train right

    def bucket_set(points):
        return {
            "x_b32": f32_list([p[0] for p in points]),
            "z_b32": f32_list([p[1] for p in points]),
            "w_b32": f32_list([p[2] for p in points]),
            "offsets": [0],
            "counts": [len(points)],
        }

    def accumulate_stop(d, w, same, cov, inter, cross):
        if catchment <= 0.0 or d > catchment:
            return cov, inter, cross
        within = _f32(1.0 - _f32(d / catchment))
        if same:
            return _f32(cov + _f32(w * within)), inter, cross
        transferable = (_f32(1.0 - _f32(d / interchange_radius))
                        if interchange_radius > 0.0 and d <= interchange_radius else 0.0)
        inter = _f32(inter + _f32(w * transferable))
        cross = _f32(cross + _f32(_f32(w * within) * _f32(1.0 - transferable)))
        return cov, inter, cross

    def expected(i):
        if buildable[i] == 0:
            return {"demand": 0.0, "jobs": 0.0, "coverage": 0.0, "access": 0.0,
                    "future": 0.0, "interchange": 0.0, "cross": 0.0}
        cx, cz = centre(i)
        comp = components[i]
        demand = 0.0
        for j in range(grid * grid):
            px, pz = centre(j)
            d = _dist(px, pz, cx, cz)
            if d > catchment or components[j] != comp:
                continue
            demand = _f32(demand + _f32(population[j] * _tri(d, catchment)))

        def gated_sum(points):
            total = 0.0
            for (px, pz, w) in points:
                d = _dist(px, pz, cx, cz)
                if d > catchment:
                    continue
                # The source's component is that of the tile it stands on; every
                # source here sits on a tile centre.
                pj = min(max(int(math.floor(_f32(_f32(px - world_min) / tile))), 0), grid - 1)
                pk = min(max(int(math.floor(_f32(_f32(pz - world_min) / tile))), 0), grid - 1)
                if components[pj + pk * grid] != comp:
                    continue
                total = _f32(total + _f32(w * _tri(d, catchment)))
            return total

        cov = 0.0
        for (px, pz, w) in stops:
            cov, _i, _c = accumulate_stop(_dist(px, pz, cx, cz), w, True, cov, 0.0, 0.0)
        cov = min(max(cov, 0.0), _f32(1.5))

        inter = 0.0
        cross = 0.0
        for (px, pz, w) in other_stops:
            _cv, inter, cross = accumulate_stop(
                _dist(px, pz, cx, cz), w, False, 0.0, inter, cross)

        node_w = 0.0
        edge_w = 0.0
        for (px, pz, _w) in nodes:
            d = _dist(px, pz, cx, cz)
            if d <= access_radius:
                node_w = _f32(node_w + _tri(d, access_radius))
        for (px, pz, _w) in edges:
            d = _dist(px, pz, cx, cz)
            if d <= access_radius:
                edge_w = _f32(edge_w + _tri(d, access_radius))
        scale = _f32(_f32(120.0 * 120.0) / _f32(access_radius * access_radius))
        access = _f32(_f32(_f32(edge_w * _f32(0.06)) + _f32(node_w * _f32(0.15))) * scale)
        access = 0.0 if access < 0.0 else (1.0 if access > 1.0 else access)

        return {
            "demand": demand,
            "jobs": gated_sum(jobs),
            "coverage": cov,
            "access": access,
            "future": _f32(gated_sum(future_homes) + gated_sum(future_jobs)),
            "interchange": inter,
            "cross": cross,
        }

    sample = [0, 1, 5, 9, 10, 15]
    rows = [expected(i) for i in sample]
    return {
        "kind": "heatmap_grid",
        "name": "heatmap-grid-plumbing",
        "comment": "synthetic; exercises the heatmap_grid evaluator, does NOT test "
                   "the Burst job (only a real export does — see C1.1b)",
        "data": {
            "grid_x": grid, "grid_y": grid,
            "bucket_x": 1, "bucket_y": 1,
            "world_min_x_b32": f32_bits(world_min),
            "world_min_z_b32": f32_bits(world_min),
            "tile_size_b32": f32_bits(tile),
            "bucket_size_b32": f32_bits(128.0),
            "catchment_b32": f32_bits(catchment),
            "access_radius_b32": f32_bits(access_radius),
            "interchange_b32": f32_bits(interchange_radius),
            "population_cell_x_b32": f32_bits(tile),
            "population_cell_z_b32": f32_bits(tile),
            "population_tex_x": grid, "population_tex_y": grid,
            "population_b32": f32_list(population),
            "stops": bucket_set(stops),
            "other_stops": bucket_set(other_stops),
            "nodes": bucket_set(nodes),
            "edges": bucket_set(edges),
            "jobs": bucket_set(jobs),
            "future_homes": bucket_set(future_homes),
            "future_jobs": bucket_set(future_jobs),
            "components": components,
            "buildable": buildable,
            "land": land,
            "mode": "Bus",
            "sample_indices": sample,
            "sample_demand_b32": f32_list([r["demand"] for r in rows]),
            "sample_jobs_b32": f32_list([r["jobs"] for r in rows]),
            "sample_coverage_b32": f32_list([r["coverage"] for r in rows]),
            "sample_access_b32": f32_list([r["access"] for r in rows]),
            "sample_future_b32": f32_list([r["future"] for r in rows]),
            "sample_interchange_b32": f32_list([r["interchange"] for r in rows]),
            "sample_cross_b32": f32_list([r["cross"] for r in rows]),
        },
    }


# ------------------------------------------------------- order stats (S1/S5)

def order_stats():
    """SelectKth (Hoare quickselect) against the sorted reference, by COMPLETE
    ENUMERATION of every array of length 1..5 over the values {-1, 0, 1, 2}
    (duplicates and negatives included) with every k, plus k-clamping cases and
    an adversarial float list. PositivePercentile is enumerated over the same
    family at the three percentiles the mod uses."""
    values = [-1.0, 0.0, 1.0, 2.0]
    cases = []
    def rec(prefix):
        if prefix:
            for k in range(len(prefix)):
                cases.append({"values_b32": f32_list(prefix), "k": k})
        if len(prefix) == 5:
            return
        for v in values:
            rec(prefix + [v])
    rec([])
    # Clamping: k below 0 and past the end.
    cases.append({"values_b32": f32_list([3.0, 1.0, 2.0]), "k": -5})
    cases.append({"values_b32": f32_list([3.0, 1.0, 2.0]), "k": 99})
    # Adversarial floats: duplicates, tiny/large magnitudes.
    adv = [1e-30, -1e30, 1.5, 1.5, 0.1, 2e30, -0.0, 7.25]
    for k in range(len(adv)):
        cases.append({"values_b32": f32_list(adv), "k": k})

    pct = []
    def rec2(prefix):
        if prefix:
            for p in (0.35, 0.5, 0.98):
                pct.append({"values_b32": f32_list(prefix),
                            "percentile_b32": f32_bits(p)})
        if len(prefix) == 4:
            return
        for v in values:
            rec2(prefix + [v])
    rec2([])
    return {
        "kind": "order_stats",
        "name": "order-stats",
        "data": {"select_cases": cases, "percentile_cases": pct},
    }


# ----------------------------------------------------------- mode_choice (S6)

def mode_choice_sweep():
    """Evidence rows sweeping every gate: flow bar, reach bar (with and without
    track relief), rider floor, length floors, unscored-demand bypass."""
    def row(network, traced, flow, length, enabled, city, track, scored, ref):
        return {
            "network": network, "traced_mode": traced,
            "flow_b32": f32_bits(flow), "length_b32": f32_bits(length),
            "enabled_demand_b32": f32_bits(enabled),
            "city_travel_weight_b32": f32_bits(city),
            "track_share_b32": f32_bits(track),
            "demand_scored": scored,
            "reference_flow_b32": f32_bits(ref),
        }

    rows = [
        # Road: tram by flow (1.5x ref), long enough.
        row("Road", "Bus", 160.0, 2500.0, 500.0, 10000.0, 0.0, True, 100.0),
        # Road: bus fallback (below tram flow bar).
        row("Road", "Bus", 120.0, 2500.0, 500.0, 10000.0, 0.0, True, 100.0),
        # Road: too short for any road mode.
        row("Road", "Bus", 160.0, 900.0, 500.0, 10000.0, 0.0, True, 100.0),
        # Rail/Train: by reach share exactly at the 0.04 bar, riders above the
        # floor (train floor = 900/0.4 = 2250 journeys).
        row("Rail", "Train", 10.0, 12000.0, 2400.0, 60000.0, 0.0, True, 100.0),
        # Rail/Train: reach bar halved on track share 0.6 (share 0.02 suffices).
        row("Rail", "Train", 10.0, 12000.0, 2400.0, 120000.0, 0.6, True, 100.0),
        # Rail/Train: reach met but riders below the floor -> DemandTooLow.
        row("Rail", "Train", 10.0, 12000.0, 400.0, 10000.0, 0.0, True, 100.0),
        # Rail/Metro: rider floor rejects (enabled < capacity/0.4).
        row("Rail", "Metro", 600.0, 4000.0, 100.0, 10000.0, 0.0, True, 100.0),
        # Rail/Metro: unscored demand bypasses the rider floor.
        row("Rail", "Metro", 600.0, 4000.0, 0.0, 10000.0, 0.0, False, 100.0),
        # Water/Ferry: flow multiple 1, min length 1200, riders above 100/0.4.
        row("Water", "Ferry", 100.0, 1500.0, 300.0, 10000.0, 0.0, True, 100.0),
        # Water/Ferry: riders fine, but too short -> TooShort.
        row("Water", "Ferry", 100.0, 1100.0, 300.0, 10000.0, 0.0, True, 100.0),
    ]
    capacities = {"Bus": 80.0, "Metro": 1080.0, "Tram": 200.0,
                  "Train": 900.0, "Ferry": 100.0}
    return {
        "kind": "mode_choice",
        "name": "mode-choice-sweep",
        "data": {
            "rows": rows,
            "capacities_b32": {k: f32_bits(v) for k, v in capacities.items()},
        },
    }


# -------------------------------------------------------------- corridor (S4)

def corridor_common(name, comment, nodes, edges, flows, node_demand, objective,
                    rounds, max_len, cannot_host=None, min_flow_fraction=0.1,
                    demand_floor=0.005, expect=None):
    inst = {
        "kind": "corridor",
        "name": name,
        "comment": comment,
        "data": {
            "node_x_b32": f32_list([n[0] for n in nodes]),
            "node_z_b32": f32_list([n[1] for n in nodes]),
            "edge_a": [e[0] for e in edges],
            "edge_b": [e[1] for e in edges],
            "edge_cost_b32": f32_list([e[2] for e in edges]),
            "edge_flow_b32": f32_list(flows),
            "node_demand_b32": f32_list(node_demand) if node_demand is not None else None,
            "edge_cannot_host": cannot_host,
            "objective": objective,
            "min_flow_fraction_b32": f32_bits(min_flow_fraction),
            "max_route_length_b32": f32_bits(max_len),
            "demand_floor_b32": f32_bits(demand_floor),
            "rounds": rounds,
            "capture_b32": f32_bits(0.85),
            "novelty_hops": 3,
            "novelty_factor_b32": f32_bits(0.15),
            "max_low_demand_bridge": 2,
        },
    }
    if expect:
        inst["expect"] = expect
    return inst


def grid_road(cols, rows, spacing):
    nodes = [((x + 0.5) * spacing, (y + 0.5) * spacing)
             for y in range(rows) for x in range(cols)]
    idx = lambda x, y: x + y * cols  # noqa: E731
    edges = []
    for y in range(rows):
        for x in range(cols):
            if x + 1 < cols:
                edges.append((idx(x, y), idx(x + 1, y), spacing))
            if y + 1 < rows:
                edges.append((idx(x, y), idx(x, y + 1), spacing))
    return nodes, edges, idx


def corridor_street():
    """6x4 street grid, one dominant east-west corridor on row 1 plus a weaker
    north-south corridor; 2 growth rounds exercise peel and novelty decay."""
    nodes, edges, idx = grid_road(6, 4, 100.0)
    flows = []
    for (a, b, _c) in edges:
        ya, yb = a // 6, b // 6
        xa, xb = a % 6, b % 6
        f = 5.0
        if ya == 1 and yb == 1:
            f = 100.0 - 3.0 * min(xa, xb)   # east-west trunk, mild gradient
        elif xa == 3 and xb == 3:
            f = 40.0
        flows.append(f)
    demand = [1.0] * len(nodes)
    return corridor_common(
        "corridor-street", "trunk + cross street, 2 rounds",
        nodes, edges, flows, demand, "Ridership", 2, 2000.0)


def corridor_bridge():
    """A line of busy nodes with a 2-node zero-demand park in the middle (must
    be crossed) and a low-demand tail at the east end (must be discarded, never
    a terminus). Node 0..9 in a row."""
    n = 10
    nodes = [((i + 0.5) * 100.0, 50.0) for i in range(n)]
    edges = [(i, i + 1, 100.0) for i in range(n - 1)]
    flows = [50.0] * (n - 1)
    demand = [1.0] * n
    demand[4] = 0.0
    demand[5] = 0.0        # the park: bridged, redeemed by node 6+
    demand[8] = 0.0
    demand[9] = 0.0        # the tail: bridged but never redeemed -> discarded
    return corridor_common(
        "corridor-bridge", "low-demand bridge crossed; unredeemed tail discarded",
        nodes, edges, flows, demand, "Ridership", 1, 5000.0,
        demand_floor=0.5,
        expect={"tail_discarded": True})


def corridor_maxlen():
    """The trunk is longer than maxRouteLength: growth must stop at the limit
    and report m_HitMaxLength. Semantics pinned by this instance: the flag
    fires only when the accumulated length REACHES the limit (loop condition);
    an extension that would merely overshoot is counted as blocked-by-length
    instead — so the limit is a multiple of the edge length here (4 x 100)."""
    n = 12
    nodes = [((i + 0.5) * 100.0, 50.0) for i in range(n)]
    edges = [(i, i + 1, 100.0) for i in range(n - 1)]
    flows = [50.0] * (n - 1)
    demand = [1.0] * n
    return corridor_common(
        "corridor-maxlen", "growth stops at the length limit",
        nodes, edges, flows, demand, "Ridership", 1, 400.0,
        expect={"hit_max_length": True})


def corridor_coverage():
    """Coverage objective over two separated busy pockets: seed novelty bias
    must steer round 2 to the untouched pocket. Also marks the middle edges
    unable to host stops."""
    nodes, edges, idx = grid_road(8, 3, 100.0)
    flows = []
    cannot = []
    for (a, b, _c) in edges:
        xa, xb = a % 8, b % 8
        ya, yb = a // 8, b // 8
        west = xa <= 3 and xb <= 3
        east = xa >= 5 and xb >= 5
        f = 60.0 if (west and ya == 1 and yb == 1) else \
            55.0 if (east and ya == 1 and yb == 1) else 5.0
        flows.append(f)
        cannot.append(xa in (4,) and xb in (4, 5) and ya == 0 and yb == 0)
    demand = [1.0] * len(nodes)
    return corridor_common(
        "corridor-coverage", "coverage objective spreads to the second pocket",
        nodes, edges, flows, demand, "Coverage", 3, 1500.0,
        cannot_host=cannot)


# --------------------------------------------------------------- lineset (S7)

def lineset_common(name, comment, zones, flows, candidates, k,
                   existing_lines=None, existing_stops=None, seed=None):
    inst = {
        "kind": "lineset",
        "name": name,
        "comment": comment,
        "data": {
            "walk_radius_b32": f32_bits(250.0),
            "board_penalty_b32": f32_bits(5.0),
            "transfer_discount_b32": f32_bits(0.6),
            "max_travel_seconds_b32": f32_bits(3600.0),
            "switch_margin_b32": f32_bits(60.0),
            "zone_stop_reach_b32": f32_bits(500.0),
            "zone_x_b32": f32_list([z[0] for z in zones]),
            "zone_z_b32": f32_list([z[1] for z in zones]),
            "flows": [
                {"origin": o, "dest": d, "weight_b32": f32_bits(w)}
                for (o, d, w) in flows
            ],
            "existing_stop_x_b32": f32_list([s[0] for s in (existing_stops or [])]),
            "existing_stop_z_b32": f32_list([s[1] for s in (existing_stops or [])]),
            "existing_lines": existing_lines or [],
            "candidates": candidates,
            "max_accept": k,
        },
    }
    if seed is not None:
        inst["seed"] = seed
    return inst


def candidate(stops, wait, speed, flow):
    return {
        "stop_x_b32": f32_list([s[0] for s in stops]),
        "stop_z_b32": f32_list([s[1] for s in stops]),
        "expected_wait_b32": f32_bits(wait),
        "speed_b32": f32_bits(speed),
        "captured_flow_b32": f32_bits(flow),
    }


def lineset_feeder():
    """Trunk-and-feeder complementarity: the heavy A->B pair (weight 100) is
    carried only by L1+L2 together (one transfer at M). L1 and L2 alone each
    unlock a small local pair (10). L3 alone unlocks 30. With K=2 greedy takes
    L3 first and never reaches {L1, L2}, whose set value 10+10+100*0.6 = 80
    beats greedy's 40. Distances are chosen so no unintended walk edges exist
    (all stop gaps > 250 m) and every zone maps onto exactly the stops meant
    for it (reach 500 m)."""
    # Geometry (metres). Corridor A -- M -- B along x; C/D on a separate y row.
    A = (0.0, 0.0); A2 = (2000.0, 0.0)          # L1: A -> M', M
    M = (4000.0, 0.0)
    B2 = (6000.0, 0.0); B = (8000.0, 0.0)       # L2: M -> B', B
    C = (0.0, 4000.0); D = (3000.0, 4000.0)     # L3: C -> D
    zones = [A, A2, M, B2, B, C, D]
    flows = [
        (0, 4, 100.0),   # A -> B, the joint pair
        (0, 1, 10.0),    # A -> A2, L1-local
        (2, 3, 10.0),    # M -> B2, L2-local
        (5, 6, 30.0),    # C -> D, L3-local
    ]
    candidates = [
        candidate([A, A2, M], 200.0, 9.0, 3.0),   # L1
        candidate([M, B2, B], 200.0, 9.0, 2.0),   # L2
        candidate([C, D], 200.0, 9.0, 1.0),       # L3
    ]
    inst = lineset_common(
        "lineset-feeder",
        "C7.4 counterexample: greedy(K=2) picks {L3, L1} worth 40; "
        "optimum {L1, L2} worth 80",
        zones, flows, candidates, 2)
    inst["expect"] = {"greedy_set_is_optimal": False}
    return inst


def lineset_staged():
    """C7.5 counterexample: one alignment of length 2000 m. The S5 stage
    (PlanCallingPoints, spacing 800 -> intervals round(2000/800)=3 -> stops at
    0, 666.67, 1333.33, 2000) yields candidate 'staged'. The alternative
    'joint' places the same number of stops at 0, 500, 1500, 2000, putting a
    middle stop within zone reach of the heavy zone at (500, 480). Distances:
    (500,480)->stop(500,0) = 480 < 500, ->stop(666.67,0) = 508 > 500,
    ->stop(0,0) = 693 > 500 — so only the joint variant reaches it. Enumeration
    over the two shows the staged stop set is strictly dominated for the S7
    objective."""
    z_end_a = (0.0, 0.0)
    z_heavy = (500.0, 480.0)     # within 500 m of a stop at x=500 only
    z_end_b = (2000.0, 0.0)
    zones = [z_end_a, z_heavy, z_end_b]
    flows = [
        (0, 2, 20.0),    # end-to-end, served by both variants
        (1, 2, 60.0),    # heavy zone -> end, served only if a stop sits near x=500
    ]
    # The staged stops are EXACTLY what PlanCallingPoints emits for length 2000 /
    # spacing 800 (intervals = 3): length * i / intervals in float32 arithmetic.
    def rf32(x):
        return struct.unpack("<f", struct.pack("<f", x))[0]

    staged_offsets = [rf32(rf32(2000.0 * i) / 3.0) for i in range(3)] + [2000.0]
    staged = candidate([(o, 0.0) for o in staged_offsets], 200.0, 9.0, 2.0)
    joint = candidate([(0.0, 0.0), (500.0, 0.0), (1500.0, 0.0), (2000.0, 0.0)],
                      200.0, 9.0, 1.0)
    inst = lineset_common(
        "lineset-staged",
        "C7.5 counterexample: the stop set produced by the S5 spacing rule is "
        "strictly dominated by an alternative stop set on the same alignment",
        zones, flows, [staged, joint], 1)
    inst["data"]["staged_candidate"] = 0
    inst["data"]["spacing_b32"] = f32_bits(800.0)
    inst["data"]["alignment_length_b32"] = f32_bits(2000.0)
    inst["expect"] = {"staged_dominated": True, "staged_stops_match_plan": True}
    return inst


def lineset_random():
    rng = random.Random(20260903)
    zones = []
    for gy in range(3):
        for gx in range(3):
            zones.append((gx * 1200.0, gy * 1200.0))
    flows = []
    for _ in range(10):
        o = rng.randrange(len(zones))
        d = rng.randrange(len(zones))
        if o == d:
            continue
        flows.append((o, d, float(rng.randrange(5, 60))))
    # Four candidate lines along rows/columns, stops on zone centres.
    def line(cells, flow):
        return candidate([zones[c] for c in cells], 200.0, 9.0, flow)
    candidates = [
        line([0, 1, 2], 4.0),
        line([6, 7, 8], 3.0),
        line([0, 3, 6], 2.0),
        line([2, 5, 8], 1.0),
    ]
    return lineset_common(
        "lineset-random", "seeded 3x3 zone grid, 4 candidates, K=2",
        zones, flows, candidates, 2, seed=20260903)


def lineset_tie():
    """Two candidates with identical credited demand: exercises the
    tie/decision-sensitivity reporting of the acceptance order."""
    zones = [(0.0, 0.0), (1000.0, 0.0), (0.0, 2000.0), (1000.0, 2000.0)]
    flows = [(0, 1, 50.0), (2, 3, 50.0)]
    candidates = [
        candidate([(0.0, 0.0), (1000.0, 0.0)], 200.0, 9.0, 2.0),
        candidate([(0.0, 2000.0), (1000.0, 2000.0)], 200.0, 9.0, 2.0),
    ]
    inst = lineset_common(
        "lineset-tie", "equal credited demand and equal flow: tie must be reported",
        zones, flows, candidates, 1)
    inst["expect"] = {"tie_affected": True}
    return inst


def main():
    os.makedirs(OUT, exist_ok=True)
    instances = [
        sites_greedy_gap(),
        sites_random("sites-random-16", 16, 16, 20260901, 3, 6),
        sites_random("sites-random-24", 24, 24, 20260902, 5, 8),
        sites_plateau(),
        lattice_path_rail(),
        lattice_path_tie(),
        calling_points_cases(),
        mode_choice_sweep(),
        corridor_street(),
        corridor_bridge(),
        corridor_maxlen(),
        corridor_coverage(),
        heatmap_point(),
        heatmap_grid_plumbing(),
        order_stats(),
        lineset_feeder(),
        lineset_staged(),
        lineset_random(),
        lineset_tie(),
    ]
    for inst in instances:
        path = os.path.join(OUT, inst["name"] + ".json")
        write_instance(path, inst)
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
