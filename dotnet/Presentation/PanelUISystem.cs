using Colossal.UI.Binding;
using Game.UI;

namespace TransitArchitect
{
    // Bindings for what the mod shows in the game.
    //
    // Since the 2026-09-06 rework this is a reporting channel, not a control surface:
    // every knob lives on the Options page, and what is left here is what the game
    // cannot show by itself — the verdict on each existing line, the suggestions, the
    // two city-wide figures, and the four clicks that act on them.
    public sealed partial class PanelUISystem : UISystemBase
    {
        private const string Group = "transitArchitect";

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private TransitArchitectSystem m_OverlaySystem;
#pragma warning restore CS8618

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlaySystem = World.GetOrCreateSystemManaged<TransitArchitectSystem>();

            AddUpdateBinding(new GetterValueBinding<bool>(Group, "heatmap", () =>
                m_OverlaySystem is not null && m_OverlaySystem.IsInfoviewActive));

            AddUpdateBinding(new GetterValueBinding<bool>(Group, "showRoutes",
                static () => Mod.Settings is not null && Mod.Settings.ShowRoutes));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "routeList", static () => TransitArchitectSystem.RouteListText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "routeUpdate", static () => TransitArchitectSystem.RouteUpdateText));
            AddBinding(new TriggerBinding(Group, "applyRouteUpdate", static () => TransitArchitectSystem.RequestApplyRouteUpdate()));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "overviewRows", static () => TransitArchitectSystem.OverviewRowsText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "dataCoverage", static () => TransitArchitectSystem.DataCoverageText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "equity", static () => TransitArchitectSystem.EquityText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "improvePlan", static () => TransitArchitectSystem.ImprovePlanText));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "improvedLine", static () => TransitArchitectSystem.ImprovedLineIndex));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "improvedRouteDrawn", static () => TransitArchitectSystem.ImprovedRouteDrawn));
            // The selection lives on the C# side so the panel's row highlight and what
            // the map draws cannot disagree, and so a refresh clearing it clears both.
            AddUpdateBinding(new GetterValueBinding<int>(Group, "selectedRoute", static () => TransitArchitectSystem.SelectedRouteIndex));

            AddTriggerBindings();
        }

        // Commands in, kept apart from the value bindings above: one method reports
        // state to the panel, the other acts on what the player clicks.
        private void AddTriggerBindings()
        {
            // The map legend's own checkbox for the suggested lines. It writes the same
            // setting the Options page shows, so the two cannot disagree and the choice
            // survives a reload.
            AddBinding(new TriggerBinding<bool>(Group, "setShowRoutes", static show =>
            {
                Setting? settings = Mod.Settings;
                if (settings is not null)
                {
                    settings.ShowRoutes = show;
                    settings.ApplyAndSave();
                }
            }));

            AddBinding(new TriggerBinding<int>(Group, "applyPlan", static id =>
            {
                TransitArchitectSystem.RequestApplyPlan(id);
            }));

            AddBinding(new TriggerBinding<int>(Group, "focusLine", static id =>
            {
                TransitArchitectSystem.RequestFocusLine(id);
            }));

            // By the line's own id, not its position: the health list is re-sorted
            // worst-first on every refresh.
            AddBinding(new TriggerBinding<int>(Group, "improveLine", static id =>
            {
                TransitArchitectSystem.RequestImprovement(id);
            }));

            AddBinding(new TriggerBinding<int>(Group, "highlightRoute", static index =>
            {
                TransitArchitectSystem.HighlightRoute(index);
            }));

            AddBinding(new TriggerBinding<int>(Group, "selectRoute", static index =>
            {
                TransitArchitectSystem.SelectRoute(index);
            }));
        }
    }
}
