namespace Katout.FlowTask.Internal;

/// <summary>FlowHandle.Join: a wait for a spawned or root task to succeed.</summary>
internal sealed class JoinNode<T> : LeafNode<T>
{
    static NodePool<JoinNode<T>> s_pool;

    FlowHandle<T> _handle;

    internal static FlowTask<T> Create(FlowHandle<T> handle, string file, int line)
    {
        var n = s_pool.Rent() ?? new JoinNode<T>();
        n._handle = handle;
        n.SetSite(file, line);
        n.InitUnstarted();
        return new FlowTask<T>(n, n.Token);
    }

    internal override string Name => "Join(" + (_handle.Node?.DisplayName ?? "?") + ")";

    internal override string DescribeWait() => Name;

    protected override void OnStart()
    {
        var target = _handle.Node;
        var targetWorld = target?.World;
        if (targetWorld != null && targetWorld != World && targetWorld.ThreadId != World.ThreadId)
        {
            Fail(new FlowThreadException(
                $"Join of '{target.DisplayName}' in {World} cannot wait for a task of {targetWorld}, which runs on another thread. " +
                "Take its Task with handle.AsTask() on that World's thread, and wait for it with FlowBridge.FromTask(_ => task)."));
            return;
        }

        if (!_handle.TryMarkJoined())
        {
            Fail(new FlowMisuseException("A task handle can be joined only once."));
            return;
        }

        if (target == null || target.IsTerminated)
        {
            Finish(sync: true);
            return;
        }

        // A flow ends only after its children: a join from inside it would wait for its own end.
        for (var n = Parent; n != null; n = n.Parent)
        {
            if (!ReferenceEquals(n, target)) continue;
            Fail(new FlowMisuseException($"Join of '{target.DisplayName}' from inside it can never complete: a flow ends only after its children. Join a flow from outside it."));
            return;
        }

        target.Awaiter = this;
        target.AwaiterToken = Token;
    }

    internal override void OnAwaitedTerminated(FlowNode child, bool fromQueue)
    {
        // A canceled join ends through its own unwinding, which its parent may be waiting for.
        if (State != NodeState.Running || IsCancelConfirmed || !ReferenceEquals(child, _handle.Node)) return;
        Finish(sync: false);
    }

    void Finish(bool sync)
    {
        var target = _handle.Node;
        if (target is not { State: NodeState.Succeeded })
        {
            const string Remedy = "Wait with FlowTask.WaitUntil(handle, h => h.IsCompleted) to see any ending.";
            var ex = target == null
                ? new FlowJoinException("The joined handle has no task: it was never started (a Spawn refused in a canceled scope, or a default handle). " + Remedy)
                : target.State == NodeState.Faulted
                    ? new FlowJoinException($"Joined task '{target.DisplayName}' faulted. {Remedy}", _handle.Exception)
                    : new FlowJoinException($"Joined task '{target.DisplayName}' was canceled ({target.Cause}). {Remedy}");
            if (sync) Fail(ex);
            else FailAsync(ex);
            return;
        }

        if (sync) CompleteSync(target.Result);
        else CompleteAsync(target.Result);
    }

    protected override void OnUnregister()
    {
        var target = _handle.Node;
        if (target != null && ReferenceEquals(target.Awaiter, this)) target.Awaiter = null;
    }

    protected override void ResetNode()
    {
        base.ResetNode();
        _handle = default;
    }

    protected override void ReturnToPool() => s_pool.Return(this);
}
