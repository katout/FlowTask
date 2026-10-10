namespace Katout.FlowTask.Internal;

/// <summary>Step 3 of a Tick: the waits that the Tick evaluates (time, frames, conditions), in the order they began.</summary>
internal sealed class TickList
{
    FlowNode _head;
    FlowNode _tail;
    int _count;
    NodeRef[] _snapshot = new NodeRef[16];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(FlowNode node)
    {
        if ((node.Flags & NodeFlags.InTickList) != 0) return;
        node.Flags |= NodeFlags.InTickList;
        node.TickPrev = _tail;
        node.TickNext = null;
        if (_tail != null) _tail.TickNext = node;
        else _head = node;
        _tail = node;
        _count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Remove(FlowNode node)
    {
        if ((node.Flags & NodeFlags.InTickList) == 0) return;
        node.Flags &= ~NodeFlags.InTickList;
        if (node.TickPrev != null) node.TickPrev.TickNext = node.TickNext;
        else _head = node.TickNext;
        if (node.TickNext != null) node.TickNext.TickPrev = node.TickPrev;
        else _tail = node.TickPrev;
        node.TickPrev = node.TickNext = null;
        _count--;
    }

    /// <summary>
    /// Evaluates the waits in the order they began, and a satisfied one reserves its resume (so waits satisfied in one Tick
    /// resume in that order, not by deadline). It walks a snapshot: a WaitUntil condition can end other waits.
    /// </summary>
    internal void Evaluate()
    {
        if (_snapshot.Length < _count) _snapshot = new NodeRef[Math.Max(_count, _snapshot.Length * 2)];
        var count = 0;
        for (var n = _head; n != null; n = n.TickNext) _snapshot[count++] = new NodeRef(n);
        for (var i = 0; i < count; i++)
        {
            var r = _snapshot[i];
            _snapshot[i] = default;
            var n = r.Node;
            if (!r.IsLive || (n.Flags & NodeFlags.InTickList) == 0 || (n.IsCancelConfirmed && !n.RunsCleanup)) continue;
            var clock = n.TickClock;
            if (clock.EffectivelyPaused)
            {
                // A removed clock stays paused, so only the waits on paused clocks pay for this check.
                if (clock.LiveWorld == null) FailRemovedClockWait(n, clock);
                continue;
            }

            if (!n.EvaluateTick()) continue;
            Remove(n);
            n.OnTickSatisfied();
        }
    }

    /// <summary>A wait still on a scope clock that was removed (a task outside the owner used it) would never end: it fails.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void FailRemovedClockWait(FlowNode n, Clock clock)
    {
        Remove(n);
        var ex = Errors.RemovedClock(clock);
        if (n.IsStateMachine) n.FailPark(ex);
        else ((ClockWait)n).FailAsync(ex);
    }
}
