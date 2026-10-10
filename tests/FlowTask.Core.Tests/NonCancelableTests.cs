using System.IO;

namespace Katout.FlowTask.Tests;

/// <summary>
/// Flow.NonCancelable: the cancellation of the scopes around does not reach the marked task. Canceled before the await or
/// while it waits, the scope starts the task, waits for it and takes its result or exception; the cancellation reaches the
/// scope at its next await that is not marked. It protects cleanup in a finally block that the scope entered before it was
/// canceled (a return or an exception), where an await would otherwise throw FlowCanceledException and skip the rest of the
/// block. World.Dispose still ends it, and says so. A canceled scope that waits in its cleanup for 10 seconds is reported.
/// </summary>
public class NonCancelableTests : FailurePathTestBase
{
    async FlowTask Step(string name, int frames)
    {
        Log.Add(name + " start");
        await FlowTask.DelayFrames(frames);
        Log.Add(name + " end");
    }

    // ------------------------------------------------------------------ a finally entered before the cancellation

    [Test]
    public void MarkedAwaitsInAFinallyEnteredByAReturnRunToTheEndOfTheBlock()
    {
        // The scope returns into its finally, which closes a view, saves and destroys it. The cancel comes during the first
        // await: both marked awaits run to their end, a parked kind of wait (DelayFrames) included, then Destroy. The flow
        // ends Canceled and nothing is reported.
        CaptureExceptions();

        async FlowTask<int> Dialog()
        {
            try
            {
                await FlowTask.NextFrame();
                return 1;
            }
            finally
            {
                await Flow.NonCancelable(Step("close", 3));
                await Flow.NonCancelable(FlowTask.DelayFrames(2));
                await Flow.NonCancelable(Step("save", 2));
                Log.Add("destroy");
            }
        }

        var h = World.Run(Dialog());
        Tick(2);
        AssertLog("close start");
        h.Cancel();
        Tick(10);
        AssertLog("close start", "close end", "save start", "save end", "destroy");
        AssertCanceled(h, CancelCause.Explicit);
        AssertNoExceptions();
    }

    [Test]
    public void WithoutTheMarkTheCancellationSkipsTheRestOfAFinallyEnteredByAReturn()
    {
        // The same finally without the mark: the await throws FlowCanceledException and Destroy does not run.
        CaptureExceptions();

        async FlowTask<int> Dialog()
        {
            try
            {
                await FlowTask.NextFrame();
                return 1;
            }
            finally
            {
                await Step("close", 3);
                Log.Add("destroy");
            }
        }

        var h = World.Run(Dialog());
        Tick(2);
        h.Cancel();
        Tick(10);
        AssertLog("close start");
        AssertCanceled(h, CancelCause.Explicit);
        AssertNoExceptions();
    }

    [Test]
    public void TheExceptionAFinallyCarriedIsReportedUndeliveredOnceItsMarkedAwaitsEnd()
    {
        // The body throws, and the finally closes the view before the exception goes on. Canceled meanwhile, the scope
        // ends Canceled; the exception, which no caller can take any more, is reported as Undelivered instead of being lost.
        CaptureExceptions();

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.NextFrame();
                throw new IOException("save failed");
            }
            finally
            {
                await Flow.NonCancelable(Step("close", 3));
            }
        }

        var h = World.Run(Screen());
        Tick(2);
        h.Cancel();
        Tick(10);
        AssertLog("close start", "close end");
        AssertCanceled(h, CancelCause.Explicit);
        AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "save failed", "Screen");
    }

    [Test]
    public void MarkedAwaitsStartInAScopeCanceledBeforeThem()
    {
        // The scope cancels itself and returns into its finally: the cancellation is confirmed before the marked awaits
        // start. They start, run one after another and the block runs to its end. An await without the mark after them
        // throws FlowCanceledException.
        CaptureExceptions();
        FlowHandle self = default;

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.NextFrame();
                self.Cancel();
                return;
            }
            finally
            {
                await Flow.NonCancelable(Step("first", 2));
                await Flow.NonCancelable(Step("second", 2));
                Log.Add("after marked");
                await Step("unmarked", 2);
                Log.Add("after unmarked");
            }
        }

        self = World.Run(Screen());
        Tick(10);
        AssertLog("first start", "first end", "second start", "second end", "after marked");
        AssertCanceled(self, CancelCause.Explicit);
        AssertNoExceptions();
    }

    [Test]
    public void AMarkedTaskGivesItsResultOrItsExceptionToACanceledScope()
    {
        CaptureExceptions();

        async FlowTask<int> Count()
        {
            await FlowTask.DelayFrames(3);
            return 42;
        }

        async FlowTask Fails()
        {
            await FlowTask.DelayFrames(3);
            throw new IOException("upload failed");
        }

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.NextFrame();
                return;
            }
            finally
            {
                var saved = await Flow.NonCancelable(Count());
                Log.Add("saved " + saved);
                try
                {
                    await Flow.NonCancelable(Fails());
                }
                catch (IOException e)
                {
                    Log.Add("caught " + e.Message);
                }
            }
        }

        var h = World.Run(Screen());
        Tick(2);
        h.Cancel();
        Tick(10);
        AssertLog("saved 42", "caught upload failed");
        AssertCanceled(h, CancelCause.Explicit);
        AssertNoExceptions();
    }

    [Test]
    public void ARaceWaitsForTheMarkedCleanupOfItsLoser()
    {
        // The losing branch is in its finally when the other one wins: the Race resumes its caller once the loser's marked
        // cleanup has ended.
        CaptureExceptions();

        async FlowTask Loser()
        {
            try
            {
                await FlowTask.NextFrame();
                return;
            }
            finally
            {
                await Flow.NonCancelable(Step("loser save", 3));
            }
        }

        async FlowTask Caller()
        {
            await FlowTask.Race(Loser(), FlowTask.DelayFrames(2));
            Log.Add("race returned");
        }

        var h = World.Run(Caller());
        Tick(10);
        AssertLog("loser save start", "loser save end", "race returned");
        AssertSucceeded(h);
        AssertNoExceptions();
    }

    [Test]
    public void AParentThatEndsWaitsForTheMarkedCleanupOfItsSpawnedChild()
    {
        CaptureExceptions();

        async FlowTask Child()
        {
            try
            {
                await FlowTask.NextFrame();
                return;
            }
            finally
            {
                await Flow.NonCancelable(Step("child save", 3));
            }
        }

        async FlowTask Parent()
        {
            var child = Flow.Spawn(Child());
            await FlowTask.DelayFrames(2);
            Log.Add("parent returns " + child.Status);
        }

        var h = World.Run(Parent());
        Tick(2);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Tick(10);
        AssertLog("child save start", "parent returns Running", "child save end");
        AssertSucceeded(h);
        AssertNoExceptions();
    }

    [Test]
    public void AMarkedTaskFollowsPause()
    {
        CaptureExceptions();
        var game = World.CreateClock("Game");

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.NextFrame();
                return;
            }
            finally
            {
                await Flow.NonCancelable(FlowTask.WaitForSeconds(0.5));
                Log.Add("closed");
            }
        }

        var h = World.Run(Screen(), game);
        Tick(2);
        h.Cancel();
        var pause = game.Pause();
        TickFor(1);
        AssertLog();
        pause.Dispose();
        TickFor(1);
        AssertLog("closed");
        AssertCanceled(h, CancelCause.Explicit);
    }

    // ------------------------------------------------------------------ misuse

    [Test]
    public void AMarkedTaskPassedToSpawnRunOrACombinatorIsAMisuse()
    {
        async FlowTask Work() => await FlowTask.NextFrame();

        Assert.Throws<FlowMisuseException>(() => World.Run(Flow.NonCancelable(Work())));
        Assert.Throws<FlowMisuseException>(() => FlowTask.Race(Flow.NonCancelable(Work()), FlowTask.NextFrame()));
        Assert.Throws<FlowMisuseException>(() => FlowTask.WhenAll(new[] { Flow.NonCancelable(Work()) }));

        FlowMisuseException fromSpawn = null;

        async FlowTask Spawner()
        {
            try
            {
                Flow.Spawn(Flow.NonCancelable(Work()));
            }
            catch (FlowMisuseException e)
            {
                fromSpawn = e;
            }

            await FlowTask.NextFrame();
        }

        World.Run(Spawner());
        Tick();
        Assert.That(fromSpawn, Is.Not.Null);
        Assert.That(fromSpawn!.Message, Does.Contain("Flow.NonCancelable"));
    }

    [Test]
    public void AMarkOnACompletedValueDoesNothing()
    {
        async FlowTask<int> Screen()
        {
            var v = await Flow.NonCancelable(FlowTask.FromResult(3));
            await Flow.NonCancelable(FlowTask.CompletedTask);
            return v;
        }

        var h = World.Run(Screen());
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    // ------------------------------------------------------------------ World.Dispose

    [Test]
    public void DisposeEndsAMarkedAwaitAndWarns()
    {
        // Dispose runs no Tick: the marked await throws FlowCanceledException there, the rest of the block does not run,
        // and a warning says so, once.
        CaptureExceptions();

        async FlowTask Screen()
        {
            using var file = new Resource(Log, "file");
            try
            {
                await FlowTask.NextFrame();
                return;
            }
            finally
            {
                await Flow.NonCancelable(Step("save", 3));
                Log.Add("saved");
            }
        }

        var h = World.Run(Screen());
        World.Run(Screen());
        Tick(2);
        World.Dispose();
        AssertLog("save start", "save start", "file closed", "file closed");
        AssertCanceled(h, CancelCause.WorldDisposed);
        AssertNoExceptions();
        Assert.That(Warnings.Select(w => w.Kind), Is.EqualTo(new[] { FlowWarningKind.CleanupCutAtDispose }));
    }

    [Test]
    public void AtDisposeAMethodWithTenNestedFinallyBlocksThatAwaitStillDisposesItsUsing()
    {
        // Each block's await throws again at Dispose: ten in one method stay under the limit of rethrows, so every block
        // runs up to its await and the using statement runs after them.
        CaptureExceptions();

        async FlowTask Deep()
        {
            using var file = new Resource(Log, "file");
            try { try { try { try { try { try { try { try { try { try { await FlowTask.Never(); }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); } }
            finally { await FlowTask.NextFrame(); Log.Add("outermost"); }
        }

        World.Run(Deep());
        Tick();
        World.Dispose();
        AssertLog("file closed");
        AssertNoExceptions();
    }

    [Test]
    public void CanceledThenTickedUntilTheFlowsEndBeforeDisposeTheCleanupFinishes()
    {
        // The procedure docs/en/guide/failures.md gives for a cleanup that must finish before the World goes: cancel the
        // flows, Tick until they have ended, with a limit, then Dispose. Nothing is cut and nothing is warned.
        CaptureExceptions();

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.NextFrame();
                return;
            }
            finally
            {
                await Flow.NonCancelable(Step("save", 3));
                Log.Add("saved");
            }
        }

        var h = World.Run(Screen());
        Tick(2);
        h.Cancel();
        for (var i = 0; i < 600 && h.Status == FlowStatus.Running; i++) Tick();
        World.Dispose();
        AssertLog("save start", "save end", "saved");
        AssertCanceled(h, CancelCause.Explicit);
        AssertNoExceptions();
        Assert.That(Warnings, Is.Empty);
    }

    // ------------------------------------------------------------------ cleanup that does not end

    [Test]
    public void ACleanupStillWaitingTenSecondsAfterItBeganIsReportedOnce([Values] bool marked)
    {
        // A finally that loops after the cancel, or a marked await that never ends: nothing cancels them, so the flow stays
        // Running. 10 seconds after the wait began, a warning names the scope; it is raised once per World.
        async FlowTask Screen()
        {
            try
            {
                await FlowTask.NextFrame();
                if (marked) return;
                await FlowTask.Never();
            }
            finally
            {
                if (marked)
                {
                    await Flow.NonCancelable(FlowTask.Never());
                }
                else
                {
                    while (true) await FlowTask.NextFrame();
                }
            }
        }

        var h = World.Run(Flow.Named("Stuck", Screen()));
        var other = World.Run(Flow.Named("Stuck too", Screen()));
        Tick(2);
        h.Cancel();
        other.Cancel();
        TickFor(9);
        Assert.That(Warnings, Is.Empty);
        TickFor(2);
        Assert.That(Warnings.Count, Is.EqualTo(1), string.Join("; ", Warnings));
        Assert.That(Warnings[0].Kind, Is.EqualTo(FlowWarningKind.LongCleanup));
        Assert.That(Warnings[0].ScopePath, Does.StartWith("Stuck"));
        TickFor(20);
        Assert.That(Warnings.Count, Is.EqualTo(1));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
    }

    [Test]
    public void ACleanupThatEndsWithinTenSecondsIsNotReported()
    {
        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.WaitForSeconds(9);
            }
        }

        var h = World.Run(Screen());
        Tick();
        h.Cancel();
        TickFor(30);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(Warnings, Is.Empty);
    }

    sealed class Resource : IDisposable
    {
        readonly Log _log;
        readonly string _name;

        internal Resource(Log log, string name)
        {
            _log = log;
            _name = name;
        }

        public void Dispose() => _log.Add(_name + " closed");
    }
}
