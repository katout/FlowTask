namespace Katout.FlowTask.Internal;

/// <summary>
/// WithoutResult: runs a task with a result as a task without one. When the task succeeds, the flow receives the
/// completion and ignores the value (<see cref="FlowNode.IgnoreResult"/>): a subscription's counts as taken, a bridge's
/// goes to its onDiscard.
/// </summary>
internal sealed class DiscardNode<T> : FlowNode<FlowUnit>
{
    static NodePool<DiscardNode<T>> s_pool;

    FlowNode<T> _inner;
    uint _innerToken;

    internal static DiscardNode<T> Rent(FlowNode<T> inner, uint token)
    {
        var n = s_pool.Rent() ?? new DiscardNode<T>();
        n._inner = inner;
        n._innerToken = token;
        n.InitUnstarted();
        return n;
    }

    internal override string Name => _inner?.DisplayName ?? "WithoutResult";

    protected override void OnStart()
    {
        var inner = _inner;
        // Passed to another consumer too, which started it first, or consumed and its node reused: it is not this node's
        // to write or release.
        if (inner.Token != _innerToken || inner.State != NodeState.Unstarted)
        {
            _inner = null;
            throw Errors.AlreadyStarted(inner, _innerToken);
        }

        inner.Awaiter = this;
        inner.AwaiterToken = Token;
        Awaiting = inner;
        AwaitingToken = inner.Token;
        inner.Start(this);
    }

    bool InnerIs(FlowNode child) => ReferenceEquals(child, _inner) && child.Token == _innerToken;

    internal override void OnAwaitedTerminated(FlowNode child, bool fromQueue)
    {
        if (IsTerminated || !InnerIs(child)) return;
        var received = child.State == NodeState.Succeeded && !IsCancelConfirmed;
        if (received) child.IgnoreResult();
        ReleaseInner();
        if (received) Terminate(NodeState.Succeeded);
        else if (IsCancelConfirmed) TerminateCanceled();
    }

    internal override void OnAwaitedFailed(FlowNode child, bool fromQueue)
    {
        if (IsTerminated || !InnerIs(child)) return;
        var fault = child.Failure;
        var world = World;
        ReleaseInner();
        if (!IsCancelConfirmed)
        {
            EndWithFault(fault);
            return;
        }

        world.Reporter.ReportUndelivered(fault.Info);
        TerminateCanceled();
    }

    internal override void UnwindSelf()
    {
        if (IsTerminated || (Flags & NodeFlags.Ending) != 0) return;
        ReleaseInner();
        TerminateCanceled();
    }

    void ReleaseInner()
    {
        var inner = _inner;
        _inner = null;
        if (inner != null && inner.Token == _innerToken) inner.ReleaseOrOrphan();
    }

    internal override string DescribeWait()
    {
        var i = _inner;
        return i != null && i.Token == _innerToken && !i.IsTerminated ? i.DisplayName : null;
    }

    protected override void ResetNode()
    {
        var inner = _inner;
        if (inner != null && inner.Token == _innerToken && inner.State == NodeState.Unstarted) inner.ReleaseUnstarted();
        base.ResetNode();
        _inner = null;
        _innerToken = 0;
    }

    protected override void ReturnToPool() => s_pool.Return(this);
}
