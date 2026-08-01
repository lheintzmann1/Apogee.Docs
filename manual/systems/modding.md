# Modding

Modding is a first-class engine service, not something bolted onto a game. `ModManager` discovers
mods at boot, resolves their load order, and runs each behind its own sandboxed scripting host —
today always Lua. A mod that throws is isolated and disabled; it cannot take the others, or the
game, down with it.

The design borrows from [godot-mod-loader](https://github.com/GodotModding/godot-mod-loader) and
[GDWeave](https://github.com/NotNite/GDWeave): a JSON manifest, semver dependencies, a resolved
load order and a hook chain — but without IL rewriting, since the hook points are declared by the
engine and the game rather than patched into them.

## Discovery

Mods live in `{ProjectFolder}/Mods/{id}/`. Each folder needs a `mod.json`; a folder without one is
skipped with a warning, and so is one whose manifest fails to parse. No `Mods/` directory at all
simply means modding is inactive.

A mod is either **loose** — a plain folder with a plain `mod.json`, which is what you develop
against — or **packaged**, a `.amod` container with the manifest embedded as a JSON asset. The
`.amod` container is the same format as `.apak` and `.adlc`, so a packaged mod mounts through the
ordinary content path and can ship assets alongside its scripts.

## The manifest

```json
{
  "id": "example-greeter",
  "name": "Greeter",
  "version": "1.2.0",
  "author": "Ada",
  "description": "Says hello.",
  "entry": "init.lua",

  "dependencies": ["example-core"],
  "optional_dependencies": ["example-ui"],
  "load_after": ["example-core"],
  "load_before": ["example-endgame"],
  "incompatibilities": ["someone-else-greeter"],

  "compatible_engine_version": "0.1.0",
  "tags": ["cosmetic"]
}
```

| Field | Meaning |
| --- | --- |
| `id` | **Required.** `namespace-name` form: lowercase `[a-z0-9_]`, at least one `-`, no leading, trailing or doubled `-`. |
| `entry` | Lua entry script, relative to the mod. Defaults to `init.lua`. |
| `version`, `compatible_engine_version` | Semver. Pre-release and build metadata after `-` or `+` is preserved but ignored for ordering. |
| `dependencies` | **Hard.** A missing one drops this mod. |
| `optional_dependencies` | Load after these if present; do not drop if absent. |
| `load_before`, `load_after` | Ordering only, no dependency implied. |
| `incompatibilities` | Mods that cannot coexist with this one. |
| `content` | Content items declared explicitly, for a mod that does not follow a content type's directory layout. See [Adding content](#adding-content). |

## Load order

`ScanMods()` parses every manifest and resolves the order; `LoadAllMods()` then loads the enabled
ones in it. Resolution runs in four stages:

1. **Duplicates.** The first definition of an id wins; later copies are disabled with a warning.
2. **Missing hard dependencies.** Mods with one are dropped, repeatedly until stable — dropping one
   mod can invalidate another that depended on it.
3. **Incompatibilities.** If a mod declares another active mod incompatible, the *declaring* mod is
   disabled. Deterministic by discovery order.
4. **Topological sort.** Dependencies, optional dependencies, `load_after` and `load_before` become
   edges, and Kahn's algorithm produces the order — seeded in discovery order so the result is
   stable across runs.

A dependency **cycle** is not fatal. It is logged, and the mods involved are appended in discovery
order rather than dropped, so a mistake in someone else's manifest degrades rather than breaks the
load.

## A mod's lifecycle

The entry script may define any of these globals:

| Global | When |
| --- | --- |
| `OnLoad()` | The mod has been loaded. |
| `OnEnable()` | The mod became enabled. |
| `OnDisable()` | The mod became disabled. |
| `OnUpdate(dt)` | Every frame, while loaded. |
| `OnUnload()` | Before the mod is torn down. |

```lua
-- Mods/example-greeter/init.lua

function OnLoad()
    Apogee.Log.Info('Greeter loaded')

    Apogee.Hooks.Add('PlayerSpawned', function(payload)
        Apogee.Log.Info('welcome: ' .. payload)
    end)
end

function OnUnload()
    Apogee.Hooks.Remove('PlayerSpawned')
end
```

## The sandbox

Each mod gets its own `LuaScriptInstance` — the same isolated Lua 5.4 state a gameplay script gets,
with the same curated standard library and the same `Apogee.*` API. See
[Lua § The sandbox](../scripting/lua.md#the-sandbox). Two mods share no globals, and neither can
reach `io`, `os`, `debug` or C extension loading.

On top of the standard API, a mod's state gets two extra tables that the mod host registers after
construction. Because they exist only in a mod's state, they are **not** in the generated
[Lua API reference](../../api-lua/index.md), which covers the bindings every host shares — this page
is their reference.

### `Apogee.Mods`

```lua
Apogee.Mods.List()             -- array of every discovered mod id
Apogee.Mods.IsLoaded('a-b')
Apogee.Mods.Load('a-b')
Apogee.Mods.Unload('a-b')
Apogee.Mods.Reload('a-b')      -- hot-reload during development
```

### `Apogee.Hooks`

Named broadcast points that the engine or the game fire and mods subscribe to:

```lua
Apogee.Hooks.Add('name', function(payload) end)   -- subscribe
Apogee.Hooks.Remove('name')                       -- drop this mod's callbacks for a hook
Apogee.Hooks.Call('name', 'payload')              -- fire a hook to every subscriber
```

The callbacks live inside each mod's own Lua state; the engine-side `HookRegistry` only tracks
which mods listen to which hook and forwards broadcasts. Subscriptions are per-mod so they can be
cleared wholesale when a mod unloads, which is what stops a broadcast from reaching a Lua reference
in a state that no longer exists.

Payloads are strings. Anything structured goes through whatever encoding both sides agree on;
`ModManager::CallModFunction` takes JSON arguments for the same reason.

## Adding content

Hooks are one of the two things a mod does: they **change behaviour**. The other is **adding a
thing of a known kind** — a car, a weapon, a perk — and that is [game content](game-content.md),
which needs no hooks and usually no Lua at all.

Every loaded mod is registered as a content *source*, so a mod only has to follow the directory
layout the game declared for the type:

```text
Mods/example-cars/vehicles/van/
    vehicle.json
    van.amesh
```

That van becomes the item `example-cars:van`, and the game's `onAdd` for `vehicle` wires it into
traffic and shops. Ids are namespaced by mod, so two mods can both ship a `van`. A mod that wants a
different layout can name its content explicitly instead:

```json
"content": [
  { "type": "vehicle", "path": "extras/secret_car/vehicle.json" }
]
```

Content is registered after the entry script runs and before `OnLoad`, so the entry script may
declare a content type of its own and `OnLoad` can already query what the mod contributed.
Unloading a mod withdraws its items — the game's `onRemove` runs — before the mod's Lua state is
destroyed, and `Apogee.Mods.Reload` therefore produces a clean remove-then-add for every item.

See [Game content](game-content.md) for the type declaration, the schema and the reload contract.

## Failure isolation

Every mod is ticked individually. If its tick throws, the mod is unloaded and marked disabled, and
a warning names it:

```text
[Modding] Auto-disabling mod 'example-greeter' after an unhandled error
```

The others keep running. `ModManager::OnModError` is raised with the id and the message, so a game
can surface it in its own UI rather than only in the log. `OnModLoaded` and `OnModUnloaded` are
raised for the ordinary transitions.

## Developing a mod

Loose mods reload without restarting the game:

```lua
Apogee.Mods.Reload('example-greeter')
```

Type that in the developer console, which is the fastest loop — the console is a Lua REPL over the
same API, so you can also inspect state and call into a mod's functions directly. See
[Lua § The developer console](../scripting/lua.md#the-developer-console).

## Adding hook points to a game

A hook point is just a broadcast. From C++:

```cpp
HookRegistry::Broadcast(TEXT("PlayerSpawned"), playerName);
```

Nothing needs registering ahead of time — a hook with no subscribers costs a lookup. What matters
is that the names are stable and documented, because they are the contract a mod is written
against, and renaming one breaks every mod that used it.

Full API: [`ModManager`, `ModInfo`, `HookRegistry`](../../api-cpp/index.md) in the C++ reference.
The rest of what a mod can call is the ordinary [Lua API](../../api-lua/index.md).
