namespace Katout.FlowTask.Internal;

/// <summary>
/// The scope of one call of an async FlowTask method. The builder keeps it and makes it the World's current scope on
/// every resume, so scope tracking never depends on who resumes it.
/// </summary>
internal abstract class StateMachineScope<T> : FlowNode<T>
{
    internal readonly Action RunAction;

    // A parked wait (NodeFlags.ParkedWait): a frame count or time of this scope's clock.
    ParkKind _parkKind;
    double _parkTarget;
    double _parkAmount;

    protected StateMachineScope()
    {
        Traits = NodeTraits.StateMachine;
        RunAction = Run;
    }

    internal override object RunActionObject => RunAction;

    protected abstract void MoveNextCore();

    // FlowNode.Start calls this with no try block: nothing here may throw before MoveNextCore, whose exceptions the
    // compiler's catch hands to SetException.
    protected override void OnStart() => RunInCurrentWorld(World);

    /// <summary>Runs the state machine to its next suspension, as the current scope.</summary>
    internal void Run()
    {
        var world = World;
        var prevWorld = FlowWorld.t_current;
        if (ReferenceEquals(prevWorld, world))
        {
            RunInCurrentWorld(world);
            return;
        }

        FlowWorld.t_current = world;
        try
        {
            RunInCurrentWorld(world);
        }
        finally
        {
            FlowWorld.t_current = prevWorld;
        }
    }

    void RunInCurrentWorld(FlowWorld world)
    {
        var prevScope = world.CurrentScope;
        world.CurrentScope = this;
        world.ExecutionDepth++;
        world.RunningStack.Push(this);
        RunDepth++;
        PendingAwait = AwaitKind.None;
        // Whatever resume was queued is taken by this run: one queued for an earlier await must not resume a later one.
        Flags &= ~NodeFlags.ResumeQueued;
        try
        {
            MoveNextCore();
        }
        finally
        {
            RunDepth--;
            world.RunningStack.Pop();
            world.CurrentScope = prevScope;
            if (RunDepth == 0 && (Flags & NodeFlags.ReleasePending) != 0)
            {
                Flags &= ~NodeFlags.ReleasePending;
                Release();
            }

            world.ExecutionDepth--;
        }
    }

    /// <summary>
    /// The builder's AwaitOnCompleted: the continuation to register, or null when none may be. Only the library's awaiters
    /// can be canceled and unwound: suspending on any other is a misuse (<see cref="OnUnbridgedSuspend"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Action BeforeSuspend<TAwaiter>()
    {
        if (LibraryAwaiter<TAwaiter>.Is) return BeforeSuspend();
        OnUnbridgedSuspend(typeof(TAwaiter));
        return null;
    }

    Action BeforeSuspend()
    {
        var kind = PendingAwait;
        PendingAwait = AwaitKind.None;
        if (kind == AwaitKind.Discard)
        {
            // The scope ends at this await: an endless loop (it carries the misuse), or the rethrow limit while
            // World.Dispose ends the flows.
            TerminateCanceled();
            return null;
        }

        SuspendedAt = World.UnscaledClock.Time;
        return RunAction;
    }

    /// <summary>
    /// The method suspended on an awaiter that is not the library's, which could be neither canceled nor unwound. Its
    /// continuation is never registered and it never resumes (its remaining finally blocks do not run; AddCleanup and Own
    /// do): the scope ends with a FlowMisuseException for its consumer, or, canceled, reports it as a cleanup
    /// exception.
    /// </summary>
    internal void OnUnbridgedSuspend(Type awaiterType)
    {
        PendingAwait = AwaitKind.None;
        var path = BuildScopePath();
        var ex = Errors.UnbridgedAwait(path, awaiterType);
        if (!IsCancelConfirmed)
        {
            EndWithFault(FailureRecord.ForFault(ex, path));
            return;
        }

        World.Reporter.ReportCleanupException(this, ex);
        TerminateCanceled();
    }

    internal void OnSetResult(T value)
    {
        if (IsTerminated) return;
        if (IsCancelConfirmed)
        {
            // FlowCanceledException reached the code, and it returned: a catch swallowed it.
            if ((Flags & NodeFlags.UnwindBegun) != 0 && !World.IsDisposing) World.Reporter.ReportSwallowedCancellation(this);
            TerminateCanceled();
            return;
        }

        Result = value;
        Terminate(NodeState.Succeeded);
    }

    /// <summary>
    /// The method threw. Live, the scope ends with the exception for its consumer (one rethrown to it at an await keeps
    /// its record). Canceled, it ends canceled and the exception is reported: as Undelivered when it was carrying it (it
    /// reached a finally block) or threw it before FlowCanceledException reached its code (after a Flow.NonCancelable
    /// await, a finally block rethrows the exception it carried), as a cleanup exception when its cleanup threw it. Canceled
    /// by a child it spawned before FlowCanceledException reached its code, its own exception goes on and the child's is
    /// Undelivered.
    /// </summary>
    internal void OnSetException(Exception ex)
    {
        if (IsTerminated) return;
        if (ex is FlowCanceledException || (IsCancelConfirmed && WrapsTheFlowsCancellation(ex)))
        {
            if (IsCancelConfirmed) TerminateCanceled();
            else EndWithFault(FailureRecord.ForFault(new FlowMisuseException("FlowCanceledException left a scope that was not canceled. Do not throw it by hand.", ex), BuildScopePath()));
            return;
        }

        var rethrown = Rethrown;
        Rethrown = null;
        var received = rethrown != null && ReferenceEquals(rethrown.Info.Exception, ex);
        var fault = received ? rethrown : null;
        if (!IsCancelConfirmed)
        {
            EndWithFault(fault ?? FailureRecord.ForFault(ex, BuildScopePath()));
            return;
        }

        if (Failure is { Kind: FailureKind.Carried } childFault && (Flags & NodeFlags.UnwindBegun) == 0)
        {
            Failure = null;
            World.Reporter.ReportUndelivered(childFault.Info);
            EndWithFault(fault ?? FailureRecord.ForFault(ex, BuildScopePath()));
            return;
        }

        if (received) World.Reporter.ReportUndelivered(rethrown.Info);
        else if ((Flags & NodeFlags.UnwindBegun) == 0) World.Reporter.ReportUndelivered(ex, BuildScopePath());
        else World.Reporter.ReportCleanupException(this, ex);
        TerminateCanceled();
    }

    /// <summary>An OperationCanceledException that wraps the scope's FlowCanceledException passes the cancellation on.</summary>
    static bool WrapsTheFlowsCancellation(Exception ex)
    {
        for (var e = ex as OperationCanceledException; e != null; e = e.InnerException as OperationCanceledException)
        {
            if (e is FlowCanceledException) return true;
        }

        return false;
    }

    /// <summary>
    /// Live, or running its cleanup: it takes results. Another canceled scope is resumed by its unwinding. A method that
    /// has ended takes none, even from a resume queued before it ended: its state machine would start over.
    /// </summary>
    bool TakesResults => (Flags & (NodeFlags.CancelConfirmed | NodeFlags.Completing)) == 0 || ((Flags & NodeFlags.Completing) == 0 && RunsCleanup);

    /// <summary>The awaited child ended (a failure too: the await rethrows it): the scope resumes.</summary>
    internal override void OnAwaitedTerminated(FlowNode child, bool fromQueue)
    {
        if (IsTerminated || !TakesResults) return;
        // As ResumeQueue.Flush decides: a removed scope clock stays paused but holds no resume.
        var clock = Clock;
        if (fromQueue && RunDepth == 0 && (!clock.EffectivelyPaused || clock.LiveWorld == null)) Run();
        else QueueResume();
    }

    void QueueResume()
    {
        Flags |= NodeFlags.ResumeQueued;
        World.ResumeQueue.Enqueue(this);
    }

    internal override void OnDequeued()
    {
        if ((Flags & NodeFlags.ResumeQueued) != 0 && State == NodeState.Running && TakesResults && RunDepth == 0) Run();
    }

    internal override void Park(FlowNode wait)
    {
        wait.GetParkTarget(Clock, out _parkKind, out _parkTarget, out _parkAmount);
        // A state machine has no place of its own: its SiteFile and SiteLine hold the parked wait's.
        SiteFile = wait.SiteFile;
        SiteLine = wait.SiteLine;
        Flags |= NodeFlags.ParkedWait;
        World.TickList.Add(this);
    }

    /// <summary>
    /// The clock of the parked wait was removed: the wait fails as the wait node would have, and the await throws the
    /// fault (AwaitCore.EndPark), which waits in <see cref="FlowNode.Rethrown"/>.
    /// </summary>
    internal override void FailPark(Exception ex)
    {
        Flags &= ~NodeFlags.ParkedWait;
        var fault = FailureRecord.ForFault(ex, BuildScopePath());
        fault.Pending = true;
        Rethrown = fault;
        QueueResume();
    }

    internal override bool EvaluateTick() => _parkKind == ParkKind.Frames ? Clock.FrameCount >= _parkTarget : Clock.Time >= _parkTarget;

    internal override void OnTickSatisfied()
    {
        if (State == NodeState.Running && TakesResults) QueueResume();
    }

    internal override void UnwindSelf()
    {
        // A method that has ended is never resumed: its state machine would start over from its first line.
        if (State != NodeState.Running || (Flags & NodeFlags.Completing) != 0 || RunsCleanup) return;
        // Its children end first, as through a call stack: FlowCanceledException reaches this code once they have ended
        // (the last one schedules this unwind again). A scope that runs now throws it at its next await itself.
        if (FirstChild != null && RunDepth == 0)
        {
            Flags |= NodeFlags.WaitsForChildren;
            // A Flow.NonCancelable await it waits in holds its unwinding as a cleanup await would.
            if (Awaiting is { } a && a.Token == AwaitingToken && (a.Flags & NodeFlags.NonCancelable) != 0) World.CleanupWatch.Add(this);
            return;
        }

        // A parked wait keeps its flag: its GetResult throws FlowCanceledException.
        if ((Flags & NodeFlags.ParkedWait) != 0) World.TickList.Remove(this);
        if (RunDepth == 0) Run();
    }

    internal override string DescribeWait()
    {
        if ((Flags & (NodeFlags.ParkedWait | NodeFlags.InTickList)) == (NodeFlags.ParkedWait | NodeFlags.InTickList))
        {
            return _parkKind == ParkKind.Frames
                ? DelayFramesNode.Describe((long)_parkAmount, Clock, (long)_parkTarget)
                : WaitForSecondsNode.Describe(_parkAmount, Clock, _parkTarget);
        }

        var a = Awaiting;
        if (a == null || a.Token != AwaitingToken || a.IsTerminated) return null;
        return a.IsLeaf ? a.DescribeWait() ?? a.DisplayName : a.DisplayName;
    }

    /// <summary>
    /// The place of the parked wait, or of the node awaited directly: a wait's or combinator's own place, none for the
    /// scope of an async FlowTask method (its call records no place).
    /// </summary>
    internal override void GetWaitSite(out string file, out int line)
    {
        if ((Flags & (NodeFlags.ParkedWait | NodeFlags.InTickList)) == (NodeFlags.ParkedWait | NodeFlags.InTickList))
        {
            file = SiteFile;
            line = SiteLine;
            return;
        }

        var a = Awaiting;
        if (a == null || a.Token != AwaitingToken || a.IsTerminated || a.IsStateMachine)
        {
            file = null;
            line = 0;
            return;
        }

        file = a.SiteFile;
        line = a.SiteLine;
    }
}

/// <summary>
/// The scope of one call of the async FlowTask method whose state machine is <typeparamref name="TStateMachine"/>: the
/// builder's Start copies the state machine into it. Pooled per state machine type.
/// </summary>
internal sealed class FlowMethodScope<TStateMachine, T> : StateMachineScope<T>
    where TStateMachine : IAsyncStateMachine
{
    static NodePool<FlowMethodScope<TStateMachine, T>> s_pool;

    internal TStateMachine StateMachine;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static FlowMethodScope<TStateMachine, T> Rent()
    {
        var scope = s_pool.Rent() ?? new FlowMethodScope<TStateMachine, T>();
        scope.InitUnstarted();
        return scope;
    }

    protected override void MoveNextCore() => StateMachine.MoveNext();

    internal override string Name => StateMachineNames<TStateMachine>.Name;

    internal override Type SourceType => StateMachineNames<TStateMachine>.DeclaringType;

    internal override string SourceMethod => StateMachineNames<TStateMachine>.MethodName;

    protected override void ReturnToPool()
    {
        StateMachine = default;
        s_pool.Return(this);
    }
}
