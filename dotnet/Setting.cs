using System;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace WhereTheyGo
{
    // One tab: after the 2026-09-14 recut there are two settings, and neither of
    // them needs explaining.
    [FileLocation(nameof(WhereTheyGo))]
    [SettingsUIGroupOrder(kPlanningGroup, kStandardsGroup)]
    [SettingsUIShowGroupName(kPlanningGroup, kStandardsGroup)]
    public sealed class Setting : ModSetting
    {
        // Tabs. The strings are UI ids, not persistence keys (settings files key on
        // the PROPERTY name), so they are free to change.
        public const string kSection = "General";

        // Groups of the General tab.
        public const string kPlanningGroup = "Planning";
        public const string kStandardsGroup = "Standards";

        // The walking horizon "served" means: a door this far in walking time from a
        // stop a line calls at counts as connected (register A1.8).
        public const int kCoverageMinutesMin = 5;
        public const int kCoverageMinutesMax = 20;

        private int m_CoverageWalkMinutes;
        private bool m_ShowInfoview = true;

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISlider(min = kCoverageMinutesMin, max = kCoverageMinutesMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kStandardsGroup)]
        public int CoverageWalkMinutes
        {
            get => m_CoverageWalkMinutes;
            set => m_CoverageWalkMinutes = ClampInt(value, kCoverageMinutesMin, kCoverageMinutesMax);
        }

        // The infoview itself. The game's infoview menu switches it too. This is the
        // same switch in the place a player looks for settings, and it is persisted so
        // a city opens the way it was left.
        [SettingsUISection(kSection, kPlanningGroup)]
        public bool ShowInfoview
        {
            get => m_ShowInfoview;
            set
            {
                m_ShowInfoview = value;
                WhereTheyGoSystem.RequestInfoview(value);
            }
        }

        public override void SetDefaults()
        {
            m_ShowInfoview = true;
            m_CoverageWalkMinutes = Assumptions.CoverageWalkMinutesDefault;
        }

        // Called once after settings are loaded from disk. The property setter already
        // clamps, but a settings file is user-editable and deserialization is the game's
        // to arrange, so the range is enforced once more where the values arrive rather
        // than trusted to the path they came in by.
        public void ClampAll()
        {
            m_CoverageWalkMinutes = ClampInt(m_CoverageWalkMinutes, kCoverageMinutesMin, kCoverageMinutesMax);
        }

        // The game targets .NET Framework, which has no Math.Clamp.
        private static int ClampInt(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        // The strings that do not depend on the Setting instance. They live here rather
        // than inside ReadEntries because they are DATA: eighty-odd rows in a method
        // body make every method-length rule measure the wrong thing, and adding one
        // locale key should not be a structural event. Only the handful of entries whose
        // key comes from the Setting object are built in the method below.
        private static readonly Dictionary<string, string> Literals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "WhereTheyGo.Panel[BuildingWalk]", "Nearest served stop" },
            { "WhereTheyGo.Panel[BuildingWalkServed]", "over the pedestrian network, to a stop your lines actually call at" },
            { "WhereTheyGo.Panel[BuildingWalkUnserved]", "further than the {0} min this city counts as served" },
            { "WhereTheyGo.Panel[WalkNone]", "no stop in reach" },
            { "WhereTheyGo.Panel[WalkMinutes]", "{0} min" },
            { "WhereTheyGo.Panel[TimeOfDay]", "Time of day" },
            { "WhereTheyGo.Panel[WholeDay]", "All day" },
            { "WhereTheyGo.Panel[AtHour]", "{0}:00" },
            { "WhereTheyGo.Panel[PurposeWork]", "Work" },
            { "WhereTheyGo.Panel[PurposeSchool]", "School" },
            { "WhereTheyGo.Panel[PurposeShopping]", "Shopping" },
            { "WhereTheyGo.Panel[PurposeLeisure]", "Leisure" },
            { "WhereTheyGo.Panel[LineRiders]", "Journeys using this line" },
            { "WhereTheyGo.Panel[LineMeasuring]", "measuring\u2026" },
            { "WhereTheyGo.Panel[LineMeasuringCaption]", "routing the city again without this line" },
            { "WhereTheyGo.Panel[LineSaved]", "Rider-minutes saved, a day" },
            { "WhereTheyGo.Panel[LineHours]", "How full it runs, hour by hour" },
            { "WhereTheyGo.Panel[LineHoursEmpty]", "no readings yet; they start once the line runs" },
            { "WhereTheyGo.Panel[BandJourneys]", "{0} journeys a day" },
            { "WhereTheyGo.Panel[BandWithout]", "{0} % travel without transit" },
            { "WhereTheyGo.Panel[BandsHide]", "Hide weak corridors" },
            { "WhereTheyGo.Panel[BandsUnder]", "under {0} %" },
            { "WhereTheyGo.Panel[BandsVisible]", "{0} corridors drawn" },
            { "WhereTheyGo.Panel[BandLength]", "{0} apart" },
            { "WhereTheyGo.Panel[Detail]", "In detail" },
            { "WhereTheyGo.Panel[WalkClasses]", "Walk to a served stop" },
            { "WhereTheyGo.Panel[WalkClassNear]", "under {0} min" },
            { "WhereTheyGo.Panel[WalkClassMid]", "{0}\u2013{1} min" },
            { "WhereTheyGo.Panel[WalkClassNone]", "no stop" },
            { "WhereTheyGo.Panel[Network]", "Your network" },
            { "WhereTheyGo.Panel[NetworkLines]", "{0} lines" },
            { "WhereTheyGo.Panel[NetworkStops]", "{0} served stops" },
            { "WhereTheyGo.Panel[NetworkJourneys]", "{0} journeys a day" },
            { "WhereTheyGo.Panel[HourDepartures]", "{0} departures" },
            { "WhereTheyGo.Panel[HourCarried]", "{0} % of them carried" },
            { "WhereTheyGo.Panel[LineFaster]", "Faster with it" },
            { "WhereTheyGo.Panel[LineFasterCaption]", "the other {0} % take just as long without it: there the line runs beside something that already carries them" },
            { "WhereTheyGo.Panel[LineStanding]", "Among your lines" },
            { "WhereTheyGo.Panel[LineStandingValue]", "{0} of {1}" },
            { "WhereTheyGo.Panel[LineStandingCaption]", "by journeys a day; this line carries {0} % of the city's travel" },
            { "WhereTheyGo.Panel[LineWait]", "Average wait" },
            { "WhereTheyGo.Panel[LineWaitCaption]", "what the routing charges every rider, from the interval this line actually keeps" },
            { "WhereTheyGo.Panel[LinePeak]", "Peak load" },
            { "WhereTheyGo.Panel[LinePeakValue]", "{0} of {1} seats" },
            { "WhereTheyGo.Panel[LinePeakWindow]", "the busiest single reading in the window" },
            { "WhereTheyGo.Panel[LinePeakInstant]", "from this reading alone; the window has not filled yet" },
            { "WhereTheyGo.Panel[LineLoop]", "Round trip" },
            { "WhereTheyGo.Panel[LineLoopCaption]", "as driven; {0} in free flow" },
            { "WhereTheyGo.Panel[LineLoopFloorCaption]", "at least this long; {0} in free flow. The game's own timing for this line is unusable, so this is a floor rather than a measurement." },
            { "WhereTheyGo.Panel[LineHoursAxis]", "the top of the scale is this line's own busiest hour, not a full vehicle" },
            { "WhereTheyGo.Panel[LineHourValue]", "{0} \u00b7 {1} %" },
            { "WhereTheyGo.Panel[LineHourGap]", "{0} \u00b7 not watched" },
            { "WhereTheyGo.Panel[ToolbarTooltip]", "Where They Go: the journeys your city makes, and who already rides" },
            { "WhereTheyGo.Panel[Purposes]", "Journeys by purpose" },
            { "WhereTheyGo.Panel[HiddenShare]", "{0} % of the journeys" },
            { "WhereTheyGo.Panel[ClassCaption]", "width: journeys a day" },
            { "WhereTheyGo.Panel[ClassCaptionAtHour]", "width: departures at {0}" },
            { "WhereTheyGo.Panel[ClassUnder]", "under {0}" },
            { "WhereTheyGo.Panel[ClassOver]", "{0} and more" },
            { "WhereTheyGo.Panel[BandPeak]", "busiest at {0}" },
            { "WhereTheyGo.Panel[BuildingSection]", "Walk to transit" },
            { "WhereTheyGo.Panel[LineSection]", "Where they go" },
            { "WhereTheyGo.Panel[LineSavedCaption]", "{0} min per journey, against walking and the rest of your network" },
            { "WhereTheyGo.Panel[BandJourneysAtHour]", "{0} journeys at {1}" },
            { "WhereTheyGo.Panel[BandJourneysDay]", "{0} a day in all" },
            { "WhereTheyGo.Panel[Carried]", "Carried by transit" },
            { "WhereTheyGo.Panel[CarriedCaption]", "of all journeys; a journey counts when transit makes it faster than walking" },
            { "WhereTheyGo.Panel[Coverage]", "Within walking distance" },


            { "WhereTheyGo.Infomode", "Where They Go" },
            { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
            { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "Where the city wants to go, and how far each building is from the service you already run." },

            { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "Desire lines" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "Every journey the city makes, bundled into bands between its two ends. Warm means nobody rides there; cool means your network already carries them." },
            { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "Walk to transit" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "Colours every building by the walk from its door to the nearest stop your lines actually serve: pale is a short walk, deep red is at or beyond the walking horizon you set under Service standards." },

            // The infoview panel composes gradient legend label keys as
            // Infoviews.LABEL[<labelId>].
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "Nobody rides" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "Half" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "All carried" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "At a stop" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "Halfway" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "Too far to walk" },

            { "WhereTheyGo.Panel[CoverageCaption]", "reach a served stop within {0} min at both ends" },
        };

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Where They Go" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "General" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlanningGroup), "Planning" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Service standards" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "Walking horizon for \"served\" (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "A journey counts as served when both its ends are within this many minutes' walk of a served stop." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowInfoview)), "Show the infoview" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowInfoview)), "Opens the mod's infoview. The game's infoview menu holds the same switch." },
            };

            foreach (KeyValuePair<string, string> entry in Literals)
            {
                entries.Add(entry.Key, entry.Value);
            }

            return entries;
        }

        public void Unload()
        {
        }
    }
}
