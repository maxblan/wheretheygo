# verification/

An independent, deterministic verification pipeline for the mod's stop-placement and
route computations. It shares **no computation logic** with the mod: the reference
model generator and the evaluator are written from `docs/formal-specification.md`,
in exact integer/rational arithmetic. The only component that touches mod code is
the *subject runner*, which links the unmodified pure files to produce the
solution-under-test — the same linking trick `tests/SuitabilityScoring.Tests` uses.

Companion documents:

- `docs/formal-specification.md` — the exact problems being verified.
- `docs/verification-architecture.md` — components, data flow, trust boundary,
  toolchain decision (SCIP 10 exact + VIPR + viprcomp + viprchk, all verified
  runnable here).
- `docs/correctness-claims.md` — every claim and its current status. **That file is
  the only place allowed to say what has been proven.**
- `docs/scientific-model-review.md` — the literature the models are judged against.

## Layout

```
verification/
  README.md            this file
  bootstrap.sh         installs the project-local toolchain (micromamba → SCIP 10
                       exact, builds viprcomp + viprchk); idempotent; no sudo; ~10 min
  run.py               orchestrator: one verified run per instance
  Makefile             make verify-all / make instance I=<name>
  instances/           canonical JSON instances (synthetic, versioned, hashed)
  generators/          deterministic instance generators
  subject/             C# runner linking the mod's pure files (system under test)
  refmodel/            instance JSON -> exact MILP (.lp) + model hash   [independent]
  evaluator/           exact feasibility/objective checker              [independent]
  enumerate/           complete enumeration for bounded instances       [independent]
  lean/                Lean 4 proofs + the VERIFIED certificate checker
                       (see the "Formal bewiesen" section of correctness-claims.md)
  runs/                artifacts per run (gitignored): certificate, logs, verdict
  .toolchain/          project-local solver + Lean toolchain (gitignored)
```

`refmodel/` and `evaluator/` are independent implementations that must agree (the
run cross-checks their candidate sets and optima on every sites instance); neither
imports the other's computation code, and nothing here imports mod code.
`enumerate/` adds only subset iteration on top of the evaluator's exact objective —
the objective IS the declared reference objective, so the S7 independence
cross-check is subject (mod code) vs evaluator, not two Python variants.

## What is verified, per instance

| Stage | Check | Level |
|---|---|---|
| S1 v1 gathered terms (historical) | the retired Burst job's seven terms on a REAL city (`heatmap_grid`) | bit-exact recomputation from the job's own exported inputs — checks old exports, not the current mod |
| S1 v2 walking-time terms | the access pass (`heatmap_walk`): integer shortest-path times over the pedestrian graph, snapping, kernel, ordered sums | **three-way bit-exact**: game export = the mod's own pure code run offline (subject) = independent Python re-derivation (evaluator) |
| S1/S5 order stats | SelectKth / PositivePercentile vs sorted reference | complete enumeration (bounded family) |
| S2 sites | output feasible (candidates, separation, budget) + greedy-faithful | exact re-evaluation |
| S2 sites | exact optimality gap vs the declared reference objective | SCIP exact + VIPR certificate, independently checked; cross-checked by enumeration on small instances |
| S2 sites | the mod's exact selection (`SuitabilityExactSites`) hits the certified optimum (gap 0) when it reports `Optimal`; when budget-stopped, its [value, ceiling] brackets the optimum and beats greedy | `judge_exact_selection` in `run.py`; `exact_*` fields in `solution.json` |
| S2 v2 network sites (`sites_walk`) | candidates are network nodes, conflicts are exact integer walking times below the spacing; feasibility, greedy baseline, certified optimum, mod's exact selection judged | evaluator conflicts by exact Dijkstra; SCIP exact + VIPR on pairwise-conflict MIP; brute force ≤ 40 candidates |
| S3 v2 equity measure (`coverage`) | served-walk field from the served stops, journeys served at both ends within the horizon, share and weighted Gini of access walk | three-way exact: game export = mod code offline = independent evaluator |
| S4 v2 road driving times (`road_times`) | arc times and turn table re-derived from street lengths, speed limits and the game's curve-angle cost; every stop-to-stop leg's fastest directed time recomputed on the explicit arc-state graph and required to equal the game's and the subject's | exact integer Dijkstra; per-leg directed certificate checked in Python and by the **Lean-proved directed checker** (`Verify.DirPathCert.check_sound`) |
| S4 lattice paths | returned path is a minimum-cost path | exact distance-label certificate (solver-free), checked by two independent checkers — a Python one and a **Lean 4 executable whose soundness is machine-proved** (`Verify.PathCert.check_sound`; the ℚ→ℤ scaling covered by `Verify.Scaling.check_scaled_sound`) |
| S4 corridor growth | GrowCorridor/PeelFlow/DecayNovelty faithful to the spec | bit-exact independent replay (corridors, blocks, full flow/novelty arrays per round) |
| S5 stops | offsets/gaps/must-call/termini/floor invariants | exact re-evaluation |
| S6 mode | gate cascade re-evaluation | exact re-evaluation |
| S7 v2 line set (`lineset_time`) | the mod's exact set selection under the passenger-time objective: before/after door-to-door over zone-node routing, riders per line over every shortest itinerary, utilisation and duplicate feasibility on the set, capped equity share, lexicographic key | subject (mod's `Solve`) must reproduce the game's set; exact rational evaluator (time saved within a derived binary32 budget); complete enumeration of all feasible subsets when Σ C(n,k) fits `ENUM_BUDGET`, otherwise exact over subsets of ≤ k′ lines and reported as bounded; key ties counted |
| S7 v1 routing/credit and greedy set (historical) | credit formula, greedy gap vs the credited-sum optimum | removed 2026-09-05 with the greedy rounds; the refutations stay in `docs/correctness-claims.md` (C7.4, C7.5) |

The mod claims no global optimality (README); the pipeline's optimality artifacts
quantify the *gap*, they do not certify the mod optimal.

## Running

```bash
./verification/bootstrap.sh          # once; installs .toolchain/ (no sudo)
make -C verification lean            # optional but recommended: builds the
                                     # formally verified certificate checker
                                     # (pinned Lean 4.15.0, ~300 MB download)
make -C verification verify-all      # every instance, full chain
make -C verification instance I=sites-greedy-gap
```

Without the Lean checker the pipeline still passes (the formally verified check
is reported as skipped); with it built, a Lean rejection fails the run.

## Verifying a real city

The synthetic instances are committed and cover the algorithms; a real city adds
the one thing they cannot — the Burst job, which does not run outside the game.

1. In the game: **Options → Station Suitability Overlay → Export verification
   instance**. The files appear after the next recalculation; the mod log names
   the folder (`…\Cities Skylines II\ModsData\StationSuitabilityOverlay\verification`).
2. Copy the three `real-<city>-<stamp>-*.json` files into `instances/`.
3. `make -C verification instance I=real-<city>-<stamp>-heatmap` — and the same
   for `-sites`, `-roads`, `-coverage` and `-lineset`. They behave like any other
   instance. An export in a wire format the subject no longer reads belongs in
   `instances/superseded/` (gitignored), not in the sweep.

Notes. The `-sites` file is the real score field: the resulting MIP has one
binary per local maximum and a conflict constraint per close pair. It is flagged
`heavy`, so `verify-all` skips it — run it by name (Valmare: 716 binaries, solved
and certified in about 5 s; larger cities will take longer). The heatmap instance
checks a deterministic sample of cells (the indices are in the file), which is
exact per cell, not a whole-grid proof. The export is read-only and changes
nothing about what the mod computes.

A real city's candidate pool (Valmare: 39 candidates, K = 5 → 667,928 subsets) is
beyond complete enumeration at seconds per subset. `run.py` then enumerates every
feasible subset of ≤ k′ lines for the largest k′ within `ENUM_BUDGET` (default
12,000 subsets, in parallel on all cores), reports the run as *bounded*, and the
mod's claim of optimality is only confirmed when the enumeration is complete. Such
linesets count as heavy and are skipped by `verify-all`.

Certificates from real models need `viprcomp`: SCIP 10 writes VIPR 1.1 "weak"
derivations that `viprchk` alone rejects as a syntax error. `bootstrap.sh` builds
it; without it the run is reported as unverified, never as passed.

First real run (Valmare, 2026-09-03): heatmap 2,073/2,073 sampled cells bit-exact
(928 unbuildable, 1,400 sources rejected by the landmass gate); v1 lineset every
credit inside its exact interval over 11 lines, 76 stops and 1,394 flows; sites
certified optimum ≈ 10.4265 with a greedy gap of ≈ 0.132 (≈ 1.3 %).

A run writes `runs/<instance>/<utc-stamp>/` with: `versions.json` (tool versions,
hashes of instance, model, certificate), solver log, `.vipr` certificate,
`viprcomp.log`, `viprchk.log`, subject/evaluator outputs, and `verdict.json`.

## Exit-code contract

`run.py` (and therefore `make verify-all`) exits 0 **only** when, for every
instance:

1. the instance validates against the schema (canonical form, hashes match),
2. the reference model was solved to proven optimality,
3. the optimality certificate was completed (`viprcomp`) and independently verified
   (`viprchk`, non-trivial: ≥ 1 derivation) — or, for the enumeration path, the
   enumeration completed over the entire declared search space,
4. the mod's solution is feasible under the specification, and
5. the mod solution's *claimed* optimality level is confirmed (for this mod that
   claim is "heuristic, faithful to its own procedure" — faithfulness must hold;
   any measured gap is reported, and a gap does not fail the run unless the
   instance declares an expected-gap bound that is exceeded).

Anything skipped, trivial, or mismatched ⇒ non-zero, with the reason in
`verdict.json`.

## Numbers policy

Instance JSON carries every float as its exact binary32 bit pattern (plus a decimal
rendering for humans). The reference side computes in `int`/`Fraction` on the exact
rationals those bits denote — inputs carry no discretization error. Where the mod's
float evaluation is compared against exact values, the comparison uses per-quantity
error budgets stated in `evaluator/tolerances.py`; any accept/reject or ranking
decision that flips inside its budget is DECISION-SENSITIVE and fails strict mode.

## Reproducibility

`bootstrap.sh` pins: conda-forge scip 10.0.1 (exact mode compiled in — verified),
gmp 6.3.0, mpfr 4.2.2; VIPR commit
`30f2951d1e90e47afa821bdd1b12b82246656c42`. `/tmp` on this machine is noexec,
so all tooling lives under `verification/.toolchain/`. Every run records versions
and hashes; two runs on the same instance must produce identical `verdict.json`
(modulo timestamps), and the certificate check makes the optimality claim
independent of SCIP itself.
