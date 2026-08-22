# Station Suitability Overlay

A Cities: Skylines II mod that adds a vanilla-style infoview scoring every 32 m tile for transit station placement, based on multi-criteria decision analysis (MCDA).

## Features

- **Station Suitability infoview** in the game's infoview menu, with standard gradient legends
- **Seven toggleable map layers** — the combined score, the recommended sites, and one layer per scoring term (demand, jobs, existing coverage, accessibility, future demand) so you can see *why* a tile scores the way it does. The terrain overlay has four channels, so up to four layers can be shown at once; a fifth is skipped with a note in the log.
- **Ranked site recommendations** — instead of only a gradient, the best distinct locations are marked as discrete spots, spaced at least one catchment apart and re-scored by true walking distance
- **Catchment-based scoring** over five criteria, each normalized to a comparable 0–1 scale before weighting:
  - Demand — residents within the catchment (weight W1)
  - Jobs — actual workplace capacity from companies and city service buildings (W2)
  - Existing coverage — stops of the selected mode penalize nearby tiles (W3). Stops that no line serves are ignored, since they provide no service.
  - Accessibility — road network density within the access radius (W4); tiles with no road access are suppressed entirely
  - Future demand — land that is zoned but not yet built on (W5), so you can place stops ahead of a district filling in
- **Terrain awareness** — tiles too steep to build on or under water score nothing, and a catchment never draws population across water or a cliff it has no route around
- **Bus / Tram / Metro / Train / Ferry presets** with per-mode weights and radii, all adjustable in Options → Station Suitability Overlay. Ferry mode restricts candidates to the shoreline.
- **Ridership calibration** — the mod samples your served stops while the city runs, then fits the demand, jobs, accessibility and future weights to the observed data and reports how well the model explains it (R²). Fitted values are only suggestions until you press **Apply fitted weights**.
- **Auto-recalculation** (debounced, off the main thread) when stops are placed or removed or settings change, plus a periodic refresh so new roads, zones and residents appear on their own
- **No surprise activation** — the game's automatic "related infoview" selection for build-menu assets has this mod's infoview stripped out and the vanilla choice restored, so the overlay only appears when you pick it
- **English and German** localization

## How it works

The mod computes the score terms over the playable area in a parallel Burst job (with spatial bucketing for stops, roads, workplaces and zoned cells), then feeds intensities through the game's own heatmap-infomode pipeline: each active layer writes into the terrain overlay channel assigned to its infomode, and the vanilla terrain shader colors it with that infomode's gradient — the same mechanism as ground pollution or land value, so the overlay looks and behaves like a built-in infoview.

### Distance model

Two different distance models are used deliberately, and it is worth knowing which is which:

- **The heatmap** uses straight-line distance, gated by landmass. A tile only accumulates demand from sources on the same connected stretch of walkable terrain, which removes the "draws population from across the river" error cheaply enough to run on every one of ~200,000 tiles.
- **The recommended sites** are then re-scored with a true walking-distance expansion (Dijkstra over the walkable terrain), which is only affordable because there is a handful of them. This is why a site's reported score differs from the raw heatmap value under it.

Neither model accounts for pedestrians being unable to cross a road mid-block; barriers are terrain and water.

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
