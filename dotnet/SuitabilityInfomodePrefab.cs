using Game.Prefabs;
using Unity.Entities;
using UnityEngine;

namespace StationSuitabilityOverlay
{
    // The overlay layers the player can toggle in the infoview panel. Order is the
    // display order; Score stays first so it is the one activated by default.
    internal enum SuitabilityLayer
    {
        Score = 0,
        Sites = 1,
        Demand = 2,
        Jobs = 3,
        Coverage = 4,
        Access = 5,
        Future = 6,
        Interchange = 7,
        CrossCoverage = 8,
        TravelDemand = 9,
    }

    internal static class SuitabilityLayers
    {
        public const int Count = 10;

        // The terrain override overlay is a single RGBA texture, so only four
        // layers can be drawn at once. ToolSystem.Activate does NOT enforce this —
        // it hands out m_Index = colorGroup * 4 + (1-based active count) with no
        // bounds check, so a fifth activation yields an index that runs off the end
        // of our channels. The overlay system skips those itself.
        public const int MaxActiveLayers = 4;

        // Only the combined score is registered as an infomode. The per-term layers
        // below are still computed and normalised — they are just not offered in the
        // infoview menu, because ten rows of legend crowded the screen and the mod
        // now presents itself through its own panel. Re-adding one is a single line.
        public static SuitabilityLayer[] All =>
            new[]
            {
                SuitabilityLayer.Score,
            };

        public static string NameOf(SuitabilityLayer layer)
        {
            switch (layer)
            {
                case SuitabilityLayer.Sites: return "StationSuitabilitySites";
                case SuitabilityLayer.Demand: return "StationSuitabilityDemand";
                case SuitabilityLayer.Jobs: return "StationSuitabilityJobs";
                case SuitabilityLayer.Coverage: return "StationSuitabilityCoverage";
                case SuitabilityLayer.Access: return "StationSuitabilityAccess";
                case SuitabilityLayer.Future: return "StationSuitabilityFuture";
                case SuitabilityLayer.Interchange: return "StationSuitabilityInterchange";
                case SuitabilityLayer.CrossCoverage: return "StationSuitabilityCrossCoverage";
                case SuitabilityLayer.TravelDemand: return "StationSuitabilityTravelDemand";
                default: return "StationSuitabilityOverlay";
            }
        }

        // Each active infomode gets its own three gradient colours in its own
        // shader slot (ToolSystem.UpdateInfoviewColors writes them at m_Index * 3),
        // so the layers are visually distinguishable when several are on at once.
        // Alpha shapes the opacity ramp: low values barely tint, peaks are opaque.
        public static void ColorsOf(SuitabilityLayer layer, out Color low, out Color medium, out Color high)
        {
            switch (layer)
            {
                case SuitabilityLayer.Sites:
                    // Discrete recommended sites: no meaningful mid-range.
                    low = new Color(0.10f, 0.30f, 0.90f, 0.15f);
                    medium = new Color(0.30f, 0.70f, 1f, 0.75f);
                    high = new Color(0.85f, 0.95f, 1f, 1f);
                    break;
                case SuitabilityLayer.Demand:
                    low = new Color(0.10f, 0.20f, 0.45f, 0.15f);
                    medium = new Color(0.30f, 0.55f, 0.90f, 0.60f);
                    high = new Color(0.75f, 0.90f, 1f, 0.95f);
                    break;
                case SuitabilityLayer.Jobs:
                    low = new Color(0.35f, 0.20f, 0.45f, 0.15f);
                    medium = new Color(0.70f, 0.40f, 0.85f, 0.60f);
                    high = new Color(1f, 0.80f, 1f, 0.95f);
                    break;
                case SuitabilityLayer.Coverage:
                    // Coverage is a penalty, so it reads as a warning ramp.
                    low = new Color(0.25f, 0.25f, 0.25f, 0.12f);
                    medium = new Color(0.65f, 0.50f, 0.20f, 0.55f);
                    high = new Color(0.95f, 0.55f, 0.15f, 0.90f);
                    break;
                case SuitabilityLayer.Access:
                    low = new Color(0.15f, 0.35f, 0.35f, 0.12f);
                    medium = new Color(0.30f, 0.75f, 0.70f, 0.55f);
                    high = new Color(0.75f, 1f, 0.95f, 0.90f);
                    break;
                case SuitabilityLayer.Future:
                    low = new Color(0.20f, 0.40f, 0.20f, 0.12f);
                    medium = new Color(0.55f, 0.85f, 0.35f, 0.55f);
                    high = new Color(0.90f, 1f, 0.60f, 0.90f);
                    break;
                case SuitabilityLayer.Interchange:
                    // Transfer opportunity reads as a positive, high-value signal.
                    low = new Color(0.35f, 0.30f, 0.10f, 0.12f);
                    medium = new Color(0.85f, 0.70f, 0.20f, 0.60f);
                    high = new Color(1f, 0.95f, 0.55f, 0.95f);
                    break;
                case SuitabilityLayer.CrossCoverage:
                    // Duplication of another mode's reach: a muted warning ramp.
                    low = new Color(0.25f, 0.20f, 0.28f, 0.12f);
                    medium = new Color(0.55f, 0.40f, 0.60f, 0.55f);
                    high = new Color(0.80f, 0.60f, 0.85f, 0.90f);
                    break;
                case SuitabilityLayer.TravelDemand:
                    // Travel demand: where journeys want to happen, not where they
                    // are served. Deliberately unlike every other layer's ramp.
                    low = new Color(0.10f, 0.10f, 0.20f, 0.12f);
                    medium = new Color(0.45f, 0.30f, 0.85f, 0.60f);
                    high = new Color(1f, 1f, 1f, 0.95f);
                    break;
                default:
                    // The combined score keeps the original green/yellow/red ramp.
                    low = new Color(0.12f, 0.46f, 0.18f, 0.2f);
                    medium = new Color(0.94f, 0.84f, 0.25f, 0.65f);
                    high = new Color(0.85f, 0.22f, 0.12f, 0.95f);
                    break;
            }
        }
    }

    public struct SuitabilityInfomodeData : IComponentData
    {
    }

    public sealed class SuitabilityInfomodePrefab : GradientInfomodeBasePrefab
    {
        public const string LocaleKey = "StationSuitabilityOverlay.Infomode";

        public override string infomodeTypeLocaleKey => LocaleKey;

        // Color group 0 is the terrain heatmap group: it makes ToolSystem assign
        // InfomodeActive indices 1..4, which map to the four channels of the terrain
        // override overlay texture and the matching shader color slots.
        public override int GetColorGroup(out int secondaryGroup)
        {
            secondaryGroup = -1;
            return 0;
        }

        public override void GetPrefabComponents(System.Collections.Generic.HashSet<ComponentType> components)
        {
            if (components is null)
            {
                throw new System.ArgumentNullException(nameof(components));
            }

            base.GetPrefabComponents(components);
            _ = components.Add(ComponentType.ReadWrite<SuitabilityInfomodeData>());
        }
    }
}
