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

// Bindings the game itself publishes, read the same way as our own. Cached per
// group+name because Api.bindValue makes a new subscription every call.
const foreign = {};

function foreignBinding(group, name, fallback) {
    const key = group + "." + name;
    if (!foreign[key]) {
        foreign[key] = Api.bindValue(group, name, fallback);
    }
    return foreign[key];
}

function useForeign(group, name, fallback) {
    return Api.useValue(foreignBinding(group, name, fallback));
}

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

// The two city-wide figures, in the game's own infoview panel (author's request
// 2026-09-06). They used to live in a window of the mod's own, which is one window too
// many: they describe the map that panel is the legend for.
//
// Built from the game's own InfoviewPanelLabel where it resolves, so the rows match the
// ones the vanilla panels draw, and from plain markup where it does not — a renamed
// export must cost a plainer row, never a missing figure.
function figureRow(label, value, caption) {
    const Label = vanillaLabel;
    return h("div", { className: "ta-figure" },
        Label
            ? h(Label, { small: true, text: label, rightText: value })
            : h("div", { className: "ta-figure-row" },
                h("div", { className: "ta-figure-label" }, label),
                h("div", { className: "ta-figure-value" }, value)),
        caption ? h("div", { className: "ta-figure-caption" }, caption) : null);
}

function InfoviewFigures() {
    const t = useTranslate();
    // Hooks first and unconditionally: the panel this sits in is shared with every
    // vanilla infoview, so this component renders for all of them and returns nothing
    // for the ones that are not ours.
    const ours = useBound("heatmap", false);
    const equityRaw = useBound("equity", "");
    const coverageRaw = useBound("dataCoverage", "");
    if (!ours) {
        return null;
    }

    const rows = [];

    const equity = (equityRaw || "").split("|");
    if (equity.length >= 4) {
        rows.push(figureRow(
            t("Equity", "Served journeys"),
            equity[0] + " %",
            t("EquityCaption", "reach a served stop within {0} min at both ends · target {1} % · Gini {2}")
                .replace("{0}", equity[1]).replace("{1}", equity[2]).replace("{2}", equity[3])));
    }

    const coverage = (coverageRaw || "").split("|");
    const readings = parseInt(coverage[1], 10) || 0;
    const observedTrips = parseInt(coverage[3], 10) || 0;
    const observed = (observedTrips
        ? t("ObservedTrips", "{0} shopping/leisure journeys seen over {1} h")
        : t("ObservedTripsEmpty", "no shopping/leisure journeys seen yet"))
        .replace("{0}", String(observedTrips))
        .replace("{1}", coverage[4] || "0");
    rows.push(figureRow(
        t("DataBasis", "Data collected"),
        readings ? (coverage[0] || "0") + " h" : t("DataBasisNone", "nothing yet"),
        (readings
            ? t("DataBasisCaption", "of the last {0} h · {1} readings")
                .replace("{0}", coverage[2] || "24")
                .replace("{1}", String(readings))
            : t("DataBasisEmpty", "readings start with your first line")) + " · " + observed));

    return h("div", { className: "ta-figures" }, rows);
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
    // The infoview panel's own building blocks. InfoviewPanelSpace is the divider the
    // panel draws once, between the MAP LEGEND heading and the infomode checkboxes, so
    // extending it is what puts our figures INSIDE that panel rather than in a second
    // box under it. It renders more than once only for the zone infoviews, which are
    // never ours.
    infoSpace: "game-ui/game/components/infoviews/active-infoview-panel/components/infoview-panel-space.tsx",
    infoLabels: "game-ui/game/components/infoviews/active-infoview-panel/components/labels/labels.tsx",
    // The map from a C# section's `group` to the component that draws it. Its setter
    // is an Object.assign, so writing one key adds a section without disturbing the
    // hundred the game registers.
    sections: "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx",
};

let pageClasses = {};

// The game's own label row, or null when the export has moved.
let vanillaLabel = null;

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

// The mod's mode names against the game's TransportType, which is what the overview's
// tab strip is keyed by. Only the two that differ are interesting: a metro is a Subway
// and a ferry is a Ship.
const TRANSPORT_TYPE = { Bus: "Bus", Tram: "Tram", Metro: "Subway", Train: "Train", Ferry: "Ship" };

// Suggestions, under the vanilla list and in its shape. They are not game entities, so
// they cannot be rows of that list; a section of our own below it is the honest place.
//
// Filtered to the transport tab the player is on (author's request 2026-09-06): a list
// mixing trams into BUS LINES is both confusing and too long for the room there is. The
// tab comes from the game's own binding, so the two cannot disagree about which mode is
// being looked at.
//
// It carries its own header row, unlike the verdict notes above: this IS our table, so
// nothing stops it having headings, and without them "2.7 km · 8 · 6" is a puzzle. Every
// cell is one line — the first cut wrapped the reach sentence onto a second line and
// turned five rows into ten.
function SuggestionsSection() {
    const t = useTranslate();
    const raw = useBound("routeList", "");
    const update = useBound("routeUpdate", "");
    const selected = useBound("selectedRoute", -1);
    const tab = useForeign("transportationOverview", "selectedPassengerType", "Bus");

    // While this section is mounted the overview is open, and the suggested lines are
    // drawn on the map whether or not the mod's infoview is on. Looking at the list and
    // not seeing the line it describes was the whole of the complaint.
    React.useEffect(() => {
        trigger("setOverviewOpen", true);
        return () => trigger("setOverviewOpen", false);
    }, []);

    // The index is the row's position in the FULL list, because that is what C# keys
    // its selection and highlight on.
    const rows = (raw || "").split("\n")
        .map((row, index) => ({ parts: row.split("|"), index }))
        .filter((row) => row.parts.length > 1 && TRANSPORT_TYPE[row.parts[0]] === tab);

    const wide = pageClasses.cellWide || "";
    const cell = pageClasses.cellDouble || "";

    return h("div", { className: "ta-suggestions" },
        h("div", { className: "ta-suggestions-head" },
            h("div", { className: "ta-suggestions-title" }, t("SuggestedLines", "Suggested lines")),
            update
                ? h("button", {
                    className: "ta-improve",
                    onClick: () => trigger("applyRouteUpdate"),
                }, t("RouteUpdate", "{0} new suggestions ready \u2014 apply").replace("{0}", update))
                : null),

        // Says so rather than vanishing: an empty space where a section was reads as a
        // fault, and "none for this mode" is itself an answer.
        rows.length === 0
            ? h("div", { className: "ta-suggestions-empty" },
                t("NoSuggestions", "nothing worth adding for this mode right now"))
            : h(React.Fragment, null,
                h("div", { className: "ta-suggestion ta-suggestion-head" },
                    h("div", { className: "ta-swatch ta-swatch-blank" }),
                    h("div", { className: wide + " ta-suggestion-name" }, t("ColMode", "Mode")),
                    h("div", { className: cell }, t("ColLength", "Length")),
                    h("div", { className: cell }, t("ColStops", "Stops")),
                    h("div", { className: cell }, t("ColVehicles", "Vehicles")),
                    h("div", { className: cell }, t("ColSchedule", "Runs")),
                    h("div", { className: cell }, t("ColReach", "Unlocks"))),

                rows.map(({ parts, index }) => {
                    const mode = parts[0] || "Bus";
                    return h("div", {
                        key: parts[6] || index,
                        className: "ta-suggestion" + (index === selected ? " ta-suggestion-on" : ""),
                        onClick: () => trigger("selectRoute", index),
                        onMouseEnter: () => trigger("highlightRoute", index),
                        onMouseLeave: () => trigger("highlightRoute", -1),
                    },
                        h("div", { className: "ta-swatch", style: { backgroundColor: parts[4] || "rgb(200,200,200)" } }),
                        h("div", { className: wide + " ta-suggestion-name" }, t("Mode." + mode, mode)),
                        h("div", { className: cell }, (parts[1] || "?") + " " + t("Km", "km")),
                        h("div", { className: cell }, parts[2] || "?"),
                        h("div", { className: cell }, parts[3] || "?"),
                        // When to run it. The game offers all day, day only or night
                        // only per line, and the recommendation rests on how full this
                        // line would be in each period on its own riders.
                        h("div", { className: cell },
                            t("Schedule." + (parts[7] || "DayAndNight"), SCHEDULE_FALLBACKS[parts[7]] || "all day")),
                        // The figure the list is ordered by, so the gap between the
                        // first row and the second is visible rather than implied.
                        h("div", { className: cell + " ta-suggestion-reach" }, (parts[5] || "0") + " %"));
                })));
}

// The walk-to-transit row in the game's own selected-building window, drawn from what
// BuildingAccessSection wrote. The props are that section's JSON: a section whose
// group has no component here is simply not drawn, so this file and the C# side can be
// updated in either order without a broken panel in between.
function BuildingAccessRow({ walkSeconds, served, horizonMinutes }) {
    const t = useTranslate();
    const minutes = Math.round((walkSeconds || 0) / 60);
    const Label = vanillaLabel;
    const value = served
        ? t("WalkMinutes", "{0} min").replace("{0}", String(minutes))
        : t("WalkBeyond", "over {0} min").replace("{0}", String(horizonMinutes || 0));

    // Deliberately the same words as the infomode this number colours the building
    // for, so a player who has both open sees one fact stated twice, not two facts.
    return h("div", { className: "ta-building" },
        Label
            ? h(Label, { small: true, text: t("BuildingWalk", "Walk to transit"), rightText: value })
            : h("div", { className: "ta-figure-row" },
                h("div", { className: "ta-figure-label" }, t("BuildingWalk", "Walk to transit")),
                h("div", { className: "ta-figure-value" }, value)),
        h("div", { className: "ta-figure-caption" },
            served
                ? t("BuildingWalkServed", "to the nearest stop your lines serve")
                : t("BuildingWalkUnserved", "no served stop within the walking horizon")));
}

// Puts the two city-wide figures inside the game's infoview panel, under its heading.
function extendInfoview(registry) {
    vanillaLabel = readVanilla(registry, VANILLA.infoLabels, "InfoviewPanelLabel");

    if (readVanilla(registry, VANILLA.infoSpace, "InfoviewPanelSpace")) {
        registry.extend(VANILLA.infoSpace, "InfoviewPanelSpace", (Original) => (props) =>
            h(React.Fragment, null, h(Original, props), h(InfoviewFigures, null)));
    }
}

// Adds one entry to the game's section map, keyed by the C# type name the section
// writes — the map reads {"Game.UI.InGame.DescriptionSection": …}, not by the section's
// `group`.
//
// The whole map is read back and copied first. Its setter ASSIGNS rather than merging
// (`set selectedInfoSectionComponents(e){GAe=e}`), so writing a one-key object took the
// component of every vanilla section with it and the selected-info panel rendered
// "Unknown element type" for all of them.
function registerBuildingSection(registry) {
    try {
        const module = registry && registry.registry ? registry.registry.get(VANILLA.sections) : null;
        const current = module ? module.selectedInfoSectionComponents : null;
        if (!current) {
            console.warn("[TransitArchitect] the selected-info section map is not where it used to be.");
            return;
        }

        const merged = {};
        Object.keys(current).forEach((key) => { merged[key] = current[key]; });
        merged["TransitArchitect.BuildingAccessSection"] = BuildingAccessRow;
        module.selectedInfoSectionComponents = merged;
    } catch (error) {
        console.warn("[TransitArchitect] the selected-info section map has moved: " + error);
    }
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

    // No window and no toolbar button of the mod's own. Everything it has to say lives
    // where the player is already looking: the figures in the game's infoview panel
    // (reached from the infoview menu like every vanilla one), the verdicts and
    // suggestions in the Transportation Overview, one row in the selected-building
    // window, and every setting in the Options page.
    extendInfoview(moduleRegistry);
    extendOverview(moduleRegistry);
    registerBuildingSection(moduleRegistry);
    console.info("[TransitArchitect] UI registered.");
};

export const hasCSS = true;
export default register;
