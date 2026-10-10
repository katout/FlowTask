using System;
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Godot;

/// <summary>
/// The second scene of the smoke test (Handover.tscn; tools/godot/run-smoke.ps1 runs it in a Godot process of its own
/// after Main.tscn). Checks that when disposing the World throws in FlowWorldNode._ExitTree (an UnhandledException
/// handler throws while Dispose unwinds a flow whose finally throws), the node still stops being the default, so the
/// next FlowWorldNode that enters the tree becomes FlowWorldNode.Default and runs flows. A scene of its own because it
/// takes the autoload, the World that Main.tscn's checks run on, out of the tree.
/// </summary>
public partial class HandoverMain : Node
{
    const int FramesToRunOnTheNextWorld = 3;

    int _frame;
    bool _handedOver;
    string _detail;
    FlowWorldNode _next;
    FlowHandle _probe;

    // Not in _Ready: the root is still adding its children then and would refuse to remove the autoload.
    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 1)
        {
            GD.Print("[smoke] FlowTask Godot smoke test, second scene (Handover.tscn)");
            _handedOver = HandOver(out _detail);
            // The new default World runs a flow over the next frames.
            if (_handedOver) _probe = FlowWorldNode.Default.Run(FlowTask.DelayFrames(FramesToRunOnTheNextWorld - 1));
            return;
        }

        if (_frame <= FramesToRunOnTheNextWorld) return;
        var ran = _handedOver && _probe.Status == FlowStatus.Succeeded;
        var ok = _handedOver && ran;
        GD.Print($"[smoke] {(ok ? "PASS" : "FAIL")}  default-handover-after-a-throwing-exception-handler  ({_detail}, a flow on the next World: {(_handedOver ? _probe.Status.ToString() : "not started")})");
        GD.Print($"[smoke] HANDOVER RESULT: {(ok ? "PASS" : "FAIL")}");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }

    bool HandOver(out string detail)
    {
        var old = FlowWorldNode.Instance;
        if (old == null || old.World == null)
        {
            detail = "no FlowWorldNode with a World (the autoload)";
            return false;
        }

        var oldWorld = old.World;
        var handlerThrew = false;

        void ThrowOnTheDisposeException(FlowExceptionInfo p)
        {
            if (p.ScopePath != nameof(ThrowsWhileDisposed)) return;
            handlerThrew = true;
            throw new InvalidOperationException("deliberate throw from an UnhandledException handler (expected)");
        }

        old.UnhandledException += ThrowOnTheDisposeException;
        oldWorld.Run(ThrowsWhileDisposed());
        GD.Print("[smoke] (the next ERROR lines are expected: a finally that throws while the autoload's World is disposed, the UnhandledException handler that throws on it, and the FlowUnhandledException out of _ExitTree)");
        Exception escaped = null;
        try
        {
            old.GetParent().RemoveChild(old);
        }
        catch (Exception ex)
        {
            escaped = ex; // Godot logs an exception thrown by a script callback; it does not reach RemoveChild's caller
        }

        old.UnhandledException -= ThrowOnTheDisposeException;
        var cleared = FlowWorldNode.Instance == null;
        _next = new FlowWorldNode { Name = "NextFlowWorld" };
        GetTree().Root.AddChild(_next);
        var isDefault = FlowWorldNode.Instance == _next && _next.World is { IsDisposed: false } && ReferenceEquals(FlowWorldNode.Default, _next.World);
        old.QueueFree();
        detail = $"handler threw: {handlerThrew}, old World disposed: {oldWorld.IsDisposed}, Instance cleared: {cleared}, next FlowWorldNode is Default: {isDefault}, exception out of RemoveChild: {escaped?.GetType().Name ?? "none"}";
        return handlerThrew && oldWorld.IsDisposed && cleared && isDefault;
    }

    static async FlowTask ThrowsWhileDisposed()
    {
        try
        {
            await FlowTask.Never();
        }
        finally
        {
            throw new InvalidOperationException("deliberate exception while the World is disposed (expected)");
        }
    }
}
