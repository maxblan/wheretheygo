# Engineering baseline

Merged from fourteen book rule sets (`.claude/books/`), deduplicated and conflict-resolved.
Where two books disagreed, one ruling shipped — the reasoning is in `.claude/books/RULINGS.md`.
Depth lives in `.claude/skills/`; load a skill when its task starts.

## Precedence

- CLAUDE.md, the ADRs in `docs/architecture/adr/`, and the conventions already in the touched
  crate outrank everything below. Follow the repo and say so.
- These are defaults, not a checklist to satisfy line by line. Do not report on rules that simply
  did not apply. If you deliberately act against one, say which and why, in one sentence.
- Three things are never skippable: the determinism and purity rules for `socha-runtime`,
  `socha_replay`, `socha_game_api` and `crates/seasons`; the additive-only rule for protobuf, the
  SQLite schema, the cross-language fixture and the version constants; and the prohibition on
  weakening a golden-file, replay-frame, or cross-language contract test.
- Never justify a change by naming a pattern. State the problem in this repo's own terms first;
  if you cannot state it without the pattern's name, do not make the change.

## Scope of a change

- Before writing code, state what observable behavior changes and what must stay identical.
- Confine the diff to the change point plus the single preparatory refactor that makes the change
  safe. Record other smells in your report instead of fixing them. The one exception is a rename
  or deletion inside a file the change already had to modify.
- Never mix a behavior change with restructuring in one indistinguishable edit. Say which you are
  doing.
- Do not rewrite a working module as a first move, and size the structure to the change: a bug fix
  or one new behavior gets a function, a match arm, or a field — not a new crate, layer, or trait.
- Reproduce a failure and explain its cause before editing. Never ship a fix you only observed to
  make a symptom disappear.
- When one conceptual change forces edits across files that are not otherwise related, stop and
  name the missing boundary before continuing. Two fan-outs here are the design, not a smell:
  a wire change moves the `.proto`, the Rust codec, `tests/fixtures/cross_language_contract.json`,
  all three hand-written parsers and the version constants together; a new season touches its own
  crate and `socha-season-registry`, then `cargo run -p xtask -- codegen` writes the rest.
  Complete those in one change rather than reporting them as coupling.
- When a change needs yet another special-case branch, look for the missing named concept first.

## Complexity and abstraction

- Before adding a module, crate, trait, wrapper, helper, layer, or parameter, write one sentence
  naming the complexity it hides from callers. If that sentence describes forwarding, field-for-
  field mapping, or "so it can be mocked", do not add it.
- Do not create a trait, port, wrapper, builder, factory, or adapter layer for a single
  implementation. Take volatile capabilities — clock, randomness, socket, storage handle — as
  explicit parameters. Introduce a trait only when a second real implementor exists today (a
  determinism or replay test double counts), or when it is the mechanism reversing a dependency
  direction.
- Both rules govern *new* indirection. `socha-season-registry`'s enum fan-in, `SeasonGame`,
  `BotCallback`, the thin binaries in `apps/`, and the pyo3/JNI facades over `socha-client-core`
  forward by design — do not dissolve or flag them. Pass-through code outside your change point
  gets recorded, not deleted.
- Never split a function because of its line count. Split when it mixes conceptual phases
  (decode / validate / compute / emit) or abstraction levels. If an extracted helper has one caller
  and its name only restates its steps, inline it back.
- Prefer a harder implementation over one that makes every caller repeat the same checks, setup, or
  call-ordering knowledge.
- Do not add a bool, mode, or config parameter that switches a function's internal behavior. Add
  the distinct operation the caller wants.
- Never let a function that reads or answers a question mutate observable state. A function that
  mutates may return the resulting effects — command→effect is the design here, not a violation.
- Shape an interface around the domain concept, but implement only what a caller needs today: no
  unused parameters, generic parameters, config knobs, or extension hooks. Delete speculative
  generality when you find it.
- Leave an honest `match` or `if` alone. Reach for an exhaustive enum match when the variants are a
  closed set so the compiler catches the next addition. Never introduce a trait object or dispatch
  layer to remove a single conditional.
- Do not add async, tasks, threads, or shared mutable state without stating what it makes possible
  that the synchronous version cannot. Do not add a dependency that is not already a workspace
  dependency without stating what code it replaces.
- Do not optimize without a measurement: state the target, measure, change one thing, remeasure.
  No cache, arena, extra index, or `unsafe` on intuition. Bounds are different — fix a queue,
  buffer, or limit at the moment you create the resource.

## Duplication

- Deduplicate knowledge, not text. A fact that must change in lockstep — a wire field's meaning, an
  enum's variant set, a timeout constant, a game rule — gets exactly one authoritative owner, and
  every other copy is generated, derived, or pinned by a contract test.
- Leave text duplicated when the copies live in crates that change for different reasons, or when
  there are only two occurrences. Unify at the third.
- Never create a `utils`, `helpers`, `common`, or `shared` module to hold shared text, and never
  default a new type into `socha_game_api` because no other home is obvious — it is the vocabulary
  seasons and replay share, not a shared kernel. Put the concept in the crate that owns it.

## Naming and comments

- Grep for the existing term before naming anything. One concept, one word across the workspace;
  one word, one concept. Prefer domain vocabulary over protobuf, SQL, or framework vocabulary.
- Reject `Manager`, `Helper`, `Data`, `Info`, `Util` when a precise term exists. If a name is hard
  to choose or only describes mechanism, treat it as evidence the boundary is wrong: move the
  boundary if it is inside your change point, otherwise pick the least-wrong name and record the
  problem in your report.
- Rename an existing term only when it is wrong rather than merely improvable — then everywhere,
  including tests, fixtures, and docs, in a change that does nothing else. Never rename code your
  diff did not otherwise have to touch. A rename that reaches the `.proto` or the fixture is a
  wire change, not a cleanup.
- Write comments only for what the code cannot state: a contract or invariant a caller must honour,
  the rationale for a non-obvious decision, a deliberate boundary violation, a compatibility
  constraint, or why a hard algorithm is shaped as it is. Delete comments that narrate what the
  following lines do — fix the name or the structure. A comment that keeps growing is evidence the
  abstraction is wrong.
- Never let a comment be the only enforcement of a precondition — unless the constraint genuinely
  cannot be expressed in code (a reserved protobuf tag, a cross-language compatibility guarantee).
  Then the comment *is* the enforcement: say so, and state the consequence of violating it.
- Turn closed sets into enums and inline literals into named constants at their declaration.
- Wrap a primitive in a newtype only when it validates at construction, prevents mixing two
  same-shaped values, or carries a unit — then keep the inner field private so no caller can
  bypass or re-check the invariant. A newtype with a `pub` inner field is noise; leave it
  primitive instead.

## Boundaries

- Core policy code must not import transport, persistence, HTTP, serialization, or vendor types.
  Convert foreign types in exactly one translation function per boundary, and do not stack a
  wrapper on top of it.
- Make no per-decision clock, env, config, or RNG read in the core: time arrives as a caller-
  supplied `Instant`/`Duration` and randomness as a `u64` seed. Two contained uses already exist
  (`clock_origin`, `reservation_code`) — `deterministic-core.md` has the detail. Do not "fix" them
  and do not add a third.
- Put each rule where its invariant lives. Never re-implement or re-check a rule in a handler,
  adapter, binding shim, or SQL statement.
- Do not move behavior onto a type merely because it holds the data. Attach a method when it
  enforces an invariant callers would otherwise re-check. Plain data operated on by pure functions
  is a correct design here, not an anemic model.
- Expose behavior, not representation: no public field or setter that lets a caller drive internal
  state through a sequence the type should own. This never applies to prost-generated messages,
  sqlx rows, serde DTOs, or the plain state structs that pure functions like `apply_move` consume —
  those stay data-only by rule, and a data-only struct here is never an anemic-model defect.
- Keep the existing crate layout as given — those boundaries are deliberate. Inside a crate, group
  modules by concept rather than by kind.

## Correctness and failure

Deadlines, bounds, retries, clocks, and instrumentation belong at the I/O edges —
`socha-transport-tcp`, `socha-http-api`, `socha-persistence`, `socha-client-core`, `apps/**`,
`bindings/**`. Inside `socha-runtime`, `socha_replay`, `socha_game_api` and `crates/seasons/**`
they are suppressed: time and dedup decisions arrive as command data. See
`.claude/rules/deterministic-core.md` and `boundary-crates.md` for each half.

- Validate exhaustively once, at the trust boundary where untrusted data enters, and convert it
  there into types that make the invalid state unrepresentable. Do not re-validate inside the core.
  If you add the same defensive branch at a second call site, strengthen the type instead.
- Use `debug_assert` for programmer errors and typed error variants for failures a caller can act
  on. Do not mix them.
- Swallow no failure: no bare `let _ =` on a `Result`, no `unwrap_or_default` to silence an error
  path. Preserve diagnostic context — an error that loses which room, session, or move failed is a
  defect. The core propagates a typed error; the edge crate that owns the boundary logs it.
- At an edge, give every wait you do not control an explicit deadline, and every queue, channel,
  buffer, pool, or cache a stated bound and overflow behavior, in the same change that creates it.
- Hide the mechanism, expose the failure: a function crossing a process, socket, FFI, or storage
  boundary carries timeout, partial failure, and unknown outcome in its return type and is never
  shaped to look like an in-process call. Encoding, framing, and pooling stay private.
- When you add a persisted row, replay frame, cached value, or projection, name its authoritative
  owner in the runtime. Derived stores must stay rebuildable; one that becomes authoritative is a
  defect, not an optimization.
- Decide up front only what is expensive to reverse: which crate owns the concept, the dependency
  direction, the wire and persisted formats, and the behavior under failure. Let the rest emerge
  from the smallest slice that actually runs, and never build more than one new abstraction deep
  before something executes end to end. If a decision can be undone by a local refactor later,
  implement it instead of deliberating.

## Tests and reporting

- Write the test first and watch it fail when the behavior is new and specifiable. When changing
  existing behavior no test covers, first write a characterization test pinning current behavior —
  even if it looks wrong — and report the suspicion rather than fixing it in that step.
- Never finish a change whose new behavior has no test that fails without it.
- Run the relevant tests yourself before reporting done. A clean `cargo check` is not completion,
  and CI's gate is `cargo fmt --check`, `cargo clippy --workspace --all-targets -- -D warnings`,
  `cargo test --workspace`, and `git diff --exit-code`. `cargo test --workspace` does **not** cover
  the Python or Java bindings — they set an empty `[workspace]`. If the change touches
  `socha_protocol`, `socha-client-core`, `tests/fixtures/`, or `bindings/**`, workspace-green is
  not enough: run `./scripts/validate-release validate`.
- Test each behavior in the crate that owns it, through its public API, with real in-process
  collaborators. Substitute a fake only for a socket, clock, database, or subprocess — never add a
  mock or a trait for pure in-process Rust.
- Cover rejected transitions, boundary values, and malformed input, not only the happy path.
- Never weaken, delete, skip, or regenerate a golden file, replay-frame assertion, or cross-language
  contract test to make a change pass. A failure there means behavior changed — find out why.
- Static "unused" signals lie across cdylib exports, prost codegen, and hand-parsed fixtures.
  Private dead code is already caught by the build; before deleting a `pub` item, a JNI/pyo3
  export, a serde or prost-reachable field, or anything named in a fixture, grep all three
  languages — clippy will not catch it.
- Report what changed, which commands you ran to verify it, and every gap or shortcut you left.
