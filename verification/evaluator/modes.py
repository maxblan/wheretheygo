"""S6 v3 mode choice (docs/formal-specification.md §5 v3): the capacity ladder over
the fleet the game's vehicle-count slider allows, re-derived in exact binary32 /
binary64 semantics from the instance's fleet facts and slider policy
(evaluator.fleet). Subject and evaluator must agree exactly on the mode, the
fleet, the span, the interval, the utilisation and the delay per stop."""

from __future__ import annotations

from common.canonical import bits_to_f32, f32_bits
from evaluator import fleet


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    facts = fleet.parse_facts(data)
    policy = fleet.parse_policy(data)
    rows = []
    all_ok = True
    for spec, subject in zip(data["rows"], solution["rows"]):
        rt = bits_to_f32(spec["round_trip_b32"])
        riders = bits_to_f32(spec["riders_b32"])
        # No split in the instance means no period information: only the whole day binds.
        day = bits_to_f32(spec["riders_day_b32"]) if "riders_day_b32" in spec else 0.0
        night = bits_to_f32(spec["riders_night_b32"]) if "riders_night_b32" in spec else 0.0
        ok, mode, vehicles, lo, hi, headway, util, uday, unight = fleet.choose_mode(
            spec["network"], riders, day, night, facts, policy, lambda _m: rt)
        expected = (ok, mode, vehicles, lo, 0 if hi is None else hi, f32_bits(headway), f32_bits(util),
                    f32_bits(uday), f32_bits(unight), f32_bits(fleet.delay_per_stop(facts, mode)))
        got = (subject["ok"], subject["mode"], subject["fleet"], subject["fleet_min"], subject["fleet_max"],
               subject["headway_b32"], subject["utilisation_b32"], subject["day_utilisation_b32"],
               subject["night_utilisation_b32"], subject["delay_per_stop_b32"])
        match = expected == got
        rows.append({"expected": list(expected), "subject": list(got), "match": match})
        all_ok = all_ok and match
    return {"ok": all_ok, "rows": rows}
