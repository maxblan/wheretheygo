using Game.Prefabs;
using Unity.Entities;

namespace StationSuitabilityOverlay
{
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
            base.GetPrefabComponents(components);
            components.Add(ComponentType.ReadWrite<SuitabilityInfomodeData>());
        }
    }
}
