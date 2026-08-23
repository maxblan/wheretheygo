// In-game control panel for the Station Suitability overlay.
//
// Hand-written ES module rather than a bundled React app: the game exposes React
// and its binding API on `window`, so no build toolchain is involved and the file
// can be deployed as-is next to the DLL.
//
// cohtml (the game's UI engine) supports neither <select>, <input type=range> nor
// checkboxes, so every control here is built from divs and buttons.

const React = window.React;
const Api = window["cs2/api"];

const GROUP = "stationSuitability";

const MODES = ["Bus", "Tram", "Metro", "Train", "Ferry"];

// Must match ColorFor() in SuitabilityRouteRenderer.cs — this is the key that lets
// you read a line's mode off the map.
const MODE_COLORS = {
    Bus: "rgb(38, 140, 255)",
    Tram: "rgb(255, 115, 26)",
    Metro: "rgb(153, 64, 242)",
    Train: "rgb(26, 191, 89)",
    Ferry: "rgb(26, 217, 242)",
};
const OBJECTIVES = ["Ridership", "Balanced", "Coverage"];

// Matches Setting.cs; the panel must not offer values the C# side would clamp.
const SLIDERS = [
    { key: "catchment", label: "Catchment", min: 150, max: 1000, step: 25, unit: " m" },
    { key: "access", label: "Road access", min: 50, max: 300, step: 10, unit: " m" },
    { key: "highlight", label: "Highlight", min: 1, max: 20, step: 1, unit: "%" },
    { key: "slope", label: "Max slope", min: 3, max: 45, step: 1, unit: "°" },
    { key: "sites", label: "Sites", min: 1, max: 20, step: 1, unit: "" },
    { key: "routes", label: "Routes", min: 1, max: 12, step: 1, unit: "" },
];

const bindings = {};

function binding(name, fallback) {
    if (!bindings[name]) {
        bindings[name] = Api.bindValue(GROUP, name, fallback);
    }

    return bindings[name];
}

function useBound(name, fallback) {
    return Api.useValue(binding(name, fallback));
}

function trigger(name, ...args) {
    Api.trigger(GROUP, name, ...args);
}

function h(tag, props, ...children) {
    return React.createElement(tag, props, ...children);
}

// A row of buttons standing in for a dropdown, since cohtml has no <select>.
function Choice({ label, options, value, onPick }) {
    return h("div", { className: "sso-row" },
        h("div", { className: "sso-label" }, label),
        h("div", { className: "sso-choice" },
            options.map((name, index) =>
                h("button", {
                    key: name,
                    className: "sso-chip" + (index === value ? " sso-chip-on" : ""),
                    onClick: () => onPick(index),
                }, name))));
}

// A stepper standing in for a range input: minus, value, plus. Holding is not
// available, so the step sizes match the ones the Options sliders use.
function Stepper({ label, value, min, max, step, unit, onSet }) {
    const clamp = (v) => Math.max(min, Math.min(max, v));
    const fraction = max > min ? (value - min) / (max - min) : 0;

    return h("div", { className: "sso-row" },
        h("div", { className: "sso-labelrow" },
            h("div", { className: "sso-label" }, label),
            h("div", { className: "sso-value" }, value + unit)),
        h("div", { className: "sso-stepper" },
            h("button", {
                className: "sso-step",
                onClick: () => onSet(clamp(value - step)),
            }, "−"),
            h("div", { className: "sso-bar" },
                h("div", { className: "sso-bar-fill", style: { width: (fraction * 100) + "%" } })),
            h("button", {
                className: "sso-step",
                onClick: () => onSet(clamp(value + step)),
            }, "+")));
}

function Toggle({ label, value, onToggle }) {
    return h("div", { className: "sso-row" },
        h("div", { className: "sso-label" }, label),
        h("button", {
            className: "sso-toggle" + (value ? " sso-toggle-on" : ""),
            onClick: () => onToggle(!value),
        }, value ? "On" : "Off"));
}

// The suitability gradient, moved off the vanilla left-hand legend panel so the
// mod presents itself in one place. Colours mirror LowColor/MediumColor/HighColor
// in SuitabilityInfomodePrefab.cs.
function Legend() {
    return h("div", { className: "sso-row" },
        h("div", { className: "sso-label" }, "Station suitability"),
        h("div", { className: "sso-legend" }),
        h("div", { className: "sso-labelrow" },
            h("div", { className: "sso-legend-end" }, "Low"),
            h("div", { className: "sso-legend-end" }, "High")));
}

function RouteList({ raw }) {
    const rows = (raw || "").split("\n").filter(Boolean).map((line) => line.split("|"));
    if (!rows.length) {
        return null;
    }

    return h("div", { className: "sso-routes" },
        h("div", { className: "sso-section" }, "Suggested lines"),
        rows.map((parts, i) => {
            const mode = parts[0] || "Bus";
            return h("div", { className: "sso-route", key: i },
                h("div", {
                    className: "sso-swatch",
                    style: { backgroundColor: MODE_COLORS[mode] || "rgb(200,200,200)" },
                }),
                h("div", { className: "sso-route-mode" }, mode),
                h("div", { className: "sso-route-meta" }, (parts[1] || "?") + " km · " + (parts[2] || "?") + " stops"));
        }));
}

const VERDICT_COLORS = {
    AtModeCapacity: "rgb(230, 60, 50)",
    Overcrowded: "rgb(240, 140, 40)",
    LongWaits: "rgb(230, 200, 60)",
    NearlyEmpty: "rgb(130, 140, 155)",
    Healthy: "rgb(80, 190, 120)",
};

// Existing lines, worst first, with the numbers the verdict came from — a remedy
// you cannot check is not worth much.
// Existing lines, worst first, each with a button that works out a concrete
// improvement. Lives in its own scrolling column so a city with twenty lines does
// not push the controls off the screen.
function LineHealth({ raw, plan, planFor }) {
    const rows = (raw || "").split("\n").filter(Boolean).map((line) => line.split("|"));
    if (!rows.length) {
        return null;
    }

    return h("div", { className: "sso-column" },
        h("div", { className: "sso-section" }, "Line health"),
        h("div", { className: "sso-scroll" },
            rows.map((parts) => {
                // parts[0] is the line's own id, not its position in this list. The list
                // is re-sorted worst-first on every refresh, so keying by position made
                // React re-seat the rows and the open plan appeared under another line.
                const index = parseInt(parts[0], 10);
                const verdict = parts[2] || "Healthy";
                const healthy = verdict === "Healthy";
                return h("div", { className: "sso-health", key: index },
                    h("div", { className: "sso-health-head" },
                        h("div", {
                            className: "sso-dot",
                            style: { backgroundColor: VERDICT_COLORS[verdict] || "rgb(160,160,160)" },
                        }),
                        h("div", { className: "sso-line-name" }, parts[1] || "Line"),
                        h("div", { className: "sso-route-meta" }, parts[4] || "")),
                    h("div", { className: "sso-health-note" }, parts[3] || ""),
                    healthy ? null : h("button", {
                        className: "sso-improve",
                        onClick: () => trigger("improveLine", index),
                    }, "Suggest improvement"),
                    // Shown against its own row: at the bottom of a twenty-line list
                    // nobody would ever see it.
                    (plan && planFor === index)
                        ? h("div", { className: "sso-plan" },
                            h("div", { className: "sso-plan-title" }, "Improved plan"),
                            h("div", {}, plan),
                            h("div", { className: "sso-plan-hint" }, "The white dashed line on the map is the re-traced route."))
                        : null);
            })));
}

function Panel() {
    const visible = useBound("visible", false);
    const [collapsed, setCollapsed] = React.useState(false);

    const mode = useBound("mode", 0);
    const objective = useBound("objective", 1);
    const showRoutes = useBound("showRoutes", true);
    const routeList = useBound("routeList", "");
    const ownsInfoview = useBound("ownsInfoview", false);
    const heatmap = useBound("heatmap", true);
    const lineHealth = useBound("lineHealth", "");
    const improvePlan = useBound("improvePlan", "");
    const improvedLine = useBound("improvedLine", -1);

    // Suppress the vanilla infoview legend while ours is showing; a class on the
    // document root is the only hook a plain CSS file can key off.
    React.useEffect(() => {
        const root = document.documentElement;
        if (!root) {
            return;
        }

        if (visible && ownsInfoview) {
            root.classList.add("sso-hide-vanilla-infoview");
        } else {
            root.classList.remove("sso-hide-vanilla-infoview");
        }
    }, [visible, ownsInfoview]);

    // Hooks must run unconditionally, so the slider values are read before the
    // visibility check rather than inside it.
    const sliderValues = SLIDERS.map((slider) => useBound(slider.key, slider.min));

    if (!visible) {
        return null;
    }

    if (collapsed) {
        return h("div", { className: "sso-panel sso-panel-collapsed" },
            h("button", {
                className: "sso-header",
                onClick: () => setCollapsed(false),
            }, "Station Suitability  +"));
    }

    return h("div", { className: "sso-panel" },
        h("button", {
            className: "sso-header",
            onClick: () => setCollapsed(true),
        }, "Station Suitability  -"),

        h("div", { className: "sso-body" },
        h("div", { className: "sso-column" },

        h(Choice, {
            label: "Mode",
            options: MODES,
            value: mode,
            onPick: (index) => trigger("setMode", index),
        }),

        h("button", {
            className: "sso-preset",
            onClick: () => trigger("applyPreset"),
        }, "Apply preset weights for this mode"),

        h(Toggle, {
            label: "Suitability heat map",
            value: heatmap,
            onToggle: (next) => trigger("setHeatmap", next),
        }),

        heatmap ? h(Legend, {}) : null,

        h("div", { className: "sso-section" }, "Route planning"),

        h(Choice, {
            label: "Objective",
            options: OBJECTIVES,
            value: objective,
            onPick: (index) => trigger("setObjective", index),
        }),

        h(Toggle, {
            label: "Show routes",
            value: showRoutes,
            onToggle: (next) => trigger("setShowRoutes", next),
        }),

        h("div", { className: "sso-section" }, "Tuning"),

        SLIDERS.map((slider, i) =>
            h(Stepper, {
                key: slider.key,
                label: slider.label,
                value: sliderValues[i],
                min: slider.min,
                max: slider.max,
                step: slider.step,
                unit: slider.unit,
                onSet: (value) => trigger("set" + slider.key.charAt(0).toUpperCase() + slider.key.slice(1), value),
            })),

        h(RouteList, { raw: routeList })),

        h(LineHealth, { raw: lineHealth, plan: improvePlan, planFor: improvedLine })));
}

// Styled to match the vanilla floating toggles beside it: same size variable, same
// rounded container, and the mod's own SVG icon rather than a text glyph.
function ToolbarButton() {
    const open = useBound("visible", false);
    return h("div", { className: "infoview-menu-toggle_bYF sso-toolbar-slot" },
        h("button", {
            className: "sso-toolbar-button" + (open ? " sso-toolbar-button-on" : ""),
            title: "Station Suitability",
            onClick: () => trigger("toggle"),
        },
            h("img", {
                className: "sso-toolbar-icon",
                src: "coui://stationsuitabilityoverlay/StationSuitability.svg",
            })));
}

// Our infoview has to stay registered and valid — the terrain heat map only draws
// while it is the active infoview — so its row cannot be removed from the game's
// infoview menu on the C# side. It is removed here instead, by hiding the menu button
// carrying our icon. Our own toolbar button uses the same icon, so it is excluded by
// checking for the wrapper class.
function HideInfoviewMenuEntry() {
    React.useEffect(() => {
        const hide = () => {
            const icons = document.querySelectorAll('img[src*="stationsuitabilityoverlay"]');
            for (let i = 0; i < icons.length; i++) {
                const icon = icons[i];
                if (icon.closest(".sso-toolbar-slot")) {
                    continue;
                }

                const button = icon.closest("button") || icon.parentElement;
                if (button && button.style.display !== "none") {
                    button.style.display = "none";
                }
            }
        };

        hide();
        // The menu is built and rebuilt as the player opens it, so one pass is not
        // enough; this watches for it appearing rather than polling on a timer.
        let observer = null;
        if (typeof MutationObserver !== "undefined") {
            observer = new MutationObserver(hide);
            observer.observe(document.body, { childList: true, subtree: true });
        }

        return () => {
            if (observer) {
                observer.disconnect();
            }
        };
    }, []);

    return null;
}

const register = (moduleRegistry) => {
    if (!React || !Api || !moduleRegistry || !moduleRegistry.append) {
        console.error("[StationSuitability] UI module could not register.");
        return;
    }

    moduleRegistry.append("Game", Panel);
    moduleRegistry.append("GameTopLeft", ToolbarButton);
    moduleRegistry.append("Game", HideInfoviewMenuEntry);
    console.info("[StationSuitability] Control panel registered.");
};

export const hasCSS = true;
export default register;
