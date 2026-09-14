// In-game panel for Where They Go.
//
// Hand-written ES module rather than a bundled React app: the game exposes React, its
// binding API and — the part that matters most here — its own UI components on
// `window`, so no build toolchain is involved and the file deploys next to the DLL.
//
// Everything visible is built from the GAME'S components rather than from divs of our
// own. `window["cs2/ui"]` is the supported surface (PanelSection, PanelSectionRow,
// Tooltip, FormattedParagraphs); the module registry has the rest — the real slider
// with its drag, gamepad and sound, the real checkbox, the infoview panel's own
// section and gradient bar, the stacked bar chart, the Chart.js wrapper and the
// tooltip that follows the cursor. A hand-rolled copy of any of these is a copy that
// looks almost right and behaves almost right.
//
// Every lookup falls back: a renamed export costs a plainer row, never a blank panel.

const React = window.React;
const Api = window["cs2/api"];
const L10n = window["cs2/l10n"];
const CsUi = window["cs2/ui"] || {};
const CsInput = window["cs2/input"] || {};

const GROUP = "wheretheygo";
// Every panel string is looked up under this prefix. The English text stays inline as
// the fallback argument, so a key missing from a locale file degrades to English
// rather than showing the raw key.
const LOC = "WhereTheyGo.Panel[";

// The game's localization, as a (key, englishFallback) => string. useLocalization is a
// hook, so this is one too and must be called at the top of a component.
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

// ---------------------------------------------------------------------------
// The game's own components.

const VANILLA = {
    infoSpace: "game-ui/game/components/infoviews/active-infoview-panel/components/infoview-panel-space.tsx",
    infoLabels: "game-ui/game/components/infoviews/active-infoview-panel/components/labels/labels.tsx",
    infoSection: "game-ui/game/components/infoviews/active-infoview-panel/components/sections/infoview-panel-section.tsx",
    valueBarSection: "game-ui/game/components/infoviews/active-infoview-panel/components/sections/value-bar-section.tsx",
    slider: "game-ui/common/input/slider/slider.tsx",
    checkbox: "game-ui/common/input/toggle/checkbox/checkbox.tsx",
    infoBarChart: "game-ui/common/charts/bar-chart/info-bar-chart.tsx",
    responsiveChart: "game-ui/common/charts/responsive-chart/responsive-chart.tsx",
    mouseTooltip: "game-ui/common/tooltip/floating-mouse-tooltip/floating-mouse-tooltip.tsx",
    colorLegend: "game-ui/common/charts/legends/color-legend.tsx",
    // The map from a C# section's type name to the component that draws it.
    sections: "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx",
};

// Resolved once at registration. Null where an export has moved, which every user of
// these checks for.
const V = {
    Label: null,
    Section: null,
    ValueBarSection: null,
    Slider: null,
    Checkbox: null,
    InfoBarChart: null,
    Chart: null,
    MouseTooltip: null,
    LegendSymbol: null,
};

function readVanilla(registry, path, exportName) {
    try {
        const module = registry && registry.registry ? registry.registry.get(path) : null;
        return module ? (module[exportName] || null) : null;
    } catch (error) {
        console.warn("[WhereTheyGo] " + path + "#" + exportName + " is not where it used to be: " + error);
        return null;
    }
}

function resolveVanilla(registry) {
    V.Label = readVanilla(registry, VANILLA.infoLabels, "InfoviewPanelLabel");
    V.Section = readVanilla(registry, VANILLA.infoSection, "InfoviewPanelSection");
    V.ValueBarSection = readVanilla(registry, VANILLA.valueBarSection, "ValueBarSection");
    V.Slider = readVanilla(registry, VANILLA.slider, "Slider");
    V.Checkbox = readVanilla(registry, VANILLA.checkbox, "Checkbox");
    V.InfoBarChart = readVanilla(registry, VANILLA.infoBarChart, "InfoBarChart");
    V.Chart = readVanilla(registry, VANILLA.responsiveChart, "ResponsiveChart");
    V.MouseTooltip = readVanilla(registry, VANILLA.mouseTooltip, "FloatingMouseTooltip");
    V.LegendSymbol = readVanilla(registry, VANILLA.colorLegend, "ColorLegendSymbol");
}

// A bordered box in the infoview panel, as the vanilla panels draw one.
function Section({ tooltip, children }) {
    if (V.Section) {
        return h(V.Section, { disableFocus: true, tooltip }, children);
    }

    return h("div", { className: "wtg-section" }, children);
}

// A label with a value on the right. `small` is the game's secondary weight.
function Row({ label, value, small, uppercase }) {
    if (V.Label) {
        return h(V.Label, { small, uppercase, text: label, rightText: value });
    }

    return h("div", { className: "wtg-row" + (small ? " wtg-row-small" : "") },
        h("div", { className: "wtg-row-label" + (uppercase ? " wtg-upper" : "") }, label),
        value === undefined || value === null ? null : h("div", { className: "wtg-row-value" }, value));
}

function Caption({ children }) {
    return h("div", { className: "wtg-caption" }, children);
}

// ---------------------------------------------------------------------------
// Formatting, in the player's own locale and units.

function number(value, unit) {
    const Localized = L10n && L10n.LocalizedNumber;
    const Unit = L10n && L10n.Unit;
    if (Localized && Unit) {
        return h(Localized, { value, unit: unit || Unit.Integer });
    }

    return String(Math.round(value));
}

function integer(value) {
    return number(Math.round(value));
}

function percent(share) {
    const Unit = L10n && L10n.Unit;
    return number(share * 100, Unit ? Unit.Percentage : undefined);
}

// An hour of the day, as the player's own clock writes it. Falls back to 24-hour text,
// which is what the mod's own log uses.
function useHourText() {
    const t = useTranslate();
    return React.useCallback(
        (hour) => t("AtHour", "{0}:00").replace("{0}", String(hour).padStart(2, "0")),
        [t]);
}

// ---------------------------------------------------------------------------
// Controls.

// The four purposes, in the order JourneyPurpose declares them. The MASK is the
// contract with C# (1 << purpose); the colours are the panel's own, because purpose is
// not something the map paints — the map paints how much of a band transit carries.
const PURPOSES = [
    { bit: 1, key: "PurposeWork", english: "Work", colour: "#4a90d9" },
    { bit: 2, key: "PurposeSchool", english: "School", colour: "#8e7cc3" },
    { bit: 4, key: "PurposeShopping", english: "Shopping", colour: "#e0a43c" },
    { bit: 8, key: "PurposeLeisure", english: "Leisure", colour: "#5fa86a" },
];

function Check({ label, on, onChange }) {
    if (V.Checkbox) {
        // FOCUS_DISABLED, exactly as vanilla's own InfomodeItem passes it to this
        // component. Without it each checkbox claims a focus key of its own, and the
        // row it sits in is a PassThroughFocusController, which can host only one
        // child: four purposes meant four "Cannot register second focus key" errors in
        // UI.log on every render.
        return h(V.Checkbox, {
            checked: on,
            onChange,
            focusKey: CsInput.FOCUS_DISABLED,
            className: "wtg-check",
        });
    }

    return h("div", {
        className: "wtg-check-fallback" + (on ? " wtg-check-on" : ""),
        onClick: () => onChange(!on),
    }, on ? "✓" : "", label);
}

function Slider({ value, start, end, onChange, className }) {
    if (V.Slider) {
        return h(V.Slider, {
            value,
            start,
            end,
            className: "wtg-slider " + (className || ""),
            onChange: (next) => onChange(Math.round(next)),
        });
    }

    // No vanilla slider: a track the pointer drags across, which is what this panel
    // used before the game's own was found.
    return h("div", {
        className: "wtg-track " + (className || ""),
        onMouseDown: (event) => pickAlong(event, start, end, onChange),
        onMouseMove: (event) => {
            if (event.buttons & 1) {
                pickAlong(event, start, end, onChange);
            }
        },
    },
        h("div", { className: "wtg-track-fill", style: { width: fillShare(value, start, end) + "%" } }),
        h("div", { className: "wtg-track-knob", style: { left: fillShare(value, start, end) + "%" } }));
}

function fillShare(value, start, end) {
    return end > start ? Math.min(100, Math.max(0, ((value - start) / (end - start)) * 100)) : 0;
}

function pickAlong(event, start, end, onChange) {
    const node = event.currentTarget;
    if (!node || !node.getBoundingClientRect) {
        return;
    }

    const box = node.getBoundingClientRect();
    if (!box.width) {
        return;
    }

    const share = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width));
    onChange(Math.round(start + (share * (end - start))));
}

// The day, as twenty-four columns of how much the city travels in each hour.
//
// The control IS the reading: before the player picks anything, the strip has already
// said when the city moves and how sharp its peaks are. Clicking a column shows the map
// at that hour; clicking it again goes back to the whole day, which is where the map
// opens and the only state in which there is no direction to draw.
function HourStrip({ hour, hourly, playing }) {
    const t = useTranslate();
    const hourText = useHourText();
    let peak = 0;
    for (let i = 0; i < hourly.length; i++) {
        if (hourly[i] > peak) {
            peak = hourly[i];
        }
    }

    const select = (next) => {
        // Touching the strip takes the day off automatic: otherwise playback would move
        // the hour out from under the pointer.
        if (playing) {
            trigger("setHourPlay", false);
        }

        trigger("selectHour", next === hour ? -1 : next);
    };

    return h("div", { className: "wtg-hours" },
        h("div", { className: "wtg-hours-head" },
            h("div", { className: "wtg-hours-title" }, t("TimeOfDay", "Time of day")),
            h("div", {
                className: "wtg-play" + (playing ? " wtg-play-on" : ""),
                onClick: () => trigger("setHourPlay", !playing),
            }, playing ? "■" : "▶"),
            h("div", { className: "wtg-hours-value" },
                hour < 0 ? t("WholeDay", "All day") : hourText(hour))),
        h("div", { className: "wtg-hours-bars" },
            hourly.map((value, index) => h("div", {
                key: index,
                className: "wtg-hour"
                    + (index === hour ? " wtg-hour-on" : "")
                    + (hour < 0 ? " wtg-hour-all" : ""),
                onClick: () => select(index),
            },
                h("div", {
                    className: "wtg-hour-fill",
                    // At least a sliver, so an hour with almost nobody travelling still
                    // reads as an hour rather than as a hole in the chart.
                    style: { height: (peak > 0 ? Math.max(2, (value / peak) * 100) : 0) + "%" },
                })))),
        h("div", { className: "wtg-hours-axis" },
            [0, 6, 12, 18].map((mark) => h("div", { key: mark, className: "wtg-hours-mark" }, hourText(mark)))));
}

// Which purposes the map is drawing, with what each is worth. The switches carry their
// own weight beside their name: "Leisure 12 000" answers a question the tick box on its
// own cannot.
function Purposes({ mask, weights }) {
    const t = useTranslate();
    const values = PURPOSES.map((purpose, index) => (mask & purpose.bit) !== 0 ? (weights[index] || 0) : 0);
    let total = 0;
    for (const value of values) {
        total += value;
    }

    const toggle = (purpose) => {
        const next = mask ^ purpose.bit;
        // Never all four off: an empty map reads as a broken one, so the last one
        // switched on stays on.
        trigger("setPurposes", next === 0 ? purpose.bit : next);
    };

    const legend = h("div", { className: "wtg-purposes" },
        PURPOSES.map((purpose, index) => h("div", {
            key: purpose.key,
            className: "wtg-purpose" + ((mask & purpose.bit) !== 0 ? "" : " wtg-purpose-off"),
            onClick: () => toggle(purpose),
        },
            h(Check, { label: "", on: (mask & purpose.bit) !== 0, onChange: () => toggle(purpose) }),
            h("div", { className: "wtg-purpose-swatch", style: { backgroundColor: purpose.colour } }),
            h("div", { className: "wtg-purpose-label" }, t(purpose.key, purpose.english)),
            h("div", { className: "wtg-purpose-value" }, integer(weights[index] || 0)))));

    const data = { values, total: total > 0 ? total : 1 };
    const colours = PURPOSES.map((purpose) => purpose.colour);
    if (V.InfoBarChart) {
        return h(V.InfoBarChart, {
            title: t("Purposes", "Journeys by purpose"),
            colors: colours,
            labels: PURPOSES.map((purpose) => t(purpose.key, purpose.english)),
            data,
            customLegend: legend,
            className: "wtg-purpose-chart",
        });
    }

    return h("div", null, h(Row, { label: t("Purposes", "Journeys by purpose"), uppercase: true }), legend);
}

// How much of the map the threshold is hiding, and what each width class means.
function Bands({ thresholdPercent, shown, total, hiddenShare, breaks, widths, ramp }) {
    const t = useTranslate();
    return h("div", { className: "wtg-bands" },
        h(Row, {
            uppercase: true,
            label: t("Bands", "Bands"),
            value: t("BandsShown", "{0} of {1}").replace("{0}", String(shown)).replace("{1}", String(total)),
        }),
        h(Slider, {
            value: thresholdPercent,
            start: 0,
            end: 25,
            onChange: (value) => trigger("setBandThreshold", value),
        }),
        h(Caption, null,
            t("ThresholdCaption", "hiding everything under {0} % of the strongest band")
                .replace("{0}", String(thresholdPercent)),
            hiddenShare > 0.005
                ? " · " + t("HiddenShare", "{0} % of the journeys")
                    .replace("{0}", String(Math.round(hiddenShare * 100)))
                : ""),
        h(WidthLegend, { breaks, widths, ramp }));
}

// What a width means, in journeys a day. The point of drawing widths in classes at all:
// a class can be read off a legend, a continuous ramp can only be compared.
function WidthLegend({ breaks, widths, ramp }) {
    const t = useTranslate();
    const mid = ramp && ramp.length > 1 ? ramp[1] : "#c07840";
    const rows = [];
    for (let i = 0; i < widths.length; i++) {
        const low = i === 0 ? 0 : breaks[i - 1];
        const high = i < breaks.length ? breaks[i] : null;
        const text = high === null
            ? t("ClassOver", "{0} and more").replace("{0}", String(Math.round(low)))
            : low === 0
                ? t("ClassUnder", "under {0}").replace("{0}", String(Math.round(high)))
                : String(Math.round(low)) + "–" + String(Math.round(high));
        rows.push(h("div", { className: "wtg-class", key: i },
            h("div", { className: "wtg-class-bar" },
                h("div", {
                    className: "wtg-class-fill",
                    // Drawn at the same relative thickness the map uses, so the legend
                    // is a sample of the thing rather than a picture of it.
                    style: {
                        height: Math.max(2, (widths[i] / widths[widths.length - 1]) * 14) + "rem",
                        backgroundColor: mid,
                    },
                })),
            h("div", { className: "wtg-class-label" }, text)));
    }

    return h("div", null,
        h(Caption, null, t("ClassCaption", "width: journeys a day")),
        h("div", { className: "wtg-classes" }, rows));
}

// ---------------------------------------------------------------------------
// The infoview panel.

function CarriedFigure({ share, ramp }) {
    const t = useTranslate();
    const title = h(Row, {
        uppercase: true,
        label: t("Carried", "Carried by transit"),
        value: percent(share),
    });
    const caption = h(Caption, null,
        t("CarriedCaption", "of all journeys — counting those transit makes faster than walking"));

    // The gradient IS the map's legend, and the pointer says where the city sits on it.
    // One object instead of a figure in one place and a colour key in another.
    if (V.ValueBarSection && ramp && ramp.length === 3) {
        return h(V.ValueBarSection, {
            title,
            value: { min: 0, max: 1, current: share },
            gradient: { stops: [{ color: ramp[0], offset: 0 }, { color: ramp[1], offset: 0.5 }, { color: ramp[2], offset: 1 }] },
        }, caption);
    }

    return h(Section, null, title, caption);
}

function CoverageFigure({ share, walkMinutes, ramp }) {
    const t = useTranslate();
    const title = h(Row, {
        uppercase: true,
        label: t("Coverage", "Within walking distance"),
        value: percent(share),
    });
    const caption = h(Caption, null,
        t("CoverageCaption", "reach a served stop within {0} min at both ends")
            .replace("{0}", String(walkMinutes)));

    if (V.ValueBarSection && ramp && ramp.length === 3) {
        return h(V.ValueBarSection, {
            title,
            value: { min: 0, max: 1, current: share },
            // Reversed against the map's own ramp: on the map the deep end is a LONG
            // walk, and here a high number is a good one.
            gradient: { stops: [{ color: ramp[2], offset: 0 }, { color: ramp[1], offset: 0.5 }, { color: ramp[0], offset: 1 }] },
        }, caption);
    }

    return h(Section, null, title, caption);
}

// Where the numbers come from. Deliberately last and deliberately quiet: it is
// provenance, not a finding about the city. It used to sit between the two figures in
// the same weight, which made "0.8 h" look like a third headline.
function DataBasis({ figures }) {
    const t = useTranslate();
    const seen = figures.observedJourneys > 0;
    return h(Section, null,
        h(Row, { uppercase: true, small: true, label: t("DataBasis", "Where these come from") }),
        h(Caption, null,
            seen
                ? t("ObservedTrips", "{0} shopping and leisure journeys seen over {1} h; commutes are read from the save")
                    .replace("{0}", String(figures.observedJourneys))
                    .replace("{1}", figures.observedHours.toFixed(1))
                : t("ObservedTripsEmpty", "no shopping or leisure journeys seen yet; commutes are read from the save")),
        h(Caption, null,
            figures.readings > 0
                ? t("DataBasisCaption", "line readings cover {0} h of the last {1} h, from {2} readings")
                    .replace("{0}", figures.coveredHours.toFixed(1))
                    .replace("{1}", String(Math.round(figures.windowHours)))
                    .replace("{2}", String(figures.readings))
                : t("DataBasisEmpty", "line readings start with your first line")));
}

// The band under the pointer, in a tooltip at the cursor rather than in the panel.
//
// It belongs at the cursor: the player is pointing at one band out of hundreds, and a
// number that appears somewhere else entirely has to be matched up by eye. The game has
// a tooltip that follows the pointer in screen space, which is exactly this job.
function HoveredBand({ band, hour }) {
    const t = useTranslate();
    const hourText = useHourText();
    if (!band || !V.MouseTooltip) {
        return null;
    }

    // With an hour picked, `journeys` is that hour's departures, not the day's total —
    // and saying "a day" over an hour's number is simply a wrong label.
    const head = hour < 0
        ? t("BandJourneys", "{0} journeys a day").replace("{0}", String(Math.round(band.dayJourneys)))
        : t("BandJourneysAtHour", "{0} journeys at {1}")
            .replace("{0}", String(Math.round(band.journeys)))
            .replace("{1}", hourText(hour));
    const content = h("div", { className: "wtg-tip" },
        h("div", { className: "wtg-tip-head" }, head),
        hour < 0 ? null : h("div", { className: "wtg-tip-line" },
            t("BandJourneysDay", "{0} a day in all").replace("{0}", String(Math.round(band.dayJourneys)))),
        h("div", { className: "wtg-tip-line" },
            t("BandWithout", "{0} % travel without transit")
                .replace("{0}", String(Math.round((1 - band.carriedShare) * 100)))),
        h("div", { className: "wtg-tip-line" },
            t("BandPeak", "busiest at {0}").replace("{0}", hourText(band.peakHour))));

    return h(V.MouseTooltip, {
        tooltip: content,
        screenSpacePosition: true,
        alwaysVisible: true,
        className: "wtg-tip-box",
    });
}

function InfoviewFigures() {
    // Hooks first and unconditionally: the panel this sits in is shared with every
    // vanilla infoview, so this component renders for all of them and returns nothing
    // for the ones that are not ours.
    const ours = useBound("infoviewActive", false);
    const figures = useBound("figures", null);
    const state = useBound("mapState", null);
    const band = useBound("hoveredBand", null);
    if (!ours || !figures || !state) {
        return null;
    }

    const hourly = state.hourly || [];
    return h("div", { className: "wtg-panel" },
        h(CarriedFigure, { share: figures.carriedShare, ramp: state.bandRamp }),
        h(CoverageFigure, {
            share: figures.coverageShare,
            walkMinutes: figures.coverageWalkMinutes,
            ramp: state.walkRamp,
        }),
        h(Section, null, h(HourStrip, {
            hour: state.hour,
            hourly,
            playing: state.playing,
        })),
        h(Section, null, h(Purposes, {
            mask: state.purposes,
            weights: state.purposeWeights || [],
        })),
        h(Section, null, h(Bands, {
            thresholdPercent: state.thresholdPercent,
            shown: state.bandsShown,
            total: state.bandsTotal,
            hiddenShare: state.hiddenShare,
            breaks: state.classBreaks || [],
            widths: state.classWidths || [],
            ramp: state.bandRamp,
        })),
        h(DataBasis, { figures }),
        h(HoveredBand, { band, hour: state.hour }));
}

// ---------------------------------------------------------------------------
// The selected-object windows.

// The walk-to-transit row in the game's own selected-building window, drawn from what
// BuildingAccessSection wrote. The props are that section's JSON.
function BuildingAccessRow({ walkSeconds, served, horizonMinutes }) {
    const t = useTranslate();
    const minutes = Math.round((walkSeconds || 0) / 60);
    // Three cases, and the third is why this is not one line: within the horizon, beyond
    // it but measured, and beyond the search itself. "over 10 min" for the last two
    // together is what made a 12-minute walk and no service at all look the same.
    const value = walkSeconds > 0
        ? t("WalkMinutes", "{0} min").replace("{0}", String(minutes))
        : t("WalkNone", "no stop in reach");

    return h(PanelSection, null,
        h(PanelRow, { uppercase: true, left: t("BuildingSection", "Walk to transit") }),
        h(PanelRow, { subRow: true, left: t("BuildingWalk", "Nearest served stop"), right: value }),
        h(Caption, null,
            served
                ? t("BuildingWalkServed", "over the pedestrian network, to a stop your lines actually call at")
                : t("BuildingWalkUnserved", "further than the {0} min this city counts as served")
                    .replace("{0}", String(horizonMinutes || 0))));
}

// The reading in the window of a line the player clicked, drawn from what
// LineInsightSection wrote. A reading, never a verdict: no advice, no buttons.
//
// Built from the game's own InfoSection and InfoRow, which is what gives it the heading
// every other section in that window has. Without one it rendered as loose rows under
// the colour picker and read as part of it.
function LineInsightRow({ measured, riders, minutesSaved, duplicatePercent, hourlyLoad }) {
    const t = useTranslate();
    const title = h(PanelRow, { uppercase: true, left: t("LineSection", "Where they go") });
    if (!measured) {
        return h(PanelSection, null,
            title,
            h(PanelRow, {
                subRow: true,
                left: t("LineRiders", "Journeys using this line"),
                right: t("LineMeasuring", "measuring…"),
            }),
            h(Caption, null, t("LineMeasuringCaption", "routing the city again without this line")));
    }

    const perJourney = riders > 0 ? minutesSaved / riders : 0;
    return h(PanelSection, null,
        title,
        h(PanelRow, {
            subRow: true,
            left: t("LineRiders", "Journeys using this line"),
            right: integer(riders || 0),
        }),
        h(PanelRow, {
            subRow: true,
            left: t("LineSaved", "Minutes it saves them, a day"),
            right: integer(minutesSaved || 0),
        }),
        h(Caption, null,
            t("LineSavedCaption", "{0} min per journey, against walking and the rest of your network")
                .replace("{0}", perJourney.toFixed(1))),
        h(PanelRow, {
            subRow: true,
            left: t("LineDuplicate", "No slower without it"),
            right: (duplicatePercent || 0) + " %",
        }),
        h(Caption, null,
            t("LineDuplicateCaption", "of those journeys would be no slower if this line did not exist")),
        h(HourlyLoad, { hourlyLoad }));
}

// Load hour by hour against the seats that were actually out in that hour. An hour the
// window never watched is a GAP, not a zero — which is why this is not a plain bar
// chart: a zero and an unwatched hour must not look alike.
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

    return h("div", { className: "wtg-load" },
        h(Caption, null,
            seen
                ? t("LineHours", "How full it runs, hour by hour")
                : t("LineHoursEmpty", "no readings yet — they start once the line runs")),
        seen ? h(LoadChart, { hours }) : null);
}

function LoadChart({ hours }) {
    const points = hours.map((share, hour) => ({ x: hour, y: share < 0 ? null : Math.round(share * 100) }));
    if (!V.Chart) {
        // No chart component: bars of our own, which at least keep the gaps visible.
        return h("div", { className: "wtg-load-bars" },
            hours.map((share, hour) => h("div", {
                key: hour,
                className: "wtg-load-bar" + (share < 0 ? " wtg-load-gap" : ""),
                style: share >= 0 ? { height: Math.max(2, Math.min(100, share * 100)) + "%" } : null,
            })));
    }

    // Chart.js, which the game bundles and uses for the traffic profile in the road
    // window. `spanGaps: false` is the whole point: a null is a gap in the line, so an
    // hour nobody watched leaves a hole instead of a dip to zero.
    const data = {
        labels: hours.map((_, hour) => hour),
        datasets: [{
            label: "load",
            data: points,
            borderColor: "#5fa8d3",
            backgroundColor: "rgba(95, 168, 211, 0.35)",
            borderWidth: 2,
            fill: true,
            spanGaps: false,
            pointRadius: 0,
        }],
    };
    const options = {
        parsing: false,
        scales: {
            x: {
                type: "linear",
                min: 0,
                max: 23,
                ticks: { stepSize: 6, color: "rgba(255,255,255,0.5)", font: { size: 9 } },
                grid: { color: "rgba(255,255,255,0.1)" },
            },
            y: {
                min: 0,
                suggestedMax: 100,
                ticks: { maxTicksLimit: 3, color: "rgba(255,255,255,0.5)", font: { size: 9 } },
                grid: { color: "rgba(255,255,255,0.1)" },
            },
        },
    };
    return h(V.Chart, { type: "line", data, options, className: "wtg-load-chart" });
}

// The game's own section and row, which is what makes these look like every other
// section in the window rather than like an add-on.
function PanelSection(props) {
    const Component = CsUi.PanelSection;
    return Component ? h(Component, { disableFocus: true }, props.children) : h("div", { className: "wtg-section" }, props.children);
}

function PanelRow(props) {
    const Component = CsUi.PanelSectionRow;
    if (Component) {
        return h(Component, { disableFocus: true, ...props });
    }

    return h(Row, { label: props.left, value: props.right, small: props.subRow, uppercase: props.uppercase });
}

// ---------------------------------------------------------------------------
// Registration.

// Puts the mod's figures and controls inside the game's infoview panel, under its
// heading. InfoviewPanelSpace is the divider the panel draws once, between the MAP
// LEGEND heading and the infomode checkboxes, so extending it is what puts our figures
// INSIDE that panel rather than in a second box under it.
function extendInfoview(registry) {
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
function registerSections(registry) {
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

// ---------------------------------------------------------------------------
// The button beside the infoview menu, top left.

// The mod's own icon, served from the mod folder by Mod.OnLoad — the same file the
// infoview carries in the Infoansicht menu, so the two ways in look like one thing.
// It is drawn as a MASK rather than as an image, which is how the buttons in this row
// take their colour from the button and not from the file.
const ICON = "coui://wheretheygo/WhereTheyGo.svg";

// Opens and closes the infoview. It is a TOGGLE and holds no state of its own:
// `selected` comes straight back from C#, so the button cannot disagree with the map.
function ToolbarButton() {
    const t = useTranslate();
    const active = useBound("infoviewActive", false);
    const button = h(CsUi.Button, {
        id: "WhereTheyGoIcon",
        variant: "floating",
        className: "wtg-toolbar-button" + (active ? " wtg-toolbar-button-on" : ""),
        onSelect: () => trigger("toggleInfoview"),
    }, h("img", { style: { maskImage: "url(" + ICON + ")" } }));

    return CsUi.Tooltip
        ? h(CsUi.Tooltip, { tooltip: t("ToolbarTooltip", "Where They Go") }, button)
        : button;
}

const register = (moduleRegistry) => {
    if (!React || !Api || !moduleRegistry || !moduleRegistry.append) {
        console.error("[WhereTheyGo] UI module could not register.");
        return;
    }

    // No window of the mod's own. Everything it has to say lives where the player is
    // already looking: the figures in the game's infoview panel, one section in the
    // selected-building window, one in the selected-line window, and every setting on
    // the Options page. The one mark it makes on the screen is a toggle in the top-left
    // row, beside the other mods' — the infoview is still in the Infoansicht menu too,
    // so losing the row to a game update costs convenience rather than the mod.
    //
    // "GameTopLeft" is the game's own name for the slot next to the infoview menu
    // button: index.js renders <ModdingHook name="GameTopLeft"/> inside the same
    // infoMenuLayout div. It is where the other mods with a button put theirs.
    resolveVanilla(moduleRegistry);
    moduleRegistry.append("GameTopLeft", ToolbarButton);
    extendInfoview(moduleRegistry);
    registerSections(moduleRegistry);
    console.info("[WhereTheyGo] UI registered.");
};

export const hasCSS = true;
export default register;
