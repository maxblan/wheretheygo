using Colossal;
using System.Collections.Generic;
using System;

namespace WhereTheyGo
{
    // German translation. Falls back to the en-US source for any key not listed
    // here, so a missing entry degrades to English rather than showing a raw key.
    public class LocaleDE : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleDE(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Where They Go" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Allgemein" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlanningGroup), "Planung" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Angebotsstandards" },





                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "Gerechtigkeit: Gehzeit-Horizont (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "Ein Weg gilt als bedient, wenn beide Enden innerhalb so vieler Gehminuten einer bedienten Haltestelle liegen." },
                { "WhereTheyGo.Panel[BuildingWalk]", "Nächste bediente Haltestelle" },
                { "WhereTheyGo.Panel[BuildingWalkServed]", "über das Fußwegenetz, zu einer Haltestelle, die deine Linien wirklich anfahren" },
                { "WhereTheyGo.Panel[BuildingWalkUnserved]", "weiter als die {0} min, die hier als bedient gelten" },
                { "WhereTheyGo.Panel[WalkNone]", "keine Haltestelle erreichbar" },
                { "WhereTheyGo.Panel[WalkMinutes]", "{0} min" },
                { "WhereTheyGo.Panel[TimeOfDay]", "Tageszeit" },
                { "WhereTheyGo.Panel[WholeDay]", "Ganzer Tag" },
                { "WhereTheyGo.Panel[AtHour]", "{0}:00 Uhr" },
                { "WhereTheyGo.Panel[PurposeWork]", "Arbeit" },
                { "WhereTheyGo.Panel[PurposeSchool]", "Schule" },
                { "WhereTheyGo.Panel[PurposeShopping]", "Einkauf" },
                { "WhereTheyGo.Panel[PurposeLeisure]", "Freizeit" },
                { "WhereTheyGo.Panel[LineRiders]", "Wege über diese Linie" },
                { "WhereTheyGo.Panel[LineMeasuring]", "wird gemessen\u2026" },
                { "WhereTheyGo.Panel[LineMeasuringCaption]", "die Stadt wird noch einmal ohne diese Linie durchgerechnet" },
                { "WhereTheyGo.Panel[LineSaved]", "Gesparte Minuten am Tag" },
                { "WhereTheyGo.Panel[LineDuplicate]", "Ohne sie nicht langsamer" },
                { "WhereTheyGo.Panel[LineDuplicateCaption]", "dieser Wege wären genauso schnell, wenn es die Linie nicht gäbe" },
                { "WhereTheyGo.Panel[LineHours]", "Auslastung Stunde für Stunde" },
                { "WhereTheyGo.Panel[LineHoursEmpty]", "noch keine Messungen \u2014 sie beginnen, sobald die Linie fährt" },
                { "WhereTheyGo.Panel[BandJourneys]", "{0} Wege am Tag" },
                { "WhereTheyGo.Panel[BandWithout]", "{0} % davon ohne ÖPNV" },
                { "WhereTheyGo.Panel[Purposes]", "Wege nach Zweck" },
                { "WhereTheyGo.Panel[Bands]", "Bänder" },
                { "WhereTheyGo.Panel[BandsShown]", "{0} von {1}" },
                { "WhereTheyGo.Panel[ThresholdCaption]", "ausgeblendet ist alles unter {0} % des stärksten Bandes" },
                { "WhereTheyGo.Panel[HiddenShare]", "{0} % der Wege" },
                { "WhereTheyGo.Panel[ClassCaption]", "Breite: Wege am Tag" },
                { "WhereTheyGo.Panel[ClassUnder]", "unter {0}" },
                { "WhereTheyGo.Panel[ClassOver]", "ab {0}" },
                { "WhereTheyGo.Panel[BandPeak]", "Spitze um {0}" },
                { "WhereTheyGo.Panel[BuildingSection]", "Fußweg zum ÖPNV" },
                { "WhereTheyGo.Panel[LineSection]", "Wohin sie fahren" },
                { "WhereTheyGo.Panel[LineSavedCaption]", "{0} min je Weg, gegenüber Gehen und dem übrigen Netz" },
                { "WhereTheyGo.Panel[BandJourneysAtHour]", "{0} Wege um {1}" },
                { "WhereTheyGo.Panel[BandJourneysDay]", "{0} am Tag insgesamt" },
                { "WhereTheyGo.Panel[Carried]", "Vom ÖPNV getragen" },
                { "WhereTheyGo.Panel[CarriedCaption]", "aller Wege — gezählt werden die, für die der ÖPNV schneller ist als Gehen" },
                { "WhereTheyGo.Panel[Coverage]", "In Gehweite" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowHeatmap)), "Infoansicht anzeigen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowHeatmap)), "Öffnet die Infoansicht der Mod. Im Infoansichts-Menü des Spiels liegt derselbe Schalter." },


                { "WhereTheyGo.Infomode", "Where They Go" },
                { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
                { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "Wie weit jedes Gebäude vom heutigen Angebot entfernt liegt." },

                { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "Wunschlinien" },
                { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "Alle Wege der Stadt, gebündelt zu Bändern zwischen den Orten, zwischen denen die Menschen unterwegs sind. Warm heißt: hier fährt niemand mit dem ÖPNV. Kühl heißt: das Netz trägt diese Wege." },
                { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "Fußweg zum ÖPNV (Gebäude)" },
                { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "Färbt jedes Gebäude nach dem Fußweg von seiner Tür zur nächsten Haltestelle, die deine Linien wirklich bedienen: grün ist kurz, rot liegt am oder jenseits des Gehweg-Horizonts aus den Angebotsstandards." },

                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "Niemand fährt" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "Die Hälfte" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "Alle getragen" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "An der Haltestelle" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "Auf halbem Weg" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "Zu weit zu Fuß" },
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
                // Statuszeilen, die die Optionsseite unverändert ausgibt (siehe Loc).
                { "WhereTheyGo.Panel[CoverageCaption]", "erreichen an beiden Enden eine bediente Haltestelle in {0} min" },
                { "WhereTheyGo.Panel[DataBasisCaption]", "Linienmessungen decken {0} h der letzten {1} h ab, aus {2} Messungen" },
                { "WhereTheyGo.Panel[DataBasis]", "Woher diese Zahlen kommen" },
                { "WhereTheyGo.Panel[DataBasisEmpty]", "Linienmessungen beginnen mit deiner ersten Linie" },
                { "WhereTheyGo.Panel[ObservedTrips]", "{0} Einkaufs- und Freizeitwege in {1} h beobachtet; Pendelwege stehen im Spielstand" },
                { "WhereTheyGo.Panel[ObservedTripsEmpty]", "noch keine Einkaufs- und Freizeitwege beobachtet; Pendelwege stehen im Spielstand" },
            };
        }

        public void Unload()
        {
        }
    }
}
