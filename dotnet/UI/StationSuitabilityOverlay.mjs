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
const L10n = window["cs2/l10n"];

const GROUP = "stationSuitability";
// Every panel string is looked up under this prefix. The English text stays inline
// as the fallback argument, so a key missing from a locale file degrades to English
// rather than showing the raw key.
const LOC = "StationSuitabilityOverlay.Panel[";

// The game's localization, as a (key, englishFallback) => string. useLocalization is
// a hook, so this is one too and must be called at the top of a component.
function useTranslate() {
    const loc = L10n && L10n.useLocalization ? L10n.useLocalization() : null;
    return React.useCallback((key, fallback) => {
        if (!loc || !loc.translate) {
            return fallback;
        }

        const value = loc.translate(LOC + key + "]", fallback);
        return (value === null || value === undefined || value === "") ? fallback : value;
    }, [loc]);
}

// Mode and objective names are indexed by the enum value the binding carries, so the
// order here is Setting.ModePreset / Setting.RouteGoal and must not be re-sorted.
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
function Choice({ label, options, optionKey, value, onPick }) {
    const t = useTranslate();
    return h("div", { className: "sso-row" },
        h("div", { className: "sso-label" }, label),
        h("div", { className: "sso-choice" },
            options.map((name, index) =>
                h("button", {
                    key: name,
                    className: "sso-chip" + (index === value ? " sso-chip-on" : ""),
                    onClick: () => onPick(index),
                }, t(optionKey + "." + name, name)))));
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
    const t = useTranslate();
    return h("div", { className: "sso-row" },
        h("div", { className: "sso-label" }, label),
        h("button", {
            className: "sso-toggle" + (value ? " sso-toggle-on" : ""),
            onClick: () => onToggle(!value),
        }, value ? t("On", "On") : t("Off", "Off")));
}

// The suitability gradient, moved off the vanilla left-hand legend panel so the
// mod presents itself in one place. Colours mirror LowColor/MediumColor/HighColor
// in SuitabilityInfomodePrefab.cs.
function Legend() {
    const t = useTranslate();
    return h("div", { className: "sso-row" },
        h("div", { className: "sso-label" }, t("Legend", "Station suitability")),
        h("div", { className: "sso-legend" }),
        h("div", { className: "sso-labelrow" },
            h("div", { className: "sso-legend-end" }, t("LegendLow", "Low")),
            h("div", { className: "sso-legend-end" }, t("LegendHigh", "High"))));
}

function RouteList({ raw }) {
    const t = useTranslate();
    const rows = (raw || "").split("\n").filter(Boolean).map((line) => line.split("|"));
    if (!rows.length) {
        return null;
    }

    return h("div", { className: "sso-routes" },
        h("div", { className: "sso-section" }, t("SuggestedLines", "Suggested lines")),
        rows.map((parts, i) => {
            const mode = parts[0] || "Bus";
            return h("div", { className: "sso-route", key: i },
                h("div", {
                    className: "sso-swatch",
                    style: { backgroundColor: MODE_COLORS[mode] || "rgb(200,200,200)" },
                }),
                h("div", { className: "sso-route-mode" }, t("Mode." + mode, mode)),
                h("div", { className: "sso-route-meta" },
                    (parts[1] || "?") + " " + t("Km", "km") + " · " + (parts[2] || "?") + " " + t("Stops", "stops")));
        }));
}

// The improvement plan arrives as "mode|vehicles|delta|intervalSeconds|shape|value|spacing",
// numbers and tokens only, so the sentence can be assembled in the player's language.
function describePlan(t, raw) {
    const p = (raw || "").split("|");
    if (p.length < 5) {
        return raw || "";
    }

    const mode = t("Mode." + p[0], p[0]);
    let text = t("Plan.Fleet", "run it as {0} with {1} vehicle(s)")
        .replace("{0}", mode)
        .replace("{1}", p[1]);

    const delta = parseInt(p[2], 10);
    if (delta) {
        text += t("Plan.Delta", " ({0})").replace("{0}", delta > 0 ? "+" + delta : String(delta));
    }

    const interval = parseInt(p[3], 10);
    if (interval >= 0) {
        text += t("Plan.Interval", ", i.e. an interval of about {0} s").replace("{0}", String(interval));
    }

    switch (p[4]) {
        case "Split":
            return text + t("Plan.Split", "; split it — {0} km is beyond what one {1} line can keep to time")
                .replace("{0}", p[5]).replace("{1}", mode);
        case "ThinStops":
            return text + t("Plan.ThinStops", "; thin the stops to about {0} — they average {1} m apart, close for a {2}")
                .replace("{0}", String(Math.round(parseFloat(p[5])))).replace("{1}", p[6]).replace("{2}", mode);
        case "Reroute":
            return text + t("Plan.Reroute",
                "; or reroute it through denser ground — the suggestions list shows where demand is unserved");
        default:
            return text + t("Plan.Fine", "; the route shape looks reasonable");
    }
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
    const t = useTranslate();
    const rows = (raw || "").split("\n").filter(Boolean).map((line) => line.split("|"));
    if (!rows.length) {
        return null;
    }

    return h("div", { className: "sso-column" },
        h("div", { className: "sso-section" }, t("LineHealth", "Line health")),
        h("div", { className: "sso-scroll" },
            rows.map((parts) => {
                // parts[0] is the line's own id, not its position in this list. The list
                // is re-sorted worst-first on every refresh, so keying by position made
                // React re-seat the rows and the open plan appeared under another line.
                const index = parseInt(parts[0], 10);
                const verdict = parts[2] || "Healthy";
                const healthy = verdict === "Healthy";
                // Two verdicts carry an argument (vehicles to add, mode to upgrade to)
                // and have their own key, so the placeholder never shows up bare.
                const arg = parts[3] || "";
                const note = t("Verdict." + verdict + (arg ? ".Arg" : ""), "")
                    .replace("{0}", verdict === "AtModeCapacity" ? t("Mode." + arg, arg) : arg);
                const meta = t("Meta", "{0}% full, {1} veh, {2} stops")
                    .replace("{0}", parts[4] || "0")
                    .replace("{1}", parts[5] || "0")
                    .replace("{2}", parts[6] || "0");
                // What the verdict rests on. The percentage above is a mean over the
                // rolling window once enough readings back it, and a single reading
                // catches a one-boat ferry mid-crossing at nobody aboard — so the row
                // says which of the two the player is looking at.
                const samples = parseInt(parts[7], 10) || 0;
                const basis = samples > 0
                    ? t("Basis", "average over {0} h, {1} readings, peak {2}%")
                        .replace("{0}", parts[8] || "0")
                        .replace("{1}", String(samples))
                        .replace("{2}", parts[9] || "0")
                    : t("BasisSingle", "single reading so far");
                return h("div", { className: "sso-health", key: index },
                    h("div", { className: "sso-health-head" },
                        h("div", {
                            className: "sso-dot",
                            style: { backgroundColor: VERDICT_COLORS[verdict] || "rgb(160,160,160)" },
                        }),
                        h("div", { className: "sso-line-name" }, parts[1] || "Line"),
                        h("div", { className: "sso-route-meta" }, meta)),
                    h("div", { className: "sso-health-note" }, note),
                    h("div", { className: "sso-health-basis" }, basis),
                    healthy ? null : h("button", {
                        className: "sso-improve",
                        onClick: () => trigger("improveLine", index),
                    }, t("SuggestImprovement", "Suggest improvement")),
                    // Shown against its own row: at the bottom of a twenty-line list
                    // nobody would ever see it.
                    (plan && planFor === index)
                        ? h("div", { className: "sso-plan" },
                            h("div", { className: "sso-plan-title" }, t("ImprovedPlan", "Improved plan")),
                            h("div", {}, describePlan(t, plan)),
                            h("div", { className: "sso-plan-hint" },
                                t("ImprovedPlanHint", "The white dashed line on the map is the re-traced route.")))
                        : null);
            })));
}

// How much observed history the verdicts and the suggestions rest on.
//
// Every percentage in this panel is a mean over a rolling 24 game-hour window, and a
// mean over twenty minutes looks exactly like a mean over a full day once it is a
// number on screen. The window fills as the city runs, so the panel says how far in
// it has got rather than leaving the player to assume it is complete.
function DataCoverage({ raw }) {
    const t = useTranslate();
    const parts = (raw || "").split("|");
    const hours = parts[0] || "0";
    const readings = parseInt(parts[1], 10) || 0;
    const window = parts[2] || "24";

    if (!readings) {
        return h("div", { className: "sso-coverage" },
            h("div", { className: "sso-coverage-label" }, t("DataBasis", "Data collected")),
            h("div", { className: "sso-coverage-empty" },
                t("DataBasisEmpty", "nothing logged yet — run the city for a minute")));
    }

    // Bar rather than only a number: the point is how much of the window is filled,
    // and a fraction is read faster as a length than as two figures to divide.
    const filled = Math.max(0, Math.min(100, (parseFloat(hours) / parseFloat(window)) * 100));
    return h("div", { className: "sso-coverage" },
        h("div", { className: "sso-coverage-label" }, t("DataBasis", "Data collected")),
        h("div", { className: "sso-coverage-value" },
            t("DataBasisValue", "{0} h of {1} h · {2} readings")
                .replace("{0}", hours)
                .replace("{1}", window)
                .replace("{2}", String(readings))),
        h("div", { className: "sso-coverage-track" },
            h("div", { className: "sso-coverage-fill", style: { width: filled + "%" } })));
}

function Panel() {
    const t = useTranslate();
    const visible = useBound("visible", false);
    const [collapsed, setCollapsed] = React.useState(false);

    const mode = useBound("mode", 0);
    const objective = useBound("objective", 1);
    const showRoutes = useBound("showRoutes", true);
    const routeList = useBound("routeList", "");
    const foreignInfoview = useBound("foreignInfoview", false);
    const heatmap = useBound("heatmap", true);
    const lineHealth = useBound("lineHealth", "");
    const dataCoverage = useBound("dataCoverage", "");
    const improvePlan = useBound("improvePlan", "");
    const improvedLine = useBound("improvedLine", -1);

    // Suppress the vanilla infoview legend while ours is showing; a class on the
    // document root is the only hook a plain CSS file can key off.
    //
    // Keyed on the panel being open and no OTHER infoview being active, not on us
    // owning the infoview. Turning the heat map off drops ownsInfoview immediately,
    // but the game takes a few frames to unmount its own legend — so the old
    // condition un-hid it just in time for the player to watch it flash in and out.
    React.useEffect(() => {
        const root = document.documentElement;
        if (!root) {
            return;
        }

        if (visible && !foreignInfoview) {
            root.classList.add("sso-hide-vanilla-infoview");
        } else {
            root.classList.remove("sso-hide-vanilla-infoview");
        }
    }, [visible, foreignInfoview]);

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
            }, t("Title", "Station Suitability") + "  +"));
    }

    return h("div", { className: "sso-panel" },
        h("button", {
            className: "sso-header",
            onClick: () => setCollapsed(true),
        }, t("Title", "Station Suitability") + "  -"),

        h("div", { className: "sso-body" },
        h("div", { className: "sso-column" },

        h(DataCoverage, { raw: dataCoverage }),

        h(Choice, {
            label: t("Mode", "Mode"),
            options: MODES,
            optionKey: "Mode",
            value: mode,
            onPick: (index) => trigger("setMode", index),
        }),

        h("button", {
            className: "sso-preset",
            onClick: () => trigger("applyPreset"),
        }, t("ApplyPreset", "Apply preset weights for this mode")),

        h(Toggle, {
            label: t("Heatmap", "Suitability heat map"),
            value: heatmap,
            onToggle: (next) => trigger("setHeatmap", next),
        }),

        heatmap ? h(Legend, {}) : null,

        h("div", { className: "sso-section" }, t("RoutePlanning", "Route planning")),

        h(Choice, {
            label: t("Objective", "Objective"),
            options: OBJECTIVES,
            optionKey: "Objective",
            value: objective,
            onPick: (index) => trigger("setObjective", index),
        }),

        h(Toggle, {
            label: t("ShowRoutes", "Show routes"),
            value: showRoutes,
            onToggle: (next) => trigger("setShowRoutes", next),
        }),

        h("div", { className: "sso-section" }, t("Tuning", "Tuning")),

        SLIDERS.map((slider, i) =>
            h(Stepper, {
                key: slider.key,
                label: t("Slider." + slider.key, slider.label),
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

// Styled to match the vanilla floating toggles beside it, but WITHOUT borrowing their
// class. infoview-menu-toggle_bYF carries a second rule,
// `width: calc(400rem * (0.33333 + var(--fontScale) / 1.5))`, which overrides its own
// square rule — it is the infoview menu BAR, not a square toggle. Borrowing it stretched
// this button into a 400rem lozenge across the toolbar. sso-toolbar-slot now supplies
// the whole box itself.
function ToolbarButton() {
    const t = useTranslate();
    const open = useBound("visible", false);
    return h("div", { className: "sso-toolbar-slot" },
        h("button", {
            className: "sso-toolbar-button" + (open ? " sso-toolbar-button-on" : ""),
            title: t("Title", "Station Suitability"),
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
