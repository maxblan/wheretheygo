using Colossal.Entities;
using Colossal.UI.Binding;
using Game.Buildings;
using Game.UI.InGame;
using Unity.Entities;
using Transform = Game.Objects.Transform;

namespace WhereTheyGo
{
    // A section in the game's own selected-building window: the walk from this
    // building to the nearest stop the city's lines actually serve.
    //
    // It is the SAME number that colours the building in the transit-access infoview,
    // read back out of the same tile field, so the map and the panel cannot disagree.
    // The city-wide Gini in the mod's figures is the spread of exactly these walks;
    // this is the one building's contribution to it.
    //
    // The section renders through a component the .mjs registers under the group name
    // below, in the game's selectedInfoSectionComponents map. Sections whose group has
    // no component are simply not drawn, so a UI module that failed to load costs a
    // missing row rather than a broken panel.
    public sealed partial class BuildingAccessSection : InfoSectionBase
    {
        // The game's selected-info panel maps a section to its component by the C#
        // TYPE NAME the section writes (IJsonWriter.TypeBegin(GetType().FullName)), not
        // by this group string. Its own map reads
        // {"Game.UI.InGame.DescriptionSection": …}. Getting that wrong shows
        // "Unknown element type" in place of the section. The .mjs registers under
        // SectionType, which is that name.
        public const string SectionType = "WhereTheyGo.BuildingAccessSection";

        protected override string group => "WhereTheyGoAccess";

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before any update.
        private WhereTheyGoSystem m_Overlay;
#pragma warning restore CS8618

        private int m_WalkSeconds;

        private bool m_Served;

        private bool m_Reached;

        private int m_HorizonMinutes;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<WhereTheyGoSystem>();
            m_InfoUISystem.AddMiddleSection(this);
        }

        protected override void Reset()
        {
            m_WalkSeconds = 0;
            m_Served = false;
            m_Reached = false;
            m_HorizonMinutes = 0;
        }

        // Buildings only, and only once the equity pass has measured a field. Anything
        // else leaves the section invisible, which is how the game hides a section.
        protected override void OnUpdate()
        {
            visible = EntityManager.HasComponent<Building>(selectedEntity)
                && EntityManager.TryGetComponent(selectedEntity, out Transform transform)
                && m_Overlay.TryGetAccessAt(transform.m_Position, out m_WalkSeconds, out m_Served, out m_Reached);

            base.OnUpdate();
        }

        protected override void OnProcess()
        {
            m_HorizonMinutes = m_Overlay.CoverageHorizonMinutes;
        }

        // `reached` false means the walk is beyond the search itself and walkSeconds
        // says nothing; the UI then shows "no stop within reach". A zero-second walk
        // with reached true is a building at a stop.
        public override void OnWriteProperties(IJsonWriter writer)
        {
            if (writer is null)
            {
                throw new System.ArgumentNullException(nameof(writer));
            }

            writer.PropertyName("walkSeconds");
            writer.Write(m_WalkSeconds);
            writer.PropertyName("served");
            writer.Write(m_Served);
            writer.PropertyName("reached");
            writer.Write(m_Reached);
            writer.PropertyName("horizonMinutes");
            writer.Write(m_HorizonMinutes);
        }
    }
}
