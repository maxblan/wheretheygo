# Station Suitability Overlay

A Cities: Skylines II mod that renders a terrain heatmap scoring every 64 m tile for transit station placement, based on multi-criteria decision analysis (MCDA).

## Features

- **Suitability heatmap** on a 64 m grid, green → yellow → red, with the top 5% of tiles highlighted at full opacity
- **400 m catchment scoring** combining four criteria:
  - Demand — residents within the catchment (weight W1)
  - Jobs — workplaces within the catchment (weight W2)
  - Existing coverage — stops of the selected mode penalize nearby tiles (weight W3)
  - Accessibility — road network density within 120 m (weight W4)
- **Bus / Metro presets** with sensible default weights; all weights adjustable live from the in-game panel or the mod options
- **Auto-recalculation** (debounced) when stops are placed or removed, plus a manual Recalculate button

## Project layout

- `dotnet/` — C# mod: ECS systems that compute the score grid in a Unity job and feed it to the terrain overlay renderer, plus settings and localization
- `ui/` — React/TypeScript UI module: the in-game control panel (built with cohtml-safe custom controls)

## Requirements

- Cities: Skylines II with the official modding toolchain installed (sets `CSII_TOOLPATH` / `CSII_USERDATAPATH`)
- .NET SDK and Node.js ≥ 18

## Build

One-step build and deploy into your local `Mods` folder:

```powershell
./build.ps1            # Release
./build.ps1 -Configuration Debug
```

Or manually:

1. C# mod: `dotnet build dotnet/StationSuitabilityOverlay.csproj -c Release`
2. UI: `cd ui && npm install && npm run build`

Both steps deploy to `%CSII_USERDATAPATH%\Mods\StationSuitabilityOverlay`.

## Usage

1. Load a city and open the **Station Suitability** button in the top-right corner.
2. Enable the overlay, pick the Bus or Metro preset, and adjust the weights.
3. Look for the brightest (fully opaque) tiles — those are the top 5% best locations for a new stop.

Default weights can also be tuned under Options → Station Suitability Overlay.

## License

See [LICENSE](LICENSE).
