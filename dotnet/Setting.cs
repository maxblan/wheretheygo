using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    [FileLocation(nameof(StationSuitabilityOverlay))]
    [SettingsUIGroupOrder(kPresetGroup, kWeightsGroup, kTuningGroup, kCalibrationGroup)]
    [SettingsUIShowGroupName(kPresetGroup, kWeightsGroup, kTuningGroup, kCalibrationGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kPresetGroup = "Preset";
        public const string kWeightsGroup = "Weights";
        public const string kTuningGroup = "Tuning";
        public const string kCalibrationGroup = "Calibration";

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
        public const int kSlopeMin = 3;
        public const int kSlopeMax = 45;
        public const int kSlopeDefault = 15;
        public const int kSiteCountMin = 1;
        public const int kSiteCountMax = 20;
        public const int kSiteCountDefault = 8;

        public enum ModePreset
        {
            // Bus and Metro keep their original numeric values so settings files
            // written by earlier versions still resolve to the same mode.
            Bus = 0,
            Metro = 1,
            Tram = 2,
            Train = 3,
            Ferry = 4,
        }

        private ModePreset m_Mode;
        private float m_W1;
        private float m_W2;
        private float m_W3;
        private float m_W4;
        private float m_W5;
        private int m_CatchmentRadius;
        private int m_AccessRadius;
        private int m_HighlightShare;
        private int m_MaxSlope;
        private int m_SiteCount;
        private string m_RidershipData = string.Empty;

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

        [SettingsUISlider(min = kWeightMin, max = kWeightMax, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W5
        {
            get => m_W5;
            set => m_W5 = ClampWeight(value);
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

        [SettingsUISlider(min = kSlopeMin, max = kSlopeMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int MaxSlope
        {
            get => m_MaxSlope;
            set => m_MaxSlope = ClampInt(value, kSlopeMin, kSlopeMax);
        }

        [SettingsUISlider(min = kSiteCountMin, max = kSiteCountMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int SiteCount
        {
            get => m_SiteCount;
            set => m_SiteCount = ClampInt(value, kSiteCountMin, kSiteCountMax);
        }

        // A get-only string property renders as read-only text in the options page
        // and is re-evaluated every frame the page is open, so the readout needs no
        // refresh plumbing of its own.
        [SettingsUIMultilineText]
        [SettingsUISection(kSection, kCalibrationGroup)]
        public string CalibrationStatus => StationSuitabilityOverlaySystem.CalibrationStatusText;

        [SettingsUIButton]
        [SettingsUISection(kSection, kCalibrationGroup)]
        public bool ApplyFittedWeights
        {
            set => StationSuitabilityOverlaySystem.RequestApplyFittedWeights();
        }

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kCalibrationGroup)]
        public bool ResetRidershipData
        {
            set => StationSuitabilityOverlaySystem.RequestResetCalibration();
        }

        // Accumulated ridership aggregates, persisted through the normal settings
        // file so the series survives across sessions. Hidden because it is data,
        // not a preference.
        [SettingsUIHidden]
        public string RidershipData
        {
            get => m_RidershipData ?? string.Empty;
            set => m_RidershipData = value ?? string.Empty;
        }

        public override void SetDefaults()
        {
            m_Mode = ModePreset.Bus;
            m_HighlightShare = kHighlightDefault;
            m_MaxSlope = kSlopeDefault;
            m_SiteCount = kSiteCountDefault;
            m_RidershipData = string.Empty;
            ApplyPreset(m_Mode);
        }

        public void ApplyPreset(ModePreset mode)
        {
            m_Mode = mode;
            (m_CatchmentRadius, m_AccessRadius) = PresetRadii(mode);
            switch (mode)
            {
                case ModePreset.Bus:
                    m_W1 = 1.0f; m_W2 = 0.8f; m_W3 = 1.2f; m_W4 = 0.6f; m_W5 = 0.3f;
                    break;
                case ModePreset.Tram:
                    m_W1 = 1.0f; m_W2 = 0.9f; m_W3 = 1.3f; m_W4 = 0.7f; m_W5 = 0.35f;
                    break;
                case ModePreset.Metro:
                    m_W1 = 0.9f; m_W2 = 1.0f; m_W3 = 1.4f; m_W4 = 0.8f; m_W5 = 0.4f;
                    break;
                case ModePreset.Train:
                    // Regional scale: jobs and long-range coverage dominate, and
                    // local street density matters less than for street modes.
                    m_W1 = 0.8f; m_W2 = 1.1f; m_W3 = 1.5f; m_W4 = 0.5f; m_W5 = 0.5f;
                    break;
                case ModePreset.Ferry:
                    // Shoreline-constrained, so accessibility is mostly decided by
                    // the geography rather than by road density.
                    m_W1 = 1.0f; m_W2 = 0.7f; m_W3 = 1.2f; m_W4 = 0.4f; m_W5 = 0.25f;
                    break;
            }
        }

        private static (int catchment, int access) PresetRadii(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return (450, 130);
                case ModePreset.Metro: return (600, 150);
                case ModePreset.Train: return (900, 200);
                case ModePreset.Ferry: return (500, 140);
                default: return (350, 120);
            }
        }

        // Called before LoadSettings: zeroing the tuning fields lets ClampAll tell
        // whether the loaded file actually contained them (releases before 1.1
        // didn't persist tuning keys, so nothing overwrites the zeros).
        public void MarkTuningUnset()
        {
            m_CatchmentRadius = 0;
            m_AccessRadius = 0;
            m_HighlightShare = 0;
            m_MaxSlope = 0;
            m_SiteCount = 0;
            // Negative marks "absent" for the future-demand weight, since zero is a
            // legitimate value a user may have chosen.
            m_W5 = -1f;
        }

        // Called once after settings are loaded from disk to sanitize persisted
        // values. Tuning fields still zero were absent from the file; they get the
        // preset defaults for the LOADED mode, without touching custom weights.
        public void ClampAll()
        {
            m_Mode = ValidMode(m_Mode);
            m_W1 = ClampWeight(m_W1);
            m_W2 = ClampWeight(m_W2);
            m_W3 = ClampWeight(m_W3);
            m_W4 = ClampWeight(m_W4);
            (int catchment, int access) = PresetRadii(m_Mode);
            m_CatchmentRadius = m_CatchmentRadius == 0 ? catchment : ClampInt(m_CatchmentRadius, kCatchmentMin, kCatchmentMax);
            m_AccessRadius = m_AccessRadius == 0 ? access : ClampInt(m_AccessRadius, kAccessMin, kAccessMax);
            m_HighlightShare = m_HighlightShare == 0 ? kHighlightDefault : ClampInt(m_HighlightShare, kHighlightMin, kHighlightMax);
            m_MaxSlope = m_MaxSlope == 0 ? kSlopeDefault : ClampInt(m_MaxSlope, kSlopeMin, kSlopeMax);
            m_SiteCount = m_SiteCount == 0 ? kSiteCountDefault : ClampInt(m_SiteCount, kSiteCountMin, kSiteCountMax);
            m_W5 = m_W5 < 0f ? PresetFutureWeight(m_Mode) : ClampWeight(m_W5);
            m_RidershipData ??= string.Empty;
        }

        private static float PresetFutureWeight(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 0.35f;
                case ModePreset.Metro: return 0.4f;
                case ModePreset.Train: return 0.5f;
                case ModePreset.Ferry: return 0.25f;
                default: return 0.3f;
            }
        }

        private static ModePreset ValidMode(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Bus:
                case ModePreset.Metro:
                case ModePreset.Tram:
                case ModePreset.Train:
                case ModePreset.Ferry:
                    return mode;
                default:
                    return ModePreset.Bus;
            }
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
                { m_Setting.GetOptionGroupLocaleID(Setting.kCalibrationGroup), "Calibration" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Mode)), "Mode preset" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Mode)), "Transit mode the overlay evaluates. Determines which existing stops count as coverage." },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Bus), "Bus" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Tram), "Tram" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Metro), "Metro" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Train), "Train" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Ferry), "Ferry" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyPresetWeights)), "Apply preset weights" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyPresetWeights)), "Reset the weights and radii below to the recommended values for the selected mode." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W1)), "Demand weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W1)), "How strongly residents within the catchment raise the score." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W2)), "Jobs weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W2)), "How strongly workplaces within the catchment raise the score." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W3)), "Existing coverage penalty" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W3)), "How strongly existing stops of the selected mode lower the score nearby. Stops that no line serves are ignored." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W4)), "Accessibility weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W4)), "How strongly nearby road network density raises the score." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W5)), "Future demand weight" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W5)), "How strongly land that is zoned but not yet built on raises the score. Lets you place stops ahead of a district filling in." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CatchmentRadius)), "Catchment radius" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CatchmentRadius)), "Walking distance a stop serves. Residents, jobs and existing stops within this radius affect the score. Typical: 300-400 m for bus, 600-800 m for metro." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AccessRadius)), "Road access radius" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AccessRadius)), "How close the road network must be to count towards accessibility." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HighlightShare)), "Highlight share" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.HighlightShare)), "Share of the best built-up tiles shown at the top of the gradient. Lower values highlight only the very best spots." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxSlope)), "Maximum slope" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxSlope)), "Tiles steeper than this (in degrees) are treated as unbuildable and score nothing." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SiteCount)), "Recommended sites" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SiteCount)), "How many discrete candidate sites the Recommended sites layer marks." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CalibrationStatus)), "Model quality" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CalibrationStatus)), "The mod samples ridership at your served stops while the game runs, then fits the weights to it." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyFittedWeights)), "Apply fitted weights" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyFittedWeights)), "Overwrite the demand, jobs, accessibility and future weights with the fitted values above. Does nothing until enough samples have been collected." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetRidershipData)), "Reset collected samples" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetRidershipData)), "Discard all collected ridership samples and start over. Useful after reshaping your network." },

                { "StationSuitabilityOverlay.Infomode", "Station Suitability" },
                { "Infoviews.INFOVIEW[StationSuitabilityOverlay]", "Station Suitability" },
                { "Infoviews.INFOVIEW_TOOLTIP[StationSuitabilityOverlay]", "Shows how suitable each location is for a new transit stop." },

                { "Infoviews.INFOMODE[StationSuitabilityOverlay]", "Station Suitability" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityOverlay]", "Combined score. Green-yellow-red heatmap of station placement quality; how many tiles count as “best” is the Highlight share option." },
                { "Infoviews.INFOMODE[StationSuitabilitySites]", "Recommended sites" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilitySites]", "The best distinct candidate locations, spaced at least one catchment apart and ranked by walk-distance score." },
                { "Infoviews.INFOMODE[StationSuitabilityDemand]", "Demand (residents)" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityDemand]", "Residents reachable within the catchment radius, on the same landmass." },
                { "Infoviews.INFOMODE[StationSuitabilityJobs]", "Jobs" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityJobs]", "Workplace capacity reachable within the catchment radius." },
                { "Infoviews.INFOMODE[StationSuitabilityCoverage]", "Existing coverage" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityCoverage]", "How well existing stops of the selected mode already serve each tile." },
                { "Infoviews.INFOMODE[StationSuitabilityAccess]", "Accessibility" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityAccess]", "Road network density near each tile." },
                { "Infoviews.INFOMODE[StationSuitabilityFuture]", "Future demand (zoned)" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityFuture]", "Land that is zoned but not yet built on." },

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
