# Verification architecture

Goal: validate the mod's stop-placement and route computations with deterministic,
machine-checkable evidence, without touching production code. The verification code
lives in `verification/` and re-implements everything it checks — it never imports or
copies logic from `dotnet/` (the one deliberate exception is the *subject runner*,
below, whose entire purpose is to execute the unmodified code under test).

## Components and data flow

```
                    +--------------------------+
   instance JSON -->| subject-runner (C#)      |--> solution JSON (mod's answer)
   (canonical,      |  links dotnet/*.cs pure  |
    versioned)      |  files UNCHANGED         |
        |           +--------------------------+
        |
        |           +--------------------------+
        +---------->| refmodel (Python, exact) |--> MIP/LP files + model hash
        |           |  Fraction/int only       |
        |           +--------------------------+
        |                      |
        |                      v
        |           +--------------------------+
        |           | solver (exact mode)      |--> reference solution +
        |           +--------------------------+    optimality certificate
        |                      |
        |                      v
        |           +--------------------------+
        |           | independent cert checker |--> VERIFIED / REJECTED
        |           +--------------------------+
        |
        |           +--------------------------+
        +---------->| evaluator (Python, exact)|--> feasibility + exact objective
   solution JSON -->|  independent of refmodel |    of the mod's solution
                    +--------------------------+
                               |
                               v
                    +--------------------------+
                    | compare + report         |--> exit 0 iff all checks pass
                    +--------------------------+
```

### 1. Instance source

Two sources, same schema:

- **Synthetic instances** (`verification/instances/`): deterministic generators for
  bounded cases — these are what complete enumeration and the counterexample search
  run on.
- **Game exports** (implemented 2026-09-03, `dotnet/SuitabilityVerificationExport.cs`):
  `Options → Export verification instance` writes canonical instances of the live city
  to `…\Cities Skylines II\ModsData\StationSuitabilityOverlay\verification`. Three
  files per press — `heatmap_walk` (the access pass's own inputs plus a sample of its
  terms), `sites_walk` (the real score field on network nodes), `road_times`,
  `coverage` and `lineset_time` (journeys, the real transit graph, the candidate
  pool and the mod's chosen set). Read-only: nothing in the export changes
  what the mod computes.

  The design point that makes the export worth trusting is *when* it captures: the
  inputs are copied inside `StartCompute`, out of the values being handed to the job,
  and the outputs are the terms that job produced. Re-collecting at export time would
  have been simpler and wrong — the input collections are rebuilt on their own timers,
  so an export could otherwise have described a city the exported terms were never
  computed from.

  Sampling: the whole grid would be a quarter-million cells per term, so the export
  carries a deterministic sample — one stride over every cell (which covers the
  unbuildable gate) and one over the buildable ones — with the chosen indices written
  into the instance, so the pipeline never has to know the rule.

**Canonical form**: UTF-8 JSON, keys sorted, arrays in a documented stable order
(stops by index, edges by (a, b, cost) lexicographic, zone flows by (origin, dest)),
`InvariantCulture` formatting, every float carried as the exact shortest
round-trip decimal string of its binary32 value plus (redundantly) its raw bits as an
integer — the reference side parses the bits, so no decimal parsing ambiguity exists.
The file embeds `schema_version`, `producer`, and is hashed (SHA-256) into every
downstream artifact.

### 2. Subject runner (`verification/subject/`)

A small C# console project that **links** (does not copy) the six pure files exactly
as `tests/SuitabilityScoring.Tests` does, reads an instance JSON, drives the pure
entry points (`FindTopSites`, `AccumulateWalkDistance`, `GrowCorridor`, `TracePath`
semantics via `DijkstraWorkspace`, `SuitabilityTransit.BuildWithZones`/`SuitabilityLineSet.Solve`,
`PlanCallingPoints`/`SelectCallingPoints`, `TransitModes.ChooseMode`) with the same
argument wiring the ECS half uses (documented per call in
`docs/formal-specification.md`), and writes a solution JSON. This is the *system
under test*; nothing in it is trusted by the verifier.

Scope note: stages that live only in the ECS half (ECS gathering,
`ScoreCandidates` orchestration) are re-driven by the subject runner following the
specification's argument-wiring; where the runner re-implements orchestration glue,
that glue is part of the trusted-to-be-faithful boundary and is listed below.

### 3. Reference model generator (`verification/refmodel/`)

Python, integers and `fractions.Fraction` only, no floats in any model coefficient.
Emits:

- **P-SITES** (S2): max Σ s_i x_i, s.t. x_i + x_j ≤ 1 for conflicting candidate
  pairs (Chebyshev < m), Σ x_i ≤ K, x ∈ {0,1}. Exact rational objective
  coefficients (scaled to integers by the common denominator — exact, documented).
- **P-PATH** (S4 lattice): shortest path as LP/MIP or, preferably, combinatorial
  certificate (see below) — the solver is not required for a shortest-path proof.
- **P-LINESET** (S7): NOT emitted as a MIP (the objective is routing-defined and
  non-additive); handled by complete enumeration of feasible subsets with the exact
  evaluator (bounded to ≤ k′ lines when the pool is large, and reported as such).

### 4. Solver + certificate

Preferred: SCIP in exact solving mode producing VIPR certificates, checked by the
independent `viprchk`. Fallback hierarchy if unavailable in the environment (decided
in the Toolchain section below): complete enumeration in exact integer arithmetic
(for P-SITES within enumerable bounds), plus a floating-point MIP solver as a
*non-certified secondary witness* clearly labelled as such.

### 5. Independent evaluator (`verification/evaluator/`)

Python, exact arithmetic, shares **no code** with refmodel (separate implementation
of feasibility and objective; the comparison of the two is itself a cross-check).
Checks the mod's solution JSON against the instance:

- S2: candidates valid (positivity, local-max, tie-break), separation, count, greedy
  faithfulness under the declared order, exact objective value.
- S4: path connectivity, edge validity, cost, shortest-path certificate via exact
  Dijkstra distance labels (d(u)=0, relaxation inequalities, tightness on path).
- S5: offsets/gap/must-call/floor invariants.
- S6: gate cascade re-evaluation.
- S7: transit-graph construction re-derived, per-pair shortest itineraries in exact
  ℚ, credit re-computation; set objective for enumeration.

Float↔ℚ comparison policy: the subject's float outputs are compared against exact
values with a per-quantity error budget derived from operation counts; any *decision*
(gate, ranking, keep/skip) that flips within the budget is reported as
DECISION-SENSITIVE and fails the strict gate unless whitelisted per instance with a
recorded reason.

### 6. Orchestration (`verification/run.py` + `make -C verification`)

One entry point per instance: validate schema → subject → refmodel → solve → check
certificate → evaluate → compare → write `runs/<instance>/<timestamp>/` containing
tool versions, config, input hash, model hash, certificate, solver log, results,
and a `verdict.json`. Exit code 0 **only** when: input valid ∧ reference solved ∧
certificate independently verified (or enumeration complete) ∧ mod solution feasible
∧ its claimed optimality level confirmed. Any skipped stage ⇒ non-zero.

## Trust / threat boundary

Components that must still be trusted after a green run:

| Component | Trusted for | Mitigation |
|---|---|---|
| ECS data gathering (game → arrays) | Faithfulness of real-save instances | Narrowed, not removed: the exporter copies the job's own inputs at the moment the job receives them, so a drifted second collection cannot be mistaken for the real one. The gather itself (ECS components → those arrays) stays trusted and is out of scope offline |
| The export's wire format | Producing what the pipeline can load | Golden-vector test in the offline harness pins the C# canonical form and digest against `canonical.py` (claim CX.5); on load the pipeline recomputes the digest and refuses a file it cannot reproduce |
| Subject runner glue | Wiring arguments as the ECS half does | Wiring table in formal-specification.md, reviewed against code; kept minimal |
| Instance generator | Representativeness of synthetic instances | Property-based generation + adversarial hand-built cases; generators seeded and versioned |
| Refmodel generator | Encoding spec → MIP correctly | Cross-checked against the independent evaluator on every instance (candidate sets must agree; on small instances the evaluator's own enumeration must reproduce the certified optimum) |
| Evaluator objective (S7) | Being the declared reference objective | The enumeration reuses it (subset iteration only); its independence cross-check is against the subject (mod code), not a second Python implementation |
| Solver | Only when no certificate is produced | Certificate mode preferred; enumeration path removes the solver entirely |
| Certificate checker (viprchk) | Correct checking | Independent codebase from the solver; small; pinned version + hash |
| Python interpreter / .NET runtime | Correct arithmetic | Pinned versions recorded per run |
| The comparison script | Correct verdict aggregation | Deliberately small; asserts on its own invariants; both evaluator and refmodel values must agree before any PASS |

## Toolchain decision (verified in this environment, 2026-09-03)

Criteria: open source, deterministic, exact integer/rational arithmetic,
machine-checkable proof objects, independent checker, reproducible pinning. A bare
solver status `OPTIMAL` without an independently checkable certificate never counts
as more than a secondary witness.

**Primary (certified MILP)** — proven end-to-end in this environment, project-local,
no sudo:

- micromamba 2.9.0 (static binary) → conda-forge **SCIP 10.0.1** (`hd8b5c82_0`),
  which is compiled **with** exact solving (SoPlex 8.0.1, GMP 6.3.0, MPFR 4.2.2,
  Boost 1.88). SCIP 9.x does not have the feature; the PyPI PySCIPOpt 6.2.1 wheel
  bundles a SCIP 10 **without** exact support (verified: `enableExactSolving`
  raises) and must not be used for the certified path.
- Activation: `set exact enable TRUE`, `set certificate filename <f>.vipr`, and
  **presolving off** (`set presolving emphasis off`): with presolve on, an instance
  solved in presolve emits a `DER 0` certificate that verifies nothing. The runner
  additionally rejects any certificate with zero derivations.
- Certificate check: **`viprcomp --soplex=off` then `viprchk`** (github.com/scipopt/vipr,
  built locally). The completion step is not optional on real models: SCIP 10 writes
  VIPR **1.1** certificates whose reasons may be `{ lin weak { 0 } … }` — linear
  combinations that only *weakly* dominate their constraint, with the completing
  bounds left implicit — and `viprchk` implements the **1.0** grammar, so it rejects
  them with a *syntax* error that reads exactly like a broken proof and is not one.
  `viprcomp` completes those steps (no LP needed for weak ones, hence `--soplex=off`).
  Found the hard way on the first real export: 87 derivations, 4 of them weak.
  Building `viprcomp` needs SoPlex — and therefore gfortran (PaPILO's config), zlib,
  and the conda compilers, because that config hands the system linker `/lib64/…`
  sysroot paths that do not exist on Debian-family layouts. `bootstrap.sh` does all
  of it and fails if `viprcomp` is missing.
- **A verified certificate is not automatically an optimality proof.** SCIP writes two
  files: the certificate proper, whose `RTP range v v` is two-sided, and a `…_ori`
  twin with `RTP range -inf v` and `DER 0`, which merely restates the primal bound.
  `viprchk` says "Successfully verified." for the latter too. The pipeline therefore
  requires a two-sided range that *pins the claimed optimum*, and rejects any
  certificate with zero derivations. A formally verified VIPR checker exists in
  HOL4/CakeML as a further escalation step (not built here).
- Python driving (optional): PySCIPOpt from git master built against the conda SCIP
  (`SCIPOPTDIR=<env>`); in exact mode models must be loaded via `readProblem`
  (files), not built via `addVar`. The pipeline instead writes `.lp`/`.mps` files
  and shells out to the `scip` CLI — fewer trusted components.

**Secondary witnesses (uncertified, cross-checks only)**: highspy (HiGHS) float MIP;
exhaustive enumeration in Python `int`/`fractions.Fraction` (which for bounded
instances is itself a *proof by complete enumeration*, independent of any solver).

**SMT side (optional escalation for logical properties)**: pip cvc5 1.3.4 with
proof production → CPC proof checked by **ethos** (built locally; observed
"correct"), carcara (Alethe) as an independent second checker ("holey" on
arithmetic exports — known cvc5 limitation; treated as partial evidence only).

**Lean 4** (built 2026-09-03): pinned `leanprover/lean4:v4.15.0` via `elan`,
project-local (`verification/.toolchain/elan`), **mathlib-free** (core tactics
only — `omega`, `simp`), so the build is minutes, not hours. `verification/lean/`
holds machine-checked proofs (no `sorry`; axioms limited to
propext/Quot.sound/Classical.choice) for: the shortest-path certificate checker's
soundness (`check_sound` — the checker is compiled into an executable the
pipeline runs on every lattice-path certificate), the calling-point plan
invariants, the keep-rule invariants including the all-zero degenerate case, and
the boardings/transfer-cost arithmetic. See the "Formal bewiesen" section of
`docs/correctness-claims.md` for the exact statements and the (small,
documented) unverified glue: JSON parsing, Python `Fraction` numerator
extraction, and first-match edge lookup (sound but incomplete under parallel
edges). The rational→integer scaling itself is formally covered:
`Verify.Scaling.check_scaled_sound` proves that accepting the k-scaled
certificate decides minimality of the original instance (CF.7).
Install/build: `make -C verification lean`. The Phase-7 non-goal stands: no
attempt to model the game or Unity.

**Environment caveats** (encoded in the scripts): `/tmp` is mounted noexec — venvs,
cargo/pip build dirs and binaries must live under the repo
(`verification/.toolchain/`); no sudo available; all installs project-local.

**Pinned versions** (recorded per run in `runs/*/versions.json`): micromamba 2.9.0;
conda-forge scip 10.0.1 hd8b5c82_0, gmp 6.3.0, mpfr 4.2.2, cmake 4.4.3; vipr @
scipopt/vipr master (commit recorded at build); Python 3.12.3; .NET SDK as
installed. A trial toolchain from the survey sits in `.verify-toolchain/` (1.1 GB,
disposable); the pipeline's own bootstrap installs into
`verification/.toolchain/`.
