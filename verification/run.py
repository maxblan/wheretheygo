#!/usr/bin/env python3
"""Verification pipeline orchestrator.

Per instance: validate -> subject (mod code) -> independent checks -> reference
optimum (SCIP exact + VIPR, independently checked, for `sites`; complete
enumeration for `lineset`; solver-free rational certificate for `lattice_path`)
-> verdict. Exit code 0 only if every instance's verdict passes the contract in
verification/README.md.

Usage:
    python3 run.py [instance names...]        # default: all instances
"""

from __future__ import annotations

import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
from fractions import Fraction

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from common.canonical import (bits_to_f32, canonical_bytes,  # noqa: E402
                              load_instance, sha256_hex)
from evaluator import corridor as ev_corridor  # noqa: E402
from evaluator import heatmap_grid as ev_heatmap_grid  # noqa: E402
from evaluator import heatmap_walk as ev_heatmap_walk  # noqa: E402
from evaluator import sites_walk as ev_sites_walk  # noqa: E402
from evaluator import roadtimes as ev_roadtimes  # noqa: E402
from evaluator.checkcert_dirpath import check_directed_certificate  # noqa: E402
from evaluator import lineset as ev_lineset  # noqa: E402
from evaluator import modes as ev_modes  # noqa: E402
from evaluator import orderstats as ev_orderstats  # noqa: E402
from evaluator import paths as ev_paths  # noqa: E402
from evaluator import sites as ev_sites  # noqa: E402
from evaluator import stops as ev_stops  # noqa: E402
from evaluator.checkcert_path import check_certificate  # noqa: E402
from enumerate.enum_lines import enumerate_optimum  # noqa: E402
from refmodel.sites_milp import build_lp, build_walk_lp  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
INSTANCES = os.path.join(HERE, "instances")
RUNS = os.path.join(HERE, "runs")
SUBJECT = os.path.join(HERE, "subject", "bin", "Release", "net9.0", "Subject")


def find_tool(name: str) -> str | None:
    for base in (os.path.join(HERE, ".toolchain"),
                 os.path.join(HERE, "..", ".verify-toolchain")):
        for sub in ("mmenv/bin", "vipr-build", "bin"):
            candidate = os.path.join(base, sub, name)
            if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
                return candidate
    return shutil.which(name)


def tool_env() -> dict:
    """viprcomp links SoPlex dynamically out of the conda prefix."""
    env = dict(os.environ)
    for base in (os.path.join(HERE, ".toolchain"),
                 os.path.join(HERE, "..", ".verify-toolchain")):
        lib = os.path.join(base, "mmenv", "lib")
        if os.path.isdir(lib):
            env["LD_LIBRARY_PATH"] = lib + os.pathsep + env.get("LD_LIBRARY_PATH", "")
            break
    return env


def run_cmd(cmd: list[str], log_path: str | None = None,
            timeout: int = 900) -> tuple[int, str]:
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                          env=tool_env())
    output = proc.stdout + proc.stderr
    if log_path:
        with open(log_path, "w", encoding="utf-8") as f:
            f.write(output)
    return proc.returncode, output


def parse_scip_exact(output: str) -> dict:
    result: dict = {"status_optimal": "problem is solved [optimal solution found]" in output}
    for key, label in (("primal", "Exact Primal Bound"), ("dual", "Exact Dual Bound")):
        m = re.search(rf"{label}\s*:\s*([-+0-9/.eE]+)", output)
        if m:
            result[key] = m.group(1)
    chosen = []
    in_solution = False
    for line in output.splitlines():
        if line.startswith("objective value:"):
            in_solution = True
            continue
        if in_solution:
            m = re.match(r"^x(\d+)\s+([-0-9/.eE]+)", line.strip())
            if m and Fraction(m.group(2)) > Fraction(1, 2):
                chosen.append(int(m.group(1)))
            elif not line.strip() or not line.startswith("x"):
                if chosen:
                    in_solution = False
    result["chosen"] = sorted(chosen)
    return result


def check_sites(instance: dict, solution: dict, out_dir: str,
                notes: list[str]) -> dict:
    report = ev_sites.check(instance, solution)
    verdict = {
        "evaluator": report,
        "pass_feasible": report["feasible"],
        "pass_greedy_faithful": report["greedy_faithful"],
        "pass_scores_exact": report["reported_scores_exact"],
    }
    exact = report.get("exact")
    verdict["exact_present"] = exact is not None
    if exact is not None:
        verdict["pass_exact_feasible"] = bool(
            exact["feasible"] and exact["reported_scores_exact"] and exact["ranked"]
            and exact["value_consistent"])
        if not verdict["pass_exact_feasible"]:
            notes.append("exact selection infeasible or inconsistent")

    # Reference model: SCIP 10 exact + VIPR, independently checked.
    lp, cands, model_hash = build_lp(instance)
    verdict["refmodel_candidates_agree"] = sorted(cands) == sorted(
        ev_sites.candidates(instance["data"]["width"], instance["data"]["height"],
                            [Fraction(bits_to_f32(b))
                             for b in instance["data"]["scores_b32"]]))
    verdict["model_hash"] = model_hash
    optimum = certify_lp(lp, out_dir, notes, verdict)
    if optimum is None:
        return verdict

    greedy_value = Fraction(report["objective_value"])
    if optimum is not None:
        verdict["certified_optimum"] = str(optimum)
        verdict["gap"] = str(optimum - greedy_value)
        verdict["greedy_is_optimal"] = optimum == greedy_value
        if exact is not None:
            judge_exact_selection(exact, optimum, verdict, notes)
        if "enumeration_optimum" in report:
            agree = Fraction(report["enumeration_optimum"]) == optimum
            verdict["enumeration_agrees_with_certificate"] = agree
            if not agree:
                notes.append("enumeration and certified optimum disagree")
                verdict["certified"] = False
    return verdict


def check_sites_walk(instance: dict, solution: dict, out_dir: str,
                     notes: list[str]) -> dict:
    """S2 v2: candidates are network nodes, conflicts are walking times below the
    spacing. Feasibility and greedy baseline from the evaluator; optimum certified
    through the same SCIP/VIPR path as the grid form; the mod's exact selection judged
    against it."""
    report = ev_sites_walk.check(instance, solution)
    verdict = {
        "evaluator": report,
        "pass_feasible": report["feasible"],
        "pass_scores_exact": report["reported_scores_exact"],
        "pass_greedy_faithful": report["greedy_faithful"],
        "exact_present": True,
        "pass_exact_feasible": bool(report["feasible"] and report["reported_scores_exact"]
                                    and report["ranked"] and report["value_consistent"]),
    }
    lp, model_hash = build_walk_lp(instance, report["conflict_pairs"])
    verdict["model_hash"] = model_hash
    optimum = certify_lp(lp, out_dir, notes, verdict)
    if optimum is None:
        return verdict
    greedy_value = Fraction(report["greedy_value"])
    verdict["certified_optimum"] = str(optimum)
    verdict["gap"] = str(optimum - greedy_value)
    verdict["greedy_is_optimal"] = optimum == greedy_value
    exact = {
        "objective_value": report["objective_value"],
        "upper_bound": report["upper_bound"],
        "optimal_claimed": report["optimal_claimed"],
    }
    verdict["evaluator"]["objective_value"] = report["greedy_value"]
    judge_exact_selection(exact, optimum, verdict, notes)
    if "enumeration_optimum" in report:
        agree = Fraction(report["enumeration_optimum"]) == optimum
        verdict["enumeration_agrees_with_certificate"] = agree
        if not agree:
            notes.append("enumeration and certified optimum disagree")
            verdict["certified"] = False
    return verdict


def certify_lp(lp: str, out_dir: str, notes: list[str], verdict: dict):
    """Solve `lp` with SCIP 10 exact, complete and independently check its VIPR
    certificate, and require the verified range to pin the claimed optimum. Fills
    `verdict` and returns the certified optimum as a Fraction, or None when any step
    fails (verdict["certified"] is then False)."""
    lp_path = os.path.join(out_dir, "model.lp")
    with open(lp_path, "w", encoding="ascii") as f:
        f.write(lp)

    scip = find_tool("scip")
    viprchk = find_tool("viprchk")
    viprttn = find_tool("viprttn")
    if scip is None or viprchk is None:
        verdict["certified"] = False
        notes.append("scip/viprchk not found — run verification/bootstrap.sh")
        return None

    cert_path = os.path.join(out_dir, "model.vipr")
    code, output = run_cmd(
        [scip,
         "-c", "set exact enable TRUE",
         "-c", "set presolving emphasis off",
         "-c", f"set certificate filename {cert_path}",
         "-c", f"read {lp_path}",
         "-c", "optimize",
         "-c", "display solution",
         "-c", "quit"],
        os.path.join(out_dir, "scip.log"))
    scip_result = parse_scip_exact(output)
    verdict["scip"] = scip_result
    if code != 0 or not scip_result.get("status_optimal"):
        verdict["certified"] = False
        notes.append("SCIP did not prove optimality")
        return None

    # SCIP may emit the certificate under the given name or with an _ori twin.
    cert_file = None
    for candidate_name in (cert_path, cert_path + "_ori"):
        if os.path.isfile(candidate_name) and os.path.getsize(candidate_name) > 0:
            cert_file = candidate_name
            break
    if cert_file is None:
        verdict["certified"] = False
        notes.append("no certificate file produced")
        return None

    # Reject trivial certificates (the presolve-solved DER 0 case).
    with open(cert_file, "r", encoding="ascii", errors="replace") as f:
        cert_text = f.read()
    m = re.search(r"^DER\s+(\d+)", cert_text, re.MULTILINE)
    verdict["certificate_derivations"] = int(m.group(1)) if m else 0
    if verdict["certificate_derivations"] <= 0:
        verdict["certified"] = False
        notes.append("certificate is trivial (0 derivations)")
        return None
    verdict["certificate_hash"] = sha256_hex(cert_text.encode("ascii", "replace"))

    # SCIP 10 emits VIPR 1.1 "weak" derivations — linear combinations that only
    # weakly dominate their constraint, with the completing bounds left implicit.
    # viprchk implements the 1.0 grammar and rejects them as a SYNTAX error, which
    # reads like a broken proof and is not one. viprcomp is the repo's own tool for
    # exactly this: it completes the weak steps first (and needs no LP for them,
    # hence --soplex=off). Certificates without weak steps pass through unchanged.
    check_target = cert_file
    viprcomp = find_tool("viprcomp")
    verdict["weak_derivations_completed"] = False
    if viprcomp is not None:
        code, _ = run_cmd([viprcomp, "--soplex=off", cert_file],
                          os.path.join(out_dir, "viprcomp.log"))
        root, extension = os.path.splitext(cert_file)
        completed = root + "_complete" + extension
        if code == 0 and os.path.isfile(completed):
            check_target = completed
            verdict["weak_derivations_completed"] = True
    elif viprttn is not None:
        code, _ = run_cmd([viprttn, cert_file], os.path.join(out_dir, "viprttn.log"))
        tightened = cert_file + ".opt"
        if code == 0 and os.path.isfile(tightened):
            check_target = tightened
    code, chk_output = run_cmd([viprchk, check_target],
                               os.path.join(out_dir, "viprchk.log"))
    verified = code == 0 and "Successfully verified" in chk_output
    verdict["viprchk_verified"] = verified
    m = re.search(r"verified optimal value range \[([^,]+),\s*([^\]]+)\]", chk_output)
    if m:
        verdict["viprchk_range"] = [m.group(1).strip(), m.group(2).strip()]

    if not verified:
        verdict["certified"] = False
        notes.append("viprchk did not verify the certificate")
        return None

    optimum = Fraction(scip_result["primal"]) if "primal" in scip_result else None
    dual = Fraction(scip_result["dual"]) if "dual" in scip_result else None
    verdict["bounds_meet"] = optimum is not None and optimum == dual

    # The independently verified certificate value must pin the claimed optimum.
    # SCIP certifies maximization problems as min(-obj), so the verified range is
    # the negated optimum; accept exactly [v, v] with v in {opt, -opt}.
    cert_matches = False
    if optimum is not None and "viprchk_range" in verdict:
        lo_s, hi_s = verdict["viprchk_range"]
        try:
            lo_v, hi_v = Fraction(lo_s), Fraction(hi_s)
            cert_matches = lo_v == hi_v and lo_v in (optimum, -optimum)
        except (ValueError, ZeroDivisionError):
            cert_matches = False
    verdict["certificate_value_matches"] = cert_matches
    if not cert_matches:
        notes.append("verified certificate value does not pin the claimed optimum")
    verdict["certified"] = verdict["bounds_meet"] and cert_matches
    return optimum


def judge_exact_selection(exact: dict, optimum: Fraction, verdict: dict,
                          notes: list[str]) -> None:
    """The mod's exact S2 result against the certified optimum. A closed search
    must hit the optimum exactly (gap 0). A budget-stopped search must report a
    value no better than the optimum and a bound no worse than it — its claimed
    interval has to contain the truth — and must be at least as good as greedy."""
    exact_value = Fraction(exact["objective_value"])
    upper_bound = Fraction(exact["upper_bound"])
    greedy_value = Fraction(verdict["evaluator"]["objective_value"])
    verdict["exact_gap"] = str(optimum - exact_value)
    verdict["exact_optimal_claimed"] = exact["optimal_claimed"]
    verdict["exact_is_optimal"] = exact_value == optimum
    verdict["exact_upper_bound"] = str(upper_bound)
    verdict["exact_upper_bound_sound"] = upper_bound >= optimum
    verdict["exact_at_least_greedy"] = exact_value >= greedy_value
    if exact_value > optimum:
        notes.append("exact selection exceeds the certified optimum — one of them is wrong")
    if exact["optimal_claimed"]:
        verdict["pass_exact_optimal"] = exact_value == optimum
        if not verdict["pass_exact_optimal"]:
            notes.append("exact search claimed optimality but missed the certified optimum")
    else:
        verdict["pass_exact_optimal"] = (exact_value <= optimum
                                        and verdict["exact_upper_bound_sound"]
                                        and verdict["exact_at_least_greedy"])
        notes.append("exact search stopped on its node budget; bound checked instead")


def integer_scaled_certificate(instance: dict, certificate: dict) -> dict:
    """The trusted 10-line rational->integer transformation: multiply every
    label, edge cost and the claimed cost by the lcm L of all denominators.
    Order and optimality are preserved (L > 0); the Lean-verified checker then
    operates on a self-contained integer problem."""
    from math import lcm

    from common.canonical import bits_to_fraction as b2f
    data = instance["data"]
    edge_costs = [b2f(c) for c in data["edge_cost_b32"]]
    labels = [None if t is None else Fraction(*map(int, t.split("/")))
              for t in certificate["labels"]]
    claimed = Fraction(*map(int, certificate["claimed_cost"].split("/")))
    dens = [c.denominator for c in edge_costs] + [claimed.denominator] + [
        l.denominator for l in labels if l is not None]
    scale = lcm(*dens) if dens else 1
    return {
        "source": certificate["source"],
        "target": certificate["target"],
        "claimed_cost_int": int(claimed * scale),
        "labels_int": [None if l is None else int(l * scale) for l in labels],
        "path": certificate["path"],
        "edges_int": [
            [a, b, int(c * scale)]
            for a, b, c in zip(data["edge_a"], data["edge_b"], edge_costs)
        ],
        "scale": str(scale),
    }


LEAN_CHECKER = os.path.join(HERE, "lean", ".lake", "build", "bin", "verify")


def integer_directed_certificate(certificate: dict) -> dict:
    """Directed certificates are already integral (milliseconds); this is the
    self-contained form the Lean checker reads, with the directed flag set."""
    def as_int(text):
        num, _, den = text.partition("/")
        assert den in ("", "1")
        return int(num)
    return {
        "directed": True,
        "source": certificate["source"],
        "target": certificate["target"],
        "claimed_cost_int": as_int(certificate["claimed_cost"]),
        "labels_int": [None if t is None else as_int(t) for t in certificate["labels"]],
        "path": certificate["path"],
        "edges_int": [[a, b, c] for a, b, c in certificate["edges"]],
        "scale": "1",
    }


MAX_LEGS_CERTIFIED = 40


def check_road_times(instance: dict, solution: dict, out_dir: str,
                     notes: list[str]) -> dict:
    """S4 v2: arc times and turn table re-derived, every leg's shortest directed
    time recomputed on the explicit state graph and required to equal both the
    game's and the subject's; the first MAX_LEGS_CERTIFIED legs get a directed
    certificate checked in Python and by the Lean-proved directed checker."""
    report, certificates = ev_roadtimes.check(instance, solution)
    verdict = {"evaluator": report, "pass_times": report["ok"]}
    python_ok = 0
    lean_ok = 0
    lean_ran = 0
    for cert in certificates[:MAX_LEGS_CERTIFIED]:
        ok, why = check_directed_certificate(cert)
        if not ok:
            notes.append(f"leg {cert['leg']}: directed certificate failed: {why}")
            continue
        python_ok += 1
        int_path = os.path.join(out_dir, f"road-certificate-{cert['leg']}.json")
        with open(int_path, "w", encoding="ascii") as f:
            json.dump(integer_directed_certificate(cert), f)
        if os.path.isfile(LEAN_CHECKER) and os.access(LEAN_CHECKER, os.X_OK):
            lean_ran += 1
            code, _ = run_cmd([LEAN_CHECKER, int_path], os.path.join(out_dir, "lean-checker.log"))
            if code == 0:
                lean_ok += 1
            else:
                notes.append(f"leg {cert['leg']}: Lean-verified directed checker rejected the certificate")
    verdict["certificates"] = min(len(certificates), MAX_LEGS_CERTIFIED)
    verdict["certificates_python_verified"] = python_ok
    verdict["certificates_lean_verified"] = lean_ok
    verdict["lean_checker_available"] = lean_ran > 0 or not certificates
    if certificates and lean_ran == 0:
        notes.append("Lean checker not built (make -C verification lean) — formally verified check skipped")
    verdict["pass_certificates"] = python_ok == verdict["certificates"] and (lean_ran == 0 or lean_ok == lean_ran)
    return verdict


def check_lattice_path(instance: dict, solution: dict, out_dir: str,
                       notes: list[str]) -> dict:
    report, certificate = ev_paths.check(instance, solution)
    verdict = {"evaluator": report, "pass_path": report["ok"]}
    if certificate is not None:
        cert_path = os.path.join(out_dir, "path-certificate.json")
        with open(cert_path, "w", encoding="ascii") as f:
            json.dump(certificate, f)
        ok, why = check_certificate(instance, certificate)
        verdict["certificate_verified"] = ok
        verdict["certificate_reason"] = why
        if not ok:
            notes.append("path certificate failed independent check")

        # Second, FORMALLY VERIFIED checker (Lean 4): soundness of the checking
        # logic is machine-proved (Verify.PathCert.check_sound).
        int_cert = integer_scaled_certificate(instance, certificate)
        int_path = os.path.join(out_dir, "path-certificate-int.json")
        with open(int_path, "w", encoding="ascii") as f:
            json.dump(int_cert, f)
        if os.path.isfile(LEAN_CHECKER) and os.access(LEAN_CHECKER, os.X_OK):
            code, output = run_cmd([LEAN_CHECKER, int_path],
                                   os.path.join(out_dir, "lean-checker.log"))
            verdict["lean_certificate_verified"] = code == 0
            if code != 0:
                notes.append("Lean-verified checker rejected the certificate")
        else:
            notes.append("Lean checker not built (make -C verification lean) — "
                         "formally verified check skipped")
    else:
        verdict["certificate_verified"] = False
        if report.get("subject_reachable"):
            notes.append("no certificate (path not optimal?)")
    # Tie reporting: count cost-minimal paths on the tight-edge DAG.
    node_count, edges = ev_paths.load_edges(instance["data"])
    dist = ev_paths.exact_dijkstra(node_count, edges, instance["data"]["from"])
    target = instance["data"]["to"]
    counts = {instance["data"]["from"]: 1}
    order = sorted((d, i) for i, d in enumerate(dist) if d is not None)
    for _, node in order:
        if node not in counts:
            counts[node] = 0
    for _, node in order:
        for a, b, c in edges:
            for u, v in ((a, b), (b, a)):
                if v == node and dist[u] is not None and dist[v] is not None \
                        and dist[u] + c == dist[v]:
                    counts[node] = counts.get(node, 0) + counts.get(u, 0)
    verdict["shortest_path_count"] = counts.get(target, 0)
    verdict["tie"] = counts.get(target, 0) > 1
    return verdict


def check_lineset(instance: dict, solution: dict, out_dir: str,
                  notes: list[str]) -> dict:
    report = ev_lineset.check(instance, solution)
    enum_result = enumerate_optimum(instance)
    with open(os.path.join(out_dir, "enumeration.json"), "w", encoding="ascii") as f:
        json.dump(enum_result, f, indent=1)

    p = ev_lineset.parse(instance)
    greedy_lo = Fraction(report["greedy_set_objective"][0])
    optimum_lo = Fraction(enum_result["optimum_lo"])
    complete = enum_result["complete"]
    k_enum = enum_result["enumerated_max_size"]
    verdict = {
        "evaluator": {k: v for k, v in report.items() if k != "rounds"},
        "rounds": report["rounds"],
        "pass_rounds": report["ok"],
        "enumeration_complete": complete,
        "enumeration_max_size": k_enum,
        "declared_k": enum_result["declared_k"],
        "subsets_evaluated": enum_result["subsets_evaluated"],
        "optimum_set": enum_result["optimum_set"],
        "optimum_value": enum_result["optimum_lo"],
        "greedy_set_value": report["greedy_set_objective"][0],
        "tie_affected": report["tie_affected"],
    }
    if complete:
        verdict["gap"] = str(optimum_lo - greedy_lo)
        verdict["greedy_set_is_optimal"] = optimum_lo == greedy_lo
    else:
        # Bounded regime: the K-line optimum is NOT established. What IS exact is
        # the optimum over <= k_enum lines, compared against the greedy's first
        # k_enum acceptances at equal cardinality.
        prefix = report["accepted"][:k_enum]
        prefix_lo, _hi, _tie = ev_lineset.set_objective(p, prefix)
        verdict["greedy_set_is_optimal"] = None
        verdict["optimality_level"] = f"bounded: exact over subsets of size <= {k_enum} of {verdict['declared_k']}"
        verdict["greedy_prefix"] = prefix
        verdict["greedy_prefix_value"] = str(prefix_lo)
        verdict["gap"] = str(optimum_lo - prefix_lo)
        verdict["greedy_prefix_optimal_at_k"] = optimum_lo == prefix_lo
        notes.append(
            f"K={verdict['declared_k']} optimum not enumerable within budget; exact "
            f"only for <= {k_enum} lines ({verdict['subsets_evaluated']} subsets)")

    data = instance["data"]
    if "staged_candidate" in data:
        staged = data["staged_candidate"]
        length = bits_to_f32(data["alignment_length_b32"])
        spacing = bits_to_f32(data["spacing_b32"])
        expected = ev_stops.plan_calling_points(length, spacing, 32)
        staged_x = [bits_to_f32(b)
                    for b in data["candidates"][staged]["stop_x_b32"]]
        verdict["staged_stops_match_plan"] = staged_x == expected
        staged_lo, staged_hi, _ = ev_lineset.set_objective(p, [staged])
        others = [
            (i, ev_lineset.set_objective(p, [i]))
            for i in range(len(p["candidates"])) if i != staged
        ]
        dominated = any(lo > staged_hi for _, (lo, _hi, _t) in others)
        verdict["staged_set_value"] = [str(staged_lo), str(staged_hi)]
        verdict["staged_dominated"] = dominated
        if not verdict["staged_stops_match_plan"]:
            notes.append("staged candidate stops do not match PlanCallingPoints")
    return verdict


def expectations_met(instance: dict, verdict: dict, notes: list[str]) -> bool:
    ok = True
    for key, expected in instance.get("expect", {}).items():
        actual = verdict.get(key)
        if actual != expected:
            ok = False
            notes.append(f"expectation failed: {key} = {actual}, expected {expected}")
    return ok


def base_pass(kind: str, verdict: dict) -> bool:
    if kind == "sites":
        return all(verdict.get(k) for k in (
            "pass_feasible", "pass_greedy_faithful", "pass_scores_exact",
            "refmodel_candidates_agree", "certified",
            "exact_present", "pass_exact_feasible", "pass_exact_optimal"))
    if kind == "road_times":
        return bool(verdict.get("pass_times") and verdict.get("pass_certificates"))
    if kind == "sites_walk":
        return all(verdict.get(k) for k in (
            "pass_feasible", "pass_greedy_faithful", "pass_scores_exact", "certified",
            "pass_exact_feasible", "pass_exact_optimal"))
    if kind == "lattice_path":
        if not verdict.get("evaluator", {}).get("subject_reachable", False):
            return verdict.get("pass_path", False)
        lean_ok = verdict.get("lean_certificate_verified", None)
        return bool(verdict.get("pass_path") and verdict.get("certificate_verified")
                    and lean_ok is not False)
    if kind in ("calling_points", "mode_choice", "corridor",
                "heatmap_grid", "heatmap_walk", "order_stats"):
        return bool(verdict.get("evaluator", {}).get("ok"))
    if kind == "lineset":
        return bool(verdict.get("pass_rounds")
                    and (verdict.get("enumeration_complete")
                         or verdict.get("enumeration_max_size", 0) >= 1))
    return False


def versions() -> dict:
    info = {
        "python": sys.version.split()[0],
        "platform": platform.platform(),
    }
    scip = find_tool("scip")
    if scip:
        try:
            _, out = run_cmd([scip, "-c", "quit"], timeout=60)
            first = out.splitlines()[0] if out else ""
            info["scip"] = first.strip()
        except Exception as e:  # noqa: BLE001
            info["scip"] = f"error: {e}"
    for tool in ("viprchk", "viprttn"):
        path = find_tool(tool)
        if path:
            with open(path, "rb") as f:
                info[tool + "_sha256"] = sha256_hex(f.read())
    try:
        _, out = run_cmd(["dotnet", "--version"], timeout=120)
        info["dotnet"] = out.strip()
    except Exception as e:  # noqa: BLE001
        info["dotnet"] = f"error: {e}"
    try:
        _, out = run_cmd(["git", "-C", HERE, "rev-parse", "HEAD"], timeout=60)
        info["git"] = out.strip()
    except Exception as e:  # noqa: BLE001
        info["git"] = f"error: {e}"
    return info


def run_instance(name: str, stamp: str, version_info: dict) -> bool:
    path = os.path.join(INSTANCES, name + ".json")
    out_dir = os.path.join(RUNS, name, stamp)
    os.makedirs(out_dir, exist_ok=True)
    notes: list[str] = []

    try:
        instance = load_instance(path)
    except Exception as e:  # noqa: BLE001
        print(f"[{name}] INVALID INSTANCE: {e}")
        return False

    solution_path = os.path.join(out_dir, "solution.json")
    code, output = run_cmd([SUBJECT, path, solution_path],
                           os.path.join(out_dir, "subject.log"))
    if code != 0:
        print(f"[{name}] SUBJECT FAILED (exit {code})")
        return False
    with open(solution_path, "r", encoding="utf-8") as f:
        solution = json.load(f)
    if solution.get("instance_hash") != instance["hash"]:
        notes.append("subject echoed a different instance hash")

    kind = instance["kind"]
    if kind == "sites":
        verdict = check_sites(instance, solution, out_dir, notes)
    elif kind == "sites_walk":
        verdict = check_sites_walk(instance, solution, out_dir, notes)
    elif kind == "road_times":
        verdict = check_road_times(instance, solution, out_dir, notes)
    elif kind == "lattice_path":
        verdict = check_lattice_path(instance, solution, out_dir, notes)
    elif kind == "calling_points":
        verdict = {"evaluator": ev_stops.check(instance, solution)}
    elif kind == "mode_choice":
        verdict = {"evaluator": ev_modes.check(instance, solution)}
    elif kind == "corridor":
        report = ev_corridor.check(instance, solution)
        verdict = {"evaluator": report}
        rounds = report.get("rounds", [])
        if rounds:
            verdict["hit_max_length"] = bool(rounds[0].get("hit_max_length"))
            if "termini_have_demand" in rounds[0]:
                verdict["tail_discarded"] = bool(rounds[0]["termini_have_demand"])
    elif kind == "heatmap_grid":
        verdict = {"evaluator": ev_heatmap_grid.check(instance, solution)}
    elif kind == "heatmap_walk":
        report = ev_heatmap_walk.check(instance, solution)
        verdict = {"evaluator": report, "three_way_exact": report["three_way_exact"]}
    elif kind == "order_stats":
        verdict = {"evaluator": ev_orderstats.check(instance, solution)}
    elif kind == "lineset":
        verdict = check_lineset(instance, solution, out_dir, notes)
    else:
        print(f"[{name}] unknown kind {kind}")
        return False

    passed = base_pass(kind, verdict) and expectations_met(instance, verdict, notes)
    verdict_doc = {
        "instance": name,
        "instance_hash": instance["hash"],
        "kind": kind,
        "pass": passed,
        "notes": notes,
        "verdict": verdict,
        "versions": version_info,
    }
    with open(os.path.join(out_dir, "verdict.json"), "w", encoding="ascii") as f:
        json.dump(verdict_doc, f, indent=1, default=str)

    status = "PASS" if passed else "FAIL"
    extra = ""
    if kind == "sites" and "gap" in verdict:
        extra = f" gap={verdict['gap']} (certified optimum {verdict.get('certified_optimum')})"
    if kind == "lineset":
        extra = (f" greedy={verdict['greedy_set_value']}"
                 f" optimum={verdict['optimum_value']} gap={verdict['gap']}")
    print(f"[{name}] {status}{extra}"
          + (f"  notes: {'; '.join(notes)}" if notes else ""))
    return passed


def is_heavy(name: str) -> bool:
    """An instance the default sweep must not pick up: flagged `heavy` by the
    exporter (a real city's score field), or a lineset whose subset count exceeds
    the enumeration budget (a real city's candidate pool). Both run by name."""
    try:
        with open(os.path.join(INSTANCES, name + ".json"), "r", encoding="ascii") as f:
            inst = json.load(f)
    except (OSError, ValueError):
        return False
    if inst.get("heavy", False):
        return True
    if inst.get("kind") == "lineset":
        from math import comb
        from enumerate.enum_lines import DEFAULT_BUDGET
        n = len(inst["data"]["candidates"])
        k = inst["data"]["max_accept"]
        return sum(comb(n, i) for i in range(k + 1)) > DEFAULT_BUDGET
    return False


def main() -> int:
    names = [a for a in sys.argv[1:] if not a.startswith("--")]
    run_all = "--all" in sys.argv[1:]
    if not names:
        names = sorted(
            os.path.splitext(f)[0]
            for f in os.listdir(INSTANCES) if f.endswith(".json"))
        if not run_all:
            skipped = [n for n in names if is_heavy(n)]
            names = [n for n in names if n not in skipped]
            for name in skipped:
                print(f"[{name}] SKIPPED (heavy; run it by name, or pass --all)")
    stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    version_info = versions()
    all_pass = True
    for name in names:
        try:
            ok = run_instance(name, stamp, version_info)
        except Exception as e:  # noqa: BLE001
            import traceback
            traceback.print_exc()
            print(f"[{name}] ERROR: {e}")
            ok = False
        all_pass = all_pass and ok
    print("RESULT:", "PASS" if all_pass else "FAIL")
    return 0 if all_pass else 1


if __name__ == "__main__":
    sys.exit(main())
