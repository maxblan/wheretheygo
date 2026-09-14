// In-game control panel for Where They Go.
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
const LOC = "WhereTheyGo.Panel[";

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
    const figuresRaw = useBound("coverage", "");
    const historyRaw = useBound("dataCoverage", "");
    if (!ours) {
        return null;
    }

    const rows = [];

    const figures = (figuresRaw || "").split("|");
    if (figures.length >= 4) {
        rows.push(figureRow(
            t("Carried", "Carried by transit"),
            figures[3] + " %",
            t("CarriedCaption", "of all journeys, counting those transit makes faster than walking")));
        rows.push(figureRow(
            t("Coverage", "Within walking distance"),
            figures[0] + " %",
            t("CoverageCaption", "reach a served stop within {0} min at both ends · Gini {1}")
                .replace("{0}", figures[1]).replace("{1}", figures[2])));
    }

    const history = (historyRaw || "").split("|");
    const readings = parseInt(history[1], 10) || 0;
    const observedTrips = parseInt(history[3], 10) || 0;
    const observed = (observedTrips
        ? t("ObservedTrips", "{0} shopping/leisure journeys seen over {1} h")
        : t("ObservedTripsEmpty", "no shopping/leisure journeys seen yet"))
        .replace("{0}", String(observedTrips))
        .replace("{1}", history[4] || "0");
    rows.push(figureRow(
        t("DataBasis", "Data collected"),
        readings ? (history[0] || "0") + " h" : t("DataBasisNone", "nothing yet"),
        (readings
            ? t("DataBasisCaption", "of the last {0} h · {1} readings")
                .replace("{0}", history[2] || "72")
                .replace("{1}", String(readings))
            : t("DataBasisEmpty", "readings start with your first line")) + " · " + observed));

    return h("div", { className: "ta-figures" }, rows);
}

const VANILLA = {
    // The infoview panel's own building blocks. InfoviewPanelSpace is the divider the
    // panel draws once, between the MAP LEGEND heading and the infomode checkboxes, so
    // extending it is what puts our figures INSIDE that panel rather than in a second
    // box under it. It renders more than once only for the zone infoviews, which are
    // never ours.
    infoSpace: "game-ui/game/components/infoviews/active-infoview-panel/components/infoview-panel-space.tsx",
    infoLabels: "game-ui/game/components/infoviews/active-infoview-panel/components/labels/labels.tsx",
    // The map from a C# section's type name to the component that draws it.
    sections: "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx",
};

// The game's own label row, or null when the export has moved.
let vanillaLabel = null;

function readVanilla(registry, path, exportName) {
    try {
        const module = registry && registry.registry ? registry.registry.get(path) : null;
        return module ? module[exportName] : null;
    } catch (error) {
        console.warn("[WhereTheyGo] " + path + " is not where it used to be: " + error);
        return null;
    }
}

// The walk-to-transit row in the game's own selected-building window, drawn from what
// BuildingAccessSection wrote. The props are that section's JSON: a section whose
// group has no component here is simply not drawn, so this file and the C# side can be
// updated in either order without a broken panel in between.
function BuildingAccessRow({ walkSeconds, served, horizonMinutes }) {
    const t = useTranslate();
    const minutes = Math.round((walkSeconds || 0) / 60);
    const Label = vanillaLabel;
    // Three cases, and the third is why this is not one line: within the horizon, beyond
    // it but measured, and beyond the search itself. "over 10 min" for the last two
    // together is what made a 12-minute walk and no service at all look like the same
    // broken reading.
    const value = walkSeconds > 0
        ? t("WalkMinutes", "{0} min").replace("{0}", String(minutes))
        : t("WalkNone", "no stop in reach");

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
                : t("BuildingWalkUnserved", "further than the {0} min this city counts as served")
                    .replace("{0}", String(horizonMinutes || 0))));
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
            console.warn("[WhereTheyGo] the selected-info section map is not where it used to be.");
            return;
        }

        const merged = {};
        Object.keys(current).forEach((key) => { merged[key] = current[key]; });
        merged["WhereTheyGo.BuildingAccessSection"] = BuildingAccessRow;
        module.selectedInfoSectionComponents = merged;
    } catch (error) {
        console.warn("[WhereTheyGo] the selected-info section map has moved: " + error);
    }
}

const register = (moduleRegistry) => {
    if (!React || !Api || !moduleRegistry || !moduleRegistry.append) {
        console.error("[WhereTheyGo] UI module could not register.");
        return;
    }

    // No window and no toolbar button of the mod's own. Everything it has to say lives
    // where the player is already looking: the figures in the game's infoview panel
    // (reached from the infoview menu like every vanilla one), one row in the
    // selected-building window, and every setting in the Options page.
    extendInfoview(moduleRegistry);
    registerBuildingSection(moduleRegistry);
    console.info("[WhereTheyGo] UI registered.");
};

export const hasCSS = true;
export default register;
