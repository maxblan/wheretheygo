---
paths:
  - "crates/socha-transport-tcp/**"
  - "crates/socha-http-api/**"
  - "crates/socha-persistence/**"
  - "crates/socha-client-core/**"
  - "crates/socha-gui-core/**"
  - "crates/socha-gui-piranhas/**"
  - "gui/**"
  - "apps/**"
---

# Boundary crates

You are in the half of the workspace that faces the outside world. Operational concerns belong
here — and only here.

- Every wait you do not fully control gets an explicit deadline: socket read/write, `await` on a
  remote call, lock, channel `recv`, pool checkout, subprocess, task join. Never a library default,
  never an unbounded block. Follow the existing pattern of a named `const`, not an inline literal.
- Every channel, buffer, queue, pool, cache, or accumulating collection gets a stated bound and a
  stated full/overflow behavior *in the same change*. An unbounded channel in the TCP actor is an
  OOM, not a slow path.
- Retry only what is safe to repeat, with a bounded attempt count and total time, backoff with
  jitter, at exactly one layer. Never retry a validation error or a permanent failure.
- Swallow nothing. No bare `let _ =` on a `Result`, no `unwrap_or_default` to silence an error
  path. Propagate a typed error or log the operation, subject, and outcome with room/session ids.
- Validate external input at the boundary — decoded frames, HTTP bodies, SQL rows, FFI arguments —
  and convert it into types that make the invalid state unrepresentable. The core does not
  re-validate.
- Hide the mechanism, expose the failure. A function crossing a process, socket, FFI, or storage
  boundary carries timeout, partial failure, and unknown outcome in its return type, and is never
  shaped to look like an in-process call. Encoding, framing, and pooling stay private.
- Hold no lock, transaction, or pooled connection across an `await` on anything else.
- Fail fast with a typed error when continuing would hold a scarce resource, corrupt state, or
  produce an unverifiable match result. Degrade only non-gameplay surfaces — operator HTTP,
  replay persistence, metrics — and never by guessing a value the runtime is authoritative about.
- These crates are adapters. They translate and they orchestrate; they hold no game rules.
- The language bindings additionally hold no season *types*. Payloads cross pyo3 and JNI encoded,
  labelled with their `game_id`, and the rules are reached through `socha_season_registry::rules`.
  A season type appearing in `bindings/**` outside the wire-contract fixture harness is a defect.
- The graphical client decides no rule. Legality, scoring, the winner, whose turn it is and whether a
  player is stuck are asked of the season through the active `GamePlugin`. A flood fill, a turn
  rotation, or a re-derived target square in `crates/socha-gui-*` or `gui/**` is a defect, not an
  optimisation. `socha-gui-core` names no season, and `gui/socha-gui-frontend` depends only on
  `socha-gui-view`, so a season type reaching either is a boundary break (ADR 0006).
- Human thinking time in the GUI comes from pausing the room, never from changing `MOVE_TIMEOUT`.
