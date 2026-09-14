using Game;
using Game.Input;
using Game.Rendering;
using Game.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace WhereTheyGo
{
    // Which band the pointer is over, so a band can say what it stands for.
    //
    // Deliberately NOT a Game.Tools.ToolBaseSystem. A tool owns the cursor: picking one
    // up would put the game into a mode, take the click away from whatever the player
    // was building with, and show a cursor of ours over the whole map. Pointing at a
    // band is not a mode. So the ray is computed from the camera directly —
    // ToolRaycastSystem does the same thing with the same public pieces — and nothing
    // about the player's input is intercepted.
    public sealed partial class BandPickSystem : GameSystemBase
    {
        // The arc is tested in this many pieces. More than the renderer draws: a hit
        // test that is coarser than the line it tests looks like a mis-aimed pointer.
        private const int PickSamples = 24;

        // How far outside its own edge a band may still be grabbed, in PIXELS.
        //
        // In pixels rather than in metres because the bands now fly: an arc is drawn
        // where it appears on screen, not where it lies on the map, so the only honest
        // question is how far the pointer is from the drawn line. It also fixes what
        // the old slack in metres got wrong at both ends of the zoom — forty metres is
        // half the screen from up close and invisible from far away.
        private const float PickSlackPixels = 14f;

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate.
        private WhereTheyGoSystem m_Overlay;
        private CameraUpdateSystem m_CameraSystem;
        private TerrainSystem m_TerrainSystem;
#pragma warning restore CS8618

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<WhereTheyGoSystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
        }

        protected override void OnUpdate()
        {
            Camera? camera = m_CameraSystem?.activeCamera;
            InputManager? input = InputManager.instance;
            if (!m_Overlay.AreBandsShown || m_Overlay.CurrentBandView.Drawn.Length == 0
                || camera == null || input is null || input.mouseOverUI || !input.mouseOnScreen)
            {
                WhereTheyGoSystem.SetHoveredBand(band: null);
                return;
            }

            // One matrix for the whole frame rather than a WorldToScreenPoint call per
            // sample per band: the same arithmetic, without crossing into the engine
            // some thousands of times while the pointer moves.
            float4x4 viewProjection = math.mul(camera.projectionMatrix, camera.worldToCameraMatrix);
            Vector2 pointer = input.mousePosition;
            TerrainHeightData heightData = m_TerrainSystem.GetHeightData(waitForPending: false);
            WhereTheyGoSystem.SetHoveredBand(NearestBand(ref heightData, viewProjection, camera.pixelWidth, camera.pixelHeight, pointer.x, pointer.y));
        }

        // The nearest band the player can actually see: the threshold, the purposes and
        // the hour all decide what is on screen, and pointing at something invisible
        // would be a lie.
        private Band? NearestBand(ref TerrainHeightData heightData, float4x4 viewProjection, float pixelWidth, float pixelHeight, float px, float py)
        {
            // The same view the renderer draws from, so what the pointer can find and
            // what the eye can see are one list rather than two agreeing by luck.
            BandView view = m_Overlay.CurrentBandView;
            Band? best = null;
            float bestDistance = float.MaxValue;
            // Lightest first, matching the draw order: where a thin band crosses a
            // thick one, the thin one is what the player is looking at.
            for (int i = view.Drawn.Length - 1; i >= 0; i--)
            {
                DrawnBand drawn = view.Drawn[i];
                Band band = drawn.Band;
                float footA = TerrainUtils.SampleHeight(ref heightData, new float3(band.Ax, 0f, band.Az));
                float footB = TerrainUtils.SampleHeight(ref heightData, new float3(band.Bx, 0f, band.Bz));
                float distanceSq = DistanceSqOnScreen(band, footA, footB, viewProjection, pixelWidth, pixelHeight, px, py);
                if (distanceSq >= bestDistance)
                {
                    continue;
                }

                // Half the band's own width on screen would be the exact answer; the
                // band is drawn in metres, so the slack alone stands in for it. A
                // thick band is easier to hit anyway because its arc is the same line.
                if (distanceSq <= PickSlackPixels * PickSlackPixels)
                {
                    bestDistance = distanceSq;
                    best = band;
                }
            }

            return best;
        }

        // How far the pointer is, in pixels, from the band's arc as it appears on
        // screen. Samples the same arc the renderer draws and walks its pieces.
        private static float DistanceSqOnScreen(
            Band band, float footA, float footB, float4x4 viewProjection,
            float pixelWidth, float pixelHeight, float px, float py)
        {
            float best = float.MaxValue;
            bool havePrevious = false;
            float lastX = 0f;
            float lastY = 0f;
            for (int i = 0; i <= PickSamples; i++)
            {
                BandGeometry.PointOnArc(
                    band.Ax, footA, band.Az, band.Bx, footB, band.Bz, i / (float)PickSamples,
                    out float x, out float y, out float z);
                if (!TryProject(viewProjection, pixelWidth, pixelHeight, x, y, z, out float sx, out float sy))
                {
                    // Behind the camera: the piece leading up to it cannot be measured
                    // either, so the chain starts again at the next point in front.
                    havePrevious = false;
                    continue;
                }

                if (havePrevious)
                {
                    float distance = BandGeometry.DistanceSqToSegment(lastX, lastY, sx, sy, px, py);
                    if (distance < best)
                    {
                        best = distance;
                    }
                }

                lastX = sx;
                lastY = sy;
                havePrevious = true;
            }

            return best;
        }

        // A world point in screen pixels, with the origin at the bottom left where the
        // input system puts the pointer. False when the point is behind the camera,
        // where the perspective divide turns round and would report a hit on the
        // opposite side of the screen.
        private static bool TryProject(
            float4x4 viewProjection, float pixelWidth, float pixelHeight,
            float x, float y, float z, out float sx, out float sy)
        {
            float4 clip = math.mul(viewProjection, new float4(x, y, z, 1f));
            if (clip.w <= 1e-4f)
            {
                sx = 0f;
                sy = 0f;
                return false;
            }

            sx = ((clip.x / clip.w) + 1f) * 0.5f * pixelWidth;
            sy = ((clip.y / clip.w) + 1f) * 0.5f * pixelHeight;
            return true;
        }
    }
}
