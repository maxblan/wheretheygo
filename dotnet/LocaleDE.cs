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

        // The strings that do not depend on the Setting instance. They live here rather
        // than inside ReadEntries because they are DATA: eighty-odd rows in a method
        // body make every method-length rule measure the wrong thing, and adding one
        // locale key should not be a structural event. Only the handful of entries whose
        // key comes from the Setting object are built in the method below.
        private static readonly Dictionary<string, string> Literals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
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
            { "WhereTheyGo.Panel[LineSaved]", "Gesparte Fahrgastminuten am Tag" },
            { "WhereTheyGo.Panel[LineHours]", "Auslastung Stunde für Stunde" },
            { "WhereTheyGo.Panel[LineHoursEmpty]", "noch keine Messungen; sie beginnen, sobald die Linie f\u00e4hrt" },
            { "WhereTheyGo.Panel[BandJourneys]", "{0} Wege am Tag" },
            { "WhereTheyGo.Panel[BandWithout]", "{0} % davon ohne ÖPNV" },
            { "WhereTheyGo.Panel[BandsHide]", "Schwache Korridore ausblenden" },
            { "WhereTheyGo.Panel[BandsUnder]", "unter {0} %" },
            { "WhereTheyGo.Panel[BandsVisible]", "{0} Korridore gezeichnet" },
            { "WhereTheyGo.Panel[BandLength]", "{0} auseinander" },
            { "WhereTheyGo.Panel[Detail]", "Genauer" },
            { "WhereTheyGo.Panel[WalkClasses]", "Fu\u00dfweg zur bedienten Haltestelle" },
            { "WhereTheyGo.Panel[WalkClassNear]", "unter {0} min" },
            { "WhereTheyGo.Panel[WalkClassMid]", "{0}\u2013{1} min" },
            { "WhereTheyGo.Panel[WalkClassNone]", "keine Haltestelle" },
            { "WhereTheyGo.Panel[Network]", "Dein Netz" },
            { "WhereTheyGo.Panel[NetworkLines]", "{0} Linien" },
            { "WhereTheyGo.Panel[NetworkStops]", "{0} bediente Haltestellen" },
            { "WhereTheyGo.Panel[NetworkJourneys]", "{0} Wege am Tag" },
            { "WhereTheyGo.Panel[HourDepartures]", "{0} Abfahrten" },
            { "WhereTheyGo.Panel[HourCarried]", "davon {0} % getragen" },
            { "WhereTheyGo.Panel[LineFaster]", "Mit ihr schneller" },
            { "WhereTheyGo.Panel[LineFasterCaption]", "die \u00fcbrigen {0} % dauern ohne sie genauso lang: dort f\u00e4hrt die Linie parallel zu etwas, das es schon gibt" },
            { "WhereTheyGo.Panel[LineStanding]", "Unter deinen Linien" },
            { "WhereTheyGo.Panel[LineStandingValue]", "{0} von {1}" },
            { "WhereTheyGo.Panel[LineStandingCaption]", "nach Wegen am Tag; diese Linie tr\u00e4gt {0} % der Wege der Stadt" },
            { "WhereTheyGo.Panel[LineWait]", "Mittlere Wartezeit" },
            { "WhereTheyGo.Panel[LineWaitCaption]", "was das Routing jedem Fahrgast berechnet, aus dem Takt, den diese Linie wirklich h\u00e4lt" },
            { "WhereTheyGo.Panel[LinePeak]", "H\u00f6chste Besetzung" },
            { "WhereTheyGo.Panel[LinePeakValue]", "{0} von {1} Pl\u00e4tzen" },
            { "WhereTheyGo.Panel[LinePeakWindow]", "die h\u00f6chste einzelne Messung im Fenster" },
            { "WhereTheyGo.Panel[LinePeakInstant]", "nur aus dieser einen Messung; das Fenster ist noch nicht voll" },
            { "WhereTheyGo.Panel[LineLoop]", "Umlaufzeit" },
            { "WhereTheyGo.Panel[LineLoopCaption]", "wie gefahren; {0} bei freier Fahrt" },
            { "WhereTheyGo.Panel[LineLoopFloorCaption]", "mindestens so lang; {0} bei freier Fahrt. Die Zeitmessung des Spiels ist für diese Linie unbrauchbar, das hier ist also eine Untergrenze und keine Messung." },
            { "WhereTheyGo.Panel[LineHoursAxis]", "ganz oben steht der H\u00f6chstwert dieser Linie, nicht ein volles Fahrzeug" },
            { "WhereTheyGo.Panel[LineHourValue]", "{0} \u00b7 {1} %" },
            { "WhereTheyGo.Panel[LineHourGap]", "{0} \u00b7 nicht gemessen" },
            { "WhereTheyGo.Panel[ToolbarTooltip]", "Where They Go: die Wege deiner Stadt, und wer davon schon f\u00e4hrt" },
            { "WhereTheyGo.Panel[Purposes]", "Wege nach Zweck" },
            { "WhereTheyGo.Panel[HiddenShare]", "{0} % der Wege" },
            { "WhereTheyGo.Panel[ClassCaption]", "Breite: Wege am Tag" },
            { "WhereTheyGo.Panel[ClassCaptionAtHour]", "Breite: Abfahrten um {0}" },
            { "WhereTheyGo.Panel[ClassUnder]", "unter {0}" },
            { "WhereTheyGo.Panel[ClassOver]", "ab {0}" },
            { "WhereTheyGo.Panel[BandPeak]", "Spitze um {0}" },
            { "WhereTheyGo.Panel[BuildingSection]", "Fußweg zum ÖPNV" },
            { "WhereTheyGo.Panel[LineSection]", "Wohin sie fahren" },
            { "WhereTheyGo.Panel[LineSavedCaption]", "{0} min je Weg, gegenüber Gehen und dem übrigen Netz" },
            { "WhereTheyGo.Panel[BandJourneysAtHour]", "{0} Wege um {1}" },
            { "WhereTheyGo.Panel[BandJourneysDay]", "{0} am Tag insgesamt" },
            { "WhereTheyGo.Panel[Carried]", "Vom ÖPNV getragen" },
            { "WhereTheyGo.Panel[CarriedCaption]", "aller Wege; gez\u00e4hlt wird ein Weg, wenn der \u00d6PNV ihn schneller macht als Gehen" },
            { "WhereTheyGo.Panel[Coverage]", "In Gehweite" },


            { "WhereTheyGo.Infomode", "Where They Go" },
            { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
            { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "Wohin die Stadt will, und wie weit jedes Geb\u00e4ude vom heutigen Angebot entfernt liegt." },

            { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "Wunschlinien" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "Alle Wege der Stadt, geb\u00fcndelt zu B\u00e4ndern zwischen ihren beiden Enden. Warm hei\u00dft: dort f\u00e4hrt niemand mit dem \u00d6PNV. K\u00fchl hei\u00dft: dein Netz tr\u00e4gt diese Wege." },
            { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "Fußweg zum ÖPNV" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "F\u00e4rbt jedes Geb\u00e4ude nach dem Fu\u00dfweg von seiner T\u00fcr zur n\u00e4chsten Haltestelle, die deine Linien wirklich bedienen: hell ist kurz, dunkelrot liegt am oder jenseits des Gehweg-Horizonts, den du unter Angebotsstandards einstellst." },

            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "Niemand fährt" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "Die Hälfte" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "Alle getragen" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "An der Haltestelle" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "Auf halbem Weg" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "Zu weit zu Fuß" },

            { "WhereTheyGo.Panel[CoverageCaption]", "erreichen an beiden Enden eine bediente Haltestelle in {0} min" },
        };

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Where They Go" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Allgemein" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlanningGroup), "Planung" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Angebotsstandards" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "Gehzeit-Horizont f\u00fcr \u201ebedient\u201c (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "Ein Weg gilt als bedient, wenn beide Enden innerhalb so vieler Gehminuten einer bedienten Haltestelle liegen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowInfoview)), "Infoansicht anzeigen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowInfoview)), "Öffnet die Infoansicht der Mod. Im Infoansichts-Menü des Spiels liegt derselbe Schalter." },
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
