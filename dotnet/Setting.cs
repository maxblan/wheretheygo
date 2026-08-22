using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    [FileLocation(nameof(StationSuitabilityOverlay))]
    [SettingsUIGroupOrder(kPresetGroup, kWeightsGroup, kTuningGroup, kRoutesGroup, kCalibrationGroup)]
    [SettingsUIShowGroupName(kPresetGroup, kWeightsGroup, kTuningGroup, kRoutesGroup, kCalibrationGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kPresetGroup = "Preset";
        public const string kWeightsGroup = "Weights";
        public const string kTuningGroup = "Tuning";
        public const string kCalibrationGroup = "Calibration";
        public const string kRoutesGroup = "Routes";

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
        public const int kRouteCountMin = 1;
        public const int kRouteCountMax = 12;
        public const int kRouteCountDefault = 5;

        // What a suggested route is grown to maximise.
        public enum RouteGoal
        {
            Ridership = 0,
            Balanced = 1,
            Coverage = 2,
        }

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
        private float m_W6;
        private float m_W7;
        private int m_CatchmentRadius;
        private int m_AccessRadius;
        private int m_HighlightShare;
        private int m_MaxSlope;
        private int m_SiteCount;
        private string m_RidershipData = string.Empty;
        private RouteGoal m_Objective;
        private int m_RouteCount;
        private bool m_ShowRoutes = true;

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

        [SettingsUISlider(min = kWeightMin, max = kWeightMax, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W6
        {
            get => m_W6;
            set => m_W6 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightMin, max = kWeightMax, step = 0.05f, scalarMultiplier = 100f, unit = Unit.kFloatTwoFractions)]
        [SettingsUISection(kSection, kWeightsGroup)]
        public float W7
        {
            get => m_W7;
            set => m_W7 = ClampWeight(value);
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

        [SettingsUISection(kSection, kRoutesGroup)]
        public bool ShowRoutes
        {
            get => m_ShowRoutes;
            set => m_ShowRoutes = value;
        }

        [SettingsUISection(kSection, kRoutesGroup)]
        public RouteGoal Objective
        {
            get => m_Objective;
            set => m_Objective = value;
        }

        [SettingsUISlider(min = kRouteCountMin, max = kRouteCountMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kRoutesGroup)]
        public int RouteCount
        {
            get => m_RouteCount;
            set => m_RouteCount = ClampInt(value, kRouteCountMin, kRouteCountMax);
        }

        [SettingsUISection(kSection, kRoutesGroup)]
        public string RouteSummary => StationSuitabilityOverlaySystem.RouteSummaryText;

        // A get-only string property renders as a read-only field in the options
        // page and is re-evaluated every frame the page is open, so the readout
        // needs no refresh plumbing of its own.
        //
        // Deliberately NOT [SettingsUIMultilineText]: that widget takes its body
        // from the display-name action rather than the property value, so a getter
        // like this one renders an empty box under the label. The full breakdown
        // goes to the log; this stays a single line.
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
            m_Objective = RouteGoal.Balanced;
            m_RouteCount = kRouteCountDefault;
            m_ShowRoutes = true;
            ApplyPreset(m_Mode);
        }

        public void ApplyPreset(ModePreset mode)
        {
            m_Mode = mode;
            (m_CatchmentRadius, m_AccessRadius) = PresetRadii(mode);
            switch (mode)
            {
                case ModePreset.Bus:
                    m_W1 = 1.0f; m_W2 = 0.8f; m_W3 = 1.2f; m_W4 = 0.6f; m_W5 = 0.3f; m_W6 = 0.9f; m_W7 = 0.4f;
                    break;
                case ModePreset.Tram:
                    m_W1 = 1.0f; m_W2 = 0.9f; m_W3 = 1.3f; m_W4 = 0.7f; m_W5 = 0.35f; m_W6 = 0.8f; m_W7 = 0.45f;
                    break;
                case ModePreset.Metro:
                    m_W1 = 0.9f; m_W2 = 1.0f; m_W3 = 1.4f; m_W4 = 0.8f; m_W5 = 0.4f; m_W6 = 0.6f; m_W7 = 0.5f;
                    break;
                case ModePreset.Train:
                    // Regional scale: jobs and long-range coverage dominate, and
                    // local street density matters less than for street modes.
                    m_W1 = 0.8f; m_W2 = 1.1f; m_W3 = 1.5f; m_W4 = 0.5f; m_W5 = 0.5f; m_W6 = 0.5f; m_W7 = 0.6f;
                    break;
                case ModePreset.Ferry:
                    // Shoreline-constrained, so accessibility is mostly decided by
                    // the geography rather than by road density.
                    m_W1 = 1.0f; m_W2 = 0.7f; m_W3 = 1.2f; m_W4 = 0.4f; m_W5 = 0.25f; m_W6 = 0.7f; m_W7 = 0.3f;
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
            m_RouteCount = 0;
            // Negative marks "absent" for weights added after 1.1, since zero is a
            // legitimate value a user may have chosen.
            m_W5 = -1f;
            m_W6 = -1f;
            m_W7 = -1f;
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
            m_RouteCount = m_RouteCount == 0 ? kRouteCountDefault : ClampInt(m_RouteCount, kRouteCountMin, kRouteCountMax);
            m_Objective = ValidObjective(m_Objective);
            m_W5 = m_W5 < 0f ? PresetWeight(m_Mode, 5) : ClampWeight(m_W5);
            m_W6 = m_W6 < 0f ? PresetWeight(m_Mode, 6) : ClampWeight(m_W6);
            m_W7 = m_W7 < 0f ? PresetWeight(m_Mode, 7) : ClampWeight(m_W7);
            m_RidershipData ??= string.Empty;
        }

        // Default for a weight that was absent from an older settings file. Kept in
        // step with ApplyPreset by hand — ModSetting has no parameterless base
        // constructor, so a throwaway instance cannot be used to read the presets.
        private static float PresetWeight(ModePreset mode, int index)
        {
            switch (index)
            {
                case 5:
                    switch (mode)
                    {
                        case ModePreset.Tram: return 0.35f;
                        case ModePreset.Metro: return 0.4f;
                        case ModePreset.Train: return 0.5f;
                        case ModePreset.Ferry: return 0.25f;
                        default: return 0.3f;
                    }
                case 6:
                    switch (mode)
                    {
                        case ModePreset.Tram: return 0.8f;
                        case ModePreset.Metro: return 0.6f;
                        case ModePreset.Train: return 0.5f;
                        case ModePreset.Ferry: return 0.7f;
                        default: return 0.9f;
                    }
                default:
                    switch (mode)
                    {
                        case ModePreset.Tram: return 0.45f;
                        case ModePreset.Metro: return 0.5f;
                        case ModePreset.Train: return 0.6f;
                        case ModePreset.Ferry: return 0.3f;
                        default: return 0.4f;
                    }
            }
        }

        private static RouteGoal ValidObjective(RouteGoal goal)
        {
            switch (goal)
            {
                case RouteGoal.Ridership:
                case RouteGoal.Balanced:
                case RouteGoal.Coverage:
                    return goal;
                default:
                    return RouteGoal.Balanced;
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

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W6)), "Interchange bonus" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W6)), "How strongly a nearby stop of a DIFFERENT mode raises the score. This is what makes a bus stop at a metro station rate highly: the new stop feeds an existing trunk line. Scaled by how much capacity the other mode carries, so a metro or train counts for far more than another bus." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W7)), "Cross-mode overlap penalty" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W7)), "How strongly another mode's service lowers the score when it is close enough to already carry the same riders, but too far to transfer to. Discourages running a new line parallel to an existing one." },

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

                { m_Setting.GetOptionGroupLocaleID(Setting.kRoutesGroup), "Route suggestions" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowRoutes)), "Show suggested routes" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowRoutes)), "Draw the suggested lines and their stops on the map while this infoview is open." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Objective)), "Route objective" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Objective)), "What a suggested line is grown to achieve. Maximum ridership follows the busiest journeys; maximum coverage spreads out to reach more districts even where demand is thin; balanced does both." },
                { m_Setting.GetEnumValueLocaleID(Setting.RouteGoal.Ridership), "Maximum ridership" },
                { m_Setting.GetEnumValueLocaleID(Setting.RouteGoal.Balanced), "Balanced" },
                { m_Setting.GetEnumValueLocaleID(Setting.RouteGoal.Coverage), "Maximum coverage" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteCount)), "Suggested lines" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteCount)), "How many lines to suggest. Each one takes the demand it would carry out of the pool, so later suggestions complement the earlier ones." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteSummary)), "Suggestions" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteSummary)), "The current suggestions, best first. The full detail is written to the mod log." },

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
                { "Infoviews.INFOMODE[StationSuitabilityInterchange]", "Interchange potential" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityInterchange]", "Where a stop of this mode would sit within transfer distance of another mode's service, weighted by how much capacity that mode carries." },
                { "Infoviews.INFOMODE[StationSuitabilityTravelDemand]", "Travel demand" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityTravelDemand]", "Where people actually want to travel, from real home-to-work and home-to-school journeys. Shows the demand your network does not already carry." },
                { "Infoviews.INFOMODE[StationSuitabilityCrossCoverage]", "Cross-mode overlap" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityCrossCoverage]", "Where another mode already serves the same riders but is too far away to transfer to." },

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
