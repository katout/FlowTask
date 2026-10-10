namespace Katout.FlowTask.Internal;

/// <summary>The await protocol that FlowTask.Awaiter and FlowTask{T}.Awaiter share.</summary>
internal static class AwaitCore
{
    /// <summary>
    /// Synchronous completions one scope may have in one Tick, Flush or Run. Past it the scope is taken for an endless
    /// loop: it ends at that await with a FlowMisuseException instead of freezing the game.
    /// </summary>
    internal const int MaxSyncCompletionsPerTick = 1_000_000;

    /// <summary>
    /// While World.Dispose ends the flows, how many awaits of a canceled scope's catch and finally blocks throw
    /// FlowCanceledException again (so that nested finally blocks each run up to their first await); the next one ends
    /// the scope there. Rethrows is a byte.
    /// </summary>
    const int MaxRethrowsAtDispose = 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsCompleted(AwaitMode mode) => mode is AwaitMode.Value or AwaitMode.Completed;

    /// <summary>
    /// Starts <paramref name="node"/> as a child of the current scope. A canceled scope does not start it and the await
    /// throws FlowCanceledException, except in its catch and finally blocks once that exception has reached them, and for
    /// a Flow.NonCancelable task: those awaits run as a live scope's do (not while World.Dispose ends the flows).
    /// </summary>
    internal static AwaitMode Begin(FlowNode node, uint token, out FlowNode scope, bool allowPark)
    {
        var world = FlowWorld.t_current;
        if (world == null) throw Errors.AwaitOutsideFlow();
        scope = world.CurrentScope;
        if (scope == null) throw Errors.AwaitOutsideFlow();
        if (node.Token != token || node.State != NodeState.Unstarted) throw Errors.AlreadyStarted(node, token);
        if (scope.IsCancelConfirmed) return BeginInCanceledScope(node, scope, world, allowPark);
        return BeginLive(node, scope, world, allowPark);
    }

    /// <summary>
    /// An await in a canceled scope. In its cleanup, it runs as a live scope's does. A Flow.NonCancelable task starts too:
    /// canceled, the scope takes no resume from it (TakesResults), so it resumes once its children have ended, as it does
    /// to unwind, and its GetResult takes the result (<see cref="UnwindAtAwait"/>). Otherwise the await throws
    /// FlowCanceledException (<see cref="AwaitInCanceledScope"/>). While World.Dispose ends the flows, every await does.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static AwaitMode BeginInCanceledScope(FlowNode node, FlowNode scope, FlowWorld world, bool allowPark)
    {
        if (scope.RunsCleanup)
        {
            world.CleanupWatch.Add(scope);
            return BeginLive(node, scope, world, allowPark);
        }

        if ((node.Flags & NodeFlags.NonCancelable) == 0) return AwaitInCanceledScope(node, scope);
        if (world.IsDisposing)
        {
            WarnCutAtDispose(scope);
            return AwaitInCanceledScope(node, scope);
        }

        world.CleanupWatch.Add(scope);
        var mode = BeginLive(node, scope, world, allowPark);
        if (mode == AwaitMode.Pending) scope.Flags |= NodeFlags.WaitsForChildren;
        return mode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static AwaitMode BeginLive(FlowNode node, FlowNode scope, FlowWorld world, bool allowPark)
    {
        // A frame or time wait on the scope's own clock needs no node: the state machine waits in the tick list itself.
        // Whether the continuation is this state machine's is known only in OnCompleted, so it parks there.
        if (allowPark && node.IsParkableWait && scope.IsStateMachine)
        {
            scope.PendingAwait = AwaitKind.Library;
            return AwaitMode.Parkable;
        }

        // The mark is written after Start: Start throws for a misuse the flow may catch, and a mark left behind would be
        // read by the scope's next suspension.
        node.Start(scope);
        if (!node.IsTerminated)
        {
            scope.PendingAwait = AwaitKind.Library;
            return AwaitMode.Pending;
        }

        if (scope.SyncTickId != world.TickId)
        {
            scope.SyncTickId = world.TickId;
            scope.SyncCount = 0;
        }

        if (++scope.SyncCount > MaxSyncCompletionsPerTick) return EndlessLoop(node, scope);
        scope.PendingAwait = AwaitKind.None;
        return AwaitMode.Completed;
    }

    /// <summary>
    /// An await in a canceled scope that does not run its cleanup: the task is released unstarted, and the await throws
    /// FlowCanceledException once its continuation registers (only then is it known to be the scope's own), which begins
    /// the unwinding. While World.Dispose ends the flows, the awaits of the catch and finally blocks throw it again, up to
    /// a limit; the await past it ends the scope there.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static AwaitMode AwaitInCanceledScope(FlowNode node, FlowNode scope)
    {
        node.ReleaseUnstarted();
        scope.PendingAwait = scope.Rethrows >= MaxRethrowsAtDispose ? AwaitKind.Discard : AwaitKind.Library;
        return AwaitMode.CancelOnRegister;
    }

    /// <summary>
    /// The scope completed more awaits synchronously in one Tick, Flush or Run than a flow can mean to: it ends at this
    /// await, whose continuation is dropped, with a FlowMisuseException for its consumer.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static AwaitMode EndlessLoop(FlowNode node, FlowNode scope)
    {
        var world = scope.World;
        if (node.Fault is { } fault) world.Reporter.ReportUndelivered(fault.Info);
        node.ReleaseOrOrphan();
        var path = scope.BuildScopePath();
        var ex = new FlowMisuseException(
            $"Scope '{path}' completed {MaxSyncCompletionsPerTick} awaits synchronously in one Tick, Flush or Run, as an endless loop does, and was ended at this await. A loop that must wait awaits FlowTask.NextFrame().");
        // A canceled scope keeps its cause and what it carries: the loop in its cleanup is only reported.
        if (scope.IsLive) Unwinder.CarryFault(scope, FailureRecord.ForFault(ex, path), null);
        else world.Reporter.ReportCleanupException(scope, ex);
        scope.PendingAwait = AwaitKind.Discard;
        return AwaitMode.CancelOnRegister;
    }

    internal static void OnCompleted(AwaitMode mode, FlowNode node, uint token, FlowNode scope, Action continuation)
    {
        if (continuation == null) throw new ArgumentNullException(nameof(continuation));
        if (scope == null || !ReferenceEquals(scope.RunActionObject, continuation))
        {
            ForeignRegistration(mode, node, token, scope);
            return;
        }

        switch (mode)
        {
            case AwaitMode.Pending:
                if (node.Token != token) throw Errors.Consumed();
                node.SetAwaiter(scope);
                break;
            case AwaitMode.Parkable:
                if (node.Token != token) throw Errors.Consumed();
                // Same condition, clock and tick-list order as the node would have; the node goes back to the pool.
                scope.Park(node);
                node.ReleaseUnstarted();
                break;
            case AwaitMode.CancelOnRegister:
                // Its children end first, as through a call stack: the scope waits, and the last child's end resumes it
                // (FlowNode.OnChildrenEnded). Otherwise it resumes at once, inside this call, and its GetResult throws
                // FlowCanceledException as a synchronous throw would.
                if (scope.FirstChild != null)
                {
                    scope.Flags |= NodeFlags.WaitsForChildren;
                    break;
                }

                continuation();
                break;
            default:
                throw new FlowMisuseException("This awaiter has already completed (IsCompleted is true): call GetResult instead of registering a continuation.");
        }
    }

    /// <summary>
    /// FlowCanceledException reaches the scope's code: the first time begins the unwinding. Again, it cuts an await of the
    /// scope's catch or finally blocks, which happens only while World.Dispose ends the flows.
    /// </summary>
    static void MarkUnwinding(FlowNode scope)
    {
        if ((scope.Flags & NodeFlags.UnwindBegun) == 0)
        {
            scope.Flags |= NodeFlags.UnwindBegun;
            return;
        }

        scope.Rethrows++;
        WarnCutAtDispose(scope);
    }

    /// <summary>World.Dispose cut an await of a canceled scope's cleanup, or a Flow.NonCancelable await.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WarnCutAtDispose(FlowNode scope) =>
        scope.World.Reporter.WarnOnce(
            FlowWarningKind.CleanupCutAtDispose,
            "World.Dispose runs no Tick, so it ended an await of a catch or finally block, or a Flow.NonCancelable await: the rest of that block did not run. " +
            "To let cleanup finish, cancel the flows and Tick the World until they end (with a limit) before Dispose; see https://katout.github.io/FlowTask/en/guide/failures/.",
            scope);

    /// <summary>
    /// A continuation that is not the awaiting scope's own registers: a method that is not a FlowTask method (async Task,
    /// ValueTask, UniTask; FLOW005), or code driving the awaiter by hand, awaits a FlowTask that did not complete at once.
    /// Only a FlowTask method can wait in a flow, so this is a misuse: the continuation is never called (the method never
    /// resumes), and the scope that runs the code is canceled carrying a FlowMisuseException for its consumer. Not thrown:
    /// a compiler-generated builder rethrows an exception of OnCompleted on the thread pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ForeignRegistration(AwaitMode mode, FlowNode node, uint token, FlowNode scope)
    {
        if (IsCompleted(mode))
            throw new FlowMisuseException("This awaiter has already completed (IsCompleted is true): call GetResult instead of registering a continuation.");
        var world = scope.World;
        try
        {
            scope.PendingAwait = AwaitKind.None;
            if (mode != AwaitMode.CancelOnRegister && node.Token == token)
            {
                if (node.Fault is { } fault) world.Reporter.ReportUndelivered(fault.Info);
                node.ReleaseOrOrphan();
            }

            var path = scope.BuildScopePath();
            var misuse = Errors.AwaitInANonFlowTaskMethod(path);
            if (!scope.IsLive)
            {
                world.Reporter.ReportCleanupException(scope, misuse);
                return;
            }

            Unwinder.CarryFault(scope, FailureRecord.ForFault(misuse, path), null);
            world.Unwinder.UnwindOrDefer(scope);
        }
#pragma warning disable CA1031 // OnCompleted must not throw for a compiler-generated builder (see the summary): reported instead
        catch (Exception ex)
#pragma warning restore CA1031
        {
            world.Reporter.Report(new FlowExceptionInfo(ex, "<FlowTask await in a Task method>", FlowExceptionKind.Unhandled));
        }
    }

    /// <summary>
    /// GetResult: true when the await must throw FlowCanceledException, the awaited node then being released. A canceled
    /// scope that runs its cleanup takes the result as a live one does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PrepareUnwind(AwaitMode mode, FlowNode node, uint token, FlowNode scope)
    {
        if (mode == AwaitMode.CancelOnRegister)
        {
            MarkUnwinding(scope);
            return true;
        }

        if (node.Token != token) throw Errors.Consumed();
        if (!scope.IsCancelConfirmed || scope.RunsCleanup) return false;
        return UnwindAtAwait(mode, node, scope);
    }

    /// <summary>
    /// <see cref="PrepareUnwind"/> in a canceled scope: an exception the node ended with is not thrown into the scope but
    /// reported as Undelivered. A Flow.NonCancelable task that ended is taken as in a live scope: its result, or its
    /// exception thrown at the await. Out of line: every await inlines PrepareUnwind.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool UnwindAtAwait(AwaitMode mode, FlowNode node, FlowNode scope)
    {
        if ((node.Flags & NodeFlags.NonCancelable) != 0)
        {
            if (!scope.World.IsDisposing && node.State != NodeState.Canceled)
            {
                // Thrown at the await now, so not reported when its turn in the queue comes.
                node.Flags &= ~NodeFlags.FaultQueued;
                return false;
            }

            if (scope.World.IsDisposing) WarnCutAtDispose(scope);
        }

        if (node.Fault is { } fault)
        {
            // Reported here, so not again when the node is released before its failure's turn in the queue.
            node.Flags &= ~NodeFlags.FaultQueued;
            scope.World.Reporter.ReportUndelivered(fault.Info);
        }

        // A pending await knows its awaiter. One that completed at once is taken as the scope's own while the scope runs:
        // cleanup code, which runs while the scope does not, never unwinds it.
        var own = mode is AwaitMode.Pending or AwaitMode.Parkable
            ? ReferenceEquals(node.Awaiter, scope) && node.AwaiterToken == scope.Token
            : scope.RunDepth > 0;
        node.ReleaseOrOrphan();
        if (own) MarkUnwinding(scope);
        return true;
    }

    /// <summary>
    /// GetResult of a parkable await. The node was released when the scope parked, so a token that still matches means
    /// that nothing parked: the awaiter is driven by hand, and the await goes on as a node's.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ParkEnd EndPark(FlowNode node, uint token, FlowNode scope)
    {
        if (node.Token == token) return ParkEnd.NotParked;
        if ((scope.Flags & NodeFlags.ParkedWait) == 0) return EndFailedPark(scope);
        scope.Flags &= ~NodeFlags.ParkedWait;
        if (!scope.IsCancelConfirmed || scope.RunsCleanup) return ParkEnd.Resumed;
        MarkUnwinding(scope);
        return ParkEnd.Unwind;
    }

    /// <summary>The parked wait failed (its clock was removed): its fault is thrown as the wait node's would be.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static ParkEnd EndFailedPark(FlowNode scope)
    {
        var fault = scope.Rethrown;
        if (fault == null || !fault.Pending) throw Errors.Consumed();
        fault.Pending = false;
        if (scope.IsCancelConfirmed && !scope.RunsCleanup)
        {
            scope.Rethrown = null;
            scope.World.Reporter.ReportUndelivered(fault.Info);
            MarkUnwinding(scope);
            return ParkEnd.Unwind;
        }

        fault.Dispatch.Throw();
        return ParkEnd.Resumed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Consume<T>(FlowNode<T> node, FlowNode scope)
    {
        if (ReferenceEquals(scope.Awaiting, node)) scope.Awaiting = null;
        if (node.State == NodeState.Succeeded) return node.ConsumeResult();
        return ConsumeEnded(node, scope);
    }

    /// <summary>
    /// A task that did not succeed: its exception is thrown at the await, with the stack trace of its first throw, and
    /// the scope records it (only for its own await: cleanup code that awaits runs while the scope does not).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static T ConsumeEnded<T>(FlowNode<T> node, FlowNode scope)
    {
        var fault = node.Fault ?? throw new FlowMisuseException($"Awaited task '{node.DisplayName}' ended as {node.State} while its awaiter is alive.");
        if (scope.RunDepth > 0) scope.Rethrown = fault;
        node.Release();
        fault.Dispatch.Throw();
        return default;
    }
}

/// <summary>
/// Whether the builders treat an awaiter type as the library's: only FlowTask.Awaiter and FlowTask{T}.Awaiter register
/// with the scope and can be unwound; every library awaitable hands out one of them. Computed once per awaiter type.
/// </summary>
internal static class LibraryAwaiter<TAwaiter>
{
    internal static readonly bool Is = Classify(typeof(TAwaiter));

    static bool Classify(Type t) =>
        t == typeof(FlowTask.Awaiter) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(FlowTask<>.Awaiter));
}
