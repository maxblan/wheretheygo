# What the game actually does

Facts about Cities: Skylines II that this mod depends on, each read out of the
decompiled assemblies rather than inferred from a name. They are here because every one
of them was wrong at least once when guessed.

Re-check any of them with:

```bash
DOTNET_ROLL_FORWARD=LatestMajor ilspycmd \
  "/mnt/c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Game.dll" \
  -t Game.Simulation.TransportLineSystem
```

## The clock and when people travel

- A day is `normalizedTime ∈ [0, 1)`, 0 = midnight. `TransportLineSystem` calls it night
  when `normalizedTime < 0.25` or `≥ 11/12` — 22:00 to 06:00 — and a day-only line is
  inactive then.
- `WorkerSystem.GetTimeToWork` computes
  `frac(RoundToInt(24 · (m_WorkDayStart + offset)) / 24)`, with `offset` a per-citizen
  ±1 h plus **0.33 of a day for the evening shift and 0.67 for the night shift**.
  **The game rounds to whole hours.** Without that rounding the evening shift lands on
  16:92 instead of 17:00, and every evening commute is stamped an hour early
  (`Daytime.WorkHours` mirrors the formula).
- The per-citizen offset is
  `WorkerSystem.GetWorkOffset(citizen) = (-10922 + citizen.GetPseudoRandom(
  CitizenPseudoRandom.WorkOffset).NextInt(21845)) / 262144f` — exactly **±1 hour**, and
  deterministic from `Citizen.m_PseudoRandom`, so it can be reproduced in a Burst job
  (`Unity.Mathematics.Random` is plain integer arithmetic). It is not a detail: after the
  rounding it spreads a city's day shift across **three** departure hours. Leaving it out
  gave the panel's hour strip one spike where the game has a rush hour.
- Students draw the **same** offset (`StudentSystem.GetStudyOffset` reads the same
  `CitizenPseudoRandom.WorkOffset`) and keep the day shift's hours, but
  `GetTimeToStudy` is `frac(m_WorkDayStart + offset)` with **no RoundToInt** — so a
  student's hour is the one their time falls in, not a rounded one
  (`Daytime.StudyHours`).

## Lines, stops and vehicles

- `RouteWaypoint` and `RouteSegment` **on the line** are travel-ordered and
  index-aligned. `ConnectedRoute` **on a stop** is not ordered and must never be used to
  infer a sequence.
- A hub is several stop entities metres apart — the train platform, the metro entrance
  below it, the bus stand out front. What modes a place offers is the union over the
  stops within walking distance of each other, never one stop's own mode.
- Fleet size is `round(stableDuration / targetInterval)` (to even), where
  `stableDuration` includes the dwell at every stop. The player sets a vehicle COUNT
  (`VehicleCountSection`); the game turns it into a slider position on the vehicle-count
  policy prefab, lerps that onto the `VehicleInterval` modifier's range and applies the
  modifier to the prefab interval, so the two ends of the slider bound the fleet.
- **A route segment carries TWO durations and they are not the same number.** This has
  cost us a round of wrong ridership, so it is worth stating plainly:
  - `PathInformation.m_Duration` is what the pathfinder found — free-flow, no traffic,
    no dwell. The game uses it for exactly one thing: `stableDuration`, which sizes the
    fleet. Never for a rider.
  - `RouteInfo.m_Duration`, on the same segment entity, is that figure scaled by what
    the vehicles actually achieve. `TransportLineSystem.RefreshLineSegments` walks the
    line leg by leg, sets `num = max(Σ pathDuration over the leg, VehicleTiming
    .m_AverageTravelTime) + stopDuration`, accumulates that into `lineDuration`, and
    writes back `RouteInfo.m_Duration = pathDuration × max(1, num / Σ pathDuration)`.
    So the dwell and the real achieved speed are both inside it, and summing
    `RouteInfo.m_Duration` over a line gives `lineDuration` exactly.
  - **The citizens' own pathfinder uses `RouteInfo`**:
    `PathUtils.GetTransportLineSpecification` sets
    `m_MaxSpeed = RouteInfo.m_Distance / RouteInfo.m_Duration`. Anything modelling what
    a rider experiences must read `RouteInfo`. On a real city the two differ by a factor
    of **3 to 11** — measured on Valmare's sixteen lines, 2026-09-14.
- **`VehicleTiming.m_AverageTravelTime` can be garbage, and it poisons `RouteInfo`.**
  `RouteUtils.UpdateAverageTravelTime` computes `(arrivalFrame - departureFrame) / 60f`
  on two **unsigned** frame counters, and `departureFrame` is regularly set AHEAD of now
  (`CalculateDepartureFrame`, or `m_SimulationFrameIndex + 60` for a vehicle not yet en
  route). The subtraction wraps, giving 2³²/60 = **71 582 788 s**, and the running
  average halves that into smaller but equally false numbers afterwards. The game
  answers it by clamping the interval it publishes at `10 × target`, so
  `m_VehicleInterval × fleetTarget` is the longest loop it will admit to — which agrees
  with `Σ RouteInfo.m_Duration` to the second wherever the data is sound.
- **A transit edge is one-way.** The same method sets `m_Flags |= EdgeFlags.Forward` and
  no backward flag. A line is a closed loop driven in one direction: stepping one stop
  "backwards" is not a cheap shortcut, it is riding the whole loop round.
- `TransportLine.m_VehicleInterval` is **not** a measured headway: it is
  `min(10 × target, lineDuration / targetFleet)` — note `lineDuration`, the RIDDEN one
  above, not the pathfinder's ideal. On a running line it is the planned interval, on an
  inactive one the whole duration. A metro with no vehicles therefore reports an
  interval of thirteen hours, which is why the router charges it a wait nobody would sit
  through: the line genuinely does not run. When the interval sits at exactly
  `10 × target` it is CLAMPED, and the line's real duration can only be read as "at
  least `10 × target × fleet`".
- `RequireVehicles` / `NotEnoughVehicles` mean "fewer out than the target" and "a request
  the game could not fill" — supply, not demand.
- `WaitingPassengers.m_AverageWaitingTime` is a pathfinder accumulator, not seconds; one
  stranded rider drives it into the thousands.
- A line's display name is a *formatted* name: the label helper returns the raw
  `"…{NUMBER}"` pattern, so the substitution has to be done against the active
  localization dictionary.

## Infoviews and overlays

- `ToolSystem.SetInfoview` only activates a view's infomodes while
  `ToolSystem.activeInfoview` is non-null, and that getter returns null for an **invalid**
  view. Hiding the mod's row by invalidating its prefab therefore kills the whole
  infoview silently. Presentation-only hiding belongs in the UI module.
- `ToolSystem.GetInfomodes` reads the infoview ENTITY's `InfoviewMode` buffer, not the
  managed prefab. For runtime-registered prefabs that buffer can be missing or empty,
  which leaves the panel without rows.
- `ToolSystem.Activate` hands an active infomode `m_Index = colorGroup * 4 + (1-based
  count)` with no bounds check. Colour group 0 is the terrain heatmap group (four
  channels of one RGBA texture); anything that does not override `GetColorGroup` lands in
  the object colour group, which is what colours buildings.
- The game auto-activates an infoview for a build-menu asset through
  `PlaceableInfoviewItem`. A mod's infomodes carry no vanilla match data and score a
  neutral 0, which beats assets whose vanilla infomodes all score negative — so the mod
  has to undo that, from a snapshot taken before its own infoview exists.
- `OverlayRenderSystem` drains and clears its buffer during `SystemUpdatePhase.Rendering`,
  which runs *before* PreCulling. Anything drawing into it must be registered in
  Rendering, and `Game.UpdateSystem` sorts by `(phase, registration index)` and **ignores
  `[UpdateBefore]`/`[UpdateAfter]` attributes entirely** — the two-type
  `UpdateBefore<A, B>` overloads are the only mechanism that actually orders against a
  named game system.
- The overlay buffer draws lines, curves, dashed variants of both, circles and custom
  meshes. **There is no text primitive.** `DrawCurve` takes one height for the whole
  curve, so anything following the ground has to be sampled into segments.
- Every line and curve call has a **second signature with an outline**:
  `DrawLine(outlineColor, fillColor, outlineWidth, styleFlags, line, width, roundness)`.
  Useful for a casing, but not for a curve sampled into pieces — the outline is drawn
  per call, so a chain of segments comes out with a dark seam at every joint. Two passes
  (all casings, then all fills) is the way.
- `CustomMeshType` is `Cylinder`, `Arrow`, `Plane`. `GuideLinesSystem` is the only
  vanilla caller of `DrawCustomMesh` with `Arrow`, and it establishes the convention:
  `Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z), Vector3.up)` points the mesh
  along `dir`, and it is drawn **twice, the second time with the height negated**,
  because the mesh is one-sided.
- **The curve primitive cannot draw anything that leaves the ground plane**, which is
  why the desire bands are tubes. Three independent reasons, all in
  `OverlayRenderSystem.Buffer`: every entry point measures `length` as
  `MathUtils.Length(curve.xz)`, the length in the MAP PLANE, and `DrawCurveImpl`
  silently drops anything under 1 cm of it; the unprojected ("absolute") curve is drawn
  as ONE flat quad (`GetMesh(box: false)` is four vertices at local y = 0); and
  `FitQuad(Bezier4x3, …)` fits that quad's plane by taking `cross(forward, b − a)` and
  `cross(forward, d − c)`, negating whichever has `y < 0`, and adding them. For a bow to
  the SIDE the two are opposite and the negation makes them agree — it works. For a bow
  purely in Y both have `y == 0` exactly, nothing is negated, they cancel, and the code
  falls back on `up = (0, 1, 0)`: a horizontal quad. A vertical arc is precisely that
  function's degenerate case.
- The `cameraFacing` parameter on `DrawLine` and on `FitQuad` is **dead in this build**:
  checked in IL, the argument is never loaded in either body.
- **The `Cylinder` mesh is built in code** (`GetCustomMeshMesh`), so its geometry is a
  fact and not a guess: 64 sides, radius 1 in the local XZ plane, from y = −0.5 to
  +0.5. So its axis is its **local Y**, `DrawCustomMesh`'s `width` is a **radius** (the
  TRS scale is `(width, height, width)`) and `height` is the full length, both centred
  on `position`. It has **no end caps**. The only vanilla caller is the water source in
  `GuideLinesSystem`, standing upright with `Quaternion.identity`, so a rotated chain is
  ours to establish — `Quaternion.FromToRotation(Vector3.up, direction)` aims one piece.
  The `Arrow` mesh, likewise generated: flat in its local XY plane (every z is 0), base
  at y = 0, tip at y = 3, so it is a FLAG, which is why it came out pointing across a
  band when aimed flat.
- All overlay instances — projected curves, absolute curves and every custom mesh — are
  drawn with **one shared bounds**, `m_BoundsData`, and that value is **never reset**:
  `DrawCircleImpl`/`DrawCurveImpl` only ever union into it with `|=`. So it grows to
  cover everything drawn since the world loaded, and geometry high above the ground
  cannot be culled away by it. Custom meshes contribute nothing to it themselves.
- `ToolRaycastSystem.CalculateRaycastLine` and `CameraRayPlaneIntersect` are public
  statics, so a mod can compute the pointer's ray without owning a tool. Note that
  `CameraRayPlaneIntersect` uses the CAMERA's forward as the plane normal — it is not a
  ground intersection.

## The selected-object window

- `SelectedInfoUISystem.AddMiddleSection(ISectionSource)` adds a section; `InfoSectionBase`
  is the base to derive from, and a section is drawn only while `visible` is true.
- The game maps a section to its UI component by the **C# type name** the section writes
  (`IJsonWriter.TypeBegin(GetType().FullName)`), not by its `group` string. The map is
  `selectedInfoSectionComponents` in
  `game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx`,
  and its setter **assigns rather than merges** — writing a one-key object takes every
  vanilla section with it.

## What cannot be used

- The game's own pathfinder is agent-shaped and asynchronous. It cannot answer bulk
  offline questions like "how long would this journey take", which is why the mod builds
  its own transit graph and mirrors the pathfinder's cost model instead
  (walk, wait and ride weighed the same; a change costs its walk and its wait).
- Cities: Skylines II keeps no per-stop ridership history. `WaitingPassengers` is an
  instantaneous queue re-tallied every few hundred simulation frames, and the city
  statistics only expose per-mode totals for the whole city.

## The UI bundle

The game's entire UI ships as readable JavaScript in
`Cities2_Data/Content/Game/UI/index.js` (2.2 MB) with its CSS beside it. Modules are
registered as `Q.add("game-ui/…", { get Export() {…} })`, which is the same registry
`moduleRegistry.registry.get(path)` reads — so every export name and every prop can be
read off rather than guessed.

- `window` carries `React`, `ReactDOM`, `cs2/api`, `cs2/bindings`, `cs2/l10n`, `cs2/ui`,
  `cs2/utils`, `cs2/input`, `cs2/modding`, `cohtml/cohtml` and **`chart.js`**.
- `cs2/ui` exports `Button`, `ConfirmationDialog`, `Dropdown`, `DropdownItem`,
  `DropdownToggle`, `FloatingButton`, `FormattedParagraphs`, `FormattedText`, `Icon`,
  `MarkdownRenderer`, `MarkupRenderer`, `MenuButton`, `Panel`, `PanelFoldout`,
  `PanelSection`, `PanelSectionRow`, `Portal`, `Scrollable`, `Tooltip`. `PanelSection`
  and `PanelSectionRow` resolve to the **same values** as the selected-info panel's
  `InfoSection` and `InfoRow`.
- `InfoRow` takes `{icon, left, center, right, tooltip, link, uppercase, subRow,
  subRowDimmed, disableFocus, className, noShrinkRight, justifyLeft}`. A vanilla section
  is `InfoSection > InfoRow(uppercase, title) > InfoRow(subRow, left/right)` — that is
  what `LineSection` does for "Länge / Haltestellen / Passagiere".
- `ResponsiveChart({type, data, options, mergeCallback, …divProps})` is a Chart.js
  canvas. The game sets `Chart.defaults.events = []`, `animation = false` and disables
  the legend and tooltip plugins globally, so a chart drawn here has no hover behaviour
  of its own.
- `ValueBarSection({title, value: {min, current, max}, gradient: {stops}, tooltip,
  children})` with `InfoviewPanelLabel({small, uppercase, text, rightText})` is how every
  vanilla infoview panel shows a city-wide figure.
- `FloatingMouseTooltip({tooltip, position, screenSpacePosition, alwaysVisible, …})`
  follows the pointer; with `screenSpacePosition: true` it binds to `document.body`
  itself, so no position has to be supplied.
- Each `*.module.scss` module exports `classes`, the generated class-name map, so vanilla
  class names are reachable where a component is not.
- `Q.get` **throws** on an unknown module path — every lookup needs a try/catch.
