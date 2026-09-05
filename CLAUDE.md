# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Cities: Skylines II mod (`StationSuitabilityOverlay`) that scores 32 m terrain tiles for
transit-stop placement, reads real citizen origin-destination data out of the save, suggests new
lines, and reports on the health of existing ones. See `README.md` for the domain model and the
reasoning behind the scoring terms.

## Commands

There is a `Makefile` wrapping the awkward parts; `make` lists the targets.

```bash
make strict        # the full gate: locked restore, --warnaserror both projects, format, tests
make verify        # check-ui + test + build + status — the quick gate before a run
make test          # offline harness; exit code = number of failures
make check-ui      # node --check on the .mjs, which nothing else validates
make build         # compile AND deploy (close the game first)
make deploy        # wait for the game to close, build, confirm by file size
make status        # built vs deployed byte sizes
make errors        # warnings and errors from the mod and UI logs
make logs          # tail the mod log
make CONFIG=Debug build
```

The C# toolchain is Windows-only, so from WSL the build has to run as a Windows process — that is
what resolves the user-level `CSII_TOOLPATH` and `CSII_USERDATAPATH` environment variables the csproj
imports `Mod.props`/`Mod.targets` from:

```bash
powershell.exe -NoProfile -Command "dotnet build dotnet/StationSuitabilityOverlay.csproj -c Release"
```

`./build.ps1 [-Configuration Debug]` does the same from PowerShell. A .NET SDK and the official CS2
modding toolchain are required.

**Building deploys.** `Mod.targets` copies the build straight into
`%CSII_USERDATAPATH%\Mods\StationSuitabilityOverlay`, so there is no separate install step — and the
running game holds a lock on the deployed DLL. Close the game before building. `ModPostProcessor`
also returns a spurious exit code if it runs within ~25 s of the game closing, so **verify a deploy
by comparing file sizes, not by trusting the exit code**. `make deploy` encodes the wait-and-compare.

**Tests.** `tests/SuitabilityScoring.Tests` is a hand-rolled harness with no test framework, so it
runs offline with nothing to restore. It is deliberately **not** in `smart-transit-planner.sln`, so
the mod toolchain build is unaffected. There is no filter flag: to run one test, comment out the
other `Run(...)` calls at the top of `Program.cs`.

## Strictness

`Directory.Build.props` turns C# up as far as it goes: `Nullable=enable`,
`TreatWarningsAsErrors`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild`,
`AllowUnsafeBlocks=false`, plus `Meziantou.Analyzer` in `all-errors` mode. `MA0191` makes the
null-forgiving `!` operator an error, so "trust me" is not available — a nullable value has to be
narrowed or guarded.

`TreatWarningsAsErrors` is a **ratchet, not a clean slate**: `WarningsNotAsErrors` lists a
pre-existing backlog (nullability, `CA1822`, `CA1814`, `CA1305`, `CA1044`, `CA1067`, `CA1825`,
`CA1716`, `CA1062`, `CA1031`) that is meant to shrink and never to grow. Anything outside that list
fails the build, so no NEW class of warning can be introduced. The build today reports **zero
warnings and zero errors**, which is the state to keep it in.

Two deliberate asymmetries:

- **`CheckForOverflowUnderflow` is on everywhere except the mod project.** `ExtractTripsJob` is
  Burst-compiled and runs per citizen; Burst cannot throw, so an
  overflow check there is either a compile failure or an abort. The test project keeps it on, and
  the pure math files compile into *both*, so overflow is still checked where it is reachable.
- **The test project relaxes four rules.** Three (`CA1303`, `CA5394`, `CA1861`) sit under a
  `[tests/**/*.cs]` section: it is a console harness with no localization, no security surface and
  no hot path. The fourth, `CA1515`, is in the csproj's `NoWarn` rather than `.editorconfig`,
  because it fires on the shared files the harness LINKS — `ModePreset` and `RouteGoal` are public
  so the game's Options UI can bind to them by reflection, a reason that does not exist here.

The `(count).ToString(CultureInfo.InvariantCulture)` wrappers all over the log statements are
**required, not noise**: `MA0076` forbids an implicit culture-sensitive `ToString` in an
interpolated string, and `FormattableString.Invariant` cannot replace them because C# 9 has no way
to concatenate two interpolated strings into one `FormattableString`, which is how every multi-line
log message here is built. Removing them fails the build in 94 places.

`.editorconfig` disables a rule only where the rule is wrong *for this codebase*, and every one
carries its reason inline — Unity's `IJobChunk` signature, game enums with no zero-valued member,
Burst and `HasFlag`, set-only properties being the settings-UI idiom for a button, the `Mod` type
name being fixed by the toolchain, `partial` being required on `SystemBase` by the ECS source
generator (found the hard way: removing it fails the build with EA0007). Volume alone is not a
reason — read the comment before adding another.

`MA0051` (method length) is **ratcheted, not disabled**: the limits sit exactly at today's worst
offender so no method may grow. The line ceiling is `SuitabilityGraphMath.GrowCorridor` at 138; the
statement ceiling is 60. Check either by build rather than by counting — MA0051 counts statements
differently from a reader, and 138/60 is where the build says the real worst sits today.

Roughly forty methods sit over 60 lines and sixteen over 100; the longest are
`SuitabilityGraphMath.GrowCorridor`, `SuitabilityRoutes.BuildForNetwork`,
`StationSuitabilityOverlaySystem.OnCreate/OnUpdate/StartCompute/ScoreCandidatesWithTransfers`,
`SuitabilityScoring.FindTopSites` and `SuitabilityRouteRenderer.OnUpdate`. Splitting them is a
refactor rather than a fix and must not be done blind, which is what the ratchet is holding the line
for. **Lower the numbers as methods are split; never raise them** — and check the real figure with a
build rather than by counting, because MA0051 counts statements differently from a reader.

### Nullability conventions

The annotations already in place encode decisions worth keeping:

- **Data arrays and caches are nullable** (`float[]? m_Scores`) because they genuinely are null
  before the first compute, and the code already tested for it.
- **ECS system references are not**, behind a narrow `#pragma warning disable CS8618` naming the
  reason: they are assigned in `OnCreate`, which always runs before `OnUpdate`, and annotating them
  nullable would force a null check at every use site for a state in which nothing works anyway.
- **Guard, then bind to a local.** The compiler discards a *field's* null-state across any
  intervening call, so a method that checks `m_TermScratch is null` and then calls something else
  will warn again. Copy the checked field into a local right after the guard and use that — see
  `TermPercentile` and `DiscountServedDemand`.
- **A field whose allocation is conditional must have every co-allocated field in the condition**,
  or flow analysis only trusts the one that was tested (`EnsureTileDensities`, `BuildPairArrays`).

**Logs are the primary instrument** — the game cannot be driven from here. After a run, read
`%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Logs\StationSuitabilityOverlay.Mod.log` (mod
diagnostics; the code logs every input and decision on purpose so nonsense numbers are visible) and
`UI.log` (JS errors from the panel module). `make errors` greps both.

## Architecture

### Two halves that ship side by side

C# ECS systems compile to `StationSuitabilityOverlay.dll`. The panel's UI is a **hand-written ES
module**, `dotnet/UI/StationSuitabilityOverlay.mjs` plus `.css`, flattened out of `UI/` on copy
because the game loads `<AssemblyName>.mjs` from the mod root. It uses `window.React` and
`window["cs2/api"]` (`bindValue`/`useValue`/`trigger`) — no build step, no JSX, and cohtml supports
neither `<select>`, `<input type=range>`, checkboxes, nor CSS `gap`. `SuitabilityPanelUISystem`
supplies the bindings; a value binding must be registered with `AddUpdateBinding`, since plain
`AddBinding` never re-polls.

### Three systems, and why each phase matters

`Mod.OnLoad` registers exactly three, and the phases are load-bearing (details in its comments):

- `StationSuitabilityOverlaySystem` at **PreCulling** — must sit between `OverlayInfomodeSystem`,
  which clears the terrain override overlay every frame, and `TerrainRenderSystem`, which consumes
  it.
- `SuitabilityRouteRenderer` at **Rendering** — `OverlayRenderSystem` drains and clears its buffer
  during Rendering, which runs *before* PreCulling, so drawing route polylines from the overlay
  system would always be a frame late.
- `SuitabilityPanelUISystem` at **UIUpdate**.

### The purity rule

Numeric logic belongs in files that use `System.*` only, so they can be linked into the offline test
project. Fourteen files are on that side, and `SuitabilityScoring.Tests.csproj` links all fourteen:

- `SuitabilityScoring.cs` — percentiles, site candidates and the greedy ranking, geodesic catchment, weight fitting
- `SuitabilityWalkAccess.cs` — the heatmap's terms since Phase 3: the pedestrian graph with
  integer-millisecond edge costs, a bounded integer Dijkstra, node snapping, and the per-node
  accumulation of every mode's walking-time terms. Runs on a worker thread in the game and
  unchanged in the offline subject; integer times are what make the offline check bit-exact
- `SuitabilityExactSites.cs` — exact site selection: branch-and-bound over a conflict graph with a
  clique-cover bound (grid blocks, or walking-time balls on the network); proven optimum, or
  best-found plus ceiling when the node budget runs out. The greedy ranking is its incumbent and
  the baseline the pipeline measures
- `SuitabilityGraphMath.cs` — CSR graph, Dijkstra, corridor growth, RDP
- `SuitabilityDirectedRoads.cs` — the streets as a vehicle drives them: one arc per admitted
  direction with speed-limit times, five turn classes priced by the game's curve-angle cost, an
  arc-state Dijkstra and directed flow assignment. Feeds ride seconds and fleet estimates of road
  routes; the undirected graph stays the corridor search's
- `SuitabilityTransit.cs` — transit routing and boarding counts
- `SuitabilityLineHistory.cs` — the rolling window of line readings
- `SuitabilityObservedTrips.cs` — the one-game-day window of observed shopping/leisure journeys and
  its per-day scaling; the live-city scan that feeds it stays in the overlay system
- `SuitabilityEquity.cs` — the equity floor: served-walk field from the served stops, journeys served
  at both ends within the horizon, share and weighted Gini, the utilisation formula
- `SuitabilityLineSet.cs` — the line-set selection: before/after door-to-door times over the
  transit graph with zone nodes, passenger time saved, riders per line, the utilisation and
  duplicate feasibility of a set, and the branch-and-bound that picks the exact best set (or the
  best found plus a ceiling when the node budget runs out)
- `SuitabilityStopPlan.cs` — where a line calls: the exact dynamic programme over candidate
  positions that trades the boarders' access gain against the through-riders' delay, with
  forced interchanges and the gap floor
- `SuitabilityLineHealth.cs` — verdicts and improvement plans
- `TransitMode.cs` — the `ModePreset`/`RouteGoal` enums and every per-mode table
- `SuitabilityExportJson.cs` — the verification export's canonical JSON and its digest.
  Not scoring math, and on this side for one reason: the format is a CONTRACT with
  `verification/common/canonical.py`, which recomputes the digest on load and refuses
  any file it cannot reproduce, so a golden-vector test is the only thing standing
  between a wire-format drift and every exported instance becoming unloadable.

Anything touching Unity or ECS types is untestable here, so **keep algorithms out of the ECS
systems**. Several silent bugs — double-counted demand, summed instead of averaged corridor flow,
free transfers, a corridor's node walk starting from a node that was not on the line — were only
caught because the math was reachable from a test.

`TransitMode.cs` is why the last two are testable at all. Everything true of a mode is keyed on
`ModePreset`, and while that enum was nested inside `Setting` — which imports Colossal, Game.Modding,
Game.Settings and Game.UI — no Unity-free file could own a per-mode table, so five of them grew
separate copies across separate files and a sixth in the panel's JavaScript. **A new per-mode fact
goes in `TransitMode.cs`**, as one more `switch` beside the others.

### Data flow

`StationSuitabilityOverlaySystem.cs` (~4100 lines) orchestrates everything and is where most work
lands. It is a god class and known to be one: the heatmap pipeline, travel-demand extraction, route
growth and selection, line health, ridership calibration, infomode registration, reflection into the
terrain texture and every UI payload all live in it. The pipeline stages have clean seams
(`UpdateTravelDemand` → `BuildTransitModel` → `BuildRoutes` → `SelectRoutes`) if it is ever split.

1. **Heatmap** — terrain/water masks (`SuitabilityMasks.cs`) → the pedestrian network, residents
   per home building, workplaces, zoned cells and served stops gathered into plain arrays
   (`SuitabilityInputs.cs`) → `SuitabilityWalkAccess.Run` on a worker `Task` (integer walking-time
   Dijkstra from every source, per-node accumulators for every mode, tile snap) → seven raw terms
   per tile → per-term percentile normalization → intensities written into a terrain overlay
   channel obtained by reflection. Channel index is `InfomodeActive.m_Index - 1`. The map is built
   for the mode the PANEL shows; because the accumulators hold every mode, `ScoreForMode` is the
   full combine of a tile's node for the suggested line's mode, not a swap. Sites are chosen
   exactly among network nodes (`SuitabilityExactSites.SolveOnNetwork`) with the mode's stop
   spacing as walking-time separation.
2. **Travel demand** — real home→work/school journeys read from `Citizen`/`HouseholdMember`
   (`SuitabilityTravelDemand.cs`) plus shopping/leisure journeys observed once a second from
   `TravelPurpose`/`Target`/`CurrentBuilding` and held for a game day, aggregated into 256 m zones, discounted by whether the existing
   network can actually route them, then assigned to a network by shortest path.
3. **Route suggestion** — two alignment searches in `SuitabilityRoutes.cs`, picked by network:
   `BuildForNetwork` grows a corridor with flow peeling and novelty decay on the road graph, where
   edge flow is a real measurement (assigned along DIRECTED fastest routes since Phase 5, summed
   per street); `BuildDirectForNetwork` traces straight between the two ends of
   the heaviest unserved journey on the lattices, because a uniform grid has no flow ridge to grow
   along — only Dijkstra's tie-breaking. Both ends of a lattice alignment are first aimed at an
   `InterchangeMap` entry within the transfer walk, so a suggestion can offer a change of vehicle;
   the map unions modes over neighbouring stops because a CS2 hub is several stop entities metres
   apart. An alignment that passes a hub further along is offered twice, direct and bent through
   the hub; the set selection decides, not a length ratio. Stops come from a stop plan per alignment
   (`SuitabilityStopPlan`): candidates every 50 m, journey doors within the mode's horizon as
   boarders, the corridor flow as through-riders, the prefabs' stop delay, forced interchanges,
   the gap floor; the mode is the smallest whose vehicles the standalone riders do not
   overload at the prefab headway (`TransitModes.ChooseMode`), a line needs three stops and
   a ride within its mode's limit; and the set of suggestions is chosen exactly
   (`SuitabilityLineSet.Solve`): every journey is routed door-to-door over the existing lines plus a
   candidate set, the objective is the passenger time saved against the best of walking and the
   existing network, ranked lexicographically after the equity share capped at its floor, and every
   line in the set must clear the utilisation floor on the set's own riders and must not duplicate
   the rest. There are no rounds any more; the mod optimises the objective the pipeline checks.
   **The whole route pipeline runs on a worker task** (`StartRoutePass` → `BuildRoutes` →
   `FinishRoutesIfReady`), because weighing one candidate against 1,400 journeys took two
   seconds and the set search asks for thousands of such evaluations; on the main thread that
   was a frozen game. A pass starts at most every 300 s (`RoutePassIntervalSeconds`) unless the
   objective or line count changed; the set search gets 60 s and half the cores. While
   `m_RoutesPending` is set, `OnUpdate` leaves every input the worker
   reads alone — no heat-map adoption or recompute, no demand refresh, no line collection, no
   recombine, no improvement or calibration request — and the pass's outputs are copied into
   the fields the panel, renderer and export read only when the task has completed. Code that
   can run on the worker logs through `DeferredLog`, never `Mod.Log`: the game's logger is an
   unguarded stream writer, so the worker's lines wait in a buffer and are flushed on adoption.
4. **Line health** — existing lines read in travel order (`SuitabilityLines.cs`) and judged
   (`SuitabilityLineHealth.cs`).

### The save state

`SuitabilitySaveState.cs` (a partial of the overlay system) implements `IDefaultSerializable`, which
is how the game persists a world system into the save (`SystemSerializerLibrary`, keyed by the
system's assembly-qualified type name). It carries the current suggestions, the observed
shopping/leisure journeys and the line readings, so a loaded city does not start cold and the
first route pass waits its normal interval. Two rules the format lives by: the block is **one
length-prefixed byte payload behind a format version**, because `ComponentSystemSerializer`
throws "Data size mismatch" unless a load consumes exactly what was written — an unknown version
reads the length, skips the bytes and starts cold; and a save made with the mod loads without it
(`SystemSerializer.DeserializeType` logs "Not serializable type" and skips the block). Bump
`SaveFormatVersion` whenever the payload layout changes; never make the parser depend on the game
state at load, it runs before the first `OnUpdate`.

### The verification export

`SuitabilityVerificationExport.cs` (a partial of the overlay system) writes the
offline pipeline in `verification/` a canonical instance of the live city, behind an
Options button. It is **read-only** — it exports what the mod already computed and
must never grow a rule of its own; anything it would have to decide belongs on the
side being verified, not here.

The one thing to preserve if you touch it: the inputs are captured **inside
`StartCompute`**, out of the very arrays being handed to the worker task, and the outputs are
that run's own terms. Re-collecting them at export time is the obvious simplification
and it is wrong — the input collections are rebuilt on their own timers, so the export
would describe a city the exported terms were never computed from.

The wire format is a contract with `verification/common/canonical.py`, which recomputes
the SHA-256 on load and refuses a file it cannot reproduce. `SuitabilityExportJson.cs`
owns it and is pinned by a golden-vector test; changing sort order, escaping or number
formatting there breaks every exported instance at once.

### Networks are not interchangeable

`SuitabilityRoadGraph` wraps either the real road entities or a free-form lattice
(`SuitabilityLattice.cs`, 128 m pitch, for rail and water). **`Network` must be set on every graph.**
Mode fallback keys off it: when nothing a lattice alignment can carry is justified, the corridor has
to be re-traced on streets before a bus may run it. Leaving the field at its enum default made every
graph claim to be a road, and ferry alignments were relabelled as buses and drawn across open water.

Road paths must be materialised with `MaterialisePath`, which inserts sampled `Curve.m_Bezier`
points; straight chords between intersections visibly leave the street.

## Working method

**Verify every game API by decompiling before writing code against it.** Guessing has been wrong
repeatedly, and the failures are silent rather than loud:

```bash
DOTNET_ROLL_FORWARD=LatestMajor ilspycmd \
  "/mnt/c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Game.dll" \
  -t Game.Simulation.TransportLineSystem
```

Hard-won facts worth not rediscovering:

- `RouteWaypoint`/`RouteSegment` on the **line** are travel-ordered and index-aligned. `ConnectedRoute`
  on a stop is *not* ordered and must never be used to infer a sequence.
- Fleet size is `round(stableDuration / targetInterval)`, where `stableDuration` includes the dwell at
  every stop and `targetInterval` is `TransportLineData.m_DefaultVehicleInterval` with the line's
  `RouteModifier` applied. `TransportLine.m_VehicleInterval` is the *achieved* interval, capped at 10x
  the target. The player sets the interval, never a vehicle count.
- `WaitingPassengers.m_AverageWaitingTime` is a pathfinder accumulator, not seconds; one stranded rider
  drives it into the thousands.
- A line's display name is a *formatted* name — the C# label helper returns the raw
  `"…{NUMBER}"` pattern, so the substitution has to be done against the active localization dictionary.
- The mod's `InfoviewPrefab` must stay valid and non-editor: `ToolSystem.SetInfoview` only activates a
  view's infomodes while `ToolSystem.activeInfoview` is non-null, and that getter returns null for an
  invalid view. Its row in the infoview menu is therefore hidden from the UI module, not from C#.
- The game's own pathfinder is agent-shaped and async; it cannot be used for bulk offline queries.

## The `.claude/` directory

- `rules/engineering-baseline.md` — always-on defaults, retargeted to this repo.
- `rules/pure-math.md` — scoped to the Unity-free files and the test project: the purity rule,
  why it exists, and the bugs that hid without it.
- `rules/ecs-systems.md` — scoped to the game-facing systems: decompile before using an API, mirror
  the game's own formulas, log every decision, respect the update phases, and the specific traps
  (buffer ordering, the infoview validity requirement, network identity).
- `rules/ui-module.md` — scoped to `dotnet/UI/**` and the binding system: cohtml's limits,
  `AddUpdateBinding`, the delimited-string payload contract, stable row keys, and why theme tokens
  cannot be trusted to resolve.
- `skills/` (8) and `books/` — depth to load when a task starts. These came from a Rust workspace and
  still speak of crates, traits and `cargo`; the design advice transfers, the toolchain nouns do not.
