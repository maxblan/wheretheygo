using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using Game.UI;

namespace StationSuitabilityOverlay
{
    public sealed partial class StationSuitabilityUISystem : UISystemBase
    {
        private const string Group = "StationSuitabilityOverlay";

        protected override void OnCreate()
        {
            base.OnCreate();

            AddValueBinding("enabled", () => Mod.Settings?.Enabled ?? false);
            AddValueBinding("mode", () => (int)(Mod.Settings?.Mode ?? Setting.ModePreset.Bus));
            AddValueBinding("w1", () => Mod.Settings?.W1 ?? 1f);
            AddValueBinding("w2", () => Mod.Settings?.W2 ?? 1f);
            AddValueBinding("w3", () => Mod.Settings?.W3 ?? 1f);
            AddValueBinding("w4", () => Mod.Settings?.W4 ?? 1f);

            AddSettingTrigger<bool>("setEnabled", (settings, value) => settings.Enabled = value);
            AddSettingTrigger<int>("setMode", (settings, value) =>
                settings.ApplyPreset(value == (int)Setting.ModePreset.Metro ? Setting.ModePreset.Metro : Setting.ModePreset.Bus));
            AddSettingTrigger<float>("setW1", (settings, value) => settings.W1 = value);
            AddSettingTrigger<float>("setW2", (settings, value) => settings.W2 = value);
            AddSettingTrigger<float>("setW3", (settings, value) => settings.W3 = value);
            AddSettingTrigger<float>("setW4", (settings, value) => settings.W4 = value);

            AddBinding(new TriggerBinding(
                Group,
                "recalculate",
                () =>
                {
                    var overlaySystem = World.GetExistingSystemManaged<StationSuitabilityOverlaySystem>();
                    overlaySystem?.RequestRecompute();
                }));
        }

        private void AddValueBinding<T>(string name, Func<T> getter)
        {
            AddBinding(new GetterValueBinding<T>(
                Group,
                name,
                getter,
                ValueWriters.Create<T>(),
                EqualityComparer<T>.Default));
        }

        private void AddSettingTrigger<T>(string name, Action<Setting, T> apply)
        {
            AddBinding(new TriggerBinding<T>(
                Group,
                name,
                value =>
                {
                    var settings = Mod.Settings;
                    if (settings != null)
                    {
                        apply(settings, value);
                    }
                },
                ValueReaders.Create<T>()));
        }
    }
}
