namespace Katout.FlowTask.Internal;

/// <summary>
/// Cancellation and unwinding: confirms the cancellation of a subtree, and unwinds it now during execution, or at the
/// head of the next flush otherwise.
/// </summary>
internal sealed class Unwinder
{
    readonly FlowWorld _world;
    readonly RunningStack _running;
    readonly List<NodeRef> _pending = new();
    readonly List<NodeRef> _deferred = new();
    readonly List<List<NodeRef>> _listPool = new();
    bool _draining;

    internal Unwinder(FlowWorld world, RunningStack running)
    {
        _world = world;
        _running = running;
    }

    internal bool HasPending => _pending.Count > 0;

    /// <summary>
    /// Cancels <paramref name="node"/> and its subtree: confirmed now, unwound now during execution, otherwise at the head of
    /// the next flush.
    /// </summary>
    internal void CancelNode(FlowNode node, CancelCause cause)
    {
        if (node.State != NodeState.Running || node.IsCancelConfirmed) return;
        MarkSubtree(node, cause);
        UnwindOrDefer(node);
    }

    internal void UnwindOrDefer(FlowNode node)
    {
        if (_world.ExecutionDepth > 0) UnwindSubtreeNow(node);
        else _pending.Add(new NodeRef(node));
    }

    /// <summary>
    /// Confirms the cancellation of <paramref name="node"/> and of its subtree. A node canceled before is not entered: what
    /// runs under it was started by its cleanup, which runs to its end. Nor is a Flow.NonCancelable node, which only a
    /// scope awaits: that scope takes its result. World.Dispose enters everything.
    /// </summary>
    internal static void MarkSubtree(FlowNode node, CancelCause cause)
    {
        if (!MarkNode(node, cause)) return;
        // Pre-order over the tree links: a chain of awaits can be thousands of nodes deep, too deep to recurse.
        var c = node.FirstChild;
        while (c != null)
        {
            if (MarkNode(c, cause) && c.FirstChild != null)
            {
                c = c.FirstChild;
                continue;
            }

            while (c.NextSibling == null)
            {
                c = c.Parent;
                if (ReferenceEquals(c, node)) return;
            }

            c = c.NextSibling;
        }
    }

    /// <summary>Marks one node; true when its children are to be marked too.</summary>
    static bool MarkNode(FlowNode node, CancelCause cause)
    {
        if (node.State != NodeState.Running) return false;
        if ((node.Flags & NodeFlags.NonCancelable) != 0 && cause != CancelCause.WorldDisposed) return false;
        if (!node.IsCancelConfirmed)
        {
            node.Flags |= NodeFlags.CancelConfirmed;
            node.Cause = cause;
            return true;
        }

        return cause == CancelCause.WorldDisposed;
    }

    /// <summary>
    /// Cancels the live <paramref name="scope"/> (CancelCause.Fault) because of a failure it did not throw: it carries the
    /// exception and ends with it once unwound. <paramref name="except"/>, the child that failed, is not canceled. The
    /// caller unwinds the scope.
    /// </summary>
    internal static void CarryFault(FlowNode scope, FailureRecord fault, FlowNode except)
    {
        scope.Failure = FailureRecord.Carry(fault);
        scope.Flags |= NodeFlags.CancelConfirmed;
        scope.Cause = CancelCause.Fault;
        for (var c = scope.FirstChild; c != null; c = c.NextSibling)
        {
            if (!ReferenceEquals(c, except)) MarkSubtree(c, CancelCause.Fault);
        }
    }

    /// <summary>A node ends: its live children are canceled (Fault when it ends Faulted, ParentEnded otherwise) and unwound.</summary>
    internal void EndChildren(FlowNode node, NodeState final)
    {
        var cause = final == NodeState.Faulted ? CancelCause.Fault : CancelCause.ParentEnded;
        for (var c = node.FirstChild; c != null; c = c.NextSibling) MarkSubtree(c, cause);
        if (_world.ExecutionDepth > 0)
        {
            UnwindChildren(node);
            return;
        }

        for (var c = node.LastChild; c != null; c = c.PrevSibling) _pending.Add(new NodeRef(c));
    }

    /// <summary>Unwinds the canceled children of <paramref name="node"/>, later-started first.</summary>
    internal void UnwindChildren(FlowNode node)
    {
        var list = RentList();
        for (var c = node.LastChild; c != null; c = c.PrevSibling) CollectPostOrder(c, list);
        ProcessUnwindList(list);
        ReturnList(list);
    }

    /// <summary>A canceled state machine held for its children unwinds now that they have ended.</summary>
    internal void ScheduleUnwindResume(FlowNode scope)
    {
        if (_world.ExecutionDepth > 0) _deferred.Add(new NodeRef(scope));
        else _pending.Add(new NodeRef(scope));
    }

    /// <summary>Unwinds the nodes that were on the running path when they were to unwind, now that it has returned.</summary>
    internal void DrainDeferred()
    {
        if (_draining || _deferred.Count == 0) return;
        _draining = true;
        try
        {
            for (var i = 0; i < _deferred.Count; i++)
            {
                var r = _deferred[i];
                if (r.IsLive) UnwindSubtreeNow(r.Node);
            }

            _deferred.Clear();
        }
        finally
        {
            _draining = false;
        }
    }

    /// <summary>Unwinds the subtrees canceled outside execution: at the head of a flush.</summary>
    internal void ProcessPending()
    {
        if (_pending.Count == 0) return;
        var batch = RentList();
        batch.AddRange(_pending);
        _pending.Clear();
        foreach (var r in batch)
        {
            if (r.IsLive) UnwindSubtreeNow(r.Node);
        }

        ReturnList(batch);
        DrainDeferred();
    }

    /// <summary>
    /// Unwinds a canceled subtree, descendants first and later-started siblings first. A node on the running path cannot be
    /// resumed re-entrantly: it unwinds itself at its next await, and its ancestors once the running chain returns.
    /// </summary>
    void UnwindSubtreeNow(FlowNode node)
    {
        var list = RentList();
        CollectPostOrder(node, list);
        ProcessUnwindList(list);
        ReturnList(list);
    }

    /// <summary>
    /// The subtree of <paramref name="root"/> in post-order, later-started children first. Iterative over the tree links:
    /// a chain of awaits can be thousands of nodes deep.
    /// </summary>
    static void CollectPostOrder(FlowNode root, List<NodeRef> list)
    {
        var n = root;
        while (n.LastChild != null) n = n.LastChild;
        while (true)
        {
            list.Add(new NodeRef(n));
            if (ReferenceEquals(n, root)) return;
            if (n.PrevSibling == null)
            {
                n = n.Parent;
                continue;
            }

            n = n.PrevSibling;
            while (n.LastChild != null) n = n.LastChild;
        }
    }

    void ProcessUnwindList(List<NodeRef> list)
    {
        foreach (var r in list)
        {
            var n = r.Node;
            if (!r.IsLive || !n.IsCancelConfirmed || (n.Flags & NodeFlags.Ending) != 0) continue;
            if (!_running.IsEmpty && _running.IsOnPath(n))
            {
                _deferred.Add(r);
                continue;
            }

            n.UnwindSelf();
        }
    }

    List<NodeRef> RentList()
    {
        if (_listPool.Count == 0) return new List<NodeRef>(16);
        var l = _listPool[^1];
        _listPool.RemoveAt(_listPool.Count - 1);
        return l;
    }

    void ReturnList(List<NodeRef> list)
    {
        list.Clear();
        _listPool.Add(list);
    }
}
