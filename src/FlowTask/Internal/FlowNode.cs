namespace Katout.FlowTask.Internal;

/// <summary>
/// A node of the scope tree. Every started FlowTask is one: state machines, combinators and waits. The tree owns every
/// lifetime, and no node ends before its children.
/// </summary>
internal abstract class FlowNode
{
    internal uint Token = 1;
    internal NodeState State;
    internal NodeFlags Flags;
    internal CancelCause Cause;
    internal AwaitKind PendingAwait;
    protected NodeTraits Traits;

    internal FlowWorld World;
    internal Clock Clock;
    internal Clock ClockOverride;
    internal string CustomName;

    // Where a wait or combinator was created (the caller's file and line), for diagnostics; null and 0 when unknown. A
    // state machine keeps here the place of the wait it parks (read only through GetWaitSite).
    internal string SiteFile;
    internal int SiteLine;

    // Tree links; children in start order.
    internal FlowNode Parent;
    internal FlowNode FirstChild;
    internal FlowNode LastChild;
    internal FlowNode PrevSibling;
    internal FlowNode NextSibling;

    // Who waits for this node, and what it waits for.
    internal FlowNode Awaiter;
    internal uint AwaiterToken;
    internal FlowNode Awaiting;
    internal uint AwaitingToken;

    // The state a node whose end waits for its children ends in, and whether with its fault.
    NodeState _heldEnd;
    bool _heldFault;

    // The World's tick list.
    internal FlowNode TickPrev;
    internal FlowNode TickNext;

    CleanupStack _cleanups;

    internal FailureRecord Failure;
    internal IFlowObserver Observer;

    /// <summary>The fault the scope last received at an await: leaving the scope, it keeps that record and its path.</summary>
    internal FailureRecord Rethrown;

    internal int RunDepth;
    internal int SyncCount;
    internal long SyncTickId;
    internal double SuspendedAt;

    /// <summary>State machines: awaits that threw FlowCanceledException again while World.Dispose ended the flows.</summary>
    internal byte Rethrows;

    /// <summary>State machines: in the World's <see cref="CleanupWatch"/>.</summary>
    internal bool CleanupWatched;

    internal bool IsTerminated => State >= NodeState.Succeeded;
    internal bool IsCancelConfirmed => (Flags & NodeFlags.CancelConfirmed) != 0;
    internal bool IsLive => State == NodeState.Running && (Flags & (NodeFlags.CancelConfirmed | NodeFlags.Completing)) == 0;

    /// <summary>
    /// A canceled state machine that runs its catch and finally blocks: FlowCanceledException has reached its code, and its
    /// awaits run as those of a live scope, except while World.Dispose ends the flows.
    /// </summary>
    internal bool RunsCleanup => (Flags & NodeFlags.UnwindBegun) != 0 && !World.IsDisposing;

    // Field tests rather than virtual properties: they are read on every await, and IL2CPP cannot devirtualize.
    internal bool IsStateMachine => (Traits & NodeTraits.StateMachine) != 0;
    internal bool IsLeaf => (Traits & NodeTraits.Leaf) != 0;

    /// <summary>
    /// A clock wait the awaiting state machine can wait for itself: no clock or name of its own, and not
    /// Flow.NonCancelable (the cancellation skips a node, which a parked wait does not have).
    /// </summary>
    internal bool IsParkableWait =>
        (Traits & NodeTraits.ParkableWait) != 0 && ClockOverride == null && CustomName == null && (Flags & NodeFlags.NonCancelable) == 0;

    internal bool IsEndHeld => _heldEnd != NodeState.Pooled;
    internal NodeState HeldEnd => _heldEnd;

    internal virtual void GetParkTarget(Clock clock, out ParkKind kind, out double target, out double amount) =>
        throw new InvalidOperationException("Not a parkable wait.");

    internal virtual void Park(FlowNode wait) => throw new InvalidOperationException("Only state machines park.");
    internal virtual void FailPark(Exception ex) => throw new InvalidOperationException("Only state machines park.");
    internal virtual object RunActionObject => null;

    /// <summary>Scope name; the method name for state machines.</summary>
    internal abstract string Name { get; }

    internal virtual Type SourceType => null;
    internal virtual string SourceMethod => null;

    /// <summary>What the node waits for, for dumps.</summary>
    internal virtual string DescribeWait() => null;

    /// <summary>
    /// Where what <see cref="DescribeWait"/> describes was created: a wait's or combinator's own place; for a state
    /// machine, the place of the wait it waits on directly.
    /// </summary>
    internal virtual void GetWaitSite(out string file, out int line)
    {
        file = SiteFile;
        line = SiteLine;
    }

    /// <summary>Records where the wait or combinator was created; an empty path is unknown, and so is its line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetSite(string file, int line)
    {
        SiteFile = string.IsNullOrEmpty(file) ? null : file;
        SiteLine = SiteFile == null ? 0 : line;
    }

    /// <summary>The clock whose Pause holds this node's queued resume.</summary>
    internal virtual Clock ResumeClock => Clock;

    /// <summary>The clock the tick list evaluates this node on.</summary>
    internal virtual Clock TickClock => Clock;

    internal string DisplayName => CustomName ?? Name;

    // ------------------------------------------------------------------ start

    /// <summary>Starts the node as the last child of <paramref name="parent"/> and runs its synchronous part.</summary>
    internal void Start(FlowNode parent)
    {
        if (State != NodeState.Unstarted)
            throw new FlowMisuseException("A FlowTask can be started (awaited or passed to a combinator) only once.");
        var world = parent.World;
        var clock = ClockOverride ?? parent.Clock;
        if (clock.LiveWorld != world) throw Errors.ClockNotUsable(clock);
        World = world;
        Clock = clock;
        State = NodeState.Running;
        AttachTo(parent);
        // A state machine runs its method inside the compiler's catch, so its OnStart does not throw. No try block on
        // that path: it made every start in a chain of synchronous awaits slower.
        if (IsStateMachine) OnStart();
        else StartGuarded();
    }

    protected abstract void OnStart();

    /// <summary>
    /// OnStart of any other node. One that throws (a misuse its start found) ends with what it started, leaves the tree
    /// and goes back to its pool, and the exception goes on to the await or combinator that started it. A node that a
    /// branch settled as it started, and that its consumer released meanwhile, is left alone: it may be another task now.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void StartGuarded()
    {
        var token = Token;
        try
        {
            OnStart();
        }
        catch
        {
            if (Token == token && State == NodeState.Running)
            {
                Terminate(NodeState.Canceled);
                if (Token == token) ReleaseOrOrphan();
            }

            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AttachTo(FlowNode parent)
    {
        Parent = parent;
        PrevSibling = parent.LastChild;
        NextSibling = null;
        if (parent.LastChild != null) parent.LastChild.NextSibling = this;
        else parent.FirstChild = this;
        parent.LastChild = this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Detach()
    {
        var p = Parent;
        if (p == null) return;
        if (PrevSibling != null) PrevSibling.NextSibling = NextSibling;
        else p.FirstChild = NextSibling;
        if (NextSibling != null) NextSibling.PrevSibling = PrevSibling;
        else p.LastChild = PrevSibling;
        Parent = null;
        PrevSibling = null;
        NextSibling = null;
    }

    // ------------------------------------------------------------------ end

    /// <summary>
    /// Ends the node: its live children are canceled and unwound, its cleanups run (AddCleanup and Own, LIFO), it leaves
    /// the tree and its consumer is told. A child that does not end at once (one running its cleanup, or on the running
    /// stack) holds the end until it has ended. Once the end has started, another call does nothing.
    /// </summary>
    internal void Terminate(NodeState final)
    {
        if (State != NodeState.Running || (Flags & NodeFlags.Ending) != 0) return;
        End(final, false);
    }

    void End(NodeState final, bool fault)
    {
        Flags |= NodeFlags.Completing | NodeFlags.Ending;
        if (FirstChild != null && !EndChildrenFirst(final, fault)) return;
        if (_cleanups is { Count: > 0 })
        {
            RunCleanups();
            // A cleanup can start flows (Flow.Spawn in an AddCleanup action): they end first too.
            if (FirstChild != null && !EndChildrenFirst(final, fault)) return;
        }

        if ((Flags & NodeFlags.InTickList) != 0) World.TickList.Remove(this);
        // A fault held with the end whose consumer left meanwhile (a Race won by another branch): nobody takes it.
        if (fault && (Flags & NodeFlags.ConsumerGone) != 0)
        {
            Failure = FailureRecord.ForUndelivered(World.Reporter.ReportUndelivered(Failure.Info));
            fault = false;
        }

        State = final;
        var parent = Parent;
        Detach();
        if (parent == null)
        {
            Notify(null, fault);
            return;
        }

        var parentToken = parent.Token;
        Notify(parent, fault);
        ContinueHeldParent(parent, parentToken);
    }

    /// <summary>Ends the live children first; false when some still run, which holds this node's end until they end.</summary>
    bool EndChildrenFirst(NodeState final, bool fault)
    {
        World.Unwinder.EndChildren(this, final);
        if (FirstChild == null) return true;
        _heldEnd = final;
        _heldFault = fault;
        Flags |= NodeFlags.WaitsForChildren;
        return false;
    }

    /// <summary>A child left <paramref name="parent"/>: the last one continues a parent that waits for its children.</summary>
    internal static void ContinueHeldParent(FlowNode parent, uint parentToken)
    {
        if ((parent.Flags & NodeFlags.WaitsForChildren) != 0 && parent.Token == parentToken && parent.FirstChild == null)
            parent.OnChildrenEnded();
    }

    /// <summary>The last child has ended: the held end goes on, or a canceled state machine held before it unwound unwinds.</summary>
    void OnChildrenEnded()
    {
        Flags &= ~NodeFlags.WaitsForChildren;
        var final = _heldEnd;
        if (final == NodeState.Pooled)
        {
            World.Unwinder.ScheduleUnwindResume(this);
            return;
        }

        _heldEnd = NodeState.Pooled;
        End(final, _heldFault);
    }

    /// <summary>Tells the handle's observers, then the consumer; a root flow's fault goes to OnUnhandledException.</summary>
    void Notify(FlowNode parent, bool fault)
    {
        var token = Token;
        Observer?.OnTerminated(this);
        var aw = Awaiter;
        if (aw != null)
        {
            if (aw.Token != AwaiterToken)
            {
                if (fault) World.Reporter.ReportUndelivered(Failure.Info);
            }
            else if (fault)
            {
                aw.OnAwaitedFailed(this, false);
            }
            else
            {
                aw.OnAwaitedTerminated(this, false);
            }
        }

        if (Token != token) return;
        if (parent is RootNode root) root.OnChildEnded(this);
        if (Token == token && (Flags & NodeFlags.ConsumerGone) != 0) Release();
    }

    // ------------------------------------------------------------------ failures

    /// <summary>
    /// An exception left the live node: its code threw or rethrew a child's, a combinator passes a branch's on, or it
    /// carried the exception of a child it spawned. The consumer receives it (the awaiting scope rethrows it, a
    /// combinator passes it on, a root flow reports it to OnUnhandledException); a spawned node passes it to the scope that spawned it
    /// while that scope runs (<see cref="FailOwner"/>). With no receiver (the consumer let go, the owner is ending, or
    /// the end is already decided), it is reported as Undelivered.
    /// </summary>
    internal void EndWithFault(FailureRecord f)
    {
        if (State != NodeState.Running) return;
        if ((Flags & NodeFlags.Ending) != 0)
        {
            World.Reporter.ReportUndelivered(f.Info);
            return;
        }

        if ((Flags & NodeFlags.ConsumerGone) != 0 || IsSpawnOfAnOwnerThatIsEnding)
        {
            Failure = FailureRecord.ForUndelivered(World.Reporter.ReportUndelivered(f.Info));
            End(NodeState.Faulted, false);
            return;
        }

        if ((Flags & NodeFlags.Spawned) != 0 && Parent is not RootNode)
        {
            FailOwner(f);
            return;
        }

        Failure = f;
        End(NodeState.Faulted, true);
    }

    /// <summary>
    /// A child spawned by a running scope failed. The scope is canceled (CancelCause.Fault) carrying the exception, which it
    /// ends with once unwound, for its own consumer. Its subtree is canceled before the child's end is told to anyone, so a
    /// join of the child inside the scope ends with the scope instead of throwing the same failure again.
    /// </summary>
    void FailOwner(FailureRecord f)
    {
        var owner = Parent;
        var ownerToken = owner.Token;
        Unwinder.CarryFault(owner, f, this);
        Failure = f;
        End(NodeState.Faulted, true);
        if (owner.Token == ownerToken && owner.State == NodeState.Running) World.Unwinder.UnwindOrDefer(owner);
    }

    /// <summary>A spawned node whose owner is ending or canceled: the owner takes no failure from it.</summary>
    bool IsSpawnOfAnOwnerThatIsEnding =>
        (Flags & NodeFlags.Spawned) != 0 && Parent is { } owner && (owner.Flags & (NodeFlags.Completing | NodeFlags.CancelConfirmed)) != 0;

    /// <summary>Ends a canceled node: Canceled, or with the exception it carries for its consumer.</summary>
    internal void TerminateCanceled()
    {
        var f = Failure;
        if (f is not { Kind: FailureKind.Carried })
        {
            Terminate(NodeState.Canceled);
            return;
        }

        Failure = null;
        f.Kind = FailureKind.Fault;
        EndWithFault(f);
    }

    /// <summary>The fault a node that ended Faulted holds for its consumer, or null.</summary>
    internal FailureRecord Fault => State == NodeState.Faulted && Failure is { Kind: FailureKind.Fault } fault ? fault : null;

    /// <summary>A node this node waits for has ended.</summary>
    internal virtual void OnAwaitedTerminated(FlowNode child, bool fromQueue)
    {
    }

    /// <summary>A node this node waits for ended with a fault. An override delivers it or reports it; it never drops it.</summary>
    internal virtual void OnAwaitedFailed(FlowNode child, bool fromQueue) => OnAwaitedTerminated(child, fromQueue);

    /// <summary>WithoutResult received the completion and ignores the value: a subscription's counts as taken.</summary>
    internal virtual void IgnoreResult()
    {
    }

    /// <summary>A queued entry: a state machine's resume, or a leaf's delivery to its awaiter.</summary>
    internal virtual void OnDequeued()
    {
    }

    /// <summary>Unwinds this node; its children have been processed first (post-order).</summary>
    internal abstract void UnwindSelf();

    /// <summary>Step 3 of a Tick: true when the wait is satisfied.</summary>
    internal virtual bool EvaluateTick() => false;

    internal virtual void OnTickSatisfied()
    {
    }

    // ------------------------------------------------------------------ cleanups

    internal void AddCleanup(object target, CleanupKind kind, uint token = 0) => (_cleanups ??= new CleanupStack()).Push(target, kind, token);

    /// <summary>True when this node made <paramref name="clock"/> with Flow.CreateClock.</summary>
    internal bool OwnsClock(Clock clock) => _cleanups != null && _cleanups.ContainsClock(clock);

    /// <summary>Runs the cleanups as the node ends; those of a state machine run with it as the current scope.</summary>
    void RunCleanups()
    {
        var world = World;
        var prevScope = world.CurrentScope;
        if (IsStateMachine) world.CurrentScope = this;
        try
        {
            _cleanups.RunAll(this);
        }
        finally
        {
            world.CurrentScope = prevScope;
        }
    }

    // ------------------------------------------------------------------ awaiting and pooling

    /// <summary>The state machine <paramref name="scope"/> awaits this node: it resumes when this node ends.</summary>
    internal void SetAwaiter(FlowNode scope)
    {
        Awaiter = scope;
        AwaiterToken = scope.Token;
        scope.Awaiting = this;
        scope.AwaitingToken = Token;
    }

    /// <summary>A node just created or rented: a task that has not started.</summary>
    internal void InitUnstarted() => State = NodeState.Unstarted;

    /// <summary>The consumer lets go: released now when unstarted or ended, otherwise when it ends.</summary>
    internal void ReleaseOrOrphan()
    {
        if (State == NodeState.Unstarted) ReleaseUnstarted();
        else if (IsTerminated) Release();
        else Flags |= NodeFlags.ConsumerGone;
    }

    /// <summary>Returns the node to its pool once nobody can observe it any more.</summary>
    internal void Release()
    {
        if (State == NodeState.Pooled) return;
        if (RunDepth > 0)
        {
            Flags |= NodeFlags.ReleasePending;
            return;
        }

        if ((Flags & NodeFlags.NoPool) != 0) return;
        ResetNode();
        ReturnToPool();
    }

    internal void ReleaseUnstarted()
    {
        if (State != NodeState.Unstarted) return;
        State = NodeState.Canceled;
        Release();
    }

    protected virtual void ResetNode()
    {
        unchecked
        {
            if (++Token == 0) Token = 1;
        }

        State = NodeState.Pooled;
        Flags = NodeFlags.None;
        Cause = CancelCause.None;
        PendingAwait = AwaitKind.None;
        World = null;
        Clock = null;
        ClockOverride = null;
        CustomName = null;
        SiteFile = null;
        SiteLine = 0;
        Parent = FirstChild = LastChild = PrevSibling = NextSibling = null;
        Awaiter = null;
        AwaiterToken = 0;
        Awaiting = null;
        AwaitingToken = 0;
        _heldEnd = NodeState.Pooled;
        _heldFault = false;
        TickPrev = TickNext = null;
        Failure = null;
        Observer = null;
        Rethrown = null;
        _cleanups?.Clear();
        RunDepth = 0;
        SyncCount = 0;
        SyncTickId = 0;
        SuspendedAt = 0;
        Rethrows = 0;
        CleanupWatched = false;
    }

    protected abstract void ReturnToPool();

    // ------------------------------------------------------------------ diagnostics

    /// <summary>Scope path from the root: the names of the state machines and named nodes above, then this node.</summary>
    internal string BuildScopePath() => PathUnder(Parent, DisplayName);

    /// <summary>The scope path of a node named <paramref name="name"/> under <paramref name="parent"/>.</summary>
    internal static string PathUnder(FlowNode parent, string name)
    {
        var names = new List<string> { name };
        for (var n = parent; n != null; n = n.Parent)
        {
            if (n.IsStateMachine || n.CustomName != null) names.Add(n.DisplayName);
        }

        names.Reverse();
        return string.Join(" > ", names);
    }

    internal FlowStatus Status => State switch
    {
        NodeState.Unstarted => FlowStatus.Unstarted,
        NodeState.Running => FlowStatus.Running,
        NodeState.Succeeded => FlowStatus.Succeeded,
        NodeState.Canceled => FlowStatus.Canceled,
        NodeState.Faulted => FlowStatus.Faulted,
        _ => FlowStatus.Invalid,
    };
}

internal abstract class FlowNode<T> : FlowNode
{
    internal T Result;

    /// <summary>Reads the result of a completed node for its awaiter and releases the node.</summary>
    internal T ConsumeResult()
    {
        var r = TakeResult();
        Release();
        return r;
    }

    /// <summary>Reads the result for a consumer that receives it: the value no longer goes back to its source.</summary>
    internal T TakeResult()
    {
        Flags &= ~NodeFlags.Unclaimed;
        return Result;
    }

    protected override void ResetNode()
    {
        base.ResetNode();
        Result = default;
    }
}

/// <summary>The World's root scope. A flow started with FlowWorld.Run is its child; a fault that ends one goes to OnUnhandledException.</summary>
internal sealed class RootNode : FlowNode
{
    internal RootNode(FlowWorld world)
    {
        World = world;
        Clock = world.DefaultClock;
        State = NodeState.Running;
        Flags |= NodeFlags.NoPool;
    }

    internal override string Name => World.Name;

    protected override void OnStart()
    {
    }

    internal void OnChildEnded(FlowNode child)
    {
        if (child.Fault is { } f) World.Reporter.Report(f.Info);
    }

    internal override void UnwindSelf()
    {
    }

    protected override void ReturnToPool()
    {
    }
}
