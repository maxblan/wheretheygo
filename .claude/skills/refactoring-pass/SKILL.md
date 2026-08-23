---
name: refactoring-pass
description: Behavior-preserving cleanup protocol - diagnose the smell, pick the smallest treatment, keep structural and behavioral edits separate, and stop at a defined point. Use when refactoring, cleaning up, tidying, restructuring, extracting, inlining, simplifying conditionals, or removing duplication, and when code is described as messy, tangled, bloated, or hard to change.
when_to_use: refactor, clean up, tidy, restructure, extract, inline, simplify, deduplicate, "this is messy", "hard to change", "too long", code smell
---

# Refactoring pass

Sources: Fowler's *Refactoring* (primary) and *Refactoring.Guru* (smell→treatment catalog, stop
condition). They overlap; where they differ, Fowler's step discipline wins and Guru supplies the
catalog and the termination criterion.

## Protocol

Do these in order. Do not skip step 1 — an unnamed smell produces unbounded cleanup.

1. **Name the target.** State the specific smell, the maintenance cost it causes, and the stop
   condition. If you cannot name a smell, there is no refactoring to do.
2. **Establish the safety net.** Confirm a test exercises the behavior you are about to move. If
   none does, add a characterization test first, or state the verification gap explicitly in your
   report. Never delete or weaken a failing test to finish a cleanup.
3. **Pick the smallest treatment that helps.** Rename, extract, inline, move, split a variable's
   meanings, introduce a parameter object, encapsulate a field or collection, decompose a
   conditional, add a guard clause. Prefer the named move over an improvised redesign.
4. **Take one step at a time.** Each step builds and keeps tests green. Run the relevant checks
   after every risky move: changed state flow, moved ownership, altered public signature,
   substituted algorithm.
5. **Stop.** When the named smell is materially reduced and the requested change is easy, stop.
   Record newly discovered smells; do not fix them unless they block the request.

## Rulings

These override the raw source rules where the books in `.claude/books/` disagree.

- **Length is a warning, not a limit.** Split a function when it mixes responsibilities,
  abstraction levels, or phases — not because it exceeds a line count. Chains of shallow
  extractions that add names without hiding work make the code worse, not better.
- **Behavior preservation beats opportunistic correction.** If current behavior looks wrong,
  say so and ask. Do not silently fix it inside a cleanup — including error semantics and edge
  cases that consumers may rely on.
- **Coincidental similarity is not duplication.** Do not merge two fragments that change for
  different reasons or belong to different contexts. Rule of three: tolerate the second
  occurrence.
- **Patterns are treatments, never targets.** Introduce polymorphism, state, strategy, a lookup
  table, or a null object only when the variation is real and repeated. Applying a named pattern
  without a diagnosed smell is itself a smell.
- **Do not remove indirection without checking reach.** Before inlining or deleting anything that
  looks unused, check pub API surface, FFI/JNI/pyo3 exports, serde and protobuf generated use,
  test fixtures, and test-only access. This repo has all five.

## In this repo

- `crates/socha-runtime` is the authoritative state machine and `apply_move` must stay pure —
  any extraction that introduces I/O, a clock read, or randomness into that path is out of scope
  for a refactoring.
- Golden-file tests (`xtask/tests/fixtures/`) and `tests/fixtures/cross_language_contract.json`
  encode behavior across three languages. Treat a change to those files as a behavior change,
  not a cleanup, and update the Rust, Python, and Java parsers together.
- Refactoring never edits the `.proto` wire format. That is a versioned contract — see
  `.claude/skills/data-and-consistency/`.

## Depth

- Full smell catalog, per-technique avoid-conditions, and pre-move safety checks:
  `.claude/books/refactoring-guru/refactoring-guru.md`
- Fowler's decision and trigger rules: `.claude/books/refactoring/refactoring.mini.md`
- If the code you are changing has no trustworthy tests, use `/legacy-change` instead — it is the
  procedure for regaining control before restructuring.
