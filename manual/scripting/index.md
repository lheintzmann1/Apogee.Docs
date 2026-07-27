# Scripting

Apogee can be scripted in three languages, and they are not alternatives to each other so much as
three different distances from the engine. **All three can write gameplay** — they differ in what
else they can reach and in what they cost to iterate on.

|  | Lua | C# | C++ |
| --- | --- | --- | --- |
| Gameplay | Yes | Yes | Yes |
| Also used for | UI, mods | Editor tools | Engine work, native plugins |
| Reload without restarting | Yes | No (domain reload on build) | No (rebuild) |
| Access to the engine API | Bound subset | Broad | Complete |
| Reachable from a mod | Yes | No | No |
| Reference | [Lua API](../../api-lua/index.md) | [C# API](../../api/index.md) | [C++ API](../../api-cpp/index.md) |

The lifecycle is the same in all three, because it is one mechanism: `OnStart`, `OnEnable`,
`OnDisable`, `OnUpdate`, `OnLateUpdate`, `OnFixedUpdate`, `OnDestroy`. A component written in any
of them attaches to an actor the same way and appears in the editor the same way.

## Choosing

- **Lua** is the default for game code. It reloads without a rebuild, which makes the iteration
  loop tight, and it is the surface mods and UI are written against. Its API is a curated binding
  of the engine rather than all of it.
- **C#** is compiled and type-checked, reaches the whole managed API, and is the only option for
  editor tooling. Good for systems and gameplay that has outgrown a script.
- **C++** is the fastest and reaches everything, including engine internals that are never bound.
  Reach for it for gameplay that is performance-critical or that needs an engine facility with no
  managed or Lua projection — and for anything that *is* an engine feature rather than a use of
  one.

A common split is UI and moment-to-moment gameplay in Lua, systems and tooling in C#, and hot paths
and engine work in C++. Nothing forces that split; a project can write all of its gameplay in any
one of them.

- [Lua](lua.md) — writing gameplay in Lua.
- [Lua editor setup](lua-editor-setup.md) — completion and signature help.
- [C#](csharp.md) — managed gameplay, editor plugins and custom editors.
- [C++](cpp.md) — native gameplay, engine modules and plugins.
