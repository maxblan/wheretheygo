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
        h("div", { className: "sso-label" }, label),
        h("div", { className: "sso-stepper" },
            h("button", {
                className: "sso-step",
                onClick: () => onSet(clamp(value - step)),
            }, "−"),
            h("div", { className: "sso-readout" },
                h("div", { className: "sso-bar" },
                    h("div", { className: "sso-bar-fill", style: { width: (fraction * 100) + "%" } })),
                h("div", { className: "sso-value" }, value + unit)),
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

function Panel() {
    const visible = useBound("visible", false);
    const [collapsed, setCollapsed] = React.useState(false);

    const mode = useBound("mode", 0);
    const objective = useBound("objective", 1);
    const showRoutes = useBound("showRoutes", true);
    const routeSummary = useBound("routeSummary", "");

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
            }, "Station Suitability ▸"));
    }

    return h("div", { className: "sso-panel" },
        h("button", {
            className: "sso-header",
            onClick: () => setCollapsed(true),
        }, "Station Suitability ▾"),

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

        routeSummary ? h("div", { className: "sso-summary" }, routeSummary) : null);
}

const register = (moduleRegistry) => {
    if (!React || !Api || !moduleRegistry || !moduleRegistry.append) {
        console.error("[StationSuitability] UI module could not register.");
        return;
    }

    moduleRegistry.append("Game", Panel);
    console.info("[StationSuitability] Control panel registered.");
};

export const hasCSS = true;
export default register;
