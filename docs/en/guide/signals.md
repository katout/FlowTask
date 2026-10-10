# Signals and shared state

This page explains the types that pass events and values between flows, and between game code and flows. Event notifications are `Signal<T>`, events you can't afford to miss are subscriptions (`Subscription<T>`), changing values are `FlowProperty<T>`, and values that are set only once are `Once<T>`. You build priorities with lifetimes from these and `Flow.Own`.

## Signal: waiting for events

`Signal<T>` is a notification with no state. You send with `Emit`, and a flow waits for the next Emit with `Next()`.

```csharp
var jump = new Signal<FlowUnit>("Jump");

async FlowTask Tutorial()
{
    ShowHint("Press A to jump");
    await jump.Next();          // waits for the next Emit
    HideHint();
}

// in the input code
if (pad.A.WasPressed) jump.Emit(FlowUnit.Default);
```

- For notifications without a value, use `Signal<FlowUnit>` and send with `Emit(FlowUnit.Default)`.
- Emit doesn't resume the waiting flow immediately. The resume is scheduled and processed at the next flush (Tick or Flush). Even when you Emit from inside a flow, the emitting side continues first.
- If you give it a name (`new Signal<int>("Hits")`), it shows up in the dump as something like `Hits.Next` ([Debugging](../tools/debugging.md)).

### Next is an edge

`Next()` receives only the next Emit after it starts waiting. Emits that happen while nobody is waiting are dropped.

```csharp
while (true)
{
    var damage = await damaged.Next();
    await FlowTask.WaitForSeconds(0.8);   // hits during this knockback are lost
}
```

Emits that happen while the loop body is doing something else, or before the wait starts (during a screen's opening animation, for example), never arrive, and nothing warns you. This is the same behavior as UniTask's `button.OnClickAsync()`. For input that's fine to drop, like repeated presses during a purchase, `Next()` is fine as is. Wait for input you can't afford to miss with a subscription, described next. Analyzer rule FLOW006 suggests a subscription for `Next()` inside a loop ([Analyzers](../tools/analyzers.md)).

## Subscriptions: waiting without missing anything

`Subscribe(BufferPolicy)` creates a subscription that buffers Emits from that point on. A flow takes them out one at a time with the subscription's `Next()`.

```csharp
async FlowTask EnemyAI(Enemy self)
{
    using var hits = self.Damaged.Subscribe(BufferPolicy.Latest);   // keeps the newest hit
    while (true)
    {
        var r = await FlowTask.Race(hits.Next(), Patrol(self));
        if (r.TryGet0(out var hit)) await HitStun(self, hit);
    }
}
```

| Policy | Behavior |
|---|---|
| `BufferPolicy.Latest` | Keeps only the newest value |
| `BufferPolicy.Queue(capacity, BufferOverflow.DropOldest)` | Keeps values in order; when full, drops the oldest |
| `BufferPolicy.Queue(capacity, BufferOverflow.DropNewest)` | Keeps values in order; when full, drops the incoming value |
| `BufferPolicy.Queue(capacity, BufferOverflow.Fail)` | Keeps values in order; on overflow, ends the scope that owns the subscription with `SubscriptionOverflowException` |

- A subscription is owned by the scope that created it and ends at the end of that scope. To end it earlier, add `using`. A subscription created outside a flow has no owner, so dispose it yourself.
- The capacity of `Queue` is an upper limit; the storage grows to fit the contents.
- If a flow is waiting, the value is handed over directly without going through the buffer.
- `TryTake(out v)` takes one value without waiting, and `Count` returns how many are buffered.
- Subscribe to the input you want to catch before you start waiting (when the screen opens, before the animation).
- A value received by a subscription wait that lost a Race goes back to the front of the subscription ([Composition](composition.md)).
- A subscription with `BufferOverflow.Fail` must be created inside a flow; outside a flow, `Subscribe` throws `FlowMisuseException` for that policy.

> **Note**: A subscription is a value type. Disposing a copy of a subscription ends the original too. `Next()` on an ended subscription fails with `FlowMisuseException`. Dispose a subscription exactly once, where you created it.

## Closing

When the sender won't Emit anymore, close the Signal.

```csharp
var lobby = new Signal<Player>("Lobby");

// the producer
lobby.Close();                       // or lobby.Close(error) when it failed

// a consumer
while (true)
{
    var (received, player) = await lobby.NextOrClosed();
    if (!received) break;            // closed
    Greet(player);
}
```

- `Next()` on a closed Signal fails with `SignalClosedException`. If you pass an error to `Close(error)`, it becomes the `InnerException`.
- `NextOrClosed()` returns `(bool Received, T Value)`: `(true, value)` when a value arrives, and `(false, default)` instead of an exception once the Signal is closed.
- A subscription closes after it has delivered all the values it buffered.
- `Emit` on a closed Signal is a `FlowMisuseException`. A second `Close` does nothing.

## Turning events into Signals: FromCallback

Turn C# events and callbacks into a Signal with `FlowBridge.FromCallback`, and wait on that.

```csharp
async FlowTask Menu()
{
    using var clicks = FlowBridge.FromCallback<FlowUnit>(emit =>
    {
        Action h = () => emit(FlowUnit.Default);
        shopButton.Clicked += h;
        return () => shopButton.Clicked -= h;   // detached when Menu ends
    });
    var r = await FlowTask.Race(clicks.Next(), closed.Next());
    if (r.Index == 0) await OpenShop();
}
```

- It's owned by the scope that created it. At the end of the scope, or on Dispose, it detaches from the event and closes. Callbacks that arrive after that go nowhere. Receive callbacks that can arrive after the screen is gone (ad rewards, in-app purchases) inside a flow that outlives the screen (one started with `FlowWorld.Run`).
- `Next()`, `NextOrClosed()`, and `Subscribe` work the same as on a Signal.
- One created inside a flow can also receive callbacks called from other threads ([Threads](threads.md)).
- Wait this way instead of calling `Flow.Spawn` inside an event handler ([Flows and the World](flows-and-world.md)).
- Bridges for Unity and Godot events are in [Unity bridges](../unity/bridges.md) and [Godot signals](../godot/signals.md).

## FlowProperty: waiting for a changing value

`FlowProperty<T>` holds a value and notifies when it changes. You can wait until a condition holds.

```csharp
var hp = new FlowProperty<int>(100);

async FlowTask WatchHealth()
{
    await hp.WaitUntil(x => x <= 0);   // completes at once if it already holds
    await GameOver();
}

// elsewhere
hp.Set(hp.Value - damage);
```

- `Value` is the value last passed to `Set`. `Set` does nothing if the value is the same (by default by `EqualityComparer<T>.Default`; you can pass a comparer to the constructor).
- `WaitUntil` ends immediately if the condition already holds. Otherwise it evaluates the condition on every `Set` and returns the value for which it held. Even if `Set(5); Set(0); Set(3);` happen in a row in the same frame, a wait for `x <= 0` receives 0.
- `Changed` is a Signal that Emits the new value on every change. To receive every change, subscribe to it. After you `Close` `Changed`, it Emits nothing more, and the FlowProperty keeps working.
- If you call `Set` from inside a condition, the value changes immediately, and the notifications go out in `Set` order after the current notification finishes.
- An exception thrown by the condition is thrown from that wait's await.

## Once: a value set only once

`Once<T>` receives a value only once, and can be waited on any number of times, by any number of flows.

```csharp
var connected = new Once<Session>();

async FlowTask Chat()
{
    var session = await connected;   // or connected.Wait(); completes at once once it is set
    await RunChat(session);
}

// when the connection is ready
connected.Set(session);
```

- A second `Set` is a `FlowMisuseException`.
- You can read it without waiting with `IsSet`, `Value`, and `TryGetValue`.
- A FlowTask can be awaited only once, so use this when several waiters need the same result.
- A Once has no failed or canceled state. If the work fails or is canceled before it calls `Set`, the waiters wait forever. To share a result that can fail (a load, a network request), put a value that describes the outcome in it and call `Set` on failure and cancellation too, or share the work's handle. A waiter on the handle waits with `await FlowTask.WaitUntil(h, x => x.IsCompleted)` and then reads `Status`, `Result`, and `Exception`.

## Building priorities with lifetimes

Build "who is on top right now", such as who receives the back key or who blocks input, from a list and signals (there is no dedicated type). Have the current scope own each entry you push with `Flow.Own`. When the scope ends, the entry is disposed and removed; with `using`, it is removed sooner. Outside a flow, `Flow.Own` owns nothing, so dispose an entry pushed there yourself. Something owned with `Flow.Own` stays in the scope's cleanup list until the scope ends, even if you dispose it early. When a loop in a long-lived scope pushes again and again, move the push and the wait into a method, so that the method's scope owns the entry (the samples' `BackKeyRouter.Next` has this shape).

A back-key router with layers (a dialog above the screen) and `Block()`, which swallows the key, is the samples' `BackKeyRouter` ([Unity samples](../unity/samples.md), [Godot samples](../godot/samples.md)).

## Create them per session and pass them in

Don't put `Signal`, `FlowProperty`, or `Once` in static fields. Create them per World (per game session) and pass them as arguments to the flows that use them.

```csharp
sealed class GameSession
{
    public readonly Signal<int> Damaged = new("Damaged");
    public readonly FlowProperty<int> Hp = new(100);
}

var session = new GameSession();
world.Run(InGame(session));
```

- These objects are tied to the first World that uses them and to its thread (binding). Writing from another thread throws `FlowThreadException` ([Threads](threads.md)).
- After the World an object is bound to has been disposed, using that object from another World throws `FlowMisuseException`. In Unity with domain reload disabled, a Signal kept in a static field throws this exception on the second play.

## Rules and pitfalls

- Even if two buttons are pressed in the same frame, `Race(ok.Next(), cancel.Next())` receives only the one that was emitted first. The other is dropped (because it's an edge).
- Call `Emit` and `Set` from outside a flow on the World's thread. From other threads, use `EmitFromAnyThread` or `FlowWorld.Post` ([Threads](threads.md)).
- When you wait on a Signal sent by a flow in another World, the wait never ends if the sender's World is disposed first. The sender should `Close` the Signal in its cleanup, and the receiver should wait with `NextOrClosed()`.
- Conversion to and from R3 Observables is covered in [R3](../integrations/r3.md).
