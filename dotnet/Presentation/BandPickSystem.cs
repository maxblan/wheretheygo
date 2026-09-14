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
        private const int PickSamples = 20;

        // How far outside its own width a band may still be grabbed, in metres. A thin
        // band is otherwise impossible to point at on a zoomed-out map.
        private const float PickSlackMetres = 40f;

        // Two iterations of ray-against-terrain. The first uses sea level, the second
        // the height found there, which is within a metre or two on anything but a
        // cliff — and a cliff is not where bands are read.
        private const int GroundIterations = 2;

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

            if (!TryGroundPoint(camera, out float px, out float pz))
            {
                WhereTheyGoSystem.SetHoveredBand(band: null);
                return;
            }

            WhereTheyGoSystem.SetHoveredBand(NearestBand(px, pz));
        }

        // Where the pointer meets the ground. The ray is intersected with a horizontal
        // plane, the terrain is sampled there, and the intersection is redone at that
        // height — which is what turns "somewhere along the line of sight" into "the
        // place under the cursor" on anything but a vertical face.
        private bool TryGroundPoint(Camera camera, out float x, out float z)
        {
            x = 0f;
            z = 0f;
            Ray ray = camera.ScreenPointToRay(InputManager.instance.mousePosition);
            // Looking along the horizon: no ground under the cursor to speak of.
            if (Mathf.Abs(ray.direction.y) < 1e-4f)
            {
                return false;
            }

            TerrainHeightData heightData = m_TerrainSystem.GetHeightData(waitForPending: false);
            float height = 0f;
            for (int i = 0; i < GroundIterations; i++)
            {
                float t = (height - ray.origin.y) / ray.direction.y;
                if (t <= 0f)
                {
                    return false;
                }

                Vector3 hit = ray.origin + (ray.direction * t);
                x = hit.x;
                z = hit.z;
                height = TerrainUtils.SampleHeight(ref heightData, new float3(x, 0f, z));
            }

            return true;
        }

        // The nearest band the player can actually see: the threshold, the purposes and
        // the hour all decide what is on screen, and pointing at something invisible
        // would be a lie.
        private Band? NearestBand(float px, float pz)
        {
            // The same view the renderer draws from, so what the pointer can find and
            // what the eye can see are one list rather than two agreeing by luck.
            BandView view = m_Overlay.CurrentBandView;
            Band? best = null;
            float bestDistance = float.MaxValue;
            // Lightest first, matching the draw order: where a thin band lies over a
            // thick one, the thin one is what the player is looking at.
            for (int i = view.Drawn.Length - 1; i >= 0; i--)
            {
                DrawnBand drawn = view.Drawn[i];
                Band band = drawn.Band;
                float reach = (BandView.WidthOf(drawn.WidthClass) * 0.5f) + PickSlackMetres;
                float distanceSq = BandGeometry.DistanceSqToArc(band.Ax, band.Az, band.Bx, band.Bz, px, pz, PickSamples);
                if (distanceSq <= reach * reach && distanceSq < bestDistance)
                {
                    bestDistance = distanceSq;
                    best = band;
                }
            }

            return best;
        }
    }
}
