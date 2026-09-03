"""C1.1b: the gathered heatmap terms of a REAL city, recomputed independently.

A `heatmap_grid` instance (written by the mod's verification export) carries the
Burst job's own inputs — masks, component labels, the population raster and the
seven bucketed point sets — together with the terms the job produced for a
sample of cells. This module recomputes those cells from the specification
(docs/formal-specification.md §7.2) in exact binary32 semantics and compares
bit-for-bit.

The job cannot run outside the game, so the comparison crosses a file rather
than a function call; what makes it a real check is that nothing here is derived
from the job's code — the accumulation order, the kernel, the gating and the
bucket sweep are re-derived from the written specification, and any divergence
shows up as a differing bit pattern.

Also verified, as properties rather than by re-execution:
  * unbuildable cells produce all-zero terms (the job's gate),
  * buildable and component labelling agree (component > 0 iff buildable),
  * C1.4: no source outside the scored cell's component contributes to the
    component-gated terms — implied by the bit-exact match, and additionally
    reported per cell as the number of sources the gate rejected.
"""

from __future__ import annotations

import math

from common.canonical import bits_to_f32, f32_bits
from evaluator import f32
from evaluator.heatmap import accumulate_stop, distance_f32

EDGE_ACCESS_COEFFICIENT = 0.06
NODE_ACCESS_COEFFICIENT = 0.15
REFERENCE_ACCESS_RADIUS = 120.0
MAX_PENALTY = 1.5


def saturate(v: float) -> float:
    return 0.0 if v < 0.0 else (1.0 if v > 1.0 else v)


def triangular(dist: float, radius: float) -> float:
    return f32.sub(1.0, f32.div(dist, radius))


class Points:
    """One bucketed point set, as SuitabilityInputs.BuildBuckets lays it out."""

    def __init__(self, node: dict):
        self.x = [bits_to_f32(b) for b in node["x_b32"]]
        self.z = [bits_to_f32(b) for b in node["z_b32"]]
        self.w = [bits_to_f32(b) for b in node["w_b32"]]
        self.offsets = list(node["offsets"])
        self.counts = list(node["counts"])


class Grid:
    def __init__(self, data: dict):
        self.grid_x = data["grid_x"]
        self.grid_y = data["grid_y"]
        self.bucket_x = data["bucket_x"]
        self.bucket_y = data["bucket_y"]
        self.world_min_x = bits_to_f32(data["world_min_x_b32"])
        self.world_min_z = bits_to_f32(data["world_min_z_b32"])
        self.tile = bits_to_f32(data["tile_size_b32"])
        self.bucket = bits_to_f32(data["bucket_size_b32"])
        self.catchment = bits_to_f32(data["catchment_b32"])
        self.access_radius = bits_to_f32(data["access_radius_b32"])
        self.interchange = bits_to_f32(data["interchange_b32"])
        self.pop_cell_x = bits_to_f32(data["population_cell_x_b32"])
        self.pop_cell_z = bits_to_f32(data["population_cell_z_b32"])
        self.pop_tex_x = data["population_tex_x"]
        self.pop_tex_y = data["population_tex_y"]
        self.population = [bits_to_f32(b) for b in data["population_b32"]]
        self.components = list(data["components"])
        self.buildable = list(data["buildable"])
        self.stops = Points(data["stops"])
        self.other_stops = Points(data["other_stops"])
        self.nodes = Points(data["nodes"])
        self.edges = Points(data["edges"])
        self.jobs = Points(data["jobs"])
        self.future_homes = Points(data["future_homes"])
        self.future_jobs = Points(data["future_jobs"])

    # SuitabilityInputs.WorldToCell: floor of the scaled offset, CLAMPED to the
    # grid (an off-map position folds onto the border, it is not rejected).
    def world_to_cell(self, px: float, pz: float, cell_size: float,
                      grid_x: int, grid_y: int) -> tuple[int, int]:
        cx = int(math.floor(f32.div(f32.sub(px, self.world_min_x), cell_size)))
        cz = int(math.floor(f32.div(f32.sub(pz, self.world_min_z), cell_size)))
        return (min(max(cx, 0), grid_x - 1), min(max(cz, 0), grid_y - 1))

    def component_at(self, px: float, pz: float) -> int:
        cx, cz = self.world_to_cell(px, pz, self.tile, self.grid_x, self.grid_y)
        return self.components[cx + cz * self.grid_x]

    def cell_centre(self, index: int) -> tuple[float, float]:
        x = index % self.grid_x
        y = index // self.grid_x
        cx = f32.add(self.world_min_x, f32.mul(f32.add(float(x), 0.5), self.tile))
        cz = f32.add(self.world_min_z, f32.mul(f32.add(float(y), 0.5), self.tile))
        return cx, cz

    # --------------------------------------------------------------- terms

    def sum_population(self, cx: float, cz: float, radius: float,
                       component: int) -> tuple[float, int]:
        map_w = f32.mul(self.pop_cell_x, float(self.pop_tex_x))
        map_h = f32.mul(self.pop_cell_z, float(self.pop_tex_y))
        map_min_x = f32.mul(-map_w, 0.5)
        map_min_z = f32.mul(-map_h, 0.5)

        def clamp_to_texture(px: float, pz: float) -> tuple[int, int]:
            rx = f32.div(f32.sub(px, map_min_x), self.pop_cell_x)
            rz = f32.div(f32.sub(pz, map_min_z), self.pop_cell_z)
            ix = min(max(int(math.floor(rx)), 0), self.pop_tex_x - 1)
            iz = min(max(int(math.floor(rz)), 0), self.pop_tex_y - 1)
            return ix, iz

        min_x, min_z = clamp_to_texture(f32.sub(cx, radius), f32.sub(cz, radius))
        max_x, max_z = clamp_to_texture(f32.add(cx, radius), f32.add(cz, radius))

        total = 0.0
        gated = 0
        for gy in range(min_z, max_z + 1):
            for gx in range(min_x, max_x + 1):
                px = f32.add(map_min_x, f32.mul(f32.add(float(gx), 0.5), self.pop_cell_x))
                pz = f32.add(map_min_z, f32.mul(f32.add(float(gy), 0.5), self.pop_cell_z))
                dist = distance_f32(px, pz, cx, cz)
                if dist > radius:
                    continue
                if self.component_at(px, pz) != component:
                    gated += 1
                    continue
                value = self.population[gx + gy * self.pop_tex_x]
                total = f32.add(total, f32.mul(value, triangular(dist, radius)))
        return total, gated

    def _sweep(self, points: Points, cx: float, cz: float, radius: float):
        """Bucket neighbourhood in the job's own iteration order: dy outer, dx
        inner, then insertion order within the bucket."""
        radius_tiles = int(math.ceil(f32.div(radius, self.bucket)))
        base_x, base_z = self.world_to_cell(cx, cz, self.bucket,
                                            self.bucket_x, self.bucket_y)
        for dy in range(-radius_tiles, radius_tiles + 1):
            gy = base_z + dy
            if gy < 0 or gy >= self.bucket_y:
                continue
            for dx in range(-radius_tiles, radius_tiles + 1):
                gx = base_x + dx
                if gx < 0 or gx >= self.bucket_x:
                    continue
                bucket = gx + gy * self.bucket_x
                start = points.offsets[bucket]
                for i in range(points.counts[bucket]):
                    yield start + i

    def sum_points(self, points: Points, cx: float, cz: float, radius: float,
                   component: int) -> tuple[float, int]:
        total = 0.0
        gated = 0
        for i in self._sweep(points, cx, cz, radius):
            dist = distance_f32(points.x[i], points.z[i], cx, cz)
            if dist > radius:
                continue
            if self.component_at(points.x[i], points.z[i]) != component:
                gated += 1
                continue
            total = f32.add(total, f32.mul(points.w[i], triangular(dist, radius)))
        return total, gated

    def sum_coverage(self, cx: float, cz: float) -> float:
        penalty = 0.0
        for i in self._sweep(self.stops, cx, cz, self.catchment):
            dist = distance_f32(self.stops.x[i], self.stops.z[i], cx, cz)
            penalty, _, _ = accumulate_stop(dist, self.stops.w[i], True,
                                            self.catchment, self.interchange,
                                            penalty, 0.0, 0.0)
        return min(max(penalty, 0.0), f32.r(MAX_PENALTY))

    def sum_other_modes(self, cx: float, cz: float) -> tuple[float, float]:
        interchange = 0.0
        cross = 0.0
        for i in self._sweep(self.other_stops, cx, cz, self.catchment):
            dist = distance_f32(self.other_stops.x[i], self.other_stops.z[i], cx, cz)
            _, interchange, cross = accumulate_stop(
                dist, self.other_stops.w[i], False, self.catchment,
                self.interchange, 0.0, interchange, cross)
        return interchange, cross

    def accessibility(self, cx: float, cz: float) -> float:
        node_weight = 0.0
        edge_weight = 0.0
        radius = self.access_radius
        radius_tiles = int(math.ceil(f32.div(radius, self.bucket)))
        base_x, base_z = self.world_to_cell(cx, cz, self.bucket,
                                            self.bucket_x, self.bucket_y)
        # Nodes and edges are swept per bucket, nodes first — the job reads both
        # sets inside the same bucket loop.
        for dy in range(-radius_tiles, radius_tiles + 1):
            gy = base_z + dy
            if gy < 0 or gy >= self.bucket_y:
                continue
            for dx in range(-radius_tiles, radius_tiles + 1):
                gx = base_x + dx
                if gx < 0 or gx >= self.bucket_x:
                    continue
                bucket = gx + gy * self.bucket_x
                start = self.nodes.offsets[bucket]
                for i in range(self.nodes.counts[bucket]):
                    dist = distance_f32(self.nodes.x[start + i],
                                        self.nodes.z[start + i], cx, cz)
                    if dist <= radius:
                        node_weight = f32.add(node_weight, triangular(dist, radius))
                start = self.edges.offsets[bucket]
                for i in range(self.edges.counts[bucket]):
                    dist = distance_f32(self.edges.x[start + i],
                                        self.edges.z[start + i], cx, cz)
                    if dist <= radius:
                        edge_weight = f32.add(edge_weight, triangular(dist, radius))

        ref = f32.r(REFERENCE_ACCESS_RADIUS)
        scale = f32.div(f32.mul(ref, ref), f32.mul(radius, radius))
        access = f32.mul(
            f32.add(f32.mul(edge_weight, f32.r(EDGE_ACCESS_COEFFICIENT)),
                    f32.mul(node_weight, f32.r(NODE_ACCESS_COEFFICIENT))),
            scale)
        return saturate(access)

    def terms(self, index: int) -> tuple[dict, int]:
        if self.buildable[index] == 0:
            return ({"demand": 0.0, "jobs": 0.0, "coverage": 0.0, "access": 0.0,
                     "future": 0.0, "interchange": 0.0, "cross": 0.0}, 0)
        cx, cz = self.cell_centre(index)
        component = self.components[index]
        demand, g1 = self.sum_population(cx, cz, self.catchment, component)
        jobs, g2 = self.sum_points(self.jobs, cx, cz, self.catchment, component)
        home, g3 = self.sum_points(self.future_homes, cx, cz, self.catchment, component)
        work, g4 = self.sum_points(self.future_jobs, cx, cz, self.catchment, component)
        interchange, cross = self.sum_other_modes(cx, cz)
        return ({
            "demand": demand,
            "jobs": jobs,
            "coverage": self.sum_coverage(cx, cz),
            "access": self.accessibility(cx, cz),
            "future": f32.add(home, work),
            "interchange": interchange,
            "cross": cross,
        }, g1 + g2 + g3 + g4)


def check(instance: dict, solution: dict) -> dict:
    grid = Grid(instance["data"])
    total_cells = grid.grid_x * grid.grid_y

    # Structural invariant: the mask and the flood fill must agree about what is
    # buildable, or every component gate below rests on a different map.
    mismatched = sum(
        1 for i in range(min(total_cells, len(grid.buildable), len(grid.components)))
        if (grid.buildable[i] != 0) != (grid.components[i] > 0))

    matched = 0
    mismatches = []
    gated_total = 0
    unbuildable_checked = 0
    for cell in solution["cells"]:
        index = cell["index"]
        expected, gated = grid.terms(index)
        gated_total += gated
        if grid.buildable[index] == 0:
            unbuildable_checked += 1
        got = {
            "demand": cell["demand_b32"], "jobs": cell["jobs_b32"],
            "coverage": cell["coverage_b32"], "access": cell["access_b32"],
            "future": cell["future_b32"], "interchange": cell["interchange_b32"],
            "cross": cell["cross_b32"],
        }
        bad = {k: (f32_bits(v), got[k]) for k, v in expected.items()
               if f32_bits(v) != got[k]}
        if bad:
            if len(mismatches) < 5:
                mismatches.append({"index": index, "terms": {
                    k: [str(a), str(b)] for k, (a, b) in bad.items()}})
        else:
            matched += 1

    return {
        "ok": not mismatches and mismatched == 0,
        "cells_checked": len(solution["cells"]),
        "cells_matched": matched,
        "mismatches": mismatches,
        "buildable_component_mismatches": mismatched,
        "unbuildable_cells_checked": unbuildable_checked,
        "sources_rejected_by_component_gate": gated_total,
    }
