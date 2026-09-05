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
7. **S7 Line-set selection** (`SuitabilityLineSet.Solve`, v2 since Phase 7): exact
   branch-and-bound over candidate subsets under the lexicographic objective
   (equity share capped at the floor, passenger time saved), with per-line
   utilisation and duplicate feasibility. v1 (`CreditLine` credits and greedy
   `AcceptBestCandidate` rounds) is kept below as history.

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

Since 2026-09-05 (A4.7) the bent alignment does not REPLACE the direct one: both are
emitted as candidates of the same journey pair, and the S7 set selection decides
between them on passenger time (they duplicate each other under §6.3 F2, so a set
holds at most one). The invariants above are now the admissibility of the via
variant only.

### 3.4 Directed road graph and driving times (v2, Phase 5, 2026-09-05; A0.7/A0.8)

Instance kind `road_times`. Alongside the undirected CompactGraph the corridor search
grows on (unchanged), the mod builds the streets as a road vehicle drives them:

- **Arcs.** For every road edge and every car lane on it (SubLane with `Road` or
  `PublicTransportDay` in its path methods, carrying `CarLane` and `EdgeLane`): the
  lane runs start→end iff `EdgeLane.m_EdgeDelta.x < m_EdgeDelta.y` (the comparison
  decompiled `Game.Pathfind.LaneDataSystem` makes); a `Twoway` lane admits both. A
  direction exists iff at least one lane admits it; its speed v = max `m_SpeedLimit`
  (m/s) over those lanes. Arc time `ms = max(1, round_half_even(L / max(0.1, v) ·
  1000))` in double with L = `Curve.m_Length` (binary32). Headings: unit tangents of
  the edge's Bézier at its ends (binary32, `math.normalizesafe`), negated for the
  backward arc; they are instance data.
- **Turns.** Arriving along arc a and leaving along arc b at a's head costs
  `turn_ms[class]`, class from the double dot product of a's arriving and b's
  departing headings: ≥ cos 15° straight, ≥ cos 45° gentle, ≥ cos 120° turn,
  ≥ cos 165° sharp, else U-turn (cosines as the exact double literals in
  `DirectedRoadGraph`). `turn_ms[c] = round_half_even(r · θ_c · 1000)` with θ =
  0, π/6, π/2, 7π/9, π and r = `PathfindCarData.m_CurveAngleCost.m_Value.x` of the
  first car lane's pathfind prefab (the game's time cost per radian of curvature;
  default 2 s/rad from the `CarPathfind` prefab class). All turns are admitted; the
  game's comfort/behaviour/money cost components are not modelled (they steer
  cars, not a planner's travel time).
- **Shortest driving time** from node s to node t: the minimum over arc-states of
  the integer Dijkstra whose state is the arc last driven — equivalently, the
  shortest path S→T on the explicit state graph with nodes = arcs ∪ {S, T}, edges
  S→a (a leaves s, cost ms_a), a→b (b leaves a's head, cost turn_ms(a,b) + ms_b),
  a→T (a's head = t, cost 0). Ties settle the lower arc index. Cap 3 600 000 ms.
- **Points on streets (stops).** A stop is projected onto the nearest arc chord
  (double arithmetic on binary32 coordinates; distance ties → lower arc index; off
  the network beyond 64 m) at fraction t; `PositionMs(arc, t) = round_half_even(t ·
  ms_arc)`. A leg from point P to point Q tries every pairing of P's arcs (the arc
  found and, on a two-way street, its reverse with 1 − t) with Q's arcs and keeps
  the fastest (ties: lower from-arc, then to-arc). Within a pairing (a, t_a) → (b,
  t_b): if a = b and t_b ≥ t_a the time is `PositionMs(b, t_b) − PositionMs(a, t_a)`;
  otherwise the vehicle starts in state a with time `start = ms_a − PositionMs(a,
  t_a)` and the leg time is min over states g ending at b's tail of
  `dist(g) + turn(g, b) + PositionMs(b, t_b)`. State graph for the certificate:
  S→a (start), a→b' transitions, g→T (turn + end), and S→T (direct) when the same-arc
  case applies.
- **Uses.** S3 flow assignment routes every zone pair along its fastest directed
  path (cap 20 000 m ÷ 9 m/s), summing each pair's weight onto the undirected
  street the arc runs along (so the corridor search still sees one figure per
  street) and onto the arc. Suggested road lines get `m_RideSeconds[i]` = the point
  leg time from stop i−1 to stop i (Unreached → cruise-speed fallback, logged), and
  their fleet from the out-and-back point-leg driving time plus dwell.
- **Verification.** The instance carries the arcs with their inputs, the turn rate,
  and the legs the mod asked for with its answers. The evaluator re-derives every
  `arc_ms` and the turn table, builds each leg's state graph and requires
  game = subject = exact; per leg a directed label certificate is checked in Python
  and by the Lean-proved directed checker (`Verify.DirPathCert.check_sound`, axioms
  propext and Quot.sound only).

## 4. S5 — Stop placement along a fixed alignment (v2, Phase 7, 2026-09-05; A5.1, A5.2, A5.4)

Given polyline P (metres), mode spacing σ (`StopSpacingFor`), access horizon H =
`CatchmentMs(mode)`/1000 s, walking speed 1.2 m/s, the fleet's delay per stop δ
(`FleetFacts.DelayPerStopSeconds` = line prefab stop duration + v/2a + v/2b, v the
cruise speed, a/b the vehicle prefab's acceleration and braking), and the journeys
(each contributing its origin and its destination as a *door* with the journey's
weight):

1. **Candidates** (`SuitabilityRoutes.BuildStopPlan`): positions every 50 m along P
   plus the far end; a position is *admissible* iff the score oracle is positive
   there (for ferries: shoreline; for rail/road: a scored tile). The first and last
   admissible positions are the termini. Within every maximal run of candidates that
   lie within 150 m of an interchange, the nearest one is *forced*; forced calls
   closer than the gap floor to an earlier forced call lose the flag. Inadmissible
   positions between the termini are dropped; arc lengths are measured from the
   first terminus. `through_c` = the assigned flow of the path edge nearest to
   candidate c (`SuitabilityRoadGraph.FlowNear`).
2. **Doors**: every journey end within H·1.2 m of P between the termini, with the
   arc length of its projection.
3. **Objective** (`SuitabilityStopPlan.Solve`), for a chosen set S containing both
   termini and every forced candidate, consecutive members ≥ σ/2 apart unless both
   are forced:
   value(S) = Σ_doors w·max(0, H − t_e(S)) − Σ_{c∈S} through_c·δ,
   where t_e(S) is the straight-line walking time from door e to the nearer of the
   two members of S bracketing its projection (ends before the first / after the
   last member see that member only). "A stop exactly where the boarders' access
   gain outweighs the through-riders' delay" (A5.4) is this objective's marginal
   condition.
4. **Exact maximisation**: dynamic programme over candidates in arc order,
   f(k) = max over admissible predecessors i of f(i) + gain(i,k) − through_k·δ, with
   gain(i,k) the doors projecting into (at_i, at_k]; a transition may not skip a
   forced candidate. Exact because a door's gain depends only on the two members
   around it. When no plan honours the gap between forced calls the termini alone
   are the plan.
5. Stops closer than 20 m to an already-added stop are dropped; the path is trimmed
   to [first call, last call]; Length recomputed.

The line then needs ≥ `MinStops` = 3 calls and an end-to-end ride within the mode's
limit (§5). Verification (`stop_plan`): the exported plans carry candidates, forced
flags, through-flow, doors and the mod's choice; the subject re-solves with the mod's
DP, the evaluator computes the exact optimum (complete enumeration of the free
candidates when ≤ 14, otherwise an independent DP over the same definition) and
requires the mod's choice to be feasible and of optimal value; ties are reported.
Three synthetic instances pass; a real-city `-stops` export is pending.

### 4.1 History: v1 windows (verified 2026-09-03, retired 2026-09-05)

v1 placed calling offsets o_i = T·i/n (n = round(T/σ)), nudged each within
±0.35·step to the best-scoring sample, forced stations within 150 m, and kept a
window iff terminus ∨ forced ∨ score ≥ 0.35·median⁺ (`PlanCallingPoints`,
`SelectCallingPoints`, Lean `Verify.CallingPoints`). C7.5 showed that stops placed
by spacing alone miss the joint optimum; the v2 plan optimises the declared
objective instead. The Lean theorems remain valid statements about the v1 rules.

## 5. S6 — Mode choice (`ChooseMode`, v2, Phase 7, 2026-09-05; A6.x, A4.6/A6.1, A5.5)

For the network's ladder — Road: Bus, Tram; Rail: Metro, Train (one rail lattice,
A4.5); Water: Ferry — the mode is the first whose vehicles are not overloaded by the
candidate's standalone riders:
utilisation(M) = riders·2 / ((D / headway_M) · 2 · capacity_M) ≤ 1, with D = 4369.07
movement seconds per game day, headway_M = `TargetHeadwayFor` (Bus 300, Tram 240,
Metro 200, Train 480, Ferry 600 s — the planning table, NOT the prefab default
interval: Valmare's prefabs say 45/45/60/90/90 s, intervals no player keeps and
against which the 15 % floor was never calibrated; the prefab value is logged,
register A5.5/A6.x RF), capacity_M the largest vehicle prefab's seats (carriages
included). δ per stop = max(prefab stop duration + v/2a + v/2b, 15 s): the prefabs
give a bus 1 s dwell at 6 m/s², which would make a stop nearly free (RF, same rows). If every mode is overloaded the
largest is chosen; a network with no vehicle installed yields no mode. The riders
are re-measured after the stops are placed for the chosen mode (stops and riders
depend on each other; one further ladder step is taken if they disagree), and the
alignment is ALSO offered as the next mode up whenever the ladder has one, so the
set's utilisation ceiling (§6.3 F1) can swap an overloaded bus for a tram. Whether the
line reaches the utilisation FLOOR is not asked here — a feeder alone rarely fills
anything — but by the set selection on the set's own riders (§6.3). A lattice
candidate whose standalone utilisation is below the floor is additionally offered
re-traced along streets (A4.7-style second candidate).

**Shape gates** (`KeepsItsShape`): ≥ 3 stops and ride time ≤ Bus 30 / Tram 35 /
Metro 30 / Train 60 / Ferry 45 min, ride time = directed street legs (or length at
cruise speed) + (stops − 2)·δ. The length floors and ceilings of v1
(`MinLengthFor`, `MaxRouteMetres`), the flow multiples, reach shares, track relief
and one-bus rider floor are removed; corridor growth and lattice traces are bounded
by the longest ride the network's modes allow at cruise speed
(`MaxAlignmentMetresFor`). Verification (`mode_choice`): binary32 re-derivation of
the ladder, utilisation and δ; bit-exact on the sweep instance.

### 5.1 History: v1 gate cascade (retired 2026-09-05)

M was chosen iff (flow ≥ refFlow·MinFlowMultiple(M) ∨ enabledShare ≥ reachBar(M))
∧ (¬demandScored ∨ enabledDemand ≥ MinRiders(M)) ∧ length ≥ MinLength(M), first
match; reachBar halved when trackShare ≥ 0.6. Bit-exact agreement was verified on
`mode-choice-sweep` (v1).

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

### 6.2 Journey door-to-door time (v2, Phase 7, 2026-09-05; A4.1, A7.2, A7.4, A3.1)

The transit graph of 6.1 is extended by one node per journey end (**zone node**,
`SuitabilityTransit.BuildWithZones`): a zone node is joined by a walk edge (cost
= Euclidean distance / 1.2 m/s in binary32, floor 0.01 s) to every stop within
`zoneReach` (= 2 × transfer walk = 432 m). Zone nodes are numbered after the
line-stop nodes (`TransitNetwork.ZoneNodeStart`).

For journey i with weight wᵢ under a network N (existing lines ∪ a set A of
candidates):

- transit(i, N) = shortest-path cost from the origin's zone node to the
  destination's zone node, Dijkstra capped at `maxTravelSeconds` (3600 s;
  unreachable = +∞), binary32 accumulation along the path in the mod. **Zone nodes
  are sinks** (since 2026-09-05 16:xx, performance run): a door is reached but never
  walked through — only the source door expands — so two stops within reach of one
  door are not joined by a walk the transfer rule (216 m) never allows. Evaluator:
  `transit.dijkstra(..., expand_below=zone_start)`; predecessor enumeration skips
  door predecessors. The mod computes these distances without door nodes at all: a search starts at
  every stop within reach of the origin door at the door's walking time to it
  (`DijkstraWorkspace.RunFromMany`), and a destination's time is the least stop
  distance plus that stop's walking time to the door — identical values, a
  fraction of the edges; origins run in parallel and the sums are folded in pair
  order (harness "one capped search per origin zone equals one search per pair",
  bit for bit against the door-node graph);
- walkOnly(i) = √(Δx² + Δz²) / 1.2 in binary32 (`WalkOnlySeconds`);
- after(i, A) = min(walkOnly(i), transit(i, N₀ ∪ A)); before(i) = after(i, ∅).

*Implementation (2026-09-05, performance; exact):* the mod deduplicates zone nodes
by position and runs one Dijkstra per origin zone, capped at
min(maxTravelSeconds, max over the zone's pairs of before(i)) (walkOnly(i) for the
baseline run). This is the definition above, not an approximation: pairs with the
same origin see the same edges from the same node, and adding lines never
lengthens a path (every N₀ path persists at the same binary32 cost), so a
destination beyond the cap has transit ≥ before(i) and after(i, A) = before(i)
either way; rider attribution is unchanged because a ridden journey has transit <
walkOnly ≤ … ≤ cap. Sums are folded in pair order, so After, Riders and saved are
bit-identical to the per-pair evaluation (harness test "one capped search per
origin zone equals one search per pair"; the five `lineset_time` instances
reproduced their previous subject outputs exactly).

Pairs are the journeys door to door (A0.5); journeys between the same two doors
are one pair with their summed weight. The baseline is the best of walking and the
existing network; the car is ignored (A3.1 decision). Times weigh walk : wait : ride = 1 : 1 : 1, a transfer costs
exactly its walk edge plus the next line's access edge, nothing is discounted
(A7.2/A7.4: the game's citizens route that way). The realism weights (2.2 / 2.1
/ 1, TCQSM) are reported as a diagnostic from the same itinerary components
(`LineSetEvaluation.WalkSeconds/WaitSeconds/RideSeconds`) and never enter the
selection.

**Riders of a candidate** in a set A: the journeys whose *retained* shortest
itinerary (Dijkstra predecessor chain) boards it — each line credited once per
journey (`AttributeItinerary`). Tie sensitivity: with equal-cost itineraries the
rider attribution depends on which path Dijkstra retained; the evaluator
enumerates every shortest itinerary and reports `tie_affected`.

### 6.3 Set objective and feasibility (`SuitabilityLineSet.Solve`)

For a set A (|A| ≤ K = RouteCount) of the candidate pool:

- **saved(A)** = Σᵢ wᵢ · max(0, before(i) − after(i, A)) — in the mod the
  difference is formed in binary32, the product and the sum in double, in pair
  order (`LineSetEvaluation.TimeSaved`).
- **coverage(A)** = the §7c served share of the walking-network journeys once A's
  stops (snapped like served stops) join the served stops
  (`SuitabilityEquity.WithStops` → `Coverage`); 0 when no equity inputs exist.
- **Key(A)** = (min(coverage(A), X), saved(A)), compared lexicographically; X =
  the equity floor share (0.8).
- **Feasible(A)** iff A holds at most one candidate per *alignment group* (the
  variants of one alignment — direct or bent through a hub, one mode or the next
  up, a rail trace or its re-trace along streets — are alternatives, A4.7;
  `LineCandidate.Group`, negative = none), and for every line c ∈ A:
  (F1) utilisation(c, A) = riders(c, A) · 2 / ((D / headway_c) · 2 · capacity_c) ≥
  the utilisation floor (0.15 default), with D = movement seconds per game day =
  4369.07 (`TimeSystem.kTicksPerDay` / 60) — riders are the set's attribution,
  so a feeder's riders count for the trunk it feeds — and, since 2026-09-05 17:xx,
  ≤ the utilisation CEILING (`MaxPlannedUtilisation` = 1.0): a line the set fills
  past its seats is overloaded for its mode and infeasible; the same alignment is
  offered as the next mode up (§5), which the set then takes instead;
  (F2) c is **not a duplicate**: with slowed(c) = Σ wᵢ over journeys with
  after(i, A∖{c}) > after(i, A), (riders(c) − slowed(c)) / riders(c) <
  `DuplicateShare` (0.5, A4.3); a line with no riders is a duplicate.
  Feasibility is a property of the set, not of a line alone (F1 and F2 both move
  with the other members).

The mod maximises Key over feasible A in three stages (`Search.Run`, since
2026-09-05 evening): (1) a **greedy build** — add the candidate that improves
Key most while the set stays feasible, until MaxLines or no improvement; (2) a
**swap local search** — replace one chosen line by one outside the set whenever
that improves Key, until no swap does (the transit route network design
literature reaches its best-known solutions with such neighbourhood moves;
greedy alone carries the Das–Kempe (1 − e^{−γ}) guarantee only for the
submodularity ratio γ, which complementarity keeps below 1 here); (3) the exact
**depth-first branch-and-bound** from that incumbent: candidates ordered by
standalone saved (desc, index tiebreak);
include-first DFS that never adds a second variant of a group already in the
set; every prefix is a candidate answer; **bound** = Key(chosen ∪ all remaining)
— and a subtree is skipped outright once the incumbent has passed the bound its
parent computed, of which every union below is a subset — valid because both components are monotone in A (a line can
only shorten a journey or serve another door; the cap keeps the first
component monotone). A node budget (`DefaultNodeBudget` = 20 000 bound
evaluations) or the caller's cancellation token (the mod passes a 30 s wall-clock
budget on its worker — the incumbent of stages 1–2 is usually reached within a
few seconds, the remainder is the proof attempt, and starts a pass at most every 300 s unless the objective
or line count changed) stops the search; the solution then reports `Optimal =
false` and `UpperBoundTimeSaved` = max over open bounds — the "best found plus
ceiling" regime, and the log names it. Evaluations of sets of ≤ K lines are
memoised within one search (a prefix is asked for again as "the rest" of its
supersets' duplicate tests). Note the bound is evaluated on the *unfiltered* union, so an infeasible
completion never prunes a feasible one.

The chosen lines are presented in standalone-saved order; each carries its
riders, utilisation, saved seconds and the realism-weighted diagnostic.

**Reference objective (user decisions 2026-09-05, A4.1/A1.8/A3.1/A4.3/A7.2/
A7.5):** exactly the Key above over the exported candidate pool — the mod now
optimises the declared objective, so the pipeline's question is no longer "how
big is the gap" but "is the mod's answer the optimum, and is it feasible".
Verification (`lineset_time`): the subject reruns `Solve` on the exported
problem; the evaluator recomputes before/after in exact rationals (binary32
edge costs, exact Dijkstra), riders over all shortest itineraries, F1/F2, the
coverage share via the `coverage` rules, and Key; `enumerate/enum_lines.py`
evaluates every subset of size ≤ k′ (k′ = K when Σ C(n, j) ≤ `ENUM_BUDGET`,
else the largest enumerable size — "bounded" regime, reported as such) and the
verdict requires the mod's set to be feasible, its saved within the Higham
budget of the exact value, and its Key equal to the enumerated optimum's
whenever the mod claims `Optimal` (ties in Key are counted and reported).

### 6.4 History: v1 credit and greedy acceptance (verified 2026-09-03, retired 2026-09-05)

v1 scored each candidate alone by `CreditLine`: per OD pair the shortest
itinerary's boardings b = (#Access edges)/2, transfers = max(0, b−1); credited
iff the itinerary used the target line, b ≥ 1 and doorToDoor < baseline − 60 s;
credit = w · 0.6^transfers (interval semantics over tied itineraries). Rounds
(`AcceptBestCandidate`) accepted the best-credited survivor of gates G1–G5
(length floor, evidence, corridor flow > 1, one-bus demand floor, ≥ 75 % of
stops within 150 m of one existing line) and re-scored the rest against the
network with it. The declared v1 reference set objective was the credited sum
(b) over N₀ ∪ A; complete enumeration refuted greedy optimality on
`lineset-feeder` (40 vs ≈ 80) and on Valmare's third export (3-line gap 6.4 %),
which is what motivated the v2 set selection. The v1 evaluator, instances and
export kind were removed with Phase 7; the claims table keeps the refutations.

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
   every citizen inside a building is remembered as (citizen → building);
   journeys are seen in two stages, keyed per (citizen, purpose) so each is recorded
   once: (A) *queued* — a citizen inside a building whose `TripNeeded` buffer's first
   entry has a watched purpose (Shopping, Leisure, Relaxing, Sightseeing,
   VisitAttractions): origin = `CurrentBuilding`, destination = `m_TargetAgent`
   (position: its Transform, or its rented property's); (B) *under way* — a citizen
   with `TravelPurpose` of a watched purpose and a `CurrentTransport`: destination =
   the creature's `Target` if it is a building or a property-renting company (never a
   vehicle), origin = the building the citizen was last seen inside. Journeys without
   a resolvable origin/destination, or first seen already at the destination, are not
   recorded. Why two stages (decompiled `TripNeededSystem`, confirmed live): the
   citizen's own `Target` and its `TripNeeded` entry are both gone once it leaves, and
   the creature's `Target` is rewritten only while boarding a vehicle that is itself
   the target or while diverted. Journeys live in a window of one game day (262 144 frames,
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

## 7c. Equity measure and selection floors (v2, Phase 6, 2026-09-05; A1.8, A1.9, A4.1, A6.4)

Instance kind `coverage`. Decisions: sufficientarian floor as ε-constraint with the
efficiency objective below it; T = 10 min walking horizon, X = 80 % served share,
utilisation floor 15 % (all three are Options sliders; the floor was 25 % until the day-length correction below).

- **Served-walk field.** Every served stop (the existing lines' stops, plus accepted
  suggestions within a selection round) is snapped to the pedestrian network (§7 v2
  snap rule, access A = 120 000 ms). `served[n]` = min over stops of `a_stop +
  d(node_stop, n)` restricted to ≤ T (integer ms; unserved = ∞), i.e. one bounded
  integer Dijkstra per stop taking the minimum.
- **Served journey.** A journey (S3 trip: home→work/school, or an observed journey
  scaled per §7b) with ends snapped to nodes (o, a_o), (d, a_d) is served iff
  `served[o] + a_o ≤ T` and `served[d] + a_d ≤ T`; an end off the network is never
  served (and counted). **Share** = fl32(Σ w_served / Σ w) with both sums of binary32
  weights taken in trip order in double.
- **Gini of access walk** (diagnostic): value_i = `served[o_i] + a_{o,i}` when the
  origin is served, else 2T; weighted Gini = 1 − Σ_i w_i (S_{i−1} + S_i) / (W · S_n)
  over trips sorted by (value, index), S the running Σ w·value; 0 when W or S_n is 0.
- **Selection.** While Share·100 < X, candidates are ordered by the journey weight they
  would newly serve (`CoverageGain`: the field with the candidate's stops added,
  coverage recomputed) descending, then by enabled demand, then corridor flow; at or
  above X the order is enabled demand, corridor flow as before. Accepting a candidate
  adds its stops to the field and re-measures.
- **Utilisation gate** (replaces "fills one bus at the peak"): a demand-scored
  candidate must reach `utilisation = enabledDemand · 2 / ((D / headway) · 2 ·
  capacity(mode)) ≥ floor` with D = 262 144 / 60 = 4 369.07 s, the movement seconds in
  one game day (SimulationSystem: 60 ticks per real second at speed 1;
  TimeSystem.kTicksPerDay), headway = 2 × the mode's suggested wait, capacity from the
  loaded prefabs. *Correction 2026-09-05:* the first version divided a real-world hour
  (3 600 s) by the headway and took a 20 % peak share, i.e. assumed an 86 400 s day; on
  the game's clock that under-read every candidate about twentyfold (Valmare's best
  read 5.0 % instead of 20.5 %). Note the game's own "usage" figure is instantaneous
  occupancy (passengers on board over capacity), a different quantity.
- **Verification.** The instance carries graph, served stops, journeys, A and T and
  the mod's share/sums/Gini/counts; the evaluator re-derives everything with exact
  integer times and the stated double summation order; game = subject = evaluator.

## 7d. Operating periods — day and night (2026-09-05, user request)

Game facts (decompiled): `TransportLineSystem` calls it night when the day fraction
`normalizedTime` is < 0.25 or ≥ 11/12 — **night is 22:00–06:00, day 06:00–22:00**;
a line with the `RouteOption.Day` policy is inactive at night, one with
`RouteOption.Night` inactive by day (the UI's `RouteSchedule` Day / Night /
DayAndNight). Workers leave for work at `EconomyParameterData.m_WorkDayStart` plus a
per-citizen offset of ±1 h and return at `m_WorkDayEnd` plus the same offset; the
evening shift adds 0.33 of a day, the night shift 0.67 (`WorkerSystem.GetTimeToWork`);
students keep the day shift's hours (`StudentSystem.GetTimeToStudy`).

Model (`SuitabilityDaytime`):
- every journey carries a **day share** ∈ [0, 1] of its rides: a commute's two rides
  are classed by their shift's nominal times (the ±1 h offset is not modelled), an
  observed shopping/leisure journey by the clock when it was seen; door pairs carry
  the weight-mean of their journeys' shares (`PairDayShare`);
- the set evaluation splits each line's riders into day and night
  (`RidersByDay/Night`); a suggested line's utilisation per period is boardings over
  the seats offered in that period (the day's seats × 16/24 or 8/24);
- **recommendation** (`Daytime.Recommend`): run by day only when the night period is
  under the utilisation floor while the day is not; by night only in the mirror
  case; otherwise all day. Both periods under the floor is not a schedule question.
- **existing lines** (`Daytime.Advise`): readings carry the clock; the window's mean
  usage per period counts only readings with vehicles out; an all-day line whose
  night mean is under the empty threshold while the day mean is not is advised to
  run by day (mirror: by night), once both periods have ≥ 4 readings. A day-only line
  has no night evidence and is never told to extend on this basis.

Verification: the rules are pure and harness-tested (night boundaries, shift shares
for a 9–17 city, period utilisation arithmetic, both recommendation rules, period
averages that skip idle readings); the game-side stamping of the clock is
log-verified (`Work day from EconomyParameterData …` states the hours and the
resulting shift shares).

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
| 2026-09-05 | **Equity floor and utilisation floor** (§7c: served-walk field, journeys served at both ends, share vs X, weighted Gini; coverage-first ranking below the floor; utilisation gate replaces the one-bus gate) | A1.8, A1.9, A4.1, A6.4, A0.4 | new kind `coverage`, three-way exact |
| 2026-09-05 | **Roads are directed for vehicles** (§3.4: one arc per admitted direction from the car lanes, speed-limit times, five turn classes priced by the game's curve-angle cost; S3 assignment, road ride seconds and fleet estimates use them) | A0.7, A0.8, A5.5 | new kind `road_times`: three-way exact leg times, per-leg directed certificates, Lean `DirPathCert` |
| 2026-09-05 | **S3 demand adds observed shopping/leisure journeys** (Phase 4: live-city scan, one-game-day window, per-day scaling ≤ 4×, merged with home-work/school at equal weight; panel shows the count and coverage) | A0.1 | ECS-side observation is unverifiable offline; the window logic is pure and harness-tested (`ObservedTripWindow`) |
| 2026-09-05 | **Heatmap arithmetic pinned to double with one rounding per store** (see §7 v2 arithmetic rule) — after the first real `heatmap_walk` export showed Mono keeping float intermediates at higher precision | — | evaluator/generator follow; a re-export is needed before the real-city three-way check can pass |
| 2026-09-05 | **S1 heatmap terms are walking times over the pedestrian network** (`SuitabilityWalkAccess`; Burst job `SuitabilityJob` deleted; residents per home building replace the 224 m population raster; access = network node within 2 min; catchments 6/11/16 min as linear time kernels; transfer 3 min; road gate replaced by "has a node"; site refinement pass removed) | A1.1, A1.2, A1.4, A1.5, A1.6, A0.5, A0.6, Phase-3 values | new kind `heatmap_walk` with three-way bit-exact check; v1 `heatmap_grid`/`heatmap_point` evaluators retired or historical |
| 2026-09-05 | **S2 candidates are network nodes; separation is walking time ≥ stop spacing** (`SolveOnNetwork`, ball clique-cover bound) | A2.1, A2.2, A2.4 | new kind `sites_walk`: exact Dijkstra conflicts, SCIP/VIPR on pairwise MIP, exact selection judged |
| 2026-09-05 | **S2 site selection is exact** (`SuitabilityExactSites`, branch-and-bound with block-partition bound, integer-scaled scores, node budget 2·10⁶ with reported ceiling); the greedy ranking stays as incumbent and measured baseline | A2.3 | subject reports `exact_*` fields; `run.py` requires gap 0 against the certified optimum when the search closed, else a sound bracket — 6/6 instances closed (two real cities: 14 and 0 nodes) |
| 2026-09-05 | **S7 selects the line set exactly under the passenger-time objective** (§6.2–6.3: zone-node routing, before/after door-to-door, saved = Σ w·(before−after), lexicographic with the capped equity share, utilisation and duplicate feasibility on the set, branch-and-bound with the monotone union bound, node budget → optimum or best + ceiling; greedy rounds, `CreditLine`, the transfer discount/`TransferPenalty` setting, switch margin and the 150 m duplicate rule removed; utilisation floor default 15 % with the 4369 s game day) | A7.5, A4.1, A1.8, A3.1, A3.3, A4.2, A4.3, A4.4, A7.2, A7.4 | new kind `lineset_time` (subject = mod `Solve`; exact evaluator; complete or bounded enumeration over feasible subsets; key ties reported); v1 kind `lineset` and its evaluator removed |
| 2026-09-05 | **S5 stop plan** (§4 v2: candidates every 50 m, forced interchanges, doors within the access horizon, through-flow at the candidate, δ from the prefabs; exact DP of Σ w·max(0, H − t) − Σ through·δ under the σ/2 gap floor) replaces the v1 windows | A5.1, A5.2, A5.4, A5.5 | new kind `stop_plan` (subject DP vs exact optimum by enumeration/independent DP); `calling_points` kind removed; Lean `CallingPoints` theorems historical |
| 2026-09-05 | **S6 capacity ladder** (§5 v2: smallest mode not overloaded at the prefab headway; ≥ 3 stops; ride limits 30/35/30/60/45 min; one rail lattice with the train's track preference for metro too; ferries offered every journey; journeys as door-to-door pairs in the set objective) | A6.x, A4.6, A6.1, A4.5, A3.4, A0.5 | `mode_choice` kind rewritten (bit-exact ladder); `lineset_time` pairs are now journeys |
| 2026-09-04 | Interchange/coverage weight of another mode's stop = **vehicle capacity ÷ bus capacity from the loaded prefabs** (`TransitModes.CapacityWeight`), replacing the table 1/1.2/1.5/2.5/3; a type without a loaded vehicle weighs 0 | A1.10 | heatmap `w_b32` remain instance data; new pure function unit-tested |
