---
paths:
  - "dotnet/Presentation/UI/**"
  - "dotnet/Presentation/PanelUISystem.cs"
---

# The panel module

`dotnet/Presentation/UI/WhereTheyGo.mjs` and its `.css` are hand-written and never compiled: the
game loads `<AssemblyName>.mjs` from the mod root, and the csproj flattens them out of `UI/` on copy.
Nothing type-checks this file, so the guard rails are explicit.

- **Run `make check-ui` (`node --check`) before every deploy.** A syntax error here is silent — the
  panel simply never appears, with nothing in the mod log. Check `UI.log` for runtime errors.
- **No build step, no JSX, no imports.** Use `window.React` (`h(...)`) and `window["cs2/api"]`
  (`bindValue`, `useValue`, `trigger`). Registration is `moduleRegistry.append(...)` /
  `.extend(...)`, with `export const hasCSS = true` and a default export.
- **cohtml is not a full browser.** No `<select>`, no `<input type=range>`, no checkboxes, no CSS
  `gap`. Build controls out of buttons and divs, as `Choice`, `Stepper` and `Toggle` already do.
  `1rem` is roughly one design pixel.
- **The map's own state lives in C#, not in the panel.** The hour, the purposes and the band
  threshold are read back over the `mapState` binding and written through triggers. Two copies
  would drift, and the renderer reads the C# one.
- **A value binding must use `AddUpdateBinding`.** `AddBinding` with a `GetterValueBinding` never
  re-polls, so the panel silently freezes on its first value.
- **The binding payloads are delimited strings** (`|` between fields, `\n` between rows). Anything
  interpolated into them — a line name the player typed, most obviously — must be sanitised of both
  delimiters on the C# side before it is appended.
- **Anything the C# side already knows, the C# side sends.** The mode and objective names, the
  slider bounds and the mode colours all arrive over bindings. They used to be copied into the
  `.mjs` and kept in step by comment, and the mode list had drifted to `Bus, Tram, Metro` while the
  enum reads `Bus, Metro, Tram` — so picking Tram selected Metro. If you are about to write a
  table here that mirrors one in C#, add a binding instead.
- **Key list rows by a stable identity, never by position.** Nothing in the panel is a list today,
  but the rule is why: a positional index once pointed at whichever line had drifted into that slot
  after a re-sort.
- **Do not trust a theme token to resolve.** `var(--menuControl)` produced a dark slate where the
  vanilla buttons are `rgb(75, 195, 241)`, which is how the toolbar button ended up looking
  transparent. When matching vanilla, measure the real pixels from a screenshot and give every
  token a concrete fallback.
- **Never hide vanilla UI with a broad selector.** A rule matching `[class*="infoview-menu"]` took
  the game's own Infoansicht button with it. Target only what belongs to this mod, and exclude this
  mod's own elements when matching by icon or asset path.

- **Payload rows grow at the END.** `coverage` is
  `walkablePercent|walkMinutes|gini|carriedPercent`, `mapState` is
  `hour|purposes|thresholdPercent|playing`, `hoveredBand` is
  `journeys|withoutPercent|peakHour|carriedPercent`, `dataCoverage` is
  `coveredHours|readings|windowHours|observedJourneys|observedHours`; the `.mjs` indexes by
  position, so a new field is appended and the old indices never move. The two selected-object
  sections are the exception: they write named JSON properties, not a delimited row.
