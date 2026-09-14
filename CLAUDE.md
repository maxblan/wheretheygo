# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Cities: Skylines II mod (`WhereTheyGo`) that reads the citizens' real journeys out of
the save, draws them as bundled desire lines coloured by how much of each the transit
network already carries, colours every building by its walk to a served stop, and reports
what one line does for those journeys. It suggests nothing and changes nothing.

`docs/product-plan.md` is the authority on what the mod is for and what it deliberately
does not do. `README.md` describes the result for a player. `docs/game-facts.md` holds
the decompiled facts the code depends on.

The mod was called `TransitArchitect` until 2026-09-14 and, before that,
`StationSuitabilityOverlay`. Both names promised that something plans for the player,
which is the opposite of the current cut. Nothing should carry either name or the old
`Suitability` prefix.

## Commands

There is a `Makefile` wrapping the awkward parts; `make` lists the targets.

```bash
make strict        # the full gate: locked restore, --warnaserror both projects, format, tests
make verify        # check-ui + test + build + status — the quick gate before a run
make test          # offline harness; exit code = number of failures
make check-ui      # node --check on the .mjs, which nothing else validates
make build         # compile AND deploy (close the game first)
make compile       # compile only, analyzers included — safe while the game runs
make deploy        # wait for the game to close, build, confirm by file size
make status        # built vs deployed byte sizes
make errors        # warnings and errors from the mod and UI logs
make logs          # tail the mod log
make CONFIG=Debug build
```

The C# toolchain is Windows-only, so from WSL the build has to run as a Windows process —
that is what resolves the user-level `CSII_TOOLPATH` and `CSII_USERDATAPATH` environment
variables the csproj imports `Mod.props`/`Mod.targets` from:

```bash
powershell.exe -NoProfile -Command "dotnet build dotnet/WhereTheyGo.csproj -c Release"
```

**Building deploys.** `Mod.targets` copies the build straight into
`%CSII_USERDATAPATH%\Mods\WhereTheyGo`, so there is no separate install step — and the
running game holds a lock on the deployed DLL. Close the game before building. To check
that a change compiles while the game is running, use `make compile`: it runs only the
`Compile` target, which the post-processor and the deploy (both hooked onto `AfterBuild`)
never see. `ModPostProcessor` also returns a spurious exit code when it runs before the
game has released its handles, so **verify a deploy by comparing file sizes, not by
trusting the exit code**. `make deploy` encodes the wait-and-compare, and `build`,
`deploy` and `strict` all wait for the handles first — `tools/wait-for-unlock.ps1` probes
them four times a second. A failed wait **aborts**: building anyway once cost a working
deployment, because MSBuild removes the Mods folder before it writes. That probe has to
run as a WINDOWS process: WSL's DrvFs ignores Windows share locks.

**Tests.** `tests/WhereTheyGo.Tests` is a hand-rolled harness with no test framework, so
it runs offline with nothing to restore. It is deliberately **not** in `WhereTheyGo.sln`.
There is no filter flag: to run one test, comment out the other `Run(...)` calls at the
top of `Program.cs`. The harness links every `Planning/` folder under `dotnet/` by glob,
so a new pure file is tested for purity the moment it exists; the test bodies are split by
feature (`BandTests.cs`, `CommonTests.cs`, `PipelineTests.cs`, `WalkNetworkTests.cs`)
beside `Program.cs`.

## Strictness

`Directory.Build.props` turns C# up as far as it goes: `Nullable=enable`,
`TreatWarningsAsErrors`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild`,
`AllowUnsafeBlocks=false`, plus `Meziantou.Analyzer` in `all-errors` mode. `MA0191` makes
the null-forgiving `!` operator an error, so "trust me" is not available.

`TreatWarningsAsErrors` is a **ratchet, not a clean slate**: `WarningsNotAsErrors` lists a
pre-existing backlog that is meant to shrink and never to grow. The build today reports
**zero warnings and zero errors**, which is the state to keep it in.

Two deliberate asymmetries:

- **`CheckForOverflowUnderflow` is on everywhere except the mod project.** `ExtractTripsJob`
  is Burst-compiled and runs per citizen; Burst cannot throw. The test project keeps it on,
  and the pure math files compile into *both*.
- **The test project relaxes four rules** (`CA1303`, `CA5394`, `CA1861` under a
  `[tests/**/*.cs]` section; `CA1515` in the csproj's `NoWarn`, because it fires on the
  shared files the harness LINKS).

The `(count).ToString(CultureInfo.InvariantCulture)` wrappers all over the log statements
are **required, not noise**: `MA0076` forbids an implicit culture-sensitive `ToString` in
an interpolated string, and `FormattableString.Invariant` cannot replace them.

`MA0051` (method length) is **ratcheted, not disabled**: the limits sit at today's worst
offender (112 lines, 52 statements) so no method may grow. Check by build rather than by
counting — MA0051 counts statements differently from a reader. **Lower the numbers as
methods are split; never raise them.** It caught `DesireBands.Build` at 137 lines on the
day it was written, which is what the ratchet is for.

### Nullability conventions

- **Data arrays and caches are nullable** (`TileSnap? m_TileSnap`) because they genuinely
  are null before the first pass.
- **ECS system references are not**, behind a narrow `#pragma warning disable CS8618`
  naming the reason: they are assigned in `OnCreate`, which always runs before `OnUpdate`.
- **Guard, then bind to a local.** The compiler discards a *field's* null-state across any
  intervening call.
- **A field whose allocation is conditional must have every co-allocated field in the
  condition**, or flow analysis only trusts the one that was tested.

**Logs are the primary instrument** — the game cannot be driven from here. After a run,
read `%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Logs\WhereTheyGo.Mod.log` (the
code logs every input and decision on purpose) and `UI.log` (JS errors from the panel
module). `make errors` greps both. Two bugs that no test could have caught were found by
reading it: an uninitialised `EntityQuery` left behind by a deletion, and a 200,000-query
pass running every ten seconds for an answer that changes when somebody builds a road.

## Architecture

### Two halves that ship side by side

C# ECS systems compile to `WhereTheyGo.dll`. The panel's UI is a **hand-written ES
module**, `dotnet/Presentation/UI/WhereTheyGo.mjs` plus `.css`, flattened out of
`Presentation/UI/` on copy because the game loads `<AssemblyName>.mjs` from the mod root.
It uses `window.React` and `window["cs2/api"]` (`bindValue`/`useValue`/`trigger`) — no
build step, no JSX, and cohtml supports neither `<select>`, `<input type=range>`,
checkboxes, nor CSS `gap`. `PanelUISystem` supplies the bindings; a value binding must be
registered with `AddUpdateBinding`, since plain `AddBinding` never re-polls.

### Five systems, and why each phase matters

`Mod.OnLoad` registers five, and the phases are load-bearing (details in its comments):

- `WhereTheyGoSystem` at **PreCulling** — the orchestration; it sits between
  `OverlayInfomodeSystem` and `TerrainRenderSystem` for historical reasons and because the
  infomode state it reads is settled there.
- `BandRenderer` at **Rendering**, before `OverlayRenderSystem` — that system drains and
  clears its buffer during Rendering, so drawing from PreCulling would always be a frame
  late.
- `BuildingAccessColorSystem` at **Rendering**, after `ObjectColorSystem` — which resets
  building colours, so writing before it is writing into the void.
- `BandPickSystem` and `LineInsightSection`/`BuildingAccessSection`/`PanelUISystem` at
  **UIUpdate**.

`Game.UpdateSystem` sorts by `(phase, registration index)` and ignores
`[UpdateBefore]`/`[UpdateAfter]`; the two-type overloads are the only thing that orders
against a named game system.

### Layout: one folder per concept, three kinds of code inside

| Folder | Planning (pure) | Gathering (reads the game) | Systems |
|---|---|---|---|
| `Journeys` | `Journey`/`DemandZones`, `ObservedTrips`, `DesireBands`, `BandGeometry` | `TravelDemand` (Burst job), `TripObserver` | `.Journeys`, `.Observed` |
| `Network` | `TransitGraph`, `JourneyRouting`, `Dijkstra` | `Lines` | `.Routing` |
| `Coverage` | `WalkAccess` (graph, Dijkstra, snap), `WalkBridging`, `Coverage` | `WalkNetwork` | `.WalkNetwork`, `.Coverage`, `BuildingAccessColorSystem`, `BuildingAccessSection` |
| `LineInsight` | `ExistingLine`, `LineHistory`, `LineWindow` | — | `.LineInsight`, `LineInsightSection` |
| `Common` | `Assumptions` (EVERY numeric constant), `float2Like`/`int2Like`, `TileGrid`, `Daytime`, `TransitMode`, `DeferredLog` | — | — |
| `Overlay` | — | — | `WhereTheyGoSystem`, `.Panel`, `.SaveState`, `Infoview`, `InfomodePrefab` |
| `Presentation` | `PanelPayload` | — | `BandRenderer`, `BandPickSystem`, `PanelUISystem`, `UI/` |

### The purity rule

Numeric logic belongs in a `Planning/` folder, where files use `System.*` only, so they
can be linked into the offline test project.

- **No Unity, ECS, Colossal or Game types in `Planning/`** — not `float2`, not `Entity`,
  not `math.*`, not even in a signature. `float2Like` is the map-plane vector: every
  operation on it is the formula decompiled from `Unity.Mathematics`, so a value computed
  on the pure side is bit-identical. The conversion happens in the `Gathering/` readers
  and the `Systems/` partials, as a two-field copy at the seam.
- **`DeferredLog` is pure and lives in `Common`.** It reaches the game's logger through
  `DeferredLog.Sink`, which `Mod.OnLoad` sets. Worker code logs through it because the
  game's logger is an unguarded stream writer.
- **Anything touching Unity or ECS types is untestable here**, so keep algorithms out of
  the `Systems/` partials and the `Gathering/` readers.
- **Every number the mod computes with lives in `Common/Planning/Assumptions.cs`** (user
  rule, 2026-09-06). Before writing a literal anywhere else, look there.

### Data flow

`WhereTheyGoSystem` orchestrates everything as a partial class with one file per concept.

1. **The pedestrian network and the tile snap** (`Coverage`) — streets read into a walk
   graph, then one nearest-node query per 32 m tile on a worker task. Re-run only when a
   signature of the graph changes, and at most once a minute: a live city tags nodes as
   `Updated` constantly without moving a pavement.
2. **Journeys** (`Journeys`) — home→work/school read from `Citizen`/`HouseholdMember` in a
   Burst job, plus shopping/leisure watched once a second and held for three game days.
   Each journey carries its purpose and the two hours it is made at.
3. **Routing** (`Network`) — every journey routed door to door over the existing lines on
   a worker task: the time each takes, whether the network carries it (faster than walking
   AND under the ceiling), and the riders each line gets. With a line selected, the same
   pass routes the city again without it, which is what the line's window reports.
4. **Bundling** (`Journeys`) — the routed pairs summed per zone pair and folded into
   bands, heaviest first, capped and counted.
5. **Coverage** (`Coverage`) — journey ends and served stops snapped to the walk graph,
   the walk to the nearest served stop rasterised onto the tile grid, and the buildings
   coloured from it.

While a routing pass is out, `OnUpdate` leaves every input it reads alone.

### The save state

`Overlay/WhereTheyGoSystem.SaveState.cs` implements `IDefaultSerializable`, which is how
the game persists a world system into the save (keyed by the system's assembly-qualified
type name). It carries the observed shopping/leisure journeys and the line readings, so a
loaded city does not start cold. Two rules the format lives by: the block is **one
length-prefixed byte payload behind a format version**, because `ComponentSystemSerializer`
throws "Data size mismatch" unless a load consumes exactly what was written; and a save
made with the mod loads without it. Bump `SaveFormatVersion` whenever the payload layout
changes; never make the parser depend on game state at load.

## Working method

**Verify every game API by decompiling before writing code against it** — see
`docs/game-facts.md` for what has already been verified, and add to it rather than
re-deriving. Guessing has been wrong repeatedly, and the failures are silent rather than
loud.

## The `.claude/` directory

- `rules/engineering-baseline.md` — always-on defaults, retargeted to this repo.
- `rules/pure-math.md` — scoped to every `Planning/` folder and the test project.
- `rules/ecs-systems.md` — scoped to the `Gathering/` readers, the `Systems/` partials,
  `Overlay/` and `Presentation/`.
- `rules/ui-module.md` — scoped to `dotnet/Presentation/UI/**` and the binding system.
- `skills/` and `books/` — depth to load when a task starts. These came from a Rust
  workspace and still speak of crates, traits and `cargo`; the design advice transfers,
  the toolchain nouns do not.
