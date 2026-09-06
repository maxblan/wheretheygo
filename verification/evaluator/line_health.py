"""S9 line health (docs/formal-specification.md §7e, register A8), re-derived
without mod code: the rolling window from the exported readings (active readings
only, nearest-rank planning load), the city's upper-median empty bar, the ladder
over the game's fleet span for each mode's round trip, the fleet the load and the
routed demand require, the utilisation at that fleet, the period utilisations and
the schedule rule, and the verdict tree in its stated order. Game = subject =
evaluator on every field, per line.

Arithmetic follows the spec's pinning: means are binary32 sums in recording order,
the load quantile is an integer, the fleet rules are evaluator.fleet.
"""

from __future__ import annotations

import math

from common.canonical import bits_to_f32, f32_bits
from evaluator import f32
from evaluator import fleet

MIN_READINGS = 4
QUANTILE = f32.r(0.9)
EMPTY_USAGE = f32.r(0.06)
EMPTY_SHARE = f32.r(0.35)
EMPTY_PEAK_ALLOWANCE = 3.0
DAY_SHARE = f32.r(16.0 / 24.0)
NIGHT_START = f32.r(11.0 / 12.0)
NIGHT_END = f32.r(0.25)

VERDICTS = ["Healthy", "FleetShort", "ModeUp", "SplitRoute", "Remove", "ModeDown", "FleetUp", "FleetDown", "Schedule"]


def is_night(t: float) -> bool:
    frac = f32.sub(t, math.floor(t))
    return frac < NIGHT_END or frac >= NIGHT_START


def quantile(values: list[int], q: float) -> int:
    if not values:
        return 0
    s = sorted(values)
    rank = math.ceil(f32.mul(q, float(len(s))))
    rank = max(1, min(len(s), rank))
    return s[rank - 1]


def window(line: dict, window_frames: int) -> dict:
    """The window as ApplyWindow rebuilds it: readings in frame order, evicted
    against the line's newest frame, means over the active readings."""
    samples = sorted(zip(line["frame"], line["passengers"], line["capacity"], line["vehicles"], line["tod"]), key=lambda s: s[0])
    if samples:
        newest = samples[-1][0]
        if newest > window_frames:
            cutoff = newest - window_frames
            samples = [s for s in samples if s[0] >= cutoff]
    active = [s for s in samples if s[2] > 0]
    result = {"readings": len(samples), "samples": len(active)}
    if not active:
        result.update({"usage": 0.0, "peak": 0.0, "load": 0, "max": 0, "day_usage": 0.0, "night_usage": 0.0})
        return result
    usage, peak = 0.0, 0.0
    for _f, p, c, _v, _t in active:
        u = f32.div(float(p), float(c))
        usage = f32.add(usage, u)
        peak = max(peak, u)
    result["usage"] = f32.div(usage, float(len(active)))
    result["peak"] = peak
    result["load"] = quantile([s[1] for s in active], QUANTILE)
    result["max"] = max(s[1] for s in active)
    return result


def parse_line(raw: dict) -> dict:
    return {
        "id": int(raw["id"]), "mode": raw["mode"], "stops": int(raw["stops"]),
        "loop": bits_to_f32(raw["loop_metres_b32"]), "stable": bits_to_f32(raw["round_trip_b32"]),
        "target_interval": bits_to_f32(raw["target_interval_b32"]),
        "vehicles": int(raw["vehicles"]), "passengers": int(raw["passengers"]), "capacity": int(raw["capacity"]),
        "require": bool(raw["require_vehicles"]), "not_enough": bool(raw["not_enough_vehicles"]),
        "schedule": raw["schedule"],
        "riders": bits_to_f32(raw["riders_b32"]), "riders_day": bits_to_f32(raw["riders_day_b32"]),
        "riders_night": bits_to_f32(raw["riders_night_b32"]),
        "frame": [int(f) for f in raw["sample_frame"]],
        "passengers_s": list(raw["sample_passengers"]), "capacity_s": list(raw["sample_capacity"]),
        "vehicles_s": list(raw["sample_vehicles"]),
        "tod": [bits_to_f32(b) for b in raw["sample_time_of_day_b32"]],
    }


def upper_median(values: list[float]) -> float:
    if not values:
        return 0.0
    s = sorted(values)
    return s[len(s) // 2]


def advise(current: str, day: float, night: float, floor: float) -> str:
    """Existing lines: both periods under the floor is not a schedule question."""
    if floor <= 0.0 or (day < floor and night < floor):
        return current
    return recommend(day, night, floor)


def recommend(day: float, night: float, floor: float) -> str:
    if floor <= 0.0:
        return "DayAndNight"
    day_ok, night_ok = day >= floor, night >= floor
    if day_ok and not night_ok:
        return "Day"
    if night_ok and not day_ok:
        return "Night"
    return "DayAndNight"


def assess(line: dict, win: dict, facts: dict, policy: dict, floor: float, ceiling: float, target_load: float,
           threshold: float) -> dict:
    has_window = win["samples"] >= MIN_READINGS
    usage = win["usage"] if has_window else (f32.div(float(line["passengers"]), float(line["capacity"])) if line["capacity"] > 0 else 0.0)
    peak = win["peak"] if has_window else usage
    load = win["load"] if has_window else line["passengers"]
    has_demand = line["riders"] >= 0.0
    cpv = line["capacity"] // line["vehicles"] if line["vehicles"] > 0 else 0

    def rung(mode: str) -> dict | None:
        cap = float(cpv) if (mode == line["mode"] and cpv > 0) else facts[mode]["capacity"]
        if cap <= 0.0:
            return None
        rt = line["stable"] if (mode == line["mode"] and line["stable"] > 0.0) else \
            fleet.round_trip(line["loop"], line["stops"], f32.r(fleet.CRUISE[mode]), fleet.delay_per_stop(facts, mode))
        lo, hi = fleet.fleet_span(policy, facts[mode]["prefab_interval"], rt)
        for_load = fleet.fleet_for_load(float(load), cap, target_load)
        for_demand = fleet.fleet_for_demand(line["riders"], rt, cap, ceiling) if has_demand else 1
        return {"mode": mode, "cap": cap, "rt": rt, "lo": lo, "hi": hi, "required": max(for_load, for_demand)}

    ladder = fleet.LADDER[fleet.NETWORK_OF[line["mode"]]]
    current = max(0, ladder.index(line["mode"]) if line["mode"] in ladder else 0)
    chosen, smallest, chosen_index, split = None, None, current, False
    for i, mode in enumerate(ladder):
        r = rung(mode)
        if r is None:
            continue
        if smallest is None:
            smallest = r
        chosen, chosen_index = r, i
        if r["hi"] is None or r["required"] <= r["hi"]:
            break
    else:
        if chosen is None:
            r = rung(line["mode"]) or {"mode": line["mode"], "cap": float(cpv), "rt": line["stable"], "lo": 1, "hi": None, "required": 1}
            r.update({"cap": float(cpv), "lo": 1, "hi": None, "required": max(1, line["vehicles"])})
            chosen, smallest, chosen_index = r, r, current
        else:
            split = True

    vehicles = fleet.clamp(chosen["required"], chosen["lo"], chosen["hi"])
    headway = fleet.game_interval(chosen["rt"], vehicles)
    if line["stable"] > 0.0 and line["target_interval"] > 0.0:
        game_target = fleet.game_fleet(line["target_interval"], line["stable"])
    else:
        game_target = max(1, line["vehicles"])
    util = fleet.utilisation(line["riders"], headway, chosen["cap"]) if has_demand else -1.0
    day_util = night_util = 0.0
    advice = line["schedule"]
    if has_demand:
        day_util = fleet.utilisation_in_period(line["riders_day"], headway, chosen["cap"], DAY_SHARE)
        night_util = fleet.utilisation_in_period(line["riders_night"], headway, chosen["cap"], f32.sub(1.0, DAY_SHARE))
        advice = advise(line["schedule"], day_util, night_util, floor)

    if line["not_enough"] or (line["require"] and line["vehicles"] < game_target):
        verdict = "FleetShort"
    elif chosen_index > current:
        verdict = "ModeUp"
    elif split:
        verdict = "SplitRoute"
    else:
        verdict = None
        measured_empty = usage <= threshold and peak <= f32.mul(threshold, EMPTY_PEAK_ALLOWANCE)
        if measured_empty and has_demand and smallest is not None:
            at_fewest = fleet.utilisation(line["riders"], fleet.game_interval(smallest["rt"], smallest["lo"]), smallest["cap"])
            if at_fewest < floor:
                verdict = "Remove"
        if verdict is None:
            if chosen_index < current:
                verdict = "ModeDown"
            elif vehicles > line["vehicles"]:
                verdict = "FleetUp"
            elif vehicles < line["vehicles"]:
                verdict = "FleetDown"
            else:
                verdict = "Schedule" if advice != line["schedule"] else "Healthy"

    return {
        "id": line["id"], "verdict": verdict, "mode": chosen["mode"], "fleet": vehicles,
        "fleet_min": chosen["lo"], "fleet_max": 0 if chosen["hi"] is None else chosen["hi"],
        "round_trip_b32": f32_bits(chosen["rt"]), "headway_b32": f32_bits(headway),
        "utilisation_b32": f32_bits(util), "day_utilisation_b32": f32_bits(day_util), "night_utilisation_b32": f32_bits(night_util),
        "advice": advice, "planning_load": load, "usage_b32": f32_bits(usage), "peak_usage_b32": f32_bits(peak),
        "target_vehicles": game_target, "window_samples": win["samples"] if has_window else 0,
    }


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    facts = fleet.parse_facts(data)
    policy = fleet.parse_policy(data)
    floor = bits_to_f32(data["utilisation_floor_b32"])
    ceiling = bits_to_f32(data["utilisation_ceiling_b32"])
    target_load = bits_to_f32(data["target_load_b32"])
    window_frames = int(data["window_frames"])
    lines = [parse_line(l) for l in data["lines"]]
    windows = {}
    for line in lines:
        windows[line["id"]] = window({"frame": line["frame"], "passengers": line["passengers_s"], "capacity": line["capacity_s"],
                                      "vehicles": line["vehicles_s"], "tod": line["tod"]}, window_frames)
    usages = []
    for line in lines:
        w = windows[line["id"]]
        has_window = w["samples"] >= MIN_READINGS
        usages.append(w["usage"] if has_window else (f32.div(float(line["passengers"]), float(line["capacity"])) if line["capacity"] > 0 else 0.0))
    median = upper_median(usages)
    threshold = min(EMPTY_USAGE, f32.mul(median, EMPTY_SHARE))
    expected = {line["id"]: assess(line, windows[line["id"]], facts, policy, floor, ceiling, target_load, threshold) for line in lines}

    subject = {int(v["id"]): v for v in solution["verdicts"]}
    game = None if data.get("verdicts") is None else {int(v["id"]): v for v in data["verdicts"]}
    fields = ["verdict", "mode", "fleet", "fleet_min", "fleet_max", "round_trip_b32", "headway_b32", "utilisation_b32",
              "day_utilisation_b32", "night_utilisation_b32", "advice", "planning_load", "usage_b32", "peak_usage_b32",
              "target_vehicles", "window_samples"]
    rows = []
    subject_ok = True
    game_ok = True
    for line in lines:
        e = expected[line["id"]]
        s = subject.get(line["id"])
        mismatch_s = [f for f in fields if s is None or s.get(f) != e[f]]
        row = {"id": line["id"], "expected": e, "subject_mismatch": mismatch_s}
        subject_ok = subject_ok and not mismatch_s
        if game is not None:
            g = game.get(line["id"])
            mismatch_g = [f for f in fields if g is None or g.get(f) != e[f]]
            row["game_mismatch"] = mismatch_g
            game_ok = game_ok and not mismatch_g
        rows.append(row)
    reference_ok = solution["median_usage_b32"] == f32_bits(median) and solution["empty_threshold_b32"] == f32_bits(threshold)
    if game is not None:
        reference_ok = reference_ok and data["median_usage_b32"] == f32_bits(median) and data["empty_threshold_b32"] == f32_bits(threshold)
    return {
        "lines": len(lines),
        "median_usage_b32": f32_bits(median),
        "empty_threshold_b32": f32_bits(threshold),
        "rows": rows,
        "subject_exact": subject_ok and reference_ok,
        "game_present": game is not None,
        "three_way_exact": game is not None and subject_ok and game_ok and reference_ok,
        "verdict_by_id": {str(line["id"]): expected[line["id"]]["verdict"] for line in lines},
        "ok": subject_ok and reference_ok and (game is None or game_ok),
    }
