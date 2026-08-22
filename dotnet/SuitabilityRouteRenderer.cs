using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace StationSuitabilityOverlay
{
    // Draws the suggested routes as world-space polylines with stop markers.
    //
    // Separate from the overlay system because of phase ordering. OverlayRenderSystem
    // drains and clears its buffer during SystemUpdatePhase.Rendering; the overlay
    // system lives in PreCulling, which runs later, so it is the wrong place to draw
    // from. This sits in Rendering alongside the vanilla overlay producers.
    //
    // Ordering within a phase follows registration order, and mods register after the
    // game does, so these draws are most likely consumed on the FOLLOWING frame. That
    // is invisible here: the routes only change when the demand pipeline reruns, and
    // the overlay is redrawn every frame regardless.
    public sealed partial class SuitabilityRouteRenderer : GameSystemBase
    {
        private const float LineWidth = 12f;
        private const float StopDiameter = 26f;
        private const float TerrainOffset = 4f;

        // One colour per suggested route, cycled. Chosen to stay legible against the
        // green-to-red terrain heatmap the mod also draws.
        private static readonly Color[] s_RouteColors =
        {
            new Color(0.15f, 0.55f, 1f, 0.9f),
            new Color(1f, 0.45f, 0.1f, 0.9f),
            new Color(0.6f, 0.25f, 0.95f, 0.9f),
            new Color(0.1f, 0.85f, 0.65f, 0.9f),
            new Color(1f, 0.85f, 0.2f, 0.9f),
            new Color(0.95f, 0.3f, 0.6f, 0.9f),
        };

        private OverlayRenderSystem m_OverlayRenderSystem;
        private RenderingSystem m_RenderingSystem;
        private TerrainSystem m_TerrainSystem;
        private StationSuitabilityOverlaySystem m_OverlaySystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_RenderingSystem = World.GetOrCreateSystemManaged<RenderingSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_OverlaySystem = World.GetOrCreateSystemManaged<StationSuitabilityOverlaySystem>();
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            if (settings == null || !settings.ShowRoutes || m_OverlaySystem == null)
            {
                return;
            }

            // Photo mode and screenshot capture hide every overlay; match that.
            if (m_RenderingSystem != null && m_RenderingSystem.hideOverlay)
            {
                return;
            }

            // Only draw while our own infoview is the active one, so the routes do
            // not float over unrelated views.
            if (!m_OverlaySystem.IsInfoviewActive)
            {
                return;
            }

            List<SuggestedRoute> routes = m_OverlaySystem.SuggestedRoutes;
            if (routes == null || routes.Count == 0)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out Unity.Jobs.JobHandle dependencies);
            dependencies.Complete();

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData(false);

            for (int r = 0; r < routes.Count; r++)
            {
                SuggestedRoute route = routes[r];
                Color color = s_RouteColors[r % s_RouteColors.Length];

                for (int i = 1; i < route.Path.Count; i++)
                {
                    float3 from = ToGround(route.Path[i - 1], ref heightData);
                    float3 to = ToGround(route.Path[i], ref heightData);
                    // DrawLine builds the straight curve internally, so there is no
                    // need to construct a Bezier here.
                    buffer.DrawLine(color, new Line3.Segment(from, to), LineWidth);
                }

                for (int s = 0; s < route.Stops.Count; s++)
                {
                    float3 stop = ToGround(route.Stops[s], ref heightData);
                    buffer.DrawCircle(color, stop, StopDiameter);
                }
            }

            // The buffer was written on the main thread with the prior writers already
            // completed, so there is no new job handle to register.
        }

        private float3 ToGround(float2 flat, ref TerrainHeightData heightData)
        {
            var probe = new float3(flat.x, 0f, flat.y);
            float height = TerrainUtils.SampleHeight(ref heightData, probe);
            // Lifted slightly so the line is not buried in the terrain it follows.
            return new float3(flat.x, height + TerrainOffset, flat.y);
        }
    }
}
