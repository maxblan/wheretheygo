# Station Suitability Overlay

A Cities: Skylines II mod that adds a vanilla-style infoview scoring every 32 m tile for transit station placement, based on multi-criteria decision analysis (MCDA).

## Features

- **Station Suitability infoview** in the game's infoview menu, with standard gradient legends
- **Ten toggleable map layers** — the combined score, the recommended sites, travel demand, and one layer per scoring term (demand, jobs, existing coverage, accessibility, future demand, interchange potential, cross-mode overlap) so you can see *why* a tile scores the way it does. The terrain overlay has four channels, so up to four layers can be shown at once; a fifth is skipped with a note in the log.
- **Ranked site recommendations** — instead of only a gradient, the best distinct locations are marked as discrete spots, spaced at least one catchment apart and re-scored by true walking distance
- **Catchment-based scoring** over five criteria, each normalized to a comparable 0–1 scale before weighting:
  - Demand — residents within the catchment (weight W1)
  - Jobs — actual workplace capacity from companies and city service buildings (W2)
  - Existing coverage — stops of the selected mode penalize nearby tiles (W3). Stops that no line serves are ignored, since they provide no service.
  - Accessibility — road network density within the access radius (W4); tiles with no road access are suppressed entirely
  - Future demand — land that is zoned but not yet built on (W5), so you can place stops ahead of a district filling in
  - Interchange potential — a served stop of a *different* mode within transfer distance (W6), which is what makes a bus stop at a metro station rate highly
  - Cross-mode overlap — another mode's service close enough to carry the same riders but too far to transfer to (W7), which discourages running parallel to an existing line
- **Terrain awareness** — tiles too steep to build on or under water score nothing, and a catchment never draws population across water or a cliff it has no route around
- **Bus / Tram / Metro / Train / Ferry presets** with per-mode weights and radii, all adjustable in Options → Station Suitability Overlay. Ferry mode restricts candidates to the shoreline.
- **Ridership calibration** — the mod samples your served stops while the city runs, then fits the demand, jobs, accessibility and future weights to the observed data and reports how well the model explains it (R²). Fitted values are only suggestions until you press **Apply fitted weights**.
- **Auto-recalculation** (debounced, off the main thread) when stops are placed or removed or settings change, plus a periodic refresh so new roads, zones and residents appear on their own
- **No surprise activation** — the game's automatic "related infoview" selection for build-menu assets has this mod's infoview stripped out and the vanilla choice restored, so the overlay only appears when you pick it
- **Travel demand map and route suggestions** — real home-to-work and home-to-school journeys read from the save, shown as desire lines, and grown into ranked line suggestions with stops and a recommended mode
- **English and German** localization

## How it works

The mod computes the score terms over the playable area in a parallel Burst job (with spatial bucketing for stops, roads, workplaces and zoned cells), then feeds intensities through the game's own heatmap-infomode pipeline: each active layer writes into the terrain overlay channel assigned to its infomode, and the vanilla terrain shader colors it with that infomode's gradient — the same mechanism as ground pollution or land value, so the overlay looks and behaves like a built-in infoview.

### Distance model

Two different distance models are used deliberately, and it is worth knowing which is which:

- **The heatmap** uses straight-line distance, gated by landmass. A tile only accumulates demand from sources on the same connected stretch of walkable terrain, which removes the "draws population from across the river" error cheaply enough to run on every one of ~200,000 tiles.
- **The recommended sites** are then re-scored with a true walking-distance expansion (Dijkstra over the walkable terrain), which is only affordable because there is a handful of them. This is why a site's reported score differs from the raw heatmap value under it.

Neither model accounts for pedestrians being unable to cross a road mid-block; barriers are terrain and water.

### How the modes interact

The modes are not scored in isolation. Every *other* mode's served stops feed two terms drawn from the same set of stops and separated purely by distance:

- **Within transfer distance** (250 m, or the catchment if that is shorter) another mode's stop makes the location *more* valuable, because a rider can change vehicles there and the new stop feeds an existing trunk line.
- **Beyond that, out to the catchment**, the same stop counts *against* the location, because it already carries some of the same riders without offering a transfer.

Both are weighted by how much capacity the other mode represents (train and metro count for far more than another bus) and expressed relative to the mode being placed. That asymmetry is deliberate: a bus gains a great deal from sitting at a metro station, while a metro gains comparatively little from sitting at a bus stop — the smaller mode should come to the trunk, not the other way around.

Only stops that a line actually calls at count for either term. That also means no line-identity check is needed for a transfer: two served stops of different modes necessarily run different lines, so a genuine transfer is always possible.

What this does *not* model is parallel duplication along a corridor — a bus route running alongside a metro rather than feeding it. Detecting that needs line geometry rather than stop positions.

### Travel demand and route suggestions

The mod also answers the other half of the question: not just where a stop belongs, but where people are trying to go.

**The demand is real, not modelled.** Cities: Skylines II stores each citizen's household and workplace or school as entity references, so the mod reads actual home-to-work and home-to-school journeys straight out of the save. There is no gravity model or estimation. Tourists, the homeless and outside commuters are excluded (they have no fixed home in the city). School trips count at a lower weight than commutes.

Journeys are then aggregated into coarse zones, discounted by how well your existing network already serves both of their endpoints, and loaded onto the road network by shortest path. The **Travel demand** layer shows the result as desire lines: where movement wants to happen, weighted towards what you are *not* already carrying.

**Journeys are routed over your actual network.** The mod builds a transit graph from your existing lines — stops, per-line boarding points, rides between consecutive stops, and walking links between stops close enough to interchange — and routes every journey over it. A journey counts as already served only if the network can genuinely carry it in reasonable time, not merely because both its ends happen to sit near a stop.

That same graph decides what a suggestion is worth. A candidate line is credited for every journey it forms **any** leg of, discounted for each change of vehicle (the Transfer penalty setting). This is what lets a short feeder rank properly: a line whose own corridor carries almost nobody can still be the leg that unlocks hundreds of journeys onto a trunk service. Wait times, the boarding cost and the absence of any separate transfer penalty all follow the game's own pathfinder, so the model behaves like the simulation rather than like an invention.

**Line health** reports on what you have already built. For each existing line the mod reads its fleet, how full its vehicles are, how long people wait, and the game's own "needs more vehicles" flags, then gives a verdict with a concrete remedy: add N vehicles, upgrade to the next mode up, shorten the route, or reroute a line that runs nearly empty. Problem lines are listed worst-first in the panel and tinted by severity on the map, and every verdict shows the numbers it came from.

**Route suggestions** are grown from that loaded network. The strongest corridor is grown outward along the heaviest remaining flow, stops are placed at mode-appropriate spacing and nudged onto the best-scoring nearby tile, and a mode is assigned from the corridor's flow intensity and length. The demand that line would carry is then removed from the pool before the next suggestion, so later lines complement earlier ones instead of stacking on the same street. Suggestions are drawn as coloured polylines with stop markers while the infoview is open.

The **Route objective** setting changes what a line is grown for: maximum ridership follows the busiest journeys and may leave outlying districts unserved; maximum coverage spreads out to reach more districts even where demand is thin; balanced does both.

Known limits, since these matter when reading the output:

- Ferries are only suggested where a route detours a long way around water that a direct crossing would cut out. Ferry corridors are not searched over water, so a genuinely good ferry route across open water will not be found.
- Corridor growth is greedy. Transit network design is NP-hard; these are suggestions, not optimal networks.

### Calibration

Cities: Skylines II keeps no per-stop ridership history — `WaitingPassengers` is an instantaneous queue that is re-tallied every few hundred simulation frames, and the city statistics only expose per-mode totals for the whole city. The mod therefore builds its own series, sampling each served stop once a minute of unpaused play and persisting per-stop aggregates.

The regression target is an **arrival rate** derived by Little's law (queue length ÷ average wait), not the queue length itself. A queue measures congestion rather than demand: a busy stop with good service drains its queue and would look idle, so fitting against queue length directly would mislead. The existing-coverage weight is deliberately not fitted — avoiding duplicate service is a planning preference, not a prediction of ridership.

A fit needs at least 8 stops with at least 30 samples each, so expect to play for a while before it reports anything beyond collection progress.

## Requirements

- Cities: Skylines II with the official modding toolchain installed (sets `CSII_TOOLPATH` / `CSII_USERDATAPATH`)
- .NET SDK

## Build

```powershell
./build.ps1            # Release
./build.ps1 -Configuration Debug
```

or `dotnet build dotnet/StationSuitabilityOverlay.csproj -c Release`. The build deploys to `%CSII_USERDATAPATH%\Mods\StationSuitabilityOverlay` automatically. The game locks the deployed DLL, so close it before building.

## Tests

The scoring math (percentile normalization, site selection, weight fitting) is free of Unity types so it can be tested directly:

```bash
dotnet run --project tests/SuitabilityScoring.Tests
```

No packages to restore; a non-zero exit code means a failure. This project is intentionally not part of the solution so the mod toolchain build is unaffected.

## Usage

1. Load a city and open the infoview menu (the ⓘ button), then select **Station Suitability**.
2. The brightest tiles are the best locations for a new stop; enable **Recommended sites** for discrete ranked suggestions.
3. Turn on individual term layers to understand a surprising result.
4. Tune mode preset, weights and radii under Options → Station Suitability Overlay; the overlay recalculates automatically.

## License

See [LICENSE](LICENSE).
