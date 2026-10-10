using System.Threading;

namespace Katout.FlowTask.Tests;

/// <summary>
/// External cancellation and how a flow's end is reported. An OperationCanceledException that is not the flow's own
/// FlowCanceledException (a Task canceled outside, a library that honours its own token) is an exception like any
/// other: thrown at the await, caught by
/// <c>catch (OperationCanceledException e) when (e is not FlowCanceledException)</c>, and an unhandled exception when
/// nothing catches it. Status: Faulted exactly when a failure passed through the flow; CancelCause tells what asked it
/// to end.
/// </summary>
public class ExternalCancellationAndStatusTests : FailurePathTestBase
{
    public enum CancelSource
    {
        /// <summary>An HttpClient-style timeout: TaskCanceledException from an async Task method.</summary>
        HttpTimeout,
        /// <summary>A library that honours its own token: a plain OperationCanceledException.</summary>
        OwnToken,
        /// <summary>An already canceled Task (<c>Task.FromCanceled</c>): TaskCanceledException.</summary>
        CanceledTask,
        /// <summary>The flow's own cancellation.</summary>
        FlowCancel,
    }

    public enum CatchForm
    {
        None,
        CatchTaskCanceled,
        CatchExternalOnly,
        CatchOperationCanceledReturn,
        CatchFlowCanceledReturn,
    }

    static async Task<int> HttpLikeTimeout(Task gate)
    {
        await gate;
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());
    }

    static async Task<int> OwnTokenCancel(Task gate)
    {
        await gate;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        cts.Token.ThrowIfCancellationRequested();
        return 1;
    }

    [Test]
    public void AnExternalCancellationIsAnExceptionAndOnlyTheFlowsOwnCancellationIsACancel([Values] CancelSource source, [Values] CatchForm form)
    {
        // TaskCanceledException, a plain OperationCanceledException and a canceled Task are thrown at the await.
        // catch (TaskCanceledException) misses the plain one; the general form
        // catch (OperationCanceledException e) when (e is not FlowCanceledException) takes them all and lets the flow's
        // own cancellation pass. Uncaught, an external cancellation ends the root flow Faulted. The flow's own
        // cancellation ends it Canceled; a catch that takes it and returns is a swallow, reported.
        // The return forms swallow the flow's own cancellation (reported); against an external cancellation they
        // are plain catches.
        CaptureExceptions();
        var gate = new TaskCompletionSource<bool>();

        async FlowTask<int> Op()
        {
            switch (source)
            {
                case CancelSource.HttpTimeout:
                    return await FlowBridge.FromTask(_ => HttpLikeTimeout(gate.Task));
                case CancelSource.OwnToken:
                    return await FlowBridge.FromTask(_ => OwnTokenCancel(gate.Task));
                case CancelSource.CanceledTask:
                    await FlowBridge.FromTask(_ => (Task)gate.Task);
                    return await FlowBridge.FromTask(_ => Task.FromCanceled<int>(new CancellationToken(true)));
                default:
                    await FlowTask.WaitForSeconds(100);
                    return 1;
            }
        }

#pragma warning disable FLOW001 // on purpose: the catch forms under test, among them those that take FlowCanceledException and return
        async FlowTask<int> Screen()
        {
            switch (form)
            {
                case CatchForm.CatchTaskCanceled:
                    try
                    {
                        return await Op();
                    }
                    catch (TaskCanceledException e)
                    {
                        Log.Add("caught " + e.GetType().Name);
                        return -1;
                    }
                case CatchForm.CatchExternalOnly:
                    try
                    {
                        return await Op();
                    }
                    catch (OperationCanceledException e) when (e is not FlowCanceledException)
                    {
                        Log.Add("caught " + e.GetType().Name);
                        return -1;
                    }
                case CatchForm.CatchOperationCanceledReturn:
                    try
                    {
                        return await Op();
                    }
                    catch (OperationCanceledException e)
                    {
                        Log.Add("caught " + e.GetType().Name);
                        return -1;
                    }
                case CatchForm.CatchFlowCanceledReturn:
                    try
                    {
                        return await Op();
                    }
                    catch (FlowCanceledException e)
                    {
                        Log.Add("caught " + e.GetType().Name);
                        return -1;
                    }
                default:
                    return await Op();
            }
        }
#pragma warning restore FLOW001

        var h = World.Run(Screen());
        Tick();
        if (source == CancelSource.FlowCancel) h.Cancel();
        else gate.SetResult(true);
        Tick(3);

        if (source == CancelSource.FlowCancel)
        {
            if (form is CatchForm.CatchOperationCanceledReturn or CatchForm.CatchFlowCanceledReturn)
            {
                AssertLog("caught FlowCanceledException");
                AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null, "Screen");
            }
            else
            {
                AssertLog();
                AssertNoExceptions();
            }

            AssertCanceled(h, CancelCause.Explicit);
            return;
        }

        var thrown = source == CancelSource.OwnToken ? nameof(OperationCanceledException) : nameof(TaskCanceledException);
        var caught = form == CatchForm.CatchExternalOnly || form == CatchForm.CatchOperationCanceledReturn ||
                     (form == CatchForm.CatchTaskCanceled && source != CancelSource.OwnToken);
        if (caught)
        {
            AssertLog("caught " + thrown);
            AssertNoExceptions();
            AssertSucceeded(h);
            Assert.That(h.Result, Is.EqualTo(-1));
        }
        else
        {
            AssertLog();
            Assert.That(Exceptions.Count, Is.EqualTo(1), DescribeExceptions());
            Assert.That(Exceptions[0].Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
            Assert.That(Exceptions[0].Exception.GetType().Name, Is.EqualTo(thrown));
            AssertFaulted(h, Exceptions[0].Exception);
        }
    }

    [Test]
    public void AFlowsExceptionThatAnotherFlowAwaitsThroughATaskIsThatFlowsFailureWithThePathOfItsBridge([Values(false, true)] bool catches)
    {
        // A spawned child fails; its owner carries the exception to OnUnhandledException with the child's path. Another flow
        // awaits the child's AsTask through a bridge: the Task faulted with the same exception, and the bridge
        // throws it at that flow's await, as any Task's exception. A Task is outside the flows, so the library does
        // not follow the exception back through it: for that flow it was thrown at its bridge, and when it does not
        // catch it, that is its own failure, reported with the bridge's path. Two receivers, two reports of the
        // same exception, as with a Task awaited in two places; the exception's stack trace still names where it
        // was thrown. Caught, it is reported once.
        CaptureExceptions();
        var go = new Signal<int>(World, "Go");
        FlowHandle a = default;

        async FlowTask A()
        {
            await go.Next();
            throw new InvalidOperationException("A bug");
        }

        async FlowTask Wrap()
        {
            a = Flow.Spawn(A());
            await FlowTask.Never();
        }

        World.Run(Wrap());
        Tick();
        var task = a.AsTask();

        async FlowTask B()
        {
            try
            {
                await task.AsFlow();
            }
            catch (InvalidOperationException e) when (catches)
            {
                Log.Add("B caught " + e.Message);
            }
        }

        var b = World.Run(B());
        Tick();
        go.Emit(1);
        Tick(5);
        if (catches)
        {
            AssertLog("B caught A bug");
            AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "A bug", "Wrap > A");
            AssertSucceeded(b);
        }
        else
        {
            AssertLog();
            Assert.That(Exceptions.Select(p => p.Kind + " " + p.ScopePath), Is.EqualTo(new[] { "Unhandled Wrap > A", "Unhandled B > Task<FlowUnit>" }), DescribeExceptions());
            Assert.That(Exceptions[1].Exception, Is.SameAs(Exceptions[0].Exception));
            AssertFaulted(b, Exceptions[0].Exception);
        }
    }

    [Test]
    public void TheChildrenOfAScopeEndedByAnExceptionAreCanceledWithCancelCauseFault([Values] bool caught)
    {
        // The host awaits a callee that throws; the exception leaves the host, which cancels the host's spawned child
        // with CancelCause.Fault whoever receives the exception (a catch, or OnUnhandledException). The root that the exception passes
        // through ends Faulted (CancelCause None: nothing asked it to end).
        CaptureExceptions();
        FlowHandle child = default;

        async FlowTask Thrower()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("x");
        }

        async FlowTask Host()
        {
            child = Flow.Spawn(FlowTask.Never());
            await Thrower();
        }

        async FlowTask Root()
        {
            if (!caught)
            {
                await Host();
                return;
            }

            try
            {
                await Host();
            }
            catch (InvalidOperationException)
            {
                Log.Add("caught");
            }
        }

        var h = World.Run(Root());
        Tick(4);
        AssertCanceled(child, CancelCause.Fault);
        if (caught)
        {
            AssertLog("caught");
            AssertNoExceptions();
            AssertSucceeded(h);
        }
        else
        {
            AssertLog();
            var report = AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "x", "Root > Host > Thrower");
            AssertFaulted(h, report.Exception);
        }
    }

    public enum CancelFrom
    {
        /// <summary>A posted action that calls <c>h.Cancel()</c> on the World's thread.</summary>
        PostedAction,
        /// <summary><c>h.Cancel()</c> called on another thread (queued in the inbox).</summary>
        OtherThread,
    }

    [Test]
    public void AnExternalTimeoutAndACancelTakenInTheSameIntakeLeaveTheCancelFirst([Values(false, true)] bool catches, [Values] CancelFrom from)
    {
        // The request's timeout and the screen's cancel are taken in the same intake, the timeout first. The cancel is
        // confirmed at once, before the flush delivers the timeout: the screen's catch does not run (no retry dialog after
        // the player left), and the timeout is Undelivered. The same whether the cancel comes from a posted action or
        // from h.Cancel() on another thread, which queues an inbox entry of its own.
        CaptureExceptions();
        var gate = new TaskCompletionSource<bool>();
        FlowHandle h = default;

        async FlowTask<int> Fetch() => await FlowBridge.FromTask(_ => HttpLikeTimeout(gate.Task));

        async FlowTask Screen()
        {
            try
            {
                try
                {
                    Log.Add("got " + await Fetch());
                }
                catch (TaskCanceledException) when (catches)
                {
                    Log.Add("retry dialog");
                    await FlowTask.NextFrame();
                    Log.Add("after dialog");
                }
            }
            finally
            {
                Log.Add("screen finally");
            }
        }

        h = World.Run(Screen());
        Tick();
        gate.SetResult(true); // the Task fails on this thread and posts its completion to the inbox
        if (from == CancelFrom.PostedAction)
        {
            World.Post(() => h.Cancel());
        }
        else
        {
            var canceler = new Thread(() => h.Cancel());
            canceler.Start();
            TestThreads.JoinOrFail(canceler);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Running), "a Cancel from another thread waits in the inbox");
        }

        Tick(3);
        AssertLog("screen finally");
        AssertSingleException<TaskCanceledException>(FlowExceptionKind.Undelivered, null);
        AssertCanceled(h, CancelCause.Explicit);
    }

    static async Task<int> Load(Task gate, Counter counter)
    {
        await gate;
        counter.Produced++;
        return 7;
    }

    sealed class Counter
    {
        public int Produced;
        public int Delivered;
        public int Discarded;
    }

    [Test]
    public void AValueTakenInTheSameIntakeAsTheCancelIsDiscarded()
    {
        // The value and the cancel are taken in the same intake, the value first: the bridge completed, but the cancel is
        // confirmed before its awaiter resumes, so the value reaches no await, and onDiscard receives it.
        CaptureExceptions();
        var gate = new TaskCompletionSource<bool>();
        var n = new Counter();
        FlowHandle h = default;

        async FlowTask Screen()
        {
            var v = await FlowBridge.FromTask(_ => Load(gate.Task, n), _ => n.Discarded++);
            n.Delivered++;
            Log.Add("got " + v);
        }

        h = World.Run(Screen());
        Tick();
        gate.SetResult(true); // the Task completes on this thread and posts its completion to the inbox
        World.Post(() => h.Cancel());
        Tick(3);
        AssertLog();
        Assert.That((n.Produced, n.Delivered, n.Discarded), Is.EqualTo((1, 0, 1)));
        AssertNoExceptions();
        AssertCanceled(h, CancelCause.Explicit);
    }
}
