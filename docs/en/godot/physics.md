# Running on physics frames

The World of `FlowWorldNode` runs in `_Process`. For flows that should advance every physics frame, create a second World and Tick it from `_PhysicsProcess`. This page explains how to create that World and how the two Worlds relate. To wait for a single physics frame, you don't need another World (see "Waiting for a single physics frame" below).

## Creating a physics World

Add a few lines to the autoload subclass.

```csharp
// res://FlowAutoload.cs
using System;
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Godot;

public partial class FlowAutoload : FlowWorldNode
{
    public FlowAutoload() => ProcessPhysicsPriority = int.MinValue; // before other nodes' _PhysicsProcess

    public static FlowWorld Physics { get; private set; }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (Physics == null || Physics.IsDisposed)
        {
            Physics = new FlowWorld("GodotPhysics");
            // Route exception reports and warnings to Godot's log like FlowWorldNode does
            // (without OnUnhandledException, an unhandled exception is thrown from _PhysicsProcess)
            Physics.OnUnhandledException = info =>
            {
                if (info.Kind == FlowExceptionKind.Undelivered) GD.PushWarning($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
                else GD.PushError($"[FlowTask] {info.Kind} at '{info.ScopePath}': {info.Exception}");
            };
            Physics.OnWarning += w => GD.PushWarning("[FlowTask] " + w);
        }
    }

    // Same time model as FlowWorldNode: tick with the unscaled step, let DefaultClock follow Engine.TimeScale
    public override void _PhysicsProcess(double delta)
    {
        var w = Physics;
        if (w == null || w.IsDisposed) return;
        var scale = Engine.TimeScale;
        if (scale >= 0 && w.DefaultClock.TimeScale != scale) ApplyTimeScale(w, scale);
        w.Tick(1.0 / Engine.PhysicsTicksPerSecond);
    }

    // A rejected scale goes to the log and the World ticks with the previous one;
    // skipping the Tick on the exception would stop the physics World
    static void ApplyTimeScale(FlowWorld w, double scale)
    {
        try { w.DefaultClock.TimeScale = scale; }
        catch (ArgumentOutOfRangeException ex) { GD.PushError($"[FlowTask] Engine.TimeScale {scale} was not applied to the physics World. {ex.Message}"); }
    }

    public override void _ExitTree()
    {
        Physics?.Dispose(); // unwinds the physics flows
        Physics = null;
        base._ExitTree();
    }
}
```

Start flows with this World's `Run`.

```csharp
FlowAutoload.Physics.Run(Dash(body));

async FlowTask Dash(CharacterBody2D body)
{
    for (var i = 0; i < 10; i++)
    {
        body.Velocity = new Vector2(600, 0);
        body.MoveAndSlide();
        await FlowTask.NextFrame(); // = the next physics frame
    }
}
```

## How time advances

- `FlowTask.NextFrame()` and `FlowTask.DelayFrames(n)` count physics frames.
- `UnscaledClock` advances by the physics step (`1 / Engine.PhysicsTicksPerSecond`). `DefaultClock` advances by Godot's physics delta (step × `Engine.TimeScale`). This is the same time handling as the main World, so the Clocks of the two Worlds stay in step (see Time in [Setup](setup.md)).
- Do not pass `_PhysicsProcess`'s `delta` straight to `Tick`. Even `UnscaledClock` would follow `Engine.TimeScale` and stop while the scale is 0. Pass the step, as above.
- The physics step is fixed, and Godot limits the number of steps per frame. So there is no need to clamp long frames as the main World does.

## Unhandled exceptions and shutdown

Set `OnUnhandledException` on any World you create yourself. Without it, unhandled exceptions and the other reports are thrown as `FlowUnhandledException` from the outermost `Tick` (`_PhysicsProcess`), `Flush`, `Run` or `Dispose` (`_ExitTree`). Handling them is covered in [Failures](../guide/failures.md).

The physics World also unwinds all its flows on `Dispose` in `_ExitTree`. The caveats on shutdown (`Dispose` does not run Ticks, so an await in `finally` only runs up to its first await) are the same as for the main World (see Shutdown in [Setup](setup.md)).

## How the two Worlds relate

Both Worlds are on the main thread, so they can work together through `Signal<T>`. When a flow in the main World Emits, a flow waiting in the physics World resumes at the next physics Tick.

```csharp
// main World: input decides, physics World: moves the body
readonly Signal<FlowUnit> _dashRequested = new("DashRequested");

async FlowTask InputLoop() // run on FlowWorldNode.Default
{
    while (true)
    {
        await FlowTask.WaitUntil(() => Input.IsActionJustPressed("dash"));
        _dashRequested.Emit(FlowUnit.Default); // resumes PhysicsLoop at the next physics Tick
        await FlowTask.NextFrame();
    }
}

async FlowTask PhysicsLoop(CharacterBody2D body) // run on FlowAutoload.Physics
{
    using var requests = _dashRequested.Subscribe(BufferPolicy.Latest);
    while (true)
    {
        await requests.Next();
        await Dash(body);
    }
}
```

Scopes have no parent-child relationship across Worlds. When a flow in the main World ends, flows started in the physics World do not stop. To tie a physics flow to another lifetime, do one of the following:

- To tie it to a node's lifetime, start it with `node.RunWhileInTree(task, FlowAutoload.Physics.DefaultClock)`. The flow runs in the physics World and unwinds when the node leaves the tree (see [Node lifetime](lifetime.md)).
- Keep the handle returned by `Physics.Run`, and call `Cancel()` in a `finally` or `Flow.AddCleanup` on the main side.
- Communicate through `Signal<T>`.

```csharp
async FlowTask Stage(CharacterBody2D body) // on the main World
{
    var physics = FlowAutoload.Physics.Run(PhysicsLoop(body));
    try
    {
        await RunStage();
    }
    finally
    {
        physics.Cancel(); // the physics flow ends with this one
    }
}
```

## Waiting for a single physics frame

To wait for a single physics frame, you don't need a separate World. In a flow on the main World, wait on the `SceneTree`'s `physics_frame` signal.

```csharp
using var physicsFrame = GetTree().ToFlowSignal(SceneTree.SignalName.PhysicsFrame);
await physicsFrame.Next(); // resumes at the main World's Tick, after the physics step
```

- The code after it runs not inside the physics step, but at the main World's Tick that follows.
- For flows that should keep running on every physics frame, use the separate World on this page. A World cannot have a Clock that advances on physics frames, because `Tick(dt)` advances every Clock in the World by the same dt. How Worlds and Clocks relate is covered in [Time and Clocks](../guide/time-and-clocks.md).
