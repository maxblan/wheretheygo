using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    [FileLocation(nameof(StationSuitabilityOverlay))]
    [SettingsUIGroupOrder(kPresetGroup, kWeightsGroup)]
    [SettingsUIShowGroupName(kPresetGroup, kWeightsGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kPresetGroup = "Preset";
        public const string kWeightsGroup = "Weights";

        public enum ModePreset
        {
            Bus = 0,
            Metro = 1,
        }

        private ModePreset m_Mode;
        private float m_W1;
        private float m_W2;
        private float m_W3;
        private float m_W4;

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(kSection, kPresetGroup)]
        public ModePreset Mode
        {
            get => m_Mode;
            set => m_Mode = value;
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kPresetGroup)]
        public bool ApplyPresetWeights
        {
            set => ApplyPreset(m_Mode);
        }

        [SettingsUISlider(min = 0f, max = 2f, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W1
        {
            get => m_W1;
            set => m_W1 = ClampWeight(value);
        }

        [SettingsUISlider(min = 0f, max = 2f, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W2
        {
            get => m_W2;
            set => m_W2 = ClampWeight(value);
        }

        [SettingsUISlider(min = 0f, max = 2f, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W3
        {
            get => m_W3;
            set => m_W3 = ClampWeight(value);
        }

        [SettingsUISlider(min = 0f, max = 2f, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W4
        {
            get => m_W4;
            set => m_W4 = ClampWeight(value);
        }

        public override void SetDefaults()
        {
            m_Mode = ModePreset.Bus;
            ApplyPreset(m_Mode);
        }

        public void ApplyPreset(ModePreset mode)
        {
            m_Mode = mode;
            switch (mode)
            {
                case ModePreset.Bus:
                    m_W1 = 1.0f;
                    m_W2 = 0.8f;
                    m_W3 = 1.2f;
                    m_W4 = 0.6f;
                    break;
                case ModePreset.Metro:
                    m_W1 = 0.9f;
                    m_W2 = 1.0f;
                    m_W3 = 1.4f;
                    m_W4 = 0.8f;
                    break;
            }
        }

        // Called once after settings are loaded from disk to sanitize persisted values.
        public void ClampAll()
        {
            m_Mode = m_Mode == ModePreset.Metro ? ModePreset.Metro : ModePreset.Bus;
            m_W1 = ClampWeight(m_W1);
            m_W2 = ClampWeight(m_W2);
            m_W3 = ClampWeight(m_W3);
            m_W4 = ClampWeight(m_W4);
        }

        public static float ClampWeight(float value)
        {
            if (value < 0f) return 0f;
            if (value > 2f) return 2f;
            return value;
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Station Suitability Overlay" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPresetGroup), "Preset" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kWeightsGroup), "Weights" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Mode)), "Mode preset" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Mode)), "Transit mode the overlay evaluates. Determines which existing stops count as coverage." },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Bus), "Bus" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Metro), "Metro" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyPresetWeights)), "Apply preset weights" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyPresetWeights)), "Reset the four weights below to the recommended values for the selected mode." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W1)), "Demand weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W1)), "How strongly residents within the 400 m catchment raise the score." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W2)), "Jobs weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W2)), "How strongly workplaces within the 400 m catchment raise the score." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W3)), "Existing coverage penalty" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W3)), "How strongly existing stops of the selected mode lower the score nearby." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W4)), "Accessibility weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W4)), "How strongly nearby road network density raises the score." },

                { "StationSuitabilityOverlay.Infomode", "Station Suitability" },
                { "Infoviews.INFOVIEW[StationSuitabilityOverlay]", "Station Suitability" },
                { "Infoviews.INFOVIEW_TOOLTIP[StationSuitabilityOverlay]", "Shows how suitable each location is for a new transit stop." },
                { "Infoviews.INFOMODE[StationSuitabilityOverlay]", "Station Suitability" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityOverlay]", "Green–yellow–red heatmap of station placement quality; the top 5% of tiles are fully opaque." },

                // The infoview panel composes gradient legend label keys as
                // Infoviews.LABEL[<labelId>].
                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.Low]", "Low" },
                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.Medium]", "Medium" },
                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.High]", "High" },
            };
        }

        public void Unload()
        {
        }
    }
}
