# Lua

Apogee embeds Lua 5.4 through [sol2](https://github.com/ThePhD/sol2). Everything the engine
exposes lives under the global `Apogee` table; the full listing is the
[Lua API reference](../../api-lua/index.md).

```lua
local speed = 5.0

function OnUpdate(dt)
    local pos = Apogee.Actor.GetPosition(self)
    Apogee.Actor.SetPosition(self, pos + Apogee.Float3.new(speed * dt, 0, 0))
end
```

## The shape of the API

- **Modules** are plain tables: `Apogee.Time`, `Apogee.Log`, `Apogee.Screen`, `Apogee.Input`,
  `Apogee.Actor`, `Apogee.Scene`, `Apogee.Physics`, `Apogee.Audio`, `Apogee.GameUI`,
  `Apogee.GameContent`.
- **Classes** are sol2 usertypes — C++ types projected into Lua. `Apogee.Float3.new(1, 2, 3)`
  constructs one; methods are called with a colon.
- **Enums** are tables of integer constants: `Apogee.Key.Space`, `Apogee.MouseButton.Left`.

Math types implement the Lua metamethods you would expect, so vectors compose with operators
rather than method calls:

```lua
local v = Apogee.Float3.new(1, 0, 0) * 2 + Apogee.Float3.Up
```

### Scene objects are handles, not objects

Math types are values and behave like values. Actors are not. An actor is addressed by its GUID as
a string, and the `Apogee.Actor.*` functions take that handle as their first argument:

```lua
local player = Apogee.Actor.FindByName('Player')
if Apogee.Actor.Exists(player) then
    Apogee.Actor.AddMovement(player, Apogee.Float3.new(0, 1, 0))
end
```

This is deliberate. A Lua value holding a raw `Actor*` outlives the actor it points at, and the
next call through it is a use-after-free rather than an error. A handle can be checked —
`Apogee.Actor.Exists` — and every function taking one fails soft when it no longer resolves,
returning `false` or `nil` instead of crashing. `Apogee.PhysicsBody.*` follows the same convention,
keyed on the handle of the actor carrying the body.

## Attaching a script to an actor

C# and C++ scripts each compile into their own scripting type, so the editor can list them
individually and an actor can reference one by type. Lua has no compile step and cannot mint types,
so one component stands in for all of them: add a **LuaScript** component to an actor and set its
**Script Path** to a `.lua` file under `Content/`.

Keep that path pointing at the `.lua` even for cooked builds. The cooker precompiles scripts to
`.luac` and the component picks the compiled chunk up on its own.

## Lifecycle

The engine's script lifecycle is forwarded to globals of the same name in your file. Every one of
them is optional.

| Global | When |
| --- | --- |
| `OnStart()` | Once, before the first update after the script becomes enabled. |
| `OnEnable()` | Whenever the script becomes enabled and active. |
| `OnDisable()` | Whenever it becomes disabled or inactive. |
| `OnUpdate(dt)` | Every frame. `dt` is the scaled frame delta, in seconds. |
| `OnLateUpdate()` | Every frame, after all `OnUpdate` calls. |
| `OnFixedUpdate()` | Every fixed step — the physics rate, not the frame rate. |
| `OnDestroy()` | Before the script is destroyed. |

`OnUpdate` is the only one that receives an argument; elsewhere `Apogee.Time.GetDeltaTime()`
returns the same value.

The owning actor is exposed as the global `self`, a handle string as described above.

## The sandbox

Each script owner — a `LuaScript` component, a mod, the developer console, the UI runtime — gets
its own `LuaScriptInstance`: an isolated `sol::state` carrying a curated subset of the standard
library plus the `Apogee.*` API. Globals are per-instance, so two scripts cannot see each other's.

Open: `base`, `coroutine`, `string`, `table`, `math`, `utf8`.

Not open: `io`, `os`, `debug`, `package`, C extension loading, `dofile`, `loadfile`,
`collectgarbage`, and — in a gameplay script — `require`. Hosts that need module loading install
their own resolver rather than opening `package`; the UI runtime is the one that does, and its
rules are in [the apogee-ui page](../ui/apogee-ui.md#modules-and-require).

Sharing state between scripts therefore goes through the engine rather than through Lua globals:
actor tags, a component field, a UI data model, or an `Apogee.Hooks` broadcast.

`Apogee.Mods` and `Apogee.Hooks` exist only inside a mod's state, so a gameplay script never sees
them — they are documented on the [modding page](../systems/modding.md#the-sandbox).
[`Apogee.GameContent`](../systems/game-content.md) is the exception in the other direction: it is
built into *every* state, because the game declares content types from an ordinary script and mods
are only one of the sources that fill them.

## Errors

A script that throws does not take the scene down with it. The error is logged with the script's
name, and that script parks itself — no further lifecycle callbacks are delivered to it until it is
reloaded. The rest of the scene keeps running.

The same isolation applies one level up in modding: a mod whose tick throws is auto-disabled and
unloaded rather than being allowed to fail every frame.

## Hot reload

In the editor, a `LuaScript` watches its source file and re-reads it when the timestamp changes,
restarting the script. Saving in your editor is the whole workflow — no rebuild, no play-mode
restart.

State does not survive a reload. Everything lives in globals and closures that are being replaced,
so the script starts from its initial state; anything that must persist belongs on the actor or in
an engine-side system.

## Coroutines

`coroutine` is open, and it is the idiomatic way to write a sequence that spans frames — there is
no built-in scheduler primitive for gameplay scripts. Drive one from `OnUpdate`:

```lua
local routine

function OnStart()
    routine = coroutine.create(function()
        Apogee.Log.Info('waiting')
        local elapsed = 0
        while elapsed < 2.0 do
            elapsed = elapsed + coroutine.yield()
        end
        Apogee.Log.Info('done')
    end)
end

function OnUpdate(dt)
    if routine and coroutine.status(routine) ~= 'dead' then
        local ok, err = coroutine.resume(routine, dt)
        if not ok then Apogee.Log.Error(err) end
    end
end
```

`coroutine.resume` returns errors rather than raising them, so check the result — an unchecked
failed coroutine simply stops, silently, and the script's own error handling never sees it.

UI code has a real scheduler and does not need any of this; see
[the apogee-ui framework](../ui/apogee-ui.md).

## The developer console

The console is a Lua REPL over the same sandboxed API, so anything a script can do you can type:

```lua
Apogee.Time.SetTimeScale(0.25)
Apogee.Physics.SetDebugDrawEnabled(true)
Apogee.Actor.Spawn('Apogee.EmptyActor', Apogee.Float3.new(0, 5, 0))
```

There is no separate command language — the API *is* the command surface. Bare expressions are
accepted the way the stock `lua` REPL accepts them: the line is compiled as `return <line>` first
and falls back to the line as written only if that fails to parse, so values print without an
explicit `return`.

The console is always available in `Debug` builds and gated in other configurations.

## Editor support

Completion, signature help and hover documentation come from a generated LuaCATS definition file
covering the whole API. See [Lua editor setup](lua-editor-setup.md).
