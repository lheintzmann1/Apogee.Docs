# C#

C# is the engine's managed surface. It is where the editor itself is written, where editor tooling
belongs, and where gameplay goes when it needs more of the engine than the Lua bindings expose or
wants a type-checked compiler between you and a mistake.

Reference: [C# API](../../api/index.md).

## Where the managed code lives

The engine's managed API is one assembly, `Apogee.CSharp.dll`, built alongside the native engine.
It has two namespace roots:

| Namespace | What it is |
| --- | --- |
| `Apogee` | The runtime API — actors, scripts, math, content, input, physics, audio, GUI. Available in both the editor and a cooked game. |
| `ApogeeEditor` | The editor: windows, custom editors, content proxies, the cooker, the surface editors. Not present in a game build. |

Almost none of that assembly is hand-written C#. It is *generated* from the C++ declarations
marked with the `API_*` macros — see [C++](cpp.md#the-api-macros). A method annotated
`API_FUNCTION()` in a header appears as a C# method with the same name, the same parameters and the
same documentation comment. That is why the three API references never disagree: they are three
projections of one set of declarations.

Where a type needs managed-only members, the generated part is extended with a hand-written
`partial class` next to the header — `Script.cs` beside `Script.h`, `Content.cs` beside
`Content.h`. That is the seam for anything that is more natural to express in C# than to marshal.

Build the bindings on their own with:

```bash
./Build.sh bindings
```

## Adding C# to a project

Managed game code is a module, declared by a `*.Build.cs` next to its sources:

```csharp
using Apogee.Build;

public class Game : GameModule
{
    public override void Setup(BuildOptions options)
    {
        base.Setup(options);

        BuildNativeCode = false;   // C#-only module
    }
}
```

`GameModule` already references the `Core`, `Engine`, `Level` and `Scripting` modules and sets up
the scripting API defines (`APOGEE`, plus `APOGEE_EDITOR` or `APOGEE_GAME` depending on the target).
`GameEditorModule` is the same thing plus a reference to `Editor`, for tooling that must not ship
in the game.

Leave `BuildNativeCode` alone if the module has C++ in it too; set `BuildCSharp = false` for a
native module that wants no bindings.

## Gameplay scripts

A gameplay script derives from `Script` and overrides the lifecycle methods it needs. The set is
the same one Lua sees, because it is the same mechanism:

```csharp
using Apogee;

public class Spinner : Script
{
    [EditorOrder(0), Limit(0), Tooltip("Degrees per second.")]
    public float Speed = 90.0f;

    public override void OnStart()
    {
        Debug.Log($"Spinner started on {Actor.Name}");
    }

    public override void OnUpdate()
    {
        Actor.LocalOrientation *= Quaternion.Euler(0, Speed * Time.DeltaTime, 0);
    }
}
```

| Override | When |
| --- | --- |
| `OnAwake` | After the object is loaded. |
| `OnEnable` / `OnDisable` | On becoming enabled/disabled and active/inactive. |
| `OnStart` | Once, before the first update after being enabled. |
| `OnUpdate` | Every frame. |
| `OnLateUpdate` | Every frame, after all `OnUpdate` calls. |
| `OnFixedUpdate` | Every fixed step — the physics rate. |
| `OnDestroy` | Before the object is destroyed. |

Public fields show up in the editor's properties panel. The attributes that shape that display —
`EditorOrder`, `EditorDisplay`, `Limit`, `Tooltip`, `VisibleIf`, `HideInEditor`, `NoSerialize`,
`CustomEditorAlias` — live in the `Apogee` namespace and are the same attributes the engine's own
types use, which makes the engine sources a usable reference for how to annotate your own.

Unlike Lua, C# has no hot reload: scripts are recompiled and the domain reloaded, which the editor
does for you on build, but a running play session does not pick up an edit.

### Actors versus scripts

A `Script` is a component attached to an actor. An `Actor` is a thing in the scene with a
transform. When what you are writing *is* an object in the world — a light, an audio source, a UI
canvas — derive from `Actor` instead; when it is behaviour attached to one, derive from `Script`.

## Editor tooling

Two plugin base classes, in the same shape:

- `GamePlugin` — runtime. It also gets `GetReferences()`, called during cooking, so a plugin can
  inject assets the cooker would otherwise not know are needed.
- `EditorPlugin` — editor-only, guarded by `USE_EDITOR`. This is where you add windows, menu
  entries and asset actions.

Both want a public parameterless constructor and are discovered automatically.

For inspector customization, implement a `CustomEditor` and point a field at it with
`[CustomEditorAlias("MyGame.Editors.ThingEditor")]`, or register it for a type. The engine does
this extensively — `ApogeeEditor.CustomEditors.Dedicated` in the
[C# API reference](../../api/index.md) is a directory of worked examples.

## What is hidden from the reference

Members marked `[HideInEditor]` or `[EditorBrowsable(EditorBrowsableState.Never)]` are filtered out
of the generated documentation. If something you wrote is missing from the C# reference, that
attribute is the first thing to check — see
[Documenting the APIs](../contributing/documenting-the-api.md).

## Where C# sits

All three languages can write gameplay, and none is a subset of another. C#'s particular position:

- Compiled and type-checked, and it reaches the whole managed API — broader than Lua's curated
  binding, narrower than C++'s complete access to engine internals.
- The **only** option for editor tooling.
- No hot reload mid-session, and not reachable from a mod.

See [Choosing](index.md#choosing) for the three-way comparison.
