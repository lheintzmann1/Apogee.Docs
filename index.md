# Apogee Engine

Apogee is a 3D game engine written in C++ and C#, with Lua scripting, a UI framework over RmlUi,
FMOD audio, Jolt physics, and Vulkan / DirectX 12 rendering.

## Start here

- **[Manual](manual/index.md)** — guides, concepts, and how-tos, written by hand.
- **[Lua API](api-lua/index.md)** — the scripting surface game code is written against.
- **[C# API](api/index.md)** — the managed engine and editor API.
- **[C++ API](api-cpp/index.md)** — the native engine API, for engine and plugin work.

## Which API am I looking for?

| You are… | Use |
| --- | --- |
| Writing gameplay for a project | [Lua](api-lua/index.md) |
| Writing editor tools or managed gameplay | [C#](api/index.md) |
| Working inside the engine, or writing a native plugin | [C++](api-cpp/index.md) |

The three surfaces are generated from a single engine revision, so they always describe the same
build. The revision is recorded in
[`commit.txt`](https://github.com/lheintzmann1/Apogee.Docs/blob/main/commit.txt).
