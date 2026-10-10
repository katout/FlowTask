# Debugging and diagnostics

This page covers the features to use when a flow is stuck, or when you can't tell where an exception came from: the scope tree dump, the diagnostics API, how to read warnings and exception reports, common exception messages, and IDE settings.

No diagnostics feature is Debug-only; they all work the same in every build. The diagnostics API (`world.Diagnostics` and the types in namespace `Katout.FlowTask.Diagnostics`) is for tools and tests, not for game logic. Represent game state with `FlowProperty` or your own objects.

## Scope tree dump

```csharp
Console.WriteLine(world.Dump());
```

```
FlowWorld [Default]
├─ Game (scope) [Default] waiting: InGame for 12.4s
│  └─ InGame (scope) [Default] waiting: Race for 12.4s
│     └─ Race (combinator) [Default] waiting: Race 2/2 branches
│        ├─ Stages (scope) [Default] waiting: WaitForSeconds(1s) on Default, 0.35s left for 0.65s
│        └─ Confirm.Next (wait) [Default] waiting: Confirm.Next
└─ Menu (scope) [UI] waiting: Never for 3s
   └─ Never (wait) [UI] waiting: Never
```

Each line shows the name, the node kind, the Clock, what it is waiting for, and how long it has been waiting.

- By default, the name is the method name. The root line shows the World's name (`new FlowWorld("Name")`; the default is `FlowWorld`).
- The kind is one of `scope` (a FlowTask method), `combinator` (Race or WhenAll), and `wait` (such as waiting on a signal).
- `NextFrame`, `DelayFrames`, and `WaitForSeconds` that a scope awaits directly don't get their own child lines; they appear on the scope's line, like `Stages` above. Ones given a Clock as an argument, and ones wrapped in `Flow.Named`, `Flow.WithClock`, or `Flow.NonCancelable`, become child lines of kind `wait`.
- The waiting time (`for 12.4s`) appears only on scope lines. It is the number of seconds of `UnscaledClock` since the scope last suspended.
- A composition line shows the number of live branches (`Race 2/2 branches`). When it has been decided and is waiting for its branches to clean up, it shows how it was decided (`decided`, `failed`, `canceled`) and the number of branches that haven't finished yet, like `Race decided, 1 branch still ending`.
- An external await that wasn't bridged doesn't appear in the dump, because the scope ends with an exception at the point it suspends.
- Lines deeper than 64 levels aren't indented any further; they start with their depth instead, like `[depth 65]`.

Lines can carry these markers (`Game` and `Fade` are Clock names):

| Marker | Meaning |
|---|---|
| `[Game, paused]` | That Clock (or an ancestor Clock) is paused. To see who paused it, look at `Paused clocks:` at the top |
| `[Fade, removed]` | A Clock from `Flow.CreateClock` was removed when the scope that created it ended. The next wait on that Clock throws `FlowMisuseException` |
| `<canceling: Explicit>` | The cancellation is settled, and cleanup (including awaits in `catch` and `finally`) is running. `Explicit` is the `CancelCause`; meanwhile, the scope stays Running |

If any Clock is paused, `Paused clocks:` appears at the top of the dump.

```
Paused clocks:
  Game: paused x2 by Main > PauseMenu, <outside any flow>
  Battle: paused via Game
```

For each Clock, it shows the number of Pauses and their owners (the paths of the scopes that paused it). If a Clock is stopped because its parent Clock is paused, it shows `paused via <parent>`. A Pause taken outside any flow has no owner (`<outside any flow>`) and stays until you dispose the handle `Pause()` returned. If the game stays frozen, look here first.

### Making the dump easier to read

- Give Signals names. A wait on `new Signal<int>(world, "Hits")` shows as `Hits.Next`, and a wait on its subscription as `Hits subscription.Next`.
- If you run the same method for many instances, tell them apart with `Flow.Named($"Enemy#{id}", EnemyAI(e))`.

### Viewing it in the editor

- **Unity**: `Window > FlowTask > Scope Tree` shows the scope tree and Clocks during play. Double-click a scope line to open that method in your script editor. Besides the default World, it can show Worlds registered with `FlowWorldRegistry.Register(world)` ([Unity bridges and tools](../unity/bridges.md)).
- **Godot**: bind `GD.Print(FlowWorldNode.Default.Dump())` to a debug key ([Godot setup](../godot/setup.md)).

## Finding stuck flows

In the dump, look for scopes whose waiting time keeps growing. To search from code, look at `WaitingSeconds` from `Diagnostics.Walk()`.

```csharp
var stuck = world.Diagnostics.Walk()
    .Where(s => s.Kind == FlowScopeKind.Scope && s.WaitingSeconds > 10)
    .Select(s => $"{s.Path}: {s.Waiting}");
```

- Ancestors waiting on a stuck descendant have been waiting a long time too, so start from the deepest scope.
- Time appears only on scopes. For a wait you started directly, like `world.Run(signal.Next())`, look at `Waiting` on the root's children (`Diagnostics.Root.Children`).
- A scope that stays at `<canceling: …>` has cleanup that doesn't finish. Awaits in cleanup have no time limit. If you need one, write `FlowTask.Race(x, FlowTask.WaitForSeconds(n))` ([Scopes and cancellation](../guide/scopes-and-cancellation.md)).
- Polling that suspends again every frame (`while (!ready) await FlowTask.NextFrame();`) doesn't grow its waiting time. To wait for a condition, write `await FlowTask.WaitUntil(() => ready)`; then you can find it when it gets stuck.
- `Next()` on a closed signal throws `SignalClosedException`, so it never waits there forever.
- Flows waiting on each other with `Join`, and a flow that bridges and awaits its own or an ancestor's `AsTask()`, aren't detected. Like Tasks, they wait forever, so look for them in the dump.

## Diagnostics API

`world.Diagnostics` has these members:

| Member | Contents |
|---|---|
| `Root` | The root `FlowScopeInfo` |
| `Walk()` | A depth-first enumeration of all nodes (scopes, compositions, waits), starting from the root |

`FlowScopeInfo` has `Name`, `Kind` (`FlowScopeKind`: `Root`, `Scope`, `Combinator`, `Wait`; `Invalid` once the node is released), `Status`, `ClockName`, `Waiting`, `WaitingSeconds`, `Path`, `Cause`, `IsCanceling`, and `Children`. When the node ends and is released, `IsValid` becomes false.

- `DeclaringType` and `MethodName` give the source location of a FlowTask method's scope. A lambda gets the name of its enclosing method; `Flow.Named` doesn't affect them, and there is no line number.
- Inside a flow, `Flow.CurrentScopePath` gives the path of the current scope. It's handy to add to your logs.
- `world.Clocks` lists the live Clocks. If it keeps growing, you are calling `Flow.CreateClock` inside a loop in a long-lived scope. Create the Clock inside a child FlowTask method instead.

## Warnings

```csharp
world.OnWarning += w => Console.Error.WriteLine(w);   // [PausedOwnClock] ... at Main > PauseMenu
```

There are four kinds of warnings. In every build, each kind is delivered only once per World. The Unity and Godot integrations write them to the engine's log.

| `FlowWarningKind` | What happened | How to fix |
|---|---|---|
| `FlushLimit` | One flush reached its limit (65,536 resumptions) and pushed the rest to the next flush | Find what keeps adding resumptions (for example, flows that keep resuming each other) |
| `PausedOwnClock` | A flow paused the Clock it runs on (or an ancestor of it). Its own resumption stops too | Run dialogs and menus on a different Clock with `Flow.WithClock(ui, …)` ([Time and Clocks](../guide/time-and-clocks.md)) |
| `LongCleanup` | A canceled scope has been waiting in cleanup (awaits in `catch` and `finally` after the cancellation, awaits in `Flow.NonCancelable`) for 10 seconds of `UnscaledClock`. The flow and the scopes waiting on it stay Running | Find the cleanup that doesn't finish (a loop, waiting on a signal from a canceled sibling, waiting for input inside `Flow.NonCancelable`), and add a limit with `FlowTask.Race`. If the Clock is stopped, the message says so |
| `CleanupCutAtDispose` | `World.Dispose` cut off an await in cleanup or in `Flow.NonCancelable`. The rest of that block didn't run | Cancel before Dispose and run Ticks ([Handling failures](../guide/failures.md)) |

- If a `Flow.Spawn` child that you meant to outlive its parent gets cut off when the parent ends, there is no warning at run time (the handle is Canceled, with cause `ParentEnded`). [FLOW008](analyzers.md) points out a `Flow.Spawn(x);` statement that doesn't use the handle at compile time.
- `OnWarning` and `OnUnhandledException` handlers are called in the middle of the scheduler (while it ends a scope or unwinds one). A flow you start or cancel there runs in the middle of that work, so keep handlers to logging or counting. To respond with a flow, such as showing an error screen, hand it to the next Tick or Flush intake with `world.Post(() => world.Run(ShowError()))`, or tell a flow that waits for it through a Signal. You can't call Tick, Flush, or the World's Dispose from a handler (`FlowMisuseException`).
- If a handler throws, the scheduler doesn't stop. An exception from an `OnWarning` handler is reported to `OnUnhandledException` as an unhandled exception whose ScopePath is `<FlowWorld.OnWarning handler>`. An exception from an `OnUnhandledException` handler is thrown, together with the report it was given, as a `FlowUnhandledException` from the outermost Tick, Flush, Run, or Dispose.

## Reading exception reports

```csharp
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // [Unhandled] at Game > InGame > Battle: System.IO.IOException: ...
```

The `FlowExceptionInfo` that reaches `OnUnhandledException` holds the exception (`Exception`), the path of the scope at the point it was thrown (`ScopePath`, for example `Game > InGame > Battle`), and the kind of report (`Kind`).

| `FlowExceptionKind` | What it is |
|---|---|
| `Unhandled` | An exception nobody caught anywhere: one that ended a flow started with `FlowWorld.Run`, or one from code outside any flow, such as `Post` or a handler |
| `Cleanup` | An exception in cleanup (code in a scope whose cancellation is settled, `AddCleanup`, Dispose from `Own`, `onDiscard`). Cleanup continues |
| `SwallowedCancellation` | A canceled scope returned after receiving `FlowCanceledException` (a `catch` swallowed it). The scope ends as canceled |
| `Undelivered` | An exception that couldn't be delivered to a receiver (an awaiter that was canceled, a Race already decided, the second and later exceptions, an exception thrown by a child spawned while its owner was ending, a Task that failed after its bridge stopped waiting) |

- `Undelivered` also happens in ordinary play (for example, two network calls that fail in the same frame). The Unity and Godot integrations log it as a warning, and the other kinds as errors.
- Reports are delivered right away, in the order they are found. So a cleanup failure (`Cleanup`) can arrive before the exception that caused it.
- `FlowHandle.Exception` has a value only when the flow ended as Faulted. It holds only the exception; read the path and the kind of report in `OnUnhandledException`.
- In a World without `OnUnhandledException`, reports of every kind are thrown together as a `FlowUnhandledException` (listed in `ExceptionInfos`) from the outermost Tick, Flush, Run, or Dispose.

The stack trace of an exception rethrown at an await contains only the place where it was first thrown and the place where it was caught, not the awaits in between. Look at `ScopePath` for those.

The full picture of failure paths is in [Handling failures](../guide/failures.md).

## Common exception messages

Misuse that the library detects is thrown as an ordinary exception of the scope at the awaiter's await, and you can catch it with `catch`.

| Message (excerpt) | Meaning | How to fix |
|---|---|---|
| `FlowTask method '…' suspended on TaskAwaiter<Int32>, which is not a FlowTask` | A FlowTask method suspended on an external awaitable that wasn't bridged. The scope ends immediately, and the remaining `finally` and `using` don't run (`AddCleanup` and `Own` do) | Bridge it with `FlowBridge.FromTask(ct => …)` or `.AsFlow()` ([FLOW002](analyzers.md)) |
| `Scope '…' returned after FlowCanceledException reached its code` | A `catch` received the cancellation and returned (reported as `SwallowedCancellation`) | End it with `throw;`, or add `when (e is not FlowCanceledException)` ([FLOW001](analyzers.md)) |
| `Scope '…' completed 1000000 awaits synchronously in one Tick, Flush or Run` | A loop of awaits that keep completing synchronously was treated as an infinite loop, and the scope was ended at that await | Put `await FlowTask.NextFrame()` in the waiting loop |
| `A method that is not a FlowTask method (async Task, ValueTask, UniTask or async void) awaited a FlowTask in scope '…', and the await did not complete at once` | An async method that isn't a FlowTask method awaited a FlowTask and suspended. That method never resumes, and its Task never completes. The scope that was running that code ends | Make the method return FlowTask ([FLOW005](analyzers.md)) |
| `A FlowTask can only be awaited inside a FlowTask method running in a World` | A FlowTask was awaited outside a flow | Start it with `FlowWorld.Run`. To wait for its result in Task code, use `world.Run(task).AsTask()` ([FLOW005](analyzers.md)) |
| `Clock '…' was removed when its scope '…' ended (Flow.CreateClock)` | A Clock from `Flow.CreateClock` was used (waited on, run on, paused, TimeScale changed) after the scope that created it ended | Create Clocks that outlive the scope with `FlowWorld.CreateClock` |
| `… is bound to FlowWorld(A), the first World that used it, which is disposed` | A Signal or similar object used in a disposed World was used in another World (for example, in Unity with domain reload turned off, when it was kept in a static) | Create Signals, FlowProperties, and Onces per World and pass them around |
| `… is bound to FlowWorld(A) on thread N and cannot be used by FlowWorld(B) on thread M`, `… was called from thread N, but the object belongs to thread M` | The object was used on a different thread from the one it is bound to (it is bound to the thread of the first World that used it) | From other threads, use `EmitFromAnyThread`, `CloseFromAnyThread`, and `FlowWorld.Post` ([Threads](../guide/threads.md)) |
| `EmitFromAnyThread was called on a signal that no World uses yet` | A value was sent with `EmitFromAnyThread` to a Signal no World has used yet. There is no World to queue the value on | Create it with `new Signal<T>(world)`, or use it on the World's thread first (create it inside a flow, or await `Next` or `Subscribe` inside a flow) |
| `… subscription.Next: this subscription has already ended` | `Next()` was awaited on a subscription that has ended (disposed through a copy, or its owning scope ended) | Dispose a subscription only once, where you created it |
| `The subscription to '…' overflowed: its queue (capacity 1) was full …` | A subscription with `BufferOverflow.Fail` overflowed | Raise the capacity, or use `DropOldest` or `DropNewest` |
| `Join of '…' from inside it can never complete` | A flow joined itself or an ancestor | Join from outside the flow |
| `No FlowTask World: …` | On Unity, there is no default World (in Edit Mode, after a recompile during play, after `Shutdown`, or when automatic setup is turned off) | Follow the instructions in the rest of the message. After a recompile, restart play ([Unity setup](../unity/setup.md)) |

Awaits on already-completed values (`FlowTask.FromResult`, `FlowTask.CompletedTask`) don't count toward synchronous completions. A loop that awaits only those never returns from Tick, Flush, or Run, and no exception is thrown.

## Handle status

`Status` on a `FlowHandle` is the flow's current state.

| `FlowStatus` | Meaning |
|---|---|
| `Running` | Started and not yet finished. Even after it is canceled, it stays Running until cleanup (such as awaits in `finally`) finishes |
| `Succeeded` | Its own code returned (for a wait, the wait completed). It wasn't canceled |
| `Canceled` | It ended by cancellation (`CancelCause` gives the reason). An exception that arrives after the cancellation is only reported; the status stays Canceled |
| `Faulted` | It ended with an exception (from its own code, from a child it spawned, or misuse the library detected). `FlowHandle.Exception` is set |

`CancelCause` tells whether a cancellation was requested and why; it is separate from `Status`. If the status is Running and `CancelCause` is something other than `None`, the flow is closing (`handle.ToString()` shows `FlowHandle(Screen, Running: Explicit)`). A handle canceled from another thread stays Running until the next Tick or Flush.

## Reproducing bugs

If you give the same sequence of `deltaTime` values and the same inputs in the same order, the execution order is the same. To reproduce a bug, record the following:

- The `deltaTime` passed to `Tick`, and the order in which `Tick` and `Flush` were called.
- The inputs given from outside any flow on the World's thread (`Emit`, `FlowProperty.Set`, `Cancel`, `Run`), and `world.UnscaledClock.FrameCount` at that moment.
- For values that come from other threads, which Tick takes them in depends on thread timing. Make them follow one shape: record the frame count and the value inside `world.Post(() => …)`, then Emit to the Signal. On replay, `Post` the recorded values from the World's thread in the same order.

Making several machines reach the same result (lockstep sync) is outside this determinism. The rules for the order of flows are the same everywhere, but FlowTask doesn't guarantee that your game code's calculations (floating point, physics, random numbers) come out the same. There is no way to save a flow's state partway through and restore it, so FlowTask can't be used for rollback-style sync, which rewinds the state and recomputes it. The execution order rules are in [Execution model](../advanced/execution-model.md).

## Stopping the IDE from breaking on every FlowCanceledException

`FlowCanceledException` is a normal exception thrown on every cancellation. The library marks its side with `[DebuggerHidden]` and `[DebuggerNonUserCode]`, but the debugger may still stop if "break when thrown" is turned on.

- **Visual Studio**: in Exception Settings (Ctrl+Alt+E), add `Katout.FlowTask.FlowCanceledException` under Common Language Runtime Exceptions and uncheck it. Leave "Enable Just My Code" on.
- **Rider**: in View Breakpoints (Ctrl+Shift+F8), under .NET Exception Breakpoints, add `Katout.FlowTask.FlowCanceledException` to the Exclude list of "Break on all exceptions", and turn on "Only break on exceptions thrown from user code".
- **VS Code (C# Dev Kit)**: set `"justMyCode": true` in `launch.json`, and in BREAKPOINTS, uncheck "All Exceptions" so that only "User-Unhandled Exceptions" is checked.
