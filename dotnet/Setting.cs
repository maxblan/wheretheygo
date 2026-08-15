using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    [FileLocation(nameof(StationSuitabilityOverlay))]
    [SettingsUIGroupOrder(kPresetGroup, kWeightsGroup, kTuningGroup)]
    [SettingsUIShowGroupName(kPresetGroup, kWeightsGroup, kTuningGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kPresetGroup = "Preset";
        public const string kWeightsGroup = "Weights";
        public const string kTuningGroup = "Tuning";

        // Single source of truth for slider ranges: the UI attributes, the
        // property setters and ClampAll all reference these.
        public const float kWeightMin = 0f;
        public const float kWeightMax = 2f;
        public const int kCatchmentMin = 150;
        public const int kCatchmentMax = 1000;
        public const int kAccessMin = 50;
        public const int kAccessMax = 300;
        public const int kHighlightMin = 1;
        public const int kHighlightMax = 20;
        public const int kHighlightDefault = 5;

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
        private int m_CatchmentRadius;
        private int m_AccessRadius;
        private int m_HighlightShare;

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

        // The W1..W4 property names are the persistence keys of already-shipped
        // settings files; renaming them would silently drop users' saved weights.

        [SettingsUISlider(min = kWeightMin, max = kWeightMax, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W1
        {
            get => m_W1;
            set => m_W1 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightMin, max = kWeightMax, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W2
        {
            get => m_W2;
            set => m_W2 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightMin, max = kWeightMax, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W3
        {
            get => m_W3;
            set => m_W3 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightMin, max = kWeightMax, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W4
        {
            get => m_W4;
            set => m_W4 = ClampWeight(value);
        }

        [SettingsUISlider(min = kCatchmentMin, max = kCatchmentMax, step = 25, scalarMultiplier = 1, unit = Unit.kLength)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int CatchmentRadius
        {
            get => m_CatchmentRadius;
            set => m_CatchmentRadius = ClampInt(value, kCatchmentMin, kCatchmentMax);
        }

        [SettingsUISlider(min = kAccessMin, max = kAccessMax, step = 10, scalarMultiplier = 1, unit = Unit.kLength)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int AccessRadius
        {
            get => m_AccessRadius;
            set => m_AccessRadius = ClampInt(value, kAccessMin, kAccessMax);
        }

        [SettingsUISlider(min = kHighlightMin, max = kHighlightMax, step = 1, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int HighlightShare
        {
            get => m_HighlightShare;
            set => m_HighlightShare = ClampInt(value, kHighlightMin, kHighlightMax);
        }

        public override void SetDefaults()
        {
            m_Mode = ModePreset.Bus;
            m_HighlightShare = kHighlightDefault;
            ApplyPreset(m_Mode);
        }

        public void ApplyPreset(ModePreset mode)
        {
            m_Mode = mode;
            (m_CatchmentRadius, m_AccessRadius) = PresetRadii(mode);
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

        private static (int catchment, int access) PresetRadii(ModePreset mode)
        {
            return mode == ModePreset.Metro ? (600, 150) : (350, 120);
        }

        // Called before LoadSettings: zeroing the tuning fields lets ClampAll tell
        // whether the loaded file actually contained them (releases before 1.1
        // didn't persist tuning keys, so nothing overwrites the zeros).
        public void MarkTuningUnset()
        {
            m_CatchmentRadius = 0;
            m_AccessRadius = 0;
            m_HighlightShare = 0;
        }

        // Called once after settings are loaded from disk to sanitize persisted
        // values. Tuning fields still zero were absent from the file; they get the
        // preset defaults for the LOADED mode, without touching custom weights.
        public void ClampAll()
        {
            m_Mode = m_Mode == ModePreset.Metro ? ModePreset.Metro : ModePreset.Bus;
            m_W1 = ClampWeight(m_W1);
            m_W2 = ClampWeight(m_W2);
            m_W3 = ClampWeight(m_W3);
            m_W4 = ClampWeight(m_W4);
            (int catchment, int access) = PresetRadii(m_Mode);
            m_CatchmentRadius = m_CatchmentRadius == 0 ? catchment : ClampInt(m_CatchmentRadius, kCatchmentMin, kCatchmentMax);
            m_AccessRadius = m_AccessRadius == 0 ? access : ClampInt(m_AccessRadius, kAccessMin, kAccessMax);
            m_HighlightShare = m_HighlightShare == 0 ? kHighlightDefault : ClampInt(m_HighlightShare, kHighlightMin, kHighlightMax);
        }

        // The game targets .NET Framework, which has no Math.Clamp.
        private static int ClampInt(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static float ClampWeight(float value)
        {
            return value < kWeightMin ? kWeightMin : (value > kWeightMax ? kWeightMax : value);
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
                { m_Setting.GetOptionGroupLocaleID(Setting.kTuningGroup), "Tuning" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Mode)), "Mode preset" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Mode)), "Transit mode the overlay evaluates. Determines which existing stops count as coverage." },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Bus), "Bus" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Metro), "Metro" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyPresetWeights)), "Apply preset weights" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyPresetWeights)), "Reset the weights and radii below to the recommended values for the selected mode." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W1)), "Demand weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W1)), "How strongly residents within the catchment raise the score." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W2)), "Jobs weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W2)), "How strongly workplaces within the catchment raise the score." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W3)), "Existing coverage penalty" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W3)), "How strongly existing stops of the selected mode lower the score nearby." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W4)), "Accessibility weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W4)), "How strongly nearby road network density raises the score." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CatchmentRadius)), "Catchment radius" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CatchmentRadius)), "Walking distance a stop serves. Residents, jobs and existing stops within this radius affect the score. Typical: 300-400 m for bus, 600-800 m for metro." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AccessRadius)), "Road access radius" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AccessRadius)), "How close the road network must be to count towards accessibility." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HighlightShare)), "Highlight share" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.HighlightShare)), "Share of the best tiles shown at the top of the gradient. Lower values highlight only the very best spots." },

                { "StationSuitabilityOverlay.Infomode", "Station Suitability" },
                { "Infoviews.INFOVIEW[StationSuitabilityOverlay]", "Station Suitability" },
                { "Infoviews.INFOVIEW_TOOLTIP[StationSuitabilityOverlay]", "Shows how suitable each location is for a new transit stop." },
                { "Infoviews.INFOMODE[StationSuitabilityOverlay]", "Station Suitability" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityOverlay]", "Green–yellow–red heatmap of station placement quality. The best tiles reach the top of the gradient; how many counts as “best” is the Highlight share option." },

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
