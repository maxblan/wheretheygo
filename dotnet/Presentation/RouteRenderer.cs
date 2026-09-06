using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TransitArchitect
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
    public sealed partial class RouteRenderer : GameSystemBase
    {
        // Width, dash and marker size all vary by mode: colour alone is not enough
        // to tell a bus line from a metro at a glance, especially against a
        // multi-coloured heatmap.
        private static float LineWidthFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 14f;
                case ModePreset.Metro: return 16f;
                case ModePreset.Train: return 20f;
                case ModePreset.Ferry: return 10f;
                default: return 10f;
            }
        }

        private static float StopDiameterFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 26f;
                case ModePreset.Metro: return 34f;
                case ModePreset.Train: return 42f;
                case ModePreset.Ferry: return 30f;
                default: return 20f;
            }
        }
        // Ranked site markers (author's decision 11a, 2026-09-06). Rank has to be
        // readable without a label, because the overlay buffer draws geometry only —
        // there is no text primitive — so it is carried by size and opacity: the best
        // site is the largest and fully opaque, and each one after it is a step
        // smaller down to a floor, so a marker never shrinks out of sight.
        private const float SiteMarkerDiameter = 90f;

        private const float SiteMarkerStep = 8f;

        private const float SiteMarkerMinDiameter = 34f;

        // The dark centre that turns a disc into a ring, as a share of the diameter.
        private const float SiteMarkerCoreShare = 0.55f;

        private const float SiteMarkerMinOpacity = 0.45f;

        private const float TerrainOffset = 4f;


        // Opacity of a drawn route. The colour itself belongs to the mode and lives
        // in TransitModes, so the map and the panel cannot disagree about it.
        private const float RouteOpacity = 0.9f;

        // What pointing at a row in the panel does to its line on the map: same mode
        // colour, drawn heavier and fully opaque with a size up on the stop markers, so
        // a glance answers "which one is this" without touching any other line.
        private const float HighlightWidthScale = 2f;
        private const float HighlightStopScale = 1.4f;
        private const float HighlightOpacity = 1f;

        private static Color ColorFor(ModePreset mode)
        {
            TransitModes.ColorFor(mode, out byte red, out byte green, out byte blue);
            return new Color(red / 255f, green / 255f, blue / 255f, RouteOpacity);
        }

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private OverlayRenderSystem m_OverlayRenderSystem;
        private RenderingSystem m_RenderingSystem;
        private TerrainSystem m_TerrainSystem;
        private TransitArchitectSystem m_OverlaySystem;
#pragma warning restore CS8618

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_RenderingSystem = World.GetOrCreateSystemManaged<RenderingSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_OverlaySystem = World.GetOrCreateSystemManaged<TransitArchitectSystem>();
        }

        protected override void OnUpdate()
        {
            var settings = Mod.Settings;
            if (settings is null || m_OverlaySystem is null)
            {
                return;
            }

            // Photo mode and screenshot capture hide every overlay; match that.
            if (m_RenderingSystem is not null && m_RenderingSystem.hideOverlay)
            {
                return;
            }

            // Draw while the mod's panel is open. Deliberately NOT gated on our
            // infoview being active: the heat map is a separate toggle beside "Show
            // routes" in the panel, and keying off the infoview meant turning the heat
            // map off silently took the route suggestions with it. Still suppressed
            // under someone else's infoview, which is what the old gate was really for.
            if (!m_OverlaySystem.PanelOpen || m_OverlaySystem.ForeignInfoviewActive)
            {
                return;
            }

            // "Show routes" is about the suggested LINES; the ranked site markers belong
            // to the heat map and are switched with it, not with them.
            List<SuggestedRoute> routes = m_OverlaySystem.SuggestedRoutes;
            bool anyRoutes = settings.ShowRoutes && routes is not null && routes.Count > 0;
            bool anySites = m_OverlaySystem.IsInfoviewActive && m_OverlaySystem.SiteCount > 0;
            // The re-traced alignment for one existing line stands on its own: it is
            // asked for from the transport overview, and a city with no suggestions at
            // all can still ask for one.
            bool anyProposal = settings.ShowRoutes && m_OverlaySystem.ImprovedRoute is not null;
            if (!anyRoutes && !anySites && !anyProposal)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out Unity.Jobs.JobHandle dependencies);
            dependencies.Complete();

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData(waitForPending: false);

            // The sites the heat map recommends, ranked. Drawn only with the heat map
            // itself: they are the answer that map is giving, and on their own over
            // bare terrain there is nothing to read them against.
            if (anySites)
            {
                DrawSiteMarkers(buffer, ref heightData, settings.Mode);
            }

            if (anyProposal)
            {
                DrawProposal(buffer, ref heightData);
            }

            if (!anyRoutes || routes is null)
            {
                return;
            }

            DrawSuggestions(buffer, ref heightData, routes);

            // The buffer was written on the main thread with the prior writers already
            // completed, so there is no new job handle to register.
        }

        // The improved alignment for whichever line the player asked about, drawn white
        // and dashed so it reads as a proposal against the existing line it replaces.
        private void DrawProposal(OverlayRenderSystem.Buffer buffer, ref TerrainHeightData heightData)
        {
            SuggestedRoute? improved = m_OverlaySystem.ImprovedRoute;
            if (improved is null)
            {
                return;
            }

            var proposalColor = new Color(1f, 1f, 1f, 0.95f);
            for (int i = 1; i < improved.Path.Count; i++)
            {
                float3 from = ToGround(improved.Path[i - 1], ref heightData);
                float3 to = ToGround(improved.Path[i], ref heightData);
                buffer.DrawDashedLine(proposalColor, new Line3.Segment(from, to), 14f, 50f, 25f);
            }

            for (int i = 0; i < improved.Stops.Count; i++)
            {
                buffer.DrawCircle(proposalColor, ToGround(improved.Stops[i], ref heightData), 28f);
            }
        }

        // The suggested lines themselves, in their mode's colour, width and dash.
        private static void DrawSuggestions(OverlayRenderSystem.Buffer buffer, ref TerrainHeightData heightData, List<SuggestedRoute> routes)
        {
            // Narrowed to one suggestion, or -1 for all of them. Both this and the
            // highlight are positions in the current list, which the overlay system
            // clears whenever it replaces that list.
            int only = TransitArchitectSystem.SelectedRouteIndex;
            int highlighted = TransitArchitectSystem.HighlightedRouteIndex;

            for (int r = 0; r < routes.Count; r++)
            {
                if (only >= 0 && r != only)
                {
                    continue;
                }

                SuggestedRoute route = routes[r];
                bool heavy = r == highlighted;
                Color color = ColorFor(route.Mode);
                if (heavy)
                {
                    color.a = HighlightOpacity;
                }

                float widthScale = heavy ? HighlightWidthScale : 1f;
                float stopScale = heavy ? HighlightStopScale : 1f;

                for (int i = 1; i < route.Path.Count; i++)
                {
                    float3 from = ToGround(route.Path[i - 1], ref heightData);
                    float3 to = ToGround(route.Path[i], ref heightData);
                    // Rail and water routes do not follow streets, so they are dashed
                    // to read as their own alignment rather than as a road overlay.
                    float width = LineWidthFor(route.Mode) * widthScale;
                    switch (route.Mode)
                    {
                        case ModePreset.Bus:
                        case ModePreset.Tram:
                            // DrawLine builds the straight curve internally, so there
                            // is no need to construct a Bezier here.
                            buffer.DrawLine(color, new Line3.Segment(from, to), width);
                            break;
                        case ModePreset.Metro:
                            buffer.DrawDashedLine(color, new Line3.Segment(from, to), width, 70f, 30f);
                            break;
                        case ModePreset.Train:
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
                    // Only where the line actually turns hard enough to leave a notch.
                    // Street routes now carry sampled curve points, and a dot at every
                    // one of those turned a smooth bend into a string of beads.
                    if (TurnsSharply(route.Path[i - 1], route.Path[i], route.Path[i + 1]))
                    {
                        float3 joint = ToGround(route.Path[i], ref heightData);
                        buffer.DrawCircle(color, joint, LineWidthFor(route.Mode) * widthScale);
                    }
                }

                for (int s = 0; s < route.Stops.Count; s++)
                {
                    float3 stop = ToGround(route.Stops[s], ref heightData);
                    buffer.DrawCircle(color, stop, StopDiameterFor(route.Mode) * stopScale);
                }
            }
        }

        // One ring per recommended site, best first. Two circles rather than one: the
        // mode colour outside and a dark core inside, so a marker reads as a target on
        // top of the heat map instead of as another blob of it.
        private void DrawSiteMarkers(OverlayRenderSystem.Buffer buffer, ref TerrainHeightData heightData, ModePreset mode)
        {
            Color color = ColorFor(mode);
            var core = new Color(0.06f, 0.08f, 0.11f, 0.85f);
            int count = m_OverlaySystem.SiteCount;
            for (int rank = 0; rank < count; rank++)
            {
                if (!m_OverlaySystem.TryGetSite(rank, out float3 flat, out int _))
                {
                    continue;
                }

                float diameter = math.max(SiteMarkerMinDiameter, SiteMarkerDiameter - (rank * SiteMarkerStep));
                // Opacity falls with rank as well as size, so two sites of the same
                // clamped size are still ordered on screen.
                float fade = count > 1 ? rank / (float)(count - 1) : 0f;
                color.a = math.lerp(1f, SiteMarkerMinOpacity, fade);

                float height = TerrainUtils.SampleHeight(ref heightData, flat);
                var position = new float3(flat.x, height + TerrainOffset, flat.z);
                buffer.DrawCircle(color, position, diameter);
                buffer.DrawCircle(core, position, diameter * SiteMarkerCoreShare);
            }
        }

        // Interior corner sharp enough that the two segments' square ends leave a gap
        // on the outside of the turn.
        private static bool TurnsSharply(float2Like before, float2Like at, float2Like after)
        {
            var incoming = new float2(at.x - before.x, at.y - before.y);
            var outgoing = new float2(after.x - at.x, after.y - at.y);
            float inLength = math.length(incoming);
            float outLength = math.length(outgoing);
            if (inLength < 0.01f || outLength < 0.01f)
            {
                return false;
            }

            // cos 20 degrees; below this the joint is invisible at any zoom.
            float cosine = math.dot(incoming / inLength, outgoing / outLength);
            return cosine < 0.94f;
        }

        private static float3 ToGround(float2Like flat, ref TerrainHeightData heightData)
        {
            var probe = new float3(flat.x, 0f, flat.y);
            float height = TerrainUtils.SampleHeight(ref heightData, probe);
            // Lifted slightly so the line is not buried in the terrain it follows.
            return new float3(flat.x, height + TerrainOffset, flat.y);
        }
    }
}
