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

function trigger(name, ...args) {
    Api.trigger(GROUP, name, ...args);
}

function h(tag, props, ...children) {
    return React.createElement(tag, props, ...children);
}

// The four purposes, in the order JourneyPurpose declares them. The MASK is the
// contract with C# (1 << purpose); the labels are ours to translate.
const PURPOSES = [
    { bit: 1, key: "PurposeWork", english: "Work" },
    { bit: 2, key: "PurposeSchool", english: "School" },
    { bit: 4, key: "PurposeShopping", english: "Shopping" },
    { bit: 8, key: "PurposeLeisure", english: "Leisure" },
];

// cohtml has no checkbox, so a switch is a div with a tick in it. Same shape the
// vanilla infomode rows use, close enough that the two read as one list.
function Check({ label, on, onClick }) {
    return h("div", { className: "ta-check", onClick },
        h("div", { className: "ta-check-box" + (on ? " ta-check-box-on" : "") }, on ? "\u2713" : ""),
        h("div", { className: "ta-check-label" }, label));
}

// And no <input type=range>, so a slider is a track the pointer drags across. The
// value is read from where the pointer is along the track's own width, which cohtml
// does report; a drag outside the track clamps rather than jumping.
function Slider({ value, min, max, onChange }) {
    const track = React.useRef(null);
    const pick = React.useCallback((event) => {
        const node = track.current;
        if (!node || !node.getBoundingClientRect) {
            return;
        }

        const box = node.getBoundingClientRect();
        if (!box.width) {
            return;
        }

        const share = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width));
        onChange(Math.round(min + (share * (max - min))));
    }, [min, max, onChange]);

    const filled = max > min ? ((value - min) / (max - min)) * 100 : 0;
    return h("div", {
        className: "ta-track",
        ref: track,
        onMouseDown: pick,
        onMouseMove: (event) => {
            // buttons is a bitmask; 1 is the left button held down.
            if (event.buttons & 1) {
                pick(event);
            }
        },
    },
        h("div", { className: "ta-track-rail" }),
        h("div", { className: "ta-track-fill", style: { width: filled + "%" } }),
        h("div", { className: "ta-track-knob", style: { left: filled + "%" } }));
}

// The hour the map is showing, or the whole day. Twenty-four steps and an off
// position: the slider's left end is "all day", which is where the map opens.
function TimeOfDay({ hour, playing }) {
    const t = useTranslate();
    const label = hour < 0
        ? t("WholeDay", "All day")
        : t("AtHour", "{0}:00").replace("{0}", String(hour).padStart(2, "0"));
    return h("div", { className: "ta-control" },
        h("div", { className: "ta-control-row" },
            h("div", { className: "ta-control-label" }, t("TimeOfDay", "Time of day")),
            h("div", {
                className: "ta-play" + (playing ? " ta-play-on" : ""),
                onClick: () => trigger("setHourPlay", !playing),
            }, playing ? "\u25A0" : "\u25B6"),
            h("div", { className: "ta-control-value" }, label)),
        h(Slider, {
            value: hour < 0 ? 0 : hour + 1,
            min: 0,
            max: 24,
            onChange: (step) => {
                // Touching the slider takes the day back off automatic: otherwise the
                // playback would move the hour out from under the pointer.
                if (playing) {
                    trigger("setHourPlay", false);
                }

                trigger("selectHour", step <= 0 ? -1 : step - 1);
            },
        }));
}

function Threshold({ percent }) {
    const t = useTranslate();
    return h("div", { className: "ta-control" },
        h("div", { className: "ta-control-row" },
            h("div", { className: "ta-control-label" }, t("Threshold", "Hide bands under")),
            h("div", { className: "ta-control-value" }, percent + " %")),
        h(Slider, {
            value: percent,
            min: 0,
            max: 25,
            onChange: (value) => trigger("setBandThreshold", value),
        }));
}

function Purposes({ mask }) {
    const t = useTranslate();
    return h("div", { className: "ta-control" },
        PURPOSES.map((purpose) => h(Check, {
            key: purpose.key,
            label: t(purpose.key, purpose.english),
            on: (mask & purpose.bit) !== 0,
            // Never all four off: an empty map reads as a broken one, so the last
            // one switched on stays on.
            onClick: () => {
                const next = mask ^ purpose.bit;
                trigger("setPurposes", next === 0 ? purpose.bit : next);
            },
        })));
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
    const stateRaw = useBound("mapState", "");
    const hoveredRaw = useBound("hoveredBand", "");
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
                .replace("{0}", history[2] || "24")
                .replace("{1}", String(readings))
            : t("DataBasisEmpty", "readings start with your first line")) + " · " + observed));

    // The band under the pointer takes the place of the city-wide figures while it is
    // there: the player is asking about that band, and two sets of numbers in one
    // panel is one set too many.
    const hovered = (hoveredRaw || "").split("|");
    if (hovered.length >= 4) {
        rows.length = 0;
        rows.push(figureRow(
            t("BandJourneys", "Journeys on this band"),
            hovered[0],
            t("BandWithout", "{0} % of them with no transit · busiest at {1}:00")
                .replace("{0}", hovered[1])
                .replace("{1}", String(hovered[2]).padStart(2, "0"))));
    }

    const state = (stateRaw || "").split("|");
    const hour = parseInt(state[0], 10);
    const purposes = parseInt(state[1], 10);
    const threshold = parseInt(state[2], 10);

    return h("div", { className: "ta-figures" },
        rows,
        h(TimeOfDay, { hour: isNaN(hour) ? -1 : hour, playing: state[3] === "1" }),
        h(Purposes, { mask: isNaN(purposes) ? 0xF : purposes }),
        h(Threshold, { percent: isNaN(threshold) ? 2 : threshold }));
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

// The reading in the window of a line the player clicked, drawn from what
// LineInsightSection wrote. A reading, never a verdict: no advice, no buttons.
function LineInsightRow({ measured, riders, minutesSaved, duplicatePercent, hourlyLoad }) {
    const t = useTranslate();
    const Label = vanillaLabel;
    const row = (label, value) => Label
        ? h(Label, { small: true, text: label, rightText: value })
        : h("div", { className: "ta-figure-row" },
            h("div", { className: "ta-figure-label" }, label),
            h("div", { className: "ta-figure-value" }, value));

    if (!measured) {
        return h("div", { className: "ta-building" },
            row(t("LineRiders", "Journeys using this line"), t("LineMeasuring", "measuring…")),
            h("div", { className: "ta-figure-caption" },
                t("LineMeasuringCaption", "routing the city again without this line")));
    }

    return h("div", { className: "ta-building" },
        row(t("LineRiders", "Journeys using this line"), String(riders || 0)),
        h("div", { className: "ta-figure-caption" },
            t("LineSaved", "saving {0} passenger-minutes a day against walking and the rest of your network")
                .replace("{0}", String(minutesSaved || 0))),
        row(t("LineDuplicate", "No slower without it"), (duplicatePercent || 0) + " %"),
        h("div", { className: "ta-figure-caption" },
            t("LineDuplicateCaption", "of those journeys would be no slower if this line did not exist")),
        h(HourlyLoad, { hourlyLoad }));
}

// Load hour by hour against the seats that were actually out in that hour. An hour
// the window never watched is a gap, not a zero.
function HourlyLoad({ hourlyLoad }) {
    const t = useTranslate();
    const hours = Array.isArray(hourlyLoad) ? hourlyLoad : [];
    if (!hours.length) {
        return null;
    }

    let seen = 0;
    for (let i = 0; i < hours.length; i++) {
        if (hours[i] >= 0) {
            seen++;
        }
    }

    return h("div", { className: "ta-hours" },
        h("div", { className: "ta-figure-caption" },
            seen
                ? t("LineHours", "How full it runs, hour by hour")
                : t("LineHoursEmpty", "no readings yet — they start once the line runs")),
        h("div", { className: "ta-hours-bars" },
            hours.map((share, hour) => h("div", {
                key: hour,
                className: "ta-hour" + (share < 0 ? " ta-hour-gap" : ""),
                // A bar is at least a sliver so an hour with almost nobody aboard is
                // still visibly an hour that was watched.
                style: share >= 0 ? { height: Math.max(2, Math.min(100, share * 100)) + "%" } : null,
            }))));
}

// Puts the two city-wide figures inside the game's infoview panel, under its heading.
function extendInfoview(registry) {
    vanillaLabel = readVanilla(registry, VANILLA.infoLabels, "InfoviewPanelLabel");

    if (readVanilla(registry, VANILLA.infoSpace, "InfoviewPanelSpace")) {
        registry.extend(VANILLA.infoSpace, "InfoviewPanelSpace", (Original) => (props) =>
            h(React.Fragment, null, h(Original, props), h(InfoviewFigures, null)));
    }
}

// Adds our entries to the game's section map, keyed by the C# type name each section
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
        merged["WhereTheyGo.LineInsightSection"] = LineInsightRow;
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
