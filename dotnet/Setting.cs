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
        private bool m_ShowHeatmap = true;

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

        // The infoview itself. The game's infoview menu switches it too — this is the
        // same switch in the place a player looks for settings, and it is persisted so
        // a city opens the way it was left.
        [SettingsUISection(kSection, kPlanningGroup)]
        public bool ShowHeatmap
        {
            get => m_ShowHeatmap;
            set
            {
                m_ShowHeatmap = value;
                WhereTheyGoSystem.RequestInfoview(value);
            }
        }

        public override void SetDefaults()
        {
            m_ShowHeatmap = true;
            m_CoverageWalkMinutes = Assumptions.CoverageWalkMinutesDefault;
        }

        // Called before LoadSettings: zeroing the tuning fields lets ClampAll tell
        // whether the loaded file actually contained them (releases before 1.1
        // didn't persist tuning keys, so nothing overwrites the zeros).
        public void MarkTuningUnset()
        {
            m_CoverageWalkMinutes = 0;
        }

        // Called once after settings are loaded from disk to sanitize persisted
        // values. A tuning field still zero was absent from the file and gets its
        // default.
        public void ClampAll()
        {
            m_CoverageWalkMinutes = m_CoverageWalkMinutes == 0 ? Assumptions.CoverageWalkMinutesDefault : ClampInt(m_CoverageWalkMinutes, kCoverageMinutesMin, kCoverageMinutesMax);
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
                { "WhereTheyGo.Panel[BuildingWalk]", "Walk to transit" },
                { "WhereTheyGo.Panel[BuildingWalkServed]", "to the nearest stop your lines serve" },
                { "WhereTheyGo.Panel[BuildingWalkUnserved]", "further than the {0} min this city counts as served" },
                { "WhereTheyGo.Panel[WalkNone]", "no stop in reach" },
                { "WhereTheyGo.Panel[WalkMinutes]", "{0} min" },
                { "WhereTheyGo.Panel[PlayDay]", "Play the day" },
                { "WhereTheyGo.Panel[TimeOfDay]", "Time of day" },
                { "WhereTheyGo.Panel[WholeDay]", "All day" },
                { "WhereTheyGo.Panel[AtHour]", "{0}:00" },
                { "WhereTheyGo.Panel[Threshold]", "Hide bands under" },
                { "WhereTheyGo.Panel[PurposeWork]", "Work" },
                { "WhereTheyGo.Panel[PurposeSchool]", "School" },
                { "WhereTheyGo.Panel[PurposeShopping]", "Shopping" },
                { "WhereTheyGo.Panel[PurposeLeisure]", "Leisure" },
                { "WhereTheyGo.Panel[LineRiders]", "Journeys using this line" },
                { "WhereTheyGo.Panel[LineMeasuring]", "measuring\u2026" },
                { "WhereTheyGo.Panel[LineMeasuringCaption]", "routing the city again without this line" },
                { "WhereTheyGo.Panel[LineSaved]", "saving {0} passenger-minutes a day against walking and the rest of your network" },
                { "WhereTheyGo.Panel[LineDuplicate]", "No slower without it" },
                { "WhereTheyGo.Panel[LineDuplicateCaption]", "of those journeys would be no slower if this line did not exist" },
                { "WhereTheyGo.Panel[LineHours]", "How full it runs, hour by hour" },
                { "WhereTheyGo.Panel[LineHoursEmpty]", "no readings yet \u2014 they start once the line runs" },
                { "WhereTheyGo.Panel[BandJourneys]", "Journeys on this band" },
                { "WhereTheyGo.Panel[BandWithout]", "{0} % of them with no transit \u00b7 busiest at {1}:00" },
                { "WhereTheyGo.Panel[Carried]", "Carried by transit" },
                { "WhereTheyGo.Panel[CarriedCaption]", "of all journeys, counting those transit makes faster than walking" },
                { "WhereTheyGo.Panel[Coverage]", "Within walking distance" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowHeatmap)), "Show the infoview" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowHeatmap)), "Opens the mod's infoview. The game's infoview menu holds the same switch." },

                { "WhereTheyGo.Infomode", "Where They Go" },
                { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
                { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "How far each building is from the service you already run." },

                { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "Desire lines" },
                { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "Every journey the city makes, bundled into bands between the places people travel between. Warm means nobody rides: the network does not carry that travel. Cool means it does." },
                { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "Walk to transit (buildings)" },
                { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "Colours every building by the walk from its door to the nearest stop your lines actually serve: green is a short walk, red is at or beyond the walking horizon set under Service standards." },

                // The infoview panel composes gradient legend label keys as
                // Infoviews.LABEL[<labelId>].
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "Nobody rides" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "Half" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "All carried" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "At a stop" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "Halfway" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "Too far to walk" },
            };

            foreach (KeyValuePair<string, string> panel in PanelEntries())
            {
                entries.Add(panel.Key, panel.Value);
            }

            return entries;
        }

        // Strings the mod's own panel resolves through cs2/l10n. Kept apart from the
        // block above because they have a different consumer: those are rendered by the
        // game's Options UI, these by WhereTheyGo.mjs.
        private static Dictionary<string, string> PanelEntries()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Control panel strings. The panel resolves these itself through cs2/l10n with
                // the English text inline as a fallback, so a key missing here shows English
                // rather than a raw key.
                // Status lines the Options page prints verbatim (see Loc).
                { "WhereTheyGo.Panel[CoverageCaption]", "reach a served stop within {0} min at both ends \u00b7 Gini {1}" },
                { "WhereTheyGo.Panel[DataBasisCaption]", "of the last {0} h \u00b7 {1} readings" },
                { "WhereTheyGo.Panel[DataBasisNone]", "nothing yet" },
                { "WhereTheyGo.Panel[DataBasis]", "Data collected" },
                { "WhereTheyGo.Panel[DataBasisEmpty]", "readings start with your first line" },
                { "WhereTheyGo.Panel[ObservedTrips]", "{0} shopping/leisure journeys seen over {1} h" },
                { "WhereTheyGo.Panel[ObservedTripsEmpty]", "no shopping/leisure journeys seen yet" },
            };
        }

        public void Unload()
        {
        }
    }
}
