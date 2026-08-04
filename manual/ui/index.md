# User interface

Apogee's UI is two layers:

- **[RmlUi](rmlui.md)** — the underlying HTML/CSS-like document and layout engine.
- **[apogee-ui](apogee-ui.md)** — a reactive Lua framework over RmlUi, in the SolidJS shape:
  components run once, and fine-grained signals patch only what depends on them.

Game and editor UI is written against `apogee-ui`. Reach for RmlUi directly only when you need
something the framework does not wrap.

Two things worth knowing about early:

- **[Async and resources](apogee-ui.md#async-and-resources)** — `ui.resource` for anything that
  takes time to load, such as a scene or an asset.
- **[Inspecting a running UI](apogee-ui.md#inspecting-a-running-ui)** — the debugger's Apogee UI
  panel, which shows the component tree and what re-ran, next to RmlUi's own
  [element inspector](rmlui.md#the-debugger).
