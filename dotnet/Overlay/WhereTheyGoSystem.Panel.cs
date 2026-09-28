namespace WhereTheyGo
{
    // The panel bridge: the static state and requests the options page and the panel
    // module read and raise. The settings object is built before the world exists, so
    // the panel talks to the system through statics rather than an instance; the
    // payloads themselves are the JSON PanelUISystem writes.
    public sealed partial class WhereTheyGoSystem
    {
        // The Options page's heat-map switch. A static request rather than a direct
        // call, because the settings object outlives the system and is edited from the
        // main menu, where there is no city and no system to talk to.
        private static int s_InfoviewRequest = -1;

        // The city-wide numbers, written by the passes that measure them and read by
        // the panel's bindings.
        private static PanelFigures s_Figures;

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

        // Every purpose switched on. Named once, on the type that sizes the arrays by
        // it, so a fifth purpose cannot be half-added.
        public const int AllPurposes = Band.AllPurposes;

        public static void RequestInfoview(bool on) => s_InfoviewRequest = on ? 1 : 0;

        internal static PanelFigures Figures => s_Figures;

        internal static void SetCoverageFigures(float coverageShare, int walkMinutes, float[] walkClassShare)
        {
            s_Figures.CoverageShare = coverageShare;
            s_Figures.CoverageWalkMinutes = walkMinutes;
            s_Figures.WalkClassShare = walkClassShare;
        }

        // The lines and the stops the other figures are read against, published by the
        // pass that reads them. The city's journey total is NOT here: it comes off the
        // same BandView the purpose rows do, so the total and the four rows above it
        // cannot disagree.
        internal static void SetNetworkFigures(int lineCount, int servedStopCount)
        {
            s_Figures.LineCount = lineCount;
            s_Figures.ServedStopCount = servedStopCount;
        }

        // Published by the ROUTING pass, which is the only thing that knows it. It used
        // to be handed over by the coverage pass, and so it waited on a walk network
        // it does not depend on: after a load the panel read "0 % carried" for as long
        // as the streets took to appear, while the log had the real figure already.
        internal static void SetCarriedFigure(float carriedShare)
        {
            s_Figures.CarriedShare = carriedShare;
        }

        internal static bool PlayingHours => s_PlayingHours;

        // -1 (the whole day) or an hour of it.
        public static void SelectHour(int hour) => s_SelectedHour = hour is >= 0 and < Band.HoursPerDay ? hour : -1;

        public static void SetPurposeFilter(int mask) => s_PurposeFilter = mask & AllPurposes;

        public static void SetBandThreshold(int percent) => s_BandThresholdPercent = percent < 0 ? 0 : percent > 100 ? 100 : percent;

        internal static int SelectedHour => s_SelectedHour;

        internal static int PurposeFilter => s_PurposeFilter;

        internal static float BandThresholdShare => s_BandThresholdPercent / 100f;

        internal static int BandThresholdPercent => s_BandThresholdPercent;

        // What the map is drawing, worked out once and handed to everyone who needs it:
        // the renderer, the hit test, the panel's band count and the legend. Rebuilt
        // only when one of its four inputs moves, which is on a player's click or a
        // finished routing pass, never per frame, and never twice for one frame.
        internal BandView CurrentBandView
        {
            get
            {
                BandSet? set = Bands;
                if (m_BandView is not null
                    && ReferenceEquals(set, m_BandViewOf)
                    && m_BandViewHour == s_SelectedHour
                    && m_BandViewPurposes == s_PurposeFilter
                    && m_BandViewThreshold == s_BandThresholdPercent)
                {
                    return m_BandView;
                }

                m_BandViewOf = set;
                m_BandViewHour = s_SelectedHour;
                m_BandViewPurposes = s_PurposeFilter;
                m_BandViewThreshold = s_BandThresholdPercent;
                m_BandView = BandView.Of(set, s_SelectedHour, s_PurposeFilter, BandThresholdShare);
                return m_BandView;
            }
        }

        private BandView? m_BandView;

        private BandSet? m_BandViewOf;

        private int m_BandViewHour = int.MinValue;

        private int m_BandViewPurposes = -1;

        private int m_BandViewThreshold = -1;

        public static void SelectLine(int lineId) => s_SelectedLineId = lineId;

        // The band under the pointer. The panel reads its figures as JSON
        // (PanelUISystem.WriteHoveredBand), at the hour the map is showing.
        internal static Band? HoveredBand => s_Hovered;

        internal static void SetHoveredBand(Band? band) => s_Hovered = band;

        private static Band? s_Hovered;

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
