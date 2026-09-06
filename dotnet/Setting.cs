using System;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace TransitArchitect
{
    // Two tabs (author's decision 8b, 2026-09-06): everything a player needs is in
    // "General"; the modelling knobs a player should not have to understand sit in
    // "Advanced". Nothing is hidden — the mod is meant to be usable without ever
    // opening the second tab.
    [FileLocation(nameof(TransitArchitect))]
    [SettingsUIGroupOrder(kPlanningGroup, kStandardsGroup, kWeightsGroup, kTuningGroup, kCalibrationGroup)]
    [SettingsUIShowGroupName(kPlanningGroup, kStandardsGroup, kWeightsGroup, kTuningGroup, kCalibrationGroup)]
    public sealed class Setting : ModSetting
    {
        // Tabs. The strings are UI ids, not persistence keys (settings files key on
        // the PROPERTY name), so they are free to change.
        public const string kSection = "General";
        public const string kAdvancedSection = "Advanced";

        // Groups of the General tab.
        public const string kPlanningGroup = "Planning";
        public const string kStandardsGroup = "Standards";

        // Groups of the Advanced tab.
        public const string kWeightsGroup = "Weights";
        public const string kTuningGroup = "Tuning";
        public const string kCalibrationGroup = "Calibration";

        // Single source of truth for slider ranges: the UI attributes, the
        // property setters and ClampAll all reference these.
        public const float kWeightMin = 0f;
        public const float kWeightMax = 2f;

        // The weight sliders are shown as whole percents, and their bounds have to be
        // given in that same scaled space. Game.UI.Menu.AutomaticSettings.AddFloatSlider
        // (decompiled 2026-09-06) reads the property through
        // `value * scalarMultiplier` but passes `min`/`max` through untouched, so a bar
        // running 0..2 against a value of 100 sat at the far right for every weight —
        // seven sliders that all looked identical whatever they held.
        public const float kWeightScale = 100f;

        public const float kWeightSliderMin = kWeightMin * kWeightScale;

        public const float kWeightSliderMax = kWeightMax * kWeightScale;

        public const float kWeightSliderStep = 5f;
        public const int kCatchmentMin = 150;
        public const int kCatchmentMax = 1000;
        public const int kCatchmentStep = 25;
        public const int kAccessMin = 50;
        public const int kAccessMax = 300;
        public const int kAccessStep = 10;
        public const int kHighlightMin = 1;
        public const int kHighlightMax = 20;
        // Equity floor (register A1.8/A1.9, decided 2026-09-05): the share of journeys
        // that must have BOTH ends within the walking horizon of a served stop before
        // suggestions may chase efficiency, and the horizon itself.
        public const int kEquityMinutesMin = 5;
        public const int kEquityMinutesMax = 20;
        public const int kEquityFloorMin = 50;
        public const int kEquityFloorMax = 100;
        // Utilisation floor (A4.1/A6.4): peak-hour boardings over peak-hour seats a
        // suggested line has to reach.
        public const int kUtilisationMin = 5;
        public const int kUtilisationMax = 60;
        public const int kSlopeMin = 3;
        public const int kSlopeMax = 45;
        public const int kSiteCountMin = 1;
        public const int kSiteCountMax = 20;
        public const int kRouteCountMin = 1;
        public const int kRouteCountMax = 12;

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
        private bool m_ShowHeatmap = true;
        private bool m_DeveloperTools;

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(kSection, kPlanningGroup)]
        public ModePreset Mode
        {
            get => m_Mode;
            set => m_Mode = value;
        }

        [SettingsUIButton]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public bool ApplyPresetWeights
        {
            set => ApplyPreset(m_Mode);
        }

        // The W1..W4 property names are the persistence keys of already-shipped
        // settings files; renaming them would silently drop users' saved weights.

        [SettingsUISlider(min = kWeightSliderMin, max = kWeightSliderMax, step = kWeightSliderStep, scalarMultiplier = kWeightScale, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public float W1
        {
            get => m_W1;
            set => m_W1 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightSliderMin, max = kWeightSliderMax, step = kWeightSliderStep, scalarMultiplier = kWeightScale, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public float W2
        {
            get => m_W2;
            set => m_W2 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightSliderMin, max = kWeightSliderMax, step = kWeightSliderStep, scalarMultiplier = kWeightScale, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public float W3
        {
            get => m_W3;
            set => m_W3 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightSliderMin, max = kWeightSliderMax, step = kWeightSliderStep, scalarMultiplier = kWeightScale, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public float W4
        {
            get => m_W4;
            set => m_W4 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightSliderMin, max = kWeightSliderMax, step = kWeightSliderStep, scalarMultiplier = kWeightScale, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public float W5
        {
            get => m_W5;
            set => m_W5 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightSliderMin, max = kWeightSliderMax, step = kWeightSliderStep, scalarMultiplier = kWeightScale, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public float W6
        {
            get => m_W6;
            set => m_W6 = ClampWeight(value);
        }

        [SettingsUISlider(min = kWeightSliderMin, max = kWeightSliderMax, step = kWeightSliderStep, scalarMultiplier = kWeightScale, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kWeightsGroup)]
        public float W7
        {
            get => m_W7;
            set => m_W7 = ClampWeight(value);
        }

        [SettingsUISlider(min = kCatchmentMin, max = kCatchmentMax, step = kCatchmentStep, scalarMultiplier = 1, unit = Unit.kLength)]
        [SettingsUISection(kAdvancedSection, kTuningGroup)]
        public int CatchmentRadius
        {
            get => m_CatchmentRadius;
            set => m_CatchmentRadius = ClampInt(value, kCatchmentMin, kCatchmentMax);
        }

        [SettingsUISlider(min = kAccessMin, max = kAccessMax, step = kAccessStep, scalarMultiplier = 1, unit = Unit.kLength)]
        [SettingsUISection(kAdvancedSection, kTuningGroup)]
        public int AccessRadius
        {
            get => m_AccessRadius;
            set => m_AccessRadius = ClampInt(value, kAccessMin, kAccessMax);
        }

        [SettingsUISlider(min = kEquityMinutesMin, max = kEquityMinutesMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kStandardsGroup)]
        public int EquityWalkMinutes
        {
            get => m_EquityWalkMinutes;
            set => m_EquityWalkMinutes = ClampInt(value, kEquityMinutesMin, kEquityMinutesMax);
        }

        [SettingsUISlider(min = kEquityFloorMin, max = kEquityFloorMax, step = 5, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kSection, kStandardsGroup)]
        public int EquityFloorPercent
        {
            get => m_EquityFloorPercent;
            set => m_EquityFloorPercent = ClampInt(value, kEquityFloorMin, kEquityFloorMax);
        }

        [SettingsUISlider(min = kUtilisationMin, max = kUtilisationMax, step = 5, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kSection, kStandardsGroup)]
        public int UtilisationFloorPercent
        {
            get => m_UtilisationFloorPercent;
            set => m_UtilisationFloorPercent = ClampInt(value, kUtilisationMin, kUtilisationMax);
        }

        [SettingsUISlider(min = kHighlightMin, max = kHighlightMax, step = 1, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kAdvancedSection, kTuningGroup)]
        public int HighlightShare
        {
            get => m_HighlightShare;
            set => m_HighlightShare = ClampInt(value, kHighlightMin, kHighlightMax);
        }

        [SettingsUISlider(min = kSlopeMin, max = kSlopeMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kAdvancedSection, kTuningGroup)]
        public int MaxSlope
        {
            get => m_MaxSlope;
            set => m_MaxSlope = ClampInt(value, kSlopeMin, kSlopeMax);
        }

        [SettingsUISlider(min = kSiteCountMin, max = kSiteCountMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kAdvancedSection, kTuningGroup)]
        public int SiteCount
        {
            get => m_SiteCount;
            set => m_SiteCount = ClampInt(value, kSiteCountMin, kSiteCountMax);
        }

        // The suitability map itself. The game's infoview menu switches it too — this is
        // the same switch in the place a player looks for settings, and it is persisted
        // so a city opens the way it was left.
        [SettingsUISection(kSection, kPlanningGroup)]
        public bool ShowHeatmap
        {
            get => m_ShowHeatmap;
            set
            {
                m_ShowHeatmap = value;
                TransitArchitectSystem.RequestInfoview(value);
            }
        }

        [SettingsUISection(kSection, kPlanningGroup)]
        public bool ShowRoutes
        {
            get => m_ShowRoutes;
            set => m_ShowRoutes = value;
        }

        // Since the set selection became exact (Phase 7) this only steers how corridors
        // are GROWN, not which lines are chosen, so it belongs with the modelling knobs
        // (author's decision 9b; default Balanced).
        [SettingsUISection(kAdvancedSection, kTuningGroup)]
        public RouteGoal Objective
        {
            get => m_Objective;
            set => m_Objective = value;
        }

        [SettingsUISlider(min = kRouteCountMin, max = kRouteCountMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kPlanningGroup)]
        public int RouteCount
        {
            get => m_RouteCount;
            set => m_RouteCount = ClampInt(value, kRouteCountMin, kRouteCountMax);
        }

        // A get-only string property renders as a read-only field in the options
        // page and is re-evaluated every frame the page is open, so the readout
        // needs no refresh plumbing of its own.
        //
        // Deliberately NOT [SettingsUIMultilineText]: that widget takes its body
        // from the display-name action rather than the property value, so a getter
        // like this one renders an empty box under the label. The full breakdown
        // goes to the log; this stays a single line.
        [SettingsUIHideByCondition(typeof(Setting), nameof(DeveloperToolsOff))]
        [SettingsUISection(kAdvancedSection, kCalibrationGroup)]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public string CalibrationStatus => TransitArchitectSystem.CalibrationStatusText;

        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(DeveloperToolsOff))]
        [SettingsUISection(kAdvancedSection, kCalibrationGroup)]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public bool ApplyFittedWeights
        {
            set => TransitArchitectSystem.RequestApplyFittedWeights();
        }

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUIHideByCondition(typeof(Setting), nameof(DeveloperToolsOff))]
        [SettingsUISection(kAdvancedSection, kCalibrationGroup)]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public bool ResetRidershipData
        {
            set => TransitArchitectSystem.RequestResetCalibration();
        }

        // Accumulated ridership aggregates, persisted through the normal settings
        // file so the series survives across sessions. Hidden because it is data,
        // not a preference.
        // Writes the offline verification pipeline a canonical instance of the current
        // city. Read-only: it exports what the mod already computed and changes
        // nothing. See verification/README.md. Hidden unless the developer switch is
        // on: it is the mod's own test harness, not a thing to hand a player.
        [SettingsUIButton]
        [SettingsUISection(kAdvancedSection, kCalibrationGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(DeveloperToolsOff))]
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI binds to instance properties by "
                + "reflection; a static member would not appear in the Options page.")]
        public bool ExportVerificationInstance
        {
            set => TransitArchitectSystem.RequestVerificationExport();
        }

        // Shows the mod's own diagnostics in this page. Off for everyone who is not
        // working on the mod.
        [SettingsUISection(kAdvancedSection, kCalibrationGroup)]
        public bool DeveloperTools
        {
            get => m_DeveloperTools;
            set => m_DeveloperTools = value;
        }

        // The settings UI calls this by name through SettingsUIHideByCondition; it is
        // not dead code, and it cannot be static for the same reason the read-only
        // string properties cannot be.
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The game's settings UI resolves the condition on the "
                + "instance by reflection.")]
        public bool DeveloperToolsOff() => !m_DeveloperTools;

        [SettingsUIHidden]
        public string RidershipData
        {
            get => m_RidershipData ?? string.Empty;
            set => m_RidershipData = value ?? string.Empty;
        }

        public override void SetDefaults()
        {
            m_Mode = ModePreset.Bus;
            m_ShowHeatmap = true;
            m_DeveloperTools = false;
            m_HighlightShare = Assumptions.HighlightShareDefaultPercent;
            m_MaxSlope = Assumptions.MaxSlopeDefaultDegrees;
            m_EquityWalkMinutes = Assumptions.EquityWalkMinutesDefault;
            m_EquityFloorPercent = Assumptions.EquityFloorDefaultPercent;
            m_UtilisationFloorPercent = Assumptions.UtilisationFloorDefaultPercent;
            m_SiteCount = Assumptions.SiteCountDefault;
            m_RidershipData = string.Empty;
            m_Objective = RouteGoal.Balanced;
            m_RouteCount = Assumptions.RouteCountDefault;
            m_ShowRoutes = true;
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
            m_HighlightShare = m_HighlightShare == 0 ? Assumptions.HighlightShareDefaultPercent : ClampInt(m_HighlightShare, kHighlightMin, kHighlightMax);
            m_MaxSlope = m_MaxSlope == 0 ? Assumptions.MaxSlopeDefaultDegrees : ClampInt(m_MaxSlope, kSlopeMin, kSlopeMax);
            m_EquityWalkMinutes = m_EquityWalkMinutes == 0 ? Assumptions.EquityWalkMinutesDefault : ClampInt(m_EquityWalkMinutes, kEquityMinutesMin, kEquityMinutesMax);
            m_EquityFloorPercent = m_EquityFloorPercent == 0 ? Assumptions.EquityFloorDefaultPercent : ClampInt(m_EquityFloorPercent, kEquityFloorMin, kEquityFloorMax);
            m_UtilisationFloorPercent = m_UtilisationFloorPercent == 0 ? Assumptions.UtilisationFloorDefaultPercent : ClampInt(m_UtilisationFloorPercent, kUtilisationMin, kUtilisationMax);
            m_SiteCount = m_SiteCount == 0 ? Assumptions.SiteCountDefault : ClampInt(m_SiteCount, kSiteCountMin, kSiteCountMax);
            m_RouteCount = m_RouteCount == 0 ? Assumptions.RouteCountDefault : ClampInt(m_RouteCount, kRouteCountMin, kRouteCountMax);
            m_Objective = ValidObjective(m_Objective);
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
                { m_Setting.GetSettingsLocaleID(), "Transit Architect" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "General" },
                { m_Setting.GetOptionTabLocaleID(Setting.kAdvancedSection), "Advanced" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlanningGroup), "Planning" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Service standards" },
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
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W4)), "Accessibility discount" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W4)), "How much the walk from a tile to its nearest pavement lowers the score: at 1 the tile scores the pavement times the walking-time kernel, at 0 the walk is free. A pavement with nothing to reach scores nothing at any setting." },
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
                { "TransitArchitect.Panel[BuildingWalk]", "Walk to transit" },
                { "TransitArchitect.Panel[BuildingWalkServed]", "to the nearest stop your lines serve" },
                { "TransitArchitect.Panel[BuildingWalkUnserved]", "further than the {0} min this city counts as served" },
                { "TransitArchitect.Panel[WalkNone]", "no stop in reach" },
                { "TransitArchitect.Panel[WalkMinutes]", "{0} min" },
                { "TransitArchitect.Panel[Equity]", "Served journeys" },
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

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowHeatmap)), "Show the suitability map" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowHeatmap)), "Opens the mod's own infoview: a green-to-red map of where a new stop of the chosen mode would do the most good. The game's infoview menu holds the same switch." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DeveloperTools)), "Developer tools" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DeveloperTools)), "Shows the mod's own diagnostics in this page, including the verification export. Nothing here affects a normal game." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowRoutes)), "Show suggested routes" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowRoutes)), "Draw the suggested lines and their stops on the map while this infoview is open." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Objective)), "Route objective" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Objective)), "What a suggested line is grown to achieve. Maximum ridership follows the busiest journeys; maximum coverage spreads out to reach more districts even where demand is thin; balanced does both." },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Ridership), "Maximum ridership" },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Balanced), "Balanced" },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Coverage), "Maximum coverage" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteCount)), "Suggested lines" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteCount)), "How many lines to suggest. Each one takes the demand it would carry out of the pool, so later suggestions complement the earlier ones." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CalibrationStatus)), "Model quality" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CalibrationStatus)), "The mod samples ridership at your served stops while the game runs, then fits the weights to it." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyFittedWeights)), "Apply fitted weights" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyFittedWeights)), "Overwrite the demand, jobs, accessibility and future weights with the fitted values above. Does nothing until enough samples have been collected." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetRidershipData)), "Reset collected samples" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetRidershipData)), "Discard all collected ridership samples and start over. Useful after reshaping your network." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ExportVerificationInstance)), "Export verification instance" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ExportVerificationInstance)), "Write this city's scoring inputs and results to ModsData/TransitArchitect/verification as canonical JSON, for the offline verification pipeline. Read-only: it exports what the mod already computed and changes nothing. The files are written after the next recalculation; the mod log names the folder." },

                { "TransitArchitect.Infomode", "Station Suitability" },
                { "Infoviews.INFOVIEW[TransitArchitect]", "Transit Architect" },
                { "Infoviews.INFOVIEW_TOOLTIP[TransitArchitect]", "Where a new stop would do the most good, and how far each building is from the service you already run." },

                { "Infoviews.INFOMODE[TransitArchitect]", "Station Suitability" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitect]", "Combined score. Green-yellow-red heatmap of station placement quality; how many tiles count as “best” is the Highlight share option." },
                { "Infoviews.INFOMODE[TransitArchitectTransitAccess]", "Walk to transit (buildings)" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectTransitAccess]", "Colours every building by the walk from its door to the nearest stop your lines actually serve: green is a short walk, red is at or beyond the walking horizon set under Service standards." },
                { "Infoviews.INFOMODE[TransitArchitectSites]", "Recommended sites" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectSites]", "The best distinct candidate locations, spaced at least one catchment apart and ranked by walk-distance score." },
                { "Infoviews.INFOMODE[TransitArchitectDemand]", "Demand (residents)" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectDemand]", "Residents reachable within the catchment radius, on the same landmass." },
                { "Infoviews.INFOMODE[TransitArchitectJobs]", "Jobs" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectJobs]", "Workplace capacity reachable within the catchment radius." },
                { "Infoviews.INFOMODE[TransitArchitectCoverage]", "Existing coverage" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectCoverage]", "How well existing stops of the selected mode already serve each tile." },
                { "Infoviews.INFOMODE[TransitArchitectAccess]", "Accessibility" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectAccess]", "Walking time from each tile to the nearest pavement a stop could stand on." },
                { "Infoviews.INFOMODE[TransitArchitectFuture]", "Future demand (zoned)" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectFuture]", "Land that is zoned but not yet built on." },
                { "Infoviews.INFOMODE[TransitArchitectInterchange]", "Interchange potential" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectInterchange]", "Where a stop of this mode would sit within transfer distance of another mode's service, weighted by how much capacity that mode carries." },
                { "Infoviews.INFOMODE[TransitArchitectTravelDemand]", "Travel demand" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectTravelDemand]", "Where people actually want to travel, from real home-to-work and home-to-school journeys. Shows the demand your network does not already carry." },
                { "Infoviews.INFOMODE[TransitArchitectCrossCoverage]", "Cross-mode overlap" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectCrossCoverage]", "Where another mode already serves the same riders but is too far away to transfer to." },

                // The infoview panel composes gradient legend label keys as
                // Infoviews.LABEL[<labelId>].
                { "Infoviews.LABEL[TransitArchitect.Legend.Low]", "Low" },
                { "Infoviews.LABEL[TransitArchitect.Legend.Medium]", "Medium" },
                { "Infoviews.LABEL[TransitArchitect.Legend.High]", "High" },
            };

            foreach (KeyValuePair<string, string> panel in PanelEntries())
            {
                entries.Add(panel.Key, panel.Value);
            }

            return entries;
        }

        // Strings the mod's own panel resolves through cs2/l10n. Kept apart from the
        // block above because they have a different consumer: those are rendered by the
        // game's Options UI, these by TransitArchitect.mjs.
        private static Dictionary<string, string> PanelEntries()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Control panel strings. The panel resolves these itself through cs2/l10n with
                // the English text inline as a fallback, so a key missing here shows English
                // rather than a raw key.
                // Status lines the Options page prints verbatim (see Loc).
                { "TransitArchitect.Status[Calibration.Waiting]", "Waiting for a city to load." },
                { "TransitArchitect.Status[Calibration.Collecting]", "Collecting while unpaused: {0} of {1} stops ready, {2} tracked (need {3} samples each)" },
                { "TransitArchitect.Status[Calibration.Fit]", "R² {0} over {1} stops — suggested: demand {2}, jobs {3}, future {4}" },
                { "TransitArchitect.Panel[EquityCaption]", "reach a served stop within {0} min at both ends \u00b7 target {1} % \u00b7 Gini {2}" },
                { "TransitArchitect.Panel[DataBasisCaption]", "of the last {0} h \u00b7 {1} readings" },
                { "TransitArchitect.Panel[DataBasisNone]", "nothing yet" },
                { "TransitArchitect.Panel[NoteApply]", "apply" },
                { "TransitArchitect.Panel[NoteShow]", "show me" },
                { "TransitArchitect.Panel[NoSuggestions]", "nothing worth adding right now \u2014 let the city run" },
                { "TransitArchitect.Panel[SuggestionsTab]", "SUGGESTIONS" },
                { "TransitArchitect.Panel[SuggestedLines]", "Suggested lines" },
                { "TransitArchitect.Panel[ColMode]", "Mode" },
                { "TransitArchitect.Panel[ColLength]", "Length" },
                { "TransitArchitect.Panel[ColStops]", "Stops" },
                { "TransitArchitect.Panel[ColVehicles]", "Vehicles" },
                { "TransitArchitect.Panel[ColSchedule]", "Runs" },
                { "TransitArchitect.Panel[ColReach]", "Unlocks" },
                { "TransitArchitect.Panel[Km]", "km" },
                { "TransitArchitect.Panel[Vehicles]", "veh" },
                { "TransitArchitect.Panel[RouteUpdate]", "{0} new suggestions ready \u2014 apply" },
                { "TransitArchitect.Panel[Schedule.DayAndNight]", "all day" },
                { "TransitArchitect.Panel[Schedule.Day]", "by day only (06:00\u201322:00)" },
                { "TransitArchitect.Panel[Schedule.Night]", "by night only (22:00\u201306:00)" },
                { "TransitArchitect.Panel[DataBasis]", "Data collected" },
                { "TransitArchitect.Panel[DataBasisEmpty]", "readings start with your first line" },
                { "TransitArchitect.Panel[ObservedTrips]", "{0} shopping/leisure journeys seen over {1} h" },
                { "TransitArchitect.Panel[ObservedTripsEmpty]", "no shopping/leisure journeys seen yet" },
                { "TransitArchitect.Panel[Mode.Bus]", "Bus" },
                { "TransitArchitect.Panel[Mode.Tram]", "Tram" },
                { "TransitArchitect.Panel[Mode.Metro]", "Metro" },
                { "TransitArchitect.Panel[Mode.Train]", "Train" },
                { "TransitArchitect.Panel[Mode.Ferry]", "Ferry" },
                { "TransitArchitect.Panel[Cell.Split]", "split" },
                { "TransitArchitect.Panel[Cell.Remove]", "remove" },
                { "TransitArchitect.Panel[CellLoad]", "{0}% full at the recommended fleet" },
                { "TransitArchitect.Panel[PlanDrawn]", "the re-traced route is on the map" },
                { "TransitArchitect.Panel[Verdict.Healthy]", "healthy" },
                { "TransitArchitect.Panel[Verdict.FleetShort]", "fleet short — the game cannot supply the vehicles it wants" },
                { "TransitArchitect.Panel[Verdict.FleetShort.Arg]", "fleet short — the game wants {0} more vehicle(s) than it can supply" },
                { "TransitArchitect.Panel[Verdict.ModeUp]", "too big for its mode" },
                { "TransitArchitect.Panel[Verdict.ModeUp.Arg]", "too big for its mode — upgrade to {0}" },
                { "TransitArchitect.Panel[Verdict.SplitRoute]", "beyond the largest fleet of any mode — split the route" },
                { "TransitArchitect.Panel[Verdict.Remove]", "empty and unjustified even as the smallest service — reroute or remove" },
                { "TransitArchitect.Panel[Verdict.ModeDown]", "a smaller vehicle would do" },
                { "TransitArchitect.Panel[Verdict.ModeDown.Arg]", "a smaller vehicle would do — run it as {0}" },
                { "TransitArchitect.Panel[Verdict.FleetUp]", "add vehicles" },
                { "TransitArchitect.Panel[Verdict.FleetUp.Arg]", "add {0} vehicle(s)" },
                { "TransitArchitect.Panel[Verdict.FleetDown]", "remove vehicles" },
                { "TransitArchitect.Panel[Verdict.FleetDown.Arg]", "remove {0} vehicle(s)" },
                { "TransitArchitect.Panel[Verdict.Schedule]", "change the schedule" },
                { "TransitArchitect.Panel[Verdict.Schedule.Arg]", "run it {0}" },
                { "TransitArchitect.Panel[Plan.Fleet]", "run it as {0} with {1} vehicle(s)" },
                { "TransitArchitect.Panel[Plan.Delta]", " ({0})" },
                { "TransitArchitect.Panel[Plan.Interval]", ", i.e. an interval of about {0} s" },
                { "TransitArchitect.Panel[Plan.Span]", ", the game allows {0} to {1}" },
                { "TransitArchitect.Panel[Plan.Split]", "; split it — {0} km is more than the largest {1} fleet can carry" },
                { "TransitArchitect.Panel[Plan.Reroute]", "; or reroute it through denser ground — the suggestions list shows where demand is unserved" },
                { "TransitArchitect.Panel[Plan.Fine]", "; the route shape looks reasonable" },
            };
        }

        public void Unload()
        {
        }
    }
}
