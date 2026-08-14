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
        private const float TileSize = 64f;
        private const float CatchmentRadius = 400f;
        private const float AccessRadius = 120f;
        private const float DebounceSeconds = 0.3f;
        private const float MaxPenalty = 1.5f;
        private const int InfomodePriority = 200;

        private static readonly Color LowColor = new Color(0.12f, 0.46f, 0.18f, 1f);
        private static readonly Color MediumColor = new Color(0.94f, 0.84f, 0.25f, 1f);
        private static readonly Color HighColor = new Color(0.85f, 0.22f, 0.12f, 1f);

        private TerrainSystem m_TerrainSystem;
        private TerrainRenderSystem m_TerrainRenderSystem;
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
        private Setting.ModePreset m_LastMode;
        private float m_LastW1;
        private float m_LastW2;
        private float m_LastW3;
        private float m_LastW4;

        private bool m_RecomputeRequested;
        private float m_RecomputeAt;
        private bool m_PrefabsAdded;
        private bool m_InfoviewLinkChecked;
        private bool m_LastOverlayApplied;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_TerrainRenderSystem = World.GetOrCreateSystemManaged<TerrainRenderSystem>();
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
                m_LastMode = settings.Mode;
                m_LastW1 = settings.W1;
                m_LastW2 = settings.W2;
                m_LastW3 = settings.W3;
                m_LastW4 = settings.W4;
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            m_Intensities = null;
            m_ExpandedCache = null;
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
                m_RecomputeRequested = false;
                return;
            }

            EnsureInfoviewLinked();

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

            bool modeChanged = settings.Mode != m_LastMode;
            bool weightsChanged = settings.W1 != m_LastW1 || settings.W2 != m_LastW2 || settings.W3 != m_LastW3 || settings.W4 != m_LastW4;
            if (active && (modeChanged || weightsChanged))
            {
                ScheduleRecompute(DebounceSeconds);
            }

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
            if (active && (m_Intensities == null || m_IntensityGrid.x != currentSize.x || m_IntensityGrid.y != currentSize.y))
            {
                ScheduleRecompute(0f);
            }

            if (active && m_RecomputeRequested && UnityEngine.Time.realtimeSinceStartup >= m_RecomputeAt)
            {
                if (ComputeOverlay(channel))
                {
                    m_RecomputeRequested = false;
                }
            }

            m_LastMode = settings.Mode;
            m_LastW1 = settings.W1;
            m_LastW2 = settings.W2;
            m_LastW3 = settings.W3;
            m_LastW4 = settings.W4;

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
            SetField(m_InfomodePrefab, "m_Steps", 12);
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
                Mod.Log.Warn("Infoview or infomode prefab entity not found yet.");
                m_InfoviewLinkChecked = true;
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

        private int m_LastLoggedIndex = int.MinValue;

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
            // Must match the grid ComputeOverlay derives from the cell maps: both
            // span the playable area, not the full terrain.
            float2 playable = m_TerrainSystem != null ? m_TerrainSystem.playableArea : new float2(0f, 0f);
            int width = math.max(1, (int)math.ceil(playable.x / TileSize));
            int height = math.max(1, (int)math.ceil(playable.y / TileSize));
            return new int2(width, height);
        }

        private bool ComputeOverlay(int channel)
        {
            if (m_PopulationSystem == null || m_AvailabilitySystem == null || m_TerrainSystem == null)
            {
                return false;
            }

            Dependency.Complete();

            JobHandle popDeps = default;
            JobHandle availDeps = default;
            CellMapData<PopulationCell> popData = m_PopulationSystem.GetData(true, out popDeps);
            CellMapData<AvailabilityInfoCell> availData = m_AvailabilitySystem.GetData(true, out availDeps);

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
            int width = math.max(1, (int)math.ceil(mapSize.x / TileSize));
            int height = math.max(1, (int)math.ceil(mapSize.y / TileSize));
            int2 gridSize = new int2(width, height);
            int totalCells = width * height;

            List<float2> stopPositions = CollectStopPositions(Mod.Settings.Mode);
            CollectRoadNetwork(out List<float2> nodePositions, out List<float2> edgePositions);

            using var stopPositionsNative = BuildBuckets(stopPositions, gridSize, worldMin, TileSize, out var stopOffsets, out var stopCounts);
            using var stopOffsetsNative = stopOffsets;
            using var stopCountsNative = stopCounts;
            using var nodePositionsNative = BuildBuckets(nodePositions, gridSize, worldMin, TileSize, out var nodeOffsets, out var nodeCounts);
            using var nodeOffsetsNative = nodeOffsets;
            using var nodeCountsNative = nodeCounts;
            using var edgePositionsNative = BuildBuckets(edgePositions, gridSize, worldMin, TileSize, out var edgeOffsets, out var edgeCounts);
            using var edgeOffsetsNative = edgeOffsets;
            using var edgeCountsNative = edgeCounts;
            using var scores = new NativeArray<float>(totalCells, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            var job = new SuitabilityJob
            {
                GridSize = gridSize,
                WorldMin = worldMin,
                TileSize = TileSize,
                CatchmentRadius = CatchmentRadius,
                AccessRadius = AccessRadius,
                W1 = Mod.Settings.W1,
                W2 = Mod.Settings.W2,
                W3 = Mod.Settings.W3,
                W4 = Mod.Settings.W4,
                PopulationMap = popData.m_Buffer,
                PopulationCellSize = popData.m_CellSize,
                PopulationTextureSize = popData.m_TextureSize,
                AvailabilityMap = availData.m_Buffer,
                AvailabilityCellSize = availData.m_CellSize,
                AvailabilityTextureSize = availData.m_TextureSize,
                StopPositions = stopPositionsNative,
                StopBucketOffsets = stopOffsetsNative,
                StopBucketCounts = stopCountsNative,
                NodePositions = nodePositionsNative,
                NodeBucketOffsets = nodeOffsetsNative,
                NodeBucketCounts = nodeCountsNative,
                EdgePositions = edgePositionsNative,
                EdgeBucketOffsets = edgeOffsetsNative,
                EdgeBucketCounts = edgeCountsNative,
                Scores = scores,
            };

            JobHandle deps = JobHandle.CombineDependencies(popDeps, availDeps);
            JobHandle handle = job.Schedule(totalCells, 64, deps);
            m_PopulationSystem.AddReader(handle);
            m_AvailabilitySystem.AddReader(handle);
            handle.Complete();

            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < scores.Length; i++)
            {
                float v = scores[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            float[] sorted = new float[scores.Length];
            scores.CopyTo(sorted);
            Array.Sort(sorted);
            int percentileIndex = sorted.Length > 0 ? (int)math.floor(sorted.Length * 0.95f) : 0;
            if (sorted.Length > 0 && percentileIndex >= sorted.Length)
            {
                percentileIndex = sorted.Length - 1;
            }
            float percentile95 = sorted.Length > 0 ? sorted[percentileIndex] : 0f;

            // Intensities only: the vanilla terrain shader colors the overlay channel
            // assigned to our infomode with its gradient (see ApplyOverlayState).
            if (m_Intensities == null || m_Intensities.Length != totalCells)
            {
                m_Intensities = new byte[totalCells];
            }
            else
            {
                Array.Clear(m_Intensities, 0, m_Intensities.Length);
            }

            float range = max - min;
            bool hasSignal = range > 1e-5f;
            // In sparse cities most tiles share the minimum score, which drags the 95th
            // percentile down to it; highlighting is only meaningful above the floor.
            bool highlightTop = hasSignal && percentile95 > min;
            if (hasSignal)
            {
                for (int i = 0; i < scores.Length; i++)
                {
                    float score = scores[i];
                    float normalized = math.saturate((score - min) / range);
                    byte intensity = (byte)math.round(normalized * 255f);
                    if (highlightTop && score >= percentile95)
                    {
                        intensity = 255;
                    }

                    m_Intensities[i] = intensity;
                }
            }

            m_IntensityGrid = gridSize;
            m_ExpandedChannel = -1;
            Mod.Log.Info($"Overlay computed: grid {width}x{height}, channel={channel}, stops={stopPositions.Count}, roads(nodes/edges)={nodePositions.Count}/{edgePositions.Count}, score range [{min:F1}, {max:F1}], p95={percentile95:F1}");
            return true;
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
            counts = new NativeArray<int>(bucketCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            for (int i = 0; i < positions.Count; i++)
            {
                int2 cell = WorldToCell(positions[i], worldMin, tileSize, gridSize);
                int index = cell.x + cell.y * gridSize.x;
                counts[index] += 1;
            }

            offsets = new NativeArray<int>(bucketCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            int running = 0;
            for (int i = 0; i < bucketCount; i++)
            {
                offsets[i] = running;
                running += counts[i];
            }

            var result = new NativeArray<float2>(positions.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var write = new NativeArray<int>(bucketCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
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

        private struct SuitabilityJob : IJobParallelFor
        {
            public int2 GridSize;
            public float2 WorldMin;
            public float TileSize;
            public float CatchmentRadius;
            public float AccessRadius;
            public float W1;
            public float W2;
            public float W3;
            public float W4;

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

                float demand = SumPopulation(center);
                float jobs = SumJobs(center);
                float penalty = ComputePenalty(center);
                float access = ComputeAccessibility(center);

                Scores[index] = (W1 * demand) + (W2 * jobs) - (W3 * penalty) + (W4 * access);
            }

            private float SumPopulation(float2 center)
            {
                return SumPopulationCells(center, CatchmentRadius);
            }

            private float SumJobs(float2 center)
            {
                return SumJobCells(center, CatchmentRadius);
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
                        if (math.distance(cellCenter, center) <= radius)
                        {
                            sum += PopulationMap[idx].m_Population;
                        }
                    }
                }

                return sum;
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
                        if (math.distance(cellCenter, center) <= radius)
                        {
                            sum += AvailabilityMap[idx].m_AvailabilityInfo.z;
                        }
                    }
                }

                return sum;
            }

            private float ComputePenalty(float2 center)
            {
                int radiusTiles = (int)math.ceil(CatchmentRadius / TileSize);
                int2 baseCell = WorldToCell(center, WorldMin, TileSize, GridSize);
                float penalty = 0f;

                for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
                {
                    int cy = baseCell.y + dy;
                    if (cy < 0 || cy >= GridSize.y)
                    {
                        continue;
                    }

                    for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                    {
                        int cx = baseCell.x + dx;
                        if (cx < 0 || cx >= GridSize.x)
                        {
                            continue;
                        }

                        int bucket = cx + cy * GridSize.x;
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

                            penalty += 1f - (dist / CatchmentRadius);
                        }
                    }
                }

                return math.clamp(penalty, 0f, MaxPenalty);
            }

            private float ComputeAccessibility(float2 center)
            {
                int radiusTiles = (int)math.ceil(AccessRadius / TileSize);
                int2 baseCell = WorldToCell(center, WorldMin, TileSize, GridSize);
                int nodes = 0;
                int edges = 0;

                for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
                {
                    int cy = baseCell.y + dy;
                    if (cy < 0 || cy >= GridSize.y)
                    {
                        continue;
                    }

                    for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
                    {
                        int cx = baseCell.x + dx;
                        if (cx < 0 || cx >= GridSize.x)
                        {
                            continue;
                        }

                        int bucket = cx + cy * GridSize.x;
                        int nodeCount = NodeBucketCounts[bucket];
                        int nodeStart = NodeBucketOffsets[bucket];
                        for (int i = 0; i < nodeCount; i++)
                        {
                            float2 pos = NodePositions[nodeStart + i];
                            if (math.distance(pos, center) <= AccessRadius)
                            {
                                nodes += 1;
                            }
                        }

                        int edgeCount = EdgeBucketCounts[bucket];
                        int edgeStart = EdgeBucketOffsets[bucket];
                        for (int i = 0; i < edgeCount; i++)
                        {
                            float2 pos = EdgePositions[edgeStart + i];
                            if (math.distance(pos, center) <= AccessRadius)
                            {
                                edges += 1;
                            }
                        }
                    }
                }

                float access = (edges * 0.02f) + (nodes * 0.05f);
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
