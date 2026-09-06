using Game.Prefabs;
using Unity.Entities;
using UnityEngine;

namespace TransitArchitect
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
        // Not a terrain layer: this one colours BUILDINGS by how far their door is
        // from a served stop (author's request 2026-09-06). It therefore lives in the
        // object colour group, not the terrain group — see AccessInfomodePrefab.
        TransitAccess = 10,
    }

    internal static class SuitabilityLayers
    {
        public const int Count = 11;

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
                SuitabilityLayer.TransitAccess,
            };

        // Layers that colour OBJECTS rather than the terrain. They take an index in
        // the object colour group, so they never occupy one of the four terrain
        // channels and must be filtered out of that bookkeeping.
        public static bool IsObjectLayer(SuitabilityLayer layer)
        {
            return layer == SuitabilityLayer.TransitAccess;
        }

        public static string NameOf(SuitabilityLayer layer)
        {
            switch (layer)
            {
                case SuitabilityLayer.Sites: return "TransitArchitectSites";
                case SuitabilityLayer.Demand: return "TransitArchitectDemand";
                case SuitabilityLayer.Jobs: return "TransitArchitectJobs";
                case SuitabilityLayer.Coverage: return "TransitArchitectCoverage";
                case SuitabilityLayer.Access: return "TransitArchitectAccess";
                case SuitabilityLayer.Future: return "TransitArchitectFuture";
                case SuitabilityLayer.Interchange: return "TransitArchitectInterchange";
                case SuitabilityLayer.CrossCoverage: return "TransitArchitectCrossCoverage";
                case SuitabilityLayer.TravelDemand: return "TransitArchitectTravelDemand";
                case SuitabilityLayer.TransitAccess: return "TransitArchitectTransitAccess";
                default: return "TransitArchitect";
            }
        }

        // Each active infomode gets its own three gradient colours in its own
        // shader slot (ToolSystem.UpdateInfoviewColors writes them at m_Index * 3),
        // so the layers are visually distinguishable when several are on at once.
        // Alpha shapes the opacity ramp: the low end of a TERRAIN layer is fully
        // transparent, so a tile that scores nothing shows the plain map, and only the
        // peaks are opaque. Anything else paints the whole countryside: a tile off the
        // walk network scores zero, and that is most of a map (2613 of 200704 tiles were
        // positive on Valmare).
        public static void ColorsOf(SuitabilityLayer layer, out Color low, out Color medium, out Color high)
        {
            switch (layer)
            {
                case SuitabilityLayer.Sites:
                    // Discrete recommended sites: no meaningful mid-range.
                    low = new Color(0.10f, 0.30f, 0.90f, 0f);
                    medium = new Color(0.30f, 0.70f, 1f, 0.75f);
                    high = new Color(0.85f, 0.95f, 1f, 1f);
                    break;
                case SuitabilityLayer.Demand:
                    low = new Color(0.10f, 0.20f, 0.45f, 0f);
                    medium = new Color(0.30f, 0.55f, 0.90f, 0.60f);
                    high = new Color(0.75f, 0.90f, 1f, 0.95f);
                    break;
                case SuitabilityLayer.Jobs:
                    low = new Color(0.35f, 0.20f, 0.45f, 0f);
                    medium = new Color(0.70f, 0.40f, 0.85f, 0.60f);
                    high = new Color(1f, 0.80f, 1f, 0.95f);
                    break;
                case SuitabilityLayer.Coverage:
                    // Coverage is a penalty, so it reads as a warning ramp.
                    low = new Color(0.25f, 0.25f, 0.25f, 0f);
                    medium = new Color(0.65f, 0.50f, 0.20f, 0.55f);
                    high = new Color(0.95f, 0.55f, 0.15f, 0.90f);
                    break;
                case SuitabilityLayer.Access:
                    low = new Color(0.15f, 0.35f, 0.35f, 0f);
                    medium = new Color(0.30f, 0.75f, 0.70f, 0.55f);
                    high = new Color(0.75f, 1f, 0.95f, 0.90f);
                    break;
                case SuitabilityLayer.Future:
                    low = new Color(0.20f, 0.40f, 0.20f, 0f);
                    medium = new Color(0.55f, 0.85f, 0.35f, 0.55f);
                    high = new Color(0.90f, 1f, 0.60f, 0.90f);
                    break;
                case SuitabilityLayer.Interchange:
                    // Transfer opportunity reads as a positive, high-value signal.
                    low = new Color(0.35f, 0.30f, 0.10f, 0f);
                    medium = new Color(0.85f, 0.70f, 0.20f, 0.60f);
                    high = new Color(1f, 0.95f, 0.55f, 0.95f);
                    break;
                case SuitabilityLayer.CrossCoverage:
                    // Duplication of another mode's reach: a muted warning ramp.
                    low = new Color(0.25f, 0.20f, 0.28f, 0f);
                    medium = new Color(0.55f, 0.40f, 0.60f, 0.55f);
                    high = new Color(0.80f, 0.60f, 0.85f, 0.90f);
                    break;
                case SuitabilityLayer.TravelDemand:
                    // Travel demand: where journeys want to happen, not where they
                    // are served. Deliberately unlike every other layer's ramp.
                    low = new Color(0.10f, 0.10f, 0.20f, 0f);
                    medium = new Color(0.45f, 0.30f, 0.85f, 0.60f);
                    high = new Color(1f, 1f, 1f, 0.95f);
                    break;
                case SuitabilityLayer.TransitAccess:
                    // Walking time from a building to the nearest served stop: pale
                    // straw is a short walk, deep red-brown is at or beyond the horizon.
                    // ColorBrewer YlOrRd, chosen with the map ramp below as a pair
                    // (author's request 2026-09-06): warm against that one's cool, and
                    // both run monotonically from light to dark, so red-green and
                    // blue-yellow colour blindness — and a greyscale screenshot — still
                    // read them. Object colours are opaque; alpha here would make the
                    // building translucent rather than tinted.
                    low = new Color(1f, 0.97f, 0.75f, 1f);
                    medium = new Color(0.99f, 0.55f, 0.24f, 1f);
                    high = new Color(0.50f, 0f, 0.15f, 1f);
                    break;
                default:
                    // The combined score: where a new stop would do the most good. Pale
                    // to deep blue (ColorBrewer Blues), the cool half of the pair with
                    // the building ramp above. It replaced a green-yellow-red ramp,
                    // which was unreadable next to the building colours for anyone with
                    // red-green colour blindness and, on a green map, for everyone else.
                    low = new Color(0.78f, 0.86f, 0.94f, 0f);
                    medium = new Color(0.26f, 0.57f, 0.78f, 0.62f);
                    high = new Color(0.03f, 0.19f, 0.42f, 0.95f);
                    break;
            }
        }
    }

    public struct SuitabilityInfomodeData : IComponentData
    {
    }

    // The object-coloured sibling of SuitabilityInfomodePrefab: identical except that
    // it does NOT override GetColorGroup, so the game gives it an index in the object
    // colour group (ToolSystem.Activate: colorGroup * 4 + count). BuildingAccessColorSystem
    // writes that index into Game.Objects.Color, and the object shader reads the three
    // gradient colours from the matching slot.
    public sealed class AccessInfomodePrefab : GradientInfomodeBasePrefab
    {
        public override string infomodeTypeLocaleKey => SuitabilityInfomodePrefab.LocaleKey;

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

    public sealed class SuitabilityInfomodePrefab : GradientInfomodeBasePrefab
    {
        public const string LocaleKey = "TransitArchitect.Infomode";

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
