# Station Suitability Overlay

A Cities: Skylines II mod that adds a vanilla-style infoview scoring every 64 m tile for transit station placement, based on multi-criteria decision analysis (MCDA).

## Features

- **Station Suitability infoview** in the game's infoview menu, with a standard gradient legend (green → yellow → red); the top 5% of tiles snap to the top of the gradient
- **400 m catchment scoring** combining four criteria:
  - Demand — residents within the catchment (weight W1)
  - Jobs — workplaces within the catchment (weight W2)
  - Existing coverage — stops of the selected mode penalize nearby tiles (weight W3)
  - Accessibility — road network density within 120 m (weight W4)
- **Bus / Metro presets** with sensible default weights, adjustable in Options → Station Suitability Overlay
- **Auto-recalculation** (debounced) when stops are placed or removed or weights change

## How it works

The mod computes scores over the playable area in a parallel Unity job (with spatial bucketing for stops and road geometry), then feeds them through the game's own heatmap-infomode pipeline: intensities go into the terrain overlay channel assigned to the active infomode, and the vanilla terrain shader colors them with the infomode's gradient — the same mechanism as ground pollution or land value, so the overlay looks and behaves like a built-in infoview.

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
3. Tune mode preset and weights under Options → Station Suitability Overlay; the overlay recalculates automatically.

## License

See [LICENSE](LICENSE).
