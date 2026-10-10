using System;
using Godot;

namespace Katout.FlowTask.Godot;

/// <summary>
/// Owns a <see cref="Katout.FlowTask.FlowWorld"/> and drives it from the SceneTree: <c>FlowWorld.Tick(dt)</c> in <c>_Process</c>
/// and an extra <c>FlowWorld.Flush()</c> after every other node's <c>_Process</c>, so signals emitted during the frame
/// resume their flows in the same frame. Meant to be an autoload:
/// Godot autoloads need a script inside the project, so add a one-line subclass
/// (<c>public partial class FlowAutoload : Katout.FlowTask.Godot.FlowWorldNode { }</c>) and register it (see
/// https://katout.github.io/FlowTask/en/godot/setup/).
/// It can also be created from code: <c>AddChild(new FlowWorldNode())</c>.
/// </summary>
/// <remarks>
/// <para>Runs with <see cref="Node.ProcessModeEnum.Always"/> by default so flows keep running while the tree is
/// paused; pause gameplay with <c>Clock.Pause()</c> instead, or set <c>ProcessMode</c> to change this. The
/// end-of-frame flush follows <c>ProcessMode</c> too: while this node is paused (for example Pausable on a paused
/// tree) or disabled, the World neither ticks nor flushes, so signals emitted meanwhile resume its flows afterwards.</para>
/// <para>Time: the World is ticked with Godot's own unscaled time, the process step that Timer, SceneTreeTimer
/// and Tween with <c>ignore_time_scale</c> advance by (Godot's delta divided by <c>Engine.TimeScale</c>, see
/// <see cref="GetDeltaTime"/>). So <c>UnscaledClock</c> ignores <c>Engine.TimeScale</c>, and the clocks keep in step
/// with Godot (its timers, animations and a physics-rate World) under a fixed frame rate too (<c>--fixed-fps</c>, Movie
/// Maker), except where the limit of <see cref="GetDeltaTime"/> clamps the step and Godot's timers do not (after a long
/// frame, and under a fixed frame rate whose 1 / fps is over the limit). While <c>Engine.TimeScale</c> is 0 Godot's
/// delta is 0, so the World is ticked with the time measured with <c>Time.GetTicksUsec</c> instead, and
/// <c>UnscaledClock</c> keeps advancing. Under a fixed frame rate that runs faster than real time, the clocks run faster
/// than real time too; bound real I/O with <c>CancellationTokenSource.CancelAfter</c> in the bridged Task
/// (https://katout.github.io/FlowTask/en/godot/setup/).
/// <c>DefaultClock.TimeScale</c> follows <c>Engine.TimeScale</c> every frame, so <c>DefaultClock</c> advances by Godot's
/// delta; put a custom slow motion on a child clock. A scale that <c>Clock.TimeScale</c> rejects (its product
/// with a child clock's scale is not finite) goes to <c>GD.PushError</c>, and the World ticks with the previous scale.</para>
/// <para>Unhandled exceptions and the other reports go to <c>GD.PushError</c> (with the scope path) and
/// <see cref="UnhandledException"/>, except <see cref="FlowExceptionKind.Undelivered"/> (an exception no receiver could
/// take), which goes to <c>GD.PushWarning</c>; warnings go to <c>GD.PushWarning</c>. The World is disposed in
/// <c>_ExitTree</c>, which unwinds every flow.</para>
/// <para>Several drive sources: flows that must run at the physics rate get a World of their own, ticked
/// from a <c>_PhysicsProcess</c> you own (https://katout.github.io/FlowTask/en/godot/physics/). Worlds on the main
/// thread talk through <see cref="Signal{T}"/>.</para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "The World is disposed in _ExitTree (Godot's lifecycle, not IDisposable); the internal child node is freed by Godot with its parent.")]
public partial class FlowWorldNode : Node
{
    static FlowWorldNode s_instance;

    FlowWorld _world;
    FlowLateFlushNode _lateFlush;
    ulong _lastTicksUsec;    // Time.GetTicksUsec() at this node's previous _Process
    ulong _lastProcessFrame; // Engine.GetProcessFrames() at this node's previous _Process (0: none yet)

    /// <summary>Creates the node with <c>ProcessMode.Always</c> and the lowest process priority, so flows run first in each frame.</summary>
    public FlowWorldNode()
    {
        ProcessMode = ProcessModeEnum.Always;
        // Flows run before the other nodes' _Process/_PhysicsProcess of the frame.
        ProcessPriority = int.MinValue;
    }

    /// <summary>The first FlowWorldNode that entered the tree (normally the autoload), or null.</summary>
    public static FlowWorldNode Instance => s_instance;

    /// <summary>The World of <see cref="Instance"/>, driven from <c>_Process</c>.</summary>
    public static FlowWorld Default
    {
        get
        {
            var n = s_instance;
            if (n == null || n._world == null)
                throw new InvalidOperationException(
                    "No FlowWorldNode is in the scene tree. Register a FlowWorldNode subclass as an autoload (first in the list) or add one with AddChild(new FlowWorldNode()). See https://katout.github.io/FlowTask/en/godot/setup/.");
            return n._world;
        }
    }

    /// <summary>This node's World (created when the node enters the tree, disposed when it exits).</summary>
    public FlowWorld World => _world;

    /// <summary>
    /// Raised by the default handler for every report to <see cref="FlowWorld.OnUnhandledException"/> of
    /// <see cref="World"/> (after it is logged), of every <see cref="FlowExceptionInfo.Kind"/>: also
    /// <see cref="FlowExceptionKind.Undelivered"/>, which is logged as a warning. Not raised when
    /// <see cref="CreateWorld"/> sets <see cref="FlowWorld.OnUnhandledException"/>: that handler replaces the default
    /// one, its logging and this event alike.
    /// </summary>
    public event Action<FlowExceptionInfo> UnhandledException;

    /// <summary>Creates a World. Override to configure it (its OnUnhandledException, OnWarning, clocks).</summary>
    protected virtual FlowWorld CreateWorld(string name) => new(name);

    /// <summary>
    /// Returns the value passed to <c>FlowWorld.Tick</c> this frame: <c>UnscaledClock</c> advances by the result and
    /// <c>DefaultClock</c> by the result times <c>Engine.TimeScale</c>. The default follows Godot's own unscaled time:
    /// while <c>Engine.TimeScale</c> is above 0 it is Godot's delta divided by <c>Engine.TimeScale</c> (the process step
    /// that Timer and Tween with <c>ignore_time_scale</c> advance by, also under <c>--fixed-fps</c> and Movie Maker), and
    /// while it is 0 (Godot's delta is 0 then) it is <paramref name="unscaledDelta"/>. Either is clamped to
    /// <c>Engine.MaxPhysicsStepsPerFrame / Engine.PhysicsTicksPerSecond</c> seconds, the limit Godot puts on its physics
    /// steps per frame, so after a long frame (a hitch, a breakpoint) waits do not all expire at once. Godot's <c>ignore_time_scale</c> timers are not clamped (Godot clamps only the delta it passes to
    /// <c>_process</c>, and not at all under a fixed frame rate), so UnscaledClock falls behind them after a long frame
    /// and under a fixed frame rate whose 1 / fps is over the limit. Override to change the limit or the time source,
    /// for example to tick with the measured time (https://katout.github.io/FlowTask/en/godot/setup/).
    /// </summary>
    /// <param name="unscaledDelta">The time since this node's previous frame measured with <c>Time.GetTicksUsec</c>. On
    /// the first frame and after frames this node did not process (a paused tree), Godot's delta divided by
    /// <c>Engine.TimeScale</c> instead, or 0 while the scale is 0, so the time this node was not processed is not added.</param>
    protected virtual double GetDeltaTime(double unscaledDelta) =>
        UnscaledStep(GetProcessDeltaTime(), Engine.TimeScale, unscaledDelta, StepLimit());

    /// <summary>
    /// The default <see cref="GetDeltaTime"/> as a pure function: Godot's unscaled process step
    /// (<paramref name="godotDelta"/> / <paramref name="timeScale"/>) while the scale is above 0, else
    /// <paramref name="measured"/>; at most <paramref name="limit"/>.
    /// </summary>
    internal static double UnscaledStep(double godotDelta, double timeScale, double measured, double limit) =>
        Math.Min(timeScale > 0 ? godotDelta / timeScale : measured, limit);

    // The limit Godot puts on its physics steps per frame, in seconds (none without physics ticks).
    static double StepLimit()
    {
        var ticksPerSecond = Engine.PhysicsTicksPerSecond;
        return ticksPerSecond > 0 ? (double)Engine.MaxPhysicsStepsPerFrame / ticksPerSecond : double.PositiveInfinity;
    }

    /// <summary>Creates the World. Call <c>base._EnterTree()</c> when you override it.</summary>
    public override void _EnterTree()
    {
        if (_world == null || _world.IsDisposed)
        {
            _world = Configure(CreateWorld("Godot"));
            _lastProcessFrame = 0;
        }

        s_instance ??= this;
    }

    /// <summary>Adds the child that flushes the World late in the frame. Call <c>base._Ready()</c> when you override it.</summary>
    public override void _Ready()
    {
        if (_lateFlush == null)
        {
            _lateFlush = new FlowLateFlushNode(this);
            AddChild(_lateFlush, false, InternalMode.Back);
        }
    }

    /// <summary>Ticks the World with this frame's time. Call <c>base._Process(delta)</c> when you override it.</summary>
    public override void _Process(double delta)
    {
        var w = _world;
        if (w == null || w.IsDisposed) return;
        var now = Time.GetTicksUsec();
        var frame = Engine.GetProcessFrames();
        var scale = Engine.TimeScale;
        double measured;
        if (_lastProcessFrame != 0 && frame == _lastProcessFrame + 1)
            measured = (now - _lastTicksUsec) / 1_000_000.0;
        else
            measured = scale > 0 ? delta / scale : 0; // first frame, or frames this node did not process (paused tree)
        _lastTicksUsec = now;
        _lastProcessFrame = frame;
        if (scale >= 0 && w.DefaultClock.TimeScale != scale) ApplyTimeScale(w, scale);
        w.Tick(GetDeltaTime(measured));
    }

    // Clock.TimeScale rejects a scale that is infinite or whose product with a descendant clock's scale is not finite.
    // DefaultClock then keeps its previous scale and the Tick still runs, so the World does not stop.
    static void ApplyTimeScale(FlowWorld w, double scale)
    {
        try
        {
            w.DefaultClock.TimeScale = scale;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            GD.PushError($"[FlowTask] Engine.TimeScale {scale} was not applied; DefaultClock keeps TimeScale {w.DefaultClock.TimeScale}. {ex.Message}");
        }
    }

    /// <summary>Disposes the World, unwinding its flows. Call <c>base._ExitTree()</c> when you override it.</summary>
    public override void _ExitTree()
    {
        try
        {
            // Dispose unwinds every flow (finally / using / AddCleanup run now). It throws
            // FlowUnhandledException when an UnhandledException handler throws meanwhile; the World is disposed either way.
            DisposeWorld(ref _world);
        }
        finally
        {
            // Otherwise Default would keep throwing, and no later FlowWorldNode could take this node's place.
            if (s_instance == this) s_instance = null;
        }
    }

    internal void FlushLate()
    {
        var w = _world;
        if (w != null && !w.IsDisposed) w.Flush();
    }

    FlowWorld Configure(FlowWorld world)
    {
        world.OnUnhandledException ??= HandleUnhandledException;
        world.OnWarning += HandleWarning;
        return world;
    }

    void HandleUnhandledException(FlowExceptionInfo info)
    {
        // An undelivered exception had no receiver left (a canceled or settled one): it happens in normal play, so it is a
        // warning. Every other kind is an error.
        var path = string.IsNullOrEmpty(info.ScopePath) ? "<root>" : info.ScopePath;
        if (info.Kind == FlowExceptionKind.Undelivered) GD.PushWarning($"[FlowTask] {info.Kind} at '{path}' (no receiver could take it; it changed no result): {info.Exception}");
        else GD.PushError($"[FlowTask] {info.Kind} at '{path}': {info.Exception}");
        UnhandledException?.Invoke(info);
    }

    static void HandleWarning(FlowWarning warning) => GD.PushWarning("[FlowTask] " + warning);

    static void DisposeWorld(ref FlowWorld world)
    {
        var w = world;
        world = null;
        if (w != null && !w.IsDisposed) w.Dispose();
    }
}

/// <summary>
/// Internal child of <see cref="FlowWorldNode"/>: flushes its World after every other node's <c>_Process</c>. Inherits
/// the owner's <c>ProcessMode</c>, so it does not flush while the owner is paused or disabled.
/// </summary>
internal sealed partial class FlowLateFlushNode : Node
{
    readonly FlowWorldNode _owner;

    /// <summary>For Godot, which constructs script instances itself; this node is only ever made by the owner.</summary>
    public FlowLateFlushNode()
    {
    }

    internal FlowLateFlushNode(FlowWorldNode owner)
    {
        _owner = owner;
        Name = "FlowLateFlush";
        // Follow the FlowWorldNode: a World whose Tick is paused must not resume flows from this flush either.
        ProcessMode = ProcessModeEnum.Inherit;
        ProcessPriority = int.MaxValue;
    }

    public override void _Process(double delta) => _owner?.FlushLate();
}
