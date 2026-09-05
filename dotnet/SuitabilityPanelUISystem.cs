using System;
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

        // The panel's static shape: which modes and objectives exist, and the bounds
        // the setters below clamp to. Sent rather than hand-copied into the .mjs,
        // where both had drifted — the mode list had Metro and Tram at each other's
        // enum values, so picking one selected the other.
        private static readonly string s_Modes = JoinNames(TransitModes.All);
        private static readonly string s_Objectives = JoinNames(TransitModes.AllGoals);
        private static readonly string s_SliderBounds = BuildSliderBounds();

        private static string JoinNames<T>(T[] values)
        {
            var names = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                names[i] = values[i]?.ToString() ?? string.Empty;
            }

            return string.Join("|", names);
        }

        // "key|min|max|step" per row, keyed by the same names the setters use.
        private static string BuildSliderBounds()
        {
            return string.Join("\n", new[]
            {
                Bounds("catchment", Setting.kCatchmentMin, Setting.kCatchmentMax, Setting.kCatchmentStep),
                Bounds("access", Setting.kAccessMin, Setting.kAccessMax, Setting.kAccessStep),
                Bounds("highlight", Setting.kHighlightMin, Setting.kHighlightMax, 1),
                Bounds("slope", Setting.kSlopeMin, Setting.kSlopeMax, 1),
                Bounds("sites", Setting.kSiteCountMin, Setting.kSiteCountMax, 1),
                Bounds("routes", Setting.kRouteCountMin, Setting.kRouteCountMax, 1),
            });
        }

        private static string Bounds(string key, int min, int max, int step)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return key + "|" + min.ToString(culture) + "|" + max.ToString(culture) + "|" + step.ToString(culture);
        }

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private StationSuitabilityOverlaySystem m_OverlaySystem;
#pragma warning restore CS8618

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlaySystem = World.GetOrCreateSystemManaged<StationSuitabilityOverlaySystem>();

            // Visibility is driven by the mod's own toolbar button rather than by the
            // infoview menu, so the panel is the single entry point.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "visible",
                () => m_OverlaySystem is not null && m_OverlaySystem.PanelOpen));

            // Lets the panel keep the vanilla legend hidden while it is open, instead
            // of only while our infoview happens to be active.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "foreignInfoview", () =>
                m_OverlaySystem is not null && m_OverlaySystem.ForeignInfoviewActive));

            AddUpdateBinding(new GetterValueBinding<bool>(Group, "heatmap", () =>
                m_OverlaySystem is not null && m_OverlaySystem.IsInfoviewActive));

            AddUpdateBinding(new GetterValueBinding<string>(Group, "modes", static () => s_Modes));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "objectives", static () => s_Objectives));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "sliderBounds", static () => s_SliderBounds));

            AddUpdateBinding(new GetterValueBinding<int>(Group, "mode", static () => Read(static s => (int)s.Mode)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "objective", static () => Read(static s => (int)s.Objective)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "catchment", static () => Read(static s => s.CatchmentRadius)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "access", static () => Read(static s => s.AccessRadius)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "highlight", static () => Read(static s => s.HighlightShare)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "slope", static () => Read(static s => s.MaxSlope)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "sites", static () => Read(static s => s.SiteCount)));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "routes", static () => Read(static s => s.RouteCount)));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "showRoutes", static () => Settings is not null && Settings.ShowRoutes));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "routeList", static () => StationSuitabilityOverlaySystem.RouteListText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "lineHealth", static () => StationSuitabilityOverlaySystem.LineHealthText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "dataCoverage", static () => StationSuitabilityOverlaySystem.DataCoverageText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "equity", static () => StationSuitabilityOverlaySystem.EquityText));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "improvePlan", static () => StationSuitabilityOverlaySystem.ImprovePlanText));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "improvedLine", static () => StationSuitabilityOverlaySystem.ImprovedLineIndex));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "improvedRouteDrawn", static () => StationSuitabilityOverlaySystem.ImprovedRouteDrawn));
            // The selection lives on the C# side so the panel's row highlight and what
            // the map draws cannot disagree, and so a refresh clearing it clears both.
            AddUpdateBinding(new GetterValueBinding<int>(Group, "selectedRoute", static () => StationSuitabilityOverlaySystem.SelectedRouteIndex));

            AddTriggerBindings();
        }

        // Commands in, kept apart from the value bindings above: one method reports
        // state to the panel, the other acts on what the player clicks.
        private void AddTriggerBindings()
        {
            AddBinding(new TriggerBinding<bool>(Group, "setHeatmap", value =>
            {
                m_OverlaySystem?.SetInfoviewActive(value);
            }));

            AddBinding(new TriggerBinding(Group, "toggle", () =>
            {
                if (m_OverlaySystem is null)
                {
                    return;
                }

                bool open = !m_OverlaySystem.PanelOpen;
                m_OverlaySystem.PanelOpen = open;
                // Opening the panel turns the overlay on and closing turns it off, so
                // the button behaves like the other mods' toolbar toggles.
                m_OverlaySystem.SetInfoviewActive(open);
            }));

            AddBinding(new TriggerBinding<int>(Group, "improveLine", static index =>
            {
                StationSuitabilityOverlaySystem.RequestImprovement(index);
            }));

            AddBinding(new TriggerBinding<int>(Group, "highlightRoute", static index =>
            {
                StationSuitabilityOverlaySystem.HighlightRoute(index);
            }));

            AddBinding(new TriggerBinding<int>(Group, "selectRoute", static index =>
            {
                StationSuitabilityOverlaySystem.SelectRoute(index);
            }));

            // One shape, nine times over. The engineering baseline exempts this
            // file's one-line VALUE binding forwarders by name; these were an
            // eight-line block repeated for every setting, which is the case its
            // "unify at the third" rule is about.
            AddSetting<int>("setMode", static (settings, value) => settings.Mode = (ModePreset)value);
            AddSetting<int>("setObjective", static (settings, value) => settings.Objective = (RouteGoal)value);
            AddSetting<int>("setCatchment", static (settings, value) => settings.CatchmentRadius = value);
            AddSetting<int>("setAccess", static (settings, value) => settings.AccessRadius = value);
            AddSetting<int>("setHighlight", static (settings, value) => settings.HighlightShare = value);
            AddSetting<int>("setSlope", static (settings, value) => settings.MaxSlope = value);
            AddSetting<int>("setSites", static (settings, value) => settings.SiteCount = value);
            AddSetting<int>("setRoutes", static (settings, value) => settings.RouteCount = value);
            AddSetting<bool>("setShowRoutes", static (settings, value) => settings.ShowRoutes = value);

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

        // Writes one setting and saves, so the panel and the Options page stay in step
        // on disk. The panel is not a second place a setting may be clamped: the
        // property setter owns that.
        private void AddSetting<T>(string name, Action<Setting, T> apply)
        {
            AddBinding(new TriggerBinding<T>(Group, name, value =>
            {
                Setting? settings = Settings;
                if (settings is null)
                {
                    return;
                }

                apply(settings, value);
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
            Settings?.ApplyAndSave();
        }
    }
}
