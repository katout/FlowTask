using System.IO;

namespace Katout.FlowTask.Tests;

/// <summary>
/// An exception that no receiver can take any more is not carried. Its receiver was canceled before it resumed (paused
/// and then left, disposed, a Task continuation that arrived late), had already settled (a Race with a winner, a WhenAll
/// that failed first), or let go of it; or the exception left a canceled scope, which passes nothing on. The exception
/// changes no result: it is reported once to OnUnhandledException as <see cref="FlowExceptionKind.Undelivered"/>, with
/// the path where it was thrown, the canceled receiver's catch does not run (its finally does) and it ends Canceled,
/// and the code that canceled it or the combinator that settled goes on.
/// </summary>
public class UndeliverableExceptionTests : FailurePathTestBase
{
    // ------------------------------------------------------------------ the receiver was canceled

    public enum QuitBy
    {
        Race,
        Handle,
        Dispose,
    }

    [Test]
    public void AHandledNetworkErrorHeldByAPauseIsUndeliveredWhenThePlayerQuits([Values] QuitBy quitBy)
    {
        // The stage's load fails while the game is paused; the stage has a catch (a retry dialog) for it, but its
        // resume follows the pause. The player quits while paused: the quit button wins a Race, the stage's handle is
        // canceled, or the World is disposed. The stage is canceled before it resumes: the retry dialog does not open,
        // the finally runs, and the thrown exception is Undelivered. The stage's handle is Canceled (by the Race, the
        // handle or Dispose), without an Exception.
        CaptureExceptions();
        var game = World.CreateClock("Game");
        var ui = World.CreateClock("UI", World.UnscaledClock);
        var load = new TaskCompletionSource<int>();
        var quit = new Signal<int>(World, "Quit");

        async FlowTask Stage()
        {
            try
            {
                try
                {
                    await FlowBridge.FromTask(_ => load.Task);
                    Log.Add("stage: loaded");
                }
                catch (IOException)
                {
                    Log.Add("stage: net error, retry dialog");
                }
            }
            finally
            {
                Log.Add("stage finally");
            }
        }

        async FlowTask Root()
        {
            var r = await FlowTask.Race(Flow.WithClock(game, Stage()), quit.Next());
            Log.Add("root: title (race index " + r.Index + ")");
        }

        var h = quitBy == QuitBy.Race ? World.Run(Root(), ui) : World.Run(Stage(), game);
        Tick();
        var pause = game.Pause();
        load.SetException(new IOException("net down"));
        Tick(2);
        AssertLog();
        AssertNoExceptions();
        switch (quitBy)
        {
            case QuitBy.Race:
                quit.Emit(1);
                break;
            case QuitBy.Handle:
                h.Cancel();
                break;
            default:
                World.Dispose();
                Log.Add("dispose returned");
                break;
        }

        if (!World.IsDisposed)
        {
            Tick();
            pause.Dispose();
            Tick(3);
        }

        switch (quitBy)
        {
            case QuitBy.Race:
                AssertLog("stage finally", "root: title (race index 1)");
                AssertSucceeded(h);
                break;
            case QuitBy.Handle:
                AssertLog("stage finally");
                AssertCanceled(h, CancelCause.Explicit);
                break;
            default:
                AssertLog("stage finally", "dispose returned");
                AssertCanceled(h, CancelCause.WorldDisposed);
                break;
        }

        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "net down", (quitBy == QuitBy.Race ? "Root > " : "") + "Stage > Task<Int32>");
    }

    [Test]
    public void ABridgedTasksFailureThatArrivesAfterTheFlowWasCanceledIsUndelivered([Values(false, true)] bool cancelParent)
    {
        // A flow awaits a Task through a bridge. With the flow alive, its catch receives the Task's exception from the
        // bridge. When the flow is canceled in the frame the exception is thrown, the Task's failure arrives through the
        // inbox after the cancel: the flow's catch does not run and the exception is Undelivered.
        CaptureExceptions();
        FlowHandle parent = default;
        var source = new TaskCompletionSource<int>();

        async FlowTask Parent()
        {
            try
            {
                await FlowBridge.FromTask(_ => (Task)source.Task);
                Log.Add("parent after bridge");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("parent caught " + e.Message);
            }
            finally
            {
                Log.Add("parent finally");
            }
        }

        async FlowTask Thrower()
        {
            await FlowTask.WaitForSeconds(1.0);
            Log.Add("throw helper bug");
            source.SetException(new InvalidOperationException("helper bug"));
        }

        async FlowTask Killer()
        {
            await FlowTask.WaitForSeconds(1.0);
            Log.Add("cancel parent");
            parent.Cancel();
        }

        parent = World.Run(Parent());
        World.Run(Thrower());
        if (cancelParent) World.Run(Killer());
        TickFor(1.3);
        if (cancelParent)
        {
            AssertLog("throw helper bug", "cancel parent", "parent finally");
            // With the path of the bridge, where the flow received it: the exception came through a Task, which the
            // library does not follow back, as for any Task's exception.
            AssertSingleException<InvalidOperationException>(FlowExceptionKind.Undelivered, "helper bug", "Parent > Task<FlowUnit>");
            AssertCanceled(parent, CancelCause.Explicit);
        }
        else
        {
            AssertLog("throw helper bug", "parent caught helper bug", "parent finally");
            AssertNoExceptions();
            AssertSucceeded(parent);
        }
    }

    [Test]
    public void AnExceptionAwaitedInTheCleanupOfACanceledScopeIsThrownThereAndUndeliveredIfItLeaves([Values] bool caught)
    {
        // Once FlowCanceledException has reached a scope's code, the awaits of its finally run as a live scope's: the
        // exception of the save it awaits there is thrown at that await, where a catch in the finally receives it. One
        // that leaves the canceled scope has no receiver: it is Undelivered, with the path where it was thrown, the rest
        // of the finally does not run, and the scope ends Canceled.
        CaptureExceptions();
        var cloud = new TaskCompletionSource<int>();

        async FlowTask SaveToCloud()
        {
            await FlowBridge.FromTask(_ => cloud.Task);
            Log.Add("saved");
        }

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("finally: save");
                if (caught)
                {
                    try
                    {
                        await SaveToCloud();
                    }
                    catch (IOException e)
                    {
                        Log.Add("finally caught " + e.Message);
                    }
                }
                else
                {
                    await SaveToCloud();
                }

                Log.Add("finally end");
            }
        }

        var h = World.Run(Screen());
        h.Cancel();
        Tick();
        AssertLog("finally: save");
        cloud.SetException(new IOException("HTTP 503"));
        Tick();
        if (caught)
        {
            AssertLog("finally: save", "finally caught HTTP 503", "finally end");
            AssertNoExceptions();
        }
        else
        {
            AssertLog("finally: save");
            AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "HTTP 503", "Screen > SaveToCloud > Task<Int32>");
        }

        AssertCanceled(h, CancelCause.Explicit);
    }

    // ------------------------------------------------------------------ the receiver had settled

    public enum TwoRequests
    {
        WhenAll,
        Race,
    }

    [Test]
    public void TheSecondFailureOfTheSameIntakeIsUndeliveredAndTheFirstIsCaught([Values] TwoRequests shape)
    {
        // Two parallel requests fail in the same intake (a network drop), the second request's first. Its
        // failure settles the combinator; the other branch is canceled and unwound before the combinator throws, and
        // its exception, delivered to a canceled branch, is Undelivered. The caller's catch receives the first failure;
        // the second is reported, not lost.
        CaptureExceptions();
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        async FlowTask<int> Fetch(TaskCompletionSource<int> t, string name)
        {
            try
            {
                return await FlowBridge.FromTask(_ => t.Task);
            }
            finally
            {
                Log.Add(name + " finally");
            }
        }

        async FlowTask Root()
        {
            try
            {
                if (shape == TwoRequests.WhenAll) await FlowTask.WhenAll(Fetch(a, "a"), Fetch(b, "b"));
                else await FlowTask.Race(Fetch(a, "a"), Fetch(b, "b"));
                Log.Add("both ok");
            }
            catch (IOException e)
            {
                Log.Add("root caught " + e.Message + ", offline dialog");
            }
        }

        var h = World.Run(Root());
        Tick();
        b.SetException(new IOException("b offline"));
        a.SetException(new IOException("a offline"));
        Tick(3);
        AssertLog("b finally", "a finally", "root caught b offline, offline dialog");
        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "a offline", "Root > Fetch > Task<Int32>");
        AssertSucceeded(h);
    }

    [Test]
    public void ABranchsExceptionReachesTheRaceWhenTheBranchHasEndedAndIsUndeliveredIfAnotherBranchWonMeanwhile([Values] bool otherBranchWins)
    {
        // The branch throws while a helper it spawned runs its cleanup (a finally that awaits): the branch ends after
        // its children, and only then does its exception reach the Race. If the other branch completes meanwhile, it
        // wins: the Race lets go of the failing branch, whose exception, with no receiver left, is Undelivered. The Race
        // resumes its caller once the branch has ended either way.
        CaptureExceptions();

        async FlowTask Helper()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("helper cleanup");
                await FlowTask.WaitForSeconds(0.5);
                Log.Add("helper cleanup end");
            }
        }

        async FlowTask Failing()
        {
            Flow.Spawn(Helper());
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("failing throws");
            throw new IOException("branch io");
        }

        async FlowTask Root()
        {
            try
            {
                var r = await FlowTask.Race(Failing(), otherBranchWins ? FlowTask.WaitForSeconds(0.2) : FlowTask.Never());
                Log.Add("race index " + r.Index);
            }
            catch (IOException e)
            {
                Log.Add("root caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        TickFor(1.0);
        AssertLog("failing throws", "helper cleanup", "helper cleanup end", otherBranchWins ? "race index 1" : "root caught branch io");
        if (otherBranchWins) AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "branch io", "Root > Failing");
        else AssertNoExceptions();
        AssertSucceeded(h);
    }

    [Test]
    public void ACombinatorCanceledWhileItUnwindsItsBranchesAfterAFailureReportsTheFailure([Values(false, true)] bool race)
    {
        // A branch throws, and the combinator cancels and unwinds the other branches before
        // it throws. One of them cancels the host in its finally (here through the host's handle), so when the unwinding
        // ends the combinator has no receiver any more: the exception is Undelivered instead of lost, the host's catch does
        // not run and the host ends Canceled.
        CaptureExceptions();
        FlowHandle host = default;

        async FlowTask Sibling()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("sibling finally");
                host.Cancel();
            }
        }

        async FlowTask Host()
        {
            try
            {
                if (race) await FlowTask.Race(Throws(0, "bug", 0.1), Sibling());
                else await FlowTask.WhenAll(Throws(0, "bug", 0.1), Sibling());
                Log.Add("host after");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("host caught " + e.Message);
            }
            finally
            {
                Log.Add("host finally");
            }
        }

        host = World.Run(Host());
        TickFor(0.3);
        AssertLog("throw bug", "sibling finally", "host finally");
        AssertSingleException<InvalidOperationException>(FlowExceptionKind.Undelivered, "bug", "Host > Throws");
        AssertCanceled(host, CancelCause.Explicit);
    }

    [Test]
    public void AWorldWithoutOnUnhandledExceptionThrowsAUndeliveredReportFromTick()
    {
        // A World without an OnUnhandledException handler throws every kind of report alike, Undelivered
        // included, from the Tick that made it (the engine integrations set a handler that logs it as a warning). The
        // same two failures as above: the caller's catch has handled the first one when Tick throws the second.
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        async FlowTask<int> Fetch(TaskCompletionSource<int> t) => await FlowBridge.FromTask(_ => t.Task);

        async FlowTask Root()
        {
            try
            {
                await FlowTask.WhenAll(Fetch(a), Fetch(b));
            }
            catch (IOException e)
            {
                Log.Add("root caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        Tick();
        b.SetException(new IOException("b offline"));
        a.SetException(new IOException("a offline"));
        var thrown = Assert.Throws<FlowUnhandledException>(() => Tick());
        Assert.That(thrown.ExceptionInfos.Count, Is.EqualTo(1));
        AssertException<IOException>(thrown.ExceptionInfos[0], FlowExceptionKind.Undelivered, "a offline", "Root > Fetch > Task<Int32>");
        AssertLog("root caught b offline");
        AssertSucceeded(h);
    }
}
