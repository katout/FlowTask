using System;
using Godot;

/// <summary>
/// The override in docs/en/godot/setup.md that ticks the World with the measured (wall-clock) time instead of Godot's unscaled
/// step, with the default limit. The smoke test uses it to check the <c>GetDeltaTime</c> override point: under
/// <c>--fixed-fps</c> its clocks follow real time, not Godot's (FixedFps.tscn), and the measured time leaves out the
/// frames the node was not processed (pausable-world-skips-paused-time in SmokeMain).
/// </summary>
public partial class WallClockWorldNode : Katout.FlowTask.Godot.FlowWorldNode
{
    protected override double GetDeltaTime(double unscaledDelta) =>
        Math.Min(unscaledDelta, (double)Engine.MaxPhysicsStepsPerFrame / Engine.PhysicsTicksPerSecond);
}
