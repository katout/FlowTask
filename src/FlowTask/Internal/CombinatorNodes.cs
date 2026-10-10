namespace Katout.FlowTask.Internal;

/// <summary>Base of Race and WhenAll: starts its branches as its children and settles on their ends.</summary>
internal abstract class CombinatorNode<TR> : FlowNode<TR>
{
    // One struct array for node and token: locality, and no covariance check on every store.
    protected NodeRef[] Branches = new NodeRef[4];
    protected int BranchCount;
    protected int StartedCount;

    /// <summary><see cref="ReleaseBranches"/>: no branch's value is in the result.</summary>
    protected const int TakenNone = -1;

    /// <summary><see cref="ReleaseBranches"/>: every branch's value is in the result.</summary>
    protected const int TakenAll = -2;

    internal void AddBranch(FlowNode node)
    {
        if (BranchCount == Branches.Length) Array.Resize(ref Branches, BranchCount * 2);
        Branches[BranchCount++] = new NodeRef(node);
    }

    protected void EnsureCapacity(int count)
    {
        if (Branches.Length < count) Branches = new NodeRef[count];
    }

    protected int IndexOf(FlowNode child)
    {
        for (var i = 0; i < StartedCount; i++)
        {
            if (ReferenceEquals(Branches[i].Node, child) && child.Token == Branches[i].Token) return i;
        }

        return -1;
    }

    /// <summary>True once the node settled, failed, was canceled or ended: it takes no other outcome.</summary>
    protected bool IsDecided => IsTerminated || (Flags & (NodeFlags.Completing | NodeFlags.CancelConfirmed)) != 0;

    /// <summary>
    /// Starts the branches in order, until one decides the node. A node released meanwhile (a branch that completed as it
    /// started settled it, and its consumer let it go) is not touched again: it may be another task by now. A branch that
    /// is not the unstarted task it was (passed to another consumer too, which started it first, or consumed and its node
    /// reused) throws before anything of it is written, and its slot is cleared: it stays with whoever has it.
    /// </summary>
    protected void StartBranches()
    {
        var token = Token;
        for (var i = 0; i < BranchCount; i++)
        {
            if (Token != token || IsDecided) return;
            var slot = Branches[i];
            var b = slot.Node;
            if (b.Token != slot.Token || b.State != NodeState.Unstarted)
            {
                Branches[i] = default;
                throw Errors.AlreadyStarted(b, slot.Token);
            }

            b.Awaiter = this;
            b.AwaiterToken = token;
            StartedCount = i + 1;
            b.Start(this);
        }
    }

    /// <summary>
    /// Cancels the started branches but <paramref name="keep"/> with <paramref name="cause"/>, then unwinds them, later
    /// started first. A branch that does not end at once (it runs its cleanup) holds this node's end. False when this node
    /// was released meanwhile: the caller must not touch it any more.
    /// </summary>
    protected bool UnwindOtherBranches(int keep, CancelCause cause)
    {
        for (var j = StartedCount - 1; j >= 0; j--)
        {
            var b = Branches[j].Node;
            if (j != keep && b.Token == Branches[j].Token) Unwinder.MarkSubtree(b, cause);
        }

        var world = World;
        var token = Token;
        for (var j = StartedCount - 1; j >= 0; j--)
        {
            var b = Branches[j].Node;
            if (j != keep && b.Token == Branches[j].Token && b.State == NodeState.Running) world.Unwinder.UnwindOrDefer(b);
            if (Token != token) return false;
        }

        return true;
    }

    /// <summary>
    /// Lets go of the branches, later started first: an unstarted one is released unstarted, a running one when it ends, an
    /// ended one now. A branch whose value is in the result (<paramref name="taken"/>: its index, TakenAll or TakenNone)
    /// counts as received; another that ended with a value gives it back to its source, so values taken from one
    /// subscription go back to its front in the order they were taken. A branch this node never started is released
    /// only while it is still unstarted: one that runs now was started by another consumer it was passed to as well, and
    /// belongs to that one.
    /// </summary>
    protected void ReleaseBranches(int taken)
    {
        for (var i = BranchCount - 1; i >= 0; i--)
        {
            var slot = Branches[i];
            Branches[i] = default;
            var b = slot.Node;
            if (b == null || b.Token != slot.Token) continue;
            if (i >= StartedCount)
            {
                b.ReleaseUnstarted();
                continue;
            }

            if (taken == i || taken == TakenAll) b.Flags &= ~NodeFlags.Unclaimed;
            b.ReleaseOrOrphan();
        }

        BranchCount = 0;
        StartedCount = 0;
    }

    /// <summary>
    /// Settles with the result set (the Race winner): the other branches are canceled (RaceLost) and unwound, then the
    /// node ends and its caller resumes.
    /// </summary>
    protected void Settle(int keep)
    {
        Flags |= NodeFlags.Completing;
        if (!UnwindOtherBranches(keep, CancelCause.RaceLost)) return;
        // A loser's unwinding may have canceled this node (its finally canceled a handle around it): then nobody takes the value.
        var canceled = IsCancelConfirmed;
        ReleaseBranches(canceled ? TakenNone : keep);
        if (canceled) TerminateCanceled();
        else Terminate(NodeState.Succeeded);
    }

    internal override void UnwindSelf()
    {
        if (IsTerminated || (Flags & NodeFlags.Ending) != 0) return;
        ReleaseBranches(TakenNone);
        TerminateCanceled();
    }

    /// <summary>
    /// A branch failed. The first failure decides the node: the other branches are canceled (Fault) and unwound, and the
    /// node ends with the exception. One that comes after the node was decided has no receiver: Undelivered.
    /// </summary>
    internal override void OnAwaitedFailed(FlowNode child, bool fromQueue)
    {
        var idx = IndexOf(child);
        if (idx < 0) return;
        var fault = child.Failure;
        var world = World;
        if (IsDecided)
        {
            world.Reporter.ReportUndelivered(fault.Info);
            return;
        }

        Flags |= NodeFlags.Completing;
        if (!UnwindOtherBranches(idx, CancelCause.Fault))
        {
            world.Reporter.ReportUndelivered(fault.Info);
            return;
        }

        ReleaseBranches(TakenNone);
        if (IsCancelConfirmed)
        {
            world.Reporter.ReportUndelivered(fault.Info);
            TerminateCanceled();
            return;
        }

        EndWithFault(fault);
    }

    protected override void ResetNode()
    {
        ReleaseBranches(TakenNone);
        base.ResetNode();
    }

    internal override string DescribeWait()
    {
        var live = 0;
        for (var c = FirstChild; c != null; c = c.NextSibling) live++;
        if (!IsEndHeld) return $"{live}/{BranchCount} branches";
        var outcome = HeldEnd switch
        {
            NodeState.Succeeded => "decided",
            NodeState.Faulted => "failed",
            _ => "canceled",
        };
        return $"{outcome}, {live} {(live == 1 ? "branch" : "branches")} still ending";
    }
}

internal abstract class RaceNodeBase<TR> : CombinatorNode<TR>
{
    protected override void OnStart() => StartBranches();

    /// <summary>The first completion processed wins: the losers are canceled and unwound, then the caller resumes.</summary>
    internal override void OnAwaitedTerminated(FlowNode child, bool fromQueue)
    {
        if (IsDecided) return;
        var idx = IndexOf(child);
        if (idx < 0 || child.State != NodeState.Succeeded) return;
        Result = BuildResult(idx);
        Settle(idx);
    }

    /// <summary>The result with the value of the winner <paramref name="index"/>.</summary>
    protected abstract TR BuildResult(int index);

    internal override string DescribeWait() => "Race " + base.DescribeWait();
}

internal abstract class WhenAllNodeBase<TR> : CombinatorNode<TR>
{
    int _completed;

    protected override void OnStart()
    {
        _completed = 0;
        if (BranchCount > 0)
        {
            StartBranches();
            return;
        }

        Result = BuildResult();
        Terminate(NodeState.Succeeded);
    }

    internal override void OnAwaitedTerminated(FlowNode child, bool fromQueue)
    {
        if (IsDecided) return;
        var idx = IndexOf(child);
        if (idx < 0 || child.State != NodeState.Succeeded || ++_completed < BranchCount) return;
        Result = BuildResult();
        ReleaseBranches(TakenAll);
        Terminate(NodeState.Succeeded);
    }

    /// <summary>The values of all branches.</summary>
    protected abstract TR BuildResult();

    internal override string DescribeWait() => "WhenAll " + base.DescribeWait();
}

internal sealed class RaceIndexNode : RaceNodeBase<int>
{
    static NodePool<RaceIndexNode> s_pool;

    internal static RaceIndexNode Rent(int count)
    {
        var n = s_pool.Rent() ?? new RaceIndexNode();
        n.EnsureCapacity(count);
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "Race";
    protected override int BuildResult(int index) => index;
    protected override void ReturnToPool() => s_pool.Return(this);
}

internal sealed class RaceArrayNode<T> : RaceNodeBase<RaceResult<T>>
{
    static NodePool<RaceArrayNode<T>> s_pool;

    internal static RaceArrayNode<T> Rent(int count)
    {
        var n = s_pool.Rent() ?? new RaceArrayNode<T>();
        n.EnsureCapacity(count);
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "Race";
    protected override RaceResult<T> BuildResult(int index) => new(index, ((FlowNode<T>)Branches[index].Node).Result);
    protected override void ReturnToPool() => s_pool.Return(this);
}

internal sealed class WhenAllArrayNode<T> : WhenAllNodeBase<T[]>
{
    static NodePool<WhenAllArrayNode<T>> s_pool;

    internal static WhenAllArrayNode<T> Rent(int count)
    {
        var n = s_pool.Rent() ?? new WhenAllArrayNode<T>();
        n.EnsureCapacity(count);
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "WhenAll";

    protected override T[] BuildResult()
    {
        var r = new T[BranchCount];
        for (var i = 0; i < BranchCount; i++) r[i] = ((FlowNode<T>)Branches[i].Node).Result;
        return r;
    }

    protected override void ReturnToPool() => s_pool.Return(this);
}

internal sealed class WhenAllVoidNode : WhenAllNodeBase<FlowUnit>
{
    static NodePool<WhenAllVoidNode> s_pool;

    internal static WhenAllVoidNode Rent(int count)
    {
        var n = s_pool.Rent() ?? new WhenAllVoidNode();
        n.EnsureCapacity(count);
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "WhenAll";
    protected override FlowUnit BuildResult() => FlowUnit.Default;
    protected override void ReturnToPool() => s_pool.Return(this);
}
