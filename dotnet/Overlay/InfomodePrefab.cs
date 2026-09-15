using Game.Prefabs;
using Unity.Entities;
using UnityEngine;

namespace WhereTheyGo
{
    // The layers the player can toggle in the infoview panel.
    internal enum OverlayLayer
    {
        // The desire bands: where the city travels, and how much of it the network
        // carries. Drawn by BandRenderer through the overlay buffer rather than as a
        // colour on anything, so it takes no colour-group index of its own — it is an
        // infomode purely so the player can switch it off beside the other one.
        DesireBands = 0,

        // Colours BUILDINGS by how far their door is from a served stop (author's
        // request 2026-09-06). It lives in the object colour group, not the terrain
        // group — see AccessInfomodePrefab.
        TransitAccess = 1,
    }

    internal static class OverlayLayers
    {
        public const int Count = 2;

        public static OverlayLayer[] All => new[] { OverlayLayer.DesireBands, OverlayLayer.TransitAccess };

        // Layers that colour OBJECTS rather than the terrain. They take an index in
        // the object colour group, so they never occupy one of the terrain channels.
        public static bool IsObjectLayer(OverlayLayer layer)
        {
            return layer == OverlayLayer.TransitAccess;
        }

        public static string NameOf(OverlayLayer layer)
        {
            switch (layer)
            {
                case OverlayLayer.DesireBands: return "WhereTheyGoDesireBands";
                case OverlayLayer.TransitAccess: return "WhereTheyGoTransitAccess";
                default: return "WhereTheyGo";
            }
        }

        // Each active infomode gets its own three gradient colours in its own shader
        // slot (ToolSystem.UpdateInfoviewColors writes them at m_Index * 3).
        public static void ColorsOf(OverlayLayer layer, out Color low, out Color medium, out Color high)
        {
            switch (layer)
            {
                case OverlayLayer.DesireBands:
                    // The bands paint themselves (BandGeometry.Colour); this is the
                    // legend's ramp, and it is the same two ends: warm where nobody
                    // rides, cool where everybody does.
                    BandGeometry.Colour(0f, out float lowR, out float lowG, out float lowB);
                    BandGeometry.Colour(0.5f, out float midR, out float midG, out float midB);
                    BandGeometry.Colour(1f, out float highR, out float highG, out float highB);
                    low = new Color(lowR, lowG, lowB, 1f);
                    medium = new Color(midR, midG, midB, 1f);
                    high = new Color(highR, highG, highB, 1f);
                    break;
                default:
                    // Walking time from a building to the nearest served stop
                    // (Assumptions.WalkColour*). Object colours are opaque; alpha here
                    // would make the building translucent rather than tinted.
                    low = Opaque(Assumptions.WalkColourNear);
                    medium = Opaque(Assumptions.WalkColourMid);
                    high = Opaque(Assumptions.WalkColourFar);
                    break;
            }
        }

        // An RGB triple from Assumptions as the game's colour, fully opaque.
        public static Color Opaque(float[] rgb)
        {
            return new Color(rgb[0], rgb[1], rgb[2], 1f);
        }
    }

    public struct WhereTheyGoInfomodeData : IComponentData
    {
    }

    // Deliberately does NOT override GetColorGroup, so the game gives it an index in
    // the object colour group (ToolSystem.Activate: colorGroup * 4 + count).
    // BuildingAccessColorSystem writes that index into Game.Objects.Color, and the
    // object shader reads the three gradient colours from the matching slot.
    public sealed class AccessInfomodePrefab : GradientInfomodeBasePrefab
    {
        public const string LocaleKey = "WhereTheyGo.Infomode";

        public override string infomodeTypeLocaleKey => LocaleKey;

        public override void GetPrefabComponents(System.Collections.Generic.HashSet<ComponentType> components)
        {
            if (components is null)
            {
                throw new System.ArgumentNullException(nameof(components));
            }

            base.GetPrefabComponents(components);
            _ = components.Add(ComponentType.ReadWrite<WhereTheyGoInfomodeData>());
        }
    }
}
