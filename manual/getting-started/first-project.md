# Your first project

A project is a folder with a `.apogee` file in it. Everything else — `Content/`, `Source/`,
`Binaries/`, `Cache/` — the editor creates as it needs them. Projects live anywhere on disk; the
engine locates one by path, not by registration.

This page assumes you have a built editor. If not, start with [Building the engine](building.md).

## Creating a project

The editor creates a project when it is launched with `-new` and a project path:

```bash
./Build.sh run editor -- -new -project ../MyGame
```

It scaffolds the folder, generates the script project files, and opens it. On later runs, drop the
`-new`:

```bash
./Build.sh run editor -- -project ../MyGame
```

`-lastproject` reopens whatever you had open most recently, which is the convenient form once you
are working on one thing.

## What is in a project

```text
MyGame/
  MyGame.apogee        the project descriptor — this is what makes the folder a project
  Content/             every asset and content file the game loads
    Scenes/
    UI/
    Scripts/
  Source/              C++ and C# code, one folder per module
    Game/
      Game.Build.cs
    Shaders/
  Binaries/            compiled script binaries
  Cache/               editor temporaries, cooked output, thumbnails
  Logs/
```

`MyGame.apogee` is JSON:

```json
{
  "Name": "MyGame",
  "Version": { "Major": 0, "Minor": 1, "Revision": 0, "Build": 1 },
  "Company": "Example",
  "Copyright": "Copyright (c) 2026 Example.",
  "GameTarget": "MyGameTarget",
  "EditorTarget": "MyGameEditorTarget",
  "References": [ { "Name": "$(EnginePath)/Apogee.apogee" } ]
}
```

`GameTarget` and `EditorTarget` name the build targets declared in `Source/`. `References` is how
a project finds the engine, and how one project depends on another — a shared module library or a
plugin is just another project referenced here.

Two directories deserve a `.gitignore` entry and nothing else: `Cache/` and `Binaries/`. Both are
derived. `Content/` is not — it is the game.

## Content, and the two kinds of it

Apogee treats content two different ways, and it is worth knowing which is which early:

- **Assets** are imported. A `.png` becomes a texture asset, an `.fbx` becomes a model asset, and
  the engine refers to them by GUID. Drop one into `Content/` and the editor imports it.
- **Content files** never become assets and are opened by path: Lua scripts, RML documents and
  RCSS stylesheets, fonts, FMOD banks. They sit under `Content/` as ordinary files.

Both survive into a cooked game; they simply take different routes there. See
[Content and assets](../systems/content.md).

## Adding a Lua script

Lua is the default for gameplay. Create `Content/Scripts/Spinner.lua`:

```lua
local speed = 90.0  -- degrees per second

function OnStart()
    Apogee.Log.Info('Spinner started on ' .. Apogee.Actor.GetName(self))
end

function OnUpdate(dt)
    local rotation = Apogee.Actor.GetOrientation(self)
    Apogee.Actor.SetOrientation(self, rotation * Apogee.Quaternion.Euler(0, speed * dt, 0))
end
```

Then, in the editor: select an actor, **Add script → LuaScript**, and set its **Script Path** to
`Scripts/Spinner.lua` — the path is relative to `Content/`.

Two things in that script are worth pointing at:

- `self` is a global the component sets before the script runs. It holds the owning actor's GUID as
  a string, and every `Apogee.Actor.*` function takes one as its first argument. Actors are not Lua
  objects; they are handles.
- The engine lifecycle is forwarded to identically-named globals. `OnStart`, `OnEnable`,
  `OnDisable`, `OnUpdate(dt)`, `OnLateUpdate`, `OnFixedUpdate` and `OnDestroy` are all optional —
  define the ones you need.

While the editor is running, saving the file re-reads and restarts the script. No rebuild, no
restart. See [Lua](../scripting/lua.md) for the rest.

### Editor completion

Point your editor at the generated [LuaCATS definition file](../../media/apogee.d.lua) to get
completion, signature help and hover documentation for the whole `Apogee` API — see
[Lua editor setup](../scripting/lua-editor-setup.md).

## Playing, and the console

Press play in the editor to run the scene. For quick experiments there is also the in-game
developer console, which is a Lua REPL over the same sandboxed API scripts and mods use — there is
no separate command language:

```lua
Apogee.Time.GetGameTime()
Apogee.Actor.FindByName('Player')
Apogee.GameUI.Mount('Content/UI/demo.lua')
```

Bare expressions work, as in the stock `lua` REPL: the console compiles `return <line>` first and
falls back to the line as written, so you can inspect a value without typing `return`.

## Building the game

```bash
./Build.sh game -c Release
```

The cooker turns `Content/` into packages, precompiles Lua to bytecode, and deploys the result
next to the `ApogeeGame` binary. `ApogeeEditor -build <preset>` runs the same thing headlessly,
which is what CI uses.

## Where to go next

- [Scripting](../scripting/index.md) — Lua, C# and C++, and when to reach for each.
- [User interface](../ui/index.md) — building a HUD with `apogee-ui`.
- [Systems](../systems/rendering.md) — rendering, physics, audio, content, modding.
