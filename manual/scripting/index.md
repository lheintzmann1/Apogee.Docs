# Scripting

Apogee can be scripted in three languages, and they are not alternatives to each other so much as
three different distances from the engine.

|  | Lua | C# | C++ |
| --- | --- | --- | --- |
| Typical use | Gameplay, UI, mods | Gameplay, editor tools | Engine work, native plugins |
| Reload without restarting | Yes | No | No |
| Access to the full engine API | Bound subset | Broad | Complete |
| Reference | [Lua API](../../api-lua/index.md) | [C# API](../../api/index.md) | [C++ API](../../api-cpp/index.md) |

Lua is the default for game code: it is the surface mods are written against, and it reloads
without a rebuild. C# is where editor tooling lives. C++ is the engine itself.

- [Lua](lua.md) — writing gameplay in Lua.
- [Lua editor setup](lua-editor-setup.md) — completion and signature help.
- [C#](csharp.md)
- [C++](cpp.md)
