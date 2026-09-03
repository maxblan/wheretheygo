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
  toolchain decision (SCIP 10 exact + VIPR + viprchk, all verified runnable here).
- `docs/correctness-claims.md` — every claim and its current status. **That file is
  the only place allowed to say what has been proven.**
- `docs/scientific-model-review.md` — the literature the models are judged against.

## Layout

```
verification/
  README.md            this file
  bootstrap.sh         installs the project-local toolchain (micromamba → SCIP 10
                       exact, builds viprchk); idempotent; no sudo; ~10 min
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
| S1 stop terms | AccumulateStop/ModeTerms (T3/T6/T7) + kernel boundary cases | bit-exact re-evaluation |
| S1/S5 order stats | SelectKth / PositivePercentile vs sorted reference | complete enumeration (bounded family) |
| S2 sites | output feasible (candidates, separation, budget) + greedy-faithful | exact re-evaluation |
| S2 sites | exact optimality gap vs the declared reference objective | SCIP exact + VIPR certificate, independently checked; cross-checked by enumeration on small instances |
| S4 lattice paths | returned path is a minimum-cost path | exact distance-label certificate (solver-free), checked by two independent checkers — a Python one and a **Lean 4 executable whose soundness is machine-proved** (`Verify.PathCert.check_sound`; the ℚ→ℤ scaling covered by `Verify.Scaling.check_scaled_sound`) |
| S4 corridor growth | GrowCorridor/PeelFlow/DecayNovelty faithful to the spec | bit-exact independent replay (corridors, blocks, full flow/novelty arrays per round) |
| S5 stops | offsets/gaps/must-call/termini/floor invariants | exact re-evaluation |
| S6 mode | gate cascade re-evaluation | exact re-evaluation |
| S7 routing/credit | itinerary optimality, boardings, credit formula | exact recomputation (ties reported) |
| S7 line set | exact gap of the greedy set vs the best set | complete enumeration (bounded instances) |

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

A run writes `runs/<instance>/<utc-stamp>/` with: `versions.json` (tool versions,
hashes of instance, model, certificate), solver log, `.vipr` certificate,
`viprchk.log`, subject/evaluator outputs, and `verdict.json`.

## Exit-code contract

`run.py` (and therefore `make verify-all`) exits 0 **only** when, for every
instance:

1. the instance validates against the schema (canonical form, hashes match),
2. the reference model was solved to proven optimality,
3. the optimality certificate was independently verified (`viprchk`, non-trivial:
   ≥ 1 derivation) — or, for the enumeration path, the enumeration completed over
   the entire declared search space,
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
gmp 6.3.0, mpfr 4.2.2; vipr checker built from a pinned commit. `/tmp` on this
machine is noexec, so all tooling lives under `verification/.toolchain/`. Every run
records versions and hashes; two runs on the same instance must produce identical
`verdict.json` (modulo timestamps), and the certificate check makes the optimality
claim independent of SCIP itself.
