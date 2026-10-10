# What is FlowTask?

FlowTask is a C# library for writing game progression (screen transitions, dialogs, character behavior, cutscenes, tutorials, and procedures that include network calls) step by step with `async`/`await`. This page explains the problems FlowTask solves, how it solves them, where it fits, and what it leaves to other mechanisms.

## Writing game progression with async

Let's write one tutorial screen with UniTask. It blinks an arrow, waits a moment, and then waits for a tap.

```csharp
async UniTask TutorialStep(CancellationToken ct)
{
    BlinkArrow(ct).Forget();                          // forget ct here and the arrow blinks forever
    await UniTask.Delay(500, cancellationToken: ct);  // forget ct here and this step outlives the screen
    await WaitForTap(ct);
}
```

The code is short, but it relies on several hidden rules to work correctly.

- **Passing the CancellationToken around**: you have to pass the token to every call. Forget it in one place, and that one operation won't stop.
- **Leaking lifetimes**: work that should stop when the screen closes keeps running if the tokens are handed out wrong. The shape of the code doesn't tell you where the screen's lifetime ends.
- **Runaway forgotten work**: nobody awaits work you `Forget()`. The responsibility for stopping it, and the place its exceptions go, leave the caller.
- **Inconsistent pausing**: if you pause the game with `Time.timeScale = 0`, you also stop UI waits that should keep running. You end up choosing, wait by wait, which time each one counts.

## How FlowTask writes it

Here is the same screen in FlowTask.

```csharp
async FlowTask TutorialStep()
{
    _ = Flow.Spawn(BlinkArrow());           // a child of this flow: ends with it
    await FlowTask.WaitForSeconds(0.5);     // on this flow's clock: stops while the game is paused
    await WaitForTap();
}
```

There are no tokens anywhere. Instead, FlowTask builds a tree of **scopes**.

- Each time a FlowTask method runs, it gets one scope. A scope you start becomes a child of the scope that started it. Above, `BlinkArrow` is a child of `TutorialStep`.
- When a parent ends, all its descendants stop. The screen's lifetime is simply the nesting of your code.
- To stop a flow, FlowTask throws `FlowCanceledException` from the await where it is suspended, and the stack unwinds. This is called **unwinding**. `using` and `finally` run as usual, so you write cleanup in plain C#.

```csharp
async FlowTask ShowDialog(Dialog dialog)
{
    dialog.Open();
    try
    {
        await dialog.Closed.Next();
    }
    finally
    {
        dialog.Close();   // runs when the dialog closes, and when the flow is canceled from outside
    }
}
```

Stopping a flow from outside is also written as structure. For example, `FlowTask.Race` lets the first branch to finish win, unwinds the losing branches, and only then resumes the caller.

```csharp
// The back key ends the tutorial step: TutorialStep and BlinkArrow are unwound.
await FlowTask.Race(backKey.Next(), TutorialStep());
```

This idea, where parent-child relationships decide how long flows live and no child outlives its parent, is called **structured concurrency**. Waits are counted on the Clock attached to the scope, too. Pausing the game's Clock doesn't stop a menu that runs on a different Clock.

[Flows and the World](../guide/flows-and-world.md) and [Scopes and cancellation](../guide/scopes-and-cancellation.md) cover these ideas in detail.

## Key features

- **Structured concurrency**: a FlowTask becomes a child of the scope it was started from. When the parent ends, all descendants unwind. You don't hand out CancellationTokens by hand.
- **Cancellation by unwinding**: FlowTask throws `FlowCanceledException` from the suspended await and unwinds the stack (this is not a rollback that restores game state). `using` and `finally` run as usual, and awaits inside `finally` also run to the end.
- **Composition**: `FlowTask.Race` (unwinds the losers before resuming the caller), `FlowTask.WhenAll`, and `Flow.Spawn` to start a child.
- **Two failure paths**: cancellation and exceptions. An exception that no `catch` handles (an unhandled exception) reaches the World's `OnUnhandledException`.
- **Fixed execution order**: Emit doesn't resume waiting flows immediately. Every resumption goes into a FIFO queue and is processed in a fixed order.
- **Game time**: Clocks, reference-counted Pause, time scale, and parent-child Clocks.
- **Signals**: stateless `Signal<T>`, subscriptions where the receiver chooses the buffer policy, `FlowProperty<T>`, and `Once<T>`.
- **Bridges**: Task / ValueTask, UniTask, R3, Unity's AsyncOperation / Awaitable / UnityEvent, and Godot signals.
- **Zero allocation in steady state**: per-frame waits and sending and receiving Signals don't allocate ([Performance and memory](../advanced/performance.md)).
- **Analyzers**: the usage rules (10 rules starting with FLOW) are checked at compile time, with code fixes. Messages are in English and Japanese.
- **Testing without an engine**: `FlowTask.Testing` advances a World in virtual time, with an NUnit adapter.
- **Diagnostics**: scope tree dumps, warnings, and the Scope Tree window in Unity.

The same core is used on .NET, Unity, and Godot.

## Where it fits

FlowTask is good at work in a game that has "steps" and a "lifetime".

- Screen transitions, dialogs, menus, and confirmation procedures
- Character and enemy behavior (including behavior that gets interrupted)
- Cutscenes, staged effects, and tutorials
- Procedures with network calls or loading in the middle (purchases, scene loading, loading you can stop with a cancel button)
- Game logic you want to verify in virtual time without running the engine

## What it leaves to other mechanisms

FlowTask takes care of the lifetime, time, and execution order of flows. Write file and network I/O, computation on another thread, the engine's async operations, and UI and input with their own mechanisms, and connect them to flows through bridges and signals. The work you connect also follows the scope's lifetime.

```csharp
async FlowTask<string> LoadSave(string path, Signal<FlowUnit> cancelPressed)
{
    var r = await FlowTask.Race(
        cancelPressed.Next(),
        FlowBridge.FromTask(ct => File.ReadAllTextAsync(path, ct)).ToFlowTask());   // ct is canceled when the button wins
    return r.TryGet1(out var text) ? text : null;
}
```

If the cancel button is pressed first, the loading branch loses, its token is canceled, and the file read stops. The same happens when the screen closes and the flow that called `LoadSave` ends.

| What you want to do | How to write it |
|---|---|
| Wait for a file, network call, or asset load, and stop it with a cancel button or when the screen ends | Write it with Task or similar, and await it with `FlowBridge.FromTask`. Work that can't be stopped (such as Addressables loads) runs to the end, and you can release a result nobody received with `onDiscard` ([Task and ValueTask](../integrations/task.md), [Unity bridges](../unity/bridges.md)) |
| Run heavy computation on another thread | Await it with `FlowBridge.FromTask(ct => Task.Run(() => Work(), ct))`. You receive the result on the World's thread ([Threads](../guide/threads.md)) |
| Deliver a value from another thread | Use `signal.EmitFromAnyThread(value)` or `world.Post(action)` ([Threads](../guide/threads.md)) |
| Wait for a button, a key, or an engine event | Turn it into a Signal and wait on it (uGUI's `ClickedSignal()`, `UnityEvent.ToSignal()`, Godot signals). Priorities, such as who receives the back key, are built from Signals and `Flow.Own` ([Signals](../guide/signals.md)) |
| Wait for a flow from existing Task or UniTask code | Turn the handle from `world.Run(...)` into a Task or UniTask with `AsTask()` or `ToUniTask()`, and await it ([Task and ValueTask](../integrations/task.md), [UniTask](../integrations/unitask.md)) |
| Run it in a .NET program that uses neither Unity nor Godot | Call `Tick` from your own loop ([Your first flow](first-flow.md)) |

### Ground rules

FlowTask's guarantees (a fixed execution order, cancellation by structure, and game time) rest on the following rules. Some ways of writing code you're used to from Task don't work as they are, but each one has an alternative. The reasons are in [Design rationale](../advanced/design-rationale.md).

- **A World runs on one thread**: flow code runs inside `Tick` and similar calls on the thread that created the World, so you don't need locks. Write the loop that calls Tick so that it doesn't leave that thread either ([Threads](../guide/threads.md)).
- **Time and outside completions advance with Tick**: time waits advance by the time you pass to `Tick`, and the results of Tasks that finish on other threads are taken in by `Tick` or `Flush`. On Unity and Godot, the integration ticks every frame.
- **On the World's thread, don't block waiting for a flow to end**: if you block, as with `AsTask().Result`, Tick stops too, so the flow never ends. Read the value of a finished flow with `handle.Result`. To wait for the end, await it or keep calling Tick ([Flows and the World](../guide/flows-and-world.md)). UniTask can't block waiting for an unfinished UniTask either.
- **`AsyncLocal<T>` values aren't separated per flow**: flows don't capture the ExecutionContext (neither does UniTask). A value set inside a flow is visible to other flows, and nothing guarantees that the flow sees the same value after an await. Pass per-flow values as arguments ([Execution model](../advanced/execution-model.md)).

If you only write work that has nothing to do with frames (such as handling server requests), using Task directly is simpler. You can use FlowTask together with Task and UniTask. Leave your existing code as it is, and await across them through bridges ([Task and ValueTask](../integrations/task.md), [UniTask](../integrations/unitask.md)).

> **Note**: the version is 0.x. While in 0.x, the API may change between versions. Every change is listed in the [CHANGELOG](../../../CHANGELOG.md).

## What to read next

- [Installation](installation.md): adding FlowTask to .NET, Unity, and Godot
- [Your first flow](first-flow.md): try the World, Tick, and Race in a console program
- [UniTask](../integrations/unitask.md): term mapping and rewrites for people coming from UniTask
