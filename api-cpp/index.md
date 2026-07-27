# C++ API

The native engine API, generated from the headers under `Source/Engine` and `Source/Editor` by
Doxygen and rendered by `Apogee.DocGen`.

This is the surface for engine work and native plugins. If you are writing gameplay, you probably
want the [Lua API](../api-lua/index.md) or the [C# API](../api/index.md) instead.

## Reading this reference

- Types are grouped by engine module (`Engine/Level`, `Engine/Graphics`, `Engine/Physics`, …),
  matching the folder layout of the sources.
- Public and protected members are listed; private members are not.
- The scripting-binding macros (`API_CLASS`, `API_FUNCTION`, …) are stripped before parsing, so a
  declaration appears here the way the compiler sees it.

Vendored third-party code, platform back-ends, build tooling and tests are excluded — see
`docgen.json` in the docs repository for the exact filter.
