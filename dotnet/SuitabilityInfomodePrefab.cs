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

        public override void GetPrefabComponents(System.Collections.Generic.HashSet<ComponentType> components)
        {
            base.GetPrefabComponents(components);
            components.Add(ComponentType.ReadWrite<SuitabilityInfomodeData>());
        }
    }
}
