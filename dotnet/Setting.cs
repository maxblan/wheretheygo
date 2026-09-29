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
    // One tab, one setting. An earlier recut left two; then the "Show the
    // infoview" switch went as well, because the top-left toolbar button is the
    // only way in (README, "What it is") and a persisted true reopened the infoview
    // on the first frame of every session whether or not the player wanted it.
    //
    // UNVERIFIED: whether the game's settings decoder ignores a stale
    // ShowInfoview member in an existing WhereTheyGo.coc, or rejects the file. If it
    // rejects it, AssetDatabase.LoadSettings falls back to the `new Setting(this)`
    // Mod.OnLoad hands it, which is the game's own path for a file it cannot load, so
    // the worst case is defaults. To confirm by loading once with an old file.
    [FileLocation(nameof(WhereTheyGo))]
    [SettingsUIGroupOrder(kStandardsGroup)]
    [SettingsUIShowGroupName(kStandardsGroup)]
    public sealed class Setting : ModSetting
    {
        // Tabs. The strings are UI ids, not persistence keys (settings files key on
        // the PROPERTY name), so they are free to change.
        public const string kSection = "General";

        // The one group of the General tab.
        public const string kStandardsGroup = "Standards";

        // The walking horizon "served" means: a door this far in walking time from a
        // stop a line calls at counts as connected.
        public const int kCoverageMinutesMin = 5;
        public const int kCoverageMinutesMax = 20;

        private int m_CoverageWalkMinutes;

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

        public override void SetDefaults()
        {
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
            // Both halves of "carried" (JourneyRouting): faster than walking the whole
            // way AND under the ceiling drawn from this city's own median carried
            // journey. The multiple stays in Assumptions.ServedCeilingMultiple, not here.
            // "Longer than a walk": the journeys within the walking
            // horizon are out of the figure on both sides, and the caption below says
            // how many they are.
            { "WhereTheyGo.Panel[CarriedCaption]", "of the journeys longer than a walk; one counts when transit beats walking the whole way and is not far slower than this city's typical transit journey" },
            { "WhereTheyGo.Panel[WalkedCaption]", "{0} % of the city's journeys are shorter than the walking horizon: a walk, not a transit question, and left out of the figures above" },
            { "WhereTheyGo.Panel[WalkedFolded]", "{0} journeys a day walked, their corridors not drawn" },
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
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Service standards" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "Walking horizon for \"served\" (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "A journey counts as served when both its ends are within this many minutes' walk of a served stop." },
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
