using System.Globalization;

namespace Katout.FlowTask.Internal;

/// <summary>A wait that the tick list evaluates on a clock: the one requested, or the scope's.</summary>
internal abstract class ClockWait : LeafNode<FlowUnit>
{
    protected Clock RequestedClock;

    internal override Clock TickClock => RequestedClock ?? Clock;

    protected override void OnUnregister()
    {
        if ((Flags & NodeFlags.InTickList) != 0) World.TickList.Remove(this);
    }

    internal override void OnTickSatisfied() => CompleteAsync(FlowUnit.Default);

    /// <summary>The clock to wait on: a removed scope clock, or a clock of another World, is a misuse.</summary>
    protected Clock EffectiveClock
    {
        get
        {
            var c = TickClock;
            if (c.LiveWorld != World) throw Errors.ClockNotUsable(c);
            return c;
        }
    }

    internal static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    protected override void ResetNode()
    {
        base.ResetNode();
        RequestedClock = null;
    }
}

internal sealed class WaitForSecondsNode : ClockWait
{
    static NodePool<WaitForSecondsNode> s_pool;

    double _seconds;
    double _target;

    internal static WaitForSecondsNode Rent(double seconds, Clock clock)
    {
        var n = s_pool.Rent() ?? new WaitForSecondsNode();
        n._seconds = seconds;
        n.RequestedClock = clock;
        n.Traits = clock == null ? NodeTraits.Leaf | NodeTraits.ParkableWait : NodeTraits.Leaf;
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "WaitForSeconds(" + Format(_seconds) + "s)";

    internal static string Describe(double seconds, Clock clock, double target) =>
        $"WaitForSeconds({Format(seconds)}s) on {clock.Name}, {Format(Math.Max(0, target - clock.Time))}s left";

    internal override void GetParkTarget(Clock clock, out ParkKind kind, out double target, out double amount)
    {
        kind = ParkKind.Time;
        target = clock.Time + _seconds;
        amount = _seconds;
    }

    protected override void OnStart()
    {
        _target = EffectiveClock.Time + _seconds;
        World.TickList.Add(this);
    }

    internal override bool EvaluateTick() => TickClock.Time >= _target;

    internal override string DescribeWait() => Describe(_seconds, TickClock, _target);

    protected override void ReturnToPool() => s_pool.Return(this);
}

internal sealed class DelayFramesNode : ClockWait
{
    static NodePool<DelayFramesNode> s_pool;

    int _frameCount;
    long _target;

    internal static DelayFramesNode Rent(int frameCount, Clock clock)
    {
        var n = s_pool.Rent() ?? new DelayFramesNode();
        n._frameCount = frameCount;
        n.RequestedClock = clock;
        // DelayFrames(0) completes synchronously, so only a wait of a frame or more parks.
        n.Traits = clock == null && frameCount > 0 ? NodeTraits.Leaf | NodeTraits.ParkableWait : NodeTraits.Leaf;
        n.InitUnstarted();
        return n;
    }

    internal override string Name => FramesName(_frameCount);

    static string FramesName(long frameCount) => frameCount == 1 ? "NextFrame" : $"DelayFrames({frameCount})";

    internal static string Describe(long frameCount, Clock clock, long target) =>
        $"{FramesName(frameCount)} on {clock.Name}, {Math.Max(0, target - clock.FrameCount)} frame(s) left";

    internal override void GetParkTarget(Clock clock, out ParkKind kind, out double target, out double amount)
    {
        kind = ParkKind.Frames;
        target = clock.FrameCount + _frameCount;
        amount = _frameCount;
    }

    protected override void OnStart()
    {
        if (_frameCount == 0)
        {
            CompleteSync(FlowUnit.Default);
            return;
        }

        _target = EffectiveClock.FrameCount + _frameCount;
        World.TickList.Add(this);
    }

    internal override bool EvaluateTick() => TickClock.FrameCount >= _target;

    internal override string DescribeWait() => Describe(_frameCount, TickClock, _target);

    protected override void ReturnToPool() => s_pool.Return(this);
}

/// <summary>WaitUntil: the condition is checked when it starts, then in step 3 of every Tick in which its clock runs.</summary>
internal sealed class WaitUntilNode<TState> : ClockWait
{
    static NodePool<WaitUntilNode<TState>> s_pool;

    TState _state;
    Func<TState, bool> _predicate;

    internal static WaitUntilNode<TState> Rent(TState state, Func<TState, bool> predicate, Clock clock)
    {
        var n = s_pool.Rent() ?? new WaitUntilNode<TState>();
        n._state = state;
        n._predicate = predicate;
        n.RequestedClock = clock;
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "WaitUntil";

    protected override void OnStart()
    {
        _ = EffectiveClock;
        if (EvaluateTick()) CompleteSync(FlowUnit.Default);
        else if (State == NodeState.Running) World.TickList.Add(this);
    }

    /// <summary>A condition that throws fails the wait: its awaiter receives the exception.</summary>
    internal override bool EvaluateTick()
    {
        try
        {
            return _predicate(_state);
        }
#pragma warning disable CA1031 // a throwing WaitUntil condition is thrown at its waiter's await
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Evaluated by OnStart before it joins the tick list, the failure is seen at once; in the list, at its turn.
            if ((Flags & NodeFlags.InTickList) != 0) FailAsync(ex);
            else Fail(ex);
            return false;
        }
    }

    internal override string DescribeWait() => $"WaitUntil on {TickClock.Name}";

    protected override void ReturnToPool()
    {
        _state = default;
        _predicate = null;
        s_pool.Return(this);
    }
}

internal sealed class NeverNode : LeafNode<FlowUnit>
{
    static NodePool<NeverNode> s_pool;

    internal static NeverNode Rent()
    {
        var n = s_pool.Rent() ?? new NeverNode();
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "Never";

    protected override void OnStart()
    {
    }

    internal override string DescribeWait() => "Never";

    protected override void ReturnToPool() => s_pool.Return(this);
}
