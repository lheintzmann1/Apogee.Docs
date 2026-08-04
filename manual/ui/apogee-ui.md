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
| `ui.resource(src, fetcher)` | An asynchronously fetched value, as reactive state. See [Async and resources](#async-and-resources). |

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

## Async and resources

`ui.resource` turns something that takes time into reactive state:

```lua
local sceneId, setSceneId = ui.signal(nil)

local scene, sceneActions = ui.resource(sceneId, function(id)
  if not Apogee.Scene.LoadAsync(id) then
    error('not a scene guid: ' .. tostring(id))
  end
  ui.wait(function() return not Apogee.Scene.IsLoading() end)
  return id
end)
```

```lua
scene()          -- the value, or nil when there isn't one
scene.loading    -- true while a fetch is in flight
scene.error      -- whatever the fetcher threw, if it did
scene.latest     -- the last value we ever had, even after a failure
scene.state      -- 'unresolved' | 'pending' | 'ready' | 'refreshing' | 'errored'

sceneActions.refetch()          -- run the fetcher again
sceneActions.mutate(value)      -- set the value directly, without fetching
```

The source is optional — `ui.resource(fetcher)` fetches once. When a source is given, the fetcher
re-runs whenever it changes, and a `nil` or `false` source means "there is nothing to fetch yet":
the fetcher is not called and the resource sits in `unresolved`.

### Writing a fetcher

The fetcher always runs inside a coroutine, so it is written as straight-line code and suspends
where it needs to:

| Helper | What it does |
| --- | --- |
| `ui.wait(predicate)` | Suspends until `predicate()` reads true, checked once per frame. |
| `ui.waitFrames(n)` | Suspends for `n` frames. |
| `coroutine.yield()` | Resumes on the next frame. |

That is the bridge to the engine's poll-based async — `Apogee.Scene.LoadAsync` with
`Apogee.Scene.IsLoading()`, or `Apogee.Content.IsLoaded(guid)`. A fetcher that never suspends is
not a special case: it resolves before `ui.resource` returns, so a refetch from a click handler
repaints in the frame it was clicked.

A fetcher signals failure by raising. Because it runs in a coroutine, that failure never reaches
the reactive graph as an error — it becomes `.error`, and `refetch()` genuinely recovers. (An
effect that throws is *parked* and never runs again; a resource that failed is not.)

### Two differences from SolidJS

`data()` keeps the previous value while `refreshing`, rather than reverting to nil. Without
Suspense, reverting would blink the UI on every refetch.

`data()` never throws. In Solid, reading a failed resource throws so an ErrorBoundary catches it;
here that would park whichever computation read it, and would put the component holding your retry
button inside the boundary that just replaced it. A failed load is a state you render. Pass
`{ boundary = true }` if a branch genuinely cannot proceed without the value and should be replaced
wholesale.

Other options: `initialValue` (seeds `.latest`, and the first fetch starts in `refreshing`),
`name` (for logs and the inspector), `equals`, and `quiet` (suppress the warning on failure).

### There is no Suspense

Suspense means showing a fallback while something below is loading *without tearing down what is
below*. The only conditional rendering here is `element.dynamic`, which destroys and rebuilds — so
a Suspense built on it would destroy the component holding the resource it is waiting for, remount,
fetch again, and suspend again. Making it correct needs a hidden-render mode in the renderer.

Branch on the state instead, which reads better anyway:

```lua
ui.Switch {
  ui.Match { when = function() return scene.state == 'pending' end, Spinner {} },
  ui.Match { when = function() return scene.error ~= nil end,
    ui.button { onclick = sceneActions.refetch, 'Retry' } },
  World { id = scene },
}
```

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

## Inspecting a running UI

RmlUi's debugger has an **Apogee UI** panel that reports on the framework rather than on the
document it produced: the component/owner-scope tree, effect re-runs in the last flush, scheduler
pass counts, elements per document, the utility-class cache and its size in bytes, and any
in-flight resources.

Open it with `Apogee.GameUI.ToggleDebugger()` — from the developer console, a gameplay script, or
an options menu; there is no key binding, matching the console itself. Then click **Apogee UI** in
the debugger's menu bar.

Recording follows the panel's visibility, so a closed panel costs nothing. The *first* time it is
opened in a session the UI modules are reloaded, because the framework's probe guards are captured
as upvalues when each module loads; that restarts components from their initial state, exactly like
any other hot reload. Closing the panel never reloads.

Two things worth knowing:

- With the panel open, every element the renderer creates carries `data-apogee-scope` and
  `data-apogee-doc` attributes. RmlUi's own **Element Info** panel lists attributes, so selecting
  any element there tells you which component produced it.
- The numbers describe the *last completed* flush and exclude the panel's own work, so what you are
  looking at cannot be an artefact of looking at it.

For a headless run or a quick check from the console, `Apogee.GameUI.Inspector.Dump()` prints the
same information as text, and `Apogee.GameUI.Inspector.SetEnabled(true)` starts recording without
the debugger.

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
