---
name: architecture-boundaries
description: Decides which crate or layer a piece of code belongs in and which direction dependencies may point - keeping transport, storage, and vendor types out of the authoritative core. Use when adding a crate or module, choosing where a responsibility lives, adding an import that crosses a subsystem, introducing a trait for substitutability, wiring something in the binary, or when logic has drifted into a handler, adapter, or SQL statement.
when_to_use: which crate should this go in, new module, dependency direction, circular dependency, "can X import Y", layering, port, adapter, composition root, wiring, logic in the handler, shared/common/utils module
---

# Architecture boundaries

Sources: Martin's *Clean Architecture* (primary — dependency direction is a mechanical, checkable
property) and Fowler's *Patterns of Enterprise Application Architecture* (selection forces for the
storage and protocol seam). PoEAA's ORM and presentation catalog does not apply to this repo; its
useful half is where persistence access, commit ownership, and protocol mapping live.

## The checks

**Direction.** Before adding an import to policy/core code, check which way it points. The core
must not import transport, persistence, HTTP, serialization, or vendor SDK types. In this
workspace that means `socha-runtime`, `socha_game_api`, and `crates/seasons/**` must not reach
prost-generated, sqlx, axum, tokio, pyo3, or JNI types.

**Purity.** No I/O, clock reads, env or config reads, randomness, or network calls in policy code.
Take them as parameters.

**Altitude.** Place each piece of code at the highest level matching its responsibility: policy,
orchestration, translation, or infrastructure. Business branching never lives in an edge handler,
listener, adapter, binding shim, or SQL statement.

**Ownership.** Name the single responsibility a new file or type owns — transport, workflow,
domain rule, storage, commit boundary, concurrency — and move everything else out.

**Construction.** Declare a trait in the crate that *consumes* it, not next to its implementation.
Construct concrete infrastructure only in `apps/socha-server` or `apps/local_runner`.

## Rulings

- **A boundary must be earned, not introduced on suspicion.** Clean Architecture says "when in
  doubt, introduce a boundary sooner" and wants ports for clocks, IDs, and gateways. That is
  overridden here: add a trait or port only when a second real implementor exists today, or a test
  genuinely needs substitution. Until then pass a value or a function. The *direction* rule is
  absolute; the *mechanism* for satisfying it should be the smallest one that works.
- **No layering theater.** Delete or refuse any type, trait, or function that only forwards to the
  next one without adding policy, translation, or a boundary. Five layers that forward method
  calls is the failure mode both books name explicitly.
- **Rules beat patterns at the storage seam.** Where PoEAA and the DDD books conflict — and they
  do, on where behavior lives — invariants and lifecycle rules go in the type that owns the state,
  never in a service that only forwards to persistence. PoEAA's vocabulary applies to the edge
  (mapping, commit ownership, identity scope), not to the game rules.
- **Never mirror a foreign schema inward.** Protobuf messages, SQLite rows, and HTTP payloads
  carry data only. Do not shape an internal type after them; translate explicitly at the edge.
  This is also why a "shared" or "common" crate is not the answer to a new cross-cutting concern —
  put the concept in the crate that owns it.
- **Keep the existing crate layout as given.** `protocol`, `transport`, `persistence`, and
  `runtime` are genuinely different volatility axes, so the "technical buckets are a smell" line
  does not apply to this workspace. Inside a crate, group modules by concept (room, session,
  timeout, replay, rules) rather than by kind.
- **When a violation is unavoidable**, keep it in the outermost layer possible, name it in the
  code or the change notes, and do not replicate the pattern elsewhere. This covers
  dependency-direction and layering violations only. It never licenses I/O, a clock read,
  randomness, task spawning, or instrumentation inside `socha-runtime`, `socha_game_api`,
  `socha_replay`, `socha-season-registry`, or `crates/seasons` — if a change appears to need one
  of those, the design is wrong. Stop and report it.

## Division of labour with `/module-design`

`/module-design` decides **whether** a trait, module, or crate should exist at all — it must hide
more than it exposes. This skill decides **where** it goes, who declares it, and which way the
imports point. Direction is mandatory; the mechanism for satisfying it is the smallest one that
works, which is usually a parameter rather than a port.

## In this repo

The boundaries are already recorded — read them before moving one:

- `docs/architecture/README.md` splits the competition hot path (season rules, runtime, transport,
  protocol, replay) from platform and SDK surfaces (client core, bindings, HTTP API, xtask).
- ADR 0001 defines the competition/platform split, ADR 0003 the thin-bindings rule, ADR 0004 the
  runtime lifecycle and timing authority. `docs/architecture/adr/`.
- `socha-season-registry` is the one place seasons fan in. `apps/socha-server` is the composition
  root and stays thin — it wires TCP, HTTP, and SQLite and delegates.
- Both the TCP transport and the HTTP API feed the *same* `ServerRuntime`. Adding a second path
  that mutates game state outside the runtime breaks the central invariant of the design.

## Depth

- Full dependency-rule, boundary, and composition guidance:
  `.claude/books/clean-architecture/clean-architecture.md`
- Storage/protocol seam selection forces (ignore the ORM and presentation catalog):
  `.claude/books/patterns-of-enterprise-application-architecture/patterns-of-enterprise-application-architecture.mini.md`
- For *what the concept is called and which context owns it*, use `/domain-modeling`. For *whether
  this interface hides enough to exist*, use `/module-design`.
