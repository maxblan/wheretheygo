// In-game control panel for Transit Architect.
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

const GROUP = "transitArchitect";
// Every panel string is looked up under this prefix. The English text stays inline
// as the fallback argument, so a key missing from a locale file degrades to English
// rather than showing the raw key.
const LOC = "TransitArchitect.Panel[";

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

// A checkbox in the shape the game uses, rather than an On/Off pill: the vanilla
// Options page next door is full of these, and a green button saying "An" was the one
// control on screen that announced itself as somebody's mod.
function Toggle({ label, value, onToggle }) {
    const t = useTranslate();
    return h("div", { className: "ta-row" },
        h("div", { className: "ta-label" }, label),
        h("button", {
            className: "ta-toggle" + (value ? " ta-toggle-on" : ""),
            title: value ? t("On", "On") : t("Off", "Off"),
            onClick: () => onToggle(!value),
        }, value ? "\u2713" : ""));
}

function Readout({ label, value, caption, filled, dim, extra }) {
    return h("div", { className: "ta-readout" },
        h("div", { className: "ta-readout-label" }, label),
        h("div", { className: "ta-readout-value" }, value),
        filled === undefined ? null : h("div", { className: "ta-readout-track" },
            h("div", {
                className: "ta-readout-fill" + (dim ? " ta-readout-fill-short" : ""),
                style: { width: Math.max(0, Math.min(100, filled)) + "%" },
            })),
        caption ? h("div", { className: "ta-readout-caption" }, caption) : null,
        extra || null);
}

function DataCoverage({ raw }) {
    const t = useTranslate();
    const parts = (raw || "").split("|");
    const hours = parts[0] || "0";
    const readings = parseInt(parts[1], 10) || 0;
    // Not `window`: that shadows the global this module reads React and the binding
    // API off. The length of the window is C#'s to state, not this file's.
    const windowHours = parts[2] || "24";
    const observedTrips = parseInt(parts[3], 10) || 0;
    const observedHours = parts[4] || "0";
    // Shopping and leisure journeys are watched, not read from the save, so the panel
    // says how many it has seen and over how long — the reader can then judge how much
    // of the demand picture is filled in.
    const observed = h("div", { className: "ta-readout-caption" },
        (observedTrips
            ? t("ObservedTrips", "{0} shopping/leisure journeys seen over {1} h")
            : t("ObservedTripsEmpty", "no shopping/leisure journeys seen yet"))
            .replace("{0}", String(observedTrips))
            .replace("{1}", observedHours));

    if (!readings) {
        return h(Readout, {
            label: t("DataBasis", "Data collected"),
            value: t("DataBasisNone", "nothing yet"),
            caption: t("DataBasisEmpty", "readings start with your first line"),
            extra: observed,
        });
    }

    return h(Readout, {
        label: t("DataBasis", "Data collected"),
        value: hours + " h",
        caption: t("DataBasisCaption", "of the last {0} h · {1} readings")
            .replace("{0}", windowHours)
            .replace("{1}", String(readings)),
        filled: (parseFloat(hours) / parseFloat(windowHours)) * 100,
        extra: observed,
    });
}

function Equity({ raw }) {
    const t = useTranslate();
    const parts = (raw || "").split("|");
    if (parts.length < 4) {
        return h(Readout, {
            label: t("Equity", "Served journeys"),
            value: t("EquityEmpty", "not measured yet"),
        });
    }

    const share = parseFloat(parts[0]);
    const target = parseFloat(parts[2]);
    return h(Readout, {
        label: t("Equity", "Served journeys"),
        value: parts[0] + " %",
        caption: t("EquityCaption", "reach a served stop within {0} min at both ends · target {1} % · Gini {2}")
            .replace("{0}", parts[1])
            .replace("{1}", parts[2])
            .replace("{2}", parts[3]),
        filled: share,
        dim: share < target,
    });
}

// What is left of the mod's own window (author's decision 6a, 2026-09-06): the two
// figures that describe the whole city, and the switch for the map. Every knob moved to
// the Options page, and the two lists moved into the game's Transportation Overview, so
// nothing here duplicates a place the player would look first.
//
// No legend of its own any more. The infomodes derive from GradientInfomodeBasePrefab,
// so the game draws its own gradient legend for them, which is the one a player already
// knows.
function Panel() {
    const t = useTranslate();
    const visible = useBound("visible", false);
    const [collapsed, setCollapsed] = React.useState(false);

    const heatmap = useBound("heatmap", true);
    const dataCoverage = useBound("dataCoverage", "");
    const equity = useBound("equity", "");

    if (!visible) {
        return null;
    }

    // The title bar carries the two things a game panel's title bar carries: fold and
    // close. Written as buttons with symbols rather than a dash appended to the title,
    // which is what the first cut did and what read as a typo.
    const header = h("div", { className: "ta-header" },
        h("div", { className: "ta-title" }, t("Title", "Transit Architect")),
        h("button", {
            className: "ta-icon",
            title: t(collapsed ? "Expand" : "Collapse", collapsed ? "Expand" : "Collapse"),
            onClick: () => setCollapsed(!collapsed),
        }, collapsed ? "\u25be" : "\u25b4"),
        h("button", {
            className: "ta-icon",
            title: t("Close", "Close"),
            onClick: () => trigger("toggle"),
        }, "\u2715"));

    if (collapsed) {
        return h("div", { className: "ta-panel ta-panel-collapsed" }, header);
    }

    return h("div", { className: "ta-panel" },
        header,
        h("div", { className: "ta-body" },
            h(Equity, { raw: equity }),
            h(DataCoverage, { raw: dataCoverage }),
            h(Toggle, {
                label: t("Heatmap", "Suitability map"),
                value: heatmap,
                onToggle: (next) => trigger("setHeatmap", next),
            })));
}

// Styled to match the vanilla floating toggles beside it, but WITHOUT borrowing their
// class. infoview-menu-toggle_bYF carries a second rule,
// `width: calc(400rem * (0.33333 + var(--fontScale) / 1.5))`, which overrides its own
// square rule — it is the infoview menu BAR, not a square toggle. Borrowing it stretched
// this button into a 400rem lozenge across the toolbar. ta-toolbar-slot now supplies
// the whole box itself.
function ToolbarButton() {
    const t = useTranslate();
    const open = useBound("visible", false);
    return h("div", { className: "ta-toolbar-slot" },
        h("button", {
            className: "ta-toolbar-button" + (open ? " ta-toolbar-button-on" : ""),
            title: t("Title", "Transit Architect"),
            onClick: () => trigger("toggle"),
        },
            h("img", {
                className: "ta-toolbar-icon",
                src: "coui://transitarchitect/TransitArchitect.svg",
            })));
}

// ---------------------------------------------------------------------------
// Inside the game's own Transportation Overview.
//
// The player's own words for this mod: it should look as though the game came with
// it. So the verdicts live in the vanilla overview's line list rather than in a
// window of ours, and the suggestions sit under that list in the same panel.
//
// Two facts decide how (docs/ui-architecture.md, read out of the game's UI bundle):
// moduleRegistry.append does nothing on these components, because none of them
// renders {children} — only extend works; and the overview's header row is a private
// const inside its page module, so an extra column can carry a value but not a
// label. Every lookup below is therefore defensive: on anything unexpected the
// vanilla component is returned untouched, because a throw inside the registrar
// takes the whole UI module down without a visible error.

const VANILLA = {
    lineItem: "game-ui/game/components/transportation-overview-panel/transport-line-item/transport-line-item.tsx",
    page: "game-ui/game/components/transportation-overview-panel/transportation-overview-page.tsx",
    pageStyle: "game-ui/game/components/transportation-overview-panel/transportation-overview-page.module.scss",
};

// The vanilla page's own cell classes, so our column is exactly as wide as a
// numeric one and moves with the game's own layout. Empty when the module is not
// where it used to be, and then the fallback class in our CSS applies.
let pageClasses = {};

function readVanilla(registry, path, exportName) {
    try {
        const module = registry && registry.registry ? registry.registry.get(path) : null;
        return module ? module[exportName] : null;
    } catch (error) {
        console.warn("[TransitArchitect] " + path + " is not where it used to be: " + error);
        return null;
    }
}

// Rows arrive as "entityIndex|id|verdict|argument|utilisation", keyed by the ECS
// index of the line entity — which is what the vanilla row carries.
// The id is handed back to C# for an action, because an index is reused once a line
// is deleted and would then point at whatever took its slot.
function useOverviewRows() {
    const raw = useBound("overviewRows", "");
    return React.useMemo(() => {
        const map = {};
        (raw || "").split("\n").filter(Boolean).forEach((line) => {
            const parts = line.split("|");
            if (parts.length >= 5) {
                map[parts[0]] = parts;
            }
        });
        return map;
    }, [raw]);
}

// The improvement plan arrives as "mode|vehicles|delta|intervalSeconds|shape|value|fleetMin|fleetMax",
// numbers and tokens only, so the sentence can be assembled in the player's language.
function describePlan(t, raw) {
    const p = (raw || "").split("|");
    // Eight fields, as PlanPayload writes them. A short payload used to pass this
    // guard and render "NaN" and "undefined" into the player's language.
    if (p.length < 8) {
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

    // The span the game's vehicle slider allows this line; 0 as the maximum means the
    // policy prefab was not found and the span is open above.
    const fleetMax = parseInt(p[7], 10);
    if (fleetMax > 0) {
        text += t("Plan.Span", ", the game allows {0} to {1}").replace("{0}", p[6]).replace("{1}", p[7]);
    }

    const interval = parseInt(p[3], 10);
    if (interval >= 0) {
        text += t("Plan.Interval", ", i.e. an interval of about {0} s").replace("{0}", String(interval));
    }

    switch (p[4]) {
        case "Split":
            return text + t("Plan.Split", "; split it \u2014 {0} km is more than the largest {1} fleet can carry")
                .replace("{0}", p[5]).replace("{1}", mode);
        case "Reroute":
            return text + t("Plan.Reroute",
                "; or reroute it through denser ground \u2014 the suggestions list shows where demand is unserved");
        default:
            return text + t("Plan.Fine", "; the route shape looks reasonable");
    }
}

// Every other t(...) call in this file carries its English inline so a key missing
// from a locale degrades to English rather than to a blank line. These did not.
const SCHEDULE_FALLBACKS = {
    DayAndNight: "all day",
    Day: "by day only (06:00\u201322:00)",
    Night: "by night only (22:00\u201306:00)",
};

const VERDICT_FALLBACKS = {
    FleetShort: "fleet short \u2014 the game wants {0} more vehicle(s) than it can supply",
    ModeUp: "too big for its mode \u2014 upgrade to {0}",
    SplitRoute: "beyond the largest fleet of any mode \u2014 split the route",
    Remove: "empty and unjustified even as the smallest service \u2014 reroute or remove",
    ModeDown: "a smaller vehicle would do \u2014 run it as {0}",
    FleetUp: "add {0} vehicle(s)",
    FleetDown: "remove {0} vehicle(s)",
    Schedule: "run it {0}",
    Healthy: "healthy",
};

const VERDICT_COLORS = {
    FleetShort: "rgb(240, 140, 40)",
    ModeUp: "rgb(230, 60, 50)",
    SplitRoute: "rgb(230, 60, 50)",
    Remove: "rgb(130, 140, 155)",
    ModeDown: "rgb(120, 170, 220)",
    FleetUp: "rgb(240, 140, 40)",
    FleetDown: "rgb(120, 170, 220)",
    Schedule: "rgb(230, 200, 60)",
    Healthy: "rgb(80, 190, 120)",
};

// What the note's action reads. Short and imperative: it is the thing to do, and the
// reason for it is the sentence beside it.
function cellText(t, parts) {
    const verdict = parts[2];
    const argument = parts[3] || "";
    const veh = t("Vehicles", "veh");
    switch (verdict) {
        case "Healthy": return "";
        case "ModeUp":
        case "ModeDown": return "\u2192 " + t("Mode." + argument, argument);
        case "SplitRoute": return t("Cell.Split", "split");
        case "Remove": return t("Cell.Remove", "remove");
        case "Schedule": return t("Schedule." + argument, SCHEDULE_FALLBACKS[argument] || argument);
        case "FleetShort":
        case "FleetUp": return "+" + argument + " " + veh;
        case "FleetDown": return "\u2212" + argument.replace(/^-/, "") + " " + veh;
        default: return "";
    }
}

// Verdicts whose sentence says something the action does not. For the others the
// action already IS the sentence ("+2 veh", "run it by day only"), and repeating it
// beside itself is what the first cut of this did.
const EXPLAINED = { ModeUp: true, ModeDown: true, SplitRoute: true, Remove: true, FleetShort: true };

function TransitNote({ parts }) {
    const t = useTranslate();
    const id = parseInt(parts[1], 10);
    const verdict = parts[2];
    const text = cellText(t, parts);
    const structural = verdict === "ModeUp" || verdict === "SplitRoute" || verdict === "Remove";

    // The worked-out plan for whichever line was asked about last. Shown on that line
    // rather than in a panel of our own, so the answer appears where the question was
    // asked; every other line shows the short reason instead.
    const plan = useBound("improvePlan", "");
    const planFor = useBound("improvedLine", -1);
    const planDrawn = useBound("improvedRouteDrawn", false);
    const mine = planFor === id && plan !== "";

    // Nothing to say about a healthy line, and nothing is exactly what a player wants
    // to read about one.
    if (!text) {
        return null;
    }

    const reason = t("Verdict." + verdict + (parts[3] ? ".Arg" : ""), VERDICT_FALLBACKS[verdict] || verdict)
        .replace("{0}", verdict === "ModeUp" || verdict === "ModeDown"
            ? t("Mode." + parts[3], parts[3])
            : verdict === "Schedule"
                ? t("Schedule." + parts[3], SCHEDULE_FALLBACKS[parts[3]] || parts[3])
                : (parts[3] || "").replace(/^-/, ""));

    // The figure the verdict was reached on, so the line says what it rests on without
    // being asked; a dash means no route pass has measured this line yet.
    const load = parts[4] && parts[4] !== "-"
        ? t("CellLoad", "{0}% full at the recommended fleet").replace("{0}", parts[4])
        : "";
    const why = EXPLAINED[verdict]
        ? (load ? reason + " \u00b7 " + load : reason)
        : (load || reason);

    // A click does the part of the plan the game can apply by itself \u2014 a fleet or a
    // schedule change, both of which the player could make in the line panel. A verdict
    // that means building work is never acted on: the camera goes there, the mod works
    // out what it would take, and the decision stays with the player. Which is which is
    // decided in C#; this only sends the click.
    const act = () => {
        if (structural) {
            trigger("improveLine", id);
            trigger("focusLine", id);
        } else {
            trigger("applyPlan", id);
        }
    };

    return h("div", { className: "ta-note", onClick: act },
        h("div", {
            className: "ta-note-dot",
            style: { backgroundColor: VERDICT_COLORS[verdict] || "rgb(160,160,160)" },
        }),
        h("div", { className: "ta-note-action" }, text),
        h("div", { className: "ta-note-reason" },
            mine
                ? describePlan(t, plan) + (planDrawn ? " \u00b7 " + t("PlanDrawn", "the re-traced route is on the map") : "")
                : why),
        h("div", { className: "ta-note-do" },
            structural ? t("NoteShow", "show me") : t("NoteApply", "apply")));
}

// Suggestions, under the vanilla list and in its shape. They are not game entities,
// so they cannot be rows of that list; a section of our own below it is the honest
// place for them.
function SuggestionsSection() {
    const t = useTranslate();
    const raw = useBound("routeList", "");
    const update = useBound("routeUpdate", "");
    const selected = useBound("selectedRoute", -1);
    const rows = (raw || "").split("\n").filter(Boolean);
    if (!rows.length && !update) {
        return null;
    }

    return h("div", { className: "ta-suggestions" },
        h("div", { className: "ta-suggestions-head" },
            h("div", { className: "ta-suggestions-title" }, t("SuggestedLines", "Suggested lines")),
            update
                ? h("button", {
                    className: "ta-improve",
                    onClick: () => trigger("applyRouteUpdate"),
                }, t("RouteUpdate", "{0} new suggestions ready \u2014 apply").replace("{0}", update))
                : null),
        rows.map((row, index) => {
            const parts = row.split("|");
            const mode = parts[0] || "Bus";
            return h("div", {
                key: parts[6] || row,
                className: "ta-suggestion" + (index === selected ? " ta-suggestion-on" : ""),
                onClick: () => trigger("selectRoute", index),
                onMouseEnter: () => trigger("highlightRoute", index),
                onMouseLeave: () => trigger("highlightRoute", -1),
            },
                h("div", { className: "ta-swatch", style: { backgroundColor: parts[4] || "rgb(200,200,200)" } }),
                h("div", { className: (pageClasses.cellWide || "") + " ta-suggestion-name" }, t("Mode." + mode, mode)),
                h("div", { className: (pageClasses.cellDouble || "") }, (parts[1] || "?") + " " + t("Km", "km")),
                h("div", { className: (pageClasses.cellDouble || "") }, (parts[2] || "?") + " " + t("Stops", "stops")),
                h("div", { className: (pageClasses.cellDouble || "") }, (parts[3] || "?") + " " + t("Vehicles", "veh")),
                // When to run it. The game offers all day, day only or night only per
                // line, and the recommendation rests on how full this line would be in
                // each period on its own riders.
                h("div", { className: (pageClasses.cellDouble || "") },
                    t("Schedule." + (parts[7] || "DayAndNight"), SCHEDULE_FALLBACKS[parts[7]] || "all day")),
                h("div", { className: (pageClasses.cellDouble || "") + " ta-suggestion-reach" },
                    t("Reach", "unlocks {0}% of unserved travel").replace("{0}", parts[5] || "0")));
        }));
}

// Wraps a vanilla line row so our note sits UNDER it. Beside it does not work: the
// overview's header row is a private const inside the game's page module, so an added
// column can carry a value but never a heading — and worse, taking width from the row
// pulled every vanilla column out from under its own heading by however wide our text
// happened to be. Stacked, the vanilla row keeps every pixel and every alignment it
// had, and the note reads as a remark about the line above it.
function extendOverview(registry) {
    pageClasses = readVanilla(registry, VANILLA.pageStyle, "classes") || {};

    if (readVanilla(registry, VANILLA.lineItem, "TransportLineItem")) {
        registry.extend(VANILLA.lineItem, "TransportLineItem", (Original) => (props) => {
            const rows = useOverviewRows();
            const entity = props && props.line && props.line.lineData ? props.line.lineData.entity : null;
            const parts = entity && entity.index !== undefined ? rows[String(entity.index)] : null;
            if (!parts) {
                return h(Original, props);
            }

            return h("div", { className: "ta-overview-row" },
                h(Original, props),
                h(TransitNote, { parts }));
        });
    }

    if (readVanilla(registry, VANILLA.page, "TransportationOverviewPage")) {
        registry.extend(VANILLA.page, "TransportationOverviewPage", (Original) => (props) =>
            h("div", { className: "ta-overview-page" },
                h("div", { className: "ta-overview-body" }, h(Original, props)),
                h(SuggestionsSection, null)));
    }
}

const register = (moduleRegistry) => {
    if (!React || !Api || !moduleRegistry || !moduleRegistry.append) {
        console.error("[TransitArchitect] UI module could not register.");
        return;
    }

    moduleRegistry.append("Game", Panel);
    moduleRegistry.append("GameTopLeft", ToolbarButton);
    // The infoview row is no longer hidden. It was, back when the mod spoke only
    // through a window of its own; now that it offers a suitability heat map and a
    // transit-access view of the buildings, a row in the game's own infoview menu is
    // exactly where a player expects to find them.
    extendOverview(moduleRegistry);
    console.info("[TransitArchitect] Control panel registered.");
};

export const hasCSS = true;
export default register;
