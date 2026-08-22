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
        // Width, dash and marker size all vary by mode: colour alone is not enough
        // to tell a bus line from a metro at a glance, especially against a
        // multi-coloured heatmap.
        private static float LineWidthFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 14f;
                case Setting.ModePreset.Metro: return 16f;
                case Setting.ModePreset.Train: return 20f;
                case Setting.ModePreset.Ferry: return 10f;
                default: return 10f;
            }
        }

        private static float StopDiameterFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Tram: return 26f;
                case Setting.ModePreset.Metro: return 34f;
                case Setting.ModePreset.Train: return 42f;
                case Setting.ModePreset.Ferry: return 30f;
                default: return 20f;
            }
        }
        private const float TerrainOffset = 4f;

        // Coloured by MODE, not by rank: rank is already visible in the ordered
        // options readout and the log, whereas which vehicle a line is for cannot be
        // read off the map any other way. Roughly follows transit-map convention.
        private static Color ColorFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Bus: return new Color(0.15f, 0.55f, 1f, 0.9f);
                case Setting.ModePreset.Tram: return new Color(1f, 0.45f, 0.1f, 0.9f);
                case Setting.ModePreset.Metro: return new Color(0.6f, 0.25f, 0.95f, 0.9f);
                case Setting.ModePreset.Train: return new Color(0.1f, 0.75f, 0.35f, 0.9f);
                case Setting.ModePreset.Ferry: return new Color(0.1f, 0.85f, 0.95f, 0.9f);
                default: return new Color(0.9f, 0.9f, 0.9f, 0.9f);
            }
        }

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

            // Existing lines that need attention, tinted by severity. Drawn thin and
            // beneath the suggestions so they inform without competing with them.
            System.Collections.Generic.List<ExistingLine> existing = m_OverlaySystem.ExistingLines;
            System.Collections.Generic.List<LineHealth> health = m_OverlaySystem.LineHealthList;
            if (existing != null && health != null)
            {
                for (int i = 0; i < health.Count; i++)
                {
                    LineHealth entry = health[i];
                    if (entry.Severity == 0)
                    {
                        continue;
                    }

                    // LineHealth is sorted worst-first, so m_Index carries the
                    // original position in the gathered list.
                    int source = entry.m_Index - 1;
                    if (source < 0 || source >= existing.Count)
                    {
                        continue;
                    }

                    ExistingLine line = existing[source];
                    Color tint = SeverityColor(entry.m_Verdict);
                    for (int p = 1; p < line.m_Path.Count; p++)
                    {
                        float3 from = ToGround(line.m_Path[p - 1], ref heightData);
                        float3 to = ToGround(line.m_Path[p], ref heightData);
                        buffer.DrawLine(tint, new Line3.Segment(from, to), 6f);
                    }
                }
            }

            for (int r = 0; r < routes.Count; r++)
            {
                SuggestedRoute route = routes[r];
                Color color = ColorFor(route.Mode);

                for (int i = 1; i < route.Path.Count; i++)
                {
                    float3 from = ToGround(route.Path[i - 1], ref heightData);
                    float3 to = ToGround(route.Path[i], ref heightData);
                    // Rail and water routes do not follow streets, so they are dashed
                    // to read as their own alignment rather than as a road overlay.
                    float width = LineWidthFor(route.Mode);
                    switch (route.Mode)
                    {
                        case Setting.ModePreset.Bus:
                        case Setting.ModePreset.Tram:
                            // DrawLine builds the straight curve internally, so there
                            // is no need to construct a Bezier here.
                            buffer.DrawLine(color, new Line3.Segment(from, to), width);
                            break;
                        case Setting.ModePreset.Metro:
                            buffer.DrawDashedLine(color, new Line3.Segment(from, to), width, 70f, 30f);
                            break;
                        case Setting.ModePreset.Train:
                            buffer.DrawDashedLine(color, new Line3.Segment(from, to), width, 140f, 60f);
                            break;
                        default:
                            buffer.DrawDashedLine(color, new Line3.Segment(from, to), width, 30f, 45f);
                            break;
                    }
                }

                // Each segment is drawn independently with flat ends, so a direction
                // change leaves a visible notch on the outside of the corner. A dot
                // the width of the line at each interior vertex closes it.
                for (int i = 1; i < route.Path.Count - 1; i++)
                {
                    float3 joint = ToGround(route.Path[i], ref heightData);
                    buffer.DrawCircle(color, joint, LineWidthFor(route.Mode));
                }

                for (int s = 0; s < route.Stops.Count; s++)
                {
                    float3 stop = ToGround(route.Stops[s], ref heightData);
                    buffer.DrawCircle(color, stop, StopDiameterFor(route.Mode));
                }
            }

            // The buffer was written on the main thread with the prior writers already
            // completed, so there is no new job handle to register.
        }

        private static Color SeverityColor(LineVerdict verdict)
        {
            switch (verdict)
            {
                case LineVerdict.AtModeCapacity: return new Color(0.9f, 0.24f, 0.2f, 0.85f);
                case LineVerdict.Overcrowded: return new Color(0.94f, 0.55f, 0.16f, 0.85f);
                case LineVerdict.LongWaits: return new Color(0.9f, 0.78f, 0.24f, 0.8f);
                default: return new Color(0.51f, 0.55f, 0.61f, 0.7f);
            }
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
