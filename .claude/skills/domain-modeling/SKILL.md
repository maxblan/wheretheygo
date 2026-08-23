---
name: domain-modeling
description: Names concepts in the domain's own vocabulary and puts each rule where its invariant lives, with explicit translation at every foreign boundary. Use when adding or changing game rules, modeling a new concept, deciding what a type should be called, promoting a primitive to a named type, deciding whether logic belongs on a type or in orchestration, or when the same word means different things in different crates.
when_to_use: model this concept, what should this be called, ubiquitous language, value object, invariant, "where do the rules go", anemic type, primitive obsession, bounded context, translate at the boundary, game rules, season rules
---

# Domain modeling

Sources merged, in this order of authority: Evans' *Domain-Driven Design* (strategic — language,
context boundaries, where to spend modeling effort), Vernon's *Domain-Driven Design Distilled*
(the placement gate, including where *not* to model), and Vernon's *Implementing DDD* (tactical
sizing). They overlap heavily; do not treat them as three voices.

## Before naming anything

1. **Name the context.** Which crate or module owns this concept? Do not default to a shared,
   core, or util location.
2. **Use that context's word.** Grep for the existing term first. One concept gets one term; one
   term means one thing. If the same word means something different across a boundary, that is
   two concepts and the boundary must translate.
3. **Say what it is.** Identity-bearing (two instances with the same fields are still different)
   or a value (interchangeable, immutable, validated at construction)?
4. **Say what invariant it protects.** If there is none, it is data — leave it plain.

## Where rules live

- Behavior goes on the type that owns the state. Expose intention-revealing operations rather than
  public fields plus external code deciding which transitions are legal.
- Orchestration may load, invoke, persist, and dispatch. It must not accumulate rule branches.
- A rule is implemented once, where its invariant lives — never re-checked in transport,
  persistence, HTTP, or binding code.
- When a change needs yet another special-case branch, look for the missing named concept before
  adding the branch. Awkwardness is a modeling signal.

## Rulings

- **Modeling effort is spent asymmetrically.** Invest in the core domain — the game rules and the
  runtime's state transitions. Keep supporting and purely technical areas plain. No aggregates,
  domain events, repositories, factories, or wrapper types where no invariant exists. All three
  sources say this explicitly; it is the guard that stops this skill from generating ceremony.
- **Fake DDD is forbidden.** Renaming things to sound sophisticated, adding a factory to hide a
  trivial constructor, wrapping CRUD in verbose abstractions, or turning every concept into an
  aggregate are anti-patterns, not applications of this skill.
- **Promote a primitive only when it carries a rule.** A unit, an identity, or a validity
  constraint that code must not violate justifies a named type with validation at construction.
  Otherwise leave it primitive — value-object maximalism produces wrapper noise.
- **Duplicate across contexts rather than share a model type.** Do not create or grow a `common`
  module that erases context boundaries. Translate at the edge instead. This deliberately
  overrides the general "eliminate duplication" pressure.
- **Eventual consistency is not the default here.** The DDD sources default to one aggregate per
  transaction and eventual consistency across boundaries. This repo has one authoritative,
  synchronous runtime; that default does not apply. Consistency questions go to
  `/data-and-consistency`.
- **Do not shape the model for storage or the wire.** Storage and wire schemas adapt to the model,
  not the reverse.
- **Rename only when a name is wrong, not merely improvable.** Never rename code your diff did not
  otherwise have to touch. When you do, change it everywhere in one step that changes nothing else.
  A rename that reaches `crates/socha_protocol/proto/` or
  `tests/fixtures/cross_language_contract.json` is a wire change — use `/data-and-consistency`,
  not `/refactoring-pass`.

## In this repo

- The real contexts are: season rules (`crates/seasons/piranhas`), the authoritative runtime
  (`crates/socha-runtime`), the wire language (`crates/socha_protocol`), the operator API
  (`crates/socha-http-api`), the store (`crates/socha-persistence`), and the client SDK
  (`crates/socha-client-core` plus bindings).
- The wire language is a *published language*: `.proto` messages and the cross-language fixture
  are an owned public contract, deliberately distinct from internal shapes. Changing it is
  `/data-and-consistency` work, not modeling.
- `socha-season-registry` is the translation point between one season's model and the generic
  runtime. Season vocabulary (`Board`, `Field`, `Team`, `Direction`, `Coordinate`) stays inside
  the season crate; the runtime speaks in `RoomId`, `SessionId`, `RuntimeCommand`, `RuntimeEffect`.
- `PiranhasState`, `PiranhasMove`, and the rules module are the core domain. `apply_move` must
  stay pure — a rule that needs a clock or randomness is a design error here, not a modeling one.

## Depth

- Strategic design, context mapping, and the anti-fake-DDD guard:
  `.claude/books/domain-driven-design/domain-driven-design.md`
- The placement gate and relationship vocabulary:
  `.claude/books/domain-driven-design-distilled/domain-driven-design-distilled.mini.md`
- Tactical sizing and translation rules:
  `.claude/books/implementing-domain-driven-design/implementing-domain-driven-design.mini.md`
- For which crate may import which, use `/architecture-boundaries`.
