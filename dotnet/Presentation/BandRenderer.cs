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
        // passes the flat band used to take. Both facts are in docs/game-facts.md; the
        // numbers - pieces, overlap, opacities, the arrowhead - are Assumptions'.
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
        private readonly float3[] m_Arc = new float3[Assumptions.BandMaxArcSegments + 1];

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

            int hour = WhereTheyGoSystem.SelectedHour;
            int purposes = WhereTheyGoSystem.PurposeFilter;
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
                float opacity = !highlighting ? Assumptions.BandOpacity
                    : band.CarriesTarget ? Assumptions.BandHighlightOpacity
                    : Assumptions.BandDimmedOpacity;
                if (ReferenceEquals(band, hovered))
                {
                    opacity = Assumptions.BandHighlightOpacity;
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

            float overlap = radius * Assumptions.BandJointOverlapShareOfRadius;
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
            int segments = math.clamp((int)(band.LengthMetres / Assumptions.BandArcMetresPerSegment), Assumptions.BandMinArcSegments, Assumptions.BandMaxArcSegments);
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
            float3 back = new float3(-dx, -dy, -dz) * Assumptions.BandArrowCos;
            float plan = math.sqrt((dx * dx) + (dz * dz));
            float3 side = plan > 1e-4f
                ? new float3(-dz / plan, 0f, dx / plan) * Assumptions.BandArrowSin
                : new float3(0f, 0f, Assumptions.BandArrowSin);

            // White rather than the band's own colour: the head has to read against the
            // band it sits on, and it is the one mark on this map that answers "which
            // way" rather than "how much" or "how well carried". Held back a little on
            // the cool end of the ramp, which is already light.
            float arrowOpacity = opacity * (Assumptions.BandArrowBaseOpacity + (Assumptions.BandArrowOpacityLift * (1f - band.CarriedShare)));
            var colour = new Color(1f, 1f, 1f, arrowOpacity);

            var tip = new float3(tipX, tipY, tipZ);
            float stroke = Math.Max(width * Assumptions.BandArrowStrokeShare, Assumptions.BandMinArrowStrokeMetres) * 0.5f;
            DrawTube(buffer, colour, tip + ((back + side) * arm), tip, stroke, 0f);
            DrawTube(buffer, colour, tip + ((back - side) * arm), tip, stroke, 0f);
        }

        private static float GroundHeight(ref TerrainHeightData heightData, float x, float z)
        {
            return TerrainUtils.SampleHeight(ref heightData, new float3(x, 0f, z)) + Assumptions.BandTerrainOffsetMetres;
        }
    }
}
