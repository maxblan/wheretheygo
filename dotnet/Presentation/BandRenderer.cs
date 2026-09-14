using System;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace WhereTheyGo
{
    // Draws the desire bands: one arc per band, flying from A to B over the city and
    // coloured by how much of its traffic the network already carries.
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
        // The arc is a chain of TUBES, not a curve primitive.
        //
        // The overlay has a curve (DrawCurve takes a Bezier4x3), and it cannot do
        // this. Its length is the length in the map plane, and a piece with no extent
        // there is dropped outright; and an unprojected curve is one flat quad whose
        // plane is fitted from the control points, a fit that falls back on a
        // horizontal plane for exactly the case where the bow is vertical. What does
        // work is CustomMeshType.Cylinder: a 64-sided tube whose axis is its local Y,
        // freely rotated, with `width` its RADIUS and `height` its full length. All of
        // them go out in one instanced draw call, so a chain costs less than the two
        // passes the flat band used to take. Both facts are in docs/game-facts.md.
        private const float ArcMetresPerSegment = 200f;

        // Enough pieces that the arc reads as a curve rather than as a folded rule:
        // a band turns through some seventy degrees between its two ends, so ten is
        // the floor however short it is.
        private const int MinArcSegments = 10;

        private const int MaxArcSegments = 20;

        // The tube has no end caps, so consecutive pieces would show a wedge of empty
        // air at every joint. Each piece is lengthened by this share of its own radius,
        // which closes the wedge by letting neighbours interpenetrate.
        private const float JointOverlapShareOfRadius = 0.5f;

        // Lifted off the ground by the same margin the route polylines used, so the
        // feet of an arc are not swallowed by the terrain they stand on.
        private const float TerrainOffset = 4f;

        // Near enough to opaque to read as one solid thing, near enough to transparent
        // that a band behind another is still there. Flow maps are drawn opaque almost
        // without exception (Jenny et al. 2016: 89 % of their sample); where two arcs
        // cross, depth now does the separating that a casing used to do.
        private const float BandOpacity = 0.88f;

        // While a line is selected: what it carries, and everything else.
        private const float HighlightOpacity = 1f;

        private const float DimmedOpacity = 0.16f;

        // The arrowhead's barbs, 35 degrees back from the tip either side, and how
        // thick they are drawn relative to the tube they sit on.
        private const float ArrowCos = 0.819f;

        private const float ArrowSin = 0.574f;

        private const float ArrowStrokeShare = 0.28f;

        private const float MinArrowStrokeMetres = 2.5f;

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state in which nothing works anyway.
        private OverlayRenderSystem m_OverlayRenderSystem;
        private RenderingSystem m_RenderingSystem;
        private TerrainSystem m_TerrainSystem;
        private WhereTheyGoSystem m_OverlaySystem;
#pragma warning restore CS8618

        // The sampled arc of the band being drawn, kept here rather than allocated
        // once per band per frame.
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

            // Heaviest first. It no longer decides what covers what — the tubes are
            // solid and depth sorts them, which is the whole point of drawing them in
            // the air — but the hit test walks the same list backwards, so the two
            // agree about which band is in front by construction.
            for (int i = 0; i < view.Drawn.Length; i++)
            {
                DrawnBand drawn = view.Drawn[i];
                Band band = drawn.Band;
                float opacity = !highlighting ? BandOpacity
                    : band.CarriesTarget ? HighlightOpacity
                    : DimmedOpacity;
                if (ReferenceEquals(band, hovered))
                {
                    opacity = HighlightOpacity;
                }

                // Only the two feet touch the ground now; everything between them
                // hangs in the air, so the terrain is asked twice per band instead of
                // once per piece of arc.
                float footA = GroundHeight(ref heightData, band.Ax, band.Az);
                float footB = GroundHeight(ref heightData, band.Bx, band.Bz);
                int segments = Sample(band, footA, footB);
                DrawBand(buffer, drawn, opacity, segments);
                DrawDirection(buffer, drawn, hour, purposes, opacity, footA, footB);
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

        private void DrawBand(OverlayRenderSystem.Buffer buffer, DrawnBand drawn, float opacity, int segments)
        {
            Band band = drawn.Band;
            BandGeometry.Colour(band.CarriedShare, out float r, out float g, out float b);
            BandGeometry.OutlineColour(band.CarriedShare, out float dr, out float dg, out float db);
            var fill = new Color(r, g, b, opacity);
            var casing = new Color(dr, dg, db, opacity);
            float width = BandView.WidthOf(drawn.WidthClass);
            float radius = width * 0.5f;

            float overlap = radius * JointOverlapShareOfRadius;
            for (int step = 0; step < segments; step++)
            {
                DrawTube(buffer, fill, m_Arc[step], m_Arc[step + 1], radius, overlap);
            }

            // A dot on the ground at each foot. Flows anchored at point symbols were
            // read with fewer errors and faster than flows floating between areas, and
            // an arc that starts in mid-air says nothing about where it starts.
            float dot = width * Assumptions.BandEndDotShareOfWidth;
            buffer.DrawCircle(casing, m_Arc[0], dot + (2f * Assumptions.BandOutlineMetres));
            buffer.DrawCircle(casing, m_Arc[segments], dot + (2f * Assumptions.BandOutlineMetres));
            buffer.DrawCircle(fill, m_Arc[0], dot);
            buffer.DrawCircle(fill, m_Arc[segments], dot);
        }

        // One piece of an arc, as a tube. The mesh's axis is its own local Y and its
        // `width` is a RADIUS, not a diameter — both read out of the code that builds
        // it in OverlayRenderSystem — so the rotation wanted here is the one that takes
        // straight up onto the piece's own direction.
        private static void DrawTube(OverlayRenderSystem.Buffer buffer, Color colour, float3 from, float3 to, float radius, float overlap)
        {
            float3 along = to - from;
            float length = math.length(along);
            if (length < 0.01f)
            {
                return;
            }

            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, along / length);
            buffer.DrawCustomMesh(colour, (from + to) * 0.5f, length + overlap, radius, OverlayRenderSystem.CustomMeshType.Cylinder, rotation);
        }

        // The band's arc, sampled into m_Arc between its two feet. Returns how many
        // straight pieces it came to, so m_Arc[0..segments] are the points.
        private int Sample(Band band, float footA, float footB)
        {
            int segments = math.clamp((int)(band.LengthMetres / ArcMetresPerSegment), MinArcSegments, MaxArcSegments);
            for (int step = 0; step <= segments; step++)
            {
                BandGeometry.PointOnArc(
                    band.Ax, footA, band.Az, band.Bx, footB, band.Bz, step / (float)segments,
                    out float x, out float y, out float z);
                m_Arc[step] = new float3(x, y, z);
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
        //
        // Two tubes rather than the game's own Arrow mesh. That mesh is a flat flag
        // standing in its local XY plane, and the only vanilla caller stands it upright
        // for the water tool; aimed along an arc it came out pointing across the band
        // and far too large. A chevron of two pieces is aimed and sized by arithmetic
        // that is right here in front of us.
        private static void DrawDirection(
            OverlayRenderSystem.Buffer buffer, DrawnBand drawn,
            int hour, int purposes, float opacity, float footA, float footB)
        {
            Band band = drawn.Band;
            int direction = band.DirectionAtHour(hour, purposes);
            if (direction == 0)
            {
                return;
            }

            // Set back from the end so the head sits ON the arc rather than over the
            // foot dot, and aimed along the arc's own tangent, which near the ends
            // climbs at better than thirty degrees.
            float t = direction > 0 ? 1f - Assumptions.BandArrowInsetShare : Assumptions.BandArrowInsetShare;
            BandGeometry.PointOnArc(band.Ax, footA, band.Az, band.Bx, footB, band.Bz, t, out float tipX, out float tipY, out float tipZ);
            BandGeometry.DirectionOnArc(band.Ax, footA, band.Az, band.Bx, footB, band.Bz, t, out float dx, out float dy, out float dz);
            if (direction < 0)
            {
                dx = -dx;
                dy = -dy;
                dz = -dz;
            }

            float width = BandView.WidthOf(drawn.WidthClass);
            float arm = width * Assumptions.BandArrowShareOfWidth;
            // The barbs run back along the arc at 35 degrees either side, and they open
            // out HORIZONTALLY: a chevron that opened in the arc's own vertical plane
            // would be edge-on from directly above, which is where this map is read.
            float3 back = new float3(-dx, -dy, -dz) * ArrowCos;
            float plan = math.sqrt((dx * dx) + (dz * dz));
            float3 side = plan > 1e-4f
                ? new float3(-dz / plan, 0f, dx / plan) * ArrowSin
                : new float3(0f, 0f, ArrowSin);

            // White rather than the band's own colour: the head has to read against the
            // band it sits on, and it is the one mark on this map that answers "which
            // way" rather than "how much" or "how well carried". Held back a little on
            // the cool end of the ramp, which is already light.
            var colour = new Color(1f, 1f, 1f, opacity * (0.55f + (0.45f * (1f - band.CarriedShare))));

            var tip = new float3(tipX, tipY, tipZ);
            float stroke = Math.Max(width * ArrowStrokeShare, MinArrowStrokeMetres) * 0.5f;
            DrawTube(buffer, colour, tip + ((back + side) * arm), tip, stroke, 0f);
            DrawTube(buffer, colour, tip + ((back - side) * arm), tip, stroke, 0f);
        }

        private static float GroundHeight(ref TerrainHeightData heightData, float x, float z)
        {
            return TerrainUtils.SampleHeight(ref heightData, new float3(x, 0f, z)) + TerrainOffset;
        }
    }
}
