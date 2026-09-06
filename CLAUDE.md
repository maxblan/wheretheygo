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
make compile       # compile only, analyzers included — safe while the game runs
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
running game holds a lock on the deployed DLL. Close the game before building. To check that a
change compiles while the game is running, use `make compile`: it runs only the `Compile` target,
which the post-processor and the deploy (both hooked onto `AfterBuild`) never see, so `bin/` and the
Mods folder stay as they were. `ModPostProcessor`
also returns a spurious exit code if it runs within ~25 s of the game closing, so **verify a deploy
by comparing file sizes, not by trusting the exit code**. `make deploy` encodes the wait-and-compare.

**Tests.** `tests/SuitabilityScoring.Tests` is a hand-rolled harness with no test framework, so it
runs offline with nothing to restore. It is deliberately **not** in `smart-transit-planner.sln`, so
the mod toolchain build is unaffected. There is no filter flag: to run one test, comment out the
other `Run(...)` calls at the top of `Program.cs`. The harness links every `Planning/` folder under
`dotnet/` by glob, so a new pure file is tested for purity the moment it exists; the test bodies are
split by feature (`AlignmentTests.cs`, `PipelineTests.cs`, `HeatmapTests.cs`) beside `Program.cs`.

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
`StationSuitabilityOverlaySystem.OnCreate/OnUpdate/StartCompute`,
`SuitabilityScoring.FindTopSites` and `SuitabilityRouteRenderer.OnUpdate`. Splitting them is a
refactor rather than a fix and must not be done blind, which is what the ratchet is holding the line
for. (The 2026-09-05 restructure split the *class* by feature and moved arithmetic into the pure
core; it deliberately left method bodies as they were, so the ratchet figures still hold.) **Lower the numbers as methods are split; never raise them** — and check the real figure with a
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
module**, `dotnet/Presentation/UI/StationSuitabilityOverlay.mjs` plus `.css`, flattened out of `Presentation/UI/` on copy
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

### Layout: one folder per feature, three kinds of code inside

`dotnet/` is arranged by the stages of `docs/formal-specification.md`, numbered `F1`–`F7` in the
spec's order (`F<n>` is the spec's `S<n>`), then the features the spec treats as cross-cutting
sections, then the shell. Inside each feature folder the code is split by what it may touch:

| Folder | Spec | Planning (pure) | Gathering (reads the game) | Systems (the overlay system's partial) |
|---|---|---|---|---|
| `F1Heatmap` | S1 §7 | `SuitabilityWalkAccess`, `SuitabilityScoring`, `SuitabilityHeatmap` | `SuitabilityInputs`, `SuitabilityMasks` | `.F1Heatmap` |
| `F2Sites` | S2 §2 | `SuitabilityExactSites` | — | `.F2Sites` |
| `F3Demand` | S3 §7b | `SuitabilityZones`, `SuitabilityServedDemand`, `SuitabilityObservedTrips` | `SuitabilityTravelDemand` (Burst job), `SuitabilityTripObserver` | `.F3Demand`, `.F3Observed` |
| `F4Alignments` | S4 §3 | `AlignmentNetwork`, `SuitabilityRoutes`, `SuitabilityLattice`, `SuitabilityGraphMath`, `SuitabilityDirectedRoads`, `SuggestedRoute` | `SuitabilityRoads` | `.F4Alignments`, `.F4Networks` |
| `F5Stops` | S5 §4 | `SuitabilityStopPlan`, `SuitabilityRoutes.Stops` | — | `.F5Stops` |
| `F6Modes` | S6 §5 | `TransitMode.Choice` | `SuitabilityFleet` | `.F6Modes`, `.F6Fleet` |
| `F7LineSet` | S7 §6 | `SuitabilityTransit`, `SuitabilityLineSet` | — | `.F7LineSet` |
| `F8Equity` | §7c | `SuitabilityEquity` | — | `.F8Equity` |
| `F9LineHealth` | §7e | `ExistingLine`, `SuitabilityLineHistory`, `SuitabilityLineHealth` (window, ladder, fleet plan, verdicts) | `SuitabilityLines` (collect, observe) | `.F9LineHealth` |
| `F10Calibration` | §7 v2 | (the fit is in `SuitabilityScoring`) | `SuitabilityCalibration` | `.F10Calibration` |
| `F11Export` | — | `SuitabilityExportJson` | — | `.VerificationExport` |
| `F12Diagnostics` | — | `SuitabilityDiagnostics` (sanity checks, churn) | — | `.F12Diagnostics` |
| `Common` | §0, §7d | `Assumptions` (EVERY numeric constant and per-mode table), `float2Like`/`int2Like`, `TileGrid`, `SuitabilityDaytime`, `TransitMode` (enums, ladders, colours), `DeferredLog` | — | — |
| `Overlay` | — | — | — | `StationSuitabilityOverlaySystem` (orchestration), `.Panel`, `.RoutePass`, `.SaveState`, `SuitabilityInfoview`, `SuitabilityInfomodePrefab` |
| `Presentation` | — | `SuitabilityPanelPayload` | — | `SuitabilityRouteRenderer`, `SuitabilityPanelUISystem`, `UI/` |

`Common` holds what several features use and no feature owns. `Overlay` is the ECS shell: the
system that schedules the features, its save state and the panel bridge. `Presentation` draws.

### The purity rule

Numeric logic belongs in a `Planning/` folder, where files use `System.*` only, so they can be
linked into the offline test project. `tests/SuitabilityScoring.Tests` and `verification/subject`
link `dotnet/**/Planning/*.cs` by glob — every pure file is compiled outside the game on every
test run, with overflow checking on, and a Unity type in any of them breaks the whole harness.

- **No Unity, ECS, Colossal or Game types in `Planning/`** — not `float2`, not `Entity`, not
  `math.*`, not even in a signature. `float2Like` (`Common/Planning/float2Like.cs`) is the map-plane
  vector: it carries `x` and `y` (the world z) like `float2`, and every operation on it is the
  formula decompiled from `Unity.Mathematics`, so a value computed on the pure side is bit-identical
  to the same expression on `float2`. The conversion to `float2` happens in the `Gathering/` readers
  and the `Systems/` partials, as a two-field copy at the seam. `int2Like` is the grid pair.
- **The whole alignment stage is pure.** `SuitabilityRoutes`, `SuitabilityLattice` and
  `AlignmentNetwork` were converted from `float2` on 2026-09-05 and characterised against the
  Unity-typed code on a seeded synthetic city: 82 171 recorded values (paths, stops, stop plans,
  flows, log lines) identical bit for bit. `AlignmentTests.cs` pins a sample of that record, so the
  harness now covers corridor growth, lattice traces, stop planning, re-tracing and the network
  helpers. `SuitabilityRoads` is what remains on the game side: it reads street entities into
  plain arrays and hands them to `AlignmentNetwork.AdoptRoads`.
- **`DeferredLog` is pure and lives in `Common`.** It reaches the game's logger through
  `DeferredLog.Sink`, which `Mod.OnLoad` sets; offline the lines are dropped unless a test binds a
  buffer. Worker code logs through it for the reason `ecs-systems.md` gives — the game's logger is
  an unguarded stream writer.
- **Anything touching Unity or ECS types is untestable here, so keep algorithms out of the
  `Systems/` partials and the `Gathering/` readers.** They gather ECS data into plain arrays, call
  the pure code and map the result back. Several silent bugs — double-counted demand, summed
  instead of averaged corridor flow, free transfers, a corridor's node walk starting from a node
  that was not on the line — were only caught because the math was reachable from a test.
- **Every number the mod computes with lives in `Common/Planning/Assumptions.cs`** (user rule,
  2026-09-06): thresholds, speeds, spacings, windows, budgets, refresh cadences, options defaults
  and the per-mode tables (`CruiseSpeedFor`, `StopSpacingFor`, `CatchmentMs`, `MaxRideSecondsFor`),
  each annotated with its register row. Before writing a literal anywhere else, look there; a
  value that lives in one file cannot drift between two, which is how five copies of one mode
  table, a walk speed copied at 1.4 after the mod moved to 1.2 and a planning-headway table
  beside the prefab intervals all happened. `TransitMode.cs` keeps what is structure, not a
  value: the enums, `ModesFor`/`NetworkOf`, the colours. The subject runner and the evaluators
  never retype a value either — it reaches them through the instance files. UI slider bounds and
  wire-format versions stay with the settings page and the serializers.
- **A per-mode fact is one more `switch` in `Assumptions.cs`**, keyed on `ModePreset`; the mode
  *choice* (`ChooseMode`, `FleetFacts`, the game's fleet arithmetic `GameFleet`/`GameInterval`/
  `FleetSpan`) is the F6 half of `TransitModes`.
- **`SuitabilityExportJson` is pure for one reason:** the format is a CONTRACT with
  `verification/common/canonical.py`, which recomputes the digest on load and refuses any file it
  cannot reproduce, so a golden-vector test is the only thing standing between a wire-format drift
  and every exported instance becoming unloadable.

### Data flow

`StationSuitabilityOverlaySystem` orchestrates everything. It used to be one 5 700-line file; it is
now a partial class with one file per feature (`F<n>*/Systems/StationSuitabilityOverlaySystem.F<n>*.cs`),
the orchestration itself in `Overlay/StationSuitabilityOverlaySystem.cs` (`OnCreate`, `OnUpdate`,
the timers and dirty flags), and two collaborators with state of their own: `SuitabilityInfoview`
(prefabs, channels, painting) and `SuitabilityTripObserver` (the live-city journey scan). Each
partial declares the fields its feature owns; a field used by two features is declared where it is
produced. The pipeline stages have clean seams (`UpdateTravelDemand` → `BuildTransitModel` →
`StartRoutePass`/`BuildRoutes` → `FinishRoutesIfReady`/`AdoptPass`).

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
   `TravelPurpose`/`Target`/`CurrentBuilding` (`SuitabilityTripObserver`) and held for a game day,
   aggregated into 256 m zones (`SuitabilityZones`), discounted by whether the existing network
   can actually route them (`ServedDemand`), then assigned to a network by shortest path.
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
4. **Line health** — a reading of every line every 15 game minutes (`SuitabilityLines.Observe`
   → `LineHistory`, on the simulation frame, never gated on the route worker); the full
   collection in travel order (`SuitabilityLines.Collect`) every 30 s while no pass is out; the
   riders the last pass's baseline attributed to each existing line (`AdoptExistingLineRiders`);
   then `SuitabilityLineHealth.JudgeAll` on a `LineHealthProblem` captured at that instant (the
   export reads the same object). Verdicts are a classification of the plan: the S6 ladder and
   the fleet rule within the game's vehicle-slider span, judged on the window's 90 % planning
   load and the routed riders; "empty" needs the readings AND the demand (spec §7e).

None of this is gated on the heat map being drawn: the passes run whenever a city is loaded, and
`active` only decides whether scores are painted. A finished route pass is **staged**
(`m_StagedPass`, binding `routeUpdate`) until the panel's button triggers `applyRouteUpdate`; only
an empty list is adopted without asking. Anything that must see the newest routes reads them after
`AdoptPass`, not when the worker finishes.

### The save state

`Overlay/StationSuitabilityOverlaySystem.SaveState.cs` (a partial of the overlay system) implements `IDefaultSerializable`, which
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

`F11Export/Systems/StationSuitabilityOverlaySystem.VerificationExport.cs` (a partial of the overlay system) writes the
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
the SHA-256 on load and refuses a file it cannot reproduce. `F11Export/Planning/SuitabilityExportJson.cs`
owns it and is pinned by a golden-vector test; changing sort order, escaping or number
formatting there breaks every exported instance at once.

### Networks are not interchangeable

`AlignmentNetwork` (pure) holds either the streets as `SuitabilityRoads` read them or a free-form
lattice (`SuitabilityLattice.cs`, 128 m pitch): one for train, one for metro and one for water. Train and
metro track are told apart by `TrackLaneData.m_TrackTypes` on the segment's sub-lanes
(`CollectTrackSegments`), and each lattice prefers only its own kind — a metro alignment on a railway
was a real finding. A rail alignment is a candidate only where its street re-trace is overloaded,
impossible or fails the gates (`ResolveCandidate`). **`Network` must be set on every graph.**
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
- Fleet size is `round(stableDuration / targetInterval)` (to even), where `stableDuration` includes the
  dwell at every stop and `targetInterval` is `TransportLineData.m_DefaultVehicleInterval` with the
  line's `RouteModifier` applied. **The player sets a vehicle COUNT** (`VehicleCountSection`); the game
  turns it into a slider position on the vehicle-count policy prefab, lerps that onto the
  `VehicleInterval` modifier's range and applies the modifier to the prefab interval — so the two ends
  of the slider bound the fleet (`TransitModes.FleetSpan`, read from
  `UITransportConfigurationPrefab.m_VehicleCountPolicy`). `TransportLine.m_VehicleInterval` is NOT a
  measured headway: `min(10 × target, pathDuration / targetFleet)`, i.e. the planned interval on a
  running line and the whole path duration on an inactive one (a day-only line at night). The
  `RequireVehicles`/`NotEnoughVehicles` flags mean "fewer out than the target" / "a request the game
  could not fill" — supply, not demand.
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
- `rules/pure-math.md` — scoped to every `Planning/` folder and the test project: the purity rule,
  why it exists, and the bugs that hid without it.
- `rules/ecs-systems.md` — scoped to the `Gathering/` readers, the `Systems/` partials, `Overlay/`
  and `Presentation/`: decompile before using an API, mirror
  the game's own formulas, log every decision, respect the update phases, and the specific traps
  (buffer ordering, the infoview validity requirement, network identity).
- `rules/ui-module.md` — scoped to `dotnet/Presentation/UI/**` and the binding system: cohtml's limits,
  `AddUpdateBinding`, the delimited-string payload contract, stable row keys, and why theme tokens
  cannot be trusted to resolve.
- `skills/` (8) and `books/` — depth to load when a task starts. These came from a Rust workspace and
  still speak of crates, traits and `cargo`; the design advice transfers, the toolchain nouns do not.
