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

namespace WhereTheyGo
{
    // The vanilla heatmap pipeline: OverlayInfomodeSystem clears the terrain
    // override overlay each frame and active heatmap infomodes rewrite it, then
    // TerrainRenderSystem consumes it into the terrain material. All three run in
    // PreCulling, so this system must be ordered between the two vanilla ones.
    [UpdateAfter(typeof(OverlayInfomodeSystem))]
    [UpdateBefore(typeof(TerrainRenderSystem))]
    public sealed partial class WhereTheyGoSystem : GameSystemBase
    {

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always

        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private TerrainSystem m_TerrainSystem;

        private WaterSystem m_WaterSystem;

        private PopulationToGridSystem m_PopulationSystem;

        private Game.Prefabs.ZoneSystem m_ZoneSystem;

        private PrefabSystem m_PrefabSystem;

        private ToolSystem m_ToolSystem;

        // Carrying out a recommendation goes through the game's own systems: the policy
        // system applies the vehicle-count and schedule policies, the camera system
        // follows a line the player asked to see (F9Actions).
        private Game.UI.InGame.PoliciesUISystem m_PoliciesUISystem;

        private Game.Rendering.CameraUpdateSystem? m_CameraUpdateSystem;

        // Map markers on the lines that need rebuilding (F9Notifications).
        private Game.Notifications.IconCommandSystem m_IconCommandSystem;

        private Game.UI.NameSystem m_NameSystem;

        private OverlayInfomodeSystem m_OverlayInfomodeSystem;

        private Infoview m_Infoview;
        private TripObserver m_TripObserver;

#pragma warning restore CS8618

        private EntityQuery m_StopQuery;

        private EntityQuery m_NodeQuery;

        private EntityQuery m_NodeChangedQuery;

        private EntityQuery m_EdgeChangedQuery;

        private EntityQuery m_ActiveInfomodeQuery;
        private EntityQuery m_PlaceableInfoviewQuery;
        private EntityQuery m_PlaceableInfoviewChangedQuery;

        private ComponentLookup<Transform> m_TransformLookup;

        private ComponentLookup<Game.Buildings.PropertyRenter> m_PropertyRenterLookup;

        private ComponentLookup<ZoneData> m_ZoneDataLookup;

        private bool m_LastActive;

        private bool m_RecomputeRequested;

        private float m_RecomputeAt;

        // The game clock, for stamping observed journeys and line readings with the
        // time of day (Daytime), and the city's working hours for the commute shifts.
        private TimeSystem? m_TimeSystem;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_WaterSystem = World.GetOrCreateSystemManaged<WaterSystem>();
            m_PopulationSystem = World.GetOrCreateSystemManaged<PopulationToGridSystem>();
            m_ZoneSystem = World.GetOrCreateSystemManaged<Game.Prefabs.ZoneSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PoliciesUISystem = World.GetOrCreateSystemManaged<Game.UI.InGame.PoliciesUISystem>();
            m_CameraUpdateSystem = World.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
            m_IconCommandSystem = World.GetOrCreateSystemManaged<Game.Notifications.IconCommandSystem>();
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

            m_NodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_AllEdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            // The city's working hours, which place every commute's two rides
            // (Daytime.CommuteHours). Created here since the fleet reader that used to
            // own this query went with the suggestions.
            m_EconomyQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());

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

            m_NodeChangedQuery = ChangedQuery(ComponentType.ReadOnly<Node>());
            m_EdgeChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Road>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });

            m_CitizenQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<HouseholdMember>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_QueuedQuery = LiveQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<TripNeeded>(), ComponentType.ReadOnly<CurrentBuilding>());
            m_TravellingQuery = LiveQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<TravelPurpose>(), ComponentType.ReadOnly<CurrentTransport>());
            m_InsideQuery = LiveQuery(ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<CurrentBuilding>());
            m_TripObserver = new TripObserver(EntityManager, m_QueuedQuery, m_TravellingQuery, m_InsideQuery);

            m_WorkerLookup = GetComponentLookup<Worker>(isReadOnly: true);
            m_StudentLookup = GetComponentLookup<Game.Citizens.Student>(isReadOnly: true);
            m_TouristLookup = GetComponentLookup<TouristHousehold>(isReadOnly: true);
            m_TransformLookup = GetComponentLookup<Transform>(isReadOnly: true);
            m_PropertyRenterLookup = GetComponentLookup<Game.Buildings.PropertyRenter>(isReadOnly: true);
            m_ZoneDataLookup = GetComponentLookup<ZoneData>(isReadOnly: true);

            CreateInfoview();

        }

        // The infoview presenter and the queries it watches. Created here, by the system,
        // so they stay registered with this system's dependencies.
        private void CreateInfoview()
        {
            m_ActiveInfomodeQuery = GetEntityQuery(
                ComponentType.ReadOnly<WhereTheyGoInfomodeData>(),
                ComponentType.ReadOnly<InfomodeActive>());

            m_PlaceableInfoviewQuery = GetEntityQuery(ComponentType.ReadOnly<PlaceableInfoviewItem>());
            m_PlaceableInfoviewChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PlaceableInfoviewItem>() },
                Any = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<Updated>() },
            });
            m_Infoview = new Infoview(
                EntityManager, m_PrefabSystem, m_ToolSystem,
                m_ActiveInfomodeQuery, m_PlaceableInfoviewQuery, m_PlaceableInfoviewChangedQuery);
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
            m_TileSnap = null;
            m_WalkGraph = null;
            m_TripObserver.Clear();
            m_Infoview.Release();

            // Leaving a city must not leave its numbers on the panel. LineHistory
            // already guards its own series against a save change; these strings had
            // no such guard and were shown against the next city until its first
            // refresh landed.
            s_Figures = default;
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
                m_RecomputeRequested = false;
                return;
            }

            m_Infoview.EnsureInfoviewLinked();
            m_Infoview.SweepPlaceableInfoviews();
            TrackInputChanges();
            HandleInfoviewRequest();
            AdvanceHourIfPlaying();
            FinishComputeIfReady();
            FinishRoutingIfReady();

            bool active = m_Infoview.ActiveLayers() > 0;

            if (active != m_LastActive)
            {
                Mod.Log.Info(active ? "Infoview shown" : "Infoview hidden");
                m_LastActive = active;
            }

            // Nothing below is gated on `active` (the infoview being open): the walk
            // pass and the demand refresh run whenever a city is loaded, because the
            // figures are read from the panel with the map off as often as on.
            // The tile snap depends on the pedestrian network and on the size of the
            // map, and on nothing else. It used to be re-run every ten seconds with
            // the old scoring pass, which on a 448x448 grid is two hundred thousand
            // nearest-node queries a stop-watch tick — for an answer that only changes
            // when somebody builds a road. Roads are watched by TrackInputChanges;
            // what is left here is the map itself.
            float now = UnityEngine.Time.realtimeSinceStartup;
            int2 currentSize = GetGridSize();
            if (!m_JobPending && (m_TileSnap is null || !m_PlayableGridAtCompute.Equals(currentSize)))
            {
                ScheduleRecompute(0f);
            }

            if (m_RecomputeRequested && !m_JobPending && now >= m_RecomputeAt)
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
            // exactly as broken as it sounds. Reading six lines is cheap.
            //
            // The READING runs on the simulation clock (every ReadingIntervalFrames,
            // A8.5).
            ObserveLines();
            if (now - m_LastLineSample >= Assumptions.DemandRefreshSeconds && !m_RoutingPending)
            {
                m_LastLineSample = now;
                RefreshLineHealth();
            }

            // Also ungated on `active`: a journey not seen is demand not counted.
            if (now - m_LastTripObservation >= Assumptions.TripObservationSeconds)
            {
                m_LastTripObservation = now;
                ObserveTrips();
            }

            MaybeUpdateTravelDemand(settings, now);

            // Clicking a line asks its question straight away rather than waiting for
            // the next demand refresh, which is up to thirty seconds off.
            if (LineSelectionChanged() && !m_RoutingPending && m_TileSnap is not null)
            {
                _ = StartRoutingPass();
            }

        }

        // The demand pipeline needs the tile snap to exist (for the served discount
        // and stop snapping), so it always runs after a snap has landed.
        private void MaybeUpdateTravelDemand(Setting settings, float now)
        {
            // While a routing pass is out, every input it reads — the journeys, the
            // lines, the stops — stays exactly as the worker saw it.
            if (m_TileSnap is null || m_JobPending || m_RoutingPending)
            {
                return;
            }

            if (now - m_LastDemandRefresh < Assumptions.DemandRefreshSeconds && m_ZoneFlows.Count > 0)
            {
                return;
            }

            float2 mapSize = new float2(m_IntensityGrid.x, m_IntensityGrid.y) * Assumptions.TileSize;
            UpdateTravelDemand(settings, m_IntensityGrid, m_ScoreWorldMin, mapSize);
        }

        // Change tags live for a single frame, so the caches must be invalidated
        // from a per-frame check rather than sampled when a compute starts.
        // Change tags live for a single frame, so the pedestrian network is watched
        // from a per-frame check rather than sampled when a snap starts. Nodes and
        // edges both carry Created/Updated/Deleted, so there is nothing here that
        // needs a timed backstop — and the snap is far too expensive to run on one.
        private void TrackInputChanges()
        {
            if (m_NodeChangedQuery.IsEmptyIgnoreFilter && m_EdgeChangedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            m_RoadCacheDirty = true;
            ScheduleRecompute(Assumptions.DebounceSeconds);
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
            return WalkNetwork.GridDims(playable, Assumptions.TileSize);
        }
    }
}
