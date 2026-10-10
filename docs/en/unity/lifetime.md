# GameObject lifetime

This page shows how to tie a flow to a GameObject's lifetime. When you deactivate or destroy the GameObject, the flows tied to it unwind right then. It also covers how to await such work in a flow, how to write objects that are reused from a pool, and how to stop a flow only on destruction.

## RunWhileActive

```csharp
public sealed class Enemy : MonoBehaviour
{
    void Start() => gameObject.RunWhileActive(Patrol()); // canceled by SetActive(false) or Destroy

    async FlowTask Patrol()
    {
        try
        {
            while (true)
            {
                // ...
                await FlowTask.WaitForSeconds(0.5);
            }
        }
        finally
        {
            // runs inside OnDisable: the object is still accessible
        }
    }
}
```

`gameObject.RunWhileActive(task)` starts the flow and returns a `FlowHandle` (a `FlowHandle<T>` if you pass a `FlowTask<T>`). When the GameObject becomes inactive in the hierarchy, the flow is canceled. That happens on:

- `SetActive(false)` on the object itself or a parent
- `Destroy`
- Scene unload (a scene transition)

The cancel happens in `OnDisable` of a hidden component that FlowTask adds to the GameObject, and that World is **flushed right then**. So `using`, `finally`, and `AddCleanup` run inside `OnDisable`, while the object is still usable. With `Destroy` too, Unity calls `OnDisable` inside that call (the destruction itself happens at the end of the frame), so the flow unwinds before `Destroy` returns.

To run on another Clock, pass the Clock as the second argument (`gameObject.RunWhileActive(Patrol(), clocks.Game)`). A Clock of a World other than the default one runs the flow in that World (`gameObject.RunWhileActive(task, physicsWorld.DefaultClock)`).

> **Note**: Reactivating the object does not resume the flow. As with coroutines, deactivation stops it; unlike coroutines, the stopped flow unwinds and ends. To run it again, start a new one (see "Objects reused from a pool" below).

### Calling it on an inactive GameObject

If you call it on a GameObject whose `activeInHierarchy` is false (the object itself or a parent is inactive), nothing runs. The task you pass is discarded without starting, and the returned handle is `Canceled` from the start.

### Always a root flow, wherever you call it

A flow started with `RunWhileActive` is a root flow (the same as one started with `FlowWorld.Run`), wherever you call it from. Its lifetime belongs to the GameObject, so even if you call it from flow code, it does not become a child of the calling scope. It does not stop when the calling scope ends, and its exceptions go to `OnUnhandledException`, not to the calling flow.

To await its result in a flow, or to also stop it when the calling scope ends, use `WhileActive`, described next.

## The awaitable version: WhileActive

```csharp
// Starts when awaited, like every FlowTask; canceled when enemy is deactivated or destroyed
bool completed = await enemy.WhileActive(Attack());

// With a result: (true, result) when it completed, (false, default) when the GameObject was deactivated first
var (finished, score) = await stage.WhileActive(MiniGame());
```

`gameObject.WhileActive(task)` returns a FlowTask and, like any other FlowTask, starts when awaited. It cancels the task when the GameObject is deactivated or destroyed, as `RunWhileActive` does, and the unwinding also runs inside `OnDisable`. The difference is that it runs inside the awaiting scope.

- It is a child of the calling scope, so it ends when that scope ends.
- The task's exceptions are thrown at the await, not sent to `OnUnhandledException`.
- For a task without a result it returns a `bool` (`true` if it completed, `false` if the GameObject was deactivated first); for a `FlowTask<T>` it returns a `(bool Completed, T Value)` (`(true, result)` if it completed, `(false, default)` if deactivated first). Ending by deactivation is not an exception, and the awaiting flow continues.
- If the GameObject is inactive (or destroyed) when it starts, the task is discarded without starting, and it returns `false` or `(false, default)`.
- It appears in dumps and scope paths as `WhileActive(GameObject name) > Attack` (see [Debugging and diagnostics](../tools/debugging.md)).

Scope parent-child relationships are covered in [Scopes and cancellation](../guide/scopes-and-cancellation.md).

## Watch out for immediate unwinding

When you deactivate or `Destroy` a GameObject outside flow code, that call also becomes a Flush point for the World. Not only do the tied flows unwind, but the queued resumptions of other flows also run inside the `SetActive(false)` or `Destroy` call (the same applies when you call `Destroy` inside a physics callback).

So if you deactivate or `Destroy` objects while enumerating a list, a `finally` may modify that list (`enemies.Remove(this)`, for example). Enumerate a copy.

```csharp
foreach (var enemy in enemies.ToArray()) // a copy: a finally may remove itself from the list
{
    Destroy(enemy.gameObject);
}
```

### Deactivating from flow code

When you call `SetActive(false)` or `Destroy` from flow code (a flow that returns an object to the pool, for example), the tied flows of the World running that code also unwind inside the call. Flows running in another World unwind at that World's next Flush.

When a flow deactivates or `Destroy`s its own GameObject, that flow is canceled at its next await. The code up to that point still runs. Even if it ends without awaiting, the handle becomes `Canceled`. A flow that deactivates its object before its first await (the part that runs inside `RunWhileActive`) is canceled at that await, and the handle it returns is already `Canceled`. One that ends there without awaiting has ended before it is tied, so it is `Succeeded`.

```csharp
async FlowTask Die()
{
    IsDead = true;
    await FlowTask.WaitForSeconds(0.3); // the death animation
    Destroy(gameObject);                // this flow ends Canceled when it returns
}
```

When the task of `WhileActive` deactivates its own GameObject, the task is canceled and `false` or `(false, default)` is returned not inside that call, but when the World next runs its queued resumes: later in the same Tick or Flush for code running in one, at the next Flush point for code running in a `FlowWorld.Run` called from outside.

## Scene transitions

When a scene closes, its objects are deactivated and then destroyed, so flows unwind as with `Destroy`. Flows on objects moved to `DontDestroyOnLoad` stay.

For work that outlives a screen (such as a service that receives in-app purchase results), tie it to an object that survives scene changes, or start it with `FlowTaskUnity.World.Run`.

## Objects reused from a pool

For objects whose `SetActive` is toggled by a pool, start the flow with one line in `OnEnable`. Returning the object to the pool (`SetActive(false)`) unwinds and ends the flow, and the next time it is taken out (`SetActive(true)`), `OnEnable` starts a new flow.

```csharp
public sealed class PooledBullet : MonoBehaviour
{
    void OnEnable() => gameObject.RunWhileActive(Fly()); // ends when the bullet goes back to the pool

    async FlowTask Fly()
    {
        try
        {
            while (true)
            {
                transform.position += transform.forward * 0.5f;
                await FlowTask.NextFrame();
            }
        }
        finally
        {
            if (TryGetComponent<TrailRenderer>(out var trail)) trail.Clear(); // runs inside OnDisable
        }
    }
}
```

- A flow started in `Start` does not run again when you deactivate and then reactivate the object. Start flows for reused objects in `OnEnable`.
- If a flow that takes objects from the pool calls `SetActive(true)`, the bullet's flow does not become a child of that flow (it is a root flow). The bullet keeps flying after the flow that took it out ends.
- Only the part of `finally` up to its first await runs inside `OnDisable`. The rest after the await runs after `OnDisable`, so do not touch the object from there (it may already have been taken out of the pool again).

> **Note**: What the flow is tied to is the GameObject's active state. Toggling a component's `enabled` does not stop the flow. However, code that disables every MonoBehaviour (looping over `GetComponents<MonoBehaviour>()` and setting `enabled = false`) also disables FlowTask's hidden component, so the flows running at that time stop.

To also stop on a component's `enabled`, keep the handle of the flow you start in `OnEnable`, and `Cancel()` it in `OnDisable`. Why there is no `this.RunWhileActive` is explained under "Lifetime of engine objects" in [Design rationale](../advanced/design-rationale.md).

```csharp
FlowHandle _aim;

void OnEnable() => _aim = gameObject.RunWhileActive(Aim());
void OnDisable() => _aim.Cancel(); // enabled = false, Destroy(this)
```

- When you set `enabled = false` outside flow code, the flow unwinds not inside `OnDisable`, but at the next Flush point or Tick (see "When cancellation takes effect" in [Execution model](../advanced/execution-model.md)).
- When you deactivate the GameObject, it unwinds inside that call, as above.

## Stopping only on destruction (WaitForDestroy)

To stop only on destruction, not on deactivation, race against `gameObject.WaitForDestroy()`.

```csharp
await FlowTask.Race(Work(), gameObject.WaitForDestroy()); // survives SetActive(false), stops on Destroy
```

- `WaitForDestroy()` is a `FlowTask` that completes when the GameObject is destroyed. It does not complete on deactivation. If the object is already destroyed, it completes immediately.
- The Race is decided by the Flush inside `OnDestroy`, so the `finally` of the losing `Work()` also runs before destruction.
- A `WaitForDestroy()` created in flow code waits on the World running that code. Await it in the same World, or pass it to a Race or similar there.
- Unity does not call `OnDestroy` on a GameObject that has never been active. For such objects, `WaitForDestroy()` checks for destruction on every PlayerLoop Tick and completes (`FlowLifetime.PollWatched()`). If you Tick yourself with `AutoTick = false`, call `PollWatched()` yourself too (see [Setup](setup.md)).

Race and the unwinding of losers are covered in [Composition](../guide/composition.md).

## After recompiling while playing

If you recompile scripts during play and the default World is gone, `FlowTaskUnity.World` and `RunWhileActive` throw a `No FlowTask World: ...` exception. The cause and the setting are described under "Recompiling while playing" in [Setup](setup.md).

## Which one to use

| What you want | How to write it |
| --- | --- |
| In `Start` or `OnEnable`, start a flow that runs only while that GameObject is active | `gameObject.RunWhileActive(task)` |
| In a flow, await work that runs only while some GameObject is active | `await gameObject.WhileActive(task)` |
| Stop only on destruction, not on deactivation | `FlowTask.Race(task, gameObject.WaitForDestroy())` |
| Run across scenes, tied to no GameObject | `FlowTaskUnity.World.Run(task)` |

## Comparison with Godot

`RunWhileActive` and `WhileActive` correspond to Godot's `RunWhileInTree` and `WhileInTree`. Just as Godot runs nothing for a node outside the tree, they run nothing for an inactive GameObject (see [Godot node lifetime](../godot/lifetime.md)).

The handle of `RunWhileActive` returns the task's own result and status, and it is `Canceled` when deactivation ended it. Godot's `RunWhileInTree` returns a `FlowHandle<bool>`, which is `Succeeded` (with the result `false`) when the node leaves the tree too.
