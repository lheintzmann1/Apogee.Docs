# The apogee-ui framework

A reactive UI framework written in Lua, living in `Content/Engine/UI/Runtime`. It follows the
SolidJS model: there is no virtual DOM and no diffing. Reading a signal inside a computation
subscribes that computation, and writing the signal re-runs it.

Ownership is the other half of the model. Every computation is also a scope: computations and
cleanups created while it runs belong to it, and disposing it disposes them depth-first. The
renderer hangs each element's effects off that element's scope, so removing a subtree provably
unsubscribes every effect that could still touch it — which matters more here than on the web,
because a stale RmlUi element handle is a segfault rather than a caught error.

## A component

```lua
local ui = require 'apogee.ui'
local div, span, button = ui.div, ui.span, ui.button
local text = ui.text

return function()
    local count, setCount = ui.signal(0)

    return div { class = 'flex flex-col gap-3 p-4 bg-gray-800 rounded-lg',
        text(function() return 'Clicked ' .. count() .. ' times' end),
        button {
            class = 'px-3 py-2 bg-blue-600 hover:bg-blue-500 rounded',
            onclick = function() setCount(count() + 1) end,
            'Click me',
        },
    }
end
```

Mount it from anywhere — C#, a gameplay Lua script, the developer console:

```lua
local doc = Apogee.GameUI.Mount('Content/UI/hud.lua')
```

Every `Mount` instances its own document, so a project runs as many independent UIs as it likes —
a HUD, a phone, an in-game TV — each with its own element tree, style sheet and component tree.
They share the full-screen RmlUi context and layer in mount order. `Unmount(docId)` tears the
component tree down but keeps the document; `UnloadDocument(docId)` gets rid of both.

The module's return value is the root component: a function taking props and returning a
descriptor. **A component runs once.** It is not re-run when its state changes — the signals it
read patch exactly the nodes that depend on them.

## Markup

Lua has no JSX, but its "one table argument needs no parentheses" rule gets close enough. A
builder takes a single table carrying both props (the hash part) and children (the array part):

```lua
div { class = 'p-4', id = 'panel',
    h1 { class = 'text-2xl', 'Inventory' },
    button { onclick = close, 'Close' },
}
```

Named builders exist for the tags UI actually reaches for (`div`, `span`, `p`, `button`, `a`,
`ul`, `li`, `h1`–`h6`, `img`, `input`, `select`, `textarea`, `form`, `label`, `progress`,
`handle`, `tabset`, …). `ui.h(tag, spec)` is the escape hatch: RmlUi instances unknown tags with
its default instancer, so any name is valid and behaves like a `div` until RCSS says otherwise.

Props map onto the element like this:

| Prop | Effect |
| --- | --- |
| `class` | Class list. Registers the utility classes used (see [Styling](#styling)). |
| `id` | Element id. |
| `style` | Table of inline RCSS properties, applied and *un*-applied wholesale. |
| `on<event>` | Event handler — `onclick`, `onchange`, `onmouseover`. Receives `(event, target, document)`. |
| `ref` | Called with the element once created, for the rare case that needs the raw handle. |
| anything else | Set as an attribute. `true` sets a presence attribute, `nil`/`false` removes it. |

Any prop can be a **function** instead of a value, which makes it reactive — re-applied whenever
the signals it reads change:

```lua
span {
    class = function()
        return 'px-2 py-1 rounded ' .. (critical() and 'bg-red-600' or 'bg-green-600')
    end,
    text(status),
}
```

A function appearing as a *child* is a reactive expression, never a component. Components are
ordinary functions you call yourself: `Panel { title = 'Stats' }`.

`ui.text(fn)` wraps a reactive child explicitly; text is patched in place rather than recreating
the node, which is what preserves the caret in an adjacent input and the scroll position of the
container. `ui.dynamic` is the same function under the name that fits when the payload is
structure rather than text. `ui.fragment` groups siblings without introducing an element.

## Reactivity

| Primitive | What it does |
| --- | --- |
| `ui.signal(v)` | Returns `get, set`. Reading inside a computation subscribes it. |
| `ui.memo(fn)` | A derived value: recomputed when its inputs change, and only wakes its own readers. |
| `ui.effect(fn)` | Runs `fn` and re-runs it when anything it read changes. |
| `ui.untrack(fn)` | Reads without subscribing. |
| `ui.batch(fn)` | Coalesces several writes into one flush. |
| `ui.onCleanup(fn)` | Registers teardown for the enclosing scope. |
| `ui.store(t)` | A table whose fields are signals — read `player.health`, write `player.health = 80`. |
| `ui.root(fn)` / `ui.getOwner` / `ui.runWithOwner` / `ui.dispose` | Manual scope control. |
| `ui.catchError(fn)` | Registers an error handler for the current scope and everything under it. |

Setters accept either a value or an updater function:

```lua
setHealth(function(hp) return math.max(0, hp - 15) end)
```

Signals compare by identity, so mutating a table in place does **not** notify. Rebuilding the
table is what makes the change visible — which is the same discipline as any immutable-update
model, and the reason the demo rebuilds its item array rather than calling `table.insert`.

A store is one level deep by design: nested tables are stored as plain values and replacing one
wholesale is what notifies. It subscribes per key, so a component reading `player.health` is not
woken when `player.mana` changes.

### The scheduler

Signal writes never touch the DOM directly. They enqueue the computations that depend on them, and
the queue is drained at exactly three points:

- once per frame, before the RmlUi context updates;
- at the end of an event handler, so a click repaints in the frame it was clicked;
- at the end of a top-level `batch()`.

The reason is cost, not style: every DOM call here crosses into C++ and dirties RmlUi layout. A
gameplay update writing ten signals in a loop would otherwise cost ten layout invalidations in one
frame; deferring makes it one.

An effect that writes a signal it also reads never settles. Rather than hang the frame, the
scheduler gives up after a bounded number of passes and reports it.

### Frame-driven state

For UI that follows continuously changing engine state with no change event to hook — a health bar
tracking a value that is simply read every frame — `ui.onFrame(fn)` runs `fn` once per frame before
the context updates. It returns an unregister function and is also unregistered automatically when
the enclosing scope is disposed.

## Control flow

In a fine-grained renderer these are components rather than syntax, because there is no re-render
pass to put an `if` inside — something has to own the subscription that decides which branch
exists.

```lua
ui.Show { when = isOpen,
    fallback = span { 'Nothing selected' },
    div { class = 'p-4', 'Contents' },
}

ui.Switch { fallback = span { 'Idle' },
    ui.Match { when = isLoading, span { 'Loading…' } },
    ui.Match { when = hasError,  span { 'Failed' } },
}

ui.For { each = items, key = function(item) return item.id end,
    function(item, index)
        return div { class = 'flex justify-between p-2', item.name }
    end
}
```

`ui.Dynamic(fn)` renders whatever descriptor `fn` returns, for when the component itself varies.
`ui.Portal` renders elsewhere in the document. `ui.Boundary` catches errors from its subtree and
renders a fallback.

Two behaviours worth internalising:

- **`Show` and `Switch` destroy and rebuild the branch** when the condition flips. State living
  inside a branch does not survive — lift anything that must persist into the enclosing component.
- **`For` is keyed**, so adding, removing and reordering leave surviving rows and their state
  alone. A reorder *moves* a row rather than rebuilding it, which is why the render function's
  second argument is an accessor (`index()`) and not a number: a row's position can change
  underneath it. Without an explicit `key`, identity is the item itself for tables and strings and
  the index otherwise — and index keys make a reorder look like a content change, so pass `key`
  for anything reorderable.

## Styling

Classes are Tailwind-shaped utilities compiled to RCSS on demand. Nothing is generated until a
component actually uses a class, so a document carries only the rules it needs:

```text
'p-3'               ->  .p-3 { padding: 0.75rem; }
'hover:bg-gray-700' ->  .hover\:bg-gray-700:hover { background-color: #374151; }
'md:p-6'            ->  @media (min-width: 768px) { .md\:p-6 { padding: 1.5rem; } }
'w-[220px]'         ->  .w-\[220px\] { width: 220px; }
'bg-blue-500/50'    ->  .bg-blue-500\/50 { background-color: #3b82f680; }
```

Injection is batched: the renderer registers class names as it creates elements, and the whole
frame's new rules go into the document in one call after the scheduler settles. This is a
correctness-of-cost requirement rather than an optimisation — each injection restyles the entire
document, so per-class injection would make building an N-row list cost N full restyles.

**Only properties RmlUi implements are offered.** There is deliberately no grid, no `calc()` and no
custom properties: shipping a utility that silently does nothing is worse than not having it,
because the author has no way to tell the difference. See
[RmlUi](rmlui.md#what-rcss-does-and-does-not-have).

### Theming

`css/theme.lua` holds the design tokens — the spacing scale, the size scale, the palette — and
`compile.lua` turns those numbers into rules. Everything in it is expressed in units RCSS actually
understands, because there are no custom properties to compose at runtime; the scale has to be
resolved ahead of time.

A project replaces the whole file by putting its own at the same Content-relative path,
`Content/Engine/UI/Runtime/css/theme.lua`, in the *project*. Content resolution checks the
project's Content folder before the engine's, so the project copy wins. That restyles every
component at once without touching a component.

### Do not compute class names

Every distinct class name mints a distinct rule and a permanent entry in the document's sheet:

```lua
div { class = 'bg-[' .. item.colour .. ']' }   -- one permanent rule per distinct colour
```

A thousand differently coloured items is a thousand rules RmlUi restyles against for the rest of
the document's life. Use an inline style instead, which puts nothing in the sheet at all:

```lua
div { style = { ['background-color'] = item.colour } }
```

Arbitrary values are for constants — `p-[13px]` is fine, `p-[' .. n .. 'px]` is not. The runtime
warns when a document's class count passes the point where this is clearly what is happening.

## Modules and `require`

The UI host installs its own module resolver, unlike a gameplay Lua script where `require` is not
available at all. Names map to Content paths:

```text
apogee.ui            ->  Engine/UI/Runtime/init.lua
apogee.ui.css.theme  ->  Engine/UI/Runtime/css/theme.lua   (or css/theme/init.lua)
hud.panels           ->  UI/hud/panels.lua                 (or UI/hud/panels/init.lua),
                         then Content-root-relative
Content/UI/x.lua     ->  taken verbatim
```

`UI/` is tried first for project modules, so `require 'hud'` finds `Content/UI/hud.lua` — which is
where UI code belongs — before falling back to the Content root. Existence is checked through
`ContentFiles`, so a cooked build's precompiled `.luac` resolves exactly as the `.lua` did.

Circular requires are detected and raise rather than hanging.

## Lifetime and disposal

The rules a caller has to respect:

- **Dispose owns everything below it.** Disposing a scope disposes its computations, its cleanups
  and its children, depth-first. You almost never call `ui.dispose` yourself; the renderer disposes
  an element's scope when the element goes away.
- **Effects that touch an element must be created inside that element's scope.** That is automatic
  when the effect comes from a prop or a child expression, which is nearly always. If you create
  one manually outside the tree, it can outlive the element and reach a freed handle.
- **`onCleanup` runs before the element is destroyed**, so it is safe to touch the element from it.
- **A component that threw is parked.** `catchError` and `Boundary` let an ancestor render
  something else; they do not resume the failed computation. Recovering means rebuilding the
  branch, which is what `Boundary`'s reset does.

There is deliberately no `createResource`/`Suspense` equivalent. Nothing reachable from Lua here is
asynchronous — the bindings are synchronous calls into the engine and content loading happens on
the C++ side before a component runs — so a resource primitive would be a loading state that is
never observed. Use a signal set from whatever completion callback the engine offers.

## Hot reload

In the editor, editing a UI module evicts the module cache, re-requires each entry and remounts
every live document. The registry of what is mounted deliberately survives the reload; component
state does not, because signals live in the closures being replaced and pretending otherwise would
silently keep stale values around.

One consequence to know: edits to `css/theme.lua` do not appear until the *document* reloads
(touch its `.rml`, or restart play mode), because the injected rules are already in the document's
style sheet and are not re-emitted. Component edits — which is what anyone is actually iterating
on — are unaffected.

## World-space UIs

An `RmlCanvas` mounts the same way, with the same components and the same utility classes, into its
own RmlUi context and its own render target. Set the component's `Module` field on the canvas.
See [RmlUi](rmlui.md#world-space-ui).

## A worked example

`Content/UI/demo.lua` in the engine repository exercises every part of the framework — signals,
derived values, reactive text and props, keyed lists, conditional rendering, event handlers, hover
and responsive variants, and an arbitrary-value utility class. Mount it from the console:

```lua
Apogee.GameUI.Mount('Content/UI/demo.lua')
```
