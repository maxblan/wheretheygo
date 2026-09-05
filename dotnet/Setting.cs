using System;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace StationSuitabilityOverlay
{
    [FileLocation(nameof(StationSuitabilityOverlay))]
    [SettingsUIGroupOrder(kPresetGroup, kWeightsGroup, kTuningGroup, kRoutesGroup, kCalibrationGroup)]
    [SettingsUIShowGroupName(kPresetGroup, kWeightsGroup, kTuningGroup, kRoutesGroup, kCalibrationGroup)]
    public sealed class Setting : ModSetting
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
        public const int kCatchmentStep = 25;
        public const int kAccessMin = 50;
        public const int kAccessMax = 300;
        public const int kAccessStep = 10;
        public const int kHighlightMin = 1;
        public const int kHighlightMax = 20;
        public const int kHighlightDefault = 5;
        // Equity floor (register A1.8/A1.9, decided 2026-09-05): the share of journeys
        // that must have BOTH ends within the walking horizon of a served stop before
        // suggestions may chase efficiency, and the horizon itself.
        public const int kEquityMinutesMin = 5;
        public const int kEquityMinutesMax = 20;
        public const int kEquityMinutesDefault = 10;
        public const int kEquityFloorMin = 50;
        public const int kEquityFloorMax = 100;
        public const int kEquityFloorDefault = 80;
        // Utilisation floor (A4.1/A6.4): peak-hour boardings over peak-hour seats a
        // suggested line has to reach.
        public const int kUtilisationMin = 5;
        public const int kUtilisationMax = 60;
        public const int kUtilisationDefault = 25;
        public const int kSlopeMin = 3;
        public const int kSlopeMax = 45;
        public const int kSlopeDefault = 15;
        public const int kSiteCountMin = 1;
        public const int kSiteCountMax = 20;
        public const int kSiteCountDefault = 8;
        public const int kRouteCountMin = 1;
        public const int kRouteCountMax = 12;
        public const int kRouteCountDefault = 5;
        public const int kTransferPenaltyMin = 0;
        public const int kTransferPenaltyMax = 80;
        public const int kTransferPenaltyDefault = 40;

        // ModePreset and RouteGoal live in TransitMode.cs, beside everything that is
        // true of a mode — see the note there for why they are not nested here.
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
        private int m_EquityWalkMinutes;
        private int m_EquityFloorPercent;
        private int m_UtilisationFloorPercent;
        private int m_SiteCount;
        private string m_RidershipData = string.Empty;
        private RouteGoal m_Objective;
        private int m_RouteCount;
        private bool m_ShowRoutes = true;
        private int m_TransferPenalty = kTransferPenaltyDefault;

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

        [SettingsUISlider(min = kCatchmentMin, max = kCatchmentMax, step = kCatchmentStep, scalarMultiplier = 1, unit = Unit.kLength)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int CatchmentRadius
        {
            get => m_CatchmentRadius;
            set => m_CatchmentRadius = ClampInt(value, kCatchmentMin, kCatchmentMax);
        }

        [SettingsUISlider(min = kAccessMin, max = kAccessMax, step = kAccessStep, scalarMultiplier = 1, unit = Unit.kLength)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int AccessRadius
        {
            get => m_AccessRadius;
            set => m_AccessRadius = ClampInt(value, kAccessMin, kAccessMax);
        }

        [SettingsUISlider(min = kEquityMinutesMin, max = kEquityMinutesMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int EquityWalkMinutes
        {
            get => m_EquityWalkMinutes;
            set => m_EquityWalkMinutes = ClampInt(value, kEquityMinutesMin, kEquityMinutesMax);
        }

        [SettingsUISlider(min = kEquityFloorMin, max = kEquityFloorMax, step = 5, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int EquityFloorPercent
        {
            get => m_EquityFloorPercent;
            set => m_EquityFloorPercent = ClampInt(value, kEquityFloorMin, kEquityFloorMax);
        }

        [SettingsUISlider(min = kUtilisationMin, max = kUtilisationMax, step = 5, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kSection, kTuningGroup)]
        public int UtilisationFloorPercent
        {
            get => m_UtilisationFloorPercent;
            set => m_UtilisationFloorPercent = ClampInt(value, kUtilisationMin, kUtilisationMax);
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

        [SettingsUISlider(min = kTransferPenaltyMin, max = kTransferPenaltyMax, step = 5, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kSection, kRoutesGroup)]
        public int TransferPenalty
        {
            get => m_TransferPenalty;
            set => m_TransferPenalty = ClampInt(value, kTransferPenaltyMin, kTransferPenaltyMax);
        }

        // Multiplier applied per change of vehicle when crediting a suggested line.
        public float TransferDiscount => 1f - (m_TransferPenalty / 100f);

        [SettingsUISection(kSection, kRoutesGroup)]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
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
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public string CalibrationStatus => StationSuitabilityOverlaySystem.CalibrationStatusText;

        [SettingsUIButton]
        [SettingsUISection(kSection, kCalibrationGroup)]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public bool ApplyFittedWeights
        {
            set => StationSuitabilityOverlaySystem.RequestApplyFittedWeights();
        }

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kCalibrationGroup)]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public bool ResetRidershipData
        {
            set => StationSuitabilityOverlaySystem.RequestResetCalibration();
        }

        // Accumulated ridership aggregates, persisted through the normal settings
        // file so the series survives across sessions. Hidden because it is data,
        // not a preference.
        // Writes the offline verification pipeline a canonical instance of the current
        // city. Read-only: it exports what the mod already computed and changes
        // nothing. See verification/README.md.
        [SettingsUIButton]
        [SettingsUISection(kSection, kCalibrationGroup)]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public bool ExportVerificationInstance
        {
            set => StationSuitabilityOverlaySystem.RequestVerificationExport();
        }

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
            m_EquityWalkMinutes = kEquityMinutesDefault;
            m_EquityFloorPercent = kEquityFloorDefault;
            m_UtilisationFloorPercent = kUtilisationDefault;
            m_SiteCount = kSiteCountDefault;
            m_RidershipData = string.Empty;
            m_Objective = RouteGoal.Balanced;
            m_RouteCount = kRouteCountDefault;
            m_ShowRoutes = true;
            m_TransferPenalty = kTransferPenaltyDefault;
            ApplyPreset(m_Mode);
        }

        public void ApplyPreset(ModePreset mode)
        {
            m_Mode = mode;
            (m_CatchmentRadius, m_AccessRadius) = PresetRadii(mode);
            float[] weights = PresetWeights(mode);
            m_W1 = weights[0];
            m_W2 = weights[1];
            m_W3 = weights[2];
            m_W4 = weights[3];
            m_W5 = weights[4];
            m_W6 = weights[5];
            m_W7 = weights[6];
        }

        // The recommended weights for a mode, W1..W7 in order.
        //
        // ONE table. ApplyPreset wants all seven; ClampAll wants individual ones to
        // fill in a weight that a pre-1.1 settings file never had. Those were two
        // switch statements with a comment promising they were "kept in step by hand",
        // which is a promise rather than a mechanism.
        private static float[] PresetWeights(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return new[] { 1.0f, 0.9f, 1.3f, 0.7f, 0.35f, 0.8f, 0.45f };
                case ModePreset.Metro: return new[] { 0.9f, 1.0f, 1.4f, 0.8f, 0.4f, 0.6f, 0.5f };
                // Regional scale: jobs and long-range coverage dominate, and local
                // street density matters less than for street modes.
                case ModePreset.Train: return new[] { 0.8f, 1.1f, 1.5f, 0.5f, 0.5f, 0.5f, 0.6f };
                // Shoreline-constrained, so accessibility is mostly decided by the
                // geography rather than by road density.
                case ModePreset.Ferry: return new[] { 1.0f, 0.7f, 1.2f, 0.4f, 0.25f, 0.7f, 0.3f };
                default: return new[] { 1.0f, 0.8f, 1.2f, 0.6f, 0.3f, 0.9f, 0.4f };
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
            m_EquityWalkMinutes = 0;
            m_EquityFloorPercent = 0;
            m_UtilisationFloorPercent = 0;
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
            m_EquityWalkMinutes = m_EquityWalkMinutes == 0 ? kEquityMinutesDefault : ClampInt(m_EquityWalkMinutes, kEquityMinutesMin, kEquityMinutesMax);
            m_EquityFloorPercent = m_EquityFloorPercent == 0 ? kEquityFloorDefault : ClampInt(m_EquityFloorPercent, kEquityFloorMin, kEquityFloorMax);
            m_UtilisationFloorPercent = m_UtilisationFloorPercent == 0 ? kUtilisationDefault : ClampInt(m_UtilisationFloorPercent, kUtilisationMin, kUtilisationMax);
            m_SiteCount = m_SiteCount == 0 ? kSiteCountDefault : ClampInt(m_SiteCount, kSiteCountMin, kSiteCountMax);
            m_RouteCount = m_RouteCount == 0 ? kRouteCountDefault : ClampInt(m_RouteCount, kRouteCountMin, kRouteCountMax);
            m_Objective = ValidObjective(m_Objective);
            m_TransferPenalty = ClampInt(m_TransferPenalty, kTransferPenaltyMin, kTransferPenaltyMax);
            float[] preset = PresetWeights(m_Mode);
            m_W5 = m_W5 < 0f ? preset[4] : ClampWeight(m_W5);
            m_W6 = m_W6 < 0f ? preset[5] : ClampWeight(m_W6);
            m_W7 = m_W7 < 0f ? preset[6] : ClampWeight(m_W7);
            m_RidershipData ??= string.Empty;
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
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Station Suitability Overlay" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPresetGroup), "Preset" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kWeightsGroup), "Weights" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kTuningGroup), "Tuning" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kCalibrationGroup), "Calibration" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Mode)), "Mode preset" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Mode)), "Transit mode the overlay evaluates. Determines which existing stops count as coverage." },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Bus), "Bus" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Tram), "Tram" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Metro), "Metro" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Train), "Train" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Ferry), "Ferry" },

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

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EquityWalkMinutes)), "Equity: walking horizon (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EquityWalkMinutes)), "A journey counts as served when both its ends are within this many minutes' walk of a served stop." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EquityFloorPercent)), "Equity: served share to reach" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EquityFloorPercent)), "Until this share of the city's journeys is served, suggestions are ranked by how many journeys they newly serve; above it, by the travel they enable." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.UtilisationFloorPercent)), "Minimum utilisation" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.UtilisationFloorPercent)), "Peak-hour boardings over peak-hour seats a suggested line must reach. Lines that would run emptier are not suggested." },
                { "StationSuitabilityOverlay.Panel[Equity]", "Served journeys" },
                { "StationSuitabilityOverlay.Panel[EquityValue]", "{0} % of journeys have home and destination within {1} min of a served stop (target {2} %) \u00b7 Gini of access walk {3}" },
                { "StationSuitabilityOverlay.Panel[EquityEmpty]", "not measured yet" },
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
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Ridership), "Maximum ridership" },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Balanced), "Balanced" },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Coverage), "Maximum coverage" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteCount)), "Suggested lines" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteCount)), "How many lines to suggest. Each one takes the demand it would carry out of the pool, so later suggestions complement the earlier ones." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TransferPenalty)), "Transfer penalty" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TransferPenalty)), "How much a journey is discounted for each change of vehicle when crediting a suggested line. Zero treats a three-leg trip as good as a direct one; higher values favour direct service." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteSummary)), "Suggestions" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteSummary)), "The current suggestions, best first. The full detail is written to the mod log." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CalibrationStatus)), "Model quality" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CalibrationStatus)), "The mod samples ridership at your served stops while the game runs, then fits the weights to it." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyFittedWeights)), "Apply fitted weights" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyFittedWeights)), "Overwrite the demand, jobs, accessibility and future weights with the fitted values above. Does nothing until enough samples have been collected." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetRidershipData)), "Reset collected samples" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetRidershipData)), "Discard all collected ridership samples and start over. Useful after reshaping your network." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ExportVerificationInstance)), "Export verification instance" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ExportVerificationInstance)), "Write this city's scoring inputs and results to ModsData/StationSuitabilityOverlay/verification as canonical JSON, for the offline verification pipeline. Read-only: it exports what the mod already computed and changes nothing. The files are written after the next recalculation; the mod log names the folder." },

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

            foreach (KeyValuePair<string, string> panel in PanelEntries())
            {
                entries.Add(panel.Key, panel.Value);
            }

            return entries;
        }

        // Strings the mod's own panel resolves through cs2/l10n. Kept apart from the
        // block above because they have a different consumer: those are rendered by the
        // game's Options UI, these by StationSuitabilityOverlay.mjs.
        private static Dictionary<string, string> PanelEntries()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Control panel strings. The panel resolves these itself through cs2/l10n with
                // the English text inline as a fallback, so a key missing here shows English
                // rather than a raw key.
                { "StationSuitabilityOverlay.Panel[Title]", "Station Suitability" },
                { "StationSuitabilityOverlay.Panel[Mode]", "Mode" },
                { "StationSuitabilityOverlay.Panel[Objective]", "Objective" },
                { "StationSuitabilityOverlay.Panel[Tuning]", "Tuning" },
                { "StationSuitabilityOverlay.Panel[RoutePlanning]", "Route planning" },
                { "StationSuitabilityOverlay.Panel[ApplyPreset]", "Apply preset weights for this mode" },
                { "StationSuitabilityOverlay.Panel[Heatmap]", "Suitability heat map" },
                { "StationSuitabilityOverlay.Panel[ShowRoutes]", "Show routes" },
                { "StationSuitabilityOverlay.Panel[On]", "On" },
                { "StationSuitabilityOverlay.Panel[Off]", "Off" },
                { "StationSuitabilityOverlay.Panel[Legend]", "Station suitability" },
                { "StationSuitabilityOverlay.Panel[LegendLow]", "Low" },
                { "StationSuitabilityOverlay.Panel[LegendHigh]", "High" },
                { "StationSuitabilityOverlay.Panel[SuggestedLines]", "Suggested lines" },
                { "StationSuitabilityOverlay.Panel[Km]", "km" },
                { "StationSuitabilityOverlay.Panel[Stops]", "stops" },
                { "StationSuitabilityOverlay.Panel[Vehicles]", "veh" },
                { "StationSuitabilityOverlay.Panel[Reach]", "unlocks {0}% of unserved travel" },
                { "StationSuitabilityOverlay.Panel[LineHealth]", "Line health" },
                { "StationSuitabilityOverlay.Panel[SuggestImprovement]", "Suggest improvement" },
                { "StationSuitabilityOverlay.Panel[ImprovedPlan]", "Improved plan" },
                { "StationSuitabilityOverlay.Panel[ImprovedPlanHint]", "The white dashed line on the map is the re-traced route." },
                { "StationSuitabilityOverlay.Panel[Meta]", "{0}% full, {1} veh, {2} stops" },
                { "StationSuitabilityOverlay.Panel[DataBasis]", "Data collected" },
                { "StationSuitabilityOverlay.Panel[DataBasisValue]", "{0} h of {1} h \u00b7 {2} readings" },
                { "StationSuitabilityOverlay.Panel[DataBasisEmpty]", "no lines to watch yet \u2014 readings start with your first one" },
                { "StationSuitabilityOverlay.Panel[ObservedTrips]", "{0} shopping/leisure journeys seen over {1} h" },
                { "StationSuitabilityOverlay.Panel[ObservedTripsEmpty]", "no shopping/leisure journeys seen yet" },
                { "StationSuitabilityOverlay.Panel[Basis]", "average over {0} h, {1} readings, peak {2}%" },
                { "StationSuitabilityOverlay.Panel[BasisSingle]", "single reading so far" },
                { "StationSuitabilityOverlay.Panel[Mode.Bus]", "Bus" },
                { "StationSuitabilityOverlay.Panel[Mode.Tram]", "Tram" },
                { "StationSuitabilityOverlay.Panel[Mode.Metro]", "Metro" },
                { "StationSuitabilityOverlay.Panel[Mode.Train]", "Train" },
                { "StationSuitabilityOverlay.Panel[Mode.Ferry]", "Ferry" },
                { "StationSuitabilityOverlay.Panel[Objective.Ridership]", "Ridership" },
                { "StationSuitabilityOverlay.Panel[Objective.Balanced]", "Balanced" },
                { "StationSuitabilityOverlay.Panel[Objective.Coverage]", "Coverage" },
                { "StationSuitabilityOverlay.Panel[Slider.catchment]", "Catchment" },
                { "StationSuitabilityOverlay.Panel[Slider.access]", "Road access" },
                { "StationSuitabilityOverlay.Panel[Slider.highlight]", "Highlight" },
                { "StationSuitabilityOverlay.Panel[Slider.slope]", "Max slope" },
                { "StationSuitabilityOverlay.Panel[Slider.sites]", "Sites" },
                { "StationSuitabilityOverlay.Panel[Slider.routes]", "Routes" },
                { "StationSuitabilityOverlay.Panel[Verdict.Healthy]", "healthy" },
                { "StationSuitabilityOverlay.Panel[Verdict.NearlyEmpty]", "nearly empty — reroute or remove" },
                { "StationSuitabilityOverlay.Panel[Verdict.LongWaits]", "long waits with spare room — shorten the route or run more often" },
                { "StationSuitabilityOverlay.Panel[Verdict.Overcrowded]", "overcrowded — increase service" },
                { "StationSuitabilityOverlay.Panel[Verdict.Overcrowded.Arg]", "overcrowded — add {0} vehicle(s)" },
                { "StationSuitabilityOverlay.Panel[Verdict.AtModeCapacity]", "at capacity — split the route" },
                { "StationSuitabilityOverlay.Panel[Verdict.AtModeCapacity.Arg]", "at capacity — upgrade to {0}" },
                { "StationSuitabilityOverlay.Panel[Plan.Fleet]", "run it as {0} with {1} vehicle(s)" },
                { "StationSuitabilityOverlay.Panel[Plan.Delta]", " ({0})" },
                { "StationSuitabilityOverlay.Panel[Plan.Interval]", ", i.e. an interval of about {0} s" },
                { "StationSuitabilityOverlay.Panel[Plan.Split]", "; split it — {0} km is beyond what one {1} line can keep to time" },
                { "StationSuitabilityOverlay.Panel[Plan.ThinStops]", "; thin the stops to about {0} — they average {1} m apart, close for a {2}" },
                { "StationSuitabilityOverlay.Panel[Plan.Reroute]", "; or reroute it through denser ground — the suggestions list shows where demand is unserved" },
                { "StationSuitabilityOverlay.Panel[Plan.Fine]", "; the route shape looks reasonable" },
            };
        }

        public void Unload()
        {
        }
    }
}
