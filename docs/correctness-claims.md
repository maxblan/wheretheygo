# Correctness claims and their verification status

Status vocabulary (exactly one per claim):

- **formal bewiesen** — proved in a proof assistant (Lean 4, pinned v4.15.0,
  mathlib-free; statements and proofs in `verification/lean/Verify/`; no `sorry`,
  axiom footprint limited to `propext`/`Quot.sound`/`Classical.choice` — checked
  via `#print axioms`).
- **zertifikat** — proved by an independently checked certificate (VIPR via
  `viprchk`, or the solver-free rational path certificate via
  `evaluator/checkcert_path.py`). Always per instance.
- **enumeration** — proved by complete enumeration of bounded instances in exact
  arithmetic; the bound is stated with the claim.
- **getestet** — established by tests / exact differential re-evaluation on the
  pipeline instances; counterexamples on other inputs not excluded.
- **widerlegt** — refuted; the counterexample instance is named.
- **offen** — not yet established either way.

Claims are numbered `C<stage>.<n>` against `docs/formal-specification.md`.
Reproduce every non-open status below with `make -C verification verify-all`
(exit 0; artifacts under `verification/runs/`). This table is the single authority
for what has and has not been shown; nothing else in the repo may claim more.

## S1 Heatmap evaluation

| # | Claim | Status | Evidence |
|---|---|---|---|
| C1.1a | The stop-derived terms T3/T6/T7 and the mode-terms combination match the specification | getestet (bit-exact against an independent binary32 re-implementation, incl. the kernel boundary cases: a stop at exactly the transfer radius contributes zero interchange, at exactly the catchment contributes nothing, beyond it nothing) | `heatmap-point` verdict |
| C1.1b | The gathered terms T1/T2/T4/T5 (population, jobs, access, future) match the specification | **getestet — on a real city.** Valmare, 448×448 = 200,704 cells: the mod exported the Burst job's own inputs and a sample of its outputs, and `evaluator/heatmap_grid.py` recomputed them from the specification in exact binary32. **2,073 of 2,073 sampled cells match bit-for-bit** on each of two exports a day apart (928 unbuildable cells covering the job's all-zero gate; the second export also carries the `land` mask, and labelling covers exactly the land). The evaluator is itself pinned by `heatmap-grid-plumbing`, whose expected terms are computed independently of its bucket machinery, and a one-ULP mutation fails the run | `real-Valmare-…-heatmap` verdict |
| C1.4 | Catchments never draw demand across water/cliffs (component gating) | **getestet — on a real city.** Part of the bit-exact recomputation above; on Valmare the gate rejected **1,400 sources** across the sampled cells, so a silently disabled or inverted gate could not have matched. The map has 8 distinct landmasses | `sources_rejected_by_component_gate` |
| C1.2 | `SelectKth` returns the k-th order statistic (with k-clamping) | **enumeration** — complete enumeration of every array of length 1–5 over {−1, 0, 1, 2} with every k (4,092 cases), plus clamping and adversarial float cases; all match the sorted reference | `order-stats` verdict |
| C1.3 | `PositivePercentile`: k = ⌊count·p⌋-th smallest of the positives; 0 when none | **enumeration** — every array of length 1–4 over {−1, 0, 1, 2} at the mod's three percentiles (1,020 cases, all-nonpositive families included) | `order-stats` verdict |
| C1.5 | Walk-distance re-scoring settles each tile exactly once | getestet (historical — the refinement pass was removed in Phase 3, A2.4; the pure function and its tests remain) | mod tests `WalkDistanceCountsOnce`/`IsDeterministic` |
| C1.6 | **v2** Edge walking times are `max(1, round-half-even(L / 1.2f · 1000))` ms and the access pass's shortest-path times are exact integers | **getestet** (harness: `WalkGraphMilliseconds`, `IntDijkstraExactBoundedReusable`; pipeline: `edge_ms_matches_spec` on `heatmap-walk-plumbing`) | `SuitabilityWalkAccess.cs`, `evaluator/heatmap_walk.py` |
| C1.7 | **v2** The seven walking-time terms at a tile equal the specification's (§7 v2) bit for bit — game export = subject (mod code offline) = independent evaluator | **getestet** on the synthetic instance (6/6 tiles three-way exact; a one-bit mutation is rejected). First real export (Valmare, 2026-09-04T23:16Z, 3 810 nodes, 922 homes): **1 788/2 073** tiles three-way exact under the original float spec — the 285 misses were the game's Mono keeping float intermediates at higher precision (access term, one weighted sum), while subject and evaluator agreed with each other. The arithmetic was then pinned to double with one rounding per store (§7 v2 rule); against the same old export that leaves 1 994/2 073 (the rest are sums the old build rounded differently). Exports with the pinned build (Valmare 2026-09-05T06:47Z and 08:33Z): **2 073/2 073 tiles three-way bit-exact** both times — **getestet on a real city** | `runs/real-Valmare-20260905T064700Z-heatmap/*` |
| C1.8 | **v2** Snapping: nearest node by double squared distance, ties to the lower index, off-network beyond 2 min; **since 2026-09-05 evening tiles and site candidates skip non-siteable (tunnel/bridge) nodes while sources do not** | **getestet** (harness `NearestNodeTiesAndReach`, `NearestSiteSkipsOffGroundNodes`; pipeline snap agreement on `heatmap-walk-plumbing` with a bridge node whose neighbouring tile must snap past it — game = subject = evaluator) | `WalkNodeIndex.NearestSite`, `SuitabilityWalkAccess.SnapSite` |
| C1.11 | **v3** The combine is `discount · Σ` with `discount = sat(1 − W4·(1 − T4))`; a node with no positive term scores 0 at any W4; zoning (T5) counts only where T1 > 0 or T2 > 0 | **getestet** (harness `CombineDiscountsByAccessAndGatesFuture`: pavement-only cell = 0, node/edge/half-way values, zoned-only = 0, zoned beside a workplace counts in full). No export kind: the combine has no offline check beyond the harness | `SuitabilityScoring.Combine` |
| C1.12 | **v2** The ridership fit's regressors satisfy `W1·f₀ + W2·f₁ + W5·f₂ = Combine(cell) − discount·ModeTerms` for every cell and weight vector (W4 included) | **getestet** (harness `CalibrationFeaturesMatchCombine`, 200 random cells, |Δ| ≤ 1e-5) | `SuitabilityScoring.CalibrationFeatures` |
| C1.13 | Which nodes are siteable follows the game's own tunnel/bridge classification (`NetCompositionData.m_Flags.m_General`) | **by construction** (`SuitabilityInputs.EndOnGround`, decompiled `NetCompositionHelpers`/`CompositionFlags`); **not yet log-verified** — the pedestrian-network log line reports the count of off-ground nodes for the next run | mod code |
| C1.9 | **v2** Kernel accumulation order is source-index order and independent of Dijkstra settle order | **formal-by-construction + getestet**: each source contributes at most once per node, so the float sum's order is fixed by the input (`WalkAccessAccumulatesKernel` pins the two-source sum) | `SuitabilityWalkAccess.Accumulate` |
| C1.10 | **v2** Catchment classes {6, 11, 16 min} are exactly the modes' horizons | **getestet** (`CatchmentClassesMatchModes`) | `TransitModes.CatchmentClassesMs` |

| C3.4 | **v2** Observed shopping/leisure journeys: the one-game-day window evicts by frame, restarts on a rewound clock, holds at most 200 000, and scales a short window to a day's rate by clamp(day/span, 1, 4) | **getestet** (harness `ObservedTripWindowHoldsADay`, `ObservedTripWindowRestartsAndCaps`). The live-city scan itself is ECS-side and verified by the log: first design (citizen `Target`) saw **0** journeys; second (citizen `TripNeeded`) saw 0 because the entry is dropped at departure (700 shopping travellers, none with an entry); the two-stage scan (queued inside the building / under way via the creature's building target) recorded **243 journeys in 1.4 game hours** on Valmare (shopping 28, leisure 27, sightseeing 188), scale ×4 capped. Car trips were under-read until the creature→vehicle→building hop was added; with it, a fresh session read every target of 473 travellers (0 unreadable) and held 309 journeys after 1.7 game hours; 417 journeys already under way at load have no known origin and are counted, not guessed | `SuitabilityObservedTrips.cs`, mod log 2026-09-05 09:27 and 10:07 |

| C3.5 | **v2** The served-walk field, the both-ends-served rule, the share and the weighted Gini equal the specification (§7c) exactly | **getestet three-way** on `coverage-line` (share 4/5, Gini 0.4: hand computation = mod code offline = independent evaluator); harness `EquityCoverage`, `EquityGiniAndUtilisation` (equal values → 0, one-of-n → (n−1)/n, weights ≡ repeats). **Real city (Valmare 2026-09-05T09:07Z): getestet three-way** — 18 017 journeys, 96 served stops, served share 4277/20627 ≈ 20.7 % (floor 80 %), Gini of access walk 0.3447, game = subject = evaluator on every figure | `runs/coverage-line/*`, `runs/real-Valmare-20260905T090739Z-coverage/*`. Export 2026-09-05 12:25 (three suggestions kept): game figure 5175/22439 vs exact 4748/22439 — the game had folded the SUGGESTED lines' stops into the served field (`ServeStops`) before exporting, while exporting the existing stops only; the subject agreed with the evaluator. Fixed the same day: the served field and the panel figure stay the measured (existing-stop) coverage, the set's own coverage is reported in the "Line set:" log line; re-export pending. Re-export 17:15 (three suggestions kept, served field measured-only): **three-way exact again** |
| C3.6 | **v2** While the served share is below the floor, candidates are ranked by journey weight newly served (lexicographically before enabled demand) | **getestet** by construction (`OrderCandidates`); observable in the log line "equity floor unmet: candidates ranked by journeys newly served first". Not offline-verifiable: the ranking runs over live candidates | — |
| C3.7 | **v2** A suggestion must reach the utilisation floor (peak boardings over peak seats, both directions) | **getestet** (`EquityGiniAndUtilisation`: 1000 journeys/day, 300 s headway, 70 seats → 23.8 %); the gate's decision is logged with headway and capacity. **Finding on Valmare and correction:** the first formula assumed an 86 400 s day and read every candidate ~20× too low (best 5.0 %); on the game's 4 369-second day the best candidate reaches 20.5 %, trams 5.4 %. Floor set to 15 % (user decision 2026-09-05). The game's own "usage" is instantaneous occupancy (median 4.1 % on Valmare's existing lines), a different quantity | `SuitabilityEquity.Utilisation`, mod log 2026-09-05 11:07 |

## S2 Site selection (`SuitabilityExactSites`, greedy baseline `FindTopSites`)

| # | Claim | Status | Evidence |
|---|---|---|---|
| C2.1 | Every returned site is feasible (positive 3×3 local max, pairwise Chebyshev ≥ m, ≤ K) | getestet (exact re-evaluation, all 4 sites instances) | `verdict.json`: `pass_feasible` |
| C2.2 | The returned list is the greedy outcome under (score desc, index asc) | getestet (exact, incl. the plateau tie instance; `Array.Sort` tie order caveat handled by dual-order check) | `pass_greedy_faithful` |
| C2.3 | The **greedy** ranking maximizes Σ score among feasible sets | **widerlegt (historical — the mod no longer ships the greedy as its answer since Phase 2, see C2.6), measured on a real city.** Valmare export 1 (716 candidate local maxima): certified optimum 174927443/16777216 ≈ 10.4265, greedy gap 2211231/16777216 ≈ 0.1318 — about 1.3 % left on the table. Export 2, a day later with a grown network (705 candidates): certified optimum 151894115/16777216 ≈ 9.0537, greedy gap **0** — so the greedy is sometimes exactly optimal and sometimes not, and only the certificate tells which. Synthetic: `sites-greedy-gap` gap 2 (optimum 10 vs greedy 8), `sites-random-24` gap ≈ 0.195. The mod never claimed optimality; the gap is now exactly known per instance | SCIP 10 exact + viprcomp + viprchk logs in `runs/` |
| C2.4 | Candidate-sweep truncation is surfaced, never silent | getestet | `truncated` out-param + warning path |
| C2.5 | On instances where greedy IS optimal, that is certified, not assumed | zertifikat (per instance: `sites-plateau`, `sites-random-16` — gap 0 with verified certificate) | `viprchk` "Successfully verified" |
| C2.6 | The mod's exact selection (`SuitabilityExactSites.Solve`, Phase 2) returns a feasible set whose Σ score equals the certified optimum whenever it reports `Optimal` | **zertifikat** on all 6 sites instances: 4 synthetic (incl. `sites-greedy-gap`, where greedy loses 2, and `sites-random-24`, where greedy loses 204705/1048576) and both Valmare exports (716 and 705 candidates; search closed after 14 and 0 nodes; `WeightsExact` true). `run.py` fails the instance if a closed search misses the optimum or a budget-stopped one reports a bracket that excludes it | `exact_gap` = 0, `pass_exact_optimal`, SCIP exact + viprcomp + viprchk |
| C2.7 | The exact selection's fallback is sound: when the node budget stops the search, value ≤ optimum ≤ reported ceiling and value ≥ greedy | **getestet** (harness: budget 1 on the counterexample yields incumbent 8 with ceiling 10 = true optimum; 40×40 dense field brackets the closed optimum). Not yet observed on a real export — every real instance closed | `ExactSitesBoundWhenExhausted`; `judge_exact_selection` in `run.py` |
| C2.9 | **v2** Network form: two candidates conflict iff the exact walking time between them is strictly below the spacing; chosen sets respect it and equal the brute-force optimum | **enumeration** (harness: 30 random graphs with Floyd–Warshall cross-check of the Dijkstra conflict lists; pipeline brute force ≤ 40 candidates) | `NetworkSitesMatchBruteForce`, `sites_walk.py` |
| C2.10 | **v2** The mod's network selection equals the certified optimum — `sites-walk-gap` (greedy 12, optimum 14, gap 0) **and the real city twice**: Valmare 2026-09-04T23:16Z (1 691 candidates, 5 438 conflicting pairs, optimum 108415371/8388608 ≈ 12.924) and 2026-09-05T06:47Z (1 692 candidates, 5 439 pairs, optimum 54219337/4194304 ≈ 12.927), K = 8, mod's exact selection gap **0** both times, search closed after 1 node | **zertifikat** (SCIP exact + viprcomp + viprchk, 97 derivations) | `runs/real-Valmare-20260904T231657Z-sites/*` |
| C2.11 | **v2** The ball clique-cover bound is sound (a budget-starved run's ceiling holds the true optimum) | **getestet** (`NetworkSitesBeatGreedy`, starved run) | — |
| C2.8 | Exact selection agrees with complete enumeration | **enumeration** — 40 random small fields with integer scores in the harness, plus the pipeline's brute force on every sites instance ≤ 40 candidates (`enumeration_agrees`) | `ExactSitesMatchBruteForce` |

## S4 Alignments

| C4.8 | **v2** Arc times and the turn table are exactly `max(1, round-half-even(L/v·1000))` and `round-half-even(r·θ_c·1000)` (§3.4) | **getestet** (harness `DirectedTurnClassesAndTimes`; pipeline `arc_ms_matches_spec`/`turn_ms_matches_spec` on `road-times-oneway`) | `SuitabilityDirectedRoads.cs`, `evaluator/roadtimes.py` |
| C4.9 | **v2** The mod's directed driving time between two points on the streets (stops mid-block, partial first and last arcs, all arc pairings) is the exact minimum over arc-states with turn costs | **zertifikat + formal checker**: `road-times-oneway` — 8 point legs three-way exact (hand computation = mod code = evaluator), a 1 ms mutation rejected; each leg's directed label certificate verified in Python and by the Lean-proved `Verify.DirPathCert.check_sound` (axioms propext, Quot.sound). Harness: 25 random graphs vs exhaustive enumeration, mid-block legs. **Real city (node-snapped form, export 2026-09-05T08:33Z):** 17 legs three-way exact, 17 certificates Python + Lean. The point-based form awaits the next export | `runs/road-times-oneway/*`, `runs/real-Valmare-20260905T083352Z-roads/*` |
| C4.10 | **v2** A one-way street is traversed only in its admitted direction; a return leg loops | **getestet** (`DirectedOneWayStreet`, `DirectedFlowAssignment`) | — |
| C4.11 | **v2** Turn costs can change the chosen route (fast-with-turn vs slow-and-straight) | **getestet** (`DirectedTurnCostsSteer`) | — |
| C4.12 | **v2** Directed flow assignment puts a pair's weight on the arcs of its fastest directed route and on their streets | **getestet** (`DirectedFlowAssignment`); the real-city assignment is not yet exported for replay | `SuitabilityDirectedRoads.AssignFlow` |

| # | Claim | Status | Evidence |
|---|---|---|---|
| C4.1 | A lattice alignment is a minimum-cost path under the lattice cost model | zertifikat (per instance: `path-rail`, `path-tie` — rational distance-label certificate checked by TWO independent checkers: `checkcert_path.py`, and the Lean executable whose checking logic is itself **formal bewiesen**, see CF.1) | `path-certificate.json`, `path-certificate-int.json`, verdicts `certificate_verified` + `lean_certificate_verified` |
| C4.2 | A grown road corridor is a simple connected path | getestet | mod tests `CorridorIsConnected` |
| C4.3 | Corridor growth (incl. PeelFlow, DecayNovelty, seeding, bridges) is a faithful implementation of its specified transition rule | getestet — bit-exact independent replay over 4 instances (trunk-following, low-demand bridge crossed + unredeemed tail discarded, max-length stop, coverage objective with unstoppable edges); corridors, block counters, and the FULL flow/novelty arrays after every round match bit-for-bit, plus structural invariants (simple path, adjacency, length bound). Semantics pinned along the way: `m_HitMaxLength` fires only when the length REACHES the limit; an extension that would overshoot counts as blocked-by-length (spec §3.2) | `corridor-*` verdicts |
| C4.4 | Accepted candidates satisfy directness/length/stoppability | offen (ECS-side composition; log-verified only) | OPEN-GAPS.md |
| C4.5 | Via-bent alignments satisfy the detour bound | getestet (bound only; selection plumbing is Unity-side) | mod tests |
| C4.6 | One-way streets / directions are respected | **getestet since Phase 5** (was widerlegt: every graph was undirected until 2026-09-05): road ride times, fleet estimates and the S3 assignment run on the directed arc graph (§3.4), certified per leg on Valmare (`road_times`, Lean `DirPathCert`). Still undirected by design: corridor GROWTH and the lattices | C4.8–C4.12; formal-specification §3.4 |
| C4.7 | Equal-cost path ties are detected and reported | getestet | `path-tie`: `shortest_path_count` = 2 reported |

## S5 Stop placement (pure parts)

| # | Claim | Status | Evidence |
|---|---|---|---|
| C5.1 | (v1, historical) `PlanCallingPoints` offsets strictly increasing, start 0, end pinned, even | getestet 2026-09-03; the function was removed with the v2 stop plan | Lean `Verify.CallingPoints` (statements about the v1 rule) |
| C5.2 | (v1, historical) Consecutive kept calls ≥ 0.6 × interval apart | superseded by C5.6 (gap floor σ/2 in the plan, verified offline) | — |
| C5.3 | A line passing ≤ 150 m from a served stop calls at it | **getestet offline since v2**: forced candidates are part of the exported plan and every plan the subject/evaluator judge must contain them (`stop-plan-forced`); the Unity-side detection of the 150 m runs remains log-verified | `stop_plan` |
| C5.4 | (v1, historical) Termini and must-call windows always kept; positive-median floor | getestet 2026-09-03; removed with v2 | — |
| C5.5 | All stops lie exactly on the polyline | offen (Unity-side: candidates are `PointAlong` samples of the path by construction) | — |
| C5.6 | **v2** The stop plan is the exact maximum of Σ w·max(0, H − t) − Σ through·δ over feasible candidate sets (termini and forced calls in, gap floor σ/2) | getestet — harness `StopPlanMatchesBruteForce` (12 seeded plans vs brute force), pipeline `stop-plan-boarders` / `-forced` / `-random`, **and on Valmare (export 17:15, `real-…-151508Z-stops`): the three kept lines' plans (23/27/21 candidates, 15 062/12 994/15 495 doors in reach) are three-way exact — game choice = subject DP = the evaluator's independent exact DP, no ties**; again for the five kept lines of export 18:47 (`…-164723Z-stops`) | `stop_plan` verdicts |
| C5.7 | **v2** A call is made exactly where the boarders' access gain exceeds the through-riders' delay | getestet (`StopPlanWeighsBoardersAgainstThrough`: 15 500 > 4000 called, 3100 < 4000 skipped, and the light door earns its call once the through-flow halves) | harness |
| C5.8 | **v2** δ per stop = prefab stop duration + v/2a + v/2b; ride limits and the 3-stop floor | getestet (`StopDelayAndRideLimits`; `mode_choice` rows compare δ bit-exactly) | harness, `mode_choice` |

## S6 Mode choice

| # | Claim | Status | Evidence |
|---|---|---|---|
| C6.1 | **v2** `ChooseMode` is the capacity ladder of §5 v2 (smallest mode not overloaded at the prefab headway; largest when all are; none without a vehicle) | getestet bit-exact (`mode-choice-sweep`: 9 rows incl. bus→tram at 1200 riders, metro→train at 30 000, table headway for a missing Ferry line prefab) and harness `ModeClimbsTheLadderByCapacity` | `mode_choice` |
| C6.2 | **v2** A suggestion has ≥ 3 stops and rides within its mode's limit, judged after the stops are placed with the same ride time the rider is quoted | getestet (`KeepsItsShape` via `PassesLineGates`; harness `StopDelayAndRideLimits`); the directed-leg ride time itself is C4.8 | mod tests |
| C6.3 | **v2** A train alignment runs on the train lattice and a metro alignment on the metro lattice (track kind by `TrackLaneData.m_TrackTypes`); a rail alignment is offered only where its street re-trace has no path, overloads every street mode (> 100 %) on its own riders, or fails the shape gates — otherwise the street variant IS the candidate | by construction (`ResolveCandidate`, `OfferOnStreets(requireFit: true)`, `CollectTrackSegments`); **not yet log-verified** — the 19:44 finding (three metros, one on train track) is the case the next game run must show gone | mod code |
| C6.4 | A finished route pass is staged until the panel applies it; a pass runs whether or not the heat map is drawn | by construction (`FinishRoutesIfReady`/`AdoptPass`, `OnUpdate` no longer gated on `active`); log lines "staged" / "adopted"; **not yet log-verified** | mod code |

## S7 Line-set selection (v2 since Phase 7) — and the v1 credit/greedy history

| # | Claim | Status | Evidence |
|---|---|---|---|
| C7.8 | The mod's chosen set is reproduced offline by its own pure code on the exported problem (`SuitabilityLineSet.Solve`) | getestet on the 5 synthetic instances (`subject_matches_game`); **offen on a real city** until the next export carries the v2 `-lineset` file | `lineset_time` verdicts |
| C7.9 | The mod's time saved lies within the Higham binary32 budget of the exact rational Σ w·(before−after) over zone-node routing | getestet (5/5; budget derived per pair from γ_L on before and after) | `time_saved_within_budget` |
| C7.10 | Every chosen line clears the utilisation floor and none is a duplicate, judged on the set's own rider attribution (F1/F2 of spec §6.3) | getestet — `lineset_time-floor` (the line saving most time is refused under a 10 % floor), `lineset_time-duplicate` (a slower parallel line carries nobody once the faster one is in the set and is refused), `lineset_time-random` (24 of 42 subsets infeasible) | `feasible`, `feasible_reason` |
| C7.15 | **v2** A line the set fills past its mode's seats (utilisation > 100 %) makes the set infeasible; the next mode up is offered as its own candidate | getestet — harness `LineSetCeiling`, pipeline `lineset_time-ceiling` (10 % ceiling refuses {L1, L2}, optimum L3 alone by complete enumeration). Motivated by the 17:06 run: a bus kept at 154 % because the mode was chosen on another variant's riders | `lineset_time` |
| C7.16 | **v2** A set holds at most one variant of an alignment (group); the search never builds two, feasibility rejects two | getestet — `lineset_time-variants` (two variants of one alignment, K = 2: optimum {L1, L3}, complete enumeration) | `lineset_time` |
| C7.17 | **v2** The door-free evaluation (multi-source search from the stops a door reaches) equals the door-node definition bit for bit | getestet — harness equivalence test against the door-node graph with sink doors; the five original `lineset_time` instances reproduce their earlier subject outputs | harness |
| C7.18 | **v2** Greedy build + swap local search reach a feasible set at least as good as the greedy build, and the exact search only improves on it | getestet — harness `LineSetLocalSearch` (feeder problem: swaps already hold the optimum); offline on Valmare export 17:59 (13 429 pairs, 68 candidates, K = 5): local search 441.7 k s·journeys/day in ≈ 3 s, unchanged by 10 s or 30 s of exact search (ceiling 630.6 k, not closed); the game's previous 60 s exact search alone had reached 422.0 k, the build before that 305.1 k | subject timing, `lineset_time` |
| C7.11 | When the search closes (`Optimal`), the chosen set's key (min(coverage, X), saved) equals the optimum over ALL feasible subsets of ≤ K lines | getestet by complete enumeration on 5/5 synthetic instances: `lineset_time-feeder` (3 candidates, K = 2 — the set {L1, L2} at ≈ 600 000 s·journeys beats the greedy order's {L3, L1} at ≈ 87 000, the trunk-and-feeder case v1 lost), `-floor`, `-duplicate`, `-tie`, `-random` (6 candidates, K = 3, 42 subsets, gap 0). **On a real city the enumeration will be bounded** (Valmare's pool exceeds `ENUM_BUDGET`): then only the k′-prefix optimum is exact and the verdict says so | `enumeration.json`, `pass_optimal`, `optimality_level` |
| C7.12 | Key ties are detected and reported, and the mod resolves them by candidate index | getestet (`lineset_time-tie`: two equal sets, `optimum_ties = 2`, mod takes [0]) | `tie_affected`, `optimum_ties` |
| C7.13 | The union bound is valid (monotonicity of saved and capped coverage in the set) | argued in spec §6.3 (a line can only shorten a journey or serve another door); **nicht formal bewiesen** — a Lean statement over the abstract Dijkstra would be the next hardening step | spec §6.3 |
| C7.14 | With the node budget exhausted the reported ceiling brackets the optimum | argued (max over open bounds); **not exercised** — every instance so far closes | `upper_bound_time_saved` |

v1 rows, kept because their refutations motivated v2 (the evaluator, instances and
export kind they cite were removed on 2026-09-05; `git log` has them):

| # | Claim | Status | Evidence |
|---|---|---|---|
| C7.1 | Crediting uses a shortest itinerary; value within the exact interval over all tied shortest itineraries | getestet 2026-09-03 (4 synthetic + Valmare: 11 lines, 76 stops, 1,394 flows, 10 candidates) | historical |
| C7.2 | Boardings = access edges/2; transfers = boardings − 1; discount^transfers applied | getestet 2026-09-03 | historical |
| C7.3 | Credit gates (3600 s ceiling, 60 s switch margin, baseline semantics) match the spec | getestet 2026-09-03 | historical |
| C7.4 | Greedy acceptance yields the maximum credited-sum set of ≤ K lines | **widerlegt** — `lineset-feeder`: greedy 40 vs optimum {L1, L2} ≈ 80.000002; Valmare third export (39 candidates, K = 5): greedy's first three [14, 15, 12] = 584.81 vs exact 3-line optimum [10, 14, 15] = 622.34, gap 6.4 %. **Resolved by v2**: the set is now chosen exactly (C7.11) | historical |
| C7.5 | Separate optimization of stops then lines can reach the joint optimum | **widerlegt** — `lineset-staged`: stop set from the S5 spacing rule worth 20, an alternative on the same alignment 80. Still open in v2: stop placement is unchanged until the S5 DP (plan Phase 7, next stage) | historical |
| C7.6 | Unmeasured zero demand never rejects a candidate (`DemandScored`) | getestet; the gate no longer exists in v2 | historical |
| C7.7 | Greedy acceptance order faithful; ties reported | getestet; superseded by C7.12 | historical |

## S8 Operating periods (day / night, 2026-09-05)

| # | Claim | Status | Evidence |
|---|---|---|---|
| C8.1 | Night is the game's 22:00–06:00 (`normalizedTime` < 0.25 or ≥ 11/12) and the Day/Night policies mean day-only/night-only | getestet against the decompiled `TransportLineSystem` (isNight) and `ScheduleSection`; harness `DaytimeRules` pins the boundaries incl. wrap-around | decompile, harness |
| C8.2 | A commute's rides are classed by shift: day shift both by day; evening/night shift one ride each side in a 9–17 city | getestet (`DaytimeRules`); the ±1 h per-citizen offset is documented as unmodelled | harness |
| C8.3 | Period utilisation = boardings / (day seats × period share); recommendation Day / Night / DayAndNight by the floor rule | getestet (`DaytimeRules`, 64.4 % / 5.1 % example) | harness |
| C8.4 | Period averages of an existing line use only that period's readings with vehicles out; advice only with ≥ 4 readings per period and never for a line that does not run in the other period | getestet (`PeriodAverages`, `DaytimeRules`) | harness |
| C8.5 | The clock stamped on observed journeys and readings is the game's `TimeSystem.normalizedTime` | **log-verified only** (Unity-side) | mod log |

## Formal bewiesen (Lean 4) — `verification/lean/`

These are machine-checked theorems about the *specified rules* (exact
arithmetic). Where a C# function implements the rule, the float32-vs-exact
correspondence is established separately by the pipeline's bit-exact
differential tests (the paired claim is referenced).

| # | Theorem | What it proves | Pairs with |
|---|---|---|---|
| CF.1 | `Verify.PathCert.check_sound` (+ `certificate_sound`, `walk_lower_bound`, `pathCost_walk`) | The executable certificate checker is SOUND: if it accepts, the certified path is a walk of exactly the claimed cost and **no walk from source to target is cheaper**. The pipeline runs this verified checker on every lattice-path certificate (integer-scaled); mutation-tested (wrong cost, lowered label, detached path — all rejected) | C4.1 |
| CF.2 (v1 rule, historical since 2026-09-05) | `Verify.CallingPoints.offset_first/_last/_even/_strict_mono/_bounded` | The calling-point plan L·i/n has offset 0 first, exactly L last, strictly increasing, evenly spaced intervals, never beyond the line (exact arithmetic over the numerators) | C5.1 (bit-exact float pairing) |
| CF.3 (v1 rule, historical) | `Verify.CallingPoints.keep_first/_last/_mustCall` | Termini and must-call windows are always kept by the keep rule | C5.4 |
| CF.4 (v1 rule, historical) | `Verify.CallingPoints.keep_all_when_all_zero` (+ `positiveMedian_of_nonpos`) | The degenerate case: with no positive score the positive-median floor collapses to 0 and an all-zero line keeps every window — the class of bug that shipped as "the whole map turns red" cannot recur in this rule | C5.4, C1.3 |
| CF.5 | `Verify.Boardings.boardings_eq_legs`, `transfers_eq` | Boardings = (access edges)/2 = vehicles used; transfers = boardings − 1 — the counting rule `Inspect` relies on | C7.2 |
| CF.6 | `Verify.Boardings.accessCost_full_per_leg` | With the access edge priced at HALF the boarding cost, every vehicle used pays the FULL cost exactly once — the invariant whose violation was the free-transfer bug | C7.2/C7.3 |
| CF.7 | `Verify.Scaling.check_scaled_sound` (+ `walk_scale`, `walk_unscale`, `mul_le_cancel`) | The ℚ→ℤ scaling is sound: for any k > 0, acceptance of the k-scaled certificate by the verified checker proves the ORIGINAL instance's path is a minimum-cost walk of exactly the original claimed cost. Rationals over the common denominator L are exactly their integer numerators (same-denominator addition/order = integer addition/order); this theorem formalizes why checking the L-scaled instance decides the rational one | C4.1 |

Unverified glue around CF.1/CF.7 (documented, deliberately small): the JSON
parsing in `Main.lean`, and Python's `Fraction` arithmetic extracting the
integer numerators (exact integer arithmetic by construction). The former
trust in the *mathematical* scaling step is discharged by CF.7. The Lean
checker uses the first matching edge per path hop (the Python one the
cheapest): on instances with parallel edges it may reject a certificate the
Python checker accepts — sound, possibly incomplete; the generators emit no
parallel edges.

## Cross-cutting

| # | Claim | Status | Evidence |
|---|---|---|---|
| CX.1 | The pure half is deterministic given its inputs | getestet — with the S2/S7 unstable-sort tie caveats documented in formal-specification §8 | pure-math rule + pipeline |
| CX.2 | Instances are canonical and tamper-evident (sorted keys, binary32 bits, SHA-256) | getestet (mutation test: a flipped bit is rejected; a wrong expectation fails the run) | run.py hash check |
| CX.3 | Reference evaluator and mod agree (exactly for S1/S5/S6; within stated budgets for S7 sums and S4 distances) | getestet on all pipeline instances. The budgets are now DERIVED, not chosen: Higham's forward bound γ_k = k·u/(1−k·u), u = 2⁻²⁴, applied per journey as γ_L·(before + after) for the v2 time-saved sum (L = the node count, an upper bound on a path's edges; v1 used γ_{m+1}·Σ credits for CreditLine's m-term sum) and γ_{L−1}·cost for an L-edge path (`evaluator/tolerances.py`, with citation). An empirical self-test of 20,000 random float32 summations stays inside the bound with a worst observed error/budget of 0.955 — sharp, not loose. Rounding only: a float32 Dijkstra retaining a near-shortest itinerary is a decision flip and is reported, not absorbed | `python3 evaluator/tolerances.py`; verdicts |
| CX.4 | ECS-half data gathering (game → arrays) is faithful | **not verifiable offline** — remains the trust boundary, but a narrower one than before: the exporter captures its inputs INSIDE `StartCompute`, from the very values handed to the Burst job, so what verification sees is the job's own input rather than a second collection that could have drifted. What is still trusted is the ECS gather itself (ECS components → those arrays) | verification-architecture.md; `SuitabilityVerificationExport.cs` |
| CX.5 | The exported wire format is exactly what the pipeline can load (canonical form + digest) | getestet — golden-vector test in the offline harness pins the C# writer against `canonical.py`'s own encoding and SHA-256, including sort order, escaping and the body-digest rule | mod tests `Export JSON …` (4 cases) |

## What the strongest levels mean here

The mod claims no global optimality (README). The certified artifacts therefore
prove, per instance: feasibility, faithfulness to the mod's own procedures, and the
**exact distance to the certified optimum** of the user-approved reference
objectives — including certified *zero* gaps where greedy happens to be optimal
(`sites-plateau`, `sites-random-16`) and certified/enumerated positive gaps where
it is not (`sites-greedy-gap`, `sites-random-24`; historically `lineset-feeder`).
Since Phases 2 and 7 the mod's site and line-set selections are themselves exact,
so for those the pipeline requires gap 0 against the certified or enumerated
optimum whenever the mod reports `Optimal`.
