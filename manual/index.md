# Manual

Hand-written guides for working with Apogee. The three API references are generated from the
engine sources and live alongside this manual:
[Lua](../api-lua/index.md), [C#](../api/index.md), [C++](../api-cpp/index.md).

## Sections

- **[Getting started](getting-started/building.md)** — building the engine and creating a project.
- **[Scripting](scripting/index.md)** — the three ways to write code against Apogee, and when to
  reach for each.
- **[User interface](ui/index.md)** — RmlUi and the `apogee-ui` reactive framework on top of it.
- **[Systems](systems/rendering.md)** — rendering, physics, audio, content and modding.
- **[Contributing](contributing/index.md)** — how these pages are built, and how to document the
  APIs so the generated references stay useful.

## New here?

1. [Building the engine](getting-started/building.md) — prerequisites, the build tool, targets.
2. [Your first project](getting-started/first-project.md) — creating a project and attaching a
   first Lua script.
3. [Lua](scripting/lua.md) — the surface most game code is written against.

## Where things are

| I want to… | Read |
| --- | --- |
| Get the engine building on my machine | [Building the engine](getting-started/building.md) |
| Write gameplay | [Lua](scripting/lua.md), or [C#](scripting/csharp.md) |
| Add something to the engine itself | [C++](scripting/cpp.md) |
| Build a HUD or a menu | [The apogee-ui framework](ui/apogee-ui.md) |
| Understand what happens in a frame | [Rendering](systems/rendering.md) |
| Add rigid bodies, characters or queries | [Physics](systems/physics.md) |
| Play sound | [Audio](systems/audio.md) |
| Know how content gets into a shipped build | [Content and assets](systems/content.md) |
| Make my game moddable | [Modding](systems/modding.md) |
| Improve these pages | [Writing documentation](contributing/index.md) |
