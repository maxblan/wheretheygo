# Station Suitability Overlay

A Cities: Skylines II mod that adds a vanilla-style infoview scoring every 32 m tile for transit station placement, based on multi-criteria decision analysis (MCDA).

## Features

- **Station Suitability infoview**, with a standard gradient legend. The mod presents itself through its own panel rather than a row of infoview toggles, so the combined score is the one layer offered. The per-term layers (demand, jobs, existing coverage, accessibility, future demand, interchange potential, cross-mode overlap, travel demand) are still defined and one line away in `SuitabilityLayers.All`; the terrain overlay has four channels, so up to four could be shown at once.
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

The mod computes the score terms as walking times over the pedestrian network — an integer-time Dijkstra from every home building, workplace, zoned cell and served stop, on a worker thread — then feeds intensities through the game's own heatmap-infomode pipeline: each active layer writes into the terrain overlay channel assigned to its infomode, and the vanilla terrain shader colors it with that infomode's gradient — the same mechanism as ground pollution or land value, so the overlay looks and behaves like a built-in infoview.

### Distance model

Two different distance models are used deliberately, and it is worth knowing which is which:

- **The heatmap** uses straight-line distance, gated by landmass. A tile only accumulates demand from sources on the same connected stretch of walkable terrain, which removes the "draws population from across the river" error cheaply enough to run on every one of ~200,000 tiles.
- **The recommended sites** are then re-scored with a true walking-distance expansion (Dijkstra over the walkable terrain), which is only affordable because there is a handful of them. This is why a site's reported score differs from the raw heatmap value under it.

Neither model accounts for pedestrians being unable to cross a road mid-block; barriers are terrain and water.

### How the modes interact

The modes are not scored in isolation. Every *other* mode's served stops feed two terms drawn from the same set of stops and separated purely by distance:

- **Within transfer distance** (250 m, or the catchment if that is shorter) another mode's stop makes the location *more* valuable, because a rider can change vehicles there and the new stop feeds an existing trunk line.
- **Beyond that, out to the catchment**, the same stop counts *against* the location, because it already carries some of the same riders without offering a transfer.

Both are weighted by how much capacity the other mode represents (train and metro count for far more than another bus) and expressed relative to the mode being placed — and "the mode being placed" means the suggested line's own mode, not whichever mode the panel happens to be showing. The heatmap you see is built for the panel's mode; a suggested tram's stops are scored as a tram, so an existing tram stop counts against them as duplicate service rather than for them as an interchange. Only the three stop-derived terms depend on the mode, so this costs a scan of the served stops at each candidate position rather than a second pass over the whole map. That asymmetry is deliberate: a bus gains a great deal from sitting at a metro station, while a metro gains comparatively little from sitting at a bus stop — the smaller mode should come to the trunk, not the other way around.

Only stops that a line actually calls at count for either term. That also means no line-identity check is needed for a transfer: two served stops of different modes necessarily run different lines, so a genuine transfer is always possible.

What this does *not* model is parallel duplication along a corridor — a bus route running alongside a metro rather than feeding it. Detecting that needs line geometry rather than stop positions.

### Travel demand and route suggestions

The mod also answers the other half of the question: not just where a stop belongs, but where people are trying to go.

**The demand is real, not modelled.** Cities: Skylines II stores each citizen's household and workplace or school as entity references, so the mod reads actual home-to-work and home-to-school journeys straight out of the save. There is no gravity model or estimation. Tourists, the homeless and outside commuters are excluded (they have no fixed home in the city). School trips count at a lower weight than commutes.

Journeys are then aggregated into coarse zones, discounted by how well your existing network already serves both of their endpoints, and loaded onto the road network by shortest path. The **Travel demand** layer shows the result as desire lines: where movement wants to happen, weighted towards what you are *not* already carrying.

**Journeys are routed over your actual network.** The mod builds a transit graph from your existing lines — stops, per-line boarding points, rides between consecutive stops, and walking links between stops close enough to interchange — and routes every journey over it. A journey counts as already served only if the network can genuinely carry it in reasonable time, not merely because both its ends happen to sit near a stop.

That same graph decides what a set of suggestions is worth. Every journey is routed door-to-door over your existing lines plus the candidate set, and the set's value is the passenger time it saves against the best of walking and what you already run. This is what lets a short feeder rank properly: a line whose own corridor carries almost nobody can still be the leg that unlocks hundreds of journeys onto a trunk service, and it is credited for exactly the seconds those journeys save. Walk, wait and ride weigh the same and a change of vehicle costs exactly its walk and its wait, because that is how the game's own pathfinder routes citizens; the realism-weighted figure (TCQSM) is shown beside it as a diagnostic.

**Line health** reports on what you have already built. For each existing line the mod reads its fleet, how full its vehicles are, the headway it actually achieves, and the game's own "needs more vehicles" flags, then gives a verdict with a concrete remedy: a larger fleet (quoted as the interval that produces it, since that is what the game lets you set), an upgrade to the next mode up, a shorter route, or a reroute for a line that runs nearly empty. Problem lines are listed worst-first in the panel, and every verdict shows the numbers it came from.

Three details matter for reading it:

- **The fleet target is the game's own.** `TransportLineSystem` sizes a line's fleet as `round(roundTrip / targetInterval)`, where the round trip includes the dwell at every stop and the target interval is the prefab default plus the line's own modifier. The mod computes exactly that, so its target agrees with the fleet the game is already maintaining.
- **Waiting is measured from the headway**, as half the achieved `m_VehicleInterval`. It is deliberately *not* read from `WaitingPassengers.m_AverageWaitingTime`: that field is an accumulator the game keeps for its pathfinder, and a single stranded rider drives it into the thousands.
- **"Nearly empty" is relative.** Usage is an instantaneous count of riders against total fleet capacity, and healthy lines sit low on that measure, so the threshold is a fraction of the city's own median rather than a fixed share.

Only modes the mod can actually plan are judged — bus, tram, metro, train and ferry. Air, ship and taxi lines are passenger transport too, but there is no alignment to suggest for them.

**Route suggestions** come from that loaded network, and the two halves of the city are searched differently because their networks are different things. On the streets, where flow is a real measurement, the strongest corridor is grown outward along the heaviest remaining traffic. Metro, train and ferry alignments have no street to follow — they are searched over a free-form lattice where every direction costs the same, and on a uniform grid the "busiest corridor" is an artifact of how the shortest-path search broke its ties, not a fact about the city. Those modes therefore take the heaviest journey the network still cannot carry and trace directly between its two ends.

Either way, the stops are then **planned, not spaced**. Every 50 m along the alignment is a candidate; every journey door within the mode's walking horizon is a potential boarder, and the corridor flow at a candidate is who would sit through a stop there. A stop is made exactly where the walking time it saves the boarders outweighs the delay it costs everyone riding through, with that delay taken from the game's own prefabs: the line's stop duration plus the time lost braking and accelerating back to cruise speed. Consecutive stops keep at least half the mode's nominal spacing apart, the termini and every interchange within 150 m are always called at, and the plan is the exact optimum of that trade-off, found by dynamic programming. A stretch with nobody to board simply gets no call, which is what a real metro does under a park. The mode is then the smallest vehicle the network can carry whose seats the line's riders do not overload at the game's default interval, and a line must have at least three stops and stay within its mode's ride limit: 30 minutes for a bus or metro, 35 for a tram, 45 for a ferry, 60 for a train.

**Where a line passes an existing station, it calls there.** Within 150 m the stop is moved onto the station rather than placed beside it, because a stop a short walk from a station serves the same people twice and connects nothing.

**Lines are aimed at interchanges.** A terminus is where every rider must either finish their journey or change vehicle, so it is the most valuable point on a line to put within walking distance of another mode. Before a metro, train or ferry alignment is traced, each of its two ends is moved onto the nearest place offering a change to a *different* mode — bounded by the transfer walking distance, so the district the line was drawn for is still served from the moved end. Where several modes meet, the bigger interchange wins over a nearer lone stop. Where a line passes an interchange further along, the alignment bent through that hub is offered as a second candidate beside the direct one, and the set selection keeps whichever saves riders more time: a detour to a pier that nobody rides to loses to the straight line on its own numbers rather than on a length ratio.

What counts as one place matters here. A hub in Cities: Skylines II is several stop entities a few metres apart — the train platform, the metro entrance below it, the bus stand out front — so the modes on offer are read as the union over every stop within walking distance of each other, not from a single stop's own mode. Reading one stop on its own would call the city's biggest interchange a train station and nothing more.

The *value* of reaching a hub was already modelled: the set objective routes every journey over the whole network, transfers included. What was missing was any reason for the search to propose such a line in the first place.

Note that for route planning, *any* served stop counts as somewhere to change — its own mode included. A suggestion is never the line that is already there, so a new tram calling at an existing tram station lets riders change between tram lines. The heatmap's interchange term excludes the same mode only because, scoring a bare tile, it cannot know which line would run there. Mode still decides the ranking, so a place where three modes meet beats a lone stop; it no longer decides whether the place counts at all.

The suggestions are then chosen **as a set**, not one at a time. A branch-and-bound search over subsets of the candidate pool maximises the passenger time saved, after first satisfying the equity floor as far as it can be reached, and either proves its set optimal or reports the best it found together with a ceiling when its node budget runs out. Two rules make a set admissible: every line must reach the utilisation floor on the riders the whole set gives it, so a feeder's riders count for the trunk it feeds; and no line may be a duplicate, meaning at least half of its riders would travel no slower without it. A second metro shadowing the first therefore never appears — with the first in the set it carries nobody. Suggestions are drawn as coloured polylines with stop markers while the infoview is open.

**The mod remembers.** Its suggestions, the shopping and leisure journeys it has observed and the line readings it has collected are written into the save, so a loaded city shows its lines at once, keeps a day's worth of demand and judges its lines from the readings it already had, instead of starting cold. A save made with the mod loads fine without it; the game skips the block.

The **Route objective** setting changes what a line is grown for: maximum ridership follows the busiest journeys and may leave outlying districts unserved; maximum coverage spreads out to reach more districts even where demand is thin; balanced does both.

Known limits, since these matter when reading the output:

- Ferries are offered every journey, coastal ones included. A boat wins only where it saves riders time against walking and the existing network and still fills its seats, so a shoreline crawl loses on its own numbers rather than on a landmass rule.
- Selection is greedy. Transit network design is NP-hard; each round takes the best candidate given what has already been accepted and never revisits an earlier choice. These are suggestions, not optimal networks.

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

The scoring math (percentile normalization, site selection, corridor growth, transit routing, weight fitting and the line-health thresholds) is free of Unity types so it can be tested directly:

```bash
dotnet run --project tests/SuitabilityScoring.Tests
```

No packages to restore; a non-zero exit code counts the failures. This project is intentionally not part of the solution so the mod toolchain build is unaffected. It links the twelve Unity-free files — scoring, walking-time access, exact site selection, observed-journey window, graph math, directed roads, the equity measure, transit routing, the line-reading window, line health, the per-mode tables and the verification export's JSON format — so the thresholds behind a verdict are testable too.

## Usage

1. Load a city and open the infoview menu (the ⓘ button), then select **Station Suitability**.
2. The brightest tiles are the best locations for a new stop. The ranked recommended sites, with the walk-distance score behind each, are written to the mod log.
3. Tune the mode, objective and radii in the mod's own panel, or under Options → Station Suitability Overlay for the full set including weights; the overlay recalculates automatically.

## License

See [LICENSE](LICENSE).
