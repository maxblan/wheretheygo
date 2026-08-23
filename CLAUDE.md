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
make verify        # check-ui + test + build + status — the gate before a run
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
project: `SuitabilityScoring.cs` (percentiles, site selection, geodesic catchment, weight fitting),
`SuitabilityGraphMath.cs` (CSR graph, Dijkstra, corridor growth, RDP), `SuitabilityTransit.cs`
(transit routing and boarding counts). Anything touching Unity or ECS types is untestable here, so
**keep algorithms out of the ECS systems**. Several silent bugs — double-counted demand, summed
instead of averaged corridor flow, free transfers — were only caught because the math was reachable
from a test.

`SuitabilityLineHealth.cs` is pure except for a `Setting.ModePreset` reference, which is why its
thresholds are *not* currently under test.

### Data flow

`StationSuitabilityOverlaySystem.cs` (~3000 lines) orchestrates everything and is where most work
lands:

1. **Heatmap** — terrain/water masks (`SuitabilityMasks.cs`) → a Burst job emitting seven raw terms
   per cell (`SuitabilityJob.cs`) → per-term percentile normalization → intensities written into a
   terrain overlay channel obtained by reflection. Channel index is `InfomodeActive.m_Index - 1`.
2. **Travel demand** — real home→work/school journeys read from `Citizen`/`HouseholdMember`
   (`SuitabilityTravelDemand.cs`), aggregated into 256 m zones, discounted by whether the existing
   network can actually route them, then assigned to a network by shortest path.
3. **Route suggestion** — corridor growth with flow peeling and novelty decay over a network
   (`SuitabilityRoutes.cs`), stops placed at mode spacing, mode chosen from flow against city-wide
   floors, then re-scored transfer-aware over the transit graph.
4. **Line health** — existing lines read in travel order (`SuitabilityLines.cs`) and judged
   (`SuitabilityLineHealth.cs`).

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
- `rules/pure-math.md` — scoped to the three Unity-free files and the test project: the purity rule,
  why it exists, and the bugs that hid without it.
- `rules/ecs-systems.md` — scoped to the game-facing systems: decompile before using an API, mirror
  the game's own formulas, log every decision, respect the update phases, and the specific traps
  (buffer ordering, the infoview validity requirement, network identity).
- `rules/ui-module.md` — scoped to `dotnet/UI/**` and the binding system: cohtml's limits,
  `AddUpdateBinding`, the delimited-string payload contract, stable row keys, and why theme tokens
  cannot be trusted to resolve.
- `skills/` (8) and `books/` — depth to load when a task starts. These came from a Rust workspace and
  still speak of crates, traits and `cargo`; the design advice transfers, the toolchain nouns do not.
