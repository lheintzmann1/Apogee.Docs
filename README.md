# Apogee.Docs

Source for the Apogee Engine documentation site.

The manual is written by hand. The three API references — **Lua**, **C#** and **C++** — are
generated from the engine sources, so they cannot drift from the engine they describe.

```bash
git clone https://github.com/lheintzmann1/Apogee.Docs.git
cd Apogee.Docs
./build.sh serve      # builds everything, serves on http://localhost:8080
```

## Layout

| Path | What it is |
| --- | --- |
| `manual/` | Hand-written guides. **This is what you edit.** |
| `media/` | Images and downloadable assets. |
| `api/`, `api-cpp/`, `api-lua/` | Generated API metadata. Not committed; rebuilt every time. |
| `tools/Apogee.DocGen/` | The generator for the C++ and Lua references. |
| `commit.txt` | The engine revision the references are built from. |
| `docfx.json`, `doxyfile`, `docgen.json`, `api-filter.yml` | Pipeline configuration. |
| `build.sh` | The entry point for every build task. |

Editing a file under `api/`, `api-cpp/` or `api-lua/` has no effect — the next build overwrites
it. To change what those pages say, change the documentation comment in the engine sources; see
[Documenting the APIs](manual/contributing/documenting-the-api.md).

## Commands

```bash
./build.sh              # full build: engine, C#, C++, Lua, site
./build.sh site         # rebuild the site only (prose changes; a few seconds)
./build.sh api          # regenerate all three API references
./build.sh cpp          # C++ only  (Doxygen -> Apogee.DocGen -> api-cpp/)
./build.sh lua          # Lua only  (sol2 bindings -> Apogee.DocGen -> api-lua/)
./build.sh cs           # C# only   (docfx metadata -> api/)
./build.sh engine       # fetch/checkout the engine at the pinned revision
./build.sh serve        # build, then serve on http://localhost:8080
./build.sh clean        # remove generated output
```

Requirements: the **.NET SDK** (8 or newer), **Doxygen**, and **git** with **git-lfs**. DocFX is
pinned as a local tool and restored by `build.sh`; nothing needs to be installed globally.

## Technical notes

### How the engine is obtained

The engine is not vendored here. `build.sh` resolves it in this order:

1. `$APOGEE_ENGINE`, if set.
2. A sibling checkout at `../Apogee.Engine` — the usual case when working on both repos. It is
   used as-is, and the build warns that it is not the pinned revision.
3. Otherwise `./Apogee.Engine`, cloned and hard-reset to the revision in `commit.txt`.

For the checkout `build.sh` owns (3), moving to a different revision also deletes the engine's
`Binaries/`. That directory is gitignored, so `git checkout` leaves it alone, and in CI it is
restored from the cache along with everything else — see the note under **C#** below for why a
stale one is worse than a missing one. The revision it was built from is recorded in
`.docs-built-revision`. A checkout you supplied yourself (1 or 2) is never touched.

To publish against a newer engine, update `commit.txt` and push. Wherever the checkout lives, it
is exposed at `./Apogee.Engine` (via a symlink when necessary) because `docfx.json` and
`docgen.json` refer to it by that fixed path.

### C# — DocFX, directly

`docfx metadata` reads the built `Apogee.CSharp.dll` together with the `Apogee.CSharp.xml`
documentation file the engine build emits beside it, and writes DocFX metadata into `api/`.

It reads the *assembly*, not `Source/Apogee.CSharp.csproj`, because that project file is generated
by `Apogee.Build`, lists roughly 1500 sources and depends on engine-specific MSBuild targets that
Roslyn cannot load standalone. `build.sh cs` builds the bindings first if the assembly is missing.

This is the only reference generated from build output rather than from sources, which makes it the
only one that can go stale without failing: an assembly built from an older engine produces a
complete, plausible C# reference for the wrong revision, while the C++ and Lua references move on
without it. The build logs which assembly it read, and a revision change discards the previous
build output.

Filtering lives in `api-filter.yml`; members marked `[HideInEditor]` or
`[EditorBrowsable(Never)]` are excluded.

### C++ — Doxygen, then a converter

Doxygen parses the headers under `Source/Engine` and `Source/Editor` and emits XML;
`Apogee.DocGen cpp` converts that into DocFX pages under `api-cpp/`.

Two details make this work on Apogee's sources:

- **The binding macros are expanded away.** `API_CLASS`, `API_FUNCTION` and friends are listed in
  the doxyfile's `PREDEFINED` as empty. Left in place, Doxygen mis-parses the declaration that
  follows and attributes the member to a type that does not exist.
- **XML comments are understood natively.** The engine documents with `/// <summary>`,
  `<param>` and `<returns>`. Doxygen folds `<summary>` into the brief description and lifts the
  rest into structured fields, so no comment rewriting is needed.

The off-the-shelf option for this step, `code2yaml`, is unmaintained and .NET Framework-era, and
the Lua side needs a bespoke generator regardless — so both languages share one emitter here and
are guaranteed to render identically.

Types are grouped in the table of contents by engine module, since the C++ API is largely
global-scope and has no namespace tree to browse. Uids remain the true qualified C++ names.

### Lua — extracted from the sol2 bindings

This is the part with no off-the-shelf equivalent. LDoc and LuaCATS read Lua source; Apogee's Lua
API does not exist in Lua at all. It is defined by roughly 260 `set_function` calls and 34
`new_usertype` declarations of C++ in `Source/Engine/LuaScripting/Bindings`.

`Apogee.DocGen lua` parses those registration calls directly. The *structure* — tables, functions,
usertypes, fields, constructors, metamethods, enums — is therefore exhaustive by construction: a
binding cannot be added without appearing in the docs, and one that is deleted cannot linger.

What the calls alone cannot state is filled in, in order of precedence:

1. **The C++ declaration.** A binding that forwards to a native member (`&Actor::SetName`,
   `static_cast<...>(&VecT::Cross)`, or a lambda that is a single `return Time::GetDeltaTime();`)
   is resolved against the same Doxygen XML the C++ reference uses, and inherits that member's
   signature, parameter names and `<summary>`. Document the header, and the Lua page follows.
2. **The comment above the registration.** Prose becomes the description; section banners
   (`---- Apogee.Time ----`) become the module description.
3. **Explicit tags** — `@param`, `@return`, `@field`, `@example`, `@deprecated` — for anything
   still unknown, chiefly the arguments of non-forwarding lambdas.

It also understands the local aliases and generic helpers the math bindings use: `using VecT =
Vector3Base<T>` together with `RegisterVector3BaseImpl<float>(lua, apogee, "Float3")` is unfolded,
so `Float3` documents its fields as `Float3` rather than `VecT` or `any`.

Where a signature genuinely cannot be determined, the page says so instead of showing an empty
argument list. `./build.sh lua` prints a warning for each unresolved binding.

The same extraction writes `media/apogee.d.lua`, a [LuaCATS](https://luals.github.io/wiki/annotations/)
definition file covering the whole API — the site and editor completion come from one source. See
[Lua editor setup](manual/scripting/lua-editor-setup.md).

### Publishing

`.github/workflows/publish.yml` runs the full build on pushes to `main` and deploys to GitHub
Pages. It needs an `ENGINE_TOKEN` repository secret: a fine-grained PAT with read access to the
private engine repository.

`.github/workflows/build.yml` runs on pull requests and builds the manual **without** the engine.
Most changes here are prose, and making each one wait on an engine clone and a C# build is a poor
trade; it still catches broken links, bad TOC entries and malformed front matter.

## Contributing

See [Writing documentation](manual/contributing/index.md) for the workflow and house style, and
[Documenting the APIs](manual/contributing/documenting-the-api.md) for how to write comments in
the engine sources so the generated references come out well.

## License

Documentation is licensed under [CC BY 4.0](LICENSE.md).
`tools/Apogee.DocGen/` is licensed under the [MIT License](tools/Apogee.DocGen/LICENSE.md).

The generated API references reproduce declarations and documentation comments from the Apogee
Engine sources. Apogee Engine itself is proprietary and no rights to it are granted here; see the
engine repository for its terms.
