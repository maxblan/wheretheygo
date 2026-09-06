"""The game's fleet arithmetic and the mod's fleet rule, re-derived from
docs/formal-specification.md §5 v3 / §7e (register A5.5, A6.8, A8.3) without mod
code, in the exact arithmetic the spec pins: binary32 where the mod computes in
float (evaluator.f32), binary64 where it computes in double (Python floats).

Game facts (decompiled TransportLineSystem, VehicleCountSection,
RouteModifierInitializeSystem, RouteUtils, 2026-09-06):
  fleet(I, T)     = max(1, round-half-even(fl32(T / max(1, I))))
  interval(T, v)  = fl32(T / max(1, v))
  slider: interval = I; interval += x; interval += interval·y, with the modifier
          delta lerped over [DeltaMin, DeltaMax] by the slider position; the two
          ends of the slider bound the fleet a line may run.
Mod rules:
  fleet for a load L at target fill t and capacity c:  max(1, ⌈fl32(L / fl32(t·c))⌉)
  fleet for B boardings a day under ceiling u:           max(1, ⌈(B·T) / ((u·D)·c)⌉)  (double)
  utilisation(B, h, c) = fl32( (B·2) / (((D / h)·2)·c) )   (double, one rounding)
"""

from __future__ import annotations

import math

from common.canonical import bits_to_f32
from evaluator import f32

DAY = f32.r(262144.0 / 60.0)   # movement seconds in a game day, as the mod's float const
RIDES_PER_JOURNEY = 2.0
CRUISE = {"Bus": 9.0, "Tram": 12.0, "Metro": 18.0, "Train": 28.0, "Ferry": 10.0}
DEFAULT_STOP = 15.0
DEFAULT_ACCEL = 1.5
MODES = ["Bus", "Metro", "Tram", "Train", "Ferry"]
LADDER = {"Road": ["Bus", "Tram"], "Rail": ["Train"], "Metro": ["Metro"], "Water": ["Ferry"]}
NETWORK_OF = {"Bus": "Road", "Tram": "Road", "Train": "Rail", "Metro": "Metro", "Ferry": "Water"}


def parse_facts(data: dict) -> dict:
    facts = {}
    for mode in MODES:
        raw = data.get("facts", {}).get(mode)
        facts[mode] = {
            "capacity": bits_to_f32(raw["capacity_b32"]) if raw else 0.0,
            "prefab_interval": bits_to_f32(raw["prefab_interval_b32"]) if raw else 0.0,
            "stop_duration": bits_to_f32(raw["stop_duration_b32"]) if raw else 0.0,
            "acceleration": bits_to_f32(raw["acceleration_b32"]) if raw and "acceleration_b32" in raw else 0.0,
            "braking": bits_to_f32(raw["braking_b32"]) if raw and "braking_b32" in raw else 0.0,
        }
    return facts


def parse_policy(data: dict) -> dict:
    raw = data.get("policy")
    if not raw or not raw.get("known", False):
        return {"known": False, "mode": "Relative", "delta_min": 0.0, "delta_max": 0.0}
    return {"known": True, "mode": raw["mode"],
            "delta_min": bits_to_f32(raw["delta_min_b32"]), "delta_max": bits_to_f32(raw["delta_max_b32"])}


def game_fleet(interval: float, round_trip: float) -> int:
    q = f32.div(round_trip, max(1.0, interval))
    return max(1, int(round(q)))   # Python round = half to even, as Math.Round


def game_interval(round_trip: float, vehicles: int) -> float:
    return f32.div(round_trip, float(max(1, vehicles)))


def interval_at(prefab_interval: float, mode: str, delta: float) -> float:
    x, y = 0.0, 0.0
    if mode == "Absolute":
        x = delta
    elif mode == "InverseRelative":
        y = f32.sub(f32.div(1.0, max(f32.r(0.001), f32.add(1.0, delta))), 1.0)
    else:
        y = delta
    value = prefab_interval
    value = f32.add(value, x)
    value = f32.add(value, f32.mul(value, y))
    return value


def fleet_span(policy: dict, prefab_interval: float, round_trip: float) -> tuple[int, int | None]:
    """(min, max); max None = unbounded (policy unknown or no line prefab)."""
    if not policy["known"] or prefab_interval <= 0.0:
        return 1, None
    lo_delta = f32.add(policy["delta_min"], f32.mul(0.0, f32.sub(policy["delta_max"], policy["delta_min"])))
    hi_delta = f32.add(policy["delta_min"], f32.mul(1.0, f32.sub(policy["delta_max"], policy["delta_min"])))
    a = game_fleet(interval_at(prefab_interval, policy["mode"], lo_delta), round_trip)
    b = game_fleet(interval_at(prefab_interval, policy["mode"], hi_delta), round_trip)
    return min(a, b), max(a, b)


def clamp(vehicles: int, lo: int, hi: int | None) -> int:
    v = max(lo, vehicles)
    return v if hi is None else min(hi, v)


def fleet_for_load(peak_riders: float, capacity: float, target_load: float) -> int:
    if capacity <= 0.0 or target_load <= 0.0 or peak_riders <= 0.0:
        return 1
    return max(1, math.ceil(f32.div(peak_riders, f32.mul(target_load, capacity))))


def fleet_for_demand(riders: float, round_trip: float, capacity: float, ceiling: float) -> int:
    if riders <= 0.0 or round_trip <= 0.0 or capacity <= 0.0 or ceiling <= 0.0:
        return 1
    needed = (riders * round_trip) / ((ceiling * DAY) * capacity)   # double, in this bracketing
    return max(1, math.ceil(needed))


DAY_SHARE = f32.r(16.0 / 24.0)


def fleet_for_demand_by_period(riders: float, day: float, night: float, round_trip: float, capacity: float, ceiling: float) -> int:
    """The busier period binds (A6.10): the largest of the whole-day, day and night requirements."""
    whole = fleet_for_demand(riders, round_trip, capacity, ceiling)
    d = fleet_for_demand(day, round_trip, capacity * DAY_SHARE, ceiling)
    n = fleet_for_demand(night, round_trip, capacity * f32.sub(1.0, DAY_SHARE), ceiling)
    return max(whole, d, n)


def utilisation(riders: float, headway: float, capacity: float) -> float:
    if headway <= 0.0 or capacity <= 0.0:
        return 0.0
    boardings = riders * RIDES_PER_JOURNEY
    seats = DAY / headway * 2.0 * capacity
    return f32.r(boardings / seats)


def utilisation_in_period(riders: float, headway: float, capacity: float, share: float) -> float:
    if share <= 0.0:
        return 0.0
    return utilisation(riders, headway, capacity * share)


def plan_fleet(riders: float, day: float, night: float, round_trip: float, capacity: float, lo: int, hi: int | None, ceiling: float):
    """(vehicles, headway, utilisation, day_utilisation, night_utilisation) within the span."""
    v = clamp(fleet_for_demand_by_period(riders, day, night, round_trip, capacity, ceiling), lo, hi)
    h = game_interval(round_trip, v)
    return (v, h, utilisation(riders, h, capacity),
            utilisation_in_period(day, h, capacity, DAY_SHARE),
            utilisation_in_period(night, h, capacity, f32.sub(1.0, DAY_SHARE)))


def overloads(plan, ceiling: float) -> bool:
    return plan[2] > ceiling or plan[3] > ceiling or plan[4] > ceiling


def stop_duration(facts: dict, mode: str) -> float:
    d = facts[mode]["stop_duration"]
    return d if d > 0.0 else f32.r(DEFAULT_STOP)


def delay_per_stop(facts: dict, mode: str) -> float:
    fx = facts[mode]
    speed = f32.r(CRUISE[mode])
    a = fx["acceleration"] if fx["acceleration"] > 0.0 else f32.r(DEFAULT_ACCEL)
    b = fx["braking"] if fx["braking"] > 0.0 else f32.r(DEFAULT_ACCEL)
    physics = f32.add(f32.add(stop_duration(facts, mode), f32.div(speed, f32.mul(2.0, a))), f32.div(speed, f32.mul(2.0, b)))
    return max(physics, f32.r(DEFAULT_STOP))


def round_trip(loop_metres: float, loop_stops: int, cruise: float, delay: float) -> float:
    return f32.add(f32.div(loop_metres, max(1.0, cruise)), f32.mul(float(max(0, loop_stops)), delay))


def choose_mode(network: str, riders: float, day: float, night: float, facts: dict, policy: dict, round_trip_of, ceiling: float = 1.0):
    """The ladder (§5 v3): (ok, mode, vehicles, lo, hi, headway, utilisation, day_util, night_util)."""
    ladder = LADDER[network]
    mode, plan, any_vehicle = ladder[0], (0, 0.0, 0.0, 0.0, 0.0), False
    lo, hi = 1, None
    for option in ladder:
        cap = facts[option]["capacity"]
        if cap <= 0.0:
            continue
        any_vehicle = True
        mode = option
        rt = round_trip_of(option)
        lo, hi = fleet_span(policy, facts[option]["prefab_interval"], rt)
        plan = plan_fleet(riders, day, night, rt, cap, lo, hi, ceiling)
        if not overloads(plan, ceiling):
            return True, mode, plan[0], lo, hi, plan[1], plan[2], plan[3], plan[4]
    return any_vehicle, mode, plan[0], lo, hi, plan[1], plan[2], plan[3], plan[4]
