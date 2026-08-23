---
name: construction-review
description: Statement- and data-level construction discipline plus a pre-ship self-check - control flow, data declarations, trust boundaries, assertion-vs-error triage, and evidence before optimizing. Use when implementing a non-trivial routine, when about to report a change as done, when self-reviewing an implementation, when debugging, or when deciding whether an optimization is justified.
when_to_use: implement this, before I finish, self-review, pre-ship check, is this done, debugging, why is this slow, optimize this, defensive checks, assert or error, magic number, nesting, control flow
---

# Construction review

Sources: McConnell's *Code Complete* (primary — the only source in this set that governs
statement- and data-level decisions) and Hunt & Thomas's *The Pragmatic Programmer* (operating
style: knowledge ownership, evidence, automation).

## While implementing

**Data.** Purpose-revealing names, smallest workable scope, deliberate initialization. Closed sets
become enums. Magic numbers and sentinels become named constants at their declaration. Units,
ranges, and ownership are visible where the value is declared, not inferred by the reader.

**Control flow.** Guard clauses over nesting. A named predicate over a compound boolean. Clear
loop initialization, termination, and update. No expression whose value depends on a side effect.
Prefer the plainest flow that expresses the logic over the shortest.

**Trust boundaries.** At every point where data arrives from outside — decoded protobuf, socket
bytes, HTTP body, SQL row, FFI argument — decide explicitly what is validated, what is rejected,
what is recovered from, and what is asserted. Never continue from an impossible state.

**Error triage.** Programmer errors and broken invariants get `assert`/`debug_assert`. Expected
external and domain failures get typed error variants. Do not mix the two, and preserve
diagnostic context when propagating or wrapping — an error that loses which room, session, or
move failed is a defect.

**Knowledge ownership.** Every fact has exactly one authoritative place in the code. Derive,
generate, or test-assert every other copy rather than hand-writing it twice. In this repo that
bites hardest across the `.proto`, the Rust types, and the Python and Java bindings.

## Evidence

- Do not depend on behavior you cannot explain. Reproduce it, isolate it, explain it, then fix and
  verify. Never ship a fix you only observed to make a symptom disappear.
- Measure before optimizing. No cache, buffer, extra index, clone-avoidance, or `unsafe` on
  intuition. Set a target, measure, change one thing, remeasure, and keep the optimization behind
  the existing interface.
- Automate work you have now done manually twice.

## Rulings

- **Comments are a construction tool, not an admission of failure.** Write the intent, contract,
  invariant, or rationale. Delete narration of what the code already says. This resolves against
  Clean Code's stricter position; see `.claude/rules/engineering-baseline.md`.
- **Interface/implementation separation is not automatically a good.** Code Complete treats
  encapsulation as a virtue in itself, which reads as licence to add a trait per struct. It does
  not: an abstraction still has to hide something. See `/module-design`.
- **Process weight scales with risk, not with rules.** Skip any step here that does not lower
  defect risk for this specific change.

## Pre-ship check

Before reporting a change as done:

- [ ] Did I run the relevant tests, not just `cargo check`? (`cargo test -p <crate>` at minimum;
      `./scripts/validate-release validate` when bindings, the wire format, or xtask are touched.)
- [ ] Does a test fail without this change?
- [ ] Are rejected transitions, boundary values, and malformed input covered — not only the happy
      path?
- [ ] Are trust boundaries, assertions, and error variants deliberate rather than inherited?
- [ ] Did the diff stay inside what the task required?
- [ ] Did I follow the touched crate's existing conventions instead of a local dialect?
- [ ] Am I reporting what actually happened — including tests I did not run, gaps I left, and
      shortcuts I took?

## Depth

- Full construction rule set: `.claude/books/code-complete/code-complete.md`
- Operating style, DRY-as-knowledge, tracer bullets, automation:
  `.claude/books/the-pragmatic-programmer/the-pragmatic-programmer.mini.md`
