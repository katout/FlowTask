# Scopes and cancellation

With FlowTask, you don't pass CancellationTokens around by hand. Flows form a tree of scopes, and when a parent ends, its children unwind. This page explains the scope tree, cancellation and unwinding, how to write cleanup (`using`, `finally`, `Flow.AddCleanup`, `Flow.Own`, `Flow.NonCancelable`), and how to write a `catch` that receives a cancellation.

## The scope tree

One run of a FlowTask method is called a scope. A task you start becomes a child of the scope it was started from.

```csharp
async FlowTask Game()
{
    _ = Flow.Spawn(PlayBgm());   // a child of Game
    await Title();                // a child of Game
    await InGame();               // a child of Game; InGame's own awaits are its children
}
```

Once `Title` has ended and `InGame` is awaiting `Battle`, the tree looks like this. A scope that has ended (`Title`) leaves the tree.

```
Game
├─ PlayBgm
└─ InGame
   └─ Battle
```

When a parent ends, all of its descendants that are still running unwind. If `Game` returns, `PlayBgm` stops too. The shape of your code decides which flow lives how long.

- This tree is the only thing that owns lifetimes. "Scope" here means a FlowTask scope; it has nothing to do with things like VContainer's `LifetimeScope`.
- A flow started with `world.Run` is a child of the World's root ([Flows and the World](flows-and-world.md)).
- You can see the current tree with `world.Dump()` ([Debugging](../tools/debugging.md)).

## Cancellation is unwinding

In a canceled scope, a `FlowCanceledException` is thrown from the await where it is suspended, and the method exits, running `using` and `finally` on the way out. This is called unwinding. It doesn't restore (roll back) the game's state.

```csharp
async FlowTask Hud()
{
    Flow.Own(new InputLock());   // disposed when Hud ends, however it ends
    var view = OpenHud();
    try
    {
        while (true)
        {
            await FlowTask.NextFrame();
            view.Refresh();
        }
    }
    finally
    {
        view.Close();   // runs on a cancel too
    }
}

var hud = world.Run(Hud());
// ...
hud.Cancel();   // Hud unwinds: the finally runs, then the InputLock is disposed
```

A scope is canceled in the following cases. The handle's `CancelCause` tells you which.

| Trigger | `CancelCause` |
|---|---|
| The parent returned (a `Flow.Spawn` child) | `ParentEnded` |
| Lost a `FlowTask.Race` | `RaceLost` |
| `FlowHandle.Cancel()` | `Explicit` |
| A failure (an exception in a Race or WhenAll sibling, an exception in a spawned child, the parent ended with an exception, misuse detected) | `Fault` |
| `FlowWorld.Dispose()` | `WorldDisposed` |

Scopes below a canceled scope inherit that scope's cause.

### Unwinding order

Unwinding proceeds in the same order as returning up a call stack.

- Descendants go first, moving toward the root. A scope receives its `FlowCanceledException` only after all of its children have finished (including their cleanup awaits).
- Among siblings, the one started last begins unwinding first.
- Within one scope, `finally` and `using` run from the inside out, and then that scope's `Flow.AddCleanup` and `Flow.Own` run in reverse order of registration (LIFO).

### When unwinding happens

When you call `handle.Cancel()` from flow code, the target starts unwinding immediately. Before you move on to the next line, the target's `finally` runs (up to its first await, if it has one). When you cancel from outside a flow (game code, a UI callback), the target unwinds at the start of the next Tick or Flush. Until then, `Status` stays `Running`. The timing when you cancel from another thread, or cancel yourself, is covered in [Execution model](../advanced/execution-model.md).

## Synchronous cleanup: using, finally, AddCleanup, Own

Cleanup runs whether the scope ends by cancellation, by an exception, or by an ordinary return.

```csharp
async FlowTask Shop()
{
    var view = Flow.Own(OpenShopView());                      // disposed when Shop ends
    Flow.AddCleanup(() => analytics.Log("shop closed"));      // runs when Shop ends
    using var pause = gameClock.Pause();                      // released at the end of this block
    await RunShop(view);
}
```

- `Flow.Own(x)` disposes the given `IDisposable` at the end of the scope and returns `x` as is. Called outside a flow, it owns nothing, and the caller is responsible for disposing.
- `Flow.AddCleanup(action)` registers work to run at the end of the scope. Outside a flow, it throws `FlowMisuseException`. To avoid a closure, use `Flow.AddCleanup(state, s => …)`.
- The handles returned by `Clock.Pause()`, `Signal.Subscribe`, and `FlowBridge.FromCallback` are also owned by the scope that created them, and released at the end of the scope. To release one earlier, add `using`. If you drop the return value, it lives until the scope ends, so analyzer rule FLOW004 warns about it.

## Awaiting in cleanup

Awaits in a `finally` or `catch` entered because a cancellation arrived run to the end. You can write a closing animation or a save as a plain await.

```csharp
async FlowTask Dialog()
{
    var view = Open();
    try
    {
        await FlowTask.Never();      // left only by the cancel
    }
    finally
    {
        await view.PlayClose();      // entered by the cancel: runs to its end
        view.Destroy();
    }
}
```

The `DisposeAsync` of an `await using` also runs to the end in the same way.

- Until the cleanup finishes, the scope stays `Running`. (`CancelCause` tells you it is closing, and its line in the dump shows something like `<canceling: Explicit>`.)
- Everything around it waits for it to finish: the parent ending, the caller of a Race (which resumes after the losers' cleanup finishes), and a failed WhenAll (which passes on the exception after its siblings' cleanup finishes).
- Tasks started by a cleanup await are not canceled even if an ancestor is canceled later. Only `World.Dispose` reaches them.
- Cleanup awaits obey Pause like any other resume. If some cleanup must finish even while the game is paused, run it on a UI Clock ([Time and Clocks](time-and-clocks.md)).

> **Note**: Cleanup awaits have no time limit. Cleanup that never ends (waiting for input, an endless loop) keeps the flow, and everything waiting for it, stuck. If you need a limit, use a Race: `await FlowTask.Race(Save(), FlowTask.WaitForSeconds(2));` (if it doesn't finish in 2 seconds, `Save` unwinds). If a canceled scope keeps waiting in cleanup for more than 10 seconds of unscaled time (`UnscaledClock`), the World raises a `LongCleanup` warning once ([Debugging](../tools/debugging.md)).

If you don't want the caller of a Race to wait for a loser's cleanup, start the cleanup as a separate flow with `FlowWorld.Current.Run(…)`.

### A finally entered before the cancel: Flow.NonCancelable

A `finally` entered by a `return`, by reaching the end of the try block, or by an exception is code of a scope that is still alive. The library can't tell whether an await belongs to the body or to the `finally`. So if a cancellation arrives during an await in such a `finally`, a `FlowCanceledException` is thrown there, the rest of the `finally` is skipped, and any exception that was in flight is replaced by it and lost.

Wrap the awaits you want to run to the end in `Flow.NonCancelable`.

```csharp
try
{
    await ShowResult();
}
finally
{
    // Entered by the return above. An ancestor's cancel does not reach these awaits.
    await Flow.NonCancelable(view.PlayClose());
    await Flow.NonCancelable(FlowTask.Race(Save(), FlowTask.WaitForSeconds(5)));   // gives up after 5 s
    view.Destroy();   // runs after them, even if canceled meanwhile
}
```

- A marked await starts the task, waits until it ends, and receives its result or exception, even if the scope has been canceled (before or during the await). The cancellation reaches the scope at the next unmarked await.
- An exception that was in flight is thrown again after the marked await. Because the scope ends by cancellation, that exception is reported as `Undelivered` ([Handling failures](failures.md)). It isn't lost.
- The mark only works when you await it directly inside a FlowTask method. Passing it to `Flow.Spawn`, `FlowWorld.Run`, or a combinator (`Race`, `WhenAll`) throws `FlowMisuseException`. To add a limit, put a Race inside the mark, as in the example above.
- Marked awaits also obey Pause and have no time limit. Don't use the mark on waits for input or for other flows; they would become impossible to cancel.
- In a `finally` or `catch` entered after a cancellation has arrived, you don't need the mark (adding it changes nothing).
- `World.Dispose` ends marked awaits too.
- Analyzer rule FLOW010 points out unmarked awaits in `finally` ([Analyzers](../tools/analyzers.md)). It also points them out in a `finally` that only a cancellation can enter, like `Dialog` above. The mark changes nothing there, so add it, or suppress the warning.

### Spawn in a canceled scope

In a scope whose cancellation has been decided, until the `FlowCanceledException` reaches your code, `Flow.Spawn` doesn't start the task and returns a handle that has already ended as `Canceled`. Code that follows a `Flow.NonCancelable` await also falls into this case, because the cancellation hasn't reached it yet. In a `finally` or `catch` after the cancellation has arrived, the task does start, and it unwinds when that scope ends.

## Keeping progress

Unwinding doesn't restore state. Progress you want to keep even after a cancellation should be written, each time you advance, to a place that outlives the scope (a `FlowProperty`, save data).

```csharp
async FlowTask Download(FlowProperty<int> done)
{
    for (var i = done.Value; i < chunkCount; i++)
    {
        await FetchChunk(i);
        done.Set(i + 1);   // kept when the download is canceled: the next run starts here
    }
}
```

If writing once at the end is enough, register it with `Flow.AddCleanup`.

## Writing catch

`FlowCanceledException` derives from `OperationCanceledException`. So a `catch` that catches cancellations in general also catches FlowTask's cancellation. A `catch` that receives a cancellation must always end with `throw;`.

```csharp
// OK: everything except the cancellation
catch (Exception e) when (e is not FlowCanceledException) { ShowError(e); }

// OK: cleanup that runs only on a cancel, then let it go on
catch (FlowCanceledException) { Undo(); throw; }

// OK: only an external cancellation (an HttpClient timeout, a canceled Task)
catch (OperationCanceledException e) when (e is not FlowCanceledException) { ShowTimeout(); }

// Wrong: swallows the flow's cancellation (FLOW001, an error)
catch (OperationCanceledException) { return; }
catch (Exception e) { Log(e); }
```

- Returning or moving on after receiving a cancellation is called swallowing it. The `catch (OperationCanceledException) { return; }` often written with UniTask is swallowing too. Analyzer rule FLOW001 stops it at compile time ([Analyzers](../tools/analyzers.md)).
- At run time, a scope that returned after receiving a cancellation is reported once as `SwallowedCancellation` and ends as canceled. The unwinding doesn't spread beyond that scope, and the caller continues.
- A swallowing `catch` inside a loop keeps looping, because awaits after the cancellation also run as cleanup awaits. It isn't reported until it returns.
- If you write `when (e is not OperationCanceledException)`, external cancellations (such as Task timeouts) also pass through the handler and become unhandled exceptions if nothing catches them. External cancellations arrive as exceptions, not as FlowTask cancellations ([Handling failures](failures.md)).
- The `CancellationToken` of a `FlowCanceledException` is `None`. `when (e.CancellationToken == token)` matches no token.
- Only the library can create a `FlowCanceledException`. Rethrow a cancellation you received only with `throw;` inside its `catch`. If you keep it and rethrow it in a scope that wasn't canceled, the scope ends with a `FlowMisuseException` that has it as its InnerException.

Work that must finish even when the flow is canceled goes in `finally`, awaited there. Work you want to abandon midway and switch away from goes in `FlowTask.Race` ([Composition](composition.md)).

## Rules and pitfalls

- `Cancel` on a finished task does nothing. `Cancel` can be called from any thread, so you can register it with an external `CancellationToken` via `ct.Register(handle.Cancel)` ([Task and ValueTask](../integrations/task.md)).
- Canceling throws one `FlowCanceledException` for each suspended async method. Leaf waits (`Next`, `WaitForSeconds`, `WaitUntil`, and so on) don't throw. Make Race branches that lose every frame leaf waits rather than async methods ([Performance](../advanced/performance.md)).
- If the debugger breaks on every `FlowCanceledException`, see [Debugging](../tools/debugging.md) for the settings.
- During `World.Dispose`, cleanup runs only up to its first await. How to close the World when some cleanup must run to the end is covered in [Handling failures](failures.md).
- To tie a flow to the lifetime of a Unity GameObject or a Godot node, see [Unity lifetime](../unity/lifetime.md) and [Godot lifetime](../godot/lifetime.md).
