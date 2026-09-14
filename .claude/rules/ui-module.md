---
paths:
  - "dotnet/Presentation/UI/**"
  - "dotnet/Presentation/PanelUISystem.cs"
  - "dotnet/Overlay/WhereTheyGoSystem.Panel.cs"
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

## Use the game's components, not divs

The game ships its whole UI as readable JavaScript in
`Cities2_Data/Content/Game/UI/index.js`, and exposes it to mods two ways. **Look there before
building a control.** Nearly every widget this panel needs already exists, and a hand-rolled copy
looks almost right and behaves almost right — no drag, no gamepad, no sound, no focus.

- `window["cs2/ui"]` is the supported surface: `Button`, `Dropdown`, `DropdownItem`,
  `DropdownToggle`, `FloatingButton`, `FormattedParagraphs`, `FormattedText`, `Icon`,
  `MarkdownRenderer`, `MenuButton`, `Panel`, `PanelFoldout`, `PanelSection`, `PanelSectionRow`,
  `Portal`, `Scrollable`, `Tooltip`. `PanelSection`/`PanelSectionRow` ARE the selected-object
  window's `InfoSection`/`InfoRow` — the same minified value — which is what gives a section its
  heading and its rows the vanilla look.
- `moduleRegistry.registry.get(path)` reaches everything else. In use here: `Slider`
  (`common/input/slider/slider.tsx`), `Checkbox` (`common/input/toggle/checkbox/checkbox.tsx`),
  `InfoviewPanelSection`, `InfoviewPanelLabel`, `ValueBarSection` (title + gradient bar with a
  pointer — how vanilla shows a city-wide share), `InfoBarChart`, `ResponsiveChart` (a **Chart.js**
  wrapper; `window["chart.js"]` is exposed too) and `FloatingMouseTooltip` (follows the cursor with
  `screenSpacePosition: true`).
- `window["cs2/l10n"]` has `LocalizedNumber` and the `Unit` enum. **Numbers reach the panel as
  numbers and are formatted there**, so a thousands separator and a unit system follow the player's
  own settings. Never format a number into a string on the C# side to show it.
- Every lookup goes through `readVanilla`, which catches and warns: a renamed export must cost a
  plainer row, never a blank panel. `Q.get` THROWS on an unknown path.
- Note that CSS class maps are exported too (`*.module.scss` exports `classes`), so vanilla class
  names can be reused where a component cannot.

`cohtml` itself still supports no `<select>`, no `<input type=range>`, no HTML checkbox and no CSS
`gap` — but the game's own `Dropdown`, `Slider` and `Checkbox` solve all three, so that is a reason
to use them rather than a reason to build divs. `1rem` is roughly one design pixel.

## The binding contract

- **Payloads are JSON, read by name.** `PanelUISystem` writes through `RawValueBinding` and an
  `IJsonWriter`, exactly as the two selected-object sections do. The delimited `|` rows this used
  to use are gone, along with the positional indexing in the `.mjs` that made inserting a field in
  the middle silently move every later one.
- **A value binding must use `AddUpdateBinding`.** `AddBinding` with a `GetterValueBinding` never
  re-polls, so the panel silently freezes on its first value.
- **The map's own state lives in C#, not in the panel.** The hour, the purposes and the band
  threshold are read back over the `mapState` binding and written through triggers. Two copies
  would drift, and the renderer reads the C# one. The same binding carries what the panel needs to
  DESCRIBE the map — the hourly profile, the band counts, the width-class breaks — all taken from
  the one `BandView` the renderer draws from, so panel and map cannot tell different stories.
- **Anything the C# side already knows, the C# side sends.** The colour ramps, the class widths and
  the slider bounds arrive over bindings. They used to be copied into the `.mjs` and kept in step by
  comment, and the mode list had drifted to `Bus, Tram, Metro` while the enum read
  `Bus, Metro, Tram` — so picking Tram selected Metro. If you are about to write a table here that
  mirrors one in C#, add a binding instead.
- **Key list rows by a stable identity, never by position.** A positional index once pointed at
  whichever line had drifted into that slot after a re-sort.
- **Sanitise anything a player typed** before it goes into a payload — a line name, most obviously.

## Styling

- **Do not trust a theme token to resolve.** `var(--menuControl)` produced a dark slate where the
  vanilla buttons are `rgb(75, 195, 241)`, which is how the toolbar button ended up looking
  transparent. cohtml also does not support `var()`'s fallback argument: `var(--textColor, #dceaf8)`
  is parsed as a variable literally named `textColor,#dceaf8` and takes the whole declaration with
  it. Write literals, measured off a screenshot of the real panel.
- **Never hide vanilla UI with a broad selector.** A rule matching `[class*="infoview-menu"]` took
  the game's own Infoansicht button with it. Target only what belongs to this mod.
