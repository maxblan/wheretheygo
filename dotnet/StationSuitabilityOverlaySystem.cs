using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Colossal.Entities;
using Game;
using Game.Common;
using Game.Companies;
using Game.Net;
using Game.Citizens;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;
using System.Diagnostics.CodeAnalysis;

namespace StationSuitabilityOverlay
{
    // The vanilla heatmap pipeline: OverlayInfomodeSystem clears the terrain
    // override overlay each frame and active heatmap infomodes rewrite it, then
    // TerrainRenderSystem consumes it into the terrain material. All three run in
    // PreCulling, so this system must be ordered between the two vanilla ones.
    [UpdateAfter(typeof(OverlayInfomodeSystem))]
    [UpdateBefore(typeof(TerrainRenderSystem))]
    public sealed partial class StationSuitabilityOverlaySystem : GameSystemBase
    {
        private const float TileSize = 32f;
        private const float DebounceSeconds = 0.3f;
        private const float PeriodicRefreshSeconds = 10f;
        // Road, workplace and zoning collections are cached and rebuilt from change
        // detection; this is the backstop for inputs that change with no
        // Created/Updated tag.
        private const float CollectionRefreshSeconds = 60f;
        private const float PlaceableReverifySeconds = 60f;
        // Terrain barely ever changes, and the mask costs a full-grid sampling pass
        // plus a flood fill, so it is refreshed far less often than the scores.
        private const float MaskRefreshSeconds = 120f;
        private const float RidershipSampleSeconds = 60f;
        private const float RidershipSaveSeconds = 300f;
        // Travel demand extraction walks every citizen and runs many shortest-path
        // searches, so it is far slower than the per-tile scoring.
        // Also the cadence at which each line is sampled into the rolling window, so
        // shortening it both gets the first suggestions up sooner and doubles the
        // number of readings a day's verdict rests on.
        private const float DemandRefreshSeconds = 30f;
        // How often the live city is scanned for shopping and leisure journeys under
        // way (register A0.1). A citizen stays inside a building for game-hours and a
        // journey lasts game-minutes, so one scan a second — a few game minutes at
        // normal speed — sees every stay and most departures.
        private const float TripObservationSeconds = 1f;
        private const int InfomodePriority = 200;

        // Demand, jobs and future demand are raw sums with unbounded scale; each is
        // normalized against this percentile of its own positive values so all five
        // weighted terms are comparable 0..1 quantities.
        private const float TermCapPercentile = 0.98f;
        private const float IntensityGamma = 0.6f;

        // Static bridge for the options page. The settings object is constructed
        // before the world exists, so the button properties and the read-only status
        // text talk to the system through these.
        private static string s_CalibrationStatus = "Waiting for a city to load.";
        private static string s_PipelineStatus = string.Empty;
        private static string s_RouteSummary = "No route suggestions yet.";
        private static string s_RouteList = string.Empty;
        private static string s_LineHealthList = string.Empty;
        private static bool s_ApplyFitRequested;
        private static bool s_ResetCalibrationRequested;
        private static int s_ImproveRequest = -1;
        private static string s_ImprovePlan = string.Empty;
        private static int s_ImprovedLine = -1;
        private static bool s_ImprovedRouteDrawn;
        // Which suggestion the panel is pointing at, and which one it has narrowed the
        // map down to. Both are positions in the CURRENT suggestion list, which is only
        // a safe key because both are cleared the moment a new list arrives — see
        // UpdateRouteSummary.
        private static int s_HighlightedRoute = -1;
        private static int s_SelectedRoute = -1;

        public static string CalibrationStatusText =>
            string.IsNullOrEmpty(s_PipelineStatus) ? s_CalibrationStatus : s_PipelineStatus + "\n" + s_CalibrationStatus;

        public static string RouteSummaryText => s_RouteSummary;

        // One route per line as "mode|km|stops|vehicles|colour|reachPercent", for the
        // panel to render as a colour-keyed list. A compact string avoids hand-rolling a JSON
        // writer for what is at most a dozen rows. The colour travels with the row
        // because the panel used to carry its own copy of the mode palette, kept in
        // step with the renderer's by a comment.
        public static string RouteListText => s_RouteList;

        // One line per existing route as "mode|verdict|detail", for the panel.
        public static string LineHealthText => s_LineHealthList;

        // "coveredHours|readings|windowHours" — how much observed history the verdicts
        // and the served-demand discount are actually resting on. The panel shows it
        // because a mean over twenty minutes and a mean over a full day are the same
        // number on screen and mean very different things.
        public static string DataCoverageText => s_DataCoverage;
        public static string EquityText => s_Equity;

        public static void RequestApplyFittedWeights() => s_ApplyFitRequested = true;

        public static void RequestResetCalibration() => s_ResetCalibrationRequested = true;

        // The panel asks for one line's improvement plan by the line's own id — never
        // by its position, since the list is re-sorted worst-first on every refresh.
        public static void RequestImprovement(int lineId) => s_ImproveRequest = lineId;

        // The suggestion the pointer is over, drawn heavier so a glance answers "which
        // one is this row". -1 for none.
        public static void HighlightRoute(int index) => s_HighlightedRoute = index;

        // Narrows the map to one suggestion, or back to all of them. Clicking the row
        // that is already selected clears it, which is the only way back.
        public static void SelectRoute(int index) => s_SelectedRoute = s_SelectedRoute == index ? -1 : index;

        // Which suggestion is drawn alone, or -1 for all of them. The panel renders its
        // row highlight from this rather than from state of its own, so the two cannot
        // disagree about what the map is showing.
        public static int SelectedRouteIndex => s_SelectedRoute;

        internal static int HighlightedRouteIndex => s_HighlightedRoute;

        public static string ImprovePlanText => s_ImprovePlan;

        // Which line the plan belongs to, so the panel can show it against the right
        // row instead of at the bottom of a long list.
        public static int ImprovedLineIndex => s_ImprovedLine;

        // The re-traced alignment for that line, drawn on the map.
        internal SuggestedRoute? ImprovedRoute => m_ImprovedRoute;

        // Whether that alignment exists. The panel's plan carries a line saying the
        // white dashed route is on the map, and BuildImprovedRoute has three ways to
        // come back with nothing — so the panel has to be told which it got.
        public static bool ImprovedRouteDrawn => s_ImprovedRouteDrawn;

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private TerrainSystem m_TerrainSystem;
        private WaterSystem m_WaterSystem;
        private PopulationToGridSystem m_PopulationSystem;
        private Game.Prefabs.ZoneSystem m_ZoneSystem;
        private PrefabSystem m_PrefabSystem;
        private ToolSystem m_ToolSystem;
        private Game.UI.NameSystem m_NameSystem;
        private OverlayInfomodeSystem m_OverlayInfomodeSystem;
#pragma warning restore CS8618

        private EntityQuery m_StopQuery;
        private EntityQuery m_StopChangedQuery;
        private EntityQuery m_NodeQuery;
        private EntityQuery m_RoadEdgeQuery;
        private EntityQuery m_WorkplaceQuery;
        private EntityQuery m_BlockQuery;
        private EntityQuery m_NodeChangedQuery;
        private EntityQuery m_EdgeChangedQuery;
        private EntityQuery m_WorkplaceChangedQuery;
        private EntityQuery m_BlockChangedQuery;
        private EntityQuery m_ActiveInfomodeQuery;
        private EntityQuery m_PlaceableInfoviewQuery;
        private EntityQuery m_PlaceableInfoviewChangedQuery;

        private ComponentLookup<Transform> m_TransformLookup;
        private ComponentLookup<Game.Buildings.PropertyRenter> m_PropertyRenterLookup;
        private ComponentLookup<ZoneData> m_ZoneDataLookup;

        // Registered layer prefabs, and the mapping from the infomode entity the
        // game activates back to the layer it draws.
        private readonly Dictionary<SuitabilityLayer, SuitabilityInfomodePrefab> m_LayerPrefabs =
            new Dictionary<SuitabilityLayer, SuitabilityInfomodePrefab>();
        private readonly Dictionary<Entity, SuitabilityLayer> m_InfomodeLayers = new Dictionary<Entity, SuitabilityLayer>();
        private InfoviewPrefab? m_InfoviewPrefab;

        private readonly SuitabilityCalibration m_Calibration = new SuitabilityCalibration();

        // One intensity array per layer, plus the interleaved RGBA buffer uploaded
        // to the terrain texture.
        private readonly byte[][] m_LayerIntensities = new byte[SuitabilityLayers.Count][];
        private byte[]? m_ExpandedCache;
        private long m_ExpandedSignature = -1;
        private int2 m_IntensityGrid;

        // Bound once rather than invoked reflectively every frame: InjectOverlay runs
        // on the render path while the overlay is on screen, and MethodInfo.Invoke
        // there allocated an object[], boxed the int2 argument and boxed the returned
        // NativeArray sixty times a second. Binding a typed delegate also checks the
        // signature the reflection lookup could not — GetMethod matches on parameters
        // only, so a changed return type would have surfaced as a per-frame cast
        // exception instead of the one-shot diagnostic below.
        private static Func<OverlayInfomodeSystem, int2, NativeArray<byte>>? s_GetTerrainTextureData;
        private static FieldInfo? s_TerrainTextureField;
        private static bool s_ReflectionChecked;

        private bool m_LastActive;
        private int m_LastLoggedIndex = int.MinValue;
        private ComputeSnapshot m_LastComputeSettings;
        private CombineSnapshot m_LastCombineSettings;

        private bool m_RecomputeRequested;
        private float m_RecomputeAt;
        private float m_LastComputeFinish;
        private float m_LastCollectionRebuild;
        private float m_LastMaskRefresh;
        private float m_LastRidershipSample;
        private float m_LastRidershipSave;
        private int[]? m_LoggedSiteIndices;
        private float[]? m_LoggedSiteScores;
        private int m_LoggedSiteCount = -1;
        private bool m_PrefabsAdded;
        private bool m_InfoviewLinkChecked;
        private bool m_InfoviewLinkWaitLogged;
        private bool m_PlaceableSweepDone;
        private float m_LastPlaceableSweep;
        private int m_LastPlaceableCount = -1;
        private Dictionary<Entity, PlaceableInfoviewItem[]>? m_VanillaPlaceableInfoviews;
        private bool m_LastOverlayApplied;
        private int m_LastStopCount;

        // The access pass runs on a worker thread as plain managed code — it is the
        // Unity-free core in SuitabilityWalkAccess, so a Task is the right vehicle and
        // the same code runs offline on an exported city. Completion is polled from
        // OnUpdate exactly as the Burst job it replaced was.
        private bool m_JobPending;
        private System.Threading.Tasks.Task? m_PendingAccess;
        private AccessBox? m_PendingBox;
        private WalkAccessInputs? m_PendingInputs;
        private int m_PendingHomeCount;
        private WalkAccessOutput? m_Access;
        private WalkAccessInputs? m_AccessInputs;
        private int2 m_PendingGrid;
        private float2 m_PendingWorldMin;
        private int m_PendingStopCount;
        private int m_PendingOrphanCount;
        private int m_PendingJobSiteCount;
        private int m_PendingZonedCount;

        // The worker writes its output here; the main thread reads it once the task
        // reports completion, which is the memory barrier that makes the hand-off safe.
        private sealed class AccessBox
        {
            public WalkAccessOutput? m_Output;
        }

        // The route pipeline — alignment search, weighing every candidate alone and the
        // exact line-set search — runs on a worker task. Everything it reads (the
        // networks, zone flows, scores, masks, served stops, existing lines) is main-
        // thread state that OnUpdate leaves untouched while m_RoutesPending is set: the
        // heat-map adoption, the demand refresh, line collection, the improvement and
        // calibration requests all wait. Everything it produces lands in a RoutePass and
        // is copied into the fields the panel and the renderer read once the task has
        // completed (FinishRoutesIfReady). The pass measured 60+ s on the main thread
        // in a 13-line city — every frame of it a frozen game.
        private bool m_RoutesPending;
        private System.Threading.Tasks.Task? m_PendingRoutes;
        private RoutePass? m_PendingRoutePass;
        private float m_RoutePassStarted;

        // Wall-clock budget for the line-set search on the worker. Past it the search
        // keeps the best set found and reports the open bound as the ceiling
        // (SuitabilityLineSet.Solve); the log says which regime the result is in.
        private const int LineSetTimeBudgetSeconds = 90;

        private sealed class RoutePass
        {
            public readonly List<SuggestedRoute> Candidates = new List<SuggestedRoute>();
            public readonly List<SuggestedRoute> Routes = new List<SuggestedRoute>();
            public readonly List<float2> AcceptedStops = new List<float2>();
            public readonly List<TransitLine> AcceptedLines = new List<TransitLine>();
            public readonly List<SuggestedRoute> Resolved = new List<SuggestedRoute>();
            public readonly List<DeferredLogLine> Log = new List<DeferredLogLine>();
            public LineSetSolution? Solution;
            public LineSetProblem? Problem;
            // What the demand refresh that started the pass would have reported.
            public int TripCount;
            public int AssignedPairs;
            public float TotalZoneWeight;
            // Where the worker's time went, for the log.
            public long AlignmentMs;
            public long WeighMs;
            public long ResolveMs;
            public long SolveMs;
        }
        // The PLAYABLE-AREA grid as of the last compute. Deliberately not the grid the
        // compute ran on — that one comes from the population map's own extent — and
        // named for what it holds, because "the grid at compute" invited the reader to
        // assume the two were the same. It exists only to notice the playable area
        // changing, which is a reason to recompute.
        private int2 m_PlayableGridAtCompute;

        // Cached raw terms from the last compute, plus everything derived from them.
        private SuitabilityCell[]? m_RawTerms;
        private float[]? m_Scores;
        private float[]? m_ScoreScratch;
        private float[]? m_TermScratch;
        private float2 m_ScoreWorldMin;
        // Term caps from the last combine. Cached because the ridership sampler
        // needs them per stop, and recomputing a percentile over the whole grid for
        // every stop on every sample would cost tens of millions of operations.
        private float m_DemandCap;
        private float m_JobsCap;
        private float m_FutureCap;

        // Terrain-derived masks, rebuilt rarely.
        private NativeArray<byte> m_Buildable;
        // Managed because only the main thread reads it (mask build + site
        // refinement); the job takes Buildable and Components.
        private byte[]? m_Land;
        private NativeArray<int> m_Components;
        private int2 m_MaskGrid;
        private bool m_MaskDirty = true;
        private ModePreset m_MaskMode;
        private int m_MaskSlope;

        // Every served stop with its transport type. The access pass accumulates each
        // type per node, so the map (one mode) and stop placement (the suggested
        // line's mode) read the same numbers.
        private readonly List<float2> m_AllStopPositions = new List<float2>();
        private readonly List<int> m_AllStopTypes = new List<int>();

        // What the score field in m_Scores was combined FOR. A query for any other mode
        // has to undo this mode's stop-derived terms and put its own in their place.
        private ModePreset m_ScoredMode;
        private float m_ScoredInvSelf;
        private float m_ScoredDemandWeight;
        private float m_ScoredJobsWeight;
        private float m_ScoredCoverageWeight;
        private float m_ScoredAccessWeight;
        private float m_ScoredFutureWeight;
        private float m_ScoredInterchangeWeight;
        private float m_ScoredCrossWeight;
        private float m_ScoredInvDemand;
        private float m_ScoredInvJobs;
        private float m_ScoredInvFuture;
        // The pedestrian network, rebuilt with the road cache.
        private WalkGraph? m_WalkGraph;
        private int m_EdgesWithoutPavement;
        private readonly List<float2> m_HomePositions = new List<float2>();
        private readonly List<float> m_HomeResidents = new List<float>();
        private EntityQuery m_HouseholdQuery;
        private readonly List<float2> m_JobPositions = new List<float2>();
        private readonly List<float> m_JobWorkers = new List<float>();
        private readonly List<float2> m_FutureHomePositions = new List<float2>();
        private readonly List<float> m_FutureHomeWeights = new List<float>();
        private readonly List<float2> m_FutureJobPositions = new List<float2>();
        private readonly List<float> m_FutureJobWeights = new List<float>();
        private bool m_RoadCacheDirty = true;
        private bool m_WorkplaceCacheDirty = true;
        private bool m_ZoneCacheDirty = true;
        private int m_LastOrphanCount;
        private int m_HouseholdsWithoutHome;

        // Travel demand and route suggestions.
        private EntityQuery m_CitizenQuery;
        private ComponentLookup<Worker> m_WorkerLookup;
        private ComponentLookup<Game.Citizens.Student> m_StudentLookup;
        private ComponentLookup<TouristHousehold> m_TouristLookup;
        private ComponentLookup<Node> m_NodeLookup;
        private ComponentLookup<Curve> m_CurveLookup;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private ComponentLookup<RoadData> m_RoadDataLookup;
        private readonly SuitabilityRoadGraph m_RoadGraph = new SuitabilityRoadGraph();
        // Rail and water get their own free-form networks: a metro tunnel or a ferry
        // crossing cannot be expressed on the street graph at all. Train and metro
        // share the lattice shape but not its costs — train reuses existing track
        // wherever it can, metro prefers fresh alignment.
        // One rail lattice for metro and train alike (register A4.5: both get the same
        // preference for existing track); the riders decide which of the two runs on it.
        private readonly SuitabilityRoadGraph m_RailNetwork = new SuitabilityRoadGraph();
        private readonly SuitabilityRoadGraph m_WaterNetwork = new SuitabilityRoadGraph();
        private EntityQuery m_AllEdgeQuery;
        private byte[]? m_TrackMask;
        private readonly List<float2> m_TrackStarts = new List<float2>();
        private readonly List<float2> m_TrackEnds = new List<float2>();
        private readonly List<SuggestedRoute> m_RouteCandidates = new List<SuggestedRoute>();

        // Existing transit system: the lines themselves, the routable model of them,
        // and their health.
        private EntityQuery m_LineQuery;
        private readonly List<ExistingLine> m_ExistingLines = new List<ExistingLine>();
        private readonly List<float2> m_TransitStops = new List<float2>();

        // Where a rider of a suggested line could change vehicle. Derived from the
        // existing network and rebuilt by BuildTransitModel with everything else that
        // depends on it — there is no separate invalidation to get wrong.
        private InterchangeMap m_Interchanges;
        private readonly Dictionary<Entity, int> m_StopIndices = new Dictionary<Entity, int>();
        private readonly List<LineHealth> m_LineHealth = new List<LineHealth>();
        private SuggestedRoute? m_ImprovedRoute;
        private TransitNetwork? m_TransitNetwork;
        private DijkstraWorkspace? m_TransitWorkspace;
        private int[]? m_ZoneStops;
        // A game day of line readings. Judging a line on the single reading a refresh
        // happens to land on condemned a one-boat ferry as empty whenever its boat was
        // mid-crossing; the window is what a verdict rests on instead.
        private readonly LineHistory m_LineHistory = new LineHistory(LineHistory.FramesPerGameDay);
        private readonly HashSet<int> m_LiveLineIds = new HashSet<int>();
        private uint m_LastHistoryFrame;
        private static string s_DataCoverage = string.Empty;
        private static string s_Equity = string.Empty;
        private uint m_LastLineRefreshFrame;
        private float m_LastLineSample;
        // Where last refresh's suggestions ran between, so churn can be measured.
        private readonly List<RouteEnds> m_PreviousRouteEnds = new List<RouteEnds>();
        // What LogRoutes last printed, so an unchanged list is not re-printed every
        // refresh. Separate from m_PreviousRouteEnds, which measures churn between
        // refreshes and is reset by that measurement.
        private readonly List<RouteEnds> m_LoggedRouteEnds = new List<RouteEnds>();
        private float[]? m_ZoneStopDistSq;
        private float[]? m_ZoneCentreX;
        private float[]? m_ZoneCentreZ;
        // The walk to and from the candidate's own stops, and what each journey costs
        // on the network as it stands. A line earns credit for a journey only by being
        // better than what the rider already has.
        // Door-to-door seconds per zone flow on the existing network, or MaxValue where
        // it cannot carry the journey at all.
        // The suggestions accepted so far this refresh, treated as though the player
        // had built them. Every later candidate is scored against a network that
        // already contains them, so two lines cannot both be credited with the same
        // journeys — which is how four near-parallel metros came to be suggested at
        // once, each one claiming the riders of the others.
        private readonly List<float2> m_AcceptedStops = new List<float2>();
        private readonly List<TransitLine> m_AcceptedLines = new List<TransitLine>();
        // Which zone flow each pair came from, so a measured journey time can be
        // written back against the flow it belongs to.
        private int[]? m_PairOrigins;
        private int[]? m_PairDests;
        // Which zone flow each pair came from, so the served-demand discount can write
        // back to it without re-deriving the mapping the pass above already did.
        private int[]? m_PairFlow;
        private int m_PairCount;
        // Carried journeys from the discount's first pass: which pair, and how long the
        // existing network takes over it. Held so the ceiling can be derived from the
        // whole set before any weight is touched.
        private int[]? m_ServedPairs;
        private float[]? m_ServedSeconds;
        // A copy for the median, because selecting it reorders what it is given and the
        // second pass still needs the travel times in journey order.
        private float[]? m_ServedScratch;

        private readonly List<ZoneFlow> m_ZoneFlows = new List<ZoneFlow>();
        private LineSetSolution? m_LineSetSolution;
        private readonly List<SuggestedRoute> m_LineSetResolved = new List<SuggestedRoute>();
        private LineSetProblem? m_LineSetProblem;

        // The equity measure (register A1.8/A1.9): every journey of the last demand
        // refresh with its ends snapped to the pedestrian network, the walk from each
        // network node to the nearest served stop, and the coverage that gives.
        private readonly List<Trip> m_Journeys = new List<Trip>();
        private int[] m_JourneyOriginNode = Array.Empty<int>();
        private int[] m_JourneyOriginAccess = Array.Empty<int>();
        private int[] m_JourneyDestinationNode = Array.Empty<int>();
        private int[] m_JourneyDestinationAccess = Array.Empty<int>();
        private float[] m_JourneyWeight = Array.Empty<float>();
        private int[]? m_ServedWalkMs;
        private CoverageReport? m_Coverage;
        private int m_EquityHorizonMs;
        private IntDijkstra? m_EquityDijkstra;

        // Observed shopping/leisure demand (register A0.1): the window of journeys
        // seen, each citizen's current journey (so one journey is recorded once),
        // and the building each citizen was last seen inside (a journey's origin).
        private readonly ObservedTripWindow m_ObservedTrips = new ObservedTripWindow(LineHistory.FramesPerGameDay);
        private readonly Dictionary<Entity, byte> m_CurrentJourney = new Dictionary<Entity, byte>();
        private readonly Dictionary<Entity, Entity> m_LastBuilding = new Dictionary<Entity, Entity>();
        private EntityQuery m_QueuedQuery;
        private EntityQuery m_TravellingQuery;
        private EntityQuery m_InsideQuery;
        private float m_LastTripObservation;
        private int m_ObservedWithoutOrigin;
        // Diagnostics for the scan itself, logged with every demand refresh: how many
        // citizens the travelling query matched, how many carried a watched purpose,
        // how many of those had a queued destination, and which purposes were seen.
        private int m_ObservedQueued;
        private int m_ObservedTravelling;
        private int m_ObservedTravellingWithBuilding;
        private int m_ObservedTargetMissing;
        private int m_ObservedTargetVehicle;
        private int m_ObservedTargetOther;
        private readonly int[] m_ObservedPurposeHistogram = new int[256];
        private int m_ObservedLastDemand;
        private float m_ObservedScaleLastDemand;
        private readonly List<SuggestedRoute> m_Routes = new List<SuggestedRoute>();
        private int[]? m_ZoneNodes;
        private float[]? m_DemandRaster;
        private int2 m_ZoneGrid;
        private float m_LastDemandRefresh;
        // Journey weight still looking for a service once the existing network has
        // taken its share — the pool every candidate is scored against, and therefore
        // the only honest denominator for a candidate's reach.
        //
        // This used to be the PRE-discount total while the credit in the numerator was
        // drawn from the post-discount weights, so a reach of "7% of city travel" was
        // really 17% of the demand it was competing for. Two different pools, one
        // ratio.
        private float m_UnservedTravelWeight;
        private EntityQuery m_VehiclePrefabQuery;
        private EntityQuery m_LinePrefabQuery;
        // Capacity per mode, read once from the prefabs. Prefabs do not change while a
        // save is loaded, so this is built on the first compute and kept; a reload
        // rebuilds it with the rest of the system.
        private ModeFacts[]? m_FleetFacts;
        // Largest vehicle capacity per Game.Prefabs.TransportType (index = enum value),
        // read alongside m_FleetFacts. The interchange weights derive from it.
        private float[]? m_TypeCapacities;
        private bool m_GraphDirty = true;
        private RouteGoal m_LastObjective;
        private int m_LastRouteCount;

        internal List<SuggestedRoute> SuggestedRoutes => m_Routes;

        // The renderer draws only while our infoview is the one on screen.
        // Opens or closes our infoview on behalf of the toolbar button. Activation
        // still goes through ToolSystem.infoview, which is what assigns the terrain
        // overlay channel our heat map is drawn into.
        public void SetInfoviewActive(bool active)
        {
            if (m_ToolSystem is null || m_InfoviewPrefab is null)
            {
                return;
            }

            if (active)
            {
                m_ToolSystem.infoview = m_InfoviewPrefab;
                bool took = m_ToolSystem.activeInfoview == m_InfoviewPrefab;
                DeferredLog.Info(
                    $"Infoview activation requested: activeInfoview matches={took}. " +
                    "If this is false the view is not registered as valid and the heat map will not draw.");
            }
            else if (m_ToolSystem.activeInfoview == m_InfoviewPrefab)
            {
                m_ToolSystem.infoview = null;
                DeferredLog.Info("Infoview deactivated.");
            }
        }

        public bool IsInfoviewActive =>
            m_InfoviewPrefab is not null && m_ToolSystem is not null && m_ToolSystem.activeInfoview == m_InfoviewPrefab;

        // Some OTHER infoview is on screen. Route polylines and the suppression of the
        // vanilla legend both key off this rather than off IsInfoviewActive: the heat
        // map is its own toggle in the panel, and turning it off must not take the
        // routes with it, nor let the vanilla legend flash back in for the frames
        // between our infoview closing and the game unmounting its panel.
        public bool ForeignInfoviewActive =>
            m_ToolSystem is not null && m_ToolSystem.activeInfoview is not null
            && m_ToolSystem.activeInfoview != m_InfoviewPrefab;

        // Whether the mod's own panel is open. Owned here rather than in the UI system
        // because the renderer needs it too, and one fact needs one owner.
        public bool PanelOpen { get; set; }

        private readonly int[] m_SiteIndices = new int[Setting.kSiteCountMax];
        private readonly float[] m_SiteScores = new float[Setting.kSiteCountMax];
        // The candidate set the last exact selection ran on, kept for the export.
        private int[] m_SiteCandidateNodes = Array.Empty<int>();
        private float[] m_SiteCandidateScores = Array.Empty<float>();
        private int m_SiteCandidateCount;
        private int m_SiteSeparationMs;
        private int m_SiteCount;

        private struct ComputeSnapshot : IEquatable<ComputeSnapshot>
        {
            public ModePreset Mode;
            public int CatchmentRadius;
            public int AccessRadius;
            public int MaxSlope;

            public static ComputeSnapshot Capture(Setting settings)
            {
                return new ComputeSnapshot
                {
                    Mode = settings.Mode,
                    CatchmentRadius = settings.CatchmentRadius,
                    AccessRadius = settings.AccessRadius,
                    MaxSlope = settings.MaxSlope,
                };
            }

            public readonly bool Equals(ComputeSnapshot other)
            {
                return Mode == other.Mode
                    && CatchmentRadius == other.CatchmentRadius
                    && AccessRadius == other.AccessRadius
                    && MaxSlope == other.MaxSlope;
            }

            public readonly override bool Equals(object obj)
            {
                return obj is ComputeSnapshot other && Equals(other);
            }

            public readonly override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = (hash * 31) ^ Mode.GetHashCode();
                    hash = (hash * 31) ^ CatchmentRadius.GetHashCode();
                    hash = (hash * 31) ^ AccessRadius.GetHashCode();
                    hash = (hash * 31) ^ MaxSlope.GetHashCode();
                    return hash;
                }
            }
        }

        private struct CombineSnapshot : IEquatable<CombineSnapshot>
        {
            public float W1;
            public float W2;
            public float W3;
            public float W4;
            public float W5;
            public float W6;
            public float W7;
            public int HighlightShare;
            public int SiteCount;

            public static CombineSnapshot Capture(Setting settings)
            {
                return new CombineSnapshot
                {
                    W1 = settings.W1,
                    W2 = settings.W2,
                    W3 = settings.W3,
                    W4 = settings.W4,
                    W5 = settings.W5,
                    W6 = settings.W6,
                    W7 = settings.W7,
                    HighlightShare = settings.HighlightShare,
                    SiteCount = settings.SiteCount,
                };
            }

            public readonly bool Equals(CombineSnapshot other)
            {
                return W1 == other.W1 && W2 == other.W2 && W3 == other.W3 && W4 == other.W4 && W5 == other.W5
                    && W6 == other.W6 && W7 == other.W7
                    && HighlightShare == other.HighlightShare && SiteCount == other.SiteCount;
            }

            public readonly override bool Equals(object obj)
            {
                return obj is CombineSnapshot other && Equals(other);
            }

            public readonly override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = (hash * 31) ^ W1.GetHashCode();
                    hash = (hash * 31) ^ W2.GetHashCode();
                    hash = (hash * 31) ^ W3.GetHashCode();
                    hash = (hash * 31) ^ W4.GetHashCode();
                    hash = (hash * 31) ^ W5.GetHashCode();
                    hash = (hash * 31) ^ W6.GetHashCode();
                    hash = (hash * 31) ^ W7.GetHashCode();
                    hash = (hash * 31) ^ HighlightShare.GetHashCode();
                    hash = (hash * 31) ^ SiteCount.GetHashCode();
                    return hash;
                }
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_WaterSystem = World.GetOrCreateSystemManaged<WaterSystem>();
            m_PopulationSystem = World.GetOrCreateSystemManaged<PopulationToGridSystem>();
            m_ZoneSystem = World.GetOrCreateSystemManaged<Game.Prefabs.ZoneSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            m_OverlayInfomodeSystem = World.GetOrCreateSystemManaged<OverlayInfomodeSystem>();

            m_StopQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_StopChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });

            m_NodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_RoadEdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_WorkplaceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WorkProvider>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_BlockQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Block>(), ComponentType.ReadOnly<Cell>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_LineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_AllEdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            // Vehicle PREFABS, not vehicles: the capacity of a mode is a property of
            // the assets installed, and has to be readable before the player has built
            // a single line of that mode.
            CreatePrefabQueries();

            m_NodeChangedQuery = ChangedQuery(ComponentType.ReadOnly<Node>());
            m_EdgeChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            m_WorkplaceChangedQuery = ChangedQuery(ComponentType.ReadOnly<WorkProvider>());
            m_BlockChangedQuery = ChangedQuery(ComponentType.ReadOnly<Block>());

            m_CitizenQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<HouseholdMember>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_HouseholdQuery = LiveQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<Game.Buildings.PropertyRenter>());
            m_QueuedQuery = LiveQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<TripNeeded>(), ComponentType.ReadOnly<CurrentBuilding>());
            m_TravellingQuery = LiveQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<TravelPurpose>(), ComponentType.ReadOnly<CurrentTransport>());
            m_InsideQuery = LiveQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<CurrentBuilding>());

            m_WorkerLookup = GetComponentLookup<Worker>(isReadOnly: true);
            m_StudentLookup = GetComponentLookup<Game.Citizens.Student>(isReadOnly: true);
            m_TouristLookup = GetComponentLookup<TouristHousehold>(isReadOnly: true);
            m_NodeLookup = GetComponentLookup<Node>(isReadOnly: true);
            m_CurveLookup = GetComponentLookup<Curve>(isReadOnly: true);
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(isReadOnly: true);
            m_RoadDataLookup = GetComponentLookup<RoadData>(isReadOnly: true);

            m_TransformLookup = GetComponentLookup<Transform>(isReadOnly: true);
            m_PropertyRenterLookup = GetComponentLookup<Game.Buildings.PropertyRenter>(isReadOnly: true);
            m_ZoneDataLookup = GetComponentLookup<ZoneData>(isReadOnly: true);

            m_ActiveInfomodeQuery = GetEntityQuery(
                ComponentType.ReadOnly<SuitabilityInfomodeData>(),
                ComponentType.ReadOnly<InfomodeActive>());

            m_PlaceableInfoviewQuery = GetEntityQuery(ComponentType.ReadOnly<PlaceableInfoviewItem>());
            m_PlaceableInfoviewChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PlaceableInfoviewItem>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>() },
            });

            m_LastStopCount = m_StopQuery.CalculateEntityCount();

            var settings = Mod.Settings;
            if (settings is not null)
            {
                m_LastComputeSettings = ComputeSnapshot.Capture(settings);
                m_LastCombineSettings = CombineSnapshot.Capture(settings);
                m_Calibration.Deserialize(settings.RidershipData);
            }

            UpdateCalibrationStatus();
        }

        // Live entities only — the None clause every gathering query here carries.
        private EntityQuery LiveQuery(params ComponentType[] all)
        {
            return GetEntityQuery(new EntityQueryDesc
            {
                All = all,
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        private EntityQuery ChangedQuery(ComponentType required)
        {
            return GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { required },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnDestroy()
        {
            DiscardPendingCompute();
            DiscardPendingRoutes();
            DisposeMasks();
            m_RawTerms = null;
            m_Scores = null;
            m_ScoreScratch = null;
            m_TermScratch = null;
            m_Access = null;
            m_AccessInputs = null;
            m_WalkGraph = null;
            m_ObservedTrips.Clear();
            m_CurrentJourney.Clear();
            m_LastBuilding.Clear();
            m_Land = null;
            m_ExpandedCache = null;
            m_VanillaPlaceableInfoviews = null;

            // Leaving a city must not leave its numbers on the panel. LineHistory
            // already guards its own series against a save change; these strings had
            // no such guard and were shown against the next city until its first
            // refresh landed.
            s_RouteSummary = "No route suggestions yet.";
            s_RouteList = string.Empty;
            s_LineHealthList = string.Empty;
            s_DataCoverage = string.Empty;
            s_Equity = string.Empty;
            s_ImprovePlan = string.Empty;
            s_ImprovedLine = -1;
            s_ImprovedRouteDrawn = false;
            s_ImproveRequest = -1;
            s_HighlightedRoute = -1;
            s_SelectedRoute = -1;
            base.OnDestroy();
        }

        private void DisposeMasks()
        {
            if (m_Buildable.IsCreated)
            {
                m_Buildable.Dispose();
            }
            if (m_Components.IsCreated)
            {
                m_Components.Dispose();
            }
        }

        // The vehicle and passenger-line prefabs the fleet facts are read from.
        private void CreatePrefabQueries()
        {
            m_VehiclePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PublicTransportVehicleData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_LinePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TransportLineData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            if (settings is null)
            {
                return;
            }

            EnsurePrefabs();

            if (GameManager.instance == null || !GameManager.instance.gameMode.IsGame())
            {
                DiscardPendingCompute();
                DiscardPendingRoutes();
                m_RecomputeRequested = false;
                return;
            }

            EnsureInfoviewLinked();
            SweepPlaceableInfoviews();
            TrackInputChanges();
            HandleExportRequest();
            FinishRoutesIfReady(settings);
            if (!m_RoutesPending)
            {
                HandleImprovementRequest();
                HandleCalibrationRequests(settings);
                FinishComputeIfReady();
            }

            int activeLayers = ResolveActiveLayers(out long signature);
            bool active = activeLayers > 0;

            if (active != m_LastActive)
            {
                if (active)
                {
                    ScheduleRecompute(0f);
                }
                else
                {
                    m_RecomputeRequested = false;
                }

                m_LastActive = active;
            }

            var computeSettings = ComputeSnapshot.Capture(settings);
            if (active && !computeSettings.Equals(m_LastComputeSettings))
            {
                // Slope and mode change what counts as buildable, so the mask has to
                // go too, not just the scores.
                if (computeSettings.Mode != m_LastComputeSettings.Mode || computeSettings.MaxSlope != m_LastComputeSettings.MaxSlope)
                {
                    m_MaskDirty = true;
                }

                if (computeSettings.Mode != m_LastComputeSettings.Mode)
                {
                    m_RoadCacheDirty = true;
                }

                ScheduleRecompute(DebounceSeconds);
            }
            m_LastComputeSettings = computeSettings;

            MaybeRecombine(settings, active);

            int stopCount = m_StopQuery.CalculateEntityCount();
            if (stopCount != m_LastStopCount)
            {
                m_LastStopCount = stopCount;
                if (active)
                {
                    ScheduleRecompute(DebounceSeconds);
                }
            }
            else if (active && !m_StopChangedQuery.IsEmptyIgnoreFilter)
            {
                ScheduleRecompute(DebounceSeconds);
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (active && !m_JobPending && !m_RecomputeRequested && m_RawTerms is not null
                && now - m_LastComputeFinish >= PeriodicRefreshSeconds)
            {
                ScheduleRecompute(0f);
            }

            int2 currentSize = GetGridSize();
            if (active && !m_JobPending && (m_RawTerms is null || !m_PlayableGridAtCompute.Equals(currentSize)))
            {
                ScheduleRecompute(0f);
            }

            if (active && m_RecomputeRequested && !m_JobPending && !m_RoutesPending && now >= m_RecomputeAt)
            {
                if (StartCompute())
                {
                    m_RecomputeRequested = false;
                }
            }

            // Deliberately NOT gated on `active`. The window is a 24 GAME-HOUR
            // measurement, and `active` means the suitability heat map is currently
            // being drawn — a player has no reason to leave the map recoloured for a
            // game day, and the moment they switch it off the readings stopped. The
            // panel then showed line verdicts and "1 Messung" beside them, which is
            // exactly as broken as it sounds. Reading six lines is cheap; the route
            // pipeline below is what stays gated.
            if (now - m_LastLineSample >= DemandRefreshSeconds && !m_RoutesPending)
            {
                m_LastLineSample = now;
                RefreshLineHealth();
            }

            // Also ungated on `active`: a journey not seen is demand not counted.
            if (now - m_LastTripObservation >= TripObservationSeconds)
            {
                m_LastTripObservation = now;
                ObserveTrips();
            }

            if (!m_RoutesPending)
            {
                MaybeUpdateTravelDemand(settings, active, now);
                SampleRidership(settings, now);
            }

            ApplyOverlayState(active, signature);
        }

        // A changed term weight re-combines the existing terms without a new compute.
        // The recombine writes the score field in place, which the route worker reads,
        // so while a pass is pending the change waits — and stays noticed, because the
        // snapshot is only recorded once the recombine has run.
        private void MaybeRecombine(Setting settings, bool active)
        {
            if (m_RoutesPending)
            {
                return;
            }

            var combineSettings = CombineSnapshot.Capture(settings);
            if (active && !combineSettings.Equals(m_LastCombineSettings) && m_RawTerms is not null)
            {
                RecombineAndNormalize();
            }

            m_LastCombineSettings = combineSettings;
        }

        // The demand pipeline needs the per-tile terms to exist (for the served
        // discount and stop snapping), so it always runs after a compute has landed.
        private void MaybeUpdateTravelDemand(Setting settings, bool active, float now)
        {
            if (!active || m_RawTerms is null || m_JobPending)
            {
                return;
            }

            bool objectiveChanged = settings.Objective != m_LastObjective || settings.RouteCount != m_LastRouteCount;
            bool due = now - m_LastDemandRefresh >= DemandRefreshSeconds;
            if (!objectiveChanged && !due && m_ZoneFlows.Count > 0)
            {
                return;
            }

            m_LastObjective = settings.Objective;
            m_LastRouteCount = settings.RouteCount;

            // Changing only the objective re-grows routes from the flow already
            // assigned; no need to walk every citizen again.
            if (objectiveChanged && !due && m_ZoneFlows.Count > 0 && m_RoadGraph.Graph is not null)
            {
                _ = StartRoutePass(settings, m_IntensityGrid, m_ScoreWorldMin, tripCount: -1, assignedPairs: -1, totalZoneWeight: 0f);
                return;
            }

            float2 mapSize = new float2(m_IntensityGrid.x, m_IntensityGrid.y) * TileSize;
            UpdateTravelDemand(settings, m_IntensityGrid, m_ScoreWorldMin, mapSize);
            LogRoutes();
        }

        // Change tags live for a single frame, so the caches must be invalidated
        // from a per-frame check rather than sampled when a compute starts.
        private void TrackInputChanges()
        {
            if (!m_NodeChangedQuery.IsEmptyIgnoreFilter || !m_EdgeChangedQuery.IsEmptyIgnoreFilter)
            {
                m_RoadCacheDirty = true;
                m_GraphDirty = true;
            }

            if (!m_WorkplaceChangedQuery.IsEmptyIgnoreFilter)
            {
                m_WorkplaceCacheDirty = true;
            }

            if (!m_BlockChangedQuery.IsEmptyIgnoreFilter)
            {
                m_ZoneCacheDirty = true;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            // Measured from the last actual REBUILD, not the last compute. Stamping
            // this per compute would keep pushing the deadline out every ten seconds
            // and the backstop would never fire at all.
            if (now - m_LastCollectionRebuild >= CollectionRefreshSeconds)
            {
                m_RoadCacheDirty = true;
                m_WorkplaceCacheDirty = true;
                m_ZoneCacheDirty = true;
            }

            if (now - m_LastMaskRefresh >= MaskRefreshSeconds)
            {
                m_MaskDirty = true;
            }
        }

        private void ScheduleRecompute(float delaySeconds)
        {
            bool wasRequested = m_RecomputeRequested;
            m_RecomputeRequested = true;
            float targetTime = UnityEngine.Time.realtimeSinceStartup + delaySeconds;
            if (delaySeconds <= 0f || !wasRequested)
            {
                m_RecomputeAt = targetTime;
                return;
            }

            if (targetTime > m_RecomputeAt)
            {
                m_RecomputeAt = targetTime;
            }
        }

        // ---- prefab registration -------------------------------------------

        private void EnsurePrefabs()
        {
            if (m_PrefabsAdded || m_PrefabSystem is null)
            {
                return;
            }

            // Must happen before our infoview exists, or the snapshot already
            // contains the auto-activation entries we are trying to undo.
            SnapshotVanillaPlaceableInfoviews();

            var infomodeInfos = new List<InfomodeInfo>();
            SuitabilityLayer[] layers = SuitabilityLayers.All;
            for (int i = 0; i < layers.Length; i++)
            {
                SuitabilityLayer layer = layers[i];
                SuitabilityInfomodePrefab prefab = CreateLayerPrefab(layer);
                m_LayerPrefabs[layer] = prefab;

                if (!m_PrefabSystem.AddPrefab(prefab))
                {
                    DeferredLog.Warn($"Failed to register infomode prefab for layer {layer}.");
                }

                var info = new InfomodeInfo();
                SetField(info, "m_Mode", prefab);
                SetField(info, "m_Priority", InfomodePriority - i);
                // Only the combined score is on by default; the rest are opt-in so
                // opening the infoview does not immediately burn all four channels.
                SetField(info, "m_Supplemental", layer != SuitabilityLayer.Score);
                SetField(info, "m_Optional", value: false);
                infomodeInfos.Add(info);
            }

            m_InfoviewPrefab = PrefabBase.Create<InfoviewPrefab>("StationSuitabilityOverlay");
            SetField(m_InfoviewPrefab, "m_Infomodes", infomodeInfos.ToArray());
            SetField(m_InfoviewPrefab, "m_IconPath", "coui://stationsuitabilityoverlay/StationSuitability.svg");
            SetField(m_InfoviewPrefab, "m_Priority", 900);
            SetField(m_InfoviewPrefab, "m_Group", 0);
            SetField(m_InfoviewPrefab, "m_DefaultColor", new Color(0.35f, 0.35f, 0.38f, 1f));
            SetField(m_InfoviewPrefab, "m_SecondaryColor", new Color(0.5f, 0.5f, 0.55f, 1f));
            // The view MUST stay valid and non-editor. InfoviewsUISystem.BindInfoviews
            // skips invalid views, which is tempting as a way to keep this out of the
            // Infoansicht menu — but ToolSystem.SetInfoview only activates a view's
            // infomodes while ToolSystem.activeInfoview is non-null, and that getter
            // returns null for an invalid view, so the heat map would never draw. The
            // menu row is hidden in the UI module instead (HideInfoviewMenuEntry).
            SetField(m_InfoviewPrefab, "m_Editor", value: false);
            SetField(m_InfoviewPrefab, "<isValid>k__BackingField", value: true);

            if (!m_PrefabSystem.AddPrefab(m_InfoviewPrefab))
            {
                DeferredLog.Warn("Failed to register infoview prefab.");
            }

            m_ToolSystem.EventInfomodesChanged?.Invoke();
            m_PrefabsAdded = true;
            DeferredLog.Info($"Registered {layers.Length} suitability infomodes and the infoview prefab.");
        }

        private static SuitabilityInfomodePrefab CreateLayerPrefab(SuitabilityLayer layer)
        {
            var prefab = PrefabBase.Create<SuitabilityInfomodePrefab>(SuitabilityLayers.NameOf(layer));
            SuitabilityLayers.ColorsOf(layer, out Color low, out Color medium, out Color high);

            SetField(prefab, "m_Priority", InfomodePriority);
            SetField(prefab, "editor", value: false);
            SetField(prefab, "m_Low", low);
            SetField(prefab, "m_Medium", medium);
            SetField(prefab, "m_High", high);
            SetField(prefab, "m_Steps", layer == SuitabilityLayer.Sites ? 4 : 16);
            SetField(prefab, "m_LegendType", GradientLegendType.Gradient);
            SetField(prefab, "m_LowLabelId", "StationSuitabilityOverlay.Legend.Low");
            SetField(prefab, "m_MediumLabelId", "StationSuitabilityOverlay.Legend.Medium");
            SetField(prefab, "m_HighLabelId", "StationSuitabilityOverlay.Legend.High");
            return prefab;
        }

        // ToolSystem.GetInfomodes reads the infoview ENTITY's InfoviewMode buffer,
        // not the managed prefab. For runtime-registered prefabs that buffer can end
        // up missing or empty, which leaves the infoview panel without rows and
        // prevents activation entirely — so verify it and repair it if needed.
        private void EnsureInfoviewLinked()
        {
            if (m_InfoviewLinkChecked || m_InfoviewPrefab == null || m_LayerPrefabs.Count == 0)
            {
                return;
            }

            if (!m_PrefabSystem.TryGetEntity(m_InfoviewPrefab, out Entity infoviewEntity))
            {
                if (!m_InfoviewLinkWaitLogged)
                {
                    DeferredLog.Info("Infoview prefab entity not found yet; retrying.");
                    m_InfoviewLinkWaitLogged = true;
                }
                return;
            }

            // Resolve every layer's entity before touching the buffer, so a partial
            // link cannot latch the checked flag.
            var entities = new List<KeyValuePair<Entity, SuitabilityLayer>>();
            SuitabilityLayer[] layers = SuitabilityLayers.All;
            for (int i = 0; i < layers.Length; i++)
            {
                if (!m_LayerPrefabs.TryGetValue(layers[i], out SuitabilityInfomodePrefab prefab) ||
                    !m_PrefabSystem.TryGetEntity(prefab, out Entity entity))
                {
                    if (!m_InfoviewLinkWaitLogged)
                    {
                        DeferredLog.Info("Infomode prefab entities not all present yet; retrying.");
                        m_InfoviewLinkWaitLogged = true;
                    }
                    return;
                }

                entities.Add(new KeyValuePair<Entity, SuitabilityLayer>(entity, layers[i]));
            }

            bool hadBuffer = EntityManager.HasComponent<InfoviewMode>(infoviewEntity);
            DynamicBuffer<InfoviewMode> buffer = hadBuffer
                ? EntityManager.GetBuffer<InfoviewMode>(infoviewEntity)
                : EntityManager.AddBuffer<InfoviewMode>(infoviewEntity);

            int added = 0;
            m_InfomodeLayers.Clear();
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i].Key;
                SuitabilityLayer layer = entities[i].Value;
                m_InfomodeLayers[entity] = layer;

                bool linked = false;
                for (int j = 0; j < buffer.Length; j++)
                {
                    if (buffer[j].m_Mode == entity)
                    {
                        linked = true;
                        break;
                    }
                }

                if (!linked)
                {
                    _ = buffer.Add(new InfoviewMode(
                        entity,
                        InfomodePriority - i,
                        supplemental: layer != SuitabilityLayer.Score,
                        optional: false));
                    added++;
                }
            }

            if (added > 0)
            {
                m_ToolSystem.EventInfomodesChanged?.Invoke();
            }

            DeferredLog.Info($"Infoview link check: buffer existed={hadBuffer}, added {(added).ToString(CultureInfo.InvariantCulture)} entries, {buffer.Length} total.");
            m_InfoviewLinkChecked = true;
        }

        private void SnapshotVanillaPlaceableInfoviews()
        {
            m_VanillaPlaceableInfoviews = new Dictionary<Entity, PlaceableInfoviewItem[]>();
            using var entities = m_PlaceableInfoviewQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                DynamicBuffer<PlaceableInfoviewItem> buffer = EntityManager.GetBuffer<PlaceableInfoviewItem>(entities[i], isReadOnly: true);
                if (buffer.Length == 0)
                {
                    continue;
                }

                var items = new PlaceableInfoviewItem[buffer.Length];
                for (int j = 0; j < buffer.Length; j++)
                {
                    items[j] = buffer[j];
                }

                m_VanillaPlaceableInfoviews[entities[i]] = items;
            }

            DeferredLog.Info($"Captured vanilla auto-activation for {m_VanillaPlaceableInfoviews.Count} placeable prefabs.");
        }

        // InfoviewInitializeSystem scores every registered infoview against every
        // placeable prefab to fill its PlaceableInfoviewItem buffer, which
        // ToolBaseSystem uses to auto-activate an infoview when the player selects
        // that asset. Our infomodes carry none of the vanilla match data, so our
        // infoview always scores a neutral 0 — which beats any asset whose vanilla
        // infomodes all score negative, making the overlay pop up for seemingly
        // random build-menu selections. Restore the pre-mod choice instead.
        private void SweepPlaceableInfoviews()
        {
            if (!m_InfoviewLinkChecked)
            {
                return;
            }

            int placeableCount = m_PlaceableInfoviewQuery.CalculateEntityCount();
            float now = UnityEngine.Time.realtimeSinceStartup;
            bool full = !m_PlaceableSweepDone
                || placeableCount != m_LastPlaceableCount
                || now - m_LastPlaceableSweep >= PlaceableReverifySeconds;

            if (!full && m_PlaceableInfoviewChangedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            if (!m_PrefabSystem.TryGetEntity(m_InfoviewPrefab, out Entity infoviewEntity))
            {
                return;
            }

            EntityQuery query = full ? m_PlaceableInfoviewQuery : m_PlaceableInfoviewChangedQuery;
            int restored = StripPlaceableInfoviewItems(query, infoviewEntity, out int cleared);
            if (!m_PlaceableSweepDone || restored > 0 || cleared > 0)
            {
                DeferredLog.Info($"Placeable infoview sweep ({(full ? "full" : "incremental")}): restored vanilla auto-activation on {(restored).ToString(CultureInfo.InvariantCulture)} prefabs, disabled it on {(cleared).ToString(CultureInfo.InvariantCulture)}.");
            }

            m_LastPlaceableCount = placeableCount;
            if (full)
            {
                m_LastPlaceableSweep = now;
            }

            m_PlaceableSweepDone = true;
        }

        private int StripPlaceableInfoviewItems(EntityQuery query, Entity infoviewEntity, out int cleared)
        {
            int restored = 0;
            cleared = 0;
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                DynamicBuffer<PlaceableInfoviewItem> buffer = EntityManager.GetBuffer<PlaceableInfoviewItem>(entity);
                if (buffer.Length == 0)
                {
                    continue;
                }

                if (buffer[0].m_Item == infoviewEntity)
                {
                    buffer.Clear();
                    if (m_VanillaPlaceableInfoviews is not null
                        && m_VanillaPlaceableInfoviews.TryGetValue(entity, out PlaceableInfoviewItem[] vanilla))
                    {
                        for (int j = 0; j < vanilla.Length; j++)
                        {
                            _ = buffer.Add(vanilla[j]);
                        }

                        restored++;
                    }
                    else
                    {
                        cleared++;
                    }

                    continue;
                }

                bool removed = false;
                for (int j = buffer.Length - 1; j >= 0; j--)
                {
                    if (buffer[j].m_Item == infoviewEntity || m_InfomodeLayers.ContainsKey(buffer[j].m_Item))
                    {
                        buffer.RemoveAt(j);
                        removed = true;
                    }
                }

                if (removed)
                {
                    cleared++;
                }
            }

            return restored;
        }

        // ---- rendering ------------------------------------------------------

        // Which layers the player has toggled on, and which terrain channel each
        // landed in. ToolSystem hands out m_Index = colorGroup * 4 + (1-based active
        // count) with NO bounds check, so a fifth active layer gets an index past
        // our four channels and must be dropped here.
        private readonly List<KeyValuePair<int, SuitabilityLayer>> m_ActiveChannels =
            new List<KeyValuePair<int, SuitabilityLayer>>();

        private int ResolveActiveLayers(out long signature)
        {
            m_ActiveChannels.Clear();
            signature = 0;

            if (m_ActiveInfomodeQuery.IsEmptyIgnoreFilter)
            {
                return 0;
            }

            using var entities = m_ActiveInfomodeQuery.ToEntityArray(Allocator.Temp);
            using var actives = m_ActiveInfomodeQuery.ToComponentDataArray<InfomodeActive>(Allocator.Temp);

            int skipped = 0;
            int unlinked = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_InfomodeLayers.TryGetValue(entities[i], out SuitabilityLayer layer))
                {
                    unlinked++;
                    continue;
                }

                int channel = actives[i].m_Index - 1;
                if (channel is < 0 or >= SuitabilityLayers.MaxActiveLayers)
                {
                    skipped++;
                    continue;
                }

                m_ActiveChannels.Add(new KeyValuePair<int, SuitabilityLayer>(channel, layer));
                signature |= ((long)((int)layer + 1)) << (channel * 8);
            }

            if (skipped > 0 && signature != m_ExpandedSignature)
            {
                DeferredLog.Warn($"{(skipped).ToString(CultureInfo.InvariantCulture)} suitability layer(s) skipped: the terrain overlay only has {SuitabilityLayers.MaxActiveLayers} channels. Turn one off to see another.");
            }

            // The query only ever holds our own infomodes, so reaching this point with
            // none resolved is a fault, not the overlay being off — and it is the one
            // fault that looks like a working overlay: the infoview is on, so
            // TerrainRenderSystem keeps painting our gradient, but no intensity is ever
            // written and the whole map sits at the low end of the ramp. It also stops
            // every compute, so the route suggestions go with it. Say so.
            if (m_LastLoggedIndex != m_ActiveChannels.Count)
            {
                if (m_ActiveChannels.Count > 0)
                {
                    DeferredLog.Info($"Active suitability layers: {m_ActiveChannels.Count}.");
                }
                else
                {
                    DeferredLog.Warn(
                        $"No suitability layer resolved from {(entities.Length).ToString(CultureInfo.InvariantCulture)} active infomode(s): " +
                        $"{(unlinked).ToString(CultureInfo.InvariantCulture)} not linked to a layer, " +
                        $"{(skipped).ToString(CultureInfo.InvariantCulture)} outside the {SuitabilityLayers.MaxActiveLayers} terrain channels. " +
                        "Nothing will be computed or drawn, and the map will show the low end of the gradient everywhere.");
                }

                m_LastLoggedIndex = m_ActiveChannels.Count;
            }

            return m_ActiveChannels.Count;
        }

        private void ApplyOverlayState(bool active, long signature)
        {
            bool applied = false;
            if (active && m_RawTerms is not null && m_OverlayInfomodeSystem is not null && CheckPipeline())
            {
                BuildExpandedCache(signature);
                applied = InjectOverlay();
            }

            if (applied != m_LastOverlayApplied)
            {
                DeferredLog.Info($"Overlay map {(applied ? "attached" : "detached")} (active={active}, data={(m_RawTerms is not null ? "yes" : "no")})");
                m_LastOverlayApplied = applied;
            }
        }

        // Verify the reflected members once and report loudly if a game update moved
        // them, rather than silently rendering nothing forever.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Reading the game's version string is only for the diagnostic below. " +
                "Any failure there must not stop the check from reporting what it found.")]
        private static bool CheckPipeline()
        {
            if (s_ReflectionChecked)
            {
                return s_GetTerrainTextureData is not null && s_TerrainTextureField != null;
            }

            s_ReflectionChecked = true;
            MethodInfo? getter = typeof(OverlayInfomodeSystem).GetMethod(
                "GetTerrainTextureData",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(int2) },
                modifiers: null);
            s_TerrainTextureField = typeof(OverlayInfomodeSystem).GetField(
                "m_TerrainTexture",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (getter != null)
            {
                s_GetTerrainTextureData = Delegate.CreateDelegate(
                    typeof(Func<OverlayInfomodeSystem, int2, NativeArray<byte>>),
                    getter,
                    throwOnBindFailure: false) as Func<OverlayInfomodeSystem, int2, NativeArray<byte>>;
            }

            if (s_GetTerrainTextureData is not null && s_TerrainTextureField != null)
            {
                return true;
            }

            string version = "unknown";
            try
            {
                version = Colossal.Core.Version.current.fullVersion;
            }
            catch
            {
                // Version lookup is best-effort diagnostics only.
            }

            s_PipelineStatus =
                "The overlay cannot draw: this game version moved the internals it renders through " +
                $"(game {version}). The mod needs an update.";
            DeferredLog.Error(
                "OverlayInfomodeSystem internals not found or the wrong shape " +
                $"(GetTerrainTextureData bound={s_GetTerrainTextureData is not null}, " +
                $"m_TerrainTexture={s_TerrainTextureField != null}) on game {version}; the overlay cannot render.");
            return false;
        }

        private void BuildExpandedCache(long signature)
        {
            int cells = m_IntensityGrid.x * m_IntensityGrid.y;
            if (cells <= 0)
            {
                return;
            }

            if (m_ExpandedCache is null || m_ExpandedCache.Length != cells * 4)
            {
                m_ExpandedCache = new byte[cells * 4];
                m_ExpandedSignature = -1;
            }

            if (m_ExpandedSignature == signature)
            {
                return;
            }

            Array.Clear(m_ExpandedCache, 0, m_ExpandedCache.Length);
            for (int a = 0; a < m_ActiveChannels.Count; a++)
            {
                int channel = m_ActiveChannels[a].Key;
                byte[] source = m_LayerIntensities[(int)m_ActiveChannels[a].Value];
                if (source is null || source.Length < cells)
                {
                    continue;
                }

                for (int i = 0; i < cells; i++)
                {
                    m_ExpandedCache[i * 4 + channel] = source[i];
                }
            }

            m_ExpandedSignature = signature;
        }

        // Feed intensities through OverlayInfomodeSystem's own terrain texture — the
        // exact path the vanilla heatmaps use. GetTerrainTextureData resizes the
        // texture, assigns it to TerrainRenderSystem.overrideOverlaymap and schedules
        // a clear only when the texture instance changed; ApplyOverlay completes that
        // job, then we copy our data in and re-upload. Runs every frame while active
        // because the vanilla system clears the override at the start of each frame.
        private bool InjectOverlay()
        {
            // CheckPipeline binds both reflected members before anything calls this.
            if (s_GetTerrainTextureData is null || s_TerrainTextureField is null)
            {
                return false;
            }

            NativeArray<byte> data = s_GetTerrainTextureData(m_OverlayInfomodeSystem, m_IntensityGrid);
            m_OverlayInfomodeSystem.ApplyOverlay();

            int expected = m_IntensityGrid.x * m_IntensityGrid.y * 4;
            if (data.Length != expected || m_ExpandedCache is null || m_ExpandedCache.Length != expected)
            {
                return false;
            }

            // Pattern-matched rather than cast: the texture is only created once the
            // game has sized it, and an unguarded cast turned "not ready yet" into an
            // exception on the render path.
            if (s_TerrainTextureField.GetValue(m_OverlayInfomodeSystem) is not Texture2D texture)
            {
                return false;
            }

            data.CopyFrom(m_ExpandedCache);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return true;
        }

        // ---- compute --------------------------------------------------------

        private int2 GetGridSize()
        {
            float2 playable = m_TerrainSystem is not null ? m_TerrainSystem.playableArea : new float2(0f, 0f);
            return SuitabilityInputs.GridDims(playable, TileSize);
        }

        private bool StartCompute()
        {
            var settings = Mod.Settings;
            if (m_JobPending || m_PopulationSystem is null || m_TerrainSystem is null || settings is null)
            {
                return false;
            }

            Dependency.Complete();

            // The population map is still what fixes the map's extent and grid, so the
            // score grid stays aligned with everything else that samples the world.
            CellMapData<PopulationCell> popData = m_PopulationSystem.GetData(readOnly: true, out JobHandle popDeps);
            if (popData.m_CellSize.x <= 0f || popData.m_CellSize.y <= 0f ||
                popData.m_TextureSize.x <= 0 || popData.m_TextureSize.y <= 0)
            {
                return false;
            }

            float2 mapSize = popData.m_CellSize * new float2(popData.m_TextureSize.x, popData.m_TextureSize.y);
            if (mapSize.x <= 0f || mapSize.y <= 0f)
            {
                return false;
            }

            popDeps.Complete();
            float2 worldMin = -mapSize * 0.5f;
            int2 gridSize = SuitabilityInputs.GridDims(mapSize, TileSize);

            EnsureMasks(settings, gridSize, worldMin);
            EnsureCollectionsCurrent(settings);
            if (m_WalkGraph is null)
            {
                return false;
            }

            WalkAccessInputs inputs = BuildAccessInputs(m_WalkGraph);
            byte[] buildable = m_Buildable.ToArray();
            int cls = SuitabilityWalkAccess.ClassOf(inputs.CatchmentMs, TransitModes.CatchmentMs(settings.Mode));
            var selfType = (int)SuitabilityInputs.TransportTypeOf(settings.Mode);
            float[] typeWeight = TypeWeights();
            int width = gridSize.x;
            int height = gridSize.y;
            float minX = worldMin.x;
            float minZ = worldMin.y;

            // Everything the task touches is captured here as plain arrays; nothing on
            // the worker reads ECS state.
            var box = new AccessBox();
            m_PendingBox = box;
            m_PendingAccess = System.Threading.Tasks.Task.Run(
                () => box.m_Output = SuitabilityWalkAccess.Run(inputs, width, height, minX, minZ, TileSize, buildable, cls, selfType, typeWeight),
                System.Threading.CancellationToken.None);
            m_PendingInputs = inputs;
            CaptureExportInputs(inputs, gridSize, worldMin, buildable, settings.Mode, cls, selfType, typeWeight);

            m_PendingGrid = gridSize;
            m_PendingWorldMin = worldMin;
            m_PendingStopCount = m_AllStopPositions.Count;
            m_PendingOrphanCount = m_LastOrphanCount;
            m_PendingJobSiteCount = m_JobPositions.Count;
            m_PendingHomeCount = m_HomePositions.Count;
            m_PendingZonedCount = m_FutureHomePositions.Count + m_FutureJobPositions.Count;
            m_JobPending = true;
            m_PlayableGridAtCompute = GetGridSize();
            return true;
        }

        // Plain-array copies of the cached collections, in their cached order.
        private WalkAccessInputs BuildAccessInputs(WalkGraph graph)
        {
            var inputs = new WalkAccessInputs
            {
                Graph = graph,
                Homes = SourcesOf(m_HomePositions, m_HomeResidents),
                Jobs = SourcesOf(m_JobPositions, m_JobWorkers),
                Future = SourcesOf(m_FutureHomePositions, m_FutureHomeWeights, m_FutureJobPositions, m_FutureJobWeights),
                StopCount = m_AllStopPositions.Count,
                StopX = new float[m_AllStopPositions.Count],
                StopZ = new float[m_AllStopPositions.Count],
                StopType = m_AllStopTypes.ToArray(),
                TypeCount = (int)TransportType.Count,
                AccessMs = TransitModes.AccessWalkMs,
                TransferMs = TransitModes.TransferWalkMs,
                CatchmentMs = TransitModes.CatchmentClassesMs,
            };
            for (int i = 0; i < m_AllStopPositions.Count; i++)
            {
                inputs.StopX[i] = m_AllStopPositions[i].x;
                inputs.StopZ[i] = m_AllStopPositions[i].y;
            }

            return inputs;
        }

        private static WalkSources SourcesOf(List<float2> positions, List<float> weights)
        {
            return SourcesOf(positions, weights, new List<float2>(), new List<float>());
        }

        // Two lists concatenated in order — zoned homes then zoned workplaces for the
        // future term.
        private static WalkSources SourcesOf(List<float2> first, List<float> firstWeights, List<float2> second, List<float> secondWeights)
        {
            int count = first.Count + second.Count;
            var sources = new WalkSources { Count = count, X = new float[count], Z = new float[count], Weight = new float[count] };
            for (int i = 0; i < first.Count; i++)
            {
                sources.X[i] = first[i].x;
                sources.Z[i] = first[i].y;
                sources.Weight[i] = firstWeights[i];
            }

            for (int i = 0; i < second.Count; i++)
            {
                sources.X[first.Count + i] = second[i].x;
                sources.Z[first.Count + i] = second[i].y;
                sources.Weight[first.Count + i] = secondWeights[i];
            }

            return sources;
        }

        // Capacity weight per Game.Prefabs.TransportType index, from the loaded prefabs
        // (register A1.10). Zero for types the save has no vehicle for.
        private float[] TypeWeights()
        {
            var weights = new float[(int)TransportType.Count];
            for (int type = 0; type < weights.Length; type++)
            {
                weights[type] = StopWeightOf((TransportType)type);
            }

            return weights;
        }

        private void FinishComputeIfReady()
        {
            System.Threading.Tasks.Task? pending = m_PendingAccess;
            WalkAccessOutput? output = m_PendingBox?.m_Output;
            if (!m_JobPending || pending is null || !pending.IsCompleted)
            {
                return;
            }

            m_JobPending = false;
            m_PendingAccess = null;
            m_PendingBox = null;
            m_LastComputeFinish = UnityEngine.Time.realtimeSinceStartup;
            if (pending.IsFaulted || pending.IsCanceled || output is null)
            {
                // A fault here is a defect in the pure core, not a game condition, so
                // it is logged in full rather than swallowed; the previous map stays.
                DeferredLog.Error($"Access pass failed: {pending.Exception}");
                m_PendingInputs = null;
                return;
            }
            m_Access = output;
            m_AccessInputs = m_PendingInputs;
            m_PendingInputs = null;
            m_RawTerms = output.Terms;

            m_IntensityGrid = m_PendingGrid;
            m_ScoreWorldMin = m_PendingWorldMin;
            RecombineAndNormalize();
            // After the combine, so the exported score field is the one these terms
            // produce rather than the previous compute's.
            WriteExportIfCaptured();

            DeferredLog.Info(
                $"Overlay computed: grid {(m_PendingGrid.x).ToString(CultureInfo.InvariantCulture)}x{(m_PendingGrid.y).ToString(CultureInfo.InvariantCulture)}, " +
                $"walk network {(output.Result.Demand.Length > 0 ? m_AccessInputs?.Graph.NodeCount ?? 0 : 0).ToString(CultureInfo.InvariantCulture)} nodes " +
                $"({(m_EdgesWithoutPavement).ToString(CultureInfo.InvariantCulture)} edges without a pedestrian lane skipped), " +
                $"{(output.TilesOnNetwork).ToString(CultureInfo.InvariantCulture)} tiles within {(TransitModes.AccessWalkMs / 1000).ToString(CultureInfo.InvariantCulture)} s of a node, " +
                $"homes={(m_PendingHomeCount).ToString(CultureInfo.InvariantCulture)} ({(m_HouseholdsWithoutHome).ToString(CultureInfo.InvariantCulture)} households without a home skipped), " +
                $"jobSites={(m_PendingJobSiteCount).ToString(CultureInfo.InvariantCulture)}, zonedCells={(m_PendingZonedCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"servedStops={(m_PendingStopCount).ToString(CultureInfo.InvariantCulture)} (orphans ignored={(m_PendingOrphanCount).ToString(CultureInfo.InvariantCulture)}), " +
                $"sourcesOffNetwork={(output.Result.SourcesOffNetwork).ToString(CultureInfo.InvariantCulture)}, " +
                $"settled={(output.Result.Relaxations).ToString(CultureInfo.InvariantCulture)}, sites={(m_SiteCount).ToString(CultureInfo.InvariantCulture)}");
        }

        private void DiscardPendingCompute()
        {
            if (!m_JobPending)
            {
                return;
            }

            // A running task finishes on its own and is ignored, since nothing reads a
            // result whose pending fields were cleared.
            m_PendingAccess = null;
            m_PendingBox = null;
            m_PendingInputs = null;
            m_JobPending = false;
        }

        private void EnsureMasks(Setting settings, int2 gridSize, float2 worldMin)
        {
            int cells = gridSize.x * gridSize.y;
            bool resized = !m_MaskGrid.Equals(gridSize) || !m_Buildable.IsCreated || m_Buildable.Length != cells;
            if (!resized && !m_MaskDirty && m_MaskMode == settings.Mode && m_MaskSlope == settings.MaxSlope)
            {
                return;
            }

            if (resized || m_Land is null)
            {
                DisposeMasks();
                m_Buildable = new NativeArray<byte>(cells, Allocator.Persistent);
                m_Components = new NativeArray<int>(cells, Allocator.Persistent);
                m_Land = new byte[cells];
                m_MaskGrid = gridSize;
            }

            SuitabilityMasks.Build(
                m_TerrainSystem,
                m_WaterSystem,
                settings.Mode,
                settings.MaxSlope,
                gridSize,
                worldMin,
                TileSize,
                m_Buildable,
                m_Components,
                m_Land,
                out int componentCount);

            m_MaskDirty = false;
            m_MaskMode = settings.Mode;
            m_MaskSlope = settings.MaxSlope;
            m_LastMaskRefresh = UnityEngine.Time.realtimeSinceStartup;

            int buildableCount = 0;
            for (int i = 0; i < cells; i++)
            {
                if (m_Buildable[i] != 0)
                {
                    buildableCount++;
                }
            }

            DeferredLog.Info($"Terrain mask rebuilt: {(buildableCount).ToString(CultureInfo.InvariantCulture)}/{(cells).ToString(CultureInfo.InvariantCulture)} tiles buildable, {(componentCount).ToString(CultureInfo.InvariantCulture)} landmasses.");
        }

        // Rebuilds the cached collections only when change detection says they moved.
        // All of these walk large parts of the entity world on the main thread, and
        // the periodic overlay refresh would otherwise pay for them every ten seconds
        // for as long as the infoview stays open.
        private void EnsureCollectionsCurrent(Setting settings)
        {
            bool rebuilt = false;

            if (m_RoadCacheDirty || m_WalkGraph is null)
            {
                SuitabilityInputs.CollectWalkNetwork(
                    EntityManager, m_NodeQuery, m_AllEdgeQuery,
                    out float[] nodeX, out float[] nodeZ, out int[] edgeA, out int[] edgeB, out float[] edgeMetres,
                    out m_EdgesWithoutPavement);
                m_WalkGraph = WalkGraph.Build(nodeX, nodeZ, edgeA, edgeB, edgeMetres, edgeA.Length);
                m_RoadCacheDirty = false;
                rebuilt = true;
            }

            if (m_WorkplaceCacheDirty || m_JobPositions.Count == 0)
            {
                m_TransformLookup.Update(this);
                m_PropertyRenterLookup.Update(this);
                SuitabilityInputs.CollectWorkplaces(m_WorkplaceQuery, m_TransformLookup, m_PropertyRenterLookup, m_JobPositions, m_JobWorkers);
                // Homes move on the same cadence as workplaces: buildings.
                SuitabilityInputs.CollectResidents(
                    EntityManager, m_HouseholdQuery, m_PropertyRenterLookup, m_TransformLookup,
                    m_HomePositions, m_HomeResidents, out m_HouseholdsWithoutHome);
                m_WorkplaceCacheDirty = false;
                rebuilt = true;
            }

            if (m_ZoneCacheDirty)
            {
                rebuilt = true;
                m_ZoneDataLookup.Update(this);
                SuitabilityInputs.CollectZonedCells(
                    EntityManager,
                    m_BlockQuery,
                    m_ZoneSystem,
                    m_ZoneDataLookup,
                    m_FutureHomePositions,
                    m_FutureHomeWeights,
                    m_FutureJobPositions,
                    m_FutureJobWeights);
                m_ZoneCacheDirty = false;
            }

            // Stops depend on the selected mode and on line membership, both of
            // which change often enough that they are collected every compute.
            SuitabilityInputs.CollectStops(
                EntityManager,
                m_PrefabSystem,
                m_StopQuery,
                settings.Mode,
                StopWeightOf,
                m_AllStopPositions,
                m_AllStopTypes,
                out m_LastOrphanCount);

            if (rebuilt)
            {
                m_LastCollectionRebuild = UnityEngine.Time.realtimeSinceStartup;
            }
        }

        // Blends the cached raw terms into weighted scores, normalizes each layer,
        // and extracts the recommended sites. Runs after every compute and again
        // whenever only weights or the highlight share change.
        private void RecombineAndNormalize()
        {
            // Guard on the INPUT only. m_Scores and m_ScoreScratch are outputs that
            // EnsureScoreBuffers allocates below, so testing them here made that call
            // unreachable and turned this whole pass into a permanent no-op: no
            // intensities were ever written, no sites extracted, and the term caps
            // stayed at zero, which starved the corridor seeds too.
            if (m_RawTerms is null)
            {
                return;
            }

            var settings = Mod.Settings;
            if (settings is null)
            {
                return;
            }

            int totalCells = m_RawTerms.Length;
            EnsureScoreBuffers(totalCells, out float[] scores, out float[] scoreScratch);

            // Each unbounded term is normalized against a high percentile of its own
            // positive values, so W1..W5 behave as real relative weights. Without
            // this, population counts drown the bounded penalty and access terms.
            m_DemandCap = TermPercentile(SuitabilityLayer.Demand, totalCells);
            m_JobsCap = TermPercentile(SuitabilityLayer.Jobs, totalCells);
            m_FutureCap = TermPercentile(SuitabilityLayer.Future, totalCells);
            float invDemand = m_DemandCap > 0f ? 1f / m_DemandCap : 0f;
            float invJobs = m_JobsCap > 0f ? 1f / m_JobsCap : 0f;
            float invFuture = m_FutureCap > 0f ? 1f / m_FutureCap : 0f;

            // Each of these is null unless its layer is actually registered as an
            // infomode, because only a registered layer can ever reach a terrain
            // channel — and filling seven of them is seven passes of rounding and
            // clamping over every cell on the map, on every recompute.
            //
            // The demand layer used to bind SuitabilityLayer.TravelDemand, so the
            // residents term was written into the travel-demand channel and
            // SuitabilityLayer.Demand was never written at all; BuildDemandLayer then
            // overwrote the same buffer with the desire-line raster and whichever pass
            // ran last decided what the channel held.
            byte[]? demandLayer = TermLayer(SuitabilityLayer.Demand);
            byte[]? jobsLayer = TermLayer(SuitabilityLayer.Jobs);
            byte[]? coverageLayer = TermLayer(SuitabilityLayer.Coverage);
            byte[]? accessLayer = TermLayer(SuitabilityLayer.Access);
            byte[]? futureLayer = TermLayer(SuitabilityLayer.Future);
            byte[]? interchangeLayer = TermLayer(SuitabilityLayer.Interchange);
            byte[]? crossLayer = TermLayer(SuitabilityLayer.CrossCoverage);
            bool anyTermLayer = demandLayer is not null || jobsLayer is not null
                || coverageLayer is not null || accessLayer is not null || futureLayer is not null
                || interchangeLayer is not null || crossLayer is not null;

            // Both cross-mode terms are expressed RELATIVE to the mode being placed,
            // so a bus gains a lot from sitting at a metro station while a metro
            // gains comparatively little from sitting at a bus stop. That asymmetry
            // is the feeder relationship: the smaller mode should come to the trunk.
            float selfWeight = math.max(0.1f, StopWeightOf(SuitabilityInputs.TransportTypeOf(settings.Mode)));
            float invSelf = 1f / selfWeight;

            // Pinned here rather than read from `settings` at query time: a score is
            // only comparable with the map it was combined into, and the panel's mode,
            // weights and radii can all move between a compute and a suggestion.
            m_ScoredMode = settings.Mode;
            m_ScoredInvSelf = invSelf;
            m_ScoredDemandWeight = settings.W1;
            m_ScoredJobsWeight = settings.W2;
            m_ScoredCoverageWeight = settings.W3;
            m_ScoredAccessWeight = settings.W4;
            m_ScoredFutureWeight = settings.W5;
            m_ScoredInterchangeWeight = settings.W6;
            m_ScoredCrossWeight = settings.W7;
            m_ScoredInvDemand = invDemand;
            m_ScoredInvJobs = invJobs;
            m_ScoredInvFuture = invFuture;

            int[]? tileNode = m_Access?.TileNode;
            for (int i = 0; i < totalCells; i++)
            {
                SuitabilityCell cell = m_RawTerms[i];
                // A tile with no network node within the access walk is not a place a
                // stop can be reached from (register A1.5): its score is zero whatever
                // the terms say.
                bool onNetwork = tileNode is not null && i < tileNode.Length && tileNode[i] >= 0;
                scores[i] = onNetwork ? CombineCell(in cell, invSelf) : 0f;

                // The per-term layers show the raw inputs, unweighted, so they stay
                // meaningful when a weight is set to zero.
                if (anyTermLayer)
                {
                    WriteTerm(demandLayer, i, SuitabilityScoring.Saturate(cell.m_Demand * invDemand));
                    WriteTerm(jobsLayer, i, SuitabilityScoring.Saturate(cell.m_Jobs * invJobs));
                    WriteTerm(coverageLayer, i, CoverageShare(cell.m_Coverage));
                    WriteTerm(accessLayer, i, cell.m_Access);
                    WriteTerm(futureLayer, i, SuitabilityScoring.Saturate(cell.m_Future * invFuture));
                    WriteTerm(interchangeLayer, i, SuitabilityScoring.Saturate(cell.m_Interchange * invSelf));
                    WriteTerm(crossLayer, i, SuitabilityScoring.Saturate(cell.m_CrossCoverage * invSelf));
                }
            }

            SuitabilityScoring.NormalizeIntensities(
                scores,
                totalCells,
                settings.HighlightShare / 100f,
                IntensityGamma,
                m_LayerIntensities[(int)SuitabilityLayer.Score],
                scoreScratch);

            LogCombine(scores, totalCells);
            ExtractSites(settings);

            // Any layer's bytes may have changed, so force the interleaved buffer to
            // be rebuilt even if the active set is identical.
            m_ExpandedSignature = -1;
        }

        // The one formula that turns seven raw terms into a score, under the weights
        // and caps pinned by the last combine. The map's own cells and a query for a
        // different mode both come through here, so they cannot disagree.
        private float CombineCell(in SuitabilityCell cell, float invSelf)
        {
            float demand = SuitabilityScoring.Saturate(cell.m_Demand * m_ScoredInvDemand);
            float jobs = SuitabilityScoring.Saturate(cell.m_Jobs * m_ScoredInvJobs);
            float future = SuitabilityScoring.Saturate(cell.m_Future * m_ScoredInvFuture);
            return (m_ScoredDemandWeight * demand)
                + (m_ScoredJobsWeight * jobs)
                + (m_ScoredAccessWeight * cell.m_Access)
                + (m_ScoredFutureWeight * future)
                + SuitabilityScoring.ModeTerms(
                    CoverageShare(cell.m_Coverage), cell.m_Interchange, cell.m_CrossCoverage, invSelf,
                    m_ScoredCoverageWeight, m_ScoredInterchangeWeight, m_ScoredCrossWeight);
        }

        private static float CoverageShare(float coverage)
        {
            return math.min(coverage, SuitabilityWalkAccess.MaxCoveragePenalty) / SuitabilityWalkAccess.MaxCoveragePenalty;
        }

        // The combine pass is where a plausible wrong map is made: a term cap of zero
        // silently drops that term out of every score, and a score field with no
        // positive member yields no sites and no corridor seeds while still painting a
        // full-looking gradient. None of those numbers were visible anywhere, so a map
        // showing only road access read exactly like a working one.
        private void LogCombine(float[] scores, int totalCells)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            int positive = 0;
            for (int i = 0; i < totalCells; i++)
            {
                float score = scores[i];
                min = math.min(min, score);
                max = math.max(max, score);
                if (score > 0f)
                {
                    positive++;
                }
            }

            float residents = 0f;
            float jobsTotal = 0f;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (inputs is not null)
            {
                for (int i = 0; i < inputs.Homes.Count; i++)
                {
                    residents += inputs.Homes.Weight[i];
                }

                for (int i = 0; i < inputs.Jobs.Count; i++)
                {
                    jobsTotal += inputs.Jobs.Weight[i];
                }
            }

            DeferredLog.Info(
                $"Combine: caps demand={(m_DemandCap).ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"jobs={(m_JobsCap).ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"future={(m_FutureCap).ToString("F1", CultureInfo.InvariantCulture)} " +
                "(a cap of 0 means that term is zero everywhere and drops out of the score); " +
                $"scores min={(min).ToString("F3", CultureInfo.InvariantCulture)} max={(max).ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"{(positive).ToString(CultureInfo.InvariantCulture)}/{(totalCells).ToString(CultureInfo.InvariantCulture)} positive; " +
                $"source totals residents={(residents).ToString("F0", CultureInfo.InvariantCulture)}, jobs={(jobsTotal).ToString("F0", CultureInfo.InvariantCulture)}");
        }

        // Returns the two buffers the combine pass writes through. Handing them back
        // is what keeps them out of that pass's entry guard, where a null test on them
        // made this allocation unreachable.
        //
        // All three co-allocated fields are in the condition: testing only m_Scores
        // left flow analysis trusting one of the three.
        private void EnsureScoreBuffers(int totalCells, out float[] scores, out float[] scoreScratch)
        {
            if (m_Scores is null || m_ScoreScratch is null || m_TermScratch is null
                || m_Scores.Length != totalCells)
            {
                m_Scores = new float[totalCells];
                m_ScoreScratch = new float[totalCells];
                m_TermScratch = new float[totalCells];
            }

            scores = m_Scores;
            scoreScratch = m_ScoreScratch;

            for (int i = 0; i < SuitabilityLayers.Count; i++)
            {
                if (m_LayerIntensities[i] is null || m_LayerIntensities[i].Length != totalCells)
                {
                    m_LayerIntensities[i] = new byte[totalCells];
                }
            }
        }

        // The intensity buffer for a layer, or null when that layer is not registered
        // as an infomode and therefore cannot be drawn. SuitabilityLayers.All decides
        // which are; EnsurePrefabs registers exactly those.
        private byte[]? TermLayer(SuitabilityLayer layer)
        {
            return m_LayerPrefabs.ContainsKey(layer) ? m_LayerIntensities[(int)layer] : null;
        }

        private static void WriteTerm(byte[]? layer, int index, float normalized)
        {
            if (layer is not null)
            {
                layer[index] = ToByte(normalized);
            }
        }

        private static byte ToByte(float normalized)
        {
            float clamped = SuitabilityScoring.Saturate(normalized);
            return (byte)math.round(clamped * 255f);
        }

        private float TermPercentile(SuitabilityLayer layer, int totalCells)
        {
            if (m_RawTerms is null || m_TermScratch is null || m_ScoreScratch is null)
            {
                return 0f;
            }

            // Bound to locals: the compiler discards a field's null-state across any
            // intervening call, and the loop below makes several.
            SuitabilityCell[] terms = m_RawTerms;
            float[] scratch = m_TermScratch;
            float[] percentileScratch = m_ScoreScratch;

            for (int i = 0; i < totalCells; i++)
            {
                SuitabilityCell cell = terms[i];
                switch (layer)
                {
                    case SuitabilityLayer.Jobs:
                        scratch[i] = cell.m_Jobs;
                        break;
                    case SuitabilityLayer.Future:
                        scratch[i] = cell.m_Future;
                        break;
                    default:
                        scratch[i] = cell.m_Demand;
                        break;
                }
            }

            return SuitabilityScoring.PositivePercentile(scratch, totalCells, TermCapPercentile, percentileScratch);
        }

        // Non-maximum suppression turns the gradient into discrete candidate sites,
        // then each survivor is re-scored with a real walk-distance expansion over
        // the landmass — affordable here precisely because there are only a handful.
        private void ExtractSites(Setting settings)
        {
            if (m_Scores is null)
            {
                return;
            }

            byte[]? sitesLayer = TermLayer(SuitabilityLayer.Sites);
            if (sitesLayer is not null)
            {
                Array.Clear(sitesLayer, 0, sitesLayer.Length);
            }

            m_SiteCount = 0;

            int width = m_IntensityGrid.x;
            int height = m_IntensityGrid.y;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (access is null || inputs is null)
            {
                return;
            }

            // Candidates are network nodes (register A2.1): every node whose own tile
            // is buildable and whose score for the map's mode is positive. Two
            // candidates conflict when the walk between them is shorter than the
            // mode's stop spacing (A2.2) — the same metric the stops are later set by.
            int wanted = math.min(settings.SiteCount, m_SiteIndices.Length);
            int separationMs = Math.Max(1, WalkGraph.WalkMilliseconds(TransitModes.StopSpacingFor(settings.Mode)));
            CollectNodeCandidates(access, inputs, width, height);

            // Exact selection: the set of at most `wanted` candidates with the largest
            // score sum under the spacing, or — should the node budget run out on a
            // pathological field — the best set found with a proven ceiling. The
            // greedy ranking it replaced left up to 1.3 % of score on a real city
            // (docs/correctness-claims.md C2.3).
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ExactSiteSolution exact = SuitabilityExactSites.SolveOnNetwork(
                inputs.Graph, m_SiteCandidateNodes, m_SiteCandidateScores, m_SiteCandidateCount,
                separationMs, wanted, SuitabilityExactSites.DefaultNodeBudget);
            stopwatch.Stop();
            m_SiteSeparationMs = separationMs;

            m_SiteCount = exact.Count;
            for (int i = 0; i < exact.Count; i++)
            {
                m_SiteIndices[i] = TileOfNode(inputs.Graph, exact.Indices[i], width, height);
                m_SiteScores[i] = exact.Scores[i];
            }

            LogSiteSelection(exact, separationMs, wanted, stopwatch.ElapsedMilliseconds);

            if (m_SiteCount == 0)
            {
                return;
            }

            if (sitesLayer is null)
            {
                LogSites();
                return;
            }

            // Paint each site as a small disc, brightest for the best rank, so the
            // layer reads as discrete markers rather than a gradient.
            const int radius = 2;
            for (int s = 0; s < m_SiteCount; s++)
            {
                int index = m_SiteIndices[s];
                int cx = index % width;
                int cy = index / width;
                byte intensity = (byte)math.clamp(255 - s * (200 / math.max(1, m_SiteCount)), 55, 255);

                for (int dy = -radius; dy <= radius; dy++)
                {
                    int y = cy + dy;
                    if (y < 0 || y >= height)
                    {
                        continue;
                    }
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= width)
                        {
                            continue;
                        }
                        if (dx * dx + dy * dy > radius * radius)
                        {
                            continue;
                        }
                        sitesLayer[x + y * width] = intensity;
                    }
                }
            }

            LogSites();
        }

        // Node candidates for the exact selection, scored exactly as a tile sitting on
        // the node would be (access 1), under the caps and weights of the last combine.
        private void CollectNodeCandidates(WalkAccessOutput access, WalkAccessInputs inputs, int width, int height)
        {
            WalkGraph graph = inputs.Graph;
            if (m_SiteCandidateNodes.Length < graph.NodeCount)
            {
                m_SiteCandidateNodes = new int[graph.NodeCount];
                m_SiteCandidateScores = new float[graph.NodeCount];
            }

            m_SiteCandidateCount = 0;
            for (int node = 0; node < graph.NodeCount; node++)
            {
                int tile = TileOfNode(graph, node, width, height);
                if (tile < 0 || tile >= m_Buildable.Length || m_Buildable[tile] == 0)
                {
                    continue;
                }

                SuitabilityCell cell = SuitabilityWalkAccess.NodeTerms(access.Result, node, access.Class, access.SelfType, access.TypeWeight);
                float score = CombineCell(in cell, m_ScoredInvSelf);
                if (score <= 0f)
                {
                    continue;
                }

                m_SiteCandidateNodes[m_SiteCandidateCount] = node;
                m_SiteCandidateScores[m_SiteCandidateCount] = score;
                m_SiteCandidateCount++;
            }
        }

        private int TileOfNode(WalkGraph graph, int node, int width, int height)
        {
            var position = new float2(graph.NodeX[node], graph.NodeZ[node]);
            int2 cell = SuitabilityInputs.WorldToCell(position, m_ScoreWorldMin, TileSize, new int2(width, height));
            return cell.x + cell.y * width;
        }

        private static void LogSiteSelection(ExactSiteSolution exact, int separationMs, int wanted, long elapsedMs)
        {
            double unit = Math.Pow(2.0, -exact.ScaleShift);
            string value = (exact.Value * unit).ToString("F4", CultureInfo.InvariantCulture);
            string closure = exact.Optimal
                ? "optimal (search closed)"
                : "best found, NOT proven optimal; ceiling " + (exact.UpperBound * unit).ToString("F4", CultureInfo.InvariantCulture);
            DeferredLog.Info(
                $"Site selection: {exact.Count.ToString(CultureInfo.InvariantCulture)} of up to {wanted.ToString(CultureInfo.InvariantCulture)} sites, "
                + $"spacing {(separationMs / 1000).ToString(CultureInfo.InvariantCulture)} s walk, "
                + $"{exact.Candidates.ToString(CultureInfo.InvariantCulture)} candidate network nodes, "
                + $"score sum {value} {closure}, "
                + $"{exact.Nodes.ToString(CultureInfo.InvariantCulture)} search nodes in {elapsedMs.ToString(CultureInfo.InvariantCulture)} ms"
                + (exact.WeightsExact ? string.Empty : " (integer weights floored: score spread beyond 2^33)"));

            if (exact.CandidatesTruncated)
            {
                DeferredLog.Warn("Site search hit its candidate budget; the reported sites may miss better ones.");
            }

            if (!exact.Optimal)
            {
                DeferredLog.Warn("Site search ran out of its node budget; the reported ranking is the best found, not proven optimal.");
            }
        }

        private static void Swap(float[] values, int a, int b)
        {
            float tmp = values[a];
            values[a] = values[b];
            values[b] = tmp;
        }

        // A site set differs if it picked different tiles, or if any score moved by
        // more than this fraction of its previous value.
        private const float SiteScoreLogThreshold = 0.05f;

        private bool SitesChangedMeaningfully()
        {
            if (m_LoggedSiteIndices is null || m_LoggedSiteScores is null)
            {
                return true;
            }

            if (m_LoggedSiteCount != m_SiteCount)
            {
                return true;
            }

            for (int s = 0; s < m_SiteCount; s++)
            {
                if (m_LoggedSiteIndices[s] != m_SiteIndices[s])
                {
                    return true;
                }

                float previous = m_LoggedSiteScores[s];
                float delta = math.abs(m_SiteScores[s] - previous);
                if (delta > math.max(1f, math.abs(previous) * SiteScoreLogThreshold))
                {
                    return true;
                }
            }

            return false;
        }

        private void LogSites()
        {
            // The overlay recomputes every ten seconds; only say something when the
            // recommendation actually changed.
            // Log when the recommendation actually changes. Scores count as changed
            // only past a relative threshold: a growing city nudges them by a
            // fraction of a percent every recompute, which is real but not worth
            // reporting, whereas the double-counting bug this guards against moved
            // them by multiples.
            if (!SitesChangedMeaningfully())
            {
                return;
            }

            if (m_LoggedSiteIndices is null || m_LoggedSiteIndices.Length != m_SiteIndices.Length)
            {
                m_LoggedSiteIndices = new int[m_SiteIndices.Length];
                m_LoggedSiteScores = new float[m_SiteScores.Length];
            }

            m_LoggedSiteCount = m_SiteCount;
            Array.Copy(m_SiteIndices, m_LoggedSiteIndices, m_SiteCount);
            Array.Copy(m_SiteScores, m_LoggedSiteScores, m_SiteCount);

            var builder = new StringBuilder();
            _ = builder.Append("Recommended sites (walk-distance ranked): ");
            for (int s = 0; s < m_SiteCount; s++)
            {
                int index = m_SiteIndices[s];
                int x = index % m_IntensityGrid.x;
                int y = index / m_IntensityGrid.x;
                float2 world = m_ScoreWorldMin + new float2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);
                if (s > 0)
                {
                    _ = builder.Append(", ");
                }

                _ = builder.Append('#');
                _ = builder.Append(s + 1);
                // float2 carries (x, z) in world space throughout this mod; the log
                // said "(x, y)" and invited the reader to look up the wrong axis.
                _ = builder.Append(" (x ");
                _ = builder.Append(((int)world.x).ToString(CultureInfo.InvariantCulture));
                _ = builder.Append(", z ");
                _ = builder.Append(((int)world.y).ToString(CultureInfo.InvariantCulture));
                _ = builder.Append(") score ");
                _ = builder.Append(m_SiteScores[s].ToString("F1", CultureInfo.InvariantCulture));
            }

            DeferredLog.Info(builder.ToString());
        }

        // ---- travel demand and routes ---------------------------------------

        // The whole demand pipeline: extract real journeys, aggregate them, load
        // them onto the road network, and grow route suggestions from the result.
        // Runs on its own slow cadence because it is far heavier than the per-tile
        // scoring — it walks every citizen and runs a shortest-path search per
        // origin zone.
        private void UpdateTravelDemand(Setting settings, int2 gridSize, float2 worldMin, float2 mapSize)
        {
            // What the refresh costs the frame, phase by phase: the one part of the
            // route pipeline still on the main thread, so its budget is logged.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            m_ZoneGrid = SuitabilityInputs.GridDims(mapSize, SuitabilityTravelDemand.ZoneSize);

            int tripCount;
            float totalWeight;
            var trips = new NativeQueue<Trip>(Allocator.TempJob);
            try
            {
                m_WorkerLookup.Update(this);
                m_StudentLookup.Update(this);
                m_TouristLookup.Update(this);
                m_PropertyRenterLookup.Update(this);
                m_TransformLookup.Update(this);

                var job = new ExtractTripsJob
                {
                    EntityType = GetEntityTypeHandle(),
                    CitizenType = GetComponentTypeHandle<Citizen>(isReadOnly: true),
                    HouseholdMemberType = GetComponentTypeHandle<HouseholdMember>(isReadOnly: true),
                    WorkerLookup = m_WorkerLookup,
                    StudentLookup = m_StudentLookup,
                    PropertyRenterLookup = m_PropertyRenterLookup,
                    TouristLookup = m_TouristLookup,
                    TransformLookup = m_TransformLookup,
                    WorkTripWeight = 1f,
                    // School trips are real transit demand but shorter and less
                    // peaked than commutes.
                    // Register A0.3: every purpose weighs the same.
                    SchoolTripWeight = 1f,
                    Trips = trips.AsParallelWriter(),
                };

                job.ScheduleParallel(m_CitizenQuery, Dependency).Complete();
                EnqueueObservedTrips(trips);
                totalWeight = SuitabilityTravelDemand.Aggregate(trips, worldMin, m_ZoneGrid, m_ZoneFlows, out tripCount, m_Journeys);
            }
            finally
            {
                trips.Dispose();
            }

            long extractMs = clock.ElapsedMilliseconds;
            BuildTransitModel(gridSize);
            MeasureEquity(settings);
            DiscountServedDemand(gridSize);
            m_UnservedTravelWeight = RemainingDemandWeight();
            BuildDemandLayer(settings, gridSize, worldMin);
            long modelMs = clock.ElapsedMilliseconds - extractMs;

            long networksMs = 0;
            if (m_GraphDirty || m_RoadGraph.Graph is null)
            {
                BuildNetworks(gridSize, worldMin);
                networksMs = clock.ElapsedMilliseconds - extractMs - modelMs;
            }

            int assignedPairs = 0;
            float assignedWeight = 0f;
            bool passStarted = false;
            if (m_RoadGraph.Graph is not null && m_ZoneNodes is not null)
            {
                // Generous cost ceiling: a trip longer than this is not a candidate
                // for a single transit line anyway.
                assignedPairs = m_RoadGraph.AssignFlow(m_ZoneFlows, m_ZoneNodes, 20000f, out assignedWeight);
                AssignLatticeFlow(m_RailNetwork, worldMin, m_ZoneFlows);

                // Every journey is offered to the water lattice too (register A3.4): a
                // ferry along a coast is judged like any other line, by the time it
                // saves and the seats it fills, not by whether the ends share a landmass.
                AssignLatticeFlow(m_WaterNetwork, worldMin, m_ZoneFlows);
                passStarted = StartRoutePass(settings, gridSize, worldMin, tripCount, assignedPairs, totalWeight);
            }

            m_LastDemandRefresh = UnityEngine.Time.realtimeSinceStartup;
            if (!passStarted)
            {
                UpdateRouteSummary(tripCount, assignedPairs);
            }

            DeferredLog.Info(
                $"Travel demand: trips={(tripCount).ToString(CultureInfo.InvariantCulture)} (observed shopping/leisure {(m_ObservedLastDemand).ToString(CultureInfo.InvariantCulture)} ×{(m_ObservedScaleLastDemand).ToString("F2", CultureInfo.InvariantCulture)} over {(LineHistory.GameHours(m_ObservedTrips.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours), " +
                $"weight={(totalWeight).ToString("F0", CultureInfo.InvariantCulture)}, zonePairs={m_ZoneFlows.Count}, " +
                $"assignedPairs={(assignedPairs).ToString(CultureInfo.InvariantCulture)}, assignedWeight={(assignedWeight).ToString("F0", CultureInfo.InvariantCulture)}, " +
                $"route pass {(passStarted ? "started on the worker" : "not started (no network)")}; " +
                $"main thread {(clock.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms (extract {(extractMs).ToString(CultureInfo.InvariantCulture)}, model {(modelMs).ToString(CultureInfo.InvariantCulture)}, " +
                $"networks {(networksMs).ToString(CultureInfo.InvariantCulture)}, assign {(clock.ElapsedMilliseconds - extractMs - modelMs - networksMs).ToString(CultureInfo.InvariantCulture)}); " +
                $"trip observation since the last refresh: {(m_ObserveCount).ToString(CultureInfo.InvariantCulture)} scans, " +
                $"mean {(m_ObserveCount > 0 ? m_ObserveMsSum / (double)m_ObserveCount : 0.0).ToString("F1", CultureInfo.InvariantCulture)} ms, max {(m_ObserveMsMax).ToString(CultureInfo.InvariantCulture)} ms");
            m_ObserveCount = 0;
            m_ObserveMsSum = 0;
            m_ObserveMsMax = 0;
            if (!passStarted)
            {
                LogSanityChecks(totalWeight);
            }
        }

        // Hands the route pipeline to a worker task. The fleet capacities are read here
        // because the first read walks the vehicle prefabs, which is ECS; afterwards the
        // worker only sees the cached copy.
        private bool StartRoutePass(Setting settings, int2 gridSize, float2 worldMin, int tripCount, int assignedPairs, float totalZoneWeight)
        {
            if (m_RoutesPending)
            {
                return false;
            }

            _ = ReadFleetFacts();
            var pass = new RoutePass
            {
                TripCount = tripCount,
                AssignedPairs = assignedPairs,
                TotalZoneWeight = totalZoneWeight,
            };
            m_PendingRoutePass = pass;
            m_RoutesPending = true;
            m_RoutePassStarted = UnityEngine.Time.realtimeSinceStartup;
            m_PendingRoutes = System.Threading.Tasks.Task.Run(
                () =>
                {
                    DeferredLog.Bind(pass.Log);
                    try
                    {
                        BuildRoutes(settings, gridSize, worldMin, pass);
                    }
                    finally
                    {
                        DeferredLog.Unbind();
                    }
                },
                System.Threading.CancellationToken.None);
            return true;
        }

        // Adopts a finished pass: its log lines first, in order, then the lists the
        // panel, the renderer and the export read, then the served-stop update the
        // kept suggestions imply. A faulted pass is logged in full and the previous
        // suggestions stay.
        private void FinishRoutesIfReady(Setting settings)
        {
            System.Threading.Tasks.Task? pending = m_PendingRoutes;
            RoutePass? pass = m_PendingRoutePass;
            if (!m_RoutesPending || pending is null || pass is null || !pending.IsCompleted)
            {
                return;
            }

            m_RoutesPending = false;
            m_PendingRoutes = null;
            m_PendingRoutePass = null;
            DeferredLog.Flush(pass.Log);
            float elapsed = UnityEngine.Time.realtimeSinceStartup - m_RoutePassStarted;
            if (pending.IsFaulted || pending.IsCanceled)
            {
                DeferredLog.Error($"Route pass failed after {(elapsed).ToString("F1", CultureInfo.InvariantCulture)} s; the previous suggestions stay: {pending.Exception}");
                return;
            }

            m_RouteCandidates.Clear();
            m_RouteCandidates.AddRange(pass.Candidates);
            m_Routes.Clear();
            m_Routes.AddRange(pass.Routes);
            m_AcceptedStops.Clear();
            m_AcceptedStops.AddRange(pass.AcceptedStops);
            m_AcceptedLines.Clear();
            m_AcceptedLines.AddRange(pass.AcceptedLines);
            m_LineSetSolution = pass.Solution;
            m_LineSetProblem = pass.Problem;
            m_LineSetResolved.Clear();
            m_LineSetResolved.AddRange(pass.Resolved);

            UpdateRouteSummary(pass.TripCount, pass.AssignedPairs);
            LogRoutes();
            if (pass.TripCount >= 0)
            {
                LogSanityChecks(pass.TotalZoneWeight);
            }

            DeferredLog.Info($"Route pass adopted: {(elapsed).ToString("F1", CultureInfo.InvariantCulture)} s on the worker, {(m_Routes.Count).ToString(CultureInfo.InvariantCulture)} suggestions");
        }

        private void DiscardPendingRoutes()
        {
            if (!m_RoutesPending)
            {
                return;
            }

            // The task finishes on its own and is ignored: nothing reads a pass whose
            // pending fields were cleared.
            m_PendingRoutes = null;
            m_PendingRoutePass = null;
            m_RoutesPending = false;
        }

        // Purposes that count as shopping or leisure (register A0.1). Working,
        // studying and going home are covered by the save's own home-work/school pairs;
        // service trips (hospital, mail, garbage, crime) are not passenger demand.
        private static bool IsShoppingOrLeisure(Purpose purpose)
        {
            return purpose is Purpose.Shopping or Purpose.Leisure or Purpose.Relaxing
                or Purpose.Sightseeing or Purpose.VisitAttractions;
        }

        // One scan of the live city, in two stages, both keyed per (citizen, purpose)
        // so one journey is recorded once however often it is seen:
        //
        //  A. Queued: a citizen still inside a building with a TripNeeded entry of a
        //     watched purpose. Origin (CurrentBuilding) and destination (the entry's
        //     m_TargetAgent) are both exact. Visible only until TripNeededSystem
        //     dispatches the trip — it runs every 16 frames over update-frame groups,
        //     so a second's sampling sees most but not all departures.
        //  B. Travelling: a citizen carrying TravelPurpose with a CurrentTransport. The
        //     TripNeeded entry is gone by then (the first live scan proved it: 700
        //     shopping travellers, none with an entry), so the destination is read from
        //     the travelling creature's Target, which decompiled ResidentAISystem
        //     rewrites only while boarding a vehicle that is itself the target and while
        //     diverted — hence the building-only filter and the per-purpose key. Origin
        //     = the building the citizen was last seen inside. Visible for the whole
        //     journey, so nothing stage A missed is lost.
        // The once-a-second scan's cost, folded into the next "Travel demand" log line
        // rather than logged sixty times a minute.
        private int m_ObserveCount;
        private long m_ObserveMsSum;
        private long m_ObserveMsMax;

        private void ObserveTrips()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = simulation?.frameIndex ?? 0u;
            m_TransformLookup.Update(this);
            m_PropertyRenterLookup.Update(this);
            RememberBuildings();

            var seen = new HashSet<Entity>();
            ObserveQueued(frame, seen);
            ObserveTravelling(frame, seen);

            // Journeys no longer under way: forget them so the next one is new.
            var ended = new List<Entity>();
            foreach (Entity citizen in m_CurrentJourney.Keys)
            {
                if (!seen.Contains(citizen))
                {
                    ended.Add(citizen);
                }
            }

            for (int i = 0; i < ended.Count; i++)
            {
                _ = m_CurrentJourney.Remove(ended[i]);
            }

            m_ObservedTrips.Prune(frame);

            m_ObserveCount++;
            m_ObserveMsSum += clock.ElapsedMilliseconds;
            m_ObserveMsMax = Math.Max(m_ObserveMsMax, clock.ElapsedMilliseconds);
        }

        private void RememberBuildings()
        {
            using var inside = m_InsideQuery.ToEntityArray(Allocator.Temp);
            using var buildings = m_InsideQuery.ToComponentDataArray<CurrentBuilding>(Allocator.Temp);
            for (int i = 0; i < inside.Length; i++)
            {
                m_LastBuilding[inside[i]] = buildings[i].m_CurrentBuilding;
            }
        }

        private void ObserveQueued(uint frame, HashSet<Entity> seen)
        {
            m_ObservedQueued = 0;
            Array.Clear(m_ObservedPurposeHistogram, 0, m_ObservedPurposeHistogram.Length);
            EntityTypeHandle entityType = GetEntityTypeHandle();
            BufferTypeHandle<TripNeeded> tripType = GetBufferTypeHandle<TripNeeded>(isReadOnly: true);
            ComponentTypeHandle<CurrentBuilding> buildingType = GetComponentTypeHandle<CurrentBuilding>(isReadOnly: true);
            using var chunks = m_QueuedQuery.ToArchetypeChunkArray(Allocator.Temp);
            for (int c = 0; c < chunks.Length; c++)
            {
                ArchetypeChunk chunk = chunks[c];
                NativeArray<Entity> entities = chunk.GetNativeArray(entityType);
                NativeArray<CurrentBuilding> buildings = chunk.GetNativeArray(ref buildingType);
                BufferAccessor<TripNeeded> trips = chunk.GetBufferAccessor(ref tripType);
                for (int i = 0; i < entities.Length; i++)
                {
                    DynamicBuffer<TripNeeded> queue = trips[i];
                    if (queue.Length == 0)
                    {
                        continue;
                    }

                    TripNeeded trip = queue[0];
                    if (!IsShoppingOrLeisure(trip.m_Purpose) || trip.m_TargetAgent == Entity.Null)
                    {
                        continue;
                    }

                    m_ObservedQueued++;
                    Entity citizen = entities[i];
                    _ = seen.Add(citizen);
                    if (m_CurrentJourney.TryGetValue(citizen, out byte current) && current == (byte)trip.m_Purpose)
                    {
                        continue;
                    }

                    m_CurrentJourney[citizen] = (byte)trip.m_Purpose;
                    RecordObservedTrip(buildings[i].m_CurrentBuilding, trip.m_TargetAgent, (byte)trip.m_Purpose, frame);
                }
            }
        }

        private void ObserveTravelling(uint frame, HashSet<Entity> seen)
        {
            m_ObservedTravelling = 0;
            m_ObservedTravellingWithBuilding = 0;
            m_ObservedTargetMissing = 0;
            m_ObservedTargetVehicle = 0;
            m_ObservedTargetOther = 0;
            using var travellers = m_TravellingQuery.ToEntityArray(Allocator.Temp);
            using var purposes = m_TravellingQuery.ToComponentDataArray<TravelPurpose>(Allocator.Temp);
            using var transports = m_TravellingQuery.ToComponentDataArray<CurrentTransport>(Allocator.Temp);
            for (int i = 0; i < travellers.Length; i++)
            {
                Purpose purpose = purposes[i].m_Purpose;
                m_ObservedPurposeHistogram[(byte)purpose]++;
                if (!IsShoppingOrLeisure(purpose))
                {
                    continue;
                }

                m_ObservedTravelling++;
                Entity citizen = travellers[i];
                _ = seen.Add(citizen);
                if (m_CurrentJourney.TryGetValue(citizen, out byte current) && current == (byte)purpose)
                {
                    continue;
                }

                if (!TryCreatureDestination(transports[i].m_CurrentTransport, out Entity target))
                {
                    continue;
                }

                m_ObservedTravellingWithBuilding++;
                m_CurrentJourney[citizen] = (byte)purpose;
                if (!m_LastBuilding.TryGetValue(citizen, out Entity origin))
                {
                    m_ObservedWithoutOrigin++;
                    continue;
                }

                RecordObservedTrip(origin, target, (byte)purpose, frame);
            }
        }

        // The travelling creature's Target, accepted only when it is a building or a
        // company renting one. A creature walking to its parked car carries the CAR as
        // its target (ResidentAISystem hands it to TryEnterVehicle); the car's own
        // Target is then the destination, so one hop through a vehicle is followed.
        // Anything else — a vehicle with no building target, a lane, nothing — is
        // counted by kind so the log says what the scan could not read.
        private bool TryCreatureDestination(Entity creature, out Entity target)
        {
            target = Entity.Null;
            if (creature == Entity.Null || !EntityManager.TryGetComponent(creature, out Game.Common.Target creatureTarget)
                || creatureTarget.m_Target == Entity.Null)
            {
                m_ObservedTargetMissing++;
                return false;
            }

            Entity candidate = creatureTarget.m_Target;
            if (IsBuildingLike(candidate))
            {
                target = candidate;
                return true;
            }

            if (EntityManager.HasComponent<Game.Vehicles.Vehicle>(candidate))
            {
                if (EntityManager.TryGetComponent(candidate, out Game.Common.Target vehicleTarget) && IsBuildingLike(vehicleTarget.m_Target))
                {
                    target = vehicleTarget.m_Target;
                    return true;
                }

                m_ObservedTargetVehicle++;
                return false;
            }

            m_ObservedTargetOther++;
            return false;
        }

        private bool IsBuildingLike(Entity entity)
        {
            return entity != Entity.Null
                && (EntityManager.HasComponent<Game.Buildings.Building>(entity) || m_PropertyRenterLookup.HasComponent(entity));
        }

        private void RecordObservedTrip(Entity origin, Entity target, byte purpose, uint frame)
        {
            if (!TryResolveBuildingPosition(origin, out float3 from) || !TryResolveBuildingPosition(target, out float3 to))
            {
                m_ObservedWithoutOrigin++;
                return;
            }

            // First seen already at the destination (the purpose stays on the citizen
            // while shopping): no journey to record.
            if (math.distancesq(new float2(from.x, from.z), new float2(to.x, to.z)) < 1f)
            {
                return;
            }

            m_ObservedTrips.Record(new ObservedTrip
            {
                m_Frame = frame,
                m_OriginX = from.x,
                m_OriginZ = from.z,
                m_DestinationX = to.x,
                m_DestinationZ = to.z,
                m_Purpose = purpose,
            });
        }

        private string PurposeHistogram()
        {
            var builder = new StringBuilder();
            for (int p = 0; p < m_ObservedPurposeHistogram.Length; p++)
            {
                if (m_ObservedPurposeHistogram[p] == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    _ = builder.Append(", ");
                }

                _ = builder.Append(((Purpose)p).ToString()).Append('=').Append(m_ObservedPurposeHistogram[p].ToString(CultureInfo.InvariantCulture));
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        // The same shape as ExtractTripsJob.TryResolvePosition: a building carries a
        // Transform itself, or a company rents one that does.
        private bool TryResolveBuildingPosition(Entity owner, out float3 position)
        {
            position = default;
            if (owner == Entity.Null)
            {
                return false;
            }

            if (m_TransformLookup.HasComponent(owner))
            {
                position = m_TransformLookup[owner].m_Position;
                return true;
            }

            if (m_PropertyRenterLookup.HasComponent(owner))
            {
                Entity property = m_PropertyRenterLookup[owner].m_Property;
                if (m_TransformLookup.HasComponent(property))
                {
                    position = m_TransformLookup[property].m_Position;
                    return true;
                }
            }

            return false;
        }

        // The observed window joins the save's home-work/school journeys in the same
        // queue, each observed journey weighted so the window reads as one day.
        private void EnqueueObservedTrips(NativeQueue<Trip> trips)
        {
            float scale = m_ObservedTrips.ScaleFor(LineHistory.FramesPerGameDay);
            m_ObservedLastDemand = m_ObservedTrips.Count;
            m_ObservedScaleLastDemand = scale;
            for (int i = 0; i < m_ObservedTrips.Count; i++)
            {
                ObservedTrip observed = m_ObservedTrips[i];
                var trip = new Trip
                {
                    m_Origin = new float2(observed.m_OriginX, observed.m_OriginZ),
                    m_Destination = new float2(observed.m_DestinationX, observed.m_DestinationZ),
                    m_Weight = scale,
                };
                if (math.distancesq(trip.m_Origin, trip.m_Destination) < 1f)
                {
                    continue;
                }

                trips.Enqueue(trip);
            }

            DeferredLog.Info(
                $"Journey scan: {(m_ObservedQueued).ToString(CultureInfo.InvariantCulture)} shopping/leisure trips queued inside buildings, " +
                $"{(m_ObservedTravelling).ToString(CultureInfo.InvariantCulture)} under way ({(m_ObservedTravellingWithBuilding).ToString(CultureInfo.InvariantCulture)} newly recorded from the creature's building target; " +
                $"targets unreadable this scan: none={(m_ObservedTargetMissing).ToString(CultureInfo.InvariantCulture)}, vehicle without building target={(m_ObservedTargetVehicle).ToString(CultureInfo.InvariantCulture)}, other={(m_ObservedTargetOther).ToString(CultureInfo.InvariantCulture)}); " +
                $"buildings remembered for {(m_LastBuilding.Count).ToString(CultureInfo.InvariantCulture)} citizens; purposes under way: {PurposeHistogram()}");
            if (m_ObservedTrips.EvictedSinceLastReport > 0 || m_ObservedTrips.DroppedAtCapSinceLastReport > 0 || m_ObservedWithoutOrigin > 0)
            {
                DeferredLog.Info(
                    $"Observed journeys: {(m_ObservedTrips.Count).ToString(CultureInfo.InvariantCulture)} held " +
                    $"(shopping {(m_ObservedTrips.CountOf((byte)Purpose.Shopping)).ToString(CultureInfo.InvariantCulture)}, " +
                    $"leisure {(m_ObservedTrips.CountOf((byte)Purpose.Leisure) + m_ObservedTrips.CountOf((byte)Purpose.Relaxing)).ToString(CultureInfo.InvariantCulture)}, " +
                    $"sightseeing {(m_ObservedTrips.CountOf((byte)Purpose.Sightseeing) + m_ObservedTrips.CountOf((byte)Purpose.VisitAttractions)).ToString(CultureInfo.InvariantCulture)}), " +
                    $"evicted={(m_ObservedTrips.EvictedSinceLastReport).ToString(CultureInfo.InvariantCulture)}, " +
                    $"droppedAtCap={(m_ObservedTrips.DroppedAtCapSinceLastReport).ToString(CultureInfo.InvariantCulture)}, " +
                    $"withoutKnownOrigin={(m_ObservedWithoutOrigin).ToString(CultureInfo.InvariantCulture)} (journeys seen before the citizen was ever seen inside a building)");
                m_ObservedTrips.ClearCounters();
                m_ObservedWithoutOrigin = 0;
            }
        }

        // Expected rider wait in seconds at a stop position, taken from the best line
        // that actually calls there — the same windowed headway the verdicts and the
        // transit router use. Zero when no collected line has a stop within reach,
        // which tells the calibration there is nothing honest to record here.
        private float ExpectedWaitAt(float2 position)
        {
            float best = 0f;
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                float wait = line.ExpectedWait;
                if (wait <= 0f)
                {
                    continue;
                }

                for (int j = 0; j < line.m_StopIndices.Count; j++)
                {
                    int index = line.m_StopIndices[j];
                    if (index < 0 || index >= m_TransitStops.Count)
                    {
                        continue;
                    }

                    if (math.distancesq(m_TransitStops[index], position) > StopMatchRadiusSq)
                    {
                        continue;
                    }

                    // The shortest wait wins: a rider at an interchange takes whichever
                    // service turns up first.
                    if (best <= 0f || wait < best)
                    {
                        best = wait;
                    }
                }
            }

            return best;
        }

        // A pass whose whole job is to disagree with the rest of the mod.
        //
        // The failure that matters here is not a crash — it is a plausible wrong number
        // reaching the map, and every number below has a range it cannot leave without
        // something upstream being broken. Each check states the invariant it is
        // testing, so a warning names the defect rather than reporting a symptom.
        // Anything this logs is a bug in the mod, not a property of the city.
        private void LogSanityChecks(float totalZoneWeight)
        {
            int complaints = 0;

            void Complain(string message)
            {
                complaints++;
                DeferredLog.Warn($"  SANITY: {message}");
            }

            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                LineHealth entry = m_LineHealth[i];
                string line = $"line {entry.m_Name} ({entry.m_Mode})";

                // Usage is a share of fleet capacity. Over 1 means passengers were
                // counted against the wrong capacity, or a window mixed two fleets.
                if (entry.m_Usage < 0f || entry.m_Usage > 1.5f || float.IsNaN(entry.m_Usage))
                {
                    Complain($"{line} usage {(entry.m_Usage).ToString("F2", CultureInfo.InvariantCulture)} is outside 0..1.5 — passengers are being divided by the wrong capacity");
                }

                if (entry.m_PeakUsage + 1e-3f < entry.m_Usage)
                {
                    Complain($"{line} peak usage {(entry.m_PeakUsage).ToString("F2", CultureInfo.InvariantCulture)} is below its mean {(entry.m_Usage).ToString("F2", CultureInfo.InvariantCulture)} — the window is not accumulating in order");
                }

                if (entry.m_Capacity > 0 && entry.m_Vehicles == 0)
                {
                    Complain($"{line} reports capacity {(entry.m_Capacity).ToString(CultureInfo.InvariantCulture)} with no vehicles");
                }

                // Half a headway. A wait past an hour means a non-seconds quantity has
                // been read as seconds — the accumulator trap this mod has hit before.
                if (entry.m_TypicalWait is > 3600f or < 0f)
                {
                    Complain($"{line} typical wait {(entry.m_TypicalWait).ToString("F0", CultureInfo.InvariantCulture)}s is not a plausible headway — check the units feeding it");
                }

                if (entry.m_WindowGameHours > 24.5f)
                {
                    Complain($"{line} window spans {(entry.m_WindowGameHours).ToString("F1", CultureInfo.InvariantCulture)} game hours, past the 24 h it is supposed to hold — eviction is not running");
                }
            }

            for (int i = 0; i < m_Routes.Count; i++)
            {
                SuggestedRoute route = m_Routes[i];
                string label = $"route #{(i + 1).ToString(CultureInfo.InvariantCulture)} ({route.Mode})";

                // Enabled demand is a share of the city's journeys. It cannot exceed
                // the total weight those journeys carry.
                if (totalZoneWeight > 0f && route.EnabledDemand > totalZoneWeight * 1.01f)
                {
                    Complain($"{label} enabled demand {(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)} exceeds the city's total travel weight {(totalZoneWeight).ToString("F0", CultureInfo.InvariantCulture)} — journeys are being counted more than once");
                }

                // A short line cannot be the thing that unlocks a large share of a
                // city's travel. The bound above was far too loose to catch it: a
                // 580 m, 3-stop bus stub was credited with 6853 against a city total
                // of 31856 — 21% of every journey — and passed silently.
                //
                // "Short" is relative to the city, not a fixed 2 km. Against the fixed
                // figure this fired on a 1524 m bus in Valmare that ran the length of
                // both its villages: in a city whose longest journey is 2 km, a 1.5 km
                // line genuinely does serve most of the travel, and the check called
                // the mod's own correct answer a defect. Measured against the longest
                // journey the city actually makes, the 580 m stub above is still 6% of
                // its city and still caught.
                float longestJourney = LongestJourneyMetres();
                if (totalZoneWeight > 0f
                    && longestJourney > 0f
                    && route.Length < longestJourney * ShortLineShareOfCity
                    && route.EnabledDemand > totalZoneWeight * ImplausibleDemandShare)
                {
                    Complain($"{label} is only {(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m yet is credited with enabling {((route.EnabledDemand / totalZoneWeight) * 100f).ToString("F0", CultureInfo.InvariantCulture)}% of the city's travel — a line this short cannot carry that, so the zone-to-stop remap is attaching journeys it does not serve");
                }

                if (route.Stops.Count < TransitModes.MinStops)
                {
                    Complain($"{label} has {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops, under the {(TransitModes.MinStops).ToString(CultureInfo.InvariantCulture)} a line needs — the shape gate was not applied after stop placement");
                }

                if (route.Stops.Count < 2)
                {
                    Complain($"{label} has {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops — a line needs at least two");
                }

                if (route.Vehicles <= 0)
                {
                    Complain($"{label} would run with no vehicles");
                }

                // The stop spacing the mode asked for, against what it actually got.
                // Wildly off means the stops were placed for a different mode — which
                // is what happens when a mode change skips its restop.
                if (route.Stops.Count >= 2)
                {
                    float spacing = route.Length / (route.Stops.Count - 1);
                    float want = TransitModes.StopSpacingFor(route.Mode);
                    if (spacing > want * 2.5f || spacing < want * 0.4f)
                    {
                        Complain($"{label} averages {(spacing).ToString("F0", CultureInfo.InvariantCulture)}m between stops but {route.Mode} spacing is {(want).ToString("F0", CultureInfo.InvariantCulture)}m — the stops were probably placed for another mode");
                    }
                }
            }

            DeferredLog.Info(complaints == 0
                ? "Sanity checks: all clear."
                : $"Sanity checks: {(complaints).ToString(CultureInfo.InvariantCulture)} problem(s) above are defects in the mod, not properties of the city.");

            LogSuggestionChurn();
        }

        // How much the suggestion list moved since the last refresh.
        //
        // A player cannot act on advice that changes every thirty seconds, and the
        // churn was invisible in the log: each refresh looked reasonable on its own
        // while the list went 0, 0, 0, 1, 1, 2 routes with entirely different termini
        // each time. Reported as a number so it can be watched rather than recalled.
        private void LogSuggestionChurn()
        {
            int held = 0;
            for (int i = 0; i < m_Routes.Count; i++)
            {
                SuggestedRoute route = m_Routes[i];
                if (route.Stops.Count < 2)
                {
                    continue;
                }

                float2 from = route.Stops[0];
                float2 to = route.Stops[route.Stops.Count - 1];
                for (int j = 0; j < m_PreviousRouteEnds.Count; j++)
                {
                    RouteEnds previous = m_PreviousRouteEnds[j];
                    // Same corridor if both ends land near where they were. The stops
                    // themselves shift a little between refreshes as scores move.
                    if (math.distancesq(previous.m_From, from) <= RouteSameEndsRadiusSq
                        && math.distancesq(previous.m_To, to) <= RouteSameEndsRadiusSq)
                    {
                        held++;
                        break;
                    }
                }
            }

            int before = m_PreviousRouteEnds.Count;
            DeferredLog.Info(
                $"Suggestion churn: {(held).ToString(CultureInfo.InvariantCulture)} of {m_Routes.Count} suggestions " +
                $"were also suggested last refresh (which offered {(before).ToString(CultureInfo.InvariantCulture)}). " +
                "A list that turns over every refresh is advice nobody can act on.");

            m_PreviousRouteEnds.Clear();
            for (int i = 0; i < m_Routes.Count; i++)
            {
                SuggestedRoute route = m_Routes[i];
                if (route.Stops.Count >= 2)
                {
                    m_PreviousRouteEnds.Add(new RouteEnds
                    {
                        m_From = route.Stops[0],
                        m_To = route.Stops[route.Stops.Count - 1],
                    });
                }
            }
        }

        // Reads the lines, folds this reading into the rolling window and re-judges
        // them. Cheap next to the route pipeline — it walks the lines and nothing else
        // — which is what lets it run on its own regardless of what is on screen.
        //
        // Idempotent within a simulation frame, so the route pipeline can call it
        // without the background tick making it happen twice.
        private void RefreshLineHealth()
        {
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = simulation?.frameIndex ?? 0u;
            if (m_ExistingLines.Count > 0 && frame == m_LastLineRefreshFrame)
            {
                return;
            }

            m_LastLineRefreshFrame = frame;
            SuitabilityLines.Collect(EntityManager, m_LineQuery, m_PrefabSystem, m_NameSystem,
                m_ExistingLines, m_TransitStops, m_StopIndices);
            RecordLineWindow();
            SuitabilityLines.Judge(m_ExistingLines, m_LineHealth);
            UpdateLineHealthText();
        }

        // Folds this collection's readings into the rolling window and hands each line
        // back its own averages.
        //
        // Stamped with SimulationSystem.frameIndex rather than the wall clock, because
        // the window is 24 GAME hours: frames stop while the game is paused and run
        // faster when the player fast forwards, and a real-time window would drain
        // itself in the pause menu and cover a quarter of a day at 4x.
        private void RecordLineWindow()
        {
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation is null)
            {
                return;
            }

            uint frame = simulation.frameIndex;

            // A paused game keeps handing back the same frame. Recording it repeatedly
            // would stack identical readings and let a pause decide the average.
            bool advanced = frame != m_LastHistoryFrame;
            if (advanced)
            {
                m_LastHistoryFrame = frame;
                m_LiveLineIds.Clear();
                for (int i = 0; i < m_ExistingLines.Count; i++)
                {
                    ExistingLine line = m_ExistingLines[i];
                    _ = m_LiveLineIds.Add(line.m_Id);
                    m_LineHistory.Record(line.m_Id, new LineObservation
                    {
                        m_Frame = frame,
                        m_Passengers = line.m_Passengers,
                        m_Capacity = line.m_Capacity,
                        m_IntervalSeconds = line.m_VehicleInterval,
                        m_Vehicles = line.m_Vehicles,
                    });
                }

                m_LineHistory.RetainOnly(m_LiveLineIds);
            }

            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                if (!m_LineHistory.TryAverage(line.m_Id, out LineAverage average))
                {
                    line.m_WindowSamples = 0;
                    continue;
                }

                line.m_WindowUsage = average.m_Usage;
                line.m_WindowPeakUsage = average.m_PeakUsage;
                line.m_WindowInterval = average.m_IntervalSeconds;
                line.m_WindowSamples = average.m_Samples;
                line.m_WindowGameHours = LineHistory.GameHours(average.m_SpanFrames);
            }

            // The widest coverage any line has, which is what the oldest reading in the
            // window buys us. Lines added later have less and say so on their own row.
            float coveredHours = 0f;
            int readings = 0;
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                coveredHours = math.max(coveredHours, line.m_WindowGameHours);
                readings = math.max(readings, line.m_WindowSamples);
            }

            s_DataCoverage =
                $"{coveredHours.ToString("F1", CultureInfo.InvariantCulture)}|" +
                $"{readings.ToString(CultureInfo.InvariantCulture)}|" +
                $"{LineHistory.GameHours(m_LineHistory.WindowFrames).ToString("F0", CultureInfo.InvariantCulture)}|" +
                $"{m_ObservedTrips.Count.ToString(CultureInfo.InvariantCulture)}|" +
                $"{LineHistory.GameHours(m_ObservedTrips.SpanFrames).ToString("F1", CultureInfo.InvariantCulture)}";

            DeferredLog.Info(
                $"Line window: frame={(frame).ToString(CultureInfo.InvariantCulture)} (advanced={advanced}), " +
                $"tracking {(m_LineHistory.TrackedLines).ToString(CultureInfo.InvariantCulture)} lines over " +
                $"{(LineHistory.GameHours(m_LineHistory.WindowFrames)).ToString("F0", CultureInfo.InvariantCulture)} game hours, " +
                $"{(LineHistory.MinSamplesForVerdict).ToString(CultureInfo.InvariantCulture)} readings needed before a verdict uses it, " +
                $"evicted={(m_LineHistory.EvictedSinceLastReport).ToString(CultureInfo.InvariantCulture)}, " +
                $"droppedAtCap={(m_LineHistory.DroppedAtCapSinceLastReport).ToString(CultureInfo.InvariantCulture)}");
            m_LineHistory.ClearCounters();
        }

        // Reads the existing transit system and turns it into a routable model, so a
        // journey can be tested against the network that actually exists rather than
        // against how close its ends happen to be to some stop.
        private void BuildTransitModel(int2 gridSize)
        {
            RefreshLineHealth();

            // A city with NO transit at all still gets a model, empty though it is.
            // Returning early here left m_TransitNetwork and m_BaselineSeconds null,
            // which is what ScoreCandidates guards on — so the transfer-scoring pass
            // never ran, every candidate kept an enabled demand of zero, and the
            // MinEnabledDemandShare gate then dropped the lot with "would improve only
            // 0.00% of unserved travel". The mod could not suggest a first line until
            // the player had already built one.
            //
            // The empty case is routable, not special: no stops means no walk edges and
            // a graph of zero nodes, every zone maps to no stop, no pair is routable,
            // and so every journey's baseline stays at "unreachable" — which is exactly
            // the baseline a first line has to be credited against.
            var xs = new float[m_TransitStops.Count];
            var zs = new float[m_TransitStops.Count];
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                xs[i] = m_TransitStops[i].x;
                zs[i] = m_TransitStops[i].y;
            }

            // Which modes serve each stop. A stop is in m_TransitStops only because a
            // line calls there, so every entry here is service a rider can actually use.
            var stopModes = new int[m_TransitStops.Count];
            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                ExistingLine line = m_ExistingLines[i];
                int bit = TransitModes.ModeBit(line.m_Mode);
                for (int k = 0; k < line.m_StopIndices.Count; k++)
                {
                    int stop = line.m_StopIndices[k];
                    if (stop >= 0 && stop < stopModes.Length)
                    {
                        stopModes[stop] |= bit;
                    }
                }
            }

            m_Interchanges = SuitabilityTransit.BuildInterchangeMap(
                xs, zs, stopModes, m_TransitStops.Count, TransferWalkRadius);

            List<TransitLine> transitLines = SuitabilityLines.ToTransitLines(m_ExistingLines);
            m_TransitNetwork = SuitabilityTransit.Build(xs, zs, m_TransitStops.Count, transitLines,
                TransferWalkRadius, SuitabilityTransit.DefaultBoardPenaltySeconds);
            m_TransitWorkspace ??= new DijkstraWorkspace(0);
            m_TransitWorkspace.Resize(m_TransitNetwork.Graph.NodeCount);

            MapZonesToStops(xs, zs);
            BuildPairArrays();

            int problems = 0;
            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                if (m_LineHealth[i].Severity > 0)
                {
                    problems++;
                }
            }

            DeferredLog.Info(
                $"Transit model: lines={m_ExistingLines.Count}, stops={m_TransitStops.Count}, " +
                $"graphNodes={(m_TransitNetwork.Graph.NodeCount).ToString(CultureInfo.InvariantCulture)}, routablePairs={(m_PairCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"linesNeedingAttention={(problems).ToString(CultureInfo.InvariantCulture)}");

            for (int i = 0; i < m_LineHealth.Count && i < 12; i++)
            {
                LineHealth entry = m_LineHealth[i];
                DeferredLog.Info(
                    $"Line {(entry.m_Index).ToString(CultureInfo.InvariantCulture)} ({entry.m_Mode}): {SuitabilityLineHealth.Describe(entry)} — " +
                    $"{(entry.m_Passengers).ToString(CultureInfo.InvariantCulture)}/{(entry.m_Capacity).ToString(CultureInfo.InvariantCulture)} aboard right now, " +
                    $"judged at {(entry.m_Usage * 100f).ToString("F1", CultureInfo.InvariantCulture)}% " +
                    $"(peak {(entry.m_PeakUsage * 100f).ToString("F1", CultureInfo.InvariantCulture)}%) " +
                    $"{(entry.m_WindowSamples > 0 ? $"from {entry.m_WindowSamples.ToString(CultureInfo.InvariantCulture)} readings over {entry.m_WindowGameHours.ToString("F1", CultureInfo.InvariantCulture)}h" : "from this reading alone")}, " +
                    $"{(entry.m_Vehicles).ToString(CultureInfo.InvariantCulture)}/{(entry.m_TargetVehicles).ToString(CultureInfo.InvariantCulture)} vehicles, typicalWait {(entry.m_TypicalWait).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                    $"{(entry.m_Stops).ToString(CultureInfo.InvariantCulture)} stops, {(entry.m_LengthKm).ToString("F1", CultureInfo.InvariantCulture)} km");
            }
        }

        // Nearest stop to each zone centre, within walking distance. A zone with no
        // stop nearby simply cannot use the network.
        private void MapZonesToStops(float[] xs, float[] zs)
        {
            int zoneCount = m_ZoneGrid.x * m_ZoneGrid.y;
            // Every co-allocated array is in the condition, or flow analysis only
            // trusts the one that was tested.
            if (m_ZoneStops is null || m_ZoneStopDistSq is null || m_ZoneCentreX is null
                || m_ZoneCentreZ is null || m_ZoneStops.Length != zoneCount)
            {
                m_ZoneStops = new int[zoneCount];
                m_ZoneStopDistSq = new float[zoneCount];
                m_ZoneCentreX = new float[zoneCount];
                m_ZoneCentreZ = new float[zoneCount];
            }

            float radiusSq = ZoneStopReachMetres * ZoneStopReachMetres;
            for (int zone = 0; zone < zoneCount; zone++)
            {
                float2 centre = SuitabilityTravelDemand.ZoneCentre(zone, m_ScoreWorldMin, m_ZoneGrid);
                int best = -1;
                float bestSq = radiusSq;
                for (int i = 0; i < xs.Length; i++)
                {
                    float dx = xs[i] - centre.x;
                    float dz = zs[i] - centre.y;
                    float distSq = dx * dx + dz * dz;
                    if (distSq < bestSq)
                    {
                        bestSq = distSq;
                        best = i;
                    }
                }

                m_ZoneStops[zone] = best;
                // Kept so a candidate's own stops can be judged against the incumbent
                // rather than only filling in zones that had nothing, and so the walk
                // to the stop can be charged rather than given away.
                m_ZoneStopDistSq[zone] = best >= 0 ? bestSq : 0f;
                m_ZoneCentreX[zone] = centre.x;
                m_ZoneCentreZ[zone] = centre.y;
            }
        }

        // Flattens the zone flows into the stop-indexed arrays the transit router
        // takes, keeping them grouped by origin so one search serves a run of pairs.
        private void BuildPairArrays()
        {
            if (m_ZoneStops is null)
            {
                return;
            }

            int[] zoneStops = m_ZoneStops;
            int count = m_ZoneFlows.Count;
            if (m_PairOrigins is null || m_PairDests is null || m_PairFlow is null
                || m_PairOrigins.Length < count)
            {
                m_PairOrigins = new int[count];
                m_PairDests = new int[count];
                m_PairFlow = new int[count];
            }

            m_PairCount = 0;
            for (int i = 0; i < count; i++)
            {
                ZoneFlow flow = m_ZoneFlows[i];
                int origin = zoneStops[flow.m_Origin];
                int destination = zoneStops[flow.m_Destination];
                if (origin < 0 || destination < 0 || origin == destination)
                {
                    continue;
                }

                m_PairOrigins[m_PairCount] = origin;
                m_PairDests[m_PairCount] = destination;
                m_PairFlow[m_PairCount] = i;
                m_PairCount++;
            }
        }

        // Unserved demand, decided by ROUTING each journey over the existing network
        // rather than by how close its ends are to a stop. A journey the network can
        // already carry within a reasonable time is discounted; one it cannot is left
        // at full weight to drive a suggestion.
        //
        // This replaces an endpoint-coverage approximation that under-discounted trips
        // between two well-served places that no single service connects.
        private void DiscountServedDemand(int2 gridSize)
        {
            if (m_RawTerms is null)
            {
                return;
            }

            if (m_TransitNetwork is null || m_TransitWorkspace is null || m_ZoneStopDistSq is null
                || m_PairOrigins is null || m_PairDests is null || m_PairFlow is null)
            {
                return;
            }

            // Every journey starts unreachable; the pass below fills in the ones the
            // network can carry. A candidate is later credited only for beating this.
            float[] zoneStopDistSq = m_ZoneStopDistSq;

            TransitNetwork network = m_TransitNetwork;
            DijkstraWorkspace workspace = m_TransitWorkspace;
            int[] pairOrigins = m_PairOrigins;
            int[] pairDests = m_PairDests;
            int[] pairFlow = m_PairFlow;

            if (m_ServedPairs is null || m_ServedSeconds is null || m_ServedScratch is null
                || m_ServedPairs.Length < m_PairCount)
            {
                m_ServedPairs = new int[m_PairCount];
                m_ServedSeconds = new float[m_PairCount];
                m_ServedScratch = new float[m_PairCount];
            }

            int[] servedPairIndex = m_ServedPairs;
            float[] servedSeconds = m_ServedSeconds;
            float[] medianScratch = m_ServedScratch;

            // First pass: find out which journeys the network carries, and how long it
            // takes over each. No weight is touched yet, because the ceiling the
            // discount is measured against comes from this whole set.
            int servedPairs = 0;
            int currentOrigin = -1;
            for (int i = 0; i < m_PairCount; i++)
            {
                int origin = pairOrigins[i];
                int destination = pairDests[i];

                // Pairs arrive grouped by origin, so one search serves a run of them.
                if (origin != currentOrigin)
                {
                    currentOrigin = origin;
                    workspace.Run(network.Graph, origin, MaxJourneySeconds);
                }

                if (!SuitabilityTransit.Inspect(network, workspace, origin, destination,
                        -1, out int boardings, out bool _, out float travelTime))
                {
                    continue;
                }

                if (boardings <= 0)
                {
                    continue;
                }

                // Door to door: the walk to the stop and from it are part of the
                // journey, and used to be free — so a stop 490 m away looked exactly as
                // good as one on the doorstep.
                ZoneFlow served = m_ZoneFlows[pairFlow[i]];
                float doorToDoor = travelTime
                    + WalkSeconds(zoneStopDistSq[served.m_Origin])
                    + WalkSeconds(zoneStopDistSq[served.m_Destination]);

                servedPairIndex[servedPairs] = pairFlow[i];
                servedSeconds[servedPairs] = doorToDoor;
                servedPairs++;
            }

            Array.Copy(servedSeconds, medianScratch, servedPairs);
            float ceiling = SuitabilityTransit.ServedCeiling(
                medianScratch, servedPairs, ServedCeilingMultiple,
                MaxJourneySeconds, MinPairsForServedMedian, out float medianSeconds);

            // Second pass: how much of each carried journey the network absorbs. One it
            // handles in a fraction of the city's typical transit journey drops out
            // almost entirely; one taking three times that keeps its whole weight,
            // because it still deserves a better option.
            float weightBefore = 0f;
            float weightAfter = 0f;
            for (int i = 0; i < servedPairs; i++)
            {
                ZoneFlow flow = m_ZoneFlows[servedPairIndex[i]];
                weightBefore += flow.m_Weight;
                flow.m_Weight *= SuitabilityScoring.Saturate(servedSeconds[i] / ceiling);
                weightAfter += flow.m_Weight;
                m_ZoneFlows[servedPairIndex[i]] = flow;
            }

            DeferredLog.Info(
                $"Served-demand discount: {(servedPairs).ToString(CultureInfo.InvariantCulture)} of {(m_PairCount).ToString(CultureInfo.InvariantCulture)} routable pairs already carried, " +
                $"median carried journey {(medianSeconds).ToString("F0", CultureInfo.InvariantCulture)}s -> ceiling {(ceiling).ToString("F0", CultureInfo.InvariantCulture)}s " +
                $"({(ceiling >= MaxJourneySeconds ? "the fixed hour: too few carried journeys for a median, or a slow network" : $"{ServedCeilingMultiple.ToString("F0", CultureInfo.InvariantCulture)}x this city's median")}); " +
                $"weight {(weightBefore).ToString("F0", CultureInfo.InvariantCulture)} -> {(weightAfter).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"({((weightBefore > 0f ? (1f - weightAfter / weightBefore) * 100f : 0f)).ToString("F0", CultureInfo.InvariantCulture)}% absorbed by existing lines)");
        }

        // What the zone flows still carry after the served-demand discount. Summed
        // rather than tracked, because the discount touches only the pairs the network
        // can route and leaves the rest at full weight.
        private float RemainingDemandWeight()
        {
            float remaining = 0f;
            for (int i = 0; i < m_ZoneFlows.Count; i++)
            {
                remaining += m_ZoneFlows[i].m_Weight;
            }

            return remaining;
        }

        // The longest journey the city actually makes, end to end in metres. Stands in
        // for the city's own scale: a map is mostly empty, and the span of the built
        // area is what decides whether a given line counts as short.
        //
        // Over the flows rather than over every pair of zones — one distance each, and
        // a zone nobody travels to says nothing about how far people go.
        private float LongestJourneyMetres()
        {
            if (m_ZoneCentreX is null || m_ZoneCentreZ is null)
            {
                return 0f;
            }

            float[] centreX = m_ZoneCentreX;
            float[] centreZ = m_ZoneCentreZ;
            float longest = 0f;
            for (int i = 0; i < m_ZoneFlows.Count; i++)
            {
                ZoneFlow flow = m_ZoneFlows[i];
                if (flow.m_Origin < 0 || flow.m_Origin >= centreX.Length
                    || flow.m_Destination < 0 || flow.m_Destination >= centreX.Length)
                {
                    continue;
                }

                float dx = centreX[flow.m_Origin] - centreX[flow.m_Destination];
                float dz = centreZ[flow.m_Origin] - centreZ[flow.m_Destination];
                longest = math.max(longest, math.sqrt((dx * dx) + (dz * dz)));
            }

            return longest;
        }

        // Passenger capacity of one vehicle of each mode, taken from the game's own
        // prefabs. This is what every rider floor is derived from, so it must be the
        // real figure and not a table in this mod that nothing keeps in step.
        //
        // What a stop of `type` is worth as a transfer partner: its vehicle's capacity
        // relative to a bus, from the loaded prefabs (register A1.10). The one owner of
        // that weight — the job's bucket weights, the combine's self weight and the
        // per-mode swap all call this, so they cannot disagree about what a train is
        // worth next to a bus stop. Logged once with the capacities it rests on.
        private float StopWeightOf(TransportType type)
        {
            if (m_TypeCapacities is null)
            {
                _ = ReadFleetFacts();
            }

            float[] byType = m_TypeCapacities ?? System.Array.Empty<float>();
            int index = (int)type;
            int bus = (int)TransportType.Bus;
            float capacity = index >= 0 && index < byType.Length ? byType[index] : 0f;
            float busCapacity = bus >= 0 && bus < byType.Length ? byType[bus] : 0f;
            return TransitModes.CapacityWeight(capacity, busCapacity);
        }

        // The game's own facts about each mode, read once from the loaded prefabs
        // (register A5.5, A6.x): the largest vehicle's seats (carriages included), its
        // acceleration and braking, and the passenger line prefab's default interval and
        // stop duration. Prefabs do not change while a save is loaded.
        private FleetFacts ReadFleetFacts()
        {
            if (m_FleetFacts is not null)
            {
                return new FleetFacts(m_FleetFacts);
            }

            var byMode = new ModeFacts[TransitModes.All.Length];
            for (int m = 0; m < byMode.Length; m++)
            {
                byMode[m] = new ModeFacts();
            }

            var byType = new float[(int)TransportType.Count];
            using (var entities = m_VehiclePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = entities[i];
                    if (!EntityManager.TryGetComponent(prefab, out PublicTransportVehicleData vehicle))
                    {
                        continue;
                    }

                    int capacity = vehicle.m_PassengerCapacity;
                    if (EntityManager.TryGetBuffer(prefab, isReadOnly: true, out DynamicBuffer<VehicleCarriageElement> carriages))
                    {
                        for (int c = 0; c < carriages.Length; c++)
                        {
                            VehicleCarriageElement carriage = carriages[c];
                            if (carriage.m_Prefab == Entity.Null
                                || !EntityManager.TryGetComponent(carriage.m_Prefab, out PublicTransportVehicleData unit))
                            {
                                continue;
                            }

                            capacity += unit.m_PassengerCapacity * carriage.m_Count.x;
                        }
                    }

                    ReadMotion(prefab, out float acceleration, out float braking);
                    int typeIndex = (int)vehicle.m_TransportType;
                    if (typeIndex >= 0 && typeIndex < byType.Length)
                    {
                        byType[typeIndex] = math.max(byType[typeIndex], capacity);
                    }

                    for (int m = 0; m < TransitModes.All.Length; m++)
                    {
                        ModePreset mode = TransitModes.All[m];
                        if (SuitabilityInputs.TransportTypeOf(mode) != vehicle.m_TransportType)
                        {
                            continue;
                        }

                        ModeFacts facts = byMode[(int)mode];
                        if (capacity > facts.Capacity)
                        {
                            facts.Capacity = capacity;
                            facts.Acceleration = acceleration;
                            facts.Braking = braking;
                        }
                    }
                }
            }

            using (var lines = m_LinePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!EntityManager.TryGetComponent(lines[i], out TransportLineData line) || !line.m_PassengerTransport)
                    {
                        continue;
                    }

                    for (int m = 0; m < TransitModes.All.Length; m++)
                    {
                        ModePreset mode = TransitModes.All[m];
                        ModeFacts facts = byMode[(int)mode];
                        if (SuitabilityInputs.TransportTypeOf(mode) != line.m_TransportType || facts.HeadwaySeconds > 0f)
                        {
                            continue;
                        }

                        facts.HeadwaySeconds = line.m_DefaultVehicleInterval;
                        facts.StopDurationSeconds = line.m_StopDuration;
                    }
                }
            }

            m_FleetFacts = byMode;
            m_TypeCapacities = byType;

            var result = new FleetFacts(byMode);
            var report = new StringBuilder("Fleet facts from the loaded prefabs (Game.Prefabs.PublicTransportVehicleData with carriages, CarData/TrainData/WatercraftData, TransportLineData): ");
            for (int m = 0; m < TransitModes.All.Length; m++)
            {
                ModePreset mode = TransitModes.All[m];
                ModeFacts facts = byMode[(int)mode];
                _ = report.Append(mode.ToString())
                    .Append(" seats ").Append(facts.Capacity.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", prefab interval ").Append(facts.HeadwaySeconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" s")
                    .Append(" (planning headway ").Append(result.HeadwayFor(mode).ToString("F0", CultureInfo.InvariantCulture)).Append(" s)")
                    .Append(", stop ").Append(facts.StopDurationSeconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" s")
                    .Append(", accel ").Append(facts.Acceleration.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(", brake ").Append(facts.Braking.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(" -> ").Append(result.DelayPerStopSeconds(mode).ToString("F0", CultureInfo.InvariantCulture)).Append(" s per stop; ");
            }

            _ = report.Append("seats 0 means no vehicle of that mode is installed and the mode is never chosen.");
            DeferredLog.Info(report.ToString());
            return result;
        }

        // Acceleration and braking of a vehicle prefab, whichever motion component it has.
        private void ReadMotion(Entity prefab, out float acceleration, out float braking)
        {
            if (EntityManager.TryGetComponent(prefab, out CarData car))
            {
                acceleration = car.m_Acceleration;
                braking = car.m_Braking;
            }
            else if (EntityManager.TryGetComponent(prefab, out TrainData train))
            {
                acceleration = train.m_Acceleration;
                braking = train.m_Braking;
            }
            else if (EntityManager.TryGetComponent(prefab, out WatercraftData boat))
            {
                acceleration = boat.m_Acceleration;
                braking = boat.m_Braking;
            }
            else
            {
                acceleration = 0f;
                braking = 0f;
            }
        }

        // Seconds spent walking to a stop that far away. The model's own walking speed,
        // the one it already uses between stops.
        private static float WalkSeconds(float distanceSq)
        {
            return (float)Math.Sqrt(distanceSq) / SuitabilityTransit.WalkSpeed;
        }

        // Builds the improvement plan for whichever line the panel asked about. Run
        // here rather than in the binding so it uses the same measurements the list
        // was built from.
        // Answers the panel's request immediately. It reads only the cached health and
        // line data, so there is no reason to make the player wait for the next demand
        // refresh — which is what made the button feel broken.
        private void HandleImprovementRequest()
        {
            if (s_ImproveRequest < 0 || m_LineHealth.Count == 0)
            {
                return;
            }

            int requested = s_ImproveRequest;
            s_ImproveRequest = -1;

            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                LineHealth health = m_LineHealth[i];
                if (health.m_Id != requested)
                {
                    continue;
                }

                // Matched by identity, not by position. The query returns lines in
                // whatever chunk order the ECS happens to have them in, so a positional
                // lookup silently pointed at a different line after a refresh — which
                // is why an improvement plan could end up sitting under the wrong name.
                ExistingLine? line = null;
                for (int k = 0; k < m_ExistingLines.Count; k++)
                {
                    if (m_ExistingLines[k].m_Id == health.m_Id)
                    {
                        line = m_ExistingLines[k];
                        break;
                    }
                }

                if (line is null)
                {
                    DeferredLog.Info($"Improvement requested for line id {(requested).ToString(CultureInfo.InvariantCulture)}, which no longer exists.");
                    return;
                }
                int perVehicle = health.m_Vehicles > 0 ? health.m_Capacity / health.m_Vehicles : health.m_Capacity;

                // Aim to fill about 70%: full enough to justify the service, with room
                // for the peaks the averages hide.
                SuitabilityLineHealth.ImprovePlan plan = SuitabilityLineHealth.Plan(
                    health, line.m_LengthMetres, line.m_StableDurationSeconds, perVehicle, 0.7f);
                s_ImprovePlan = SuitabilityLineHealth.PlanPayload(plan);
                s_ImprovedLine = health.m_Id;

                BuildImprovedRoute(health, line);

                DeferredLog.Info(
                    $"Improvement for \"{health.m_Name}\" ({health.m_Mode}): " +
                    $"{SuitabilityLineHealth.Improve(health, line.m_LengthMetres, line.m_StableDurationSeconds, perVehicle, 0.7f)} " +
                    $"[measured: {(health.m_Passengers).ToString(CultureInfo.InvariantCulture)}/{(health.m_Capacity).ToString(CultureInfo.InvariantCulture)} aboard, {(health.m_Vehicles).ToString(CultureInfo.InvariantCulture)}/{(health.m_TargetVehicles).ToString(CultureInfo.InvariantCulture)} veh, " +
                    $"typicalWait {(health.m_TypicalWait).ToString("F0", CultureInfo.InvariantCulture)}, {(health.m_Stops).ToString(CultureInfo.InvariantCulture)} stops, {(health.m_LengthKm).ToString("F1", CultureInfo.InvariantCulture)} km, " +
                    $"perVehicle {(perVehicle).ToString(CultureInfo.InvariantCulture)}, roundTrip {(line.m_StableDurationSeconds).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                    $"targetInterval {(line.m_TargetInterval).ToString("F0", CultureInfo.InvariantCulture)}s]");
                return;
            }
        }

        // The alignment a mode may run on. Buses and trams are stuck with streets,
        // metro and train lay their own, ferries need water.
        private SuitabilityRoadGraph NetworkForMode(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Train: return m_RailNetwork;
                case ModePreset.Metro: return m_RailNetwork;
                case ModePreset.Ferry: return m_WaterNetwork;
                default: return m_RoadGraph;
            }
        }

        // Re-traces the line between its own endpoints using current demand, then
        // re-spaces its stops for the recommended mode. This is the improved routing
        // the plan talks about, made visible rather than merely described.
        private void BuildImprovedRoute(LineHealth health, ExistingLine line)
        {
            m_ImprovedRoute = null;
            s_ImprovedRouteDrawn = false;
            if (line.m_StopIndices.Count < 2)
            {
                return;
            }

            int firstStop = line.m_StopIndices[0];
            int lastStop = line.m_StopIndices[line.m_StopIndices.Count - 1];
            // Both ends of the range, as every other stop-index guard in this file
            // checks: a negative index reads off the front of the list just as surely.
            if (firstStop < 0 || lastStop < 0
                || firstStop >= m_TransitStops.Count || lastStop >= m_TransitStops.Count)
            {
                return;
            }

            ModePreset mode = health.m_Verdict == LineVerdict.AtModeCapacity
                ? TransitModes.NextModeUp(health.m_Mode)
                : health.m_Verdict == LineVerdict.NearlyEmpty
                    ? TransitModes.NextModeDown(health.m_Mode)
                    : health.m_Mode;

            // Re-trace on the network the recommended mode can actually use. Tracing a
            // train on the road graph reported "no road path between its endpoints" and
            // produced nothing at all.
            SuitabilityRoadGraph graph = NetworkForMode(mode);
            if (graph?.Graph is null)
            {
                DeferredLog.Info($"Improved route for \"{health.m_Name}\": no {mode} network is built yet.");
                return;
            }

            var scratch = new List<int>();
            int from = graph.NearestNode(m_TransitStops[firstStop], ReplanSnapMetres);
            int to = graph.NearestNode(m_TransitStops[lastStop], ReplanSnapMetres);
            if (from < 0 || to < 0 || !graph.TracePath(from, to, ReplanMaxPathMetres, scratch))
            {
                DeferredLog.Info(
                    $"Improved route for \"{health.m_Name}\": no {graph.Network} path between its endpoints " +
                    $"(fromNode={(from).ToString(CultureInfo.InvariantCulture)}, toNode={(to).ToString(CultureInfo.InvariantCulture)}).");
                return;
            }

            var route = new SuggestedRoute { Network = graph.Network, Mode = mode, Source = graph };
            route.Nodes.AddRange(scratch);
            graph.MaterialisePath(scratch, route.Path);

            float length = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                length += math.distance(route.Path[i - 1], route.Path[i]);
            }

            route.Length = length;
            route.CapturedFlow = graph.FlowAlong(scratch);
            FleetFacts facts = ReadFleetFacts();
            SuitabilityRoutes.Restop(route, mode, BuildStopContext());
            route.Vehicles = RoadVehicles(route, facts.HeadwayFor(mode), facts.DelayPerStopSeconds(mode));

            m_ImprovedRoute = route.Stops.Count >= 2 ? route : null;
            s_ImprovedRouteDrawn = m_ImprovedRoute is not null;

            DeferredLog.Info(
                $"Improved route for \"{health.m_Name}\": {mode}, {(length / 1000f).ToString("F2", CultureInfo.InvariantCulture)} km " +
                $"(was {(health.m_LengthKm).ToString("F2", CultureInfo.InvariantCulture)}), {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops (was {(health.m_Stops).ToString(CultureInfo.InvariantCulture)}), " +
                $"{(route.Vehicles).ToString(CultureInfo.InvariantCulture)} vehicles (was {(health.m_Vehicles).ToString(CultureInfo.InvariantCulture)}), corridorFlow={(route.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}");
        }

        private void UpdateLineHealthText()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                LineHealth health = m_LineHealth[i];
                if (i > 0)
                {
                    _ = builder.Append('\n');
                }

                _ = builder.Append(health.m_Id);
                _ = builder.Append('|');
                // The game's own name, so this list matches the Transportation
                // Overview rather than using an invented index.
                _ = builder.Append(string.IsNullOrEmpty(health.m_Name) ? health.m_Mode.ToString() : health.m_Name);
                _ = builder.Append('|');
                _ = builder.Append(health.m_Verdict);
                _ = builder.Append('|');
                // Token plus argument, never a finished sentence: the panel is the only
                // place that knows the player's language.
                _ = builder.Append(SuitabilityLineHealth.VerdictArgument(health));
                _ = builder.Append('|');
                _ = builder.Append((health.m_Usage * 100f).ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append(health.m_Vehicles);
                _ = builder.Append('|');
                _ = builder.Append(health.m_Stops);
                _ = builder.Append('|');
                // How much evidence the verdict rests on. The percentage above is a
                // mean over the rolling window once there are enough readings, and a
                // player has no way to tell that from a single instantaneous count
                // unless the panel says so.
                _ = builder.Append(health.m_WindowSamples);
                _ = builder.Append('|');
                _ = builder.Append(health.m_WindowGameHours.ToString("F0", CultureInfo.InvariantCulture));
                _ = builder.Append('|');
                _ = builder.Append((health.m_PeakUsage * 100f).ToString("F0", CultureInfo.InvariantCulture));
            }

            s_LineHealthList = builder.ToString();
        }

        // How far a rider will walk to reach or change service.
        // How long a rider will walk to change vehicle: three minutes at the planning
        // walking speed (register A1.11; the literature gives transfer TIME weights,
        // no distance threshold — TCQSM Exhibit 4-5). Metres follow from the speed.
        private const float TransferWalkSeconds = TransitModes.TransferWalkMs / 1000f;
        private const float TransferWalkRadius = SuitabilityTransit.WalkSpeed * TransferWalkSeconds;
        // How far a zone's centre may be from a stop for that stop to serve it. A zone
        // is 256 m across, so its centre is further from a stop than its edges are, and
        // the plain transfer radius left most zones unserved.
        private const float ZoneStopReachMetres = TransferWalkRadius * 2f;
        // How close a candidate's stops must be to an existing line's for the two to
        // count as the same alignment.
        private const float DuplicateLineMatchMetres = 150f;
        // How far an existing line's endpoint may be from a node when re-planning it,
        // and how long the replacement path may be.
        private const float ReplanSnapMetres = 600f;
        private const float ReplanMaxPathMetres = 60000f;
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
        private const float StopMatchRadiusSq = 40f * 40f;
        // Share of the city's longest journey below which a line cannot plausibly be
        // what unlocks a large part of its travel, whatever the transfer model credits
        // it with. A share rather than a distance because a city's scale is the whole
        // point of the test — see the sanity check that reads it.
        private const float ShortLineShareOfCity = 0.25f;
        // How far a suggestion's termini may move and still count as the same corridor.
        private const float RouteSameEndsRadiusSq = 200f * 200f;
        private const float ImplausibleDemandShare = 0.15f;
        // Journeys longer than this are not realistically made by transit. Also the
        // routing cap, and the fallback ceiling for the served-demand discount when
        // the network carries too little for a median to mean anything.
        private const float MaxJourneySeconds = 3600f;
        // A journey taking this many times the city's typical transit journey is not
        // carried in any useful sense, so it keeps its whole weight.
        private const float ServedCeilingMultiple = 3f;
        // Below this many carried journeys the median is noise, and the fixed hour is
        // the more honest reference.
        private const int MinPairsForServedMedian = 20;

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
        private const float RoadFlowFraction = 0.1f;
        // Normalized demand a corridor's next node must have beside it. Corridors must
        // serve somebody along their length, not merely carry through-traffic — that is
        // what stopped routes looping into empty land. Loosened from 0.02, where the
        // gate truncated corridors at the first thin block and made almost every one
        // too short to suggest.
        private const float CorridorDemandFloor = 0.005f;

        private void BuildDemandLayer(Setting settings, int2 gridSize, float2 worldMin)
        {
            // Rasterising and normalising a layer no infomode can select is a pass over
            // every cell on the map for nothing.
            if (TermLayer(SuitabilityLayer.TravelDemand) is null)
            {
                return;
            }

            int cells = gridSize.x * gridSize.y;
            if (m_DemandRaster is null || m_DemandRaster.Length != cells)
            {
                m_DemandRaster = new float[cells];
            }

            SuitabilityTravelDemand.RasterizeDesireLines(
                m_ZoneFlows, worldMin, m_ZoneGrid, gridSize, TileSize, m_DemandRaster);

            byte[] layer = m_LayerIntensities[(int)SuitabilityLayer.TravelDemand];
            if (layer is not null && layer.Length == cells && m_ScoreScratch is not null && m_ScoreScratch.Length >= cells)
            {
                SuitabilityScoring.NormalizeIntensities(
                    m_DemandRaster, cells, settings.HighlightShare / 100f, IntensityGamma, layer, m_ScoreScratch);
                m_ExpandedSignature = -1;
            }
        }

        // Builds all four networks. The rail lattices differ only in how much they
        // discount running along track that already exists.
        private void BuildNetworks(int2 gridSize, float2 worldMin)
        {
            m_NodeLookup.Update(this);
            m_CurveLookup.Update(this);
            m_PrefabRefLookup.Update(this);
            m_RoadDataLookup.Update(this);

            m_RoadGraph.Build(EntityManager, m_RoadEdgeQuery, m_NodeLookup, m_CurveLookup,
                m_PrefabRefLookup, m_RoadDataLookup);
            m_RoadLegs.Clear();
            m_RoadLegsDropped = 0;
            m_ZoneNodes = m_RoadGraph.MapZonesToNodes(m_ZoneGrid, worldMin);

            int cells = gridSize.x * gridSize.y;
            if (m_TrackMask is null || m_TrackMask.Length != cells)
            {
                m_TrackMask = new byte[cells];
            }

            SuitabilityRoadGraph.CollectTrackSegments(EntityManager, m_AllEdgeQuery, m_NodeLookup, m_TrackStarts, m_TrackEnds);
            SuitabilityLattice.RasterizeTracks(m_TrackStarts, m_TrackEnds, gridSize, worldMin, TileSize, m_TrackMask);

            bool LandTile(int tile) => m_Land is not null && tile < m_Land.Length && m_Land[tile] != 0;
            bool WaterTile(int tile) => m_Land is not null && tile < m_Land.Length && m_Land[tile] == 0;
            bool OnTrack(int tile) => m_TrackMask is not null && tile < m_TrackMask.Length && m_TrackMask[tile] != 0;

            CompactGraph railGraph = SuitabilityLattice.Build(gridSize, worldMin, TileSize, LandTile,
                tile => SuitabilityLattice.RailCostScale(OnTrack(tile)),
                out float[] railX, out float[] railZ);
            m_RailNetwork.Adopt(railGraph, railX, railZ, RouteNetwork.Rail);

            CompactGraph waterGraph = SuitabilityLattice.Build(gridSize, worldMin, TileSize, WaterTile,
                tileCostScale: null, out float[] waterX, out float[] waterZ);
            m_WaterNetwork.Adopt(waterGraph, waterX, waterZ, RouteNetwork.Water);

            m_GraphDirty = false;
            DeferredLog.Info(
                $"Networks built: road {(m_RoadGraph.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_RoadGraph.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"rail {(m_RailNetwork.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_RailNetwork.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"water {(m_WaterNetwork.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_WaterNetwork.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"trackSegments={m_TrackStarts.Count}; directed road arcs {(m_RoadGraph.Directed?.ArcCount ?? 0).ToString(CultureInfo.InvariantCulture)}, " +
                $"one-way streets {(m_RoadGraph.OneWayEdges).ToString(CultureInfo.InvariantCulture)}, " +
                $"streets without a car lane {(m_RoadGraph.EdgesWithoutCarLane).ToString(CultureInfo.InvariantCulture)}, " +
                $"turn cost {(m_RoadGraph.TurnSecondsPerRadian).ToString("F2", CultureInfo.InvariantCulture)} s/rad from the car pathfind prefab");
        }

        private void AssignLatticeFlow(SuitabilityRoadGraph network, float2 worldMin, List<ZoneFlow> flows)
        {
            if (network.Graph is null || network.NodeCount == 0 || flows.Count == 0)
            {
                return;
            }

            int[] zoneNodes = network.MapZonesToNodes(m_ZoneGrid, worldMin);
            _ = network.AssignFlow(flows, zoneNodes, 30000f, out float _);
        }

        // Each network contributes candidates for the modes it can carry; the merged
        // set is ranked by trips carried and the best kept. Auto-assignment therefore
        // falls out of which network won, rather than being guessed after the fact.
        private void BuildRoutes(Setting settings, int2 gridSize, float2 worldMin, RoutePass pass)
        {
            var objective = (RouteObjective)settings.Objective;
            List<SuggestedRoute> candidates = pass.Candidates;
            candidates.Clear();

            float[]? roadDemand = BuildNodeDemand(m_RoadGraph, gridSize);

            const float demandFloor = CorridorDemandFloor;

            int grownTotal = 0;
            int shortTotal = 0;

            var phases = System.Diagnostics.Stopwatch.StartNew();
            StopContext stops = BuildStopContext();
            SuitabilityRoutes.BuildForNetwork(m_RoadGraph, objective, settings.RouteCount,
                RoadFlowFraction, TransitModes.MaxAlignmentMetresFor(RouteNetwork.Road), roadDemand, demandFloor, forcedMode: null, candidates,
                stops, out int g1, out int s1, out int h1);

            // The lattices connect two places rather than following a flow ridge — see
            // BuildDirectForNetwork for why growth is the wrong instrument on a uniform
            // grid. One rail lattice serves metro and train (A4.5); the water lattice
            // sees every journey (A3.4). Alignments start as the network's smallest
            // mode; the riders decide the real one (ChooseMode).
            SuitabilityRoutes.BuildDirectForNetwork(m_RailNetwork, m_ZoneFlows,
                m_RailNetwork.MapZonesToNodes(m_ZoneGrid, worldMin), settings.RouteCount,
                TransitModes.MaxAlignmentMetresFor(RouteNetwork.Rail), TransitModes.ModesFor(RouteNetwork.Rail)[0], candidates,
                stops, out int g2, out int s2, out int h2);

            var shoreline = new StopContext
            {
                Ends = stops.Ends,
                EndWeights = stops.EndWeights,
                Facts = stops.Facts,
                Hubs = stops.Hubs,
                ScoreAt = (point, _) => ShorelineScoreAt(point, gridSize),
            };
            SuitabilityRoutes.BuildDirectForNetwork(m_WaterNetwork, m_ZoneFlows,
                m_WaterNetwork.MapZonesToNodes(m_ZoneGrid, worldMin), settings.RouteCount,
                TransitModes.MaxAlignmentMetresFor(RouteNetwork.Water), ModePreset.Ferry, candidates,
                shoreline, out int g4, out int s4, out int h4);
            int g3 = 0;
            int s3 = 0;
            int h3 = 0;

            grownTotal = g1 + g2 + g3 + g4;
            shortTotal = s1 + s2 + s3 + s4;

            DeferredLog.Info(
                $"Candidates by network: road grown={(g1).ToString(CultureInfo.InvariantCulture)} tooShort={(s1).ToString(CultureInfo.InvariantCulture)}, " +
                $"rail pairs tried={(g2 + g3).ToString(CultureInfo.InvariantCulture)} tooShort={(s2 + s3).ToString(CultureInfo.InvariantCulture)}, " +
                $"ferry pairs tried={(g4).ToString(CultureInfo.InvariantCulture)} tooShort={(s4).ToString(CultureInfo.InvariantCulture)}, " +
                $"termini aimed at an interchange={(h1 + h2 + h3 + h4).ToString(CultureInfo.InvariantCulture)} (road {(h1).ToString(CultureInfo.InvariantCulture)}) of {(m_Interchanges.Count).ToString(CultureInfo.InvariantCulture)} served stops, " +
                $"tooShort = fewer than {(TransitModes.MinStops).ToString(CultureInfo.InvariantCulture)} stops; " +
                $"ride limits (min): bus {(TransitModes.MaxRideSecondsFor(ModePreset.Bus) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"tram {(TransitModes.MaxRideSecondsFor(ModePreset.Tram) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"metro {(TransitModes.MaxRideSecondsFor(ModePreset.Metro) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"train {(TransitModes.MaxRideSecondsFor(ModePreset.Train) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"ferry {(TransitModes.MaxRideSecondsFor(ModePreset.Ferry) / 60f).ToString("F0", CultureInfo.InvariantCulture)}");

            // Corridor flow is all there is to rank by until the routing pass runs, and
            // it decides which candidates are inside the scoring window. Enabled demand
            // takes over from there, and is re-measured once per accepted suggestion.
            candidates.Sort(static (a, b) => b.CapturedFlow.CompareTo(a.CapturedFlow));
            pass.AlignmentMs = phases.ElapsedMilliseconds;
            SelectRoutes(settings, gridSize, grownTotal, shortTotal, pass);
        }

        // Everything a stop plan reads that is not the alignment itself: the journeys'
        // doors (each journey contributes its origin and its destination, both at the
        // journey's weight), the prefab facts, the score oracle and the interchanges.
        private StopContext BuildStopContext()
        {
            var ends = new float2[m_Journeys.Count * 2];
            var weights = new float[m_Journeys.Count * 2];
            for (int i = 0; i < m_Journeys.Count; i++)
            {
                Trip trip = m_Journeys[i];
                ends[2 * i] = trip.m_Origin;
                ends[(2 * i) + 1] = trip.m_Destination;
                weights[2 * i] = trip.m_Weight;
                weights[(2 * i) + 1] = trip.m_Weight;
            }

            int2 gridSize = m_IntensityGrid;
            return new StopContext
            {
                Ends = ends,
                EndWeights = weights,
                Facts = ReadFleetFacts(),
                Hubs = m_Interchanges,
                ScoreAt = (point, mode) => ScoreForMode(point, gridSize, mode),
            };
        }

        // The two ends of a suggested line, kept only to compare one refresh's list
        // against the last.
        private struct RouteEnds
        {
            public float2 m_From;
            public float2 m_To;
        }

        // The suggestions as a SET (register A7.5, decided 2026-09-05): every candidate is
        // first weighed alone — its riders feed the mode decision and the log — then the
        // resolved candidates are handed to the exact line-set search, which maximises
        // (equity share up to the floor, then passenger time saved) over sets of at most
        // RouteCount lines under the utilisation and duplicate rules. Nothing is taken
        // greedily: a feeder that pays only next to its trunk is found with it.
        private void SelectRoutes(Setting settings, int2 gridSize, int grownTotal, int shortTotal, RoutePass pass)
        {
            var tally = new RejectionTally();
            var scratch = new List<int>();
            FleetFacts facts = ReadFleetFacts();
            StopContext stops = BuildStopContext();

            var phases = System.Diagnostics.Stopwatch.StartNew();
            WeighCandidatesAlone(settings, pass.Candidates);
            pass.WeighMs = phases.ElapsedMilliseconds;
            phases.Restart();
            List<SuggestedRoute> resolved = pass.Resolved;
            for (int i = 0; i < pass.Candidates.Count; i++)
            {
                ResolveCandidate(settings, pass.Candidates[i], i, facts, stops, scratch, tally, resolved);
            }

            pass.ResolveMs = phases.ElapsedMilliseconds;

            if (resolved.Count > 0)
            {
                LineSetProblem problem = BuildLineSetProblem(settings, resolved, settings.RouteCount);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                LineSetSolution solution;
                using (var budget = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(LineSetTimeBudgetSeconds)))
                {
                    solution = SuitabilityLineSet.Solve(problem, SuitabilityLineSet.DefaultNodeBudget, budget.Token);
                }

                stopwatch.Stop();
                pass.SolveMs = stopwatch.ElapsedMilliseconds;
                pass.Solution = solution;
                pass.Problem = problem;
                AdoptLineSet(settings, resolved, problem, solution, stopwatch.ElapsedMilliseconds, pass);
            }

            DeferredLog.Info(
                $"Route suggestions: grown={(grownTotal).ToString(CultureInfo.InvariantCulture)}, tooShort={(shortTotal).ToString(CultureInfo.InvariantCulture)}, " +
                $"candidates={pass.Candidates.Count}, resolved={(resolved.Count).ToString(CultureInfo.InvariantCulture)}, unjustified={(tally.Unjustified).ToString(CultureInfo.InvariantCulture)}, " +
                $"retracedOnRoad={(tally.Retraced).ToString(CultureInfo.InvariantCulture)}, alreadyBuilt={(tally.AlreadyBuilt).ToString(CultureInfo.InvariantCulture)}, " +
                $"kept={pass.Routes.Count}; worker phases: alignments+stops {(pass.AlignmentMs).ToString(CultureInfo.InvariantCulture)} ms, " +
                $"weighing {(pass.WeighMs).ToString(CultureInfo.InvariantCulture)} ms, resolving {(pass.ResolveMs).ToString(CultureInfo.InvariantCulture)} ms, " +
                $"set search {(pass.SolveMs).ToString(CultureInfo.InvariantCulture)} ms");
        }

        // Every candidate evaluated on its own against the existing network: the journey
        // weight that would ride it becomes EnabledDemand (what the mode decision and the
        // panel's reach figure read), and its standalone time saving is logged.
        private void WeighCandidatesAlone(Setting settings, List<SuggestedRoute> candidates)
        {
            var usable = new List<SuggestedRoute>();
            for (int i = 0; i < candidates.Count; i++)
            {
                candidates[i].EnabledDemand = 0f;
                candidates[i].DemandScored = false;
                if (candidates[i].Stops.Count >= 2)
                {
                    usable.Add(candidates[i]);
                }
            }

            if (usable.Count == 0)
            {
                return;
            }

            LineSetProblem probe = BuildLineSetProblem(settings, usable, 1);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            float[] before = SuitabilityLineSet.Evaluate(probe, Array.Empty<int>(), 0, before: null).After;
            DeferredLog.Info(
                $"Baseline door-to-door times for {(probe.PairCount).ToString(CultureInfo.InvariantCulture)} pairs from " +
                $"{(SuitabilityLineSet.GeometryOf(probe).ZoneCount).ToString(CultureInfo.InvariantCulture)} zones in {(stopwatch.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms");
            for (int c = 0; c < usable.Count; c++)
            {
                stopwatch.Restart();
                LineSetEvaluation alone = SuitabilityLineSet.Evaluate(probe, new[] { c }, 1, before);
                usable[c].EnabledDemand = (float)alone.Riders[c];
                usable[c].DemandScored = true;
                DeferredLog.Info(
                    $"  candidate alone: {usable[c].Network} {usable[c].Mode}, {usable[c].Stops.Count} stops, " +
                    $"corridorFlow={(usable[c].CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"riders/day={(alone.Riders[c]).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"timeSaved={(alone.TimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} passenger-hours/day, {(stopwatch.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms");
            }
        }

        // The set problem over `candidates`: existing served stops and lines as the base
        // network, every journey at its full weight, each candidate with the wait, speed,
        // ride times, headway and capacity of its resolved mode.
        private LineSetProblem BuildLineSetProblem(Setting settings, List<SuggestedRoute> candidates, int maxLines)
        {
            var problem = new LineSetProblem
            {
                BaseStopCount = m_TransitStops.Count,
                BaseStopX = new float[m_TransitStops.Count],
                BaseStopZ = new float[m_TransitStops.Count],
                BaseLines = SuitabilityLines.ToTransitLines(m_ExistingLines),
                WalkRadius = TransferWalkRadius,
                BoardPenaltySeconds = SuitabilityTransit.DefaultBoardPenaltySeconds,
                MaxTravelSeconds = MaxJourneySeconds,
                ZoneReachMetres = ZoneStopReachMetres,
                MaxLines = maxLines,
                UtilisationFloor = settings.UtilisationFloorPercent / 100f,
                UtilisationCeiling = TransitModes.MaxPlannedUtilisation,
                MovementSecondsPerDay = SuitabilityEquity.MovementSecondsPerGameDay,
                DuplicateShare = DuplicateRiderShare,
                EquityFloorShare = settings.EquityFloorPercent / 100f,
            };
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                problem.BaseStopX[i] = m_TransitStops[i].x;
                problem.BaseStopZ[i] = m_TransitStops[i].y;
            }

            // Journeys door to door (register A0.5): every trip at its own two
            // positions, not a zone centre; trips between the same two doors (one
            // household's commuters to one workplace) are one pair with their summed
            // weight. Evaluate then searches once per distinct origin door.
            var pairIndex = new Dictionary<(float, float, float, float), int>();
            var ox = new List<float>();
            var oz = new List<float>();
            var dx = new List<float>();
            var dz = new List<float>();
            var weight = new List<float>();
            for (int i = 0; i < m_Journeys.Count; i++)
            {
                Trip trip = m_Journeys[i];
                var key = (trip.m_Origin.x, trip.m_Origin.y, trip.m_Destination.x, trip.m_Destination.y);
                if (pairIndex.TryGetValue(key, out int existing))
                {
                    weight[existing] += trip.m_Weight;
                    continue;
                }

                pairIndex.Add(key, ox.Count);
                ox.Add(trip.m_Origin.x);
                oz.Add(trip.m_Origin.y);
                dx.Add(trip.m_Destination.x);
                dz.Add(trip.m_Destination.y);
                weight.Add(trip.m_Weight);
            }

            problem.PairCount = ox.Count;
            problem.PairOx = ox.ToArray();
            problem.PairOz = oz.ToArray();
            problem.PairDx = dx.ToArray();
            problem.PairDz = dz.ToArray();
            problem.PairWeight = weight.ToArray();

            FleetFacts facts = ReadFleetFacts();
            for (int c = 0; c < candidates.Count; c++)
            {
                SuggestedRoute route = candidates[c];
                var line = new LineCandidate
                {
                    StopX = new float[route.Stops.Count],
                    StopZ = new float[route.Stops.Count],
                    ExpectedWait = facts.ExpectedWaitFor(route.Mode),
                    SpeedMetresPerSecond = TransitModes.CruiseSpeedFor(route.Mode),
                    RideSeconds = RoadRideSeconds(route),
                    HeadwaySeconds = facts.HeadwayFor(route.Mode),
                    VehicleCapacity = facts.CapacityFor(route.Mode),
                };
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    line.StopX[i] = route.Stops[i].x;
                    line.StopZ[i] = route.Stops[i].y;
                }

                problem.Candidates.Add(line);
            }

            WalkAccessInputs? accessInputs = m_AccessInputs;
            if (m_ServedWalkMs is not null && m_Access?.Index is not null && accessInputs is not null)
            {
                // Its own workspace: the pass runs off the main thread, which keeps the
                // equity measure's for itself.
                var dijkstra = new IntDijkstra(accessInputs.Graph.NodeCount);
                problem.CoverageOf = (chosen, count) => CoverageWith(candidates, chosen, count, dijkstra);
            }

            return problem;
        }

        // Share of journeys served at both ends once the chosen candidates' stops join the
        // served network — the equity component of the set objective.
        private float CoverageWith(List<SuggestedRoute> candidates, int[] chosen, int count, IntDijkstra dijkstra)
        {
            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (m_ServedWalkMs is null || access?.Index is null || inputs is null)
            {
                return 0f;
            }

            var stops = new List<float2>();
            for (int k = 0; k < count; k++)
            {
                stops.AddRange(candidates[chosen[k]].Stops);
            }

            SnapStops(stops, access.Index, inputs.AccessMs, out int[] nodes, out int[] stopAccess);
            int[] merged = SuitabilityEquity.WithStops(inputs.Graph, dijkstra, m_ServedWalkMs, nodes, stopAccess, nodes.Length, m_EquityHorizonMs);
            return SuitabilityEquity.Coverage(
                merged, m_EquityHorizonMs,
                m_JourneyOriginNode, m_JourneyOriginAccess, m_JourneyDestinationNode, m_JourneyDestinationAccess,
                m_JourneyWeight, m_Journeys.Count).Share;
        }

        // Riders of a line may already have an equally fast route without it: above this
        // share of them the line duplicates the set it sits in (register A4.3).
        private const float DuplicateRiderShare = 0.5f;

        private void AdoptLineSet(Setting settings, List<SuggestedRoute> resolved, LineSetProblem problem, LineSetSolution solution, long elapsedMs, RoutePass pass)
        {
            var order = new List<int>();
            for (int k = 0; k < solution.Count; k++)
            {
                order.Add(solution.Chosen[k]);
            }

            order.Sort((a, b) => solution.StandaloneTimeSaved[b].CompareTo(solution.StandaloneTimeSaved[a]));
            LineSetEvaluation? evaluation = solution.Evaluation;
            for (int k = 0; k < order.Count; k++)
            {
                SuggestedRoute route = resolved[order[k]];
                if (evaluation is not null)
                {
                    route.EnabledDemand = (float)evaluation.Riders[order[k]];
                }

                route.Vehicles = RoadVehicles(route, problem.Candidates[order[k]].HeadwaySeconds, ReadFleetFacts().DelayPerStopSeconds(route.Mode));
                pass.Routes.Add(route);
                AcceptIntoNetwork(route, pass);
                DeferredLog.Info(
                    $"  KEPT #{(k + 1).ToString(CultureInfo.InvariantCulture)}: {route.Network} {route.Mode}{(route.BentThroughHub ? " via an interchange" : string.Empty)}, {route.Stops.Count} stops " +
                    $"(plan: {(route.StopPlan?.CandidateCount ?? 0).ToString(CultureInfo.InvariantCulture)} candidates, {(route.StopPlan?.EndCount ?? 0).ToString(CultureInfo.InvariantCulture)} doors in reach, " +
                    $"gain {(route.StopPlanGain / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} h vs delay {(route.StopPlanDelay / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} h a day), " +
                    $"len={(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m, riders/day in the set={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"utilisation={(SuitabilityLineSet.Utilisation(problem, order[k], route.EnabledDemand) * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, " +
                    $"alone={(solution.StandaloneTimeSaved[order[k]] / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} passenger-hours/day, {(route.Vehicles).ToString(CultureInfo.InvariantCulture)} veh");
            }

            string realism = evaluation is null
                ? string.Empty
                : $"; the set's journeys spend walk {(evaluation.WalkSeconds / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} h, wait {(evaluation.WaitSeconds / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} h, ride {(evaluation.RideSeconds / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} h a day " +
                  $"(realism-weighted 2.2/2.1/1: {((evaluation.WalkSeconds * 2.2 + evaluation.WaitSeconds * 2.1 + evaluation.RideSeconds) / 3600.0).ToString("F0", CultureInfo.InvariantCulture)} weighted hours — shown, not planned with; register A7.2)";
            DeferredLog.Info(
                $"Line set: {(solution.Count).ToString(CultureInfo.InvariantCulture)} of {(resolved.Count).ToString(CultureInfo.InvariantCulture)} resolved candidates chosen, " +
                $"time saved {(solution.TimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)} passenger-hours/day " +
                $"{(solution.Optimal ? "(proven optimal" : $"(best found, NOT proven optimal; ceiling {(solution.UpperBoundTimeSaved / 3600.0).ToString("F1", CultureInfo.InvariantCulture)}")} " +
                $"under equity floor {settings.EquityFloorPercent.ToString(CultureInfo.InvariantCulture)} % (set reaches {(solution.Coverage * 100f).ToString("F1", CultureInfo.InvariantCulture)} %), " +
                $"utilisation floor {settings.UtilisationFloorPercent.ToString(CultureInfo.InvariantCulture)} % and ceiling {(TransitModes.MaxPlannedUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture)} %, duplicate share {(DuplicateRiderShare * 100f).ToString("F0", CultureInfo.InvariantCulture)} %), " +
                $"{(solution.Nodes).ToString(CultureInfo.InvariantCulture)} search nodes, {(solution.Infeasible).ToString(CultureInfo.InvariantCulture)} infeasible sets met, " +
                $"{(elapsedMs).ToString(CultureInfo.InvariantCulture)} ms of a {(LineSetTimeBudgetSeconds).ToString(CultureInfo.InvariantCulture)} s budget{realism}");
        }

        // The per-line bars a resolved candidate has to clear before the set search may
        // consider it: its shape (at least MinStops calls, a ride within the mode's
        // limit) and not being a line the player has already built. Whether it fills
        // its vehicles or duplicates another suggestion is the set search's to judge.
        private bool PassesLineGates(SuggestedRoute candidate, int index, FleetFacts facts, RejectionTally tally)
        {
            float rideSeconds = RideSecondsOf(candidate, facts);
            if (!SuitabilityRoutes.KeepsItsShape(candidate, rideSeconds))
            {
                tally.Unjustified++;
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(candidate.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, {candidate.Stops.Count} stops, ride {(rideSeconds / 60f).ToString("F1", CultureInfo.InvariantCulture)} min — DROPPED, " +
                    $"{(candidate.Stops.Count < TransitModes.MinStops ? $"fewer than {TransitModes.MinStops.ToString(CultureInfo.InvariantCulture)} stops" : $"over the {(TransitModes.MaxRideSecondsFor(candidate.Mode) / 60f).ToString("F0", CultureInfo.InvariantCulture)} min ride limit for a {candidate.Mode}")}");
                return false;
            }

            if (SuitabilityRoutes.DuplicatesExisting(candidate, m_ExistingLines, m_TransitStops, DuplicateLineMatchMetres))
            {
                tally.AlreadyBuilt++;
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, {candidate.Stops.Count} stops — DROPPED, already built");
                return false;
            }

            return true;
        }

        // End-to-end ride time as the rider is quoted it: directed street legs where
        // they exist, cruise speed otherwise, plus one stop delay per intermediate stop.
        private float RideSecondsOf(SuggestedRoute route, FleetFacts facts)
        {
            float delay = facts.DelayPerStopSeconds(route.Mode);
            float[]? legs = RoadRideSeconds(route);
            if (legs is null)
            {
                return TransitModes.RideSeconds(route.Length, route.Stops.Count, TransitModes.CruiseSpeedFor(route.Mode), delay);
            }

            float driving = 0f;
            float speed = TransitModes.CruiseSpeedFor(route.Mode);
            for (int i = 1; i < legs.Length; i++)
            {
                driving += legs[i] > 0f ? legs[i] : math.distance(route.Stops[i - 1], route.Stops[i]) / math.max(1f, speed);
            }

            return driving + (Math.Max(0, route.Stops.Count - 2) * delay);
        }

        // Why the candidates that did not become suggestions were turned down, for the
        // one summary line the log is read by.
        //
        // A class rather than four `ref int` parameters threaded through two methods:
        // every rejection reason added so far added a parameter to both, and the count
        // was reaching the point where the call site said nothing about what it did.
        private sealed class RejectionTally
        {
            public int Unjustified;
            public int Retraced;
            public int AlreadyBuilt;
        }

        // Treats an accepted suggestion as though the player had built it, so the next
        // round measures every remaining candidate against a network containing it.
        private void AcceptIntoNetwork(SuggestedRoute route, RoutePass pass)
        {
            var stops = new int[route.Stops.Count];
            for (int i = 0; i < route.Stops.Count; i++)
            {
                stops[i] = m_TransitStops.Count + pass.AcceptedStops.Count;
                pass.AcceptedStops.Add(route.Stops[i]);
            }

            pass.AcceptedLines.Add(new TransitLine
            {
                m_Stops = stops,
                m_ExpectedWait = ReadFleetFacts().ExpectedWaitFor(route.Mode),
                m_RideSeconds = RoadRideSeconds(route),
                m_SpeedMetresPerSecond = TransitModes.CruiseSpeedFor(route.Mode),
            });
        }

        // Stops on the road network sit on or beside a road node; further than this
        // the stop is not on the street it was placed along.
        private const float StopNodeSnapMetres = 64f;

        // Driving time into each stop of a road route from the stop before it, along
        // the fastest DIRECTED path with the street's speed limits and turn costs
        // (register A0.7/A0.8). Index i is the ride into stop i; 0 where no directed
        // path exists, which SuitabilityTransit reads as "fall back to distance over
        // cruise speed" — and which is logged, because a stop pair with no drivable
        // path between them is a line the game cannot run either. Null for lattice
        // routes, whose alignments have no streets.
        private float[]? RoadRideSeconds(SuggestedRoute route)
        {
            if (route.Network != RouteNetwork.Road || m_RoadGraph.Directed is null || route.Stops.Count < 2)
            {
                return null;
            }

            var seconds = new float[route.Stops.Count];
            int unreachable = 0;
            for (int i = 1; i < route.Stops.Count; i++)
            {
                long ms = RoadLegMs(route.Stops[i - 1], route.Stops[i]);
                if (ms == DirectedDijkstra.Unreached)
                {
                    unreachable++;
                    continue;
                }

                seconds[i] = ms / 1000f;
            }

            if (unreachable > 0)
            {
                DeferredLog.Warn(
                    $"Road route {route.Mode} with {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops: " +
                    $"{(unreachable).ToString(CultureInfo.InvariantCulture)} stop-to-stop legs have no drivable directed path (one-way streets?); cruise-speed fallback used for them.");
            }

            return seconds;
        }

        // Snaps every journey end to the pedestrian network once per demand refresh,
        // measures how many journeys the served stops reach at both ends within the
        // walking horizon, and publishes the figure the panel and the ranking use.
        private void MeasureEquity(Setting settings)
        {
            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (access?.Index is null || inputs is null)
            {
                m_Coverage = null;
                return;
            }

            m_EquityHorizonMs = settings.EquityWalkMinutes * 60_000;
            int count = m_Journeys.Count;
            if (m_JourneyWeight.Length < count)
            {
                m_JourneyOriginNode = new int[count];
                m_JourneyOriginAccess = new int[count];
                m_JourneyDestinationNode = new int[count];
                m_JourneyDestinationAccess = new int[count];
                m_JourneyWeight = new float[count];
            }

            for (int i = 0; i < count; i++)
            {
                Trip trip = m_Journeys[i];
                m_JourneyOriginNode[i] = SuitabilityWalkAccess.SnapPoint(access.Index, trip.m_Origin.x, trip.m_Origin.y, inputs.AccessMs, out m_JourneyOriginAccess[i]);
                m_JourneyDestinationNode[i] = SuitabilityWalkAccess.SnapPoint(access.Index, trip.m_Destination.x, trip.m_Destination.y, inputs.AccessMs, out m_JourneyDestinationAccess[i]);
                m_JourneyWeight[i] = trip.m_Weight;
            }

            if (m_EquityDijkstra is null || m_EquityDijkstra.Dist.Length != inputs.Graph.NodeCount)
            {
                m_EquityDijkstra = new IntDijkstra(inputs.Graph.NodeCount);
            }

            SnapStops(m_TransitStops, access.Index, inputs.AccessMs, out int[] stopNodes, out int[] stopAccess);
            m_ServedWalkMs = SuitabilityEquity.ServedWalkMs(inputs.Graph, m_EquityDijkstra, stopNodes, stopAccess, stopNodes.Length, m_EquityHorizonMs);
            RefreshCoverage(settings, "measured");
        }

        private static void SnapStops(List<float2> stops, WalkNodeIndex index, int accessMs, out int[] nodes, out int[] access)
        {
            nodes = new int[stops.Count];
            access = new int[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                nodes[i] = SuitabilityWalkAccess.SnapPoint(index, stops[i].x, stops[i].y, accessMs, out access[i]);
            }
        }

        private void RefreshCoverage(Setting settings, string why)
        {
            if (m_ServedWalkMs is null)
            {
                return;
            }

            m_Coverage = SuitabilityEquity.Coverage(
                m_ServedWalkMs, m_EquityHorizonMs,
                m_JourneyOriginNode, m_JourneyOriginAccess, m_JourneyDestinationNode, m_JourneyDestinationAccess,
                m_JourneyWeight, m_Journeys.Count);
            s_Equity =
                $"{(m_Coverage.Share * 100f).ToString("F1", CultureInfo.InvariantCulture)}|" +
                $"{settings.EquityWalkMinutes.ToString(CultureInfo.InvariantCulture)}|" +
                $"{settings.EquityFloorPercent.ToString(CultureInfo.InvariantCulture)}|" +
                $"{m_Coverage.GiniWalk.ToString("F2", CultureInfo.InvariantCulture)}";
            DeferredLog.Info(
                $"Equity ({why}): {(m_Coverage.Share * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of journey weight served at both ends within " +
                $"{settings.EquityWalkMinutes.ToString(CultureInfo.InvariantCulture)} min (floor {settings.EquityFloorPercent.ToString(CultureInfo.InvariantCulture)} %), " +
                $"{(m_Coverage.TripsCovered).ToString(CultureInfo.InvariantCulture)}/{(m_Coverage.Trips).ToString(CultureInfo.InvariantCulture)} journeys, " +
                $"{(m_Coverage.TripsOffNetwork).ToString(CultureInfo.InvariantCulture)} with an end off the pedestrian network, " +
                $"Gini of access walk {m_Coverage.GiniWalk.ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"served stops {(m_TransitStops.Count).ToString(CultureInfo.InvariantCulture)}");
        }

        // Stops are points along a street, not its ends: each is projected onto the
        // nearest arc within the snap distance and timed from there (RoadLegs).
        private long RoadLegMs(float2 from, float2 to)
        {
            long ms = m_RoadGraph.PointLegMs(from, to, StopNodeSnapMetres, (long)MaxJourneySeconds * 1000L, out RoadLeg leg);
            RememberRoadLeg(from, to, ms, leg);
            return ms;
        }

        // The stop-to-stop legs the last route pass asked the directed graph for, with
        // the answers it got: what the export hands the pipeline to certify. Bounded,
        // and reset whenever the graph is rebuilt so no leg outlives its graph.
        private const int MaxRememberedRoadLegs = 400;
        private readonly List<(float2 from, float2 to, long ms, RoadLeg leg)> m_RoadLegs = new List<(float2, float2, long, RoadLeg)>();
        private int m_RoadLegsDropped;

        private void RememberRoadLeg(float2 from, float2 to, long ms, RoadLeg leg)
        {
            for (int i = 0; i < m_RoadLegs.Count; i++)
            {
                if (m_RoadLegs[i].from.Equals(from) && m_RoadLegs[i].to.Equals(to))
                {
                    return;
                }
            }

            if (m_RoadLegs.Count >= MaxRememberedRoadLegs)
            {
                m_RoadLegsDropped++;
                return;
            }

            m_RoadLegs.Add((from, to, ms, leg));
        }

        // Out and back over the directed network — the return leg may take other
        // streets than the outward one — plus a dwell at every call each way. Falls
        // back to the cruise-speed estimate where a leg has no directed path.
        private int RoadVehicles(SuggestedRoute route, float headwaySeconds, float delayPerStopSeconds)
        {
            if (route.Network != RouteNetwork.Road || m_RoadGraph.Directed is null || route.Stops.Count < 2)
            {
                return SuitabilityRoutes.EstimateVehicles(route.Mode, route.Length, route.Stops.Count, headwaySeconds, delayPerStopSeconds);
            }

            float speed = TransitModes.CruiseSpeedFor(route.Mode);
            double roundTrip = 0.0;
            for (int i = 1; i < route.Stops.Count; i++)
            {
                roundTrip += LegSeconds(route.Stops[i - 1], route.Stops[i], speed);
                roundTrip += LegSeconds(route.Stops[i], route.Stops[i - 1], speed);
            }

            return SuitabilityRoutes.EstimateVehiclesFromRoundTrip((float)roundTrip, route.Stops.Count, headwaySeconds, delayPerStopSeconds);
        }

        private double LegSeconds(float2 from, float2 to, float cruiseSpeed)
        {
            long ms = RoadLegMs(from, to);
            return ms == DirectedDijkstra.Unreached
                ? math.distance(from, to) / math.max(1f, cruiseSpeed)
                : ms / 1000.0;
        }

        // Settles what mode a candidate runs as and hands it to the line gates. The
        // mode is the smallest whose vehicles the candidate's OWN standalone riders do
        // not overload (TransitModes.ChooseMode, register A6.x), measured again after
        // the stops are re-placed for it — a metro's doors are not a bus's. The next
        // mode up is offered as a second candidate whenever the ladder has one, because
        // a set can hand a line more riders than it carries alone (a feeder's trunk),
        // and the set's utilisation ceiling then rules the smaller one out. A lattice
        // alignment whose riders leave even the smallest rail vehicle under the
        // utilisation floor is also offered re-traced along streets. Variants of one
        // alignment duplicate each other, so a set holds at most one of them.
        private void ResolveCandidate(
            Setting settings,
            SuggestedRoute candidate,
            int index,
            FleetFacts facts,
            StopContext stops,
            List<int> scratch,
            RejectionTally tally,
            List<SuggestedRoute> resolved)
        {
            if (!SettleMode(settings, candidate, index, facts, stops, out float utilisation, out ModePreset? nextUp))
            {
                tally.Unjustified++;
                return;
            }

            if (PassesLineGates(candidate, index, facts, tally))
            {
                resolved.Add(candidate);
            }

            if (nextUp is ModePreset larger)
            {
                SuggestedRoute variant = candidate.CopyFor(larger);
                SuitabilityRoutes.Restop(variant, larger, stops);
                variant.EnabledDemand = RidersAlone(settings, variant);
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: also offered as a {larger} " +
                    $"({variant.Stops.Count} stops, riders/day alone={(variant.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)})");
                if (PassesLineGates(variant, index, facts, tally))
                {
                    resolved.Add(variant);
                }
            }

            if (candidate.Network == RouteNetwork.Road || candidate.Stops.Count < 2 || utilisation >= settings.UtilisationFloorPercent / 100f)
            {
                return;
            }

            SuggestedRoute? onRoad = SuitabilityRoutes.RetraceOnRoad(
                m_RoadGraph, candidate.Stops[0], candidate.Stops[candidate.Stops.Count - 1], stops, scratch);
            if (onRoad is null)
            {
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: under the utilisation floor as a {candidate.Mode} and no road path between its ends to offer instead");
                return;
            }

            onRoad.DemandScored = true;
            onRoad.EnabledDemand = RidersAlone(settings, onRoad);
            tally.Retraced++;
            if (!SettleMode(settings, onRoad, index, facts, stops, out _, out ModePreset? roadNextUp))
            {
                return;
            }

            if (PassesLineGates(onRoad, index, facts, tally))
            {
                resolved.Add(onRoad);
            }

            if (roadNextUp is ModePreset largerRoad)
            {
                SuggestedRoute variant = onRoad.CopyFor(largerRoad);
                SuitabilityRoutes.Restop(variant, largerRoad, stops);
                variant.EnabledDemand = RidersAlone(settings, variant);
                if (PassesLineGates(variant, index, facts, tally))
                {
                    resolved.Add(variant);
                }
            }
        }

        // Chooses the mode from the route's own riders, re-placing its stops for the
        // mode and re-measuring once, since stops and riders depend on each other.
        // False when no vehicle of any mode on the network is installed.
        private bool SettleMode(Setting settings, SuggestedRoute route, int index, FleetFacts facts, StopContext stops, out float utilisation, out ModePreset? nextUp)
        {
            nextUp = null;
            if (!TransitModes.ChooseMode(route.Network, route.EnabledDemand, facts, out ModePreset mode, out utilisation))
            {
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {route.Network}, riders/day={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)} — DROPPED, no vehicle of any {route.Network} mode is installed");
                return false;
            }

            if (mode != route.Mode)
            {
                SuitabilityRoutes.Restop(route, mode, stops);
                route.EnabledDemand = RidersAlone(settings, route);
                if (TransitModes.ChooseMode(route.Network, route.EnabledDemand, facts, out ModePreset again, out utilisation) && again != mode)
                {
                    mode = again;
                    SuitabilityRoutes.Restop(route, mode, stops);
                    route.EnabledDemand = RidersAlone(settings, route);
                    _ = TransitModes.ChooseMode(route.Network, route.EnabledDemand, facts, out _, out utilisation);
                }
            }

            ModePreset[] ladder = TransitModes.ModesFor(route.Network);
            int rung = Array.IndexOf(ladder, mode);
            for (int i = rung + 1; i < ladder.Length; i++)
            {
                if (facts.CapacityFor(ladder[i]) > 0f)
                {
                    nextUp = ladder[i];
                    break;
                }
            }

            DeferredLog.Info(
                $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {route.Network} -> {mode}{(route.BentThroughHub ? " via an interchange" : string.Empty)}, " +
                $"riders/day alone={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, utilisation alone={(utilisation * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, " +
                $"{route.Stops.Count} stops, len={(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m");
            return true;
        }

        // The journey weight that would ride this line on its own against the existing
        // network (the same evaluation WeighCandidatesAlone makes for the pool).
        private float RidersAlone(Setting settings, SuggestedRoute route)
        {
            if (route.Stops.Count < 2)
            {
                return 0f;
            }

            LineSetProblem probe = BuildLineSetProblem(settings, new List<SuggestedRoute> { route }, 1);
            float[] before = SuitabilityLineSet.Evaluate(probe, Array.Empty<int>(), 0, before: null).After;
            return (float)SuitabilityLineSet.Evaluate(probe, s_OnlyCandidate, 1, before).Riders[0];
        }

        private static readonly int[] s_OnlyCandidate = { 0 };

        // Demand near each network node, so corridor growth can tell a street with
        // people on it from a rural through-road carrying only passing trips.
        private float[]? BuildNodeDemand(SuitabilityRoadGraph network, int2 gridSize)
        {
            if (network.Graph is null || m_RawTerms is null || network.NodeCount == 0)
            {
                return null;
            }

            var demand = new float[network.NodeCount];
            float invDemand = m_DemandCap > 0f ? 1f / m_DemandCap : 0f;
            float invJobs = m_JobsCap > 0f ? 1f / m_JobsCap : 0f;

            for (int n = 0; n < network.NodeCount; n++)
            {
                var position = new float2(network.NodePositionsX[n], network.NodePositionsZ[n]);
                int2 cell = SuitabilityInputs.WorldToCell(position, m_ScoreWorldMin, TileSize, gridSize);
                int index = cell.x + cell.y * gridSize.x;
                if (index < 0 || index >= m_RawTerms.Length)
                {
                    continue;
                }

                SuitabilityCell terms = m_RawTerms[index];
                demand[n] = SuitabilityScoring.Saturate(terms.m_Demand * invDemand)
                    + SuitabilityScoring.Saturate(terms.m_Jobs * invJobs);
            }

            return demand;
        }

        private float ScoreAtWorld(float2 point, int2 gridSize)
        {
            if (m_Scores is null)
            {
                return 0f;
            }

            int2 cell = SuitabilityInputs.WorldToCell(point, m_ScoreWorldMin, TileSize, gridSize);
            int index = cell.x + cell.y * gridSize.x;
            if (index < 0 || index >= m_Scores.Length)
            {
                return 0f;
            }

            return m_Scores[index];
        }

        // The same tile, scored for a DIFFERENT mode than the map was built for.
        //
        // The access pass keeps every mode's accumulators per network node, so this is
        // the full combine over the tile's node for the requested mode — not a swap of
        // the stop-derived terms against the panel's map. It matters because the map is
        // built for whatever mode the PANEL is showing, while a suggested line's mode is
        // decided by flow and length. A tram was being placed against the bus map:
        // penalised for sitting near bus stops, which is not its own service, and
        // rewarded for sitting near trams, which is.
        private float ScoreForMode(float2 point, int2 gridSize, ModePreset mode)
        {
            float baseScore = ScoreAtWorld(point, gridSize);
            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (mode == m_ScoredMode || access is null || inputs is null)
            {
                return baseScore;
            }

            int2 cell = SuitabilityInputs.WorldToCell(point, m_ScoreWorldMin, TileSize, gridSize);
            int index = cell.x + (cell.y * gridSize.x);
            if (index < 0 || index >= access.TileNode.Length || access.TileNode[index] < 0)
            {
                return 0f;
            }

            int cls = SuitabilityWalkAccess.ClassOf(inputs.CatchmentMs, TransitModes.CatchmentMs(mode));
            if (cls < 0)
            {
                return baseScore;
            }

            var selfType = (int)SuitabilityInputs.TransportTypeOf(mode);
            SuitabilityCell terms = SuitabilityWalkAccess.NodeTerms(access.Result, access.TileNode[index], cls, selfType, access.TypeWeight);
            terms.m_Access = (float)SuitabilityWalkAccess.Kernel(access.TileWalkMs[index], inputs.AccessMs);
            float invSelf = 1f / math.max(0.1f, StopWeightOf(SuitabilityInputs.TransportTypeOf(mode)));
            return CombineCell(in terms, invSelf);
        }

        // A ferry pier belongs where the water meets the land it serves. Scoring open
        // water at zero keeps stops off mid-crossing positions; the score of the
        // nearby land then decides which stretch of coast gets the pier.
        private float ShorelineScoreAt(float2 point, int2 gridSize)
        {
            if (m_Land is null)
            {
                return 0f;
            }

            int2 centre = SuitabilityInputs.WorldToCell(point, m_ScoreWorldMin, TileSize, gridSize);
            int span = 2;
            bool touchesLand = false;
            float best = 0f;
            var bestCell = default(int2);

            for (int dy = -span; dy <= span; dy++)
            {
                int y = centre.y + dy;
                if (y < 0 || y >= gridSize.y)
                {
                    continue;
                }
                for (int dx = -span; dx <= span; dx++)
                {
                    int x = centre.x + dx;
                    if (x < 0 || x >= gridSize.x)
                    {
                        continue;
                    }

                    int index = x + y * gridSize.x;
                    if (index >= m_Land.Length || m_Land[index] == 0)
                    {
                        continue;
                    }

                    touchesLand = true;
                    if (m_Scores is not null && index < m_Scores.Length && m_Scores[index] > best)
                    {
                        best = m_Scores[index];
                        bestCell = new int2(x, y);
                    }
                }
            }

            if (!touchesLand)
            {
                return 0f;
            }

            // Corrected for the mode being placed, like every other scoring path. This
            // read m_Scores straight, so a ferry stop was judged by whatever mode the
            // PANEL happened to be showing.
            //
            // The 5x5 sweep still picks the best cell off the uncorrected map, and only
            // the winner is re-scored: correcting all twenty-five would cost twenty-five
            // scans of the stop list per candidate position. The correction swaps three
            // stop-derived terms, which vary over the catchment radius rather than over
            // the 160 m this window spans, so it cannot reorder the cells within it.
            return ScoreForMode(
                SuitabilityInputs.CellCentre(bestCell, m_ScoreWorldMin, TileSize),
                gridSize, ModePreset.Ferry);
        }

        // Two routes are the same suggestion when they run between the same places.
        private static string RouteKeyOf(SuggestedRoute route)
        {
            if (route.Stops.Count < 2)
            {
                return "empty";
            }

            float2 from = route.Stops[0];
            float2 to = route.Stops[route.Stops.Count - 1];
            return $"{((int)from.x).ToString(CultureInfo.InvariantCulture)},{((int)from.y).ToString(CultureInfo.InvariantCulture)}>" +
                $"{((int)to.x).ToString(CultureInfo.InvariantCulture)},{((int)to.y).ToString(CultureInfo.InvariantCulture)}";
        }

        private void UpdateRouteSummary(int tripCount, int assignedPairs)
        {
            // Both are positions in the list about to be replaced. Nothing may be shown
            // as selected that is not in the list on screen, so they go with it.
            s_HighlightedRoute = -1;
            s_SelectedRoute = -1;

            if (m_Routes.Count == 0)
            {
                s_RouteList = string.Empty;
                if (tripCount < 0)
                {
                    s_RouteSummary = "No corridor was strong enough to suggest a line.";
                }
                else if (tripCount == 0)
                {
                    s_RouteSummary = "No journeys found yet — load a city and let it run.";
                }
                else
                {
                    s_RouteSummary = $"{(tripCount).ToString(CultureInfo.InvariantCulture)} journeys, {(assignedPairs).ToString(CultureInfo.InvariantCulture)} routed, but no new line would improve enough of what is still unserved.";
                }

                return;
            }

            var list = new StringBuilder();
            for (int i = 0; i < m_Routes.Count; i++)
            {
                SuggestedRoute r = m_Routes[i];
                if (i > 0)
                {
                    _ = list.Append('\n');
                }

                _ = list.Append(r.Mode);
                _ = list.Append('|');
                _ = list.Append((r.Length / 1000f).ToString("F1", CultureInfo.InvariantCulture));
                _ = list.Append('|');
                _ = list.Append(r.Stops.Count);
                _ = list.Append('|');
                _ = list.Append(r.Vehicles);
                _ = list.Append('|');
                _ = list.Append(TransitModes.ColorCssFor(r.Mode));
                _ = list.Append('|');
                // The figure the list is ordered by. Without it "best first" is a claim
                // the panel makes and cannot show, and there is no way to tell a clear
                // leader from three suggestions of much the same worth.
                float reach = m_UnservedTravelWeight > 0f ? r.EnabledDemand / m_UnservedTravelWeight : 0f;
                // A decimal below ten percent. The figure the list is RANKED by was
                // rounded to whole percent, so every suggestion in a well-served city
                // read "unlocks 0%" — including the one that unlocked the most.
                _ = list.Append((reach * 100f).ToString(reach >= 0.1f ? "F0" : "F1", CultureInfo.InvariantCulture));
                _ = list.Append('|');
                // Where the line runs between, as the row's identity. Mode, length,
                // stop count and vehicles are not one: two different suggestions can
                // agree on all four, and when they did the panel keyed two rows the
                // same and handed one row's hover to the other's line. This is the
                // identity SuggestionsChanged already compares by.
                _ = list.Append(RouteKeyOf(r));
            }
            s_RouteList = list.ToString();

            var builder = new StringBuilder();
            _ = builder.Append(m_Routes.Count);
            _ = builder.Append(" suggested: ");
            for (int i = 0; i < m_Routes.Count; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append("; ");
                }

                SuggestedRoute route = m_Routes[i];
                _ = builder.Append('#');
                _ = builder.Append(i + 1);
                _ = builder.Append(' ');
                _ = builder.Append(route.Mode);
                _ = builder.Append(' ');
                _ = builder.Append((route.Length / 1000f).ToString("F1", CultureInfo.InvariantCulture));
                _ = builder.Append("km, ");
                _ = builder.Append(route.Stops.Count);
                _ = builder.Append(" stops, ");
                _ = builder.Append(route.Vehicles);
                _ = builder.Append(" veh");
            }

            s_RouteSummary = builder.ToString();
        }

        // Only when the list actually moved. LogSites is gated the same way and for the
        // same reason: a refresh every thirty seconds that re-prints an unchanged list
        // buries the one that changed.
        private void LogRoutes()
        {
            if (!SuggestionsChanged())
            {
                return;
            }

            for (int i = 0; i < m_Routes.Count; i++)
            {
                SuggestedRoute route = m_Routes[i];
                float2 from = route.Stops.Count > 0 ? route.Stops[0] : float2.zero;
                float2 to = route.Stops.Count > 0 ? route.Stops[route.Stops.Count - 1] : float2.zero;
                DeferredLog.Info(
                    $"Route #{(i + 1).ToString(CultureInfo.InvariantCulture)}: {route.Mode}, {(route.Length / 1000f).ToString("F2", CultureInfo.InvariantCulture)} km, {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops, " +
                    $"corridorFlow={(route.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, {(route.Vehicles).ToString(CultureInfo.InvariantCulture)} vehicles, " +
                    $"{(route.BentThroughHub ? "bent through an interchange, " : string.Empty)}" +
                    $"({((int)from.x).ToString(CultureInfo.InvariantCulture)},{((int)from.y).ToString(CultureInfo.InvariantCulture)}) -> ({((int)to.x).ToString(CultureInfo.InvariantCulture)},{((int)to.y).ToString(CultureInfo.InvariantCulture)})");
            }
        }

        // Whether the suggestion list differs from the one last logged, by mode and by
        // where each line runs between.
        private bool SuggestionsChanged()
        {
            if (m_LoggedRouteEnds.Count != m_Routes.Count)
            {
                Refresh();
                return true;
            }

            for (int i = 0; i < m_Routes.Count; i++)
            {
                SuggestedRoute route = m_Routes[i];
                RouteEnds logged = m_LoggedRouteEnds[i];
                if (route.Stops.Count < 2
                    || math.distancesq(logged.m_From, route.Stops[0]) > RouteSameEndsRadiusSq
                    || math.distancesq(logged.m_To, route.Stops[route.Stops.Count - 1]) > RouteSameEndsRadiusSq)
                {
                    Refresh();
                    return true;
                }
            }

            return false;

            void Refresh()
            {
                m_LoggedRouteEnds.Clear();
                for (int i = 0; i < m_Routes.Count; i++)
                {
                    SuggestedRoute route = m_Routes[i];
                    m_LoggedRouteEnds.Add(new RouteEnds
                    {
                        m_From = route.Stops.Count > 0 ? route.Stops[0] : float2.zero,
                        m_To = route.Stops.Count > 0 ? route.Stops[route.Stops.Count - 1] : float2.zero,
                    });
                }
            }
        }

        // ---- calibration ----------------------------------------------------

        private void SampleRidership(Setting settings, float now)
        {
            if (m_RawTerms is null || now - m_LastRidershipSample < RidershipSampleSeconds)
            {
                return;
            }

            // Only sample while the simulation is actually running; a paused game
            // would otherwise contribute many identical observations.
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation is not null && simulation.selectedSpeed <= 0f)
            {
                return;
            }

            m_LastRidershipSample = now;

            // Records are keyed by world position and live in a mod setting rather
            // than the save, so a different city would otherwise inherit the last
            // one's ridership at the same coordinates.
            var configuration = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
            string city = configuration?.cityName ?? string.Empty;
            if (m_Calibration.RetargetTo(city))
            {
                DeferredLog.Info(
                    $"Ridership samples discarded: they were gathered in another city, now in \"{city}\". " +
                    "Records are keyed by world position, so they cannot be carried across.");
                settings.RidershipData = m_Calibration.Serialize();
                UpdateCalibrationStatus();
            }

            m_Calibration.Sample(EntityManager, m_StopQuery, m_PrefabSystem, settings.Mode,
                SampleFeaturesAt, ExpectedWaitAt);

            if (m_Calibration.TryFit())
            {
                DeferredLog.Info(
                    $"Ridership fit: R²={(m_Calibration.RSquared).ToString("F3", CultureInfo.InvariantCulture)}, demand={(m_Calibration.FittedDemand).ToString("F2", CultureInfo.InvariantCulture)}, " +
                    $"jobs={(m_Calibration.FittedJobs).ToString("F2", CultureInfo.InvariantCulture)}, access={(m_Calibration.FittedAccess).ToString("F2", CultureInfo.InvariantCulture)}, future={(m_Calibration.FittedFuture).ToString("F2", CultureInfo.InvariantCulture)}, " +
                    $"meanWait={(m_Calibration.MeanWaitSeconds).ToString("F0", CultureInfo.InvariantCulture)}s (the divisor in Little's law; a value in the thousands means an accumulator is being read as seconds)");
            }

            settings.RidershipData = m_Calibration.Serialize();
            UpdateCalibrationStatus();

            // Setting a property does not touch disk, so the accumulated series
            // would be lost on exit without an occasional explicit save. Collecting
            // for half an hour and losing it would be worse than the write.
            if (now - m_LastRidershipSave >= RidershipSaveSeconds)
            {
                m_LastRidershipSave = now;
                settings.ApplyAndSave();
            }
        }

        // Normalized term values at a world position, in the same units the weights
        // multiply, so a fitted weight means exactly what the slider means.
        private bool SampleFeaturesAt(float2 position, float[] features)
        {
            if (m_RawTerms is null || m_IntensityGrid.x <= 0)
            {
                return false;
            }

            int2 cell = SuitabilityInputs.WorldToCell(position, m_ScoreWorldMin, TileSize, m_IntensityGrid);
            int index = cell.x + cell.y * m_IntensityGrid.x;
            if (index < 0 || index >= m_RawTerms.Length)
            {
                return false;
            }

            // Caps come from the last combine pass rather than being recomputed here:
            // this runs once per stop per sample.
            SuitabilityCell terms = m_RawTerms[index];
            features[0] = m_DemandCap > 0f ? SuitabilityScoring.Saturate(terms.m_Demand / m_DemandCap) : 0f;
            features[1] = m_JobsCap > 0f ? SuitabilityScoring.Saturate(terms.m_Jobs / m_JobsCap) : 0f;
            features[2] = terms.m_Access;
            features[3] = m_FutureCap > 0f ? SuitabilityScoring.Saturate(terms.m_Future / m_FutureCap) : 0f;
            return true;
        }

        private void HandleCalibrationRequests(Setting settings)
        {
            if (s_ResetCalibrationRequested)
            {
                s_ResetCalibrationRequested = false;
                m_Calibration.Clear();
                settings.RidershipData = string.Empty;
                UpdateCalibrationStatus();
                DeferredLog.Info("Ridership samples reset.");
            }

            if (!s_ApplyFitRequested)
            {
                return;
            }

            s_ApplyFitRequested = false;
            if (!m_Calibration.HasFit)
            {
                DeferredLog.Info("Apply fitted weights requested, but there is no fit yet.");
                return;
            }

            settings.W1 = m_Calibration.FittedDemand;
            settings.W2 = m_Calibration.FittedJobs;
            settings.W4 = m_Calibration.FittedAccess;
            settings.W5 = m_Calibration.FittedFuture;
            settings.ApplyAndSave();
            ScheduleRecompute(0f);
            DeferredLog.Info("Applied fitted weights to the scoring sliders.");
        }

        private void UpdateCalibrationStatus()
        {
            s_CalibrationStatus = m_Calibration.BuildSummary();
        }

        private static void SetField(object target, string name, object value)
        {
            if (target is null)
            {
                return;
            }

            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                DeferredLog.Warn($"Field not found: {target.GetType().Name}.{name}");
                return;
            }

            field.SetValue(target, value);
        }
    }
}
