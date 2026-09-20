// In-game panel for Where They Go.
//
// Hand-written ES module rather than a bundled React app: the game exposes React, its
// binding API and, the part that matters most here, its own UI components on
// `window`, so no build toolchain is involved and the file deploys next to the DLL.
//
// Everything visible is built from the GAME'S components rather than from divs of our
// own. `window["cs2/ui"]` is the supported surface (PanelSection, PanelSectionRow,
// Tooltip, PanelFoldout); the module registry has the rest: the real slider with its
// drag, gamepad and sound, the real checkbox, the infoview panel's own section and
// gradient bar, the stacked bar chart and the tooltip that follows the cursor. A
// hand-rolled copy of any of these is a copy that looks almost right and behaves
// almost right.
//
// The one deliberate exception is the load chart in a line's window. The game's
// Chart.js wrapper is there, but the build sets Chart.defaults.events = [] and turns
// the tooltip plugin off globally, so no chart drawn through it can be pointed at.
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
    mouseTooltip: "game-ui/common/tooltip/floating-mouse-tooltip/floating-mouse-tooltip.tsx",
    colorLegend: "game-ui/common/charts/legends/color-legend.tsx",
    // One entry in the Infoansicht menu. Extended to leave ours out of it.
    infoviewButton: "game-ui/game/components/infoviews/infoviews-button/infoview-button.tsx",
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

// A part of a whole, with the whole possibly zero. Not arithmetic the panel decides
// anything by, since every share it shows is a share of something on the same screen, and a
// division that would say Infinity says nothing instead.
function shareOf(part, whole) {
    return whole > 0 ? part / whole : 0;
}

// Seconds as whole minutes, as a plain string. These go into "{0} min" templates that
// are filled by String.replace, which cannot take the localised number element - the
// same trade the building's walk row already makes, and at these sizes there is no
// thousands separator to lose.
function minutes(seconds) {
    return String(Math.round(seconds / 60));
}

// Metres as kilometres to one decimal, as a plain string: the templates these go into
// are filled by String.replace, which cannot take the localised number element.
function kilometres(metres) {
    return (metres / 1000).toFixed(1) + " km";
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
// not something the map paints. The map paints how much of a band transit carries.
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
function HourStrip({ hour, hourly, hourlyCarried, playing, ramp }) {
    const t = useTranslate();
    const hourText = useHourText();
    const [pointed, setPointed] = React.useState(-1);
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
        h("div", { className: "wtg-hours-bars", onMouseLeave: () => setPointed(-1) },
            hourly.map((value, index) => h("div", {
                key: index,
                className: "wtg-hour"
                    + (index === hour ? " wtg-hour-on" : "")
                    + (hour < 0 ? " wtg-hour-all" : ""),
                onClick: () => select(index),
                onMouseEnter: () => setPointed(index),
            },
                h("div", {
                    className: "wtg-hour-fill",
                    // At least a sliver, so an hour with almost nobody travelling still
                    // reads as an hour rather than as a hole in the chart.
                    style: { height: (peak > 0 ? Math.max(2, (value / peak) * 100) : 0) + "%" },
                },
                    // The carried part sits INSIDE the column rather than beside it, in
                    // the cool end of the map's own ramp: the strip then answers "when
                    // does the city travel" and "when does the network fail it" in one
                    // picture, and being a share of the column it can never exceed it.
                    h("div", {
                        className: "wtg-hour-carried",
                        style: {
                            height: (shareOf(hourlyCarried[index] || 0, value) * 100) + "%",
                            backgroundColor: ramp && ramp.length > 2 ? ramp[2] : "#2a4a8c",
                        },
                    }))))),
        h("div", { className: "wtg-hours-axis" },
            [0, 6, 12, 18].map((mark) => h("div", { key: mark, className: "wtg-hours-mark" }, hourText(mark)))),
        h(PointedHour, { hour: pointed, hourly, hourlyCarried }));
}

// What one column of the strip is worth. The strip is the panel's only picture of the
// whole day, and until now the only way to read a number off it was to click it and
// change the map.
function PointedHour({ hour, hourly, hourlyCarried }) {
    const t = useTranslate();
    const hourText = useHourText();
    if (hour < 0 || !V.MouseTooltip) {
        return null;
    }

    // Departures, not journeys: every journey is made twice and appears in two hours,
    // which is why the strip's columns add up to twice the day in "Journeys by purpose".
    const value = hourly[hour] || 0;
    const tooltip = h("div", { className: "wtg-tip" },
        h("div", { className: "wtg-tip-head" }, hourText(hour)),
        h("div", { className: "wtg-tip-line" },
            t("HourDepartures", "{0} departures").replace("{0}", String(Math.round(value)))),
        h("div", { className: "wtg-tip-line" },
            t("HourCarried", "{0} % of them carried")
                .replace("{0}", String(Math.round(shareOf(hourlyCarried[hour] || 0, value) * 100)))));
    return h(V.MouseTooltip, {
        tooltip,
        screenSpacePosition: true,
        alwaysVisible: true,
        className: "wtg-tip-box",
    });
}

// Which purposes the map is drawing, with what each is worth. The switches carry their
// own weight beside their name: "Leisure 12 000" answers a question the tick box on its
// own cannot.
function Purposes({ mask, weights, day }) {
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
            h("div", { className: "wtg-purpose-value" }, integer(weights[index] || 0)),
            // The absolute number on its own has no scale: 35 606 work journeys is a
            // large city or a rounding error depending on what else the city does.
            h("div", { className: "wtg-purpose-share" }, percent(shareOf(weights[index] || 0, day))))));

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

// The threshold, named after what it DOES. It used to read "Bands · 51 of 400", which
// paired a count with a slider measured in percent and made "400" look like the city's
// corridors when it is Assumptions.MaxBands, the cap the bundling stops at. The row now
// says what the slider sets; the count of what survives it moved into the caption,
// where it cannot be mistaken for the slider's own unit.
function Bands({ hour, thresholdPercent, thresholdMax, shown, hiddenShare, breaks, widths, ramp }) {
    const t = useTranslate();
    return h("div", { className: "wtg-bands" },
        h(Row, {
            uppercase: true,
            label: t("BandsHide", "Hide weak corridors"),
            value: t("BandsUnder", "under {0} %").replace("{0}", String(thresholdPercent)),
        }),
        h(Slider, {
            value: thresholdPercent,
            start: 0,
            // The range is C#'s (Assumptions.BandThresholdMaxPercent); the fallback only
            // covers a map state written before the field existed.
            end: thresholdMax || 25,
            onChange: (value) => trigger("setBandThreshold", value),
        }),
        h(Caption, null,
            t("BandsVisible", "{0} corridors drawn").replace("{0}", String(shown)),
            hiddenShare > 0.005
                ? " · " + t("HiddenShare", "{0} % of the journeys")
                    .replace("{0}", String(Math.round(hiddenShare * 100)))
                : ""),
        h(WidthLegend, { hour, breaks, widths, ramp }));
}

// What a width means, in journeys a day. The point of drawing widths in classes at all:
// a class can be read off a legend, a continuous ramp can only be compared.
function WidthLegend({ hour, breaks, widths, ramp }) {
    const t = useTranslate();
    const hourText = useHourText();
    const mid = ramp && ramp.length > 1 ? ramp[1] : "#c07840";
    const rows = [];
    for (let i = 0; i < widths.length; i++) {
        const low = i === 0 ? 0 : breaks[i - 1];
        const high = i < breaks.length ? breaks[i] : null;
        // Two equal boundaries are an empty class (BandView.ClassBreaks): nothing on the
        // map is drawn at that width, so the legend has no row for it.
        if (high !== null && low === high) {
            continue;
        }

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

    // The boundaries are cut against the hour the map is showing (BandView.Of), so with
    // an hour picked they count that hour's departures, not the day's journeys. Saying
    // "a day" over them was simply the wrong unit.
    return h("div", null,
        h(Caption, null,
            hour < 0
                ? t("ClassCaption", "width: journeys a day")
                : t("ClassCaptionAtHour", "width: departures at {0}").replace("{0}", hourText(hour))),
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
        t("CarriedCaption", "of all journeys; a journey counts when transit makes it faster than walking"));

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

    // With an hour picked, `journeys` is that hour's departures rather than the day's
    // total, and saying "a day" over an hour's number is simply a wrong label.
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
            t("BandPeak", "busiest at {0}").replace("{0}", hourText(band.peakHour))),
        // Both of these have been in the payload since it became JSON and were never
        // drawn. The length says whether a corridor is a bus ride or a metro one; the
        // purposes say whether it is a rush hour or an evening out.
        h("div", { className: "wtg-tip-line" },
            t("BandLength", "{0} apart").replace("{0}", kilometres(band.lengthMetres || 0))),
        h("div", { className: "wtg-tip-purposes" },
            PURPOSES.map((purpose, index) => {
                const weight = (band.purposeWeights || [])[index] || 0;
                // A purpose that does not travel here is left out rather than shown as
                // a zero: four rows of which three are zero read as a table, not as an
                // answer.
                return weight < 0.5 ? null : h("div", { className: "wtg-tip-purpose", key: purpose.key },
                    h("div", { className: "wtg-purpose-swatch", style: { backgroundColor: purpose.colour } }),
                    h("div", null, t(purpose.key, purpose.english) + " " + String(Math.round(weight))));
            })));

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
            hourlyCarried: state.hourlyCarried || [],
            playing: state.playing,
            ramp: state.bandRamp,
        })),
        h(Section, null, h(Purposes, {
            mask: state.purposes,
            weights: state.purposeWeights || [],
            day: state.journeysPerDay || 0,
        })),
        h(Section, null, h(Bands, {
            hour: state.hour,
            thresholdPercent: state.thresholdPercent,
            thresholdMax: state.thresholdMaxPercent,
            shown: state.bandsShown,
            hiddenShare: state.hiddenShare,
            breaks: state.classBreaks || [],
            widths: state.classWidths || [],
            ramp: state.bandRamp,
        })),
        h(Detail, { figures, state }),
        h(HoveredBand, { band, hour: state.hour }));
}

// What the two headline figures rest on, folded away. Two figures are the panel; these
// are the shape behind them, and a player who never opens this loses nothing he was
// reading anyway. Collapsed by default on purpose: the map is the thing to read.
function Detail({ figures, state }) {
    const t = useTranslate();
    const [open, setOpen] = React.useState(false);
    const body = h("div", { className: "wtg-detail-body" },
        h(WalkClasses, {
            shares: figures.walkClasses || [],
            horizon: figures.coverageWalkMinutes || 0,
            ramp: state.walkRamp,
        }),
        h(Row, { uppercase: true, label: t("Network", "Your network") }),
        h(Caption, null,
            t("NetworkLines", "{0} lines").replace("{0}", String(figures.lineCount || 0))
            + " · "
            + t("NetworkStops", "{0} served stops").replace("{0}", String(figures.servedStops || 0))
            + " · "
            + t("NetworkJourneys", "{0} journeys a day")
                .replace("{0}", String(Math.round(state.journeysPerDay || 0)))));

    // The game's own foldout, read off the bundle: ({header, initialExpanded,
    // expandFromContent, focusKey, tooltip, disableFocus, className, onToggleExpanded,
    // children}). It already wraps itself in the vanilla info-section, so it is NOT put
    // inside one of ours. `disableFocus` for the same reason the checkboxes need their
    // focus key disabled: two focusable things in one infoview panel fight over it.
    // Its children must always be there, because it counts them to decide whether to draw a
    // body at all, and it owns the open state itself.
    if (CsUi.PanelFoldout) {
        return h(CsUi.PanelFoldout, {
            header: t("Detail", "In detail"),
            initialExpanded: false,
            disableFocus: true,
            className: "wtg-detail",
        }, body);
    }

    // Without it the block still opens and closes, it just does not animate.
    return h(Section, null,
        h("div", { className: "wtg-detail-head", onClick: () => setOpen(!open) },
            h(Row, { uppercase: true, label: t("Detail", "In detail"), value: open ? "−" : "+" })),
        open ? body : null);
}

// The walk to the nearest served stop, in four classes. "71 % within walking distance"
// says nothing about the other 29 %: five minutes too far and no stop at all are the
// same number there and different problems here.
function WalkClasses({ shares, horizon, ramp }) {
    const t = useTranslate();
    const near = Math.round(horizon * 0.5);
    const far = horizon * 2;
    const labels = [
        t("WalkClassNear", "under {0} min").replace("{0}", String(near)),
        t("WalkClassMid", "{0}–{1} min").replace("{0}", String(near)).replace("{1}", String(horizon)),
        t("WalkClassMid", "{0}–{1} min").replace("{0}", String(horizon)).replace("{1}", String(far)),
        t("WalkClassNone", "no stop"),
    ];
    // The map's own ramp, near to far, so the bar and the buildings say the same thing.
    const colours = ramp && ramp.length > 2
        ? [ramp[0], ramp[1], ramp[2], "#3a1018"]
        : ["#fff8bf", "#fd8c3d", "#800026", "#3a1018"];
    const values = labels.map((_, i) => shares[i] || 0);
    let total = 0;
    for (const value of values) {
        total += value;
    }

    const legend = h("div", { className: "wtg-purposes" },
        labels.map((label, index) => h("div", { className: "wtg-purpose", key: label },
            h("div", { className: "wtg-purpose-swatch", style: { backgroundColor: colours[index] } }),
            h("div", { className: "wtg-purpose-label" }, label),
            h("div", { className: "wtg-purpose-value" }, percent(values[index])))));

    if (V.InfoBarChart) {
        return h(V.InfoBarChart, {
            title: t("WalkClasses", "Walk to a served stop"),
            colors: colours,
            labels,
            data: { values, total: total > 0 ? total : 1 },
            customLegend: legend,
            className: "wtg-purpose-chart",
        });
    }

    return h("div", null, h(Row, { label: t("WalkClasses", "Walk to a served stop"), uppercase: true }), legend);
}

// ---------------------------------------------------------------------------
// The selected-object windows.

// The walk-to-transit row in the game's own selected-building window, drawn from what
// BuildingAccessSection wrote. The props are that section's JSON.
function BuildingAccessRow({ walkSeconds, served, reached, horizonMinutes }) {
    const t = useTranslate();
    const minutes = Math.round((walkSeconds || 0) / 60);
    // Three cases, and the third is why this is not one line: within the horizon, beyond
    // it but measured, and beyond the search itself. "over 10 min" for the last two
    // together is what made a 12-minute walk and no service at all look the same.
    // `reached` rather than a non-zero walk decides the third: a building at a stop
    // walks zero seconds and is not "no stop in reach".
    const value = reached
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
function LineInsightRow(props) {
    const t = useTranslate();
    const { measured, riders, minutesSaved, duplicatePercent, hourlyLoad } = props;
    const title = h(PanelRow, { uppercase: true, left: t("LineSection", "Where they go") });
    const routed = measured
        ? [
            h(PanelRow, {
                key: "riders",
                subRow: true,
                left: t("LineRiders", "Journeys using this line"),
                right: integer(riders || 0),
            }),
            h(PanelRow, {
                key: "saved",
                subRow: true,
                left: t("LineSaved", "Rider-minutes saved, a day"),
                right: integer(minutesSaved || 0),
            }),
            h(Caption, { key: "savedCaption" },
                t("LineSavedCaption", "{0} min per journey, against walking and the rest of your network")
                    .replace("{0}", (riders > 0 ? minutesSaved / riders : 0).toFixed(1))),
            // Read the way round a player asks it. The same measurement said backwards
            // ("no slower without it") answered "why is my line empty" and nothing else;
            // the diagnosis keeps that, in the caption where it belongs.
            h(PanelRow, {
                key: "faster",
                subRow: true,
                left: t("LineFaster", "Faster with it"),
                right: percent(1 - (duplicatePercent || 0) / 100),
            }),
            h(Caption, { key: "fasterCaption" },
                t("LineFasterCaption", "the other {0} % take just as long without it: there the line runs beside something that already carries them")
                    .replace("{0}", String(duplicatePercent || 0))),
        ]
        : [
            h(PanelRow, {
                key: "measuring",
                subRow: true,
                left: t("LineRiders", "Journeys using this line"),
                right: t("LineMeasuring", "measuring…"),
            }),
            h(Caption, { key: "measuringCaption" },
                t("LineMeasuringCaption", "routing the city again without this line")),
        ];

    return h(PanelSection, null,
        title,
        routed,
        // These come off the line itself rather than off the routing pass, so they are
        // there while the pass is still out.
        h(LineFacts, props),
        h(HourlyLoad, { hourlyLoad, axisTop: props.loadAxisTop }));
}

// Where the line stands, what it makes people wait, how full it got and how long its
// loop takes against free flow. Every one of these was measured already and only ever
// reached the log.
function LineFacts({ read, rank, lineCount, cityShare, waitSeconds, peakAboard, capacity, loopSeconds, idealLoopSeconds, loopIsFloor, fromWindow }) {
    const t = useTranslate();
    if (!read) {
        return null;
    }

    return h(React.Fragment, null,
        rank > 0 ? h(PanelRow, {
            subRow: true,
            left: t("LineStanding", "Among your lines"),
            right: t("LineStandingValue", "{0} of {1}")
                .replace("{0}", String(rank))
                .replace("{1}", String(lineCount)),
        }) : null,
        rank > 0 ? h(Caption, null,
            t("LineStandingCaption", "by journeys a day; this line carries {0} % of the city's travel")
                .replace("{0}", String(Math.round((cityShare || 0) * 100)))) : null,
        h(PanelRow, {
            subRow: true,
            left: t("LineWait", "Average wait"),
            right: t("WalkMinutes", "{0} min").replace("{0}", minutes(waitSeconds || 0)),
        }),
        h(Caption, null,
            t("LineWaitCaption", "what the routing charges every rider, from the interval this line actually keeps")),
        capacity > 0 ? h(PanelRow, {
            subRow: true,
            left: t("LinePeak", "Peak load"),
            right: t("LinePeakValue", "{0} of {1} seats")
                .replace("{0}", String(peakAboard || 0))
                .replace("{1}", String(capacity)),
        }) : null,
        capacity > 0 ? h(Caption, null,
            fromWindow
                ? t("LinePeakWindow", "the busiest single reading in the window")
                : t("LinePeakInstant", "from this reading alone; the window has not filled yet")) : null,
        loopSeconds > 0 ? h(PanelRow, {
            subRow: true,
            left: t("LineLoop", "Round trip"),
            right: t("WalkMinutes", "{0} min").replace("{0}", minutes(loopSeconds)),
        }) : null,
        loopSeconds > 0 ? h(Caption, null,
            // A loop the game's own timing could not give is a lower bound, and it must
            // not wear the "as driven" label. Same rule as the unwatched hour in the
            // load chart: say what the number is, rather than dressing it as a
            // measurement it is not.
            (loopIsFloor
                ? t("LineLoopFloorCaption", "at least this long; {0} in free flow. The game's own timing for this line is unusable, so this is a floor rather than a measurement.")
                : t("LineLoopCaption", "as driven; {0} in free flow"))
                .replace("{0}", t("WalkMinutes", "{0} min").replace("{0}", minutes(idealLoopSeconds || 0)))) : null);
}

// Load hour by hour against the seats that were actually out in that hour. An hour the
// window never watched is a GAP rather than a zero, which is why this is not a plain
// bar chart: a zero and an unwatched hour must not look alike.
function HourlyLoad({ hourlyLoad, axisTop }) {
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
                : t("LineHoursEmpty", "no readings yet; they start once the line runs")),
        seen ? h(LoadChart, { hours, axisTop }) : null,
        seen ? h(Caption, null, t("LineHoursAxis", "the top of the scale is this line's own busiest hour, not a full vehicle")) : null);
}

// Bars of our own rather than the game's Chart.js wrapper. The game sets
// Chart.defaults.events = [] and turns the tooltip plugin off for every chart in the
// build, so a ResponsiveChart here can never be pointed at, and
// pointing at it is the whole ask. Bars also make the two other faults cheap to fix:
// the axis follows the line instead of sitting at a fixed hundred percent, and an hour
// nobody watched stays a visible gap instead of a dip to zero.
function LoadChart({ hours, axisTop }) {
    const t = useTranslate();
    const hourText = useHourText();
    const [pointed, setPointed] = React.useState(-1);
    // The axis comes from C# (LoadAxis.TopOf) so the panel and the log agree on it; the
    // fallback only covers a payload written before the field existed.
    const top = axisTop > 0 ? axisTop : 1;
    const bars = hours.map((share, hour) => h("div", {
        key: hour,
        className: "wtg-load-slot" + (hour === pointed ? " wtg-load-slot-on" : ""),
        onMouseEnter: () => setPointed(hour),
    },
        share < 0
            ? h("div", { className: "wtg-load-gap" })
            : h("div", {
                className: "wtg-load-bar",
                // At least a sliver: an hour with almost nobody aboard is still an hour
                // that was watched, and must not read as a gap.
                style: { height: Math.max(1.5, Math.min(100, (share / top) * 100)) + "%" },
            })));

    const tip = pointed < 0 || !V.MouseTooltip
        ? null
        : h(V.MouseTooltip, {
            tooltip: h("div", { className: "wtg-tip" },
                h("div", { className: "wtg-tip-head" },
                    hours[pointed] < 0
                        ? t("LineHourGap", "{0} · not watched").replace("{0}", hourText(pointed))
                        : t("LineHourValue", "{0} · {1} %")
                            .replace("{0}", hourText(pointed))
                            .replace("{1}", String(Math.round(hours[pointed] * 100))))),
            screenSpacePosition: true,
            alwaysVisible: true,
            className: "wtg-tip-box",
        });

    return h("div", { className: "wtg-load-chart", onMouseLeave: () => setPointed(-1) },
        h("div", { className: "wtg-load-plot" },
            h("div", { className: "wtg-load-marks" },
                h("div", { className: "wtg-load-mark" }, String(Math.round(top * 100)) + " %"),
                h("div", { className: "wtg-load-mark" }, String(Math.round(top * 50)) + " %"),
                h("div", { className: "wtg-load-mark" }, "0")),
            h("div", { className: "wtg-load-bars" }, bars)),
        h("div", { className: "wtg-load-axis" },
            [0, 6, 12, 18].map((mark) => h("div", { key: mark, className: "wtg-load-tick" }, hourText(mark)))),
        tip);
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
// writes. The map reads {"Game.UI.InGame.DescriptionSection": …}, not the section's
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

// The mod's own icon, served from the mod folder by Mod.OnLoad. It is the same file the
// infoview carries in the Infoansicht menu, so the two ways in look like one thing.
// It is drawn as a MASK rather than as an image, which is how the buttons in this row
// take their colour from the button and not from the file.
const ICON = "coui://wheretheygo/WhereTheyGo.svg";

// The infoview prefab's name, which is the id the menu knows it by (Infoview.cs:
// PrefabBase.Create<InfoviewPrefab>("WhereTheyGo")).
const INFOVIEW_ID = "WhereTheyGo";

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
        ? h(CsUi.Tooltip, { tooltip: t("ToolbarTooltip", "Where They Go: the journeys your city makes, and who already rides") }, button)
        : button;
}

// With a button of its own in the top row, the entry in the Infoansicht menu is a
// second door to the same room. Hidden HERE rather than on the prefab: a prefab is
// only listed while it is valid, and an invalid one cannot be activated at all, so
// hiding it that way would take the infoview with it.
//
// Only ever called once the button is known to have registered, so the mod cannot end
// up with no door at all.
function hideFromInfoviewMenu(registry) {
    try {
        registry.extend(VANILLA.infoviewButton, "InfoviewButton", (Button) => (props) =>
            (props && props.infoview && props.infoview.id === INFOVIEW_ID)
                ? h(React.Fragment, null)
                : h(Button, props));
    } catch (error) {
        console.warn("[WhereTheyGo] the infoview menu's button has moved, so the menu keeps its entry: " + error);
    }
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
    // row, beside the other mods'. That button REPLACES the entry in the Infoansicht
    // menu, and only once it is known to have registered.
    //
    // "GameTopLeft" is the game's own name for the slot next to the infoview menu
    // button: index.js renders <ModdingHook name="GameTopLeft"/> inside the same
    // infoMenuLayout div. It is where the other mods with a button put theirs.
    resolveVanilla(moduleRegistry);
    try {
        moduleRegistry.append("GameTopLeft", ToolbarButton);
        hideFromInfoviewMenu(moduleRegistry);
    } catch (error) {
        console.warn("[WhereTheyGo] no toolbar button, so the infoview menu keeps its entry: " + error);
    }

    extendInfoview(moduleRegistry);
    registerSections(moduleRegistry);
    console.info("[WhereTheyGo] UI registered.");
};

export const hasCSS = true;
export default register;
