using Colossal;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
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
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Haltestellen-Eignung" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Allgemein" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPresetGroup), "Voreinstellung" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kWeightsGroup), "Gewichtungen" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kTuningGroup), "Feinabstimmung" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kCalibrationGroup), "Kalibrierung" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Mode)), "Verkehrsmittel" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Mode)), "Verkehrsmittel, das die Karte bewertet. Bestimmt, welche bestehenden Haltestellen als Abdeckung zählen." },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Bus), "Bus" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Tram), "Straßenbahn" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Metro), "U-Bahn" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Train), "Zug" },
                { m_Setting.GetEnumValueLocaleID(Setting.ModePreset.Ferry), "Fähre" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyPresetWeights)), "Voreinstellung anwenden" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyPresetWeights)), "Setzt Gewichtungen und Radien auf die empfohlenen Werte für das gewählte Verkehrsmittel zurück." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W1)), "Gewichtung Nachfrage" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W1)), "Wie stark Einwohner im Einzugsbereich die Bewertung erhöhen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W2)), "Gewichtung Arbeitsplätze" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W2)), "Wie stark Arbeitsplätze im Einzugsbereich die Bewertung erhöhen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W3)), "Abzug bestehende Abdeckung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W3)), "Wie stark bestehende Haltestellen des gewählten Verkehrsmittels die Bewertung in der Nähe senken. Haltestellen ohne Linie werden ignoriert." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W4)), "Gewichtung Erreichbarkeit" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W4)), "Wie stark die Dichte des Straßennetzes in der Nähe die Bewertung erhöht." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W5)), "Gewichtung künftige Nachfrage" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W5)), "Wie stark ausgewiesenes, aber noch unbebautes Land die Bewertung erhöht. So lassen sich Haltestellen setzen, bevor ein Viertel sich füllt." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W6)), "Umsteige-Bonus" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W6)), "Wie stark eine nahe Haltestelle eines ANDEREN Verkehrsmittels die Bewertung erhöht. Dadurch wird eine Bushaltestelle an einer U-Bahn-Station hoch bewertet: sie speist eine bestehende Hauptlinie. Skaliert mit der Kapazität des anderen Verkehrsmittels, sodass U-Bahn oder Zug deutlich mehr zählen als ein weiterer Bus." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W7)), "Abzug Parallelverkehr" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W7)), "Wie stark das Angebot eines anderen Verkehrsmittels die Bewertung senkt, wenn es dieselben Fahrgäste bereits bedient, aber zu weit für einen Umstieg entfernt ist. Verhindert Linien parallel zu bestehenden." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CatchmentRadius)), "Einzugsradius" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CatchmentRadius)), "Fußweg, den eine Haltestelle bedient. Einwohner, Arbeitsplätze und bestehende Haltestellen in diesem Radius beeinflussen die Bewertung. Typisch: 300-400 m für Bus, 600-800 m für U-Bahn." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AccessRadius)), "Radius Straßenanbindung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AccessRadius)), "Wie nah das Straßennetz liegen muss, um zur Erreichbarkeit zu zählen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HighlightShare)), "Anteil Hervorhebung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.HighlightShare)), "Anteil der besten bebauten Felder am oberen Ende des Farbverlaufs. Kleinere Werte heben nur die allerbesten Stellen hervor." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxSlope)), "Maximale Steigung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxSlope)), "Felder, die steiler als dieser Wert (in Grad) sind, gelten als unbebaubar und werden nicht bewertet." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SiteCount)), "Empfohlene Standorte" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SiteCount)), "Wie viele einzelne Standorte die Ebene „Empfohlene Standorte“ markiert." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CalibrationStatus)), "Modellgüte" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CalibrationStatus)), "Der Mod erfasst während des Spiels die Fahrgastzahlen an deinen bedienten Haltestellen und passt die Gewichtungen daran an." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyFittedWeights)), "Angepasste Gewichtungen übernehmen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyFittedWeights)), "Überschreibt die Gewichtungen für Nachfrage, Arbeitsplätze, Erreichbarkeit und künftige Nachfrage mit den oben ermittelten Werten. Ohne genügend Messwerte ohne Wirkung." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetRidershipData)), "Messwerte zurücksetzen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetRidershipData)), "Verwirft alle erfassten Fahrgast-Messwerte und beginnt neu. Sinnvoll nach einem Umbau des Netzes." },

                { "StationSuitabilityOverlay.Infomode", "Haltestellen-Eignung" },
                { "Infoviews.INFOVIEW[StationSuitabilityOverlay]", "Haltestellen-Eignung" },
                { "Infoviews.INFOVIEW_TOOLTIP[StationSuitabilityOverlay]", "Zeigt, wie gut sich jeder Ort für eine neue Haltestelle eignet." },

                { "Infoviews.INFOMODE[StationSuitabilityOverlay]", "Haltestellen-Eignung" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityOverlay]", "Gesamtbewertung. Grün-gelb-rote Karte der Standortqualität; wie viele Felder als „beste“ gelten, steuert der Anteil der Hervorhebung." },
                { "Infoviews.INFOMODE[StationSuitabilitySites]", "Empfohlene Standorte" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilitySites]", "Die besten einzelnen Standorte, mindestens einen Einzugsbereich voneinander entfernt und nach Fußweg-Bewertung sortiert." },
                { "Infoviews.INFOMODE[StationSuitabilityDemand]", "Nachfrage (Einwohner)" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityDemand]", "Einwohner, die im Einzugsradius auf derselben Landmasse erreichbar sind." },
                { "Infoviews.INFOMODE[StationSuitabilityJobs]", "Arbeitsplätze" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityJobs]", "Im Einzugsradius erreichbare Arbeitsplätze." },
                { "Infoviews.INFOMODE[StationSuitabilityCoverage]", "Bestehende Abdeckung" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityCoverage]", "Wie gut bestehende Haltestellen des gewählten Verkehrsmittels jedes Feld bereits bedienen." },
                { "Infoviews.INFOMODE[StationSuitabilityAccess]", "Erreichbarkeit" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityAccess]", "Dichte des Straßennetzes in der Nähe jedes Feldes." },
                { "Infoviews.INFOMODE[StationSuitabilityFuture]", "Künftige Nachfrage (ausgewiesen)" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityFuture]", "Land, das ausgewiesen, aber noch nicht bebaut ist." },
                { "Infoviews.INFOMODE[StationSuitabilityInterchange]", "Umsteige-Potenzial" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityInterchange]", "Wo eine Haltestelle dieses Verkehrsmittels in Umsteige-Entfernung zum Angebot eines anderen Verkehrsmittels läge, gewichtet nach dessen Kapazität." },
                { "Infoviews.INFOMODE[StationSuitabilityCrossCoverage]", "Überlappung anderer Verkehrsmittel" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityCrossCoverage]", "Wo ein anderes Verkehrsmittel dieselben Fahrgäste bereits bedient, aber zu weit für einen Umstieg entfernt ist." },

                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.Low]", "Niedrig" },
                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.Medium]", "Mittel" },
                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.High]", "Hoch" },
            };
        }

        public void Unload()
        {
        }
    }
}
