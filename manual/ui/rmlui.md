# RmlUi

[RmlUi](https://github.com/mikke89/RmlUi) is the document and layout engine underneath all of
Apogee's game UI. It parses **RML** (an HTML-like markup) and **RCSS** (a CSS-like stylesheet
language), lays the result out, and produces geometry that the engine draws.

It is not a browser. There is no DOM API surface to speak of beyond what it chooses to expose, no
JavaScript, no network, no HTML5 elements — and, importantly, only a subset of CSS. In exchange it
costs a few megabytes rather than a few hundred, and its draw calls go through the engine's own
renderer.

Most UI code should not talk to RmlUi directly. The [apogee-ui framework](apogee-ui.md) sits on top
of it and is what game and editor UI is written against. Read this page to understand what that
framework is standing on, and for the cases where you do want a raw document.

## Loading a document

`GameUI` is the static service that owns the full-screen RmlUi context:

```lua
local hud = Apogee.GameUI.LoadDocument('Content/UI/hud.rml')
Apogee.GameUI.ShowDocument(hud)
-- ...later
Apogee.GameUI.HideDocument(hud)
Apogee.GameUI.UnloadDocument(hud)
```

`LoadDocument` returns a document id greater than zero, or zero on failure. Every other call takes
that id. Documents layer in load order.

Paths are project-relative (`Content/UI/hud.rml`). They resolve through
[`ContentFiles`](../systems/content.md#content-files), which is what lets a cooked game carry the
markup packed inside a content package while documents keep referring to each other by ordinary
relative paths — a `<link href="theme.rcss">` inside a document works identically loose or packed,
because RmlUi's file access is routed through the same resolver.

## Fonts are not optional

**RmlUi ships no fonts and renders no text at all until a face is loaded.** Not fallback text, not
boxes — nothing. A document whose font never resolved looks like an empty document, which is a
miserable thing to debug from the symptom.

The engine loads a bundled **Inter** face at startup and registers it as the fallback for glyphs
missing from other families, so `font-family: Inter` always works. A project supplies its own by
either dropping it at `Content/Fonts/Default.ttf` or calling:

```lua
Apogee.GameUI.LoadFontFace('Content/UI/fonts/MyFace.ttf', false)  -- second arg: use as fallback
```

Note that RmlUi matches font *weight* by nearest value but font *style* by exact equality, and it
does not synthesise an oblique. With only a normal face loaded, anything asking for
`font-style: italic` would match nothing and render as nothing — so the engine registers the
bundled face under the italic slot as well. Text comes out upright instead of slanted, which is a
far better failure than text silently vanishing. Load a real italic face to replace it.

## What RCSS does and does not have

RCSS covers the parts of CSS that a UI actually needs — the box model, flexbox, positioning,
colours, borders, backgrounds, transitions, media queries, pseudo-classes like `:hover` and
`:active`, and class/id/descendant selectors.

What it does **not** have, and which is the usual source of surprise:

- **No CSS grid.** Flexbox only.
- **No `calc()`.**
- **No custom properties** (`--var`), and therefore no runtime theming by variable reassignment.
- **No `@supports`**, no cascade layers, no container queries.

This is why `apogee-ui`'s styling layer resolves its design tokens *ahead of time* in Lua and emits
concrete values, rather than composing them at runtime the way a web utility framework would — and
why it deliberately offers no utility class for a property RmlUi does not implement. Shipping a
class that silently does nothing is worse than not having it, because nothing tells the author
which one they got.

Colours parse as `#rrggbb` and `#rrggbbaa`, which is what makes an alpha suffix (`bg-blue-500/50`)
expressible at all.

## Dynamic content

Three mechanisms, in increasing order of structure:

**Direct markup replacement** — cheapest, for pushing text into a region:

```lua
Apogee.GameUI.SetElementInnerRML(doc, 'log', '<p>' .. line .. '</p>')
```

The markup is *not* escaped. Escape untrusted text before passing it.

**Data models** — RmlUi's own binding layer, driving `{{ key }}` interpolation, `data-*`
attributes and `data-for` lists. Variables are created on first set and are two-way bound:

```lua
local model = Apogee.GameUI.CreateDataModel('player')
Apogee.GameUI.SetModelString(model, 'name', 'Ada')
Apogee.GameUI.SetModelInt(model, 'health', 100)
```

A document binds to one with `data-model="player"`. The model has to exist *before* the document
that binds to it is loaded.

**apogee-ui** — a full reactive component model. This is what to use for anything with real
structure; see [the framework page](apogee-ui.md).

## Lua inside a document

RmlUi's official Lua plugin is installed on the same `sol` state that carries the `Apogee.*` API, so
one Lua world holds both. Inline handlers in markup are Lua, and RmlUi's own `Element` / `Document`
/ `rmlui` API is reachable alongside the engine's:

```html
<button onclick="Phone.OpenContacts()">Contacts</button>
```

Define the tables those handlers call by running a script into the shared state first:

```lua
Apogee.GameUI.LoadScript('Content/UI/phone.lua')
local doc = Apogee.GameUI.LoadDocument('Content/UI/phone.rml')
```

There is also an ergonomic shim over the plugin's data-model API — `Apogee.GameUI.OpenDataModel`
binds a whole Lua table, nested tables and arrays included, and returns a two-way bound handle:

```lua
local model = Apogee.GameUI.OpenDataModel('phone', { contacts = { 'Ada', 'Grace' } })
model.contacts = { 'Ada', 'Grace', 'Alan' }   -- pushes straight to the UI
```

## Input

Window input is routed into the UI by default. Turn it off while gameplay should have it
exclusively:

```lua
Apogee.GameUI.SetInputEnabled(false)
```

`SetResolution` needs calling on window resize; the engine does this for the main context.

## World-space UI

`RmlCanvas` is a script component that renders a document into an off-screen GPU texture instead of
onto the screen — a screen in the world, an in-game terminal, a UI mapped onto a curved surface.
Attach it to an actor, set `Module` (an apogee-ui entry point) or `Document` (plain RML) and
`Resolution`, then sample `GetRenderTarget()` from a material.

Interaction is fed in manually, in surface UV coordinates, which is what lets you drive it from a
raycast against the surface:

```csharp
canvas.SetPointer(hitUV);
canvas.SetPointerButton(MouseButton.Left, pressed);
```

Each canvas gets its own RmlUi context, so world-space UIs are fully independent of the full-screen
stack and of each other.

## The debugger

RmlUi ships a visual debugger — the UI equivalent of a browser's dev tools. There is no key
binding for it, matching the developer console; open it from the console, a gameplay script or an
options menu:

```lua
Apogee.GameUI.ToggleDebugger()
Apogee.GameUI.SetDebuggerVisible(true)
Apogee.GameUI.IsDebuggerVisible()
```

Its panels are **Element Info** (the selected element's tree position, attributes and computed
RCSS), **Event Log**, **Outlines** (draws every element's box), **Data Models**, and **Apogee UI** —
which reports on the reactive framework rather than on the document, and is documented in
[apogee-ui](apogee-ui.md#inspecting-a-running-ui).

### Inspecting a world-space canvas

By default the debugger inspects the full-screen `"main"` context, so an `RmlCanvas` is invisible to
it. Point it at one by name — a canvas names its context after its actor's id:

```lua
Apogee.GameUI.SetDebuggerContext(tostring(actorId))
Apogee.GameUI.SetDebuggerContext('main')          -- back to the full-screen stack
```

The debugger's own panels stay on the main screen either way. That is what you want: a canvas is
painted onto a surface somewhere in the world, and you would not be able to read an inspector
mapped onto it.

### Version

`Apogee.GameUI.GetRmlUiVersion()` returns the vendored RmlUi version, which the debugger also shows
in its corner and which the engine logs at startup. It is recorded in
`Source/ThirdParty/RmlUi/VENDOR.txt` and must be updated when RmlUi is re-vendored.
