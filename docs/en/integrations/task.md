# Task / ValueTask bridges

Work that returns a `Task`, like reading a file or a network call, is awaited from a flow through a bridge. This page explains how to use `FlowBridge.FromTask` and `.AsFlow()`, how exceptions and external cancellation are handled, how to clean up results nobody received, and `AsTask()` for awaiting a result from outside a flow.

## Awaiting external work: FromTask

```csharp
async FlowTask<Stage> LoadStage(string path)
{
    // The token is canceled when this scope is canceled.
    var json = await FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct));
    return Stage.Parse(json);
}
```

You pass `FlowBridge.FromTask` a factory that takes a `CancellationToken` and returns a `Task`.

- Like any other FlowTask, the factory is called when the bridge starts (when you await it, or when a composition you passed it to starts).
- The token it receives is canceled when the scope is canceled. The cancellation reaches the external work both when the bridge loses a Race and when its parent ends.
- Whatever thread the Task completes on, the flow resumes on the World's thread, at the next Tick or Flush. To run heavy computation on another thread, write `FlowBridge.FromTask(ct => Task.Run(() => Work(), ct))` and wait on it the same way ([Threads](../guide/threads.md)).

A `Task` with no result works the same way (`FlowBridge.FromTask(ct => SaveAsync(data, ct))`).

## Awaiting a running Task: AsFlow

Wrap a `Task` or `ValueTask` that has already started in `.AsFlow()` and await it.

```csharp
var download = http.GetStringAsync(url);   // started elsewhere, without the scope's token
var body = await download.AsFlow();
```

`.AsFlow()` doesn't pass cancellation through. When the scope is canceled, the flow stops waiting and unwinds, but the Task keeps running. If the work can take a token, use `FromTask`.

## Don't await directly

Inside a FlowTask method, don't await an external awaitable directly.

```csharp
async FlowTask LoadStage()
{
    var json = await File.ReadAllTextAsync(path);   // FLOW002: not bridged
    await Task.Delay(500);                          // FLOW002: ignores the World's time and Pause
}
```

While a flow waits on an external awaitable, FlowTask can neither cancel nor unwind its scope. So if the flow suspends there, the scope ends at that point with a `FlowMisuseException`.

- This covers `Task`, `ValueTask`, UniTask, Unity's `Awaitable` and `AsyncOperation`, Godot's `SignalAwaiter`, `Task.Yield()`, the result of `ConfigureAwait(...)`, and `await foreach`.
- The rest of the scope's `finally` blocks and `using` statements don't run. Only cleanup from `Flow.AddCleanup` and `Flow.Own` runs.
- The exception is thrown at the awaiter's await, and you can catch it with `catch`. The message, such as `FlowTask method '…' suspended on TaskAwaiter<Int32>, which is not a FlowTask`, shows the awaiter type, the scope path, and the bridge to use.
- An await that completes immediately doesn't suspend, so it isn't caught at run time. It can pass in tests with an already-completed fake Task and fail only in production.

[FLOW002](../tools/analyzers.md) (an error) stops this at compile time. Its code fix rewrites the await to `FromTask` or `.AsFlow()`. Use `FlowTask.WaitForSeconds` instead of `Task.Delay`, and `FlowTask.NextFrame()` instead of `Task.Yield()` ([Time and Clocks](../guide/time-and-clocks.md)).

Bridges for Unity and Godot awaitables are in [Unity bridges](../unity/bridges.md) and [Godot signals](../godot/signals.md).

## Exceptions and external cancellation

An exception from a bridged Task is thrown at its await. You can catch it with an ordinary `try` / `catch`.

```csharp
try
{
    var save = await FlowBridge.FromTask(ct => cloud.Download(ct));
    Apply(save);
}
catch (HttpRequestException e)
{
    await ShowError(e.Message);
}
catch (OperationCanceledException e) when (e is not FlowCanceledException)
{
    // HttpClient's timeout: the Task was canceled outside the flow.
    await ShowError("Timed out");
}
```

A Task canceled outside the flow (an HttpClient timeout, a `CancellationToken` passed in from outside) arrives as an ordinary exception, not as a cancellation of the flow. If you don't catch it, it becomes an unhandled exception.

- When the scope itself is canceled, the bridge unwinds without reading the result. An `OperationCanceledException` reaches the bridge's await only when the cancellation came from outside the scope.
- To catch only external cancellation, write `catch (OperationCanceledException e) when (e is not FlowCanceledException)`. A `catch (OperationCanceledException)` without a filter also swallows the flow's own cancellation ([FLOW001](../tools/analyzers.md)).
- `catch (TaskCanceledException)` is sometimes not enough. Cancellation from UniTask and `ThrowIfCancellationRequested` is a plain `OperationCanceledException`.
- When an exception from a Task is reported to `OnUnhandledException`, its `ScopePath` is the path of the place where it was bridged.

How to turn an external exception into an expected failure that the caller handles is covered in [Handling failures](../guide/failures.md).

## Cleaning up results nobody received: onDiscard

For work that returns something that needs releasing, like an Addressables handle, pass `onDiscard`.

```csharp
var boss = await FlowBridge.FromTask(
    ct => assets.Load("boss", ct),
    onDiscard: a => a.Release());   // called only for a result no flow received
```

`onDiscard` receives, once, a successful result that no flow received.

- It is called for: a result that arrives after the bridge was canceled, a result nobody took because the awaiter unwound, a result that lost a Race in the same flush, a result inside a WhenAll that failed, and a result ignored with `WithoutResult()`.
- A value a flow received with await belongs to that flow. Even if the flow throws it away later, `onDiscard` isn't called.
- A bridge's value passed to Race or WhenAll counts as received once the composition receives it. Even if it is thrown away before the composition's result reaches a flow (for example, because it lost an outer Race), `onDiscard` isn't called.
- A bridge started with `Flow.Spawn` or `FlowWorld.Run` keeps its value for the handle (its `Result` can be read at any time), so `onDiscard` isn't called for it.
- It is called on the World's thread, while taking in completions at the next Tick or Flush, or inside `FlowWorld.Dispose`.
- However, if the Task finishes after the World has been disposed, it is called right away on the thread that completed the Task (usually a thread pool thread). If you call engine APIs there, move back to the main thread first. In this case, an exception thrown by `onDiscard` is dropped without being reported.
- Otherwise, an exception thrown by `onDiscard` is reported as `FlowExceptionKind.Cleanup`.

For a bridge without `onDiscard`, a result nobody received is dropped without a report, the same as the result of a Task nobody awaits.

If the Task fails after the bridge has stopped waiting, the exception is reported as `FlowExceptionKind.Undelivered` (the exception is marked as observed and doesn't become an `UnobservedTaskException`). If it ends with cancellation, nothing is reported.

## Passing bridges to compositions: cancel buttons and timeouts

You can also pass bridges to Race and WhenAll. Turn a bridge with a result into a `FlowTask<T>` with `.ToFlowTask()` before passing it. Without it, the type argument can't be inferred and you get a compile error (CS1503). A bridge with no result can be passed as is.

```csharp
var r = await FlowTask.Race(
    cancelButton.Next(),
    FlowBridge.FromTask(ct => assets.Load("boss", ct), onDiscard: a => a.Release()).ToFlowTask());
if (!r.TryGet1(out var boss)) return;   // canceled: ct was canceled, and a result that still arrives goes to onDiscard
```

- The token of a bridge that loses a Race is canceled. If the work takes the token, it stops there.
- Work that can't be stopped (such as a load that doesn't take a token) runs to the end. No flow receives the result that arrives, so it goes to `onDiscard` (see "Cleaning up results nobody received" above).

You can also set a time limit with Race.

```csharp
var r = await FlowTask.Race(
    FlowBridge.FromTask(ct => http.GetStage(ct)).ToFlowTask(),
    FlowTask.WaitForSeconds(10));
if (!r.TryGet0(out var json)) return ApiError.Timeout;   // the request was canceled through its token
```

`WaitForSeconds` advances on the scope's Clock, so the limit also stops while the game is paused. To limit a network call in real time, use the external side's own mechanism (`HttpClient.Timeout`, `CancellationTokenSource.CancelAfter`). In that case, the cancellation arrives as an exception (see "Exceptions and external cancellation" above). Using compositions is covered in [Composition](../guide/composition.md).

## Stopping a flow with an external token

To stop a flow with a `CancellationToken` passed in from outside, register the handle's `Cancel`. You can call `Cancel` from any thread.

```csharp
async Task<Save> Download(CancellationToken token)
{
    var handle = world.Run(DownloadSave());
    using var registration = token.Register(handle.Cancel);
    return await handle.AsTask();   // canceled when the flow is canceled
}
```

A `Cancel` from another thread takes effect at the next Tick or Flush. Until then, `Status` doesn't change. If the World has been disposed, or the flow has finished, it does nothing.

## Awaiting a result from outside a flow: AsTask

Don't await a FlowTask inside a method that returns `Task` ([FLOW005](../tools/analyzers.md), an error). Start the flow with `FlowWorld.Run` and await the handle's `AsTask()`.

```csharp
Task<int> GetScore() => world.Run(Arcade()).AsTask();
```

- The Task completes on the World's thread, right after the Tick, Flush, Run, or Dispose in which the flow ended. If the flow is Succeeded, the Task is RanToCompletion; if Canceled, Canceled; if Faulted, Faulted with the original exception.
- The Task's continuation runs synchronously on the World's thread. That's also true for code that does `await handle.AsTask()` on a background thread, even with `ConfigureAwait(false)`. Move heavy work elsewhere with `Task.Run` or similar.
- Inside the continuation, don't wait for another `AsTask()` with `.Result` or `.Wait()`, and don't spin Tick to wait for it. A Tick inside the continuation doesn't complete AsTask, so it never finishes. Check the handle's `Status` or `IsCompleted` instead.
- If a flow or one of its descendants bridges and awaits the flow's own `AsTask()`, it never finishes, because a flow ends only after its children end. This isn't detected.
- Calling `AsTask()` doesn't use up the handle's `Join()`. You can call it any number of times.
- Call `AsTask()` on the World's thread (from another thread it throws `FlowThreadException`).

The Task completes at the end of the World's Tick or Flush, so it doesn't complete unless code keeps the World going. On Unity and Godot, the integration ticks every frame. In a console program, you tick it from your own loop ([Your first flow](../getting-started/first-flow.md)).

On the World's thread, don't block on this Task with `.Result` or `.Wait()`. Tick stops too, so it never finishes. You can read the value of a finished flow with `handle.Result`. From another thread, you can block on a Task you got on the World's thread ([Threads](../guide/threads.md)).

## Flows await each other with Join

Flows await each other with the handle's `Join()`, without going through a Task.

```csharp
var loading = Flow.Spawn(LoadAssets());
await PlayIntro();
await loading.Join();
```

However, you can't use `Join` when the other World is on a different thread (the await fails with `FlowThreadException`). Get `AsTask()` on the other World's thread, and await it through a bridge.

```csharp
// On the other World's thread:
Task<Map> mapTask = otherWorld.Run(BuildMap()).AsTask();

// In a flow of this World:
var map = await FlowBridge.FromTask(_ => mapTask);
```

Don't connect flows on the same thread this way. If the other flow fails and the awaiting side doesn't catch the exception either, the same exception is reported twice (once for the other flow, and once for the awaiting side).

## Details

- Each bridge allocates. For per-frame waits, use FlowTask's own waits (`WaitForSeconds`, `NextFrame`, signals) ([Performance](../advanced/performance.md)).
- On Unity, if you await inside the factory without `ConfigureAwait(false)`, the continuation goes through `UnitySynchronizationContext` ([Unity bridges](../unity/bridges.md)).
- Notes on making fake Tasks in tests are in [Testing](../tools/testing.md).
- For UniTask, see the [UniTask](unitask.md) page; for R3, see the [R3](r3.md) page.
