using Colossal;
using System.Collections.Generic;
using System;

namespace WhereTheyGo
{
    // Simplified Chinese translation. Falls back to the en-US source for any key
    // not listed here, so a missing entry degrades to English rather than showing a
    // raw key. The transit vocabulary is the game's own (Locale.cok, zh-HANS):
    // 路线, 站点, 公共交通, 乘客, 信息视图.
    public class LocaleZHHANS : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleZHHANS(Setting setting)
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
            { "WhereTheyGo.Panel[BuildingWalk]", "最近的已服务站点" },
            { "WhereTheyGo.Panel[BuildingWalkServed]", "沿人行网络，到你的路线真正停靠的站点" },
            { "WhereTheyGo.Panel[BuildingWalkUnserved]", "超过本市视为已服务的 {0} 分钟" },
            { "WhereTheyGo.Panel[WalkNone]", "没有可达的站点" },
            { "WhereTheyGo.Panel[WalkMinutes]", "{0} 分钟" },
            { "WhereTheyGo.Panel[TimeOfDay]", "时段" },
            { "WhereTheyGo.Panel[WholeDay]", "全天" },
            { "WhereTheyGo.Panel[AtHour]", "{0}:00" },
            { "WhereTheyGo.Panel[PurposeWork]", "上班" },
            { "WhereTheyGo.Panel[PurposeSchool]", "上学" },
            { "WhereTheyGo.Panel[PurposeShopping]", "购物" },
            { "WhereTheyGo.Panel[PurposeLeisure]", "休闲" },
            { "WhereTheyGo.Panel[LineRiders]", "经由这条路线的出行" },
            { "WhereTheyGo.Panel[LineMeasuring]", "测算中…" },
            { "WhereTheyGo.Panel[LineMeasuringCaption]", "正在不含这条路线的情况下重新计算全城路径" },
            { "WhereTheyGo.Panel[LineSaved]", "每天节省的乘客分钟" },
            { "WhereTheyGo.Panel[LineHours]", "逐小时的满载程度" },
            { "WhereTheyGo.Panel[LineHoursEmpty]", "还没有读数；路线开始运行后就会出现" },
            { "WhereTheyGo.Panel[BandJourneys]", "每天 {0} 次出行" },
            { "WhereTheyGo.Panel[BandWithout]", "其中 {0}% 不乘公共交通" },
            { "WhereTheyGo.Panel[BandsHide]", "隐藏弱走廊" },
            { "WhereTheyGo.Panel[BandsUnder]", "低于 {0}%" },
            { "WhereTheyGo.Panel[BandsVisible]", "已绘制 {0} 条走廊" },
            { "WhereTheyGo.Panel[BandLength]", "相距 {0}" },
            { "WhereTheyGo.Panel[Detail]", "详情" },
            { "WhereTheyGo.Panel[WalkClasses]", "到已服务站点的步行时间" },
            { "WhereTheyGo.Panel[WalkClassNear]", "低于 {0} 分钟" },
            { "WhereTheyGo.Panel[WalkClassMid]", "{0}–{1} 分钟" },
            { "WhereTheyGo.Panel[WalkClassNone]", "没有站点" },
            { "WhereTheyGo.Panel[Network]", "你的网络" },
            { "WhereTheyGo.Panel[NetworkLines]", "{0} 条路线" },
            { "WhereTheyGo.Panel[NetworkStops]", "{0} 个已服务站点" },
            { "WhereTheyGo.Panel[NetworkJourneys]", "每天 {0} 次出行" },
            { "WhereTheyGo.Panel[HourDepartures]", "{0} 次出发" },
            { "WhereTheyGo.Panel[HourCarried]", "其中 {0}% 被承运" },
            { "WhereTheyGo.Panel[LineFaster]", "有它更快" },
            { "WhereTheyGo.Panel[LineFasterCaption]", "其余 {0}% 没有它也一样快：那里这条路线与已经承运他们的服务并行" },
            { "WhereTheyGo.Panel[LineStanding]", "在你的路线中" },
            { "WhereTheyGo.Panel[LineStandingValue]", "第 {0} 名，共 {1} 条" },
            { "WhereTheyGo.Panel[LineStandingCaption]", "按每天出行数排名；这条路线承运全市 {0}% 的出行" },
            { "WhereTheyGo.Panel[LineWait]", "平均等待" },
            { "WhereTheyGo.Panel[LineWaitCaption]", "路径计算向每位乘客计入的时间，取自这条路线实际保持的发车间隔" },
            { "WhereTheyGo.Panel[LinePeak]", "最高载客" },
            { "WhereTheyGo.Panel[LinePeakValue]", "{0} / {1} 个座位" },
            { "WhereTheyGo.Panel[LinePeakWindow]", "观测窗口内最满的一次读数" },
            { "WhereTheyGo.Panel[LinePeakInstant]", "仅来自这一次读数；观测窗口还没填满" },
            { "WhereTheyGo.Panel[LineLoop]", "一圈用时" },
            { "WhereTheyGo.Panel[LineLoopCaption]", "按实际行驶计；畅通时为 {0}" },
            { "WhereTheyGo.Panel[LineLoopFloorCaption]", "至少这么久；畅通时为 {0}。本线路的游戏自身计时不可用，因此这是下限而非实测值。" },
            { "WhereTheyGo.Panel[LineHoursAxis]", "刻度顶端是这条路线自己最忙的一小时，而不是满载的车辆" },
            { "WhereTheyGo.Panel[LineHourValue]", "{0} · {1}%" },
            { "WhereTheyGo.Panel[LineHourGap]", "{0} · 未观测" },
            { "WhereTheyGo.Panel[ToolbarTooltip]", "Where They Go：你的城市在走哪些路，以及其中谁已经在乘车" },
            { "WhereTheyGo.Panel[Purposes]", "按出行目的" },
            { "WhereTheyGo.Panel[HiddenShare]", "占出行的 {0}%" },
            { "WhereTheyGo.Panel[ClassCaption]", "宽度：每天出行数" },
            { "WhereTheyGo.Panel[ClassCaptionAtHour]", "宽度：{0} 的出发数" },
            { "WhereTheyGo.Panel[ClassUnder]", "低于 {0}" },
            { "WhereTheyGo.Panel[ClassOver]", "{0} 及以上" },
            { "WhereTheyGo.Panel[BandPeak]", "{0} 达到高峰" },
            { "WhereTheyGo.Panel[BuildingSection]", "到公共交通的步行" },
            { "WhereTheyGo.Panel[LineSection]", "他们去哪里" },
            { "WhereTheyGo.Panel[LineSavedCaption]", "每次出行 {0} 分钟，相对于步行和你网络的其余部分" },
            { "WhereTheyGo.Panel[BandJourneysAtHour]", "{1} 有 {0} 次出行" },
            { "WhereTheyGo.Panel[BandJourneysDay]", "全天共 {0} 次" },
            { "WhereTheyGo.Panel[Carried]", "由公共交通承运" },
            { "WhereTheyGo.Panel[CarriedCaption]", "占比步行更远的出行；当公共交通在全程上比步行更快、且不比这座城市典型的公共交通出行慢太多时，这次出行才计入" },
            { "WhereTheyGo.Panel[WalkedCaption]", "本市 {0}% 的出行短于步行上限：那是步行，不是公共交通的问题，已从上方数字中剔除" },
            { "WhereTheyGo.Panel[WalkedFolded]", "每天 {0} 次出行为步行，其走廊不予绘制" },
            { "WhereTheyGo.Panel[Coverage]", "在步行可达范围内" },


            { "WhereTheyGo.Infomode", "Where They Go" },
            { "Infoviews.INFOVIEW[WhereTheyGo]", "Where They Go" },
            { "Infoviews.INFOVIEW_TOOLTIP[WhereTheyGo]", "这座城市想去哪里，以及每栋建筑离你已经运营的服务有多远。" },

            { "Infoviews.INFOMODE[WhereTheyGoDesireBands]", "期望线" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoDesireBands]", "全城的所有出行，按两端归并成带状。暖色表示那里没人乘公共交通；冷色表示你的网络已经承运了他们。" },
            { "Infoviews.INFOMODE[WhereTheyGoTransitAccess]", "到公共交通的步行" },
            { "Infoviews.INFOMODE_TOOLTIP[WhereTheyGoTransitAccess]", "按每栋建筑门口到你的路线真正服务的最近站点的步行时间上色：浅色是短步行，深红色处于或超出你在“服务标准”中设定的步行上限。" },

            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Low]", "无人乘车" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.Medium]", "一半" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoDesireBands.High]", "全部承运" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Low]", "就在站点" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.Medium]", "一半路程" },
            { "Infoviews.LABEL[WhereTheyGo.Legend.WhereTheyGoTransitAccess.High]", "步行太远" },

            { "WhereTheyGo.Panel[CoverageCaption]", "两端都能在 {0} 分钟内到达已服务站点" },
        };

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { m_Setting.GetSettingsLocaleID(), "Where They Go" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "常规" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStandardsGroup), "服务标准" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CoverageWalkMinutes)), "“已服务”的步行上限（分钟）" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CoverageWalkMinutes)), "当一次出行的两端都在距离已服务站点这么多分钟步行范围内时，这次出行才算已服务。" },
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
