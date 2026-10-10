using System.IO;

namespace Katout.FlowTask.Tests;

/// <summary>
/// Cleanup of a canceled scope. Once FlowCanceledException has reached a scope's code, the awaits of its catch and finally
/// blocks run as a live scope's: they run to their end, the tasks they start are not canceled, and a later cancel of an
/// ancestor does not reach them. Everything around waits for the cleanup: the scope stays Running (its CancelCause tells
/// that it is closing), and so do its parent's end, a Race that it lost and a WhenAll that failed. The children of a
/// canceled scope finish their cleanup first, as through a call stack. World.Dispose runs no Tick, so there an await of a
/// canceled scope throws again: each block runs up to its first await, and nothing starts.
/// </summary>
public class CleanupTests : FailurePathTestBase
{
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

    /// <summary>Closed asynchronously: <c>await using</c> awaits its DisposeAsync, a FlowTask method.</summary>
    sealed class Connection
    {
        readonly Log _log;

        internal Connection(Log log) => _log = log;

        public async FlowTask DisposeAsync()
        {
            _log.Add("connection closing");
            await FlowTask.NextFrame();
            _log.Add("connection closed");
        }
    }

    /// <summary>Waits until canceled, then saves for <paramref name="frames"/> frames in its finally.</summary>
    async FlowTask SavesWhenClosed(string name, int frames)
    {
        try
        {
            await FlowTask.Never();
        }
        finally
        {
            Log.Add(name + " saving");
            await FlowTask.DelayFrames(frames);
            Log.Add(name + " saved");
        }
    }

    async FlowTask FailsNextFrame(string message)
    {
        await FlowTask.NextFrame();
        Log.Add(message);
        throw new IOException(message);
    }

    // ------------------------------------------------------------------ the awaits of a cleanup run to their end

    [Test]
    public void AwaitsInTheFinallyBlocksOfACanceledScopeRunToTheirEnd()
    {
        // The inner finally awaits once, the outer one several times, and an await using closes a connection whose
        // DisposeAsync is a FlowTask method: each await runs to its end, inner blocks first, then the using statements, then
        // AddCleanup. Meanwhile the flow is Running and says that it is closing; nothing is reported.
        CaptureExceptions();

        async FlowTask Fade()
        {
            Log.Add("fade start");
            await FlowTask.DelayFrames(2);
            Log.Add("fade end");
        }

        async FlowTask SaveItem(int i)
        {
            await FlowTask.NextFrame();
            Log.Add("item " + i + " saved");
        }

        async FlowTask Screen()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            await using var connection = new Connection(Log);
            using var file = new Resource(Log, "file");
            try
            {
                try
                {
                    await FlowTask.Never();
                }
                finally
                {
                    Log.Add("inner finally");
                    await Fade();
                    Log.Add("inner finally end");
                }
            }
            finally
            {
                for (var i = 0; i < 3; i++) await SaveItem(i);
                Log.Add("outer finally end");
            }
        }

        var h = World.Run(Screen());
        Tick();
        h.Cancel();
        Tick();
        AssertLog("inner finally", "fade start");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
        Assert.That(h.ToString(), Is.EqualTo("FlowHandle(Screen, Running: Explicit)"));
        Assert.That(World.Dump(), Does.Contain("Screen (scope) [Default]").And.Contain("<canceling: Explicit>"), World.Dump());
        Tick(10);
        AssertLog("inner finally", "fade start", "fade end", "inner finally end", "item 0 saved", "item 1 saved", "item 2 saved",
            "outer finally end", "file closed", "connection closing", "connection closed", "cleanup");
        AssertNoExceptions();
        AssertCanceled(h, CancelCause.Explicit);
    }

    [Test]
    public void AFinallyThatBeganWhileTheScopeWasLiveIsCutAndTheNextOneRunsToItsEnd()
    {
        // The callee's failure goes up into a finally while the flow is live: that finally is ordinary code, so the cancel
        // that comes during its fade cancels the fade, and its await throws FlowCanceledException, which replaces the
        // failure (the C# rule). The outer finally runs after the exception reached the code: its await runs to its end.
        CaptureExceptions();
        FlowHandle parent = default;

        async FlowTask Fade()
        {
            Log.Add("fade start");
            await FlowTask.WaitForSeconds(0.3);
            Log.Add("fade end");
        }

        async FlowTask Parent()
        {
            try
            {
                try
                {
                    await Throws(0, "child bug", 1.0);
                }
                finally
                {
                    await Fade();
                    Log.Add("inner finally end");
                }
            }
            finally
            {
                await FlowTask.NextFrame();
                Log.Add("outer finally end");
            }
        }

        async FlowTask Killer()
        {
            await FlowTask.WaitForSeconds(1.1);
            Log.Add("cancel");
            parent.Cancel();
        }

        parent = World.Run(Parent());
        World.Run(Killer());
        TickFor(1.6);
        AssertLog("throw child bug", "fade start", "cancel", "outer finally end");
        AssertNoExceptions();
        AssertCanceled(parent, CancelCause.Explicit);
    }

    // ------------------------------------------------------------------ everything around waits for the cleanup

    public enum Holder
    {
        ParentReturns,
        ParentThrows,
        Race,
        FailedWhenAll,
    }

    [Test]
    public void TheParentsEndARaceAndAFailedWhenAllWaitForABranchsCleanup([Values] Holder holder)
    {
        // The branch saves in its finally when its parent returns or throws (it spawned the branch), when it loses a Race, or
        // when its WhenAll sibling fails. The branch is Running and canceling meanwhile, with the cause, and the parent, the
        // Race and the WhenAll end after it: the parent's AddCleanup and the code after the combinator come after the save.
        CaptureExceptions();
        FlowHandle spawned = default;

        async FlowTask Parent()
        {
            Flow.AddCleanup(() => Log.Add("parent cleanup"));
            switch (holder)
            {
                case Holder.ParentReturns:
                case Holder.ParentThrows:
                    spawned = Flow.Spawn(SavesWhenClosed("branch", 3));
                    await FlowTask.NextFrame();
                    if (holder == Holder.ParentThrows) throw new IOException("load failed");
                    Log.Add("parent returns");
                    break;
                case Holder.Race:
                    Log.Add("race won by " + (await FlowTask.Race(SavesWhenClosed("branch", 3), FlowTask.NextFrame())).Index);
                    break;
                default:
                    try
                    {
                        await FlowTask.WhenAll(SavesWhenClosed("branch", 3), FailsNextFrame("load failed"));
                    }
                    catch (IOException e)
                    {
                        Log.Add("whenall threw " + e.Message);
                    }

                    break;
            }
        }

        async FlowTask App()
        {
            try
            {
                await Parent();
            }
            catch (IOException e)
            {
                Log.Add("app caught " + e.Message);
            }

            Log.Add("app continues");
        }

        var h = World.Run(App());
        Tick();
        var branch = World.Diagnostics.Walk().Single(s => s.Name == nameof(SavesWhenClosed));
        var cause = holder switch
        {
            Holder.ParentReturns => CancelCause.ParentEnded,
            Holder.Race => CancelCause.RaceLost,
            _ => CancelCause.Fault,
        };
        Assert.That(branch.Status, Is.EqualTo(FlowStatus.Running), World.Dump());
        Assert.That(branch.IsCanceling, Is.True, World.Dump());
        Assert.That(branch.Cause, Is.EqualTo(cause), World.Dump());
        var parent = World.Diagnostics.Walk().Single(s => s.Name == "Parent");
        Assert.That(parent.Status, Is.EqualTo(FlowStatus.Running), World.Dump());
        Assert.That(parent.IsCanceling, Is.False, World.Dump());
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        // The dump tells why a combinator that has settled has not returned yet.
        if (holder == Holder.Race) Assert.That(World.Dump(), Does.Contain("Race decided, 1 branch still ending"), World.Dump());
        if (holder == Holder.FailedWhenAll) Assert.That(World.Dump(), Does.Contain("WhenAll failed, 1 branch still ending"), World.Dump());
        if (holder is Holder.ParentReturns or Holder.ParentThrows)
        {
            Assert.That(spawned.Status, Is.EqualTo(FlowStatus.Running));
            Assert.That(spawned.CancelCause, Is.EqualTo(cause));
        }

        Tick(3);
        var expected = new List<string>();
        if (holder == Holder.FailedWhenAll) expected.Add("load failed");
        if (holder == Holder.ParentReturns) expected.Add("parent returns");
        expected.AddRange(new[] { "branch saving", "branch saved" });
        if (holder == Holder.Race) expected.Add("race won by 1");
        if (holder == Holder.FailedWhenAll) expected.Add("whenall threw load failed");
        expected.Add("parent cleanup");
        if (holder == Holder.ParentThrows) expected.Add("app caught load failed");
        expected.Add("app continues");
        AssertLog(expected.ToArray());
        AssertNoExceptions();
        AssertSucceeded(h);
        if (holder is Holder.ParentReturns or Holder.ParentThrows) AssertCanceled(spawned, cause);
    }

    [Test]
    public void ALaterCancelOfAnAncestorDoesNotInterruptARunningCleanup([Values] bool byAFailure)
    {
        // Settings loses a Race and saves in its finally. While the save runs, the whole App is canceled: by its handle, or
        // by a child it spawned that fails. The cancellation does not enter Settings, which was canceled before, so the save
        // is not canceled and runs to its end; the App ends after it, Canceled or with the child's exception.
        CaptureExceptions();
        var back = new Signal<FlowUnit>(World);

        async FlowTask Save()
        {
            Log.Add("save start");
            await FlowTask.DelayFrames(5);
            Log.Add("save done");
        }

        async FlowTask Settings()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await Save();
                Log.Add("settings closed");
            }
        }

        async FlowTask Bomb()
        {
            await FlowTask.DelayFrames(4);
            throw new InvalidOperationException("bomb");
        }

        async FlowTask App()
        {
            if (byAFailure) Flow.Spawn(Bomb());
            await FlowTask.Race(Settings(), back.Next());
            Log.Add("back");
            await FlowTask.Never();
        }

        var h = World.Run(App());
        Tick();
        back.Emit(FlowUnit.Default);
        Tick(2);
        if (!byAFailure) h.Cancel();
        Tick(2);
        AssertLog("save start");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(byAFailure ? CancelCause.Fault : CancelCause.Explicit));
        var save = World.Diagnostics.Walk().Single(s => s.Name == "Save");
        Assert.That(save.IsCanceling, Is.False, World.Dump());
        Assert.That(save.Cause, Is.EqualTo(CancelCause.None), World.Dump());
        Assert.That(World.Diagnostics.Walk().Single(s => s.Name == "Settings").Cause, Is.EqualTo(CancelCause.RaceLost), World.Dump());
        Tick(5);
        AssertLog("save start", "save done", "settings closed");
        if (byAFailure)
        {
            var report = AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "bomb", "App > Bomb");
            AssertFaulted(h, report.Exception, CancelCause.Fault);
        }
        else
        {
            AssertNoExceptions();
            AssertCanceled(h, CancelCause.Explicit);
        }
    }

    [Test]
    public void ACleanupOnAPausedClockWaitsForTheRelease()
    {
        // The screen runs on the Game clock, which is paused when the screen is canceled. FlowCanceledException reaches its
        // code at once, but the cleanup's resumes follow the Pause as any resume does: a fade on the unscaled clock ends,
        // and the screen goes on after it only once the Pause is released; then its wait on its own clock runs.
        CaptureExceptions();
        var game = World.CreateClock("Game");

        async FlowTask Fade()
        {
            await FlowTask.WaitForSeconds(0.2);
            Log.Add("fade done");
        }

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("closing");
                await Flow.WithClock(World.UnscaledClock, Fade());
                Log.Add("after fade");
                await FlowTask.WaitForSeconds(0.2);
                Log.Add("closed");
            }
        }

        var h = World.Run(Screen(), game);
        Tick();
        var pause = game.Pause();
        h.Cancel();
        TickFor(1.0);
        AssertLog("closing", "fade done");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
        pause.Dispose();
        Tick();
        AssertLog("closing", "fade done", "after fade");
        TickFor(0.3);
        AssertLog("closing", "fade done", "after fade", "closed");
        AssertNoExceptions();
        AssertCanceled(h, CancelCause.Explicit);
    }

    [Test]
    public void FlowSpawnStartsInACleanupAndTheTaskEndsWithTheScope()
    {
        // The scope cancels itself: until FlowCanceledException reaches its code, a Spawn does not start its task and
        // returns a handle that has ended Canceled. In the finally a Spawn starts; the task runs until the scope ends, which
        // unwinds it. A spawned task that fails while its owner closes has no receiver: Undelivered, and the cleanup goes on.
        CaptureExceptions();
        FlowHandle self = default, refused = default, ticker = default, failing = default;

        async FlowTask Ticker()
        {
            try
            {
                while (true)
                {
                    await FlowTask.NextFrame();
                    Log.Add("tick");
                }
            }
            finally
            {
                Log.Add("ticker finally");
            }
        }

        async FlowTask Screen()
        {
            await FlowTask.NextFrame();
            self.Cancel();
            refused = Flow.Spawn(Ticker());
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                ticker = Flow.Spawn(Ticker());
                failing = Flow.Spawn(FailsNextFrame("spawned fails"));
                Log.Add("spawned: " + ticker.Status);
                await FlowTask.DelayFrames(3);
                Log.Add("finally end");
            }
        }

        self = World.Run(Screen());
        Tick(6);
        AssertLog("spawned: Running", "tick", "spawned fails", "tick", "finally end", "ticker finally");
        Assert.That(refused.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(refused.IsCompleted, Is.True);
        AssertCanceled(ticker, CancelCause.ParentEnded);
        var undelivered = AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "spawned fails", "Screen > FailsNextFrame");
        Assert.That(failing.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(failing.Exception, Is.SameAs(undelivered.Exception));
        AssertCanceled(self, CancelCause.Explicit);
    }

    // ------------------------------------------------------------------ exceptions of the cleanup

    public enum CleanupFailure
    {
        /// <summary>The finally throws after its await: a cleanup exception.</summary>
        Throws,
        /// <summary>The finally awaits a task that fails and lets the exception out: it goes on through the outer finally, then Undelivered.</summary>
        LetsAChildsExceptionOut,
        /// <summary>The finally catches the exception of the task it awaits, as a live scope does: nothing is reported.</summary>
        CatchesAChildsException,
        /// <summary>The finally awaits a Task without a bridge: a cleanup exception, and the rest of the method does not run.</summary>
        UnbridgedAwait,
    }

    [Test]
    public void AnExceptionOfACleanupIsReportedAndTheScopeEndsCanceled([Values] CleanupFailure failure)
    {
        // The awaits of the cleanup take the results of the tasks they start, exceptions included. An exception that leaves
        // the canceled scope has no receiver: one its code threw is a cleanup exception, one it received at an await is
        // Undelivered, with the path where it was thrown. Either way the scope ends Canceled and AddCleanup runs.
        CaptureExceptions();
        var never = new TaskCompletionSource<int>();

        async FlowTask Screen()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            try
            {
                try
                {
                    await FlowTask.Never();
                }
                finally
                {
                    switch (failure)
                    {
                        case CleanupFailure.Throws:
                            await FlowTask.NextFrame();
                            throw new IOException("cleanup failed");
                        case CleanupFailure.LetsAChildsExceptionOut:
                            await FailsNextFrame("save failed");
                            break;
                        case CleanupFailure.CatchesAChildsException:
                            try
                            {
                                await FailsNextFrame("save failed");
                            }
                            catch (IOException e)
                            {
                                Log.Add("caught " + e.Message);
                            }

                            break;
                        default:
#pragma warning disable FLOW002 // on purpose: an unbridged await in a cleanup
                            await never.Task;
#pragma warning restore FLOW002
                            break;
                    }

                    Log.Add("inner finally end");
                }
            }
            finally
            {
                await FlowTask.NextFrame();
                Log.Add("outer finally end");
            }
        }

        var h = World.Run(Screen());
        Tick();
        h.Cancel();
        Tick(4);
        switch (failure)
        {
            case CleanupFailure.Throws:
                AssertLog("outer finally end", "cleanup");
                AssertSingleException<IOException>(FlowExceptionKind.Cleanup, "cleanup failed", "Screen");
                break;
            case CleanupFailure.LetsAChildsExceptionOut:
                AssertLog("save failed", "outer finally end", "cleanup");
                AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "save failed", "Screen > FailsNextFrame");
                break;
            case CleanupFailure.CatchesAChildsException:
                AssertLog("save failed", "caught save failed", "inner finally end", "outer finally end", "cleanup");
                AssertNoExceptions();
                break;
            default:
                AssertLog("cleanup");
                var report = AssertSingleException<FlowMisuseException>(FlowExceptionKind.Cleanup, null, "Screen");
                Assert.That(report.Exception.Message, Does.Contain("FLOW002"));
                break;
        }

        AssertCanceled(h, CancelCause.Explicit);
    }

    [Test]
    public void AnEndlessSynchronousLoopInACleanupEndsTheScopeAtThatAwait()
    {
        // The awaits of a cleanup count as synchronous completions as a live scope's do: a finally that retries, without
        // end, a save that fails at once is ended at the await past the limit, and the rest of the block does not run. The
        // scope was canceled, so the misuse is reported as another exception of a canceled scope's code is (a cleanup exception),
        // and the scope ends Canceled with the cause of its cancellation. The loop gives up by itself at 2,000,000
        // attempts, so that a broken limit fails the test instead of hanging it.
        CaptureExceptions();
        var attempts = 0;

        async FlowTask<bool> TrySave()
        {
            attempts++;
            return false;
        }

        async FlowTask Screen()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                while (attempts < 2_000_000 && !await TrySave())
                {
                }

                Log.Add("saved");
            }
        }

        var h = World.Run(Screen());
        Tick();
        h.Cancel();
        Tick();
        Assert.That(attempts, Is.EqualTo(1_000_001));
        AssertLog("cleanup");
        var report = AssertSingleException<FlowMisuseException>(FlowExceptionKind.Cleanup, null, "Screen");
        Assert.That(report.Exception.Message, Does.Contain("awaits synchronously in one Tick"));
        AssertCanceled(h, CancelCause.Explicit);
    }

    // ------------------------------------------------------------------ a resume queued before the cancel

    public enum QueuedResume
    {
        /// <summary>The screen loses a Race in the frame its own wait is due: its resume is queued behind the winner's.</summary>
        ItLostARaceInTheFrameItsWaitWasDue,
        /// <summary>The child the screen awaits ends in a frame; a flow processed before the screen's resume cancels it.</summary>
        AChildEnded,
        /// <summary>The child ended while a Pause held the screen's resume; the screen is canceled, then the Pause released.</summary>
        HeldByAPause,
    }

    [Test]
    public void AResumeQueuedBeforeTheCancelDoesNotResumeTheCleanup([Values] QueuedResume queued, [Values] bool cleanupAwaitsAScope)
    {
        // The screen's await has ended and its resume is queued when the screen is canceled: the unwinding throws
        // FlowCanceledException at that await at once, and the finally begins its cleanup, which awaits. The queued resume
        // belonged to the await the unwinding consumed: the cleanup's await still runs its three frames to their end.
        CaptureExceptions();
        var game = World.CreateClock("Game");
        FlowHandle screen = default;

        async FlowTask Child()
        {
            await FlowTask.NextFrame();
        }

        async FlowTask Fade()
        {
            await FlowTask.DelayFrames(3);
            Log.Add("fade end");
        }

        async FlowTask Screen()
        {
            try
            {
                if (queued == QueuedResume.ItLostARaceInTheFrameItsWaitWasDue) await FlowTask.NextFrame();
                else if (queued == QueuedResume.AChildEnded) await Child();
                else await Flow.WithClock(World.UnscaledClock, Child());
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("closing");
                if (cleanupAwaitsAScope) await Fade();
                else await FlowTask.DelayFrames(3);
                Log.Add("closed");
            }
        }

        async FlowTask Menu()
        {
            await FlowTask.Race(FlowTask.NextFrame(), Screen());
            Log.Add("race returned");
        }

        async FlowTask Killer()
        {
            await FlowTask.NextFrame();
            screen.Cancel();
        }

        FlowHandle root;
        switch (queued)
        {
            case QueuedResume.ItLostARaceInTheFrameItsWaitWasDue:
                root = World.Run(Menu());
                Tick();
                break;
            case QueuedResume.AChildEnded:
                // The child's wait comes first in the frame, the killer's second.
                root = screen = World.Run(Screen());
                World.Run(Killer());
                Tick();
                break;
            default:
                root = screen = World.Run(Screen(), game);
                var pause = game.Pause();
                Tick(); // the child ends; the Pause holds the screen's resume
                screen.Cancel();
                Tick();
                pause.Dispose();
                break;
        }

        AssertLog("closing");
        Tick(2);
        AssertLog("closing");
        Assert.That(root.Status, Is.EqualTo(FlowStatus.Running));
        Tick(2);
        var expected = new List<string> { "closing" };
        if (cleanupAwaitsAScope) expected.Add("fade end");
        expected.Add("closed");
        if (queued == QueuedResume.ItLostARaceInTheFrameItsWaitWasDue) expected.Add("race returned");
        AssertLog(expected.ToArray());
        AssertNoExceptions();
        if (queued == QueuedResume.ItLostARaceInTheFrameItsWaitWasDue) AssertSucceeded(root);
        else AssertCanceled(root, CancelCause.Explicit);
    }

    [Test]
    public void AMethodWhoseEndIsHeldIsNotRunAgainByAResumeQueuedBeforeItsCancel()
    {
        // The screen loses a Race in the frame its own wait is due, so its resume is queued behind the winner's. The
        // unwinding runs its finally at once, which spawns a task that saves when it is unwound, and the screen's code ends;
        // its end waits for that task. The queued resume must not run the ended method again: the body runs once, nothing
        // is reported, and the Race returns once the task has saved.
        CaptureExceptions();
        var runs = 0;

        async FlowTask Screen()
        {
            runs++;
            try
            {
                await FlowTask.NextFrame();
                Log.Add("unreachable");
            }
            finally
            {
                Flow.Spawn(SavesWhenClosed("child", 3));
                Log.Add("screen finally");
            }
        }

        async FlowTask Menu()
        {
            await FlowTask.Race(FlowTask.NextFrame(), Screen());
            Log.Add("race returned");
        }

        var h = World.Run(Menu());
        Tick(6);
        Assert.That(runs, Is.EqualTo(1), "log: " + Log);
        AssertLog("screen finally", "child saving", "child saved", "race returned");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    // ------------------------------------------------------------------ the order of the cleanups

    [Test]
    public void ACanceledScopesChildrenFinishTheirCleanupBeforeItsOwnFinallyRuns()
    {
        // As through a call stack: the child the screen awaits (and the child that one awaits) and the child it spawned run
        // their finally blocks to their end, awaits included, before FlowCanceledException reaches the screen's own code.
        CaptureExceptions();

        async FlowTask Closing(string name, int frames, bool awaitsInner)
        {
            try
            {
                if (awaitsInner) await Closing("inner", 1, false);
                else await FlowTask.Never();
            }
            finally
            {
                Log.Add(name + " finally");
                await FlowTask.DelayFrames(frames);
                Log.Add(name + " finally end");
            }
        }

        async FlowTask Screen()
        {
            Flow.AddCleanup(() => Log.Add("screen cleanup"));
            try
            {
                Flow.Spawn(Closing("spawned", 4, false));
                await Closing("middle", 1, true);
            }
            finally
            {
                Log.Add("screen finally");
            }
        }

        var h = World.Run(Screen());
        Tick();
        h.Cancel();
        Tick(2);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Tick(3);
        AssertLog("inner finally", "spawned finally", "inner finally end", "middle finally", "middle finally end", "spawned finally end",
            "screen finally", "screen cleanup");
        AssertNoExceptions();
        AssertCanceled(h, CancelCause.Explicit);
    }

    [Test]
    public void AScopeCanceledWhileItRunsLetsItsChildrenFinishTheirCleanupBeforeItsFinally()
    {
        // The screen cancels itself from its own code while the child it spawned waits; the child saves in its finally. As
        // for a scope canceled while it waits, FlowCanceledException reaches the screen's code (at its next await) only once
        // the child has finished its cleanup.
        CaptureExceptions();
        FlowHandle self = default;

        async FlowTask Screen()
        {
            try
            {
                Flow.Spawn(SavesWhenClosed("child", 3));
                await FlowTask.NextFrame();
                self.Cancel();
                await FlowTask.NextFrame();
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("screen finally");
            }
        }

        self = World.Run(Screen());
        Tick(6);
        AssertLog("child saving", "child saved", "screen finally");
        AssertNoExceptions();
        AssertCanceled(self, CancelCause.Explicit);
    }

    // ------------------------------------------------------------------ World.Dispose

    async FlowTask Fade(string name)
    {
        Log.Add(name + " start");
        await FlowTask.DelayFrames(10);
        Log.Add(name + " end");
    }

    [Test]
    public void AtDisposeEachFinallyRunsUpToItsFirstAwaitAndNothingStarts([Values] bool canceledFirst)
    {
        // Dispose runs no Tick, so an await in a canceled scope's finally throws FlowCanceledException again: each block runs
        // up to its first await, whose task does not start, and the next block runs; so do the using statements and
        // AddCleanup. Nothing starts meanwhile: FlowWorld.Run returns a handle that has ended Canceled, and so does
        // Flow.Spawn. A flow canceled before, whose cleanup was running, ends so too and keeps its cause. Nothing is reported.
        CaptureExceptions();
        FlowHandle run = default, spawned = default;

        async FlowTask Screen()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            using var file = new Resource(Log, "file");
            try
            {
                try
                {
                    await FlowTask.Never();
                }
                finally
                {
                    Log.Add("inner finally");
                    await Fade("inner fade");
                    Log.Add("inner finally end");
                }
            }
            finally
            {
                Log.Add("outer finally");
                run = World.Run(Fade("run"));
                spawned = Flow.Spawn(Fade("spawn"));
                await Fade("outer fade");
                Log.Add("outer finally end");
            }
        }

        var h = World.Run(Screen());
        Tick();
        if (canceledFirst)
        {
            h.Cancel();
            Tick();
        }

        World.Dispose();
        if (canceledFirst) AssertLog("inner finally", "inner fade start", "outer finally", "file closed", "cleanup");
        else AssertLog("inner finally", "outer finally", "file closed", "cleanup");
        Assert.That(run.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(run.IsCompleted, Is.True);
        Assert.That(spawned.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(spawned.IsCompleted, Is.True);
        AssertNoExceptions();
        AssertCanceled(h, canceledFirst ? CancelCause.Explicit : CancelCause.WorldDisposed);
    }

    [Test]
    public void AtDisposeARetryLoopOfAwaitsInAFinallyEnds([Values] bool canceledFirst)
    {
        // The finally retries its upload until it succeeds, and its catch takes FlowCanceledException too. At Dispose each
        // attempt's await throws again and the catch retries: after 32 such throws the next await ends the scope there, so
        // the loop ends (the rest of the finally does not run; AddCleanup does) and nothing is reported.
        CaptureExceptions();
        var attempts = 0;

        async FlowTask Uploader()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                while (true)
                {
                    attempts++;
                    try
                    {
                        await Fade("upload");
                        break;
                    }
#pragma warning disable FLOW001 // on purpose: a retry loop whose catch takes FlowCanceledException too
                    catch (Exception)
#pragma warning restore FLOW001
                    {
                    }
                }

                Log.Add("finally end");
            }
        }

        var h = World.Run(Uploader());
        Tick();
        if (canceledFirst)
        {
            h.Cancel();
            Tick();
        }

        World.Dispose();
        Assert.That(attempts, Is.EqualTo(33));
        if (canceledFirst) AssertLog("upload start", "cleanup");
        else AssertLog("cleanup");
        AssertNoExceptions();
        AssertCanceled(h, canceledFirst ? CancelCause.Explicit : CancelCause.WorldDisposed);
    }

    [Test]
    public void DisposeEndsEveryFlowWhateverHoldsIt()
    {
        // Flows held in each way: one that waits, one whose cleanup never ends (there is no time limit: it holds the flow),
        // a parent whose end waits for its spawned child's cleanup, a Race that waits for its loser's cleanup, and a cleanup
        // on a paused clock. Dispose ends them all and empties the tree; nothing is reported.
        CaptureExceptions();
        var game = World.CreateClock("Game");
        var back = new Signal<FlowUnit>(World);

        async FlowTask Stuck()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.Never();
            }
        }

        async FlowTask Parent()
        {
            Flow.Spawn(SavesWhenClosed("child", 1000));
            await FlowTask.NextFrame();
        }

        async FlowTask Menu()
        {
            await FlowTask.Race(SavesWhenClosed("loser", 1000), back.Next());
            Log.Add("menu resumed");
        }

        var waiting = World.Run(SavesWhenClosed("waiting", 1000));
        var stuck = World.Run(Stuck());
        var parent = World.Run(Parent());
        var menu = World.Run(Menu());
        var paused = World.Run(SavesWhenClosed("paused", 1), game);
        Tick();
        stuck.Cancel();
        paused.Cancel();
        back.Emit(FlowUnit.Default);
        var pause = game.Pause();
        Tick(300);
        AssertLog("child saving", "paused saving", "loser saving");
        foreach (var h in new[] { waiting, stuck, parent, menu, paused }) Assert.That(h.Status, Is.EqualTo(FlowStatus.Running), h.ToString());
        Assert.That(stuck.CancelCause, Is.EqualTo(CancelCause.Explicit));

        World.Dispose();
        Assert.That(World.Diagnostics.Root.Children, Is.Empty, World.Dump());
        AssertLog("child saving", "paused saving", "loser saving", "waiting saving");
        AssertNoExceptions();
        AssertCanceled(waiting, CancelCause.WorldDisposed);
        AssertCanceled(stuck, CancelCause.Explicit);
        AssertCanceled(menu, CancelCause.WorldDisposed);
        AssertCanceled(paused, CancelCause.Explicit);
        // The parent's code had returned: its end, decided then, only waited for its child.
        AssertSucceeded(parent);
        Assert.That(parent.CancelCause, Is.EqualTo(CancelCause.WorldDisposed));
        pause.Dispose();
    }
}
