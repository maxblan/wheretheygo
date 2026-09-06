using Colossal;
using System.Collections.Generic;
using System;

namespace TransitArchitect
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
                { m_Setting.GetSettingsLocaleID(), "Transit Architect" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Allgemein" },
                { m_Setting.GetOptionTabLocaleID(Setting.kAdvancedSection), "Erweitert" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlanningGroup), "Planung" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Angebotsstandards" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStatusGroup), "Status" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kWeightsGroup), "Gewichtungen" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kTuningGroup), "Feinabstimmung" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kCalibrationGroup), "Kalibrierung" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Mode)), "Verkehrsmittel" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Mode)), "Verkehrsmittel, das die Karte bewertet. Bestimmt, welche bestehenden Haltestellen als Abdeckung zählen." },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Bus), "Bus" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Tram), "Straßenbahn" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Metro), "U-Bahn" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Train), "Zug" },
                { m_Setting.GetEnumValueLocaleID(ModePreset.Ferry), "Fähre" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyPresetWeights)), "Voreinstellung anwenden" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyPresetWeights)), "Setzt Gewichtungen und Radien auf die empfohlenen Werte für das gewählte Verkehrsmittel zurück." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W1)), "Gewichtung Nachfrage" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W1)), "Wie stark Einwohner im Einzugsbereich die Bewertung erhöhen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W2)), "Gewichtung Arbeitsplätze" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W2)), "Wie stark Arbeitsplätze im Einzugsbereich die Bewertung erhöhen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W3)), "Abzug bestehende Abdeckung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W3)), "Wie stark bestehende Haltestellen des gewählten Verkehrsmittels die Bewertung in der Nähe senken. Haltestellen ohne Linie werden ignoriert." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W4)), "Rabatt Erreichbarkeit" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W4)), "Wie stark der Fußweg vom Feld zum nächsten Gehweg die Bewertung senkt: bei 1 zählt der Gehweg mal Gehzeit-Kern, bei 0 ist der Weg frei. Ein Gehweg ohne erreichbare Bewohner, Jobs oder Umstiege zählt bei jeder Einstellung nichts." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W5)), "Gewichtung künftige Nachfrage" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W5)), "Wie stark ausgewiesenes, aber noch unbebautes Land die Bewertung erhöht. So lassen sich Haltestellen setzen, bevor ein Viertel sich füllt." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W6)), "Umsteige-Bonus" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W6)), "Wie stark eine nahe Haltestelle eines ANDEREN Verkehrsmittels die Bewertung erhöht. Dadurch wird eine Bushaltestelle an einer U-Bahn-Station hoch bewertet: sie speist eine bestehende Hauptlinie. Skaliert mit der Kapazität des anderen Verkehrsmittels, sodass U-Bahn oder Zug deutlich mehr zählen als ein weiterer Bus." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.W7)), "Abzug Parallelverkehr" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.W7)), "Wie stark das Angebot eines anderen Verkehrsmittels die Bewertung senkt, wenn es dieselben Fahrgäste bereits bedient, aber zu weit für einen Umstieg entfernt ist. Verhindert Linien parallel zu bestehenden." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EquityWalkMinutes)), "Gerechtigkeit: Gehzeit-Horizont (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EquityWalkMinutes)), "Ein Weg gilt als bedient, wenn beide Enden innerhalb so vieler Gehminuten einer bedienten Haltestelle liegen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EquityFloorPercent)), "Gerechtigkeit: Zielanteil bedienter Wege" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.EquityFloorPercent)), "Bis dieser Anteil aller Wege der Stadt bedient ist, werden Vorschläge danach gereiht, wie viele Wege sie neu bedienen; darüber nach dem Verkehr, den sie ermöglichen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.UtilisationFloorPercent)), "Mindestauslastung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.UtilisationFloorPercent)), "Einsteiger in der Spitzenstunde geteilt durch die Plätze in der Spitzenstunde, die eine vorgeschlagene Linie mindestens erreichen muss. Leerere Linien werden nicht vorgeschlagen." },
                { "TransitArchitect.Panel[BuildingWalk]", "Fu\u00dfweg zum \u00d6PNV" },
                { "TransitArchitect.Panel[BuildingWalkServed]", "zur n\u00e4chsten Haltestelle, die deine Linien bedienen" },
                { "TransitArchitect.Panel[BuildingWalkUnserved]", "keine bediente Haltestelle im Gehweg-Horizont" },
                { "TransitArchitect.Panel[WalkMinutes]", "{0} min" },
                { "TransitArchitect.Panel[WalkBeyond]", "\u00fcber {0} min" },
                { "TransitArchitect.Panel[Equity]", "Bediente Wege" },
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

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowHeatmap)), "Eignungskarte anzeigen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowHeatmap)), "Öffnet die Infoansicht der Mod: eine grün-rote Karte, wo eine neue Haltestelle des gewählten Verkehrsmittels am meisten bringt. Im Infoansichts-Menü des Spiels liegt derselbe Schalter." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DeveloperTools)), "Entwicklerwerkzeuge" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DeveloperTools)), "Zeigt die Diagnosen der Mod in dieser Seite, einschließlich des Verifikations-Exports. Nichts davon beeinflusst ein normales Spiel." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowRoutes)), "Vorgeschlagene Linien anzeigen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowRoutes)), "Zeichnet die vorgeschlagenen Linien und ihre Haltestellen auf der Karte, solange diese Infoansicht offen ist." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Objective)), "Ziel der Linienplanung" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Objective)), "Worauf eine vorgeschlagene Linie optimiert wird. Maximale Fahrgastzahl folgt den stärksten Verkehrsströmen; maximale Abdeckung erschließt mehr Viertel, auch bei geringer Nachfrage; ausgewogen verbindet beides." },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Ridership), "Maximale Fahrgastzahl" },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Balanced), "Ausgewogen" },
                { m_Setting.GetEnumValueLocaleID(RouteGoal.Coverage), "Maximale Abdeckung" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteCount)), "Anzahl Vorschläge" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteCount)), "Wie viele Linien vorgeschlagen werden. Jede entnimmt dem Pool die Nachfrage, die sie bedienen würde, sodass spätere Vorschläge die früheren ergänzen." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RouteSummary)), "Vorschläge" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RouteSummary)), "Die aktuellen Vorschläge, beste zuerst. Alle Details stehen im Mod-Log." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CalibrationStatus)), "Modellgüte" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CalibrationStatus)), "Der Mod erfasst während des Spiels die Fahrgastzahlen an deinen bedienten Haltestellen und passt die Gewichtungen daran an." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApplyFittedWeights)), "Angepasste Gewichtungen übernehmen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ApplyFittedWeights)), "Überschreibt die Gewichtungen für Nachfrage, Arbeitsplätze, Erreichbarkeit und künftige Nachfrage mit den oben ermittelten Werten. Ohne genügend Messwerte ohne Wirkung." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetRidershipData)), "Messwerte zurücksetzen" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetRidershipData)), "Verwirft alle erfassten Fahrgast-Messwerte und beginnt neu. Sinnvoll nach einem Umbau des Netzes." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ExportVerificationInstance)), "Verifikationsdaten exportieren" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ExportVerificationInstance)), "Schreibt die Bewertungs-Eingaben und -Ergebnisse dieser Stadt als kanonisches JSON nach ModsData/TransitArchitect/verification, für die externe Verifikations-Pipeline. Nur lesend: exportiert wird, was der Mod ohnehin berechnet hat. Die Dateien entstehen bei der nächsten Neuberechnung; das Mod-Log nennt den Ordner." },

                { "TransitArchitect.Infomode", "Haltestellen-Eignung" },
                { "Infoviews.INFOVIEW[TransitArchitect]", "Transit Architect" },
                { "Infoviews.INFOVIEW_TOOLTIP[TransitArchitect]", "Wo eine neue Haltestelle am meisten bringt, und wie weit jedes Gebäude vom heutigen Angebot entfernt liegt." },

                { "Infoviews.INFOMODE[TransitArchitect]", "Haltestellen-Eignung" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitect]", "Gesamtbewertung. Grün-gelb-rote Karte der Standortqualität; wie viele Felder als „beste“ gelten, steuert der Anteil der Hervorhebung." },
                { "Infoviews.INFOMODE[TransitArchitectTransitAccess]", "Fußweg zum ÖPNV (Gebäude)" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectTransitAccess]", "Färbt jedes Gebäude nach dem Fußweg von seiner Tür zur nächsten Haltestelle, die deine Linien wirklich bedienen: grün ist kurz, rot liegt am oder jenseits des Gehweg-Horizonts aus den Angebotsstandards." },
                { "Infoviews.INFOMODE[TransitArchitectSites]", "Empfohlene Standorte" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectSites]", "Die besten einzelnen Standorte, mindestens einen Einzugsbereich voneinander entfernt und nach Fußweg-Bewertung sortiert." },
                { "Infoviews.INFOMODE[TransitArchitectDemand]", "Nachfrage (Einwohner)" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectDemand]", "Einwohner, die im Einzugsradius auf derselben Landmasse erreichbar sind." },
                { "Infoviews.INFOMODE[TransitArchitectJobs]", "Arbeitsplätze" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectJobs]", "Im Einzugsradius erreichbare Arbeitsplätze." },
                { "Infoviews.INFOMODE[TransitArchitectCoverage]", "Bestehende Abdeckung" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectCoverage]", "Wie gut bestehende Haltestellen des gewählten Verkehrsmittels jedes Feld bereits bedienen." },
                { "Infoviews.INFOMODE[TransitArchitectAccess]", "Erreichbarkeit" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectAccess]", "Gehzeit von jedem Feld zum nächsten Gehweg, an dem ein Halt stehen kann." },
                { "Infoviews.INFOMODE[TransitArchitectFuture]", "Künftige Nachfrage (ausgewiesen)" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectFuture]", "Land, das ausgewiesen, aber noch nicht bebaut ist." },
                { "Infoviews.INFOMODE[TransitArchitectInterchange]", "Umsteige-Potenzial" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectInterchange]", "Wo eine Haltestelle dieses Verkehrsmittels in Umsteige-Entfernung zum Angebot eines anderen Verkehrsmittels läge, gewichtet nach dessen Kapazität." },
                { "Infoviews.INFOMODE[TransitArchitectTravelDemand]", "Verkehrsnachfrage" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectTravelDemand]", "Wohin die Menschen tatsächlich wollen, aus echten Wegen von Zuhause zur Arbeit und zur Schule. Zeigt die Nachfrage, die dein Netz noch nicht bedient." },
                { "Infoviews.INFOMODE[TransitArchitectCrossCoverage]", "Überlappung anderer Verkehrsmittel" },
                { "Infoviews.INFOMODE_TOOLTIP[TransitArchitectCrossCoverage]", "Wo ein anderes Verkehrsmittel dieselben Fahrgäste bereits bedient, aber zu weit für einen Umstieg entfernt ist." },

                { "Infoviews.LABEL[TransitArchitect.Legend.Low]", "Niedrig" },
                { "Infoviews.LABEL[TransitArchitect.Legend.Medium]", "Mittel" },
                { "Infoviews.LABEL[TransitArchitect.Legend.High]", "Hoch" },
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
                // Statuszeilen, die die Optionsseite unverändert ausgibt (siehe Loc).
                { "TransitArchitect.Status[Suggestions.One]", "1 Vorschlag bereit — in der Transportübersicht ansehen." },
                { "TransitArchitect.Status[Suggestions]", "{0} Vorschläge bereit — in der Transportübersicht ansehen." },
                { "TransitArchitect.Status[NoneYet]", "Noch keine Linienvorschläge." },
                { "TransitArchitect.Status[NoCorridor]", "Kein Korridor war stark genug für einen Linienvorschlag." },
                { "TransitArchitect.Status[NoJourneys]", "Noch keine Wege gefunden — lade eine Stadt und lass sie laufen." },
                { "TransitArchitect.Status[NothingUnserved]", "{0} Wege, {1} davon geroutet, aber keine neue Linie würde genug vom Unbedienten verbessern." },
                { "TransitArchitect.Status[Calibration.Waiting]", "Warte auf eine geladene Stadt." },
                { "TransitArchitect.Status[Calibration.Collecting]", "Sammelt bei laufender Zeit: {0} von {1} Haltestellen bereit, {2} erfasst (je {3} Messungen nötig)" },
                { "TransitArchitect.Status[Calibration.Fit]", "R² {0} über {1} Haltestellen — Vorschlag: Nachfrage {2}, Arbeitsplätze {3}, Zukunft {4}" },
                { "TransitArchitect.Panel[Title]", "Transit Architect" },
                { "TransitArchitect.Panel[EquityCaption]", "erreichen an beiden Enden eine bediente Haltestelle in {0} min \u00b7 Ziel {1} % \u00b7 Gini {2}" },
                { "TransitArchitect.Panel[DataBasisCaption]", "der letzten {0} h \u00b7 {1} Messungen" },
                { "TransitArchitect.Panel[DataBasisNone]", "noch nichts" },
                { "TransitArchitect.Panel[NoteApply]", "\u00fcbernehmen" },
                { "TransitArchitect.Panel[NoteShow]", "zeigen" },
                { "TransitArchitect.Panel[SuggestedLines]", "Vorgeschlagene Linien" },
                { "TransitArchitect.Panel[ColMode]", "Verkehrsmittel" },
                { "TransitArchitect.Panel[ColLength]", "L\u00e4nge" },
                { "TransitArchitect.Panel[ColStops]", "Halte" },
                { "TransitArchitect.Panel[ColVehicles]", "Fahrzeuge" },
                { "TransitArchitect.Panel[ColSchedule]", "F\u00e4hrt" },
                { "TransitArchitect.Panel[ColReach]", "Erschlie\u00dft" },
                { "TransitArchitect.Panel[Km]", "km" },
                { "TransitArchitect.Panel[Vehicles]", "Fz." },
                { "TransitArchitect.Panel[RouteUpdate]", "{0} neue Vorschläge bereit — übernehmen" },
                { "TransitArchitect.Panel[Schedule.DayAndNight]", "ganztags" },
                { "TransitArchitect.Panel[Schedule.Day]", "nur tags (06–22 Uhr)" },
                { "TransitArchitect.Panel[Schedule.Night]", "nur nachts (22–06 Uhr)" },
                { "TransitArchitect.Panel[DataBasis]", "Datenbasis" },
                { "TransitArchitect.Panel[DataBasisEmpty]", "Messungen beginnen mit deiner ersten Linie" },
                { "TransitArchitect.Panel[ObservedTrips]", "{0} Einkaufs-/Freizeitwege in {1} h beobachtet" },
                { "TransitArchitect.Panel[ObservedTripsEmpty]", "noch keine Einkaufs-/Freizeitwege beobachtet" },
                { "TransitArchitect.Panel[Mode.Bus]", "Bus" },
                { "TransitArchitect.Panel[Mode.Tram]", "Straßenbahn" },
                { "TransitArchitect.Panel[Mode.Metro]", "U-Bahn" },
                { "TransitArchitect.Panel[Mode.Train]", "Zug" },
                { "TransitArchitect.Panel[Mode.Ferry]", "Fähre" },
                { "TransitArchitect.Panel[Cell.Split]", "teilen" },
                { "TransitArchitect.Panel[Cell.Remove]", "entfernen" },
                { "TransitArchitect.Panel[CellLoad]", "{0} % ausgelastet bei der empfohlenen Flotte" },
                { "TransitArchitect.Panel[PlanDrawn]", "Die neu getrassierte Linie liegt auf der Karte." },
                { "TransitArchitect.Panel[Verdict.Healthy]", "gesund" },
                { "TransitArchitect.Panel[Verdict.FleetShort]", "Flotte unter Soll — das Spiel bekommt die gewünschten Fahrzeuge nicht" },
                { "TransitArchitect.Panel[Verdict.FleetShort.Arg]", "Flotte unter Soll — dem Spiel fehlen {0} Fahrzeug(e), die es nicht liefern kann" },
                { "TransitArchitect.Panel[Verdict.ModeUp]", "zu groß für dieses Verkehrsmittel" },
                { "TransitArchitect.Panel[Verdict.ModeUp.Arg]", "zu groß für dieses Verkehrsmittel — auf {0} umstellen" },
                { "TransitArchitect.Panel[Verdict.SplitRoute]", "mehr Last als die größte Flotte jedes Verkehrsmittels trägt — Route aufteilen" },
                { "TransitArchitect.Panel[Verdict.Remove]", "leer und selbst als kleinstes Angebot nicht ausgelastet — umleiten oder entfernen" },
                { "TransitArchitect.Panel[Verdict.ModeDown]", "ein kleineres Fahrzeug genügt" },
                { "TransitArchitect.Panel[Verdict.ModeDown.Arg]", "ein kleineres Fahrzeug genügt — als {0} betreiben" },
                { "TransitArchitect.Panel[Verdict.FleetUp]", "Fahrzeuge hinzufügen" },
                { "TransitArchitect.Panel[Verdict.FleetUp.Arg]", "{0} Fahrzeug(e) hinzufügen" },
                { "TransitArchitect.Panel[Verdict.FleetDown]", "Fahrzeuge abziehen" },
                { "TransitArchitect.Panel[Verdict.FleetDown.Arg]", "{0} Fahrzeug(e) abziehen" },
                { "TransitArchitect.Panel[Verdict.Schedule]", "Fahrplan ändern" },
                { "TransitArchitect.Panel[Verdict.Schedule.Arg]", "{0} fahren" },
                { "TransitArchitect.Panel[Plan.Fleet]", "als {0} mit {1} Fahrzeug(en) betreiben" },
                { "TransitArchitect.Panel[Plan.Delta]", " ({0})" },
                { "TransitArchitect.Panel[Plan.Interval]", ", also ein Takt von etwa {0} s" },
                { "TransitArchitect.Panel[Plan.Span]", ", das Spiel erlaubt {0} bis {1}" },
                { "TransitArchitect.Panel[Plan.Split]", "; aufteilen — {0} km sind mehr, als die größte {1}-Flotte tragen kann" },
                { "TransitArchitect.Panel[Plan.Reroute]", "; oder durch dichter besiedeltes Gebiet umleiten — die Vorschlagsliste zeigt, wo Nachfrage unbedient ist" },
                { "TransitArchitect.Panel[Plan.Fine]", "; der Streckenverlauf wirkt sinnvoll" },
            };
        }

        public void Unload()
        {
        }
    }
}
