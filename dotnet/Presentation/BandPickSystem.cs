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
            BandSet? bands = m_Overlay.Bands;
            Camera? camera = m_CameraSystem?.activeCamera;
            InputManager? input = InputManager.instance;
            if (!m_Overlay.AreBandsShown || bands is null || bands.Bands.Length == 0
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

            WhereTheyGoSystem.SetHoveredBand(NearestBand(bands, px, pz));
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
        private Band? NearestBand(BandSet bands, float px, float pz)
        {
            float threshold = m_Overlay.BandThresholdShare;
            int hour = m_Overlay.SelectedHour;
            int purposes = m_Overlay.PurposeFilter;
            Band? best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < bands.Bands.Length; i++)
            {
                Band band = bands.Bands[i];
                if ((band.PurposeMask & purposes) == 0)
                {
                    continue;
                }

                float weight = band.WeightAtHour(hour);
                if (!BandGeometry.IsVisible(weight, bands.HeaviestWeight, threshold))
                {
                    continue;
                }

                float reach = (BandGeometry.Width(weight, bands.HeaviestWeight) * 0.5f) + PickSlackMetres;
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
