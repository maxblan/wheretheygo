using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Companies;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
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
        // The population cell map refreshes 32x per in-game day, so a periodic
        // refresh at this cadence keeps the overlay tracking city growth without
        // needing change-detection on every building and road entity.
        private const float PeriodicRefreshSeconds = 10f;
        // Road and workplace collections are cached and rebuilt from change
        // detection; this is the backstop for input changes that carry no
        // Created/Updated tag (e.g. a company's worker capacity being retuned).
        private const float CollectionRefreshSeconds = 60f;
        // The vanilla infoview initializer can refill PlaceableInfoviewItem
        // buffers, so the auto-activation strip is re-verified on this cadence in
        // addition to reacting to Created/Updated.
        private const float PlaceableReverifySeconds = 60f;
        private const float MaxPenalty = 1.5f;
        private const int InfomodePriority = 200;
        // Demand and jobs are raw sums with unbounded scale; each is normalized
        // against this percentile of its own positive values so the four weighted
        // terms are all comparable 0..1 quantities. A percentile (not the max)
        // keeps one extreme downtown cell from crushing the rest of the city.
        private const float TermCapPercentile = 0.98f;
        // Tiles with no road access within AccessRadius are not placeable station
        // sites; the gate fades the score in over the low end of the access term
        // (full score from access >= 1/RoadGateScale) so hotspots stay anchored
        // to the network instead of drifting onto empty land.
        private const float RoadGateScale = 2f;
        // Gamma < 1 lifts the low and mid range of the normalized scores so
        // low-density areas remain visible next to saturated high-density cores.
        private const float IntensityGamma = 0.6f;

        // Alpha shapes the overlay's opacity ramp: low scores barely tint the map,
        // hotspots are nearly opaque. These same fields feed the infoview panel's
        // gradient legend — if the legend ever renders its low end too faint,
        // raise LowColor's alpha rather than restructuring the ramp.
        private static readonly Color LowColor = new Color(0.12f, 0.46f, 0.18f, 0.2f);
        private static readonly Color MediumColor = new Color(0.94f, 0.84f, 0.25f, 0.65f);
        private static readonly Color HighColor = new Color(0.85f, 0.22f, 0.12f, 0.95f);

        private TerrainSystem m_TerrainSystem;
        private PopulationToGridSystem m_PopulationSystem;
        private PrefabSystem m_PrefabSystem;
        private ToolSystem m_ToolSystem;
        private EntityQuery m_StopQuery;
        private EntityQuery m_StopChangedQuery;
        private EntityQuery m_NodeQuery;
        private EntityQuery m_RoadEdgeQuery;
        private EntityQuery m_WorkplaceQuery;
        private EntityQuery m_NodeChangedQuery;
        private EntityQuery m_EdgeChangedQuery;
        private EntityQuery m_WorkplaceChangedQuery;
        private EntityQuery m_ActiveInfomodeQuery;
        private EntityQuery m_PlaceableInfoviewQuery;
        private EntityQuery m_PlaceableInfoviewChangedQuery;
        private int m_LastStopCount;

        // Chunk-index lookups instead of per-entity EntityManager calls: the
        // workplace collection touches every WorkProvider in the city, so the
        // per-entity archetype resolution EntityManager does is worth avoiding.
        private ComponentLookup<Transform> m_TransformLookup;
        private ComponentLookup<Game.Buildings.PropertyRenter> m_PropertyRenterLookup;

        // Collected inputs are reused across recomputes; the dirty flags are set
        // from change detection every frame (the tags only live for one frame, so
        // they cannot be sampled lazily at compute time).
        private List<float2> m_CachedNodePositions;
        private List<float2> m_CachedEdgePositions;
        private List<float2> m_CachedJobPositions;
        private List<float> m_CachedJobWorkers;
        private bool m_RoadCacheDirty = true;
        private bool m_WorkplaceCacheDirty = true;
        private float m_LastCollectionRefresh;

        private SuitabilityInfomodePrefab m_InfomodePrefab;
        private InfoviewPrefab m_InfoviewPrefab;

        private OverlayInfomodeSystem m_OverlayInfomodeSystem;
        // Intensities (0..255) from the last compute, expanded to RGBA bytes for the
        // channel currently assigned to our infomode.
        private byte[] m_Intensities;
        private byte[] m_ExpandedCache;
        private int m_ExpandedChannel = -1;
        private int2 m_IntensityGrid;

        private static MethodInfo s_GetTerrainTextureData;
        private static FieldInfo s_TerrainTextureField;

        private bool m_LastActive;
        private int m_LastChannel = -1;
        private int m_LastLoggedIndex = int.MinValue;
        private ComputeSnapshot m_LastComputeSettings;
        private CombineSnapshot m_LastCombineSettings;

        private bool m_RecomputeRequested;
        private float m_RecomputeAt;
        private float m_LastComputeFinish;
        private bool m_PrefabsAdded;
        private bool m_InfoviewLinkChecked;
        private bool m_InfoviewLinkWaitLogged;
        private bool m_PlaceableSweepDone;
        private float m_LastPlaceableSweep;
        private int m_LastPlaceableCount = -1;
        // Each placeable prefab's PlaceableInfoviewItem buffer as it stood BEFORE
        // this mod registered its infoview, so the vanilla auto-activation choice
        // can be put back verbatim when our infoview displaces it. Vanilla's
        // runner-up is never stored in the buffer itself, so it is unrecoverable
        // any other way short of reimplementing the game's scoring pass.
        private Dictionary<Entity, PlaceableInfoviewItem[]> m_VanillaPlaceableInfoviews;
        private bool m_LastOverlayApplied;

        // In-flight suitability job; resolved in FinishComputeIfReady so a large
        // recompute never blocks the frame it was scheduled on.
        private bool m_JobPending;
        private JobHandle m_PendingHandle;
        private NativeArray<float4> m_PendingTerms;
        private int2 m_PendingGrid;
        private int m_PendingStopCount;
        private int m_PendingJobSiteCount;
        private int m_PendingNodeCount;
        private int m_PendingEdgeCount;
        // Playable-area grid observed when the last compute was scheduled. The
        // resize trigger compares against this snapshot (not m_IntensityGrid, which
        // is derived from the cell-map extents) so a mismatch between the two
        // derivations cannot cause a recompute-every-frame loop.
        private int2 m_GridAtCompute;
        // Per-cell raw terms from the last compute (x=demand, y=jobs, z=coverage
        // penalty, w=access). Weight and highlight-share changes re-run only the
        // combine/normalize pass over these instead of the whole job.
        private float4[] m_RawTerms;
        private float[] m_Scores;
        private float[] m_ScoreScratch;

        // Settings that change the job's inputs; anything else (weights, highlight
        // share) only affects the managed combine pass over the cached terms.
        private struct ComputeSnapshot : IEquatable<ComputeSnapshot>
        {
            public Setting.ModePreset Mode;
            public int CatchmentRadius;
            public int AccessRadius;

            public static ComputeSnapshot Capture(Setting settings)
            {
                return new ComputeSnapshot
                {
                    Mode = settings.Mode,
                    CatchmentRadius = settings.CatchmentRadius,
                    AccessRadius = settings.AccessRadius,
                };
            }

            public bool Equals(ComputeSnapshot other)
            {
                return Mode == other.Mode
                    && CatchmentRadius == other.CatchmentRadius
                    && AccessRadius == other.AccessRadius;
            }
        }

        private struct CombineSnapshot : IEquatable<CombineSnapshot>
        {
            public float W1;
            public float W2;
            public float W3;
            public float W4;
            public int HighlightShare;

            public static CombineSnapshot Capture(Setting settings)
            {
                return new CombineSnapshot
                {
                    W1 = settings.W1,
                    W2 = settings.W2,
                    W3 = settings.W3,
                    W4 = settings.W4,
                    HighlightShare = settings.HighlightShare,
                };
            }

            public bool Equals(CombineSnapshot other)
            {
                return W1 == other.W1 && W2 == other.W2 && W3 == other.W3 && W4 == other.W4
                    && HighlightShare == other.HighlightShare;
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_PopulationSystem = World.GetOrCreateSystemManaged<PopulationToGridSystem>();
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
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_StopChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_NodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_RoadEdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Road>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // WorkProvider sits on company entities (positioned via the rented
            // building) and directly on city service buildings; both carry the
            // actual workplace capacity, which is what the jobs term should count.
            m_WorkplaceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<WorkProvider>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_NodeChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Node>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_EdgeChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Road>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_WorkplaceChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<WorkProvider>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_TransformLookup = GetComponentLookup<Transform>(true);
            m_PropertyRenterLookup = GetComponentLookup<Game.Buildings.PropertyRenter>(true);

            m_ActiveInfomodeQuery = GetEntityQuery(
                ComponentType.ReadOnly<SuitabilityInfomodeData>(),
                ComponentType.ReadOnly<InfomodeActive>());

            m_PlaceableInfoviewQuery = GetEntityQuery(
                ComponentType.ReadOnly<PlaceableInfoviewItem>());

            m_PlaceableInfoviewChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PlaceableInfoviewItem>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Updated>(),
                },
            });

            m_LastStopCount = m_StopQuery.CalculateEntityCount();

            var settings = Mod.Settings;
            if (settings != null)
            {
                m_LastComputeSettings = ComputeSnapshot.Capture(settings);
                m_LastCombineSettings = CombineSnapshot.Capture(settings);
            }
        }

        protected override void OnDestroy()
        {
            DiscardPendingCompute();
            m_Intensities = null;
            m_ExpandedCache = null;
            m_RawTerms = null;
            m_Scores = null;
            m_ScoreScratch = null;
            m_CachedNodePositions = null;
            m_CachedEdgePositions = null;
            m_CachedJobPositions = null;
            m_CachedJobWorkers = null;
            m_VanillaPlaceableInfoviews = null;
            base.OnDestroy();
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
            FinishComputeIfReady();

            // The overlay is driven entirely by the vanilla infoview menu: when the
            // player activates our infoview, the game adds InfomodeActive to our
            // infomode entity, and its index selects the texture channel the terrain
            // shader colors with our gradient.
            int channel = GetActiveChannel();
            bool active = channel >= 0;

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

            if (active && channel != m_LastChannel)
            {
                ScheduleRecompute(0f);
            }
            m_LastChannel = channel;

            var computeSettings = ComputeSnapshot.Capture(settings);
            if (active && !computeSettings.Equals(m_LastComputeSettings))
            {
                ScheduleRecompute(DebounceSeconds);
            }
            m_LastComputeSettings = computeSettings;

            var combineSettings = CombineSnapshot.Capture(settings);
            if (active && !combineSettings.Equals(m_LastCombineSettings) && m_RawTerms != null)
            {
                // Weights and highlight share only affect the combine pass, so the
                // cached raw terms can be re-blended without re-running the job.
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

            // Periodic refresh so new roads, zones and residents show up without
            // needing an explicit trigger (the score inputs change as the city
            // simulates, not only when stops or settings change). Skipped while a
            // recompute is already queued so it cannot cancel a pending debounce
            // and fire on a half-dragged slider value.
            if (active && !m_JobPending && !m_RecomputeRequested && m_RawTerms != null
                && UnityEngine.Time.realtimeSinceStartup - m_LastComputeFinish >= PeriodicRefreshSeconds)
            {
                ScheduleRecompute(0f);
            }

            int2 currentSize = GetGridSize();
            if (active && !m_JobPending && (m_Intensities == null || !m_GridAtCompute.Equals(currentSize)))
            {
                ScheduleRecompute(0f);
            }

            if (active && m_RecomputeRequested && !m_JobPending
                && UnityEngine.Time.realtimeSinceStartup >= m_RecomputeAt)
            {
                if (StartCompute())
                {
                    m_RecomputeRequested = false;
                }
            }

            ApplyOverlayState(active, channel);
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

            if (UnityEngine.Time.realtimeSinceStartup - m_LastCollectionRefresh >= CollectionRefreshSeconds)
            {
                m_RoadCacheDirty = true;
                m_WorkplaceCacheDirty = true;
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

        private void EnsurePrefabs()
        {
            if (m_PrefabsAdded || m_PrefabSystem == null)
            {
                return;
            }

            // Must happen before our infoview exists, or the snapshot already
            // contains the auto-activation entries we are trying to undo.
            SnapshotVanillaPlaceableInfoviews();

            m_InfomodePrefab = PrefabBase.Create<SuitabilityInfomodePrefab>("StationSuitabilityOverlay");
            SetField(m_InfomodePrefab, "m_Priority", InfomodePriority);
            SetField(m_InfomodePrefab, "editor", false);
            SetField(m_InfomodePrefab, "m_Low", LowColor);
            SetField(m_InfomodePrefab, "m_Medium", MediumColor);
            SetField(m_InfomodePrefab, "m_High", HighColor);
            SetField(m_InfomodePrefab, "m_Steps", 16);
            SetField(m_InfomodePrefab, "m_LegendType", GradientLegendType.Gradient);
            SetField(m_InfomodePrefab, "m_LowLabelId", "StationSuitabilityOverlay.Legend.Low");
            SetField(m_InfomodePrefab, "m_MediumLabelId", "StationSuitabilityOverlay.Legend.Medium");
            SetField(m_InfomodePrefab, "m_HighLabelId", "StationSuitabilityOverlay.Legend.High");

            if (!m_PrefabSystem.AddPrefab(m_InfomodePrefab, null, null, null))
            {
                Mod.Log.Warn("Failed to register infomode prefab.");
            }

            var info = new InfomodeInfo();
            SetField(info, "m_Mode", m_InfomodePrefab);
            SetField(info, "m_Priority", InfomodePriority);
            SetField(info, "m_Supplemental", false);
            SetField(info, "m_Optional", false);

            m_InfoviewPrefab = PrefabBase.Create<InfoviewPrefab>("StationSuitabilityOverlay");
            SetField(m_InfoviewPrefab, "m_Infomodes", new[] { info });
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
            Mod.Log.Info("Suitability infomode and infoview prefabs registered.");
        }

        // ToolSystem.GetInfomodes reads the infoview ENTITY's InfoviewMode buffer, not
        // the managed prefab. For runtime-registered prefabs that buffer can end up
        // missing or empty, which leaves the infoview panel without rows and prevents
        // infomode activation entirely — so verify it and repair it if needed.
        private void EnsureInfoviewLinked()
        {
            if (m_InfoviewLinkChecked || m_InfoviewPrefab == null || m_InfomodePrefab == null)
            {
                return;
            }

            if (!m_PrefabSystem.TryGetEntity(m_InfoviewPrefab, out Entity infoviewEntity) ||
                !m_PrefabSystem.TryGetEntity(m_InfomodePrefab, out Entity infomodeEntity))
            {
                // Prefab entities can materialize a frame after AddPrefab; retry
                // next update instead of latching the checked flag.
                if (!m_InfoviewLinkWaitLogged)
                {
                    Mod.Log.Info("Infoview or infomode prefab entity not found yet; retrying.");
                    m_InfoviewLinkWaitLogged = true;
                }
                return;
            }

            bool hadBuffer = EntityManager.HasComponent<InfoviewMode>(infoviewEntity);
            DynamicBuffer<InfoviewMode> buffer = hadBuffer
                ? EntityManager.GetBuffer<InfoviewMode>(infoviewEntity)
                : EntityManager.AddBuffer<InfoviewMode>(infoviewEntity);

            bool linked = false;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_Mode == infomodeEntity)
                {
                    linked = true;
                    break;
                }
            }

            if (!linked)
            {
                buffer.Add(new InfoviewMode(infomodeEntity, InfomodePriority, supplemental: false, optional: false));
                m_ToolSystem.EventInfomodesChanged?.Invoke();
            }

            Mod.Log.Info($"Infoview link check: buffer existed={hadBuffer}, was linked={linked}, entries now={buffer.Length}");
            m_InfoviewLinkChecked = true;
        }

        // InfoviewInitializeSystem scores every registered infoview against every
        // placeable prefab to fill its PlaceableInfoviewItem buffer, which
        // ToolBaseSystem uses to auto-activate an infoview when the player selects
        // that asset in a build menu. Our infomode carries none of the vanilla
        // match data, so our infoview always scores a neutral 0 — which beats any
        // asset whose vanilla infomodes all score negative, making the overlay pop
        // up for seemingly random build-menu selections. Strip our entries so the
        // overlay is only ever activated deliberately through the infoview menu.
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

        private void SweepPlaceableInfoviews()
        {
            if (!m_InfoviewLinkChecked)
            {
                return;
            }

            // A full re-verify also runs periodically and whenever the placeable
            // set changes size: if the vanilla initializer ever refills these
            // buffers outside a Created/Updated frame, reacting to those tags
            // alone would let the auto-activation bug return unnoticed.
            int placeableCount = m_PlaceableInfoviewQuery.CalculateEntityCount();
            float now = UnityEngine.Time.realtimeSinceStartup;
            bool full = !m_PlaceableSweepDone
                || placeableCount != m_LastPlaceableCount
                || now - m_LastPlaceableSweep >= PlaceableReverifySeconds;

            if (!full && m_PlaceableInfoviewChangedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            if (!m_PrefabSystem.TryGetEntity(m_InfoviewPrefab, out Entity infoviewEntity) ||
                !m_PrefabSystem.TryGetEntity(m_InfomodePrefab, out Entity infomodeEntity))
            {
                return;
            }

            EntityQuery query = full ? m_PlaceableInfoviewQuery : m_PlaceableInfoviewChangedQuery;
            int restored = StripPlaceableInfoviewItems(query, infoviewEntity, infomodeEntity, out int cleared);
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

        private int StripPlaceableInfoviewItems(EntityQuery query, Entity infoviewEntity, Entity infomodeEntity, out int cleared)
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

                // Item 0 is the infoview the tool auto-activates; any further
                // entries are supplemental infomodes. When ours took the top slot
                // it displaced a vanilla choice that the buffer never recorded, so
                // put back the pre-mod snapshot when we have one and fall back to
                // disabling auto-activation for that asset when we do not.
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
                    if (buffer[j].m_Item == infoviewEntity || buffer[j].m_Item == infomodeEntity)
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

        private int GetActiveChannel()
        {
            if (m_ActiveInfomodeQuery.IsEmptyIgnoreFilter)
            {
                return -1;
            }

            using var actives = m_ActiveInfomodeQuery.ToComponentDataArray<InfomodeActive>(Allocator.Temp);
            if (actives.Length == 0)
            {
                return -1;
            }

            InfomodeActive active = actives[0];
            if (active.m_Index != m_LastLoggedIndex)
            {
                Mod.Log.Info($"Infomode active: index={active.m_Index}, secondaryIndex={active.m_SecondaryIndex}, priority={active.m_Priority}");
                m_LastLoggedIndex = active.m_Index;
            }

            // The terrain overlay has four channels; vanilla heatmap jobs write to
            // (m_Index - 1). Our prefab uses color group 0, so the index is 1..4.
            int channel = active.m_Index - 1;
            if (channel < 0 || channel > 3)
            {
                Mod.Log.Warn($"Unexpected infomode index {active.m_Index}; overlay disabled this frame.");
                return -1;
            }

            return channel;
        }

        // Feed our intensities through OverlayInfomodeSystem's own terrain texture —
        // the exact path the vanilla heatmaps use. GetTerrainTextureData (private)
        // resizes the texture, assigns it to TerrainRenderSystem.overrideOverlaymap
        // and schedules a clear job; ApplyOverlay completes that job, then we copy
        // our data in and re-upload. Runs every frame while active because the
        // vanilla system clears the override at the start of each frame.
        private void ApplyOverlayState(bool active, int channel)
        {
            bool applied = false;
            if (active && m_Intensities != null && m_OverlayInfomodeSystem != null)
            {
                ExpandIntensities(channel);
                applied = InjectOverlay();
            }

            if (applied != m_LastOverlayApplied)
            {
                Mod.Log.Info($"Overlay map {(applied ? "attached" : "detached")} (active={active}, data={(m_Intensities != null ? "yes" : "no")})");
                m_LastOverlayApplied = applied;
            }
        }

        private bool InjectOverlay()
        {
            if (s_GetTerrainTextureData == null)
            {
                s_GetTerrainTextureData = typeof(OverlayInfomodeSystem).GetMethod(
                    "GetTerrainTextureData",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int2) },
                    null);
                s_TerrainTextureField = typeof(OverlayInfomodeSystem).GetField(
                    "m_TerrainTexture",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (s_GetTerrainTextureData == null || s_TerrainTextureField == null)
                {
                    Mod.Log.Warn("OverlayInfomodeSystem internals not found; overlay cannot render.");
                    return false;
                }
            }

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

        private void ExpandIntensities(int channel)
        {
            int cells = m_IntensityGrid.x * m_IntensityGrid.y;
            if (m_ExpandedCache == null || m_ExpandedCache.Length != cells * 4)
            {
                m_ExpandedCache = new byte[cells * 4];
                m_ExpandedChannel = -1;
            }

            if (m_ExpandedChannel == channel)
            {
                return;
            }

            Array.Clear(m_ExpandedCache, 0, m_ExpandedCache.Length);
            for (int i = 0; i < cells; i++)
            {
                m_ExpandedCache[i * 4 + channel] = m_Intensities[i];
            }

            m_ExpandedChannel = channel;
        }

        private int2 GetGridSize()
        {
            // Change-detection only: the compute grid itself is derived from the
            // cell-map extents in StartCompute (see m_GridAtCompute).
            float2 playable = m_TerrainSystem != null ? m_TerrainSystem.playableArea : new float2(0f, 0f);
            return GridDims(playable, TileSize);
        }

        private static int2 GridDims(float2 size, float cellSize)
        {
            return new int2(
                math.max(1, (int)math.ceil(size.x / cellSize)),
                math.max(1, (int)math.ceil(size.y / cellSize)));
        }

        // Schedules the suitability job without blocking; FinishComputeIfReady
        // consumes the result on a later frame. Input arrays are released
        // automatically once the job has run.
        private bool StartCompute()
        {
            if (m_JobPending || m_PopulationSystem == null || m_TerrainSystem == null)
            {
                return false;
            }

            var settings = Mod.Settings;
            Dependency.Complete();

            CellMapData<PopulationCell> popData = m_PopulationSystem.GetData(true, out JobHandle popDeps);

            if (popData.m_CellSize.x <= 0f || popData.m_CellSize.y <= 0f ||
                popData.m_TextureSize.x <= 0 || popData.m_TextureSize.y <= 0)
            {
                return false;
            }

            // The score grid and texture both span the playable area, matching the
            // extents the vanilla heatmap cell maps use for this overlay channel.
            float2 mapSize = popData.m_CellSize * new float2(popData.m_TextureSize.x, popData.m_TextureSize.y);
            if (mapSize.x <= 0f || mapSize.y <= 0f)
            {
                return false;
            }

            float2 worldMin = -mapSize * 0.5f;
            int2 gridSize = GridDims(mapSize, TileSize);
            int totalCells = gridSize.x * gridSize.y;

            List<float2> stopPositions = CollectStopPositions(settings.Mode);
            EnsureCollectionsCurrent();
            List<float2> nodePositions = m_CachedNodePositions;
            List<float2> edgePositions = m_CachedEdgePositions;
            List<float2> jobPositions = m_CachedJobPositions;
            List<float> jobWorkers = m_CachedJobWorkers;

            int2 bucketGrid = GridDims(mapSize, BucketSize);

            NativeArray<float2> stopPositionsNative = BuildBuckets(stopPositions, bucketGrid, worldMin, BucketSize, out NativeArray<int> stopOffsets, out NativeArray<int> stopCounts);
            NativeArray<float2> nodePositionsNative = BuildBuckets(nodePositions, bucketGrid, worldMin, BucketSize, out NativeArray<int> nodeOffsets, out NativeArray<int> nodeCounts);
            NativeArray<float2> edgePositionsNative = BuildBuckets(edgePositions, bucketGrid, worldMin, BucketSize, out NativeArray<int> edgeOffsets, out NativeArray<int> edgeCounts);
            NativeArray<float2> jobPositionsNative = BuildWeightedBuckets(jobPositions, jobWorkers, bucketGrid, worldMin, BucketSize, out NativeArray<int> jobOffsets, out NativeArray<int> jobCounts, out NativeArray<float> jobWeightsNative);
            var terms = new NativeArray<float4>(totalCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            var job = new SuitabilityJob
            {
                GridSize = gridSize,
                BucketGridSize = bucketGrid,
                WorldMin = worldMin,
                TileSize = TileSize,
                BucketSize = BucketSize,
                CatchmentRadius = settings.CatchmentRadius,
                AccessRadius = settings.AccessRadius,
                PopulationMap = popData.m_Buffer,
                PopulationCellSize = popData.m_CellSize,
                PopulationTextureSize = popData.m_TextureSize,
                StopPositions = stopPositionsNative,
                StopBucketOffsets = stopOffsets,
                StopBucketCounts = stopCounts,
                NodePositions = nodePositionsNative,
                NodeBucketOffsets = nodeOffsets,
                NodeBucketCounts = nodeCounts,
                EdgePositions = edgePositionsNative,
                EdgeBucketOffsets = edgeOffsets,
                EdgeBucketCounts = edgeCounts,
                JobPositions = jobPositionsNative,
                JobWeights = jobWeightsNative,
                JobBucketOffsets = jobOffsets,
                JobBucketCounts = jobCounts,
                Terms = terms,
            };

            JobHandle handle = job.Schedule(totalCells, 64, popDeps);
            m_PopulationSystem.AddReader(handle);

            stopPositionsNative.Dispose(handle);
            stopOffsets.Dispose(handle);
            stopCounts.Dispose(handle);
            nodePositionsNative.Dispose(handle);
            nodeOffsets.Dispose(handle);
            nodeCounts.Dispose(handle);
            edgePositionsNative.Dispose(handle);
            edgeOffsets.Dispose(handle);
            edgeCounts.Dispose(handle);
            jobPositionsNative.Dispose(handle);
            jobWeightsNative.Dispose(handle);
            jobOffsets.Dispose(handle);
            jobCounts.Dispose(handle);

            m_PendingHandle = handle;
            m_PendingTerms = terms;
            m_PendingGrid = gridSize;
            m_PendingStopCount = stopPositions.Count;
            m_PendingJobSiteCount = jobPositions.Count;
            m_PendingNodeCount = nodePositions.Count;
            m_PendingEdgeCount = edgePositions.Count;
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
            // Stamped on completion, not on schedule, so the refresh interval is a
            // real idle cooldown even if a compute cycle outlasts it.
            m_LastComputeFinish = UnityEngine.Time.realtimeSinceStartup;

            int totalCells = m_PendingTerms.Length;
            if (m_RawTerms == null || m_RawTerms.Length != totalCells)
            {
                m_RawTerms = new float4[totalCells];
            }
            m_PendingTerms.CopyTo(m_RawTerms);
            m_PendingTerms.Dispose();

            m_IntensityGrid = m_PendingGrid;
            RecombineAndNormalize();
            Mod.Log.Info($"Overlay computed: grid {m_PendingGrid.x}x{m_PendingGrid.y}, stops={m_PendingStopCount}, jobSites={m_PendingJobSiteCount}, roads(nodes/edges)={m_PendingNodeCount}/{m_PendingEdgeCount}");
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

        // Blends the cached raw terms into weighted scores and turns those into
        // 0..255 intensities. Runs after every compute and again whenever only
        // weights or the highlight share change. The vanilla terrain shader colors
        // the overlay channel assigned to our infomode with its gradient (see
        // ApplyOverlayState).
        private void RecombineAndNormalize()
        {
            var settings = Mod.Settings;
            int totalCells = m_RawTerms.Length;
            if (m_Intensities == null || m_Intensities.Length != totalCells)
            {
                m_Intensities = new byte[totalCells];
            }
            if (m_Scores == null || m_Scores.Length != totalCells)
            {
                m_Scores = new float[totalCells];
            }
            if (m_ScoreScratch == null || m_ScoreScratch.Length != totalCells)
            {
                m_ScoreScratch = new float[totalCells];
            }

            m_ExpandedChannel = -1;

            // Demand and jobs are raw sums; normalize each against a high
            // percentile of its own positive values so all four terms are 0..1 and
            // the W1..W4 weights are actually comparable. Without this, population
            // counts (hundreds to thousands) drown the bounded penalty and access
            // terms completely.
            float demandCap = PositivePercentile(0, TermCapPercentile);
            float jobsCap = PositivePercentile(1, TermCapPercentile);
            float invDemandCap = demandCap > 0f ? 1f / demandCap : 0f;
            float invJobsCap = jobsCap > 0f ? 1f / jobsCap : 0f;

            for (int i = 0; i < totalCells; i++)
            {
                float4 t = m_RawTerms[i];
                float demand = math.saturate(t.x * invDemandCap);
                float jobs = math.saturate(t.y * invJobsCap);
                float penalty = t.z / MaxPenalty;
                float access = t.w;
                float score = (settings.W1 * demand) + (settings.W2 * jobs) + (settings.W4 * access) - (settings.W3 * penalty);
                m_Scores[i] = score * math.saturate(access * RoadGateScale);
            }

            // The gradient cap is a percentile of the POSITIVE scores only. Most of
            // the map is empty land at exactly 0 and coverage can push covered cells
            // below 0, so a percentile over all cells would collapse onto ~0 and
            // saturate the whole map (the "everything turns red" failure mode).
            int positiveCount = 0;
            for (int i = 0; i < totalCells; i++)
            {
                if (m_Scores[i] > 0f)
                {
                    m_ScoreScratch[positiveCount++] = m_Scores[i];
                }
            }

            if (positiveCount == 0)
            {
                Array.Clear(m_Intensities, 0, totalCells);
                return;
            }

            float highlightShare = settings.HighlightShare / 100f;
            int capIndex = ClampInt((int)math.floor(positiveCount * (1f - highlightShare)), 0, positiveCount - 1);
            float cap = SelectKth(m_ScoreScratch, positiveCount, capIndex);
            float invCap = 1f / math.max(cap, 1e-5f);

            for (int i = 0; i < totalCells; i++)
            {
                float t = math.saturate(m_Scores[i] * invCap);
                m_Intensities[i] = (byte)math.round(math.pow(t, IntensityGamma) * 255f);
            }
        }

        // Percentile over the positive values of one raw-term component
        // (0 = demand, 1 = jobs). Returns 0 when the term is empty everywhere.
        private float PositivePercentile(int component, float percentile)
        {
            int count = 0;
            for (int i = 0; i < m_RawTerms.Length; i++)
            {
                float v = component == 0 ? m_RawTerms[i].x : m_RawTerms[i].y;
                if (v > 0f)
                {
                    m_ScoreScratch[count++] = v;
                }
            }

            if (count == 0)
            {
                return 0f;
            }

            int k = ClampInt((int)math.floor(count * percentile), 0, count - 1);
            return SelectKth(m_ScoreScratch, count, k);
        }

        private static int ClampInt(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        // Hoare-partition quickselect: returns the k-th smallest element. O(n)
        // average instead of the O(n log n) full sort a percentile doesn't need.
        // Reorders the array in place.
        private static float SelectKth(float[] values, int length, int k)
        {
            int lo = 0;
            int hi = length - 1;
            while (lo < hi)
            {
                float pivot = values[(lo + hi) >> 1];
                int i = lo;
                int j = hi;
                while (i <= j)
                {
                    while (values[i] < pivot) i++;
                    while (values[j] > pivot) j--;
                    if (i <= j)
                    {
                        float tmp = values[i];
                        values[i] = values[j];
                        values[j] = tmp;
                        i++;
                        j--;
                    }
                }

                if (k <= j)
                {
                    hi = j;
                }
                else if (k >= i)
                {
                    lo = i;
                }
                else
                {
                    return values[k];
                }
            }

            return values[k];
        }

        private List<float2> CollectStopPositions(Setting.ModePreset mode)
        {
            var result = new List<float2>();
            using var entities = m_StopQuery.ToEntityArray(Allocator.Temp);
            using var transforms = m_StopQuery.ToComponentDataArray<Transform>(Allocator.Temp);
            using var prefabs = m_StopQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                Entity prefab = prefabs[i].m_Prefab;
                if (!IsStopOfMode(prefab, mode))
                {
                    continue;
                }

                float3 pos = transforms[i].m_Position;
                result.Add(new float2(pos.x, pos.z));
            }

            return result;
        }

        private bool IsStopOfMode(Entity prefab, Setting.ModePreset mode)
        {
            PrefabBase prefabBase = m_PrefabSystem.GetPrefab<PrefabBase>(prefab);
            if (prefabBase != null && m_PrefabSystem.TryGetComponentData(prefabBase, out TransportStopData stopData))
            {
                if (!stopData.m_PassengerTransport)
                {
                    return false;
                }

                return mode == Setting.ModePreset.Bus
                    ? stopData.m_TransportType == TransportType.Bus
                    : stopData.m_TransportType == TransportType.Subway;
            }

            string name = prefabBase != null ? prefabBase.name : null;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string lower = name.ToLowerInvariant();
            if (mode == Setting.ModePreset.Bus)
            {
                return lower.Contains("bus");
            }

            return lower.Contains("metro") || lower.Contains("subway");
        }

        // Rebuilds the cached road and workplace collections only when change
        // detection says they moved. Both walk large parts of the entity world on
        // the main thread, and the periodic overlay refresh would otherwise pay for
        // them every ten seconds for as long as the infoview stays open.
        private void EnsureCollectionsCurrent()
        {
            if (m_RoadCacheDirty || m_CachedNodePositions == null)
            {
                CollectRoadNetwork(out m_CachedNodePositions, out m_CachedEdgePositions);
                m_RoadCacheDirty = false;
            }

            if (m_WorkplaceCacheDirty || m_CachedJobPositions == null)
            {
                CollectWorkplaces(out m_CachedJobPositions, out m_CachedJobWorkers);
                m_WorkplaceCacheDirty = false;
            }

            m_LastCollectionRefresh = UnityEngine.Time.realtimeSinceStartup;
        }

        // Accessibility only considers the road network: pipes, power lines and rail
        // would otherwise inflate the score in places pedestrians cannot reach.
        // The node map spans every net node because road edges reference their
        // endpoints by entity; only road endpoints end up in nodePositions.
        private void CollectRoadNetwork(out List<float2> nodePositions, out List<float2> edgePositions)
        {
            using var nodeEntities = m_NodeQuery.ToEntityArray(Allocator.Temp);
            using var nodeData = m_NodeQuery.ToComponentDataArray<Node>(Allocator.Temp);
            var nodeMap = new Dictionary<Entity, float3>(nodeEntities.Length);
            for (int i = 0; i < nodeEntities.Length; i++)
            {
                nodeMap[nodeEntities[i]] = nodeData[i].m_Position;
            }

            using var edges = m_RoadEdgeQuery.ToComponentDataArray<Edge>(Allocator.Temp);
            edgePositions = new List<float2>(edges.Length);
            var roadNodes = new HashSet<Entity>();

            for (int i = 0; i < edges.Length; i++)
            {
                Edge edge = edges[i];
                if (!nodeMap.TryGetValue(edge.m_Start, out float3 start) || !nodeMap.TryGetValue(edge.m_End, out float3 end))
                {
                    continue;
                }

                float3 mid = (start + end) * 0.5f;
                edgePositions.Add(new float2(mid.x, mid.z));
                roadNodes.Add(edge.m_Start);
                roadNodes.Add(edge.m_End);
            }

            nodePositions = new List<float2>(roadNodes.Count);
            foreach (Entity node in roadNodes)
            {
                float3 pos = nodeMap[node];
                nodePositions.Add(new float2(pos.x, pos.z));
            }
        }

        // Actual workplace capacity, positioned at the building. Companies rent a
        // building (PropertyRenter), city service buildings carry WorkProvider and
        // a Transform themselves. The vanilla availability cell map is NOT usable
        // here: its "workplaces" channel is a road-network reachability ratio, not
        // a job count, which is why office and industry areas never registered.
        private void CollectWorkplaces(out List<float2> positions, out List<float> workers)
        {
            positions = new List<float2>();
            workers = new List<float>();
            using var entities = m_WorkplaceQuery.ToEntityArray(Allocator.Temp);
            using var providers = m_WorkplaceQuery.ToComponentDataArray<WorkProvider>(Allocator.Temp);

            m_TransformLookup.Update(this);
            m_PropertyRenterLookup.Update(this);

            for (int i = 0; i < entities.Length; i++)
            {
                int maxWorkers = providers[i].m_MaxWorkers;
                if (maxWorkers <= 0)
                {
                    continue;
                }

                Entity entity = entities[i];
                float3 pos;
                if (m_TransformLookup.HasComponent(entity))
                {
                    pos = m_TransformLookup[entity].m_Position;
                }
                else if (m_PropertyRenterLookup.HasComponent(entity))
                {
                    Entity property = m_PropertyRenterLookup[entity].m_Property;
                    if (property == Entity.Null || !m_TransformLookup.HasComponent(property))
                    {
                        continue;
                    }

                    pos = m_TransformLookup[property].m_Position;
                }
                else
                {
                    continue;
                }

                positions.Add(new float2(pos.x, pos.z));
                workers.Add(maxWorkers);
            }
        }

        private static NativeArray<float2> BuildBuckets(
            List<float2> positions,
            int2 gridSize,
            float2 worldMin,
            float tileSize,
            out NativeArray<int> offsets,
            out NativeArray<int> counts)
        {
            NativeArray<float2> result = BuildWeightedBuckets(positions, null, gridSize, worldMin, tileSize, out offsets, out counts, out NativeArray<float> weights);
            if (weights.IsCreated)
            {
                weights.Dispose();
            }

            return result;
        }

        private static NativeArray<float2> BuildWeightedBuckets(
            List<float2> positions,
            List<float> weights,
            int2 gridSize,
            float2 worldMin,
            float tileSize,
            out NativeArray<int> offsets,
            out NativeArray<int> counts,
            out NativeArray<float> weightsOut)
        {
            int bucketCount = gridSize.x * gridSize.y;
            counts = new NativeArray<int>(bucketCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            for (int i = 0; i < positions.Count; i++)
            {
                int2 cell = WorldToCell(positions[i], worldMin, tileSize, gridSize);
                int index = cell.x + cell.y * gridSize.x;
                counts[index] += 1;
            }

            offsets = new NativeArray<int>(bucketCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            int running = 0;
            for (int i = 0; i < bucketCount; i++)
            {
                offsets[i] = running;
                running += counts[i];
            }

            var result = new NativeArray<float2>(positions.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            // Only the weighted callers need this array; the unweighted ones would
            // otherwise pay a Persistent allocation per compute just to free it.
            weightsOut = weights != null
                ? new NativeArray<float>(positions.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory)
                : default;
            var write = new NativeArray<int>(bucketCount, Allocator.Temp);
            NativeArray<int>.Copy(offsets, write);

            for (int i = 0; i < positions.Count; i++)
            {
                int2 cell = WorldToCell(positions[i], worldMin, tileSize, gridSize);
                int index = cell.x + cell.y * gridSize.x;
                int writeIndex = write[index]++;
                result[writeIndex] = positions[i];
                if (weights != null)
                {
                    weightsOut[writeIndex] = weights[i];
                }
            }

            write.Dispose();
            return result;
        }

        private static int2 WorldToCell(float2 pos, float2 worldMin, float tileSize, int2 gridSize)
        {
            float2 rel = (pos - worldMin) / tileSize;
            int2 cell = new int2((int)math.floor(rel.x), (int)math.floor(rel.y));
            cell.x = math.clamp(cell.x, 0, gridSize.x - 1);
            cell.y = math.clamp(cell.y, 0, gridSize.y - 1);
            return cell;
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

        // Emits the four raw score terms per cell; the managed combine pass
        // normalizes and weights them (see RecombineAndNormalize), so weight
        // changes never need to re-run this job.
        [BurstCompile]
        private struct SuitabilityJob : IJobParallelFor
        {
            // Per-feature access contributions; tuned so a normal street grid
            // saturates the access term at the reference radius.
            private const float EdgeAccessCoefficient = 0.06f;
            private const float NodeAccessCoefficient = 0.15f;
            // Radius the access coefficients were tuned at. Scaling by
            // (reference/actual)^2 keeps the saturation density constant, so the
            // configurable radius changes which roads count, not how quickly the
            // score maxes out.
            private const float ReferenceAccessRadius = 120f;

            public int2 GridSize;
            public int2 BucketGridSize;
            public float2 WorldMin;
            public float TileSize;
            public float BucketSize;
            public float CatchmentRadius;
            public float AccessRadius;

            [ReadOnly] public NativeArray<PopulationCell> PopulationMap;
            public float2 PopulationCellSize;
            public int2 PopulationTextureSize;

            [ReadOnly] public NativeArray<float2> StopPositions;
            [ReadOnly] public NativeArray<int> StopBucketOffsets;
            [ReadOnly] public NativeArray<int> StopBucketCounts;

            [ReadOnly] public NativeArray<float2> NodePositions;
            [ReadOnly] public NativeArray<int> NodeBucketOffsets;
            [ReadOnly] public NativeArray<int> NodeBucketCounts;

            [ReadOnly] public NativeArray<float2> EdgePositions;
            [ReadOnly] public NativeArray<int> EdgeBucketOffsets;
            [ReadOnly] public NativeArray<int> EdgeBucketCounts;

            [ReadOnly] public NativeArray<float2> JobPositions;
            [ReadOnly] public NativeArray<float> JobWeights;
            [ReadOnly] public NativeArray<int> JobBucketOffsets;
            [ReadOnly] public NativeArray<int> JobBucketCounts;

            // x = demand (population sum), y = jobs (workplace sum),
            // z = coverage penalty (0..MaxPenalty), w = access (0..1).
            public NativeArray<float4> Terms;

            public void Execute(int index)
            {
                int x = index % GridSize.x;
                int y = index / GridSize.x;
                float2 center = WorldMin + new float2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);

                float demand = SumPopulationCells(center, CatchmentRadius);
                float jobs = SumWeightedPoints(JobPositions, JobWeights, JobBucketOffsets, JobBucketCounts, center, CatchmentRadius);
                float penalty = ComputePenalty(center);
                float access = ComputeAccessibility(center);

                Terms[index] = new float4(demand, jobs, penalty, access);
            }

            // Triangular kernel: contributions fade linearly with distance, so the
            // coarse source cells cannot imprint rectangular plateaus. Callers must
            // ensure dist <= radius.
            private static float TriangularWeight(float dist, float radius)
            {
                return 1f - dist / radius;
            }

            private float SumPopulationCells(float2 center, float radius)
            {
                float2 mapSize = PopulationCellSize * new float2(PopulationTextureSize.x, PopulationTextureSize.y);
                float2 mapMin = -mapSize * 0.5f;
                float2 minPos = center - new float2(radius, radius);
                float2 maxPos = center + new float2(radius, radius);
                int2 minCell = WorldToCellInternal(minPos, mapMin, PopulationCellSize, PopulationTextureSize);
                int2 maxCell = WorldToCellInternal(maxPos, mapMin, PopulationCellSize, PopulationTextureSize);

                float sum = 0f;
                for (int cy = minCell.y; cy <= maxCell.y; cy++)
                {
                    for (int cx = minCell.x; cx <= maxCell.x; cx++)
                    {
                        int idx = cx + cy * PopulationTextureSize.x;
                        float2 cellCenter = mapMin + new float2((cx + 0.5f) * PopulationCellSize.x, (cy + 0.5f) * PopulationCellSize.y);
                        float dist = math.distance(cellCenter, center);
                        if (dist <= radius)
                        {
                            sum += PopulationMap[idx].m_Population * TriangularWeight(dist, radius);
                        }
                    }
                }

                return sum;
            }

            private float SumWeightedPoints(
                NativeArray<float2> positions,
                NativeArray<float> weights,
                NativeArray<int> bucketOffsets,
                NativeArray<int> bucketCounts,
                float2 center,
                float radius)
            {
                int radiusTiles = (int)math.ceil(radius / BucketSize);
                int2 baseCell = WorldToCell(center, WorldMin, BucketSize, BucketGridSize);
                float sum = 0f;

                for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
                {
                    int cy = baseCell.y + dy;
                    if (cy < 0 || cy >= BucketGridSize.y)
                    {
                        continue;
                    }

                    for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                    {
                        int cx = baseCell.x + dx;
                        if (cx < 0 || cx >= BucketGridSize.x)
                        {
                            continue;
                        }

                        int bucket = cx + cy * BucketGridSize.x;
                        int count = bucketCounts[bucket];
                        int start = bucketOffsets[bucket];

                        for (int i = 0; i < count; i++)
                        {
                            float dist = math.distance(positions[start + i], center);
                            if (dist <= radius)
                            {
                                sum += weights[start + i] * TriangularWeight(dist, radius);
                            }
                        }
                    }
                }

                return sum;
            }

            private float ComputePenalty(float2 center)
            {
                int radiusTiles = (int)math.ceil(CatchmentRadius / BucketSize);
                int2 baseCell = WorldToCell(center, WorldMin, BucketSize, BucketGridSize);
                float penalty = 0f;

                for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
                {
                    int cy = baseCell.y + dy;
                    if (cy < 0 || cy >= BucketGridSize.y)
                    {
                        continue;
                    }

                    for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                    {
                        int cx = baseCell.x + dx;
                        if (cx < 0 || cx >= BucketGridSize.x)
                        {
                            continue;
                        }

                        int bucket = cx + cy * BucketGridSize.x;
                        int count = StopBucketCounts[bucket];
                        int start = StopBucketOffsets[bucket];

                        for (int i = 0; i < count; i++)
                        {
                            float2 pos = StopPositions[start + i];
                            float dist = math.distance(pos, center);
                            if (dist > CatchmentRadius)
                            {
                                continue;
                            }

                            penalty += TriangularWeight(dist, CatchmentRadius);
                        }
                    }
                }

                return math.clamp(penalty, 0f, MaxPenalty);
            }

            private float ComputeAccessibility(float2 center)
            {
                int radiusTiles = (int)math.ceil(AccessRadius / BucketSize);
                int2 baseCell = WorldToCell(center, WorldMin, BucketSize, BucketGridSize);
                float nodeWeight = 0f;
                float edgeWeight = 0f;

                for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
                {
                    int cy = baseCell.y + dy;
                    if (cy < 0 || cy >= BucketGridSize.y)
                    {
                        continue;
                    }

                    for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                    {
                        int cx = baseCell.x + dx;
                        if (cx < 0 || cx >= BucketGridSize.x)
                        {
                            continue;
                        }

                        int bucket = cx + cy * BucketGridSize.x;
                        int nodeCount = NodeBucketCounts[bucket];
                        int nodeStart = NodeBucketOffsets[bucket];
                        for (int i = 0; i < nodeCount; i++)
                        {
                            float2 pos = NodePositions[nodeStart + i];
                            float nodeDist = math.distance(pos, center);
                            if (nodeDist <= AccessRadius)
                            {
                                nodeWeight += TriangularWeight(nodeDist, AccessRadius);
                            }
                        }

                        int edgeCount = EdgeBucketCounts[bucket];
                        int edgeStart = EdgeBucketOffsets[bucket];
                        for (int i = 0; i < edgeCount; i++)
                        {
                            float2 pos = EdgePositions[edgeStart + i];
                            float edgeDist = math.distance(pos, center);
                            if (edgeDist <= AccessRadius)
                            {
                                edgeWeight += TriangularWeight(edgeDist, AccessRadius);
                            }
                        }
                    }
                }

                float radiusScale = (ReferenceAccessRadius * ReferenceAccessRadius) / (AccessRadius * AccessRadius);
                float access = ((edgeWeight * EdgeAccessCoefficient) + (nodeWeight * NodeAccessCoefficient)) * radiusScale;
                return math.saturate(access);
            }

            private static int2 WorldToCellInternal(float2 pos, float2 mapMin, float2 cellSize, int2 textureSize)
            {
                float2 rel = (pos - mapMin) / cellSize;
                int2 cell = new int2((int)math.floor(rel.x), (int)math.floor(rel.y));
                cell.x = math.clamp(cell.x, 0, textureSize.x - 1);
                cell.y = math.clamp(cell.y, 0, textureSize.y - 1);
                return cell;
            }
        }
    }
}
