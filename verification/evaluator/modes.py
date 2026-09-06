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
        ok, mode, vehicles, lo, hi, headway, util = fleet.choose_mode(
            spec["network"], bits_to_f32(spec["riders_b32"]), facts, policy, lambda _m: rt)
        expected = (ok, mode, vehicles, lo, 0 if hi is None else hi, f32_bits(headway), f32_bits(util),
                    f32_bits(fleet.delay_per_stop(facts, mode)))
        got = (subject["ok"], subject["mode"], subject["fleet"], subject["fleet_min"], subject["fleet_max"],
               subject["headway_b32"], subject["utilisation_b32"], subject["delay_per_stop_b32"])
        match = expected == got
        rows.append({"expected": list(expected), "subject": list(got), "match": match})
        all_ok = all_ok and match
    return {"ok": all_ok, "rows": rows}
