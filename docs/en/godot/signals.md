# Godot signals

To wait on Godot signals (the ones from `[Signal]`, `EmitSignal`, `Connect`, and `ToSignal`) in a flow, bridge them to FlowTask signals first. This page explains how to create bridges, how to wait just once, and what to watch for regarding missed emits and lifetimes.

On this page, "Godot signal" means a signal from Godot's own system, and "FlowTask signal" means FlowTask's `Signal<T>` or an `EventSignal<T>` bridged from a Godot signal.

## Waiting on a button

```csharp
// A confirmation dialog: resolves once, even when both buttons are pressed in the same frame
async FlowTask<bool> Confirm(Button ok, Button cancel)
{
    using var okPressed = ok.PressedSignal();         // disconnected when the scope ends
    using var cancelPressed = cancel.PressedSignal();
    var r = await FlowTask.Race(okPressed.Next(), cancelPressed.Next());
    return r.Index == 0;
}
```

`PressedSignal()` turns `BaseButton.pressed` into an `EventSignal<FlowUnit>`. Wait for the next press with `Next()`.

- The connection is made at the time of the call, and the current scope owns that `EventSignal`.
- When the scope ends (completion, cancellation, or an exception), it is disconnected automatically and the `EventSignal` closes. With `using`, you can disconnect earlier, at the end of the block.
- An `EventSignal` created outside a flow has no owner, so the caller must `Dispose` it.
- The flow does not resume at the moment Godot emits. The resumption is scheduled and happens at the World's next Tick or Flush (see ordering within a frame in [Setup](setup.md)).

## Kinds of bridge

| API | Type | Use |
| --- | --- | --- |
| `obj.ToFlowSignal(signal)` | `EventSignal<FlowUnit>` | Discards the arguments. Any number of arguments (`pressed`, `timeout`, `finished`, and so on) |
| `obj.ToFlowSignal<T>(signal)` | `EventSignal<T>` | Signals with exactly one argument |
| `obj.ToFlowSignalArgs(signal)` | `EventSignal<Variant[]>` | Receives the arguments as a `Variant[]`. Any number of arguments |
| `button.PressedSignal()` | `EventSignal<FlowUnit>` | `BaseButton.pressed` |

```csharp
// A script-defined signal: [Signal] delegate void HitEventHandler(int damage);
using var hit = enemy.ToFlowSignal<int>(Enemy.SignalName.Hit);
var damage = await hit.Next();

// Two or more arguments
using var moved = body.ToFlowSignalArgs(MyBody.SignalName.Moved);
Variant[] args = await moved.Next();
```

- The typed `ToFlowSignal<T>` connects with Godot's `Callable.From<T>`. If the signal's argument count doesn't match, Godot reports an error. For two or more arguments, use `ToFlowSignalArgs`.
- If, at the time of the call, the signal source is null, has been freed, or does not have that signal, it throws an exception (`ArgumentNullException`, `ObjectDisposedException`, or `ArgumentException`).
- The `Signal` property of `EventSignal<T>` is the `Signal<T>` inside. You can pass it to methods that take a `Signal<T>`.

## Not missing emits: `Subscribe`

`EventSignal<T>` has the same semantics as `Signal<T>`. `Next()` is an edge, and emits while nobody is waiting are lost.

```csharp
using var hit = enemy.ToFlowSignal<int>(Enemy.SignalName.Hit);
using var hits = hit.Subscribe(BufferPolicy.Latest); // keeps only the latest hit while nobody waits
while (true)
{
    var damage = await hits.Next();
    await Flinch(damage); // a hit during the flinch waits in the buffer
}
```

If you don't want to miss any, subscribe with `Subscribe(BufferPolicy)` and wait on the subscription's `Next()`. To keep every one in order, choose a capacity and an overflow policy, as in `BufferPolicy.Queue(8, BufferOverflow.DropOldest)`. Subscriptions and `BufferPolicy` are explained in detail in [Signals](../guide/signals.md).

Emits before you start waiting are also lost, without a warning. One example is a button press during a dialog's opening animation. To catch it, create the `PressedSignal()` and call `Subscribe(BufferPolicy.Latest)` before the animation, then wait on the subscription's `Next()` after it. Just creating the `EventSignal` is not enough.

## Waiting just once

Connect before the operation that triggers the signal, then wait on `Next()`.

```csharp
var timer = NodeLifetime.Own(new Timer { WaitTime = 0.5, OneShot = true });
AddChild(timer);
using var timeout = timer.ToFlowSignal(Timer.SignalName.Timeout); // connect before Start
timer.Start();
await timeout.Next();
```

`Next()` is an edge, so emits before the connection are not delivered. If you connect after an operation such as `timer.Start()`, you may miss the emit.

The `SignalAwaiter` returned by Godot's `ToSignal` can be bridged with `AsFlow()`.

```csharp
await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout).AsFlow();
```

| How to write it | Difference |
| --- | --- |
| `using var s = obj.ToFlowSignal(signal); await s.Next();` | Disconnects when the scope ends (including on cancellation). Recommended |
| `await ToSignal(obj, signal).AsFlow();` | The result is a `FlowTask<Variant[]>`. On cancellation it ignores the result, but the one-shot connection on the Godot side remains until the signal fires or the object is freed |

> **Note**: Inside a FlowTask method, do not await `await ToSignal(...)` directly. `SignalAwaiter` is an external awaiter that does not go through a bridge, and while waiting on it, the flow can neither be canceled nor unwound. When it suspends at that await, the scope ends with `FlowMisuseException`. The rest of the method, `finally`, and `using` do not run (`AddCleanup` and `NodeLifetime.Own` do). The analyzer's FLOW002 reports this as an error (see [Analyzers](../tools/analyzers.md)). Bridge it with the `ToFlowSignal` family or `.AsFlow()`.

## When the signal source is freed first

If the signal source is freed before it emits, that `Next()` never completes. Race it against a timeout, or run it tied to the signal source's node.

```csharp
// Ends when the enemy leaves the tree, even if it never emits Died
await NodeLifetime.WhileInTree(enemy, WaitDied(enemy));

async FlowTask WaitDied(Enemy enemy)
{
    using var died = enemy.ToFlowSignal(Enemy.SignalName.Died);
    await died.Next();
}
```

`WhileInTree` is covered in [Node lifetime](lifetime.md).

## An `EventSignal` after it closes

When its owning scope ends, the `EventSignal` closes.

- Waiting with `Next()` after it closes throws `SignalClosedException`. If not caught, it becomes an unhandled exception.
- If you wait with `NextOrClosed()`, you get `(false, default)` instead of an exception. A subscription hands over its remaining values before returning `(false, default)`.
- Emits that arrive after it closes are silently dropped.

Receive signals that can arrive after a screen closes (granting rewards, purchase completion, and so on) in a flow that outlives the screen (such as one started with `FlowWorldNode.Default.Run`).

## Signals emitted on another thread

An `EventSignal` created inside a flow is bound to the World. Even if the Godot signal is emitted off the main thread, it goes through the World's inbox and is delivered at the next Tick or Flush.

An `EventSignal` created outside a flow does not check threads until a World uses it. If it is emitted on another thread, it is delivered on that thread (a race condition). Bridge signals that cross threads inside a flow. The general threading rules are in [Threads](../guide/threads.md).
