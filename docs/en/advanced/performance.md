# Performance and memory

This page covers when FlowTask allocates and when it doesn't, what cancellation costs, and how the pool behaves. Read it when you write flows that run every frame, or when you design a screen transition that stops many flows at once.

## Make a branch that loses every frame a leaf wait

First, the guideline that matters most. In a `Race` that settles every frame, make the losing branch a leaf wait (`NextFrame`, `WaitForSeconds`, `Signal.Next`, a subscription's `Next`, `WaitUntil`), not an async method.

```csharp
// Costly: every frame the async method loses, and unwinds by throwing an exception
while (true)
{
    var r = await FlowTask.Race(WaitForJump(jump), FlowTask.NextFrame());
    if (r.Index == 0) Jump();
    UpdateStamina();
}

static async FlowTask<int> WaitForJump(Signal<int> jump) => await jump.Next();
```

```csharp
// Cheap: both branches are leaf waits; nothing is thrown or allocated
while (true)
{
    var r = await FlowTask.Race(jump.Next(), FlowTask.NextFrame());
    if (r.Index == 0) Jump();
    UpdateStamina();
}
```

Canceling an async method unwinds it by throwing an exception at the await where it is suspended ("The cost of cancellation" below). Unwinding a leaf wait doesn't use exceptions. For outcomes that happen only now and then, like taking a hit or a screen transition, an async method branch is fine.

## When FlowTask doesn't allocate

In steady state, the following don't allocate on the heap.

- Per-Tick waits (`NextFrame`, `DelayFrames`, `WaitForSeconds`, `WaitUntil`)
- Loops that stay in the same scope (including ones that await child FlowTask methods inside the loop)
- Sending and receiving signals, and subscriptions
- Settling a `Race` whose losers are leaf waits, and a `Race` that returns a subscription's value from a loser
- Settling a `WhenAll` with leaf branches
- `FlowProperty`, `Clock.Pause()`

Tests (`AllocationTests`) check this on .NET, NativeAOT, and Unity. On Unity's IL2CPP (a Windows desktop development player, with scripts compiled with optimization), it is also 0 bytes.

State machines and nodes are reused through per-type pools ("Pool" below).

## When FlowTask allocates

The following allocate every time.

- The root of a flow started with `FlowWorld.Run` or `Flow.Spawn`. It isn't pooled, so that you can read its state from the handle at any time.
- Bridges: `FlowBridge.FromTask`, `.AsFlow()`, `FlowBridge.FromCallback`. For per-frame waits, use FlowTask's waits, not bridges ([Task and ValueTask](../integrations/task.md)).
- `Flow.CreateClock`.
- Unwinding an async method (next section).
- One failure: one failure record, one `FlowExceptionInfo`, one `ExceptionDispatchInfo`, and one path string. When an exception travels up a chain of awaits, each level that rethrows it also allocates. A chain that ends in success doesn't allocate, however many levels it has.
- Your code compiled in Debug. The compiler makes the state machines classes. This also applies to Unity's Development Build and to the editor when Code Optimization is set to Debug ([Unity setup](../unity/setup.md)). Measure allocations in an optimized build.

## The cost of cancellation

A canceled async method throws a new `FlowCanceledException` once at the await where it is suspended, and exits while running `finally` and `using`. This happens once per suspended async method. It doesn't become a chain that rethrows the exception at each level. Unwinding a leaf wait throws no exception and allocates nothing.

Here is a rough cost per method. All of these were measured on a desktop PC.

| Runtime | Time per method | Allocation per method |
|---|---|---|
| .NET 10 (JIT, Release) | about 1.2 µs | 200 to 300 B |
| .NET 8 (JIT; the version Godot 4.4 games target) | about 5 µs | about 208 B |
| Unity IL2CPP (Windows development player, scripts compiled with optimization) | about 14 to 24 µs | about 1 KB |
| Leaf wait (for reference) | about 20 ns | none |

- **.NET 10**: measured with the BenchmarkDotNet `Cancellation` benchmark (`tests/FlowTask.Benchmarks`, .NET 10.0.10). It cancels 100 suspended async methods that each have a `try`/`finally` at once, and takes (canceled − completed normally) / 100 as the cost per method. The side-by-side shape (`WhenAll`) came to 1.18 µs and 224 B, and the nested shape to 1.20 µs and 224 B. Almost all of the cost is throwing and catching the exception. Of the allocation, 128 B is the exception object, and the rest is space for the stack trace that the runtime captures on every throw. The latter is 72 B when the throwing await is inlined into MoveNext, and grows with the JIT tier and the shape of the method.
- **IL2CPP**: every throw captures the stack trace again, walking the whole thread stack to do so, so deeper calls cost more. Reusing the exception instance only saves the exception object.
- There are no IL2CPP measurements on real devices such as smartphones. Measure on your target devices.

### Estimating a screen transition

Closing a screen with about 500 suspended async methods at once took 6 to 12 ms on the IL2CPP development player above (on a desktop PC). On the .NET JIT, it takes less than 1 ms. It hasn't been measured on real devices such as smartphones, so the cost there is not known.

The mechanism is the same as the default in UniTask and Task (unwinding by throwing `OperationCanceledException`). The code that interrupts gets which branch won from the Race result (`Index`), so it can branch without an exception. Only the async methods suspended inside the losing branch unwind with an exception ([Composition](../guide/composition.md)). There is no await that returns "was it canceled?" without throwing, like UniTask's `SuppressCancellationThrow`. The reasons are in [Design rationale](design-rationale.md).

## Pool

- State machines and nodes are reused through per-type pools.
- The limit is 1,024 per type. Anything released beyond that at the same time is left to the GC. There is no setting.
- The pools are shared by all Worlds and threads. Disposing a World doesn't empty them; they stay until the process (on Unity, the domain) ends. The worst-case leftover is number of node types used × 1,024 × node size.
- There is no API to empty the pools.

If you end more than 1,024 flows of the same type at once (a swarm of enemies leaving, for example), the ones over the limit are allocated again when they next start.

## Measuring it yourself

The repository has BenchmarkDotNet benchmarks in `tests/FlowTask.Benchmarks`. The FlowTask benchmarks drive the World the same way the engines do, with `Emit` followed by `Tick`, and include comparisons with UniTask.

```sh
dotnet run -c Release --project tests/FlowTask.Benchmarks -- --filter '*Cancellation*'
```

When you measure your own game, measure in Release (on Unity, a player that is not a Development Build) and on the target runtime (IL2CPP and so on). As the table above shows, the cost of cancellation differs by more than 10 times between the JIT and IL2CPP.
