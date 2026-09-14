using System;

namespace WhereTheyGo
{
    // Every number the mod computes with, in one place (user decision 2026-09-06): a
    // value that lives in one file cannot drift between two. Each entry carries the
    // register row it implements (docs/assumptions-register.md) or the game type it
    // mirrors; the comments were moved here with the values. Per-mode tables live at
    // the end. Nothing here is a UI bound or a wire-format version — those stay with
    // the settings page and the serializers, because they are not assumptions about
    // the city.
    internal static class Assumptions
    {
        // ---- The game clock
        public const float NightStart = 11f / 12f;

        public const float NightEnd = 0.25f;

        // 16 of 24 hours are day.
        public const float DayShareOfDay = 16f / 24f;

        public const float EveningShiftOffset = 0.33f;

        public const float NightShiftOffset = 0.67f;

        // A game day in the seconds vehicles and headways are measured in: the
        // simulation runs 60 ticks per real second at speed 1 (decompiled
        // Game.Simulation.SimulationSystem) and TimeSystem.kTicksPerDay ticks make a
        // day, so a "24-hour" day holds 4 369 s of movement — not 86 400. Every headway,
        // ride time and journey count in this mod lives on that clock; a formula that
        // assumed real-world hours under-read every line's utilisation twentyfold.
        public const float MovementSecondsPerGameDay = FramesPerGameDay / 60f;

        // Game.Simulation.TimeSystem.kTicksPerDay. One simulation frame is one tick,
        // so a game day is this many frames whatever speed the player is running at.
        public const uint FramesPerGameDay = 262144u;

        // ---- Grids and lattices
        // Zones are much coarser than the score grid: they exist to make the flow
        // assignment tractable, not to be looked at.
        public const float ZoneSize = 256f;

        // Nodes further apart than this from a zone centre are not considered that
        // zone's access point — a zone with no road near its middle simply does not
        // participate in the assignment.
        public const float ZoneSnapRadius = ZoneSize;

        // Lattice pitch. Coarse enough that a city-sized map stays a few thousand
        // nodes, fine enough that a corridor still bends around obstacles.
        public const float LatticeSpacing = 128f;

        // How many of the strongest tiles the combine pass names in the log. Three is
        // enough to tell "the whole map is bright" from "one spot is", and short enough
        // to stay one line.
        // How far the pedestrian network may be bridged across a gap the game's data
        // leaves in it (WalkBridging). 50 m stitched Valmare's 582 pieces into 3, and is
        // about as far as a person walks in a straight line without a pavement; wider,
        // and the graph starts stepping over canals.
        public const float WalkBridgeMetres = 50f;

        // Each pass can only join a component to one neighbour, so a chain of fragments
        // needs several. Ten is far more than a real map has used.
        public const int WalkBridgePasses = 10;

        // How much further than the equity horizon the served-walk search looks, so the
        // buildings beyond it can be told how far they actually are. Purely a reporting
        // range: every "is this served" test still compares against the horizon.
        public const int AccessFieldHorizonMultiple = 4;

        // How far the transit-access field may carry a walk time onto tiles that have no
        // pedestrian node of their own: three tiles, about 96 m, which is a house to its
        // street and no further. The field is what colours the buildings, and a building
        // is not on the pavement its residents walk from.
        public const int AccessFieldSpreadTiles = 3;

        public const int TopCellsLogged = 3;

        public const float TileSize = 32f;

        // ---- Walking
        // Walking-time horizons of the access model (register A1.1/A1.2, decided
        // 2026-09-05). Catchment: how long a rider walks to a stop of the mode —
        // TCQSM's 400 m bus / 800 m rail standard read as time, stretched to the
        // measured 85th percentiles (El-Geneidy et al. 2014: bus 484 m, metro 873 m,
        // commuter rail 1 259 m at 1.2 m/s ≈ 6 / 11 / 16 min); ferry like metro, no
        // source of its own. Access: the straight-line walk from a door to the
        // pavement network beyond which a point is treated as off-network. Transfer:
        // how long a rider walks to change vehicle (A1.11).
        public const int AccessWalkMs = 120_000;

        public const int TransferWalkMs = 180_000;

        // How far a rider will walk to reach or change service.
        // How long a rider will walk to change vehicle: three minutes at the planning
        // walking speed (register A1.11; the literature gives transfer TIME weights,
        // no distance threshold — TCQSM Exhibit 4-5). Metres follow from the speed.
        public const float TransferWalkSeconds = TransferWalkMs / 1000f;

        public const float TransferWalkRadius = WalkSpeed * TransferWalkSeconds;

        // How far a zone's centre may be from a stop for that stop to serve it. A zone
        // is 256 m across, so its centre is further from a stop than its edges are, and
        // the plain transfer radius left most zones unserved.
        public const float ZoneStopReachMetres = TransferWalkRadius * 2f;

        // Walking is slow enough that a long connection is worse than a detour by
        // vehicle, which is what keeps interchanges local.
        // 1.2 m/s is the planning value (TCQSM 3rd ed. ch. 5; FHWA-RD-98-107), not the
        // brisk 1.4 the routing used before — register decision, 2026-09-04.
        public const float WalkSpeed = 1.2f;

        // Flat cost of boarding, on top of the wait. Matches TransportPathfind's
        // m_StartingCost time component (5), so a change of vehicle costs what the
        // game itself charges for one. There is no separate transfer penalty in
        // vanilla — a transfer is simply a second boarding — so modelling boardings
        // is modelling transfers.
        public const float DefaultBoardPenaltySeconds = 5f;

        // ---- S1 heat map
        // A tile counts as water when the surface is deeper than this. Shallow
        // puddles and shoreline wash should not carve up the walkable landmass.
        public const float WaterDepthThreshold = 0.5f;

        // Ferry stops want the shoreline, so they accept shallow water and require
        // proximity to it; this is how far a land tile may be from water and still
        // count as shoreline.
        public const float FerryShorelineDepth = 0.1f;

        // Demand, jobs and future demand are raw sums with unbounded scale; each is
        // normalized against this percentile of its own positive values so all five
        // weighted terms are comparable 0..1 quantities.
        public const float TermCapPercentile = 0.98f;

        public const float IntensityGamma = 0.6f;

        // Ceiling on the same-mode coverage term: three fully covering stops is as
        // "already served" as a tile gets.
        public const float MaxCoveragePenalty = 1.5f;

        // ---- Journeys
        // How many game days of shopping and leisure journeys the observation keeps
        // (author's decision 2026-09-14). Three rather than one: the hour-by-hour
        // picture a single day gives jumps about, because an hour of one day is a few
        // hundred observations. Averaging three days steadies it at three times the
        // memory.
        public const int ObservationWindowDays = 3;

        public const uint ObservationWindowFrames = FramesPerGameDay * ObservationWindowDays;

        // Backstop against a player leaving the game running for days at speed; far
        // above what the window's days of a large city produce.
        public const int ObservedTripCapacity = 200_000 * ObservationWindowDays;

        // ---- Desire bands (the map's main layer)
        // Two zone pairs are the same corridor when BOTH their ends lie this close
        // together. Raised from 250 m on 2026-09-14: at the zone grid's own 256 m
        // pitch almost nothing merged beyond what the grid had already merged, and
        // the map drew hundreds of near-duplicate corridors on top of each other.
        public const float BandMergeMetres = 450f;

        // Bands drawn at most. A real city settles well under this; a map with tens of
        // thousands of zone pairs would otherwise draw until the frame died. What does
        // not fit is counted and logged, never quietly dropped.
        public const int MaxBands = 400;

        // Band widths in metres on the ground, between the lightest band drawn and the
        // heaviest in the city (BandGeometry.Width, on the square root between them).
        // 110 m was a blob at any zoom a player actually uses.
        public const float BandMinWidthMetres = 5f;

        public const float BandMaxWidthMetres = 48f;

        // How far a band bows out of the straight line, as a share of its length and
        // capped in metres, so a cross-city band does not swing out over the sea. Just
        // enough to separate two bands between the same districts; more than this and
        // a band stops reading as a connection between its two ends.
        public const float BandBowShare = 0.06f;

        public const float BandBowMaxMetres = 250f;

        // Bands under this share of the heaviest band are hidden until the player
        // moves the panel's slider. Five percent leaves the corridors and drops the
        // hair; two showed every one of the four hundred at once.
        public const int BandThresholdDefaultPercent = 5;

        // How long an observed journey's maker stays before travelling back, by
        // purpose. Only the RETURN hour rests on it: the outbound hour is the clock
        // the journey was actually seen at. Shopping is an errand, leisure an outing.
        public const int ShoppingStayHours = 1;

        public const int LeisureStayHours = 2;

        // The per-day scale stops growing once the window is shorter than this
        // fraction of a day, so six minutes of readings cannot be multiplied into a
        // full day's demand. Reported by ScaleFor; the caller logs it.
        public const int ObservedTripMaxDayScale = 4;

        // Journeys longer than this are not realistically made by transit. Also the
        // routing cap, and the fallback ceiling for the served-demand discount when
        // the network carries too little for a median to mean anything.
        public const float MaxJourneySeconds = 3600f;

        // A journey taking this many times the city's typical transit journey is not
        // carried in any useful sense, so it keeps its whole weight.
        public const float ServedCeilingMultiple = 3f;

        // Below this many carried journeys the median is noise, and the fixed hour is
        // the more honest reference.
        public const int MinPairsForServedMedian = 20;

        // ---- S4 alignments
        // Bow below which an edge is drawn as a straight chord.
        public const float StraightEnough = 3f;

        public const int MaxCurveSamples = 8;

        // How much a corridor prefers to carry straight on.
        //
        // Growth picks the best adjacent edge on flow and novelty alone, and on a
        // lattice — a 128 m grid where flow is spread thin and nearly uniform — the
        // tiniest difference between two edges steers it. The result wandered across
        // the whole city in a staircase, which is not an alignment anyone would build
        // and not something Ramer-Douglas-Peucker can straighten afterwards: the
        // corridor genuinely went that way.
        //
        // A real line continues along the street or the alignment it is on and turns
        // only for a reason. At 0.6 a right-angle turn keeps 70% of its score and a
        // reversal 40%, so a genuinely busier direction still wins — this is a
        // preference, not a constraint.
        public const float TurnPenalty = 0.6f;

        // How much a corridor prefers to be GOING somewhere.
        //
        // The turn penalty is local: it stops a staircase but says nothing about the
        // shape overall, and a corridor can carry smoothly round a long arc back to
        // where it started. On a lattice that is exactly what happened — a metro was
        // proposed as a box around an empty field, and another as a ring around the
        // whole city.
        //
        // The cause is that a lattice is a UNIFORM grid, so the shortest path between
        // two zones is degenerate: hundreds of staircases cost the same, and which one
        // Dijkstra picks falls out of the order edges were added. The flow those paths
        // accumulate forms ridges that are an artifact of the grid rather than of where
        // anyone travels, and growth follows them faithfully.
        //
        // A line connects two places. Every extension is therefore weighed on whether
        // it takes this end FURTHER from the other one: at 0.7 an extension that curls
        // back keeps 30% of its score and one heading straight out keeps all of it.
        public const float SpreadPenalty = 0.7f;

        // A line whose ends are closer together than this share of the distance it
        // travels is a ring, not a route. The spread bias only helps where growth had
        // an alternative; on a corridor with nowhere else to go it still comes round,
        // and this is what catches that.
        public const float MinDirectness = 0.45f;

        // How much longer an alignment may become to pass through an interchange.
        //
        // A judgement, and stated as one: every rider already on the line pays the
        // detour in minutes, while only those changing vehicle collect the benefit, so
        // the bound is about what the majority will tolerate rather than about what the
        // hub is worth. A quarter again is roughly the point at which a bend stops
        // reading on a map as "the line goes past the station" and starts reading as
        // "the line goes out of its way".
        //
        // Deliberately NOT scaled by how many modes the hub offers. A better hub is a
        // reason to prefer one via over another — which is what the ranking does — not
        // a reason to make the people on board travel further for it.
        public const float MaxViaDetour = 1.25f;

        // Consecutive quiet nodes a corridor may cross before giving up. Two is a
        // park, a river, a rail crossing or an industrial strip — the things that sit
        // between two busy districts — and not a licence to strike out into open
        // country, which is what the gate exists to prevent.
        public const int DefaultLowDemandBridge = 2;

        // How heavily a crossing is outranked by an extension into somewhere with
        // people. Low enough that a bridge is only ever taken when it is the only
        // thing on offer.
        public const float LowDemandBridgePenalty = 0.05f;

        // Fraction of a corridor's demand a single line is taken to satisfy. Below 1
        // so a genuinely enormous corridor can still justify a second line.
        public const float CaptureFraction = 0.85f;

        // How far novelty suppression spreads once a corridor is chosen, in graph
        // hops, and how hard it bites at the corridor itself.
        public const int NoveltyHops = 3;

        public const float NoveltyFactor = 0.15f;

        // Corners closer than this to the straight line between their neighbours are
        // lattice artefacts rather than real alignment.
        public const float SimplifyTolerance = 120f;

        // How far a line's endpoint may be from a road node when re-tracing it, and how
        // long the re-traced path may be.
        public const float RetraceSnapMetres = 600f;

        public const float RetraceMaxPathMetres = 30000f;

        // Share of a candidate's stops that must already have service on the same
        // alignment before it counts as a line the player has already built.
        public const float DuplicateStopShare = 0.75f;

        // How far off an alignment an interchange may sit and still be worth bending
        // towards. Generous on purpose: the real bound is the detour
        // (GraphMath.IsDetourWorthwhile), and it scales with the line, which
        // this cannot. Reaching a hub 2 km to one side costs about 4 km of extra
        // running, so a quarter-again detour only pays for it on a line already 16 km
        // long — short lines rule out far hubs on their own, without a second constant
        // that would have to be kept in step with the first.
        public const float ViaReachMetres = 2000f;

        // How far the lattice node standing in for the hub may be from the hub itself.
        // The lattice has a 128 m pitch, so the nearest node to a station is not the
        // station; past a transfer walk it is not an interchange either.
        public const float ViaSnapMetres = 250f;

        // Every this many nodes along the alignment, look sideways for a hub. At the
        // 128 m lattice pitch that is a look every half kilometre, which cannot miss
        // anything inside a 2 km reach.
        public const int ViaSampleStride = 4;

        public const float InterchangeReachMetres = 500f;

        // What each network's corridor growth is allowed to consider: the share of the
        // network's own mean edge flow an edge must carry to be eligible, and the
        // longest corridor that may be grown on it.
        //
        // The lattice fractions were once far too high for a lattice to produce a line
        // at all, which the growth diagnostics finally made visible: at 0.6 the train
        // floor stood at 256 against a mean edge flow of 426, and 226 of the refused
        // extensions were refused by that floor alone. Train corridors averaged 414 m
        // and every one of the fifteen died against the 4000 m minimum for a train.
        // Metro at 0.4 fared little better — 1684 m average against a 2000 m minimum,
        // so 15 of 19 were thrown away.
        //
        // The demand gate, the length floors and ChooseMode's own multiples of the
        // network reference all still apply downstream, so these widen what may be
        // CONSIDERED rather than what may be suggested.
        public const float RoadFlowFraction = 0.1f;

        // Normalized demand a corridor's next node must have beside it. Corridors must
        // serve somebody along their length, not merely carry through-traffic — that is
        // what stopped routes looping into empty land. Loosened from 0.02, where the
        // gate truncated corridors at the first thin block and made almost every one
        // too short to suggest.
        public const float CorridorDemandFloor = 0.005f;

        // ---- S4 directed roads
        // cos 15°, cos 45°, cos 120°, cos 165°: the class boundaries, as the exact
        // double literals the specification names.
        public const double CosGentle = 0.9659258262890683;

        public const double CosTurn = 0.7071067811865476;

        public const double CosSharp = -0.5;

        public const double CosUTurn = -0.9659258262890683;

        // Stops on the road network sit on or beside a road node; further than this
        // the stop is not on the street it was placed along.
        public const float StopNodeSnapMetres = 64f;

        // The stop-to-stop legs the last route pass asked the directed graph for, with
        // the answers it got: what the export hands the pipeline to certify. Bounded,
        // and reset whenever the graph is rebuilt so no leg outlives its graph.
        public const int MaxRememberedRoadLegs = 400;

        // ---- S5 stop plan
        // A line passing this close to an existing served stop calls AT it rather than
        // beside it. Any player would put the stop at the station; doing otherwise
        // leaves two stops a short walk apart and no reason for either.
        public const float StationCallMetres = 150f;

        // Candidate stop positions along an alignment are this far apart; the stop plan
        // decides which of them are called at. Fine enough that a door is never more
        // than half a step from the position that would serve it best.
        public const float CandidateStepMetres = 50f;

        // Hard floor between consecutive stops: half the mode's nominal spacing
        // (register A5.1, decided 2026-09-05).
        public const float MinGapShareOfSpacing = 0.5f;

        // Two stops closer than this are the same stop: the along-line search can land
        // consecutive placements on nearly the same spot.
        public const float MinStopSeparationMetres = 20f;

        // ---- S6 modes and fleets
        // Out and back: every journey the demand model knows boards twice a day.
        public const float RidesPerJourney = 2f;

        // Fallbacks for prefab facts the save does not carry (A5.5: the game's own
        // values are read at run time and logged; these only stand in for a missing
        // vehicle or line prefab).
        public const float DefaultStopDurationSeconds = 15f;

        public const float DefaultAcceleration = 1.5f;

        // Past this share of its seats a mode is overloaded and the next one up is
        // wanted (A6.x: the game's own capacities decide the mode). Also the ceiling
        // the fleet of a line is sized to on its daily boardings (A6.8): the smallest
        // fleet whose seats a day the boardings do not exceed.
        public const float MaxPlannedUtilisation = 1f;

        // A suggested line has at least this many stops: two stops are a shuttle, not a
        // service (register A4.6/A6.1, 2026-09-05).
        public const int MinStops = 3;

        // The share of its seats a vehicle is planned to fill at the busiest reading
        // (A8.3): the fleet of an existing line is the smallest that carries the
        // planning load at this fill, leaving the rest for the peaks the readings miss.
        public const float TargetLoad = 0.7f;

        // ---- S7 line set
        // Two candidates of one mode whose stops all lie within this distance of each
        // other are the same line (F6 DropIdenticalCandidates).
        public const float IdenticalCandidateStopMetres = 20f;

        // How close a candidate's stops must be to an existing line's for the two to
        // count as the same alignment.
        public const float DuplicateLineMatchMetres = 150f;

        public const long JourneyRoutingNodeBudget = 20_000;

        // Wall-clock budget for the line-set search on the worker. Past it the search
        // keeps the best set found and reports the open bound as the ceiling
        // (JourneyRouting.Solve); the log says which regime the result is in.
        public const int JourneyRoutingTimeBudgetSeconds = 15;

        // Riders of a line may already have an equally fast route without it: above this
        // share of them the line duplicates the set it sits in (register A4.3).
        public const float DuplicateRiderShare = 0.5f;

        // ---- S9 line health
        // Below this share of capacity a line is not carrying enough to justify itself
        // (A8.1). Occupancy is a snapshot of passengers aboard against fleet capacity,
        // and a healthy line sits well under half full most of the time — at 0.15 this
        // flagged 18 of 19 lines on a working city, which is noise rather than advice.
        public const float EmptyUsage = 0.06f;

        // A line is only "empty" if it is far below what this city's lines normally
        // carry (A8.1). Without the relative test a fixed threshold flags most of a
        // healthy network. Because the reference is the city's upper median, at most
        // half the lines can ever fall under this bar (Lean: Verify.LineHealth).
        public const float EmptyShareOfMedian = 0.35f;

        // How far above the empty threshold a line's BUSIEST reading may sit and still
        // count as empty (A8.1). A line that never reaches three times the bar even at
        // its peak is genuinely carrying nobody; one that does has a demand pattern, and
        // the answer to that is a timetable, not a demolition.
        public const float EmptyPeakAllowance = 3f;

        // The planning load of a line is this quantile (nearest rank) of the passengers
        // aboard over the window's active readings (A8.4, user decision 2026-09-06): the
        // single busiest reading is kept beside it for the display but sizes nothing.
        public const float PlanningLoadQuantile = 0.9f;

        // Readings are taken in GAME time, this many per game day (A8.5, 2026-09-06),
        // so the sample is the same at every simulation speed. 96 = one every 15 game
        // minutes; the frame gate below is the day divided by it.
        public const int ReadingsPerGameDay = 96;

        public const uint ReadingIntervalFrames = FramesPerGameDay / ReadingsPerGameDay;

        // Enough readings that one outlier cannot carry a verdict on its own. Below
        // this the caller is told to fall back to the instantaneous reading rather
        // than be handed a mean of two samples dressed up as a day's evidence.
        public const int MinReadingsForVerdict = 4;

        // Per line, so a player who leaves the game running overnight at high speed
        // cannot grow these without bound. At the default sampling cadence this is
        // far more than a day holds; it is a backstop, not a tuning knob.
        public const int LineReadingsCap = 512;

        // How far an existing line's endpoint may be from a node when re-planning it,
        // and how long the replacement path may be.
        public const float ReplanSnapMetres = 600f;

        public const float ReplanMaxPathMetres = 60000f;

        // Candidates transfer-scored per requested route. Four networks each grow up
        // to RouteCount * 4, so this covers all of them rather than only the network
        // whose corridors happen to carry the most flow per edge.
        //
        // It has to rise with that budget. Candidates enter scoring sorted by corridor
        // flow, and enabled demand does not follow flow at all — the best candidate in
        // one refresh carried a corridor flow of 63 and unlocked more journeys than
        // anything else in the run. A scoring window narrower than the candidate set
        // would drop exactly that kind of line, unscored, to the bottom of a ranking
        // led by the number it never got.
        // How close a sampled stop entity has to be to a collected line's stop to be
        // the same stop. Generous, because the two come from different game components
        // and their positions need not agree exactly.
        public const float StopMatchRadiusSq = 40f * 40f;

        // How often the live city is scanned for shopping and leisure journeys under
        // way (register A0.1). A citizen stays inside a building for game-hours and a
        // journey lasts game-minutes, so one scan a second — a few game minutes at
        // normal speed — sees every stay and most departures.
        public const float TripObservationSeconds = 1f;


        public const float DebounceSeconds = 0.3f;

        // Floor on how often the tile snap may re-run. It is one nearest-node query per
        // 32 m tile — two hundred thousand on a full map — and it answers a question
        // that only changes when somebody builds a road. A new road therefore shows in
        // the building colours within a minute rather than instantly, which is the
        // right trade for an infoview.
        public const float SnapIntervalSeconds = 60f;




        // Travel demand extraction walks every citizen and runs many shortest-path
        // searches, so it is far slower than the per-tile scoring.
        // Also the cadence at which each line is sampled into the rolling window, so
        // shortening it both gets the first suggestions up sooner and doubles the
        // number of readings a day's verdict rests on.
        public const float DemandRefreshSeconds = 30f;

        // ---- Options defaults
        public const int HighlightShareDefaultPercent = 5;

        public const int CoverageWalkMinutesDefault = 10;


        public const int UtilisationFloorDefaultPercent = 15;

        public const int MaxSlopeDefaultDegrees = 15;

        public const int RouteCountDefault = 5;

        // ---- Per-mode tables (a switch per property: adding a mode is one visible edit
        // per property and the compiler cannot fill in a default for a forgotten one).

        // The distinct catchment horizons over all modes, ascending: one access pass
        // computes every class at once so the map (one mode) and stop placement (any
        // mode) read the same numbers. Pinned by a test against Assumptions.CatchmentMs.
        public static readonly int[] CatchmentClassesMs = { 360_000, 660_000, 960_000 };

        public static int CatchmentMs(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Metro: return 660_000;
                case ModePreset.Train: return 960_000;
                case ModePreset.Ferry: return 660_000;
                default: return 360_000;
            }
        }

        public static float CruiseSpeedFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 12f;
                case ModePreset.Metro: return 18f;
                case ModePreset.Train: return 28f;
                case ModePreset.Ferry: return 10f;
                default: return 9f;
            }
        }

        // What a stop of another mode is worth as a transfer partner: its vehicle's
        // trunk capacity relative to a bus, read from the loaded prefabs
        // (assumptions register A1.10 — this replaced a hand-typed table of 1 / 1.2 /
        // 1.5 / 2.5 / 3). Zero when either capacity is unknown: a mode the save has
        // no vehicle for is not a transfer partner, and the caller logs it.
        // How far apart stops belong, in metres. Used to place them on a suggestion
        // and to judge whether an existing line makes too many.
        public static float StopSpacingFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 450f;
                case ModePreset.Metro: return 800f;
                case ModePreset.Train: return 2000f;
                case ModePreset.Ferry: return 1200f;
                default: return 350f;
            }
        }

        // End-to-end ride time a line of this mode may ask of its riders, replacing the
        // length floors and ceilings (A4.6/A6.1): Bus 30, Tram 35, Metro 30, Train 60,
        // Ferry 45 minutes.
        public static float MaxRideSecondsFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 35f * 60f;
                case ModePreset.Metro: return 30f * 60f;
                case ModePreset.Train: return 60f * 60f;
                case ModePreset.Ferry: return 45f * 60f;
                default: return 30f * 60f;
            }
        }
    }
}
