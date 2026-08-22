# Station Suitability Overlay

A Cities: Skylines II mod that adds a vanilla-style infoview scoring every 32 m tile for transit station placement, based on multi-criteria decision analysis (MCDA).

## Features

- **Station Suitability infoview** in the game's infoview menu, with a standard gradient legend (green → yellow → red); the configurable top share of tiles (default 5%) reaches the top of the gradient
- **Catchment-based scoring** combining four criteria, each normalized to a comparable 0–1 scale before weighting:
  - Demand — residents within the catchment (weight W1)
  - Jobs — actual workplace capacity within the catchment, from companies and city service buildings (weight W2)
  - Existing coverage — stops of the selected mode penalize nearby tiles (weight W3)
  - Accessibility — road network density within the access radius (weight W4); tiles with no road access are suppressed entirely, so hotspots stay on placeable ground
- **Bus / Metro presets** with sensible default weights and radii (350 m / 600 m catchment), all adjustable in Options → Station Suitability Overlay: four weights, catchment radius (150–1000 m), road access radius (50–300 m), and highlight share (1–20% of the built-up tiles)
- **Auto-recalculation** (debounced, off the main thread) when stops are placed or removed or settings change, plus a periodic refresh every 10 s so new roads, zones and residents show up on their own; changing only weights or the highlight share re-blends instantly without recomputing
- **No surprise activation** — the game's automatic "related infoview" selection for build-menu assets is stripped of this mod's infoview, so the overlay only ever appears when picked from the infoview menu

## How it works

The mod computes scores over the playable area in a parallel Burst job (with spatial bucketing for stops and road geometry), then feeds them through the game's own heatmap-infomode pipeline: intensities go into the terrain overlay channel assigned to the active infomode, and the vanilla terrain shader colors them with the infomode's gradient — the same mechanism as ground pollution or land value, so the overlay looks and behaves like a built-in infoview.

## Requirements

- Cities: Skylines II with the official modding toolchain installed (sets `CSII_TOOLPATH` / `CSII_USERDATAPATH`)
- .NET SDK

## Build

```powershell
./build.ps1            # Release
./build.ps1 -Configuration Debug
```

or `dotnet build dotnet/StationSuitabilityOverlay.csproj -c Release`. The build deploys to `%CSII_USERDATAPATH%\Mods\StationSuitabilityOverlay` automatically.

## Usage

1. Load a city and open the infoview menu (the ⓘ button), then select **Station Suitability**.
2. The brightest red tiles are the best locations for a new stop.
3. Tune mode preset, weights and radii under Options → Station Suitability Overlay; the overlay recalculates automatically.

## License

See [LICENSE](LICENSE).
