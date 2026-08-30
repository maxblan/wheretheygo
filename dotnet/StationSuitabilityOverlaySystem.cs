using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
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
        private const float BucketSize = 128f;
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
        private const int InfomodePriority = 200;

        // Demand, jobs and future demand are raw sums with unbounded scale; each is
        // normalized against this percentile of its own positive values so all five
        // weighted terms are comparable 0..1 quantities.
        private const float TermCapPercentile = 0.98f;
        // Tiles with no road access are not viable sites; the gate fades the score
        // in over the low end of the access term.
        private const float RoadGateScale = 2f;
        // How far a rider will walk to change vehicles. Capped by the catchment so a
        // very short catchment cannot make everything an "interchange".
        private const float MaxInterchangeRadius = 250f;
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

        public static string CalibrationStatusText =>
            string.IsNullOrEmpty(s_PipelineStatus) ? s_CalibrationStatus : s_PipelineStatus + "\n" + s_CalibrationStatus;

        public static string RouteSummaryText => s_RouteSummary;

        // One route per line as "mode|km|stops|vehicles|colour", for the panel to
        // render as a colour-keyed list. A compact string avoids hand-rolling a JSON
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

        private bool m_JobPending;
        private JobHandle m_PendingHandle;
        private NativeArray<SuitabilityCell> m_PendingTerms;
        private int2 m_PendingGrid;
        private float2 m_PendingWorldMin;
        private int m_PendingStopCount;
        private int m_PendingOrphanCount;
        private int m_PendingJobSiteCount;
        private int m_PendingZonedCount;
        private int m_PendingOtherStopCount;
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

        // Cached input collections.
        private readonly List<float2> m_StopPositions = new List<float2>();
        private readonly List<float2> m_OtherStopPositions = new List<float2>();
        private readonly List<float> m_OtherStopWeights = new List<float>();
        private readonly List<float2> m_NodePositions = new List<float2>();
        private readonly List<float2> m_EdgePositions = new List<float2>();
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

        // Per-tile densities for the walk-distance refinement of reported sites.
        private float[]? m_TileDemand;
        private float[]? m_TileJobs;
        private float[]? m_DistanceScratch;
        private byte[]? m_VisitedScratch;

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
        private int[]? m_CandidateOrigins;
        private int[]? m_CandidateDests;
        private float[]? m_CandidateWeights;
        private float[]? m_CandidateStopX;
        private float[]? m_CandidateStopZ;
        private int[]? m_PairOrigins;
        private int[]? m_PairDests;
        // Which zone flow each pair came from, so the served-demand discount can write
        // back to it without re-deriving the mapping the pass above already did.
        private int[]? m_PairFlow;
        private int m_PairCount;

        private readonly List<ZoneFlow> m_ZoneFlows = new List<ZoneFlow>();
        private readonly List<SuggestedRoute> m_Routes = new List<SuggestedRoute>();
        private int[]? m_ZoneNodes;
        private float[]? m_DemandRaster;
        private int2 m_ZoneGrid;
        private float m_LastDemandRefresh;
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
            m_TileDemand = null;
            m_TileJobs = null;
            m_DistanceScratch = null;
            m_VisitedScratch = null;
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

            float2 worldMin = -mapSize * 0.5f;
            int2 gridSize = SuitabilityInputs.GridDims(mapSize, TileSize);
            int totalCells = gridSize.x * gridSize.y;
            int2 bucketGrid = SuitabilityInputs.GridDims(mapSize, BucketSize);

            EnsureMasks(settings, gridSize, worldMin);
            EnsureCollectionsCurrent(settings);

            // Per-tile densities are read on the main thread by the site
            // refinement, so the population job must be finished before we sample.
            popDeps.Complete();
            EnsureTileDensities(popData, gridSize, worldMin);

            PointBuckets stops = SuitabilityInputs.BuildBuckets(m_StopPositions, weights: null, bucketGrid, worldMin, BucketSize);
            PointBuckets nodes = SuitabilityInputs.BuildBuckets(m_NodePositions, weights: null, bucketGrid, worldMin, BucketSize);
            PointBuckets edges = SuitabilityInputs.BuildBuckets(m_EdgePositions, weights: null, bucketGrid, worldMin, BucketSize);
            PointBuckets jobs = SuitabilityInputs.BuildBuckets(m_JobPositions, m_JobWorkers, bucketGrid, worldMin, BucketSize);
            PointBuckets futureHomes = SuitabilityInputs.BuildBuckets(m_FutureHomePositions, m_FutureHomeWeights, bucketGrid, worldMin, BucketSize);
            PointBuckets futureJobs = SuitabilityInputs.BuildBuckets(m_FutureJobPositions, m_FutureJobWeights, bucketGrid, worldMin, BucketSize);
            PointBuckets otherStops = SuitabilityInputs.BuildBuckets(m_OtherStopPositions, m_OtherStopWeights, bucketGrid, worldMin, BucketSize);

            var terms = new NativeArray<SuitabilityCell>(totalCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            var job = new SuitabilityJob
            {
                GridSize = gridSize,
                BucketGridSize = bucketGrid,
                WorldMin = worldMin,
                TileSize = TileSize,
                BucketSize = BucketSize,
                CatchmentRadius = settings.CatchmentRadius,
                AccessRadius = settings.AccessRadius,
                InterchangeRadius = math.min(MaxInterchangeRadius, settings.CatchmentRadius),
                PopulationMap = popData.m_Buffer,
                PopulationCellSize = popData.m_CellSize,
                PopulationTextureSize = popData.m_TextureSize,
                StopPositions = stops.m_Positions,
                StopWeights = stops.m_Weights,
                StopOffsets = stops.m_Offsets,
                StopCounts = stops.m_Counts,
                NodePositions = nodes.m_Positions,
                NodeWeights = nodes.m_Weights,
                NodeOffsets = nodes.m_Offsets,
                NodeCounts = nodes.m_Counts,
                EdgePositions = edges.m_Positions,
                EdgeWeights = edges.m_Weights,
                EdgeOffsets = edges.m_Offsets,
                EdgeCounts = edges.m_Counts,
                JobPositions = jobs.m_Positions,
                JobWeights = jobs.m_Weights,
                JobOffsets = jobs.m_Offsets,
                JobCounts = jobs.m_Counts,
                FutureHomePositions = futureHomes.m_Positions,
                FutureHomeWeights = futureHomes.m_Weights,
                FutureHomeOffsets = futureHomes.m_Offsets,
                FutureHomeCounts = futureHomes.m_Counts,
                FutureJobPositions = futureJobs.m_Positions,
                FutureJobWeights = futureJobs.m_Weights,
                FutureJobOffsets = futureJobs.m_Offsets,
                FutureJobCounts = futureJobs.m_Counts,
                OtherStopPositions = otherStops.m_Positions,
                OtherStopWeights = otherStops.m_Weights,
                OtherStopOffsets = otherStops.m_Offsets,
                OtherStopCounts = otherStops.m_Counts,
                Components = m_Components,
                Buildable = m_Buildable,
                Terms = terms,
            };

            JobHandle handle = job.Schedule(totalCells, 64);
            m_PopulationSystem.AddReader(handle);

            stops.Dispose(handle);
            nodes.Dispose(handle);
            edges.Dispose(handle);
            jobs.Dispose(handle);
            futureHomes.Dispose(handle);
            futureJobs.Dispose(handle);
            otherStops.Dispose(handle);

            m_PendingHandle = handle;
            m_PendingTerms = terms;
            m_PendingGrid = gridSize;
            m_PendingWorldMin = worldMin;
            m_PendingStopCount = m_StopPositions.Count;
            m_PendingOrphanCount = m_LastOrphanCount;
            m_PendingJobSiteCount = m_JobPositions.Count;
            m_PendingZonedCount = m_FutureHomePositions.Count + m_FutureJobPositions.Count;
            m_PendingOtherStopCount = m_OtherStopPositions.Count;
            m_JobPending = true;
            m_PlayableGridAtCompute = GetGridSize();
            return true;
        }

        private void FinishComputeIfReady()
        {
            if (!m_JobPending || !m_PendingHandle.IsCompleted)
            {
                return;
            }

            m_PendingHandle.Complete();
            m_JobPending = false;
            m_LastComputeFinish = UnityEngine.Time.realtimeSinceStartup;

            int totalCells = m_PendingTerms.Length;
            if (m_RawTerms is null || m_RawTerms.Length != totalCells)
            {
                m_RawTerms = new SuitabilityCell[totalCells];
            }
            m_PendingTerms.CopyTo(m_RawTerms);
            m_PendingTerms.Dispose();

            m_IntensityGrid = m_PendingGrid;
            m_ScoreWorldMin = m_PendingWorldMin;
            RecombineAndNormalize();

            Mod.Log.Info(
                $"Overlay computed: grid {(m_PendingGrid.x).ToString(CultureInfo.InvariantCulture)}x{(m_PendingGrid.y).ToString(CultureInfo.InvariantCulture)}, stops={(m_PendingStopCount).ToString(CultureInfo.InvariantCulture)} " +
                $"(orphans ignored={(m_PendingOrphanCount).ToString(CultureInfo.InvariantCulture)}), otherModeStops={(m_PendingOtherStopCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"jobSites={(m_PendingJobSiteCount).ToString(CultureInfo.InvariantCulture)}, zonedCells={(m_PendingZonedCount).ToString(CultureInfo.InvariantCulture)}, sites={(m_SiteCount).ToString(CultureInfo.InvariantCulture)}");
        }

        private void DiscardPendingCompute()
        {
            if (!m_JobPending)
            {
                return;
            }

            m_PendingHandle.Complete();
            m_PendingTerms.Dispose();
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

            if (m_RoadCacheDirty || m_NodePositions.Count == 0)
            {
                SuitabilityInputs.CollectRoadNetwork(m_NodeQuery, m_RoadEdgeQuery, m_NodePositions, m_EdgePositions);
                m_RoadCacheDirty = false;
                rebuilt = true;
            }

            if (m_WorkplaceCacheDirty || m_JobPositions.Count == 0)
            {
                m_TransformLookup.Update(this);
                m_PropertyRenterLookup.Update(this);
                SuitabilityInputs.CollectWorkplaces(m_WorkplaceQuery, m_TransformLookup, m_PropertyRenterLookup, m_JobPositions, m_JobWorkers);
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
                m_StopPositions,
                m_OtherStopPositions,
                m_OtherStopWeights,
                out m_LastOrphanCount);

            if (rebuilt)
            {
                m_LastCollectionRebuild = UnityEngine.Time.realtimeSinceStartup;
            }
        }

        // Per-tile demand and jobs density, used by the walk-distance refinement of
        // the reported sites (the heatmap itself uses the coarse population map with
        // a triangular kernel, which is already calibrated).
        private void EnsureTileDensities(CellMapData<PopulationCell> popData, int2 gridSize, float2 worldMin)
        {
            int cells = gridSize.x * gridSize.y;
            if (m_TileDemand is null || m_TileJobs is null || m_DistanceScratch is null
                || m_VisitedScratch is null || m_TileDemand.Length != cells)
            {
                m_TileDemand = new float[cells];
                m_TileJobs = new float[cells];
                m_DistanceScratch = new float[cells];
                m_VisitedScratch = new byte[cells];
            }

            Array.Clear(m_TileDemand, 0, cells);
            Array.Clear(m_TileJobs, 0, cells);

            // Population spreads evenly across the tiles covering each source cell.
            float popCellArea = popData.m_CellSize.x * popData.m_CellSize.y;
            float share = popCellArea > 0f ? (TileSize * TileSize) / popCellArea : 0f;
            float2 popMapSize = popData.m_CellSize * new float2(popData.m_TextureSize.x, popData.m_TextureSize.y);
            float2 popMin = -popMapSize * 0.5f;

            for (int y = 0; y < gridSize.y; y++)
            {
                for (int x = 0; x < gridSize.x; x++)
                {
                    float2 center = worldMin + new float2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);
                    int2 cell = SuitabilityInputs.WorldToCell(center, popMin, popData.m_CellSize.x, popData.m_TextureSize);
                    m_TileDemand[x + y * gridSize.x] = popData.m_Buffer[cell.x + cell.y * popData.m_TextureSize.x].m_Population * share;
                }
            }

            for (int i = 0; i < m_JobPositions.Count; i++)
            {
                int2 cell = SuitabilityInputs.WorldToCell(m_JobPositions[i], worldMin, TileSize, gridSize);
                m_TileJobs[cell.x + cell.y * gridSize.x] += m_JobWorkers[i];
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
            float selfWeight = math.max(0.1f, SuitabilityInputs.ModeWeight(SuitabilityInputs.TransportTypeOf(settings.Mode)));
            float invSelf = 1f / selfWeight;

            for (int i = 0; i < totalCells; i++)
            {
                SuitabilityCell cell = m_RawTerms[i];
                float demand = SuitabilityScoring.Saturate(cell.m_Demand * invDemand);
                float jobs = SuitabilityScoring.Saturate(cell.m_Jobs * invJobs);
                float future = SuitabilityScoring.Saturate(cell.m_Future * invFuture);
                float coverage = cell.m_Coverage / SuitabilityJob.MaxPenalty;
                float access = cell.m_Access;
                float interchange = SuitabilityScoring.Saturate(cell.m_Interchange * invSelf);
                float crossCoverage = SuitabilityScoring.Saturate(cell.m_CrossCoverage * invSelf);

                float score = (settings.W1 * demand)
                    + (settings.W2 * jobs)
                    + (settings.W4 * access)
                    + (settings.W5 * future)
                    + (settings.W6 * interchange)
                    - (settings.W3 * coverage)
                    - (settings.W7 * crossCoverage);

                scores[i] = score * SuitabilityScoring.Saturate(access * RoadGateScale);

                // The per-term layers show the raw inputs, unweighted, so they stay
                // meaningful when a weight is set to zero.
                if (anyTermLayer)
                {
                    WriteTerm(demandLayer, i, demand);
                    WriteTerm(jobsLayer, i, jobs);
                    WriteTerm(coverageLayer, i, coverage);
                    WriteTerm(accessLayer, i, access);
                    WriteTerm(futureLayer, i, future);
                    WriteTerm(interchangeLayer, i, interchange);
                    WriteTerm(crossLayer, i, crossCoverage);
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

            float tileDemand = 0f;
            float tileJobs = 0f;
            if (m_TileDemand is not null && m_TileJobs is not null)
            {
                for (int i = 0; i < m_TileDemand.Length; i++)
                {
                    tileDemand += m_TileDemand[i];
                    tileJobs += m_TileJobs[i];
                }
            }

            Mod.Log.Info(
                $"Combine: caps demand={(m_DemandCap).ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"jobs={(m_JobsCap).ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"future={(m_FutureCap).ToString("F1", CultureInfo.InvariantCulture)} " +
                "(a cap of 0 means that term is zero everywhere and drops out of the score); " +
                $"scores min={(min).ToString("F3", CultureInfo.InvariantCulture)} max={(max).ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"{(positive).ToString(CultureInfo.InvariantCulture)}/{(totalCells).ToString(CultureInfo.InvariantCulture)} positive; " +
                $"tile totals population={(tileDemand).ToString("F0", CultureInfo.InvariantCulture)}, jobs={(tileJobs).ToString("F0", CultureInfo.InvariantCulture)}");
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

            // Sites should not cluster inside one catchment.
            int separation = math.max(2, (int)math.round(settings.CatchmentRadius / TileSize));
            int wanted = math.min(settings.SiteCount, m_SiteIndices.Length);

            m_SiteCount = SuitabilityScoring.FindTopSites(
                m_Scores, width, height, separation, wanted, m_SiteIndices, m_SiteScores, out bool truncated);

            if (truncated)
            {
                Mod.Log.Warn("Site search hit its candidate budget; the reported sites may miss better ones.");
            }

            if (m_SiteCount == 0)
            {
                return;
            }

            RefineAndRankSites(settings);
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

        private void RefineAndRankSites(Setting settings)
        {
            if (m_Land is null || m_TileDemand is null || m_TileJobs is null
                || m_DistanceScratch is null || m_VisitedScratch is null)
            {
                return;
            }

            byte[] land = m_Land;
            float[] tileDemand = m_TileDemand;
            float[] tileJobs = m_TileJobs;
            float[] distanceScratch = m_DistanceScratch;
            byte[] visitedScratch = m_VisitedScratch;

            var refined = new float[m_SiteCount];
            for (int s = 0; s < m_SiteCount; s++)
            {
                refined[s] = SuitabilityScoring.AccumulateWalkDistance(
                    m_SiteIndices[s],
                    m_IntensityGrid.x,
                    m_IntensityGrid.y,
                    TileSize,
                    settings.CatchmentRadius,
                    land,
                    tileDemand,
                    tileJobs,
                    settings.W1,
                    settings.W2,
                    distanceScratch,
                    visitedScratch,
                    out float _,
                    out float _);
            }

            // Re-rank on the walk-distance score, carrying the indices with it. The
            // reported scores are then the refined ones: swapping m_SiteScores in
            // lockstep here was dead work, since the whole array is overwritten below.
            for (int i = 1; i < m_SiteCount; i++)
            {
                for (int j = i; j > 0 && refined[j] > refined[j - 1]; j--)
                {
                    Swap(refined, j, j - 1);
                    int tmp = m_SiteIndices[j];
                    m_SiteIndices[j] = m_SiteIndices[j - 1];
                    m_SiteIndices[j - 1] = tmp;
                }
            }

            for (int s = 0; s < m_SiteCount; s++)
            {
                m_SiteScores[s] = refined[s];
            }

            // A site the walk-distance pass finds serves nobody is not a
            // recommendation. These are remote specks the Euclidean pass scored on a
            // stray road, and reporting them alongside real candidates is misleading.
            while (m_SiteCount > 0 && m_SiteScores[m_SiteCount - 1] <= 0f)
            {
                m_SiteCount--;
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
                    SchoolTripWeight = 0.6f,
                    Trips = trips.AsParallelWriter(),
                };

                job.ScheduleParallel(m_CitizenQuery, Dependency).Complete();
                totalWeight = SuitabilityTravelDemand.Aggregate(trips, worldMin, m_ZoneGrid, m_ZoneFlows, out tripCount);
            }
            finally
            {
                trips.Dispose();
            }

            BuildTransitModel(gridSize);
            DiscountServedDemand(gridSize);
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
                $"Travel demand: trips={(tripCount).ToString(CultureInfo.InvariantCulture)}, weight={(totalWeight).ToString("F0", CultureInfo.InvariantCulture)}, zonePairs={m_ZoneFlows.Count}, " +
                $"assignedPairs={(assignedPairs).ToString(CultureInfo.InvariantCulture)}, assignedWeight={(assignedWeight).ToString("F0", CultureInfo.InvariantCulture)}, " +
                $"crossWaterPairs={m_CrossWaterFlows.Count}, candidates={m_RouteCandidates.Count}, routes={m_Routes.Count}");
            LogSanityChecks(totalWeight);
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
                if (totalZoneWeight > 0f
                    && route.Length < ShortLineMetres
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
                $"{LineHistory.GameHours(m_LineHistory.WindowFrames).ToString("F0", CultureInfo.InvariantCulture)}";

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

            if (m_TransitStops.Count == 0)
            {
                m_TransitNetwork = null;
                return;
            }

            var xs = new float[m_TransitStops.Count];
            var zs = new float[m_TransitStops.Count];
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                xs[i] = m_TransitStops[i].x;
                zs[i] = m_TransitStops[i].y;
            }

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
                // rather than only filling in zones that had nothing.
                m_ZoneStopDistSq[zone] = bestSq;
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

            if (m_TransitNetwork is null || m_TransitWorkspace is null
                || m_PairOrigins is null || m_PairDests is null || m_PairFlow is null)
            {
                return;
            }

            TransitNetwork network = m_TransitNetwork;
            DijkstraWorkspace workspace = m_TransitWorkspace;
            int[] pairOrigins = m_PairOrigins;
            int[] pairDests = m_PairDests;
            int[] pairFlow = m_PairFlow;

            float weightBefore = 0f;
            float weightAfter = 0f;
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

                // How much of the journey the existing network absorbs. A trip it
                // carries in a few minutes drops out almost entirely; one that takes
                // most of the hour ceiling barely moves, because it still deserves a
                // better option. The search is capped at MaxJourneySeconds, so this
                // ratio cannot leave 0..1.
                ZoneFlow flow = m_ZoneFlows[pairFlow[i]];
                weightBefore += flow.m_Weight;
                flow.m_Weight *= SuitabilityScoring.Saturate(travelTime / MaxJourneySeconds);
                weightAfter += flow.m_Weight;
                servedPairs++;
                m_ZoneFlows[pairFlow[i]] = flow;
            }

            Mod.Log.Info(
                $"Served-demand discount: {(servedPairs).ToString(CultureInfo.InvariantCulture)} of {(m_PairCount).ToString(CultureInfo.InvariantCulture)} routable pairs already carried, " +
                $"weight {(weightBefore).ToString("F0", CultureInfo.InvariantCulture)} -> {(weightAfter).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"({((weightBefore > 0f ? (1f - weightAfter / weightBefore) * 100f : 0f)).ToString("F0", CultureInfo.InvariantCulture)}% absorbed by existing lines)");
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
        private int BuildCandidatePairs(
            SuggestedRoute candidate,
            int baseStops,
            out int[] origins,
            out int[] dests,
            out float[] weights)
        {
            origins = s_NoPairs;
            dests = s_NoPairs;
            weights = s_NoWeights;

            if (m_ZoneStops is null || m_ZoneStopDistSq is null || m_ZoneCentreX is null
                || m_ZoneCentreZ is null || m_ZoneStopsScratch is null)
            {
                return 0;
            }

            int stopCount = candidate.Stops.Count;
            EnsureCandidateBuffers(m_ZoneFlows.Count, stopCount);
            if (m_CandidateStopX is null || m_CandidateStopZ is null || m_CandidateOrigins is null
                || m_CandidateDests is null || m_CandidateWeights is null)
            {
                return 0;
            }

            for (int i = 0; i < stopCount; i++)
            {
                m_CandidateStopX[i] = candidate.Stops[i].x;
                m_CandidateStopZ[i] = candidate.Stops[i].y;
            }

            int[] zoneStops = m_ZoneStopsScratch;
            _ = SuitabilityTransit.RemapZones(
                m_ZoneCentreX, m_ZoneCentreZ, m_ZoneStops.Length,
                m_ZoneStops, m_ZoneStopDistSq,
                m_CandidateStopX, m_CandidateStopZ, stopCount,
                baseStops, ZoneStopReachMetres, zoneStops);

            origins = m_CandidateOrigins;
            dests = m_CandidateDests;
            weights = m_CandidateWeights;

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

                origins[count] = origin;
                dests[count] = destination;
                weights[count] = flow.m_Weight;
                count++;
            }

            return count;
        }

        private void EnsureCandidateBuffers(int pairCapacity, int stopCapacity)
        {
            if (m_CandidateOrigins is null || m_CandidateDests is null || m_CandidateWeights is null
                || m_CandidateOrigins.Length < pairCapacity)
            {
                m_CandidateOrigins = new int[pairCapacity];
                m_CandidateDests = new int[pairCapacity];
                m_CandidateWeights = new float[pairCapacity];
            }

            if (m_CandidateStopX is null || m_CandidateStopZ is null || m_CandidateStopX.Length < stopCapacity)
            {
                m_CandidateStopX = new float[stopCapacity];
                m_CandidateStopZ = new float[stopCapacity];
            }
        }

        private void ScoreCandidatesWithTransfers(Setting settings)
        {
            // Deliberately NOT guarded on the base pair arrays any more: each candidate
            // now builds its own. Testing them here would refuse to score anything in a
            // city with no existing stops within reach of a zone — the case where a
            // suggestion is worth the most.
            if (m_TransitNetwork?.Graph is null || m_RouteCandidates.Count == 0)
            {
                return;
            }

            // Each candidate needs its own routing pass over the whole matrix, so this
            // is bounded — but the bound has to cover every network, not just the
            // busiest. Candidates arrive sorted by corridor flow, and a lattice
            // corridor's flow is systematically lower than a street's, so a cap of
            // RouteCount * 2 scored the road candidates and nothing else: every rail
            // and water candidate kept an enabled demand of zero and sank to the
            // bottom of a ranking led by exactly that number.
            int evaluate = math.min(m_RouteCandidates.Count, settings.RouteCount * MaxScoredPerRoute);
            if (m_RouteCandidates.Count > evaluate)
            {
                Mod.Log.Info(
                    $"  transfer scoring capped at {(evaluate).ToString(CultureInfo.InvariantCulture)} of " +
                    $"{m_RouteCandidates.Count} candidates; the rest keep an enabled demand of zero and rank on corridor flow alone");
            }
            float discount = settings.TransferDiscount;

            var baseLines = SuitabilityLines.ToTransitLines(m_ExistingLines);
            int baseStops = m_TransitStops.Count;

            // Everything that does not vary between candidates is built once. The base
            // stop positions are identical every time, the line list only ever gains
            // one entry at the end, and DijkstraWorkspace.Resize exists to be reused —
            // a fresh position array, line list and workspace per candidate meant
            // rebuilding the whole city's transit graph up to ninety-six times per
            // refresh, on the main thread.
            int widest = 0;
            for (int c = 0; c < evaluate; c++)
            {
                widest = math.max(widest, m_RouteCandidates[c].Stops.Count);
            }

            var xs = new float[baseStops + widest];
            var zs = new float[baseStops + widest];
            for (int i = 0; i < baseStops; i++)
            {
                xs[i] = m_TransitStops[i].x;
                zs[i] = m_TransitStops[i].y;
            }

            var lines = new List<TransitLine>(baseLines) { default };
            int candidateLine = lines.Count - 1;
            var workspace = new DijkstraWorkspace(0);

            for (int c = 0; c < evaluate; c++)
            {
                SuggestedRoute candidate = m_RouteCandidates[c];
                if (candidate.Stops.Count < 2)
                {
                    continue;
                }

                // The candidate's stops join the existing stop set; walk edges then
                // connect them to whatever is already nearby, which is exactly how a
                // new line becomes an interchange.
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
                // base mapping only knows the stops that exist today, so a journey
                // starting where nothing runs yet had no origin stop at all and could
                // never be credited — which made every candidate score zero enabled
                // demand, for exactly the lines most worth building.
                int pairCount = BuildCandidatePairs(candidate, baseStops,
                    out int[] origins, out int[] dests, out float[] weights);
                float enabled = pairCount > 0
                    ? SuitabilityTransit.CreditLine(
                        withCandidate, workspace, origins, dests, weights,
                        pairCount, candidateLine, discount, MaxJourneySeconds, out float _)
                    : 0f;

                Mod.Log.Info(
                    $"  transfer scoring {(c).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, {candidate.Stops.Count} stops, " +
                    $"corridorFlow={(candidate.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, enabledDemand={(enabled).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"routablePairs={(pairCount).ToString(CultureInfo.InvariantCulture)} (base {(m_PairCount).ToString(CultureInfo.InvariantCulture)})");

                // Enabled demand is kept SEPARATE from corridor flow rather than
                // replacing it. It governs the ranking — which is what makes a
                // suggestion stop being offered once it is built, since its enabled
                // demand collapses while its corridor flow does not — but the mode
                // floors are multiples of the network's mean edge flow, and only
                // CapturedFlow is on that scale. Folding the two into one field made
                // ChooseMode compare a city-wide journey-weight sum against a per-edge
                // mean and promote a 3.7 km corridor to Metro on a flow of 6545 against
                // a floor of 3128 that a corridor flow of 648 never came close to.
                candidate.EnabledDemand = enabled;
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
            SuitabilityRoutes.Restop(route, mode, point => ScoreAtWorld(point, m_IntensityGrid));
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
        private const float TransferWalkRadius = 250f;
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
        // to RouteCount * 2, so this covers all of them rather than only the network
        // whose corridors happen to carry the most flow per edge.
        private const int MaxScoredPerRoute = 8;
        // How close a sampled stop entity has to be to a collected line's stop to be
        // the same stop. Generous, because the two come from different game components
        // and their positions need not agree exactly.
        private const float StopMatchRadiusSq = 40f * 40f;
        // A line below this length cannot plausibly be what unlocks a large share of a
        // city's journeys, whatever the transfer model credits it with.
        private const float ShortLineMetres = 2000f;
        // How far a suggestion's termini may move and still count as the same corridor.
        private const float RouteSameEndsRadiusSq = 200f * 200f;
        private const float ImplausibleDemandShare = 0.15f;
        // Corridor flow — NOT enabled demand — a candidate must carry to be worth
        // drawing at all. Deliberately tiny: this rejects corridors with nothing on
        // them, not weak ones.
        private const float MinCandidateFlow = 1f;
        // Journeys longer than this are not realistically made by transit.
        private const float MaxJourneySeconds = 3600f;

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
        private const float TrainFlowFraction = 0.3f;
        private const float TrainMaxRouteMetres = 20000f;
        private const float MetroFlowFraction = 0.2f;
        private const float MetroMaxRouteMetres = 15000f;
        private const float FerryFlowFraction = 0.5f;
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

        // Demand reachable from a water node, sampled from the land around it. Open
        // ocean scores nothing, which is what stops a ferry corridor crawling along
        // an empty coastline.
        private float[]? BuildWaterNodeDemand(SuitabilityRoadGraph network, int2 gridSize)
        {
            if (network.Graph is null || m_RawTerms is null || network.NodeCount == 0)
            {
                return null;
            }

            var demand = new float[network.NodeCount];
            float invDemand = m_DemandCap > 0f ? 1f / m_DemandCap : 0f;
            float invJobs = m_JobsCap > 0f ? 1f / m_JobsCap : 0f;
            // A ferry pier serves the land within walking distance of it.
            int span = math.max(2, (int)math.round(400f / TileSize));

            for (int n = 0; n < network.NodeCount; n++)
            {
                var position = new float2(network.NodePositionsX[n], network.NodePositionsZ[n]);
                int2 centre = SuitabilityInputs.WorldToCell(position, m_ScoreWorldMin, TileSize, gridSize);
                float best = 0f;

                for (int dy = -span; dy <= span; dy += 2)
                {
                    int y = centre.y + dy;
                    if (y < 0 || y >= gridSize.y)
                    {
                        continue;
                    }
                    for (int dx = -span; dx <= span; dx += 2)
                    {
                        int x = centre.x + dx;
                        if (x < 0 || x >= gridSize.x)
                        {
                            continue;
                        }

                        int index = x + y * gridSize.x;
                        if (index >= m_RawTerms.Length)
                        {
                            continue;
                        }

                        SuitabilityCell terms = m_RawTerms[index];
                        float local = SuitabilityScoring.Saturate(terms.m_Demand * invDemand)
                            + SuitabilityScoring.Saturate(terms.m_Jobs * invJobs);
                        if (local > best)
                        {
                            best = local;
                        }
                    }
                }

                demand[n] = best;
            }

            return demand;
        }

        // Each network contributes candidates for the modes it can carry; the merged
        // set is ranked by trips carried and the best kept. Auto-assignment therefore
        // falls out of which network won, rather than being guessed after the fact.
        private void BuildRoutes(Setting settings, int2 gridSize, float2 worldMin)
        {
            var objective = (RouteObjective)settings.Objective;
            m_RouteCandidates.Clear();

            float[]? roadDemand = BuildNodeDemand(m_RoadGraph, gridSize);
            float[]? trainDemand = BuildNodeDemand(m_TrainNetwork, gridSize);
            float[]? metroDemand = BuildNodeDemand(m_MetroNetwork, gridSize);

            const float demandFloor = CorridorDemandFloor;

            int grownTotal = 0;
            int shortTotal = 0;

            SuitabilityRoutes.BuildForNetwork(m_RoadGraph, objective, settings.RouteCount,
                RoadFlowFraction, RoadMaxRouteMetres, roadDemand, demandFloor, forcedMode: null, m_RouteCandidates,
                point => ScoreAtWorld(point, gridSize), out int g1, out int s1);

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
            SuitabilityRoutes.BuildForNetwork(m_TrainNetwork, objective, settings.RouteCount,
                TrainFlowFraction, TrainMaxRouteMetres, trainDemand, demandFloor, ModePreset.Train, m_RouteCandidates,
                point => ScoreAtWorld(point, gridSize), out int g2, out int s2);

            SuitabilityRoutes.BuildForNetwork(m_MetroNetwork, objective, settings.RouteCount,
                MetroFlowFraction, MetroMaxRouteMetres, metroDemand, demandFloor, ModePreset.Metro, m_RouteCandidates,
                point => ScoreAtWorld(point, gridSize), out int g3, out int s3);

            // Water is gated on the demand of the land beside it, so a ferry cannot
            // wander down an empty coast.
            SuitabilityRoutes.BuildForNetwork(m_WaterNetwork, objective, settings.RouteCount,
                FerryFlowFraction, FerryMaxRouteMetres, BuildWaterNodeDemand(m_WaterNetwork, gridSize), demandFloor,
                ModePreset.Ferry, m_RouteCandidates, point => ShorelineScoreAt(point, gridSize),
                out int g4, out int s4);

            grownTotal = g1 + g2 + g3 + g4;
            shortTotal = s1 + s2 + s3 + s4;

            Mod.Log.Info(
                $"Candidates by network: road grown={(g1).ToString(CultureInfo.InvariantCulture)} tooShort={(s1).ToString(CultureInfo.InvariantCulture)}, train grown={(g2).ToString(CultureInfo.InvariantCulture)} tooShort={(s2).ToString(CultureInfo.InvariantCulture)}, " +
                $"metro grown={(g3).ToString(CultureInfo.InvariantCulture)} tooShort={(s3).ToString(CultureInfo.InvariantCulture)}, ferry grown={(g4).ToString(CultureInfo.InvariantCulture)} tooShort={(s4).ToString(CultureInfo.InvariantCulture)}, " +
                $"minLengths: bus {(TransitModes.MinLengthFor(ModePreset.Bus)).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"tram {(TransitModes.MinLengthFor(ModePreset.Tram)).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"metro {(TransitModes.MinLengthFor(ModePreset.Metro)).ToString("F0", CultureInfo.InvariantCulture)}");

            // Before scoring, corridor flow is all there is to rank by. Afterwards the
            // enabled demand leads and corridor flow breaks ties, which also covers the
            // candidates past the scoring cutoff and a city with no transit at all,
            // where every enabled demand is zero.
            m_RouteCandidates.Sort(static (a, b) => b.CapturedFlow.CompareTo(a.CapturedFlow));
            ScoreCandidatesWithTransfers(settings);
            m_RouteCandidates.Sort(static (a, b) =>
            {
                int byDemand = b.EnabledDemand.CompareTo(a.EnabledDemand);
                return byDemand != 0 ? byDemand : b.CapturedFlow.CompareTo(a.CapturedFlow);
            });

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
                "floors below are multiples of; enabledDemand = total journey weight the line would put on the " +
                "network, summed city-wide, and is what the ranking uses. They differ by one to two orders of " +
                "magnitude and must never be compared with each other.");

            m_Routes.Clear();
            int rejected = 0;
            int retraced = 0;
            int duplicates = 0;
            var scratch = new List<int>();

            for (int i = 0; i < m_RouteCandidates.Count && m_Routes.Count < settings.RouteCount; i++)
            {
                SuggestedRoute candidate = m_RouteCandidates[i];

                float beforeFlow = candidate.CapturedFlow;
                float networkReference = references.For(candidate.Network);
                SuggestedRoute? resolved = ResolveCandidateMode(
                    candidate, i, gridSize, networkReference, roadReference, scratch, ref retraced);
                if (resolved is null)
                {
                    rejected++;
                    continue;
                }

                candidate = resolved;

                // Placing the stops trimmed the line back to its termini, which can
                // leave it shorter than the floor ChooseMode approved it against.
                if (!SuitabilityRoutes.KeepsItsFloor(candidate))
                {
                    rejected++;
                    Mod.Log.Info(
                        $"  candidate {(i).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                        $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                        $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m after stops, {candidate.Stops.Count} stops — DROPPED, " +
                        $"under the {(TransitModes.MinLengthFor(candidate.Mode)).ToString("F0", CultureInfo.InvariantCulture)}m minimum for a {candidate.Mode} once trimmed");
                    continue;
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
                if (candidate.EnabledDemand <= 0f && beforeFlow < networkReference * MinFlowShareOfReference)
                {
                    rejected++;
                    Mod.Log.Info(
                        $"  candidate {(i).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)} " +
                        $"(needs {(networkReference * MinFlowShareOfReference).ToString("F0", CultureInfo.InvariantCulture)} without enabled demand), enabledDemand=0, " +
                        $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m — DROPPED, too little travel on this corridor to justify a line");
                    continue;
                }

                // A corridor nobody travels at all is not a suggestion.
                if (beforeFlow <= MinCandidateFlow)
                {
                    rejected++;
                    Mod.Log.Info(
                        $"  candidate {(i).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(beforeFlow).ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"(minimum {(MinCandidateFlow).ToString("F2", CultureInfo.InvariantCulture)}), enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                        $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m — DROPPED, no demand on this corridor");
                    continue;
                }

                // A suggestion the player has already built should stop being offered.
                if (SuitabilityRoutes.DuplicatesExisting(candidate, m_ExistingLines, m_TransitStops, DuplicateLineMatchMetres))
                {
                    duplicates++;
                    Mod.Log.Info(
                        $"  candidate {(i).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                        $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                        $"{candidate.Stops.Count} stops — DROPPED, already built");
                    continue;
                }

                candidate.Vehicles = SuitabilityRoutes.EstimateVehicles(
                    candidate.Mode, candidate.Length, candidate.Stops.Count,
                    SuggestedWaitFor(candidate.Mode) * 2f);

                Mod.Log.Info(
                    $"  candidate {(i).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} -> {candidate.Mode}, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(floor {(networkReference * TransitModes.MinFlowMultipleFor(candidate.Mode)).ToString("F0", CultureInfo.InvariantCulture)}), " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, {candidate.Stops.Count} stops, {(candidate.Vehicles).ToString(CultureInfo.InvariantCulture)} veh — KEPT");

                m_Routes.Add(candidate);
            }

            Mod.Log.Info(
                $"Route suggestions: grown={(grownTotal).ToString(CultureInfo.InvariantCulture)}, tooShort={(shortTotal).ToString(CultureInfo.InvariantCulture)}, " +
                $"candidates={m_RouteCandidates.Count}, unjustified={(rejected).ToString(CultureInfo.InvariantCulture)}, retracedOnRoad={(retraced).ToString(CultureInfo.InvariantCulture)}, " +
                $"alreadyBuilt={(duplicates).ToString(CultureInfo.InvariantCulture)}, kept={m_Routes.Count}, {references.Describe()}");
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
            ref int retraced)
        {
            float beforeFlow = candidate.CapturedFlow;
            if (SuitabilityRoutes.ChooseMode(candidate.Network, candidate.CapturedFlow, candidate.Length,
                    networkReference, out ModePreset mode, out SuitabilityRoutes.ModeRejection why))
            {
                // Spacing is mode-specific, so a changed mode needs its stops back.
                if (mode != candidate.Mode)
                {
                    SuitabilityRoutes.Restop(candidate, mode, point => ScoreAtWorld(point, gridSize));
                }

                return candidate;
            }

            if (candidate.Network == RouteNetwork.Road || candidate.Stops.Count < 2)
            {
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: road, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(tram floor {(networkReference * TransitModes.MinFlowMultipleFor(ModePreset.Tram)).ToString("F0", CultureInfo.InvariantCulture)}), enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, {candidate.Stops.Count} stops — DROPPED, " +
                    $"{(why == SuitabilityRoutes.ModeRejection.TooShort ? $"too short: it clears a demand floor but not the {TransitModes.MinLengthFor(ModePreset.Bus).ToString("F0", CultureInfo.InvariantCulture)}m minimum for a Bus" : "below every demand floor")}");
                return null;
            }

            // Nothing this alignment can carry is justified — a tunnel for a handful of
            // riders. Re-trace the same journey along streets, where a bus or tram can
            // actually run it.
            SuggestedRoute? onRoad = SuitabilityRoutes.RetraceOnRoad(
                m_RoadGraph, candidate.Stops[0], candidate.Stops[candidate.Stops.Count - 1],
                roadReference, point => ScoreAtWorld(point, gridSize), scratch);

            if (onRoad is null)
            {
                Mod.Log.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network}, corridorFlow={(beforeFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(candidate.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, " +
                    $"networkReference={(networkReference).ToString("F0", CultureInfo.InvariantCulture)} — DROPPED, " +
                    $"{(why == SuitabilityRoutes.ModeRejection.TooShort ? "too short for any mode this alignment carries" : "below every demand floor")} and no road path between its ends");
                return null;
            }

            // Same journey, different alignment: the demand it would enable is
            // unchanged, and dropping it here would sink the retraced candidate to the
            // bottom of a ranking led by enabled demand.
            onRoad.EnabledDemand = candidate.EnabledDemand;
            retraced++;
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
                    }
                }
            }

            return touchesLand ? best + 1f : 0f;
        }

        private void UpdateRouteSummary(int tripCount, int assignedPairs)
        {
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
                    s_RouteSummary = $"{(tripCount).ToString(CultureInfo.InvariantCulture)} journeys, {(assignedPairs).ToString(CultureInfo.InvariantCulture)} routed, but no corridor was strong enough to suggest.";
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
