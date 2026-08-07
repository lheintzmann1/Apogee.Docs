# Documenting the APIs

The three API references are generated from the engine sources, not written here. This page is
about how to write in those sources so the generated pages come out right.

Nothing on this page is required to get a binding listed — every public C++ declaration and every
sol2 registration is picked up automatically. What follows is how to make the entries *good*.

## C++

Document with XML comments, the style already used throughout the engine:

```cpp
/// <summary>
/// Sets the layer the actor belongs to.
/// </summary>
/// <param name="layerIndex">The index of the layer.</param>
/// <returns>True if the layer changed.</returns>
API_FUNCTION() bool SetLayer(int32 layerIndex);
```

Doxygen folds `<summary>` into the brief description and lifts `<param>` and `<returns>` into
structured fields, so all three end up in the right place on the page. `<remarks>`, `<example>`
and `<see>` work too.

Notes:

- The `API_*` macros are expanded to nothing before parsing. A member is documented the same way
  whether or not it is exported to C# and Lua.
- Doxygen's native commands (`\param`, `\note`, `\deprecated`) are also understood; the XML style
  is preferred only for consistency with the rest of the engine.
- Markdown is **not** processed inside C++ comments. Write prose; the generator escapes and
  formats it. Code samples in `<example>` blocks are rendered as C++.
- Private members are excluded. Protected members are included, because a subclass author needs
  them.

To keep a type out of the reference entirely, add it to `cpp.excludeTypes` (a regex) or
`cpp.excludePaths` in `docgen.json`.

## C#

Standard XML documentation comments. DocFX reads them from `Apogee.CSharp.xml`, which the engine
build emits next to the assembly.

```csharp
/// <summary>Gets the actor's world transform.</summary>
/// <returns>The transform in world space.</returns>
public Transform GetWorldTransform() { ... }
```

Members marked `[HideInEditor]` or `[EditorBrowsable(EditorBrowsableState.Never)]` are excluded —
see `api-filter.yml`.

## Lua

The Lua reference is extracted from the sol2 registration calls themselves, so the *structure* —
tables, functions, usertypes, fields, constructors, metamethods — is always complete and always
current. The generator fills in the rest from two further sources, in this order:

1. **The C++ declaration.** A binding that forwards to a native member picks up that member's
   signature, parameter names and `<summary>` automatically:

   ```cpp
   actorType.set_function("SetName", &Actor::SetName);   // documented from Actor::SetName
   ```

   This is the best case, and it needs nothing written in the binding file at all. Document the
   header, and the Lua page follows.

2. **The documentation comment above the registration.** Prose in a `///` (or `/** */`) comment
   becomes the description. Lambdas hide their signature behind a C++ closure, so anything the
   parser cannot see goes in tags:

   ```cpp
   /// Pins the reported delta time to a constant, regardless of how long the frame
   /// actually took — used for deterministic replays and capture.
   /// @param enable boolean  Whether to override the frame delta.
   /// @param value number    The delta to report, in seconds.
   /// @return nil
   time.set_function("SetFixedDeltaTime", [](bool enable, float value)
   {
       Time::SetFixedDeltaTime(enable, value);
   });
   ```

   A plain `//` comment is **not** documentation. Registration code is dense with notes about sol2
   overload resolution, pointer lifetime and why a binding is shaped the way it is; those are for
   whoever maintains the binding, and they read as nonsense on a page aimed at someone scripting a
   game. Write the note as `//` and the description as `///`, in either order — a note between the
   two does not break the block:

   ```cpp
   // Fullname is a StringAnsiView and is not guaranteed to be null-terminated,
   // so the length is passed explicitly.
   /// The actor's scripting type name, suitable for passing back to Spawn.
   actor.set_function("GetTypeName", [](const std::string& guid) -> std::string { ... });
   ```

   The same rule covers the table itself: the `///` comment above `create_named` is what the
   domain's page says at the top, so every table wants one.

### Tags

Written as `@tag` or `---@tag` (the LuaCATS spelling), inside a `///` comment directly above the
registration. A blank line ends the block.

| Tag | Meaning |
| --- | --- |
| `@param <name> <type> <description>` | One parameter. Suffix the name with `?` if optional. |
| `@return <type> <description>` | A return value. Repeat for multiple returns. |
| `@field <name> <type> <description>` | A field on a usertype or module. |
| `@example` / `@usage` | Everything below is a Lua code sample. |
| `@deprecated <message>` | Renders the deprecation banner. |
| `@see <text>` | Adds a "See also" entry. |
| `@luaname <name>` | The Lua name, when a generic helper registers the type. |
| `@hidden` | Omit from the reference. |

Types are Lua-facing names: `number`, `integer`, `string`, `boolean`, `table`, `function`, `any`,
`nil`, an array like `number[]`, or another bound type like `Float3`.

### What the parser infers on its own

- Table and usertype names, from `create_named`, `new_usertype` and `new_enum`.
- Parameter names and types from a lambda's argument list — `[](float value)` becomes
  `value: number`.
- Trailing return types (`-> bool`) when present.
- `sol::overload` sets, rendered as additional signatures on one entry.
- `sol::meta_function::*` metamethods, rendered as operators with their Lua syntax.
- `sol::var`, `sol::property` and `sol::readonly`, rendered as constants, properties and
  read-only fields.
- Constructors from `sol::constructors<...>`, collapsed onto a single `.new`.

When a binding's signature genuinely cannot be determined, the page says so explicitly rather
than showing an empty argument list. That is the cue to add `@param` tags.

### Section banners

A banner rule is dropped rather than read as prose, so the section comments already used in the
binding files can stay exactly as they are:

```cpp
/// ---- Apogee.Time --------------------------------------------------------
/// The frame clock. GetDeltaTime()/GetGameTime() are scaled by TimeScale and
/// stop advancing while the game is paused.
sol::table time = apogee.create_named("Time");
```

The first paragraph becomes the summary shown in listings; the rest becomes the remarks section.
A rule is any line of three or more `-` or `=` that also ends in one, whatever label sits between
them — the label names the section, which the page already has in its heading.

## Checking your work

```bash
./build.sh lua      # or cpp, or cs
./build.sh serve    # rebuild and browse at http://localhost:8080
```

`./build.sh lua` prints a warning for every binding it could not resolve, which is the fastest way
to find the ones still needing tags.
