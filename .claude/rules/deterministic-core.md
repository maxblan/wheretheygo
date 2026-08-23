---
paths:
  - "crates/socha-runtime/**"
  - "crates/seasons/**"
  - "crates/socha_game_api/**"
  - "crates/socha_replay/**"
  - "crates/socha-season-registry/**"
---

# Deterministic core

You are in the authoritative, replay-verified half of the workspace. The invariant is that
`apply_move` and everything on the replay path are pure functions of their inputs, so
`socha_replay` reproduces every recorded frame and the final result exactly.

**Two clock/RNG uses already exist here and are deliberately contained.** Do not "fix" them, and
do not add a third:

- `clock_origin: Instant::now()` in `ServerRuntime::new` (`runtime.rs:232`) stamps audit-log
  milliseconds (`runtime.rs:1149`). It never feeds a game decision.
- `opaque_code()` (`reservation.rs`) uses `rand::random::<u128>()` for reservation codes and
  resume tokens. Codes are handed out, redeemed, and forgotten; they are never replayed.

Everything else here takes time as an explicit `Instant` or `Duration` supplied by the caller, and
randomness as a `u64` seed on `generate_setup`.

- No clock read, RNG draw, env or config read, I/O, or task spawn inside a command handler,
  `apply_move`, setup generation, or anything that reaches a replay frame.
- Never add an RNG handle, `&mut dyn RngCore`, or `impl Rng` parameter to a season or runtime
  function. Randomness enters as a seed value, not as a generator.
- Anything with iteration-order, hash-order, or float-formatting sensitivity breaks replay
  silently. `HashMap` iteration must not decide anything observable.
- No `prost`-generated, `sqlx`, `axum`, `tokio`, `pyo3`, or JNI types in signatures here. The
  registry and the season's `protobuf.rs` are the only translation points.
- Returning effects from a command is the design, not a command/query violation. What is forbidden
  is a function that *reads* or answers and mutates observable state.
- Instrumentation belongs to the boundary crates. Do not add timeouts, retries, metrics, or
  structured logging here — the caller owns those, and deadlines arrive as command data.
- Replay re-application is exact, not deduplicated. Never add a dedup key or an
  "already applied, skip" guard to the replay path; that is the verification mechanism.
- `MOVE_TIMEOUT` and `MAX_LATENCY_CREDIT` (`runtime.rs:17-18`) are competition-authoritative.
  Changing them changes match outcomes: treat it as a rules change, not tuning.
- Never weaken, delete, skip, or regenerate a replay-frame assertion or golden file to make a
  change pass. A failure there means behavior changed — find out why.
- `socha_game_api` owns the platform vocabulary every season and the replay path share: `GameId`,
  `SeasonGame`, `Forfeit`. Adding a concept there needs the same justification as a new crate; it
  is not a dumping ground.
- Turn order is a rule, not runtime bookkeeping: the runtime asks `SeasonGame::current_player`
  rather than rotating seats. Do not reintroduce `(player_index + 1) % players.len()` anywhere.
- A season may carry derived facts on the wire (Piranhas' `largest_swarms`, `current_team`) so
  every language reads one number the rules produced. Compute them in the encoder and ignore them
  on decode, so they can never disagree with the position they came from.
