---
name: data-and-consistency
description: Data ownership, idempotency, and versioned-contract discipline - name the source of truth, make retried and replayed work safe, and evolve wire and storage formats without breaking old readers. Use when changing a protobuf message, a stored schema, a shared test fixture, an enum or status value crossing a process or language boundary, a persistence or replay path, or any handler reachable by reconnect, retry, or duplicate delivery.
when_to_use: protobuf, .proto, schema change, migration, wire format, serialization, backwards compatibility, replay, idempotent, duplicate delivery, reconnect, source of truth, derived data, cache invalidation, event ordering, sqlite, sqlx
---

# Data and consistency

Source: Kleppmann, *Designing Data-Intensive Applications*. Scoped here to what a single-node
game platform actually faces: wire contracts, replay, persistence, and duplicate delivery.

## Before you write state

1. **Name the single owner of the fact.** Every other copy — cache, index, projection, replay
   frame, DB row, in-memory snapshot — is *derived* and must be rebuildable from the owner.
2. **Separate the four states.** Accepted, persisted, applied, acknowledged. A returned `Ok`, a
   sent frame, or an elapsed timeout is not proof of what the other side did.
3. **Scope the ordering you actually need.** Per room, per session, per stream, per entity
   history. Do not assume global order, and keep ordering-sensitive logic inside its scope.
4. **Use monotonic time for correctness.** Elapsed time, deadlines, and timeouts use `Instant`.
   Wall-clock time is for display and logs only, never for ordering or decisions.

## Idempotency

In `socha-transport-tcp`, `socha-http-api`, `socha-persistence`, and `socha-client-core`, any
handler reachable by retry, reconnect, or duplicate delivery must be idempotent — via a dedup key
or a naturally idempotent state transition. "It probably won't arrive twice" is not a design.

The reachable-twice paths here are reconnect after a dropped TCP session and a client resending a
move after a missed acknowledgement. `SubmitMove` already carries `request_id` and
`expected_state_version` — use them; do not add a second dedup scheme.

**This does not apply to `socha_replay`.** Re-applying every recorded move is the verification
mechanism, not duplicate delivery. Never add a dedup key or an "already applied, skip" guard
there — it would defeat frame-exact reproduction, and the golden replay tests are what would
appear to fail.

## Versioned contracts

Treat every format that crosses a process, language, or version boundary as a contract:
`.proto` messages, the cross-language JSON fixture, SQLite columns, HTTP payloads, enum values,
and `REPLAY_FORMAT_VERSION`.

- **Add fields and variants. Never repurpose or renumber existing ones.** A protobuf field number
  is permanent. An enum discriminant that changes meaning silently corrupts old replays.
- **Plan for mixed versions explicitly**: old reader / new writer, new reader / old data,
  in-flight messages, and stored replays from a previous build.
- **A rename is a wire change.** Renaming a field in `.proto` or a key in the shared fixture is
  not a refactoring, and `/refactoring-pass` will not cover you.
- Changing the wire format means updating, in one change: the `.proto`, the Rust encode/decode,
  `tests/fixtures/cross_language_contract.json`, and the Rust, Python, and Java parsers that each
  hand-parse that fixture.
- Bumping `REPLAY_FORMAT_VERSION` requires deciding what happens to replays already on disk.

## Rulings

- **Derived copies are legitimate; unowned copies are not.** This overrides the general DRY
  pressure: a cache, projection, or denormalized field is fine as long as the owner is named and
  the copy is rebuildable. What is forbidden is two places that both think they are authoritative.
- **Do not hide failure behind an abstraction.** An interface that conceals version skew, partial
  failure, or an unknown outcome is worse than one that surfaces it, even though the general rule
  favors deep modules that hide complexity.
- **Storage and wire shape are design inputs, not afterthoughts.** Consistency semantics, the
  durability point, and schema evolution are decided before the write path is written.
- **Commit ownership is visible at one caller.** The use-case owner decides when writes land — the
  runtime transition for gameplay, the HTTP or CLI handler for operator actions. Functions in
  `socha-persistence` either take an explicit transaction or perform one self-contained write;
  they never commit on a caller's behalf. Hold no lock, transaction, or pooled connection across
  an `await` on anything else.
- **No repository trait, Unit of Work, or aggregate wrapper.** `socha-persistence` is plain
  functions over explicit SQL returning owned types. Enterprise persistence patterns and DDD
  aggregate patterns stack into pure ceremony here; both are switched off.
- **Load behavior is explicit.** No hidden or lazy query inside a loop or a serialization path.
  One in-memory representation per entity per unit of work, so two copies cannot diverge.

## In this repo

- `socha_replay` verifies that replaying recorded moves reproduces every recorded frame and the
  result. That check is the repo's strongest correctness invariant — anything that makes
  `apply_move` non-deterministic (clock, RNG, hash iteration order, float formatting) breaks it.
- Seasons are seeded (`generate_setup(seed)`); the seed is the owner of initial state, and the
  stored setup plus `initial_state_hash` is the proof.
- `socha-persistence` holds replays, admin sessions, audit events, and room history. The runtime
  is the source of truth for live state; SQLite is derived and must never become a second one.

## Depth

- Full rule set covering replication, partitioning, isolation, stream processing, and fault models
  — most of it beyond this repo's single-node scope, useful when that changes:
  `.claude/books/designing-data-intensive-applications/designing-data-intensive-applications.md`
- For the failure *mechanics* (timeouts, retries, bounded queues), use `/reliability-review`.
