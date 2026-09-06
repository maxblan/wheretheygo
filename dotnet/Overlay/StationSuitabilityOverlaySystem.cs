using System;
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
using Unity.Entities;
using Unity.Mathematics;
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

        private const float DebounceSeconds = 0.3f;

        private const float PeriodicRefreshSeconds = 10f;

        // Road, workplace and zoning collections are cached and rebuilt from change
        // detection; this is the backstop for inputs that change with no
        // Created/Updated tag.
        private const float CollectionRefreshSeconds = 60f;

        // Terrain barely ever changes, and the mask costs a full-grid sampling pass
        // plus a flood fill, so it is refreshed far less often than the scores.
        private const float MaskRefreshSeconds = 120f;

        // Travel demand extraction walks every citizen and runs many shortest-path
        // searches, so it is far slower than the per-tile scoring.
        // Also the cadence at which each line is sampled into the rolling window, so
        // shortening it both gets the first suggestions up sooner and doubles the
        // number of readings a day's verdict rests on.
        private const float DemandRefreshSeconds = 30f;

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

        private SuitabilityInfoview m_Infoview;
        private SuitabilityTripObserver m_TripObserver;

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

        private bool m_LastActive;

        private ComputeSnapshot m_LastComputeSettings;

        private CombineSnapshot m_LastCombineSettings;

        private bool m_RecomputeRequested;

        private float m_RecomputeAt;

        private int m_LastStopCount;

        // The game clock, for stamping observed journeys and line readings with the
        // time of day (Daytime), and the city's working hours for the commute shifts.
        private TimeSystem? m_TimeSystem;

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
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
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
            m_TripObserver = new SuitabilityTripObserver(EntityManager, m_QueuedQuery, m_TravellingQuery, m_InsideQuery);

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

            CreateInfoview();

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

        // The infoview presenter and the queries it watches. Created here, by the system,
        // so they stay registered with this system's dependencies.
        private void CreateInfoview()
        {
            m_ActiveInfomodeQuery = GetEntityQuery(
                ComponentType.ReadOnly<SuitabilityInfomodeData>(),
                ComponentType.ReadOnly<InfomodeActive>());

            m_PlaceableInfoviewQuery = GetEntityQuery(ComponentType.ReadOnly<PlaceableInfoviewItem>());
            m_PlaceableInfoviewChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PlaceableInfoviewItem>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>() },
            });
            m_Infoview = new SuitabilityInfoview(
                EntityManager, m_PrefabSystem, m_ToolSystem, m_OverlayInfomodeSystem,
                m_ActiveInfomodeQuery, m_PlaceableInfoviewQuery, m_PlaceableInfoviewChangedQuery,
                static fault => s_PipelineStatus = fault);
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
            m_RawTerms = null;
            m_Scores = null;
            m_ScoreScratch = null;
            m_TermScratch = null;
            m_Access = null;
            m_AccessInputs = null;
            m_WalkGraph = null;
            m_TripObserver.Clear();
            m_Land = null;
            m_Infoview.Release();

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

        private float TimeOfDay => m_TimeSystem?.normalizedTime ?? 0f;

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            if (settings is null)
            {
                return;
            }

            m_Infoview.EnsurePrefabs();

            if (GameManager.instance == null || !GameManager.instance.gameMode.IsGame())
            {
                DiscardPendingCompute();
                DiscardPendingRoutes();
                m_RecomputeRequested = false;
                return;
            }

            m_Infoview.EnsureInfoviewLinked();
            m_Infoview.SweepPlaceableInfoviews();
            TrackInputChanges();
            HandleExportRequest();
            FinishRoutesIfReady(settings);
            if (!m_RoutesPending)
            {
                HandleImprovementRequest();
                HandleCalibrationRequests(settings);
                FinishComputeIfReady();
            }

            int activeLayers = m_Infoview.ResolveActiveLayers(out long signature);
            bool active = activeLayers > 0;

            if (active != m_LastActive)
            {
                // Drawing the map neither starts nor cancels a pass: the scores it paints
                // are the ones the route pass already keeps fresh.
                Mod.Log.Info(active ? "Heat map shown" : "Heat map hidden");
                m_LastActive = active;
            }

            // From here on nothing is gated on `active` (the heat map being DRAWN): the
            // access pass, the demand refresh and the route pass run whenever a city is
            // loaded, because the suggestions and the line verdicts are read from the
            // panel with the map off as often as on. `active` only decides whether the
            // scores are painted (SuitabilityInfoview.ApplyOverlayState).
            var computeSettings = ComputeSnapshot.Capture(settings);
            if (!computeSettings.Equals(m_LastComputeSettings))
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

            MaybeRecombine(settings);

            int stopCount = m_StopQuery.CalculateEntityCount();
            if (stopCount != m_LastStopCount)
            {
                m_LastStopCount = stopCount;
                ScheduleRecompute(DebounceSeconds);
            }
            else if (!m_StopChangedQuery.IsEmptyIgnoreFilter)
            {
                ScheduleRecompute(DebounceSeconds);
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!m_JobPending && !m_RecomputeRequested && m_RawTerms is not null
                && now - m_LastComputeFinish >= PeriodicRefreshSeconds)
            {
                ScheduleRecompute(0f);
            }

            int2 currentSize = GetGridSize();
            if (!m_JobPending && (m_RawTerms is null || !m_PlayableGridAtCompute.Equals(currentSize)))
            {
                ScheduleRecompute(0f);
            }

            if (m_RecomputeRequested && !m_JobPending && !m_RoutesPending && now >= m_RecomputeAt)
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
                MaybeUpdateTravelDemand(settings, now);
                SampleRidership(settings, now);
            }

            m_Infoview.ApplyOverlayState(active, signature, m_RawTerms is not null, m_LayerIntensities, m_IntensityGrid);
        }

        // A changed term weight re-combines the existing terms without a new compute.
        // The recombine writes the score field in place, which the route worker reads,
        // so while a pass is pending the change waits — and stays noticed, because the
        // snapshot is only recorded once the recombine has run.
        private void MaybeRecombine(Setting settings)
        {
            if (m_RoutesPending)
            {
                return;
            }

            var combineSettings = CombineSnapshot.Capture(settings);
            if (!combineSettings.Equals(m_LastCombineSettings) && m_RawTerms is not null)
            {
                RecombineAndNormalize();
            }

            m_LastCombineSettings = combineSettings;
        }

        // The demand pipeline needs the per-tile terms to exist (for the served
        // discount and stop snapping), so it always runs after a compute has landed.
        private void MaybeUpdateTravelDemand(Setting settings, float now)
        {
            if (m_RawTerms is null || m_JobPending)
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
                _ = StartRoutePass(settings, m_IntensityGrid, m_ScoreWorldMin, tripCount: -1, totalZoneWeight: 0f);
                return;
            }

            float2 mapSize = new float2(m_IntensityGrid.x, m_IntensityGrid.y) * TileSize;
            UpdateTravelDemand(settings, m_IntensityGrid, m_ScoreWorldMin, mapSize, objectiveChanged);
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

        private int2 GetGridSize()
        {
            float2 playable = m_TerrainSystem is not null ? m_TerrainSystem.playableArea : new float2(0f, 0f);
            return SuitabilityInputs.GridDims(playable, TileSize);
        }
    }
}
