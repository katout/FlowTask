using System.IO;

namespace Katout.FlowTask.Tests;

/// <summary>
/// The nursery. A live spawned child that fails while its owner (the state machine that spawned it) runs cancels the
/// owner with <see cref="CancelCause.Fault"/>; the owner's catch does not see the child's exception (its finally runs);
/// when the owner has ended, the exception is thrown at the await of the owner's caller, or reaches
/// OnUnhandledException when the owner is a root flow. An owner whose own exception leaves it before
/// FlowCanceledException reaches its code keeps it, and the child's is Undelivered; so is the exception of a child
/// whose owner is ending or canceled. The child's own handle stays Faulted with its exception.
/// </summary>
public class SpawnFailureTests : FailurePathTestBase
{
    async FlowTask EnemyAI(double seconds)
    {
        try
        {
            await FlowTask.WaitForSeconds(seconds);
            Log.Add("enemy throws");
            throw new IOException("enemy io");
        }
        finally
        {
            Log.Add("enemy finally");
        }
    }

    async FlowTask FailingChild(double seconds)
    {
        await FlowTask.WaitForSeconds(seconds);
        Log.Add("child throws");
        throw new IOException("child io");
    }

    async FlowTask CatchingCaller(Func<FlowTask> owner)
    {
        try
        {
            await owner();
            Log.Add("caller: owner ok");
        }
        catch (IOException e)
        {
            Log.Add("caller caught " + e.Message);
        }
    }

    // ------------------------------------------------------------------ the owner's caller receives it

    [Test]
    public void TheSpawningFlowsOwnCatchDoesNotSeeItsChildsException()
    {
        // The owner's own catch around its await does not receive the child's exception (the
        // owner receives FlowCanceledException there; its finally runs); the owner's caller does. To catch it, move the
        // region that spawns into a method of its own and put the catch around the call.
        CaptureExceptions();

        async FlowTask Child()
        {
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("child throws");
            throw new InvalidOperationException("child bug");
        }

        async FlowTask Owner()
        {
            Flow.Spawn(Child());
            try
            {
                await FlowTask.WaitForSeconds(1.0);
                Log.Add("owner done");
            }
            catch (InvalidOperationException)
            {
                Log.Add("owner caught its child's exception");
            }
            finally
            {
                Log.Add("owner finally");
            }

            Log.Add("owner after try");
        }

        async FlowTask Caller()
        {
            try
            {
                await Owner();
                Log.Add("caller: owner ok");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("caller caught " + e.Message);
            }

            Log.Add("caller continues");
        }

        var h = World.Run(Caller());
        TickFor(1.5);
        AssertLog("child throws", "owner finally", "caller caught child bug", "caller continues");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    [Test]
    public void ARootFlowThatSpawnedAFailingChildEndsFaultedAndReportsTheChildsExceptionOnce()
    {
        // The owner is a root flow, so the child's exception reaches OnUnhandledException, once, with the child's path. The owner was
        // canceled because of a failure (CancelCause.Fault) and the failure passed through it: it ends Faulted with the
        // child's exception. The child's handle is Faulted with the same exception.
        CaptureExceptions();
        FlowHandle child = default;

        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("bad child");
        }

        async FlowTask Parent()
        {
            try
            {
                child = Flow.Spawn(Bad());
                await FlowTask.WaitForSeconds(10);
            }
            finally
            {
                Log.Add("parent finally");
            }
        }

        var h = World.Run(Parent());
        Tick(3);
        AssertLog("parent finally");
        var report = AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "bad child", "Parent > Bad");
        AssertFaulted(child, report.Exception);
        AssertFaulted(h, report.Exception, CancelCause.Fault);
    }

    [Test]
    public void NestedSpawnsCarryTheChildsExceptionToTheCallerOfTheOutermostOwner()
    {
        // The outer flow spawns a wave, the wave spawns the failing enemy. The wave is canceled and ends carrying the
        // exception; it is itself a spawned child that failed, so the outer flow is canceled too, and the exception is
        // thrown at the outer flow's caller.
        CaptureExceptions();

        async FlowTask Wave()
        {
            try
            {
                Flow.Spawn(EnemyAI(0.1));
                await FlowTask.WaitForSeconds(2.0);
            }
            finally
            {
                Log.Add("wave finally");
            }
        }

        async FlowTask Outer()
        {
            try
            {
                Flow.Spawn(Wave());
                await FlowTask.WaitForSeconds(1.0);
            }
            finally
            {
                Log.Add("outer finally");
            }
        }

        async FlowTask Caller()
        {
            try
            {
                await Outer();
            }
            catch (IOException e)
            {
                Log.Add("caller caught " + e.Message);
            }
        }

        var h = World.Run(Caller());
        TickFor(2.5);
        AssertLog("enemy throws", "enemy finally", "wave finally", "outer finally", "caller caught enemy io");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    [Test]
    public void AnOwnerThatIsABranchOfACombinatorCarriesItsChildsExceptionToTheCombinator([Values(false, true)] bool race)
    {
        // The flow that spawned the failing child is a branch of a Race or a WhenAll. The child's failure cancels
        // its owner, whose finally runs; the owner then carries the exception to its awaiter, the combinator, which
        // fails with it: the other branch is unwound before the combinator throws (as when a branch fails), and the
        // caller's catch receives the child's exception. The child's handle keeps it.
        CaptureExceptions();
        FlowHandle child = default;

        async FlowTask Owner()
        {
            child = Flow.Spawn(FailingChild(0.2));
            try
            {
                await FlowTask.WaitForSeconds(5.0);
                Log.Add("owner done");
            }
            finally
            {
                Log.Add("owner finally");
            }
        }

        async FlowTask Other()
        {
            try
            {
                await FlowTask.WaitForSeconds(5.0);
                Log.Add("other done");
            }
            finally
            {
                Log.Add("other finally");
            }
        }

        async FlowTask Caller()
        {
            try
            {
                if (race)
                {
                    var r = await FlowTask.Race(Owner(), Other());
                    Log.Add("race index " + r.Index);
                }
                else
                {
                    await FlowTask.WhenAll(Owner(), Other());
                    Log.Add("whenall ok");
                }
            }
            catch (IOException e)
            {
                Log.Add("caller caught " + e.Message);
            }

            Log.Add("caller continues");
        }

        var h = World.Run(Caller());
        TickFor(1.0);
        AssertLog("child throws", "owner finally", "other finally", "caller caught child io", "caller continues");
        AssertNoExceptions();
        AssertSucceeded(h);
        AssertFaulted(child, "child io");
    }

    [Test]
    public void AChildThatFailsAsItIsSpawnedCancelsItsOwnerAtItsNextAwait([Values] bool ownerReturnsAtOnce)
    {
        // While the owner is the flow running: the child's first part runs inside Flow.Spawn and throws there. The
        // owner is canceled at once, goes on to its next await and receives FlowCanceledException there; its
        // caller then receives the child's exception. An owner that returns before any await swallowed nothing: it ends
        // with the child's exception all the same.
        CaptureExceptions();
        FlowHandle child = default;

        async FlowTask FailsAtOnce()
        {
            Log.Add("child throws");
            throw new IOException("at once");
        }

        async FlowTask Owner()
        {
            try
            {
                child = Flow.Spawn(FailsAtOnce());
                if (ownerReturnsAtOnce)
                {
                    Log.Add("owner returns");
                    return;
                }

                Log.Add("owner goes on to its next await");
                await FlowTask.NextFrame();
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("owner finally");
            }
        }

        var h = World.Run(CatchingCaller(Owner));
        Tick(2);
        AssertLog("child throws", ownerReturnsAtOnce ? "owner returns" : "owner goes on to its next await", "owner finally", "caller caught at once");
        AssertNoExceptions();
        AssertSucceeded(h);
        AssertFaulted(child, "at once");
    }

    [Test]
    public void ASpawnedBridgeWhoseTaskFailsCancelsItsOwnerInTheTickThatTakesTheFailure()
    {
        // A spawned leaf that fails outside every flow's code: the bridged Task's failure is taken in step 1 of
        // the Tick (the intake), which ends the bridge; its owner is canceled then and unwinds at the head of the flush of
        // the same Tick, and its caller receives the Task's exception.
        CaptureExceptions();
        var request = new TaskCompletionSource<int>();

        async FlowTask Owner()
        {
            Flow.Spawn(FlowBridge.FromTask(_ => request.Task).ToFlowTask());
            try
            {
                await FlowTask.WaitForSeconds(10);
            }
            finally
            {
                Log.Add("owner finally");
            }
        }

        var h = World.Run(CatchingCaller(Owner));
        Tick();
        request.SetException(new IOException("request failed"));
        AssertLog();
        Tick();
        AssertLog("owner finally", "caller caught request failed");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    [Test]
    public void AnOwnerOnAPausedClockIsCanceledAtOnceAndItsCallerReceivesTheExceptionWhenThePauseEnds()
    {
        // With a Pause: the child runs on the game clock, the owner and its caller on the UI clock, which is paused when
        // the child fails. The owner's cancellation is a cancellation, which a Pause does not hold: its finally runs at
        // once. The exception reaches the caller at its await, whose resume a Pause holds like any other: the caller's
        // catch runs when the pause ends.
        CaptureExceptions();
        var ui = World.CreateClock("UI", World.UnscaledClock);
        var game = World.CreateClock("Game");

        async FlowTask Owner()
        {
            Flow.Spawn(Flow.WithClock(game, FailingChild(0.1)));
            try
            {
                await FlowTask.WaitForSeconds(1.0);
            }
            finally
            {
                Log.Add("owner finally");
            }
        }

        var h = World.Run(CatchingCaller(Owner), ui);
        Tick();
        var pause = ui.Pause();
        TickFor(0.3);
        AssertLog("child throws", "owner finally");
        pause.Dispose();
        Tick();
        AssertLog("child throws", "owner finally", "caller caught child io");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    // ------------------------------------------------------------------ the child's handle, and joins

    [Test]
    public void AFailedSpawnedChildKeepsItsExceptionOnItsHandleAndItsTask()
    {
        // The owner's caller receives the child's exception, and the child's own handle
        // still tells how the child ended: Faulted with its exception, and AsTask faults with the original exception
        // (not FlowJoinException).
        CaptureExceptions();
        FlowHandle child = default;
        Task childTask = null;
        var go = new Signal<int>(World, "Go");

        async FlowTask A()
        {
            await go.Next();
            throw new InvalidOperationException("A bug");
        }

        async FlowTask Owner()
        {
            child = Flow.Spawn(A());
            childTask = child.AsTask();
            await FlowTask.Never();
        }

        var owner = World.Run(Owner());
        Tick();
        go.Emit(1);
        Tick(2);
        var report = AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "A bug", "Owner > A");
        AssertFaulted(child, report.Exception);
        AssertFaulted(owner, report.Exception, CancelCause.Fault);
        Assert.That(childTask.IsFaulted, Is.True, childTask.Status.ToString());
        Assert.That(childTask.Exception.InnerException, Is.SameAs(report.Exception));
    }

    [Test]
    public void AnotherFlowJoiningTheFailedChildGetsFlowJoinException()
    {
        // A watcher joins the spawned child. The child's failure goes to its owner's caller; the watcher's Join
        // throws FlowJoinException (catch-able), and nothing is reported.
        CaptureExceptions();
        FlowHandle child = default;

        async FlowTask Owner()
        {
            child = Flow.Spawn(EnemyAI(0.1));
            await FlowTask.WaitForSeconds(1.0);
        }

        async FlowTask Watcher()
        {
            await FlowTask.NextFrame();
            try
            {
                await child.Join();
                Log.Add("join ok");
            }
            catch (FlowJoinException)
            {
                Log.Add("join threw FlowJoinException");
            }
        }

        async FlowTask Caller()
        {
            try
            {
                await Owner();
            }
            catch (IOException e)
            {
                Log.Add("caller caught " + e.Message);
            }
        }

        var h = World.Run(Caller());
        var watcher = World.Run(Watcher());
        TickFor(1.5);
        Assert.That(Log.Entries, Is.EquivalentTo(new[] { "enemy throws", "enemy finally", "caller caught enemy io", "join threw FlowJoinException" }), "log: " + Log);
        Assert.That(Log.Entries[0], Is.EqualTo("enemy throws"));
        Assert.That(Log.Entries[1], Is.EqualTo("enemy finally"));
        AssertNoExceptions();
        AssertSucceeded(h);
        AssertSucceeded(watcher);
        Assert.That(child.Status, Is.EqualTo(FlowStatus.Faulted));
    }

    [Test]
    public void AnOwnerThatJoinsItsOwnFailedChildReportsTheFailureOnce()
    {
        // A awaits a failing child and catches; B spawns it and joins it; C awaits it in a WhenAll and catches. B is
        // the owner of the failing child: its subtree is settled first (B is canceled, so its catch of the Join sees
        // FlowCanceledException, not FlowJoinException), and the failure is reported once, from B, a root flow.
        CaptureExceptions();

        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new IOException("bad");
        }

        async FlowTask A()
        {
            try
            {
                await Bad();
            }
            catch (IOException)
            {
                Log.Add("A caught");
            }

            Log.Add("A continues");
        }

        async FlowTask B()
        {
            try
            {
                var h = Flow.Spawn(Bad());
                await h.Join();
            }
            catch (IOException)
            {
                Log.Add("B caught");
            }
            catch (FlowJoinException)
            {
                Log.Add("B join exception");
            }

            Log.Add("B continues");
        }

        async FlowTask C()
        {
            try
            {
                await FlowTask.WhenAll(Bad(), FlowTask.WaitForSeconds(1));
            }
            catch (IOException)
            {
                Log.Add("C caught");
            }

            Log.Add("C continues");
        }

        var a = World.Run(A());
        var b = World.Run(B());
        var c = World.Run(C());
        TickFor(1.5);
        AssertLog("A caught", "A continues", "C caught", "C continues");
        var report = AssertSingleException<IOException>(FlowExceptionKind.Unhandled, "bad", "B > Bad");
        AssertSucceeded(a);
        AssertFaulted(b, report.Exception, CancelCause.Fault);
        AssertSucceeded(c);
    }

    // ------------------------------------------------------------------ an owner that fails, unwinds or ends

    [Test]
    public void AnOwnersExceptionThatLeavesItBeforeTheCancellationReachesItsCodeGoesOn()
    {
        // The child fails inside Flow.Spawn, which cancels the owner; before the owner's next await, where
        // FlowCanceledException would reach its code, the owner's own exception leaves it. That exception goes to the
        // caller, and the child's is Undelivered.
        CaptureExceptions();

        async FlowTask FailsAtOnce()
        {
            Log.Add("child throws");
            throw new IOException("child io");
        }

        async FlowTask Owner()
        {
            await FlowTask.NextFrame();
            Flow.Spawn(FailsAtOnce());
            Log.Add("owner throws");
            throw new TimeoutException("owner timeout");
        }

        async FlowTask Caller()
        {
            try
            {
                await Owner();
            }
            catch (TimeoutException e)
            {
                Log.Add("caller caught " + e.Message);
            }
            catch (IOException e)
            {
                Log.Add("caller caught " + e.Message);
            }
        }

        var h = World.Run(Caller());
        Tick(2);
        AssertLog("child throws", "owner throws", "caller caught owner timeout");
        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "child io", "Caller > Owner > FailsAtOnce");
        AssertSucceeded(h);
    }

    [Test]
    public void AnExceptionTheOwnerThrowsAfterTheCancellationReachedItIsACleanupExceptionAndTheChildsGoesOn()
    {
        // The other side: once FlowCanceledException has reached the owner's code, a new exception its finally
        // throws is cleanup code failing (a cleanup exception), and the owner still carries its child's exception to its
        // caller.
        CaptureExceptions();

        async FlowTask Owner()
        {
            Flow.Spawn(FailingChild(0.1));
            try
            {
                await FlowTask.WaitForSeconds(1.0);
            }
            finally
            {
                Log.Add("owner finally throws");
                throw new InvalidOperationException("cleanup bug");
            }
        }

        var h = World.Run(CatchingCaller(Owner));
        TickFor(0.5);
        AssertLog("child throws", "owner finally throws", "caller caught child io");
        AssertSingleException<InvalidOperationException>(FlowExceptionKind.Cleanup, "cleanup bug", "CatchingCaller > Owner");
        AssertSucceeded(h);
    }

    [Test]
    public void AnOwnerThatSwallowsTheCancellationItsChildCausedStillCarriesTheChildsException()
    {
        // With a swallow: the owner catches the FlowCanceledException its child's failure caused
        // and returns. The swallow is reported on its own (SwallowedCancellation) and does not replace the child's
        // exception, which the owner still carries to its caller.
        CaptureExceptions();

#pragma warning disable FLOW001 // on purpose: the swallow under test
        async FlowTask Owner()
        {
            Flow.Spawn(FailingChild(0.1));
            try
            {
                await FlowTask.WaitForSeconds(1.0);
            }
            catch (FlowCanceledException)
            {
                Log.Add("owner swallowed");
            }
        }
#pragma warning restore FLOW001

        var h = World.Run(CatchingCaller(Owner));
        TickFor(0.5);
        AssertLog("child throws", "owner swallowed", "caller caught child io");
        AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null, "CatchingCaller > Owner");
        AssertSucceeded(h);
    }

    [Test]
    public void AnOwnerStillUnwindingShowsNoExceptionUntilItEndsWithItsChildsException([Values] bool unbridgedAwait)
    {
        // FlowHandle.Exception is set exactly when the status is Faulted. A root flow canceled by its spawned child's
        // failure (an exception, or the misuse of an unbridged await) is Running while its finally awaits: its
        // CancelCause is already Fault, its Exception is not set yet; it ends Faulted with the exception.
        CaptureExceptions();
        var never = new TaskCompletionSource<int>();

        async FlowTask Child()
        {
            await FlowTask.WaitForSeconds(0.1);
            if (!unbridgedAwait) throw new IOException("child io");
#pragma warning disable FLOW002 // on purpose: an unbridged await ends the child with a FlowMisuseException
            await never.Task;
#pragma warning restore FLOW002
        }

        async FlowTask Owner()
        {
            Flow.Spawn(Child());
            try
            {
                await FlowTask.WaitForSeconds(10);
            }
            finally
            {
                await FlowTask.WaitForSeconds(0.3);
            }
        }

        var h = World.Run(Owner());
        TickFor(0.2);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running), "its finally awaits");
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Fault));
        Assert.That(h.Exception, Is.Null, "not ended yet");
        AssertNoExceptions();
        TickFor(0.5);
        var report = Exceptions.Single();
        Assert.That(report.Exception, unbridgedAwait ? Is.TypeOf<FlowMisuseException>() : Is.TypeOf<IOException>());
        AssertFaulted(h, report.Exception, CancelCause.Fault);
    }

    [Test]
    public void AChildThatFailsWhileItsOwnerIsCanceledIsUndeliveredAndTheFirstChildsExceptionGoesOn()
    {
        // The first child's failure cancels the owner. The owner's finally, which runs to its end, spawns a second child
        // (a task started in a canceled scope's cleanup is not canceled), which fails meanwhile: its owner is canceled
        // and takes no failure any more, so its exception is Undelivered, and the caller receives the first one.
        CaptureExceptions();

        async FlowTask Second()
        {
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("second throws");
            throw new IOException("second io");
        }

        async FlowTask Owner()
        {
            Flow.Spawn(FailingChild(0.1));
            try
            {
                await FlowTask.WaitForSeconds(1.0);
            }
            finally
            {
                Flow.Spawn(Second());
                await FlowTask.WaitForSeconds(0.3);
                Log.Add("owner finally end");
            }
        }

        var h = World.Run(CatchingCaller(Owner));
        TickFor(1.0);
        AssertLog("child throws", "second throws", "owner finally end", "caller caught child io");
        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "second io", "CatchingCaller > Owner > Second");
        AssertSucceeded(h);
    }

    [Test]
    public void AChildThatEndsWithAnExceptionAfterItsOwnerReturnedIsUndelivered()
    {
        // The worker's own spawned job fails, which cancels the worker, whose finally awaits. Meanwhile the owner
        // returns: its end waits for the worker. The worker then ends with the job's exception, but its owner is ending
        // and takes no failure any more: Undelivered. The owner ends normally.
        CaptureExceptions();

        async FlowTask Job()
        {
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("job throws");
            throw new IOException("job io");
        }

        async FlowTask Worker()
        {
            try
            {
                Flow.Spawn(Job());
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("worker finally");
                await FlowTask.WaitForSeconds(0.3);
                Log.Add("worker finally end");
            }
        }

        async FlowTask Owner()
        {
            Flow.Spawn(Worker());
            await FlowTask.WaitForSeconds(0.2);
            Log.Add("owner returns");
        }

        var h = World.Run(CatchingCaller(Owner));
        TickFor(1.0);
        AssertLog("job throws", "worker finally", "owner returns", "worker finally end", "caller: owner ok");
        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "job io", "CatchingCaller > Owner > Worker > Job");
        AssertSucceeded(h);
    }

    [Test]
    public void AnOwnerWhoseCallerIsCanceledWhileItUnwindsReportsItsChildsExceptionUndelivered()
    {
        // The caller of the owner is canceled while the owner's finally awaits. When the owner ends, nothing receives
        // the child's exception any more: it is Undelivered with the child's path, and the caller's catch does not run.
        CaptureExceptions();

        async FlowTask Owner()
        {
            Flow.Spawn(FailingChild(0.1));
            try
            {
                await FlowTask.WaitForSeconds(1.0);
            }
            finally
            {
                await FlowTask.WaitForSeconds(0.3);
                Log.Add("owner finally end");
            }
        }

        var h = World.Run(CatchingCaller(Owner));
        TickFor(0.2);
        h.Cancel();
        TickFor(0.5);
        AssertLog("child throws", "owner finally end");
        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "child io", "CatchingCaller > Owner > FailingChild");
        AssertCanceled(h, CancelCause.Explicit);
    }

    [Test]
    public void WorldDisposeWhileAnOwnerCarriesItsChildsExceptionReportsItUndelivered()
    {
        // The World is disposed while the owner, canceled by its child's failure, runs its finally. Dispose runs no
        // Tick, so the finally's await throws FlowCanceledException again and the owner ends with the child's exception;
        // its caller is canceled (WorldDisposed) and does not receive it: it is Undelivered with the child's path.
        CaptureExceptions();

        async FlowTask Owner()
        {
            Flow.Spawn(FailingChild(0.1));
            try
            {
                await FlowTask.WaitForSeconds(10);
            }
            finally
            {
                Log.Add("owner finally");
                await FlowTask.WaitForSeconds(5);
                Log.Add("owner finally end");
            }
        }

        var h = World.Run(CatchingCaller(Owner));
        TickFor(0.3);
        AssertLog("child throws", "owner finally");
        World.Dispose();
        AssertLog("child throws", "owner finally");
        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "child io", "CatchingCaller > Owner > FailingChild");
        AssertCanceled(h, CancelCause.WorldDisposed);
    }

    // ------------------------------------------------------------------ a child whose failure has not reached it yet

    public enum OwnerLetsGo
    {
        /// <summary>The owner cancels the child's handle.</summary>
        CancelsTheHandle,
        /// <summary>The owner returns, which ends the child with it.</summary>
        Returns,
        /// <summary>The owner loses a Race, which ends the child with it.</summary>
        LosesARace,
    }

    public enum PendingFailure
    {
        /// <summary>The signal the child waits on is closed with an error just after the owner's resume was queued.</summary>
        SignalClosedInTheSameFlush,
        /// <summary>The Task the child waits on through a bridge fails just after the owner's resume was queued.</summary>
        BridgeFailedInTheSameFlush,
        /// <summary>The signal was closed with an error a Tick earlier, but the child runs on a paused clock that holds the failure.</summary>
        HeldByAPause,
    }

    [Test]
    public void AChildWhoseFailureHasNotReachedItWhenItsOwnerLetsItGoEndsCanceledAndTheFailureIsUndelivered(
        [Values] OwnerLetsGo letsGo, [Values] PendingFailure pending)
    {
        // The child's wait has failed, but the failure has not reached the child's code when its owner lets the child go:
        // the owner's resume was queued before it, or a Pause holds it. The child is canceled first, so its catch does not
        // see the failure and its finally runs; the failure is reported once as Undelivered with the path where it was
        // thrown, and the child ends Canceled, not Faulted. Its owner takes no failure from it: the owner is not canceled
        // by it, and the owner's caller sees no exception.
        CaptureExceptions();
        var go = new Signal<int>(World, "Go");
        var source = new Signal<int>(World, "Source");
        var tcs = new TaskCompletionSource<int>();
        var paused = World.CreateClock("Paused");
        var failure = new IOException("source failed");
        var bridged = pending == PendingFailure.BridgeFailedInTheSameFlush;
        FlowHandle child = default;

        async FlowTask Child()
        {
            try
            {
                if (bridged) await FlowBridge.FromTask(_ => tcs.Task);
                else await source.Next();
                Log.Add("child resumed");
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                Log.Add("child caught " + e.GetType().Name);
            }
            finally
            {
                Log.Add("child finally");
            }
        }

        async FlowTask Owner()
        {
            child = Flow.Spawn(pending == PendingFailure.HeldByAPause ? Flow.WithClock(paused, Child()) : Child());
            if (letsGo == OwnerLetsGo.LosesARace)
            {
                await FlowTask.Never();
                return;
            }

            await go.Next();
            if (letsGo == OwnerLetsGo.CancelsTheHandle) child.Cancel();
            Log.Add("owner returns");
        }

        async FlowTask Caller()
        {
            if (letsGo == OwnerLetsGo.LosesARace) Log.Add("race won by " + (await FlowTask.Race(Owner(), go.Next())).Index);
            else await Owner();
            Log.Add("caller continues");
        }

        var h = World.Run(Caller());
        ScopedHandle pause = default;
        if (pending == PendingFailure.HeldByAPause)
        {
            pause = paused.Pause();
            source.Close(failure);
            Tick(); // the child's failure is held
            AssertLog();
            Assert.That(child.Status, Is.EqualTo(FlowStatus.Running));
            AssertNoExceptions();
        }

        go.Emit(1); // the owner's resume is queued first
        if (pending == PendingFailure.SignalClosedInTheSameFlush) source.Close(failure);
        if (bridged) tcs.SetException(failure); // taken in the intake of the next Tick, after the emit above
        Tick();

        switch (letsGo)
        {
            case OwnerLetsGo.CancelsTheHandle:
                AssertLog("child finally", "owner returns", "caller continues");
                break;
            case OwnerLetsGo.Returns:
                AssertLog("owner returns", "child finally", "caller continues");
                break;
            default:
                AssertLog("child finally", "race won by 1", "caller continues");
                break;
        }

        var path = "Caller > Owner > Child > " + (bridged ? "Task<Int32>" : "Source.Next");
        var report = bridged
            ? AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "source failed", path)
            : AssertSingleException<SignalClosedException>(FlowExceptionKind.Undelivered, null, path);
        Assert.That(bridged ? report.Exception : report.Exception.InnerException, Is.SameAs(failure));
        var cause = letsGo switch
        {
            OwnerLetsGo.CancelsTheHandle => CancelCause.Explicit,
            OwnerLetsGo.Returns => CancelCause.ParentEnded,
            _ => CancelCause.RaceLost,
        };
        AssertCanceled(child, cause);
        AssertSucceeded(h);

        // Nothing follows, also once the pause that held the failure is released: the wait it was for is gone.
        pause.Dispose();
        Tick();
        Assert.That(Exceptions.Count, Is.EqualTo(1), DescribeExceptions());
        Assert.That(Log.Entries.Count, Is.EqualTo(3), "log: " + Log);
    }
}
