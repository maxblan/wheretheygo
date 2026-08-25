using Colossal;
using System.Collections.Generic;
using System;

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
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
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

                { m_Setting.GetOptionGroupLocaleID(Setting.kRoutesGroup), "Linienvorschläge" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowRoutes)), "Vorgeschlagene Linien anzeigen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowRoutes)), "Zeichnet die vorgeschlagenen Linien und ihre Haltestellen auf der Karte, solange diese Infoansicht offen ist." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Objective)), "Ziel der Linienplanung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Objective)), "Worauf eine vorgeschlagene Linie optimiert wird. Maximale Fahrgastzahl folgt den stärksten Verkehrsströmen; maximale Abdeckung erschließt mehr Viertel, auch bei geringer Nachfrage; ausgewogen verbindet beides." },
                { m_Setting.GetEnumValueLocaleID(Setting.RouteGoal.Ridership), "Maximale Fahrgastzahl" },
                { m_Setting.GetEnumValueLocaleID(Setting.RouteGoal.Balanced), "Ausgewogen" },
                { m_Setting.GetEnumValueLocaleID(Setting.RouteGoal.Coverage), "Maximale Abdeckung" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteCount)), "Anzahl Vorschläge" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteCount)), "Wie viele Linien vorgeschlagen werden. Jede entnimmt dem Pool die Nachfrage, die sie bedienen würde, sodass spätere Vorschläge die früheren ergänzen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TransferPenalty)), "Abzug pro Umstieg" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TransferPenalty)), "Wie stark eine Fahrt für jeden Fahrzeugwechsel abgewertet wird, wenn eine vorgeschlagene Linie bewertet wird. Null bewertet eine Fahrt mit drei Umstiegen so gut wie eine Direktfahrt; höhere Werte bevorzugen Direktverbindungen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteSummary)), "Vorschläge" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteSummary)), "Die aktuellen Vorschläge, beste zuerst. Alle Details stehen im Mod-Log." },

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
                { "Infoviews.INFOMODE[StationSuitabilityTravelDemand]", "Verkehrsnachfrage" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityTravelDemand]", "Wohin die Menschen tatsächlich wollen, aus echten Wegen von Zuhause zur Arbeit und zur Schule. Zeigt die Nachfrage, die dein Netz noch nicht bedient." },
                { "Infoviews.INFOMODE[StationSuitabilityCrossCoverage]", "Überlappung anderer Verkehrsmittel" },
                { "Infoviews.INFOMODE_TOOLTIP[StationSuitabilityCrossCoverage]", "Wo ein anderes Verkehrsmittel dieselben Fahrgäste bereits bedient, aber zu weit für einen Umstieg entfernt ist." },

                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.Low]", "Niedrig" },
                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.Medium]", "Mittel" },
                { "Infoviews.LABEL[StationSuitabilityOverlay.Legend.High]", "Hoch" },
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
                { "StationSuitabilityOverlay.Panel[Title]", "Haltestellen-Eignung" },
                { "StationSuitabilityOverlay.Panel[Mode]", "Verkehrsmittel" },
                { "StationSuitabilityOverlay.Panel[Objective]", "Zielsetzung" },
                { "StationSuitabilityOverlay.Panel[Tuning]", "Feinabstimmung" },
                { "StationSuitabilityOverlay.Panel[RoutePlanning]", "Linienplanung" },
                { "StationSuitabilityOverlay.Panel[ApplyPreset]", "Voreinstellung für dieses Verkehrsmittel anwenden" },
                { "StationSuitabilityOverlay.Panel[Heatmap]", "Eignungs-Heatmap" },
                { "StationSuitabilityOverlay.Panel[ShowRoutes]", "Linien anzeigen" },
                { "StationSuitabilityOverlay.Panel[On]", "An" },
                { "StationSuitabilityOverlay.Panel[Off]", "Aus" },
                { "StationSuitabilityOverlay.Panel[Legend]", "Haltestellen-Eignung" },
                { "StationSuitabilityOverlay.Panel[LegendLow]", "Niedrig" },
                { "StationSuitabilityOverlay.Panel[LegendHigh]", "Hoch" },
                { "StationSuitabilityOverlay.Panel[SuggestedLines]", "Vorgeschlagene Linien" },
                { "StationSuitabilityOverlay.Panel[Km]", "km" },
                { "StationSuitabilityOverlay.Panel[Stops]", "Haltestellen" },
                { "StationSuitabilityOverlay.Panel[LineHealth]", "Linienzustand" },
                { "StationSuitabilityOverlay.Panel[SuggestImprovement]", "Verbesserung vorschlagen" },
                { "StationSuitabilityOverlay.Panel[ImprovedPlan]", "Verbesserter Vorschlag" },
                { "StationSuitabilityOverlay.Panel[ImprovedPlanHint]", "Die weiß gestrichelte Linie auf der Karte ist die neu geführte Route." },
                { "StationSuitabilityOverlay.Panel[Meta]", "{0} % ausgelastet, {1} Fz., {2} Haltestellen" },
                { "StationSuitabilityOverlay.Panel[Mode.Bus]", "Bus" },
                { "StationSuitabilityOverlay.Panel[Mode.Tram]", "Straßenbahn" },
                { "StationSuitabilityOverlay.Panel[Mode.Metro]", "U-Bahn" },
                { "StationSuitabilityOverlay.Panel[Mode.Train]", "Zug" },
                { "StationSuitabilityOverlay.Panel[Mode.Ferry]", "Fähre" },
                { "StationSuitabilityOverlay.Panel[Objective.Ridership]", "Fahrgastzahl" },
                { "StationSuitabilityOverlay.Panel[Objective.Balanced]", "Ausgewogen" },
                { "StationSuitabilityOverlay.Panel[Objective.Coverage]", "Abdeckung" },
                { "StationSuitabilityOverlay.Panel[Slider.catchment]", "Einzugsradius" },
                { "StationSuitabilityOverlay.Panel[Slider.access]", "Straßenanbindung" },
                { "StationSuitabilityOverlay.Panel[Slider.highlight]", "Hervorhebung" },
                { "StationSuitabilityOverlay.Panel[Slider.slope]", "Max. Steigung" },
                { "StationSuitabilityOverlay.Panel[Slider.sites]", "Standorte" },
                { "StationSuitabilityOverlay.Panel[Slider.routes]", "Linien" },
                { "StationSuitabilityOverlay.Panel[Verdict.Healthy]", "gesund" },
                { "StationSuitabilityOverlay.Panel[Verdict.NearlyEmpty]", "fast leer — umleiten oder entfernen" },
                { "StationSuitabilityOverlay.Panel[Verdict.LongWaits]", "lange Wartezeiten trotz freier Kapazität — Route kürzen oder häufiger fahren" },
                { "StationSuitabilityOverlay.Panel[Verdict.Overcrowded]", "überfüllt — Angebot erhöhen" },
                { "StationSuitabilityOverlay.Panel[Verdict.Overcrowded.Arg]", "überfüllt — {0} Fahrzeug(e) hinzufügen" },
                { "StationSuitabilityOverlay.Panel[Verdict.AtModeCapacity]", "an der Kapazitätsgrenze — Route aufteilen" },
                { "StationSuitabilityOverlay.Panel[Verdict.AtModeCapacity.Arg]", "an der Kapazitätsgrenze — auf {0} umstellen" },
                { "StationSuitabilityOverlay.Panel[Plan.Fleet]", "als {0} mit {1} Fahrzeug(en) betreiben" },
                { "StationSuitabilityOverlay.Panel[Plan.Delta]", " ({0})" },
                { "StationSuitabilityOverlay.Panel[Plan.Interval]", ", also ein Takt von etwa {0} s" },
                { "StationSuitabilityOverlay.Panel[Plan.Split]", "; aufteilen — {0} km sind mehr, als eine {1}-Linie im Takt halten kann" },
                { "StationSuitabilityOverlay.Panel[Plan.ThinStops]", "; Haltestellen auf etwa {0} ausdünnen — sie liegen im Schnitt {1} m auseinander, eng für eine {2}" },
                { "StationSuitabilityOverlay.Panel[Plan.Reroute]", "; oder durch dichter besiedeltes Gebiet umleiten — die Vorschlagsliste zeigt, wo Nachfrage unbedient ist" },
                { "StationSuitabilityOverlay.Panel[Plan.Fine]", "; der Streckenverlauf wirkt sinnvoll" },
            };
        }

        public void Unload()
        {
        }
    }
}
