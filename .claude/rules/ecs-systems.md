---
paths:
  - "dotnet/**/Gathering/**"
  - "dotnet/**/Systems/**"
  - "dotnet/Overlay/**"
  - "dotnet/Presentation/*.cs"
  - "dotnet/Mod.cs"
  - "dotnet/Setting.cs"
---

# Game-facing systems

You are in the half that talks to Cities: Skylines II: the `Gathering/` readers, the `Systems/`
partials of `WhereTheyGoSystem`, and the `Overlay/` and `Presentation/` shell. Nothing
here can be executed outside the game, so the discipline that replaces testing is: verify the API against the real assembly, and log
enough that a wrong number is visible in the log rather than only on screen.

- **Decompile before writing code against any game API.** Do not infer a component's meaning from
  its name; several have been the opposite of what they look like.
  ```bash
  DOTNET_ROLL_FORWARD=LatestMajor ilspycmd \
    "/mnt/c/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed/Game.dll" \
    -t Game.Simulation.TransportLineSystem
  ```
  When the game already computes the quantity you want, mirror its formula rather than deriving
  your own — a fleet target invented from the achieved interval read `9 vehicles, target 1`.
- **Log every input and decision with the numbers behind it.** This is the only instrument
  available. A verdict, a mode choice, a rejection or a count that reaches the panel should also
  reach `WhereTheyGo.Mod.log` with the values it came from, and units named when they
  are not obvious. A quantity you are unsure about gets logged with what it actually is
  (`waitAccumulator=… (game units, not seconds)`), never silently presented as seconds.
- **The save block is consumed exactly or the load fails.** `WhereTheyGoSystem.SaveState` writes one
  length-prefixed payload behind `SaveFormatVersion`; anything added to it goes INSIDE the payload,
  never as extra fields beside it. The layout itself belongs in `Overlay/Planning/SavePayload.cs`,
  which is pure and tested: add a SECTION there and give it its own version, so a reader that cannot
  read one section still keeps the others. Parsing errors inside the payload are caught and mean
  "start cold"; a length the reader cannot consume must throw, because reading past the block
  corrupts the rest of the save.
- **Anything the routing worker may execute logs through `DeferredLog`.** `Mod.Log` is an
  unguarded `StreamWriter`; two threads writing at once corrupt it. `DeferredLog` writes straight
  through on the main thread and buffers on a thread that bound a buffer, so the same code logs
  correctly on both. New main-thread work that touches a field the worker reads (the journeys,
  the existing lines, the served stops, the walk graph) must be gated on `!m_RoutingPending` like
  its neighbours in `OnUpdate`.
- **Respect the update phases.** `Mod.OnLoad` documents why each system sits where it does:
  Rendering for anything using `OverlayRenderSystem`, whose buffer is drained *before* PreCulling;
  Rendering after `ObjectColorSystem` for the building colours, which it would otherwise reset;
  UIUpdate for bindings and for the pointer, which must run after the camera has moved. Moving a
  system between phases is a behaviour change.
- **The infoview prefab must stay valid and non-editor.** `ToolSystem.SetInfoview` only activates a
  view's infomodes while `ToolSystem.activeInfoview` is non-null, and that getter returns null for
  an invalid view — so hiding the mod from the infoview menu by invalidating the prefab silently
  kills the whole infoview. Presentation-only hiding belongs in the UI module.
- **Ordering of game buffers is not a detail.** `RouteWaypoint`/`RouteSegment` on the *line* are
  travel-ordered and index-aligned. `ConnectedRoute` on a stop is not ordered and must never be
  used to infer a sequence.
- **Change detection is not a heartbeat.** A live city tags nodes and edges `Updated` for reasons
  that move no pavement — the walk network's node count was seen wobbling by four with nobody
  building anything. Anything expensive hung off a changed-query needs a signature of what it
  actually depends on, and an interval. The tile snap is the precedent.
- **Keep the arithmetic out of here.** Anything numerically interesting belongs in the pure files
  (see `pure-math.md`); this side gathers ECS data, calls into that math, and renders the result.
- **Burst jobs constrain what you may reference** — no managed types, no exceptions. Prefer widening
  an existing job's outputs over adding a second pass over the same data.
- **Verify a deploy by file size.** Building deploys into the game's Mods folder, the running game
  locks the DLL, and `ModPostProcessor` returns a misleading exit code shortly after the game closes.
  `make deploy` encodes the wait-and-compare; do not trust a build's exit code alone.
