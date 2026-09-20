using Colossal;
using System.Collections.Generic;
using System;

namespace WhereTheyGo
{
    // French translation. Falls back to the en-US source for any key not listed
    // here, so a missing entry degrades to English rather than showing a raw key.
    // The transit vocabulary is the game's own (Locale.cok, fr-FR): ligne, arrêt,
    // transport public, passager, affichage des infos.
    public class LocaleFR : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleFR(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Where They Go" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Général" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kPlanningGroup), "Planification" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Standards de service" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "Horizon de marche pour « desservi » (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "Un déplacement compte comme desservi lorsque ses deux extrémités se trouvent à moins de ce nombre de minutes de marche d'un arrêt desservi." },
                { "WhereTheyGo.Panel[BuildingWalk]", "Arrêt desservi le plus proche" },
                { "WhereTheyGo.Panel[BuildingWalkServed]", "par le réseau piéton, jusqu'à un arrêt que vos lignes desservent réellement" },
                { "WhereTheyGo.Panel[BuildingWalkUnserved]", "plus loin que les {0} min qui comptent ici comme desservies" },
                { "WhereTheyGo.Panel[WalkNone]", "aucun arrêt accessible" },
                { "WhereTheyGo.Panel[WalkMinutes]", "{0} min" },
                { "WhereTheyGo.Panel[TimeOfDay]", "Heure de la journée" },
                { "WhereTheyGo.Panel[WholeDay]", "Toute la journée" },
                { "WhereTheyGo.Panel[AtHour]", "{0} h 00" },
                { "WhereTheyGo.Panel[PurposeWork]", "Travail" },
                { "WhereTheyGo.Panel[PurposeSchool]", "École" },
                { "WhereTheyGo.Panel[PurposeShopping]", "Achats" },
                { "WhereTheyGo.Panel[PurposeLeisure]", "Loisirs" },
                { "WhereTheyGo.Panel[LineRiders]", "Déplacements empruntant cette ligne" },
                { "WhereTheyGo.Panel[LineMeasuring]", "mesure en cours…" },
                { "WhereTheyGo.Panel[LineMeasuringCaption]", "la ville est recalculée sans cette ligne" },
                { "WhereTheyGo.Panel[LineSaved]", "Minutes-passager économisées par jour" },
                { "WhereTheyGo.Panel[LineHours]", "Son remplissage, heure par heure" },
                { "WhereTheyGo.Panel[LineHoursEmpty]", "pas encore de relevés ; ils commencent dès que la ligne circule" },
                { "WhereTheyGo.Panel[BandJourneys]", "{0} déplacements par jour" },
                { "WhereTheyGo.Panel[BandWithout]", "{0} % sans transport public" },
                { "WhereTheyGo.Panel[BandsHide]", "Masquer les corridors faibles" },
                { "WhereTheyGo.Panel[BandsUnder]", "sous {0} %" },
                { "WhereTheyGo.Panel[BandsVisible]", "{0} corridors dessinés" },
                { "WhereTheyGo.Panel[BandLength]", "{0} d'écart" },
                { "WhereTheyGo.Panel[Detail]", "En détail" },
                { "WhereTheyGo.Panel[WalkClasses]", "Marche jusqu'à un arrêt desservi" },
                { "WhereTheyGo.Panel[WalkClassNear]", "moins de {0} min" },
                { "WhereTheyGo.Panel[WalkClassMid]", "{0}–{1} min" },
                { "WhereTheyGo.Panel[WalkClassNone]", "aucun arrêt" },
                { "WhereTheyGo.Panel[Network]", "Votre réseau" },
                { "WhereTheyGo.Panel[NetworkLines]", "{0} lignes" },
                { "WhereTheyGo.Panel[NetworkStops]", "{0} arrêts desservis" },
                { "WhereTheyGo.Panel[NetworkJourneys]", "{0} déplacements par jour" },
                { "WhereTheyGo.Panel[HourDepartures]", "{0} départs" },
                { "WhereTheyGo.Panel[HourCarried]", "dont {0} % transportés" },
                { "WhereTheyGo.Panel[LineFaster]", "Plus rapide avec elle" },
                { "WhereTheyGo.Panel[LineFasterCaption]", "les {0} % restants prennent autant de temps sans elle : là, la ligne double une offre qui les transporte déjà" },
                { "WhereTheyGo.Panel[LineStanding]", "Parmi vos lignes" },
                { "WhereTheyGo.Panel[LineStandingValue]", "{0} sur {1}" },
                { "WhereTheyGo.Panel[LineStandingCaption]", "selon les déplacements par jour ; cette ligne transporte {0} % des déplacements de la ville" },
                { "WhereTheyGo.Panel[LineWait]", "Attente moyenne" },
                { "WhereTheyGo.Panel[LineWaitCaption]", "ce que le calcul d'itinéraire compte à chaque passager, d'après l'intervalle que cette ligne tient réellement" },
                { "WhereTheyGo.Panel[LinePeak]", "Charge maximale" },
                { "WhereTheyGo.Panel[LinePeakValue]", "{0} places sur {1}" },
                { "WhereTheyGo.Panel[LinePeakWindow]", "le relevé le plus chargé de la fenêtre" },
                { "WhereTheyGo.Panel[LinePeakInstant]", "d'après ce seul relevé ; la fenêtre n'est pas encore remplie" },
                { "WhereTheyGo.Panel[LineLoop]", "Temps de rotation" },
                { "WhereTheyGo.Panel[LineLoopCaption]", "tel que parcouru ; {0} en circulation fluide" },
                { "WhereTheyGo.Panel[LineHoursAxis]", "le haut de l'échelle est l'heure la plus chargée de cette ligne, pas un véhicule plein" },
                { "WhereTheyGo.Panel[LineHourValue]", "{0} · {1} %" },
                { "WhereTheyGo.Panel[LineHourGap]", "{0} · non relevé" },
                { "WhereTheyGo.Panel[ToolbarTooltip]", "Where They Go : les déplacements de votre ville, et qui les fait déjà en transport public" },
                { "WhereTheyGo.Panel[Purposes]", "Déplacements par motif" },
                { "WhereTheyGo.Panel[HiddenShare]", "{0} % des déplacements" },
                { "WhereTheyGo.Panel[ClassCaption]", "largeur : déplacements par jour" },
                { "WhereTheyGo.Panel[ClassCaptionAtHour]", "largeur : départs à {0}" },
                { "WhereTheyGo.Panel[ClassUnder]", "moins de {0}" },
                { "WhereTheyGo.Panel[ClassOver]", "{0} et plus" },
                { "WhereTheyGo.Panel[BandPeak]", "pointe à {0}" },
                { "WhereTheyGo.Panel[BuildingSection]", "Marche jusqu'au transport public" },
                { "WhereTheyGo.Panel[LineSection]", "Où ils vont" },
                { "WhereTheyGo.Panel[LineSavedCaption]", "{0} min par déplacement, face à la marche et au reste de votre réseau" },
                { "WhereTheyGo.Panel[BandJourneysAtHour]", "{0} déplacements à {1}" },
                { "WhereTheyGo.Panel[BandJourneysDay]", "{0} par jour en tout" },
                { "WhereTheyGo.Panel[Carried]", "Transportés par le transport public" },
                { "WhereTheyGo.Panel[CarriedCaption]", "de tous les déplacements ; un déplacement compte lorsque le transport public le rend plus rapide que la marche" },
                { "WhereTheyGo.Panel[Coverage]", "À distance de marche" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ShowHeatmap)), "Afficher l'affichage des infos" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ShowHeatmap)), "Ouvre l'affichage des infos du mod. Le menu des affichages des infos du jeu contient le même interrupteur." },

                { "WhereTheyGo.Infomode", "Where They Go" },
                { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
                { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "Où la ville veut aller, et à quelle distance chaque bâtiment se trouve de l'offre que vous exploitez déjà." },

                { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "Lignes de désir" },
                { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "Tous les déplacements de la ville, regroupés en bandes entre leurs deux extrémités. Chaud signifie que personne n'y prend les transports ; froid signifie que votre réseau les transporte déjà." },
                { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "Marche jusqu'au transport public" },
                { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "Colore chaque bâtiment selon la marche de sa porte jusqu'à l'arrêt le plus proche que vos lignes desservent réellement : clair est une marche courte, rouge foncé est à l'horizon de marche réglé sous Standards de service, ou au-delà." },

                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "Aucun passager" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "La moitié" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "Tous transportés" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "À l'arrêt" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "À mi-chemin" },
                { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "Trop loin à pied" },
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
                { "WhereTheyGo.Panel[CoverageCaption]", "atteignent un arrêt desservi en {0} min aux deux extrémités" },
            };
        }

        public void Unload()
        {
        }
    }
}
