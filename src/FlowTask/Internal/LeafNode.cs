namespace Katout.FlowTask.Internal;

/// <summary>Base of the waits (time, signals, bridges, joins). A leaf has no children.</summary>
internal abstract class LeafNode<T> : FlowNode<T>
{
    protected LeafNode() => Traits = NodeTraits.Leaf;

    internal override Clock ResumeClock
    {
        get
        {
            var aw = Awaiter;
            return aw != null && aw.Token == AwaiterToken ? aw.Clock : Clock;
        }
    }

    /// <summary>Completes during OnStart: the awaiter sees it at once.</summary>
    protected void CompleteSync(T value)
    {
        Result = value;
        OnUnregister();
        Terminate(NodeState.Succeeded);
    }

    /// <summary>
    /// Completes from outside the waiter (Emit, Set, the tick list, a bridge): the node ends now, and its delivery to the
    /// awaiter is queued for the flush.
    /// </summary>
    internal void CompleteAsync(T value)
    {
        if (State != NodeState.Running) return;
        Result = value;
        OnUnregister();
        if ((Flags & NodeFlags.InTickList) != 0) World.TickList.Remove(this);
        State = NodeState.Succeeded;
        var parent = Parent;
        Detach();
        // A Run or Spawn wait tells its handle's AsTask here, where the handle becomes Succeeded, not at the delivery,
        // which a Pause may hold and Dispose drops.
        Observer?.OnTerminated(this);
        World.ResumeQueue.Enqueue(this);
        if (parent != null) ContinueHeldParent(parent, parent.Token);
    }

    /// <summary>
    /// Fails during OnStart: the awaiter sees it at once. Also the fallback of <see cref="FailAsync"/>. A canceled leaf just
    /// ends canceled.
    /// </summary>
    internal void Fail(Exception ex)
    {
        if (State != NodeState.Running) return;
        OnUnregister();
        if (IsCancelConfirmed)
        {
            Terminate(NodeState.Canceled);
            return;
        }

        EndWithFault(FailureRecord.ForFault(ex, BuildScopePath()));
    }

    /// <summary>
    /// Fails from outside the waiter (a close, a condition, a bridge, a removed clock). An awaited leaf ends now and its
    /// failure reaches the awaiter through the flush queue, as <see cref="CompleteAsync"/> delivers a value: in order with
    /// the completions queued before it. A root or spawned wait fails at once (its handle, or the scope that spawned it).
    /// </summary>
    internal void FailAsync(Exception ex)
    {
        if (State != NodeState.Running) return;
        var aw = Awaiter;
        if (aw == null || aw.Token != AwaiterToken || IsCancelConfirmed || (Flags & (NodeFlags.Spawned | NodeFlags.ConsumerGone)) != 0)
        {
            Fail(ex);
            return;
        }

        OnUnregister();
        if ((Flags & NodeFlags.InTickList) != 0) World.TickList.Remove(this);
        Failure = FailureRecord.ForFault(ex, BuildScopePath());
        Flags |= NodeFlags.FaultQueued;
        State = NodeState.Faulted;
        var parent = Parent;
        Detach();
        World.ResumeQueue.Enqueue(this);
        if (parent != null) ContinueHeldParent(parent, parent.Token);
    }

    /// <summary>The delivery of this leaf's end (<see cref="CompleteAsync"/>, <see cref="FailAsync"/>) to its awaiter.</summary>
    internal override void OnDequeued()
    {
        var token = Token;
        var aw = Awaiter;
        var fault = (Flags & NodeFlags.FaultQueued) != 0;
        Flags &= ~NodeFlags.FaultQueued;
        if (aw != null && aw.Token == AwaiterToken)
        {
            if (fault) aw.OnAwaitedFailed(this, true);
            else aw.OnAwaitedTerminated(this, true);
        }
        else if (fault)
        {
            World.Reporter.ReportUndelivered(Failure.Info);
        }

        if (Token == token && (Flags & NodeFlags.ConsumerGone) != 0) Release();
    }

    protected override void ResetNode()
    {
        // Released before its failure's turn (a Race settled by a branch queued earlier): nobody receives the failure.
        if ((Flags & NodeFlags.FaultQueued) != 0) World.Reporter.ReportUndelivered(Failure.Info);
        base.ResetNode();
    }

    internal override void UnwindSelf()
    {
        if (IsTerminated) return;
        OnUnregister();
        Terminate(NodeState.Canceled);
    }

    /// <summary>Leaves whatever the leaf is registered with (a signal, a FlowProperty, the tick list, a joined task).</summary>
    protected virtual void OnUnregister()
    {
    }
}

/// <summary>A leaf that completes at once with a value (FromResult passed to Run, Spawn or a combinator).</summary>
internal sealed class ValueNode<T> : LeafNode<T>
{
    static NodePool<ValueNode<T>> s_pool;

    T _value;

    internal static ValueNode<T> Create(T value)
    {
        var n = s_pool.Rent() ?? new ValueNode<T>();
        n._value = value;
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "Value";

    protected override void OnStart() => CompleteSync(_value);

    protected override void ReturnToPool()
    {
        _value = default;
        s_pool.Return(this);
    }
}
