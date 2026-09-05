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
        private readonly SuitabilityRoadGraph m_TrainNetwork = new SuitabilityRoadGraph();
        private readonly SuitabilityRoadGraph m_MetroNetwork = new SuitabilityRoadGraph();
        private readonly SuitabilityRoadGraph m_WaterNetwork = new SuitabilityRoadGraph();
        private EntityQuery m_AllEdgeQuery;
        private byte[]? m_TrackMask;
        private readonly List<float2> m_TrackStarts = new List<float2>();
        private readonly List<float2> m_TrackEnds = new List<float2>();
        private readonly List<SuggestedRoute> m_RouteCandidates = new List<SuggestedRoute>();
        private readonly List<ZoneFlow> m_CrossWaterFlows = new List<ZoneFlow>();

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
        private int[]? m_ZoneStopsScratch;
        private static readonly int[] s_NoPairs = Array.Empty<int>();
        private static readonly float[] s_NoWeights = Array.Empty<float>();
        private static readonly List<float2> s_NoStops = new List<float2>();
        private int[]? m_CandidateOrigins;
        private int[]? m_CandidateDests;
        private float[]? m_CandidateWeights;
        // The walk to and from the candidate's own stops, and what each journey costs
        // on the network as it stands. A line earns credit for a journey only by being
        // better than what the rider already has.
        private float[]? m_CandidateAccess;
        private float[]? m_CandidateBaseline;
        private float[]? m_ZoneStopScratchDistSq;
        // Door-to-door seconds per zone flow on the existing network, or MaxValue where
        // it cannot carry the journey at all.
        private float[]? m_BaselineSeconds;
        // The suggestions accepted so far this refresh, treated as though the player
        // had built them. Every later candidate is scored against a network that
        // already contains them, so two lines cannot both be credited with the same
        // journeys — which is how four near-parallel metros came to be suggested at
        // once, each one claiming the riders of the others.
        private readonly List<float2> m_AcceptedStops = new List<float2>();
        private readonly List<TransitLine> m_AcceptedLines = new List<TransitLine>();
        private int[]? m_RoundZoneStop;
        private float[]? m_RoundZoneWalkSq;
        // Which zone flow each pair came from, so a measured journey time can be
        // written back against the flow it belongs to.
        private int[]? m_PairFlowScratch;
        private float[]? m_CandidateStopX;
        private float[]? m_CandidateStopZ;
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

        // Observed shopping/leisure demand (register A0.1): the window of journeys
        // seen, each citizen's current journey (so one journey is recorded once),
        // and the building each citizen was last seen inside (a journey's origin).
        private readonly ObservedTripWindow m_ObservedTrips = new ObservedTripWindow(LineHistory.FramesPerGameDay);
        private readonly Dictionary<Entity, (Entity target, byte purpose)> m_CurrentJourney = new Dictionary<Entity, (Entity, byte)>();
        private readonly Dictionary<Entity, Entity> m_LastBuilding = new Dictionary<Entity, Entity>();
        private EntityQuery m_TravellingQuery;
        private EntityQuery m_InsideQuery;
        private float m_LastTripObservation;
        private int m_ObservedWithoutOrigin;
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
        // Capacity per mode, read once from the prefabs. Prefabs do not change while a
        // save is loaded, so this is built on the first compute and kept; a reload
        // rebuilds it with the rest of the system.
        private float[]? m_FleetCapacities;
        // Largest vehicle capacity per Game.Prefabs.TransportType (index = enum value),
        // read alongside m_FleetCapacities. The interchange weights derive from it.
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
                Mod.Log.Info(
                    $"Infoview activation requested: activeInfoview matches={took}. " +
                    "If this is false the view is not registered as valid and the heat map will not draw.");
            }
            else if (m_ToolSystem.activeInfoview == m_InfoviewPrefab)
            {
                m_ToolSystem.infoview = null;
                Mod.Log.Info("Infoview deactivated.");
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
            m_VehiclePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PublicTransportVehicleData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

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
            m_TravellingQuery = LiveQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<TravelPurpose>(), ComponentType.ReadOnly<Game.Common.Target>());
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
                m_RecomputeRequested = false;
                return;
            }

            HandleImprovementRequest();
            EnsureInfoviewLinked();
            SweepPlaceableInfoviews();
            TrackInputChanges();
            HandleCalibrationRequests(settings);
            HandleExportRequest();
            FinishComputeIfReady();

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

            var combineSettings = CombineSnapshot.Capture(settings);
            if (active && !combineSettings.Equals(m_LastCombineSettings) && m_RawTerms is not null)
            {
                RecombineAndNormalize();
            }
            m_LastCombineSettings = combineSettings;

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

            if (active && m_RecomputeRequested && !m_JobPending && now >= m_RecomputeAt)
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
            if (now - m_LastLineSample >= DemandRefreshSeconds)
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

            MaybeUpdateTravelDemand(settings, active, now);
            SampleRidership(settings, now);
            ApplyOverlayState(active, signature);
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
                BuildRoutes(settings, m_IntensityGrid, m_ScoreWorldMin);
                UpdateRouteSummary(-1, -1);
                LogRoutes();
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
                    Mod.Log.Warn($"Failed to register infomode prefab for layer {layer}.");
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
                Mod.Log.Warn("Failed to register infoview prefab.");
            }

            m_ToolSystem.EventInfomodesChanged?.Invoke();
            m_PrefabsAdded = true;
            Mod.Log.Info($"Registered {layers.Length} suitability infomodes and the infoview prefab.");
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
                    Mod.Log.Info("Infoview prefab entity not found yet; retrying.");
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
                        Mod.Log.Info("Infomode prefab entities not all present yet; retrying.");
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

            Mod.Log.Info($"Infoview link check: buffer existed={hadBuffer}, added {(added).ToString(CultureInfo.InvariantCulture)} entries, {buffer.Length} total.");
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

            Mod.Log.Info($"Captured vanilla auto-activation for {m_VanillaPlaceableInfoviews.Count} placeable prefabs.");
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
                Mod.Log.Info($"Placeable infoview sweep ({(full ? "full" : "incremental")}): restored vanilla auto-activation on {(restored).ToString(CultureInfo.InvariantCulture)} prefabs, disabled it on {(cleared).ToString(CultureInfo.InvariantCulture)}.");
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
                Mod.Log.Warn($"{(skipped).ToString(CultureInfo.InvariantCulture)} suitability layer(s) skipped: the terrain overlay only has {SuitabilityLayers.MaxActiveLayers} channels. Turn one off to see another.");
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
                    Mod.Log.Info($"Active suitability layers: {m_ActiveChannels.Count}.");
                }
                else
                {
                    Mod.Log.Warn(
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
                Mod.Log.Info($"Overlay map {(applied ? "attached" : "detached")} (active={active}, data={(m_RawTerms is not null ? "yes" : "no")})");
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
            Mod.Log.Error(
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
                Mod.Log.Error($"Access pass failed: {pending.Exception}");
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

            Mod.Log.Info(
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

            Mod.Log.Info($"Terrain mask rebuilt: {(buildableCount).ToString(CultureInfo.InvariantCulture)}/{(cells).ToString(CultureInfo.InvariantCulture)} tiles buildable, {(componentCount).ToString(CultureInfo.InvariantCulture)} landmasses.");
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

            Mod.Log.Info(
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
            Mod.Log.Info(
                $"Site selection: {exact.Count.ToString(CultureInfo.InvariantCulture)} of up to {wanted.ToString(CultureInfo.InvariantCulture)} sites, "
                + $"spacing {(separationMs / 1000).ToString(CultureInfo.InvariantCulture)} s walk, "
                + $"{exact.Candidates.ToString(CultureInfo.InvariantCulture)} candidate network nodes, "
                + $"score sum {value} {closure}, "
                + $"{exact.Nodes.ToString(CultureInfo.InvariantCulture)} search nodes in {elapsedMs.ToString(CultureInfo.InvariantCulture)} ms"
                + (exact.WeightsExact ? string.Empty : " (integer weights floored: score spread beyond 2^33)"));

            if (exact.CandidatesTruncated)
            {
                Mod.Log.Warn("Site search hit its candidate budget; the reported sites may miss better ones.");
            }

            if (!exact.Optimal)
            {
                Mod.Log.Warn("Site search ran out of its node budget; the reported ranking is the best found, not proven optimal.");
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

            Mod.Log.Info(builder.ToString());
        }

        // ---- travel demand and routes ---------------------------------------

        // The whole demand pipeline: extract real journeys, aggregate them, load
        // them onto the road network, and grow route suggestions from the result.
        // Runs on its own slow cadence because it is far heavier than the per-tile
        // scoring — it walks every citizen and runs a shortest-path search per
        // origin zone.
        private void UpdateTravelDemand(Setting settings, int2 gridSize, float2 worldMin, float2 mapSize)
        {
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
                totalWeight = SuitabilityTravelDemand.Aggregate(trips, worldMin, m_ZoneGrid, m_ZoneFlows, out tripCount);
            }
            finally
            {
                trips.Dispose();
            }

            BuildTransitModel(gridSize);
            DiscountServedDemand(gridSize);
            m_UnservedTravelWeight = RemainingDemandWeight();
            BuildDemandLayer(settings, gridSize, worldMin);

            if (m_GraphDirty || m_RoadGraph.Graph is null)
            {
                BuildNetworks(gridSize, worldMin);
            }

            int assignedPairs = 0;
            float assignedWeight = 0f;
            if (m_RoadGraph.Graph is not null && m_ZoneNodes is not null)
            {
                // Generous cost ceiling: a trip longer than this is not a candidate
                // for a single transit line anyway.
                assignedPairs = m_RoadGraph.AssignFlow(m_ZoneFlows, m_ZoneNodes, 20000f, out assignedWeight);
                AssignLatticeFlow(m_TrainNetwork, worldMin, m_ZoneFlows);
                AssignLatticeFlow(m_MetroNetwork, worldMin, m_ZoneFlows);

                // Ferries only ever serve journeys that actually cross water. Feeding
                // the water network every trip put flow along the whole shoreline —
                // any zone within snapping distance of the coast had its ordinary
                // land trips routed out to sea — which is why coastal crawls were
                // being suggested as ferry lines.
                BuildCrossWaterFlows(gridSize);
                AssignLatticeFlow(m_WaterNetwork, worldMin, m_CrossWaterFlows);
                BuildRoutes(settings, gridSize, worldMin);
            }

            m_LastDemandRefresh = UnityEngine.Time.realtimeSinceStartup;
            UpdateRouteSummary(tripCount, assignedPairs);
            Mod.Log.Info(
                $"Travel demand: trips={(tripCount).ToString(CultureInfo.InvariantCulture)} (observed shopping/leisure {(m_ObservedLastDemand).ToString(CultureInfo.InvariantCulture)} ×{(m_ObservedScaleLastDemand).ToString("F2", CultureInfo.InvariantCulture)} over {(LineHistory.GameHours(m_ObservedTrips.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours), " +
                $"weight={(totalWeight).ToString("F0", CultureInfo.InvariantCulture)}, zonePairs={m_ZoneFlows.Count}, " +
                $"assignedPairs={(assignedPairs).ToString(CultureInfo.InvariantCulture)}, assignedWeight={(assignedWeight).ToString("F0", CultureInfo.InvariantCulture)}, " +
                $"crossWaterPairs={m_CrossWaterFlows.Count}, candidates={m_RouteCandidates.Count}, routes={m_Routes.Count}");
            LogSanityChecks(totalWeight);
        }

        // Purposes that count as shopping or leisure (register A0.1). Working,
        // studying and going home are covered by the save's own home-work/school pairs;
        // service trips (hospital, mail, garbage, crime) are not passenger demand.
        private static bool IsShoppingOrLeisure(Purpose purpose)
        {
            return purpose is Purpose.Shopping or Purpose.Leisure or Purpose.Relaxing
                or Purpose.Sightseeing or Purpose.VisitAttractions;
        }

        // One scan of the live city: remember the building every citizen is inside,
        // and record every shopping/leisure journey the moment it is first seen with
        // its Target — origin = the building the citizen was last inside. A journey
        // stays recorded once for as long as the same (citizen, target, purpose) is
        // seen; when it ends the citizen may start another.
        private void ObserveTrips()
        {
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = simulation?.frameIndex ?? 0u;
            m_TransformLookup.Update(this);
            m_PropertyRenterLookup.Update(this);

            using (var inside = m_InsideQuery.ToEntityArray(Allocator.Temp))
            using (var buildings = m_InsideQuery.ToComponentDataArray<CurrentBuilding>(Allocator.Temp))
            {
                for (int i = 0; i < inside.Length; i++)
                {
                    m_LastBuilding[inside[i]] = buildings[i].m_CurrentBuilding;
                }
            }

            using var travellers = m_TravellingQuery.ToEntityArray(Allocator.Temp);
            using var purposes = m_TravellingQuery.ToComponentDataArray<TravelPurpose>(Allocator.Temp);
            using var targets = m_TravellingQuery.ToComponentDataArray<Game.Common.Target>(Allocator.Temp);
            var seen = new HashSet<Entity>();
            for (int i = 0; i < travellers.Length; i++)
            {
                Purpose purpose = purposes[i].m_Purpose;
                if (!IsShoppingOrLeisure(purpose))
                {
                    continue;
                }

                Entity citizen = travellers[i];
                Entity target = targets[i].m_Target;
                _ = seen.Add(citizen);
                if (m_CurrentJourney.TryGetValue(citizen, out (Entity target, byte purpose) current)
                    && current.target == target && current.purpose == (byte)purpose)
                {
                    continue;
                }

                m_CurrentJourney[citizen] = (target, (byte)purpose);
                RecordObservedTrip(citizen, target, (byte)purpose, frame);
            }

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
        }

        private void RecordObservedTrip(Entity citizen, Entity target, byte purpose, uint frame)
        {
            if (!m_LastBuilding.TryGetValue(citizen, out Entity origin)
                || !TryResolveBuildingPosition(origin, out float3 from)
                || !TryResolveBuildingPosition(target, out float3 to))
            {
                m_ObservedWithoutOrigin++;
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

            if (m_ObservedTrips.EvictedSinceLastReport > 0 || m_ObservedTrips.DroppedAtCapSinceLastReport > 0 || m_ObservedWithoutOrigin > 0)
            {
                Mod.Log.Info(
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
                Mod.Log.Warn($"  SANITY: {message}");
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

                if (route.Length < TransitModes.MinLengthFor(route.Mode))
                {
                    Complain($"{label} is {(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m, under the {(TransitModes.MinLengthFor(route.Mode)).ToString("F0", CultureInfo.InvariantCulture)}m minimum for its mode — the length floor was not applied after stop placement");
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

            Mod.Log.Info(complaints == 0
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
            Mod.Log.Info(
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

            Mod.Log.Info(
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

            Mod.Log.Info(
                $"Transit model: lines={m_ExistingLines.Count}, stops={m_TransitStops.Count}, " +
                $"graphNodes={(m_TransitNetwork.Graph.NodeCount).ToString(CultureInfo.InvariantCulture)}, routablePairs={(m_PairCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"linesNeedingAttention={(problems).ToString(CultureInfo.InvariantCulture)}");

            for (int i = 0; i < m_LineHealth.Count && i < 12; i++)
            {
                LineHealth entry = m_LineHealth[i];
                Mod.Log.Info(
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
                || m_ZoneCentreZ is null || m_ZoneStopsScratch is null || m_ZoneStops.Length != zoneCount)
            {
                m_ZoneStops = new int[zoneCount];
                m_ZoneStopDistSq = new float[zoneCount];
                m_ZoneCentreX = new float[zoneCount];
                m_ZoneCentreZ = new float[zoneCount];
                m_ZoneStopsScratch = new int[zoneCount];
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
            if (m_BaselineSeconds is null || m_BaselineSeconds.Length < m_ZoneFlows.Count)
            {
                m_BaselineSeconds = new float[m_ZoneFlows.Count];
            }

            float[] baseline = m_BaselineSeconds;
            for (int i = 0; i < m_ZoneFlows.Count; i++)
            {
                baseline[i] = float.MaxValue;
            }

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

                baseline[pairFlow[i]] = doorToDoor;
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

            Mod.Log.Info(
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
                _ = ReadFleetCapacities();
            }

            float[] byType = m_TypeCapacities ?? System.Array.Empty<float>();
            int index = (int)type;
            int bus = (int)TransportType.Bus;
            float capacity = index >= 0 && index < byType.Length ? byType[index] : 0f;
            float busCapacity = bus >= 0 && bus < byType.Length ? byType[bus] : 0f;
            return TransitModes.CapacityWeight(capacity, busCapacity);
        }

        // A consist is summed the way TransportVehicleSelectData.CreateVehicle does it
        // (decompiled): the base vehicle's own capacity plus, for every entry in its
        // VehicleCarriages buffer, that carriage's capacity times the MINIMUM count —
        // `m_Count.x`, which is the game's own choice in that loop. The minimum, not the
        // maximum, because a floor must be what the smallest sensible train carries;
        // sizing it off the longest consist the player could couple would demand demand
        // for a service nobody has to run.
        //
        // The largest vehicle of each type wins, since that is what the player would
        // reach for on a line worth building.
        private FleetCapacity ReadFleetCapacities()
        {
            if (m_FleetCapacities is not null)
            {
                return new FleetCapacity(m_FleetCapacities);
            }

            var byMode = new float[TransitModes.All.Length];
            var byType = new float[(int)TransportType.Count];
            using var entities = m_VehiclePrefabQuery.ToEntityArray(Allocator.Temp);
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

                    int index = (int)mode;
                    if (index >= 0 && index < byMode.Length)
                    {
                        byMode[index] = math.max(byMode[index], capacity);
                    }
                }
            }

            m_FleetCapacities = byMode;
            m_TypeCapacities = byType;

            var report = new StringBuilder("Fleet capacities read from the loaded prefabs (one vehicle, carriages included): ");
            for (int m = 0; m < TransitModes.All.Length; m++)
            {
                ModePreset mode = TransitModes.All[m];
                float capacity = byMode[(int)mode];
                _ = report.Append(mode.ToString()).Append(' ')
                    .Append(capacity.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(" (needs ")
                    .Append(TransitModes.MinRidersFor(mode, capacity).ToString("F0", CultureInfo.InvariantCulture))
                    .Append(" journeys), ");
            }

            _ = report.Append("a capacity of 0 means no vehicle of that mode is installed, and its rider floor drops out.");
            Mod.Log.Info(report.ToString());
            return new FleetCapacity(byMode);
        }

        // Seconds spent walking to a stop that far away. The model's own walking speed,
        // the one it already uses between stops.
        private static float WalkSeconds(float distanceSq)
        {
            return (float)Math.Sqrt(distanceSq) / SuitabilityTransit.WalkSpeed;
        }

        // Re-scores candidates by the demand they would ENABLE once riders are allowed
        // to change vehicles, not just the demand along their own corridor.
        //
        // This is what makes a feeder worth building: a short line whose own corridor
        // carries almost nobody can still be the leg that unlocks hundreds of journeys
        // onto a trunk service. Each change of vehicle discounts the journey, so a
        // direct service still outranks a three-leg itinerary carrying the same people.
        // Zone-to-stop pairs for ONE candidate: the base mapping with the candidate's
        // own stops folded in, flattened into the arrays the transit router takes.
        // Returns the pair count, or 0 if the inputs are not ready.
        // Hands the arrays back rather than leaving the caller to re-test the fields:
        // the compiler discards a field's null-state across any intervening call.
        // Zone-to-stop pairs once `extraStops` are added to `incumbentStop` — the
        // mapping as it stands, whether that is the player's network or the network
        // plus the suggestions already accepted this refresh.
        private int BuildPairsWith(
            List<float2> extraStops,
            int baseStops,
            int[] incumbentStop,
            float[] incumbentWalkSq,
            out int[] origins,
            out int[] dests,
            out float[] weights,
            out float[] access,
            out float[] baseline)
        {
            origins = s_NoPairs;
            dests = s_NoPairs;
            weights = s_NoWeights;
            access = s_NoWeights;
            baseline = s_NoWeights;

            if (m_ZoneStops is null || m_ZoneStopDistSq is null || m_ZoneCentreX is null
                || m_ZoneCentreZ is null || m_ZoneStopsScratch is null || m_BaselineSeconds is null)
            {
                return 0;
            }

            if (m_ZoneStopScratchDistSq is null || m_ZoneStopScratchDistSq.Length < m_ZoneStops.Length)
            {
                m_ZoneStopScratchDistSq = new float[m_ZoneStops.Length];
            }

            int stopCount = extraStops.Count;
            EnsureCandidateBuffers(m_ZoneFlows.Count, stopCount);
            if (m_CandidateStopX is null || m_CandidateStopZ is null || m_CandidateOrigins is null
                || m_CandidateDests is null || m_CandidateWeights is null
                || m_CandidateAccess is null || m_CandidateBaseline is null || m_PairFlowScratch is null)
            {
                return 0;
            }

            for (int i = 0; i < stopCount; i++)
            {
                m_CandidateStopX[i] = extraStops[i].x;
                m_CandidateStopZ[i] = extraStops[i].y;
            }

            int[] zoneStops = m_ZoneStopsScratch;
            float[] zoneWalkSq = m_ZoneStopScratchDistSq;
            _ = SuitabilityTransit.RemapZones(
                m_ZoneCentreX, m_ZoneCentreZ, m_ZoneStops.Length,
                incumbentStop, incumbentWalkSq,
                m_CandidateStopX, m_CandidateStopZ, stopCount,
                baseStops, ZoneStopReachMetres, zoneStops, zoneWalkSq);

            origins = m_CandidateOrigins;
            dests = m_CandidateDests;
            weights = m_CandidateWeights;
            access = m_CandidateAccess;
            baseline = m_CandidateBaseline;

            int count = 0;
            for (int i = 0; i < m_ZoneFlows.Count; i++)
            {
                ZoneFlow flow = m_ZoneFlows[i];
                int origin = zoneStops[flow.m_Origin];
                int destination = zoneStops[flow.m_Destination];
                if (origin < 0 || destination < 0 || origin == destination)
                {
                    continue;
                }

                m_PairFlowScratch[count] = i;
                origins[count] = origin;
                dests[count] = destination;
                weights[count] = flow.m_Weight;
                // The walk this candidate asks for, which may be longer than the one the
                // rider already has — that is the whole point of charging for it.
                access[count] = WalkSeconds(zoneWalkSq[flow.m_Origin]) + WalkSeconds(zoneWalkSq[flow.m_Destination]);
                baseline[count] = m_BaselineSeconds[i];
                count++;
            }

            return count;
        }

        private void EnsureCandidateBuffers(int pairCapacity, int stopCapacity)
        {
            if (m_CandidateOrigins is null || m_CandidateDests is null || m_CandidateWeights is null
                || m_CandidateAccess is null || m_CandidateBaseline is null || m_PairFlowScratch is null
                || m_CandidateOrigins.Length < pairCapacity)
            {
                m_CandidateOrigins = new int[pairCapacity];
                m_CandidateDests = new int[pairCapacity];
                m_CandidateWeights = new float[pairCapacity];
                m_CandidateAccess = new float[pairCapacity];
                m_CandidateBaseline = new float[pairCapacity];
                m_PairFlowScratch = new int[pairCapacity];
            }

            if (m_CandidateStopX is null || m_CandidateStopZ is null || m_CandidateStopX.Length < stopCapacity)
            {
                m_CandidateStopX = new float[stopCapacity];
                m_CandidateStopZ = new float[stopCapacity];
            }
        }

        // Scores every candidate still in play against the network AS IT WOULD BE once
        // the suggestions already accepted this refresh are built.
        //
        // Scoring each candidate against the untouched network was how four
        // near-parallel metros came to be suggested at once: enabled demand was
        // measured independently, so every one of them was credited with the same
        // journeys and none knew the others existed. Peeling handles this during
        // corridor GROWTH, but the ranking is by enabled demand, which was never
        // peeled — the README's promise that "the demand that line would carry is
        // removed from the pool before the next suggestion" was never actually kept.
        private void ScoreCandidates(Setting settings, bool[] settled)
        {
            if (m_TransitNetwork?.Graph is null || m_RouteCandidates.Count == 0
                || m_ZoneStops is null || m_ZoneStopDistSq is null || m_BaselineSeconds is null)
            {
                return;
            }

            // The candidates worth the routing pass. Candidates arrive sorted by
            // corridor flow, and a lattice corridor's flow is systematically lower than
            // a street's, so too narrow a window scored the road candidates and nothing
            // else: every rail and water candidate kept an enabled demand of zero and
            // sank to the bottom of a ranking led by exactly that number.
            int evaluate = math.min(m_RouteCandidates.Count, settings.RouteCount * MaxScoredPerRoute);
            if (m_RouteCandidates.Count > evaluate)
            {
                Mod.Log.Info(
                    $"  transfer scoring capped at {(evaluate).ToString(CultureInfo.InvariantCulture)} of " +
                    $"{m_RouteCandidates.Count} candidates; the rest keep an enabled demand of zero and rank on corridor flow alone");
            }

            float discount = settings.TransferDiscount;

            var baseLines = SuitabilityLines.ToTransitLines(m_ExistingLines);
            baseLines.AddRange(m_AcceptedLines);
            int baseStops = m_TransitStops.Count + m_AcceptedStops.Count;

            // Zones see the accepted suggestions' stops too, so the baseline below is
            // what a rider would really have once they are built.
            if (m_RoundZoneStop is null || m_RoundZoneStop.Length < m_ZoneStops.Length
                || m_RoundZoneWalkSq is null)
            {
                m_RoundZoneStop = new int[m_ZoneStops.Length];
                m_RoundZoneWalkSq = new float[m_ZoneStops.Length];
            }

            Array.Copy(m_ZoneStops, m_RoundZoneStop, m_ZoneStops.Length);
            Array.Copy(m_ZoneStopDistSq, m_RoundZoneWalkSq, m_ZoneStopDistSq.Length);
            if (m_AcceptedStops.Count > 0)
            {
                _ = BuildPairsWith(m_AcceptedStops, m_TransitStops.Count, m_ZoneStops, m_ZoneStopDistSq,
                    out _, out _, out _, out _, out _);
                Array.Copy(m_ZoneStopsScratch, m_RoundZoneStop, m_RoundZoneStop.Length);
                Array.Copy(m_ZoneStopScratchDistSq, m_RoundZoneWalkSq, m_RoundZoneWalkSq.Length);
            }

            // Everything that does not vary between candidates is built once: the stop
            // positions, the line list which only ever gains one entry at the end, and
            // the workspace, which Resize exists to reuse.
            int widest = 0;
            for (int c = 0; c < evaluate; c++)
            {
                widest = math.max(widest, m_RouteCandidates[c].Stops.Count);
            }

            var xs = new float[baseStops + widest];
            var zs = new float[baseStops + widest];
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                xs[i] = m_TransitStops[i].x;
                zs[i] = m_TransitStops[i].y;
            }

            for (int i = 0; i < m_AcceptedStops.Count; i++)
            {
                xs[m_TransitStops.Count + i] = m_AcceptedStops[i].x;
                zs[m_TransitStops.Count + i] = m_AcceptedStops[i].y;
            }

            var lines = new List<TransitLine>(baseLines) { default };
            int candidateLine = lines.Count - 1;
            var workspace = new DijkstraWorkspace(0);

            MeasureBaseline(xs, zs, baseStops, baseLines, workspace);

            for (int c = 0; c < evaluate; c++)
            {
                SuggestedRoute candidate = m_RouteCandidates[c];
                if (settled[c] || candidate.Stops.Count < 2)
                {
                    continue;
                }

                // The candidate's stops join the stop set; walk edges then connect them
                // to whatever is already nearby, which is exactly how a new line becomes
                // an interchange.
                int total = baseStops + candidate.Stops.Count;
                var stops = new int[candidate.Stops.Count];
                for (int i = 0; i < candidate.Stops.Count; i++)
                {
                    int index = baseStops + i;
                    xs[index] = candidate.Stops[i].x;
                    zs[index] = candidate.Stops[i].y;
                    stops[i] = index;
                }

                lines[candidateLine] = new TransitLine
                {
                    m_Stops = stops,
                    m_ExpectedWait = SuggestedWaitFor(candidate.Mode),
                    m_SpeedMetresPerSecond = TransitModes.CruiseSpeedFor(candidate.Mode),
                };

                TransitNetwork withCandidate = SuitabilityTransit.Build(
                    xs, zs, total, lines, TransferWalkRadius, SuitabilityTransit.DefaultBoardPenaltySeconds);
                workspace.Resize(withCandidate.Graph.NodeCount);

                // Re-map zones against the candidate's OWN stops before routing. The
                // mapping above only knows the stops that would exist without it, so a
                // journey starting where nothing runs yet had no origin stop at all and
                // could never be credited — which made every candidate score zero
                // enabled demand, for exactly the lines most worth building.
                int pairCount = BuildPairsWith(candidate.Stops, baseStops, m_RoundZoneStop, m_RoundZoneWalkSq,
                    out int[] origins, out int[] dests, out float[] weights,
                    out float[] access, out float[] baseline);
                candidate.EnabledDemand = pairCount > 0
                    ? SuitabilityTransit.CreditLine(
                        withCandidate, workspace, origins, dests, weights, access, baseline,
                        pairCount, candidateLine, discount, MaxJourneySeconds, SwitchMarginSeconds, out float _)
                    : 0f;
                candidate.DemandScored = true;

                Mod.Log.Info(
                    $"  transfer scoring {(c).ToString(CultureInfo.InvariantCulture)} (round {(m_Routes.Count).ToString(CultureInfo.InvariantCulture)}): " +
                    $"{candidate.Network} {candidate.Mode}, {candidate.Stops.Count} stops, " +
                    $"corridorFlow={(candidate.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"routablePairs={(pairCount).ToString(CultureInfo.InvariantCulture)}, " +
                    $"against {(m_AcceptedLines.Count).ToString(CultureInfo.InvariantCulture)} already accepted");

                // Enabled demand is kept SEPARATE from corridor flow rather than
                // replacing it. It governs the ranking — which is what makes a
                // suggestion stop being offered once it is built — but the mode floors
                // are multiples of the network's mean edge flow, and only CapturedFlow
                // is on that scale. Folding the two into one field made ChooseMode
                // compare a city-wide journey-weight sum against a per-edge mean.
            }
        }

        // What every journey costs on the network as it would be once the accepted
        // suggestions are built. This is what a candidate has to beat before anyone
        // would change how they travel, and re-measuring it after each acceptance is
        // what stops the next suggestion claiming riders the last one already took.
        private void MeasureBaseline(
            float[] xs,
            float[] zs,
            int baseStops,
            List<TransitLine> baseLines,
            DijkstraWorkspace workspace)
        {
            if (m_BaselineSeconds is null || m_PairFlowScratch is null || m_RoundZoneStop is null
                || m_RoundZoneWalkSq is null)
            {
                return;
            }

            for (int i = 0; i < m_ZoneFlows.Count; i++)
            {
                m_BaselineSeconds[i] = float.MaxValue;
            }

            TransitNetwork network = SuitabilityTransit.Build(
                xs, zs, baseStops, baseLines, TransferWalkRadius, SuitabilityTransit.DefaultBoardPenaltySeconds);
            workspace.Resize(network.Graph.NodeCount);

            int pairCount = BuildPairsWith(s_NoStops, baseStops, m_RoundZoneStop, m_RoundZoneWalkSq,
                out int[] origins, out int[] dests, out float[] _, out float[] access, out float[] _);
            int[] flowOf = m_PairFlowScratch;

            int currentOrigin = -1;
            for (int i = 0; i < pairCount; i++)
            {
                int origin = origins[i];
                if (origin != currentOrigin)
                {
                    currentOrigin = origin;
                    workspace.Run(network.Graph, origin, MaxJourneySeconds);
                }

                if (SuitabilityTransit.Inspect(network, workspace, origin, dests[i], -1,
                        out int boardings, out bool _, out float travelTime)
                    && boardings > 0)
                {
                    m_BaselineSeconds[flowOf[i]] = travelTime + access[i];
                }
            }
        }

        // A proposed line has no fleet yet, so its service level is assumed from its
        // mode rather than measured.
        private static float SuggestedWaitFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Metro: return 150f;
                case ModePreset.Train: return 300f;
                case ModePreset.Tram: return 180f;
                case ModePreset.Ferry: return 400f;
                default: return 200f;
            }
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
                    Mod.Log.Info($"Improvement requested for line id {(requested).ToString(CultureInfo.InvariantCulture)}, which no longer exists.");
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

                Mod.Log.Info(
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
                case ModePreset.Train: return m_TrainNetwork;
                case ModePreset.Metro: return m_MetroNetwork;
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
                Mod.Log.Info($"Improved route for \"{health.m_Name}\": no {mode} network is built yet.");
                return;
            }

            var scratch = new List<int>();
            int from = graph.NearestNode(m_TransitStops[firstStop], ReplanSnapMetres);
            int to = graph.NearestNode(m_TransitStops[lastStop], ReplanSnapMetres);
            if (from < 0 || to < 0 || !graph.TracePath(from, to, ReplanMaxPathMetres, scratch))
            {
                Mod.Log.Info(
                    $"Improved route for \"{health.m_Name}\": no {graph.Network} path between its endpoints " +
                    $"(fromNode={(from).ToString(CultureInfo.InvariantCulture)}, toNode={(to).ToString(CultureInfo.InvariantCulture)}).");
                return;
            }

            var route = new SuggestedRoute { Network = graph.Network, Mode = mode };
            graph.MaterialisePath(scratch, route.Path);

            float length = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                length += math.distance(route.Path[i - 1], route.Path[i]);
            }

            route.Length = length;
            route.CapturedFlow = graph.FlowAlong(scratch);
            SuitabilityRoutes.Restop(route, mode, (point, forMode) => ScoreForMode(point, m_IntensityGrid, forMode), m_Interchanges);
            route.Vehicles = SuitabilityRoutes.EstimateVehicles(mode, length, route.Stops.Count,
                SuggestedWaitFor(mode) * 2f);

            m_ImprovedRoute = route.Stops.Count >= 2 ? route : null;
            s_ImprovedRouteDrawn = m_ImprovedRoute is not null;

            Mod.Log.Info(
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
        // How much better a journey has to get before anyone changes how they make it.
        // Below a minute the difference is not worth the bother, and crediting a line
        // for it is how a proposed metro came to be credited with demand that carried
        // on riding the tram.
        private const float SwitchMarginSeconds = 60f;
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
        // Share of its own network's mean edge flow a candidate must carry when it has
        // no enabled demand to show for itself. A quarter of the typical edge is a low
        // bar deliberately — it rejects the empty-country stub, not a genuinely quiet
        // but real corridor.
        private const float MinFlowShareOfReference = 0.25f;
        // Least a network's reference flow may be, as a share of the road network's.
        // Stops an empty lattice from justifying a line on noise.
        private const float MinNetworkReferenceShare = 0.25f;
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
        private const int MaxScoredPerRoute = 16;
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
        // Corridor flow — NOT enabled demand — a candidate must carry to be worth
        // drawing at all. Deliberately tiny: this rejects corridors with nothing on
        // them, not weak ones.
        private const float MinCandidateFlow = 1f;
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
        private const float RoadMaxRouteMetres = 12000f;
        // The lattice modes carry no flow fraction: BuildDirectForNetwork picks its
        // endpoints from the zone-flow table rather than from a grown ridge, so there
        // is no per-network mean edge flow to take a share of.
        private const float TrainMaxRouteMetres = 20000f;
        private const float MetroMaxRouteMetres = 15000f;
        private const float FerryMaxRouteMetres = 20000f;

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

            CompactGraph trainGraph = SuitabilityLattice.Build(gridSize, worldMin, TileSize, LandTile,
                tile => SuitabilityLattice.RailCostScale(ModePreset.Train, OnTrack(tile)),
                out float[] trainX, out float[] trainZ);
            m_TrainNetwork.Adopt(trainGraph, trainX, trainZ, RouteNetwork.Rail);

            CompactGraph metroGraph = SuitabilityLattice.Build(gridSize, worldMin, TileSize, LandTile,
                tile => SuitabilityLattice.RailCostScale(ModePreset.Metro, OnTrack(tile)),
                out float[] metroX, out float[] metroZ);
            m_MetroNetwork.Adopt(metroGraph, metroX, metroZ, RouteNetwork.Rail);

            CompactGraph waterGraph = SuitabilityLattice.Build(gridSize, worldMin, TileSize, WaterTile,
                tileCostScale: null, out float[] waterX, out float[] waterZ);
            m_WaterNetwork.Adopt(waterGraph, waterX, waterZ, RouteNetwork.Water);

            m_GraphDirty = false;
            Mod.Log.Info(
                $"Networks built: road {(m_RoadGraph.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_RoadGraph.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"rail {(m_TrainNetwork.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_TrainNetwork.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"water {(m_WaterNetwork.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_WaterNetwork.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"trackSegments={m_TrackStarts.Count}");
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

        // Keeps only journeys whose two ends sit on different landmasses — the one
        // case a boat is the right answer. Same-island trips are left to the road,
        // rail and metro networks.
        private void BuildCrossWaterFlows(int2 gridSize)
        {
            m_CrossWaterFlows.Clear();
            if (!m_Components.IsCreated)
            {
                return;
            }

            for (int i = 0; i < m_ZoneFlows.Count; i++)
            {
                ZoneFlow flow = m_ZoneFlows[i];
                int originLand = LandComponentAt(SuitabilityTravelDemand.ZoneCentre(flow.m_Origin, m_ScoreWorldMin, m_ZoneGrid), gridSize);
                int destLand = LandComponentAt(SuitabilityTravelDemand.ZoneCentre(flow.m_Destination, m_ScoreWorldMin, m_ZoneGrid), gridSize);

                if (originLand <= 0 || destLand <= 0 || originLand == destLand)
                {
                    continue;
                }

                m_CrossWaterFlows.Add(flow);
            }
        }

        private int LandComponentAt(float2 position, int2 gridSize)
        {
            int2 cell = SuitabilityInputs.WorldToCell(position, m_ScoreWorldMin, TileSize, gridSize);
            int index = cell.x + cell.y * gridSize.x;
            return index >= 0 && index < m_Components.Length ? m_Components[index] : 0;
        }

        // Each network contributes candidates for the modes it can carry; the merged
        // set is ranked by trips carried and the best kept. Auto-assignment therefore
        // falls out of which network won, rather than being guessed after the fact.
        private void BuildRoutes(Setting settings, int2 gridSize, float2 worldMin)
        {
            var objective = (RouteObjective)settings.Objective;
            m_RouteCandidates.Clear();

            float[]? roadDemand = BuildNodeDemand(m_RoadGraph, gridSize);

            const float demandFloor = CorridorDemandFloor;

            int grownTotal = 0;
            int shortTotal = 0;

            SuitabilityRoutes.BuildForNetwork(m_RoadGraph, objective, settings.RouteCount,
                RoadFlowFraction, RoadMaxRouteMetres, roadDemand, demandFloor, forcedMode: null, m_RouteCandidates,
                (point, mode) => ScoreForMode(point, gridSize, mode), m_Interchanges,
                out int g1, out int s1, out int h1);

            // The lattice flow fractions were far too high for the lattices to ever
            // produce a line, which the growth diagnostics finally made visible: at
            // 0.6 the train floor stood at 256 against a mean edge flow of 426, and
            // 226 of the refused extensions were refused by that floor alone. Train
            // corridors averaged 414 m and every one of the fifteen died against the
            // 4000 m minimum for a train. Metro at 0.4 fared little better — 1684 m
            // average against a 2000 m minimum, so 15 of 19 were thrown away.
            //
            // Lowered so a lattice corridor can actually reach the length its mode
            // requires. The demand gate, the length floors and ChooseMode's own
            // multiples of the network reference all still apply downstream, so this
            // widens what may be considered rather than what may be suggested.
            // The lattices connect two places rather than following a flow ridge — see
            // BuildDirectForNetwork for why growth is the wrong instrument on a uniform
            // grid. Ferries see only the journeys that actually cross water.
            SuitabilityRoutes.BuildDirectForNetwork(m_TrainNetwork, m_ZoneFlows,
                m_TrainNetwork.MapZonesToNodes(m_ZoneGrid, worldMin), settings.RouteCount,
                TrainMaxRouteMetres, ModePreset.Train, m_RouteCandidates,
                (point, mode) => ScoreForMode(point, gridSize, mode), m_Interchanges,
                out int g2, out int s2, out int h2);

            SuitabilityRoutes.BuildDirectForNetwork(m_MetroNetwork, m_ZoneFlows,
                m_MetroNetwork.MapZonesToNodes(m_ZoneGrid, worldMin), settings.RouteCount,
                MetroMaxRouteMetres, ModePreset.Metro, m_RouteCandidates,
                (point, mode) => ScoreForMode(point, gridSize, mode), m_Interchanges,
                out int g3, out int s3, out int h3);

            SuitabilityRoutes.BuildDirectForNetwork(m_WaterNetwork, m_CrossWaterFlows,
                m_WaterNetwork.MapZonesToNodes(m_ZoneGrid, worldMin), settings.RouteCount,
                FerryMaxRouteMetres, ModePreset.Ferry, m_RouteCandidates,
                (point, _) => ShorelineScoreAt(point, gridSize), m_Interchanges,
                out int g4, out int s4, out int h4);

            grownTotal = g1 + g2 + g3 + g4;
            shortTotal = s1 + s2 + s3 + s4;

            Mod.Log.Info(
                $"Candidates by network: road grown={(g1).ToString(CultureInfo.InvariantCulture)} tooShort={(s1).ToString(CultureInfo.InvariantCulture)}, " +
                $"train pairs tried={(g2).ToString(CultureInfo.InvariantCulture)} tooShort={(s2).ToString(CultureInfo.InvariantCulture)}, " +
                $"metro pairs tried={(g3).ToString(CultureInfo.InvariantCulture)} tooShort={(s3).ToString(CultureInfo.InvariantCulture)}, " +
                $"ferry pairs tried={(g4).ToString(CultureInfo.InvariantCulture)} tooShort={(s4).ToString(CultureInfo.InvariantCulture)}, " +
                $"termini aimed at an interchange={(h1 + h2 + h3 + h4).ToString(CultureInfo.InvariantCulture)} (road {(h1).ToString(CultureInfo.InvariantCulture)}) of {(m_Interchanges.Count).ToString(CultureInfo.InvariantCulture)} served stops, " +
                $"minLengths: bus {(TransitModes.MinLengthFor(ModePreset.Bus)).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"tram {(TransitModes.MinLengthFor(ModePreset.Tram)).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"metro {(TransitModes.MinLengthFor(ModePreset.Metro)).ToString("F0", CultureInfo.InvariantCulture)}");

            // Corridor flow is all there is to rank by until the routing pass runs, and
            // it decides which candidates are inside the scoring window. Enabled demand
            // takes over from there, and is re-measured once per accepted suggestion.
            m_RouteCandidates.Sort(static (a, b) => b.CapturedFlow.CompareTo(a.CapturedFlow));
            SelectRoutes(settings, gridSize, grownTotal, shortTotal);
        }

        // The two ends of a suggested line, kept only to compare one refresh's list
        // against the last.
        private struct RouteEnds
        {
            public float2 m_From;
            public float2 m_To;
        }

        // What a typical edge on each network carries. The mode floors are multiples of
        // this, so a corridor is judged against the network it was actually grown on.
        //
        // A named type rather than four loose floats because mixing them up is exactly
        // the defect this replaced: every alignment used to be judged against the ROAD
        // mean, and the lattices lay an edge every 128 m across the whole map where
        // streets are sparse, so lattice corridor flows ran about a third of road ones
        // (679 and 455 against 2415 and 1791 in one refresh). The metro floor was
        // unreachable and both rail candidates were dropped as "nothing justified"
        // while carrying the highest enabled demand in the run.
        private readonly struct NetworkReferences
        {
            public NetworkReferences(float road, float train, float metro, float water)
            {
                Road = road;
                Train = train;
                Metro = metro;
                Water = water;
            }

            public float Road { get; }

            public float Train { get; }

            public float Metro { get; }

            public float Water { get; }

            public float For(RouteNetwork network)
            {
                switch (network)
                {
                    // Rail carries trains and metros both, and a corridor grown on one
                    // lattice is judged against the lattice it came from.
                    case RouteNetwork.Rail: return math.max(Train, Metro);
                    case RouteNetwork.Water: return Water;
                    default: return Road;
                }
            }

            public string Describe()
            {
                return
                    $"referenceFlow(road {(Road).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"rail {(math.max(Train, Metro)).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"water {(Water).ToString("F0", CultureInfo.InvariantCulture)}) " +
                    $"(road floors: tram {(Road * TransitModes.MinFlowMultipleFor(ModePreset.Tram)).ToString("F0", CultureInfo.InvariantCulture)}; " +
                    $"rail floors: metro {(math.max(Train, Metro) * TransitModes.MinFlowMultipleFor(ModePreset.Metro)).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"train {(math.max(Train, Metro) * TransitModes.MinFlowMultipleFor(ModePreset.Train)).ToString("F0", CultureInfo.InvariantCulture)})";
            }
        }

        private NetworkReferences MeasureNetworks()
        {
            float road = SuitabilityGraphMath.MeanPositiveFlow(m_RoadGraph.EdgeFlow, m_RoadGraph.EdgeCount);

            // A network that carries almost nothing has no meaningful "typical edge",
            // and dividing by that near-zero number makes a trickle look significant.
            // The water lattice averaged a flow of 3 while the roads averaged 628: the
            // ferry floor came out at 3, a corridor carrying 5 cleared it, and a ferry
            // to nowhere with no enabled demand at all was suggested to the player.
            //
            // The floor for a network is therefore its own mean or a fixed share of
            // the city's road traffic, whichever is larger — the roads being the one
            // network that always reflects how much this city actually travels.
            float minimum = road * MinNetworkReferenceShare;
            float Floor(float mean) => math.max(mean, minimum);

            return new NetworkReferences(
                road,
                Floor(SuitabilityGraphMath.MeanPositiveFlow(m_TrainNetwork.EdgeFlow, m_TrainNetwork.EdgeCount)),
                Floor(SuitabilityGraphMath.MeanPositiveFlow(m_MetroNetwork.EdgeFlow, m_MetroNetwork.EdgeCount)),
                Floor(SuitabilityGraphMath.MeanPositiveFlow(m_WaterNetwork.EdgeFlow, m_WaterNetwork.EdgeCount)));
        }

        // Second phase: turn the grown candidates into the handful of suggestions the
        // player sees. Growing decides where a line could run; this decides whether it
        // is worth running at all, and on which mode.
        // grownTotal/shortTotal come from the growing phase purely so the one summary
        // line the log is read by stays whole.
        private void SelectRoutes(Setting settings, int2 gridSize, int grownTotal, int shortTotal)
        {
            // Pick each candidate's mode from what its demand actually justifies, and
            // re-trace it on the streets when nothing its own alignment can carry is
            // justified.
            NetworkReferences references = MeasureNetworks();
            float roadReference = references.Road;

            Mod.Log.Info(
                "Route scales: corridorFlow = mean demand per network edge along the corridor, and is what the mode " +
                "floors below are multiples of; enabledDemand = the share of still-unserved journeys the line would " +
                "newly improve, and is what the ranking uses. They differ by one to two orders of magnitude and must " +
                "never be compared with each other.");

            m_Routes.Clear();
            m_AcceptedStops.Clear();
            m_AcceptedLines.Clear();
            var tally = new RejectionTally();
            var scratch = new List<int>();
            var settled = new bool[m_RouteCandidates.Count];

            // One acceptance per round. Between rounds the accepted suggestion joins the
            // network and every remaining candidate is measured again against it, so a
            // near-duplicate's demand collapses on its own — it no longer improves the
            // journeys the accepted line already improved — and a line that COMPLEMENTS
            // it can overtake one that merely repeated it.
            for (int round = 0; round < settings.RouteCount; round++)
            {
                ScoreCandidates(settings, settled);
                if (!AcceptBestCandidate(settings, gridSize, references, roadReference, scratch, settled, tally))
                {
                    break;
                }
            }

            Mod.Log.Info(
                $"Route suggestions: grown={(grownTotal).ToString(CultureInfo.InvariantCulture)}, tooShort={(shortTotal).ToString(CultureInfo.InvariantCulture)}, " +
                $"candidates={m_RouteCandidates.Count}, unjustified={(tally.Unjustified).ToString(CultureInfo.InvariantCulture)}, retracedOnRoad={(tally.Retraced).ToString(CultureInfo.InvariantCulture)}, " +
                $"alreadyBuilt={(tally.AlreadyBuilt).ToString(CultureInfo.InvariantCulture)}, improvedTooLittle={(tally.ImprovedTooLittle).ToString(CultureInfo.InvariantCulture)}, " +
                $"kept={m_Routes.Count}, {references.Describe()}");
        }

        // Best first: the share of unserved demand a candidate would newly improve, with
        // corridor flow breaking ties — which covers the candidates past the scoring
        // window, where enabled demand was never measured at all.
        private List<int> OrderCandidates(bool[] settled)
        {
            var order = new List<int>(m_RouteCandidates.Count);
            for (int i = 0; i < m_RouteCandidates.Count; i++)
            {
                if (!settled[i])
                {
                    order.Add(i);
                }
            }

            order.Sort((left, right) =>
            {
                SuggestedRoute a = m_RouteCandidates[left];
                SuggestedRoute b = m_RouteCandidates[right];
                int byDemand = b.EnabledDemand.CompareTo(a.EnabledDemand);
                return byDemand != 0 ? byDemand : b.CapturedFlow.CompareTo(a.CapturedFlow);
            });

            return order;
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
            public int ImprovedTooLittle;
        }

        // Every bar a resolved candidate has to clear before it is worth offering, in
        // the order that makes the log readable: shape first, then the two kinds of
        // demand evidence, then whether the player has already built it. Each gate
        // reports its own reason, because "too short once trimmed" and "nobody would
        // ride it" call for opposite responses from whoever reads the log.
        //
        // Split out of AcceptBestCandidate, which owns the ranking and the acceptance;
        // this owns the rejecting. Behaviour is unchanged by the split.
        private bool SurvivesEveryBar(
            SuggestedRoute candidate,
            int index,
            float corridorFlow,
            float networkReference,
            RejectionTally tally)
        {
            // Placing the stops trimmed the line back to its termini, which can
            // leave it shorter than the floor ChooseMode approved it against.
            if (!SuitabilityRoutes.KeepsItsFloor(candidate))
            {
                tally.Unjustified++;
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(corridorFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m after stops, {candidate.Stops.Count} stops — DROPPED, " +
                    $"under the {(TransitModes.MinLengthFor(candidate.Mode)).ToString("F0", CultureInfo.InvariantCulture)}m minimum for a {candidate.Mode} once trimmed");
                return false;
            }

            // A bus has no demand floor of its own — MinFlowMultipleFor(Bus) is 0
            // so every road corridor yields a "usable" suggestion — and a bare
            // MinCandidateFlow of 1 was not a bar at all: a 540 m line carrying a
            // corridor flow of 27 against a city mean of 625, with zero enabled
            // demand, was suggested to the player. A suggestion nobody can justify
            // is worse than no suggestion.
            //
            // Either kind of evidence will do, because they answer different
            // questions: enabled demand says journeys exist that this line would
            // newly serve, corridor flow says people travel this way at all. A
            // city with no transit yet has no enabled demand anywhere, so corridor
            // flow has to be able to carry a suggestion on its own.
            if (candidate.EnabledDemand <= 0f && corridorFlow < networkReference * MinFlowShareOfReference)
            {
                tally.Unjustified++;
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(corridorFlow).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(needs {(networkReference * MinFlowShareOfReference).ToString("F0", CultureInfo.InvariantCulture)} without enabled demand), enabledDemand=0, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m — DROPPED, too little travel on this corridor to justify a line");
                return false;
            }

            // A corridor nobody travels at all is not a suggestion.
            if (corridorFlow <= MinCandidateFlow)
            {
                tally.Unjustified++;
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(corridorFlow).ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"(minimum {(MinCandidateFlow).ToString("F2", CultureInfo.InvariantCulture)}), enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m — DROPPED, no demand on this corridor");
                return false;
            }

            // The least a suggestion may be worth: enough journeys to fill one BUS at
            // the peak, the smallest vehicle the game has. A line that cannot manage
            // that is not a line, whatever mode it would run as — and the bus is the
            // one mode with no rider floor of its own, precisely so an over-ambitious
            // alignment can come back as one, so this is where that bus is judged.
            //
            // This replaced a measured bar of 0.1% of the city's unserved travel. The
            // measurement was real — over one session 37 of 101 accepted suggestions
            // enabled exactly nothing, and the rest split into 0,1,2,4,6,7,8 against
            // 38,59,84,115,291,742 out of an unserved 13709, with the bar set in that
            // gap. But a SHARE of a small city is not a bar at all: with 568 journeys
            // unserved it asked for 0.6 of one, which is how a tram enabling SIX
            // journeys came to be the single suggestion offered in Valmare. One bus
            // load is 200 journeys, above the 38-115 that measurement called useful —
            // deliberately, because those lines would have filled a fifth of a bus at
            // the peak. It could separate worthless from less worthless; it never
            // showed the upper group was worth building.
            //
            // Only when the demand was MEASURED. A candidate past the transfer scoring
            // window keeps a zero it was never routed for, and reading that as
            // "improves nothing" dropped it for a measurement nobody took.
            float busLoad = TransitModes.RidersToFillOne(ReadFleetCapacities().For(ModePreset.Bus));
            if (candidate.DemandScored && candidate.EnabledDemand < busLoad)
            {
                tally.ImprovedTooLittle++;
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(corridorFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)} — DROPPED, would newly serve " +
                    $"{(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)} journeys against the " +
                    $"{(busLoad).ToString("F0", CultureInfo.InvariantCulture)} it takes to fill one bus at the peak " +
                    $"(this city still has {(m_UnservedTravelWeight).ToString("F0", CultureInfo.InvariantCulture)} journeys unserved)");
                return false;
            }

            // A suggestion the player has already built should stop being offered.
            if (SuitabilityRoutes.DuplicatesExisting(candidate, m_ExistingLines, m_TransitStops, DuplicateLineMatchMetres))
            {
                tally.AlreadyBuilt++;
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(corridorFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"{candidate.Stops.Count} stops — DROPPED, already built");
                return false;
            }

            return true;
        }

        // Takes the best remaining candidate that survives every gate, and adds it to
        // the network the next round measures against. Returns false when nothing is
        // left worth suggesting.
        private bool AcceptBestCandidate(
            Setting settings,
            int2 gridSize,
            NetworkReferences references,
            float roadReference,
            List<int> scratch,
            bool[] settled,
            RejectionTally tally)
        {
            List<int> order = OrderCandidates(settled);
            for (int slot = 0; slot < order.Count; slot++)
            {
                int i = order[slot];
                SuggestedRoute candidate = m_RouteCandidates[i];

                float networkReference = references.For(candidate.Network);
                SuggestedRoute? resolved = ResolveCandidateMode(
                    candidate, i, gridSize, networkReference, roadReference, scratch, tally);
                if (resolved is null)
                {
                    tally.Unjustified++;
                    settled[i] = true;
                    continue;
                }

                candidate = resolved;

                // From here the candidate is judged as it will actually RUN. A
                // re-traced candidate is a road line now: its flow is the road path's
                // and its floors are the road network's, while `corridorFlow` and the
                // reference above still describe the lattice corridor it was grown as.
                //
                // Mixing the two put "corridorFlow=63 (floor 143)" on a KEPT tram in
                // the log — a line reading as approved below its own floor, against a
                // floor computed from a network it no longer runs on. The preamble two
                // screens up says these quantities must never be compared with each
                // other; this was the log doing it.
                float corridorFlow = candidate.CapturedFlow;
                networkReference = references.For(candidate.Network);

                if (!SurvivesEveryBar(candidate, i, corridorFlow, networkReference, tally))
                {
                    settled[i] = true;
                    continue;
                }

                candidate.Vehicles = SuitabilityRoutes.EstimateVehicles(
                    candidate.Mode, candidate.Length, candidate.Stops.Count,
                    SuggestedWaitFor(candidate.Mode) * 2f);

                Mod.Log.Info(
                    $"  candidate {(i).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} -> {candidate.Mode}, corridorFlow={(corridorFlow).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(floor {(networkReference * TransitModes.MinFlowMultipleFor(candidate.Mode)).ToString("F0", CultureInfo.InvariantCulture)}), " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, {candidate.Stops.Count} stops, {(candidate.Vehicles).ToString(CultureInfo.InvariantCulture)} veh — KEPT");

                m_Routes.Add(candidate);
                settled[i] = true;
                AcceptIntoNetwork(candidate);
                return true;
            }

            return false;
        }

        // Treats an accepted suggestion as though the player had built it, so the next
        // round measures every remaining candidate against a network containing it.
        private void AcceptIntoNetwork(SuggestedRoute route)
        {
            var stops = new int[route.Stops.Count];
            for (int i = 0; i < route.Stops.Count; i++)
            {
                stops[i] = m_TransitStops.Count + m_AcceptedStops.Count;
                m_AcceptedStops.Add(route.Stops[i]);
            }

            m_AcceptedLines.Add(new TransitLine
            {
                m_Stops = stops,
                m_ExpectedWait = SuggestedWaitFor(route.Mode),
                m_SpeedMetresPerSecond = TransitModes.CruiseSpeedFor(route.Mode),
            });
        }

        // Settles what mode a candidate would run as, and on what alignment.
        //
        // Returns null when nothing is justified — logging which of the two tests did
        // the rejecting, because "below every demand floor" and "too short for any
        // mode this alignment carries" call for opposite responses and the log used to
        // blame demand for both. A non-road candidate whose own alignment cannot be
        // justified is re-traced along streets rather than relabelled: a metro corridor
        // is a tunnel path, and a bus cannot drive it.
        private SuggestedRoute? ResolveCandidateMode(
            SuggestedRoute candidate,
            int index,
            int2 gridSize,
            float networkReference,
            float roadReference,
            List<int> scratch,
            RejectionTally tally)
        {
            float beforeFlow = candidate.CapturedFlow;
            var evidence = new CorridorEvidence(
                candidate.CapturedFlow,
                candidate.Length,
                candidate.EnabledDemand,
                m_UnservedTravelWeight,
                TrackShareOf(candidate),
                candidate.DemandScored);
            if (TransitModes.ChooseMode(candidate.Network, candidate.TracedMode, evidence, networkReference,
                    ReadFleetCapacities(), out ModePreset mode, out ModeRejection why))
            {
                // Spacing is mode-specific, so a changed mode needs its stops back.
                if (mode != candidate.Mode)
                {
                    SuitabilityRoutes.Restop(candidate, mode, (point, forMode) => ScoreForMode(point, gridSize, forMode), m_Interchanges);
                }

                return candidate;
            }

            if (candidate.Network == RouteNetwork.Road || candidate.Stops.Count < 2)
            {
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: road, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(tram floor {(networkReference * TransitModes.MinFlowMultipleFor(ModePreset.Tram)).ToString("F0", CultureInfo.InvariantCulture)}), enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, {candidate.Stops.Count} stops — DROPPED, " +
                    $"{(why == ModeRejection.TooShort ? $"too short: it clears a demand bar but not the {TransitModes.MinLengthFor(ModePreset.Bus).ToString("F0", CultureInfo.InvariantCulture)}m minimum for a Bus" : "below every demand bar")}");
                return null;
            }

            // Nothing this alignment can carry is justified — a tunnel for a handful of
            // riders. Re-trace the same journey along streets, where a bus or tram can
            // actually run it.
            SuggestedRoute? onRoad = SuitabilityRoutes.RetraceOnRoad(
                m_RoadGraph, candidate.Stops[0], candidate.Stops[candidate.Stops.Count - 1],
                roadReference, ReadFleetCapacities(),
                (point, mode) => ScoreForMode(point, gridSize, mode), m_Interchanges, scratch);

            if (onRoad is null)
            {
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network}, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, " +
                    $"networkReference={(networkReference).ToString("F0", CultureInfo.InvariantCulture)} — DROPPED, " +
                    $"{(why == ModeRejection.TooShort ? "too short for any mode this alignment carries" : "below every demand bar")} and no road path between its ends" +
                    $" (reach {(evidence.EnabledDemandShare * 100f).ToString("F1", CultureInfo.InvariantCulture)}% of unserved demand, " +
                    $"{(evidence.TrackShare * 100f).ToString("F0", CultureInfo.InvariantCulture)}% on existing track)");
                return null;
            }

            // Same journey, different alignment: the demand it would enable is
            // unchanged, and dropping it here would sink the retraced candidate to the
            // bottom of a ranking led by enabled demand.
            onRoad.EnabledDemand = candidate.EnabledDemand;
            onRoad.DemandScored = candidate.DemandScored;
            tally.Retraced++;
            return onRoad;
        }

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

        // Suitability score at a world position, used to choose where along a route
        // each stop sits. Previously this MOVED the stop to the best nearby tile,
        // which is why markers ended up sitting beside their own line instead of on
        // it; the route builder now only asks how good a point is.
        // How much of a candidate runs along rail that already exists.
        //
        // The rail lattice already PREFERS existing track through its cost scale, but
        // nothing measured whether the result actually used any. A train that extends
        // the network the city has is cheaper to build and likelier to be wanted than
        // one on fresh alignment, so ChooseMode lets it clear its reach bar on less
        // demand — a preference, not a requirement.
        //
        // Zero for anything not grown on the rail lattice: a street or a ferry
        // crossing has no rail alignment to follow.
        private float TrackShareOf(SuggestedRoute route)
        {
            if (route.Network != RouteNetwork.Rail || m_TrackMask is null || route.Path.Count < 2)
            {
                return 0f;
            }

            int onTrack = 0;
            int sampled = 0;
            for (int i = 0; i < route.Path.Count; i++)
            {
                int2 cell = SuitabilityInputs.WorldToCell(route.Path[i], m_ScoreWorldMin, TileSize, m_IntensityGrid);
                int index = cell.x + cell.y * m_IntensityGrid.x;
                if (index < 0 || index >= m_TrackMask.Length)
                {
                    continue;
                }

                sampled++;
                if (m_TrackMask[index] != 0)
                {
                    onTrack++;
                }
            }

            return sampled > 0 ? onTrack / (float)sampled : 0f;
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
                Mod.Log.Info(
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
                Mod.Log.Info(
                    $"Ridership samples discarded: they were gathered in another city, now in \"{city}\". " +
                    "Records are keyed by world position, so they cannot be carried across.");
                settings.RidershipData = m_Calibration.Serialize();
                UpdateCalibrationStatus();
            }

            m_Calibration.Sample(EntityManager, m_StopQuery, m_PrefabSystem, settings.Mode,
                SampleFeaturesAt, ExpectedWaitAt);

            if (m_Calibration.TryFit())
            {
                Mod.Log.Info(
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
                Mod.Log.Info("Ridership samples reset.");
            }

            if (!s_ApplyFitRequested)
            {
                return;
            }

            s_ApplyFitRequested = false;
            if (!m_Calibration.HasFit)
            {
                Mod.Log.Info("Apply fitted weights requested, but there is no fit yet.");
                return;
            }

            settings.W1 = m_Calibration.FittedDemand;
            settings.W2 = m_Calibration.FittedJobs;
            settings.W4 = m_Calibration.FittedAccess;
            settings.W5 = m_Calibration.FittedFuture;
            settings.ApplyAndSave();
            ScheduleRecompute(0f);
            Mod.Log.Info("Applied fitted weights to the scoring sliders.");
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
                Mod.Log.Warn($"Field not found: {target.GetType().Name}.{name}");
                return;
            }

            field.SetValue(target, value);
        }
    }
}
