# UI architecture — how the mod reaches into the vanilla interface

Goal (author, 2026-09-06): the mod must be **trivially easy to use**, every setting in the game's
own Options page, and everything else built into the vanilla interface so closely that a player
would not guess a mod is running.

Everything below was read out of **this installed build**: the game's UI bundle
`…/Cities Skylines II/Cities2_Data/Content/Game/UI/index.js` (one minified file, 2.2 MB, dated
2025-06-23) and `index.css` (463 KB), plus the three reference mods in `example-mods/`. Minified
local names change with every patch; the **registry paths and export names are the stable surface**
mods have used since 1.0. Wrap every lookup and fall back to vanilla — a throw inside the registrar
takes the whole UI module down, silently (`.claude/rules/ui-module.md`).

## The module registry

`window["cs2/modding"]` exposes `{findModule, getModule}`; the registrar callback receives
`moduleRegistry` with `extend`, `append`, `registry`, `find`, `reset`.

- `extend(path, exportName, cb)` is `override(path, exportName, cb(current))` — the higher-order
  component pattern. `cb` receives the current export and returns the replacement.
- `extend(scssPath, {myClass: "…"})` **appends** class names to a vanilla `classes` map;
  `extend(scssPath, "classes", cb)` replaces it.
- `append(path, exportName, Comp)` injects `Comp` as an extra child — **only where the target
  actually renders `{children}`**. `TransportLineItem`, `TransportationOverviewPage` and
  `TransportationOverviewPanel` do not, so `append` into them is silently dropped.
- `append("Game" | "GameTopLeft" | "GameTopRight" | "GameBottomRight" | "Menu" | "Editor", Comp)`
  routes through `game-ui/modding/modding-hook.tsx` → `ModdingHook`, matched on the hook's `name`.
  These are the only vanilla hook targets. (`"UniversalModMenu"` belongs to another mod.)
- `getModule(path, export)` **throws** when missing. Prefer the defensive
  `moduleRegistry.registry.get(path)?.[export]` (as `CS2TransitView/UI/src/VanillaComponentResolver.tsx`
  does) at registration time.

## Registry keys we need

Transport overview:

```
game-ui/game/components/transportation-overview-panel/transportation-overview-panel.tsx  → TransportationOverviewPanel
game-ui/game/components/transportation-overview-panel/transportation-overview-page.tsx   → TransportationOverviewPage
…/transport-line-item/transport-line-item.tsx                                            → TransportLineItem
…/transport-type-item/transport-type-item.tsx                                            → TransportTypeItem
…/lines-utils.ts                                                                         → getScheduleIcon, useLineName
game-ui/game/data-binding/transport-bindings.ts → transportLines, selectLine, showLine, hideLine,
    setLineActive, setLineSchedule, setLineColor, renameLine, deleteLine, toggleHighlight, …
```

The C# binding group is `"transportationOverview"` (the raw binding behind `transportLines` is
`lines`). A row's `line` is `{name, lineData:{entity, length, stops, vehicles, cargo, usage, color,
active, visible, schedule, isCargo, type}}`; `TransportLineItem` props are `{focusKey, line,
onFocusedColumnChanged, focusedColumnKey}`.

Column widths are flex ratios in `index.css`: `cellSingle` `flex:.8 0 0`, `cellDouble` `flex:2 0 0`,
`cellWide` `flex:4 0 0`. **The header row is a private const inside the page module** — there is no
registry key for it, so an aligned header needs option A2 below.

Infoview:

```
…/infoviews/active-infoview-panel/active-infoview-panel.tsx        → ActiveInfoviewPanel
…/components/infomode-item/infomode-item.tsx                       → InfomodeItem
…/components/labels/labels.tsx                                     → InfoviewPanelLabel, InfoviewPanelIconLabel
…/components/sections/infoview-panel-section.tsx                   → InfoviewPanelSection
…/components/bars/bars.tsx                                         → AvailabilityBar, ValueBar
…/components/infoview-panels-util.tsx                              → 7 named gradients
game-ui/game/components/infoviews/infoview-menu.tsx                → InfoviewMenu
game-ui/game/data-binding/infoview-bindings.ts                     → activeInfoview, infoviews, setActiveInfoview, setInfomodeActive, …
game-ui/common/panel/collapsible-panel.tsx                         → CollapsiblePanel
```

`InfomodeItem` renders its legend **from data**: `gradientLegend {lowLabel, highLabel, gradient}`
and `colorLegends [{color, label}]`. `Game.dll` has `BindInfomodeGradientLegend`, `BindColorLegend(s)`,
`GradientLegendType`, `m_LegendType` — so a vanilla-perfect legend is a C#-side prefab property, not
UI code.

Notifications:

```
game-ui/menu/data-binding/notification-bindings.ts → notifications, selectNotification, ProgressState
game-ui/menu/components/notifications-panel/notifications-panel.tsx → NotificationsPanel, NotificationItem
```

`NotificationItem` is already a `Button` calling `selectNotification(id)`, which routes back to the
C# `onClicked`. `Game.dll` carries `Game.UI.Menu.NotificationUISystem` with `AddOrUpdateNotification`,
`RemoveNotification`, `AddModNotification` and the parameter names `onClicked`, `progressState`,
`textId`. A clickable, game-style notification is therefore **entirely a C#-side construct** — do not
patch the panel.

`cs2/ui` exports (complete): `Button, ConfirmationDialog, DialogContext, DialogRenderer, DialogStack,
Dropdown, DropdownItem, DropdownToggle, FloatingButton, FormattedParagraphs, FormattedText, Icon,
MarkdownRenderer, MarkupRenderer, MenuButton, Panel, PanelFoldout, PanelSection, PanelSectionRow,
Portal, Scrollable, Tooltip`. **No Slider, no Checkbox, no InfoRow** — `InfoRow`/`InfoSection` are
`PanelSectionRow`/`PanelSection`, and importing them from `cs2/ui` is reported to throw at run time;
take them from `…/selected-info-panel/shared-components/info-row/info-row.tsx` instead.

## Theme

All variables sit on `:root` in `index.css`, so they resolve without a theme class. Confirmed to
exist: `--panelColorNormal/Dark(+hover/active)`, `--panelRadius`, `--panelRadiusInner`,
`--screenPadding`, `--textColor{,Dim,Dimmer,Dimmest,Disabled,Highlight,Locked}`, `--normalTextColor*`,
`--accentColor{Normal,Light,Lighter,Lightest,Dark,Darker}`, `--dividerColor`,
`--sectionBackgroundColor`, `--sectionHeaderColor`, `--selectedColor`, `--focusedColor`,
`--scrollbarColor/Size`, `--warningColor`, `--tooltipColor`, `--fontSizeXXS…XXXL`, `--fontFamily`,
`--gap1…8`, `--stroke1…4` (the last two scale with resolution — using them is how density matches
vanilla). `--menuControl`, which the rules file warns about, is genuinely absent. Always give a
fallback; style only our own `ta-`-prefixed elements, never override a vanilla class rule.

## Chosen approach per surface

1. **Suggestions and line health in the Transport Overview** (author's choice 7c). Start with
   *A1*: `extend(TransportLineItem)` wrapping the original in a flex row and appending one
   `cellDouble`-width cell of our own; the header stays unlabelled (tooltip only) because the vanilla
   header is private. Move to *A2* — `extend(TransportationOverviewPage)` and reimplement its ~60
   lines from the exported pieces (`TransportTypeItem`, `TransportLineItem`, both SCSS `classes`,
   `Loc.Transport.LEGEND_*`, `Scrollable`) — only if the missing header proves unacceptable. Join our
   data by `line.lineData.entity` (an `{index, version}` pair): give it its own binding keyed by
   entity index rather than reusing the panel's delimited rows, and check the identity matches
   `Lines.IdentityOf`.
2. **Infoview legend**: prefer the data-driven route — extend `SuitabilityInfomodePrefab` with the
   gradient/colour legend the game already renders (confirm the C# component name first). Fall back
   to `extend(ActiveInfoviewPanel)` in the `transit-hotspots` pattern, reusing `CollapsiblePanel`,
   `InfomodeItem`, `InfoviewPanelLabel` and one of the seven exported gradients.
3. **Hiding our row from the infoview menu**: replace the current `MutationObserver` +
   `querySelectorAll` hack with `extend(InfoviewMenu)` filtering by id, or drop the need for it by
   owning the panel for our infoview id.
4. **Notifications**: `NotificationUISystem.AddOrUpdateNotification` from C# with an `onClicked` that
   opens the overview (`showTransportationOverviewPanel`) or focuses the line. Verify the overload
   set against the assembly before writing code — only the method and parameter names are confirmed
   so far.
5. **Buildings coloured by transit access** (author's request with 10a): needs the building-colour
   path of a custom infomode; being decompiled separately.

## The C# side (decompiled 2026-09-06, `Game.dll`)

**Outside connections.** The vanilla precedent is `Game.UI.InGame.LineVisualizerSection`: its
`LineStop` record carries `isOutsideConnection`, computed as "resolve the waypoint's
`Connected.m_Connected` stop up its `Game.Common.Owner` chain, then
`HasComponent<Game.Objects.OutsideConnection>`". The tag is a size-1 component; for some connection
types it sits on the stop entity itself (`TransportStop.GetArchetypeComponents`, Airplane branch),
for others on an ancestor — so check the stop and then walk owners, which is what
`Lines.CityStopCount` does. `Game.Simulation.TransportBoardingHelpers` likewise excludes outside
connections from its usage statistics, so ignoring them matches how the game itself counts.

**Setting a line's vehicle count** (`VehicleCountSection`, `PoliciesUISystem`):

```csharp
var buffer  = EntityManager.GetBuffer<RouteModifierData>(vehicleCountPolicy, isReadOnly: true);
var slider  = EntityManager.GetComponentData<PolicySliderData>(vehicleCountPolicy);
float adj   = VehicleCountSection.CalculateVehicleCountJob.CalculateAdjustmentFromVehicleCount(
                  vehicleCount, lineData.m_DefaultVehicleInterval, stableDuration, buffer, slider);
World.GetOrCreateSystemManaged<PoliciesUISystem>().SetPolicy(lineEntity, vehicleCountPolicy, active: true, adj);
```

`stableDuration` must be the game's own (`CalculateStableDuration`: path durations plus the stop
duration at every waypoint carrying `VehicleTiming`, starting from the first such waypoint), or the
adjustment is wrong. `SetPolicy` raises a `Game.Policies.Modify` event through the end-frame barrier.

**Setting the schedule** (`ScheduleSection`): Day = night policy off, day policy on; Night = the
mirror; DayAndNight = both off. The policies are `UITransportConfigurationPrefab.m_DayRoutePolicy`
and `m_NightRoutePolicy`, both boolean (no adjustment).

**Notification icons** (`Game.Notifications.IconCommandSystem` / `IconCommandBuffer`):

```csharp
var icons = World.GetOrCreateSystemManaged<IconCommandSystem>().CreateCommandBuffer();
icons.Add(lineEntity, iconPrefabEntity, IconPriority.Problem);   // …Remove(lineEntity, iconPrefabEntity)
```

`IconPriority`: Info 10, Problem 50, Warning 100, MajorProblem 150, Error 200. Clicking a world icon
puts the icon entity into `ToolSystem.selected`, and `SelectedInfoUISystem.FilterSelection` rewrites
it to the owner — for a line's waypoint icon that is the line, so the vanilla line panel opens by
itself. A **new** icon needs a `NotificationIconPrefab` with a `Texture2D`, and how that texture
reaches the renderer is not yet traced; reusing an existing prefab entity (for instance
`TransportLineData.m_VehicleNotification`) avoids the question entirely.

**Infoview legend, confirmed.** Deriving the infomode prefab from `GradientInfomodeBasePrefab`
(fields `m_Low`, `m_Medium`, `m_High`, `m_Steps`, `m_LegendType ∈ {Gradient, Fields}`, and the label
ids `m_LowLabelId`/`m_MediumLabelId`/`m_HighLabelId`, resolved as `Infoviews.LABEL[<id>]`) makes
`InfoviewsUISystem.BindInfomode` emit `gradientLegend` with a three-stop gradient, which
`InfomodeItem` renders. No UI code at all. A `ColorInfomodeBasePrefab` gets one swatch instead.
There is no chart infomode.

**Colouring buildings: the vanilla path cannot carry our values.** `Game.Rendering.ObjectColorSystem`
writes `Game.Objects.Color {m_Index, m_Value, m_SubColor}` per object while an infoview is active. It
picks the active infomode with the lowest `InfomodeActive.m_Priority` whose `InfoviewBuildingData`
`BuildingType` (or `InfoviewBuildingStatusData` `BuildingStatusType`) matches the chunk — both are
closed enums resolved by a hard-coded `switch` over game components, and the gradient value comes
from `InfoviewUtils.GetColor(statusData, status)` on a game-supplied status. **There is no per-entity
value channel a mod can fill.** Three ways out, in order of preference:

1. Write `Game.Objects.Color` ourselves from a system ordered after `ObjectColorSystem`
   (`m_Index` = our infomode's `InfomodeActive.m_Index`, `m_Value` = 0…255 along our gradient). The
   producer path is public; what is untested is whether the write survives the next update and which
   system consumes it.
2. Colour the STOPS instead of the buildings: `TransportStopInfomodePrefab` /
   `InfoviewTransportStopData` is a much closer fit for a transit mod, and
   `ObjectColorSystem.GetTransportStopColor` is the branch that reads it.
3. Keep the terrain overlay we already paint and mark buildings with icons or markers.

**Focusing the camera.** Either trigger `camera.focusEntity` (bound by
`Game.UI.InGame.CameraUISystem`) or set `orbitCameraController.followedEntity` and
`activeCameraController` directly. `SelectedInfoUISystem.TryGetPosition` resolves a `RouteWaypoint`
buffer to the route's centroid, so **focusing a line entity works without any extra work**; a bare
position has no public API and needs the orbit controller's `pivot` or a throwaway entity with a
`Transform`.

## Open questions for implementation

- Exact C# API of `NotificationUISystem.AddOrUpdateNotification` (names confirmed, signature not) —
  only needed for a panel notification; the world-space icon path above is fully confirmed.
- Whether a mod-written `Game.Objects.Color` survives `ObjectColorSystem`'s next update, and whether
  stop-level colouring (`TransportStopInfomodePrefab`) is the better answer for
  "show me how well each building is connected".
- Whether a runtime-created `NotificationIconPrefab` with a fresh `Texture2D` actually renders.
- Whether `::after { content: … }` works in cohtml (would give the A1 column a header).
- CSS load order of our file relative to `index.css`.

## What was built (2026-09-06)

The reshuffle is in. Where it differs from the plan above, the plan was wrong and this section is
what the code does.

| Surface | Built as | File |
|---|---|---|
| Every knob | Options page, two tabs (`General`, `Advanced`) | `dotnet/Setting.cs` |
| Line verdicts | An extra cell on each vanilla Transport Overview row, `extend(TransportLineItem)` (approach A1) | `TransitArchitect.mjs` `extendOverview` |
| Suggestions | A section under the vanilla list, `extend(TransportationOverviewPage)` | `TransitArchitect.mjs` `SuggestionsSection` |
| Infoview legend | The game's own, from `GradientInfomodeBasePrefab` | `Overlay/SuitabilityInfomodePrefab.cs` |
| Buildings by transit access | `Game.Objects.Color` written between `ObjectColorSystem` and `BatchDataSystem` | `F8Equity/Systems/BuildingAccessColorSystem.cs` |
| Ranked sites | Rings in the overlay buffer, size and opacity by rank | `Presentation/RouteRenderer.cs` `DrawSiteMarkers` |
| One-click actions | `PoliciesUISystem.SetPolicy` for the fleet and the schedule, `ToolSystem.selected` plus the orbit camera to focus | `F9LineHealth/Systems/TransitArchitectSystem.F9Actions.cs` |
| Rebuild markers | `IconCommandSystem`, reusing a shipped icon prefab by name | `…F9Notifications.cs` |
| The mod's own window | Two city-wide figures and the heat-map switch, nothing else | `TransitArchitect.mjs` `Panel` |

Four planned things did **not** survive contact:

- **Hiding our infoview row is gone, not replaced.** The `MutationObserver` hack was deleted rather
  than rewritten as `extend(InfoviewMenu)`: with a suitability map and a transit-access view to
  offer, a row in the game's own infoview menu is where a player expects to find them.
- **The mod's own gradient legend is gone.** The infomode prefabs make the game draw its own, and
  two legends on one screen was the thing the hack was hiding from in the first place.
- **The site markers are not clickable.** The overlay buffer draws geometry only, and a click would
  need a tool of its own; rank is carried by size and opacity instead of a number.
- **The panel's improvement plan moved into the row that asked for it.** Clicking a verdict that
  means building work sends `improveLine` and `focusLine`; the worked-out plan then replaces that
  row's own hint, so the answer appears where the question was asked.

Two of the open questions above are answered: a mod-written `Game.Objects.Color` does survive, as
long as the system writing it is ordered after `ObjectColorSystem` and before `BatchDataSystem`; and
a runtime-created notification icon prefab is the wrong path, because
`NotificationIconRenderSystem` packs every registered icon into one `Texture2DArray` and takes its
format from the last prefab it walks.
