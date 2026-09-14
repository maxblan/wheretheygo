using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace WhereTheyGo
{
    // Draws the desire bands: one flat arc per band, over the terrain, coloured by how
    // much of its traffic the network already carries and as wide as its width class.
    //
    // Separate from the overlay system because of phase ordering. OverlayRenderSystem
    // drains and clears its buffer during SystemUpdatePhase.Rendering; the overlay
    // system lives in PreCulling, which runs later, so it is the wrong place to draw
    // from. This sits in Rendering alongside the vanilla overlay producers.
    //
    // Ordering within a phase follows registration order, and mods register after the
    // game does, so these draws are most likely consumed on the FOLLOWING frame. That
    // is invisible here: the bands only change when the demand pipeline reruns.
    public sealed partial class BandRenderer : GameSystemBase
    {
        // The arc is sampled into straight pieces. A curve primitive exists (DrawCurve
        // takes a Bezier4x3), but it takes ONE height for the whole curve, and a band
        // crosses hills; sampling lets every piece sit on the ground under it.
        //
        // Sampled by LENGTH rather than at a fixed count: four hundred bands at
        // sixteen pieces each is six thousand draw calls a frame, and a 400 m band
        // does not need sixteen.
        private const float ArcMetresPerSegment = 200f;

        private const int MinArcSegments = 3;

        private const int MaxArcSegments = 14;

        // Lifted off the ground by the same margin the route polylines used, so a band
        // is not swallowed by the terrain it follows.
        private const float TerrainOffset = 4f;

        // Near enough to opaque to read as one shape where bands cross, near enough to
        // transparent to see the street underneath. Flow maps are drawn opaque almost
        // without exception (Jenny et al. 2016: 89 % of their sample); the casing does
        // the separating, so the fill no longer has to be see-through to be legible.
        private const float BandOpacity = 0.88f;

        // While a line is selected: what it carries, and everything else.
        private const float HighlightOpacity = 1f;

        private const float DimmedOpacity = 0.16f;

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state in which nothing works anyway.
        private OverlayRenderSystem m_OverlayRenderSystem;
        private RenderingSystem m_RenderingSystem;
        private TerrainSystem m_TerrainSystem;
        private WhereTheyGoSystem m_OverlaySystem;
#pragma warning restore CS8618

        // The sampled arc of the band being drawn. Kept here rather than allocated per
        // band because both passes over it — casing then fill — want the same points,
        // and sampling the terrain twice for the same metre is wasted work.
        private readonly float3[] m_Arc = new float3[MaxArcSegments + 1];

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_RenderingSystem = World.GetOrCreateSystemManaged<RenderingSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_OverlaySystem = World.GetOrCreateSystemManaged<WhereTheyGoSystem>();
        }

        protected override void OnUpdate()
        {
            if (m_RenderingSystem is not null && m_RenderingSystem.hideOverlay)
            {
                return;
            }

            // The bands come and go with their own infomode, which is ticked by
            // default when the infoview opens.
            if (!m_OverlaySystem.AreBandsShown)
            {
                return;
            }

            BandView view = m_OverlaySystem.CurrentBandView;
            if (view.Drawn.Length == 0)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out Unity.Jobs.JobHandle dependencies);
            dependencies.Complete();
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData(waitForPending: false);

            int hour = m_OverlaySystem.SelectedHour;
            int purposes = m_OverlaySystem.PurposeFilter;
            // With a line selected, the bands it carries stay as they are and every
            // other band steps back. Without one, nothing is dimmed.
            bool highlighting = AnyBandCarriesTheSelectedLine(view);
            Band? hovered = WhereTheyGoSystem.HoveredBand;

            // HEAVIEST FIRST, so the light bands end up on top of the heavy ones.
            // Cartographic practice since Dent: a thin flow hidden under a thick one
            // is gone, while a thin flow crossing a thick one costs the thick one
            // nothing — it is still the widest thing on the map.
            for (int i = 0; i < view.Drawn.Length; i++)
            {
                DrawnBand drawn = view.Drawn[i];
                float opacity = !highlighting ? BandOpacity
                    : drawn.Band.CarriesTarget ? HighlightOpacity
                    : DimmedOpacity;
                if (ReferenceEquals(drawn.Band, hovered))
                {
                    opacity = HighlightOpacity;
                }

                DrawBand(buffer, ref heightData, drawn, opacity);
                DrawDirection(buffer, ref heightData, drawn, hour, purposes, opacity);
            }

            // The buffer was written on the main thread with the prior writers already
            // completed, so there is no new job handle to register.
        }

        // Whether any band carries the line the player has selected. Asked once per
        // frame rather than per band: a line whose bands all fell under the threshold
        // must not dim the whole map for nothing.
        private static bool AnyBandCarriesTheSelectedLine(BandView view)
        {
            for (int i = 0; i < view.Drawn.Length; i++)
            {
                if (view.Drawn[i].Band.CarriesTarget)
                {
                    return true;
                }
            }

            return false;
        }

        private void DrawBand(OverlayRenderSystem.Buffer buffer, ref TerrainHeightData heightData, DrawnBand drawn, float opacity)
        {
            Band band = drawn.Band;
            BandGeometry.Colour(band.CarriedShare, out float r, out float g, out float b);
            BandGeometry.OutlineColour(band.CarriedShare, out float dr, out float dg, out float db);
            var fill = new Color(r, g, b, opacity);
            var casing = new Color(dr, dg, db, opacity);
            float width = BandView.WidthOf(drawn.WidthClass);
            int segments = Sample(ref heightData, band);

            // Two passes rather than DrawLine's own outline argument: the arc is drawn
            // as a chain of straight pieces, and an outline per piece would draw a
            // dark seam across every joint. Casing first for the whole band, fill over
            // it, and the seams land under the band's own colour.
            float casingWidth = width + (2f * Assumptions.BandOutlineMetres);
            for (int step = 0; step < segments; step++)
            {
                buffer.DrawLine(casing, new Line3.Segment(m_Arc[step], m_Arc[step + 1]), casingWidth);
            }

            for (int step = 0; step < segments; step++)
            {
                buffer.DrawLine(fill, new Line3.Segment(m_Arc[step], m_Arc[step + 1]), width);
            }

            // A dot at each end. Flows anchored at point symbols were read with fewer
            // errors and faster than flows floating between areas, and our ends are
            // the weighted middle of a district with nothing to mark them.
            float dot = width * Assumptions.BandEndDotShareOfWidth;
            buffer.DrawCircle(casing, m_Arc[0], dot + (2f * Assumptions.BandOutlineMetres));
            buffer.DrawCircle(casing, m_Arc[segments], dot + (2f * Assumptions.BandOutlineMetres));
            buffer.DrawCircle(fill, m_Arc[0], dot);
            buffer.DrawCircle(fill, m_Arc[segments], dot);
        }

        // The band's arc, sampled onto the ground into m_Arc. Returns how many straight
        // pieces it came to, so m_Arc[0..segments] are the points.
        private int Sample(ref TerrainHeightData heightData, Band band)
        {
            int segments = math.clamp((int)(band.LengthMetres / ArcMetresPerSegment), MinArcSegments, MaxArcSegments);
            m_Arc[0] = Ground(ref heightData, band.Ax, band.Az);
            for (int step = 1; step <= segments; step++)
            {
                BandGeometry.PointOnArc(band.Ax, band.Az, band.Bx, band.Bz, step / (float)segments, out float x, out float z);
                m_Arc[step] = Ground(ref heightData, x, z);
            }

            return segments;
        }

        // Which way the band's traffic runs at the chosen hour, as one arrowhead near
        // the end it is running towards.
        //
        // An arrowhead rather than the travelling dots this used to draw: of the flow
        // maps that show direction at all, every single one uses arrowheads, and in the
        // user study behind that count arrowheads beat every alternative tested. They
        // also hold still, which the product plan asks of everything on this map.
        private void DrawDirection(
            OverlayRenderSystem.Buffer buffer, ref TerrainHeightData heightData,
            DrawnBand drawn, int hour, int purposes, float opacity)
        {
            Band band = drawn.Band;
            int direction = band.DirectionAtHour(hour, purposes);
            if (direction == 0)
            {
                return;
            }

            // Set back from the end so the head sits ON the band rather than over the
            // end dot, and pointed along the arc's own tangent — on a bowed band the
            // straight A-to-B heading is visibly wrong near the ends.
            float t = direction > 0 ? 1f - Assumptions.BandArrowInsetShare : Assumptions.BandArrowInsetShare;
            BandGeometry.PointOnArc(band.Ax, band.Az, band.Bx, band.Bz, t, out float x, out float z);
            BandGeometry.DirectionOnArc(band.Ax, band.Az, band.Bx, band.Bz, t, out float dx, out float dz);
            if (direction < 0)
            {
                dx = -dx;
                dz = -dz;
            }

            float size = BandView.WidthOf(drawn.WidthClass) * Assumptions.BandArrowShareOfWidth;
            float3 position = Ground(ref heightData, x, z);
            BandGeometry.OutlineColour(band.CarriedShare, out float dr, out float dg, out float db);
            var colour = new Color(dr, dg, db, opacity);
            var rotation = Quaternion.LookRotation(new Vector3(dx, 0f, dz), Vector3.up);
            // Drawn twice, once with the height negated: the mesh is one-sided, and
            // this is how the game's own GuideLinesSystem makes it readable either way.
            buffer.DrawCustomMesh(colour, position, size, size, OverlayRenderSystem.CustomMeshType.Arrow, rotation);
            buffer.DrawCustomMesh(colour, position, 0f - size, size, OverlayRenderSystem.CustomMeshType.Arrow, rotation);
        }

        private static float3 Ground(ref TerrainHeightData heightData, float x, float z)
        {
            var flat = new float3(x, 0f, z);
            return new float3(x, TerrainUtils.SampleHeight(ref heightData, flat) + TerrainOffset, z);
        }
    }
}
