using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Game;
using Game.Common;
using Game.Companies;
using Game.Net;
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
        private static bool s_ApplyFitRequested;
        private static bool s_ResetCalibrationRequested;

        public static string CalibrationStatusText =>
            string.IsNullOrEmpty(s_PipelineStatus) ? s_CalibrationStatus : s_PipelineStatus + "\n" + s_CalibrationStatus;

        public static void RequestApplyFittedWeights() => s_ApplyFitRequested = true;

        public static void RequestResetCalibration() => s_ResetCalibrationRequested = true;

        private TerrainSystem m_TerrainSystem;
        private WaterSystem m_WaterSystem;
        private PopulationToGridSystem m_PopulationSystem;
        private Game.Prefabs.ZoneSystem m_ZoneSystem;
        private PrefabSystem m_PrefabSystem;
        private ToolSystem m_ToolSystem;
        private OverlayInfomodeSystem m_OverlayInfomodeSystem;

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
        private InfoviewPrefab m_InfoviewPrefab;

        private readonly SuitabilityCalibration m_Calibration = new SuitabilityCalibration();

        // One intensity array per layer, plus the interleaved RGBA buffer uploaded
        // to the terrain texture.
        private readonly byte[][] m_LayerIntensities = new byte[SuitabilityLayers.Count][];
        private byte[] m_ExpandedCache;
        private long m_ExpandedSignature = -1;
        private int2 m_IntensityGrid;

        private static MethodInfo s_GetTerrainTextureData;
        private static FieldInfo s_TerrainTextureField;
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
        private int[] m_LoggedSiteIndices;
        private float[] m_LoggedSiteScores;
        private int m_LoggedSiteCount = -1;
        private bool m_PrefabsAdded;
        private bool m_InfoviewLinkChecked;
        private bool m_InfoviewLinkWaitLogged;
        private bool m_PlaceableSweepDone;
        private float m_LastPlaceableSweep;
        private int m_LastPlaceableCount = -1;
        private Dictionary<Entity, PlaceableInfoviewItem[]> m_VanillaPlaceableInfoviews;
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
        private int2 m_GridAtCompute;

        // Cached raw terms from the last compute, plus everything derived from them.
        private SuitabilityCell[] m_RawTerms;
        private float[] m_Scores;
        private float[] m_ScoreScratch;
        private float[] m_TermScratch;
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
        private byte[] m_Land;
        private NativeArray<int> m_Components;
        private int2 m_MaskGrid;
        private bool m_MaskDirty = true;
        private Setting.ModePreset m_MaskMode;
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
        private float[] m_TileDemand;
        private float[] m_TileJobs;
        private float[] m_DistanceScratch;
        private byte[] m_VisitedScratch;

        private readonly int[] m_SiteIndices = new int[Setting.kSiteCountMax];
        private readonly float[] m_SiteScores = new float[Setting.kSiteCountMax];
        private int m_SiteCount;

        private struct ComputeSnapshot : IEquatable<ComputeSnapshot>
        {
            public Setting.ModePreset Mode;
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

            public bool Equals(ComputeSnapshot other)
            {
                return Mode == other.Mode
                    && CatchmentRadius == other.CatchmentRadius
                    && AccessRadius == other.AccessRadius
                    && MaxSlope == other.MaxSlope;
            }
        }

        private struct CombineSnapshot : IEquatable<CombineSnapshot>
        {
            public float W1;
            public float W2;
            public float W3;
            public float W4;
            public float W5;
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
                    HighlightShare = settings.HighlightShare,
                    SiteCount = settings.SiteCount,
                };
            }

            public bool Equals(CombineSnapshot other)
            {
                return W1 == other.W1 && W2 == other.W2 && W3 == other.W3 && W4 == other.W4 && W5 == other.W5
                    && HighlightShare == other.HighlightShare && SiteCount == other.SiteCount;
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

            m_NodeChangedQuery = ChangedQuery(ComponentType.ReadOnly<Node>());
            m_EdgeChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            m_WorkplaceChangedQuery = ChangedQuery(ComponentType.ReadOnly<WorkProvider>());
            m_BlockChangedQuery = ChangedQuery(ComponentType.ReadOnly<Block>());

            m_TransformLookup = GetComponentLookup<Transform>(true);
            m_PropertyRenterLookup = GetComponentLookup<Game.Buildings.PropertyRenter>(true);
            m_ZoneDataLookup = GetComponentLookup<ZoneData>(true);

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
            if (settings != null)
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
            base.OnDestroy();
        }

        private void DisposeMasks()
        {
            if (m_Buildable.IsCreated) m_Buildable.Dispose();
            if (m_Components.IsCreated) m_Components.Dispose();
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            if (settings == null)
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
            if (active && !combineSettings.Equals(m_LastCombineSettings) && m_RawTerms != null)
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
            if (active && !m_JobPending && !m_RecomputeRequested && m_RawTerms != null
                && now - m_LastComputeFinish >= PeriodicRefreshSeconds)
            {
                ScheduleRecompute(0f);
            }

            int2 currentSize = GetGridSize();
            if (active && !m_JobPending && (m_RawTerms == null || !m_GridAtCompute.Equals(currentSize)))
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

            SampleRidership(settings, now);
            ApplyOverlayState(active, signature);
        }

        public void RequestRecompute()
        {
            ScheduleRecompute(0f);
        }

        // Change tags live for a single frame, so the caches must be invalidated
        // from a per-frame check rather than sampled when a compute starts.
        private void TrackInputChanges()
        {
            if (!m_NodeChangedQuery.IsEmptyIgnoreFilter || !m_EdgeChangedQuery.IsEmptyIgnoreFilter)
            {
                m_RoadCacheDirty = true;
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
            if (m_PrefabsAdded || m_PrefabSystem == null)
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

                if (!m_PrefabSystem.AddPrefab(prefab, null, null, null))
                {
                    Mod.Log.Warn($"Failed to register infomode prefab for layer {layer}.");
                }

                var info = new InfomodeInfo();
                SetField(info, "m_Mode", prefab);
                SetField(info, "m_Priority", InfomodePriority - i);
                // Only the combined score is on by default; the rest are opt-in so
                // opening the infoview does not immediately burn all four channels.
                SetField(info, "m_Supplemental", layer != SuitabilityLayer.Score);
                SetField(info, "m_Optional", false);
                infomodeInfos.Add(info);
            }

            m_InfoviewPrefab = PrefabBase.Create<InfoviewPrefab>("StationSuitabilityOverlay");
            SetField(m_InfoviewPrefab, "m_Infomodes", infomodeInfos.ToArray());
            SetField(m_InfoviewPrefab, "m_IconPath", "coui://stationsuitabilityoverlay/StationSuitability.svg");
            SetField(m_InfoviewPrefab, "m_Priority", 900);
            SetField(m_InfoviewPrefab, "m_Group", 0);
            SetField(m_InfoviewPrefab, "m_DefaultColor", new Color(0.35f, 0.35f, 0.38f, 1f));
            SetField(m_InfoviewPrefab, "m_SecondaryColor", new Color(0.5f, 0.5f, 0.55f, 1f));
            SetField(m_InfoviewPrefab, "m_Editor", false);
            SetField(m_InfoviewPrefab, "<isValid>k__BackingField", true);

            if (!m_PrefabSystem.AddPrefab(m_InfoviewPrefab, null, null, null))
            {
                Mod.Log.Warn("Failed to register infoview prefab.");
            }

            m_ToolSystem.EventInfomodesChanged?.Invoke();
            m_PrefabsAdded = true;
            Mod.Log.Info($"Registered {layers.Length} suitability infomodes and the infoview prefab.");
        }

        private SuitabilityInfomodePrefab CreateLayerPrefab(SuitabilityLayer layer)
        {
            var prefab = PrefabBase.Create<SuitabilityInfomodePrefab>(SuitabilityLayers.NameOf(layer));
            SuitabilityLayers.ColorsOf(layer, out Color low, out Color medium, out Color high);

            SetField(prefab, "m_Priority", InfomodePriority);
            SetField(prefab, "editor", false);
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
                    buffer.Add(new InfoviewMode(
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

            Mod.Log.Info($"Infoview link check: buffer existed={hadBuffer}, added {added} entries, {buffer.Length} total.");
            m_InfoviewLinkChecked = true;
        }

        private void SnapshotVanillaPlaceableInfoviews()
        {
            m_VanillaPlaceableInfoviews = new Dictionary<Entity, PlaceableInfoviewItem[]>();
            using var entities = m_PlaceableInfoviewQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                DynamicBuffer<PlaceableInfoviewItem> buffer = EntityManager.GetBuffer<PlaceableInfoviewItem>(entities[i], true);
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
                Mod.Log.Info($"Placeable infoview sweep ({(full ? "full" : "incremental")}): restored vanilla auto-activation on {restored} prefabs, disabled it on {cleared}.");
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
                    if (m_VanillaPlaceableInfoviews != null
                        && m_VanillaPlaceableInfoviews.TryGetValue(entity, out PlaceableInfoviewItem[] vanilla))
                    {
                        for (int j = 0; j < vanilla.Length; j++)
                        {
                            buffer.Add(vanilla[j]);
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
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_InfomodeLayers.TryGetValue(entities[i], out SuitabilityLayer layer))
                {
                    continue;
                }

                int channel = actives[i].m_Index - 1;
                if (channel < 0 || channel >= SuitabilityLayers.MaxActiveLayers)
                {
                    skipped++;
                    continue;
                }

                m_ActiveChannels.Add(new KeyValuePair<int, SuitabilityLayer>(channel, layer));
                signature |= ((long)((int)layer + 1)) << (channel * 8);
            }

            if (skipped > 0 && signature != m_ExpandedSignature)
            {
                Mod.Log.Warn($"{skipped} suitability layer(s) skipped: the terrain overlay only has {SuitabilityLayers.MaxActiveLayers} channels. Turn one off to see another.");
            }

            if (m_ActiveChannels.Count > 0 && m_LastLoggedIndex != m_ActiveChannels.Count)
            {
                Mod.Log.Info($"Active suitability layers: {m_ActiveChannels.Count}.");
                m_LastLoggedIndex = m_ActiveChannels.Count;
            }

            return m_ActiveChannels.Count;
        }

        private void ApplyOverlayState(bool active, long signature)
        {
            bool applied = false;
            if (active && m_RawTerms != null && m_OverlayInfomodeSystem != null && CheckPipeline())
            {
                BuildExpandedCache(signature);
                applied = InjectOverlay();
            }

            if (applied != m_LastOverlayApplied)
            {
                Mod.Log.Info($"Overlay map {(applied ? "attached" : "detached")} (active={active}, data={(m_RawTerms != null ? "yes" : "no")})");
                m_LastOverlayApplied = applied;
            }
        }

        // Verify the reflected members once and report loudly if a game update moved
        // them, rather than silently rendering nothing forever.
        private bool CheckPipeline()
        {
            if (s_ReflectionChecked)
            {
                return s_GetTerrainTextureData != null && s_TerrainTextureField != null;
            }

            s_ReflectionChecked = true;
            s_GetTerrainTextureData = typeof(OverlayInfomodeSystem).GetMethod(
                "GetTerrainTextureData",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(int2) },
                null);
            s_TerrainTextureField = typeof(OverlayInfomodeSystem).GetField(
                "m_TerrainTexture",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (s_GetTerrainTextureData != null && s_TerrainTextureField != null)
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
                $"OverlayInfomodeSystem internals not found (GetTerrainTextureData={s_GetTerrainTextureData != null}, " +
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

            if (m_ExpandedCache == null || m_ExpandedCache.Length != cells * 4)
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
                if (source == null || source.Length < cells)
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
            var data = (NativeArray<byte>)s_GetTerrainTextureData.Invoke(m_OverlayInfomodeSystem, new object[] { m_IntensityGrid });
            m_OverlayInfomodeSystem.ApplyOverlay();

            int expected = m_IntensityGrid.x * m_IntensityGrid.y * 4;
            if (data.Length != expected || m_ExpandedCache == null || m_ExpandedCache.Length != expected)
            {
                return false;
            }

            data.CopyFrom(m_ExpandedCache);
            var texture = (Texture2D)s_TerrainTextureField.GetValue(m_OverlayInfomodeSystem);
            texture.Apply(false, false);
            return true;
        }

        // ---- compute --------------------------------------------------------

        private int2 GetGridSize()
        {
            float2 playable = m_TerrainSystem != null ? m_TerrainSystem.playableArea : new float2(0f, 0f);
            return SuitabilityInputs.GridDims(playable, TileSize);
        }

        private bool StartCompute()
        {
            var settings = Mod.Settings;
            if (m_JobPending || m_PopulationSystem == null || m_TerrainSystem == null || settings == null)
            {
                return false;
            }

            Dependency.Complete();

            CellMapData<PopulationCell> popData = m_PopulationSystem.GetData(true, out JobHandle popDeps);
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

            PointBuckets stops = SuitabilityInputs.BuildBuckets(m_StopPositions, null, bucketGrid, worldMin, BucketSize);
            PointBuckets nodes = SuitabilityInputs.BuildBuckets(m_NodePositions, null, bucketGrid, worldMin, BucketSize);
            PointBuckets edges = SuitabilityInputs.BuildBuckets(m_EdgePositions, null, bucketGrid, worldMin, BucketSize);
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
            m_GridAtCompute = GetGridSize();
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
            if (m_RawTerms == null || m_RawTerms.Length != totalCells)
            {
                m_RawTerms = new SuitabilityCell[totalCells];
            }
            m_PendingTerms.CopyTo(m_RawTerms);
            m_PendingTerms.Dispose();

            m_IntensityGrid = m_PendingGrid;
            m_ScoreWorldMin = m_PendingWorldMin;
            RecombineAndNormalize();

            Mod.Log.Info(
                $"Overlay computed: grid {m_PendingGrid.x}x{m_PendingGrid.y}, stops={m_PendingStopCount} " +
                $"(orphans ignored={m_PendingOrphanCount}), otherModeStops={m_PendingOtherStopCount}, " +
                $"jobSites={m_PendingJobSiteCount}, zonedCells={m_PendingZonedCount}, sites={m_SiteCount}");
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

            if (resized)
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
                if (m_Buildable[i] != 0) buildableCount++;
            }

            Mod.Log.Info($"Terrain mask rebuilt: {buildableCount}/{cells} tiles buildable, {componentCount} landmasses.");
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
            if (m_TileDemand == null || m_TileDemand.Length != cells)
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
            var settings = Mod.Settings;
            if (settings == null || m_RawTerms == null)
            {
                return;
            }

            int totalCells = m_RawTerms.Length;
            EnsureScoreBuffers(totalCells);

            // Each unbounded term is normalized against a high percentile of its own
            // positive values, so W1..W5 behave as real relative weights. Without
            // this, population counts drown the bounded penalty and access terms.
            m_DemandCap = TermPercentile(SuitabilityLayer.Demand, totalCells);
            m_JobsCap = TermPercentile(SuitabilityLayer.Jobs, totalCells);
            m_FutureCap = TermPercentile(SuitabilityLayer.Future, totalCells);
            float invDemand = m_DemandCap > 0f ? 1f / m_DemandCap : 0f;
            float invJobs = m_JobsCap > 0f ? 1f / m_JobsCap : 0f;
            float invFuture = m_FutureCap > 0f ? 1f / m_FutureCap : 0f;

            byte[] demandLayer = m_LayerIntensities[(int)SuitabilityLayer.Demand];
            byte[] jobsLayer = m_LayerIntensities[(int)SuitabilityLayer.Jobs];
            byte[] coverageLayer = m_LayerIntensities[(int)SuitabilityLayer.Coverage];
            byte[] accessLayer = m_LayerIntensities[(int)SuitabilityLayer.Access];
            byte[] futureLayer = m_LayerIntensities[(int)SuitabilityLayer.Future];
            byte[] interchangeLayer = m_LayerIntensities[(int)SuitabilityLayer.Interchange];
            byte[] crossLayer = m_LayerIntensities[(int)SuitabilityLayer.CrossCoverage];

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

                m_Scores[i] = score * SuitabilityScoring.Saturate(access * RoadGateScale);

                // The per-term layers show the raw inputs, unweighted, so they stay
                // meaningful when a weight is set to zero.
                demandLayer[i] = ToByte(demand);
                jobsLayer[i] = ToByte(jobs);
                coverageLayer[i] = ToByte(coverage);
                accessLayer[i] = ToByte(access);
                futureLayer[i] = ToByte(future);
                interchangeLayer[i] = ToByte(interchange);
                crossLayer[i] = ToByte(crossCoverage);
            }

            SuitabilityScoring.NormalizeIntensities(
                m_Scores,
                totalCells,
                settings.HighlightShare / 100f,
                IntensityGamma,
                m_LayerIntensities[(int)SuitabilityLayer.Score],
                m_ScoreScratch);

            ExtractSites(settings);

            // Any layer's bytes may have changed, so force the interleaved buffer to
            // be rebuilt even if the active set is identical.
            m_ExpandedSignature = -1;
        }

        private void EnsureScoreBuffers(int totalCells)
        {
            if (m_Scores == null || m_Scores.Length != totalCells)
            {
                m_Scores = new float[totalCells];
                m_ScoreScratch = new float[totalCells];
                m_TermScratch = new float[totalCells];
            }

            for (int i = 0; i < SuitabilityLayers.Count; i++)
            {
                if (m_LayerIntensities[i] == null || m_LayerIntensities[i].Length != totalCells)
                {
                    m_LayerIntensities[i] = new byte[totalCells];
                }
            }
        }

        private static byte ToByte(float normalized)
        {
            float clamped = SuitabilityScoring.Saturate(normalized);
            return (byte)math.round(clamped * 255f);
        }

        private float TermPercentile(SuitabilityLayer layer, int totalCells)
        {
            for (int i = 0; i < totalCells; i++)
            {
                SuitabilityCell cell = m_RawTerms[i];
                switch (layer)
                {
                    case SuitabilityLayer.Jobs:
                        m_TermScratch[i] = cell.m_Jobs;
                        break;
                    case SuitabilityLayer.Future:
                        m_TermScratch[i] = cell.m_Future;
                        break;
                    default:
                        m_TermScratch[i] = cell.m_Demand;
                        break;
                }
            }

            return SuitabilityScoring.PositivePercentile(m_TermScratch, totalCells, TermCapPercentile, m_ScoreScratch);
        }

        // Non-maximum suppression turns the gradient into discrete candidate sites,
        // then each survivor is re-scored with a real walk-distance expansion over
        // the landmass — affordable here precisely because there are only a handful.
        private void ExtractSites(Setting settings)
        {
            byte[] sitesLayer = m_LayerIntensities[(int)SuitabilityLayer.Sites];
            Array.Clear(sitesLayer, 0, sitesLayer.Length);
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

            // Paint each site as a small disc, brightest for the best rank, so the
            // layer reads as discrete markers rather than a gradient.
            int radius = 2;
            for (int s = 0; s < m_SiteCount; s++)
            {
                int index = m_SiteIndices[s];
                int cx = index % width;
                int cy = index / width;
                byte intensity = (byte)math.clamp(255 - s * (200 / math.max(1, m_SiteCount)), 55, 255);

                for (int dy = -radius; dy <= radius; dy++)
                {
                    int y = cy + dy;
                    if (y < 0 || y >= height) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= width) continue;
                        if (dx * dx + dy * dy > radius * radius) continue;
                        sitesLayer[x + y * width] = intensity;
                    }
                }
            }

            LogSites();
        }

        private void RefineAndRankSites(Setting settings)
        {
            if (m_Land == null || m_TileDemand == null || m_DistanceScratch == null || m_VisitedScratch == null)
            {
                return;
            }

            var refined = new float[m_SiteCount];
            for (int s = 0; s < m_SiteCount; s++)
            {
                refined[s] = SuitabilityScoring.AccumulateWalkDistance(
                    m_SiteIndices[s],
                    m_IntensityGrid.x,
                    m_IntensityGrid.y,
                    TileSize,
                    settings.CatchmentRadius,
                    m_Land,
                    m_TileDemand,
                    m_TileJobs,
                    settings.W1,
                    settings.W2,
                    m_DistanceScratch,
                    m_VisitedScratch,
                    out float _,
                    out float _);
            }

            // Re-rank on the walk-distance score, keeping the arrays aligned.
            for (int i = 1; i < m_SiteCount; i++)
            {
                for (int j = i; j > 0 && refined[j] > refined[j - 1]; j--)
                {
                    Swap(refined, j, j - 1);
                    Swap(m_SiteScores, j, j - 1);
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
            if (m_LoggedSiteCount != m_SiteCount || m_LoggedSiteIndices == null)
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

            if (m_LoggedSiteIndices == null || m_LoggedSiteIndices.Length != m_SiteIndices.Length)
            {
                m_LoggedSiteIndices = new int[m_SiteIndices.Length];
                m_LoggedSiteScores = new float[m_SiteScores.Length];
            }

            m_LoggedSiteCount = m_SiteCount;
            Array.Copy(m_SiteIndices, m_LoggedSiteIndices, m_SiteCount);
            Array.Copy(m_SiteScores, m_LoggedSiteScores, m_SiteCount);

            var builder = new StringBuilder();
            builder.Append("Recommended sites (walk-distance ranked): ");
            for (int s = 0; s < m_SiteCount; s++)
            {
                int index = m_SiteIndices[s];
                int x = index % m_IntensityGrid.x;
                int y = index / m_IntensityGrid.x;
                float2 world = m_ScoreWorldMin + new float2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);
                if (s > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('#');
                builder.Append(s + 1);
                builder.Append(" (");
                builder.Append(((int)world.x).ToString(CultureInfo.InvariantCulture));
                builder.Append(", ");
                builder.Append(((int)world.y).ToString(CultureInfo.InvariantCulture));
                builder.Append(") score ");
                builder.Append(m_SiteScores[s].ToString("F1", CultureInfo.InvariantCulture));
            }

            Mod.Log.Info(builder.ToString());
        }

        // ---- calibration ----------------------------------------------------

        private void SampleRidership(Setting settings, float now)
        {
            if (m_RawTerms == null || now - m_LastRidershipSample < RidershipSampleSeconds)
            {
                return;
            }

            // Only sample while the simulation is actually running; a paused game
            // would otherwise contribute many identical observations.
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation != null && simulation.selectedSpeed <= 0f)
            {
                return;
            }

            m_LastRidershipSample = now;
            m_Calibration.Sample(EntityManager, m_StopQuery, m_PrefabSystem, settings.Mode, SampleFeaturesAt);

            if (m_Calibration.TryFit())
            {
                Mod.Log.Info(
                    $"Ridership fit: R²={m_Calibration.RSquared:F3}, demand={m_Calibration.FittedDemand:F2}, " +
                    $"jobs={m_Calibration.FittedJobs:F2}, access={m_Calibration.FittedAccess:F2}, future={m_Calibration.FittedFuture:F2}");
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
            if (m_RawTerms == null || m_IntensityGrid.x <= 0)
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
            if (target == null)
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
