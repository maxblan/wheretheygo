---
name: legacy-change
description: Procedure for safely changing code that has no trustworthy tests - characterize current behavior, choose a seam, break the blocking dependency, change behavior, then improve locally. Use when modifying untested or poorly understood code, when tests are missing, slow, or cannot run, when a class or function is hard to instantiate or observe, or when a change feels risky because the current behavior is unclear.
when_to_use: no tests, untested code, "I don't know what this does", hard to test, can't instantiate, hidden dependency, global state, characterization test, seam, "should I rewrite this"
---

# Legacy change

Source: Feathers, *Working Effectively with Legacy Code*.

Any area without trustworthy tests is legacy code — including code written five minutes ago.
The goal of this skill is to regain control *before* improving design.

## The loop

1. **Identify the change point.** The narrowest place the behavior must differ.
2. **Check existing protection.** Does a test actually exercise this behavior? Run it and watch it
   fail for the right reason. Assume nothing from a test's name.
3. **State the delta.** Write down what behavior changes and what must stay identical. Characterize
   uncertain behavior instead of silently fixing it — if consumers may rely on ugly behavior,
   capture it and mark it.
4. **Add characterization tests.** Trace effects outward from the change point through return
   values, mutated fields, calls to collaborators, and outputs. Test at the nearest pinch point
   that lets you observe the effect.
5. **Find or create the smallest seam.** Be explicit about whether the seam is for *sensing*
   (observing what happened) or *separation* (substituting a collaborator).
6. **Break the one dependency that blocks feedback.** Match the technique to the actual barrier:
   parameterize a constructor or method, extract an interface, encapsulate a global, inject the
   clock/RNG/env read, or subclass-and-override. Do not break dependencies that are not in the way.
7. **Change behavior.** Now, and only now.
8. **Refactor locally.** Leave the touched area more testable than you found it.

## Rulings

- **A rewrite is not a first move.** Take the smallest sprout method, sprout class, wrap method,
  wrap class, or extract-and-override step that makes today's change safe. Rewrite only when the
  user explicitly asks for one or it is demonstrably safer.
- **Confine edits to the change point.** Do not clean up, reorganize, or modernize the surrounding
  module while fulfilling a request. This overrides the general "leave it better" pressure: better
  means *more testable here*, not tidier everywhere.
- **Keep the three kinds of edit separate.** Behavior change, structural refactoring, and cosmetic
  cleanup never share one indistinguishable patch.
- **Indirection to gain testability is justified** even when it hides no complexity — this is the
  one place the "every abstraction must earn its interface" rule yields. But mark temporary seams
  (public-for-test, subclass-only, sensing variables) with a cleanup obligation, and do not let a
  test-shaped seam become the permanent public interface.
- **Do not add hidden dependencies while you are there.** No new globals, statics, ambient clock or
  RNG reads, or direct I/O in code you touch. Take them as parameters.

## In this repo

- The pure core (`crates/socha-runtime`, `crates/seasons/piranhas`, `crates/socha_game_api`) is
  already seam-friendly: it is synchronous and takes commands, so characterize it by driving
  commands and asserting on effects rather than by mocking.
- The async edges (`crates/socha-transport-tcp`, `crates/socha-http-api`) hide time and sockets.
  `crates/socha-transport-tcp` already uses `#[cfg(test)]` timeout constants — extend that pattern
  rather than inventing a new injection mechanism.
- `apps/socha-server/src/lib.rs` exposes `TestServerHandle` and `socha-http-api` exposes
  `test_router()`. Use these existing seams before creating another.
- Cross-language behavior is pinned by `tests/fixtures/cross_language_contract.json`. That fixture
  is a characterization test for the wire protocol; treat changing it as a behavior change.

## Depth

- Full technique list matched to barriers (link seams, extract implementer, instance delegator,
  parameterize constructor, and the rest): `.claude/books/working-effectively-with-legacy-code/working-effectively-with-legacy-code.md`
- Once tests exist and you are restructuring, switch to `/refactoring-pass`.
