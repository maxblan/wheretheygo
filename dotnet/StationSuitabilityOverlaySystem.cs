using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
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
        private const float MaxPenalty = 1.5f;
        private const int InfomodePriority = 200;
        // Gamma < 1 lifts the mid-range of the normalized scores for readability.
        private const float IntensityGamma = 0.85f;

        // Alpha shapes the overlay's opacity ramp: low scores barely tint the map,
        // hotspots are nearly opaque. These same fields feed the infoview panel's
        // gradient legend — if the legend ever renders its low end too faint,
        // raise LowColor's alpha rather than restructuring the ramp.
        private static readonly Color LowColor = new Color(0.12f, 0.46f, 0.18f, 0.2f);
        private static readonly Color MediumColor = new Color(0.94f, 0.84f, 0.25f, 0.65f);
        private static readonly Color HighColor = new Color(0.85f, 0.22f, 0.12f, 0.95f);

        private TerrainSystem m_TerrainSystem;
        private PopulationToGridSystem m_PopulationSystem;
        private AvailabilityInfoToGridSystem m_AvailabilitySystem;
        private PrefabSystem m_PrefabSystem;
        private ToolSystem m_ToolSystem;
        private EntityQuery m_StopQuery;
        private EntityQuery m_StopChangedQuery;
        private EntityQuery m_NodeQuery;
        private EntityQuery m_RoadEdgeQuery;
        private EntityQuery m_ActiveInfomodeQuery;
        private int m_LastStopCount;

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
        private SettingsSnapshot m_LastComputeSettings;
        private int m_LastHighlightShare;

        private bool m_RecomputeRequested;
        private float m_RecomputeAt;
        private bool m_PrefabsAdded;
        private bool m_InfoviewLinkChecked;
        private bool m_InfoviewLinkWaitLogged;
        private bool m_LastOverlayApplied;

        // In-flight suitability job; resolved in FinishComputeIfReady so a large
        // recompute never blocks the frame it was scheduled on.
        private bool m_JobPending;
        private JobHandle m_PendingHandle;
        private NativeArray<float> m_PendingScores;
        private int2 m_PendingGrid;
        private int m_PendingStopCount;
        private int m_PendingNodeCount;
        private int m_PendingEdgeCount;
        // Playable-area grid observed when the last compute was scheduled. The
        // resize trigger compares against this snapshot (not m_IntensityGrid, which
        // is derived from the cell-map extents) so a mismatch between the two
        // derivations cannot cause a recompute-every-frame loop.
        private int2 m_GridAtCompute;
        // Raw scores from the last compute; HighlightShare changes re-run only the
        // normalization over these instead of the whole job.
        private float[] m_RawScores;
        private float[] m_ScoreScratch;

        // Settings that change the computed scores. HighlightShare is tracked
        // separately because it only affects the post-job normalization pass.
        private struct SettingsSnapshot : IEquatable<SettingsSnapshot>
        {
            public Setting.ModePreset Mode;
            public float W1;
            public float W2;
            public float W3;
            public float W4;
            public int CatchmentRadius;
            public int AccessRadius;

            public static SettingsSnapshot Capture(Setting settings)
            {
                return new SettingsSnapshot
                {
                    Mode = settings.Mode,
                    W1 = settings.W1,
                    W2 = settings.W2,
                    W3 = settings.W3,
                    W4 = settings.W4,
                    CatchmentRadius = settings.CatchmentRadius,
                    AccessRadius = settings.AccessRadius,
                };
            }

            public bool Equals(SettingsSnapshot other)
            {
                return Mode == other.Mode
                    && W1 == other.W1 && W2 == other.W2 && W3 == other.W3 && W4 == other.W4
                    && CatchmentRadius == other.CatchmentRadius
                    && AccessRadius == other.AccessRadius;
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_PopulationSystem = World.GetOrCreateSystemManaged<PopulationToGridSystem>();
            m_AvailabilitySystem = World.GetOrCreateSystemManaged<AvailabilityInfoToGridSystem>();
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

            m_ActiveInfomodeQuery = GetEntityQuery(
                ComponentType.ReadOnly<SuitabilityInfomodeData>(),
                ComponentType.ReadOnly<InfomodeActive>());

            m_LastStopCount = m_StopQuery.CalculateEntityCount();

            var settings = Mod.Settings;
            if (settings != null)
            {
                m_LastComputeSettings = SettingsSnapshot.Capture(settings);
                m_LastHighlightShare = settings.HighlightShare;
            }
        }

        protected override void OnDestroy()
        {
            DiscardPendingCompute();
            m_Intensities = null;
            m_ExpandedCache = null;
            m_RawScores = null;
            m_ScoreScratch = null;
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

            var currentSettings = SettingsSnapshot.Capture(settings);
            bool computeSettingsChanged = !currentSettings.Equals(m_LastComputeSettings);
            bool highlightChanged = settings.HighlightShare != m_LastHighlightShare;
            if (active && computeSettingsChanged)
            {
                ScheduleRecompute(DebounceSeconds);
            }
            else if (active && highlightChanged && m_RawScores != null)
            {
                // Highlight share only affects the normalization pass, so the
                // cached raw scores can be re-normalized without re-running the job.
                NormalizeIntensities();
            }
            m_LastComputeSettings = currentSettings;
            m_LastHighlightShare = settings.HighlightShare;

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
            if (m_JobPending || m_PopulationSystem == null || m_AvailabilitySystem == null || m_TerrainSystem == null)
            {
                return false;
            }

            var settings = Mod.Settings;
            Dependency.Complete();

            CellMapData<PopulationCell> popData = m_PopulationSystem.GetData(true, out JobHandle popDeps);
            CellMapData<AvailabilityInfoCell> availData = m_AvailabilitySystem.GetData(true, out JobHandle availDeps);

            if (popData.m_CellSize.x <= 0f || popData.m_CellSize.y <= 0f ||
                availData.m_CellSize.x <= 0f || availData.m_CellSize.y <= 0f ||
                popData.m_TextureSize.x <= 0 || popData.m_TextureSize.y <= 0 ||
                availData.m_TextureSize.x <= 0 || availData.m_TextureSize.y <= 0)
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
            CollectRoadNetwork(out List<float2> nodePositions, out List<float2> edgePositions);

            int2 bucketGrid = GridDims(mapSize, BucketSize);

            NativeArray<float2> stopPositionsNative = BuildBuckets(stopPositions, bucketGrid, worldMin, BucketSize, out NativeArray<int> stopOffsets, out NativeArray<int> stopCounts);
            NativeArray<float2> nodePositionsNative = BuildBuckets(nodePositions, bucketGrid, worldMin, BucketSize, out NativeArray<int> nodeOffsets, out NativeArray<int> nodeCounts);
            NativeArray<float2> edgePositionsNative = BuildBuckets(edgePositions, bucketGrid, worldMin, BucketSize, out NativeArray<int> edgeOffsets, out NativeArray<int> edgeCounts);
            var scores = new NativeArray<float>(totalCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            var job = new SuitabilityJob
            {
                GridSize = gridSize,
                BucketGridSize = bucketGrid,
                WorldMin = worldMin,
                TileSize = TileSize,
                BucketSize = BucketSize,
                CatchmentRadius = settings.CatchmentRadius,
                AccessRadius = settings.AccessRadius,
                DemandWeight = settings.W1,
                JobsWeight = settings.W2,
                CoverageWeight = settings.W3,
                AccessWeight = settings.W4,
                PopulationMap = popData.m_Buffer,
                PopulationCellSize = popData.m_CellSize,
                PopulationTextureSize = popData.m_TextureSize,
                AvailabilityMap = availData.m_Buffer,
                AvailabilityCellSize = availData.m_CellSize,
                AvailabilityTextureSize = availData.m_TextureSize,
                StopPositions = stopPositionsNative,
                StopBucketOffsets = stopOffsets,
                StopBucketCounts = stopCounts,
                NodePositions = nodePositionsNative,
                NodeBucketOffsets = nodeOffsets,
                NodeBucketCounts = nodeCounts,
                EdgePositions = edgePositionsNative,
                EdgeBucketOffsets = edgeOffsets,
                EdgeBucketCounts = edgeCounts,
                Scores = scores,
            };

            JobHandle deps = JobHandle.CombineDependencies(popDeps, availDeps);
            JobHandle handle = job.Schedule(totalCells, 64, deps);
            m_PopulationSystem.AddReader(handle);
            m_AvailabilitySystem.AddReader(handle);

            stopPositionsNative.Dispose(handle);
            stopOffsets.Dispose(handle);
            stopCounts.Dispose(handle);
            nodePositionsNative.Dispose(handle);
            nodeOffsets.Dispose(handle);
            nodeCounts.Dispose(handle);
            edgePositionsNative.Dispose(handle);
            edgeOffsets.Dispose(handle);
            edgeCounts.Dispose(handle);

            m_PendingHandle = handle;
            m_PendingScores = scores;
            m_PendingGrid = gridSize;
            m_PendingStopCount = stopPositions.Count;
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

            int totalCells = m_PendingScores.Length;
            if (m_RawScores == null || m_RawScores.Length != totalCells)
            {
                m_RawScores = new float[totalCells];
            }
            m_PendingScores.CopyTo(m_RawScores);
            m_PendingScores.Dispose();

            m_IntensityGrid = m_PendingGrid;
            NormalizeIntensities();
            Mod.Log.Info($"Overlay computed: grid {m_PendingGrid.x}x{m_PendingGrid.y}, stops={m_PendingStopCount}, roads(nodes/edges)={m_PendingNodeCount}/{m_PendingEdgeCount}");
        }

        private void DiscardPendingCompute()
        {
            if (!m_JobPending)
            {
                return;
            }

            m_PendingHandle.Complete();
            m_PendingScores.Dispose();
            m_JobPending = false;
        }

        // Turns the cached raw scores into 0..255 intensities. Runs after every
        // compute and again when only HighlightShare changes. The vanilla terrain
        // shader colors the overlay channel assigned to our infomode with its
        // gradient (see ApplyOverlayState).
        private void NormalizeIntensities()
        {
            int totalCells = m_RawScores.Length;
            if (m_Intensities == null || m_Intensities.Length != totalCells)
            {
                m_Intensities = new byte[totalCells];
            }

            m_ExpandedChannel = -1;

            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < totalCells; i++)
            {
                float v = m_RawScores[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            if (max - min <= 1e-5f)
            {
                Array.Clear(m_Intensities, 0, totalCells);
                return;
            }

            if (m_ScoreScratch == null || m_ScoreScratch.Length != totalCells)
            {
                m_ScoreScratch = new float[totalCells];
            }
            Array.Copy(m_RawScores, m_ScoreScratch, totalCells);

            // HighlightShare >= 1% keeps capIndex < totalCells.
            float highlightShare = Mod.Settings.HighlightShare / 100f;
            int capIndex = (int)math.floor(totalCells * (1f - highlightShare));
            float capScore = SelectKth(m_ScoreScratch, totalCells, capIndex);

            // In sparse cities most tiles share the minimum score, which drags the
            // cap percentile down to it; capping is only meaningful above the floor.
            // Normalizing against the cap keeps a few outlier tiles from compressing
            // everything else into the low bands.
            float cap = capScore > min ? capScore : max;
            float invRange = 1f / math.max(cap - min, 1e-5f);
            for (int i = 0; i < totalCells; i++)
            {
                float t = math.saturate((m_RawScores[i] - min) * invRange);
                m_Intensities[i] = (byte)math.round(math.pow(t, IntensityGamma) * 255f);
            }
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

        // Accessibility only considers the road network: pipes, power lines and rail
        // would otherwise inflate the score in places pedestrians cannot reach.
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

        private static NativeArray<float2> BuildBuckets(
            List<float2> positions,
            int2 gridSize,
            float2 worldMin,
            float tileSize,
            out NativeArray<int> offsets,
            out NativeArray<int> counts)
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
            var write = new NativeArray<int>(bucketCount, Allocator.Temp);
            NativeArray<int>.Copy(offsets, write);

            for (int i = 0; i < positions.Count; i++)
            {
                int2 cell = WorldToCell(positions[i], worldMin, tileSize, gridSize);
                int index = cell.x + cell.y * gridSize.x;
                int writeIndex = write[index]++;
                result[writeIndex] = positions[i];
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

        [BurstCompile]
        private struct SuitabilityJob : IJobParallelFor
        {
            // The disc mean of the triangular kernel is 1/3, so demand/jobs sums are
            // scaled back to the magnitude a flat kernel would produce; this keeps
            // the weights comparable to the (inherently triangular) coverage penalty.
            private const float KernelNormalization = 3f;
            // Per-feature access contributions, already including the 3x kernel
            // normalization (flat-kernel equivalents: 0.02 per edge, 0.05 per node).
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
            public float DemandWeight;
            public float JobsWeight;
            public float CoverageWeight;
            public float AccessWeight;

            [ReadOnly] public NativeArray<PopulationCell> PopulationMap;
            public float2 PopulationCellSize;
            public int2 PopulationTextureSize;

            [ReadOnly] public NativeArray<AvailabilityInfoCell> AvailabilityMap;
            public float2 AvailabilityCellSize;
            public int2 AvailabilityTextureSize;

            [ReadOnly] public NativeArray<float2> StopPositions;
            [ReadOnly] public NativeArray<int> StopBucketOffsets;
            [ReadOnly] public NativeArray<int> StopBucketCounts;

            [ReadOnly] public NativeArray<float2> NodePositions;
            [ReadOnly] public NativeArray<int> NodeBucketOffsets;
            [ReadOnly] public NativeArray<int> NodeBucketCounts;

            [ReadOnly] public NativeArray<float2> EdgePositions;
            [ReadOnly] public NativeArray<int> EdgeBucketOffsets;
            [ReadOnly] public NativeArray<int> EdgeBucketCounts;

            public NativeArray<float> Scores;

            public void Execute(int index)
            {
                int x = index % GridSize.x;
                int y = index / GridSize.x;
                float2 center = WorldMin + new float2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);

                float demand = SumPopulationCells(center, CatchmentRadius);
                float jobs = SumJobCells(center, CatchmentRadius);
                float penalty = ComputePenalty(center);
                float access = ComputeAccessibility(center);

                Scores[index] = (DemandWeight * demand) + (JobsWeight * jobs) - (CoverageWeight * penalty) + (AccessWeight * access);
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

                return sum * KernelNormalization;
            }

            private float SumJobCells(float2 center, float radius)
            {
                float2 mapSize = AvailabilityCellSize * new float2(AvailabilityTextureSize.x, AvailabilityTextureSize.y);
                float2 mapMin = -mapSize * 0.5f;
                float2 minPos = center - new float2(radius, radius);
                float2 maxPos = center + new float2(radius, radius);
                int2 minCell = WorldToCellInternal(minPos, mapMin, AvailabilityCellSize, AvailabilityTextureSize);
                int2 maxCell = WorldToCellInternal(maxPos, mapMin, AvailabilityCellSize, AvailabilityTextureSize);

                float sum = 0f;
                for (int cy = minCell.y; cy <= maxCell.y; cy++)
                {
                    for (int cx = minCell.x; cx <= maxCell.x; cx++)
                    {
                        int idx = cx + cy * AvailabilityTextureSize.x;
                        float2 cellCenter = mapMin + new float2((cx + 0.5f) * AvailabilityCellSize.x, (cy + 0.5f) * AvailabilityCellSize.y);
                        float dist = math.distance(cellCenter, center);
                        if (dist <= radius)
                        {
                            sum += AvailabilityMap[idx].m_AvailabilityInfo.z * TriangularWeight(dist, radius);
                        }
                    }
                }

                return sum * KernelNormalization;
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
