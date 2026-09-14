using Game.Prefabs;
using Unity.Entities;
using UnityEngine;

namespace TransitArchitect
{
    // The layers the player can toggle in the infoview panel.
    internal enum SuitabilityLayer
    {
        // Colours BUILDINGS by how far their door is from a served stop (author's
        // request 2026-09-06). It lives in the object colour group, not the terrain
        // group — see AccessInfomodePrefab.
        TransitAccess = 0,
    }

    internal static class SuitabilityLayers
    {
        public const int Count = 1;

        public static SuitabilityLayer[] All => new[] { SuitabilityLayer.TransitAccess };

        // Layers that colour OBJECTS rather than the terrain. They take an index in
        // the object colour group, so they never occupy one of the terrain channels.
        public static bool IsObjectLayer(SuitabilityLayer layer)
        {
            return layer == SuitabilityLayer.TransitAccess;
        }

        public static string NameOf(SuitabilityLayer layer)
        {
            switch (layer)
            {
                case SuitabilityLayer.TransitAccess: return "TransitArchitectTransitAccess";
                default: return "TransitArchitect";
            }
        }

        // Each active infomode gets its own three gradient colours in its own shader
        // slot (ToolSystem.UpdateInfoviewColors writes them at m_Index * 3).
        public static void ColorsOf(SuitabilityLayer layer, out Color low, out Color medium, out Color high)
        {
            switch (layer)
            {
                default:
                    // Walking time from a building to the nearest served stop: pale
                    // straw is a short walk, deep red-brown is at or beyond the horizon.
                    // ColorBrewer YlOrRd (author's request 2026-09-06): it runs
                    // monotonically from light to dark, so red-green and blue-yellow
                    // colour blindness — and a greyscale screenshot — still read it.
                    // Object colours are opaque; alpha here would make the building
                    // translucent rather than tinted.
                    low = new Color(1f, 0.97f, 0.75f, 1f);
                    medium = new Color(0.99f, 0.55f, 0.24f, 1f);
                    high = new Color(0.50f, 0f, 0.15f, 1f);
                    break;
            }
        }
    }

    public struct SuitabilityInfomodeData : IComponentData
    {
    }

    // Deliberately does NOT override GetColorGroup, so the game gives it an index in
    // the object colour group (ToolSystem.Activate: colorGroup * 4 + count).
    // BuildingAccessColorSystem writes that index into Game.Objects.Color, and the
    // object shader reads the three gradient colours from the matching slot.
    public sealed class AccessInfomodePrefab : GradientInfomodeBasePrefab
    {
        public const string LocaleKey = "TransitArchitect.Infomode";

        public override string infomodeTypeLocaleKey => LocaleKey;

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
