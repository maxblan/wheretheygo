using Colossal.Entities;
using Colossal.UI.Binding;
using Game.Prefabs;
using Game.UI.InGame;
using Unity.Entities;

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
        }

        // Passenger transport lines only. A cargo line carries no journeys the mod
        // knows about, and a section with nothing to say is worse than no section.
        protected override void OnUpdate()
        {
            visible = IsPassengerLine(selectedEntity);
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

        private bool IsPassengerLine(Entity entity)
        {
            return EntityManager.HasComponent<Game.Routes.TransportLine>(entity)
                && EntityManager.TryGetComponent(entity, out PrefabRef prefab)
                && EntityManager.TryGetComponent(prefab.m_Prefab, out TransportLineData data)
                && data.m_PassengerTransport;
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
            // that hour. An hour the window never watched writes -1, which the panel
            // draws as a gap: nobody watching is not the same as nobody riding.
            writer.PropertyName("hourlyLoad");
            writer.ArrayBegin(Band.HoursPerDay);
            for (int hour = 0; hour < Band.HoursPerDay; hour++)
            {
                float share = m_HourlySamples[hour] > 0 && m_HourlyCapacity[hour] > 0f
                    ? m_HourlyRiders[hour] / m_HourlyCapacity[hour]
                    : -1f;
                writer.Write(share);
            }

            writer.ArrayEnd();
        }
    }
}
