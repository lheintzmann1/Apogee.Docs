# Physics

Apogee uses [Jolt](https://github.com/jrouwe/JoltPhysics) for rigid-body physics. The engine owns
Jolt's factory, allocators and job pool; scenes own worlds; actors carry bodies through script
components.

## The three layers

**`Physics`** is the static service. It holds the shared runtime and the global settings —
`Gravity`, `SimulationHz` (120 Hz by default), `MaxBodies`, `MaxBodyPairs`,
`MaxContactConstraints`, `DebugDrawEnabled` — all configurable per project through
`PhysicsSettings`.

**`PhysicsWorld`** is one simulation. `Physics::CreateWorld()` makes one; `Physics::GetDefaultWorld()`
returns the global one, created on first use and stepped every frame by the service. Most games
need only the default. A separate world is for something genuinely separate — a prediction rollout,
a preview viewport, a server-side simulation.

**`PhysicsBody`** wraps a `JPH::BodyID` and is the per-object handle: transform, velocity, forces
and impulses, motion type and quality, friction and restitution, sleep state.

## Adding physics to an actor

Attach a **PhysicsBodyScript**. On enable it creates a body at the actor's world transform and adds
it to the scene's world; on disable it removes it. Per fixed update, who drives whom depends on the
motion type:

| Motion type | Behaviour |
| --- | --- |
| `Static` | Never moves. No per-frame sync. Terrain, walls, level geometry. |
| `Kinematic` | The **actor drives the body** — the transform is pushed into Jolt. Moving platforms, doors, anything animated. |
| `Dynamic` | The **body drives the actor** — position and rotation are read back from Jolt. |

The component's own shape is a primitive: `Box`, `Sphere`, `Capsule` or `Cylinder`, sized by
`BoxHalfExtent`, `Radius` and `CapsuleHalfHeight`. Alongside it are `Mass`, `LinearDamping`,
`AngularDamping`, `GravityScale`, `Friction`, `Restitution`, `IsSensor` and `UseCCD`.

For anything a primitive cannot express, build the shape in C++ with the `CollisionShapes` helpers
— convex hulls (including straight from a mesh's position buffer), triangle meshes, height fields,
compound and scaled shapes — and add the body to the world yourself. Triangle-mesh shapes are
static-only, as in any solver.

Kinematic bodies should move with `MoveKinematic(position, rotation, deltaTime)` rather than by
setting the transform. It produces the correct velocity, so dynamic bodies the platform pushes
react properly instead of being teleported through.

## Collisions and triggers

`PhysicsBodyScript` raises four events, delivered on the main thread after the simulation step —
never from a job thread:

```csharp
body.CollisionEntered += other => { /* started touching a solid body */ };
body.CollisionExited  += other => { /* stopped touching it */ };
body.TriggerEntered   += other => { /* entered a sensor, or a sensor was entered */ };
body.TriggerExited    += other => { };
```

A **sensor** (`IsSensor = true`) detects overlaps and produces no contact response. In C++,
`PhysicsBody`'s `OnCollisionEnter` / `OnCollisionExit` callbacks carry a `CollisionInfo` with the
other body, the contact point, the normal and the penetration depth.

## Motion quality

`PhysicsMotionQuality` chooses between discrete stepping and continuous collision detection. CCD
costs more and is for bodies fast enough to tunnel through thin geometry between steps — a bullet,
not a crate. `UseCCD` on the component is the same switch.

## Layers

Object layers are a fixed set of three, not a user-configurable matrix:

| Layer | Collides with |
| --- | --- |
| `STATIC` | Dynamic |
| `DYNAMIC` | Everything |
| `SENSOR` | Dynamic |

They map onto two broad-phase layers (`NON_MOVING`, `MOVING`), which is what keeps the broad phase
cheap: static geometry never gets tested against itself. Filtering beyond this is done per query
(below) or in your own contact handling.

## Queries

`PhysicsWorld` exposes raycasts, shape casts and overlap tests. In C++ each takes a
`PhysicsQueryFilter` — `IncludeStatic`, `IncludeDynamic`, `IncludeSensors` (sensors excluded by
default). The Lua bindings are read-only convenience wrappers that always run against the default
world with the default filter.

```lua
local hit = Apogee.Physics.Raycast(origin, direction, 100.0)
if hit.hit then
    -- hit.point, hit.normal, hit.distance, hit.bodyID
end

local hits = Apogee.Physics.RaycastAll(origin, direction, 100.0)  -- 1-based array, near to far
local grounded = Apogee.Physics.CheckSphere(0.4, feetPosition)    -- boolean
local overlapping = Apogee.Physics.CollidePoint(point)
```

Every query returns a table, so check the `hit` field rather than the table itself — a miss is an
`{ hit = false }` table, which is truthy.

Shape casts sweep a volume instead of a ray, which is what character movement and thick projectiles
need: a ray can slip between two colliders that a capsule would catch.
`SphereCast(radius, origin, direction, maxDistance)`,
`BoxCast(halfExtent, origin, rotation, direction, maxDistance)` and
`CapsuleCast(halfHeight, radius, origin, rotation, direction, maxDistance)` report a `fraction` in
`[0, 1]` along the sweep where a raycast reports a `distance`.

## Characters

`CharacterControllerScript` drives an actor with a kinematic `CharacterVirtual` — the right tool
for a player or NPC, which wants to walk up steps and slide along walls rather than tumble like a
rigid body.

```csharp
controller.MoveInput = new Vector3(input.X, 0, input.Y) * speed;  // every frame
if (jumpPressed && controller.IsGrounded)
    controller.Jump();
```

Configure it with `Radius`, `HalfHeight`, `Mass` (how hard it can push dynamic bodies),
`MaxSlopeAngle` and `JumpSpeed`. `CharacterGroundState` reports the finer distinction — on ground,
on a too-steep slope, in the air.

## Joints and vehicles

`PhysicsJointScript` constrains two actors. `Type` selects the constraint, `ConnectedActor` the
other end, and `Axis` / `LimitMin` / `LimitMax` the parameters:

| Type | What it does |
| --- | --- |
| `Fixed` | Welds two bodies rigidly together. |
| `Point` | Ball socket: shares a point, rotation free. |
| `Distance` | Keeps two points within a `[min, max]` distance. |
| `Hinge` | Rotation about one axis — a door. |
| `Slider` | Translation along one axis — a piston. |
| `Cone` | Limits the angle between two axes. |
| `SwingTwist` | Ragdoll-style swing plus twist limits. |
| `SixDOF` | Fully configurable per-axis limits. |

`PhysicsVehicleScript` wraps Jolt's vehicle constraint for wheeled vehicles.

## The Lua surface

`Apogee.Physics` covers global settings and queries. `Apogee.PhysicsBody` operates on the body
attached to an actor, addressed by that actor's handle — the same convention as
[`Apogee.Actor`](../scripting/lua.md#scene-objects-are-handles-not-objects). Every entry returns
`false` or `nil` if the actor has no `PhysicsBodyScript`, so a stale handle is an ignored call
rather than a crash.

```lua
function OnFixedUpdate()
    local v = Apogee.PhysicsBody.GetLinearVelocity(self)
    if v and v.Y < -20 then
        Apogee.PhysicsBody.AddImpulse(self, Apogee.Float3.new(0, 50, 0))
    end
end
```

One thing worth saying plainly, because it is the most common mistake: setting velocity directly
overrides the solver on that axis. That is right for a character controller and wrong for anything
meant to respond to collisions, which should use forces or impulses.

## Debugging

```lua
Apogee.Physics.SetDebugDrawEnabled(true)
```

draws the default world's body and constraint shapes through `DebugDraw` each frame. Also useful:
`Apogee.Physics.GetGravity` / `SetGravity`, `GetSimulationHz` / `SetSimulationHz`, and
`Apogee.Time.SetTimeScale` to step through a collision in slow motion.

Full API: [`Physics`, `PhysicsBody`, `PhysicsWorld`](../../api-cpp/index.md) in C++,
[`Apogee.Physics`](../../api-lua/index.md) in Lua.
