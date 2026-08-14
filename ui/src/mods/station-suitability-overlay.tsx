import React, { useCallback, useRef, useState } from "react";
import { bindTrigger, bindTriggerWithArgs, bindValue, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import "./station-suitability-overlay.scss";

const GROUP = "StationSuitabilityOverlay";

const enabled$ = bindValue<boolean>(GROUP, "enabled", false);
const mode$ = bindValue<number>(GROUP, "mode", 0);
const w1$ = bindValue<number>(GROUP, "w1", 1.0);
const w2$ = bindValue<number>(GROUP, "w2", 0.8);
const w3$ = bindValue<number>(GROUP, "w3", 1.2);
const w4$ = bindValue<number>(GROUP, "w4", 0.6);

const setEnabled = bindTriggerWithArgs<[boolean]>(GROUP, "setEnabled");
const setMode = bindTriggerWithArgs<[number]>(GROUP, "setMode");
const setW1 = bindTriggerWithArgs<[number]>(GROUP, "setW1");
const setW2 = bindTriggerWithArgs<[number]>(GROUP, "setW2");
const setW3 = bindTriggerWithArgs<[number]>(GROUP, "setW3");
const setW4 = bindTriggerWithArgs<[number]>(GROUP, "setW4");
const recalc = bindTrigger(GROUP, "recalculate");

// The game's UI engine (cohtml/Gameface) does not render native <input type="range">,
// <select> or checkbox controls, so every control here is built from plain divs.

type WeightSliderProps = {
    label: string;
    value: number;
    onChange: (value: number) => void;
};

const SLIDER_MIN = 0;
const SLIDER_MAX = 2;

const WeightSlider = ({ label, value, onChange }: WeightSliderProps) => {
    const trackRef = useRef<HTMLDivElement>(null);

    const setFromPointer = useCallback(
        (clientX: number) => {
            const track = trackRef.current;
            if (!track) {
                return;
            }
            const rect = track.getBoundingClientRect();
            if (rect.width <= 0) {
                return;
            }
            const t = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
            const raw = SLIDER_MIN + t * (SLIDER_MAX - SLIDER_MIN);
            onChange(Math.round(raw * 100) / 100);
        },
        [onChange]
    );

    const onMouseDown = useCallback(
        (event: React.MouseEvent) => {
            event.preventDefault();
            setFromPointer(event.clientX);

            const onMove = (moveEvent: MouseEvent) => setFromPointer(moveEvent.clientX);
            const onUp = () => {
                window.removeEventListener("mousemove", onMove);
                window.removeEventListener("mouseup", onUp);
            };
            window.addEventListener("mousemove", onMove);
            window.addEventListener("mouseup", onUp);
        },
        [setFromPointer]
    );

    const percent = ((value - SLIDER_MIN) / (SLIDER_MAX - SLIDER_MIN)) * 100;

    return (
        <div className="sso-field">
            <div className="sso-row">
                <span>{label}</span>
                <span className="sso-value">{value.toFixed(2)}</span>
            </div>
            <div className="sso-slider" ref={trackRef} onMouseDown={onMouseDown}>
                <div className="sso-slider-track">
                    <div className="sso-slider-fill" style={{ width: `${percent}%` }} />
                </div>
                <div className="sso-slider-thumb" style={{ left: `${percent}%` }} />
            </div>
        </div>
    );
};

export const StationSuitabilityOverlay = () => {
    const { translate } = useLocalization();
    const [open, setOpen] = useState(false);

    const enabled = useValue(enabled$);
    const mode = useValue(mode$);
    const w1 = useValue(w1$);
    const w2 = useValue(w2$);
    const w3 = useValue(w3$);
    const w4 = useValue(w4$);

    const t = (key: string, fallback: string) => translate(`StationSuitabilityOverlay.UI.${key}`, fallback) ?? fallback;

    const title = t("PanelTitle", "Station Suitability");

    return (
        <div className="sso-root">
            <button
                className={"sso-toggle" + (enabled ? " sso-toggle--active" : "")}
                onClick={() => setOpen((value) => !value)}
            >
                {title}
            </button>
            {open && (
                <div className="sso-panel">
                    <div className="sso-header">
                        <div className="sso-title">{title}</div>
                        <button className="sso-close" onClick={() => setOpen(false)}>
                            ✕
                        </button>
                    </div>
                    <div className="sso-check-row" onClick={() => setEnabled(!enabled)}>
                        <div className={"sso-checkbox" + (enabled ? " sso-checkbox--checked" : "")}>
                            {enabled && <div className="sso-checkmark" />}
                        </div>
                        <span>{t("EnableOverlay", "Enable overlay")}</span>
                    </div>
                    <div className="sso-field">
                        <div className="sso-row">
                            <span>{t("ModePreset", "Mode preset")}</span>
                        </div>
                        <div className="sso-segmented">
                            <button
                                className={"sso-segment" + (mode === 0 ? " sso-segment--active" : "")}
                                onClick={() => setMode(0)}
                            >
                                {t("Mode.Bus", "Bus")}
                            </button>
                            <button
                                className={"sso-segment" + (mode === 1 ? " sso-segment--active" : "")}
                                onClick={() => setMode(1)}
                            >
                                {t("Mode.Metro", "Metro")}
                            </button>
                        </div>
                    </div>
                    <WeightSlider label={t("WeightDemand", "Demand")} value={w1} onChange={setW1} />
                    <WeightSlider label={t("WeightJobs", "Jobs")} value={w2} onChange={setW2} />
                    <WeightSlider label={t("WeightCoverage", "Coverage penalty")} value={w3} onChange={setW3} />
                    <WeightSlider label={t("WeightAccessibility", "Accessibility")} value={w4} onChange={setW4} />
                    <button className="sso-recalc" onClick={() => recalc()}>
                        {t("Recalculate", "Recalculate")}
                    </button>
                </div>
            )}
        </div>
    );
};
