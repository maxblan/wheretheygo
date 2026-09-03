"""S6 mode choice: independent exact re-implementation of the gate cascade
(docs/formal-specification.md §5, constants from TransitMode.cs), in exact
binary32 semantics — subject and evaluator must agree exactly."""

from __future__ import annotations

from common.canonical import bits_to_f32
from evaluator import f32

MODES = ["Bus", "Metro", "Tram", "Train", "Ferry"]

MIN_LENGTH = {"Tram": 1800.0, "Metro": 3200.0, "Train": 10000.0,
              "Ferry": 1200.0, "Bus": 1400.0}
MIN_FLOW_MULTIPLE = {"Tram": 1.5, "Metro": 5.0, "Train": 8.0,
                     "Ferry": 1.0, "Bus": 0.0}
MIN_ENABLED_SHARE = {"Train": 0.04, "Metro": 0.02}
MOSTLY_ON_TRACK = 0.6
ON_TRACK_RELIEF = 0.5
PEAK_SHARE = 0.2
RIDES_PER_JOURNEY = 2.0


def modes_for_traced(network: str, traced: str) -> list[str]:
    if network == "Road":
        return ["Tram", "Bus"]
    return [traced]


def riders_to_fill_one(capacity: float) -> float:
    per_journey = f32.mul(f32.r(PEAK_SHARE), f32.r(RIDES_PER_JOURNEY))
    return f32.div(capacity, per_journey) if per_journey > 0.0 else 0.0


def choose_mode(network: str, traced: str, flow: float, length: float,
                enabled: float, city: float, track_share: float, scored: bool,
                reference: float, capacities: dict[str, float]) -> tuple[bool, str, str]:
    enabled_share = f32.div(enabled, city) if city > 0.0 else 0.0
    met_some_bar = False
    options = modes_for_traced(network, traced)
    for option in options:
        by_flow = flow >= f32.mul(reference, f32.r(MIN_FLOW_MULTIPLE[option]))
        reach_bar = f32.r(MIN_ENABLED_SHARE.get(option, 0.0))
        if reach_bar > 0.0 and track_share >= f32.r(MOSTLY_ON_TRACK):
            reach_bar = f32.mul(reach_bar, f32.r(ON_TRACK_RELIEF))
        by_reach = reach_bar > 0.0 and enabled_share >= reach_bar
        min_riders = 0.0 if option == "Bus" else riders_to_fill_one(
            capacities.get(option, 0.0))
        enough_riders = (not scored) or enabled >= min_riders
        met_some_bar = met_some_bar or ((by_flow or by_reach) and enough_riders)
        if (by_flow or by_reach) and enough_riders and length >= f32.r(MIN_LENGTH[option]):
            return True, option, "None"
    return False, options[-1], "TooShort" if met_some_bar else "DemandTooLow"


def check(instance: dict, solution: dict) -> dict:
    data = instance["data"]
    capacities = {k: bits_to_f32(v) for k, v in data["capacities_b32"].items()}
    rows = []
    all_ok = True
    for spec, subject in zip(data["rows"], solution["rows"]):
        expected = choose_mode(
            spec["network"], spec["traced_mode"],
            bits_to_f32(spec["flow_b32"]), bits_to_f32(spec["length_b32"]),
            bits_to_f32(spec["enabled_demand_b32"]),
            bits_to_f32(spec["city_travel_weight_b32"]),
            bits_to_f32(spec["track_share_b32"]), spec["demand_scored"],
            bits_to_f32(spec["reference_flow_b32"]), capacities)
        got = (subject["ok"], subject["mode"], subject["rejection"])
        match = expected == got
        rows.append({"expected": list(expected), "subject": list(got), "match": match})
        all_ok = all_ok and match
    return {"ok": all_ok, "rows": rows}
