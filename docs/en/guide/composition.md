# Composition: Race and WhenAll

This page explains how to combine several flows. Write sequences with await, "any one of them" with `FlowTask.Race`, and "all of them" with `FlowTask.WhenAll`. Timeouts, and stopping a load or a network call with a button, are written with Race too.

## Sequences are just await

```csharp
async FlowTask Stage()
{
    await Intro();
    await Battle();
    await Outro();
}
```

If you only wait for things in order, you don't need any composition API.

## Race: take the first to finish, stop the rest

`FlowTask.Race` makes the first branch to finish the winner, cancels and unwinds the other branches, and then resumes the caller.

```csharp
async FlowTask<bool> Confirm()
{
    var r = await FlowTask.Race(okButton.Next(), cancelButton.Next(), FlowTask.WaitForSeconds(10));
    return r.Index == 0;   // OK pressed; Cancel or 10 s means no
}
```

- A Race with 2 or 3 branches returns a `RaceResult<T0, T1>` (for 3 branches, `RaceResult<T0, T1, T2>`) that keeps each branch's type. `Index` is the number of the winning branch (zero-based). `TryGet0(out v)` and friends get the value when that branch won, and `Value0` and friends throw if that branch didn't win. A branch that returns no value (`FlowTask`) has the value `FlowUnit`.
- For 4 or more branches, pass an `IReadOnlyList` such as an array. A list of `FlowTask` returns the winner's index (`FlowTask<int>`), and a list of `FlowTask<T>` returns a `RaceResult<T>` (`Index` and `Value`).
- Each branch runs in a child scope under the combinator.

```csharp
var r = await FlowTask.Race(hits.Next(), Patrol(self));
if (r.TryGet0(out var hit)) await HitStun(self, hit);
```

### Losers unwind before the caller resumes

Losing branches unwind, just as with a cancel (`CancelCause.RaceLost`). `using` and `finally` run, and awaits in `finally` run to the end. The caller resumes only after all cleanup in the losing branches has finished.

```csharp
async FlowTask Patrol(Enemy self)
{
    var effect = self.ShowAlert();
    try
    {
        while (true) await self.WalkTo(NextPoint());
    }
    finally
    {
        await effect.FadeOut();   // the caller of the Race resumes after this
    }
}
```

If you want to move on without waiting for that cleanup, start the cleanup as a separate flow with `FlowWorld.Current.Run(…)`. The rules for awaiting in cleanup are covered in [Scopes and cancellation](scopes-and-cancellation.md).

### Branches start in the order you write them

Branches run one at a time, in argument order, each up to its first await that suspends. If a branch finishes synchronously when it starts, that branch wins and the remaining branches never start.

When several branches finish in the same frame, the winner is decided by processing order, which is usually the order you wrote them in. (When two deadlines pass in one Tick, the order in which those time waits began decides, not which deadline was earlier; when the branches are the time waits themselves, that is the written order. See [Execution model](../advanced/execution-model.md).) So write the interrupt (waiting for an event) first, and the work it interrupts after it.

```csharp
// Good: a hit already buffered in the subscription wins before Patrol moves
var r = await FlowTask.Race(hits.Next(), Patrol(self));

// Worse: Patrol starts first and takes one step even if a hit is waiting
var r = await FlowTask.Race(Patrol(self), hits.Next());
```

### Values a losing branch had received

When a subscription's (`Subscription<T>`) `Next()` loses a Race, a value that wait had already received goes back to the front of the subscription. A hit that arrived in the same frame can be taken by the next `hits.Next()`. The same goes for waits in a WhenAll branch that failed.

- A value put back follows the subscription's overflow policy. If the buffer is full, `DropOldest` and `Latest` drop the value that was put back, and `DropNewest` drops the newest value. `Fail` keeps it beyond capacity and doesn't treat it as an overflow.
- A value received by an edge `Signal.Next()` that lost is gone. Wait for input you can't afford to miss through a subscription ([Signals](signals.md)).
- A `Next()` wrapped in `WithoutResult()` also puts its value back when it loses.
- Only values not yet taken into a combinator's result are put back. If an inner Race has already taken a value into its own result, and that result then loses an outer Race and is thrown away, the value is not put back.
- How bridge values are handled (`onDiscard`) is covered in [Task and ValueTask](../integrations/task.md).

## Timeouts

There's no dedicated timeout API. Race the work against `WaitForSeconds`.

```csharp
var r = await FlowTask.Race(Download(), FlowTask.WaitForSeconds(10));
if (!r.TryGet0(out var data))
{
    ShowTimeout();   // Download was unwound before this line
    return;
}
```

Time advances on the scope's Clock, so the timeout also stops while the game is paused. If you don't want it to stop, pass a UI Clock or `world.UnscaledClock`: `FlowTask.WaitForSeconds(10, world.UnscaledClock)` ([Time and Clocks](time-and-clocks.md)).

Every Clock advances by the time passed to `Tick`, so it isn't necessarily real time (long frames are clamped, and time doesn't advance while the app is suspended). For limits you want to measure in real time, such as a network timeout, use the external side's own mechanism (`HttpClient.Timeout`, `CancellationTokenSource.CancelAfter`, Unity's `UnityWebRequest.timeout`). Measure in game time the limits that come from the game itself (such as answering No if the player doesn't answer within 10 seconds).

To wait for a flow to end with a time limit, you can Race a `Join` too.

```csharp
var r = await FlowTask.Race(handle.Join(), FlowTask.WaitForSeconds(5));
```

## Stopping a load or a network call with a button

Bridged external work can also be a branch of a Race. The token of a losing bridge is canceled.

```csharp
var r = await FlowTask.Race(
    cancelButton.Next(),
    FlowBridge.FromTask(ct => File.ReadAllBytesAsync(path, ct)).ToFlowTask());   // the read stops when the button wins
if (!r.TryGet1(out var bytes)) return;
```

- Pass a bridge with a result with `.ToFlowTask()` added (without it, the type argument can't be inferred).
- Work that doesn't take a token (such as an Addressables load) doesn't stop, and runs to the end. To release a result that arrives, pass `onDiscard` ([Task and ValueTask](../integrations/task.md), [Unity bridges](../unity/bridges.md)).
- When the flow that contains the Race ends, for example because the screen closes, the token is canceled in the same way.

## When you don't want the losers stopped

Losing branches of a Race always stop. If you only want to know which finishes first without stopping the others, start the work with `Flow.Spawn` and Race the `Join`s. Only the losing `Join` wait unwinds; the work itself goes on.

```csharp
var a = Flow.Spawn(LoadA());
var b = Flow.Spawn(LoadB());
var first = await FlowTask.Race(a.Join(), b.Join());   // the other load goes on
```

Spawned work unwinds when the calling scope ends. To keep it going longer than the scope, start it with `FlowWorld.Run` ([Flows and the World](flows-and-world.md)).

> **Note**: A handle can be joined only once, and a Join that lost a Race counts as one. To wait later for the losing work to end, wait with `FlowTask.WaitUntil(b, h => h.IsCompleted)` and read `b.Result`.

## WhenAll: wait for all of them

`FlowTask.WhenAll` waits until every branch has finished.

```csharp
var (map, units) = await FlowTask.WhenAll(LoadMap(), LoadUnits());
await FlowTask.WhenAll(FadeOutBgm(), FadeOutScreen());   // branches without values
```

- With 2 or 3 branches, it returns the values as a tuple (one-based, starting at `Item1`). A branch that returns no value has the value `FlowUnit`. If no branch returns a value, WhenAll returns no value either.
- For 4 or more branches, pass an `IReadOnlyList`. A list of `FlowTask<T>` returns an array of values (`T[]`).

When one branch ends with an exception, WhenAll cancels and unwinds the remaining branches (`CancelCause.Fault`), and throws the exception from the await after their cleanup has finished. Unlike Task's `WhenAll`, it doesn't keep the rest running, and it doesn't aggregate exceptions. The second and later exceptions are reported as `Undelivered` ([Handling failures](failures.md)).

To stop the rest at an expected failure, such as a failed load, throw that failure as a dedicated exception type too, and catch that type around the WhenAll (see "Expected failures" in [Handling failures](failures.md)). If you want the rest to run to the end even when one fails, and collect the outcome of each branch, catch the exception inside each branch and return the outcome as a value.

## Dropping values: WithoutResult

When you don't need a branch's value, for example to put 4 or more branches of different types into one `FlowTask` list, turn it into a value-less `FlowTask` with `WithoutResult()`.

```csharp
var winner = await FlowTask.Race(new[]
{
    shopClicks.Next().WithoutResult(),   // a Signal<FlowUnit>
    itemPicked.Next().WithoutResult(),   // a Signal<Item>: its value is dropped
    closed.Next().WithoutResult(),
    FlowTask.WaitForSeconds(30),
});
if (winner == 0) await OpenShop();
```

When a wait wrapped in `WithoutResult()` receives a value (for a Race branch, when that branch wins), the value counts as received by the flow and doesn't go back to the subscription. (Bridge values go to `onDiscard`; see [Task and ValueTask](../integrations/task.md).)

## Rules and pitfalls

- Creating a combinator doesn't start its branches. They start when you await the combinator or pass it to `Flow.Spawn` or `FlowWorld.Run`.
- A task can be passed to only one combinator.
  - Passing a task that has already started (one you awaited) makes the call that creates the combinator throw `FlowMisuseException`.
  - Passing a task that hasn't started yet to two combinators can't be detected at creation time. The await of the combinator started later throws `FlowMisuseException`. The task stays with the combinator that started it first, which goes on as before.
  - Passing the same task twice to one combinator makes the await of that combinator throw `FlowMisuseException`, and the branch that started is unwound. If the earlier branch finishes immediately, though, the combinator settles right there, and the misuse may go unnoticed.
- You can't pass a task wrapped in `Flow.NonCancelable` to a combinator (`FlowMisuseException`).
- If starting a branch fails (for example, a wait on a Clock that can't be used), the combinator unwinds the branches it has started so far and throws the exception from the await.
- In a Race that settles every frame (such as racing each frame's input against some work), make the losing branch a leaf wait (`NextFrame`, `WaitForSeconds`, `Next`, `WaitUntil`) rather than an async method. When an async method loses, you pay the cost of the unwinding exception every time ([Performance](../advanced/performance.md)). For outcomes that happen only now and then, such as taking a hit or a screen transition, this isn't a problem.
- There are no APIs that correspond to `WhenAny` or `Timeout`. Write both with Race (a timeout is a Race between the work and `FlowTask.WaitForSeconds`).
