# Scientific model review

The mod's algorithms measured against the peer-reviewed literature. Every claim
carries its source; claims that could not be traced to a primary source are marked
UNVERIFIED and are not relied on anywhere in the verification. Statements about the
mod cite `docs/formal-specification.md` (spec §) for the implementation detail.

Legend per entry: **Result** (what the source shows) · **Assumptions** ·
**Transfer** (applicability to Cities: Skylines II and this mod).

## 1. Problem classification and hardness

The mod's route suggestion is a Transit Route Network Design / Line Planning Problem
with freely constructed lines, transfer-aware passenger assignment, and per-line
fixed costs. Every relevant sub-problem is NP-hard:

- **Line planning with freely constructed lines is NP-hard; even its LP relaxation
  is NP-hard.** Borndörfer, Grötschel & Pfetsch, *Transportation Science* 41(1),
  2007, DOI 10.1287/trsc.1060.0161 (Prop. 4.1; pricing = longest path).
  *Assumptions*: lines are simple paths built freely, continuous frequencies.
  *Transfer*: the mod builds lines freely on the road graph and lattices — the
  hardness applies directly.
- **Transfer-objective line planning is NP-complete even on a single path.**
  Schöbel & Scholl, "Line Planning with Minimal Transfers", 2004/ATMOS'05,
  DOI 10.4230/OASIcs.ATMOS.2005.660. *Transfer*: even one corridor of the mod's
  transfer-aware objective is hard.
- **Non-pool line planning with per-line fixed cost is NP-hard even on paths and
  stars, and inapproximable within n^(1−ε) (d_fix = 0) unless P = NP.** Heinrich,
  Schiewe & Seebach, ATMOS 2022, DOI 10.4230/OASIcs.ATMOS.2022.8. *Transfer*: the
  mod's per-line vehicle overhead is the fixed-cost regime; no polynomial algorithm
  class carries a worst-case guarantee for this objective family.
- **Stop covering with continuous placement along lines is NP-hard; a
  polynomial-size finite dominating set reduces it to discrete covering.** Hamacher,
  Liebers, Schöbel, Wagner & Wagner, ATMOS 2001, DOI 10.1016/S1571-0661(04)00162-8.
  *Transfer*: justifies the mod's discrete stop candidates (32 m tiles, spacing
  windows) in principle.
- **MCLP is NP-hard on networks** (Megiddo, Zemel & Hakimi, DOI 10.1137/0604028);
  **p-median is NP-hard even on planar graphs of max degree 3** (Kariv & Hakimi,
  DOI 10.1137/0137041). Vertex-optimality of p-median: Hakimi 1964/65,
  DOI 10.1287/opre.12.3.450 and 10.1287/opre.13.3.462; first IP: ReVelle & Swain
  1970, DOI 10.1111/j.1538-4632.1970.tb00142.x.

Consequence for verification: heuristics are scientifically justified; *guarantees*
are not available for free. Optimality can only be established per instance, by an
exact solve with certificate — which is what the pipeline does.

## 2. The greedy acceptance rounds

- **Greedy achieves ≥ 1 − 1/e of the optimum for monotone submodular objectives
  under a cardinality constraint** (Nemhauser, Wolsey & Fisher 1978,
  DOI 10.1007/BF01588971; precursor Cornuéjols, Fisher & Nemhauser 1977,
  DOI 10.1287/mnsc.23.8.789), and **no polynomial algorithm can do better for max
  k-cover** (Feige 1998, DOI 10.1145/285055.285059).
- **Applicability to the mod**: if the objective were "demand newly walk-covered by
  the line's stops", the guarantee would hold. The mod's actual objective — an OD
  pair counts only when the transit graph routes it, with transfer discounts — is
  monotone but **not submodular in general**: two lines that individually serve
  neither end of a pair can serve it jointly via a transfer (complementarity). The
  (1 − 1/e) bound therefore does **not** transfer; this is an assessment from the
  cited theorems' preconditions, not itself a cited theorem. Any claim that the
  mod's rounds carry an approximation guarantee is unsupported.
- **Sequential/greedy staging is in general unboundedly suboptimal**: the "price of
  sequentiality" is unbounded even for two chained LPs, and bounded only in special
  structures (matroids, acyclic shortest paths). Schiewe & Schöbel, *EURO J.
  Transportation and Logistics* 11:100073, 2022, DOI 10.1016/j.ejtl.2022.100073.
  *Transfer*: the mod's pipeline (alignment → stops → mode → greedy line selection)
  is a sequential process in exactly this sense; a joint-optimum guarantee cannot
  exist structurally, which is why claim C7.5 is settled by counterexample rather
  than argument.
- The mod itself claims none of this (README: "Selection is greedy … These are
  suggestions, not optimal networks") — the claim inventory holds the mod to what it
  says.

## 3. Stop spacing and per-stop cost

- **Interior optimum spacing exists; it grows with vehicle speed and per-stop cost,
  shrinks with walk speed** — Wu & Levinson, *EJTIR* 21(2), 2021,
  DOI 10.18757/ejtir.2021.21.2.4794 (open access). Classical corridor results:
  Vuchic & Newell 1968, DOI 10.1287/trsc.2.4.303 (optimal spacing varies along the
  line); Vuchic 1969, DOI 10.1287/trsc.3.3.214 (patronage-optimal ≠ time-optimal —
  the optimum depends on the objective); Wirasinghe & Ghoneim 1981,
  DOI 10.1287/trsc.15.3.210 (many-to-many demand; skipping empty stops permits
  closer nominal spacing).
- **Stop placement along one fixed alignment is exactly solvable** (a 1-D problem;
  dynamic programming on a real route in Furth & Rahbee 2000, DOI 10.3141/1731-03) —
  so while joint network design is NP-hard, the per-line placement step can be
  certified optimal per instance against a declared objective. The mod's S5 is a
  procedure without a declared objective; the reference pipeline can nevertheless
  bound it against the DP optimum of a stated objective.
- **Empirical anchors**: US bus spacing measured mean ≈ 352 m traversal-weighted
  (Devunuri et al., *J. Public Transportation* 26:100083, 2024,
  DOI 10.1016/j.jpubtr.2024.100083); optimization on a real route gave ≈ 400 m vs
  200 m existing (Furth & Rahbee, *TRR* 1731, 2000, DOI 10.3141/1731-03); TCRP
  Report 19 (1996) tables: CBD 90–305 m, urban 152–366 m, suburban 183–762 m.
  Metro: measured mean interstation ≈ 1.1 km over 14 world systems, range
  0.57–1.79 km (Roth, Kang, Batty & Barthélemy, *J. R. Soc. Interface* 9(75), 2012,
  DOI 10.1098/rsif.2012.0259). Consolidation evidence: ≈ 6 % running-time gain, no
  ridership loss (El-Geneidy et al., *TRR* 1971, 2006,
  DOI 10.1177/0361198106197100104).
- **Per-stop time cost, composed from primary sources** (no single "20–30 s" source
  exists): bus ≈ 10 s accel/decel (TCQSM 3rd ed., TCRP Report 165, 2013,
  DOI 10.17226/24766) + dwell ≈ 5 s + 2.75 s per passenger (Levinson, *TRR* 915,
  1983); rail ≥ 25 s + ≈ 45 s dwell incl. margin (TCQSM). Buses actually stop at
  only 68–78 % of designated stops (Levinson 1983) — which matches both CS2
  behaviour and the mod's window-skipping.
- **Verdict on the mod's tables** (`TransitModes.StopSpacingFor`: bus 350, tram 450,
  metro 800, train 2000, ferry 1200 m): bus 350 m sits on the measured US mean and
  slightly below the optimization result — defensible. Metro 800 m sits at the low
  end of the measured world range — defensible. Tram 450 m has **no primary
  anchor** (only the theoretical ordering bus < tram < metro). Ferry 1200 m has
  **no literature anchor at all** — no ferry stop-spacing literature was found.
  Train 2000 m is city-rail-like; world heavy-rail spacings are wider; no direct
  guideline source. These are model parameters, not derived values, and the claims
  inventory treats them as such. The mod's *even-interval* spacing is not the
  theoretical optimum (which varies along the line, Vuchic & Newell); its
  score-based window-skipping is a crude approximation of the spatially varying
  optimum and is directionally supported by Wirasinghe & Ghoneim.

## 4. Generalized cost: walk, wait, transfers

- **Walk ≈ 2.0×, wait ≈ 2.5× in-vehicle time** — Wardman, *Transport Policy* 11(4),
  2004, DOI 10.1016/j.tranpol.2004.05.001 (concluding recommendation). The mod
  weights walk/wait/ride all 1:1:1 in its routing costs (spec §6.1) — mirroring the
  game's own pathfinder rather than the valuation literature; a deliberate
  simulation-fidelity choice, recorded as a divergence from empirical valuations.
- **Wait = headway/2** holds for random arrivals at regular headways; observed waits
  run ≈ 30 % below it in mixed arrival regimes (Jolliffe & Hutchinson 1975,
  DOI 10.1287/trsc.9.3.248), and ≈ 11 min headway marks the switch to timed
  arrivals (Fan & Machemehl, *TRR* 2111, 2009, DOI 10.3141/2111-19). The mod uses
  max(interval/2, observed) − dwell, mirroring the game (spec §6.1); beyond ~11 min
  headways the H/2 component overstates real-world waits — irrelevant to fidelity
  with the game's own pathfinder, relevant to realism claims.
- **Transfer penalties**: pure penalty ≈ 4.9 IVT-min within-mode at good stations
  (Guo & Wilson, *TR-A* 45(2), 2011, DOI 10.1016/j.tra.2010.11.002); ≈ 15–18
  IVT-min multimodal (Garcia-Martinez et al., *TR-A* 114, 2018,
  DOI 10.1016/j.tra.2018.01.016); survey: Iseki & Taylor, *Transport Reviews*
  29(6), 2009, DOI 10.1080/01441640902811304. The mod charges a boarding cost of
  wait + 5 s per boarding (the game's own StartingCost) in routing, plus a
  multiplicative credit discount (default 0.6 per transfer) in *scoring*. The
  scoring discount is a preference parameter, not an empirical valuation; at
  typical in-game journey times it is broadly of the empirical penalty's magnitude
  but is not derived from it.
- **Operator side**: optimal frequency ∝ √demand (Mohring, *AER* 62(4), 1972 —
  square-root rule for *frequency*; a square-root *spacing* formula is UNVERIFIED
  as a published theorem). The mod sizes fleets by the game's own formula
  round(roundTrip/interval) — simulation fidelity again.

## 5. Accessibility and the demand model

- The mod's demand term is a Hansen-type demand-weighted accessibility (Hansen,
  *JAPA* 25(2), 1959, DOI 10.1080/01944365908978307) with a triangular distance
  kernel, composed with routing feasibility (the served-demand discount). No single
  canonical literature measure matches that composition exactly; Geurs & van Wee
  (*J. Transport Geography* 12(2), 2004, DOI 10.1016/j.jtrangeo.2003.10.005) is the
  citation for preferring demand-weighted measures over infrastructure counts, and
  Owen & Levinson (*TR-A* 74, 2015, DOI 10.1016/j.tra.2015.02.002) for
  frequency-aware access predicting ridership.
- The usual OD-uncertainty caveat of the literature (fixed estimated matrices,
  Borndörfer et al. §3) largely does not apply: the mod reads the true citizen OD
  population from the save. School-trip weight 0.6 is a model assumption with no
  cited source.

## 6. Weighted-sum scoring and Pareto completeness

- **Weighted sums cannot reach non-convex Pareto points, and even weight sweeps
  spread unevenly** — Das & Dennis, *Structural Optimization* 14:63–69, 1997,
  DOI 10.1007/BF01197559. Convex counterpart: Geoffrion 1968,
  DOI 10.1016/0022-247X(68)90201-1.
- *Transfer*: the mod's 7-term suitability score and its ridership/coverage
  objective blend are weighted sums; the integer selection problems' attainable
  sets are non-convex, so **some Pareto-optimal stop sets / line plans are
  unreachable for every weight setting**. The verification treats the weights as
  given (they are player preferences), reports the certified optimum *for the
  weighted objective*, and notes that ε-constraint sweeps would be needed to
  enumerate the full front (the pipeline implements a bounded ε-constraint sweep on
  small instances for exactly this demonstration).

## 7. What exact methods achieve (instance-size reality)

- City-scale exact line planning reaches **LP bounds, not certified integer
  optima**: Potsdam, 951 nodes / 1321 edges / 4685 OD pairs — LP relaxation solved,
  integer solutions heuristic with ~42 % gap to a weak LP bound (Borndörfer et al.
  2007 §5).
- **Joint exact TNDP with transfers is at ~15-node benchmark scale** (Mandl):
  Gao, Ma, He & Dong, *EJOR* 334(1):111–127, 2026, DOI 10.1016/j.ejor.2026.04.020
  (branch-and-cut + ε-constraint Pareto front; larger instances only for *adding*
  routes to an existing network — structurally the mod's per-round problem).
  Branch-and-cut for cost-oriented rail line planning: Goossens, van Hoesel &
  Kroon, *Transportation Science* 38(3), 2004, DOI 10.1287/trsc.1030.0051.
  Surveys agreeing on the exact-vs-metaheuristic split: Guihaire & Hao 2008,
  DOI 10.1016/j.tra.2008.03.011; Kepaptsoglou & Karlaftis 2009,
  DOI 10.1061/(ASCE)0733-947X(2009)135:8(491); Ibarra-Rojas et al. 2015,
  DOI 10.1016/j.trb.2015.03.002; Durán-Micco & Vansteenwegen 2022,
  DOI 10.1007/s12469-021-00284-y.
- *Transfer*: the pipeline certifies (i) the site-selection subproblem at full size
  (a pure MILP), and (ii) line-set optimality by complete enumeration on bounded
  instances — full joint certification at city scale is beyond the published state
  of the art and is not attempted.

## 8. Verification tooling

- **VIPR certificates**: Cheung, Gleixner & Steffy, "Verifying Integer Programming
  Results", IPCO 2017, DOI 10.1007/978-3-319-59250-3_13; reference checker
  `viprchk` (github.com/scipopt/vipr); a formally verified checker exists in
  HOL4/CakeML. Certificates cover branch-and-cut on rational data *after*
  presolving.
- **Exact rational MIP**: Eifler & Gleixner, *Mathematical Programming*, 2022,
  DOI 10.1007/s10107-021-01749-5. **SCIP Optimization Suite 10.0** (release report,
  arXiv:2511.18580) ships exact solving officially: `exact/enabled = TRUE`,
  VIPR output via `certificate/filename`, MILP only, ≈ 3–10× slowdown, certificate
  overhead ≤ solve time. SCIP 9 does *not* contain the feature.
- *Transfer*: the mod's float scores must be explicitly rationalized before a
  certificate means anything (binary32 → exact rational is lossless; the
  rationalization step is part of the audited pipeline).

## 9. Unverified items (not relied upon)

Carried verbatim from the research so nobody re-chases them: Quak (2003) thesis
content; Das & Kempe submodularity-ratio citation; (H/2)(1+CV²) attribution;
Mohring's exact formula at primary level; Wirasinghe–Ghoneim closed form; "bus
300–500 m" / "metro 800–1200 m" as stated guidelines; any tram/ferry spacing
recommendation; "20–30 s per stop" as a single figure; standalone marginal money
cost per stop; Goossens et al. internal instance sizes; van Nes & Bovy specific
meter optima. None of these appears as evidence anywhere in
`docs/correctness-claims.md`.

## 10. Time-based walking access and equity (added 2026-09-04, for the redesign review)

**Walking access as time, not radius.** The 400 m bus / 800 m rail service-coverage
standard is TCQSM's (TCRP Report 165, 2013, ch. 4–5, DOI 10.17226/24766), stated as
5/10 minutes at 3 mi/h; TCQSM itself notes ~75 % of local-bus riders walk ≤ 400 m and
that rail walks are at least double. Measured network walks (home end): Montreal 85th
percentile bus 484 m, metro 873 m, commuter rail 1 259 m (El-Geneidy et al.,
*Transportation* 41, 2014, DOI 10.1007/s11116-013-9508-z, Table 2); Sydney medians bus
364 m, train 749 m (Daniels & Mulley, *JTLU* 6(2), 2013). Euclidean buffers over-count
coverage — Madrid metro by up to 57.5 % (Gutiérrez & García-Palomares, *EPB* 35, 2008,
DOI 10.1068/b33043); TCQSM's connectivity factors are 1.00 grid / 0.85 hybrid / 0.45
cul-de-sac. Measured circuity 1.18–1.24 (O'Sullivan & Morrall, TRR 1538, 1996,
DOI 10.1177/0361198196153800103; Levinson & El-Geneidy, *RSUE* 39, 2009). Walking
speed: 1.2 m/s planning value, 1.0 m/s with ≥ 20 % elderly (TCQSM ch. 5; FHWA-RD-98-107);
physiological comfortable speeds 1.27–1.46 m/s (Bohannon, *Age and Ageing* 26, 1997,
DOI 10.1093/ageing/26.1.15). Perceived time weights: walk 2.2, initial wait 2.1,
transfer time 2.5 × in-vehicle (TCQSM Exhibit 4-5). *Transfer:* the mod's Euclidean
catchments (A1.2) should become network walking times; the finest raster in this
literature is 30 m, so the 32 m tile is defensible as display resolution.

**Equity as an objective.** Horizontal vs vertical equity: Litman, VTPI *Evaluating
Transportation Equity*. Gini of transit supply: Delbosc & Currie, *J. Transport
Geography* 19(6), 2011, DOI 10.1016/j.jtrangeo.2011.02.008 (Melbourne G = 0.68) — scale-
invariant, hence a diagnostic, not a target. Theil (1967) decomposes between/within
groups; Atkinson (*J. Econ. Theory* 2, 1970, DOI 10.1016/0022-0531(70)90039-6) tunes
inequality aversion up to Rawlsian. Rawlsian/limited-gap: Martens, *Transportation* 39,
2012, DOI 10.1007/s11116-012-9388-7; Martens, Golub & Robinson, *TR-A* 46, 2012,
DOI 10.1016/j.tra.2012.01.004. Sufficientarian minimum standards: van Wee & Geurs,
*EJTIR* 11(4), 2011; Lucas, van Wee & Maat, *Transportation* 43, 2016,
DOI 10.1007/s11116-015-9585-2; Pereira, Schwanen & Banister, *Transport Reviews* 37(2),
2017, DOI 10.1080/01441647.2016.1257660. Equity inside network design as a constraint:
Camporeale et al., *TR-A* 125, 2019, DOI 10.1016/j.tra.2018.04.006; frameworks per
justice theory: Behbahani et al., *TR-A* 125, 2019, DOI 10.1016/j.tra.2018.04.005.
Price of fairness bounds (proportional vs max-min): Bertsimas, Farias & Trichakis,
*Operations Research* 59(1), 2011, DOI 10.1287/opre.1100.0865. Method: ε-constraint /
AUGMECON (Mavrotas, *Appl. Math. Comput.* 213, 2009, DOI 10.1016/j.amc.2009.03.037).
*Transfer:* a sufficientarian floor on time-based access as an ε-constraint over the
existing efficiency objective fits the user's stated goal literally and yields a
well-defined problem against which S2/S7 optimality can be certified.

**UNVERIFIED (not relied on):** Untermann's walking-distance percentages; any primary
source for an "acceptable transfer walk of 2–3 minutes"; the specific inequality index
used by Camporeale et al.; Zhao et al. 2003 numeric thresholds.

## 11. Line health: fleet sizing, load standards and monitoring (added 2026-09-06, for S9 v2)

What S9 now computes and what the literature says about each piece. Marked (V) only
where the primary text was read; everything else is stated as "practice" and carries
no weight in `docs/correctness-claims.md`.

**Fleet from cycle time and headway.** The identity the game itself uses — vehicles =
round trip ÷ interval — is the textbook fleet-size relation N = T_cycle / h (Vuchic,
*Urban Transit: Operations, Planning and Economics*, Wiley 2005, ch. 2; Ceder, *Public
Transit Planning and Operation*, Elsevier 2007 / CRC 2016, ch. 6–7). The mod does not
derive it: it mirrors `TransportLineSystem.CalculateVehicleCount` (V, decompiled) and
proves the derived fleet rule minimal (Lean CF.8).

**Frequency from the peak load.** Setting a line's frequency so that the load at the
busiest point does not exceed a design load is the "max load" method of Furth & Wilson
("Setting Frequencies on Bus Routes: Theory and Practice", *Transportation Research
Record* 818, 1981) and Ceder ("Bus frequency determination using passenger count data",
*Transportation Research Part A* 18(5–6), 1984, doi 10.1016/0191-2607(84)90019-0). S9's
`v_load = ⌈L / (0.7·c)⌉` is that rule with L the fleet-wide planning load; the 0.7 is a
judgement (register A8.3) standing in for the design load factor, which TCQSM (TCRP
Report 165, 2013, ch. 5) treats as an agency policy value — commonly seated load for
long trips and up to crush load for short peak trips — not a constant. The fleet-wide
count aboard is not the max-load-point count; the 0.7 is also what absorbs that gap.

**A quantile as the planning statistic.** Sizing to a high percentile of observed loads
rather than to the single maximum is standard monitoring practice (TCQSM's peak-15-minute
loads; agency load standards are stated as shares of trips exceeding a load) but the 90th
percentile here is the user's decision (2026-09-06, question 4b), not a literature value.

**Occupancy vs. throughput utilisation.** Two quantities carry the word "utilisation" in
this mod and the register keeps them apart: occupancy (riders aboard ÷ seats, a snapshot
— what the game's own UI shows) drives the relative "empty" bar; throughput utilisation
(boardings a day ÷ seats offered a day) drives the floor, the ladder and the schedule.
TCQSM's load factor is the former, its productivity measures (boardings per revenue
hour) the latter.

**Waiting time.** The v1 "long waits" verdict rested on the game's `m_VehicleInterval`,
which turned out (decompiled 2026-09-06) not to be a measurement; the verdict is
retired. The literature's headway-based waiting models — E[W] = h/2 for random arrivals
at short headways (Welding 1957; Osuna & Newell 1972, *Transportation Science* 6(1)),
(h/2)(1 + CV²) under irregular headways — would need observed vehicle arrivals
(`VehicleTiming` at the waypoints), which the mod does not read yet.

**Relative thresholds.** The upper-median bar has no literature source; its one
provable property — it can flag at most half the lines — is CF.9.

