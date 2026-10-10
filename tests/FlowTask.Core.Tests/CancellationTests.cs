using System.Runtime.CompilerServices;
using Katout.FlowTask.Testing;

namespace Katout.FlowTask.Tests;

/// <summary>Cancellation and unwinding.</summary>
public class CancellationTests : FlowTestBase
{
    readonly List<Exception> _observed = new();

    [SetUp]
    public void ResetObserved() => _observed.Clear(); // the fixture instance is shared by its tests

    bool Observe(Exception e)
    {
        _observed.Add(e);
        return false; // exception filter: observe without catching
    }

    async FlowTask Waiting(string name)
    {
        try
        {
            await FlowTask.WaitForSeconds(100);
        }
#pragma warning disable FLOW001 // on purpose: a filter that observes FlowCanceledException without catching it (it returns false)
        catch (Exception e) when (Observe(e))
#pragma warning restore FLOW001
        {
        }
        finally
        {
            Log.Add(name + " finally");
        }
    }

    [Test]
    public void EachUnwindThrowsItsOwnFlowCanceledException()
    {
        // One shared instance let a later unwind rewrite the StackTrace of an exception observed earlier, and
        // shared its Data between scopes.
        var a = World.Run(Waiting("a"));
        var b = World.Run(Waiting("b"));
        a.Cancel();
        Tick();
        var first = _observed.Single();
        Assert.That(first, Is.InstanceOf<FlowCanceledException>());
        first.Data["scope"] = "a";
        var trace = first.StackTrace;

        b.Cancel();
        Tick();
        Assert.That(_observed, Has.Count.EqualTo(2));
        Assert.That(_observed[1], Is.InstanceOf<FlowCanceledException>());
        Assert.That(_observed[1], Is.Not.SameAs(first));
        Assert.That(_observed[1].Data.Contains("scope"), Is.False);
        Assert.That(first.StackTrace, Is.EqualTo(trace));
        AssertLog("a finally", "b finally");
    }

    [Test]
    public void FlowCanceledExceptionIsAnOperationCanceledException()
    {
        // The .NET conventions for cancellation apply to the unwind as they do to a canceled Task.
        CaptureExceptions();

        async FlowTask CleanupThenRethrow()
        {
            try
            {
                try
                {
                    await FlowTask.WaitForSeconds(100);
                }
                catch (OperationCanceledException)
                {
                    Log.Add("cleanup");
                    throw;
                }
            }
            finally
            {
                Log.Add("finally");
            }
        }

        async FlowTask ErrorHandlerThatLetsCancellationPass()
        {
            try
            {
                await FlowTask.WaitForSeconds(100);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Add("error handled");
            }
        }

        var a = World.Run(CleanupThenRethrow());
        var b = World.Run(ErrorHandlerThatLetsCancellationPass());
        var c = World.Run(Waiting("c"));
        Tick();
        a.Cancel();
        b.Cancel();
        c.Cancel();
        Tick();

        AssertLog("cleanup", "finally", "c finally");
        Assert.That(a.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(b.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(c.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(Exceptions, Is.Empty);
        var observed = (OperationCanceledException)_observed.Single();
        Assert.That(observed, Is.InstanceOf<FlowCanceledException>());
        Assert.That(observed.CancellationToken.CanBeCanceled, Is.False);
    }

    // ------------------------------------------------------------------ FlowWorld.Dispose ends every flow

    [Test]
    public void WorldDisposeDropsAScopeWhoseCleanupKeepsAwaiting()
    {
        // Dispose runs no Tick: an await in a canceled scope's catch or finally throws FlowCanceledException again. A
        // finally that catches it and awaits again without end is dropped at its 33rd await: the scope ends Canceled
        // there, its AddCleanup runs, and nothing is reported (a warning says that Dispose cut a cleanup).
        CaptureExceptions();
        var cleanups = 0;
        var rethrows = 0;

        async FlowTask Stuck()
        {
            Flow.AddCleanup(() => cleanups++);
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                while (true)
                {
                    try
                    {
                        await FlowTask.NextFrame();
                    }
#pragma warning disable FLOW001 // on purpose: the cleanup that never lets the cancellation pass, under test
                    catch (FlowCanceledException)
#pragma warning restore FLOW001
                    {
                        rethrows++;
                    }
                }
            }
        }

        var h = World.Run(Stuck());
        var task = h.AsTask();
        Tick();
        World.Dispose();

        Assert.That(rethrows, Is.EqualTo(32));
        Assert.That(cleanups, Is.EqualTo(1));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.WorldDisposed));
        Assert.That(h.Exception, Is.Null);
        Assert.That(task.IsCanceled, Is.True, task.Status.ToString());
        Assert.That(Exceptions, Is.Empty);
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void ARunFromCleanupCodeWhileWorldDisposeEndsTheFlowsStartsNothing()
    {
        // A finally block and an AddCleanup that start their flow again with FlowWorld.Run (a supervisor restarting it).
        // While Dispose ends the flows, Run does not start the task: it returns a handle that has ended as Canceled. The
        // flow ran once, and nothing is reported.
        CaptureExceptions();
        var starts = 0;
        var finallies = 0;
        var handles = new List<FlowHandle>();

        async FlowTask Loop()
        {
            starts++;
            Flow.AddCleanup(() => handles.Add(World.Run(Loop())));
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                finallies++;
                handles.Add(World.Run(Loop()));
            }
        }

        World.Run(Loop());
        Tick();
        World.Dispose();

        Assert.That(starts, Is.EqualTo(1));
        Assert.That(finallies, Is.EqualTo(1));
        Assert.That(handles.Select(h => h.Status), Is.EqualTo(new[] { FlowStatus.Canceled, FlowStatus.Canceled }));
        Assert.That(Exceptions, Is.Empty);
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void WorldDisposeEndsAFlowWhoseExceptionIsStillOnItsWay()
    {
        // A spawned child throws: its owner is canceled carrying the exception, and the owner's finally awaits without
        // end, so the exception has not reached the root when Dispose comes (a cleanup has no time limit). Dispose ends
        // the flow (the finally's await throws FlowCanceledException again), and the exception reaches the root with it.
        CaptureExceptions();

        async FlowTask Failing()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("boom");
        }

        async FlowTask Stuck()
        {
            try
            {
                Flow.Spawn(Failing());
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.Never();
            }
        }

        var h = World.Run(Stuck());
        var task = h.AsTask();
        Tick(2);
        Assert.That(Exceptions, Is.Empty, "the exception reaches the root when its flow ends");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Fault));
        World.Dispose();

        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
        Assert.That(p.Exception.Message, Is.EqualTo("boom"));
        Assert.That(p.ScopePath, Is.EqualTo("Stuck > Failing"));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(h.Exception, Is.SameAs(p.Exception));
        Assert.That(task.IsFaulted, Is.True);
        Assert.That(task.Exception.InnerException, Is.SameAs(p.Exception));
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void WorldDisposeEndsEveryHandleAndAsTask()
    {
        // Every kind of root flow ends Canceled and its AsTask is canceled; nothing is reported. Dispose runs no Tick, so
        // the await in Screen's finally throws FlowCanceledException again: the block runs up to that await.
        CaptureExceptions();

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("finally");
                await FlowTask.WaitForSeconds(0.5);
                Log.Add("unreachable");
            }
        }

        var handles = new[]
        {
            World.Run(FlowTask.WaitForSeconds(100)),
            World.Run(FlowTask.Race(FlowTask.Never(), FlowTask.WaitForSeconds(100)).WithoutResult()),
            World.Run(Screen()),
        };
        var tasks = handles.Select(h => h.AsTask()).ToArray();
        Tick();
        World.Dispose();

        AssertLog("finally");
        foreach (var h in handles)
        {
            Assert.That(h.IsCompleted, Is.True);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(h.CancelCause, Is.EqualTo(CancelCause.WorldDisposed));
        }

        Assert.That(tasks.All(t => t.IsCanceled), Is.True, string.Join(", ", tasks.Select(t => t.Status)));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void ConfirmedWaitUntilDoesNotEvaluateItsConditionAgain()
    {
        // A Cancel outside the flush is confirmed at once and unwound at the head of the next flush, after step 3
        // evaluates the tick waits. A confirmed WaitUntil is skipped there: its condition (user code) is not called
        // again, although it would now be true, and the flow does not resume normally.
        var calls = 0;
        var open = false;

        async FlowTask Root()
        {
            await FlowTask.WaitUntil(() =>
            {
                calls++;
                return open;
            });
            Log.Add("resumed normally");
        }

        var h = World.Run(Root());
        Tick();
        var before = calls;
        open = true;
        h.Cancel();
        Tick();
        Assert.That(calls, Is.EqualTo(before), "condition evaluated after the Cancel");
        AssertLog();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    // withClock: the wait gets its own node; without: the scope waits in the tick list itself. Both must behave alike.
    [TestCase(false)]
    [TestCase(true)]
    public void AFrameWaitSatisfiedAndCanceledInTheSameFlushUnwinds(bool withClock)
    {
        FlowHandle a = default;

        async FlowTask Canceller()
        {
            await FlowTask.NextFrame();
            a.Cancel();
            Log.Add("a canceled");
        }

        async FlowTask Victim()
        {
            try
            {
                await FlowTask.NextFrame(withClock ? World.DefaultClock : null);
                Log.Add("a resumed");
            }
            finally
            {
                Log.Add("a finally");
            }
        }

        World.Run(Canceller());
        a = World.Run(Victim());
        Tick(); // both waits are satisfied in step 3; the canceller's resume runs first and cancels the victim
        AssertLog("a finally", "a canceled");
        Assert.That(a.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AFrameWaitSatisfiedThenPausedThenCanceledUnwinds(bool withClock)
    {
        var game = World.CreateClock("Game");
        var ui = World.CreateClock("UI", World.UnscaledClock);
        var pause = default(ScopedHandle);

        async FlowTask Pauser()
        {
            await FlowTask.NextFrame();
            pause = game.Pause();
            Log.Add("game paused");
            await FlowTask.Never();
        }

        async FlowTask Victim()
        {
            try
            {
                await FlowTask.NextFrame(withClock ? game : null);
                Log.Add("a resumed");
            }
            finally
            {
                Log.Add("a finally");
            }
        }

        World.Run(Pauser(), ui);
        var a = World.Run(Victim(), game);
        Tick(); // both satisfied; the pause comes first, so the victim's resume is held back
        AssertLog("game paused");
        a.Cancel();
        Tick();
        AssertLog("game paused", "a finally");
        pause.Dispose();
        Tick();
        AssertLog("game paused", "a finally");
        Assert.That(a.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void SwallowThenReturnIsDetected()
    {
        CaptureExceptions();

        async FlowTask<int> Swallower()
        {
            try
            {
                await FlowTask.WaitForSeconds(100);
            }
#pragma warning disable FLOW001 // on purpose: the swallow under test
            catch (Exception)
#pragma warning restore FLOW001
            {
                return -1;
            }

            return 1;
        }

        var h = World.Run(Swallower());
        h.Cancel();
        Tick();
        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.SwallowedCancellation));
        Assert.That(p.Exception, Is.TypeOf<FlowMisuseException>());
        Assert.That(p.ScopePath, Is.EqualTo("Swallower"));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.Exception, Is.Null);
    }

    // Canceled while running (the unwind has not started, there was no await to throw at) and then returning without
    // another await is not a swallow. The scope simply ends Canceled and nothing stays alive.
    [Test]
    public void CanceledWhileRunningThenReturningWithoutAwaitEndsCanceled()
    {
        CaptureExceptions();
        FlowHandle h = default;

        async FlowTask Self()
        {
            Log.Add("start");
            await FlowTask.NextFrame();
            h.Cancel();
            Log.Add("after cancel");
        }

        h = World.Run(Self());
        Tick(3);
        AssertLog("start", "after cancel");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(Exceptions, Is.Empty);
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void ASwallowedCancellationRunsOnAndIsReportedWhenTheScopeReturns()
    {
        // The winner of a Race cancels the loser, which swallows the cancellation and goes on: its awaits after the
        // catch run as a live scope's, and the Race waits for it. The swallow is reported once, when the loser returns,
        // and the loser ends Canceled; the winner stands.
        CaptureExceptions();

        async FlowTask<int> Swallower()
        {
            try
            {
                await FlowTask.Never();
            }
#pragma warning disable FLOW001 // on purpose: the swallow under test
            catch
#pragma warning restore FLOW001
            {
                Log.Add("swallowed");
            }

            await FlowTask.NextFrame();
            Log.Add("ran on");
            return 1;
        }

        async FlowTask<int> Wins()
        {
            await FlowTask.NextFrame();
            return 2;
        }

        async FlowTask Root()
        {
            var r = await FlowTask.Race(Swallower(), Wins());
            Log.Add("winner " + r);
        }

        var h = World.Run(Root());
        Tick(3);
        AssertLog("swallowed", "ran on", "winner Race[1] = 2");
        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.SwallowedCancellation));
        Assert.That(p.ScopePath, Is.EqualTo("Root > Swallower"));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void AwaitInACanceledScopeUnwindsImmediately()
    {
        var self = new Once<FlowHandle>();

        async FlowTask Root()
        {
            var h = await self;
            try
            {
                h.Cancel(); // cancel ourselves while running
                Log.Add("still running");
                await FlowTask.WaitForSeconds(0.01);
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("finally");
            }
        }

        var handle = World.Run(Root());
        self.Set(handle);
        Tick();
        AssertLog("still running", "finally");
        Assert.That(handle.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    // ------------------------------------------------------------------ cleanup that awaits

    [Test]
    public void AnAwaitInTheFinallyOfACanceledScopeRunsToItsEnd()
    {
        // Once FlowCanceledException has reached a scope's code, the awaits of its catch and finally blocks run as those
        // of a live scope. Meanwhile the scope stays Running, and its CancelCause tells that it is closing.
        async FlowTask FadeOut()
        {
            Log.Add("fade start");
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("fade end");
        }

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FadeOut();
                Log.Add("after fade");
            }
        }

        var h = World.Run(Screen());
        h.Cancel();
        Tick();
        AssertLog("fade start");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
        Assert.That(h.ToString(), Is.EqualTo("FlowHandle(Screen, Running: Explicit)"));
        TickFor(0.2);
        AssertLog("fade start", "fade end", "after fade");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void ACleanupTaskRunsUnderItsCanceledScopeAndAnAncestorsCancelDoesNotReachIt()
    {
        // The task a finally awaits is a child of the canceled scope, which its parent keeps until the task has ended. It
        // is not canceled, and a later cancellation of an ancestor does not reach it: it runs to its end, then the
        // ancestor unwinds.
        string cleanupPath = null;

        async FlowTask Cleanup()
        {
            cleanupPath = Flow.CurrentScopePath;
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("cleanup end");
        }

        async FlowTask Doomed()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await Cleanup();
            }
        }

        async FlowTask Host()
        {
            try
            {
                var doomed = Flow.Spawn(Doomed());
                await FlowTask.NextFrame();
                doomed.Cancel();
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("host finally");
            }
        }

        var host = World.Run(Host());
        Tick();
        Assert.That(cleanupPath, Is.EqualTo("Host > Doomed > Cleanup"));
        host.Cancel();
        Tick();
        AssertLog();
        Assert.That(host.Status, Is.EqualTo(FlowStatus.Running), "Host waits for Doomed's cleanup");
        var cleanup = World.Diagnostics.Walk().Single(s => s.Name == "Cleanup");
        Assert.That(cleanup.IsCanceling, Is.False);
        Assert.That(cleanup.Cause, Is.EqualTo(CancelCause.None));

        TickFor(0.2);
        AssertLog("cleanup end", "host finally");
        Assert.That(host.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(host.CancelCause, Is.EqualTo(CancelCause.Explicit));
    }

    [Test]
    public void TheUnwindIgnoresPauseButTheAwaitsOfTheCleanupFollowIt()
    {
        // FlowCanceledException reaches a scope on a paused clock at once; an await in its finally is a resume like any
        // other, which waits for the pause to be released.
        var done = new Signal<int>();

        async FlowTask Closing()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("finally");
                await done.Next();
                Log.Add("cleanup resumed");
            }
        }

        var h = World.Run(Closing());
        var pause = World.DefaultClock.Pause();
        h.Cancel();
        Tick();
        AssertLog("finally");
        done.Emit(0);
        Tick();
        AssertLog("finally");
        pause.Dispose();
        World.Flush();
        AssertLog("finally", "cleanup resumed");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    // ------------------------------------------------------------------ an unbridged await is a misuse

    /// <summary>A foreign awaitable that never completes and records whether a continuation was registered with it.</summary>
    sealed class ForeignAwaitable
    {
        public bool Registered;

        public Awaiter GetAwaiter() => new(this);

        public readonly struct Awaiter : ICriticalNotifyCompletion
        {
            readonly ForeignAwaitable _owner;

            public Awaiter(ForeignAwaitable owner) => _owner = owner;

            public bool IsCompleted => false;

            public void GetResult()
            {
            }

            public void OnCompleted(Action continuation) => _owner.Registered = true;

            public void UnsafeOnCompleted(Action continuation) => _owner.Registered = true;
        }
    }

    [Test]
    public void UnbridgedAwaitFailsWhenTheScopeSuspends()
    {
        CaptureExceptions();
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            try
            {
#pragma warning disable FLOW002 // on purpose: the unbridged await under test
                var v = await tcs.Task;
#pragma warning restore FLOW002
                Log.Add("foreign done " + v);
            }
            finally
            {
                Log.Add("finally");
            }
        }

        var h = World.Run(Root());

        // At the await itself, inside World.Run: the method is abandoned, its AddCleanup runs and its finally does not.
        AssertLog("cleanup");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
        Assert.That(p.ScopePath, Is.EqualTo("Root"));
        Assert.That(p.Exception, Is.InstanceOf<FlowMisuseException>());
        Assert.That(p.Exception.Message, Does.Contain("TaskAwaiter<Int32>").And.Contain(".AsFlow()").And.Contain("FLOW002"));
        // The message names the scope path and the awaiter type.
        Assert.That(p.Exception.Message, Does.StartWith("FlowTask method 'Root' suspended on TaskAwaiter<Int32>, which is not a FlowTask"));
        // The bridge that passes the scope's cancellation comes first, as in FLOW002's message and code fix.
        Assert.That(p.Exception.Message.IndexOf("FlowBridge.FromTask", StringComparison.Ordinal),
            Is.GreaterThanOrEqualTo(0).And.LessThan(p.Exception.Message.IndexOf(".AsFlow()", StringComparison.Ordinal)));
        Assert.That(h.Exception, Is.SameAs(p.Exception));

        // The Task's completion finds nothing to resume.
        tcs.SetResult(5);
        Tick(2);
        AssertLog("cleanup");
        World.Dispose();
        var task = h.AsTask();
        Assert.That(task.IsFaulted, Is.True);
        Assert.That(task.Exception.InnerException, Is.SameAs(p.Exception));
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void UnbridgedAwaitInTheSynchronousPartOfRunThrowsFromRun()
    {
        // Without OnUnhandledException, the exception surfaces from the World.Run that started the flow: next to the cause.
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
#pragma warning disable FLOW002 // on purpose: the unbridged await under test
            await tcs.Task;
#pragma warning restore FLOW002
        }

        var ex = Assert.Throws<FlowUnhandledException>(() => World.Run(Root()));
        Assert.That(ex.ExceptionInfos.Single().Exception, Is.InstanceOf<FlowMisuseException>());
    }

    [TestCase("completed at once")]
    [TestCase("peeked child")]
    [TestCase("peeked wait")]
    [TestCase("peeked once")]
    [TestCase("failed to start")]
    public void AnUnbridgedAwaitIsFoundWhateverLibraryAwaitCameBefore(string before)
    {
        // The builders tell a foreign awaiter by its type, so no library await before it lets it through: one that
        // completed at once, an awaiter only peeked (GetAwaiter without await), or a wait that threw as it started. The
        // continuation is never registered with the foreign awaitable, which could resume the method outside any Tick.
        CaptureExceptions();
        var foreign = new ForeignAwaitable();
        var once = new Once<int>();

        async FlowTask Child() => await FlowTask.NextFrame();

#pragma warning disable CS1998 // completes at once, on purpose: the clock is removed as the method returns
        async FlowTask<Clock> ScopeClock() => Flow.CreateClock("removed when its scope ends");
#pragma warning restore CS1998

        async FlowTask Root()
        {
#pragma warning disable FLOW007 // on purpose: awaiters used by hand, which is what this test checks
            switch (before)
            {
                case "completed at once":
                    await FlowTask.DelayFrames(0);
                    break;
                case "peeked child":
                    _ = Child().GetAwaiter().IsCompleted;
                    break;
                case "peeked wait":
                    _ = FlowTask.WaitForSeconds(1).GetAwaiter().IsCompleted;
                    break;
                case "peeked once":
                    _ = once.GetAwaiter().IsCompleted;
                    break;
                case "failed to start":
                {
                    var removed = await ScopeClock();
                    try
                    {
                        await FlowTask.WaitForSeconds(1, removed);
                    }
                    catch (FlowMisuseException)
                    {
                        // the wait on a removed clock throws as it starts
                    }

                    break;
                }
            }
#pragma warning restore FLOW007

            Log.Add("before");
#pragma warning disable FLOW002 // on purpose: the unbridged await under test
            await foreign;
#pragma warning restore FLOW002
            Log.Add("unreachable");
        }

        var h = World.Run(Root());
        AssertLog("before");
        Assert.That(foreign.Registered, Is.False, "a continuation was registered with the foreign awaiter");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(Exceptions.Single().Exception.Message, Does.Contain("ForeignAwaitable.Awaiter"));
    }

    /// <summary>A user awaitable that hands out a foreign awaiter.</summary>
    sealed class WrappedForeignAwaitable
    {
        public readonly ForeignAwaitable Inner = new();

        public ForeignAwaitable.Awaiter GetAwaiter() => Inner.GetAwaiter();
    }

    /// <summary>A user awaitable that hands out the awaiter of a FlowTask.</summary>
    readonly struct NextFrameWrapper
    {
        public FlowTask.Awaiter GetAwaiter() => FlowTask.NextFrame().GetAwaiter();
    }

    [Test]
    public void AUserAwaitableTakesPartOnlyThroughAFlowTaskAwaiter()
    {
        // The builders decide by the awaiter type, not by the awaitable that hands it out.
        CaptureExceptions();
        var marked = new WrappedForeignAwaitable();

        async FlowTask Wrapped()
        {
            await new NextFrameWrapper();
            Log.Add("wrapper resumed");
        }

        async FlowTask Marked()
        {
#pragma warning disable FLOW002 // on purpose: the user type hands out a foreign awaiter
            await marked;
#pragma warning restore FLOW002
            Log.Add("unreachable");
        }

        var w = World.Run(Wrapped());
        var m = World.Run(Marked());
        Tick();
        AssertLog("wrapper resumed");
        Assert.That(w.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(m.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(marked.Inner.Registered, Is.False, "a continuation was registered with the foreign awaiter");
        Assert.That(Exceptions.Single().Exception.Message, Does.Contain("ForeignAwaitable.Awaiter"));
    }

    [Test]
    public void UnbridgedAwaitThatCompletesSynchronouslyDoesNotFail()
    {
        // The price of deciding at the suspension: an unbridged await that does not suspend is not detected (FLOW002 is).
        CaptureExceptions();
        var done = new TaskCompletionSource<int>();
        done.SetResult(7);

        async FlowTask<int> Root()
        {
#pragma warning disable FLOW002 // on purpose: unbridged awaits that complete synchronously
            await Task.CompletedTask;
            var v = await done.Task;
#pragma warning restore FLOW002
            await FlowTask.NextFrame();
            return v;
        }

        var h = World.Run(Root());
        Tick();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.Result, Is.EqualTo(7));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void UnbridgedAwaitWhileUnwindingIsACleanupException()
    {
        CaptureExceptions();
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Screen()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            try
            {
                await FlowTask.WaitForSeconds(100);
            }
            finally
            {
                Log.Add("finally");
#pragma warning disable FLOW002 // on purpose: the unbridged await under test
                await tcs.Task;
#pragma warning restore FLOW002
                Log.Add("unreachable");
            }
        }

        var h = World.Run(Screen());
        Tick();
        h.Cancel();
        Tick();
        AssertLog("finally", "cleanup");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(p.ScopePath, Is.EqualTo("Screen"));
        Assert.That(p.Exception, Is.InstanceOf<FlowMisuseException>());
        tcs.SetResult(1);
        Tick();
        AssertLog("finally", "cleanup");
    }

    /// <summary>A foreign awaitable whose awaiter is only INotifyCompletion: the builders' AwaitOnCompleted.</summary>
    sealed class NotifyOnlyAwaitable
    {
        public bool Registered;

        public Awaiter GetAwaiter() => new(this);

        public readonly struct Awaiter : INotifyCompletion
        {
            readonly NotifyOnlyAwaitable _owner;

            public Awaiter(NotifyOnlyAwaitable owner) => _owner = owner;

            public bool IsCompleted => false;

            public void GetResult()
            {
            }

            public void OnCompleted(Action continuation) => _owner.Registered = true;
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void UnbridgedAwaitFailsThroughEveryBuilderMethod(bool withResult, bool notifyOnly)
    {
        // FlowTask and FlowTask<T> methods; an ICriticalNotifyCompletion awaiter (AwaitUnsafeOnCompleted) and an
        // INotifyCompletion one (AwaitOnCompleted). Each decides by the awaiter type and registers nothing.
        CaptureExceptions();
        var critical = new ForeignAwaitable();
        var notify = new NotifyOnlyAwaitable();

        async FlowTask Plain()
        {
#pragma warning disable FLOW002 // on purpose: the unbridged awaits under test
            if (notifyOnly) await notify;
            else await critical;
#pragma warning restore FLOW002
            Log.Add("unreachable");
        }

        async FlowTask<int> WithResult()
        {
#pragma warning disable FLOW002 // on purpose: the unbridged awaits under test
            if (notifyOnly) await notify;
            else await critical;
#pragma warning restore FLOW002
            Log.Add("unreachable");
            return 1;
        }

        var status = withResult ? World.Run(WithResult()).Status : World.Run(Plain()).Status;
        Tick();

        Assert.That(status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(critical.Registered || notify.Registered, Is.False, "a continuation was registered with the foreign awaiter");
        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
        Assert.That(p.Exception.Message, Does.Contain(notifyOnly ? "NotifyOnlyAwaitable.Awaiter" : "ForeignAwaitable.Awaiter"));
        AssertLog();
    }

    [Test]
    public void ChildOnAnUnbridgedAwaitDoesNotOutliveItsParent()
    {
        // The child used to wait on in the tree after its parent ended, and run its code when the Task completed.
        CaptureExceptions();
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Child()
        {
            try
            {
#pragma warning disable FLOW002 // on purpose: the unbridged await under test
                await tcs.Task;
#pragma warning restore FLOW002
                Log.Add("child code");
                await FlowTask.NextFrame();
            }
            finally
            {
                Log.Add("child finally");
            }
        }

        async FlowTask Parent()
        {
            try
            {
                Flow.Spawn(Child());
                await FlowTask.NextFrame();
                Log.Add("parent end");
            }
            finally
            {
                Log.Add("parent finally");
            }
        }

        var h = World.Run(Parent());
        Tick(2);
        // The child's exception cancels its parent, which ends Faulted with it and passes it on to the root.
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Fault));
        var p = Exceptions.Single();
        Assert.That(h.Exception, Is.SameAs(p.Exception));
        Assert.That(p.ScopePath, Is.EqualTo("Parent > Child"));
        Assert.That(p.Exception, Is.InstanceOf<FlowMisuseException>());
        Assert.That(World.Diagnostics.Walk().Any(s => s.Name == "Child"), Is.False, "the child ended with its parent");

        tcs.SetResult(1);
        Tick(3);
        AssertLog("parent finally");
        FlowAssert.NoLiveScopes(World);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ATaskMethodWhoseAwaitOfAFlowTaskSuspendsFailsTheScopeThatCalledIt(bool nested)
    {
        // Only a FlowTask method waits in a flow: a Task method (FLOW005) whose await of a FlowTask does not complete at
        // once is a misuse, as an unbridged await is. The scope that called it is canceled carrying the misuse, unwinds
        // at its next await and ends with it; the Task method never resumes, and its Task never completes. Nested: a
        // scope between the root and the caller receives the exception at its await.
        CaptureExceptions();
        Task helper = null!;

        async FlowTask Waiting()
        {
            await FlowTask.NextFrame();
            Log.Add("waited");
        }

        async Task Helper()
        {
            await Waiting();
            Log.Add("unreachable");
        }

        async FlowTask Caller()
        {
            try
            {
                helper = Helper();
                Log.Add("caller goes on");
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("caller finally");
            }
        }

        async FlowTask Host() => await Caller();

        var h = World.Run(nested ? Host() : Caller());
        Tick(3);

        AssertLog("caller goes on", "caller finally");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(helper.IsCompleted, Is.False);
        var report = Exceptions.Single();
        Assert.That(report.Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
        Assert.That(report.ScopePath, Is.EqualTo(nested ? "Host > Caller" : "Caller"));
        Assert.That(report.Exception, Is.TypeOf<FlowMisuseException>().And.Message.Contains("FLOW005"));
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void RaceLoserOnAnUnbridgedAwaitCannotRunAfterTheRace()
    {
        // The loser used to run its code after the race's caller had resumed. Its unbridged await ends it with the
        // misuse, which ends the Race: the caller catches it, and the Task's completion resumes nothing.
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Loser()
        {
            try
            {
#pragma warning disable FLOW002 // on purpose: the unbridged await under test
                await tcs.Task;
#pragma warning restore FLOW002
                Log.Add("loser code");
                await FlowTask.NextFrame();
            }
            finally
            {
                Log.Add("loser finally");
            }
        }

        async FlowTask Root()
        {
            try
            {
                await FlowTask.Race(FlowTask.DelayFrames(1), Loser());
                Log.Add("race returned");
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                Log.Add("caught " + e.GetType().Name);
            }
        }

        var h = World.Run(Root());
        Tick(3);
        tcs.SetResult(1);
        Tick(3);
        AssertLog("caught FlowMisuseException");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ATaskMethodThatACanceledScopeCallsIsDroppedAndTheScopeUnwindsAsBefore([Values(false, true)] bool returns)
    {
        // A scope canceled while it runs calls a Task method that awaits a FlowTask (FLOW005). That await is not the
        // scope's own (the library tells them apart when the continuation registers): it is dropped and reported as a
        // cleanup exception, since the scope is canceled, and the scope's own marks are left as they are. The scope still
        // gets the cancellation at its own next await, runs its finally and ends Canceled; or it returns without awaiting
        // again, which swallows nothing.
        CaptureExceptions();
        FlowHandle h = default;
        Task helper = null!;

        async Task Helper()
        {
            await FlowTask.NextFrame();
            Log.Add("unreachable");
        }

        async FlowTask S()
        {
            try
            {
                await FlowTask.NextFrame();
                h.Cancel(); // canceled while running: the scope unwinds at its next await
                helper = Helper();
                if (returns) return;
                await FlowTask.NextFrame();
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("finally");
            }
        }

        h = World.Run(S());
        Tick(3);
        AssertLog("finally");
        Assert.That(helper.IsCompleted, Is.False);
        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(p.ScopePath, Is.EqualTo("S"));
        Assert.That(p.Exception, Is.TypeOf<FlowMisuseException>().And.Message.Contains("FLOW005"));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ATaskMethodThatCleanupCodeCallsIsDroppedWithACleanupException(bool canceled)
    {
        // Cleanup code (an AddCleanup action) runs while its scope ends, canceled or not. A Task method it calls whose
        // await of a FlowTask does not complete at once is dropped and reported as a cleanup exception, and the scope ends as
        // it would have.
        CaptureExceptions();
        Task helper = null!;

        async Task Helper()
        {
            await FlowTask.NextFrame();
            Log.Add("unreachable");
        }

        async FlowTask S()
        {
            Flow.AddCleanup(() => helper = Helper());
            await FlowTask.NextFrame();
            Log.Add("returns");
        }

        var h = World.Run(S());
        if (canceled) h.Cancel();
        Tick(3);
        if (canceled) AssertLog();
        else AssertLog("returns");
        Assert.That(helper.IsCompleted, Is.False);
        var p = Exceptions.Single();
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(p.ScopePath, Is.EqualTo("S"));
        Assert.That(p.Exception, Is.TypeOf<FlowMisuseException>().And.Message.Contains("FLOW005"));
        Assert.That(h.Status, Is.EqualTo(canceled ? FlowStatus.Canceled : FlowStatus.Succeeded));
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void CanceledScopesWithoutHandlersUnwindChildrenFirst()
    {
        // Scopes whose code has no handler: resuming them runs none of their code. They end canceled children first,
        // with their cleanups run and every node released; a parked wait (NextFrame) and a pending one (a scope, a leaf
        // wait) alike.
        async FlowTask Leaf()
        {
            Flow.AddCleanup(() => Log.Add("leaf cleanup"));
            await FlowTask.WaitForSeconds(100);
            Log.Add("leaf not reached");
        }

        async FlowTask Mid()
        {
            Flow.AddCleanup(() => Log.Add("mid cleanup"));
            await Leaf();
            Log.Add("mid not reached");
        }

        async FlowTask Parked()
        {
            Flow.AddCleanup(() => Log.Add("parked cleanup"));
            while (true) await FlowTask.NextFrame();
        }

        var mid = World.Run(Mid());
        var parked = World.Run(Parked());
        Tick();
        mid.Cancel();
        parked.Cancel();
        Tick();
        Assert.That(mid.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(mid.CancelCause, Is.EqualTo(CancelCause.Explicit));
        Assert.That(parked.Status, Is.EqualTo(FlowStatus.Canceled));
        AssertLog("leaf cleanup", "mid cleanup", "parked cleanup");
        FlowAssert.NoLiveScopes(World);
    }

    [TestCase("AddCleanup")]
    [TestCase("finally")]
    [TestCase("finally that awaits")]
    public void TheCallerOfARaceResumesAfterTheLoserHasEnded(string cleanup)
    {
        // A loser ends the same whether its code has a handler or not; one whose finally awaits holds the Race, and the
        // caller resumes once that cleanup has ended.
        async FlowTask Loser()
        {
            Flow.AddCleanup(() => Log.Add("loser cleanup"));
            await FlowTask.Never();
        }

        async FlowTask LoserWithFinally()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                if (cleanup == "finally that awaits") await FlowTask.NextFrame();
                Log.Add("loser cleanup");
            }
        }

        async FlowTask Main()
        {
            var r = await FlowTask.Race(cleanup == "AddCleanup" ? Loser() : LoserWithFinally(), FlowTask.NextFrame());
            Log.Add("winner " + r.Index);
        }

        var h = World.Run(Main());
        Tick(3);
        AssertLog("loser cleanup", "winner 1");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        FlowAssert.NoLiveScopes(World);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnExceptionThatACanceledScopeDidNotReceiveIsUndeliveredWithOrWithoutHandlers(bool withFinally)
    {
        // The awaited task fails, and its cleanup cancels the scope that awaits it before that scope resumes for the
        // exception: the scope ends canceled, and the exception is reported as Undelivered, with or without a finally.
        CaptureExceptions();
        var s = new Signal<int>();
        FlowHandle scope = default;

        async FlowTask Child()
        {
            Flow.AddCleanup(() => scope.Cancel());
            await s.Next();
            throw new InvalidOperationException("child failed");
        }

        async FlowTask Plain() => await Child();

        async FlowTask WithFinally()
        {
            try
            {
                await Child();
            }
            finally
            {
                Log.Add("finally");
            }
        }

        scope = World.Run(withFinally ? WithFinally() : Plain());
        Tick();
        s.Emit(1);
        Tick();
        Assert.That(scope.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(scope.CancelCause, Is.EqualTo(CancelCause.Explicit));
        Assert.That(Exceptions.Select(p => p.Kind + ": " + p.Exception.Message), Is.EqualTo(new[] { "Undelivered: child failed" }));
        Assert.That(Log.Entries, Is.EqualTo(withFinally ? new[] { "finally" } : Array.Empty<string>()));
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void ParentCancelUnwindsDescendantsFirst()
    {
        // Call-stack order: FlowCanceledException reaches a scope's code once its children have ended, so the inner
        // finally blocks run to their end, their awaits included, before the outer ones.
        async FlowTask Level(int depth)
        {
            try
            {
                if (depth < 3) await Level(depth + 1);
                else await FlowTask.Never();
            }
            finally
            {
                await FlowTask.NextFrame();
                Log.Add("level " + depth);
            }
        }

        var h = World.Run(Level(0));
        h.Cancel();
        Tick(8);
        AssertLog("level 3", "level 2", "level 1", "level 0");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }
}
