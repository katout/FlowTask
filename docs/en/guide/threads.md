# Threads

A World and its flows run on a single thread. To run heavy computation or I/O on another thread, write it with Task and wait for it through a bridge. The flow only waits, and never leaves the World's thread. This page explains which thread a World and objects are tied to, how to run work on another thread, how to pass values and work to a World from other threads (`EmitFromAnyThread`, `Post`, `Cancel`), and how to receive a flow's result on another thread.

## A World runs on the thread that created it

A `FlowWorld` is tied to the thread that created it. All flow code runs on that thread, inside `Tick` or `Flush`. You don't need locks.

```csharp
// Wrong: the World belongs to the main thread
await Task.Run(() => world.Run(Load()));   // FlowThreadException
```

- Calling `Tick`, `Flush`, `Run`, `Dispose`, or `CreateClock` from another thread throws `FlowThreadException`.
- `world.IsBoundToCurrentThread` tells you whether the current thread is the World's thread.
- You can create several Worlds on one thread and tick them in turn.
- Write the loop that calls Tick so that it doesn't leave the World's thread. In a console `async Main`, if you put `await Task.Delay(16)` between Ticks, the code continues on the thread pool after the await, so the next Tick throws `FlowThreadException`. Wait with `Thread.Sleep` ([Your first flow](../getting-started/first-flow.md)), or create a thread just for the loop.

## Running work on another thread

Write heavy computation and I/O with Task, and wait for it through a bridge. The Task can run on any thread. The flow receives the result on the World's thread.

```csharp
var map = await FlowBridge.FromTask(ct => Task.Run(() => GenerateMap(seed, ct), ct));   // resumes on the World thread
```

- A bridged Task is queued in the World's inbox no matter which thread it completes on, regardless of the SynchronizationContext. The flow resumes at the intake of the next Tick or Flush.
- When the scope ends (including when it loses a Race and when its parent ends), `ct` is canceled. Whether the token is checked is up to the work, so call `ct.ThrowIfCancellationRequested()` partway through a long computation.
- Inside work on another thread, don't write to objects the World uses (`Signal`, `FlowProperty`). Return the result and write it on the flow side, or pass it with the APIs in "Passing things to a World from another thread" below.

How to use bridges (exceptions, external cancellation, cleaning up results nobody received) is covered in [Task and ValueTask](../integrations/task.md).

## Object binding

`Signal`, `FlowProperty`, and `Once` are tied to the first World that uses them and to its thread. This is called binding.

```csharp
var hp = new FlowProperty<int>(LoadSavedHp());   // no World has used it yet: no check
world.Run(WatchHealth(hp));                       // WatchHealth waits on hp: bound to world's thread

await Task.Run(() => hp.Set(0));                  // FlowThreadException
```

- An object is bound to a World's thread at the point that World uses it (a wait inside a flow, a subscription, creation inside a flow, `new Signal<T>(world)`). It isn't bound to the thread that created it.
- Objects that no World has used yet aren't checked. Like ordinary .NET objects, use them from a single thread. It's fine to `Set` an initial value on a loading thread and then hand the object to the World.
- After binding, writes from another thread (`Emit`, `Set`, a subscription's `TryTake` or `Dispose`) and use from a World on another thread throw `FlowThreadException`. Reads (`Value` and so on) aren't checked.
- `FlowProperty.Changed` shares the binding of the `FlowProperty` it belongs to.
- Other Worlds on the same thread can use the object (the binding stays with the first World). If you share an object across several Worlds, keep the World that uses it first alive longer than the others. After the World it's bound to is disposed, the object can't be used anymore, so you usually create these per session ([Signals](signals.md)).

## Passing things to a World from another thread

Of the operations that act on a World, these four can be called from another thread. In addition, the callback you register through `FlowBridge.FromCallback`, and `Dispose` of the `EventSignal` it returns, can also be called from another thread ("Rules and pitfalls" below).

| API | What it does |
|---|---|
| `signal.EmitFromAnyThread(value)` | Emits a value |
| `signal.CloseFromAnyThread(error)` | Closes the Signal |
| `world.Post(action)` | Runs work on the World's thread |
| `handle.Cancel()` | Cancels a flow |

Each of these is queued in the World's inbox and processed in queued order at the start (the intake) of the next `Tick` or `Flush`. Items sent from the same thread are processed in the order they were sent, regardless of kind.

```csharp
var downloaded = new Signal<byte[]>(world, "Downloaded");   // bound to world: ready for other threads

// on a worker thread
downloaded.EmitFromAnyThread(bytes);

// a flow on the World thread
var data = await downloaded.Next();
```

### EmitFromAnyThread and CloseFromAnyThread

- `EmitFromAnyThread` queues the value in the inbox of the World the Signal is bound to. On a Signal not yet bound to any World, there's nowhere to queue it, so it throws `FlowMisuseException`. Create a Signal you'll send to from other threads with `new Signal<T>(world)`, or use it on the World's thread first (create it inside a flow, or wait on or subscribe to it inside a flow). `Subscribe` or `Emit` outside a flow alone doesn't bind it.
- `CloseFromAnyThread` takes effect after the Emits sent before it. On a Signal not yet bound to any World, it marks the Signal as closed immediately.
- Sends after the bound World has been disposed are dropped without an exception.

### Post

`world.Post(action)` runs `action` on the World's thread, during the intake of the next Tick or Flush. Use it when you need to write something like `FlowProperty.Set`, or start a flow, from another thread.

```csharp
// a callback on a worker thread
void OnLoaded(int value) => world.Post(() => property.Set(value));

// an event that may fire on another thread starts a flow on the World thread
void OnPurchased(Receipt r) => world.Post(() => world.Run(ShowReceipt(r)));
```

- `action` doesn't belong to any scope. An exception it throws is reported to `OnUnhandledException` as `Unhandled`, and the intake continues.
- You can't call `Tick`, `Flush`, or `Dispose` from inside `action`.
- Work posted after the World has been disposed is dropped without running.

### Cancel from another thread

`FlowHandle.Cancel()` can be called from any thread. A Cancel from another thread is confirmed at the intake of the next Tick or Flush, and unwinds at the start of that same flush. Until then, `Status` doesn't change. If the World has been disposed or the task has already ended, it does nothing. How to register it with an external `CancellationToken` is in [Task and ValueTask](../integrations/task.md).

## Receiving a flow's result on another thread

Code outside a flow waits for the flow to end with the handle's `AsTask()`.

- Call `AsTask()` on the World's thread (from another thread it throws `FlowThreadException`). To hand it to code on another thread, pass the Task you got on the World's thread from the World's thread side.
- That Task completes on the World's thread, at the end of the Tick, Flush, Run, or Dispose in which the flow ended. Continuations that don't capture a context (ones awaited on the thread pool, or with `ConfigureAwait(false)`) run synchronously right there, still on the World's thread. Move heavy work off with `Task.Run` or similar.
- On another thread, it's fine to block on that Task (`Wait()` or `.Result`). It completes as long as the World's thread keeps ticking. If you block on the World's thread, Tick stops too, so it never finishes ([Flows and the World](flows-and-world.md)).
- You can't `Join` a flow in a World on another thread (the await throws `FlowThreadException`). Get `AsTask()` on the other World's thread, and wait on it through a bridge.

Details of `AsTask()` are in [Task and ValueTask](../integrations/task.md).

## Rules and pitfalls

- An event bridge (`FlowBridge.FromCallback`) can also receive callbacks called from other threads (their values arrive through the inbox). A bridge created outside a flow does so once it is bound to the first World that uses it; until then it is unchecked and has no inbox, like any other object no World uses yet, so do not let another thread call it before it is first used on the World's thread.
- Flows don't capture the ExecutionContext; they run in the ExecutionContext of whoever called Tick or a similar method. An `AsyncLocal<T>` value set inside a flow doesn't belong to that flow: other flows and the caller of Tick see it too, and nothing guarantees that the flow sees the same value after an await. Pass per-flow values as arguments ([Execution model](../advanced/execution-model.md)).
- For values coming from other threads, which Tick takes them in depends on thread timing. How to record input so you can reproduce it is in [Debugging](../tools/debugging.md).
- How the main thread relates to the World in Unity and Godot is in [Unity setup](../unity/setup.md) and [Godot setup](../godot/setup.md).
