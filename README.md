# Where They Go

A Cities: Skylines II mod that shows **where the people of your city want to go, and how
much of that your transit network already carries.** You build; it only ever tells you
what is there.

Cities: Skylines II knows every home, every workplace, every school and every errand
its citizens make, and shows you a utilisation percentage per line. This reads the
journeys out of your save and draws them.

## What it is

One infoview in the game's own infoview menu, and one section in the window of a line
you click. No window of its own, no toolbar button, no column in the transport overview,
and no button anywhere in it that changes your city.

### Desire lines

Every journey the mod knows — home to work and home to school out of the save, shopping
and leisure from watching the city run — drawn as a band between its two ends.

- **Width** is how many journeys. On the square root of the count, so the city's biggest
  corridor does not eat the map and a tenth of the traffic is still visible beside it.
- **Colour** is how much of it your network already carries. Warm means those people are
  in their cars; cool means they are already on your metro. That is the mod's whole
  answer in one glance.
- **Bundling.** Journeys are summed per 256 m zone pair, and zone pairs whose two ends
  lie close together fold into one band, heaviest corridor first. A slider in the panel
  hides the thin ones.
- **Time of day.** Pick an hour, or press play and watch the day go by: bands swell and
  fade, and travelling dots show which way the traffic runs in that hour. Over a whole
  day there is no direction to show — every journey is made twice — which is why the dots
  appear only once you pick an hour.
- **Purposes.** Work, school, shopping and leisure switch on and off separately.
- **Point at a band** and the panel names its numbers: journeys on it, how many of them
  travel without transit, and its busiest hour.

### Walk to transit

Every building coloured by the walk from its door to the nearest stop a line actually
calls at. Real walking time over the pedestrian network, not a circle on the map. The
building's own window shows the number the colour came from.

### Two figures

- How much of the city's travel the network carries.
- How much of it has **both** ends within walking distance of a served stop.

Both say what they are made of, and the walking horizon is yours to set.

### What a line does

Click a line and its window gains a reading — not a verdict:

- how many journeys ride it, and how many passenger-minutes a day it saves them against
  walking and the rest of your network;
- how many of those journeys would be **no slower without it**, which is the answer to
  "why is my line empty": it runs beside something that already carries them;
- how full it runs hour by hour, from its own readings;
- and the bands it carries light up on the map.

No advice on fleets, modes or timetables follows. That is the game.

## What it does not do

It does not suggest lines, place stops, choose modes, set vehicle counts, write
timetables, price tickets, or change anything at all about your city. Where another
widely used mod already does a job well, this one stays out of it: it hangs only on
vanilla surfaces that no large mod replaces — the infoview menu, the infoview panel, and
the window of the object you clicked.

## How it works

**The demand is real, not modelled.** Citizens' households and workplaces are entity
references in the save, so home-to-work and home-to-school journeys are read, not
estimated. Shopping and leisure journeys are watched once a second from the live city
and kept for three game days, with the hour they happened at. There is no gravity model
and no calibration.

**Where a model is needed, the game's own is mirrored and said so.** "Does the network
carry this journey" needs routing over your lines, so the mod builds a transit graph out
of them — stops, boarding points, rides, and walking links between stops close enough to
interchange — and routes every journey door to door over it, with walk, wait and ride
weighed the same and a change costing its walk and its wait, because that is how the
game's own pathfinder routes citizens. A journey counts as carried when transit is
**faster than walking the whole way** and stays under a ceiling drawn from your city's
own median carried journey. Mods that change pathfinding costs (Realistic PathFinding and
the like) are not modelled.

**A line's worth is measured by taking it away.** The two routed figures in a line's
window come from routing the whole city a second time without that line. Nothing is
attributed from inside a single search, because that is the kind of arithmetic that hides
its own mistakes.

Everything heavy runs on a worker thread; the numbers that reach the panel also reach the
log with the values they came from.

## Requirements

- Cities: Skylines II with the official modding toolchain installed (sets
  `CSII_TOOLPATH` / `CSII_USERDATAPATH`)
- .NET SDK

## Build

```powershell
./build.ps1            # Release
./build.ps1 -Configuration Debug
```

or `dotnet build dotnet/WhereTheyGo.csproj -c Release`. There is a `Makefile` wrapping the
awkward parts — run `make` for the targets. The build deploys into the game's Mods folder,
and the running game locks the deployed DLL, so close it first.

## Tests

```bash
dotnet run --project tests/WhereTheyGo.Tests
```

No packages to restore; the exit code is the number of failures. Every `Planning/` folder
is free of Unity types so it can be run outside the game, and the harness links them all
by glob: the bundling, the geometry, the routing, the walking-time search, the window
arithmetic and every numeric constant.

## License

See [LICENSE](LICENSE).
