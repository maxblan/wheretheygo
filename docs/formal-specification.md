# Formal specification of the problems the mod actually computes

Status: complete against the code as of commit `b96e9bd` (working tree, 2026-09-03).
Line references cite `StationSuitabilityOverlaySystem.cs` unless another file is named.

This document specifies, in exact terms, the computational problems solved by
`StationSuitabilityOverlay`. It is written for verification: every set, parameter and
objective below is stated so that an independent implementation can reproduce it in
exact rational arithmetic. Where the mod leaves an objective implicit (it computes a
*procedure*, not a declared optimum), that is stated explicitly, and the candidate
reference objectives are listed rather than invented silently.

## 0. Number representation and discretization

**Ground rule.** Every numeric input the mod consumes is an IEEE-754 `float`
(binary32) or an `int`. Every finite binary32 value is exactly representable as a
rational number p/q with q a power of two. The canonical instance export therefore
carries every float as an exact decimal string of its binary32 value (round-trip
`R`-format), and the reference model interprets it as the exact rational it denotes.
**There is no discretization error on inputs.**

Discretization/divergence arises only from *evaluation order*:

- The mod evaluates in binary32/binary64 with rounding at every operation; the
  reference evaluator computes the same expression tree in exact ℚ.
- Comparisons (`>`, `>=`) against accumulated floats may therefore differ from the
  exact comparison in a band of width ≤ the accumulated rounding error. The
  verification pipeline treats a mod value V_f and reference value V_q as *matching*
  when |V_f − V_q| ≤ ε_op · n_ops · scale, with the bound derived per evaluator
  (documented in `verification/README.md`), and reports any comparison whose
  *decision* (accept/reject, ranking order) flips inside that band as
  DECISION-SENSITIVE rather than silently passing it.
- `Math.Sqrt`, `Math.Pow` (used in: diagonal step √2·tileSize, transfer discount
  `discount^transfers`, distance computations) are correctly-rounded (sqrt) or
  faithful (pow) per .NET; the reference model uses exact squared-distance comparisons
  wherever the mod only compares distances, and interval arithmetic (or exact algebraic
  handling of √2) elsewhere.

**Units.** Distances: metres (float). Times: seconds (float). Demand weights:
dimensionless journey weights (commute = 1.0, school = 0.6; worker takes precedence
over student; tourists, homeless and households without `PropertyRenter` excluded;
self-trips < 1 m rejected). Grid resolutions: heatmap tile 32 m; spatial buckets
128 m; demand zone 256 m; lattice pitch 128 m. World extent = population-map extent,
centred on the origin (`worldMin = −mapSize/2`).

## 1. The pipeline, as stages with their own specifications

The mod computes, in order (per recompute):

1. **S1 Heatmap**: per-tile suitability scores (7 terms, percentile-normalized,
   weighted sum). Pure evaluation — no optimization.
2. **S2 Site selection** (`SuitabilityScoring.FindTopSites`): discrete recommended
   sites from the score grid. Greedy heuristic; no declared objective.
3. **S3 Travel demand**: origin–destination journey weights aggregated to 256 m zones,
   discounted by existing service, assigned to network edges by shortest path. Pure
   evaluation.
4. **S4 Corridor/alignment search**: `GrowCorridor` (roads) / `TracePath` direct
   alignments (lattices). Heuristic growth; Dijkstra shortest paths.
5. **S5 Stop placement along a line** (`PlanCallingPoints`, `ScanStopWindows`,
   `SelectCallingPoints`, `ChooseInWindow`): deterministic procedure.
6. **S6 Mode choice** (`TransitModes.ChooseMode`): deterministic gate cascade.
7. **S7 Candidate scoring and greedy acceptance** (`CreditLine`,
   `AcceptBestCandidate` rounds): greedy set selection; no declared joint objective.

Stages S1, S3, S5, S6 are *functions* — verification target: evaluator correctness.
Stages S2, S4, S7 are *search/selection* — verification targets: feasibility of the
output, plus exact optimality gap against a declared reference objective.

## 2. S2 — Site selection (`FindTopSites`)

**v2 (Phase 3, 2026-09-05) — network form (`sites_walk`, `SolveOnNetwork`).** The
mod's live path. Instance: the pedestrian graph of §7 (v2), candidate nodes
c₁..c_n with scores s_k ∈ ℚ (binary32; in the mod: every node whose own tile is
buildable and whose combined score for the map's mode is positive — the candidate
*definition* is instance data, not checked here), a spacing σ ∈ ℕ ms
(`WalkMilliseconds(StopSpacingFor(mode))`, ≥ 1), budget K. Two candidates conflict
iff the exact integer walking time between them is **strictly below** σ (equal is
allowed). A solution is a set of ≤ K pairwise non-conflicting candidates; the
objective is max Σ s_k. Search as in the exact grid form below, with the clique cover
replaced by walking-time balls of radius ⌊(σ−1)/2⌋ around centres taken in (score
desc, index asc) order and in node-index order (triangle inequality ⟹ a ball is a
clique). Result ranked by (score desc, node asc); the mod maps each node to the tile
under it for display. The grid form below remains as specified for the synthetic
instances and as the v1 record.

### Instance (grid form, v1)

- Integers W, H ≥ 1; score grid s ∈ ℚ^(W·H) (from binary32), cell index
  i = x + y·W.
- Separation m ∈ ℕ (`minSeparation`, in tiles, Chebyshev), site budget K ∈ ℕ
  (`maxSites`).

### Candidate set (part of the mod's problem definition)

C = { i : s_i > 0 ∧ LocalMax(i) } where LocalMax(i) holds iff for every 8-neighbour
j inside the grid: s_j < s_i, or s_j = s_i ∧ j > i (strict tie-break on linear
index — exactly one cell of a plateau qualifies). Candidate collection is capped at
min(W·H, 65536) with row-major sweep; overflow sets `truncated` (reported, biased to
low indices).

### Feasibility

A solution is an ordered list S = (i₁, …, i_r), r ≤ K, i_k ∈ C, such that for all
k < l: Chebyshev(i_k, i_l) ≥ m (the code rejects `< separation`).

### What the mod computes

**v2 (Phase 2, 2026-09-05) — exact.** `SuitabilityExactSites.Solve` returns a feasible
S maximizing Σ_{i∈S} s_i (objective (a) below), or, if a node budget of 2·10⁶ is
exhausted first, the best feasible S found together with a proven upper bound
U ≥ max Σ; the result carries `Optimal ∈ {true,false}`. Definition of the search:

- Total candidate order: score descending, ties by cell index ascending.
- Integer objective: each score is multiplied by 2^σ, σ chosen so the largest
  candidate lands in [2^(b−1), 2^b) with b = 62 − ⌈log₂ K⌉, and floored. A binary32
  value scaled by a power of two is exact in double, so the floor is a no-op unless a
  candidate is ~2^33 below the largest (`WeightsExact = false` then; the bound is
  slackened by K units in rational terms).
- Branch-and-bound, depth-first, include branch first, incumbent = the greedy
  solution under the total order.
- Bound at a node with remaining sorted list R and r free slots: partition the grid
  into m×m blocks (m = max(1, separation)); two candidates in one block have
  Chebyshev distance < m, so any feasible set has ≤ 1 per block. Bound_P(R, r) = the
  sum of the heaviest remaining candidate of the first r distinct blocks met in R.
  Two partitions are used (aligned; shifted by ⌊m/2⌋ in both axes) and the minimum
  taken. Prune when value + bound ≤ incumbent.
- On budget exhaustion: U = max(incumbent, max over unexpanded positions of
  value + bound). The bound is monotone non-increasing along a sorted list, so the
  first unexpanded position of each open frame suffices.
- Output S is ranked by (score desc, index asc).

The greedy ranking (`FindTopSites`) is retained as the incumbent and as the baseline
the pipeline measures: sort C by score descending (**tie order between equal scores
is implementation-defined** — `Array.Sort` is unstable introsort; deterministic per
runtime version but unspecified), then accept best-first subject to separation.

### Declared reference objectives (choice = open question Q1)

- **(a) Max-sum**: maximize Σ_{i∈S} s_i over feasible S, |S| ≤ K. Integer program:
  binary x_i per candidate, pairwise conflict constraints (or clique constraints per
  m×m window). The literature analogue is maximum-weight independent set on a king-graph
  power; greedy gives no constant-factor guarantee in general.
- **(b) Greedy-consistency only**: verify the output *is* the greedy outcome under a
  total order (score desc, tie by index asc) — a determinism/faithfulness check, no
  optimality claim.

The mod's UI presents a *ranked list*, which argues (b) is the faithful reading;
(a) measures how much score-mass the ranking leaves on the table.

**Resolved (user decision, 2026-09-03): the certified reference objective is (a),
max-sum over the mod's own candidate set; greedy faithfulness (b) is checked in
every run regardless.**

## 3. S4 — Alignments

### 3.1 Lattice direct alignment (`TracePath` on `SuitabilityRoadGraph`)

Instance: undirected graph G = (V, E), edge costs c_e ∈ ℚ_{>0}. Lattice costs:
128 (orthogonal) or 128·1.41421356 (diagonal — a hard-coded 7-digit constant, not
√2) times a per-edge scale `max(0.05, tileCostScale(midpoint tile))`; rail scales:
train 0.35 on existing track / 1.6 off, metro 0.9 / 1.0, water unscaled. Road edge
cost: `max(1, Curve.m_Length)` (arc length). Endpoints u, v; cost cap L (road
corridor 12000 m, train 20000, metro 15000, ferry 20000; flow assignment caps:
road 20000, lattices 30000 cost units).

Claim to verify: the returned node path is a **minimum-cost u–v path** in G with cost
≤ L, under the mod's cost model. Certificate: distance labels d: V → ℚ with d(u) = 0,
d(b) ≤ d(a) + c_{ab} for every half-edge, and tightness along the returned path;
checked in exact arithmetic. Ties: Dijkstra's returned path among equal-cost paths
depends on heap order and edge insertion order — the verifier checks *a* shortest
path was returned, not a specific one.

Note: lattice edge costs are *preference-scaled*, so "shortest" means cheapest under
that scale, not geometrically shortest. `NodePathLength`/detour bounds use geometric
length separately.

**Directionality.** Every graph in the mod is undirected (`CompactGraph`). One-way
streets, turn restrictions and junction costs are **not modelled**: a road edge is
kept iff any sub-lane carries `Road | PublicTransportDay` path methods, and is then
traversable both ways. The mod nowhere claims otherwise; this is a model limitation
recorded as such (see correctness-claims C4.6), not an implementation bug. Highways
(`UseHighwayRules`) are routable but excluded from hosting stops/corridors
(`EdgeCannotHostStops`).

### 3.2 Road corridor growth (`GrowCorridor`)

Deterministic greedy growth; full transition rule (seed selection, extension scoring
flow + novelty, TurnPenalty 0.6, SpreadPenalty 0.7, low-demand bridge ≤ 2 nodes with
0.05 penalty, discard-on-exit rules, tie-break lower edge index) is specified by the
code and reproduced in the reference evaluator for *faithfulness* checking. There is
no declared objective for a corridor; no optimality claim exists or is checkable.
Derived quantities with exact definitions:

- Length = Σ edge costs of committed edges (bridges discarded at the end are refunded).
- CapturedFlow = (Σ_e flow_e · cost_e) / Length (length-weighted mean).
- Feasibility invariants (verifiable): edges form a simple path (node sequence has no
  repeats), every committed edge has flow ≥ flowFloor at the time it was taken
  (history-dependent — checked by replay), no edge with `EdgeCannotHostStops`,
  Length ≤ maxLength, `IsDirectEnough`: endToEnd/Length ≥ 0.45 for accepted candidates.
- `m_HitMaxLength` semantics (pinned by the replay checker): the flag is true iff the
  accumulated length REACHES maxLength (the `while (length < maxLength)` condition);
  an extension that would merely overshoot the limit is rejected and counted in
  `blocks.m_Length` instead — the two are different stopping reasons by design.

### 3.3 Via-bending (`BendThroughInterchange`, lattices only)

Feasibility invariants: bent length ≤ 1.25 × direct length (`MaxViaDetour`), ≤
maxRouteLength, legs share no node but the via, directness ≥ 0.45, hub sampled every
4th node within 2000 m reach, via node within 250 m of the hub.

## 4. S5 — Stop placement along a fixed alignment

Given polyline P with total length T (metres, ℚ), mode spacing σ
(`TransitModes.StopSpacingFor`), the mod computes:

1. intervals n = max(1, round(T/σ, away-from-zero)); calling offsets
   o_i = T·i/n for i < n, o_n = T (`PlanCallingPoints`). Property: offsets strictly
   increasing, first = 0, last = T, count = min(n+1, buffer).
2. Per window: nudge search within ±0.35·step clamped to [0, T] and to
   ≥ previous + 0.6·step (`MinStopGapShare`); 7 samples (step/6) per window; termini
   pinned when their pinned score > 0; a sampled position within 150 m of an existing
   served stop forces `mustCall` and snaps to the nearest such sample.
3. `SelectCallingPoints`: keep window i iff i ∈ {0, count−1} ∨ mustCall_i ∨
   score_i ≥ 0.35 · median⁺(scores), where median⁺ is the k = ⌊count/2⌋-th smallest
   of the strictly positive scores (0 if none).
4. Stops closer than 20 m to an already-added stop are dropped; path trimmed to
   [first kept offset, last kept offset]; Length recomputed from the trimmed polyline.

All deterministic given the score oracle; verification target: independent
re-evaluation + invariant checking (gap ≥ 0.6·step between consecutive kept calls
except across skipped windows — note the invariant the code actually maintains is on
*chosen positions of consecutive kept windows*, verified as such).

## 5. S6 — Mode choice (`ChooseMode`)

For network-permitted modes in capacity order: mode M is chosen iff
(flow ≥ refFlow · MinFlowMultiple(M) ∨ (reachBar(M) > 0 ∧ enabledShare ≥ reachBar(M)))
∧ (¬demandScored ∨ enabledDemand ≥ MinRiders(M, capacity)) ∧ length ≥ MinLength(M),
first match wins; reachBar halved when trackShare ≥ 0.6. Constant tables in
`TransitMode.cs`. enabledShare = EnabledDemand / m_UnservedTravelWeight (the
post-discount city-wide sum). trackShare is the fraction of *path vertices* whose
32 m tile has the track mask set (vertex- not length-weighted — a documented bias).
MinRiders(M, cap) = cap / (0.2 · 2) for non-bus modes; vehicle capacity is read from
the largest loaded prefab per mode, minimum consist count. Deterministic gate
cascade — verification target: re-evaluation. (Note: `MinCityTravelForReach = 20000`
mentioned in `OPEN-GAPS.md` no longer exists in the code; the reach gate is the share
above.) On rejection, a non-road candidate is re-traced on the road graph between its
two end stops (`RetraceOnRoad`, snap 600 m, path cap 30000 m), inheriting
EnabledDemand/DemandScored unchanged.

## 6. S7 — Transit routing, candidate credit, greedy acceptance

### 6.1 Transit graph (`SuitabilityTransit.Build`)

Nodes: stop nodes 0..S−1, then one line-stop node per (line, position). Undirected
edges with cost floor 0.01 s:

- Walk: between stop pairs within walkRadius (Euclidean), cost = distance / 1.2 m/s
  (v2, 2026-09-04; v1 used 1.4).
- Access: stop ↔ line-stop, cost = (expectedWait + boardPenalty)/2 with
  boardPenalty = 5 s; halved because an undirected edge is traversed on and off, so
  one line use pays the full cost once.
- Ride: consecutive line-stops, cost = pathfound seconds (fallback: Euclidean
  distance / max(1, speed)).

expectedWait = max(0, max(interval/2, observedAvgWait) − stopDwell) (mirrors vanilla).

### 6.2 Journey evaluation (`CreditLine` / `Inspect`)

Per OD pair (grouped by origin; one Dijkstra per origin with cap maxTravelTime): the
shortest itinerary's boardings b = (#Access edges on path)/2, transfers = max(0, b−1).
Pair is *served* if reachable and travelTime + accessSeconds ≤ maxTravelTime. Pair is
*credited* to the target line iff additionally: the itinerary uses a target-line
access edge, b ≥ 1, and doorToDoor < baseline − switchMargin. Credit =
w · transferDiscount^transfers. **Tie sensitivity**: with equal-cost itineraries,
`usesTarget` and b depend on which shortest path Dijkstra retains. **Resolved (user
decision, 2026-09-03): interval semantics** — the mod's value passes iff it lies in
the [min, max] credit over all shortest itineraries; instances with
decision-relevant ties are reported.

### 6.3 Greedy acceptance (`SelectRoutes` / `AcceptBestCandidate`)

Candidate pool: per network, up to 4 × RouteCount candidates (roads grown with
peeling CaptureFraction 0.85 and novelty decay 0.15 over 3 hops; lattices direct,
one per heaviest unserved zone pair, deduplicated within 256 m of an already-taken
pair). Pool sorted by CapturedFlow descending (unstable sort — tie order
unspecified).

For round t = 1..RouteCount:

1. `ScoreCandidates`: only the first `RouteCount × 16` candidates of the pool are
   ever scored ("scoring window"); the rest keep EnabledDemand = 0 with
   DemandScored = false. Baseline travel times are re-measured against
   N_{t−1} = existing lines + accepted suggestions (fresh Dijkstra per origin,
   cap 3600 s). Each windowed candidate is scored by `CreditLine` on
   N_{t−1} + candidate with per-mode assumed wait (Bus 200 s, Metro 150, Tram 180,
   Train 300, Ferry 400) and cruise speed; discount = 1 − TransferPenalty/100
   (default 0.6); switch margin 60 s; zone→stop remap radius 500 m.
2. `AcceptBestCandidate`: iterate unsettled candidates ordered by EnabledDemand
   desc, ties by CapturedFlow desc (unstable sort). For each: resolve mode
   (§5 cascade; failed non-road candidates re-trace as road); then the gates, in
   order: (G1) `KeepsItsFloor` — post-trim length ≥ mode minimum and ≥ 2 stops;
   (G2) evidence gate — EnabledDemand > 0 ∨ corridorFlow ≥ 0.25 × network
   reference (reference = mean positive edge flow of the candidate's network,
   floored at 0.25 × road reference); (G3) corridorFlow > 1; (G4) demand floor —
   ¬(DemandScored ∧ EnabledDemand < RidersToFillOne(bus capacity))
   ("improvedTooLittle"); (G5) ¬DuplicatesExisting (≥ 75 % of ≥ 3 stops within
   150 m of one existing line's stops). First survivor is accepted, its stops and
   an assumed TransitLine are added to the network, and the round ends.

Output: ordered list of ≤ RouteCount suggestions ("next best line given the ones
above it").

### Declared reference objectives (choice = open question Q3)

For a *set* A of candidate lines (|A| ≤ K) the natural joint objectives differ:

- **(a) Union served weight**: total journey weight the network N₀ ∪ A can carry
  (served pairs, no per-line attribution). Monotone; a coverage-type objective.
- **(b) Sum of switch-credited weight**: Σ over pairs of w · discount^transfers for
  pairs that improve on baseline by the margin under N₀ ∪ A — the quantity the mod's
  per-round score approximates for single lines.
- Greedy's round-t scores do not sum to either; the reference model must fix one.

Exact optimum computed by complete enumeration over the candidate pool for bounded
instances (the pool is bounded per network: budget = 4 × maxRoutes).

**Resolved (user decision, 2026-09-03): the reference set objective is (b), the
credited sum** — Σ over OD pairs of w · discount^transfers for pairs that the
network N₀ ∪ A carries with ≥ switch-margin improvement over the N₀ baseline
(evaluated with interval semantics over shortest-itinerary ties). Lean 4
formalization is deferred until after the pipeline (user decision, same date).

## 7. S1 — Heatmap evaluator

**v2 (Phase 3, 2026-09-05): walking-time access over the pedestrian network.** The
Burst job of 7.2 is retired; the mod's live computation is `SuitabilityWalkAccess`
(pure C#, run on a worker thread; the identical code runs offline in the subject).
Instance kind `heatmap_walk`. Definitions:

- **Graph.** Nodes = endpoints of every net edge carrying a lane with
  `PathMethod.Pedestrian` (streets with pavements and stand-alone paths; `Curve` and
  `Edge` present, not Deleted/Temp); node order = first-seen in edge order. Edge cost
  in whole milliseconds: `ms = max(1, round_half_even(L / w · 1000))` with L =
  `Curve.m_Length` (binary32, widened to double) and w = the binary32 constant 1.2f
  widened (1.2000000476837158), arithmetic in double. Undirected.
- **Snap.** A point p is served by the node minimising the double squared distance
  Σ(Δ²) (ties: lower node index); its access walk is `a = round_half_even(√d² / w ·
  1000)` ms (no floor); p is off-network if no node lies within A = 120 000 ms ·
  w/1000 m or a > A. Tile centres are `worldMin + (i + 0.5)·32` in binary32.
- **Times.** t(s, n) = a_s + d(node_s, n) with d the exact integer shortest-path time.
  Horizons: catchment classes C = {360 000, 660 000, 960 000} ms (Bus/Tram 6, Metro/
  Ferry 11, Train 16 min), transfer τ = 180 000 ms.
- **Kernel.** K(t, T) = 1 − t/T computed in **double** (t, T exact), for t ≤ T.
- **Arithmetic rule (v2, 2026-09-05).** Every store into a binary32 accumulator is
  `fl32(acc + w · k)` with acc, w widened to double, k the double kernel (or a product
  of doubles), and exactly one rounding at the store. Rationale: C# lets a runtime keep
  binary32 intermediates at higher precision, and the game's Mono does while .NET does
  not — the first `heatmap_walk` export disagreed in the last bit of 285/2073 tiles
  (access term and a weighted sum) with both the offline run of the same code and the
  evaluator. Spelling the arithmetic out in double with one explicit narrowing is
  runtime-independent; the Mono results observed were consistent with it.
- **Accumulators per node n**, for each class c with horizon T_c: Demand_c[n] =
  Σ_homes fl32-store(w_h·K(t, T_c)); Jobs_c[n]; Future_c[n] (zoned homes then zoned
  workplaces); per stop type y: Within_c[y][n] = Σ_stops of type y K(t, T_c);
  Inter[y][n] = Σ K(t, τ) over t ≤ τ; Cross_c[y][n] = Σ K(t, T_c)·(1 − K(t, τ)·[t ≤ τ])
  (the product formed in double). All sums **in source index order** under the
  arithmetic rule, each source contributing at most once per node (so the settle
  order is irrelevant). Sources: residents per home building (household
  citizens count, at the building's Transform), workplaces (max workers), zoned
  cells (cell area), served passenger stops with `TransportStopData.m_TransportType`.
- **Node terms for mode M** (class c(M), own type y(M), type weights ω from capacity
  ratios, A1.10): T1 = Demand_c, T2 = Jobs_c, T5 = Future_c, T3 = Within_c[y(M)],
  T6 = Σ_{y ≠ y(M), ω_y > 0} ω_y · Inter[y] (types ascending), T7 = Σ ω_y · Cross_c[y].
- **Tile terms.** Unbuildable tile ⇒ all zero, no node. Otherwise the tile's node's
  terms with T4 = fl32(K(a_tile, A)); a tile with no node ⇒ all zero. T6/T7 sums over
  types follow the arithmetic rule (fl32(acc + ω_y · Inter[y][n])).
- **Combine (v2).** Caps and weights as in 7.3; coverage share = min(T3, 1.5)/1.5;
  `final = score` if the tile has a node, else 0 (the v1 road gate `sat(T4·2)` is
  retired). `ScoreForMode(p, M)` = the full combine of the node terms for M at p's
  tile (no swap), access from the tile's own walk.
- **Verification.** `heatmap_walk` requires three-way bit equality on sampled tiles:
  the game's exported terms, the subject (the mod's pure code on the exported inputs)
  and the evaluator's independent re-derivation; plus edge_ms = spec(edge_metres) and
  snap agreement. C1.6–C1.9 in the claims table.

Everything from 7.1 to 7.5 below is the **v1 record** (Burst job), kept because the
exported v1 instances are still checked against it.

### 7.1 Masks (per 32 m tile, probed at the tile centre)

- land: water depth ≤ 0.5 m. gentle: terrain normal.y ≥ cos(clamp(maxSlope°,1,89)).
- buildable = land ∧ gentle; ferry additionally admits depth ≤ 0.6 m and then
  restricts to tiles with at least one non-land 8-neighbour (shoreline).
- Connected components: 8-connected flood fill over **land** tiles — deliberately not
  over buildable ones (`SuitabilityMasks`, pass 2, states the reason: a steep hillside
  still joins the valleys either side of it, whereas water genuinely separates them).
  Labels 1..N in raster-scan seed order; non-land = 0. 8-connected to agree with
  `AccumulateWalkDistance`, which walks diagonals.
  Consequences, both confirmed on a real export (Valmare, 200,704 cells): buildable
  ⟹ land ⟹ labelled holds exactly (0 violations), while labelled ⟹ buildable is
  false by design (146,093 labelled-but-unbuildable tiles, all steep ground). In
  FERRY mode the implication also breaks the other way: a shallow-water shoreline
  tile is buildable while `land` is 0, so it carries no label.
  *(This entry was wrong until the first real export: it claimed the fill ran over
  buildable tiles. The mod was right; the specification was not.)*

### 7.2 Raw terms (Burst job, one output per tile, batch 64 — outputs independent)

Gate: unbuildable tile ⇒ all seven terms 0. Kernel K(d, r) = 1 − d/r for d ≤ r.
Component gating: a source counts only if its position's tile has the same component
label as the scored tile (applied to T1, T2, T5; **not** to T3, T6, T7; T4 ungated).

- T1 Demand = Σ over population cells within CatchmentRadius: pop · K(d, R_c).
- T2 Jobs = Σ over workplaces (weight = maxWorkers) within R_c: w · K(d, R_c).
- T3 Coverage = clamp(Σ over same-mode served stops: w · K(d, R_c), 0, 1.5).
- T4 Access = saturate((0.06·Σ_edges K(d, R_a) + 0.15·Σ_nodes K(d, R_a)) · (120/R_a)²),
  edges sampled at chord midpoints, nodes at road-edge endpoints.
- T5 Future = Σ over zoned-unbuilt cells (weight = CELL_AREA), residential + working,
  each · K(d, R_c).
- T6/T7 Interchange/CrossCoverage: per other-mode served stop with mode weight w
  (Bus 1, Ferry 1.2, Tram 1.5, Subway 2.5, Train 3, …), within R_c:
  transferable = d ≤ R_i ? 1 − d/R_i : 0 with R_i = min(250, R_c);
  T6 += w · transferable; T7 += w · K(d, R_c) · (1 − transferable).

### 7.3 Combine (main thread, sequential)

Caps: cap_X = PositivePercentile(term X, 0.98) for X ∈ {Demand, Jobs, Future};
inv_X = cap > 0 ? 1/cap : 0 (zero cap zeroes the term). selfWeight =
max(0.1, ModeWeight(panel mode)); invSelf = 1/selfWeight.

score = W1·sat(T1·invD) + W2·sat(T2·invJ) + W4·T4 + W5·sat(T5·invF)
      + W6·sat(T6·invSelf) − W3·(T3/1.5) − W7·sat(T7·invSelf)
final = score · sat(T4 · 2)          (road gate: fades to 0 below access 0.5)

Intensity: cap = PositivePercentile(final, 1 − HighlightShare/100);
byte = round((sat(final/cap))^0.6 · 255, away-from-zero); layer cleared if cap ≤ 0.

### 7.4 ScoreForMode (mode swap at a point)

result = m_Scores[cell] + (ModeTerms recomputed for the target mode over a flat scan
of all served stops − ModeTerms as combined) · sat(T4_raw · 2), using the pinned
weights/radii of the last combine. Not component-gated (neither are T3/T6/T7 in the
job). `ShorelineScoreAt`: best uncorrected score in a 5×5 tile window that touches
land, corrected via ScoreForMode(Ferry), plus an unconditional +1 bonus.

### 7.5 Site extraction parameters

separation = max(2, round(R_c/32)) tiles; wanted = min(SiteCount, 20). Survivors
re-scored by tile-Dijkstra walk catchment (8-connected over the land mask — not the
buildable mask — diagonal √2·32, radius R_c, W1·demand + W2·jobs with K(d, R_c)),
re-sorted descending (insertion sort, stable), trailing non-positive scores dropped.

## 7b. S3 — Demand evaluator

1. **Trips**: per citizen with a rented home (tourists included since v2, A0.2): home
   = household property transform; dest = workplace else school, both **w = 1.0**
   (A0.3; v1 had school 0.6); reject < 1 m. Parallel extraction into a queue (order
   non-deterministic).
   **v2 (Phase 4, 2026-09-05) — observed shopping/leisure journeys (A0.1).** The
   save holds no such destinations, so the live city is scanned once per real second:
   every citizen inside a building is remembered as (citizen → building); every
   citizen carrying `TravelPurpose` ∈ {Shopping, Leisure, Relaxing, Sightseeing,
   VisitAttractions} together with a `Target` is a journey (target position: the
   target's Transform, or its rented property's), recorded once per distinct
   (citizen, target, purpose) while continuously seen, origin = the building the
   citizen was last seen inside (journeys without a known origin are counted and
   dropped). Journeys live in a window of one game day (262 144 frames,
   `ObservedTripWindow`, cap 200 000, frame-rewind clears it). At each demand refresh
   they join the queue above with weight `ScaleFor(day) = clamp(day/span, 1, 4)` —
   a full window counts one per journey, a shorter one is scaled to a day's rate but
   never more than 4×. The panel shows how many were seen over how many game hours.
2. **Zones**: zone = floor((p − worldMin)/256) (out-of-range dropped, NOT clamped —
   unlike WorldToCell); key = origin·zoneCount + dest accumulated in a dictionary;
   result list **sorted totally by (origin, dest)** — this restores determinism of
   everything downstream. (`totalWeight` alone is summed in dequeue order and is
   last-bit non-deterministic; it feeds only logs/sanity checks.)
3. **Zone→stop**: nearest served stop to zone centre within 500 m (O(zones·stops)
   scan, strict <, ties → lower index).
4. **Baseline & discount**: per OD pair with distinct mapped stops, one Dijkstra per
   origin over the transit graph (cap 3600 s); pair is served iff reachable with
   boardings ≥ 1; doorToDoor = travelTime + walk(originZone→stop) +
   walk(destZone→stop) at 1.4 m/s. ceiling = min(3·median(served doorToDoor), 3600)
   if ≥ 20 served pairs else 3600. **Discount: w ← w · sat(doorToDoor/ceiling).**
   Unserved pairs keep full weight. m_UnservedTravelWeight = Σ w afterwards.
5. **Assignment**: per network, Array.Clear(EdgeFlow); flows in sorted order, one
   Dijkstra per origin zone (caps §3.1); each flow's weight added to every edge of
   the shortest path. Ferry only gets flows whose zone centres lie on different
   positive land components.
6. **Node demand** (for corridor growth): per road node,
   sat(T1·invD) + sat(T2·invJ) at the node's tile ∈ [0, 2]; corridor demand floor
   0.005.

## 8. Determinism inventory (whole mod)

- **No RNG anywhere** (pure or ECS half). No time-dependent arithmetic (timers gate
  *when* recomputes run, not what they compute).
- Parallelism: `SuitabilityJob` (IJobParallelFor) writes one independent output per
  tile from read-only inputs — deterministic. `ExtractTripsJob` enqueues in
  thread-dependent order — neutralized by dictionary aggregation + total sort;
  residual: `totalWeight` float sum (logs only).
- Deterministic-order fixes in the code: road nodes interned via ordered list (not
  hash order); zone flows total-sorted by (origin, dest); calibration rows sorted by
  key.
- Under-specified ties (deterministic per runtime, unspecified by construction):
  `Array.Sort` on equal site scores (S2); `List.Sort` instability on equal
  CapturedFlow (pool order) and equal (EnabledDemand, CapturedFlow) (acceptance
  order); Dijkstra heap order among equal-cost paths (S4 path shape, S6.2 itinerary).
- Floating point: sequential accumulation everywhere else; binary32 with three
  double-precision islands (calibration sums, WalkSeconds sqrt, NormalizeIntensities
  pow).

## 9. Global-optimality statement

The mod claims **no** global optimality anywhere (README: "Selection is greedy …
These are suggestions, not optimal networks"). The verification therefore proves:
(i) feasibility of every mod output against this specification, (ii) faithfulness of
the mod's procedures to their own definitions, and (iii) the exact gap between the
mod's selections (S2, S7) and the certified global optimum of the declared reference
objectives on exported instances — not that the mod is optimal.

## 10. Specification changes since the verified v1 (redesign, from 2026-09-04)

Every change below is a decision from `docs/assumptions-register.md`; the pipeline's
constants follow the spec, so each row names where verification had to move too.

| Date | Change | Register | Verification impact |
|---|---|---|---|
| 2026-09-04 | Walking speed 1.4 → **1.2 m/s** (routing walk edges, transfer walks) | Gehgeschwindigkeit | `evaluator/transit.py` WALK_SPEED |
| 2026-09-04 | Transfer walk radius 250 m → **180 s × 1.2 m/s = 216 m** (one constant for the routing's walk edges, the interchange map and the heatmap's transfer distance); zone→stop reach = 2× = 432 m | A1.11 | instance data — exported v1 instances keep their own values |
| 2026-09-04 | Trip weights: school 0.6 → **1.0**; tourists/homeless no longer filtered (only "no rented property" excludes, structurally) | A0.2, A0.3 | none offline (ECS extraction) |
| 2026-09-04 | Ferry shoreline **+1 bonus removed** | A1.13 | none offline (ECS) |
| 2026-09-05 | **S3 demand adds observed shopping/leisure journeys** (Phase 4: live-city scan, one-game-day window, per-day scaling ≤ 4×, merged with home-work/school at equal weight; panel shows the count and coverage) | A0.1 | ECS-side observation is unverifiable offline; the window logic is pure and harness-tested (`ObservedTripWindow`) |
| 2026-09-05 | **Heatmap arithmetic pinned to double with one rounding per store** (see §7 v2 arithmetic rule) — after the first real `heatmap_walk` export showed Mono keeping float intermediates at higher precision | — | evaluator/generator follow; a re-export is needed before the real-city three-way check can pass |
| 2026-09-05 | **S1 heatmap terms are walking times over the pedestrian network** (`SuitabilityWalkAccess`; Burst job `SuitabilityJob` deleted; residents per home building replace the 224 m population raster; access = network node within 2 min; catchments 6/11/16 min as linear time kernels; transfer 3 min; road gate replaced by "has a node"; site refinement pass removed) | A1.1, A1.2, A1.4, A1.5, A1.6, A0.5, A0.6, Phase-3 values | new kind `heatmap_walk` with three-way bit-exact check; v1 `heatmap_grid`/`heatmap_point` evaluators retired or historical |
| 2026-09-05 | **S2 candidates are network nodes; separation is walking time ≥ stop spacing** (`SolveOnNetwork`, ball clique-cover bound) | A2.1, A2.2, A2.4 | new kind `sites_walk`: exact Dijkstra conflicts, SCIP/VIPR on pairwise MIP, exact selection judged |
| 2026-09-05 | **S2 site selection is exact** (`SuitabilityExactSites`, branch-and-bound with block-partition bound, integer-scaled scores, node budget 2·10⁶ with reported ceiling); the greedy ranking stays as incumbent and measured baseline | A2.3 | subject reports `exact_*` fields; `run.py` requires gap 0 against the certified optimum when the search closed, else a sound bracket — 6/6 instances closed (two real cities: 14 and 0 nodes) |
| 2026-09-04 | Interchange/coverage weight of another mode's stop = **vehicle capacity ÷ bus capacity from the loaded prefabs** (`TransitModes.CapacityWeight`), replacing the table 1/1.2/1.5/2.5/3; a type without a loaded vehicle weighs 0 | A1.10 | heatmap `w_b32` remain instance data; new pure function unit-tested |
