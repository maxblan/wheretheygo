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

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private StationSuitabilityOverlaySystem m_OverlaySystem;
#pragma warning restore CS8618
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
                m_OverlaySystem is not null && m_OverlaySystem.IsInfoviewActive));

            AddUpdateBinding(new GetterValueBinding<bool>(Group, "heatmap", () =>
                m_OverlaySystem is not null && m_OverlaySystem.IsInfoviewActive));

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

            AddUpdateBinding(new GetterValueBinding<int>(Group, "mode", static () => Read(static s => (int)s.Mode)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "objective", static () => Read(static s => (int)s.Objective)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "catchment", static () => Read(static s => s.CatchmentRadius)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "access", static () => Read(static s => s.AccessRadius)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "highlight", static () => Read(static s => s.HighlightShare)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "slope", static () => Read(static s => s.MaxSlope)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "sites", static () => Read(static s => s.SiteCount)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "routes", static () => Read(static s => s.RouteCount)));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "showRoutes", static () => Settings is not null && Settings.ShowRoutes));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "routeSummary", static () => StationSuitabilityOverlaySystem.RouteSummaryText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "routeList", static () => StationSuitabilityOverlaySystem.RouteListText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "lineHealth", static () => StationSuitabilityOverlaySystem.LineHealthText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "improvePlan", static () => StationSuitabilityOverlaySystem.ImprovePlanText));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "improvedLine", static () => StationSuitabilityOverlaySystem.ImprovedLineIndex));

            AddBinding(new TriggerBinding<int>(Group, "improveLine", static index =>
            {
                StationSuitabilityOverlaySystem.RequestImprovement(index);
            }));

            AddBinding(new TriggerBinding<int>(Group, "setMode", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.Mode = (Setting.ModePreset)value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setObjective", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.Objective = (Setting.RouteGoal)value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setCatchment", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.CatchmentRadius = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setAccess", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.AccessRadius = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setHighlight", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.HighlightShare = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setSlope", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.MaxSlope = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setSites", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.SiteCount = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<int>(Group, "setRoutes", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.RouteCount = value;
                Changed();
            }));

            AddBinding(new TriggerBinding<bool>(Group, "setShowRoutes", static value =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.ShowRoutes = value;
                Changed();
            }));

            AddBinding(new TriggerBinding(Group, "applyPreset", static () =>
            {
                if (Settings is null)
                {
                    return;
                }
                Settings.ApplyPreset(Settings.Mode);
                Changed();
            }));
        }

        private static Setting? Settings => Mod.Settings;

        private static int Read(System.Func<Setting, int> getter)
        {
            Setting? settings = Settings;
            return settings is not null ? getter(settings) : 0;
        }

        // The overlay system already watches the settings for changes and reschedules
        // its own recompute, so the panel does not need to poke it directly. Saving
        // keeps the panel and the Options page in step on disk.
        private static void Changed()
        {
            // The getter bindings poll and diff on their own, so nothing needs to be
            // pushed here; saving keeps the panel and the Options page in step.
            Settings?.ApplyAndSave();
        }
    }
}
