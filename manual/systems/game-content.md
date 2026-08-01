# Game content

A mod that ships a car needs some way to say "this is a car", so that the game can put it into
traffic and list it in a shop without the mod ever calling into game internals. Game content is
that mechanism. The game declares what a *vehicle* is; the engine finds vehicles, checks them, and
hands them over.

The engine never learns what a car is. It supplies the mechanism — declare a kind, discover items
of that kind, validate them against a schema, dispatch add and remove, answer queries — and the
game supplies the vocabulary. A racing game declares `vehicle`, a shooter declares `weapon`, and
neither needs an engine change. This is the arrangement [FiveM](https://docs.fivem.net/docs/) uses:
the framework has no idea what a vehicle is; the game defines the type and the framework only
routes files to it.

The base game ships its own content through exactly this pipeline. That is the point rather than a
bonus: it keeps the mechanism exercised by the people who own it, it makes the game's own data
sheets the reference a modder reads, and it means balance changes reload in the editor without a
rebuild.

## Declaring a type

A content type is declared once, from the game's own Lua — typically a `LuaScript` component in the
boot scene.

```lua
Apogee.GameContent.RegisterType {
    id         = 'vehicle',
    directory  = 'vehicles',
    configFile = 'vehicle.json',

    schema = {
        { name = 'displayName', kind = 'string',  required = true },
        { name = 'model',       kind = 'asset',   required = true, assetType = 'Apogee.Model' },
        { name = 'topSpeed',    kind = 'number',  default = 100 },
        { name = 'seats',       kind = 'integer', default = 4 },
        { name = 'class',       kind = 'enum', values = { 'compact', 'suv' }, default = 'compact' },
        { name = 'tags',        kind = 'stringArray' },
    },

    onAdd    = function(item) Traffic.Register(item); Shop.List(item)   end,
    onRemove = function(item) Traffic.Forget(item);   Shop.Delist(item) end,
}
```

| Field | Meaning |
| --- | --- |
| `id` | **Required.** Lowercase `[a-z0-9_]` plus single `-`, no leading, trailing or doubled hyphen. |
| `directory` | Subdirectory scanned inside every source. Omit it for a type filled only from script. |
| `configFile` | Config file expected in each item folder. Defaults to `{id}.json`. |
| `schema` | Declared fields. Anything not listed is preserved but never checked. |
| `strictAssets` | Make an unresolved `asset` field reject the item instead of warning. Off by default. |
| `onAdd`, `onRemove` | Called as items appear and disappear. Both optional. |
| `displayName` | Label for logs and tooling. Defaults to `id`. |

For a schema with no defaults or enums there is a shorthand:

```lua
schema = { displayName = 'string', topSpeed = 'number', seats = 'integer' }
```

## Shipping an item

One folder per item. The folder name is the item's name, and it holds the config file plus whatever
else the item ships.

```text
Mods/example-cars/vehicles/van/
    vehicle.json
    van.amesh
    van_diffuse.atex
```

```json
{
  "displayName": "Delivery Van",
  "model": "Content/Vehicles/van.amesh",
  "topSpeed": 120,
  "seats": 2,
  "class": "compact"
}
```

The base game uses the same layout under its own `Content/`:

```text
Content/vehicles/police/vehicle.json
```

One declaration serves both, because `directory` is relative to each *source* rather than to any
fixed root.

> [!NOTE]
> A `.json` under `Content/` always means a data sheet like this one. The engine's own JSON
> *assets* — settings, scenes, prefabs, localization tables — carry `.acfg`, `.scene`, `.prefab`
> and `.alocale` instead, so the two can never be confused. See
> [Content and assets](content.md#json-assets).

## Sources and item ids

A **source** is a place items come from. There are two, and both are registered for you:

| Source | id | root |
| --- | --- | --- |
| the project | `game` | the project's `Content/` folder |
| a mod | its mod id | the mod's folder |

Item ids are namespaced `{source}:{name}` — `example-cars:van`, `game:police`. Two mods can
therefore both ship an `ak47` without either knowing the other exists. The reserved source id
`game` can never collide with a mod, because a mod id is required to contain a hyphen and `game`
does not.

Each source is scanned both as a directory tree and as packed content, because a cooked game
deploys some content loose and packs the rest. Loose files are scanned first and shadow packed ones
of the same name, which is what lets you patch a shipped build by dropping a file next to the
executable.

## Order does not matter

Mods load at engine start; the game's scripts run at scene load, much later. A vehicle shipped by a
mod therefore exists long before anything has declared what a vehicle *is*.

Items are stored under their type id whether or not that type has been declared. Declaring the type
scans every known source for it and then dispatches `onAdd` for everything already stored, so the
declaration finds the mod's van waiting for it. Registering a source later works the same way in
reverse. Neither half has to know about the other, and there is no initialisation order to get
right.

The same rule is why editing the declaring script in the editor is instant. Dropping a type
declaration keeps its items — the listener is going away, not the content — so the reloaded script
re-declares the type and every item is replayed to the fresh callbacks with no rescan and no disk
access.

## Validation

Each item's config is checked against the schema before anybody sees it. A file that fails is
skipped with a warning naming the file and the problem, and the rest of the scan continues: one
broken mod file must not take out the other forty.

| `kind` | Accepts |
| --- | --- |
| `any` | Anything. Stored verbatim, never checked. |
| `string`, `number`, `boolean` | The matching JSON type. |
| `integer` | A JSON number with no fractional part. |
| `stringArray` | An array whose every element is a string. |
| `enum` | A string appearing in the field's `values`. |
| `asset` | A content path or asset id. See below. |

Every problem in a file is reported in one pass, so a broken config tells you everything wrong with
it at once rather than one issue per edit-and-retry cycle.

A `default` is materialised into the item's config, so a consumer never has to branch on whether a
field was authored. Keys the schema does not mention are kept and never flagged — an item written
against a newer build of the game must still load on an older one.

**`asset` fields warn rather than reject by default.** A loose mod's own assets live outside the
project's `Content/` folder and so are not in the asset registry at all, which means a strict check
would silently delete every mod-shipped vehicle that references its own model. Set
`strictAssets = true` on the type once your content pipeline actually registers mod assets.

## Reading content back

Push (`onAdd`/`onRemove`) is one half; pull is the other.

```lua
Apogee.GameContent.GetTypes()                    --> { 'vehicle', 'weapon' }
Apogee.GameContent.HasType('vehicle')            --> boolean
Apogee.GameContent.GetItems('vehicle')           --> array of item tables
Apogee.GameContent.GetItemIds('vehicle')         --> array of id strings
Apogee.GameContent.GetItem('example-cars:van')   --> item table, or nil
```

An item table:

```lua
{
    id         = 'example-cars:van',
    name       = 'van',
    type       = 'vehicle',
    source     = 'example-cars',
    directory  = '/…/Mods/example-cars/vehicles/van',
    configPath = '/…/Mods/example-cars/vehicles/van/vehicle.json',
    config     = { displayName = 'Delivery Van', topSpeed = 120, … },
}
```

Items can also be added from script, for content that is generated rather than authored:

```lua
Apogee.GameContent.AddItem('vehicle', 'van', { displayName = 'Van' })  --> 'game:van'
Apogee.GameContent.RemoveItem('game:van')
```

An item added this way is namespaced under the calling state's source — a mod's own id inside a
mod, `game` everywhere else — so a script cannot add content in another mod's name.

While iterating on data sheets, `Apogee.GameContent.RescanSource('game')` re-reads one source from
disk, the counterpart of `Apogee.Mods.Reload`. There is no file watcher: polling a content tree
every frame is a different cost class from polling one script file.

## The reload contract

A reload is dispatched as `onRemove` followed by `onAdd`. The engine cannot verify that `onRemove`
undid what `onAdd` did, and it will not try — that symmetry is a game-side contract. Leaked state
on reload is the recurring bug in every system shaped like this one, so keep the two functions
mirror images of each other and prefer removing by id over remembering handles.

## From C++ and C\#

The Lua surface is a thin layer over `GameContentRegistry`. From C++, implement
`IGameContentHandler` and pass it to `GameContentRegistry::RegisterType`; items arrive as
`GameContentItem`, whose `Config` is a parsed tree with typed accessors.

From C#, subscribe to the registry's events — `OnTypeRegistered`, `OnItemAdded`, `OnItemRemoved` —
and read `GameContentItem.ConfigJson`, which is the config as authored with defaults merged in.

## Mods that do not follow the convention

A mod can name content explicitly in its `mod.json` instead of adopting a type's directory layout:

```json
"content": [
  { "type": "vehicle", "path": "extras/secret_car/vehicle.json" },
  { "type": "weapon",  "path": "guns/ak47.json", "name": "ak47" }
]
```

This is the engine's equivalent of FiveM's `data_file`. It works even for a type the current build
has never heard of: the item is stored and dispatched if and when that type is declared. `name`
defaults to the config file's parent directory name.

## Limits

- **Overriding a base-game item is not supported yet.** Ids are namespaced by source, so a mod
  cannot produce a `game:van` that replaces the project's own. That needs an explicit `overrides`
  field and load-order-aware conflict resolution; the registry is add-only for now.
- There is no editor UI for declared types. The registry's events are the seam one would attach to.

See also: [Modding](modding.md), [Content and assets](content.md), [Lua](../scripting/lua.md).
