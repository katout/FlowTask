# Execution model

This page explains when FlowTask flows start, when they resume, and in what order they run. Most of the time, [Flows and the World](../guide/flows-and-world.md) and [Scopes and cancellation](../guide/scopes-and-cancellation.md) are all you need. Read this page when you need the exact order of several things that happen in the same frame.

## When a task starts

A FlowTask is deferred. Calling the method alone runs nothing. It starts when it is awaited, when it is passed to `Flow.Spawn` or `FlowWorld.Run`, or when the combinator you passed it to starts ([Flows and the World](../guide/flows-and-world.md)).

A task that has started runs synchronously, right there, up to the first await that suspends.

```csharp
world.Run(Intro());
Console.WriteLine("after Run");   // printed after "intro starts"

static async FlowTask Intro()
{
    Console.WriteLine("intro starts");   // runs inside Run
    await FlowTask.NextFrame();
    Console.WriteLine("intro resumes");  // runs in the first Tick after Run
}
```

- `await` starts the task when it creates the awaiter (`GetAwaiter`). So a `GetAwaiter()` call written in your code also starts the task. The FLOW007 analyzer warns about this ([Analyzer rules](../tools/analyzers.md)).
- An awaiter's `IsCompleted` keeps the value it had when the awaiter was created. You can use an awaiter by hand only when `IsCompleted` is true (the task completed synchronously).
- You can start a FlowTask only once. A second await throws `FlowMisuseException`.
- `default(FlowTask)` is a completed task (`FlowTask.CompletedTask`). `default(FlowTask<T>)` is not a task; awaiting it or passing it to a combinator throws `FlowMisuseException`.

### ExecutionContext and AsyncLocal

FlowTask does not capture the ExecutionContext. Flows run in the ExecutionContext of whoever calls `Tick`, `Flush`, `Run`, or `Dispose`.

- An `AsyncLocal<T>` value set outside flows before calling Tick is visible to every flow.
- A value set inside a flow doesn't belong to that flow. It is also visible to other flows that run later on the same thread, and to the caller of Tick. There is also no guarantee that the same value is visible after an await.
- Inside the async method that makes a bridged Task (the one the `FlowBridge.FromTask` factory calls), values carry over across awaits, as in ordinary .NET.
- Pass per-flow values (such as a correlation ID for logs) as arguments. To include the flow's location in logs, use `Flow.CurrentScopePath` ([Flows and the World](../guide/flows-and-world.md)).
- Don't keep a scope built on `AsyncLocal<T>`, such as `ILogger.BeginScope`, open across an await. It would also be attached to other flows' logs.

UniTask's builder doesn't capture the ExecutionContext either. The reasons are in [Design rationale](design-rationale.md).

## What happens in one Tick

`world.Tick(dt)` does the following, in this order.

- **Intake**: processes messages from other threads, in the order they were queued in the World's inbox. These are `EmitFromAnyThread`, `CloseFromAnyThread`, `FlowWorld.Post`, `Cancel` and `EventSignal.Dispose` from other threads, completions of bridged Tasks, and `onDiscard` for values nobody received.
- **Advance time**: advances each Clock by `dt × effective scale`, in creation order (parents first). Paused Clocks don't advance.
- **Check time and frame waits**: checks `WaitForSeconds`, `DelayFrames`, `NextFrame`, and `WaitUntil` in the order they started waiting, and reserves a resume for each one that is satisfied.
- **Flush**: processes the reserved resumes in the order they were queued (FIFO). Resumes queued during the flush are processed in the same flush.
- **End of the Tick**: completes the `AsTask()` Tasks of flows that ended in this Tick. If `OnUnhandledException` is not set, the reports it would have received are thrown here (`FlowUnhandledException`).

`world.Flush()` does only the intake, the flush, and the end step, without advancing time. It doesn't check time and frame waits.

In the next example, resumes of three flows line up in one Tick.

```csharp
var main = new Signal<string>(world);
var cross = new Signal<string>(world);

world.Run(Delay());
world.Run(Print(cross));
world.Run(Print(main));

// Wait keeps this thread as the World's thread (await would continue on a pool thread)
Task.Run(() => cross.EmitFromAnyThread("from another thread")).Wait();
main.Emit("from the World thread");   // reserved now
world.Tick(1.0 / 60);
// from the World thread
// from another thread
// delay

static async FlowTask Print(Signal<string> s) => Console.WriteLine(await s.Next());

static async FlowTask Delay()
{
    await FlowTask.WaitForSeconds(1.0 / 60);
    Console.WriteLine("delay");
}
```

An `Emit` on the World's thread reserves the resume immediately. A message from another thread is reserved during the intake at the start of the Tick, and a time wait is reserved after time advances. That gives this order.

### Intake order

- Messages sent from the same thread are processed in the order they were sent, whatever their kind.
- A bridged Task's completion is queued in the inbox immediately, on the thread that completed it, regardless of the SynchronizationContext.
- Work passed to `Post` belongs to no scope. An exception it throws becomes an unhandled exception at the root.

Working with other threads is covered in [Threads](../guide/threads.md).

### When AsTask completes

The Task from `FlowHandle.AsTask()` completes on the World's thread, at the end of the Tick, Flush, Run, or Dispose in which the flow ended. It doesn't complete in the middle of a flush. This keeps outside code from running inside a flush and keeps the timing of resumes fixed.

A continuation that doesn't capture a context runs synchronously at that point. If that continuation waits for another `AsTask()` with `.Result` or a Tick loop, it never completes.

## Resumes are reserved

Neither a completion nor an `Emit` resumes a waiting flow immediately. They reserve the resume, and the flush processes the reservations in order.

```csharp
var hit = new Signal<int>();
world.Run(Listen(hit));

hit.Emit(10);
Console.WriteLine("after Emit");   // printed first
world.Flush();                     // "hit 10" is printed here

static async FlowTask Listen(Signal<int> hit)
{
    var damage = await hit.Next();
    Console.WriteLine($"hit {damage}");
}
```

The same is true for an `Emit` inside a flow. `Emit` only fixes the flows waiting at that moment as receivers and reserves their resumes. The emitting code keeps running, without being interrupted, until its next await. A flow that starts waiting after the `Emit` doesn't get that value.

The waiting `Next()` task has already ended when `Emit` returns: its `Status` is `Succeeded`. Only the resume waits for the flush. Code outside the flows that hands a value to one receiver can therefore check `Status` right after `Emit` to learn whether the value was taken. A flow canceled from outside is not a receiver, even before it unwinds (the `Status` of its `Next()` task stays `Running`). A wait wrapped in `Flow.NonCancelable` still takes the value after the cancellation.

Reservations go to the end of the queue and are processed in the order they were queued.

```csharp
var a = new Signal<int>();
var b = new Signal<int>();

world.Run(Waiter(a, "a1"));
world.Run(Waiter(a, "a2"));
world.Run(Waiter(b, "b1"));
world.Run(Waiter(a, "a3"));
a.Emit(0);
world.Tick(1.0 / 60);
// a1, a2, a3, b1: a1 emits b while a2 and a3 are already queued

async FlowTask Waiter(Signal<int> s, string name)
{
    await s.Next();
    Console.WriteLine(name);
    if (name == "a1") b.Emit(0);
}
```

- Completion with a value and failure with an exception go through the same queue. When a child ends, a resume of the parent waiting for it is reserved. A result travels from child to parent one level at a time, through reservations.
- An await whose child completes immediately (without suspending) continues directly, without a reservation.

## Frame and time waits

### NextFrame and DelayFrames

`NextFrame` and `DelayFrames` don't resume in the flush where the wait started. They resume in the "check time and frame waits" step of a later Tick.

```csharp
async FlowTask Steps(Signal<int> sig)
{
    await sig.Next();             // resumed by the flush of Tick 1
    await FlowTask.NextFrame();   // resumes in Tick 2
    await FlowTask.DelayFrames(2);  // resumes in Tick 4
}
```

- A frame is one Tick in which the Clock is not paused (`Clock.FrameCount`).
- `DelayFrames(0)` completes immediately.
- `Flush` doesn't advance frames, so `NextFrame` never resumes no matter how many times you call it.
- A wait started inside `FlowWorld.Post` work is the exception. Post work runs during the intake at the start of a Tick, and that same Tick then advances the frame and checks waits, so `NextFrame` resumes within that Tick.

### WaitForSeconds

`WaitForSeconds(seconds)` resumes in the first Tick in which the Clock's time reaches the target. The argument is in seconds (not milliseconds).

- `WaitForSeconds(0)` doesn't complete immediately either. It resumes the next time a Tick checks waits (even in a Tick with a dt of 0). When started inside `Post` work, it resumes within that Tick, just like `NextFrame`.
- A Clock adds up time as a double. At a number of seconds that falls exactly on a frame boundary, the wait can be one frame late. For example, adding 1/60 six times gives 0.09999999999999999, so at 60 fps `WaitForSeconds(0.1)` resumes on the 7th Tick. Comparing with a tolerance would change the execution order, so FlowTask doesn't do that.
- To wait an exact number of frames, use `DelayFrames`.

### WaitUntil

`WaitUntil(condition)` checks the condition once when awaited, and continues right away if it holds. If not, it checks the condition every Tick from then on.

- It doesn't check while the Clock is paused.
- An exception thrown by the condition is thrown at the await. This is the same for the first check and for later ones.

### Waits satisfied in the same Tick resume in the order they started

When several time waits are satisfied in one Tick, they resume in the order they started waiting, not in order of their deadlines.

```csharp
var r = await FlowTask.Race(FlowTask.WaitForSeconds(30), FlowTask.WaitForSeconds(20));
// After world.Tick(60), r.Index is 0: both deadlines passed in one Tick,
// and the first branch began to wait first.
```

When one large dt passes two deadlines, the order in which those time waits began decides the winner of the Race. When the branches are the time waits themselves, that is the order of the arguments. Sorting by deadline would change the execution order of every flow and add cost, so FlowTask doesn't do that. The Unity and Godot integrations clamp a large dt to the engine's limit before they Tick ([Time and Clocks](../guide/time-and-clocks.md)).

## Order in Race and WhenAll

- In `Race`, the branch whose completion is **processed** first wins. For a leaf-wait branch, that is when the flush delivers its value; for an async method branch, that is when the method ends.
- Branches start in the order they are written. If a branch that started completes synchronously, the remaining branches don't start.
- Once there is a winner, the losing branches are canceled and unwound. After their cleanup (including awaits in `finally`) finishes, the caller's resume is reserved.
- `WhenAll` reserves the caller's resume when all branches have ended. If one ends with an exception, the rest are canceled and unwound, and the exception is passed on after their cleanup finishes.

Which of several branches that complete at the same time wins is decided by processing order (usually the written order). Why you should write the interrupting wait first, and what happens to values the losing branches had received, are covered in [Composition](../guide/composition.md).

## When cancellation takes effect

A cancel request is confirmed immediately, wherever it comes from. When the unwinding runs depends on where you made the request.

| Where the request is made | When the unwinding runs |
|---|---|
| Inside flow code (in a flush, in the synchronous part of `Run`, during unwinding) | Immediately |
| Outside flows (game code, UI callbacks) | At the head of the flush of the next Tick or Flush. Until then, `Status` is `Running` |
| On another thread | Confirmed in the intake of the next Tick or Flush, and run at the head of the same flush |
| The running scope canceled itself or an ancestor | At that scope's next await |

A flow canceled from outside doesn't resume even if its resume was already reserved. It unwinds at the head of the flush.

```csharp
var victim = world.Run(Victim(sig));
world.Run(Other(sig));
sig.Emit(0);       // both resumes are reserved
victim.Cancel();   // confirmed now; victim.Status is still Running
world.Tick(1.0 / 60);
// victim finally   <- unwound at the head of the flush; "victim resumed" is never printed
// other resumed
```

Unwinding goes from descendants toward the root. Among siblings, the one started last starts unwinding first. Within one scope, `finally` blocks run from the inside out, and then `Flow.AddCleanup` and `Flow.Own` run in the reverse order of registration. For details, including how awaits in cleanup behave, see [Scopes and cancellation](../guide/scopes-and-cancellation.md).

## Pause and resume

- Resumes for scopes that belong to a paused Clock are held. When the pause is lifted, the held resumes go back to the head of the queue in their original order. They are processed before the reservations already in the queue at that point.
- The start of unwinding (`FlowCanceledException` at an await) is not held. Cancellation arrives even during a pause.
- Resumes of awaits inside cleanup are held like any other resume. Cleanup that should finish even while the game is paused should run on the UI Clock ([Time and Clocks](../guide/time-and-clocks.md)).

## Limits

A World has two limits that keep the game from freezing. Both are constants, with no settings.

- **Up to 65,536 resumes per flush**. The rest are carried over to the next flush, and a `FlowWarningKind.FlushLimit` warning is raised once per World. You hit this with, for example, two flows that keep emitting to each other without end.
- **Up to 1,000,000 synchronously completed awaits per scope in one Tick, Flush, or Run** (called from outside). Past that, FlowTask treats it as an infinite loop and ends the scope at that await. The rest of the method doesn't run. The waiter receives `FlowMisuseException`.

The synchronous completion limit counts awaits where a started task completed without suspending. Awaits of already-completed values (`FlowTask.FromResult`, `FlowTask.CompletedTask`) are not counted and continue synchronously, the same as Task and UniTask. So a loop that awaits only completed values never returns from the Tick, Flush, or Run that runs it.

```csharp
// Never returns from the Tick that runs it
while (!ready) await FlowTask.CompletedTask;

// Checks once per frame
while (!ready) await FlowTask.NextFrame();
```

In a loop that waits for a condition, put a `NextFrame` in it, or use `FlowTask.WaitUntil`.

## What you can't call while the World is running

While the World is running flow code, cleanup, `OnUnhandledException` and `OnWarning` handlers, or inbox work, you can't call `Tick`, `Flush`, or `Dispose` (`FlowMisuseException`). Handlers are called in the middle of the scheduler. In a handler, only log and count; don't start or cancel flows. To respond with a flow, for example by showing an error screen, defer it to the next intake with `world.Post(() => world.Run(ShowError()))`, or notify a flow that waits for it through a Signal. If a handler throws, the scheduler and the other handlers keep going.

## Scope of determinism

If you give the same sequence of dt values and the same inputs (calls on the World's thread and messages to the inbox) in the same order, flows run in the same order every time. There are tests that check this against the same reference log on .NET, Unity, and Godot.

FlowTask itself computes Clock time with double additions, multiplications, and comparisons only, and doesn't use floating-point functions whose last bits can depend on the CPU (trigonometric, exponential, logarithm, and power functions, estimate instructions, and fused multiply-add). A test checks this.

This determinism is for testing and for reproducing bugs.

- The order in which messages from other threads reach the inbox depends on thread races.
- There is no way to save and restore the state of flows. So it can't be used for rollback synchronization, which rewinds state and recomputes it.
- Making several devices reach the same result (lockstep synchronization) is outside this determinism. The rules for the order of flows are the same everywhere, but FlowTask doesn't guarantee that your own code, such as floating-point calculations, gives the same result.

How to advance a World with fixed dt values in tests is covered in [Testing](../tools/testing.md).
