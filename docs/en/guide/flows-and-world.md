# Flows and the World

This page explains when the code you write with FlowTask starts, what drives it forward, and where it belongs. It covers the `FlowTask` type, `FlowWorld`, `Run` and `Tick`, `Flow.Spawn` for starting children, and `FlowHandle` for controlling a task you started.

## FlowTask methods and the World

You write game progression as async methods that return `FlowTask` or `FlowTask<T>`. We call these FlowTask methods. FlowTask methods are driven by a `FlowWorld` (the World). When you `Tick` the World every frame, flows whose waits are satisfied resume.

```csharp
using Katout.FlowTask;

var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // where uncaught exceptions go

var title = world.Run(TitleScreen());             // starts the flow; returns a handle

// In your game loop, once per frame:
world.Tick(unscaledDeltaTime);

async FlowTask TitleScreen()
{
    ShowLogo();
    await FlowTask.WaitForSeconds(2);   // seconds of the scope's clock
    await MainMenu();                   // a child flow: starts here, and this one waits for it
}
```

- `world.Run(task)` starts the task as a child of the World's root and returns a `FlowHandle`. The flow runs right away, inside `Run`, up to its first await that suspends.
- `world.Tick(dt)` advances time by the given number of seconds and resumes the waits that are now satisfied. Pass unscaled elapsed time as dt ([Time and Clocks](time-and-clocks.md)).
- The Unity and Godot integrations create a World and tick it every frame, so you don't tick it yourself ([Unity setup](../unity/setup.md), [Godot setup](../godot/setup.md)). In tests, you tick with virtual time ([Testing](../tools/testing.md)).
- `OnUnhandledException` receives exceptions that no flow caught. If you don't set it, `Tick` and the other entry points throw such an exception as a `FlowUnhandledException` ([Handling failures](failures.md)).

A World is the unit that maps to one timeline and one thread. You can create several Worlds on one thread and tick them in turn (for example, a separate World for physics frames).

## Tasks are lazy

Calling a FlowTask method runs nothing. A task starts at one of these points:

- When it is awaited
- When it is passed to `Flow.Spawn` or `FlowWorld.Run`
- When a combinator it was passed to (`FlowTask.Race`, `FlowTask.WhenAll`, and so on) starts

```csharp
async FlowTask Battle()
{
    PlayBgm();                   // does nothing: the task is created and dropped
    await SpawnEnemies();        // starts here, as a child of Battle
}
```

A task you drop, like `PlayBgm();`, never runs, and nothing reports it at run time. Analyzer rule FLOW003 warns about this pattern ([Analyzers](../tools/analyzers.md)).

A task you start becomes a child of the scope it was started from. Scopes and their tree are covered in [Scopes and cancellation](scopes-and-cancellation.md).

### Await only once

A `FlowTask` value can be started only once.

```csharp
var load = LoadStage();
await load;
await load;   // FlowMisuseException: this task was already started
```

If several flows need to wait for the same result, put the result in a `Once<T>` and have them wait on it ([Signals](signals.md)). For work that may end with a failure or cancellation, start it with `Flow.Spawn`, have each flow wait on the handle with `FlowTask.WaitUntil(h, x => x.IsCompleted)`, and check `Status`. Passing an already started task to `Flow.WithClock` or `Flow.Named` also throws `FlowMisuseException`. Apply both to a task before it starts.

### Tasks you decide not to start: Discard

When you decide, based on some condition, not to start a task, release it with `Discard()`.

```csharp
var intro = ShowIntro();
if (tutorial) await intro;
else intro.Discard();   // not started on purpose
```

`Discard()` is not UniTask's `Forget()`. It throws the task away without starting it. If you want to run a task without waiting for it, use `Flow.Spawn` or `FlowWorld.Run`, described next.

## Running without waiting: Spawn and Run

await means "start a child and wait until it ends." To run something alongside without waiting, choose between two options depending on how long it should live.

```csharp
async FlowTask Battle()
{
    var bgm = Flow.Spawn(PlayBgm());              // a child of Battle: stops when Battle ends
    FlowWorld.Current.Run(SendAnalytics());       // a root flow: runs on after Battle ends
    await Fight();
}   // Battle returns here, and PlayBgm is unwound
```

| How you write it | Parent | When the calling scope ends | Where exceptions go |
|---|---|---|---|
| `await task` | The current scope | (it waits until the task ends) | That await |
| `Flow.Spawn(task)` | The current scope | Unwound | Cancels the calling scope, then rethrown at its caller |
| `world.Run(task)` | The World's root | Keeps running | `OnUnhandledException` |

- A parent of a `Flow.Spawn` child can return without waiting for the child. When it returns, any children still running are unwound. To wait for a child to end, write `await handle.Join()`.
- How a `Flow.Spawn` child's exception reaches its parent is covered in [Handling failures](failures.md).
- When porting UniTask's `Forget()`, use `FlowWorld.Run` if the work should outlive the scope, and `Flow.Spawn` if it may stop together with the scope.
- To get the World from inside a flow, use `FlowWorld.Current`. Outside a flow (in an event handler, for example), call `Run` on a World you kept a reference to.

> **Note**: Analyzer rule FLOW008 warns about a `Flow.Spawn(x);` statement that doesn't use the handle. If the child may end with its parent, write `_ = Flow.Spawn(x);`. If it should outlive the parent, use `FlowWorld.Run`. When a child that you meant to outlive its parent is cut off by the parent ending, nothing warns you at run time.

> **Note**: If you call `Flow.Spawn` inside an event handler, the new flow becomes a child of whichever flow was running when the event fired (if no flow was running, you get a `FlowMisuseException`). Turn the event into a signal with `FlowBridge.FromCallback`, or start the flow with `Run` on a World you kept (FLOW009).

### Run on another World

A flow you start from inside a flow with `Run` on another World is a child of that World's root. It isn't unwound when the calling scope ends. To end it together with the scope, register it as a cleanup.

```csharp
var h = physicsWorld.Run(Simulate());
Flow.AddCleanup(h.Cancel);   // ends with this scope
```

## FlowHandle

`Flow.Spawn` and `FlowWorld.Run` return a handle to the task they started (`FlowHandle` or `FlowHandle<T>`).

```csharp
var download = world.Run(Download());

// later, from the game's code
if (cancelPressed) download.Cancel();
if (download.IsCompleted && download.Status == FlowStatus.Succeeded) ShowDone();
```

| Member | Description |
|---|---|
| `Status` | `Running`, `Succeeded`, `Canceled`, `Faulted`, and so on |
| `IsCompleted` | Whether the task has ended |
| `Cancel()` | Cancels the task and its descendants. Callable from any thread |
| `CancelCause` | Whether cancellation was requested, and why |
| `Exception` | The exception that ended the task, when it ended with one (`Faulted`). Otherwise null |
| `Result` | The value when it succeeded (`FlowHandle<T>`). Throws otherwise |
| `Join()` | A `FlowTask` that waits until the task ends. Usable only once per handle |
| `AsTask()` | A `Task` for code outside flows to wait on |

- A handle itself can't be awaited. To wait, use `await handle.Join()`.
- `Join()` throws `FlowJoinException` when the other task ends by cancellation or by an exception. When it ended by an exception, that exception is the `InnerException`. Joining a handle that never started (a Spawn refused in a canceled scope, or a default handle) throws a `FlowJoinException` whose message says it was never started. To wait regardless of how it ends, use `await FlowTask.WaitUntil(handle, h => h.IsCompleted)` and then check `Status`.
- Joining one handle twice makes the second await throw `FlowMisuseException`. A Join that lost a Race and was unwound also counts as one. To wait again, use `WaitUntil`.
- After you cancel, `Status` stays `Running` until the cleanup (awaits in `finally`, and so on) has finished. If `CancelCause` is anything other than `None`, the task is in the middle of closing ([Scopes and cancellation](scopes-and-cancellation.md)).
- `Join()` is an ordinary FlowTask, so you can compose it, as in `FlowTask.Race(handle.Join(), FlowTask.WaitForSeconds(5))` ([Composition](composition.md)).
- Joining yourself or an ancestor would never end, so it throws `FlowMisuseException`. Flows joining each other in a cycle are not detected. If things get stuck, look for it in the dump ([Debugging](../tools/debugging.md)).

### Receiving the result outside a flow

Code outside a flow (the game loop, or a Task method) receives the end of a flow in one of these ways.

- **Check whether it has ended**: after `IsCompleted` becomes true, read `Status` and `Result`. This is how you check from per-frame code.
- **Tick while you wait**: in a .NET program without an engine, call Tick inside a `while (!handle.IsCompleted)` loop, and read `Result` once it has ended ([Your first flow](../getting-started/first-flow.md)). In tests, `RunUntilComplete` does the same in virtual time ([Testing](../tools/testing.md)).
- **Await it**: from a Task method, wait with `await handle.AsTask()`; from a UniTask method, with `await handle.ToUniTask()` ([Task and ValueTask](../integrations/task.md), [UniTask](../integrations/unitask.md)).

If you block on the World's thread, as with `handle.AsTask().Result` or `.Wait()`, the Tick that advances the World stops too, so it never finishes. Block only from another thread ([Threads](threads.md)).

When and on which thread the `AsTask()` Task completes is covered in [Task and ValueTask](../integrations/task.md).

## You can await only inside FlowTask methods

You can await a FlowTask only inside a FlowTask method running in a World. To start a flow from outside, use `FlowWorld.Run`.

```csharp
// Wrong: an async Task method that awaits a flow
async Task OnClick() => await OpenShop();   // FLOW005; at run time, a misuse

// Right
void OnClick() => world.Run(OpenShop());

// Right, when Task code needs the end of the flow
async Task OnClickAsync() => await world.Run(OpenShop()).AsTask();
```

- When an `async Task`, `ValueTask`, UniTask, or `async void` method awaits a FlowTask and suspends, it is treated as misuse. That method never resumes, and the calling scope ends with a `FlowMisuseException`. Analyzer rule FLOW005 stops this at compile time.
- The other direction is misuse too: directly awaiting a Task, a UniTask, or an engine awaitable inside a FlowTask method. Wait on external awaitables through a bridge. That way, the scope's cancellation reaches `ct` (FLOW002; [Task and ValueTask](../integrations/task.md)).

```csharp
var json = await FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct));   // the scope's cancel reaches ct
```

## Tick and Flush

Completions and `Emit` don't resume the waiting flow right away. The resume is scheduled, and all scheduled resumes are processed together at the next `Tick` or `Flush`.

```csharp
var start = new Signal<FlowUnit>();
world.Run(WaitForStart(start));   // runs up to `await start.Next()`
start.Emit(FlowUnit.Default);     // reserves the resume; WaitForStart has not resumed yet
world.Flush();                    // resumes it now, without advancing time
```

`Tick(dt)` takes in what other threads have sent, advances time, and then processes the satisfied waits and the scheduled resumes in order. `Flush()` does only the intake and the resumes, without advancing time. The Unity integration calls Flush at several points within a frame so that input from that frame is handled in the same frame ([Unity setup](../unity/setup.md)). The steps of a Tick and the order of resumes are covered in [Execution model](../advanced/execution-model.md).

- You can't pass a negative value, NaN, or infinity to `Tick` (`ArgumentOutOfRangeException`).
- You can't call `Tick`, `Flush`, or `Dispose` from flow code, cleanup, or handlers such as `OnUnhandledException` (`FlowMisuseException`).
- Call `Tick`, `Flush`, and `Run` from the thread that created the World ([Threads](threads.md)).

## Closing the World

`world.Dispose()` cancels and unwinds every flow. Dispose doesn't run Ticks, so awaits in cleanup don't run to the end. If some cleanup must run to the end, such as saving, cancel the flows first, run Ticks, and then Dispose ([Handling failures](failures.md)).

## Names

By default, dumps and scope paths show method names. When you run many copies of the same method, tell them apart with `Flow.Named`.

```csharp
_ = Flow.Spawn(Flow.Named($"Enemy#{id}", EnemyAI(enemy)));
```

Inside a flow, you can log where you are with `Flow.CurrentScopePath` (in the form `Game > InGame > Battle`). `Flow.IsInFlow` tells you whether the current code is running inside a flow.

## Rules and pitfalls

- `default(FlowTask)` is a completed task (`FlowTask.CompletedTask`). `default(FlowTask<T>)` is not a task, and awaiting it or passing it to a combinator throws `FlowMisuseException`. Create a completed task that returns a value with `FlowTask.FromResult(value)`.
- Writing `GetAwaiter()` in your code starts the task immediately, even without an await. The awaiter's `IsCompleted` keeps the value it had when you got it. Don't write `GetAwaiter()`; use await (FLOW007).
- Flows don't capture the ExecutionContext, so an `AsyncLocal<T>` value set inside a flow doesn't belong to that flow, and nothing guarantees that the flow sees the same value after an await. Pass per-flow values as arguments ([Execution model](../advanced/execution-model.md)).
- Differences from Task and UniTask are covered in [UniTask](../integrations/unitask.md) (term mapping and rewrites) and the [Glossary](../advanced/glossary.md).
