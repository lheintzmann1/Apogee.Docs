# C++

C++ writes both the engine and gameplay. Everything the other two languages can reach is a
projection of a C++ declaration, so this is where a feature is *added* rather than exposed — and
it is also the fastest way to write a component, with no subset to work within and no marshalling
in the hot path.

Reference: [C++ API](../../api-cpp/index.md).

## Gameplay in C++

A game's native code is a module in the project's `Source/`, declared by a `*.Build.cs` deriving
from `GameModule` — which builds native code by default and pre-declares the `Core`, `Engine`,
`Level` and `Scripting` dependencies gameplay always wants:

```csharp
using Apogee.Build;

public class Game : GameModule
{
}
```

A component in it is an ordinary `Script` (or `Actor`) subclass, and the `API_*` macros make it
appear in the editor and, if you want, in C# and Lua:

```cpp
API_CLASS() class GAME_API Spinner : public Script
{
    API_AUTO_SERIALIZATION();
    DECLARE_SCRIPTING_TYPE(Spinner);

public:
    /// <summary>Rotation speed, in degrees per second.</summary>
    API_FIELD(Attributes="Limit(0)") float Speed = 90.0f;

    // [Script]
    void OnUpdate() override;
};
```

```cpp
void Spinner::OnUpdate()
{
    const Quaternion spin = Quaternion::Euler(0.0f, Speed * Time::GetDeltaTime(), 0.0f);
    GetActor()->SetLocalOrientation(GetActor()->GetLocalOrientation() * spin);
}
```

Add it to an actor in the editor exactly as you would a C# or Lua script. The cost is iteration
time: a change means a rebuild and a restart, where Lua is a file save. That trade is the main
reason to reach for one over the other — see
[Choosing](index.md#choosing).

The rest of this page is about writing C++ against the engine, which applies whether the code is
gameplay or engine.

## Modules

`Source/Engine/` is a flat list of modules — `Core`, `Graphics`, `Renderer`, `Level`, `Physics`,
`Audio`, `UI`, `Content`, `LuaScripting`, `Modding`, and so on. Each is a folder with a
`<Name>.Build.cs` in it that declares what it depends on:

```csharp
using Apogee.Build;
using Apogee.Build.NativeCpp;

/// <summary>
/// Lua scripting module — the language runtime itself, via sol2.
/// </summary>
public class LuaScripting : EngineModule
{
    public override void Setup(BuildOptions options)
    {
        base.Setup(options);

        options.PublicDependencies.Add("sol2");
        options.PrivateDependencies.Add("Content");
        options.PrivateDependencies.Add("Scripting");
    }
}
```

Public dependencies propagate to anything that depends on this module; private ones do not. All
engine modules share one include root, so an include is written from the source root
(`#include "Engine/Level/Actor.h"`) regardless of which module the header belongs to — the
dependency list controls linkage and build order, not visibility.

Two things about that list are worth knowing before you edit one:

- Engine modules link into a single binary, so a missing dependency often still links. It will
  still bite you as a cycle when the build tool orders modules, or on a platform where the module
  is excluded.
- A cycle is a real error, and the fix is usually not to add the edge. `LuaScripting`'s build file
  documents its own deliberately-omitted dependencies and why they are safe, which is the pattern
  to follow when you hit one.

Modules built by a *project* rather than the engine derive from `GameModule` or
`GameEditorModule`, which pre-declare the engine dependencies gameplay code always wants. See
[C#](csharp.md#adding-c-to-a-project).

## The API macros

`API_CLASS`, `API_FUNCTION` and friends are what make a C++ declaration visible to C# and Lua. They
are defined as *nothing* — in `Engine/Core/Config.h` each expands to an empty macro. They are read
by the binding generator during the build, not by the compiler.

| Macro | Applied to | Effect |
| --- | --- | --- |
| `API_CLASS(...)` | A class | Exposes it as a scripting type. |
| `API_STRUCT(...)` | A struct | Exposes it as a value type. |
| `API_INTERFACE(...)` | A class | Exposes it as an interface. |
| `API_ENUM(...)` | An enum | Exposes the enum and its values. |
| `API_FUNCTION(...)` | A method | Exposes it as a method. |
| `API_PROPERTY(...)` | A getter/setter pair | Exposes them as one property. |
| `API_FIELD(...)` | A field | Exposes it as a field. |
| `API_EVENT(...)` | A `Delegate` member | Exposes it as an event. |
| `API_PARAM(...)` | A parameter | Marks it `Ref`, `Out`, or gives it a default. |
| `API_TYPEDEF(...)` | A typedef | Exposes an alias. |
| `API_INJECT_CODE(...)` | Anywhere | Injects verbatim code into the generated binding. |

The parenthesised arguments carry the metadata: `Static`, `Abstract`, `sealed`, `ReadOnly`,
`Namespace="..."`, and `Attributes="..."` — the last one being a literal C# attribute list applied
to the generated member, which is how a C++ field controls how the editor draws it:

```cpp
/// <summary>Maximum attenuation distance (world units).</summary>
API_PROPERTY(Attributes="EditorDisplay(\"Audio Source\"), Limit(0), EditorOrder(31)")
float GetMaxDistance() const;
```

Alongside them, one of the `DECLARE_*` macros gives the type its runtime identity:

| Macro | Use for |
| --- | --- |
| `DECLARE_SCRIPTING_TYPE(T)` | A scripting object that can be constructed from script. |
| `DECLARE_SCRIPTING_TYPE_NO_SPAWN(T)` | A static service — `Physics`, `Renderer`, `GameUI`. |
| `DECLARE_SCRIPTING_TYPE_MINIMAL(T)` | A type that only needs a type initializer, e.g. a settings container. |
| `DECLARE_SCRIPTING_TYPE_STRUCTURE(T)` | A value type. |
| `DECLARE_SCENE_OBJECT(T)` | An `Actor` or `Script` — a scene object with an id and a prefab link. |

And `API_AUTO_SERIALIZATION()` generates `Serialize` / `Deserialize` / `ShouldSerialize` over the
type's `API_FIELD`s, which is what makes a component's fields survive a scene save without you
writing any of it.

The `Spinner` declaration [above](#gameplay-in-c) is a typical component: it produces a C#
`Spinner : Script` with a `Speed` field the editor draws with a minimum of zero, serialization that
survives a scene save, and — if you add a binding for it — a Lua entry, all from the same
`<summary>`.

## Object lifetime

Scripting objects are reference-counted by the scripting runtime and registered by GUID, not owned
by whoever created them. The consequences that matter in practice:

- Destroy with `DeleteObject()`, never `delete`. Deletion is deferred to the end of the frame, so
  other code may still resolve the handle for the remainder of the tick — which is what makes it
  safe to destroy an actor from inside a callback that is iterating the scene.
- Hold a reference to another scripting object with `ScriptingObjectReference<T>` (or
  `SoftObjectReference<T>` for one that may not be loaded), not a raw pointer. Those participate in
  serialization and are nulled when the target dies; a raw pointer is not and is not.
- Assets use `AssetReference<T>` / `SoftAssetReference<T>` for the same reasons.
- Scene objects are also serialized, and `API_AUTO_SERIALIZATION()` handles that as long as fields
  are declared with `API_FIELD`.

## Actor, Script, service

Three shapes cover most of what gets written:

- **`Actor`** — something in the scene with a transform. `AudioSource` and `AudioListener` are
  actors. Declared with `DECLARE_SCENE_OBJECT`, usually with `ActorContextMenu` / `ActorToolbox`
  attributes so the editor offers it in the create menus.
- **`Script`** — behaviour attached to an actor. `LuaScript`, `PhysicsBodyScript`,
  `CharacterControllerScript` and `RmlCanvas` are scripts. Same lifecycle callbacks as C# and Lua
  see: `OnAwake`, `OnEnable`, `OnStart`, `OnUpdate`, `OnLateUpdate`, `OnFixedUpdate`, `OnDisable`,
  `OnDestroy`.
- **A static service** — `API_CLASS(Static)` with `DECLARE_SCRIPTING_TYPE_NO_SPAWN`, for
  process-wide systems: `Physics`, `Renderer`, `GameUI`, `AAudio`, `ContentFiles`. The public API
  is the static class; the state and the per-frame work live in a file-local subclass of `AEngine`,
  the engine service base, which gives it `Init`, `Update`, `FixedUpdate`, `Draw` and `Dispose`
  callbacks driven by the engine loop. `ModManager` is a compact example of the pattern.

## Native plugins

A plugin is a module like any other, with a type deriving from `GamePlugin` (runtime) or
`EditorPlugin` (editor-only, guarded by `USE_EDITOR`). Both want a public parameterless constructor
and are discovered automatically; `GamePlugin::GetReferences()` is called during cooking so a
plugin can declare assets the cooker would otherwise not know to include.

A plugin project is referenced from the consuming project's `.apogee` file the same way the engine
itself is — see [Your first project](../getting-started/first-project.md#what-is-in-a-project).

## Exposing something to Lua

Adding an `API_*` macro gets you C#. Lua is a separate, explicit step: a registration in
`Source/Engine/LuaScripting/Bindings/`, one file per domain. A binding that simply forwards to the
native member inherits its signature, its parameter names and its `<summary>` automatically:

```cpp
actorType.set_function("SetName", &Actor::SetName);   // documented from Actor::SetName
```

which means the header you already documented is also the Lua documentation. Bindings that wrap a
lambda need their parameters described explicitly; see
[Documenting the APIs](../contributing/documenting-the-api.md#lua).

## Style

The engine's conventions, in brief: 4-space indent, PascalCase for types and public members, XML
documentation comments (`/// <summary>`) on public declarations, forward declarations in headers
over includes where practical. `Source/.editorconfig` is the authority. Files under `Source/` carry
the upstream Flax Engine header for attribution and should keep it.
