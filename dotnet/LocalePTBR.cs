using Colossal;
using System.Collections.Generic;
using System;

namespace WhereTheyGo
{
    // Brazilian Portuguese translation. Falls back to the en-US source for any key
    // not listed here, so a missing entry degrades to English rather than showing a
    // raw key. The transit vocabulary is the game's own (Locale.cok, pt-BR): linha,
    // parada, transporte público, passageiro, informativo.
    public class LocalePTBR : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocalePTBR(Setting setting)
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
            { "WhereTheyGo.Panel[BuildingWalk]", "Parada atendida mais próxima" },
            { "WhereTheyGo.Panel[BuildingWalkServed]", "pela rede de pedestres, até uma parada em que suas linhas realmente param" },
            { "WhereTheyGo.Panel[BuildingWalkUnserved]", "mais longe que os {0} min que esta cidade conta como atendidos" },
            { "WhereTheyGo.Panel[WalkNone]", "nenhuma parada ao alcance" },
            { "WhereTheyGo.Panel[WalkMinutes]", "{0} min" },
            { "WhereTheyGo.Panel[TimeOfDay]", "Hora do dia" },
            { "WhereTheyGo.Panel[WholeDay]", "Todo o dia" },
            { "WhereTheyGo.Panel[AtHour]", "{0}:00" },
            { "WhereTheyGo.Panel[PurposeWork]", "Trabalho" },
            { "WhereTheyGo.Panel[PurposeSchool]", "Escola" },
            { "WhereTheyGo.Panel[PurposeShopping]", "Compras" },
            { "WhereTheyGo.Panel[PurposeLeisure]", "Lazer" },
            { "WhereTheyGo.Panel[LineRiders]", "Viagens que usam esta linha" },
            { "WhereTheyGo.Panel[LineMeasuring]", "medindo…" },
            { "WhereTheyGo.Panel[LineMeasuringCaption]", "a cidade está sendo recalculada sem esta linha" },
            { "WhereTheyGo.Panel[LineSaved]", "Minutos-passageiro economizados por dia" },
            { "WhereTheyGo.Panel[LineHours]", "Quão cheia ela roda, hora a hora" },
            { "WhereTheyGo.Panel[LineHoursEmpty]", "ainda sem leituras; elas começam quando a linha entra em operação" },
            { "WhereTheyGo.Panel[BandJourneys]", "{0} viagens por dia" },
            { "WhereTheyGo.Panel[BandWithout]", "{0} % viajam sem transporte público" },
            { "WhereTheyGo.Panel[BandsHide]", "Ocultar corredores fracos" },
            { "WhereTheyGo.Panel[BandsUnder]", "abaixo de {0} %" },
            { "WhereTheyGo.Panel[BandsVisible]", "{0} corredores desenhados" },
            { "WhereTheyGo.Panel[BandLength]", "{0} de distância" },
            { "WhereTheyGo.Panel[Detail]", "Em detalhe" },
            { "WhereTheyGo.Panel[WalkClasses]", "Caminhada até uma parada atendida" },
            { "WhereTheyGo.Panel[WalkClassNear]", "abaixo de {0} min" },
            { "WhereTheyGo.Panel[WalkClassMid]", "{0}–{1} min" },
            { "WhereTheyGo.Panel[WalkClassNone]", "sem parada" },
            { "WhereTheyGo.Panel[Network]", "Sua rede" },
            { "WhereTheyGo.Panel[NetworkLines]", "{0} linhas" },
            { "WhereTheyGo.Panel[NetworkStops]", "{0} paradas atendidas" },
            { "WhereTheyGo.Panel[NetworkJourneys]", "{0} viagens por dia" },
            { "WhereTheyGo.Panel[HourDepartures]", "{0} partidas" },
            { "WhereTheyGo.Panel[HourCarried]", "{0} % delas transportadas" },
            { "WhereTheyGo.Panel[LineFaster]", "Mais rápido com ela" },
            { "WhereTheyGo.Panel[LineFasterCaption]", "os outros {0} % levam o mesmo tempo sem ela: ali a linha corre ao lado de algo que já os transporta" },
            { "WhereTheyGo.Panel[LineStanding]", "Entre suas linhas" },
            { "WhereTheyGo.Panel[LineStandingValue]", "{0} de {1}" },
            { "WhereTheyGo.Panel[LineStandingCaption]", "por viagens por dia; esta linha transporta {0} % das viagens da cidade" },
            { "WhereTheyGo.Panel[LineWait]", "Espera média" },
            { "WhereTheyGo.Panel[LineWaitCaption]", "o que o cálculo de rota cobra de cada passageiro, a partir do intervalo que esta linha realmente mantém" },
            { "WhereTheyGo.Panel[LinePeak]", "Carga máxima" },
            { "WhereTheyGo.Panel[LinePeakValue]", "{0} de {1} assentos" },
            { "WhereTheyGo.Panel[LinePeakWindow]", "a leitura mais cheia da janela" },
            { "WhereTheyGo.Panel[LinePeakInstant]", "só desta leitura; a janela ainda não está cheia" },
            { "WhereTheyGo.Panel[LineLoop]", "Tempo de ciclo" },
            { "WhereTheyGo.Panel[LineLoopCaption]", "como percorrido; {0} com fluxo livre" },
            { "WhereTheyGo.Panel[LineLoopFloorCaption]", "no mínimo isso; {0} com fluxo livre. A medição de tempo do próprio jogo é inutilizável nesta linha, portanto este é um piso, não uma medição." },
            { "WhereTheyGo.Panel[LineHoursAxis]", "o topo da escala é a hora mais cheia desta linha, não um veículo lotado" },
            { "WhereTheyGo.Panel[LineHourValue]", "{0} · {1} %" },
            { "WhereTheyGo.Panel[LineHourGap]", "{0} · não observado" },
            { "WhereTheyGo.Panel[ToolbarTooltip]", "Where They Go: as viagens que sua cidade faz, e quem já vai de transporte público" },
            { "WhereTheyGo.Panel[Purposes]", "Viagens por motivo" },
            { "WhereTheyGo.Panel[HiddenShare]", "{0} % das viagens" },
            { "WhereTheyGo.Panel[ClassCaption]", "largura: viagens por dia" },
            { "WhereTheyGo.Panel[ClassCaptionAtHour]", "largura: partidas às {0}" },
            { "WhereTheyGo.Panel[ClassUnder]", "abaixo de {0}" },
            { "WhereTheyGo.Panel[ClassOver]", "{0} e mais" },
            { "WhereTheyGo.Panel[BandPeak]", "pico às {0}" },
            { "WhereTheyGo.Panel[BuildingSection]", "Caminhada até o transporte público" },
            { "WhereTheyGo.Panel[LineSection]", "Para onde eles vão" },
            { "WhereTheyGo.Panel[LineSavedCaption]", "{0} min por viagem, em relação a caminhar e ao resto da sua rede" },
            { "WhereTheyGo.Panel[BandJourneysAtHour]", "{0} viagens às {1}" },
            { "WhereTheyGo.Panel[BandJourneysDay]", "{0} por dia no total" },
            { "WhereTheyGo.Panel[Carried]", "Transportadas pelo transporte público" },
            { "WhereTheyGo.Panel[CarriedCaption]", "das viagens mais longas do que uma caminhada; uma viagem conta quando o transporte público vence a caminhada no trajeto inteiro e não é muito mais lento do que uma viagem típica de transporte público nesta cidade" },
            { "WhereTheyGo.Panel[WalkedCaption]", "{0} % das viagens da cidade são mais curtas do que o horizonte de caminhada: uma caminhada, não uma questão de transporte público, e deixadas fora dos números acima" },
            { "WhereTheyGo.Panel[WalkedFolded]", "{0} viagens por dia a pé, seus corredores não desenhados" },
            { "WhereTheyGo.Panel[Coverage]", "A uma caminhada de distância" },


            { "WhereTheyGo.Infomode", "Where They Go" },
            { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
            { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "Para onde a cidade quer ir, e a que distância cada edifício está do serviço que você já opera." },

            { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "Linhas de desejo" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "Todas as viagens da cidade, agrupadas em faixas entre suas duas pontas. Quente significa que ali ninguém usa transporte público; frio significa que sua rede já os transporta." },
            { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "Caminhada até o transporte público" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "Colore cada edifício pela caminhada da sua porta até a parada mais próxima que suas linhas realmente atendem: claro é uma caminhada curta, vermelho escuro está no horizonte de caminhada definido em Padrões de serviço, ou além dele." },

            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "Ninguém embarca" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "A metade" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "Todas transportadas" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "Na parada" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "No meio do caminho" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "Longe demais a pé" },

            { "WhereTheyGo.Panel[CoverageCaption]", "alcançam uma parada atendida em {0} min nas duas pontas" },
        };

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Where They Go" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Geral" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Padrões de serviço" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "Horizonte de caminhada para \"atendido\" (min)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "Uma viagem conta como atendida quando as duas pontas estão a esse tanto de minutos de caminhada de uma parada atendida." },
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
