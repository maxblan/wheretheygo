"""S6 v2 mode choice (docs/formal-specification.md §5 v2): the capacity ladder,
re-derived in exact binary32 semantics from the instance's fleet facts. Subject and
evaluator must agree exactly on the mode; utilisation and the delay per stop are
compared as binary32 too."""

from __future__ import annotations

from common.canonical import bits_to_f32, f32_bits
from evaluator import f32

LADDER = {"Road": ["Bus", "Tram"], "Rail": ["Metro", "Train"], "Water": ["Ferry"]}
TARGET_HEADWAY = {"Bus": 300.0, "Tram": 240.0, "Metro": 200.0, "Train": 480.0, "Ferry": 600.0}
CRUISE = {"Bus": 9.0, "Tram": 12.0, "Metro": 18.0, "Train": 28.0, "Ferry": 10.0}
DEFAULT_STOP = 15.0
DEFAULT_ACCEL = 1.5
DAY = f32.r(262144.0 / 60.0)   # SuitabilityEquity.MovementSecondsPerGameDay (binary32)
RIDES_PER_JOURNEY = 2.0
MAX_UTILISATION = 1.0


def headway(facts: dict, mode: str) -> float:
    # planning headway is the table (FleetFacts.HeadwayFor); the prefab interval is log-only
    return f32.r(TARGET_HEADWAY[mode])


def utilisation(riders: float, hw: float, capacity: float) -> float:
    if hw <= 0.0 or capacity <= 0.0:
        return 0.0
    boardings = f32.mul(riders, f32.r(RIDES_PER_JOURNEY))
    seats = f32.mul(f32.mul(f32.div(DAY, hw), f32.r(2.0)), capacity)
    return f32.div(boardings, seats)


def delay_per_stop(facts: dict, mode: str) -> float:
    fx = facts[mode]
    speed = f32.r(CRUISE[mode])
    a = fx["acceleration"] if fx["acceleration"] > 0.0 else f32.r(DEFAULT_ACCEL)
    b = fx["braking"] if fx["braking"] > 0.0 else f32.r(DEFAULT_ACCEL)
    stop = fx["stop_duration"] if fx["stop_duration"] > 0.0 else f32.r(DEFAULT_STOP)
    physics = f32.add(f32.add(stop, f32.div(speed, f32.mul(f32.r(2.0), a))), f32.div(speed, f32.mul(f32.r(2.0), b)))
    return max(physics, f32.r(DEFAULT_STOP))


def choose(network: str, riders: float, facts: dict) -> tuple[bool, str, float]:
    ladder = LADDER[network]
    mode, util, any_vehicle = ladder[0], 0.0, False
    for option in ladder:
        cap = facts[option]["capacity"]
        if cap <= 0.0:
            continue
        any_vehicle = True
        mode = option
        util = utilisation(riders, headway(facts, option), cap)
        if util <= MAX_UTILISATION:
            return True, mode, util
    return any_vehicle, mode, util


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    facts = {}
    for mode in ["Bus", "Tram", "Metro", "Train", "Ferry"]:
        raw = data["facts"].get(mode)
        facts[mode] = {k: (bits_to_f32(raw[k + "_b32"]) if raw else 0.0)
                       for k in ["capacity", "headway", "stop_duration", "acceleration", "braking"]}
    rows = []
    all_ok = True
    for spec, subject in zip(data["rows"], solution["rows"]):
        ok, mode, util = choose(spec["network"], bits_to_f32(spec["riders_b32"]), facts)
        expected = (ok, mode, f32_bits(util), f32_bits(delay_per_stop(facts, mode)))
        got = (subject["ok"], subject["mode"], subject["utilisation_b32"], subject["delay_per_stop_b32"])
        match = expected == got
        rows.append({"expected": list(expected), "subject": list(got), "match": match})
        all_ok = all_ok and match
    return {"ok": all_ok, "rows": rows}
