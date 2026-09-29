using Colossal;
using System.Collections.Generic;
using System;

namespace WhereTheyGo
{
    // Russian translation. Falls back to the en-US source for any key not listed
    // here, so a missing entry degrades to English rather than showing a raw key.
    // The transit vocabulary is the game's own (Locale.cok, ru-RU): линия,
    // остановка, общественный транспорт, пассажир, индикатор.
    public class LocaleRU : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleRU(Setting setting)
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
            { "WhereTheyGo.Panel[BuildingWalk]", "Ближайшая обслуживаемая остановка" },
            { "WhereTheyGo.Panel[BuildingWalkServed]", "по пешеходной сети, до остановки, на которой ваши линии действительно останавливаются" },
            { "WhereTheyGo.Panel[BuildingWalkUnserved]", "дальше, чем {0} мин, которые здесь считаются обслуженными" },
            { "WhereTheyGo.Panel[WalkNone]", "остановок в пределах доступности нет" },
            { "WhereTheyGo.Panel[WalkMinutes]", "{0} мин" },
            { "WhereTheyGo.Panel[TimeOfDay]", "Время суток" },
            { "WhereTheyGo.Panel[WholeDay]", "Весь день" },
            { "WhereTheyGo.Panel[AtHour]", "{0}:00" },
            { "WhereTheyGo.Panel[PurposeWork]", "Работа" },
            { "WhereTheyGo.Panel[PurposeSchool]", "Учёба" },
            { "WhereTheyGo.Panel[PurposeShopping]", "Покупки" },
            { "WhereTheyGo.Panel[PurposeLeisure]", "Досуг" },
            { "WhereTheyGo.Panel[LineRiders]", "Поездки по этой линии" },
            { "WhereTheyGo.Panel[LineMeasuring]", "измерение…" },
            { "WhereTheyGo.Panel[LineMeasuringCaption]", "город пересчитывается заново без этой линии" },
            { "WhereTheyGo.Panel[LineSaved]", "Сэкономлено пассажиро-минут в день" },
            { "WhereTheyGo.Panel[LineHours]", "Насколько она заполнена, час за часом" },
            { "WhereTheyGo.Panel[LineHoursEmpty]", "замеров пока нет; они начнутся, когда линия поедет" },
            { "WhereTheyGo.Panel[BandJourneys]", "{0} поездок в день" },
            { "WhereTheyGo.Panel[BandWithout]", "{0} % едут без общественного транспорта" },
            { "WhereTheyGo.Panel[BandsHide]", "Скрыть слабые коридоры" },
            { "WhereTheyGo.Panel[BandsUnder]", "менее {0} %" },
            { "WhereTheyGo.Panel[BandsVisible]", "коридоров нарисовано: {0}" },
            { "WhereTheyGo.Panel[BandLength]", "на расстоянии {0}" },
            { "WhereTheyGo.Panel[Detail]", "Подробно" },
            { "WhereTheyGo.Panel[WalkClasses]", "Ходьба до обслуживаемой остановки" },
            { "WhereTheyGo.Panel[WalkClassNear]", "менее {0} мин" },
            { "WhereTheyGo.Panel[WalkClassMid]", "{0}–{1} мин" },
            { "WhereTheyGo.Panel[WalkClassNone]", "нет остановки" },
            { "WhereTheyGo.Panel[Network]", "Ваша сеть" },
            { "WhereTheyGo.Panel[NetworkLines]", "линий: {0}" },
            { "WhereTheyGo.Panel[NetworkStops]", "обслуживаемых остановок: {0}" },
            { "WhereTheyGo.Panel[NetworkJourneys]", "{0} поездок в день" },
            { "WhereTheyGo.Panel[HourDepartures]", "{0} отправлений" },
            { "WhereTheyGo.Panel[HourCarried]", "из них {0} % перевезено" },
            { "WhereTheyGo.Panel[LineFaster]", "С ней быстрее" },
            { "WhereTheyGo.Panel[LineFasterCaption]", "остальные {0} % без неё занимают столько же: там линия идёт рядом с тем, что уже их перевозит" },
            { "WhereTheyGo.Panel[LineStanding]", "Среди ваших линий" },
            { "WhereTheyGo.Panel[LineStandingValue]", "{0} из {1}" },
            { "WhereTheyGo.Panel[LineStandingCaption]", "по поездкам в день; эта линия перевозит {0} % поездок города" },
            { "WhereTheyGo.Panel[LineWait]", "Среднее ожидание" },
            { "WhereTheyGo.Panel[LineWaitCaption]", "столько маршрутизация начисляет каждому пассажиру, исходя из интервала, который эта линия действительно держит" },
            { "WhereTheyGo.Panel[LinePeak]", "Максимальная загрузка" },
            { "WhereTheyGo.Panel[LinePeakValue]", "{0} из {1} мест" },
            { "WhereTheyGo.Panel[LinePeakWindow]", "самый загруженный отдельный замер в окне" },
            { "WhereTheyGo.Panel[LinePeakInstant]", "только по этому замеру; окно ещё не заполнено" },
            { "WhereTheyGo.Panel[LineLoop]", "Время оборота" },
            { "WhereTheyGo.Panel[LineLoopCaption]", "как проезжается; {0} при свободном движении" },
            { "WhereTheyGo.Panel[LineLoopFloorCaption]", "не меньше этого; {0} при свободном движении. Собственный хронометраж игры для этой линии непригоден, поэтому это нижняя граница, а не измерение." },
            { "WhereTheyGo.Panel[LineHoursAxis]", "верх шкалы — самый загруженный час этой линии, а не полный транспорт" },
            { "WhereTheyGo.Panel[LineHourValue]", "{0} · {1} %" },
            { "WhereTheyGo.Panel[LineHourGap]", "{0} · не замерялось" },
            { "WhereTheyGo.Panel[ToolbarTooltip]", "Where They Go: поездки вашего города и кто из них уже едет на транспорте" },
            { "WhereTheyGo.Panel[Purposes]", "Поездки по цели" },
            { "WhereTheyGo.Panel[HiddenShare]", "{0} % поездок" },
            { "WhereTheyGo.Panel[ClassCaption]", "толщина: поездок в день" },
            { "WhereTheyGo.Panel[ClassCaptionAtHour]", "толщина: отправлений в {0}" },
            { "WhereTheyGo.Panel[ClassUnder]", "менее {0}" },
            { "WhereTheyGo.Panel[ClassOver]", "{0} и больше" },
            { "WhereTheyGo.Panel[BandPeak]", "пик в {0}" },
            { "WhereTheyGo.Panel[BuildingSection]", "Ходьба до транспорта" },
            { "WhereTheyGo.Panel[LineSection]", "Куда они едут" },
            { "WhereTheyGo.Panel[LineSavedCaption]", "{0} мин на поездку по сравнению с ходьбой и остальной вашей сетью" },
            { "WhereTheyGo.Panel[BandJourneysAtHour]", "{0} поездок в {1}" },
            { "WhereTheyGo.Panel[BandJourneysDay]", "всего {0} в день" },
            { "WhereTheyGo.Panel[Carried]", "Перевозит общественный транспорт" },
            { "WhereTheyGo.Panel[CarriedCaption]", "от поездок длиннее пешей прогулки; поездка считается, когда транспорт быстрее ходьбы на всём пути и не намного медленнее типичной поездки на транспорте в этом городе" },
            { "WhereTheyGo.Panel[WalkedCaption]", "{0} % поездок города короче порога пешей доступности: это ходьба, а не вопрос к транспорту, и в цифры выше они не входят" },
            { "WhereTheyGo.Panel[WalkedFolded]", "{0} поездок в день пешком, их коридоры не нарисованы" },
            { "WhereTheyGo.Panel[Coverage]", "В пешей доступности" },


            { "WhereTheyGo.Infomode", "Where They Go" },
            { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
            { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "Куда хочет ехать город и как далеко каждое здание от того обслуживания, которое вы уже ведёте." },

            { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "Линии желания" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "Все поездки города, собранные в полосы между их концами. Тёплый цвет значит, что там никто не едет на транспорте; холодный — что ваша сеть их уже перевозит." },
            { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "Ходьба до транспорта" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "Красит каждое здание по ходьбе от его двери до ближайшей остановки, которую ваши линии действительно обслуживают: светлое — короткая ходьба, тёмно-красное — на пороге пешей доступности, заданном в «Стандартах обслуживания», или за ним." },

            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "Никто не едет" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "Половина" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "Все перевезены" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "На остановке" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "На полпути" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "Слишком далеко идти" },

            { "WhereTheyGo.Panel[CoverageCaption]", "на обоих концах достигают обслуживаемой остановки за {0} мин" },
        };

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Where They Go" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Общее" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "Стандарты обслуживания" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "Порог пешей доступности для «обслужено» (мин)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "Поездка считается обслуженной, когда оба её конца лежат в пределах такого числа минут ходьбы от обслуживаемой остановки." },
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
