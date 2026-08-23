---
name: module-design
description: Complexity-first design review for a module, crate, trait, or public API - does this boundary hide more than it reveals, and does the caller have to know less afterwards. Use when designing or changing a public interface, deciding whether to extract or combine, adding a trait or wrapper, choosing where a responsibility lives, naming a new abstraction, or when a change feels awkward and spreads across files.
when_to_use: API design, public interface, should I extract this, split or combine, add a trait, new module, wrapper, helper, "where should this live", "this feels awkward", change touches many files, naming an abstraction
---

# Module design

Sources: Ousterhout's *A Philosophy of Software Design* (primary, boundary depth) and Martin's
*Clean Code* (local readability). They disagree about decomposition; the rulings below settle it.

## The test every new boundary must pass

Before adding a module, crate, trait, wrapper, helper, facade, layer, option, callback, or
parameter, write one sentence: **what complexity does this hide from its callers?**

If the answer is "it forwards to the next thing", do not add it. A boundary that reveals as much
as it hides has made the system larger and no simpler.

Then check the boundary from the caller's side:
- Does an ordinary caller have to know sequencing, setup steps, internal representation, storage
  shape, wire format, transport mechanics, or caching to use it correctly? If yes, redesign it.
- Does every caller repeat the same check, conversion, or defensive handling? Pull that complexity
  down into the module that owns it — prefer a harder implementation over a harder interface.
- Can the invalid case be defined out of existence by changing the invariant or the type, instead
  of being handled by each caller?

## Rulings

Where the two sources conflict, these win:

- **Split by mixed responsibility, not by size.** A function gets split when it mixes parse,
  validate, compute, and effect phases, or jumps abstraction level — never because it crossed a
  line count. Chains of shallow extractions that add names without hiding work are a net loss.
  (Ousterhout over Martin.)
- **Readability rules apply inside a unit; depth rules apply at its boundary.** Keep the happy
  path clear, avoid a function that both answers and mutates, and avoid boolean/mode parameters —
  but do not turn those local habits into new named types and files.
- **Comments carry contracts, not narration.** Document the invariant, the contract, and the
  reason the abstraction exists. Delete comments that restate the code. A comment that keeps
  growing is evidence the boundary is in the wrong place. (Ousterhout and Code Complete over
  Clean Code's comment abolitionism.)
- **Generality is not free, and neither is overfitting.** Do not build a speculative general
  mechanism; do not overfit an interface to a single caller's current call site either. Pick the
  interface that would still be right for the second plausible caller you can actually name.
- **"So it can be mocked" is not a justification.** Pass the collaborator — clock, randomness,
  socket, storage handle — as an explicit parameter instead of inventing a trait for it. That
  delivers determinism and substitutability at zero indirection cost. Seam techniques activate
  only when a test cannot otherwise observe existing behavior in code the change must touch;
  that is `/legacy-change`, and the seam carries a cleanup obligation.

## Division of labour with `/architecture-boundaries`

This skill decides **whether** a boundary should exist. `/architecture-boundaries` decides
**where** it goes and **which way** dependencies point once it does. They are never equal
guidance on the same question: a boundary that passes the direction check but hides nothing still
must not be created.

## Before a decision that is expensive to reverse

Which crate owns a concept, a dependency direction, a wire or persisted format, timeout or
consistency semantics: name a second plausible design and say why the chosen one hides more
complexity. One sentence each.

If a local refactor could undo the decision later, implement it instead of deliberating.

## In this repo

- `SeasonGame` (`crates/socha_game_api/src/season.rs`) is the deepest interface here: seven
  associated types and pure functions hide an entire game's rules. New season-facing capability
  belongs on that trait, not in a helper crate beside it.
- `ServerRuntime` takes `RuntimeCommand` and returns `RuntimeEffect`. That command→effect shape is
  the boundary that keeps the runtime synchronous and I/O-free. Adding a method that performs an
  effect directly breaks the depth of the whole design.
- `socha-season-registry` is an enum-dispatch fan-in, deliberately not a `dyn` registry. Adding a
  season means an arm in the registry, not a new abstraction layer.
- The Rust/Python/Java client split is already decided by ADR 0003: shared logic goes in
  `crates/socha-client-core`, bindings stay thin facades. Read
  `docs/architecture/adr/0003-rust-sdk-core-and-thin-language-bindings.md` before moving logic
  across that line.

## Depth

- Full decision, trigger, and checklist rules:
  `.claude/books/a-philosophy-of-software-design/a-philosophy-of-software-design.md`
- Local readability rules: `.claude/books/clean-code/clean-code.mini.md`
- If the question is *which crate owns this* or *may this import that*, use
  `/architecture-boundaries`. If it is *what is the domain concept*, use `/domain-modeling`.
