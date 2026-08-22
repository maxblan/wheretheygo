using Colossal.UI.Binding;
using Game.UI;

namespace StationSuitabilityOverlay
{
    // Bindings for the in-game control panel.
    //
    // The panel exists because the mode, the route objective and the tuning values
    // are things you want to change while looking at the map, and the Options page
    // costs two clicks and covers the city. Values here write straight through to
    // the same Setting object the Options page edits, so the two always agree.
    public sealed partial class SuitabilityPanelUISystem : UISystemBase
    {
        private const string Group = "stationSuitability";

        private StationSuitabilityOverlaySystem m_OverlaySystem;
        private bool m_Open;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlaySystem = World.GetOrCreateSystemManaged<StationSuitabilityOverlaySystem>();

            // Visibility is driven by the mod's own toolbar button rather than by the
            // infoview menu, so the panel is the single entry point.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "visible", () => m_Open));

            // Lets the panel hide the vanilla infoview legend, which would otherwise
            // duplicate this panel's own legend on the opposite side of the screen.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "ownsInfoview", () =>
                m_OverlaySystem != null && m_OverlaySystem.IsInfoviewActive));

            AddUpdateBinding(new GetterValueBinding<bool>(Group, "heatmap", () =>
                m_OverlaySystem != null && m_OverlaySystem.IsInfoviewActive));

            AddBinding(new TriggerBinding<bool>(Group, "setHeatmap", value =>
            {
                m_OverlaySystem?.SetInfoviewActive(value);
            }));

            AddBinding(new TriggerBinding(Group, "toggle", () =>
            {
                m_Open = !m_Open;
                // Opening the panel turns the overlay on and closing turns it off, so
                // the button behaves like the other mods' toolbar toggles.
                m_OverlaySystem?.SetInfoviewActive(m_Open);
            }));

            AddUpdateBinding(new GetterValueBinding<int>(Group, "mode", () => Read(s => (int)s.Mode)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "objective", () => Read(s => (int)s.Objective)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "catchment", () => Read(s => s.CatchmentRadius)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "access", () => Read(s => s.AccessRadius)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "highlight", () => Read(s => s.HighlightShare)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "slope", () => Read(s => s.MaxSlope)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "sites", () => Read(s => s.SiteCount)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "routes", () => Read(s => s.RouteCount)));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "showRoutes", () => Settings != null && Settings.ShowRoutes));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "routeSummary", () => StationSuitabilityOverlaySystem.RouteSummaryText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "routeList", () => StationSuitabilityOverlaySystem.RouteListText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "lineHealth", () => StationSuitabilityOverlaySystem.LineHealthText));

            AddBinding(new TriggerBinding<int>(Group, "setMode", value =>
            {
                if (Settings == null) return;
                Settings.Mode = (Setting.ModePreset)value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setObjective", value =>
            {
                if (Settings == null) return;
                Settings.Objective = (Setting.RouteGoal)value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setCatchment", value =>
            {
                if (Settings == null) return;
                Settings.CatchmentRadius = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setAccess", value =>
            {
                if (Settings == null) return;
                Settings.AccessRadius = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setHighlight", value =>
            {
                if (Settings == null) return;
                Settings.HighlightShare = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setSlope", value =>
            {
                if (Settings == null) return;
                Settings.MaxSlope = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setSites", value =>
            {
                if (Settings == null) return;
                Settings.SiteCount = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setRoutes", value =>
            {
                if (Settings == null) return;
                Settings.RouteCount = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<bool>(Group, "setShowRoutes", value =>
            {
                if (Settings == null) return;
                Settings.ShowRoutes = value;
                Changed();
            }));

            AddBinding(new TriggerBinding(Group, "applyPreset", () =>
            {
                if (Settings == null) return;
                Settings.ApplyPreset(Settings.Mode);
                Changed();
            }));
        }

        private static Setting Settings => Mod.Settings;

        private static int Read(System.Func<Setting, int> getter)
        {
            Setting settings = Settings;
            return settings != null ? getter(settings) : 0;
        }

        // The overlay system already watches the settings for changes and reschedules
        // its own recompute, so the panel does not need to poke it directly. Saving
        // keeps the panel and the Options page in step on disk.
        private void Changed()
        {
            // The getter bindings poll and diff on their own, so nothing needs to be
            // pushed here; saving keeps the panel and the Options page in step.
            Settings?.ApplyAndSave();
        }
    }
}
