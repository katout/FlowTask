# UniTask bridges and migration

UniTask and FlowTask can await each other through bridges. The first half of this page explains how to use the `FlowTask.UniTask` package. The second half covers how terms map and how behavior differs when you rewrite UniTask code with FlowTask. If you are migrating from UniTask, start with "Migrating from UniTask" in the second half.

## Installation

The UniTask bridge is a separate package from the core.

- **.NET / Godot**: reference `FlowTask.UniTask` from NuGet. The UniTask NuGet package (2.5.10 or later) comes in as a dependency.
- **Unity**: add `com.katout.flowtask.unitask` through UPM, and install UniTask (2.0.0 or later) separately from a git URL or OpenUPM. If you installed UniTask as a .unitypackage, add `FLOWTASK_UNITASK` to Scripting Define Symbols. Without it, the bridge is silently left out of compilation, and code that uses `FlowUniTask` fails with `CS0246`. From code with an asmdef, add `FlowTask.UniTask` and `UniTask` to its references.

The detailed steps are in [Installation](../getting-started/installation.md). The namespace is `Katout.FlowTask`, the same as the core.

## Awaiting a UniTask: FromUniTask and AsFlow

```csharp
async FlowTask<Texture2D> LoadIcon(string key)
{
    // The token is canceled when this scope is canceled.
    return await FlowUniTask.FromUniTask(ct => icons.Load(key, ct));
}
```

`FlowUniTask.FromUniTask` works the same as [`FlowBridge.FromTask` for Task](task.md), except that it takes no `onDiscard`.

- The factory is called when the bridge starts. The token it receives is canceled when the scope is canceled.
- Whatever thread the UniTask completes on, the flow resumes on the World's thread, at the next Tick or Flush.
- Exceptions are thrown at the await. A UniTask canceled outside the flow throws a plain `OperationCanceledException` (if you don't catch it, it becomes an unhandled exception).
- If it returns something that must be released (like an Addressables handle), turn it into a Task and bridge that, as in `FlowBridge.FromTask(ct => icons.Load(key, ct).AsTask(), onDiscard: …)`, passing [`onDiscard`](task.md); or release it in the flow that receives it.

Wrap a UniTask that is already running in `.AsFlow()`. Cancellation doesn't reach it. A UniTask can be awaited only once, so don't await the wrapped UniTask anywhere else.

```csharp
var loading = icons.Load(key, destroyToken);   // started elsewhere
var icon = await loading.AsFlow();
```

Writing `await uniTask` directly inside a FlowTask method is [FLOW002](../tools/analyzers.md) (an error). To pass a bridge with a result to a composition such as Race, turn it into a FlowTask with `.ToFlowTask()` (without it, you get a compile error).

```csharp
var r = await FlowTask.Race(FlowUniTask.FromUniTask(ct => icons.Load(key, ct)).ToFlowTask(), FlowTask.WaitForSeconds(5));
```

Exceptions and catching external cancellation work the same as for [Task bridges](task.md).

## Awaiting a flow from UniTask code: ToUniTask

Inside an `async UniTask` method, you can't await a FlowTask directly ([FLOW005](../tools/analyzers.md), an error). Start the flow with `FlowWorld.Run`, and turn the handle into a UniTask with `ToUniTask()`.

```csharp
async UniTask ShowIntro()
{
    await FlowTaskUnity.World.Run(Intro()).ToUniTask();
}
```

- The UniTask completes when the flow ends. If the flow succeeds, it returns the result; if the flow ends with cancellation, it throws `OperationCanceledException`; if the flow ends with an exception, it throws the original exception.
- If there is a `SynchronizationContext` when you call `ToUniTask()` (such as Unity's main thread), the completion is posted there. If not, it completes on the World's thread right after the Tick, Flush, Run, or Dispose in which the flow ended.
- Calling `ToUniTask()` doesn't use up the handle's `Join()`.

## Using them together on Unity

FlowTask and UniTask run on the same PlayerLoop. The order between UniTask continuations and FlowTask's Tick is described in [Unity bridges](../unity/bridges.md).

## Migrating from UniTask

From here on, this is a guide for rewriting code written with UniTask in FlowTask.

### Term mapping

| UniTask | FlowTask |
|---|---|
| Pass a `CancellationToken` as an argument | Don't pass one. The scope structure decides (the parent ending, losing a Race, `FlowHandle.Cancel()`) |
| `Forget()` | `FlowWorld.Run` (outlives the calling scope), `Flow.Spawn` (ends together with the calling scope) |
| `WhenAny` | `FlowTask.Race` (unwinds the losers) |
| `WhenAll` | `FlowTask.WhenAll` (when one fails, the rest are unwound) |
| `UniTask.Delay(milliseconds)` | `FlowTask.WaitForSeconds(seconds)` (advances on the scope's Clock) |
| `UniTask.Yield()`, `NextFrame()` | `FlowTask.NextFrame()` |
| `UniTask.DelayFrame(n)` | `FlowTask.DelayFrames(n)` |
| `UniTask.WaitUntil(condition)` | `FlowTask.WaitUntil(condition)` |
| `.Timeout(…)` | `FlowTask.Race(work, FlowTask.WaitForSeconds(t))` (you tell from the result's `Index`, not from an exception; the losing work is unwound) |
| `UniTaskCompletionSource<T>` | `Once<T>` (`Set` it once, on the World's thread; it can't be ended with a failure or a cancellation). To complete it from another thread, or with a failure, wait on a `TaskCompletionSource<T>` with `FlowBridge.FromTask(_ => tcs.Task)` |
| `AsyncReactiveProperty<T>` | `FlowProperty<T>` |
| `GetCancellationTokenOnDestroy()` | `gameObject.RunWhileActive(task)` (also stops on deactivation). To await it in a flow, `await gameObject.WhileActive(task)`. To stop only on destroy, `FlowTask.Race(task, gameObject.WaitForDestroy())`. On Godot, `node.RunWhileInTree(task)` |
| `button.OnClickAsync()` | `Next()` on `button.ClickedSignal()` |
| `catch (OperationCanceledException) { cleanup; }` | `finally { cleanup; }` |
| `SuppressCancellationThrow` | None. On the side being stopped, cancellation always unwinds with `FlowCanceledException`. The caller can tell without an exception, from the Race result (`Index`) or the handle's `Status` (for ways to make per-frame cancellation cheaper, see [Performance](../advanced/performance.md)) |
| `TaskPool.SetMaxPoolSize` | None (the pool limit is 1,024 per type) |

GameObject and node lifetimes are covered in [Unity lifetime](../unity/lifetime.md) and [Godot lifetime](../godot/lifetime.md), and signals in [Signals](../guide/signals.md).

### Differences in behavior

FlowTask looks like plain `async` / `await`, but it guarantees lifetime and order through structure, so it behaves differently from UniTask.

| Aspect | UniTask | FlowTask |
|---|---|---|
| Start | Starts the moment you call it | **Deferred start**. Starts when you await it, when you pass it to `Flow.Spawn` or `FlowWorld.Run`, or when a composition you passed it to starts. `DoThing();` alone does nothing (FLOW003) |
| `GetAwaiter()` | Starts nothing | Starts the task at the point you call it (FLOW007) |
| Number of awaits | Once (any number of times with `Preserve()`) | Once (a second await throws `FlowMisuseException`). To wait from several places, use `Once<T>` |
| Parent and child | None | Becomes a child of the scope that started it. When the parent returns, its children are unwound (the parent doesn't wait for them). To wait, use `handle.Join()` |
| Cancellation | You pass tokens by hand | Decided by structure. `FlowCanceledException` comes out of the suspended await |
| Work canceled externally | The caller is canceled too. In work you `Forget()`, the cancellation is silently dropped by default | Thrown as an exception at the bridge's await; if not caught, it is reported as an unhandled exception |
| Continuations | Sometimes run synchronously where the completion happens | Never resume synchronously. A completion schedules a resumption, which the World's flush processes in FIFO order |
| Time | Advances with `Time.deltaTime` by default, and follows `Time.timeScale`. Each wait can choose real time or no scaling with `DelayType` and similar options | The time of the scope's Clock. The Clock's Pause and scale apply to every wait in the scopes below it |
| Where you can await | Anywhere | Only inside FlowTask methods (FLOW005). To start one from outside, use `FlowWorld.Run` |
| External awaitables | Await them directly | Go through a bridge (FLOW002) |
| Threads | Any. You can move to another thread with `UniTask.RunOnThreadPool` or `SwitchToThreadPool` | A World and the objects it used are bound to the World's thread. Wait for work on another thread with `FlowBridge.FromTask(ct => Task.Run(…, ct))`; the flow resumes on the World's thread |
| Blocking | You can't block on an unfinished UniTask (`GetAwaiter().GetResult()` throws) | Don't block on the World's thread (Tick stops too). Read a finished flow's value with `handle.Result`. From outside a flow, await `AsTask()` or `ToUniTask()` |
| `AsyncLocal` | The builder doesn't capture the ExecutionContext; a value set is also visible to the caller and to code that runs later | The same. Pass per-flow values as arguments ([Execution model](../advanced/execution-model.md)) |

Deferred start and parent-child relationships are covered in [Flows and the World](../guide/flows-and-world.md), cancellation in [Scopes and cancellation](../guide/scopes-and-cancellation.md), and resumption order in [Execution model](../advanced/execution-model.md).

### Rewriting Forget

```csharp
// UniTask
SendAnalytics().Forget();

// FlowTask: outlives this scope
FlowWorld.Current.Run(SendAnalytics());

// FlowTask: ends with this scope
_ = Flow.Spawn(SendAnalytics());
```

- A flow started with `FlowWorld.Run` is a child of the World's root. It keeps running after the calling scope ends, and its exceptions reach `FlowWorld.OnUnhandledException`. Its Clock is the World's default Clock unless you pass one as an argument.
- A child from `Flow.Spawn` is unwound when the calling scope ends, even when the scope returns normally. The child's exceptions don't disappear silently; they reach the caller of the scope that spawned it.
- [FLOW008](../tools/analyzers.md) warns about a `Flow.Spawn(x);` statement that doesn't use the handle. If it's fine for the child to end with the scope, add `_ =`. If a child you meant to outlive its parent gets cut off when the parent ends, there is no warning at run time.
- If you call `Flow.Spawn` inside an event handler, it becomes a child of whichever flow was running when the event fired (outside a flow, it throws `FlowMisuseException`). From a handler, call `Run` on a World you saved inside a flow ([FLOW009](../tools/analyzers.md)).
- `Discard()` isn't Forget. It releases a FlowTask that hasn't started, without starting it.

### Cancellation and catch

In UniTask, it's common to catch cancellation with `catch (OperationCanceledException)` and clean up there. In FlowTask, write cleanup in `finally` or `using`.

```csharp
// UniTask
async UniTask Open(CancellationToken ct)
{
    await UniTask.Delay(500, cancellationToken: ct);
    try { await Show(ct); }
    catch (OperationCanceledException) { Cleanup(); throw; }
}

// FlowTask: no token, cleanup in finally, time on the scope's clock
async FlowTask Open()
{
    await FlowTask.WaitForSeconds(0.5);
    try { await Show(); }
    finally { Cleanup(); }
}
```

`FlowCanceledException` derives from `OperationCanceledException`. So the UniTask habit `catch (OperationCanceledException) { return; }` also swallows FlowTask's cancellation. [FLOW001](../tools/analyzers.md) (an error) stops this.

- To catch every exception, write `catch (Exception e) when (e is not FlowCanceledException)`.
- To catch only external cancellation (such as an HttpClient timeout), write `catch (OperationCanceledException e) when (e is not FlowCanceledException)`.
- The `CancellationToken` of a `FlowCanceledException` is `None`. `when (e.CancellationToken == token)` doesn't match any token.

See [Handling failures](../guide/failures.md) for details.

### Await inside finally

As in UniTask, you can await inside `finally`. Awaits in a `finally` entered because of cancellation run to the end, and the caller (the parent, Race, WhenAll) waits for them to finish.

What's different is a `finally` entered before the cancellation (through `return` or an exception). If a cancellation arrives during an await there, the await throws `FlowCanceledException`, and the rest of the block doesn't run. Wrap cleanup that must run to the end in `Flow.NonCancelable` ([FLOW010](../tools/analyzers.md)).

```csharp
finally
{
    // Runs on a cancel too; gives up after 5 seconds.
    await Flow.NonCancelable(FlowTask.Race(Save(), FlowTask.WaitForSeconds(5)));
}
```

Awaits in cleanup have no time limit. If you need one, use a Race as above. See [Scopes and cancellation](../guide/scopes-and-cancellation.md) for details.

### Rewriting WhenAny

`FlowTask.Race` unwinds the losing branches (running their `using` and `finally`) before it resumes the caller. To keep the losing work going, start it with `Flow.Spawn` and use the handle's `Join()` as the branch.

```csharp
// Stop the losers: the other branch is unwound before the caller resumes.
var r = await FlowTask.Race(Download(), cancelButton.Next());

// Keep the losers: only the losing Join is unwound; the spawned work goes on.
var a = Flow.Spawn(LoadA());
var b = Flow.Spawn(LoadB());
var first = await FlowTask.Race(a.Join(), b.Join());
```

- Spawned work is also unwound when the calling scope ends. To keep it going longer than the scope, start it with `FlowWorld.Run`.
- Race starts its branches in the order you write them, and a branch that completes synchronously wins at once. Write interrupts (waiting for input) first.

Race and WhenAll are covered in detail in [Composition](../guide/composition.md).

### Exceptions

As in UniTask, an exception from an awaited child is rethrown at that await. The differences are:

- An exception whose receiver is gone (an awaiter that was canceled, a Race that already has a winner) isn't rethrown. It is reported to `OnUnhandledException` as `FlowExceptionKind.Undelivered`.
- The stack trace contains only the place where it was first thrown and the place where it was caught. Look at `FlowExceptionInfo.ScopePath` for the steps in between.
- An exception nobody caught anywhere reaches the root's `FlowWorld.OnUnhandledException`. In a World without `OnUnhandledException`, the outermost `Tick`, `Flush`, `Run` or `Dispose` throws `FlowUnhandledException`.
- `handle.Join()` throws `FlowJoinException` when the other flow ends with cancellation or an exception. If you don't care how it ends, wait with `await FlowTask.WaitUntil(handle, h => h.IsCompleted)` and check `Status`.

### Threads

UniTask can be used from any thread, but a FlowTask World is bound to the thread that created it. Signals and FlowProperties are also bound to the thread of the first World that used them.

```csharp
// In an async Task / UniTask method: Set runs on a pool thread. Once a World has used the property,
// Set throws FlowThreadException there, and this await rethrows it.
await Task.Run(() => property.Set(Load()));

// In a FlowTask: load on a worker thread, then write on the World's thread.
var loaded = await FlowBridge.FromTask(ct => Task.Run(() => Load(), ct));
property.Set(loaded);
```

The APIs you can call from another thread are `EmitFromAnyThread`, `CloseFromAnyThread`, `FlowWorld.Post`, and `FlowHandle.Cancel`. See [Threads](../guide/threads.md) for details.

### Naming

This documentation and the samples don't add `Async` to methods that return FlowTask. In game flows, almost every method returns FlowTask, so the suffix carries no information. [FLOW003](../tools/analyzers.md) catches a missing await. How to turn off analyzers that require `Async` is described in [Analyzers](../tools/analyzers.md).
