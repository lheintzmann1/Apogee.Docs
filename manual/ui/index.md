# User interface

Apogee's UI is two layers:

- **[RmlUi](rmlui.md)** — the underlying HTML/CSS-like document and layout engine.
- **[apogee-ui](apogee-ui.md)** — a reactive Lua framework over RmlUi, in the SolidJS shape:
  components run once, and fine-grained signals patch only what depends on them.

Game and editor UI is written against `apogee-ui`. Reach for RmlUi directly only when you need
something the framework does not wrap.
