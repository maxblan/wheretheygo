# Open gaps

Everything raised during review, from a run log or by the user that is **not** closed, as of
2026-09-05 (evening). Each entry was re-checked against the code in this commit rather than
recalled, and carries the evidence it rests on. Ordered by impact within each section.

Closed items are not listed. The previous edition of this file (2026-08-31) described code that no
longer exists — `ScanStopWindows`, `ChooseInWindow`, `AcceptBestCandidate`, `PlanCallingPoints`,
`MinEnabledDemandShareFor`, `MinRidersFor` are all gone with the redesign (stop plan DP, exact set
selection, capacity ladder) — so it was rewritten rather than amended.

---

## Behavioural — known to produce wrong or missing output

None open. Every finding from the 2026-09-05 runs is fixed in code:

| Finding (time, Valmare) | Fix | Verified how |
|---|---|---|
| Game frozen for the whole route pass (12:25) | pass on a worker task; door-free evaluation, sinks, parallel folds, memo, greedy → swap → exact B&B | in-game: 114 s → 25 s pass, 47 ms main thread per refresh |
| Bus chosen at 154 % utilisation (17:06) | mode re-weighed on its own riders after re-stopping; next mode up as a variant; set ceiling 100 % | harness `LineSetCeiling`, `ModeClimbsTheLadderByCapacity` |
| Three metros of 1.4–5.4 km, one on train track (19:44) | two rail lattices split by `TrackLaneData.m_TrackTypes`; a rail alignment stands only where its street re-trace is overloaded, impossible or fails the gates | **code only — see "Awaiting the next run"** |
| Suggestions frozen while the heat map was off; export never written | passes no longer gated on `active` | **code only** |
| Route list reset under the player's selection on every refresh | finished pass staged behind `routeUpdate` / `applyRouteUpdate` | **code only** |
| Tunnels and empty roads glowing yellow (screenshot) | access as a discount; tunnel/bridge nodes walkable but not sites; zoning counts only beside people | harness ×3, `heatmap-walk-plumbing` with a bridge node three-way exact, old real exports re-run green (174518Z, 064700Z) |

---

## Awaiting the next game run — implemented, tested where the harness reaches, never seen in the log

The build deployed at 323 072 bytes carries all of the following. Each names the log line that
proves it, so the next run is a checklist and not a hunt.

| Behaviour | Where | What the log must show |
|---|---|---|
| Metro on metro track only; rail only where streets fail | `CollectTrackSegments`, `ResolveCandidate`, `OfferOnStreets(requireFit: true)` | `streets overloaded as a … — the Metro alignment stands` / `no street path between its ends` for every rail suggestion that survives; a city with its lines removed no longer offers three metros |
| Passes with the heat map hidden | `OnUpdate` | `Heat map hidden` followed later by `Route pass finished` |
| Staged updates | `FinishRoutesIfReady` / `AdoptPass` | `New suggestions are staged; the panel offers to apply them`, the panel button, then `Route pass adopted` only after the click; the first list after load adopts directly |
| Tunnel/bridge nodes counted | `SuitabilityInputs.EndOnGround` | `Pedestrian network: N nodes, E edges with a pavement, K nodes in tunnels or on bridges (walkable, not sites)` with K > 0 where the city has them; the yellow over underground roads gone |
| Whether the game flags `Elevated` only at bridge height or already for a slightly raised street | same line | if K is implausibly large, `CompositionFlags.General.Elevated` fires for retaining walls too and `Side.Raised` should be the split instead |
| Calibration model stamp | `Calibration.Deserialize` | once: `Ridership samples discarded: gathered under scoring model 1`; fits report `(W4 is a discount and is not fitted)` |
| Save-state round trip at format v2 | `TransitArchitectSystem.SaveState` | `Restored N suggestions …` on load; no `Data size mismatch` |
| Route-pass time after the ladder change | `StartRoutePass` timing lines | last measured 25 s before the street-first re-trace, which adds one `RetraceOnRoad` + `SettleMode` per rail candidate |
| Day/night recommendation on real lines | `Daytime.Advise` | `HealthScheduleAdvice` rows only once a period has ≥ 4 readings |

---

## Decisions still with the user (register "RF")

| Register | Question | Today |
|---|---|---|
| A5.5 | Headway as a planning variable (choose the interval so utilisation hits a target) instead of the fixed table 300/240/200/480/600 s? | table; prefab intervals (45–90 s) are logged, not used |
| A8 | Should the set selection optimise the schedule itself (seats only in the operating period)? | schedule is a recommendation after selection |
| A1.16 | Drop the future term entirely (W5 = 0 default) now that zoning alone counts nothing? | gated, default weights unchanged |
| A1.6 v3 | Keep the per-mode W4 defaults (0.4–0.8) as discount strengths, or set 1.0 = "score × kernel" literally? | defaults unchanged; 1.0 is what option A said |
| A4.5 | Is the 4.6× preference for existing track (0.35 vs 1.6) wanted? | unchanged, now per track kind |

---

## Verification — implemented and believed correct, but not proven offline

| Behaviour | Where | How it is checked today |
|---|---|---|
| The combine formula (discount, zoning gate) | `SuitabilityScoring.Combine` | harness only (`CombineDiscountsByAccessAndGatesFuture`, `CalibrationFeaturesMatchCombine`); there is no export kind for the combine — `heatmap_walk` checks the seven terms feeding it, bit-exact |
| Which nodes are siteable | `SuitabilityInputs.EndOnGround` (ECS side) | by construction from the decompiled `NetCompositionHelpers`; the flag is exported (`node_siteable`) and consumed offline, its derivation is not |
| Real line-set instance 174518Z | `real-Valmare-20260905T174518Z-lineset` | **never run** — the bounded enumeration takes hours and the three earlier ones (155915Z, 164723Z, 173551Z) were stopped on request before finishing; the quick kinds of 174518Z all PASS (heatmap 2 069 tiles three-way, sites certified with gap 194 161/524 288 nodes, roads, coverage, stops three-way exact) |
| Lattice via-bending plumbing | `Routes.BendThroughInterchange` | the bound (`IsDetourWorthwhile`) is tested; sampling hubs along a `SuitabilityRoadGraph` is on the Unity side; `ViaReachMetres` (2000) unverified in game |
| `ScoreForMode` leaving the map unchanged | `TransitArchitectSystem` | read against `Combine`; both go through `CombineCell`, so they cannot disagree, but no test reaches the ECS method |
| Panel rows keyed by route identity across a staged apply | `TransitArchitect.mjs` | `node --check` only; behaviour by eye |

---

## Known modelling gaps — not bugs, stated limits

- **A street line may plan a stop inside a tunnel or on a bridge.** The stop plan works on the
  road graph (`SuitabilityRoadGraph`), which carries no elevation; only the pedestrian graph
  learnt `Siteable` today. The stop-plan candidates every 50 m along a road alignment can therefore
  sit on a tunnel segment. Fix shape: read the same composition flags in `CollectRoadSegments` and
  exclude those positions from `StopContext` candidates.
- **The ridership fit cannot identify W4.** At a served stop the access kernel is ≈ 1, so the
  discount is an intercept; W4 stays whatever the slider says. Recorded in register A1.14 v2.
- **Elevation is ignored in every alignment cost** (register A4.5): a train lattice climbs a
  cliff for free.
- **Citizens' ±1 h shift offset is not modelled** in the day/night shares (register A8).
- **Line health is not part of the optimisation**; verdict thresholds (A8.1–A8.3) are judgement.

---

## Structural — no wrong output, but the shape invites one

### 1. The overlay system is a partial class over 20 files

Split on 2026-09-05 by feature (`F<n>*/Systems/TransitArchitectSystem.F<n>*.cs`) with the
orchestration in `Overlay/`, the infoview presenter and the trip observer as classes of their own,
and the arithmetic moved into the `Planning/` folders (alignments, stop placement, served demand,
the combine pass, site candidates, the panel payload, the sanity checks). What is left in the
partials is gathering, scheduling and logging. Method bodies were moved, not rewritten, so the
`MA0051` ratchet figures are unchanged and every method that was long still is.

### 2. Sixteen pure test-side files, four harness files

`Program.cs` in `tests/TransitArchitect.Tests` is ~4 000 lines with no framework and no filter
flag; the newer tests sit in `AlignmentTests.cs`, `PipelineTests.cs` and `HeatmapTests.cs`. It still
runs in seconds, so this is a convenience gap, not a correctness one.

### 3. `mode-choice-sweep` fails in the pipeline, and did before the restructure

`verification/run.py` reports `[mode-choice-sweep] FAIL`: the evaluator expects `Metro` for the
rail rows, the subject answers `Train`. The run from 20:26 on 2026-09-05 — before any code moved —
carries the identical `solution.json`, so the instance's expectation is stale since the evening's
two-lattice split (`RouteNetwork.Rail` is trains only; metros have `RouteNetwork.Metro`), not the
code. The instance or `evaluator/modes.py` needs the new ladder; the mod side is unchanged.

---

## Reported as a gap, but is not one

**The bus credited with 78 % of Valmare's travel (2026-08-31).** The city is 2 km across and the
bus ran its length; the defect was in the check, now `ShortLineShareOfCity` of `LongestJourneyMetres()`.

**`route.Length` after `TrimPath` (2026-08-30).** `TrimPath` recomputes the length from the trimmed
polyline; `KeepsItsShape` re-verifies against the real ride time.

**Three metro lines needed (2026-09-05 19:44).** Not a demand error: the ladder started at Metro on
a rail lattice and the alignment won on speed. A rule error, fixed above; the demand numbers were right.
