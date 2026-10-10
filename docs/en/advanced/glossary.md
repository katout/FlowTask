# Glossary

This page lists FlowTask's API names and the terms used in this documentation, alongside the closest terms in Task, UniTask, and R3. The closest terms are hints for migrating, and don't always mean the same thing. Differences from Task and UniTask are covered in [UniTask](../integrations/unitask.md) and [Task and ValueTask](../integrations/task.md).

## How this documentation writes terms

- API and type names are written in code format. World, Clock, and Emit are capitalized when they mean FlowTask's concepts.
- **Unwinding** means throwing `FlowCanceledException` from a suspended await and exiting the method while running `finally`, `using`, and cleanup (stack unwinding). It does not mean restoring game state (rollback).
- "Scope" means only a FlowTask scope. It is unrelated to VContainer's `LifetimeScope`.
- Sending a value to a Signal is called an Emit. "Raise" and "fire" are used for .NET and Godot events. Godot's `signal` is called a "Godot signal".
- "Held" means set aside to be processed later (like resumes during a Pause). It doesn't mean a task that hasn't started yet (`FlowStatus.Unstarted`).

## Units of execution

| Term | Meaning | Closest equivalent |
|---|---|---|
| `FlowTask`, `FlowTask<T>` (task) | Deferred async work. Starts when awaited or started, and can be awaited only once | `Task`, `UniTask` |
| FlowTask method | An async method, local function, or lambda that returns a FlowTask | async UniTask method |
| Flow | A running FlowTask and its tree of descendants. One started with `FlowWorld.Run` is a root flow | — |
| Scope | One run of a FlowTask method. It becomes a child of the scope it was started from, forming a tree | Parent and child `CancellationTokenSource`s |
| `FlowWorld` (World) | The unit of execution that owns the scope tree, Clocks, and the resume queue. Ticked every frame | UniTask's PlayerLoop |
| Tick | Advances time and resumes satisfied waits | A PlayerLoop update |
| Flush, `Flush()` | A flush processes the reserved resumes in FIFO order. `Flush()` does only the intake and the flush, without advancing time | — |
| Flush point | A place where an engine integration calls `Flush()` besides the Tick. On Unity, the places set in the PlayerLoop (`FlushPoints`); on Godot, the end of the frame. The integration also flushes when a GameObject or node tied to a flow's lifetime is disabled or leaves the tree | — |
| Reserved resume | A completion or Emit doesn't resume the waiting flow immediately; it puts the resume in a queue | — |
| Inbox | The queue that holds messages from other threads (`EmitFromAnyThread`, `Post`, `Cancel`, bridge completions), taken in at the start of the next Tick or Flush | `SynchronizationContext.Post` |
| Binding | A Signal, FlowProperty, or Once becoming tied to the thread of the first World that uses it | — |

## Starting and lifetime

| Term | Meaning | Closest equivalent |
|---|---|---|
| `Flow.Spawn` (Spawn) | Starts a task as a child of the current scope. It unwinds when the parent ends, and its exceptions go to the parent's caller | — |
| `FlowWorld.Run` (root flow) | Runs a task independently of the calling scope. Its exceptions go to `OnUnhandledException` | `Forget()`, `UniTask.Void` |
| `Discard()` | Releases a task without starting it | Not `Forget()` |
| `FlowHandle` (handle) | The state, result, cancellation, and Join of a started task | Keeping a `Task` in a variable |
| `Join()` | Waits for the other flow to end. If it ends by cancellation or an exception, throws `FlowJoinException` | `await task` |
| `Flow.AddCleanup` (registering cleanup) | Registers work that runs when the scope ends | `CancellationToken.Register` |
| `Flow.Own` (owning) | Disposes what you pass when the scope ends | R3's `AddTo` |
| `Flow.CreateClock` (scope Clock) | A Clock owned by the current scope, which disappears when the scope ends | — |
| `RunWhileActive`, `WhileActive` (Unity) | Unwinds the flow when the GameObject is deactivated or destroyed. `RunWhileActive` is always a root flow, wherever you call it from; `WhileActive` runs inside the awaiting scope | `GetCancellationTokenOnDestroy` |
| `WaitForDestroy` (Unity) | Completes when the GameObject is destroyed. Doesn't complete on deactivation | `GetCancellationTokenOnDestroy` |
| `RunWhileInTree`, `WhileInTree` (Godot) | Unwinds the flow when the node leaves the tree. `RunWhileInTree` is always a root flow, wherever you call it from | — |

## Cancellation and failure

The full picture is in [Handling failures](../guide/failures.md).

| Term | Meaning | Closest equivalent |
|---|---|---|
| Cancellation | Stopping a flow because the parent ended, it lost a Race, `Cancel()` was called, and so on. The scope unwinds | UniTask cancellation |
| Confirmed cancellation | The point at which a cancellation is decided. A confirmed scope starts unwinding at its next await | `IsCancellationRequested` |
| `FlowCanceledException` | The exception thrown from an await during unwinding | `OperationCanceledException` (derives from it) |
| `CancelCause` | The reason for a cancellation (`ParentEnded`, `RaceLost`, `Explicit`, and so on) | — |
| Swallowing | Continuing without rethrowing a received cancellation. Reported when the scope returns, and the scope ends as canceled | `catch (OperationCanceledException) { }` |
| Awaits in cleanup | Awaits in `catch` and `finally` of a canceled scope. They run to the end, and the surroundings (parent, Race, WhenAll) wait for them | Awaits in `finally` in UniTask |
| `Flow.NonCancelable` | A marker that keeps ancestors' cancellation from reaching the wrapped await. Lets an await in a `finally` entered before the cancellation run to the end | Kotlin's `withContext(NonCancellable)` |
| Unhandled exception | An exception that nothing caught and that reached the root. The scopes along the way end as `Faulted`, and the exception is reported to `FlowWorld.OnUnhandledException` | `AppDomain.UnhandledException`, Task's `Faulted` status |
| `FlowExceptionKind` | The kind of a report to `OnUnhandledException`: `Unhandled` (an unhandled exception), and the report-only `Cleanup` (an exception in cleanup), `SwallowedCancellation` (swallowing), and `Undelivered` (an exception that couldn't be delivered to its receiver) | — |
| Receiver, waiter | The side that receives an exception or value (the awaiting scope, a combinator, a handle) | — |

## Composition and waits

| Term | Meaning | Closest equivalent |
|---|---|---|
| `FlowTask.Race` | Settled by the first completion processed. Unwinds the losing branches, then resumes (time waits satisfied in the same Tick are processed in the order they began) | `UniTask.WhenAny` |
| `FlowTask.WhenAll` | Waits for all of them. If one fails, unwinds the rest | `UniTask.WhenAll` |
| `WaitForSeconds`, `DelayFrames`, `NextFrame` | Wait by the scope Clock's time (in seconds) and frames | `UniTask.Delay` (milliseconds), `DelayFrame` |
| `Clock`, `Flow.WithClock` | Game time, with Pause, scale, and parent-child relationships. `WithClock` changes the Clock of the tasks inside | `DelayType` |
| `DefaultClock`, `UnscaledClock` | Time with the engine's scale applied, and time without it | `Time.deltaTime`, `Time.unscaledDeltaTime` |
| `Clock.Pause()` (Pause) | Stops a Clock. Reference-counted; lifted by disposing the returned handle | `Time.timeScale = 0` |

## Signals

| Term | Meaning | Closest equivalent |
|---|---|---|
| `Signal<T>` (Signal, signal) | A notification with no state. Reaches only those waiting | R3's `Subject<T>` |
| `Next()` | Waits for the next Emit. Emits while nothing is waiting are dropped (edge) | R3's `FirstAsync()` |
| `Subscription<T>` (subscription) | Buffers Emits and takes them out one at a time with `Next()` (pull-based) | R3's `Subscribe` (push-based) |
| `FlowProperty<T>` | Holds a value and Emits `Changed` when it changes. You can wait for a condition with `WaitUntil` | R3's `ReactiveProperty<T>` |
| `Once<T>` | Gets a value only once, and can be waited on any number of times | `TaskCompletionSource<T>` |
| `EventSignal<T>` (callback bridge) | Waits on a callback or event as a Signal, through `FlowBridge.FromCallback` | R3's `Observable.FromEvent` |

## Bridges and diagnostics

| Term | Meaning | Closest equivalent |
|---|---|---|
| Bridge | Waits on a Task, UniTask, and so on as a FlowTask, through `FlowBridge.FromTask` or `.AsFlow()`. With `FlowBridge.FromTask`, the scope's cancellation is passed through a token | `AsUniTask()` |
| Dump, diagnostics | `FlowWorld.Dump()` and `Diagnostics` show the scope tree and waits | `UniTaskTracker` |
| Lifetime handle | A handle returned by `Clock.Pause()`, `Signal.Subscribe`, and so on, released by Dispose (FLOW004) | A subscription `IDisposable` |
| Virtual time | World time that a test advances with `Tick(dt)` ([Testing](../tools/testing.md)) | — |
