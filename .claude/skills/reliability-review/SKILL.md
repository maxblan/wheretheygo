---
name: reliability-review
description: Production failure-semantics review - bounded waits, retry discipline, back pressure, blast-radius isolation, and diagnosability, applied to code that must survive real operation. Use when touching sockets, connection handling, timeouts, retries, queues, channels, pools, caches, background tasks, health checks, startup, shutdown, migrations, or admin/control endpoints, and when diagnosing a hang, leak, overload, or cascading failure.
when_to_use: timeout, retry, hang, deadlock, backpressure, queue full, unbounded channel, connection drop, circuit breaker, load shedding, graceful shutdown, health check, "it stopped responding", memory grows, cascading failure
---

# Reliability review

Source: Nygard, *Release It!*. A passing happy path is not production readiness.

## The questions that must have answers

For every change on this list, answer in the same change — not later.

**Any wait you do not fully control** (socket read/write, `await` on a remote call, lock, channel
`recv`, pool checkout, subprocess, task join):
- What is the explicit deadline? Library defaults and unbounded blocks are not answers.
- What happens to the caller when it expires? Who observes the expiry?

**Any queue, buffer, channel, pool, cache, or collection-returning API:**
- What is its bound, and what happens when it is full — block, drop, shed, or fail?
- Who observes saturation?

**Any retry:**
- Is the operation safe to repeat for both caller and provider?
- Bounded attempt count *and* total time? Backoff with jitter?
- Is it retried at exactly one layer? Stacked retries at three layers is a retry storm.
- Validation errors and permanent failures are never retried.

**Any error path:**
- No swallowed failures. No bare `let _ =` on a fallible call, no generic "something went wrong".
- Either propagate a typed error or log the dependency, operation, outcome, and correlation id.

**Any external input or external response** (decoded frames, HTTP bodies, DB rows, FFI arguments):
- Validated for shape, status, and business plausibility *before* it reaches persistent state,
  caches, or queues.

**Any startup, shutdown, migration, one-time job, or admin control:**
- Restartable or idempotent? Authorized? Auditable? Stoppable? Does it have a rollback path?

## Rulings

- **Instrumentation at a boundary is not noise.** Timeouts, validation, structured logging, and
  metrics belong inline at boundaries and failure points, even though the general rule is to keep
  the happy path clean. Keep them out of the *inner* common path, not out of the edge.
- **Name the failure mode before adding a mechanism.** Add a circuit breaker, bulkhead, extra
  pool, fallback, or degraded mode only when you can state the failure it defends against and the
  blast radius it limits. Otherwise this book's pattern vocabulary becomes ceremony.
- **Fail fast and degrade gracefully are both correct, in different places.** Fail fast when
  continuing would hold a scarce resource or hide unrecoverable trouble. Degrade when core service
  can survive without the failed part. Decide which one this call site is.
- **Failure semantics are designed up front, not discovered.** This is the one area where the
  emergent-design default yields: timeouts, overload behavior, and isolation are part of the
  original change, not a follow-up.

## In this repo

- `crates/socha-transport-tcp` already sets `PING_INTERVAL`, `READ_IDLE_TIMEOUT`, `AUTH_TIMEOUT`,
  `WRITE_TASK_SHUTDOWN_TIMEOUT`, `OUTBOUND_QUEUE_CAPACITY` (64) and `MAX_FRAME_BYTES` (64 KiB).
  New waits and buffers in that crate follow the same pattern: a named `const` with a value, not
  an inline literal and not an unbounded default.
- The outbound queue uses `try_send` — a full queue is a decision point, not an error to unwrap.
- `MOVE_TIMEOUT` and `MAX_LATENCY_CREDIT` in `crates/socha-runtime` are competition-authoritative.
  Changing them changes match outcomes; treat them as a rules change, not a tuning knob.
- `socha-http-api` sets a 32 KiB body limit and a 10s timeout layer. New routes inherit those —
  do not add a route that streams or blocks past them without saying so.
- Bots are untrusted clients. Every decoded frame is external input.

## Depth

- Full stability-pattern set, failure-mode-to-pattern mapping, and the operational checklist:
  `.claude/books/release-it/release-it.md`
- For duplicate delivery, replay safety, and schema evolution, use `/data-and-consistency` —
  it covers what happens to *data* when these failures occur.
