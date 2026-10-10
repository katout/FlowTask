# Time and Clocks

FlowTask waits advance with game time, not real time. This page explains the time you pass to `Tick`, Clocks that represent a flow of time, Pause and time scale, and the kinds of waits (`WaitForSeconds`, `NextFrame`, `DelayFrames`, `WaitUntil`).

## The time you pass to Tick

The World's time advances by the number of seconds you pass to `Tick(dt)`. For dt, pass the unscaled time elapsed since the previous Tick.

```csharp
var now = stopwatch.Elapsed.TotalSeconds;
world.Tick(Math.Min(now - last, 0.1));   // unscaled seconds since the last Tick; long frames clamped
last = now;
```

- Don't multiply dt by the game's time scale. Apply the scale with `world.DefaultClock.TimeScale` instead.
- Clamp dt yourself for long frames (loading, breakpoints, coming back from the background). Without a limit, waits get satisfied all at once.
- The Unity and Godot integrations do both for you. They tick with the engine's unscaled elapsed time, clamped by the engine's own limit, and set `DefaultClock.TimeScale` to the engine's time scale every frame.

| Engine | Time passed to Tick | Limit | `DefaultClock` scale |
|---|---|---|---|
| Unity | `Time.unscaledDeltaTime` | `Time.maximumDeltaTime` | The value that makes `DefaultClock` advance by the same amount as `Time.deltaTime` (usually `Time.timeScale`) |
| Godot | Godot's delta ÷ `Engine.TimeScale` | `Engine.MaxPhysicsStepsPerFrame ÷ Engine.PhysicsTicksPerSecond` | `Engine.TimeScale` |

The details are in [Unity setup](../unity/setup.md) and [Godot setup](../godot/setup.md).

The only time a World receives is the value you pass to `Tick(dt)`. To change the time source, you change that value.

- Unity: set `AutoTick` to `false` and tick yourself ([Unity setup](../unity/setup.md)).
- Godot: override `GetDeltaTime` of `FlowWorldNode` ([Godot setup](../godot/setup.md)).
- Tests: use the virtual time that the test advances with Ticks ([Testing](../tools/testing.md)).

## Clock

A Clock is a flow of time with its own Pause and scale. Clocks form a parent-child tree, and a parent's Pause and scale pass down to its children.

A World starts with two Clocks.

- `world.UnscaledClock`: advances by exactly the dt passed to Tick. It can't be paused, and its scale can't be changed.
- `world.DefaultClock`: the root scope's Clock. Flows that don't specify a Clock run on it. With an engine integration, the engine's time scale applies to it.

Add the Clocks your game uses with `world.CreateClock`.

```csharp
var game = world.CreateClock("Game");                     // a child of DefaultClock: follows the engine's time scale
var ui = world.CreateClock("UI", world.UnscaledClock);   // keeps running while Game is paused

world.Run(InGame(), game);   // InGame and what it starts run on Game
```

- If you omit the parent, the Clock becomes a child of `DefaultClock`.
- Create a UI Clock that keeps going while the game is paused with `UnscaledClock` as its parent. It follows only its own Pause and scale.
- A Clock from `world.CreateClock` lives as long as the World.
- You can read `clock.Time` (this Clock's time), `clock.DeltaTime` (how much it advanced in the last Tick), and `clock.FrameCount` (the number of Ticks in which it wasn't paused).

### Choosing a flow's Clock

A scope inherits its parent scope's Clock. To run on a different Clock, attach the Clock to the task before it starts.

```csharp
FlowTask OpenPauseMenu() => Flow.WithClock(ui, OpenPauseMenuOnUi());

async FlowTask OpenPauseMenuOnUi()
{
    using var pause = game.Pause();   // Game stops until the menu closes; this flow runs on UI
    await PauseMenu();
}
```

- `Flow.WithClock(clock, task)` runs `task`, and the tasks it starts, on `clock`.
- `world.Run(task, clock)` also sets the Clock of a root flow.
- To count just one wait on a different Clock, pass the Clock to the wait: `FlowTask.WaitForSeconds(3, world.UnscaledClock)`.
- You can get the current scope's Clock with `Flow.CurrentClock`.

## Pause

`clock.Pause()` stops the Clock and its descendants. Disposing the returned handle releases the pause.

```csharp
async FlowTask Inventory()   // runs on the UI clock
{
    using var pause = game.Pause();   // released when this block ends, however it ends
    await InventoryScreen();
}
```

- Pause is reference-counted. If two flows pause a Clock, it stays stopped until both release it.
- The handle is owned by the scope that created it. Without `using`, it's released at the end of the scope, but the Clock stays stopped until then. Analyzer rule FLOW004 warns if you drop the return value ([Analyzers](../tools/analyzers.md)).
- A pause taken outside a flow has no owner. The Clock stays stopped until you dispose it yourself.
- You can call Dispose on the handle any number of times.

On a paused Clock, time doesn't advance and frames aren't counted. Resumes of scopes running on that Clock are held, and processed in their original order at the flush after the pause is released. The start of unwinding by a cancel isn't held, but resumes of awaits inside cleanup are.

> **Note**: If you pause the Clock you're running on (or one of its ancestors), your own resumes stop too. If a pause menu or a dialog runs on `DefaultClock` and pauses `DefaultClock`, it stops responding even to its close button. In this case, the World raises a `PausedOwnClock` warning once. Pin dialogs and menus to a UI Clock with `Flow.WithClock(ui, …)`, as in the example above.

When the game is stuck and won't move, check `Paused clocks:` at the top of the dump to see who holds a pause ([Debugging](../tools/debugging.md)).

## TimeScale

`clock.TimeScale` is the scale of that Clock. The time that actually passes is multiplied by the scales of all its ancestors.

```csharp
game.TimeScale = 0.5;   // slow motion for everything on Game
```

- You can set only finite values of 0 or more. Anything else, including values whose product with the ancestors' scales isn't finite, throws `ArgumentOutOfRangeException`.
- A scale of 0 stops time but keeps counting frames (`DelayFrames` and `NextFrame` still advance). To stop frames too, use Pause.
- You can't change the scale of `UnscaledClock` (`FlowMisuseException`).

> **Note**: The Unity and Godot integrations overwrite `DefaultClock.TimeScale` with the engine's scale every frame. Put your own slow motion on a child Clock of `DefaultClock` (such as `game` above).

## Scope Clocks

For time that belongs to one flow, like slowing down a single enemy, use `Flow.CreateClock`. The scope that created it owns it, and it goes away at the end of the scope.

```csharp
async FlowTask EnemyAI(Enemy self)
{
    var local = Flow.CreateClock($"Enemy#{self.Id}");   // a child of this scope's clock
    local.TimeScale = self.IsSlowed ? 0.3 : 1.0;         // this enemy only
    await Flow.WithClock(local, Behave(self));
}
```

- If you omit the parent, it becomes a child of `Flow.CurrentClock` and follows the scope's Pause and scale. The parent can be a World Clock, or a Clock created by the current scope or one of its ancestors. In a flow started on a Clock created by a different scope, omitting the parent throws `FlowMisuseException`, so pass a parent.
- A scope Clock goes away only after all child scopes (including cleanup awaits) have finished. Children can use the Clock until the very end.
- A Clock that has gone away doesn't advance. Starting a task on it, pausing it, or setting its scale throws `FlowMisuseException`. A wait that was waiting on a Clock that has gone away throws `FlowMisuseException` from its await at the next Tick.
- Create Clocks that must outlive the scope with `world.CreateClock`.

> **Note**: There's no API to remove a scope Clock early. If you call `Flow.CreateClock` on every pass of a loop in a long-lived scope, Clocks pile up until the scope ends, and the cost of each Tick grows too. Create a Clock you'll use for a while inside a child FlowTask method that you await. If `world.Clocks` keeps growing, suspect this.

## Kinds of waits

| Wait | Ends when |
|---|---|
| `FlowTask.WaitForSeconds(seconds)` | The first Tick in which the Clock's time has advanced by the given number of seconds |
| `FlowTask.NextFrame()` | The Clock's next frame (same as `DelayFrames(1)`) |
| `FlowTask.DelayFrames(n)` | The Clock has counted n Ticks in which it wasn't paused |
| `FlowTask.WaitUntil(condition)` | The condition returns true |
| `FlowTask.Never()` | Never ends. Unwinds when the scope is canceled |

Every wait except `Never` accepts a Clock as its optional last argument. If you don't pass one, the wait counts on the scope's Clock.

```csharp
await FlowTask.WaitForSeconds(1.5);                  // seconds, not milliseconds
await FlowTask.NextFrame();
await FlowTask.WaitUntil(() => player.IsGrounded);
await FlowTask.WaitUntil(player, p => p.IsGrounded); // with state: no closure
```

- The argument of `WaitForSeconds` is in seconds (not milliseconds, as in UniTask's `Delay`). Time is accumulated as a double, so for a number of seconds that falls exactly on a frame boundary, the wait can end one frame late (a 0.1-second wait at 60 fps ends on the 7th Tick). To wait an exact number of frames, use `DelayFrames`.
- `NextFrame` and `DelayFrames` resume at the next Tick at the earliest. They count Ticks in which the Clock and its ancestors aren't paused (Ticks with a scale of 0 count). `DelayFrames(0)` ends immediately.
- `WaitUntil` evaluates the condition once when the wait starts, then on every Tick in which the Clock isn't paused. An exception thrown by the condition is thrown from the await.
- Instead of polling that suspends again every frame (`while (!ready) await FlowTask.NextFrame();`), write `WaitUntil(() => ready)`. That way, when things get stuck, the dump shows how long it has been waiting. To wait for a value to change, `FlowProperty<T>.WaitUntil` is a better fit ([Signals](signals.md)).

The detailed rules for which Tick each wait resumes on are in [Execution model](../advanced/execution-model.md).

## Rules and pitfalls

- Run flows that should move with physics frames in a separate World that you tick in `FixedUpdate` or `_PhysicsProcess`. You can't advance individual Clocks at different rates ([Godot physics frames](../godot/physics.md), [Unity setup](../unity/setup.md)).
- Given the same sequence of dt values and the same input, the execution order is the same ([Execution order rules](../advanced/execution-model.md)).
- When two deadlines pass in one large dt, the winner of a Race is decided by the order in which those time waits began, not by which deadline was earlier. When the branches are the time waits themselves, that is the order of the arguments ([Composition](composition.md)). Clamp large dt values.
- Clocks advance by the time passed to `Tick`, so they don't always follow real time (long frames are clamped, and nothing advances while the app is stopped). For limits measured in real time, such as a limit on a network request, use the mechanism on the external side (`HttpClient.Timeout`, `CancellationTokenSource.CancelAfter`) ([Composition](composition.md)).
