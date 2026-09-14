using System.Globalization;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace WhereTheyGo
{
    // The panel bridge: the static strings and requests the options page and the panel
    // module read and raise. The settings object is built before the world exists, so
    // the panel talks to the system through statics rather than an instance, and the
    // payloads are the delimited strings ui-module.md describes.
    public sealed partial class WhereTheyGoSystem
    {
        // The Options page's heat-map switch. A static request rather than a direct
        // call, because the settings object outlives the system and is edited from the
        // main menu, where there is no city and no system to talk to.
        private static int s_InfoviewRequest = -1;

        private static string s_DataCoverage = string.Empty;

        private static string s_CoverageFigures = string.Empty;

        // What the map is showing right now, all three owned here so the panel and the
        // renderer cannot disagree about it.
        //
        // -1 for the hour means the whole day at once, which is what the map opens on:
        // a still picture of where the city travels, before anyone asks about when.
        private static int s_SelectedHour = -1;

        private static int s_PurposeFilter = AllPurposes;

        private static int s_BandThresholdPercent = Assumptions.BandThresholdDefaultPercent;

        // Playing the day through, an hour at a time. The advance lives on this side
        // rather than in the panel because the hour does: two clocks would drift.
        private static bool s_PlayingHours;

        // The line whose window is open, by the line's own id (index|version), or -1.
        // Written by the section in the selected-line window and read by the routing
        // pass, which measures what that line is worth by taking it out.
        private static int s_SelectedLineId = -1;

        // The id the last started pass was asked about, so selecting a line asks for
        // a fresh pass exactly once.
        private int m_RequestedLineId = -1;

        private float m_LastHourAdvance;

        // Every purpose switched on: the four bits of JourneyPurpose.
        public const int AllPurposes = 0xF;

        public static void RequestInfoview(bool on) => s_InfoviewRequest = on ? 1 : 0;

        // "coveredHours|readings|windowHours" — how much observed history the figures
        // are actually resting on. The panel shows it because a mean over twenty
        // minutes and a mean over a full day are the same number on screen and mean
        // very different things.
        public static string DataCoverageText => s_DataCoverage;

        public static string CoverageText => s_CoverageFigures;

        // "hour|purposes|thresholdPercent" — the map's own state, so the panel draws
        // its controls from what the map is actually doing rather than from state of
        // its own.
        public static string MapStateText =>
            $"{s_SelectedHour.ToString(CultureInfo.InvariantCulture)}|" +
            $"{s_PurposeFilter.ToString(CultureInfo.InvariantCulture)}|" +
            $"{s_BandThresholdPercent.ToString(CultureInfo.InvariantCulture)}|" +
            $"{(s_PlayingHours ? 1 : 0).ToString(CultureInfo.InvariantCulture)}";

        // -1 (the whole day) or an hour of it.
        public static void SelectHour(int hour) => s_SelectedHour = hour is >= 0 and < Band.HoursPerDay ? hour : -1;

        public static void SetPurposeFilter(int mask) => s_PurposeFilter = mask & AllPurposes;

        public static void SetBandThreshold(int percent) => s_BandThresholdPercent = percent < 0 ? 0 : percent > 100 ? 100 : percent;

        internal int SelectedHour => s_SelectedHour;

        internal int PurposeFilter => s_PurposeFilter;

        internal float BandThresholdShare => s_BandThresholdPercent / 100f;

        public static void SelectLine(int lineId) => s_SelectedLineId = lineId;

        internal static int SelectedLineId => s_SelectedLineId;

        // A newly selected line is worth a pass of its own: the reading is what the
        // player clicked for, and a pass is some fifty milliseconds on the worker.
        private bool LineSelectionChanged()
        {
            if (m_RequestedLineId == s_SelectedLineId)
            {
                return false;
            }

            m_RequestedLineId = s_SelectedLineId;
            return true;
        }

        // Pressing play on the whole day starts at midnight rather than leaving the
        // map on "all day", which has no direction to show.
        public static void SetHourPlay(bool playing)
        {
            s_PlayingHours = playing;
            if (playing && s_SelectedHour < 0)
            {
                s_SelectedHour = 0;
            }
        }

        // One hour every HourPlaySeconds of real time, so a whole day takes about half
        // a minute. Slow on purpose: the bands are the thing to read, not the motion.
        private void AdvanceHourIfPlaying()
        {
            if (!s_PlayingHours)
            {
                return;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - m_LastHourAdvance < Assumptions.HourPlaySeconds)
            {
                return;
            }

            m_LastHourAdvance = now;
            s_SelectedHour = (s_SelectedHour + 1) % Band.HoursPerDay;
        }

        // Opens or closes our infoview on behalf of the Options page. Activation goes
        // through ToolSystem.infoview, which is what assigns the terrain overlay
        // channel the heat map is drawn into.
        public void SetInfoviewActive(bool active) => m_Infoview.SetActive(active);

        public bool IsInfoviewActive => m_Infoview.IsActive;

        // The bands are drawn only while their own infomode is ticked, so switching
        // them off leaves the building colours behind rather than emptying the map.
        internal bool AreBandsShown => m_Infoview.IsLayerActive(OverlayLayer.DesireBands);

        // Some OTHER infoview is on screen. The suppression of the vanilla legend keys
        // off this rather than off IsInfoviewActive, so the vanilla legend cannot flash
        // back in for the frames between our infoview closing and the game unmounting
        // its panel.
        public bool ForeignInfoviewActive => m_Infoview.ForeignActive;

        private void HandleInfoviewRequest()
        {
            if (s_InfoviewRequest < 0)
            {
                return;
            }

            bool on = s_InfoviewRequest == 1;
            s_InfoviewRequest = -1;
            SetInfoviewActive(on);
        }
    }
}
