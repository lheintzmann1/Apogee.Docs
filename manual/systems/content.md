# Content and assets

Everything a game loads lives under `Content/`, and the engine handles it in two distinct ways. The
distinction runs through the importer, the cooker and the runtime, so it is worth getting straight
first.

**Assets** are imported, stored in the engine's own container format, and referenced by **GUID**. A
`.png` becomes a texture asset; an `.fbx` becomes a model asset. Nothing refers to them by path at
runtime — a scene holds an id, and the id is resolved through the asset registry.

**Content files** never become assets and are opened by **path**: Lua scripts, RML documents and
RCSS stylesheets, fonts, FMOD banks. They stay as ordinary files under `Content/`, and third-party
libraries that insist on a real path (RmlUi, FMOD) can be handed one.

Both survive into a cooked game; the routes there differ.

## Assets

An asset file is a chunked container with a header, a type name, a serialized version and up to
sixteen data chunks. Its extension names the *domain* it belongs to — `.atex` for a texture,
`.amesh` for a model, `.amat` for a material:

| Extension | Asset types |
| --- | --- |
| `.atex` | `Texture`, `CubeTexture`, `SpriteAtlas`, `IESProfile` |
| `.amesh` | `Model`, `SkinnedModel` |
| `.amat` | `Material`, `MaterialInstance`, `MaterialFunction` |
| `.ashader` | `Shader` |
| `.afont` | `FontAsset` |
| `.aanim` | `Animation`, `AnimationGraph`, `AnimationGraphFunction`, `SkeletonMask`, `SceneAnimation` |
| `.afx` | `ParticleEmitter`, `ParticleEmitterFunction`, `ParticleSystem` |
| `.adata` | `BehaviorTree`, `VisualScript`, `GameplayGlobals`, `RawDataAsset` |
| `.ap` | Anything with no registered domain, and every asset written before the split. |

They are all the same container, read by the same code. **The extension is a convention, not a
loader selector** — loading is driven by the header magic and the type name stored inside, so a
mislabelled file still loads. What the extension buys is everything that selects assets by glob:
mod packaging rules, Git LFS and `.gitattributes` entries, review filters, `escrow_ignore
'**/*.atex'`.

### JSON assets

Not every asset is a binary container. Scenes, prefabs, settings and localization tables are stored
as **JSON text** — an object carrying an `"ID"` and a `"TypeName"` next to its data. They are
assets in every other respect: registered by GUID, referenced the same way, cooked into the same
packages.

| Extension | Asset types |
| --- | --- |
| `.scene` | `Scene` |
| `.prefab` | `Prefab` |
| `.acfg` | `GameSettings` and every other settings object, plus any `JsonAsset` with no domain of its own |
| `.alocale` | `LocalizedStringTable` |

The same rule applies: the extension names the domain, the `TypeName` inside selects the loader.

**`.json` is deliberately not in that table.** A plain `.json` under `Content/` is a
[game content](game-content.md) data sheet — read by path, never an asset. The two used to share
one extension, which meant telling them apart required parsing the file, and a data sheet that
happened to carry `ID` and `TypeName` fields was mistaken for an asset and silently dropped from
cooked builds. Splitting them removed the ambiguity in both directions.

A project written before the split migrates with `ApogeeEditor -migrateassets`, which renames JSON
assets to their domain and leaves plain data files alone — see
[Command line](../editor/command-line.md).

`AssetExtensions` is the table behind it. A game or mod that adds its own asset type inherits a
domain from whichever built-in type it derives from, or claims its own:

```csharp
AssetExtensions.Register("MyGame.DialogueTree", "adlg");
```

`.ap` stays readable forever. To rename assets that still use it, run the editor once with
`-migrateassets`:

```bash
ApogeeEditor -project ../MyGame -migrateassets
```

It reads each asset's type out of its header — handling every storage version, which is why this is
an engine command and not a script — and renames the file to match. Asset references are GUIDs, so
nothing that points at them needs updating. Re-importing an asset migrates it too. See
[Editor command line](../editor/command-line.md) for the other one-shot editor commands.

`Content` is the static service that loads them:

```csharp
var texture = Content.Load<Texture>(id);
var material = Content.LoadAsync<Material>(path);   // editor: by path
```

Loading is asynchronous and reference-counted. `AssetReference<T>` and `SoftAssetReference<T>`
(which does not force a load) are how a C++ or C# type holds one; both serialize and both null
themselves when the target goes away.

### Importing

The importers live in `Source/Engine/ContentImporters/` and are invoked by dropping a file into
`Content/` in the editor:

| Importer | Handles |
| --- | --- |
| `ImportTexture` | Textures and cube textures. |
| `ImportModel` | Models and skinned models, with their skeletons and animations. |
| `ImportAudio` | Audio data. |
| `ImportFont` | Font assets. |
| `ImportShader` | Shader sources. |
| `ImportIES` | IES light profiles. |

Alongside them the `Create*` factories mint an empty asset of a given type — materials, animation
graphs, particle emitters, behaviour trees, JSON assets, raw data.

Import settings are stored with the asset, so re-importing the source file keeps them. Deleting the
asset file and re-dropping the source does not.

### The registry

`AssetsCache` maps ids to paths and type names. It is what makes `Content.Load` by id work without
scanning the disk, and what the cooker rebuilds for the packaged game. In the editor it is kept in
sync as files appear, move and disappear.

### Streaming

`Streaming` manages residency for the asset types that support it — textures and models load their
low mip levels or LODs first and stream the rest based on what is actually being drawn.
`Apogee.Content.IsResident` and `GetMemoryUsage` report what is currently in memory, which is the
first thing to look at when a build's memory is larger than expected.

## Content files

`ContentFiles` is the path-based reader, and it resolves both storage modes behind one API:

```csharp
if (ContentFiles::Exists(TEXT("UI/hud.rml")))
{
    Array<byte> bytes;
    ContentFiles::ReadAllBytes(TEXT("UI/hud.rml"), bytes);   // returns true on failure
}
```

Paths are normalized to a canonical Content-relative form — forward slashes, no leading separator,
no `Content/` prefix — so `Content/UI/hud.rml`, `UI/hud.rml` and an absolute path under the project
Content folder are all the same key.

Two resolution rules matter:

- **Loose files win over packed ones.** That is what makes a packed build patchable: dropping a
  file next to the executable overrides the packed copy without rebuilding the package. In the
  editor, where nothing is packed, it is simply the only path that ever hits.
- **The project's Content folder is checked before the engine's.** This is what lets a project
  shadow an engine-shipped file — replacing `css/theme.lua` to restyle the whole UI, for instance —
  by putting its own copy at the same Content-relative path.

`GetLooseFilePath` returns the real path on disk, or an empty string when the file exists only
inside a package. Callers that need a genuine file (a third-party library that opens it itself) use
that and fall back to reading the bytes.

## Cooking a game

`GameCooker` runs an ordered list of steps:

| Step | What it does |
| --- | --- |
| `ValidateStep` | Checks the build configuration and target platform. |
| `CompileScriptsStep` | Builds the game's native and managed code for the target. |
| `DeployDataStep` | Copies the engine and platform runtime data into the output. |
| `PrecompileAssembliesStep` | AOT-compiles managed assemblies where the platform requires it. |
| `CollectAssetsStep` | Walks the scenes and their references to find every asset the build needs. |
| `CookAssetsStep` | Converts each asset to its platform form and writes the content packages. |
| `PostProcessStep` | Platform-specific finishing — signing, bundling, packaging. |

Only what is reachable is cooked. An asset nothing references does not ship — which is why a plugin
implements `GamePlugin::GetReferences()` to declare content the walk would otherwise miss.

Run it from the editor's build dialog, or headlessly:

```bash
ApogeeEditor -project ../MyGame -build MyPreset -exit
```

### Content packages

Cooked assets are written into `.apak` packages. The same container format also backs `.adlc`
(downloadable content) and `.amod` (a packaged mod); the runtime mounts all three the same way, so
a mod and a DLC are simply additional package files.

### Content files, at cook time

`ContentFilesCooker` handles the files the asset pass never sees: Lua scripts, RML documents and
stylesheets, font faces, FMOD banks, and `.json` [game content](game-content.md) data sheets. It
can deploy them two ways, selected by `BuildSettings.PackContentFiles`:

- **Packed** (the default) — each file becomes a raw-bytes asset keyed by its Content path, inside
  the content packages. Fewer files, and out of reach of casual editing.
- **Loose** — copied into the game's `Content/` folder. Easier to patch and inspect.

Both produce the same Content-relative paths, so nothing downstream — including the game — needs to
know which mode a build used.

Two exceptions to that:

- **Lua scripts are precompiled.** A cooked build carries a `.luac` chunk in place of the `.lua`
  source. Path resolution substitutes it transparently, which is why a `LuaScript` component's
  **Script Path** should keep pointing at the `.lua` even for a shipping build.
- **FMOD banks are always deployed loose**, so they can stream sample data off disk instead of
  being held in memory.

## From Lua

`Apogee.Content` is a read-only view of the registry — useful for diagnostics, not for loading:

```lua
Apogee.Content.Exists('Scenes/Main.scene')
Apogee.Content.GetTypeName(id)
Apogee.Content.IsLoaded(id)
Apogee.Content.GetMemoryUsage()
```

Scenes are loaded through `Apogee.Scene`, by **id** rather than by path — which is what
`Apogee.Content.GetId` is for:

```lua
local id = Apogee.Content.GetId('Scenes/Level2.scene')
Apogee.Scene.LoadAsync(id)
-- async loads land between frames, so wait before assuming the actors exist
if not Apogee.Scene.IsLoading() then ... end
```

Loading is always **additive** — it does not unload what is already open. Call
`Apogee.Scene.UnloadAll()` first to swap scenes.

Full API: [`Content`, `ContentFiles`, `Asset`](../../api-cpp/index.md) in C++,
[`Apogee.Content`](../../api-lua/index.md) in Lua.
