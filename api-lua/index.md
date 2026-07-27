# Lua API

Everything the engine exposes to Lua lives under the global `Apogee` table. This reference is
generated directly from the sol2 registration code in
`Source/Engine/LuaScripting/Bindings`, so it lists exactly what is bound — nothing is documented
that does not exist, and nothing bound is missing.

```lua
local dt = Apogee.Time.GetDeltaTime()
Apogee.Log.Info(('frame took %.2f ms'):format(dt * 1000))
```

## Reading this reference

- **Modules** (`Apogee.Time`, `Apogee.Log`, `Apogee.Screen`, …) are plain tables of functions and
  constants. Call them with a dot: `Apogee.Time.GetDeltaTime()`.
- **Classes** (`Apogee.Float3`, `Apogee.Transform`, …) are sol2 usertypes — C++ types projected
  into Lua. They are constructed with `.new(...)` and their methods are called with a colon.
- **Enums** (`Apogee.Key`, `Apogee.MouseButton`, …) are tables of integer constants.

Where a binding forwards straight to a C++ member, its signature and description come from the
C++ declaration, so the Lua page and the [C++ page](../api-cpp/index.md) agree by construction.

## Editor completion

The same extraction produces a [LuaCATS definition file](../media/apogee.d.lua) covering the whole
API. Point your Lua language server at it to get completion and signature help while writing
gameplay code — see [Editor setup](../manual/scripting/lua-editor-setup.md).
