using Colossal.UI.Binding;
using Game.UI.InGame;

namespace WhereTheyGo
{
    // A section in the game's own window for a line the player has clicked: what that
    // line does for the journeys the city makes (product plan 3.5).
    //
    // A READING, not a verdict. It says how many journeys ride the line, how much time
    // they save by it, how many of them would be no slower without it, and how full it
    // runs hour by hour. It never says what to do about any of that: no fleet advice,
    // no mode advice, no buttons. The player decides.
    //
    // The two routed figures come from taking the line out of the network and routing
    // the whole city again (JourneyRouting.Measure), which the routing pass does on the
    // worker as soon as a line is selected. Until that lands the section says it is
    // measuring rather than showing zeros.
    public sealed partial class LineInsightSection : InfoSectionBase
    {
        // The game maps a section to its component by the C# TYPE NAME it writes, not
        // by the group string — see BuildingAccessSection for the whole story.
        public const string SectionType = "WhereTheyGo.LineInsightSection";

        protected override string group => "WhereTheyGoLine";

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before any update.
        private WhereTheyGoSystem m_Overlay;
#pragma warning restore CS8618

        private bool m_Measured;
        private int m_Riders;
        private int m_MinutesSaved;
        private int m_DuplicatePercent;
        private readonly float[] m_HourlyRiders = new float[Band.HoursPerDay];
        private readonly float[] m_HourlyCapacity = new float[Band.HoursPerDay];
        private readonly int[] m_HourlySamples = new int[Band.HoursPerDay];

        // The load of each hour as the panel draws it: riders over the seats that were
        // actually out, or -1 for an hour the window never watched. Built here rather
        // than while writing, because the chart's own axis is measured from it.
        private readonly float[] m_HourlyLoad = new float[Band.HoursPerDay];

        private bool m_Read;
        private LineReading m_Reading;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<WhereTheyGoSystem>();
            m_InfoUISystem.AddMiddleSection(this);
        }

        protected override void Reset()
        {
            m_Measured = false;
            m_Riders = 0;
            m_MinutesSaved = 0;
            m_DuplicatePercent = 0;
            System.Array.Clear(m_HourlyRiders, 0, m_HourlyRiders.Length);
            System.Array.Clear(m_HourlyCapacity, 0, m_HourlyCapacity.Length);
            System.Array.Clear(m_HourlySamples, 0, m_HourlySamples.Length);
            System.Array.Clear(m_HourlyLoad, 0, m_HourlyLoad.Length);
            m_Read = false;
            m_Reading = default;
        }

        // Only the lines the routing models: passenger lines of a surface mode with two
        // stops in the city (Lines.IsInsightLine). For any other line the measurement
        // would never arrive, and a section saying "measuring" for ever is worse than
        // no section.
        protected override void OnUpdate()
        {
            visible = Lines.IsInsightLine(EntityManager, selectedEntity);
            if (visible)
            {
                // Telling the system which line is open is what starts the measurement;
                // it is idempotent, so doing it every update costs nothing.
                WhereTheyGoSystem.SelectLine(Lines.IdentityOf(selectedEntity));
            }
            else
            {
                WhereTheyGoSystem.SelectLine(-1);
            }

            base.OnUpdate();
        }

        protected override void OnProcess()
        {
            int lineId = Lines.IdentityOf(selectedEntity);
            m_Measured = m_Overlay.TryGetLineContribution(lineId, out LineContribution contribution);
            if (m_Measured)
            {
                m_Riders = (int)System.Math.Round(contribution.RiderWeight, System.MidpointRounding.ToEven);
                m_MinutesSaved = (int)System.Math.Round(contribution.MinutesSaved, System.MidpointRounding.ToEven);
                m_DuplicatePercent = (int)System.Math.Round(contribution.DuplicateShare * 100f, System.MidpointRounding.ToEven);
            }

            m_Overlay.ReadHourlyLoad(lineId, m_HourlyRiders, m_HourlyCapacity, m_HourlySamples);
            for (int hour = 0; hour < Band.HoursPerDay; hour++)
            {
                // -1 is the sentinel for an hour nobody watched, which the panel draws
                // as a gap. Nobody watching is not the same as nobody riding.
                m_HourlyLoad[hour] = m_HourlySamples[hour] > 0 && m_HourlyCapacity[hour] > 0f
                    ? m_HourlyRiders[hour] / m_HourlyCapacity[hour]
                    : -1f;
            }

            m_Read = m_Overlay.TryGetLineReading(lineId, m_HourlyLoad, out m_Reading);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            if (writer is null)
            {
                throw new System.ArgumentNullException(nameof(writer));
            }

            writer.PropertyName("measured");
            writer.Write(m_Measured);
            writer.PropertyName("riders");
            writer.Write(m_Riders);
            writer.PropertyName("minutesSaved");
            writer.Write(m_MinutesSaved);
            writer.PropertyName("duplicatePercent");
            writer.Write(m_DuplicatePercent);

            // The load hour by hour, as a share of the seats that were actually out in
            // that hour, with -1 for an hour the window never watched.
            writer.PropertyName("hourlyLoad");
            writer.ArrayBegin(Band.HoursPerDay);
            for (int hour = 0; hour < Band.HoursPerDay; hour++)
            {
                writer.Write(m_HourlyLoad[hour]);
            }

            writer.ArrayEnd();
            WriteReading(writer);
        }

        // Everything the mod already knew about this line and never showed. Written
        // even when the routing has not landed yet: these come from the line itself,
        // not from the pass, and there is no reason to make the player wait for them.
        private void WriteReading(IJsonWriter writer)
        {
            writer.PropertyName("read");
            writer.Write(m_Read);
            writer.PropertyName("rank");
            writer.Write(m_Reading.Standing.Rank);
            writer.PropertyName("lineCount");
            writer.Write(m_Reading.Standing.LineCount);
            writer.PropertyName("cityShare");
            writer.Write(m_Reading.Standing.CityShare);
            writer.PropertyName("waitSeconds");
            writer.Write(m_Reading.WaitSeconds);
            writer.PropertyName("peakAboard");
            writer.Write(m_Reading.PeakAboard);
            writer.PropertyName("capacity");
            writer.Write(m_Reading.Capacity);
            writer.PropertyName("loopSeconds");
            writer.Write(m_Reading.LoopSeconds);
            writer.PropertyName("idealLoopSeconds");
            writer.Write(m_Reading.IdealLoopSeconds);
            writer.PropertyName("loadAxisTop");
            writer.Write(m_Reading.LoadAxisTop);
            writer.PropertyName("fromWindow");
            writer.Write(m_Reading.FromWindow);
        }
    }
}
