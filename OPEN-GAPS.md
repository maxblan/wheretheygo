# Open gaps

Everything raised during review or from a run log that is **not** fixed, as of 2026-08-31.
Each entry was re-checked against the code in this commit rather than recalled, and carries the
evidence it rests on. Ordered by impact within each section.

Closed items are not listed. Two items previously reported as gaps turned out not to be, or to be
something else; they are recorded at the bottom so they do not get chased again.

---

## Behavioural — these produce wrong or missing output

None open. The last one, "a hub off the alignment does not bend the route", is closed: lattice
alignments are now re-traced through an interchange they pass near, bounded by
`SuitabilityGraphMath.IsDetourWorthwhile`. See the Verification section for what is and is not
proven about it, and for the one part deliberately not done.

---

## Structural — no wrong output, but the shape invites one

### 1. `StationSuitabilityOverlaySystem.cs` remains a god class

~4400 lines, and every change lands in it. Already documented in `CLAUDE.md`; listed here so the
backlog is in one place. The pipeline seams (`UpdateTravelDemand` -> `BuildTransitModel` ->
`BuildRoutes` -> `SelectRoutes`) are clean if it is ever split.

Deliberately left. `CLAUDE.md` states the position directly: splitting these methods "is a refactor
rather than a fix and must not be done blind", which is what the `MA0051` ratchet is holding the
line for. Doing it in the same change as eleven behavioural fixes would make every one of them
unreviewable.

---

## Verification — implemented and believed correct, but nothing proves it

The offline harness links only the six Unity-free files. Anything touching `float2`, ECS or the game
cannot be reached from it, so the items below are verified by reading the code and by the mod log
after a run — never by a test. This is the repo's stated position for the game-facing half; it is
listed so the distinction is not lost.

| Behaviour | Where | How it is checked today |
|---|---|---|
| Minimum gap between consecutive calls (`MinStopGapShare`) | `SuitabilityRoutes.ScanStopWindows` | Log + screenshot |
| Calling at a station the line passes (`StationCallMetres`) | `SuitabilityRoutes.ChooseInWindow` | Log + screenshot |
| The demand floor rejecting a suggestion that improves nothing | `AcceptBestCandidate` | `improvedTooLittle=` in the log |
| Panel rows keyed by route identity | `StationSuitabilityOverlay.mjs` | `node --check` only; behaviour by eye |
| `ScoreForMode` leaving the heatmap itself unchanged | `StationSuitabilityOverlaySystem` | Read term-by-term against the code it replaced; **not** re-run in game |
| A city with no transit at all producing any suggestion | `BuildTransitModel` -> `ScoreCandidates` | `transfer scoring` lines in the log; **fixed 2026-08-31, not yet re-run in game** |
| Metro and ferry candidates existing at all | `BuildDirectForNetwork` per-network budget | `metro pairs tried=` / `ferry pairs tried=` in the log; **fixed 2026-08-31, not yet re-run in game** |
| A road corridor's termini aimed at a hub | `SuitabilityRoutes.AimEndsAtInterchange` | `termini aimed at an interchange=… (road N)` in the log; **new 2026-08-31, never run in game** |
| `ShorelineScoreAt` mode correction | `StationSuitabilityOverlaySystem` | Read against `ScoreForMode`; unobservable until a ferry candidate survives |
| Bending an alignment through a hub it passes | `SuitabilityRoutes.BendThroughInterchange` | `alignments bent through an interchange` in the log; the bound is tested, the graph plumbing is not |

The even-interval rule behind the spacing fix was deliberately moved into `SuitabilityScoring`
(`PlanCallingPoints`) so at least the headline arithmetic is testable. The same trick would work for
the minimum-gap rule if it earns a regression.

**Via-point routing, and what is not covered.** Only the LATTICES bend. A road corridor's shape is
a measurement — grown along real street flow, then peeled — and re-tracing its middle would throw
that measurement away to buy a transfer; its ends are aimed at a hub instead
(`AimEndsAtInterchange`), and its stops already call at any station within 150 m of where it runs.
That is a decision, not an omission, and it is the reason this gap is closed rather than partly
closed.

What is tested is the bound: `IsDetourWorthwhile` lives in the pure math and has its own case,
including the degenerate ones. What is NOT tested is the selection around it — sampling the
alignment for hubs, snapping to a lattice node, and rejecting a via whose two legs overlap — because
that needs a `SuitabilityRoadGraph`, which is on the Unity side of the boundary. Two constants there
are unverified in game: `ViaReachMetres` (2000) and `ViaSampleStride` (4). The reach is deliberately
generous because the detour bound scales with the line and it cannot; if bent alignments start
looking eccentric, tighten `MaxViaDetour` before touching the reach.

**A caution on the mode floors.** The absolute floor that used to live here,
`TransitModes.MinCityTravelForReach` (20,000 weighted journeys), **no longer exists** —
the verification pass of 2026-09-03 found this paragraph describing code that had been
replaced. What gates a rail mode today is a pair of rules in `TransitMode.cs`: a reach
SHARE (`MinEnabledDemandShareFor`: train 4 %, metro 2 % of the unserved travel weight,
halved when the alignment mostly follows existing track) and a rider floor
(`MinRidersFor`: the journeys needed to fill one vehicle at the peak, derived from the
loaded prefabs' capacities rather than typed in). Neither is calibrated against a large
city, so the original caution stands in spirit: if suggestions in a big city look timid,
these two are the first constants to check. The gate cascade itself is verified bit-exact
against an independent re-implementation (`docs/correctness-claims.md`, C6.1).

**The stop-less city, in detail.** `BuildTransitModel` returned early on `m_TransitStops.Count == 0`,
leaving `m_TransitNetwork` and `m_BaselineSeconds` null — the two fields `ScoreCandidates` guards on.
The transfer-scoring pass therefore never ran in a city with no lines, every candidate kept its
initial `EnabledDemand = 0`, and the reach gate dropped all of them. Observed in Valmare on
2026-08-31: `stops=0`, 1346 trips and 97 assigned zone pairs, corridor flows of 250-500, `kept=0`,
and not one `transfer scoring` line in 557 log lines. The early return is gone.

Only half of this is testable. `EmptyNetworkCreditsTheFirstLine` pins the contract the fix leans on —
an empty network is routable, zones remap onto a candidate's own stops, and the line is credited the
whole journey weight because nothing carried it before. The early return itself is in the ECS half
and no test can reach it. **What a run must show:** a `Transit model: lines=0, stops=0, graphNodes=0`
line, which has never appeared before, followed by `transfer scoring` lines carrying a non-zero
`routablePairs`. A `routablePairs=0` there means the 512 m zone reach is not catching the corridor's
zones, which is a different defect from this one.

---

## Reported as a gap, but is not one

**The bus credited with 78% of Valmare's travel.** Reported by the mod's own sanity check on
2026-08-31 as "the zone-to-stop remap is attaching journeys it does not serve". It was not: the city
is about 2 km across and the 1524 m bus ran the length of both villages, so it genuinely did serve
most of the travel. The defect was in the CHECK, which compared the line against a fixed 2 km rather
than against the city's own scale — now `ShortLineShareOfCity` of `LongestJourneyMetres()`. The
582 m stub the rule was written for is still caught.

**`route.Length` after `TrimPath`.** Reported as stale on 2026-08-30. It is not: `TrimPath` recomputes
`route.Length` from the trimmed polyline, with a comment saying why (`SuitabilityRoutes.cs:877-885`),
and that code predates the report. `KeepsItsFloor` therefore does re-verify against the real length.
No action needed.
