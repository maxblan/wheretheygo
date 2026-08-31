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

// The mode and objective names, the slider bounds and every mode colour all arrive
// from C#. They used to be copied here and kept in step by comment, and two of the
// three had already drifted: the mode list read Bus, Tram, Metro while the enum is
// Bus, Metro, Tram, so picking Tram selected Metro and picking Metro selected Tram.
//
// The slider KEYS stay here, because each one names its own C# trigger and because
// useBound is a hook — a list whose length could change at runtime would change the
// number of hooks a render makes. Only the bounds come over the wire.
const SLIDERS = [
    { key: "catchment", label: "Catchment", unit: " m" },
    { key: "access", label: "Road access", unit: " m" },
    { key: "highlight", label: "Highlight", unit: "%" },
    { key: "slope", label: "Max slope", unit: "°" },
    { key: "sites", label: "Sites", unit: "" },
    { key: "routes", label: "Routes", unit: "" },
];

// Used only until the first binding value arrives.
const FALLBACK_BOUNDS = { min: 0, max: 100, step: 1 };

function parseNames(raw, fallback) {
    const names = (raw || "").split("|").filter(Boolean);
    return names.length ? names : fallback;
}

function parseBounds(raw) {
    const bounds = {};
    (raw || "").split("\n").filter(Boolean).forEach((row) => {
        const p = row.split("|");
        if (p.length >= 4) {
            bounds[p[0]] = { min: Number(p[1]), max: Number(p[2]), step: Number(p[3]) };
        }
    });
    return bounds;
}

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

// Rows arrive as "mode|km|stops|vehicles|colour|reachPercent", best first. The colour comes with
// the row so this file holds no copy of the mode palette.
//
// Pointing at a row draws its line heavier on the map; clicking narrows the map to
// that line alone, and clicking it again brings the others back. Neither decision is
// taken here: the row reports what the pointer did and renders `selected` from the
// binding, so the highlight in the list and the lines on the map cannot disagree —
// and a refresh clearing the selection on the C# side clears it here too.
//
// The index is a position in the list, which is only safe because that clearing
// happens the moment a new list arrives.
function RouteList({ raw, selected }) {
    const t = useTranslate();
    const rows = (raw || "").split("\n").filter(Boolean);

    return h("div", { className: "sso-half" },
        h("div", { className: "sso-section" }, t("SuggestedLines", "Suggested lines")),
        h("div", { className: "sso-scroll" },
            rows.map((row, index) => {
                const parts = row.split("|");
                const mode = parts[0] || "Bus";
                const chosen = index === selected;
                return h("button", {
                    // Keyed by where the line runs between, which the C# side sends as
                    // parts[6]. Not the position — the list is re-ranked on every
                    // refresh and a positional key re-seats the rows. Not the row's
                    // text either: two different suggestions can agree on mode, length,
                    // stop count, vehicles and reach, and when they did React saw one
                    // key twice, re-seated the rows against the handlers, and hovering
                    // a bus highlighted a tram.
                    key: parts[6] || row,
                    className: "sso-route" + (chosen ? " sso-route-on" : ""),
                    onClick: () => trigger("selectRoute", index),
                    onMouseEnter: () => trigger("highlightRoute", index),
                    onMouseLeave: () => trigger("highlightRoute", -1),
                },
                    h("div", {
                        className: "sso-swatch",
                        style: { backgroundColor: parts[4] || "rgb(200, 200, 200)" },
                    }),
                    h("div", { className: "sso-route-text" },
                        h("div", { className: "sso-route-head" },
                            h("div", { className: "sso-route-mode" }, t("Mode." + mode, mode)),
                            h("div", { className: "sso-route-meta" },
                                (parts[1] || "?") + " " + t("Km", "km") + " \u00b7 "
                                + (parts[2] || "?") + " " + t("Stops", "stops") + " \u00b7 "
                                + (parts[3] || "?") + " " + t("Vehicles", "veh"))),
                        // The figure the list is ordered by, so the gap between the
                        // first row and the second is visible rather than implied.
                        h("div", { className: "sso-route-reach" },
                            t("Reach", "unlocks {0}% of unserved travel").replace("{0}", parts[5] || "0"))));
            })));
}

// The improvement plan arrives as "mode|vehicles|delta|intervalSeconds|shape|value|spacing",
// numbers and tokens only, so the sentence can be assembled in the player's language.
function describePlan(t, raw) {
    const p = (raw || "").split("|");
    // Seven fields, as PlanPayload writes them. A short payload used to pass this
    // guard and render "NaN" and "undefined" into the player's language.
    if (p.length < 7) {
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

// Every other t(...) call in this file carries its English inline so a key missing
// from a locale degrades to English rather than to a blank line. These did not.
const VERDICT_FALLBACKS = {
    AtModeCapacity: "at capacity \u2014 upgrade to {0}",
    Overcrowded: "overcrowded \u2014 add {0} vehicle(s)",
    LongWaits: "long waits with spare room \u2014 shorten the route or run more often",
    NearlyEmpty: "nearly empty \u2014 reroute or remove",
    Healthy: "healthy",
};

const VERDICT_COLORS = {
    AtModeCapacity: "rgb(230, 60, 50)",
    Overcrowded: "rgb(240, 140, 40)",
    LongWaits: "rgb(230, 200, 60)",
    NearlyEmpty: "rgb(130, 140, 155)",
    Healthy: "rgb(80, 190, 120)",
};

// Existing lines, worst first, each with the numbers its verdict came from and a
// button that works out a concrete improvement — a remedy you cannot check is not
// worth much. Lives in its own scrolling column so a city with twenty lines does
// not push the controls off the screen.
function LineHealth({ raw, plan, planFor, planDrawn }) {
    const t = useTranslate();
    const rows = (raw || "").split("\n").filter(Boolean).map((line) => line.split("|"));

    return h("div", { className: "sso-half" },
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
                const note = t("Verdict." + verdict + (arg ? ".Arg" : ""), VERDICT_FALLBACKS[verdict] || verdict)
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
                            // Only when an alignment was actually traced. There are
                            // three ways for the re-trace to come back with nothing,
                            // and the panel used to promise a map line regardless.
                            planDrawn
                                ? h("div", { className: "sso-plan-hint" },
                                    t("ImprovedPlanHint", "The white dashed line on the map is the re-traced route."))
                                : null)
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
    // Not `window`: that shadows the global this module reads React and the binding
    // API off. The length of the window is C#'s to state, not this file's.
    const windowHours = parts[2] || "24";

    if (!readings) {
        return h("div", { className: "sso-coverage" },
            h("div", { className: "sso-coverage-label" }, t("DataBasis", "Data collected")),
            h("div", { className: "sso-coverage-empty" },
                t("DataBasisEmpty", "no lines to watch yet — readings start with your first one")));
    }

    // Bar rather than only a number: the point is how much of the window is filled,
    // and a fraction is read faster as a length than as two figures to divide.
    const filled = Math.max(0, Math.min(100, (parseFloat(hours) / parseFloat(windowHours)) * 100));
    return h("div", { className: "sso-coverage" },
        h("div", { className: "sso-coverage-label" }, t("DataBasis", "Data collected")),
        h("div", { className: "sso-coverage-value" },
            t("DataBasisValue", "{0} h of {1} h · {2} readings")
                .replace("{0}", hours)
                .replace("{1}", windowHours)
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
    const improvedRouteDrawn = useBound("improvedRouteDrawn", false);
    const selectedRoute = useBound("selectedRoute", -1);

    // The panel's static shape, from the side that owns it.
    const modes = parseNames(useBound("modes", ""), ["Bus", "Metro", "Tram", "Train", "Ferry"]);
    const objectives = parseNames(useBound("objectives", ""), ["Ridership", "Balanced", "Coverage"]);
    const bounds = parseBounds(useBound("sliderBounds", ""));

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
    // visibility check rather than inside it. SLIDERS is a module constant of fixed
    // length, which is what keeps the number of hooks the same on every render.
    const sliderValues = SLIDERS.map((slider) => useBound(slider.key, 0));

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
            options: modes,
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
            options: objectives,
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

        SLIDERS.map((slider, i) => {
            const b = bounds[slider.key] || FALLBACK_BOUNDS;
            return h(Stepper, {
                key: slider.key,
                label: t("Slider." + slider.key, slider.label),
                value: sliderValues[i],
                min: b.min,
                max: b.max,
                step: b.step,
                unit: slider.unit,
                onSet: (value) => trigger("set" + slider.key.charAt(0).toUpperCase() + slider.key.slice(1), value),
            });
        }),

        ),

        // The right-hand column, split in half: what to build on top, how what you
        // have is doing below. Each half scrolls on its own so neither can push the
        // other off the bottom.
        h("div", { className: "sso-column" },
            h(RouteList, { raw: routeList, selected: selectedRoute }),
            h(LineHealth, {
                raw: lineHealth,
                plan: improvePlan,
                planFor: improvedLine,
                planDrawn: improvedRouteDrawn,
            }))));
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
        //
        // Coalesced onto an animation frame. hide() queries the whole document and
        // itself mutates it, and the game's UI changes on almost every frame, so
        // running it once per mutation record meant a full-document query several
        // times a frame.
        let observer = null;
        let queued = 0;
        if (typeof MutationObserver !== "undefined") {
            observer = new MutationObserver(() => {
                if (queued) {
                    return;
                }

                queued = requestAnimationFrame(() => {
                    queued = 0;
                    hide();
                });
            });
            observer.observe(document.body, { childList: true, subtree: true });
        }

        return () => {
            if (observer) {
                observer.disconnect();
            }

            if (queued) {
                cancelAnimationFrame(queued);
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
