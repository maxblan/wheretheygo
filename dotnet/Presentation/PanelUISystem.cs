using Colossal.UI.Binding;
using Game.UI;

namespace WhereTheyGo
{
    // Bindings for what the mod shows in the game.
    //
    // A reporting channel, not a control surface: every knob lives on the Options page,
    // and what is left here is what the game cannot show by itself — the two city-wide
    // figures and how much observed history they rest on.
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
        }
    }
}
