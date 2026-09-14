using Colossal.UI.Binding;
using Game.UI;

namespace WhereTheyGo
{
    // Bindings for what the mod shows in the game.
    //
    // Two city-wide figures, how much observed history they rest on, and the three
    // controls that decide what the map draws — the hour of the day, which purposes,
    // and how thin a band may be before it is hidden. Everything else lives on the
    // Options page.
    public sealed partial class PanelUISystem : UISystemBase
    {
        private const string Group = "transitArchitect";

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private WhereTheyGoSystem m_OverlaySystem;
#pragma warning restore CS8618

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlaySystem = World.GetOrCreateSystemManaged<WhereTheyGoSystem>();

            AddUpdateBinding(new GetterValueBinding<bool>(Group, "heatmap", () =>
                m_OverlaySystem is not null && m_OverlaySystem.IsInfoviewActive));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "dataCoverage", static () => WhereTheyGoSystem.DataCoverageText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "coverage", static () => WhereTheyGoSystem.CoverageText));
            // What the map is showing: the hour, the purposes and the threshold. Owned
            // in C# so the panel's controls and the map cannot disagree about it.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "mapState", static () => WhereTheyGoSystem.MapStateText));

            AddBinding(new TriggerBinding<int>(Group, "selectHour", static hour => WhereTheyGoSystem.SelectHour(hour)));
            AddBinding(new TriggerBinding<int>(Group, "setPurposes", static mask => WhereTheyGoSystem.SetPurposeFilter(mask)));
            AddBinding(new TriggerBinding<int>(Group, "setBandThreshold", static percent => WhereTheyGoSystem.SetBandThreshold(percent)));
            AddBinding(new TriggerBinding<bool>(Group, "setHourPlay", static playing => WhereTheyGoSystem.SetHourPlay(playing)));
        }
    }
}
