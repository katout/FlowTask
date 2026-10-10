# Design rationale

This page sums up FlowTask's main design decisions: why they were made, and what they cost. The behavior itself is described on each page. Here, you can check the reasons when you wonder "why does it work this way?"

## Cancellation unwinds with an exception

A canceled flow throws `FlowCanceledException` from the await where it is suspended, and exits while running `finally` and `using` ([Scopes and cancellation](../guide/scopes-and-cancellation.md)).

**Reasons**

- In C#, throwing an exception is the only way to exit an async method from the middle of an await while running `finally` and `using`.
- `FlowCanceledException` derives from `OperationCanceledException`. This way, the common patterns in .NET and UniTask (`catch (OperationCanceledException) { cleanup; throw; }` and `when (e is not OperationCanceledException)`) work for cancellation as they are. C# has no way to create an exception hierarchy that `catch (Exception)` doesn't catch. In languages like that, it is normal to align with the standard cancellation exception (Kotlin's `CancellationException` is also a standard JVM exception).
- Every unwinding throws a new instance. This keeps a later unwinding from overwriting the `StackTrace` and `Data` of an exception that was caught earlier.
- Unwinding works the same way on every runtime. If some runtimes threw an exception and others didn't, it would change how the debugger's "break when thrown" setting behaves, and whether a `finally` added later by hot reload runs.

**Costs**

- Every cancellation throws an exception once per suspended async method. The cost is in [Performance and memory](performance.md). Leaf waits don't use exceptions.
- Continuing after `catch (OperationCanceledException) { }` swallows the cancellation (next section).
- `FlowCanceledException.CancellationToken` is `None`, so `when (e.CancellationToken == token)` matches no token.

**Why there is no await that doesn't throw**: an await that returns "was it canceled?", like UniTask's `SuppressCancellationThrow`, adds public API, and in deep calls it only works if every level uses it. On the .NET JIT, unwinding 500 scopes takes less than 1 ms. On IL2CPP, it took 6 to 12 ms on a development player on a desktop PC ([Performance and memory](performance.md)). The criterion is based on real IL2CPP devices: one is added if unwinding 500 scopes on a mid-range device takes more than 8 ms, or 50 cancellations per frame take more than 1 ms. Real devices such as smartphones haven't been measured yet. Since the desktop figure is close to the criterion, the decision will be made from measurements on real devices. Until then, the guideline is "make a branch that loses every frame a leaf wait". Even now, the caller can receive a cancellation without an exception, as the Race result (`Index`). Only the async methods inside the losing branch unwind with an exception.

## A swallowed cancellation stops at that scope

When a canceled scope catches `FlowCanceledException` and returns, FlowTask reports it to `OnUnhandledException` as a swallowed cancellation and ends that scope as canceled. The caller continues. The FLOW001 analyzer stops this pattern as an error by default.

**Reasons**

- `FlowCanceledException` derives from `OperationCanceledException`, so `catch (OperationCanceledException) { hide the panel; return; }`, which is normal in UniTask, also swallows a cancellation. If a swallowed cancellation unwound everything outward, pressing "back" once would unwind the whole main menu.
- The side effects of the swallowing catch (showing an error, retrying) have already run, however they are handled. Unwinding further out protects nothing; it only widens what gets unwound.
- It follows the same idea as exceptions with no receiver (described later): what happens inside a canceled subtree is only reported, without changing the flow of live code.

**Cost**: a catch that swallows cancellation inside a loop keeps looping after the cancellation. At run time, it isn't reported until the scope returns. FLOW001 stops it at compile time.

## Outside awaits only through bridges

If a FlowTask method awaits an unbridged Task, UniTask, Unity Awaitable, or similar and suspends, that is a misuse at that point (`FlowMisuseException`). The same applies when an async method that isn't a FlowTask method (`async Task` and so on) awaits a FlowTask and suspends ([Flows and the World](../guide/flows-and-world.md), [Task and ValueTask](../integrations/task.md)).

**Reasons**

- While waiting on an outside awaitable, the library can't inject an exception into that await, so it can't deliver cancellation.
- Implementations of structured concurrency do one of two things: "the parent waits for its children to end" (Kotlin, Swift), or "outside awaitables are forbidden and an explicit bridge is required" (Trio). None of them let "the parent end first while the child keeps running".
- If the parent waits, the parent or a Race never ends until the outside work ends. That symptom only shows up when cancellation and stopping happen at the same time, so it is hard to find in tests. Treating it as a misuse at the point it suspends makes it always visible the first time it suspends, so you notice during development regardless of cancellation timing.
- An `async Task` method is not a scope, so it isn't canceled with the flow and doesn't unwind. Applying the same rule leaves one rule: "inside a flow, you can only wait on FlowTask methods".
- Bridges already exist for the main outside awaitables. Through a bridge, outside work also follows the scope's lifetime. When the scope ends, the token is canceled, and a result nobody received can be released with `onDiscard`, so you can also write a Race between a load and a cancel button.

**Costs**

- An outside await that completes synchronously doesn't suspend, so it isn't caught at run time. If a test directly awaits a fake Task that completes synchronously, the test passes, and the misuse only happens in production. The FLOW002 and FLOW005 analyzers report the same awaits at compile time.
- A suspended `async Task` method never resumes, and its Task never completes. To wait for a flow to end from Task code, await `world.Run(x).AsTask()`.
- Patterns that would have worked as long as nothing was canceled are all misuses too.

## A Task canceled from outside is an exception

When a bridged Task ends with `OperationCanceledException` (an `HttpClient` timeout, for example), FlowTask throws it at the await as an ordinary exception, not as cancellation of the flow.

**Reason**: when the bridge's scope is canceled, the bridge discards the result without reading it. So an `OperationCanceledException` that reaches the bridge always has a cause outside the scope. Treating it like a flow cancellation would let an outside timeout silently stop the flow, and you couldn't catch it.

**Cost**: if you don't catch it, a Task would end as Canceled, but in FlowTask it becomes an unhandled exception. To handle only outside cancellations, write `catch (OperationCanceledException e) when (e is not FlowCanceledException)`.

## Deferred start and GetAwaiter

A FlowTask doesn't start when you call it. It starts when the awaiter is created (`GetAwaiter`).

**Reason**: a deferred awaitable has to start somewhere. Starting in the `IsCompleted` getter would give the getter a side effect. Starting in `OnCompleted` would make even tasks that complete synchronously suspend once, losing the fast path for synchronous completion. There is precedent in .NET: Rx's `await observable` subscribes inside `GetAwaiter`.

**Cost**: Task's `GetAwaiter()` starts nothing, so calling it with the same expectation silently runs the task. The FLOW007 analyzer warns about `GetAwaiter()` written in code. You can use an awaiter by hand only when the task completed synchronously (`IsCompleted` is true), and such code suppresses the warning with `#pragma`.

## Signal.Next is an edge

`Signal<T>.Next()` drops Emits that happen while nothing is waiting. For input you must not miss, subscribe with `Subscribe(BufferPolicy)` ([Signals](../guide/signals.md)).

**Reasons**

- Making edges the default matches other libraries. UniTask's `button.OnClickAsync()` doesn't receive clicks from before it started waiting either.
- A buffered default would conflict with guarding against repeated taps (dropping taps during a purchase). If a later `Next` picked up taps made during a purchase, it could buy twice.
- The library can't tell whether dropped input was "dropped on purpose" or "missed". A guessing warning would fire on correct code too (code that drops repeated taps on purpose, a Race over shared input), and narrowing it would miss real misses. So there is no run-time warning, and the edge behavior is documented instead.
- `Next()` inside a loop easily misses input, so the FLOW006 analyzer points it out. Some edge loops are intentional (taps during an animation should be dropped), so it is only informational.

**Cost**: when input from before the wait started is dropped, nothing tells you at run time. Subscribe to input you want to catch when the screen opens (before the opening animation).

## Race branch order, and values from losing branches

In `Race`, the branch whose completion is processed first wins. Branches start in the order they are written, and if a winner is decided synchronously, the rest don't start. When branches complete at the same time, processing order (usually the written order) decides ([Composition](../guide/composition.md)).

**Reason**: every alternative brings a different surprise or slows things down.

- Checking leaf waits first would let a later leaf win even when an async method written earlier completes synchronously.
- Starting all branches before deciding would run the synchronous part of every branch even when the race settles synchronously, and pay the exception cost each time a losing branch unwinds.
- Stating the bias explicitly is how Kotlin's `select` treats it too.
- When one large dt passes several deadlines, the winner is also decided by the order the waits started, not by deadline. Sorting by deadline would change the execution order of every flow and add cost.

**Values from losing branches**: a value received by a losing subscription wait is put back at the front of the subscription. This keeps a hit from being lost when the hit subscription is a Race branch. Putting the value back doesn't change the Race's settling rules or the public API, and doesn't affect determinism or allocation.

- Only values received by leaf waits are put back. Once a combinator takes a value into its own result, that value counts as received. FlowTask has no mechanism to put back values from nested combinators level by level, because the cases it helps are narrow for the amount of implementation and rules it needs.
- Values from the edge `Signal.Next` are not put back. Putting them back would let the next `Next` that starts waiting receive a value from before it waited, which changes the meaning of an edge.

## Emit doesn't resume synchronously

Neither `Emit` nor a completion resumes a waiting flow immediately. They reserve the resume, which a FIFO queue processes ([Execution model](execution-model.md)).

**Reasons**

- Every resume goes through one queue in a fixed order, so the same input runs in the same order every time. Tests and bug reproduction rely on this determinism.
- The emitting code keeps running, without other flows cutting in, until its next await.
- The `AsTask()` Task follows the same idea: it completes at the end of a Tick, Flush, Run, or Dispose, not in the middle of a flush. This keeps outside code from running inside a flush and keeps the timing of resumes fixed.

**Cost**: right after `Emit`, you can't read the results of the receiving flow running within the same call. You read them after the next flush. If you emitted from outside a flow, calling `Flush()` right after runs the receivers on the spot.

## Two failure paths: cancellation and exceptions

Failures go through two paths: exceptions and cancellation ([Handling failures](../guide/failures.md)). There is no type for returning expected failures as values.

**Reasons**

- Exceptions work like Task: an exception from an awaited child is rethrown at that await. Ordinary C# like `try { await Load(); } catch (IOException) { … }` works as it is.
- There is no path that delivers exceptions straight to an outer boundary of the flow, and no abort path that skips catches along the way. In C#, you can write the same thing with exception types and try/catch. This means fewer paths and boundary rules, and exception handling that works the same as Task.
- Misuses the library detects (an unbridged await and so on) are also thrown at the waiter's await, as ordinary exceptions of the scope. If misuses took a separate path that can't be caught, users would have to learn a different path from other failures. An unbridged await ends the scope before it is thrown to the waiter, so even if you catch it and continue, no continuation leaks.
- There are no general types (Result, Maybe) for returning outcomes as values. C# has no standard ones; users who want one pick their own type or a library they like. If this library had a type of the same name, the names would clash in users' code (R3 exposes a `Result`, CSharpFunctionalExtensions a `Maybe<T>`). A return value is an ordinary value that goes through no path, so the user's own type returns it just as well, and the rules for where exceptions go (next item) don't affect it. APIs that may have no value (`NextOrClosed()` and so on) return a named tuple `(bool …, T Value)`.
- Stopping operations run side by side at the first failure is written with exceptions and `WhenAll`. You write it the same way as with Task's `WhenAll`, but unlike Task, the remaining branches stop.

**Exceptions with no receiver**: by the time an exception arrives, its receiver may already be canceled, or the Race may already be settled. Such an exception isn't carried further. It is reported to `OnUnhandledException` as `FlowExceptionKind.Undelivered`, and the result doesn't change.

- Carrying it to the next live waiter would carry it to the side that did the canceling, which either stops the canceling side or the already-settled caller after the fact, or makes the exception vanish silently. Only reporting it avoids both.
- The name Undelivered states a fact (it wasn't delivered) and makes no judgment about severity. It also happens in normal play, like a second failure in parallel work, so the default handlers in Unity and Godot log it as a warning, not an error.

**Order of reports**: reports go to `OnUnhandledException` immediately, in the order they are found. Holding reports back so the causing exception arrives first would need a buffering mechanism and special rules for advancing the World from inside a handler. Instead, while the World is running (flow code, cleanup, `OnUnhandledException` and `OnWarning` handlers, `Post` work), `Tick`, `Flush`, and `Dispose` are uniformly refused (`FlowMisuseException`).

**Costs**

- Your own exception, thrown to abandon work and return outward, is also caught by catch-alls along the way. A catch-all inside the place you return to must rethrow, or exclude the type with `when`.
- Misuse exceptions can also be caught by a catch-all, and execution continues.
- An exception's stack trace only contains where it was first thrown and where it was caught. To see the awaits in between, look at `FlowExceptionInfo.ScopePath`. A caught exception doesn't tell you the flow's path.
- A cleanup failure (reported as `Cleanup`) can arrive before the exception that caused it. The first entry of `FlowUnhandledException`, thrown when there is no `OnUnhandledException`, can be the effect rather than the cause.
- An expected failure written as an exception also becomes Undelivered, without reaching a catch, if there is no receiver (the second of two network calls run side by side that fail in the same frame, for example). To avoid that, return it as a value, and write stopping the rest at the first failure with `Once<T>` and a Race (see "Things written by combination" below).

## Lifetime and exceptions of spawned children

A child started with `Flow.Spawn` unwinds when the parent scope ends (even when it ends normally). A child's exception cancels the parent and is rethrown in the caller awaiting the parent ([Flows and the World](../guide/flows-and-world.md)).

**Reasons**

- Wrapping the lifetime means that when a screen or state ends, the work derived from it stops too. The goal is to make it impossible to write work you forgot to stop. `FlowWorld.Run` is a separate way to run something longer than its parent.
- Where a child's exception goes has the same shape as Kotlin's `coroutineScope`, Trio's nursery, and Swift's task groups. If you rewrite `await FlowTask.WhenAll(EnemyAI(), PlayerTurns())` as `_ = Flow.Spawn(EnemyAI()); await PlayerTurns();`, the caller's `catch (IOException)` still handles it the same way.
- Writing `Flow.Spawn(x);` and throwing away the handle, out of the habit of UniTask's `Forget()`, makes the child silently stop when the parent returns. The FLOW008 analyzer warns at compile time and makes you choose `_ = Flow.Spawn(x)` (ends with the parent) or `FlowWorld.Run` (outlives the parent). A run-time warning would also fire on correct uses where the child ends with its parent (a loop that ends with the screen). At compile time, the author only has to state the intent once with `_ =`. `_ =` is the ordinary C# way to discard a value on purpose.
- `FlowHandle` is not awaitable. Making it awaitable would trigger the compiler's CS4014, but CS4014 only appears inside async methods, and the fix it suggests, `await`, changes the meaning by making the parent wait for the child.

**Costs**

- An exception from a child spawned inside a try in the same method can't be caught by that method's catch. If you extract that range into a method, you can catch it as the caller.
- The caller's catch-all also catches bugs in spawned children.
- A child meant to run long but silenced with `_ =` can't be detected.

**Run on another World**: a flow started from inside a flow with another World's `Run` isn't tied to the calling scope. Tying it automatically would mean the opposite of `Run` on the same World (the way to detach from the parent), and behavior would change depending on the caller's World. To tie them, write one line: `Flow.AddCleanup(handle.Cancel)`.

## Awaits in cleanup, and Flow.NonCancelable

Even in a canceled scope, awaits in `catch` and `finally` entered after `FlowCanceledException` reaches your code run to the end. To protect an await in a `finally` entered before the cancellation, wrap it in `Flow.NonCancelable` ([Scopes and cancellation](../guide/scopes-and-cancellation.md)).

**Reasons**

- `finally { await FadeOut(); }` works the same as in UniTask. You don't have to choose a time limit in seconds for each cleanup.
- The surroundings (the parent, a Race's caller, a failed WhenAll) wait for the cleanup to end. Like a call stack, outer code runs after the inner cleanup ends.
- Cleanup has no default time limit. A limit would again require choosing the number of seconds and deciding what to do after cutting it off. Cleanup that needs a limit can be written with `Race`.
- At run time, an await in a `finally` entered before the cancellation (by `return`, the end of the try, or an exception) can't be told apart from an await in the body. The C# compiler keeps the try/finally exception inside the state machine, so the library can't see it. Only the author can mark the place to protect, so this is done with a marker. Every structured concurrency library with implicit cancellation has an explicit marker (Kotlin's `withContext(NonCancellable)`, Trio's `CancelScope(shield=True)`).
- The marker only works on a direct await; passing it to `Flow.Spawn`, `FlowWorld.Run`, or a combinator is a misuse. This makes it impossible to create "a marker that doesn't work" or "a flow that can't be canceled".
- `World.Dispose` doesn't run Ticks; it ends flows with a single pass of unwinding. Running Ticks inside Dispose would make when Dispose returns depend on cleanup. Cleanup during Dispose doesn't start new flows either, so that nothing is left after Dispose. There is no API that gives Dispose a grace period, because you can write it by combining cancellation, a bounded number of Ticks, and Dispose.

**Costs**

- Cleanup that never ends keeps the flow stuck. If a canceled scope waits in cleanup for more than 10 seconds of `UnscaledClock` time, a `LongCleanup` warning is raised once per World.
- If a marked await is running in a losing branch of a `Race`, the `Race` doesn't return until it ends.
- Cleanup also stops while the game is paused. Run cleanup that shouldn't stop on the UI Clock.
- With `World.Dispose`, even a save protected by the marker is cut off at its first await. Finish work that must run to the end before Dispose.
- FLOW010 also fires on awaits in a `finally` that is only entered after a cancellation, because the two can't be told apart statically. Marking such an await doesn't change its behavior, so either mark it or suppress the warning.
- A marked wait for input can't be canceled and keeps the flow stuck.

## A World runs on one thread and advances with Tick

Flow code runs only on the thread that created the World, inside `Tick`, `Flush`, `Run`, and `Dispose`. Time waits advance by the time passed to `Tick`, and completions from other threads are received at the intake of a Tick or Flush ([Threads](../guide/threads.md)).

**Reasons**

- Flow code needs no locks. Inside FlowTask, the scope tree and the resume queue don't use locks either; locks are limited to places such as the inbox that receives from other threads. Much game code uses APIs that can only be called on the engine's main thread.
- The order of resumes is decided by the order of one queue, not by thread timing. The same input runs in the same order, and can be reproduced in tests ([Execution model](execution-model.md)).
- Game time (Pause, scale, virtual time in tests) can be built from the one value passed to Tick.

**Costs, and the alternatives**

- I/O and heavy computation don't run inside flows. Write them with Task and wait for them through a bridge (the earlier "Outside awaits only through bridges").
- On the World's thread, you can't block waiting for a flow to end, because blocking stops Tick too. Await the end, or keep calling Tick while you wait. From another thread, you can block.
- A program without an engine needs a loop that calls Tick. Write the loop so that it doesn't leave the World's thread.
- In a program with only work that has nothing to do with frames, FlowTask brings little benefit, and using Task directly is simpler.

## Flows don't capture the ExecutionContext

FlowTask's builder doesn't capture the ExecutionContext, and flows run in the ExecutionContext of whoever calls Tick or a similar method. An `AsyncLocal<T>` value set inside a flow doesn't belong to that flow, and nothing guarantees that the flow sees the same value after an await ([Execution model](execution-model.md)).

**Reasons**

- Capturing it would make every flow pay the cost of capturing the ExecutionContext at each await that suspends, and restoring it at each resume. Per-frame waits are the most common awaits.
- Resumes happen together inside a Tick or Flush. It would have to be decided, case by case, which point's context a spawned child, a flow resumed by Emit, and a `finally` during unwinding run in.
- Game flows rarely use `AsyncLocal<T>`, and UniTask's builder doesn't capture it either.

**Cost**: mechanisms built on `AsyncLocal<T>` (`ILogger.BeginScope`, `Activity.Current`, and so on) can't be used across awaits inside a flow. Pass per-flow values as arguments, and to include the flow's location in logs, use `Flow.CurrentScopePath`. Inside a bridged Task, you can use them as in ordinary .NET.

## World thread binding and sessions

`Signal`, `FlowProperty`, and `Once` are bound to the first World that uses them and to its thread. Using them from another World after the bound World is disposed throws `FlowMisuseException` ([Threads](../guide/threads.md)).

**Reasons**

- There is no static state; the object's lifetime is tied to a session (World), so you can trace in code who created it.
- Rebinding would silently carry over state left from the previous session (a closed mark, subscriptions created outside flows). In Unity, a Signal kept in a static field with domain reload turned off throws at the first wait of the next play session, and the exception message points to the fix: create one per session and pass it along.
- Objects that no World has used yet aren't checked. Like ordinary .NET objects, they are used from one thread. This keeps it to one rule, and still lets you fill in initial values on a loading thread and then hand the object to a World.
- `EmitFromAnyThread` on a Signal not bound to any World throws `FlowMisuseException`. There is nowhere to queue the value, so the call would always be discarded. A send after the bound World is disposed can also happen in correct use, arriving from another thread during shutdown, so it is discarded without an exception.
- The check only runs on the slow path of binding (when the World differs), so the usual path costs no more.

**Costs**

- If several Worlds on the same thread share one object, after the first World that used it is disposed, the other Worlds can't use it anew either. Keep the World that uses it first alive longer than the others.
- Writing to an object from two threads before a World uses it is a race.
- A Signal sent from another thread must be created with `new Signal<T>(world)`, or used on the World's thread first.

## Clocks and time

**No API to inject time**: the core's only entry point for time is the argument of `FlowWorld.Tick(dt)`, and the core never reads real time. The engine integrations also have a way to substitute it (in Unity, turn off the automatic Tick and Tick yourself; in Godot, override `GetDeltaTime`). Adding an injection API wouldn't change the default for users who set nothing. A Godot autoload is created with a parameterless constructor, so even with injection you would write it in a subclass, which is the same amount of code as an override.

**Godot's default time**: Godot's `FlowWorldNode` uses Godot's process delta with `Engine.TimeScale` divided out (clamped to a limit; real time only while the scale is 0). This makes recording at a fixed frame rate (`--fixed-fps`, Movie Maker) correct, lines up with the physics World in the same game, and is deterministic with `--fixed-fps`. The cost is that at a fixed frame rate that runs faster than real time, `UnscaledClock` waits also advance faster. To put a real-time limit on outside work, use `CancellationTokenSource.CancelAfter` on the side of the bridged Task. Also, at a fixed frame rate while the scale is 0, `UnscaledClock` drifts from Godot's `ignore_time_scale` timers, so pause the game with `Clock.Pause()` rather than a scale of 0 ([Godot setup](../godot/setup.md)).

**CreateClock without a parent**: `world.CreateClock(name)` creates a child of `DefaultClock`, and `Flow.CreateClock(name)` creates a child of the current Clock. The form without a parent is the one written most often, and readers read it as "create under the current time", so the default matches that reading. To create an independent Clock that keeps running while the game is paused, make `UnscaledClock` its parent (`world.CreateClock("UI", world.UnscaledClock)`). It reads as "follows unscaled time" and needs no new API.

**Large dt and time comparison**: time waits satisfied in the same Tick resume in the order they started waiting. `WaitForSeconds` doesn't compare with a tolerance. Both are to avoid changing the execution order ([Execution model](execution-model.md)).

## The synchronous completion limit

When one scope completes more than 1,000,000 awaits synchronously in one top-level pass (one Tick, Flush, or Run called from outside), FlowTask treats it as an infinite loop and ends the scope.

**Reason**: without a limit, a loop that keeps completing synchronously freezes the whole game, and the cause is hard to find. The limit is a safety net that protects the game from real infinite loops, not a number correct flows hit, so it is large, and the scope ends when it is hit. It doesn't carry the rest over to the next Tick, because that would reorder execution and need a rule for each combination with other rules. The count restarts at each Flush and Run as well as each Tick, so that a World driven only by Flush doesn't accumulate a total over its lifetime.

**Why completed values aren't counted**: if awaits of `FromResult` and `CompletedTask` were counted, every await would pay for that check, including code that never awaits completed values. Task, ValueTask, and UniTask also continue synchronously when awaiting a completed value, so this matches the behavior C# users already know.

**Costs**: a loop that awaits only completed values never returns from the Tick, Flush, or Run that runs it, and doesn't hit the limit either. In a loop that waits for a condition, put a `NextFrame` in it, or use `FlowTask.WaitUntil`. A correct flow that completes more than 1,000,000 awaits synchronously in one Tick, Flush, or Run is also ended as an infinite loop.

## Keeping diagnostics and safety nets small

- **No Debug-only behavior**: FlowTask behaves the same regardless of build configuration. Tests don't split between Debug and Release, and you can notice problems that only happen in Release.
- **Warnings only for abnormal situations, once per kind per World**: warnings run only in abnormal branches, so the usual path doesn't pay for them. Once is enough so that a problem happening every frame doesn't flood the log.
- **No warnings that fire on correct code**: run-time warnings are limited to ones that never fire on correct code. A forgotten Spawn handle is found at compile time by FLOW008. For bridged results nobody received, pass `onDiscard` if they need releasing; results without `onDiscard` are silently discarded, the same as the result of a Task nobody awaits.
- **Only Joins on yourself or an ancestor are detected as never ending**: a cycle where flows wait on each other keeps waiting, the same as with Task and Kotlin coroutines. Look for it with `Dump`.
- **Where a wait was created is recorded with caller attributes**: the APIs that create waits and combinators take `[CallerFilePath]` and `[CallerLineNumber]` in their last optional parameters, and a node holds only a string reference and an integer. The values are compile-time constants, so nothing is allocated, and IL2CPP and NativeAOT get them the same way. A stack trace (`System.Diagnostics.StackTrace`) allocates every time it is taken, and IL2CPP and AOT may not give its file and line, so it is not used. As parameters, they let a game function that wraps a wait pass its own caller's place on. The call of a FlowTask method has no place: the call starts a compiler-generated state machine, to which no parameter can be added.
- **No settings**: the limits (flush resumes, pool, synchronous completion) are constants. There is no need to verify combinations of settings.

**Cost**: there is no execution log (tracer), and no run-time detection of FlowTasks created but never started. Find stuck flows with `Dump` and `Diagnostics.Walk`; FLOW003 points out forgotten starts at compile time ([Debugging and diagnostics](../tools/debugging.md)). The path of the place where a wait was created, with the folders of the machine that built it (which can include a user name), goes into the user's assembly as a string and stays in a release build. To avoid that, map the paths with `PathMap` (`ContinuousIntegrationBuild`).

## Keeping the public API small

Public API (types, members, settings) is added only when there are real users and it can't be written by combining existing APIs.

**Reason**: API adds to what you have to learn and maintain, and creates two ways of writing the same thing. FlowTask only owns lifetime, time, and order; it has no UI, input, or back key. You build those by combining signals with `Flow.Own` and `Flow.AddCleanup`, which run cleanup at the end of a scope. For a back key, see the `BackKeyRouter` of the samples ([Unity samples](../unity/samples.md)).

**Things written by combination**

| Situation | How to write it |
|---|---|
| WhenAny, timeout | `FlowTask.Race` |
| Stopping operations run side by side at the first failure returned as a value | A failing branch puts its failure in a `Once<T>` (unless `IsSet`) and awaits `FlowTask.Never()`; wait for the whole with `FlowTask.Race(FlowTask.WhenAll(…), failed.Wait())` |
| Exception boundary | try/catch |
| A Join that waits regardless of how it ends | `FlowTask.WaitUntil(h, x => x.IsCompleted)`, then check `Status` |
| Stop another World's flow along with the current scope | `Flow.AddCleanup(handle.Cancel)` |
| Time limit on cleanup | `await FlowTask.Race(Save(), FlowTask.WaitForSeconds(2))` |
| Finish cleanup before Dispose | Cancel, a bounded number of Ticks, Dispose |
| Independent Clock | `world.CreateClock(name, world.UnscaledClock)` |
| Priorities with lifetimes (who receives the back key) | Push entries on a list, have the pushing scope own each entry with `Flow.Own` so that it is removed when the scope ends, and deliver through a `Signal` per entry |

**Added even though they could be written by combination**: `onDiscard` on `FlowBridge.FromTask` (reliably releases results nobody received, on the World's thread), `FlowHandle.Cancel` from another thread (connects an outside token with `ct.Register(handle.Cancel)`), and `Flow.CreateClock` (a Clock that disappears at the end of the scope, with detection of use after it disappears, can only be written in the core).

## Names

- **FlowTask**: a name that doesn't overlap with other libraries or engine assets.
- **Namespace `Katout.FlowTask`**: follows the .NET namespace guidelines (`<Company>.<Product>`, PascalCase). The type `FlowTask` and the last part of the namespace are the same, so if your code is in a `Katout.*` namespace, `FlowTask` refers to the namespace (CS0118). This rare case can be avoided with an alias, so it was accepted. Libraries that put a type named after the product in the product's namespace are not unusual (`Options` in `Microsoft.Extensions.Options`, for example). A plural namespace was rejected because it would differ from the product and package name by one letter, which is easy to mistype.
- **Assembly name `FlowTask`**: giving the package and the assembly the same name is the ordinary shape, as in UniTask, R3, and others.
- **Names that don't silently misbehave**: the API that waits in seconds is `WaitForSeconds` (out of UniTask habit, writing `Delay(500)` would wait 500 seconds). Names that clash with other libraries or engines were avoided (`FlowWorld`, `FlowUnit`, `BufferOverflow`), as were names where the same word means the opposite elsewhere (not started is `Unstarted`, success is `Succeeded`; in UniTask, Pending means running).
- **`Discard`**: discards a task without starting it, which is different from `Forget()` (discards it while it keeps running). FLOW003 warns about `X().Discard()`.
- **`FlowTaskUnity`**: the entry point of the Unity integration (the default World and its settings). You write `FlowTaskUnity.World.Run(...)` every time you start a flow, so it is named after what it is the entry point to, not after how it is driven (the PlayerLoop). `FlowUnity` was rejected because it differs from the core's `FlowUnit` by one letter and is easy to pick by mistake from completion. When there is no default World (in Edit Mode, after `Shutdown`, after a recompile during play), `World` throws an exception that names the cause instead of returning null. Returning null would turn `FlowTaskUnity.World.Run(...)` into a `NullReferenceException` with no cause. Godot's `FlowWorldNode.Default` does the same. To check whether there is one, use `FlowTaskUnity.IsInstalled` on Unity, and check that `FlowWorldNode.Instance` isn't null on Godot.
- **Unhandled exceptions**: the names for an exception nothing caught follow .NET, not the "panic" of Rust and Go (`OnUnhandledException` matches `AppDomain.UnhandledException`, and `FlowStatus.Faulted` matches Task's Faulted status). C# users can read them with words they already know.

## C# 10 and Unity 2023.1

The library is written in C# 10, and the minimum Unity version is 2023.1.

**Reasons**

- C# 10 is the newest C# usable on both Unity and Godot. It is the newest finalized version available in the compiler of Unity 6 (6000.3), Roslyn 4.3.1, and Unity 2022.2 and later use the same Roslyn 4 line. Godot 4.4 games build with the .NET 8 SDK or later, so they can use up to C# 12.
- Unity compiles its own assemblies as C# 9. A `csc.rsp` next to each assembly in the packages raises the language version for that assembly only. It doesn't change your project's language version. Your code can stay on C# 9.
- Setting the minimum to 2023.1 removed the need to split the Awaitable bridge by version.
- Only syntax-only features are used; features that need runtime support (static abstract members in interfaces, ref fields) are not. They don't work on Mono and IL2CPP.
- The version is pinned by number, not `-langversion:latest` or `preview`, so that the accepted syntax doesn't change with the Unity version.

**Costs**

- It can't be used on Unity 2021.3 or 2022.x.
- In IDEs that don't apply the `csc.rsp` language version to the csproj files Unity generates for IDEs, the package sources show C# 10 syntax errors. This doesn't affect Unity's compilation ([Unity setup](../unity/setup.md)).

## Pool limit

State machines and nodes are reused through per-type pools, with a limit of 1,024 per type ([Performance and memory](performance.md)).

**Reason**: so that allocations don't keep happening even when hundreds or more nodes of the same type are released together (a swarm of enemies leaving, for example). There is no API to empty the pools, because nothing needs it and its effect hasn't been measured. The limit is a constant with no setting (see "Keeping diagnostics and safety nets small" above). The roots of `FlowWorld.Run` and `Flow.Spawn` aren't pooled, so you can read their state from the handle at any time.

**Cost**: the worst-case leftover (number of node types used × 1,024 × node size) stays until the process (on Unity, the domain) ends.

## Lifetime of engine objects

Unity's `RunWhileActive` unwinds the flow when the GameObject is deactivated or destroyed, and always starts it as a root flow of the World, wherever you call it from. Godot's `RunWhileInTree` is also a root flow wherever you call it from. Both have a version that doesn't start right away, to await in a flow (Unity's `WhileActive`, Godot's `WhileInTree`) ([Unity GameObject lifetime](../unity/lifetime.md), [Godot node lifetime](../godot/lifetime.md)).

**Reasons**

- Object pools are the most common form of reuse in Unity. If `SetActive(false)` didn't stop the flow, it would silently keep running, against what the author expects. Stopping on deactivation lets you write it with the same feel as coroutines.
- When the object is reactivated after being deactivated, the flow doesn't resume. State may have changed while it was inactive, and a resumed flow would continue on stale assumptions. When you take an object from the pool for a different use, running the previous continuation is wrong. Coroutines don't resume either.
- Starting at the root gives the lifetime a single owner. Making it a child of the calling scope would give it two owners, the object and the calling scope, and behavior would change depending on where it was called from.
- To end it with the calling scope as well (to await its result in a flow), await `WhileActive` / `WhileInTree`. It starts when awaited, and the code shows that it becomes a child of the awaiting scope. The two engines have the same shape, so what you learn on one works on the other.
- The second parameter is a Clock. A Clock knows its World, so the Clock alone also decides the World it runs in, and the call has the same shape as `FlowWorld.Run(task, clock)`. With a World parameter, running on the game's Clock meant wrapping the task in `Flow.WithClock` every time.
- There is no `this.RunWhileActive(...)` (called on a component). What it binds to is whether the GameObject is active, so `this` would read as stopping on the component's `enabled` or on `Destroy(this)`. `gameObject.` shows the owner.
- There is no mechanism to recover flows after recompiling during play. After a recompile, `Awake` and `Start` aren't called again and every library's running state is lost, so recovering FlowTask alone would help little. Instead, FlowTask warns and explains the cause in the exception message.

**Costs**

- Exceptions of a flow started with `RunWhileActive` go to `OnUnhandledException`, not to the caller of the calling scope.
- To keep a flow running across deactivation, tie it to destruction only with `FlowTask.Race(task, go.WaitForDestroy())`.
- If a flow deactivates or `Destroy`s its own GameObject in its last statement, the flow ends as `Canceled`, not `Succeeded`.
- Deactivation and `Destroy` become Flush points for that World, and the tied flows unwind inside that call. If you `Destroy` while enumerating a list and a `finally` changes that list, the enumeration throws. Enumerate a copy.
- When flow code deactivates the GameObject, `WhileActive` is decided not inside that call but when the World next runs its queued resumes (later in the same Tick or Flush, or at the next Flush point inside a `FlowWorld.Run` called from outside), as Godot's `WhileInTree` is.
