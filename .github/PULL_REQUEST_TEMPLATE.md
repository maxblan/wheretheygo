## What changes

<!-- What observable behaviour is different afterwards, and what stays identical. -->

## Why

<!-- The problem in this repository's own terms. If you cannot state it without
     naming a pattern, it may not be a problem yet. -->

## How it was verified

<!-- Which of these you ran, and what they said. Delete what does not apply. -->

- [ ] `make verify` (check-ui, check-links, test, build, status)
- [ ] `make strict` (locked restore, warnings as errors, format, tests)
- [ ] New tests that fail without this change
- [ ] Read `WhereTheyGo.Mod.log` after a run, and it said:

<!-- The game-facing half cannot be executed outside the game. If your change is
     in it, say plainly which parts the log confirmed and which are still
     unverified. "Compiles" is not "works". -->

**Still unverified:**

## Checklist

- [ ] Numbers I added live in `Common/Planning/Assumptions.cs`
- [ ] Nothing in a `Planning/` folder references Unity, ECS, Colossal or Game types
- [ ] Any game API I wrote against was verified against what the game actually does, and a comment names the system it came from
- [ ] If this adds a panel control or an overlay layer, all six locale files moved with it
- [ ] If the save layout changed, the **section** version went up and `SaveFormatVersion` did not
- [ ] No test was weakened, skipped or deleted to make this pass
