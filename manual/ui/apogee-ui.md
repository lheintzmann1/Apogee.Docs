# The apogee-ui framework

A reactive UI framework written in Lua, living in `Content/Engine/UI/Runtime`. It follows the
SolidJS model: there is no virtual DOM and no diffing. Reading a signal inside a computation
subscribes that computation, and writing the signal re-runs it.

Ownership is the other half of the model. Every computation is also a scope: computations and
cleanups created while it runs belong to it, and disposing it disposes them depth-first. The
renderer hangs each element's effects off that element's scope, so removing a subtree provably
unsubscribes every effect that could still touch it — which matters more here than on the web,
because a stale RmlUi element handle is a segfault rather than a caught error.

Modules: `signal`, `state`, `store`, `element`, `control`, `renderer`, `scheduler`, and the `css`
sub-package.

> [!NOTE]
> Stub — expand with the component model, the control-flow helpers, styling via the `css` package,
> and the disposal rules callers have to respect.
