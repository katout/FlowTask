using System;
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Godot;

/// <summary>
/// The autoload (project.godot: FlowAutoload="*res://FlowAutoload.cs"). Godot autoloads need a script inside the project,
/// so this one-line subclass exposes FlowWorldNode. It also shows the documented pattern for several drive sources (one World per source): flows that must run at the
/// physics rate get a World of their own, ticked from _PhysicsProcess (FlowWorldNode itself has no physics option).
/// </summary>
public partial class FlowAutoload : FlowWorldNode
{
    public FlowAutoload()
    {
        ProcessPhysicsPriority = int.MinValue; // physics flows run before other nodes' _PhysicsProcess
    }

    /// <summary>The physics-rate World of the smoke test.</summary>
    public static FlowWorld Physics { get; private set; }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (Physics == null || Physics.IsDisposed)
        {
            Physics = new FlowWorld("GodotPhysics");
            // Route reports and warnings like FlowWorldNode does for its own World: an undelivered exception (no receiver
            // could take it) is a warning, every other kind an error.
            Physics.OnUnhandledException = p =>
            {
                if (p.Kind == FlowExceptionKind.Undelivered) GD.PushWarning($"[FlowTask] {p.Kind} at '{p.ScopePath}': {p.Exception}");
                else GD.PushError($"[FlowTask] {p.Kind} at '{p.ScopePath}': {p.Exception}");
            };
            Physics.OnWarning += w => GD.PushWarning("[FlowTask] " + w);
        }
    }

    // Same time model as FlowWorldNode (docs/en/godot/setup.md): Tick with the unscaled physics step, so UnscaledClock ignores
    // Engine.TimeScale, and let DefaultClock follow Engine.TimeScale, so it advances by Godot's physics delta
    // (step x TimeScale). A physics step is fixed and Godot caps the steps per frame, so there is no long frame to clamp.
    public override void _PhysicsProcess(double delta)
    {
        var w = Physics;
        if (w == null || w.IsDisposed) return;
        var scale = Engine.TimeScale;
        if (scale >= 0 && w.DefaultClock.TimeScale != scale) ApplyTimeScale(w, scale);
        w.Tick(1.0 / Engine.PhysicsTicksPerSecond);
    }

    // Clock.TimeScale rejects a scale that is infinite or whose product with a descendant clock's scale is not finite.
    // As in FlowWorldNode, DefaultClock then keeps its previous scale and the Tick still runs, so the physics
    // World does not stop.
    static void ApplyTimeScale(FlowWorld w, double scale)
    {
        try
        {
            w.DefaultClock.TimeScale = scale;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            GD.PushError($"[FlowTask] Engine.TimeScale {scale} was not applied to the physics World; DefaultClock keeps TimeScale {w.DefaultClock.TimeScale}. {ex.Message}");
        }
    }

    public override void _ExitTree()
    {
        Physics?.Dispose();
        Physics = null;
        base._ExitTree();
    }
}
