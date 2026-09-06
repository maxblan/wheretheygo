# Engineering baseline

Merged from fourteen book rule sets (`.claude/books/`), deduplicated and conflict-resolved.
Where two books disagreed, one ruling shipped — the reasoning is in `.claude/books/RULINGS.md`.
Depth lives in `.claude/skills/`; load a skill when its task starts.

The book sets and skills were written for a Rust workspace and still speak of crates, traits and
`cargo`; this file has been retargeted to this repository, and the path-scoped rules beside it
(`pure-math.md`, `ecs-systems.md`, `ui-module.md`) are this project's own. Where a skill's wording
assumes Rust, read the intent — the design advice transfers, the toolchain nouns do not.

## Precedence

- CLAUDE.md, `README.md`, and the conventions already in the touched file outrank everything
  below. Follow the repo and say so.
- These are defaults, not a checklist to satisfy line by line. Do not report on rules that simply
  did not apply. If you deliberately act against one, say which and why, in one sentence.
- Three things are never skippable: the purity and determinism rules for the testable core
  (`pure-math.md`) — a Unity or ECS type in those files breaks every test in them; verifying a game
  API against the decompiled assembly before writing code against it (`ecs-systems.md`); and the
  prohibition on weakening or deleting a test in `tests/SuitabilityScoring.Tests` to make a change
  pass.
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
  or one new behavior gets a function, a switch arm, or a field — not a new file, layer, or
  interface.
- Reproduce a failure and explain its cause before editing. Never ship a fix you only observed to
  make a symptom disappear.
- When one conceptual change forces edits across files that are not otherwise related, stop and
  name the missing boundary before continuing. Two fan-outs here are the design, not a smell: a new
  panel control moves together through `Setting.cs`, `SuitabilityPanelUISystem.cs`, the `.mjs`, the
  `.css` and both locale files; and a new scoring term moves through `SuitabilityWalkAccess.cs`, the
  combine pass in `SuitabilityHeatmap.cs`, the F1 partial of the overlay system, the infomode
  registration in `SuitabilityInfoview.cs` and the legend. Complete those in one change rather than reporting them as coupling.
- When a change needs yet another special-case branch, look for the missing named concept first.

## Complexity and abstraction

- Before adding a file, class, interface, wrapper, helper, layer, or parameter, write one sentence
  naming the complexity it hides from callers. If that sentence describes forwarding, field-for-
  field mapping, or "so it can be mocked", do not add it.
- Do not create an interface, port, wrapper, builder, factory, or adapter layer for a single
  implementation. Take volatile capabilities — the score lookup, a graph, a settings value — as
  explicit parameters, the way `PlaceStops` takes a scoring callback. Introduce an interface only
  when a second real implementor exists today, or when it is the mechanism reversing a dependency
  direction.
- Both rules govern *new* indirection. `AlignmentNetwork`'s uniform view over the streets and the
  free-form lattices, the `float2Like` vector that keeps the pure core Unity-free,
  and `SuitabilityPanelUISystem`'s one-line binding forwarders exist by design — do not dissolve or
  flag them. Pass-through code outside your change point gets recorded, not deleted.
- Never split a function because of its line count. Split when it mixes conceptual phases
  (gather ECS data / compute / render) or abstraction levels. If an extracted helper has one caller
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
- Leave an honest `switch` or `if` alone. Prefer a `switch` over a closed enum — the per-mode tables
  (`MinLengthFor`, `StopSpacingFor`, `ColorFor`) are that shape on purpose, so adding a mode is one
  visible edit per table. Never introduce a dispatch layer to remove a single conditional.
- Do not add jobs, threads, or shared mutable state without stating what it makes possible that the
  single-threaded version cannot — and rule out a scheduling bug first: "Suggest improvement" was
  slow because it was handled in the 60-second refresh, not because anything needed parallelising.
  Do not add an assembly reference without stating what code it replaces.
- Do not optimize without a measurement: state the target, measure, change one thing, remeasure.
  No cache, arena, extra index, or `unsafe` on intuition. Bounds are different — fix a queue,
  buffer, or limit at the moment you create the resource.

## Duplication

- Deduplicate knowledge, not text. A fact that must change in lockstep — a wire field's meaning, an
  enum's variant set, a timeout constant, a game rule — gets exactly one authoritative owner, and
  every other copy is generated, derived, or pinned by a contract test.
- Leave text duplicated when the copies live in files that change for different reasons, or when
  there are only two occurrences. Unify at the third.
- Never create a `utils`, `helpers`, `common`, or `shared` file to hold shared text, and never
  default a new type into the pure math files because no other home is obvious — they are the
  testable core, not a shared kernel. Put the concept in the file that owns it.

## Naming and comments

- Grep for the existing term before naming anything. One concept, one word across the workspace;
  one word, one concept. Prefer transit-planning vocabulary (line, stop, headway, catchment,
  corridor) over ECS or framework vocabulary. Where the game already names a concept, use the
  game's word.
- Reject `Manager`, `Helper`, `Data`, `Info`, `Util` when a precise term exists. If a name is hard
  to choose or only describes mechanism, treat it as evidence the boundary is wrong: move the
  boundary if it is inside your change point, otherwise pick the least-wrong name and record the
  problem in your report.
- Rename an existing term only when it is wrong rather than merely improvable — then everywhere,
  including tests and docs, in a change that does nothing else. Never rename code your diff did not
  otherwise have to touch. Beware blanket renames: a field renamed by search-and-replace has
  already silently rewritten a *game* field of a similar name.
- A field whose name no longer matches what it holds is wrong, not improvable. `m_AverageWait`
  holding an accumulator that is not an average, in units that are not seconds, is exactly the case
  the previous rule exists for.
- Write comments only for what the code cannot state: a contract or invariant a caller must honour,
  the rationale for a non-obvious decision, a deliberate boundary violation, a compatibility
  constraint, or why a hard algorithm is shaped as it is. Delete comments that narrate what the
  following lines do — fix the name or the structure. A comment that keeps growing is evidence the
  abstraction is wrong.
- Never let a comment be the only enforcement of a precondition — unless the constraint genuinely
  cannot be expressed in code (an update-phase ordering the game imposes, a buffer the game
  documents as travel-ordered, a decompiled formula being mirrored). Then the comment *is* the
  enforcement: say so, name the game type or system it comes from, and state the consequence of
  violating it.
- Turn closed sets into enums and inline literals into named constants at their declaration.
- Wrap a primitive in a type only when it validates at construction, prevents mixing two
  same-shaped values, or carries a unit — then keep the field private so no caller can bypass or
  re-check the invariant. A wrapper with a public field is noise; leave it primitive instead. Where
  a raw number stays raw, put the unit in the name: seconds, metres and game-clock units have all
  been confused here.

## Boundaries

- The testable core must not import Unity, ECS, Colossal or Game types — see `pure-math.md`.
  Convert at the call site in the ECS system: gather into plain arrays, call the math, map the
  result back. Do not stack a wrapper on top of that.
- Make no clock, RNG, settings or `EntityManager` read inside the core: everything it needs arrives
  as an argument. A recompute must be a function of its inputs so two runs over the same city can be
  compared.
- Put each rule where its invariant lives. Never re-implement or re-check a rule in a UI binding,
  a renderer, or the panel module — the `.mjs` formats what it is given and triggers actions; it
  does not decide.
- Do not move behavior onto a type merely because it holds the data. Attach a method when it
  enforces an invariant callers would otherwise re-check. Plain data operated on by pure functions
  is a correct design here, not an anemic model.
- Expose behavior, not representation: no public field or setter that lets a caller drive internal
  state through a sequence the type should own. This never applies to the ECS component structs, the
  gathered `ExistingLine`/`LineHealth`/`SuggestedRoute` records, or the plain arrays the pure
  functions consume — those stay data-only by rule, and a data-only struct here is never an
  anemic-model defect.
- Keep the existing file split as given — the pure/game-facing boundary is what makes any of this
  testable. Group by concept, not by kind.

## Correctness and failure

Budgets, caching, debouncing and logging belong in the ECS systems, which know about frames and
the simulation. The pure core takes what it is given and returns a result. See
`.claude/rules/pure-math.md` and `ecs-systems.md` for each half.

The failure mode that matters in this mod is not a crash — it is a plausible wrong number reaching
the map. Guard against that specifically: log the inputs, mirror the game's own formula where one
exists, and prefer a logged rejection over a suggestion nobody can justify.

- Validate exhaustively once, at the trust boundary where untrusted data enters, and convert it
  there into types that make the invalid state unrepresentable. Do not re-validate inside the core.
  If you add the same defensive branch at a second call site, strengthen the type instead.
- Do not swallow failure. A `try`/`catch` is acceptable only where the game may legitimately not
  have the data and the feature is cosmetic — and then it logs what it lost. An empty catch that
  hides a wrong assumption about a game API is a defect.
- Give every buffer, cache and candidate list a stated bound and say in the log when it truncates.
  A silently truncated result reads as "covered everything" when it did not.
- Cache invalidation is explicit: state which input dirties a cache in the same change that adds it.
  A cache whose timer is stamped on every read never expires — that has already happened here.
- Hide the mechanism, expose the failure: a function crossing a process, socket, FFI, or storage
  boundary carries timeout, partial failure, and unknown outcome in its return type and is never
  shaped to look like an in-process call. Encoding, framing, and pooling stay private.
- When you add a cached grid, mask, network or gathered list, name what invalidates it. Derived
  data must stay rebuildable from the save; one that becomes authoritative is a defect, not an
  optimization.
- Decide up front only what is expensive to reverse: which side of the pure/game-facing boundary a
  concept lives on, the update phase a system runs in, the persisted settings format, and the
  behaviour when the game gives you nothing. Let the rest emerge
  from the smallest slice that actually runs, and never build more than one new abstraction deep
  before something executes end to end. If a decision can be undone by a local refactor later,
  implement it instead of deliberating.

## Tests and reporting

- Write the test first and watch it fail when the behavior is new and specifiable. When changing
  existing behavior no test covers, first write a characterization test pinning current behavior —
  even if it looks wrong — and report the suspicion rather than fixing it in that step.
- Never finish a change whose new behavior has no test that fails without it.
- Run the checks yourself before reporting done: `make verify` (`check-ui`, `test`, `build`). A
  compile is not completion, and neither is a green test run when the change is in the game-facing
  half — that half is verified by the log after a run, so say plainly which parts are unverified
  rather than implying otherwise.
- Test each behavior in the file that owns it, through real arrays and real graphs. There are no
  mocks here and no framework to add one with; if a behavior cannot be reached from the harness, the
  arithmetic is on the wrong side of the boundary.
- Cover rejected candidates, boundary values, empty and degenerate input (no stops, one node, all
  scores zero), not only the happy path. Several shipped bugs were the degenerate case: a percentile
  over a set with no positive member turned the whole map red.
- Never weaken, delete or skip a test in `tests/SuitabilityScoring.Tests` to make a change pass. A
  failure there means behavior changed — find out why.
- Static "unused" signals lie here. Anything the game reaches by reflection or by name — an infomode
  field, a settings property rendered by the Options UI, a locale key, an `.mjs` export, a binding
  name paired with a string in the panel — has no visible C# caller. Grep the `.mjs`, the locale
  files and the settings class before deleting a `public` member.
- Report what changed, which commands you ran to verify it, what the log said, and every gap or
  shortcut you left. When a number came from the game, say which decompiled system it came from.
