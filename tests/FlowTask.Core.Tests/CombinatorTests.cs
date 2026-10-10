using System.Reflection;

namespace Katout.FlowTask.Tests;

/// <summary>Composition.</summary>
public class CombinatorTests : FlowTestBase
{
    async FlowTask<int> After(double seconds, int value, string name = null)
    {
        try
        {
            await FlowTask.WaitForSeconds(seconds);
            return value;
        }
        finally
        {
            if (name != null) Log.Add(name + " finally");
        }
    }

    [Test]
    public void SequenceIsPlainAwait()
    {
        async FlowTask Seq()
        {
            Log.Add("1:" + await After(0.01, 1));
            Log.Add("2:" + await After(0.01, 2));
        }

        World.Run(Seq());
        TickFor(0.1);
        AssertLog("1:1", "2:2");
    }

    [Test]
    public void WhenAllReturnsAllValues()
    {
        async FlowTask Root()
        {
            var (a, b, c) = await FlowTask.WhenAll(After(0.03, 1), After(0.01, 2), After(0.02, 3));
            Log.Add($"{a},{b},{c}");
            var arr = await FlowTask.WhenAll(new[] { After(0.01, 4), After(0.02, 5) });
            Log.Add(string.Join(",", arr));
            await FlowTask.WhenAll(FlowTask.WaitForSeconds(0.01), FlowTask.NextFrame());
            Log.Add("void done");
        }

        World.Run(Root());
        TickFor(0.2);
        AssertLog("1,2,3", "4,5", "void done");
    }

    // ------------------------------------------------------------------ WhenAll failures

    [Test]
    public void AnExceptionEndsWhenAllOnceTheOtherBranchesHaveUnwound()
    {
        // The other branches are canceled (Fault) and unwound, later-started first, before the exception is thrown at
        // the await.
        async FlowTask<int> Thrower()
        {
            await FlowTask.WaitForSeconds(0.02);
            throw new InvalidOperationException("x");
        }

        async FlowTask Root()
        {
            try
            {
                await FlowTask.WhenAll(After(1, 1, "a"), Thrower(), After(1, 2, "b"));
            }
            catch (InvalidOperationException e)
            {
                Log.Add("caught " + e.Message);
            }
        }

        World.Run(Root());
        TickFor(0.1);
        AssertLog("b finally", "a finally", "caught x");
    }

    [Test]
    public void FailuresReturnedAsValuesCanStopTheOtherBranchesThroughOnceAndRace()
    {
        // Without exceptions: a failing branch puts its failure in a Once and waits; the Race against the Once settles at
        // the first failure and unwinds the other branches. A second failure in the same frame reports nothing.
        CaptureExceptions();

        async FlowTask<int> Load(Once<string> failed, double seconds, string error, int value)
        {
            try
            {
                await FlowTask.WaitForSeconds(seconds);
                if (error == null) return value;
                if (!failed.IsSet) failed.Set(error);
                await FlowTask.Never();
                return 0;
            }
            finally
            {
                Log.Add("end " + (error ?? "ok"));
            }
        }

        async FlowTask Root()
        {
            var failed = new Once<string>();
            var r = await FlowTask.Race(
                FlowTask.WhenAll(Load(failed, 0.05, null, 1), Load(failed, 0.02, "map", 0), Load(failed, 0.02, "units", 0)),
                failed.Wait());
            Log.Add(r.Index == 0 ? "loaded" : "failed " + r.Value1);
        }

        World.Run(Root());
        TickFor(0.1);
        AssertLog("end units", "end map", "end ok", "failed map");
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void WhenAllWithNoBranchesCompletesAtOnce()
    {
        async FlowTask Root()
        {
            var values = await FlowTask.WhenAll(Array.Empty<FlowTask<int>>());
            Log.Add("values " + values.Length);
            await FlowTask.WhenAll(Array.Empty<FlowTask>());
            Log.Add("void done");
        }

        World.Run(Root());
        AssertLog("values 0", "void done");
        Assert.Throws<ArgumentNullException>(() => FlowTask.WhenAll<int>(null));
    }

    [Test]
    public void WhenAllBranchesAreUnwoundWithTheCaller()
    {
        async FlowTask Root()
        {
            await FlowTask.WhenAll(After(1, 1, "a"), After(1, 2, "b"));
            Log.Add("unreachable");
        }

        var h = World.Run(Root());
        Tick();
        Assert.That(World.Dump(), Does.Contain("WhenAll (combinator)").And.Contain("waiting: WhenAll 2/2 branches"));
        h.Cancel();
        Tick();
        AssertLog("b finally", "a finally");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
    }

    // ------------------------------------------------------------------ Race

    [Test]
    public void RaceReturnsTypedWinnerAndUnwindsLosersBeforeResuming()
    {
        var sig = new Signal<string>();

        async FlowTask Root()
        {
            var r = await FlowTask.Race(After(1, 1, "slow"), sig.Next());
            Log.Add("index=" + r.Index);
            if (r.TryGet1(out var s)) Log.Add("value=" + s);
        }

        World.Run(Root());
        Tick();
        sig.Emit("hello");
        Tick();
        AssertLog("slow finally", "index=1", "value=hello");
    }

    [Test]
    public void AChildSpawnedInALoserInTheFrameTheRaceIsDecidedTakesTheLosersCause()
    {
        // The winner cancels the loser (RaceLost) and its subtree, with the child the loser spawned in the same Tick: the
        // child is canceled with its branch's cause, not by a parent that returned.
        FlowHandle child = default;

        async FlowTask Telemetry()
        {
            await FlowTask.NextFrame();
            Log.Add("sent");
        }

        async FlowTask<int> Loser()
        {
            await FlowTask.NextFrame();
            child = Flow.Spawn(Telemetry());
            await FlowTask.Never();
            return 1;
        }

        async FlowTask<int> Winner()
        {
            await FlowTask.NextFrame();
            return 2;
        }

        async FlowTask Root()
        {
            var r = await FlowTask.Race(Loser(), Winner());
            Log.Add("result " + r);
        }

        World.Run(Root());
        Tick(3);
        AssertLog("result Race[1] = 2");
        Assert.That(child.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(child.CancelCause, Is.EqualTo(CancelCause.RaceLost));
    }

    [Test]
    public void ACleanupExceptionInALoserKeepsTheRacesResult()
    {
        // The winner ends the Race; the loser's finally throws while it is unwound. That is a cleanup exception, not the
        // result: the caller still receives the winner, and its flow goes on.
        CaptureExceptions();

        async FlowTask<int> ThrowsInFinally()
        {
            try
            {
                await FlowTask.Never();
                return 1;
            }
            finally
            {
                Log.Add("loser finally");
                throw new InvalidOperationException("cleanup boom");
            }
        }

        async FlowTask<int> Winner()
        {
            await FlowTask.NextFrame();
            return 2;
        }

        async FlowTask Root()
        {
            var r = await FlowTask.Race(ThrowsInFinally(), Winner());
            Log.Add("result " + r);
            await FlowTask.NextFrame();
            Log.Add("root goes on");
        }

        var h = World.Run(Root());
        Tick(2);
        AssertLog("loser finally", "result Race[1] = 2", "root goes on");
        Assert.That(Exceptions.Select(p => p.Kind), Is.EqualTo(new[] { FlowExceptionKind.Cleanup }));
        Assert.That(Exceptions[0].Exception.Message, Is.EqualTo("cleanup boom"));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ACombinatorWaitsForTheCleanupOfTheBranchesItCancels([Values] bool race)
    {
        // Once FlowCanceledException has reached the canceled branch's finally, its awaits run as a live scope's do. The
        // Race it lost, or the WhenAll that failed around it, resumes its caller only after that cleanup has ended.
        async FlowTask Canceled()
        {
            try
            {
                await FlowTask.WaitForSeconds(10);
            }
            finally
            {
                Log.Add("finally start");
                await Cleanup();
                Log.Add("finally end");
            }
        }

        async FlowTask Cleanup()
        {
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("cleanup done");
        }

        async FlowTask Throws()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("x");
        }

        async FlowTask Root()
        {
            try
            {
                if (race) Log.Add("race won " + (await FlowTask.Race(Canceled(), FlowTask.NextFrame())).Index);
                else await FlowTask.WhenAll(Canceled(), Throws());
            }
            catch (InvalidOperationException e)
            {
                Log.Add("caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        Tick();
        AssertLog("finally start");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        TickFor(0.2);
        AssertLog("finally start", "cleanup done", "finally end", race ? "race won 1" : "caught x");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ALoserWhoseEndWaitsForACleanupItStartedIsNotResumedAgain()
    {
        // C ends and queues Loser's resume; in the same flush the NextFrame wins, and the Race unwinds Loser at its await.
        // Loser's finally spawns a cleanup that takes a frame, which holds Loser's end after its code has returned. Its
        // queued resume comes after that: a method whose code has ended is never resumed again (its body would start
        // over).
        async FlowTask C() => await FlowTask.NextFrame();

        async FlowTask SlowCleanup()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.NextFrame();
                Log.Add("cleanup done");
            }
        }

        async FlowTask Loser()
        {
            Log.Add("loser start");
            try
            {
                await C();
            }
            finally
            {
                Log.Add("loser finally");
                Flow.Spawn(SlowCleanup());
            }
        }

        async FlowTask Root() => Log.Add("race won " + (await FlowTask.Race(Loser(), FlowTask.NextFrame())).Index);

        var h = World.Run(Root());
        Tick(3);
        AssertLog("loser start", "loser finally", "cleanup done", "race won 1");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ARaceReleasedWhileItStartsItsBranchesStartsNoOtherBranch()
    {
        // The inner Race wins at once as it starts its first branch. That settles the Race around it while the inner one's
        // loop over its branches is still running: the inner Race goes back to its pool, and the finally of the loser
        // that the outer Race unwinds rents it again for a Race of its own. The inner loop must not go on with the
        // branches of that new Race, which start when it is awaited.
        FlowTask<RaceResult<FlowUnit, FlowUnit>> reused = default;

        async FlowTask Loser()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                reused = FlowTask.Race(FlowTask.Never(), FlowTask.NextFrame());
            }
        }

        async FlowTask Root()
        {
            await FlowTask.Race(Loser(), FlowTask.Race(FlowTask.CompletedTask, FlowTask.Never()).WithoutResult());
            Log.Add("outer done");
            Log.Add("reused won " + (await reused).Index);
        }

        var h = World.Run(Root());
        AssertLog("outer done");
        Tick();
        AssertLog("outer done", "reused won 1");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ACombinatorWhoseBranchCannotStartThrowsAtTheAwaitAndUnwindsTheBranchesItStarted()
    {
        // The second branch waits on a clock of another World, which its start refuses. The Race ends, the first branch
        // is unwound and runs its cleanup to its end, and the exception is thrown at the await. The Race stays in the
        // tree until that cleanup has ended, and the caller's end waits for it.
        using var other = new FlowWorld("Other");

        async FlowTask Started()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("started finally");
                await FlowTask.NextFrame();
                Log.Add("started cleaned");
            }
        }

        async FlowTask Root()
        {
            try
            {
                await FlowTask.Race(Started(), FlowTask.WaitForSeconds(1, other.DefaultClock));
            }
            catch (FlowMisuseException e)
            {
                Log.Add("caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        AssertLog("started finally", "caught Clock 'Default' belongs to another World.");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Tick();
        AssertLog("started finally", "caught Clock 'Default' belongs to another World.", "started cleaned");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ATaskPassedToTwoCombinatorsStaysWithTheOneThatStartedIt([Values] bool discard)
    {
        // The second combinator's start finds the task started by the first: it throws at its await and leaves the task
        // alone, so the first one still receives its end.
        FlowHandle first = default;

        async FlowTask Root()
        {
            var t = After(1, 7, "t");
            var r1 = discard ? t.WithoutResult() : FlowTask.Race(t, FlowTask.Never()).WithoutResult();
            var r2 = discard ? t.WithoutResult() : FlowTask.Race(t, FlowTask.Never()).WithoutResult();
            first = Flow.Spawn(r1);
            try
            {
                await r2;
            }
            catch (FlowMisuseException)
            {
                Log.Add("misuse");
            }

            await FlowTask.WaitForSeconds(2);
            Log.Add("first " + first.Status);
        }

        var h = World.Run(Root());
        AssertLog("misuse");
        for (var i = 0; i < 300 && h.Status == FlowStatus.Running; i++) Tick();
        AssertLog("misuse", "t finally", "first Succeeded");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ATaskPassedTwiceToOneCombinatorThrowsAtItsAwaitAndIsUnwound()
    {
        async FlowTask Root()
        {
            var t = After(1, 7, "t");
            try
            {
                await FlowTask.WhenAll(t, t);
            }
            catch (FlowMisuseException)
            {
                Log.Add("misuse");
            }
        }

        var h = World.Run(Root());
        AssertLog("t finally", "misuse");
        TickFor(2);
        AssertLog("t finally", "misuse");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    // Used by this test only, so that its state machine has a pool of its own.
    async FlowTask<int> ReusedBranch(int v)
    {
        Log.Add("run " + v);
        await FlowTask.NextFrame();
        return v;
    }

    [Test]
    public void ACombinatorDoesNotStartTheTaskThatReusedTheNodeOfItsBranch([Values] bool discard)
    {
        // The first Race consumes t, and its node goes back to its pool; u rents it. The second combinator, made before,
        // still holds t: its start must throw, not start u (this relies on the pools being LIFO, as the stale-copy tests
        // in FlowTaskTests do).
        CaptureExceptions();

        async FlowTask Root()
        {
            var t = ReusedBranch(1);
            var r1 = FlowTask.Race(t, FlowTask.Never());
            var r2 = discard ? t.WithoutResult() : FlowTask.Race(t, FlowTask.Never()).WithoutResult();
            Log.Add("first " + (await r1).Index);
            var u = ReusedBranch(2);
            try
            {
                await r2;
            }
            catch (FlowMisuseException)
            {
                Log.Add("misuse");
            }

            Log.Add("u " + await u);
        }

        var h = World.Run(Root());
        for (var i = 0; i < 5 && h.Status == FlowStatus.Running; i++) Tick();
        AssertLog("run 1", "first 0", "misuse", "run 2", "u 2");
        Assert.That(Exceptions, Is.Empty);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void HomogeneousRaceReturnsIndexAndValue()
    {
        async FlowTask Root()
        {
            var r = await FlowTask.Race(After(0.03, 10), After(0.01, 20), After(0.02, 30));
            Log.Add($"{r.Index}:{r.Value1}");
            var idx = await FlowTask.Race(new[] { FlowTask.WaitForSeconds(0.05), FlowTask.WaitForSeconds(0.01), FlowTask.WaitForSeconds(1), FlowTask.WaitForSeconds(2), FlowTask.WaitForSeconds(3) });
            Log.Add("idx=" + idx);
        }

        World.Run(Root());
        TickFor(0.2);
        AssertLog("1:20", "idx=1");
    }

    [Test]
    public void EachBranchRunsInItsOwnChildScope()
    {
        async FlowTask Branch(string name)
        {
            Flow.AddCleanup(() => Log.Add(name + " cleanup"));
            await FlowTask.WaitForSeconds(name == "fast" ? 0.01 : 1);
        }

        async FlowTask Root()
        {
            await FlowTask.Race(Branch("slow"), Branch("fast"));
            Log.Add("after race");
        }

        World.Run(Root());
        var race = World.Diagnostics.Walk().Single(s => s.Name == "Race");
        Assert.That(race.Children.Select(c => c.Name), Is.EqualTo(new[] { "Branch", "Branch" }));
        TickFor(0.1);
        AssertLog("fast cleanup", "slow cleanup", "after race");
    }

    [Test]
    public void NoWhenAnyOrTimeoutHelpers()
    {
        var names = typeof(FlowTask).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).ToArray();
        Assert.That(names, Has.None.EqualTo("WhenAny"));
        Assert.That(names, Has.None.EqualTo("Timeout"));
    }

    [Test]
    public void TimeoutIsComposedWithRace()
    {
        async FlowTask Root()
        {
            var r = await FlowTask.Race(After(5, 1, "work"), FlowTask.WaitForSeconds(0.1));
            Log.Add(r.Index == 1 ? "timed out" : "finished");
        }

        World.Run(Root());
        TickFor(0.3);
        AssertLog("work finally", "timed out");
    }

    // ------------------------------------------------------------------ waits

    [Test]
    public void WaitsUseTheScopeClockByDefault()
    {
        var slow = World.CreateClock("Slow");
        slow.TimeScale = 0.5;

        async FlowTask Root()
        {
            await FlowTask.WaitForSeconds(1.0);
            Log.Add("default 1s at " + World.UnscaledClock.Time.ToString("0.00"));
        }

        async FlowTask OnSlow()
        {
            await FlowTask.WaitForSeconds(1.0);
            Log.Add("slow 1s at " + World.UnscaledClock.Time.ToString("0.00"));
        }

        async FlowTask Explicit()
        {
            await FlowTask.WaitForSeconds(1.0, World.UnscaledClock);
            Log.Add("explicit unscaled 1s at " + World.UnscaledClock.Time.ToString("0.00"));
        }

        World.Run(Root());
        World.Run(Flow.WithClock(slow, OnSlow()));
        World.Run(Flow.WithClock(slow, Explicit()));
        TickFor(2.5, 0.25);
        AssertLog("default 1s at 1.00", "explicit unscaled 1s at 1.00", "slow 1s at 2.00");
    }

    [Test]
    public void DelayFramesCountsTicksExceptPausedOnes()
    {
        async FlowTask Root()
        {
            await FlowTask.DelayFrames(3);
            Log.Add("3 frames at tick " + World.DefaultClock.FrameCount);
        }

        World.Run(Root());
        Tick();
        var pause = World.DefaultClock.Pause();
        Tick(5);
        pause.Dispose();
        Tick();
        AssertLog();
        Tick();
        AssertLog("3 frames at tick 3");
    }

    [Test]
    public void WaitUntilChecksImmediatelyThenEveryTick()
    {
        var flag = false;

        async FlowTask Root()
        {
            await FlowTask.WaitUntil(() => true);
            Log.Add("immediate");
            await FlowTask.WaitUntil(() => flag);
            Log.Add("flag");
        }

        World.Run(Root());
        AssertLog("immediate");
        Tick(3);
        flag = true;
        Tick();
        AssertLog("immediate", "flag");
    }

    sealed class Counter
    {
        public int Value;
    }

    [Test]
    public void WaitUntilWithStateAvoidsClosures()
    {
        var counter = new Counter();

        async FlowTask Loop()
        {
            while (true)
            {
                await FlowTask.WaitUntil(counter, c => c.Value >= 0);
                await FlowTask.NextFrame();
            }
        }

        World.Run(Loop());
        AssertSteadyStateAllocationFree(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                counter.Value++;
                World.Tick(Dt);
            }
        });
    }

    static async FlowTask<int> SyncOne() => 1;

    [Test]
    public void AnEndlessSynchronousLoopIsEndedAtItsAwaitWithAMisuse()
    {
        // A scope whose awaits all complete synchronously would freeze the game: its 1,000,001st synchronous completion
        // in one Tick ends it at that await with a FlowMisuseException for its consumer. Awaits of completed values
        // (CompletedTask, FromResult) go on at once and are not counted. The loop stops by itself at 2,000,000, so that a
        // broken limit fails the test instead of hanging it.
        CaptureExceptions();
        var iterations = 0;

        async FlowTask Spin()
        {
            while (iterations < 2_000_000)
            {
                iterations++;
                await FlowTask.CompletedTask;
                await FlowTask.FromResult(0);
                await SyncOne();
            }
        }

        var h = World.Run(Spin());
        Assert.That(iterations, Is.EqualTo(1_000_001));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(h.Exception, Is.TypeOf<FlowMisuseException>().And.Message.Contains("synchronously in one Tick"));
        Assert.That(Exceptions.Select(p => p.Kind), Is.EqualTo(new[] { FlowExceptionKind.Unhandled }));
    }

    [Test]
    public void TheSynchronousCompletionCountStartsOverInEveryTick()
    {
        // The limit counts one Tick: a scope that completes 600,000 awaits synchronously, waits for the next frame and
        // completes 600,000 more, 1,200,000 in all, stays under it.
        CaptureExceptions();
        var completed = 0;

        async FlowTask Loader()
        {
            for (var i = 0; i < 600_000; i++) completed += await SyncOne();
            await FlowTask.NextFrame();
            for (var i = 0; i < 600_000; i++) completed += await SyncOne();
        }

        var h = World.Run(Loader());
        Assert.That(completed, Is.EqualTo(600_000));
        Tick();
        Assert.That(completed, Is.EqualTo(1_200_000));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void TheSynchronousCompletionCountIsPerScope()
    {
        // The limit counts one scope: two flows that complete 600,000 awaits synchronously each in the same Tick,
        // 1,200,000 in all, both stay under it.
        CaptureExceptions();
        var completed = 0;

        async FlowTask Loader()
        {
            await FlowTask.NextFrame();
            for (var i = 0; i < 600_000; i++) completed += await SyncOne();
        }

        var first = World.Run(Loader());
        var second = World.Run(Loader());
        Tick();
        Assert.That(completed, Is.EqualTo(1_200_000));
        Assert.That(first.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(second.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(Exceptions, Is.Empty);
    }
}
