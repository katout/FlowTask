# Your first flow

In this tutorial, you build a small flow step by step in a .NET console program. You'll try, in order, a loop that creates a World and ticks it, waiting for time, waiting for a key press, a timeout with `FlowTask.Race`, and the `finally` of the losing branch running.

By the end, you'll have a program that does the following. Press a key during a 5-second countdown to start; if you don't, time runs out.

```text
Press any key to start.
5
4
3
Countdown ended.
Start! (Enter)
```

You don't need an engine. This page assumes a project created by `dotnet new console` from the .NET 6 or later SDK (with implicit `using` directives enabled).

## Create the project

```sh
dotnet new console -n FirstFlow
cd FirstFlow
dotnet add package FlowTask --version 0.1.0-preview.1
```

From here on, you'll rewrite the contents of `Program.cs`.

## Create a World and tick it

Change `Program.cs` to the following. It's a flow that counts down once per second and then prints "Go!".

```csharp
using System.Diagnostics;
using Katout.FlowTask;

using var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // without a handler, the outermost Tick, Flush, Run or Dispose throws the exception
var countdown = world.Run(Countdown(3));

var time = Stopwatch.StartNew();
var last = 0.0;
while (!countdown.IsCompleted)
{
    Thread.Sleep(16);                               // one frame of your loop
    var now = time.Elapsed.TotalSeconds;
    world.Tick(Math.Min(now - last, 0.1));          // unscaled seconds since the last Tick; long frames clamped
    last = now;
}

static async FlowTask Countdown(int from)
{
    for (var i = from; i > 0; i--)
    {
        Console.WriteLine(i);
        await FlowTask.WaitForSeconds(1);
    }
    Console.WriteLine("Go!");
}
```

Run it with `dotnet run`. It prints `3`, `2`, `1`, and `Go!` one second apart, and exits.

Let's go through the pieces.

- **`FlowWorld`**: the unit that runs flows (called the World). It holds the tree of flows, time (Clocks), and the resumption queue. With `using`, it is disposed at the end of the program.
- **`async FlowTask`**: an async method that returns `FlowTask` becomes a FlowTask flow. Apart from returning `FlowTask` instead of Task, you write it like any async method.
- **`world.Run(task)`**: starts the flow and returns a `FlowHandle`. The flow runs right away, inside `Run`, up to its first await that suspends. Unlike Task, just calling `Countdown(3)` doesn't start anything.
- **`world.Tick(dt)`**: advances time by `dt` seconds and resumes flows whose waits are satisfied. Call it once per frame in your game loop. Pass elapsed time without any scale applied, and clamp frames that are too long (for example, when you stop at a breakpoint).
- **`FlowTask.WaitForSeconds(1)`**: waits 1 second of World time. It advances by the time passed to `Tick`, not by real time.
- **`OnUnhandledException`**: receives exceptions that no flow catches. If you don't set it, the outermost `Tick`, `Flush`, `Run` or `Dispose` throws that exception as a `FlowUnhandledException`.

> **Note**: time waits don't advance unless you Tick. If a flow seems stuck, first check that you are calling Tick.

> **Note**: in the loop, don't use `await Task.Delay(16)` instead of `Thread.Sleep`. After the await, the code continues on the thread pool, so the next Tick throws `FlowThreadException`. A World can only be advanced on the thread that created it ([Threads](../guide/threads.md)).

## Turn input into a Signal

Next, let's make it possible to wait for key input. To announce an event, use `Signal<T>`. In the loop, `Emit` the keys that were pressed.

```csharp
var keys = new Signal<ConsoleKey>();
```

```csharp
while (...)
{
    Thread.Sleep(16);
    while (Console.KeyAvailable) keys.Emit(Console.ReadKey(intercept: true).Key);   // input of this frame
    // ... Tick as before
}
```

On the flow side, awaiting `keys.Next()` waits for the next Emit.

- Emit doesn't resume waiting flows immediately. The resumption is scheduled and processed in a fixed order inside the next `Tick`.
- `Next()` is an edge that waits for "the next one". An Emit while nobody is waiting is dropped. If you don't want to miss any, use a subscription ([Signals](../guide/signals.md)).

> **Note**: `Console.KeyAvailable` throws `InvalidOperationException` when there is no console, or when input is redirected. Run it with `dotnet run` from a terminal.

## Wait for input with a timeout using Race

Write a flow that waits for a key. If no key is pressed within 5 seconds, time runs out.

```csharp
static async FlowTask Title(Signal<ConsoleKey> keys)
{
    Console.WriteLine("Press any key to start.");
    var r = await FlowTask.Race(keys.Next(), FlowTask.WaitForSeconds(5));
    if (r.TryGet0(out var key)) Console.WriteLine($"Start! ({key})");
    else Console.WriteLine("Time over.");
}
```

`FlowTask.Race` starts the branches in the order you write them, and the first branch to finish wins. The losing branches are stopped.

- The result, a `RaceResult`, holds the index of the winning branch (`Index`) and its value. `TryGet0` returns true when branch 0 won, and gives you its value (here, the key that was pressed).
- There is no special API for timeouts. In FlowTask, you write a timeout by racing the work against `FlowTask.WaitForSeconds`.
- When both are satisfied in the same Tick, the branch whose completion is processed first wins. That is usually the branch written first ([Composition](../guide/composition.md)). Put interrupts such as input first.

Change the top of the program to run `Title`.

```csharp
using var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);

var keys = new Signal<ConsoleKey>();
var title = world.Run(Title(keys));

var time = Stopwatch.StartNew();
var last = 0.0;
while (!title.IsCompleted)
{
    Thread.Sleep(16);
    while (Console.KeyAvailable) keys.Emit(Console.ReadKey(intercept: true).Key);
    var now = time.Elapsed.TotalSeconds;
    world.Tick(Math.Min(now - last, 0.1));
    last = now;
}
```

Run it. If you press a key within 5 seconds, it prints `Start! (…)`; if you don't, it prints `Time over.` after 5 seconds.

## See the losing branch's finally run

Replace the timeout branch with a countdown that shows the remaining seconds. Give the countdown a `try`/`finally`.

```csharp
static async FlowTask Title(Signal<ConsoleKey> keys)
{
    Console.WriteLine("Press any key to start.");
    var r = await FlowTask.Race(keys.Next(), Countdown(5));
    if (r.TryGet0(out var key)) Console.WriteLine($"Start! ({key})");
    else Console.WriteLine("Time over.");
}

static async FlowTask Countdown(int from)
{
    try
    {
        for (var i = from; i > 0; i--)
        {
            Console.WriteLine(i);
            await FlowTask.WaitForSeconds(1);
        }
    }
    finally
    {
        Console.WriteLine("Countdown ended.");
    }
}
```

If you press a key in the middle of the countdown, you get this:

```text
Press any key to start.
5
4
3
Countdown ended.
Start! (Enter)
```

When the key branch wins, the losing `Countdown` is unwound. `FlowCanceledException` is thrown from the suspended `await FlowTask.WaitForSeconds(1)`, and the `finally` runs before the method exits. Only after that does `Title` resume. That's why `Countdown ended.` appears before `Start!`.

If you don't press a key, the countdown counts to the end and finishes normally, the same `finally` runs, and then `Time over.` appears.

```text
Press any key to start.
5
4
3
2
1
Countdown ended.
Time over.
```

The code being stopped doesn't take a token or check whether it was stopped. If you put cleanup in `finally` (or `using`), it runs both when the code finishes normally and when it is stopped.

> **Note**: if you catch `FlowCanceledException` with `catch (Exception)` or `catch (OperationCanceledException)` and keep going, the flow you meant to stop keeps running. End a `catch` that receives a cancellation with `throw;` (analyzer rule FLOW001 makes this an error). See [Scopes and cancellation](../guide/scopes-and-cancellation.md) for how to write it.

## The finished program

```csharp
using System.Diagnostics;
using Katout.FlowTask;

using var world = new FlowWorld();
world.OnUnhandledException = info => Console.Error.WriteLine(info);   // without a handler, the outermost Tick, Flush, Run or Dispose throws the exception

var keys = new Signal<ConsoleKey>();
var title = world.Run(Title(keys));

var time = Stopwatch.StartNew();
var last = 0.0;
while (!title.IsCompleted)
{
    Thread.Sleep(16);                                // one frame of your loop
    while (Console.KeyAvailable) keys.Emit(Console.ReadKey(intercept: true).Key);
    var now = time.Elapsed.TotalSeconds;
    world.Tick(Math.Min(now - last, 0.1));           // unscaled seconds since the last Tick; long frames clamped
    last = now;
}

static async FlowTask Title(Signal<ConsoleKey> keys)
{
    Console.WriteLine("Press any key to start.");
    var r = await FlowTask.Race(keys.Next(), Countdown(5));
    if (r.TryGet0(out var key)) Console.WriteLine($"Start! ({key})");
    else Console.WriteLine("Time over.");
}

static async FlowTask Countdown(int from)
{
    try
    {
        for (var i = from; i > 0; i--)
        {
            Console.WriteLine(i);
            await FlowTask.WaitForSeconds(1);
        }
    }
    finally
    {
        Console.WriteLine("Countdown ended.");
    }
}
```

## On Unity and Godot

The flow methods (`Title`, `Countdown`) work as they are. What changes is how you set up the World and input.

- **Unity**: the integration creates a World and ticks it every frame from the PlayerLoop. Don't write a loop; start with `FlowTaskUnity.World.Run(Title(keys))`. To match the lifetime of a GameObject, use `gameObject.RunWhileActive(...)`. For input, Emit into a Signal from a UnityEvent or similar ([Unity setup](../unity/setup.md), [Bridges](../unity/bridges.md)).
- **Godot**: a `FlowWorldNode` set up as an autoload ticks the World in `_Process`. Start with `FlowWorldNode.Default.Run(...)`, or with `node.RunWhileInTree(...)` to match the node's lifetime. For input, you can use the Godot signal bridge ([Godot setup](../godot/setup.md), [Signals](../godot/signals.md)).

In both, the World created by the integration writes unhandled exceptions to the engine's log (the Unity console, Godot's error output), so you don't have to set `OnUnhandledException` yourself.

## What to read next

- [Flows and the World](../guide/flows-and-world.md): Run, Tick, Spawn, FlowHandle, deferred start
- [Scopes and cancellation](../guide/scopes-and-cancellation.md): unwinding, awaits inside `finally`, how to write `catch`
- [Composition: Race and WhenAll](../guide/composition.md): Race rules, WhenAll, timeouts
- [Time and Clocks](../guide/time-and-clocks.md): the time you pass to Tick, Pause, time scale
- [Signals](../guide/signals.md): Signal, subscriptions, FlowProperty
- [Task and ValueTask](../integrations/task.md): await files and network calls from a flow, and stop them with a cancel button
