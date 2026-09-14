using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace WhereTheyGo
{
    // Draws the desire bands: one flat arc per band, over the terrain, coloured by how
    // much of its traffic the network already carries.
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

        // Enough to read against the city, little enough to read the city through it.
        private const float BandOpacity = 0.55f;

        private const int MaxDots = 6;

        private const float DotOpacity = 0.7f;

        private const float DotShareOfWidth = 0.55f;

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state in which nothing works anyway.
        private OverlayRenderSystem m_OverlayRenderSystem;
        private RenderingSystem m_RenderingSystem;
        private TerrainSystem m_TerrainSystem;
        private WhereTheyGoSystem m_OverlaySystem;
#pragma warning restore CS8618

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

            BandSet? bands = m_OverlaySystem.Bands;
            if (bands is null || bands.Bands.Length == 0 || bands.HeaviestWeight <= 0f)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out Unity.Jobs.JobHandle dependencies);
            dependencies.Complete();
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData(waitForPending: false);

            float threshold = m_OverlaySystem.BandThresholdShare;
            int hour = m_OverlaySystem.SelectedHour;
            int purposes = m_OverlaySystem.PurposeFilter;
            // Lightest first, so the city's real corridors end up ON TOP of the hair
            // rather than under it: the bands come out of the bundling heaviest first.
            for (int i = bands.Bands.Length - 1; i >= 0; i--)
            {
                Band band = bands.Bands[i];
                if ((band.PurposeMask & purposes) == 0)
                {
                    continue;
                }

                // What the band weighs right now: the whole day, or the hour the
                // player has the slider on.
                float weight = band.WeightAtHour(hour);
                if (!BandGeometry.IsVisible(weight, bands.HeaviestWeight, threshold))
                {
                    continue;
                }

                DrawBand(buffer, ref heightData, band, weight, bands.HeaviestWeight);
                DrawDirection(buffer, ref heightData, band, hour, weight, bands.HeaviestWeight);
            }

            // The buffer was written on the main thread with the prior writers already
            // completed, so there is no new job handle to register.
        }

        private static void DrawBand(OverlayRenderSystem.Buffer buffer, ref TerrainHeightData heightData, Band band, float weight, float heaviest)
        {
            BandGeometry.Colour(band.CarriedShare, out float r, out float g, out float b);
            var colour = new Color(r, g, b, BandOpacity);
            float width = BandGeometry.Width(weight, heaviest);

            int segments = math.clamp((int)(band.LengthMetres / ArcMetresPerSegment), MinArcSegments, MaxArcSegments);
            float3 previous = Ground(ref heightData, band.Ax, band.Az);
            for (int step = 1; step <= segments; step++)
            {
                BandGeometry.PointOnArc(band.Ax, band.Az, band.Bx, band.Bz, step / (float)segments, out float x, out float z);
                float3 next = Ground(ref heightData, x, z);
                buffer.DrawLine(colour, new Line3.Segment(previous, next), width);
                previous = next;
            }
        }

        // Which way the band's traffic runs at the chosen hour, as a few dots drifting
        // along it. Dots rather than a dashed line with a moving phase: the overlay
        // buffer's dashes have no phase to move (DrawDashedLine takes lengths only),
        // and an arrow head at one end would be a claim about the whole band rather
        // than about this hour.
        private void DrawDirection(OverlayRenderSystem.Buffer buffer, ref TerrainHeightData heightData, Band band, int hour, float weight, float heaviest)
        {
            int direction = band.DirectionAtHour(hour);
            if (direction == 0)
            {
                return;
            }

            float length = band.LengthMetres;
            int dots = math.clamp((int)(length / Assumptions.BandDotSpacingMetres), 1, MaxDots);
            float width = BandGeometry.Width(weight, heaviest);
            // Bright enough to see against the band it rides on, and never wider.
            var colour = new Color(1f, 1f, 1f, DotOpacity);
            float phase = Daytime.Frac(UnityEngine.Time.realtimeSinceStartup / Assumptions.BandDotSeconds);
            for (int i = 0; i < dots; i++)
            {
                float t = Daytime.Frac(phase + (i / (float)dots));
                if (direction < 0)
                {
                    t = 1f - t;
                }

                BandGeometry.PointOnArc(band.Ax, band.Az, band.Bx, band.Bz, t, out float x, out float z);
                buffer.DrawCircle(colour, Ground(ref heightData, x, z), width * DotShareOfWidth);
            }
        }

        private static float3 Ground(ref TerrainHeightData heightData, float x, float z)
        {
            var flat = new float3(x, 0f, z);
            return new float3(x, TerrainUtils.SampleHeight(ref heightData, flat) + TerrainOffset, z);
        }
    }
}
