using System;

namespace WhereTheyGo
{
    // Every number the mod computes or draws with, in one place: a value that lives in
    // one file cannot drift between two. Each entry says what it models or which game
    // type it mirrors; the comments were moved here with the values. Per-mode tables
    // live at the end. Two kinds of number stay elsewhere on purpose: the bounds of the
    // settings page (a UI contract, beside the property they bound) and the save-format
    // version and its size caps (a wire contract, beside the serializer).
    internal static class Assumptions
    {
        // ---- The game clock
        public const float NightStart = 11f / 12f;

        public const float NightEnd = 0.25f;

        public const float EveningShiftOffset = 0.33f;

        public const float NightShiftOffset = 0.67f;

        // Game.Simulation.TimeSystem.kTicksPerDay. One simulation frame is one tick,
        // so a game day is this many frames whatever speed the player is running at.
        public const uint FramesPerGameDay = 262144u;

        // ---- Grids and lattices
        // Zones are much coarser than the score grid: they exist to make the flow
        // assignment tractable, not to be looked at.
        public const float ZoneSize = 256f;

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

        public const float TileSize = 32f;

        // Where one class of walk-to-transit ends and the next begins, as multiples of
        // the player's own coverage horizon, so the four classes the panel draws move
        // with the setting instead of contradicting it. At the default five minutes
        // they read under 2.5 / 2.5-5 / 5-10 / no stop reached. The last class holds
        // everything the access field never reached, which it searches to
        // AccessFieldHorizonMultiple times the horizon.
        public const float WalkClassNearShare = 0.5f;

        public const float WalkClassFarShare = 2f;

        // ---- Walking
        // Walking-time horizons of the access model. Access: the straight-line walk
        // from a door to the pavement network beyond which a point is treated as
        // off-network. Transfer: how long a rider walks to change vehicle.
        public const int AccessWalkMs = 120_000;

        // How many doors one pedestrian network's snap memo holds before it starts
        // over (SnapMemo). Far above the doors of any city, so in practice it never
        // fills; it exists so a session that keeps meeting new positions cannot grow
        // the memo without end.
        public const int SnapMemoCapacity = 1_000_000;

        public const int TransferWalkMs = 180_000;

        // How long a rider will walk to change vehicle: three minutes at the planning
        // walking speed (the literature gives transfer TIME weights,
        // no distance threshold - TCQSM Exhibit 4-5). Metres follow from the speed.
        public const float TransferWalkSeconds = TransferWalkMs / 1000f;

        public const float TransferWalkRadius = WalkSpeed * TransferWalkSeconds;

        // How far a zone's centre may be from a stop for that stop to serve it. A zone
        // is 256 m across, so its centre is further from a stop than its edges are, and
        // the plain transfer radius left most zones unserved.
        public const float ZoneStopReachMetres = TransferWalkRadius * 2f;

        // Walking is slow enough that a long connection is worse than a detour by
        // vehicle, which is what keeps interchanges local.
        // 1.2 m/s is the planning value (TCQSM 3rd ed. ch. 5; FHWA-RD-98-107), not the
        // brisk 1.4 the routing used before.
        public const float WalkSpeed = 1.2f;

        // Flat cost of boarding, on top of the wait. Matches TransportPathfind's
        // m_StartingCost time component (5), so a change of vehicle costs what the
        // game itself charges for one. There is no separate transfer penalty in
        // vanilla - a transfer is simply a second boarding - so modelling boardings
        // is modelling transfers.
        public const float DefaultBoardPenaltySeconds = 5f;

        // ---- Journeys
        // The city's working hours when EconomyParameterData is not there to read, as
        // day fractions: the game's own 06:00 and 17:00.
        public const float WorkDayStartDefault = 0.25f;

        public const float WorkDayEndDefault = 0.7083f;

        // Two doors closer than this, in squared metres, are the same door: working
        // from the building one lives in, or being first seen already at the shop, is
        // not a journey.
        public const float MinJourneyDistanceSq = 1f;

        // How many game days of shopping and leisure journeys the observation keeps.
        // Three rather than one: the hour-by-hour
        // picture a single day gives jumps about, because an hour of one day is a few
        // hundred observations. Averaging three days steadies it at three times the
        // memory - averaging, so each observed journey weighs one over the days held
        // (ObservedTripWindow.ScaleFor).
        public const int ObservationWindowDays = 3;

        public const uint ObservationWindowFrames = FramesPerGameDay * ObservationWindowDays;

        // Backstop against a player leaving the game running for days at speed; far
        // above what the window's days of a large city produce.
        public const int ObservedTripCapacity = 200_000 * ObservationWindowDays;

        // ---- Desire bands (the map's main layer)
        // Two zone pairs are the same corridor when BOTH their ends lie this close
        // together. Raised from 250 m: at the zone grid's own 256 m
        // pitch almost nothing merged beyond what the grid had already merged, and
        // the map drew hundreds of near-duplicate corridors on top of each other.
        public const float BandMergeMetres = 450f;

        // Bands drawn at most. A real city settles well under this; a map with tens of
        // thousands of zone pairs would otherwise draw until the frame died. What does
        // not fit is counted and logged, never quietly dropped.
        public const int MaxBands = 400;

        // Band widths in metres on the ground, one per width class, thinnest first
        // (BandView). Four classes rather than a continuous ramp, because a width that
        // cannot be read off a legend can only be compared, and comparing needs the
        // whole map at once. The steps widen towards the top so the classes stay
        // apart: 6 and 11 differ as clearly as 26 and 44.
        public static readonly float[] BandClassWidthsMetres = { 6f, 11f, 22f, 40f };

        // The dark casing drawn around the dot at each end of a band, in metres. The
        // bands themselves no longer carry one: a tube in the air is separated from
        // the tube it crosses by depth, which reads better than any outline. The dots
        // lie flat on the ground among the streets and still need theirs.
        public const float BandOutlineMetres = 1.6f;

        // The dot drawn at each end of a band, as a share of the band's own width.
        // Flow maps that anchor their flows at point symbols were read with fewer
        // errors than flows floating between areas (Jenny et al. 2016, 74 % of the
        // sample), and our ends are zone centroids with nothing to mark them.
        public const float BandEndDotShareOfWidth = 1.35f;

        // The arrowhead that says which way an hour's traffic runs: how long each barb
        // is as a share of the band's width, and how far before the far end the tip
        // sits. Arrowheads beat every other direction cue in the user study behind
        // those design principles, and unlike the travelling dots they hold still.
        //
        // 1.1 rather than the 2.2 first tried: a head twice the band's width reads as
        // a second object lying on the map rather than as a mark on the band.
        public const float BandArrowShareOfWidth = 1.1f;

        public const float BandArrowInsetShare = 0.16f;

        // How high a band's arc flies over its middle: a share of its length, capped
        // in metres. The arc rises into the AIR rather than bowing sideways, so a band
        // stands over the straight line between its two ends and two bands between the
        // same districts are told apart by how high they fly rather than by which way
        // they swing. Eighteen percent puts a two-kilometre corridor well above the
        // tallest tower without burying the city under it.
        public const float BandArcHeightShare = 0.18f;

        public const float BandArcMaxHeightMetres = 400f;

        // Bands under this share of the heaviest band are hidden until the player
        // moves the panel's slider. Five percent leaves the corridors and drops the
        // hair; two showed every one of the four hundred at once. The slider runs from
        // zero to the maximum below.
        public const int BandThresholdDefaultPercent = 5;

        public const int BandThresholdMaxPercent = 25;

        // Real seconds per hour of the day while the time-of-day slider is playing: a
        // whole day in about half a minute. Nothing in this mod moves fast.
        public const float HourPlaySeconds = 1.4f;

        // How far one direction must lead the other before a band is drawn as flowing
        // that way: a tenth of the hour's traffic. Below it the hour is even and an
        // arrow would invent a rush hour that is not there.
        public const float BandDirectionLead = 0.1f;

        // ---- Drawing the bands (BandRenderer, BandPickSystem, BandGeometry)
        // The arc is a chain of tubes: one piece per this many metres of band, never
        // fewer than the floor (a band turns through some seventy degrees, and ten
        // pieces is where it stops reading as a folded rule) nor more than the cap.
        public const float BandArcMetresPerSegment = 200f;

        public const int BandMinArcSegments = 10;

        public const int BandMaxArcSegments = 20;

        // The tube has no end caps, so each piece is lengthened by this share of its
        // radius to let neighbours interpenetrate and close the wedge at every joint.
        public const float BandJointOverlapShareOfRadius = 0.5f;

        // Lifted off the ground so the feet of an arc are not swallowed by the terrain.
        public const float BandTerrainOffsetMetres = 4f;

        // Near enough to opaque to read as one solid thing, near enough to transparent
        // that a band behind another is still there (Jenny et al. 2016: 89 % of flow
        // maps are drawn opaque). With a line selected, what it carries stays at the
        // highlight and everything else steps back to the dimmed value.
        public const float BandOpacity = 0.88f;

        public const float BandHighlightOpacity = 1f;

        public const float BandDimmedOpacity = 0.16f;

        // The arrowhead's barbs run back along the arc 35 degrees either side (cos and
        // sin of 35 degrees), drawn this thick relative to the tube and never thinner
        // than the floor in metres.
        public const float BandArrowCos = 0.819f;

        public const float BandArrowSin = 0.574f;

        public const float BandArrowStrokeShare = 0.28f;

        public const float BandMinArrowStrokeMetres = 2.5f;

        // The arrowhead is white over the band; its opacity is the band's times
        // (base + lift x (1 - carried share)), held back on the cool end of the ramp,
        // which is already light.
        public const float BandArrowBaseOpacity = 0.55f;

        public const float BandArrowOpacityLift = 0.45f;

        // The hit test samples the arc in this many pieces (more than the renderer
        // draws, so the test is never coarser than the line) and grabs a band this many
        // PIXELS outside its drawn edge - pixels, because the arcs fly and are pointed at
        // where they appear, not where they lie.
        public const int BandPickSamples = 24;

        public const float BandPickSlackPixels = 14f;

        // The casing colour of a band's foot dots is the band's own hue driven down by
        // this factor: a neutral black outline would read as a fifth colour on a map
        // that already carries a ramp.
        public const float BandOutlineDarkening = 0.28f;

        // The band ramp, warm where nobody rides to cool where everybody does, as RGB
        // in 0..1. Its LIGHTNESS is monotone as well as its hue, so red-green and
        // blue-yellow colour blindness and a greyscale screenshot all still read it.
        public static readonly float[] BandColourWarm = { 0.99f, 0.55f, 0.24f };

        public static readonly float[] BandColourCool = { 0.13f, 0.25f, 0.55f };

        // The building ramp: walking time from a door to the nearest served stop, pale
        // straw at a stop through orange to deep red-brown at or beyond the horizon.
        // ColorBrewer YlOrRd, monotone in lightness for
        // the same reason as the band ramp.
        public static readonly float[] WalkColourNear = { 1f, 0.97f, 0.75f };

        public static readonly float[] WalkColourMid = { 0.99f, 0.55f, 0.24f };

        public static readonly float[] WalkColourFar = { 0.50f, 0f, 0.15f };

        // ---- The infoview and its infomodes (Infoview)
        // Where the mod's infoview and infomodes sort among the game's, the number of
        // steps the legend gradient is drawn in, and the infoview's own two colours.
        public const int InfoviewPriority = 900;

        public const int InfomodePriority = 200;

        public const int InfomodeGradientSteps = 16;

        public static readonly float[] InfoviewDefaultColour = { 0.35f, 0.35f, 0.38f };

        public static readonly float[] InfoviewSecondaryColour = { 0.5f, 0.5f, 0.55f };

        // How often the auto-activation sweep re-checks every placeable prefab even when
        // change detection reports nothing.
        public const float PlaceableReverifySeconds = 60f;

        // ---- Line insight
        // Under this many seconds, a journey is "no slower without the line". Half a
        // minute: the difference a rider would not notice, and well under the
        // granularity of anything else the mod measures.
        public const float NoSlowerSeconds = 30f;

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

        // Below this many carried PAIRS the median is noise, and the fixed hour is the
        // more honest reference. A count of pairs, not of journey weight, even though
        // the median itself is weighted: it is statistical support, and
        // one heavy pair is still one observation.
        public const int MinPairsForServedMedian = 20;

        // ---- The transit graph (TransitGraph, JourneyRouting)
        // No edge is free: a zero-cost edge lets the search walk it for nothing.
        public const float MinEdgeSeconds = 0.01f;

        // The tops the load chart's y axis may take, in percent, smallest first: the
        // smallest one at or above the line's own busiest hour is chosen. A fixed
        // 0-100 axis drew a metro that runs at 1-3 % as a flat line on the floor.
        // Each entry halves to a readable number, because the chart marks the top and
        // its half and nothing else.
        public static readonly int[] LoadAxisTopsPercent = { 2, 4, 10, 20, 40, 60, 80, 100 };

        // ---- Line readings
        // The planning load of a line is this quantile (nearest rank) of the passengers
        // aboard over the window's active readings: the
        // single busiest reading is kept beside it for the display but sizes nothing.
        public const float PlanningLoadQuantile = 0.9f;

        // Readings are taken in GAME time, this many per game day,
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

        // ---- The ridden loop against the game's own clamp (RiddenLoop)
        // TransportLineSystem publishes an interval of min(cap x target, lineDuration /
        // fleetTarget); this is that cap. At the cap the interval says only "the loop is
        // at least this long", which is why the clamp treats capped lines differently
        // from the others.
        public const float VehicleIntervalCapMultiple = 10f;

        // How far a line's ridden loop may exceed interval x fleet before it is treated
        // as broken: the game writes m_VehicleInterval with a hysteresis of a second, so
        // the reconstruction is exact to within a second PER VEHICLE the interval is
        // multiplied by. Additive on purpose: the healthy excess in the log was 0-16 s
        // and never more than one second a vehicle, while a wrapped average decays
        // through every proportional band on its way down.
        public const float RiddenLoopSlackSecondsPerVehicle = 1f;

        // On a CAPPED line the clamp is a floor, not a figure, so a ridden loop above it
        // is what a slow line honestly looks like. Only past this multiple is it read as
        // a wrapped average instead: the loops that are actually broken overshoot by
        // five times and more (RouteUtils.UpdateAverageTravelTime on unsigned frames).
        public const float RiddenLoopWrapMultiple = 5f;

        // How far past the clamp a line has to be before the clamp is worth SAYING.
        // Clamping and warning are different questions: snapping a loop that is three
        // seconds over back onto the game's own figure is right and costs nothing, but
        // saying "this line's travel time has wrapped" about it is false. Seen in the
        // log at 1.0x on three healthy lines (327s against 324s, 297 against 295, 932
        // against 929) while one genuinely broken line sat at 12x.
        public const float RiddenLoopWarnMultiple = 1.5f;

        // The Owner chain from a stop up to the building the game marks as an outside
        // connection: stop, station sub-object, station, and one to spare.
        public const int StopOwnerChainDepth = 4;

        // ---- Cadences (real seconds; the readings above run on the game clock)
        // How often the live city is scanned for shopping and leisure journeys under
        // way. A citizen stays inside a building for game-hours and a
        // journey lasts game-minutes, so one scan a second - a few game minutes at
        // normal speed - sees every stay and most departures.
        public const float TripObservationSeconds = 1f;

        public const float DebounceSeconds = 0.3f;

        // Floor on how often the tile snap may re-run. It is one nearest-node query per
        // 32 m tile - two hundred thousand on a full map - and it answers a question
        // that only changes when somebody builds a road. A new road therefore shows in
        // the building colours within a minute rather than instantly, which is the
        // right trade for an infoview.
        public const float SnapIntervalSeconds = 60f;

        // How long to wait before asking again while there is no tile snap at all.
        // Short, because the only reason to be in that state is that the game has not
        // finished building the world yet.
        public const float WalkNetworkRetrySeconds = 2f;

        // The demand refresh walks every citizen and hands the routing to a worker, and
        // it is also the cadence at which the lines are re-read. Half a minute.
        public const float DemandRefreshSeconds = 30f;

        // ---- Options defaults
        // The walking horizon the player starts with: how far a door may be from a
        // served stop to count as connected, and how long a whole journey may take on
        // foot before it is a walk rather than a transit question. Lowered from ten to
        // five minutes after reading a live city: at ten, 57.5 % of the journeys left
        // the map as walks and the network read as carrying 95.9 % of what remained, a
        // threshold describing the city rather than the network. The time is a straight
        // line at WalkSpeed, so five minutes is 360 m as the crow flies, nearer seven
        // on real pavements. This is also the slider's floor
        // (Setting.kCoverageMinutesMin).
        public const int CoverageWalkMinutesDefault = 5;

        // ---- Per-mode tables (a switch per property: adding a mode is one visible edit
        // per property and the compiler cannot fill in a default for a forgotten one).

        // Typical running speed in metres per second: the router's fallback for a ride
        // whose route segment carries no pathfound duration yet.
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
    }
}
