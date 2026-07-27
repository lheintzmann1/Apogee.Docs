# Lua

Apogee embeds Lua 5.4 through [sol2](https://github.com/ThePhD/sol2). Everything the engine
exposes lives under the global `Apogee` table; the full listing is the
[Lua API reference](../../api-lua/index.md).

```lua
local speed = 5.0

function OnUpdate()
    local dt = Apogee.Time.GetDeltaTime()
    local pos = Actor:GetPosition()
    Actor:SetPosition(pos + Apogee.Float3.new(speed * dt, 0, 0))
end
```

## The shape of the API

- **Modules** are plain tables: `Apogee.Time`, `Apogee.Log`, `Apogee.Screen`, `Apogee.Input`.
- **Classes** are sol2 usertypes — C++ types projected into Lua. `Apogee.Float3.new(1, 2, 3)`
  constructs one; methods are called with a colon.
- **Enums** are tables of integer constants: `Apogee.Key.Space`, `Apogee.MouseButton.Left`.

Math types implement the Lua metamethods you would expect, so vectors compose with operators
rather than method calls:

```lua
local v = Apogee.Float3.new(1, 0, 0) * 2 + Apogee.Float3.Up
```

> [!NOTE]
> Stub — expand with the script lifecycle callbacks, attaching scripts to actors, coroutines and
> the scheduler, and error handling.
