# Rulings

How the fourteen rule sets in this directory were reconciled. Nothing here loads
automatically — it is the audit trail behind `.claude/rules/engineering-baseline.md`
and the `## Rulings` sections in `.claude/skills/`.

Each ruling replaces both raw positions with one instruction. Where a ruling names a
crate, it is calibrated to this repository and would not transfer unchanged.

## Contradictions and their rulings

### Function size and extraction aggressiveness

- **CleanCode, WELC** — Functions must be small and sit at one abstraction level; extract phases out of any function that mixes setup/validation/computation/effects, and extract aggressively near the change point.
- **APoSD, CodeComplete, Refactoring, Refactoring.Guru** — Length is a warning, never a trigger. 'Chains of tiny functions readers must jump between' and 'micro-method noise with no explanatory value' are named anti-patterns; split only on tangled responsibility.

**Ruling.** Never split a function because of its line count. Split only when it mixes conceptual phases (decode/validate/compute/emit), mixes abstraction levels, or contains a fragment you can name after its purpose rather than its mechanism. If an extracted helper has exactly one caller and its name only restates the steps inside it, inline it back.

The line-count reading of Clean Code is the single biggest ceremony generator for an LLM agent: it produces one-call helper chains that raise reading cost with no information hiding. The 'diagnosed tangle, not size' rule is what four of the six books actually say and it is falsifiable, so an agent can check it. It also protects socha-runtime's command handlers and piranhas' apply_move, where following one idea across five hops would be strictly worse than one inspectable body. *(confidence: high)*

### Deep modules vs many small units

- **APoSD** — A module must hide substantially more complexity than its interface exposes; prefer one deep boundary over replicated per-concern scaffolding.
- **CleanArch, CleanCode, DDD, IDDD** — Split god modules by use case or actor, use role-sized interfaces, and prefer many small single-action units and small named types.

**Ruling.** Before adding any crate, module, trait, wrapper, or helper, write one sentence naming the complexity it hides from callers; if that sentence describes forwarding, field-for-field mapping, or 'so it can be mocked', do not add it. Split an existing unit only when two callers need disjoint halves of its interface, or when it changes for two genuinely independent reasons.

APoSD's criterion is the only positive test for NOT decomposing and it is cheap to apply per edit. Clean Architecture's split-by-use-case pressure is aimed at CRUD god services that this repo does not have; applied here it would invent UseCase structs around a state machine already at the right altitude. The named exceptions (apps/socha-server, socha-season-registry, the pyo3/JNI facades) are pre-existing and hide process-wiring, dispatch, and language-boundary concerns, so the rule governs NEW indirection only. *(confidence: high)*

### Comments: failure signal vs design artifact

- **CleanCode, Refactoring, Refactoring.Guru, DDD, DDD-Distilled** — A comment explaining code is a failure of expression; rename or restructure instead. A rule living in a comment signals a missing model element.
- **APoSD, CodeComplete, PragProg, CleanArch, DDIA, ReleaseIt** — Contracts, invariants, rationale, documented boundary violations, and consistency/timeout semantics are required written outputs, sometimes written before the implementation.

**Ruling.** Write comments only for what the code cannot state: the contract or invariant a caller must honour, the rationale for a non-obvious decision, a deliberate boundary violation, a wire/schema compatibility constraint, and why a hard algorithm is shaped the way it is — and write those with or before the implementation. Delete any comment that narrates what the following lines do; fix the name or the structure instead. Treat a comment that keeps growing as evidence the abstraction is wrong.

Both camps agree narration is waste; they only disagree about rationale and contracts, where the comment-abolitionist position is plainly wrong — Rust types cannot express 'this frame index is monotonic within a room' or 'tag 7 is reserved, never reuse'. Splitting on 'can the code express this?' gives the agent a mechanical test instead of a taste judgement, and preserves the only record of the repo's determinism and protobuf-compatibility reasoning. *(confidence: high)*

### DRY vs tolerated duplication across contexts

- **CleanCode, PragProg, Refactoring** — Eliminate duplication aggressively; duplicated logic is a smell to fix on sight, and one fact must have one authoritative representation.
- **DDD, DDD-Distilled, IDDD, CleanArch, DDIA, ReleaseIt, Refactoring.Guru** — Duplicate across bounded contexts, actors, derived stores, and isolated resource pools; a wrong abstraction is worse than duplication, and Rule of Three applies.

**Ruling.** Deduplicate knowledge, not text. A fact that must change in lockstep — a wire field's meaning, an enum's variant set, a timeout constant, a game rule — gets exactly one authoritative owner, and every other copy must be generated, derived, or pinned by a contract test (tests/fixtures/cross_language_contract.json is that pin for Rust/Python/Java). Leave text duplicated when the copies live in different crates that would change for different reasons, when it is a deliberately isolated resource or config, or when there are only two occurrences; unify at the third. Never create a utils, helpers, or common module to hold shared text.

PragProg's knowledge-vs-text distinction dissolves most of the apparent disagreement and is exactly the discipline this repo needs, where one fact is expressed in .proto, Rust, Python and Java. Rule of Three prevents the agent from abstracting coincidental similarity across seasons or bindings. Banning utils/common is the concrete guard that stops sideways coupling between crates the workspace deliberately separates. *(confidence: high)*

### Layered enterprise patterns vs domain model (matrix-flagged hard conflict)

- **PoEAA** — Layering with a Service Layer owning workflow and transactions is the default; Transaction Script, Table Module, and Active Record are legitimate choices when the forces fit.
- **DDD, IDDD, CleanArch** — Business rules belong in the type that owns the state; transaction-script and table-driven designs are anti-patterns and infrastructure is subordinate to the model.

**Ruling.** Keep every game and runtime rule in the crate that owns the state — socha-runtime, crates/seasons/*, socha_game_api — and keep socha-persistence, socha-http-api, and socha-transport-tcp as translation-only adapters containing no rule branching. Use PoEAA vocabulary only inside those adapter crates, and only to decide where a query, a mapping, or a commit lives; never to introduce a service/repository/DTO stack elsewhere.

The books conflict because they assume an enterprise CRUD app; the repo has already settled the question with an authoritative I/O-free runtime, so the ruling records the settlement rather than reopening it. Scoping PoEAA to the two crates that actually face SQLite and HTTP keeps its genuinely useful contributions (commit ownership, no N+1, wire shapes are not domain shapes) without letting either book's layering machinery near the state machine. *(confidence: high)*

### Anemic model vs data-plus-pure-functions

- **DDD, IDDD, Refactoring.Guru, CleanCode** — 'Data Class' and 'anemic model' are defects: move behavior onto the type that owns the data, remove broad accessors, forbid passive data holders.
- **PoEAA, DDIA** — Data plus procedures is an approved design; wire and storage shapes carry data only.

**Ruling.** Do not move behavior onto a type merely because it holds the data. Keep prost-generated wire types, sqlx rows, and serde DTOs behavior-free by rule, and keep game state as plain data operated on by pure functions such as apply_move. Attach a method to a type only when it enforces an invariant that callers would otherwise have to re-check. Never report a data-only struct in this repo as an anemic-model defect.

The Data Class smell is a Java-object-graph rule with no purchase in a functional-core Rust design; applied mechanically it would push logic onto generated protobuf structs and break the purity guarantee replay depends on. The invariant-enforcement carve-out keeps the useful half (don't expose mutable internals so external code can drive illegal transitions) without the OO reflex. *(confidence: high)*

### Up-front design vs emergent design

- **CleanArch, CodeComplete, DDIA, DDD, IDDD, PoEAA, ReleaseIt** — Decide layering order, consistency semantics, schema evolution, error policy, and failure modes before construction.
- **Refactoring, Refactoring.Guru, WELC, CleanCode, PragProg** — Design emerges from smell-driven incremental transformation and from getting a thin slice running; abstraction before a second real need is forbidden.

**Ruling.** Before writing code, decide and state only what is expensive to reverse: which crate owns the concept, the dependency direction, the wire/persisted/fixture format, the consistency and timeout semantics, and the behavior under failure. Let everything else emerge from the smallest slice that actually runs, and never build more than one new abstraction deep before something executes end to end. If a decision can be undone by a local refactor later, implement it instead of deliberating.

The expensive-to-reverse test is the one line both camps would accept and is directly calibrated to this repo: protobuf tags, the SQLite schema, the cross-language fixture, and crate ownership really are one-way doors, while module shape inside a crate is not. It also blocks the failure mode where an agent writes an architecture document instead of a tracer slice. *(confidence: medium)*

### Test-first strictness vs characterization tests

- **CleanCode** — Prefer a failing test before production code; ship every behavior change with a test that fails without it.
- **WELC** — Tests come after the code, pinning what it already does — including behavior that looks wrong — before any edit.
- **CodeComplete, Refactoring, Refactoring.Guru, PragProg, DDIA** — Tests are a risk-matched safety net around change; no red-green mandate.

**Ruling.** Write the test first and watch it fail when the behavior is new and specifiable — a bug fix, a new rule, a new command. When you are about to change existing behavior that no test currently covers, first write a characterization test pinning the current observable behavior, even if it looks wrong, and report the suspicion rather than fixing it in that step. Never finish a change whose new or changed behavior has no test that fails without it, and never report done without running the relevant tests yourself.

The two positions are not actually rivals — they are selected by whether the behavior already exists — so a trigger-based ruling gets both benefits with no ceremony. The 'run them yourself' clause targets the dominant agent failure mode (declaring completion on a clean build), and the characterization branch is what protects golden-file and replay invariants when an agent touches socha-transport-tcp or the runtime. *(confidence: high)*

### Abstraction and indirection budget (ports and seams for single implementations)

- **CleanArch, WELC, IDDD, DDD-Distilled** — When in doubt introduce a boundary sooner; create ports for clocks, IDs, storage and publishers even with one implementation, and add wrappers/seams purely to make code testable.
- **APoSD, Refactoring, Refactoring.Guru, PragProg, PoEAA, CleanCode** — Pass-through layers are forbidden; no interface before a second real need; delete speculative seams; 'layering theater' is a review blocker.

**Ruling.** Do not create a trait, port, wrapper, or adapter layer for a single implementation. Take volatile capabilities — clock, randomness, socket, storage handle — as explicit function parameters rather than inventing an interface for them. Introduce a trait only when a second real implementation exists today (a determinism or replay test double counts), or when the trait is the mechanism that reverses an existing dependency direction. Delete any layer whose functions only forward.

'When in doubt, introduce a boundary sooner' is the single most ceremony-generating line in the whole set and contradicts Clean Architecture's own anti-ceremony guards. Explicit parameters deliver everything the port would (determinism, substitutability, no ambient I/O) at zero indirection cost and are idiomatic Rust. Keeping the test-double carve-out preserves WELC's real contribution without licensing a trait per collaborator. *(confidence: high)*

### Error handling: define errors out of existence vs defensive validation everywhere

- **APoSD** — Reduce error cases by strengthening the interface or invariant; APIs requiring every caller to repeat defensive ceremony are forbidden.
- **CodeComplete, ReleaseIt, DDIA, CleanCode** — Validate and assert at every trust boundary, never continue from an impossible state, and distinguish programmer errors from expected failures.

**Ruling.** Validate exhaustively exactly once, at the trust boundary where untrusted data enters — decoded protobuf, socket frames, HTTP and SQL input, FFI arguments — and convert it there into types that make the invalid state unrepresentable. Inside the core, do not re-validate: use debug_assert for programmer errors and return typed error variants only for failures a caller can act on, preserving diagnostic context across every boundary crossing. If you catch yourself adding the same defensive branch at a second call site, strengthen the type or the boundary instead of adding the branch.

'Validate at the boundary, make it unrepresentable inside' is exactly the synthesis: Code Complete gets its trust boundaries, APoSD gets its no-repeated-ceremony core, and Rust's type system makes the conversion the natural place. The 'second call site' clause gives the agent an observable trigger rather than a philosophy, and preserving error context is what makes a failed match or replay diagnosable at all. *(confidence: high)*

### Premature generalization vs interfaces that are not overfitted

- **APoSD, PragProg** — Do not overfit an interface to its single current caller; keep volatile choices behind a reversible seam and move hard-coded details into validated configuration.
- **Refactoring, Refactoring.Guru, CleanCode** — No interface, hook, parameter, or generic before a second real caller exists; delete speculative generality on sight.

**Ruling.** Shape the interface around the domain concept rather than the current call site, but implement only what a caller needs today: no unused parameters, generic type parameters, config knobs, extension hooks, or trait methods without a present caller. Delete speculative generality when you find it. Never add a configuration knob for a decision this repo has deliberately hard-committed to — protobuf-only gameplay traffic, one authoritative runtime, deterministic replay.

The disagreement is only about naming and shape versus implemented surface area, and separating those keeps both goods: an honest domain-level name today, no dead extension points ever. The explicit hard-commitment clause is needed because PragProg's reversibility rule would otherwise push an agent to add a transport-abstraction or runtime-mode config that the repo intentionally refuses. *(confidence: high)*

### Command/query separation vs command→effect returns

- **CleanCode, Refactoring.Guru, DDD** — A function that answers a question must not mutate; split mutation from interrogation.
- **DDIA, PoEAA** — Commands, events, and views are distinct types; a state transition legitimately produces the facts it caused.

**Ruling.** Never let a function that reads or answers a question mutate observable state. A function that mutates may and should return the resulting effects, decision, or typed error — returning effects from a command is required in socha-runtime, not a CQS violation. Split only when the mutation is invisible in the function's name and signature.

Read literally, CQS would flag the runtime's core command->effect contract as a defect and invite an agent to 'fix' it, which would break the design. Rewriting the rule as 'no hidden mutation' keeps the real value (no lazily-mutating getters, no surprise side effects) and makes it compatible with the repo's deliberate shape. *(confidence: high)*

### Scope of a change: boy-scout rule vs minimal diff

- **CleanCode, PragProg, Refactoring** — Leave touched code better than you found it; fix cheap decay in the area you touched; a review should ask whether you fixed at least one thing.
- **WELC, Refactoring.Guru, CleanCode** — Confine edits to the change point, do not clean the surrounding module, and stop the moment the named smell is materially reduced.

**Ruling.** Confine every diff to the change point plus the single preparatory refactor that makes the requested change safe. When you spot another smell, record it in your report instead of fixing it — the only exception is a rename or a deletion inside a file your change already had to modify. Land structural edits and behavioral edits as separate steps and state which step you are in.

Unbounded boy-scouting is the failure mode that turns a 20-line fix into an unreviewable 600-line diff across crates nobody asked about, and it is uniquely dangerous here because an undeclared behavior change inside a cleanup shows up as a replay or golden-file divergence with no attributable step. The narrow exception preserves the cheapest real benefit of the boy-scout rule without opening the door. *(confidence: high)*

### Hiding infrastructure behind abstractions vs making failure visible

- **DDIA, ReleaseIt** — Never hide network failure, latency, version skew, or partial failure behind a local-looking API; distributed complexity must be visible in the signature.
- **CleanArch, DDD, APoSD, PoEAA** — Details belong behind ports and deep interfaces; core code must not see transport or storage mechanics.

**Ruling.** Hide the mechanism, expose the failure. Any function that crosses a process, socket, FFI, or storage boundary must carry timeout, partial failure, and unknown-outcome in its return type and must not be shaped to look like an in-process call, while the encoding, framing, connection pooling, and query details behind it stay private to the owning crate.

The two positions target different things — one the signature, one the implementation — so a single sentence satisfies both with no residue. It is also directly load-bearing for socha-transport-tcp, where a caller that cannot distinguish 'move rejected' from 'client vanished' will corrupt a match. *(confidence: high)*

### Consistency default: eventual across aggregates vs one authoritative synchronous runtime

- **IDDD, DDD-Distilled, DDD** — Change one aggregate per transaction and default to eventual consistency across aggregate boundaries.
- **DDIA, CleanArch** — Every fact has one named owner; derived copies must be rebuildable; consistency semantics are explicit design inputs.

**Ruling.** Treat socha-runtime as the single synchronous source of truth: apply each state change as one authoritative in-process transition, and never introduce eventual consistency, background reconciliation, or a second writer inside it. socha-persistence, socha_replay, HTTP views, and any cache are derived and must be rebuildable frame-for-frame from the runtime's output; a derived store that becomes authoritative is a defect, not an optimization.

The eventual-consistency default is imported from distributed enterprise systems and would directly destroy determinism and frame-exact replay in a single-process game server. DDIA's source-of-truth-plus-derived-data framing is the version that survives, and it is already the repo's implicit architecture — writing it down stops an agent from letting SQLite or a projection drift into authority. *(confidence: high)*

### Translating wire/storage types vs not adding thin wrappers

- **DDD, IDDD, DDD-Distilled, CleanArch, PoEAA** — Translate explicitly at every foreign boundary; generated, persisted, and FFI types must never travel inward.
- **APoSD, Refactoring, Refactoring.Guru** — A wrapper that adds no simplification is forbidden; field-for-field mapping layers are pass-through indirection.

**Ruling.** Convert prost-generated messages, sqlx rows, and FFI arguments into owned types in exactly one translation function per boundary, and never let those foreign types appear in the signatures of socha-runtime, socha_game_api, or crates/seasons/*. Do not stack a second wrapper on top of that translation, and do not wrap a dependency whose types never cross into core code.

'Exactly one translation function, and no wrapper on top of it' is what both camps actually want: the DDD books get the seam that stops codegen from defining the domain, APoSD gets a hard cap on how many layers that seam may become. It matches what piranhas/src/protobuf.rs already does, so it preserves rather than churns existing structure. *(confidence: high)*

### Determinism vs operational instrumentation (timeouts, jitter, metrics, clocks)

- **ReleaseIt, DDIA** — Every wait needs a deadline; retries need backoff and jitter; queues need bounds; failure paths need structured logging and metrics — inline at the point of work.
- **APoSD, CleanCode, Refactoring** — Instrumentation density and defensive branches inflate functions and obscure the main path.

**Ruling.** Put timeouts, retries, backoff with jitter, bounded queues, structured logging, and metrics in socha-transport-tcp, socha-http-api, socha-persistence, and the binaries only. Keep socha-runtime, crates/seasons/*, and socha_replay free of clocks, randomness, I/O, and instrumentation: time enters as an explicit monotonic duration or tick supplied by the caller, so every replay reproduces every frame. Use monotonic time for all elapsed-time and deadline decisions and wall-clock time only for display and logging.

Both books are right in their own half of the workspace, and the only real error is applying either repo-wide. The path-scoped ruling gives the agent an unambiguous test (which crate am I in?) and simultaneously encodes the repo's hardest invariant. The monotonic-clock clause is DDIA's most valuable single rule here because move-timeout accounting is where determinism most easily breaks. *(confidence: high)*

### Fail fast vs graceful degradation

- **ReleaseIt, PragProg, CodeComplete** — Fail fast when continuing holds scarce resources or state is unverifiable; assert impossible states and crash loudly.
- **ReleaseIt** — Degrade gracefully to preserve core service under stress; shed optional work rather than rejecting everything.

**Ruling.** Fail fast with a typed error whenever continuing would hold a scarce resource, corrupt state, or produce a game result that cannot be verified. Degrade only non-gameplay surfaces — operator HTTP endpoints, replay persistence, metrics — and never degrade by guessing a value the runtime must be authoritative about.

Release It genuinely holds both sides and gives no default, which leaves an agent to pick arbitrarily. Splitting on 'is this gameplay authority or an operator convenience?' is an observable trigger inside this codebase and matches what a competition server must guarantee: a wrong-but-served match result is far worse than a rejected request. *(confidence: high)*

### Value objects / primitive obsession

- **DDD, IDDD, DDD-Distilled** — Wrap primitives aggressively wherever they carry meaning, units, or a rule; validate at construction.
- **APoSD, CodeComplete, Refactoring.Guru** — A wrapper that adds no name, validation, behavior, or error prevention is noise; do not wrap for its own sake.

**Ruling.** Introduce a newtype only when it validates at construction, prevents mixing two same-shaped values (team id vs room id vs frame index), or carries a unit; a wrapper that merely renames u32 is noise and must not be added. When you do create one, validate inside the constructor and keep the field private so no downstream code re-checks the invariant.

The three-way test converts an unbounded 'wrap everything' instinct into a checkable condition, and the two cases it admits are exactly where this repo has real bugs available: confusable integer identifiers and unvalidated coordinates. Requiring private fields is what makes the newtype actually pay for itself rather than becoming a transparent alias. *(confidence: high)*

### Naming authority and how eagerly to rename

- **DDD, IDDD, DDD-Distilled** — Domain vocabulary wins over technical naming; rename as soon as understanding improves — keeping a bad name because it exists is an anti-pattern.
- **CleanCode** — Reuse the codebase's existing term; grep the vocabulary before naming anything new.
- **WELC, Refactoring.Guru** — Do not rename or reformat surrounding code; cosmetic renames that leave the real knot intact are not work.

**Ruling.** Use the term the repo already uses for a concept — grep before you name — and prefer domain vocabulary over protobuf, SQL, or framework vocabulary when introducing a genuinely new concept. Rename an existing term only when it is wrong rather than merely improvable, do it everywhere including tests, fixtures, and docs in a commit that changes nothing else, and never rename code your diff did not otherwise have to touch.

Unrestricted rename-on-insight is dangerous in a workspace where names cross into .proto, a JSON fixture, and two foreign-language bindings; 'wrong, not merely improvable' plus 'commit alone' keeps the DDD benefit while making the blast radius visible. Grep-first is the cheapest fix for the agent habit of inventing a third synonym per file. *(confidence: high)*

### Brittle low-level tests vs golden/contract tests

- **Refactoring.Guru** — Replace or lift brittle low-level tests when they block behavior-preserving structural change.
- **Refactoring, CleanCode, WELC** — Never delete or weaken a failing test to complete a refactor; a skipped or flaky test is an unresolved defect.

**Ruling.** Never weaken, delete, skip, or regenerate a golden file, replay-frame assertion, or cross-language contract test to make a refactor pass — a failure there means behavior changed, and you must find out why. You may retarget a test that is coupled to a private implementation detail, but only after stating what observable behavior it protected and covering that behavior through the public contract first.

These tests are deliberately brittle: brittleness is the feature that encodes determinism and cross-language compatibility. Refactoring.Guru's lift-the-test advice assumes ordinary unit tests over mutable object graphs and would license exactly the change that silently breaks the repo's core guarantee. The retargeting carve-out keeps the legitimate case (tests reaching into internals) without touching the fixtures. *(confidence: high)*

### Pattern vocabulary as justification

- **PoEAA, CleanArch, IDDD** — Named patterns (Service Layer, Repository, Gateway, Mapper, Port, Anticorruption Layer, presenter/request models) are the vocabulary for structural decisions.
- **APoSD, Refactoring, Refactoring.Guru, PragProg, DDD** — A pattern name is never a justification; applying a pattern without a diagnosed smell is an anti-pattern and fake-DDD/layering theater are forbidden.

**Ruling.** Never justify a change by naming a pattern. State the diagnosed smell or the specific complexity being hidden, in this repo's own terms, and then choose the smallest structure that treats it; if you cannot state the problem without the pattern's name, do not make the change.

With five pattern-catalog books in the merge, pattern pressure is triple-counted and an LLM agent is unusually prone to reaching for a recognizable name as a proxy for quality. Requiring a problem statement in repo terms is the cheapest universal gate, and every anti-ceremony guard in the set already agrees with it. *(confidence: high)*

### Table-driven dispatch vs polymorphism vs plain conditionals

- **CodeComplete** — Replace stable repeated branching chains with validated lookup tables.
- **CleanCode** — Excessive conditionals are a smell; replace repeated switches with polymorphism or an argument object.
- **Refactoring, Refactoring.Guru** — Keep simple honest conditionals; never introduce polymorphism to remove a small local conditional.

**Ruling.** Leave an honest match or if alone. Replace branching with an exhaustive enum match when the variants are a closed domain set (season, phase, effect kind, message kind) so the compiler catches the next addition, and with a data table only when the mapping is stable, complete, and its completeness is testable. Never introduce a trait object, strategy type, or dispatch layer to remove a single conditional.

Rust's exhaustive enum match already delivers what Clean Code wanted from polymorphism at zero indirection cost, which resolves the three-way dispute in the language's favor. The trait-object prohibition is the operative clause for an agent, since dynamic dispatch introduced for one branch is both a ceremony cost and a determinism hazard. *(confidence: high)*

### Performance: measure first vs performance as an up-front design input

- **APoSD, CleanCode, CodeComplete** — Do not tune until requirement and evidence justify it; hide any optimization behind the existing interface.
- **DDIA, ReleaseIt** — Latency percentiles, throughput, contention, and load parameters are required design inputs before architectural change.

**Ruling.** Do not optimize without a measurement: state the target, measure, change one thing, remeasure, and note the clarity cost if you keep the faster version. Treat capacity as a design input only where it is structural — connection and session limits, queue and channel bounds, frame and replay sizes — and fix those bounds at the moment you create the resource rather than after it overflows.

The disagreement collapses once you separate tuning (evidence-driven, later) from bounding (structural, decided at creation). Bounding is where this repo actually fails without guidance — an unbounded channel in the TCP actor is an OOM, not a slow path — while speculative micro-optimization inside the runtime buys nothing and costs inspectability. *(confidence: medium)*

### Transaction and commit ownership

- **PoEAA, CleanArch** — Commit ownership belongs to the application/service workflow layer, visible at one caller.
- **DDD, IDDD** — The aggregate is the consistency boundary; one aggregate is changed per transaction.

**Ruling.** Let the caller that owns the use case decide when writes commit — the runtime transition for gameplay, the HTTP or CLI handler for operator actions. Functions in socha-persistence either take an explicit transaction or perform one self-contained write; they never commit on a caller's behalf, and no lock, transaction, or pooled connection is held across an await on anything else.

PoEAA's visible-commit-owner rule is the transferable half and it maps onto real code in socha-persistence; DDD's aggregate-as-transaction rule assumes an ORM and an object graph that do not exist here. The no-lock-across-await clause is the concrete defect this ruling prevents in a tokio codebase. *(confidence: medium)*

### Test level: fakes at ports vs real collaborators vs integration-heavy failure tests

- **CleanArch** — Test use cases with fakes or mocks for ports; confine integration tests to seams.
- **DDIA, ReleaseIt** — Test duplicate delivery, reordering, replay after crash, saturation, restart, and schema compatibility — integration-shaped by nature.
- **Refactoring, WELC** — Test observable behavior through fast narrow tests at a seam; do not couple tests to internals.

**Ruling.** Test each behavior in the crate that owns it, through its public API, with real in-process collaborators; substitute a fake only for a socket, clock, database, or subprocess. Do not add mocks for pure in-process code, and do not let an end-to-end test stand in for a rule test — but do add explicit tests for the failure paths that only exist across a boundary: timeout expiry, duplicate delivery, reconnect, restart with partial state, and schema compatibility in both directions.

Mocking pure Rust code is pure ceremony and couples tests to internals, which Refactoring and the repo's golden-test strategy both reject; but the failure-path tests DDIA and Release It ask for genuinely cannot be written at the unit level and are exactly what a reconnecting game client will exercise in production. Splitting on 'does a real boundary exist?' gives both without inviting a mock framework. *(confidence: medium)*

### Code organization: use-case/feature structure vs technical crate layout

- **CleanArch, DDD, IDDD** — Organize by use case or business capability before technical buckets; a codebase of technical buckets is a smell.
- **PoEAA, CleanCode** — Layer and technical grouping are legitimate organizing principles.

**Ruling.** Keep the existing crate layout as given — the crates are deliberate technical and volatility boundaries and are not to be reorganized. Inside a crate, group modules by domain concept (room, session, timeout, replay, rules) rather than by kind, and place any new concept in the crate that owns its authority; never default a new type into socha-core or a shared module because no other home is obvious.

Clean Architecture's technical-buckets-are-a-smell line, read literally, would trigger a workspace-wide reorganization of a layout whose boundaries are correct (protocol, transport, persistence, runtime are genuinely different volatility axes). The transferable part is the inside-a-crate rule and the anti-dumping-ground rule, which is where drift actually happens in this repo. *(confidence: high)*

## Overlapping sets merged

These push the same decisions and are never shipped as competing guidance.

- **APoSD, CleanCode** → primary **APoSD**. Produce one design-and-hygiene section. APoSD arbitrates every disputed decision — module/interface boundaries, whether to split, whether to add indirection, and comment policy. Import from Clean Code only its distinctive per-edit hygiene that APoSD does not cover: one term per concept (grep before naming), no boolean/mode parameters, no query that secretly mutates, rename before commenting, and ship a test that fails without the change. Drop Clean Code's small-function mandate, its class-vs-DTO and inheritance material, and its wrap-every-third-party-library rule.
- **Refactoring, Refactoring.Guru** → primary **Refactoring**. Merge into one refactoring-process section: name the smell, take the smallest behavior-preserving move, keep it building and green, verify against an identified check, stop. Import from Refactoring.Guru only the explicit stop condition ('stop when the named smell is materially reduced; record further smells separately'), the pre-extraction checklist (enumerate reads/writes/mutations first), and the check-external-reachability-before-deleting rule (pub API, FFI exports, prost codegen, fixtures). Drop both catalogs' inheritance/getter/setter recipes and Guru's 'lift brittle low-level tests' clause entirely.
- **DDD, DDD-Distilled, IDDD** → primary **DDD-Distilled**. Collapse three overlapping catalogs into one short strategic section: name the owning crate and its term before naming a type, keep one vocabulary per context, translate at every foreign boundary, invest modeling effort only in the game rules and runtime, and keep supporting/plumbing code plain. Import Evans' 'awkwardness means a missing concept, find it before adding the branch' and IDDD's sizing pressure (small invariant boundaries, reference other boundaries by identifier, root-mediated mutation). Suppress all three books' repository/factory/application-service/CQRS/event-sourcing machinery and IDDD's eventual-consistency default, which contradicts the authoritative runtime.
- **CleanArch, PoEAA** → primary **CleanArch**. Clean Architecture owns dependency direction, port ownership, and composition-root wiring workspace-wide. Narrow PoEAA to socha-persistence and socha-http-api only, and use it solely for four questions: where a query lives, where a mapping lives, who owns the commit, and whether load behavior is explicit (no N+1, no duplicate in-memory copies). Drop PoEAA's ORM/Active Record/Identity Map/Unit of Work/lazy-load/session/remoting catalog and Clean Architecture's presenter, request/response-model, and 'boundary sooner' clauses.
- **CleanArch, IDDD** → primary **CleanArch**. Both compete for the application-architecture layer. Clean Architecture wins on anything mechanical — which crate may import which, who declares the trait, where concretes are constructed, what a test may touch. IDDD contributes only the two rules Clean Architecture lacks: name the owning context before naming a type, and never let another context's model type serve as an integration contract. Do not stack ports, use cases, adapters, repositories, DTOs and application services for the same seam.
- **CleanCode, CodeComplete** → primary **CodeComplete**. One construction section, Code Complete primary: trust-boundary validation, meaning in types and named constants, plainest control flow, assertion-vs-typed-error triage, evidence before changing behavior, follow the touched crate's existing conventions. Add from Clean Code only the four rules Code Complete does not state as sharply: one term per concept, no flag parameters, no hidden mutation in a query, and rename before commenting. Drop both books' coding-standards and layout material — rustfmt and clippy already own it.
- **CleanCode, PragProg** → primary **PragProg**. PragProg owns the operating-style layer: knowledge-ownership DRY, orthogonality, don't-program-by-coincidence, automate repeated manual work, run the tests yourself, and be pragmatic rather than dogmatic. Restrict Clean Code to hygiene inside code the diff already touches. Where PragProg's 'leave it better' meets Clean Code's 'do not broaden scope', the scope-confinement ruling governs both.
- **CodeComplete, PragProg** → primary **PragProg**. Split by altitude rather than loading both as general guides: PragProg owns judgment, feedback, automation, DRY-as-knowledge, and debugging-from-evidence; Code Complete owns everything inside a function body — variable scope, initialization, named constants, control-flow shape, parameter lists, defensive checks at trust boundaries. Do not carry Code Complete's risk-scaled process ceremony (inspections, formal reviews) into an agent workflow.
- **Refactoring, WELC** → primary **Refactoring**. Both govern changing existing code. Refactoring is the default protocol (separate structural from behavioral edits, small verified steps, preparatory refactor then feature, stop deliberately). WELC activates only on the observable trigger 'no test currently covers the behavior I am about to change', and then contributes its ordered loop: find the change point, find an observation point, pin behavior with a characterization test, break exactly one dependency, then change behavior. Suppress WELC's licence to add wrappers and seams purely for testability — the indirection-budget ruling governs that instead.
- **DDIA, ReleaseIt** → primary **ReleaseIt**. Merge into one reliability section scoped to socha-transport-tcp, socha-http-api, socha-persistence, socha-client-core and the binaries. Release It owns in-process behavior: deadlines on every wait, bounds on every queue and buffer, retry discipline at exactly one layer, no swallowed errors, blast-radius isolation, steady-state retention. DDIA contributes only four things — wire/schema/fixture as versioned contracts, idempotency under retry and reconnect, timeout is not failure (accepted vs applied vs acknowledged), and monotonic clocks for all ordering and elapsed-time decisions. Drop replication, partitioning, consensus, distributed transactions, and OLTP/analytics guidance entirely.

## Pairs that must never be equal guidance

- **DDD vs PoEAA** — Never active as equal guidance. DDD governs only crates/seasons/*, socha_game_api and socha-runtime; PoEAA governs only socha-persistence and socha-http-api. Where both could speak — is this rule rich-model behavior or a transaction script? — the owning-crate rule decides: rules live in the runtime and season crates, adapters hold none.
- **IDDD vs PoEAA** — Never active as equal guidance; this pair produces the worst ceremony stacking in the set (aggregate-root repositories fighting generic gateways, DTOs and projections layered over hand-written sqlx). Disable both books' persistence stacks. socha-persistence uses plain functions over explicit SQL returning owned domain types; no repository trait, no Unit of Work, no aggregate wrapper is to be introduced.
- **APoSD vs CleanCode** — Never active as equal guidance on function size, decomposition, or comments. APoSD arbitrates all three: no size-driven splitting, no comment-as-smell blanket rule. Clean Code is active only for per-edit hygiene inside touched code (naming consistency, flag parameters, hidden mutation, test-with-every-fix).
- **CleanArch vs APoSD** — Never active as equal guidance on whether to add a boundary. APoSD decides IF a trait, port, crate, or layer is created (it must hide more than it exposes; no port for a single implementation). Clean Architecture decides only the DIRECTION and enforcement once a boundary exists (who declares the trait, which crate may import which, where concretes are constructed). Clean Architecture's 'when in doubt, introduce a boundary sooner' clause is disabled in this repo.
- **WELC vs APoSD** — Never active as equal guidance on testability-driven indirection. Default to APoSD: no wrapper, seam, or trait added solely to make code testable — pass the collaborator as an explicit parameter instead. WELC's seam techniques activate only when a test cannot otherwise observe existing behavior in code the change must touch, and any test-only seam, pub(crate)-for-test item, or cfg(test) hook carries a stated cleanup obligation in the same report.
- **IDDD vs DDIA** — Never active as equal guidance inside socha-runtime, crates/seasons/* or socha_replay: neither eventual consistency across aggregates nor distributed-systems coordination machinery may enter the deterministic core. DDIA's ownership rules (single source of truth, derived stores rebuildable, idempotent handlers, versioned wire contracts) apply only to socha-persistence, socha_replay, socha_protocol, socha-transport-tcp and the fixtures; IDDD's consistency defaults are disabled workspace-wide.
- **CleanCode vs WELC** — Never active as equal guidance on diff scope. WELC's confinement wins: edits stay at the change point, and the surrounding module is not cleaned, reorganized, or modernized. Clean Code's boy-scout rule survives only as the narrow exception in the scope ruling — a rename or a deletion inside a file the change already had to modify.

## Deliberately not in the always-on baseline

Every item below was considered and left out, with the rule that covers it instead.
This is what keeps the baseline at ~50 rules rather than ~2,900.

- Fine-grained refactoring catalog moves (extract function/variable, inline, move method, replace conditional with polymorphism, parameter object, method object, rule-of-three mechanics) from refactoring / refactoring-guru / clean-code — task-scoped to refactoring work; the always-on residue is 'split by phase not line count' and 'duplicate rather than unify until the third same occurrence'.
- Legacy-change loop mechanics from working-effectively-with-legacy-code (locate change point, choose observation point, cut a seam, break one dependency per step, sprout/wrap, scratch refactoring) — task-scoped; residue kept as 'add a characterization test or state the verification gap' and 'keep entry points thin'.
- Aggregate sizing, one-aggregate-per-transaction, eventual-consistency default, repositories, factories, domain events, event sourcing (domain-driven-design, implementing-domain-driven-design, domain-driven-design-distilled, patterns-of-enterprise-application-architecture) — no ORM/CRUD here and the eventual-consistency default contradicts the single authoritative synchronous runtime; covered by 'no pattern vocabulary without an invariant' and 'rules live in the crate owning the invariant'.
- Context-map relationship vocabulary (conformist, anticorruption layer, open host service, published language, separate ways) — architecture-task scoped; covered by 'convert foreign types at the owning crate's edge' and 'do not shape internal types after the wire/DB schema'.
- Circuit breakers, bulkheads, load shedding, back-pressure strategy selection, health/readiness checks, steady-state retention, chaos testing, restartable operational automation (release-it) — production/ops task type; the always-on residue is deadlines, bounds, single-layer bounded retry, no swallowed errors, resource release on error paths.
- Replication, partitioning, quorum/consensus, distributed transactions, multi-leader conflict resolution, OLTP-vs-analytics storage layout (designing-data-intensive-applications) — no analogue in a single-node SQLite, single-runtime repo.
- Layered scaffolding from clean-architecture and patterns-of-enterprise-application-architecture (use-case objects, request/response models, presenters, gateways, Unit of Work, Identity Map, Lazy Load, Table Module, Active Record, Remote Facade) — actively harmful here; the dependency-direction, port-ownership and composition-root residue is kept in the boundaries section, and the anti-ceremony section clamps the rest.
- 'When in doubt, introduce a boundary sooner' (clean-architecture) and 'move volatile choices behind a reversible seam' (the-pragmatic-programmer) — deliberately inverted by the proportionality rule requiring a second real caller or a named problem.
- Test-first / red-green-refactor mandates (clean-code, and the TDD pressure inside domain-driven-design and working-effectively-with-legacy-code) — replaced by the checkable 'ship a test that fails without the change, confirm it fails first', which does not prescribe authoring order.
- Boy-scout-rule / 'leave the area better than you found it' (clean-code, the-pragmatic-programmer, refactoring) — deliberately dropped in favor of 'fix only the blocking friction and report the rest', because diff sprawl is the documented failure mode.
- Aggressive duplication elimination on sight (clean-code, the-pragmatic-programmer DRY) — narrowed into 'one authoritative owner per fact, derive the copies' plus the rule-of-three line, so it cannot be cited against deliberate per-context duplication.
- Up-front modeling order (context → language → tactical type → aggregate → invariants) from the three DDD books and the pattern-first ordering in patterns-of-enterprise-application-architecture — covered by 'state what changes and what stays identical' and 'name a second plausible design'.
- Comment-abolitionism from clean-code and the 'comments are deodorant' framing in refactoring-guru — resolved in favor of a-philosophy-of-software-design / code-complete: contracts, invariants and rationale stay; narration goes.
- Pseudocode-first construction, variable-scope minimization, no temp reuse, table-driven dispatch, loop/exit structure (code-complete) — statement-level habits either enforced by clippy or too fine-grained to earn a line; 'plainest control flow' carries the decision-changing part.
- Value-object maximalism and promoting every primitive/flag/status (domain-driven-design-distilled, implementing-domain-driven-design) — collapsed into the conditional newtype rule so it cannot generate wrapper noise.
- Load-parameter analysis before architecture change (designing-data-intensive-applications) — folded into 'measure before optimizing'.
- Process and team rules (pair work, inspections, scaling review weight to risk, negotiating quality with sponsors, timeboxed event storming) from code-complete, domain-driven-design-distilled and the-pragmatic-programmer — no self-checkable agent action.
- Repo facts already stated in CLAUDE.md (changing the wire format means updating the fixture plus all three parsers; the season id is enforced by xtask golden files; read the relevant ADR before changing a boundary; build/test commands; generated code must be committed) — the precedence rule points at CLAUDE.md rather than restating them.
- Anything rustfmt/clippy already enforces (formatting, naming case, unused imports and dead code, obvious unwrap/panic lints, must-use handling) — excluded per the no-tooling-duplication constraint.
