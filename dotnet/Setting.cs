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
    // One tab: after the 2026-09-14 recut there are two settings, and neither of
    // them needs explaining.
    [FileLocation(nameof(TransitArchitect))]
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
        public const int kEquityMinutesMin = 5;
        public const int kEquityMinutesMax = 20;

        private int m_EquityWalkMinutes;
        private bool m_ShowHeatmap = true;

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISlider(min = kEquityMinutesMin, max = kEquityMinutesMax, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kStandardsGroup)]
        public int EquityWalkMinutes
        {
            get => m_EquityWalkMinutes;
            set => m_EquityWalkMinutes = ClampInt(value, kEquityMinutesMin, kEquityMinutesMax);
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
                TransitArchitectSystem.RequestInfoview(value);
            }
        }

        public override void SetDefaults()
        {
            m_ShowHeatmap = true;
            m_EquityWalkMinutes = Assumptions.EquityWalkMinutesDefault;
        }

        // Called before LoadSettings: zeroing the tuning fields lets ClampAll tell
        // whether the loaded file actually contained them (releases before 1.1
        // didn't persist tuning keys, so nothing overwrites the zeros).
        public void MarkTuningUnset()
        {
            m_EquityWalkMinutes = 0;
        }

        // Called once after settings are loaded from disk to sanitize persisted
        // values. A tuning field still zero was absent from the file and gets its
        // default.
        public void ClampAll()
        {
            m_EquityWalkMinutes = m_EquityWalkMinutes == 0 ? Assumptions.EquityWalkMinutesDefault : ClampInt(m_EquityWalkMinutes, kEquityMinutesMin, kEquityMinutesMax);
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
                { m_Setting.GetSettingsLocaleID(), "Transit Architect" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "General" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlanningGroup), "Planning" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Service standards" },





                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EquityWalkMinutes)), "Equity: walking horizon (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EquityWalkMinutes)), "A journey counts as served when both its ends are within this many minutes' walk of a served stop." },
                { "TransitArchitect.Panel[BuildingWalk]", "Walk to transit" },
                { "TransitArchitect.Panel[BuildingWalkServed]", "to the nearest stop your lines serve" },
                { "TransitArchitect.Panel[BuildingWalkUnserved]", "further than the {0} min this city counts as served" },
                { "TransitArchitect.Panel[WalkNone]", "no stop in reach" },
                { "TransitArchitect.Panel[WalkMinutes]", "{0} min" },
                { "TransitArchitect.Panel[Equity]", "Served journeys" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowHeatmap)), "Show the infoview" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowHeatmap)), "Opens the mod's infoview. The game's infoview menu holds the same switch." },

                { "TransitArchitect.Infomode", "Transit Architect" },
                { "Infoviews.INFOVIEW[TransitArchitect]", "Transit Architect" },
                { "Infoviews.INFOVIEW_TOOLTIP[TransitArchitect]", "How far each building is from the service you already run." },

                { "Infoviews.INFOMODE[TransitArchitectTransitAccess]", "Walk to transit (buildings)" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectTransitAccess]", "Colours every building by the walk from its door to the nearest stop your lines actually serve: green is a short walk, red is at or beyond the walking horizon set under Service standards." },

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
                { "TransitArchitect.Panel[EquityCaption]", "reach a served stop within {0} min at both ends \u00b7 target {1} % \u00b7 Gini {2}" },
                { "TransitArchitect.Panel[DataBasisCaption]", "of the last {0} h \u00b7 {1} readings" },
                { "TransitArchitect.Panel[DataBasisNone]", "nothing yet" },
                { "TransitArchitect.Panel[DataBasis]", "Data collected" },
                { "TransitArchitect.Panel[DataBasisEmpty]", "readings start with your first line" },
                { "TransitArchitect.Panel[ObservedTrips]", "{0} shopping/leisure journeys seen over {1} h" },
                { "TransitArchitect.Panel[ObservedTripsEmpty]", "no shopping/leisure journeys seen yet" },
            };
        }

        public void Unload()
        {
        }
    }
}
