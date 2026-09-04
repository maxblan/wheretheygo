---
paths:
  - "dotnet/SuitabilityScoring.cs"
  - "dotnet/SuitabilityExactSites.cs"
  - "dotnet/SuitabilityWalkAccess.cs"
  - "dotnet/SuitabilityGraphMath.cs"
  - "dotnet/SuitabilityTransit.cs"
  - "dotnet/SuitabilityLineHistory.cs"
  - "dotnet/SuitabilityLineHealth.cs"
  - "dotnet/TransitMode.cs"
  - "dotnet/SuitabilityExportJson.cs"
  - "tests/SuitabilityScoring.Tests/**"
---

# The testable core

You are in the half of this mod that can be executed without the game. These files use `System.*`
only, which is what lets `tests/SuitabilityScoring.Tests` link them and run offline. That property
is the whole reason they exist as separate files.

- **No Unity, ECS, Colossal or Game types here** — not `float2`, not `Entity`, not
  `EntityManager`, not `math.*`, not even in a signature. `float2Like` in
  `SuitabilityGraphMath.cs` exists precisely because `Unity.Mathematics.float2` may not appear.
  A single such reference breaks the test project for every algorithm in the file.
- **A per-mode fact belongs in `TransitMode.cs`**, as one more `switch` beside the others. That
  file exists because `ModePreset` used to be nested inside `Setting`, which drags in Colossal and
  Game: with no Unity-free home for a mode table, five of them grew separate copies in separate
  files — two byte-for-byte identical — and a sixth in the panel's JavaScript.
- **Algorithms belong here, not in the ECS systems.** If you are about to write a loop with real
  arithmetic in `StationSuitabilityOverlaySystem.cs`, `SuitabilityRoutes.cs` or
  `SuitabilityLines.cs`, ask whether it can be expressed against plain arrays and moved here
  instead. Every numerical bug this mod has shipped that stayed hidden — demand counted once per
  Dijkstra pop, corridor flow summed instead of averaged, transfers costing nothing because an
  undirected graph made the alight edge a free boarding — was invisible until the math was
  reachable from a test.
- **Every change here gets a test in the same commit**, and the test asserts the property that was
  wrong, not merely that the function returns something. `Run(...)` in `Program.cs` is the
  registry; there is no discovery.
- **Keep the harness dependency-free.** No NuGet package, no test framework, and the project stays
  out of `smart-transit-planner.sln` so the mod toolchain build is unaffected. Exit code is the
  failure count.
- **Determinism is a requirement, not a nicety.** These functions run on every recompute and their
  output is compared across runs by eye and in the log. No time, no randomness, no dictionary or
  hash iteration order deciding anything observable, no reliance on floating-point accumulation
  order that a reordering would change.
- **Reused workspaces must be cleared, not trusted.** `DijkstraWorkspace` keeps a touched-list so
  it can be reused without an O(n) wipe; anything similar you add must be provably clean on entry,
  and a test must assert that reuse gives identical results to a fresh instance.
