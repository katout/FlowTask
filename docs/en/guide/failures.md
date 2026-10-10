# Handling failures

Failures in FlowTask go through two paths: cancellation and exceptions. This page explains how to write each kind of failure, where exceptions go, exceptions that nothing caught (unhandled exceptions) and `OnUnhandledException`, misuse the library detects, and cleanup when you close the World.

## Two paths

| Kind | How it happens | Scopes along the way | Where it ends up |
|---|---|---|---|
| Cancellation | The parent ends, a Race is lost, `handle.Cancel()` | Unwind with `FlowCanceledException` | Not delivered as an exception. You can tell from the handle's `Status` and `CancelCause`, the `FlowJoinException` of `Join`, and the Canceled `AsTask()` Task |
| Exception | `throw`, a failure in an awaited child or bridge, misuse detected by the library | Climbs the chain of awaits like an ordinary exception | The awaiting caller. If nothing catches it, the World's `OnUnhandledException` |

Cancellation is explained in [Scopes and cancellation](scopes-and-cancellation.md). This page covers exceptions. You can also return an expected failure as a return value; a return value is an ordinary value that goes through neither path.

### Which one to use

1. **Bugs on the calling side** (invalid arguments, broken invariants, null references): throw an exception. If nothing catches it, it becomes an unhandled exception.
2. **Expected failures that the caller handles right away** (network failures, failed purchases): throw a dedicated exception type and `catch` it in the caller, or return a value that describes the outcome (see "Expected failures" below).
3. **Interruptions that abort a whole screen or stage of progress, where the outer code decides where to go back to** (maintenance, an expired session): throw a dedicated exception type, and `catch` that type where you want to return to.
4. **The user interrupting** (the back key, a cancel button): `FlowTask.Race` the work being interrupted against that input. The losing branch unwinds ([Composition](composition.md)).
5. **Exceptions coming from outside** (Task, SDKs): `catch` them where they are awaited, and turn them into the form of item 2 if needed.

## Exceptions

An awaited child's exception is rethrown at that await, just as with Task. If nothing catches it, it climbs the chain of awaits, and each scope along the way ends with that exception (`FlowStatus.Faulted`). If nothing catches it anywhere, it reaches the World's `OnUnhandledException`.

```csharp
async FlowTask ShopScreen()
{
    while (true)
    {
        var item = await buyClicked.Next();
        try
        {
            await Purchase(item);
        }
        catch (Exception e) when (e is not FlowCanceledException)
        {
            ShowError(e);   // a bug in the store SDK ends this purchase, not the screen
        }
    }
}
```

- If you wrap work started from a button in `try` / `catch`, a single SDK bug won't stop the screen or the whole game.
- Always add `when (e is not FlowCanceledException)` to a `catch` that catches everything. Without it, you swallow the cancellation (FLOW001; [Scopes and cancellation](scopes-and-cancellation.md)).
- When a branch of `FlowTask.Race` or `FlowTask.WhenAll` ends with an exception, the combinator finishes unwinding the other branches (including their cleanup) and then rethrows that exception.
- Failures are delivered through the same resume queue as value completions, so they obey Pause too.
- The stack trace of an exception rethrown at an await contains only the place it was first thrown and the place it was caught. See the steps in between with `FlowExceptionInfo.ScopePath` ([Debugging](../tools/debugging.md)).

### Expected failures

```csharp
sealed class ApiException : Exception
{
    public ApiException(ApiError error, Exception inner) : base(error.ToString(), inner) => Error = error;
    public ApiError Error { get; }
}

async FlowTask<Stage> FetchStage()
{
    string json;
    try
    {
        json = await FlowBridge.FromTask(ct => http.GetStage(ct));
    }
    catch (HttpRequestException e)
    {
        throw new ApiException(ApiError.Network, e);
    }
    catch (OperationCanceledException e) when (e is not FlowCanceledException)
    {
        throw new ApiException(ApiError.Timeout, e); // HttpClient's timeout, or a source canceled outside the flow
    }

    return Parse(json);
}

// the caller
try
{
    ShowStage(await FetchStage());
}
catch (ApiException e)
{
    ShowError(e.Error);
}
```

- Turn the external exception into a type that describes the failure of that call, and throw it. The caller catches only that type. SDK bugs (`NullReferenceException` and the like) pass through unchanged.
- With only `catch (HttpRequestException)`, an HttpClient timeout (`TaskCanceledException`) isn't caught and becomes an unhandled exception. For external cancellations, see the "Tasks canceled from outside" section below.
- If the caller branches on the failure every time, you can return a value that describes the outcome instead of throwing (an `enum`, a type holding the value and the reason for the failure, or the Result type you already use). A return value is an ordinary value, so the rules in "Exceptions that can't be delivered" below don't affect it.

To run several operations side by side and stop the rest at the first failure, throw the failure as an exception and wait with `FlowTask.WhenAll` ([Composition](composition.md)). To let the rest run to the end when one fails and collect the outcome of each branch, `catch` the exception inside each branch and return the outcome as a value.

### Aborting and going back to a return point

```csharp
sealed class SessionExpiredException : Exception { }

async FlowTask Game()
{
    while (true)
    {
        await Title();
        try
        {
            await InGame();
        }
        catch (SessionExpiredException)
        {
            await ShowNotice("Session expired");
        }
    }
}
```

- The throwing side doesn't resume. Scopes along the way end with the exception, and `using` and `finally` always run.
- A catch-all along the way (`catch (Exception e) when (e is not FlowCanceledException)`) also catches this exception. A catch-all inside the return point should log and `throw;`, or exclude the type with `when`.
- For an interruption that happens independently of any await (such as a session-expired notification), Race near the return point and turn it into an exception.

### Exceptions from spawned children

When a child started with `Flow.Spawn` ends with an exception, the scope that spawned it (the owner) is canceled (`CancelCause.Fault`). After the owner has unwound, the exception is rethrown at the caller awaiting the owner.

```csharp
async FlowTask Battle()
{
    _ = Flow.Spawn(EnemyWave());   // if EnemyWave throws, Battle is unwound...
    await PlayerTurn();
}

async FlowTask InGame()
{
    try { await Battle(); }
    catch (WaveException) { ShowWaveError(); }   // ...and the exception arrives here
}
```

- The owner's `catch` receives a `FlowCanceledException`, and its `finally` runs.
- The child's exception doesn't reach a `catch` in the same method that surrounds the Spawn. Extract the range where you want to receive it into its own method.
- An exception from a root flow started with `FlowWorld.Run` goes to `OnUnhandledException`. For a child that shouldn't drag its owner down, use `try` / `catch` inside the child's method, or start it with `FlowWorld.Run`.
- The child's handle also holds the exception (`Exception`; the `AsTask()` Task is Faulted with that exception).

## OnUnhandledException and unhandled exceptions

An exception that nothing caught and that reached the World's root is called an unhandled exception. Unhandled exceptions are reported to `FlowWorld.OnUnhandledException` as `FlowExceptionInfo`.

```csharp
world.OnUnhandledException = info =>
{
    if (info.Kind == FlowExceptionKind.Undelivered) Log.Warning(info.ToString());
    else Log.Error(info.ToString());   // "[Unhandled] at Game > InGame > Battle: System.InvalidOperationException: ..."
};
```

`FlowExceptionInfo` has `Exception`, `ScopePath` (the path from the root at the time the exception was thrown, in the form `Game > InGame > Battle`), and `Kind`.

| `FlowExceptionKind` | What it is |
|---|---|
| `Unhandled` | An exception that nothing caught: an exception that ended a flow started with `FlowWorld.Run`, or an exception from code outside flows (`Post`ed work, handlers) |
| `Cleanup` | An exception in cleanup: a new exception thrown by a canceled scope's `catch` or `finally`, or an exception from `AddCleanup`, the Dispose of `Own`, or `onDiscard`. The rest of the cleanup goes on |
| `SwallowedCancellation` | A canceled scope returned after receiving the cancellation (swallowed it). The scope ends as canceled |
| `Undelivered` | An exception that couldn't be delivered to its receiver (see the section below) |

Kinds other than `Unhandled` are report-only and don't change the flow of control.

- In a World without `OnUnhandledException`, the reports are thrown as a `FlowUnhandledException` (listed in `ExceptionInfos`) from the outermost `Tick`, `Flush`, `Run`, or `Dispose`, after it has finished its work. An exception thrown by the handler is also thrown in the same way, together with the report it was given.
- Reports are delivered immediately, in the order they are found. So a cleanup failure (`Cleanup`) can arrive before the exception that caused it.
- `OnUnhandledException` and `OnWarning` handlers are called in the middle of the scheduler (while it ends a scope or unwinds one). A flow you start or cancel there runs in the middle of that work, so keep handlers to logging and counting. To respond with a flow, such as showing an error screen, hand it to the next Tick or Flush intake with `world.Post(() => world.Run(ShowError()))`, or tell a flow that waits for it through a Signal. You can't call `Tick`, `Flush`, or `Dispose`.
- The Unity and Godot integrations set a default handler that logs `Undelivered` as a warning and everything else as an error.
- `FlowHandle.Exception` is the exception that ended the task. It has a value only when the task ended with an exception (`Faulted`). The path where the exception was thrown, and the report-only kinds, come through `OnUnhandledException`.

### Exceptions that can't be delivered (Undelivered)

Sometimes the party that should receive an exception has already been canceled, or has already settled. Such an exception isn't carried; it's reported to `OnUnhandledException` as `Undelivered`, and the outcome doesn't change.

| Situation | What happens |
|---|---|
| A child's exception arrived at a waiter whose cancellation has been decided (but that hasn't started unwinding yet). For example, a screen that failed while paused and was then closed, `World.Dispose`, or a Cancel that arrived in the same intake | The waiter gets only a `FlowCanceledException` (the `catch` never sees the exception). The scope ends as canceled |
| An exception received by a cleanup await in a canceled scope left the scope uncaught | The scope ends as canceled |
| An in-flight exception rethrown by a `finally` entered before the cancel, after its `Flow.NonCancelable` await | The scope ends as canceled |
| An exception that arrived later at a Race that already has a winner, or at a WhenAll already settled by a failure | Doesn't change the outcome |
| The second and later exceptions (from siblings after a Race or WhenAll settled on the first exception) | The main one is whichever was processed first |
| An exception from a spawned child of an owner that is finishing or was canceled | The child's handle holds the exception |
| A Task that failed after the bridge stopped waiting for it | The exception is marked as observed, so it doesn't become an `UnobservedTaskException` |

`Undelivered` happens in normal play too (two network calls failing in the same frame, for example). If an expected failure becoming `Undelivered` instead of reaching your `catch` is a problem, return that failure as a value instead of throwing it (how to stop operations run side by side at the first failure is in "Things written by combination" in [Design rationale](../advanced/design-rationale.md)).

## Tasks canceled from outside

When a bridged Task is canceled from outside (an HttpClient timeout, a `CancellationToken` passed in from outside, a UniTask cancellation), it isn't a flow cancellation. It's an ordinary exception thrown from the bridge's await. If nothing catches it, it's an unhandled exception. Catch it with `catch (OperationCanceledException e) when (e is not FlowCanceledException)`, as in `FetchStage` above.

When the scope itself is canceled, the bridge unwinds without reading the result, so this `catch` isn't reached. Details, such as cancellations that `catch (TaskCanceledException)` can't catch, are in [Task and ValueTask](../integrations/task.md).

## Misuse the library detects

Misuse the library detects at run time becomes an ordinary exception of the scope, `FlowMisuseException`. It's thrown from the waiter's await, and you can catch it with `catch`.

| Misuse | What happens | How to fix |
|---|---|---|
| A FlowTask method suspended on an external awaitable that wasn't bridged (Task, UniTask, Unity's `Awaitable`, Godot's `SignalAwaiter`, `Task.Yield()`, and so on) | The scope ends immediately. The remaining `finally` and `using` don't run (`AddCleanup` and `Own` do) | Go through `.AsFlow()` or `FlowBridge.FromTask`. Replace `Task.Yield()` with `FlowTask.NextFrame()`, and `Task.Delay` with `FlowTask.WaitForSeconds` (FLOW002) |
| An async method that isn't a FlowTask method (`async Task`, UniTask, `async void`) awaited a FlowTask and suspended | That method never resumes, and its Task never completes. The calling scope unwinds and then ends with this exception | Make the method return FlowTask. To wait from outside a flow, use `world.Run(task).AsTask()` (FLOW005) |
| The same FlowTask was started twice (awaited twice, passed to two combinators), or awaited outside a flow | That await throws | [Flows and the World](flows-and-world.md), [Composition](composition.md) |
| One scope completed more than 1,000,000 awaits synchronously in one Tick, Flush, or Run | Treated as an infinite loop; the scope ends at that await. Awaits of already completed values (`FlowTask.FromResult`, `CompletedTask`) aren't counted, so a loop that only spins on those never returns from Tick, Flush, or Run | Put `await FlowTask.NextFrame()` in loops that wait |
| You kept a `FlowCanceledException` you received and rethrew it in a scope that wasn't canceled (only the library can create this exception) | The scope ends with an exception that has it as its InnerException | Rethrow a cancellation only with `throw;` inside the `catch` that received it |

In addition, when a subscription with `BufferOverflow.Fail` overflows, the scope ends with `SubscriptionOverflowException` ([Signals](signals.md)). Use from another thread is `FlowThreadException` (derived from `FlowMisuseException`) ([Threads](threads.md)).

> **Note**: A completed Task (such as a fake `Task.FromResult` in a test) doesn't suspend even if you await it directly, so it isn't misuse at run time. It passes in tests and throws only in production. Wait on fake Tasks through a bridge too. FLOW002 stops both forms at compile time.

Messages of common exceptions and how to fix them are in [Debugging](../tools/debugging.md).

## Finishing cleanup before closing the World

`World.Dispose` cancels every flow and unwinds them in one pass. Dispose doesn't run Ticks, so awaits in the `finally` and `catch` of canceled scopes (including `Flow.NonCancelable` awaits) throw `FlowCanceledException` again, and each block runs only up to its first await. Outer `finally`, `using`, `AddCleanup`, and `Own` do run. When cleanup awaits are cut off, a `CleanupCutAtDispose` warning is raised.

If some cleanup must run to the end, such as saving, cancel the flows before Dispose, run Ticks with a limit, and Dispose after the flows have ended.

```csharp
void CloseWorld(FlowWorld world, FlowHandle game)
{
    game.Cancel();
    // Tick until the cleanup has ended, for 2 seconds at most.
    for (var i = 0; i < 120 && game.Status == FlowStatus.Running; i++) world.Tick(1.0 / 60);
    world.Dispose();   // cuts what is still running, and warns (CleanupCutAtDispose)
}
```

- `FlowWorld.Run` called from code running during Dispose (`finally`, `AddCleanup`, `Own`, handlers) doesn't start the task and returns a handle that has already ended as `Canceled`. `Flow.Spawn` doesn't start the task either.
- During Dispose, swallowed cancellations aren't reported.
- Anything sent from another thread after Dispose is dropped.
- How to write this for engine shutdown and scene changes is in [Unity setup](../unity/setup.md) and [Godot setup](../godot/setup.md).

## In depth: fine points of exception handling

These are rules you normally don't need to think about. You can avoid most of them by not throwing exceptions in cleanup.

- An exception thrown by the `finally` of a live scope replaces the exception in progress (a C# rule).
- During `World.Dispose`, an exception thrown by the `catch` or `finally` of a canceled scope is replaced by the `FlowCanceledException` that an outer `finally`'s await throws again, and isn't reported (a C# rule).
- In the cleanup of a canceled scope, an exception you throw yourself is reported as `Cleanup`, and an exception received from an await is reported as `Undelivered`. Just extracting a method can change the kind.
- If an owner canceled by a spawned child's failure exits with an exception before the `FlowCanceledException` reaches its code, that exception becomes the main one, and the child's exception becomes `Undelivered`. An exception thrown by cleanup after the cancellation has arrived is reported as `Cleanup`, and the child's exception is the main one.
- A Cancel that arrived in the same intake takes effect before a failure that arrived earlier (this happens with a Cancel from another thread).
- An exception that went through a Task is reported with the path of the bridge in the flow that received it. If a flow's `AsTask()` is awaited by another flow through a bridge and neither catches the exception, the same exception is reported twice. Have flows wait on each other with `Join`.
