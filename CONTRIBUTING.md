# Contributing

Thanks for looking. This file covers the things about this repository that will
waste your afternoon if you find them out by trial.

## You cannot build this without the game

`dotnet/WhereTheyGo.csproj` imports `Mod.props` and `Mod.targets` from
`CSII_TOOLPATH` and references `Game.dll` and the `Colossal.*` and `Unity.*`
assemblies from an installed copy of Cities: Skylines II. None of that is
redistributable, so there is no way to build the mod half on a machine without
the game and the official modding toolchain installed. CI does not try.

What you can build and run anywhere is `tests/WhereTheyGo.Tests`. It has no
package references and no test framework, so `dotnet run --project
tests/WhereTheyGo.Tests` works on a bare .NET 9 SDK. The exit code is the number
of failures.

## Building also installs

`Mod.targets` copies the build straight into
`%CSII_USERDATAPATH%\Mods\WhereTheyGo`. There is no separate install step, and
the running game holds a lock on the deployed DLL.

**Close the game before you build.** If you build while it runs, MSBuild removes
the Mods folder before it writes, fails on the locked file, and leaves you with
no mod installed. `make deploy` waits for the handles to clear and refuses
rather than racing. To check that something compiles while the game is open, use
`make compile`, which runs the compiler without the deploy.

`ModPostProcessor` also returns a misleading exit code when it runs just after
the game closes, so confirm a deploy by comparing file sizes (`make status`), not
by trusting the build's exit code.

## The gates

```bash
make verify    # check-ui, check-links, test, build, status
make strict    # locked restore, warnings as errors everywhere, format, tests
```

Run `make verify` before you open a pull request. `make` on its own lists every
target.

A green test run is not enough on its own when your change is in the
game-facing half. That half cannot be executed outside the game, so it is
verified by reading
`%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Logs\WhereTheyGo.Mod.log`
after a run. Say in the pull request which parts you verified that way and which
you did not.

## Where code goes

Every folder holds one concept, split three ways:

| | |
|---|---|
| `Planning/` | pure arithmetic, `System.*` only |
| `Gathering/` | reads the game's ECS data |
| `Systems/` | the ECS systems and the partials of `WhereTheyGoSystem` |

**No Unity, ECS, Colossal or Game type may appear in a `Planning/` folder**, not
even in a signature. Not `float2`, not `Entity`, not `math.*`. The test project
links every `Planning/` file by glob, so one of those types breaks the whole test
build. `float2Like` is the map-plane vector, and every operation on it mirrors the
formula `Unity.Mathematics` itself uses, so a value computed on the pure side
matches the game bit for bit. Convert at the seam, in the readers and the system
partials.

Keep arithmetic out of the readers and the system partials. If a behaviour cannot
be reached from the test harness, it is on the wrong side of that line.

**Every number the mod computes with lives in
`dotnet/Common/Planning/Assumptions.cs`.** Look there before writing a literal
anywhere else.

## Check the game, do not guess it

Never take a game component to mean what its name suggests. Several have turned
out to mean the opposite, and when that happens this mod puts a plausible wrong
number on the map instead of throwing, so nothing tells you.

Establish what a game API actually does before you write against it, then record
what you established in a comment beside the code that depends on it, naming the
game system and the date you checked. Where the game already computes the
quantity you want, mirror its formula rather than deriving your own.

## Things that move together

Some changes have to touch several files at once. These two are the design, not a
smell:

- A new panel control moves through `WhereTheyGoSystem.Panel.cs`,
  `PanelUISystem.cs`, the `.mjs`, the `.css` and **all six locale files**.
- A new overlay layer moves through `OverlayLayer`, `OverlayLayers`, the infomode
  registration in `Infoview.cs`, its renderer, and the legend labels in all six
  locale files.

`make check-links` catches the half of this that is matched by string: locale key
parity, every `t("Key")` having a `Panel[Key]`, and binding and trigger names
agreeing in both directions. Those three fail as a blank row or a dead control in
the running game rather than as a build error, which is why there is a checker.

## Tests

Write the test first when the behaviour is new. When you are changing behaviour
nothing covers, pin the current behaviour in a test first, even if it looks
wrong, and say so rather than fixing both at once.

The harness has no framework and no filter flag. To run one test, comment out the
other `Run(...)` calls at the top of `Program.cs`. Test bodies are split by
feature beside it: `BandTests.cs`, `CommonTests.cs`, `PipelineTests.cs`,
`WalkNetworkTests.cs`, `SaveStateTests.cs`.

Cover the degenerate cases. No stops, one node, every score zero, an empty city.
Several shipped bugs were exactly those: a percentile over a set with no positive
member once turned the whole map red.

**Do not weaken, skip or delete a test to make a change pass.** A failure there
means behaviour changed, and the interesting question is why.

## The save format

`Overlay/Planning/SavePayload.cs` owns the layout. The payload is a list of
sections, each behind its own id, version and byte length, and a reader skips a
section it does not know while keeping the ones it does.

Bump the **section's** version when its bytes change. `SaveFormatVersion` is the
framing, and moving it throws away every existing block, which costs every player
three game days of observed journeys and a game day of line readings that only
game time can gather again.

## Pull requests

Say what observable behaviour changes and what stays identical. Keep behaviour
changes and restructuring in separate commits, and say which one a commit is.

Commit messages here are a descriptive sentence, then the problem the change
solves, then a `Geprüft:` line listing the commands you ran and what is still
unverified. Existing history is in German; English is fine too.

## Scope

The mod shows what is there. It does not suggest lines, place stops, choose
modes, set vehicle counts, write timetables, price tickets, or change anything
about the city. A feature that recommends an action is out of scope by design,
however useful it would be, so please open an issue and make the case before
building one.
