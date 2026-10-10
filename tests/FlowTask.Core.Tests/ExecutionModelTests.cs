namespace Katout.FlowTask.Tests;

/// <summary>The execution model: the lifecycle, the steps of a Tick and the execution-order rules.</summary>
public class ExecutionModelTests : FlowTestBase
{
    // ------------------------------------------------------------------ lifecycle

    [Test]
    public void Lifecycle_UnstartedRunningSucceeded()
    {
        var sig = new Signal<int>();

        async FlowTask<int> Work() => await sig.Next();

        var task = Work();
        Assert.That(task.Status, Is.EqualTo(FlowStatus.Unstarted));
        var h = World.Run(task);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        sig.Emit(1);
        Tick();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void Lifecycle_CanceledRecordsTheCause()
    {
        FlowHandle parentEnded = default, raceLost = default, faulted = default, explicitCancel = default;

        async FlowTask Forever() => await FlowTask.Never();

        async FlowTask ParentEnds()
        {
            parentEnded = Flow.Spawn(Forever());
            await FlowTask.NextFrame();
        }

        async FlowTask Loser()
        {
            raceLost = Flow.Spawn(Forever());
            await FlowTask.Never();
        }

        async FlowTask Thrower()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("x");
        }

        async FlowTask FaultHost()
        {
            faulted = Flow.Spawn(Forever());
            await Thrower();
        }

        async FlowTask Root()
        {
            await ParentEnds();
            await FlowTask.Race(Loser(), FlowTask.NextFrame());
            try
            {
                await FaultHost();
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                Log.Add("caught " + e.Message);
            }

            explicitCancel = Flow.Spawn(Forever());
            explicitCancel.Cancel();
        }

        World.Run(Root());
        Tick(8);
        AssertLog("caught x");
        Assert.That(parentEnded.CancelCause, Is.EqualTo(CancelCause.ParentEnded));
        Assert.That(raceLost.CancelCause, Is.EqualTo(CancelCause.RaceLost));
        Assert.That(faulted.CancelCause, Is.EqualTo(CancelCause.Fault));
        Assert.That(explicitCancel.CancelCause, Is.EqualTo(CancelCause.Explicit));
        Assert.That(new[] { parentEnded, raceLost, faulted, explicitCancel }.Select(h => h.Status),
            Is.All.EqualTo(FlowStatus.Canceled));
    }

    // ------------------------------------------------------------------ Tick steps

    [Test]
    public void TickOrder_IntakeThenClocksThenTimeWaitsThenFlush()
    {
        var main = new Signal<string>(World);
        var cross = new Signal<string>(World);

        async FlowTask OnSignal(Signal<string> s) => Log.Add(await s.Next());

        async FlowTask OnDelay()
        {
            await FlowTask.WaitForSeconds(Dt);
            Log.Add("delay");
        }

        World.Run(OnDelay());
        World.Run(OnSignal(cross));
        World.Run(OnSignal(main));
        TestThreads.WaitOrFail(Task.Run(() => cross.EmitFromAnyThread("intake")));
        main.Emit("main");
        Tick();
        AssertLog("main", "intake", "delay");
    }

    [Test]
    public void TickOrder_FlushDoesNotEvaluateTimeWaits()
    {
        async FlowTask Root()
        {
            await FlowTask.NextFrame();
            Log.Add("frame");
        }

        World.Run(Root());
        World.Flush();
        World.Flush();
        AssertLog();
        Tick();
        AssertLog("frame");
    }

    // ------------------------------------------------------------------ execution-order rules

    [Test]
    public void TickListKeepsRegistrationOrderThroughRemovalsAndGrowth()
    {
        async FlowTask Wait(int i)
        {
            // Even flows wait in the tick list themselves, odd ones through a wait node: one list, one order.
            await FlowTask.NextFrame(i % 2 == 0 ? null : World.DefaultClock);
            Log.Add("w" + i);
        }

        var handles = new System.Collections.Generic.List<FlowHandle>();
        for (var i = 0; i < 16; i++) handles.Add(World.Run(Wait(i)));
        for (var i = 0; i < 16; i += 3) handles[i].Cancel();
        World.Flush(); // unwinds the canceled flows: their entries leave the tick list
        for (var i = 16; i < 40; i++) handles.Add(World.Run(Wait(i)));
        Tick();
        var expected = Enumerable.Range(0, 40).Where(i => i >= 16 || i % 3 != 0).Select(i => "w" + i);
        Assert.That(Log.Entries, Is.EqualTo(expected));
    }

    [Test]
    public void ResumesAreFifo()
    {
        var a = new Signal<int>();
        var b = new Signal<int>();

        async FlowTask Waiter(Signal<int> s, string n)
        {
            await s.Next();
            Log.Add(n);
            if (n == "a1") b.Emit(0); // reserved at the tail, after a2/a3
        }

        World.Run(Waiter(a, "a1"));
        World.Run(Waiter(a, "a2"));
        World.Run(Waiter(b, "b1"));
        World.Run(Waiter(a, "a3"));
        a.Emit(0);
        Tick();
        AssertLog("a1", "a2", "a3", "b1");
    }

    [Test]
    public void NextFrameRegisteredDuringAFlushWaitsForTheNextTick()
    {
        var sig = new Signal<int>();

        async FlowTask Root()
        {
            await sig.Next();
            Log.Add("signal at frame " + World.DefaultClock.FrameCount);
            await FlowTask.NextFrame();
            Log.Add("next frame at " + World.DefaultClock.FrameCount);
            await FlowTask.DelayFrames(2);
            Log.Add("2 frames at " + World.DefaultClock.FrameCount);
        }

        World.Run(Root());
        sig.Emit(0);
        Tick(4);
        AssertLog("signal at frame 1", "next frame at 2", "2 frames at 4");
    }

    [Test]
    public void FirstProcessedCompletionWinsAndLosersUnwindBeforeTheCaller()
    {
        var a = new Signal<string>();
        var b = new Signal<string>();

        async FlowTask<string> Branch(Signal<string> s)
        {
            try
            {
                return await s.Next();
            }
            finally
            {
                Log.Add("finally " + (s == a ? "a" : "b"));
            }
        }

        async FlowTask Root()
        {
            var r = await FlowTask.Race(Branch(a), Branch(b));
            Log.Add("winner " + r.Index);
        }

        World.Run(Root());
        b.Emit("b"); // reserved first
        a.Emit("a");
        Tick();
        AssertLog("finally b", "finally a", "winner 1");
    }

    [Test]
    public void TimeWaitsSatisfiedInOneTickResolveInRegistrationOrder()
    {
        // One large dt passes both deadlines in the same step 3, which evaluates waits in registration order,
        // so the first argument wins although its deadline is later. Engines clamp large dt when that matters.
        async FlowTask Root()
        {
            var r = await FlowTask.Race(FlowTask.WaitForSeconds(30), FlowTask.WaitForSeconds(20));
            Log.Add("winner " + r.Index);
        }

        World.Run(Root());
        World.Tick(60);
        AssertLog("winner 0");
    }

    [Test]
    public void WhenAllUnwindsRemainingBranchesBeforePropagating()
    {
        // b throws: c is canceled and unwound, and the exception is thrown at the await of the WhenAll once c has ended,
        // the await of its finally included.
        async FlowTask Branch(string name, double seconds, bool fails)
        {
            try
            {
                await FlowTask.WaitForSeconds(seconds);
                if (fails) throw new InvalidOperationException(name + " failed");
                Log.Add(name + " done");
            }
            finally
            {
                await FlowTask.NextFrame();
                Log.Add(name + " finally");
            }
        }

        async FlowTask Root()
        {
            try
            {
                await FlowTask.WhenAll(Branch("a", 0.01, false), Branch("b", 0.02, true), Branch("c", 1, false));
                Log.Add("unreachable");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("caught " + e.Message);
            }
        }

        World.Run(Root());
        TickFor(0.2);
        AssertLog("a done", "a finally", "b finally", "c finally", "caught b failed");
    }

    [Test]
    public void UnwindOrderIsDescendantsFirstLaterSiblingsFirstFinallyThenCleanupLifo()
    {
        async FlowTask Grandchild()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("grandchild finally");
            }
        }

        async FlowTask Child(string name, bool nested)
        {
            Flow.AddCleanup(() => Log.Add(name + " cleanup"));
            try
            {
                if (nested) await Grandchild();
                else await FlowTask.Never();
            }
            finally
            {
                Log.Add(name + " finally");
            }
        }

        async FlowTask Root()
        {
            Flow.AddCleanup(() => Log.Add("root cleanup 1"));
            Flow.AddCleanup(() => Log.Add("root cleanup 2"));
            Flow.Spawn(Child("c1", true));
            Flow.Spawn(Child("c2", false));
            try
            {
                try
                {
                    await FlowTask.Never();
                }
                finally
                {
                    Log.Add("root inner finally");
                }
            }
            finally
            {
                Log.Add("root outer finally");
            }
        }

        var h = World.Run(Root());
        h.Cancel();
        Tick();
        AssertLog(
            "c2 finally", "c2 cleanup",
            "grandchild finally", "c1 finally", "c1 cleanup",
            "root inner finally", "root outer finally", "root cleanup 2", "root cleanup 1");
    }

    [Test]
    public void PausedResumesKeepTheirOriginalOrder()
    {
        var game = World.CreateClock("Game");
        var sig = new Signal<int>();

        async FlowTask Waiter(string n)
        {
            await sig.Next();
            Log.Add(n);
        }

        World.Run(Waiter("g1"), game);
        World.Run(Waiter("free"));
        World.Run(Waiter("g2"), game);
        var pause = game.Pause();
        sig.Emit(0);
        Tick();
        AssertLog("free");
        pause.Dispose();
        World.Flush();
        AssertLog("free", "g1", "g2");
    }

    // Resumes released from a pause go to the front of the queue (the steps of a Tick in docs/en/advanced/execution-model.md). The test above
    // releases with an empty queue, where front and back are the same; here a resume is already queued when the pause
    // ends.
    [Test]
    public void ResumesReleasedFromAPauseRunBeforeLaterReservations()
    {
        var game = World.CreateClock("Game");
        var sig = new Signal<int>();
        var go = new Signal<int>();
        var other = new Signal<int>();
        var pause = default(ScopedHandle);

        async FlowTask Paused(string n)
        {
            await sig.Next();
            Log.Add(n);
        }

        async FlowTask Free()
        {
            await other.Next();
            Log.Add("free");
        }

        async FlowTask Releaser()
        {
            await go.Next();
            other.Emit(0); // reserves free's resume at the back of the queue
            pause.Dispose(); // g1 and g2 go in front of it
            Log.Add("released");
        }

        World.Run(Paused("g1"), game);
        World.Run(Paused("g2"), game);
        World.Run(Free());
        World.Run(Releaser());
        pause = game.Pause(); // outside a flow: disposed by hand
        sig.Emit(0);
        Tick();
        AssertLog();
        go.Emit(0);
        Tick();
        AssertLog("released", "g1", "g2", "free");
    }

    [Test]
    public void FlushLimitBreaksLivelocks()
    {
        // Two flows that resume each other without end: a flush stops after 65,536 resumes and leaves the rest to the
        // next one. The FlushLimit warning comes once per World.
        const int limit = 65536;
        var ping = new Signal<int>();
        var pong = new Signal<int>();
        var count = 0;

        async FlowTask Player(Signal<int> me, Signal<int> other, bool serve)
        {
            if (serve) other.Emit(0);
            // Bounded, so that a broken flush limit fails the assertions below instead of hanging the run.
            for (var n = 0; n < 2 * limit; n++)
            {
                await me.Next();
                count++;
                other.Emit(0);
            }
        }

        World.Run(Player(ping, pong, false));
        World.Run(Player(pong, ping, true));
        Tick();
        Assert.That(count, Is.EqualTo(limit));
        Tick();
        Assert.That(count, Is.EqualTo(2 * limit));
        Assert.That(Warnings.Select(w => w.Kind), Is.EqualTo(new[] { FlowWarningKind.FlushLimit }));
    }

    [Test]
    public void CancelInsideAFlushUnwindsOnTheSpot()
    {
        async FlowTask Victim()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("victim finally");
            }
        }

        async FlowTask Root()
        {
            var v = Flow.Spawn(Victim());
            await FlowTask.NextFrame();
            v.Cancel();
            Log.Add("after cancel");
        }

        World.Run(Root());
        Tick();
        AssertLog("victim finally", "after cancel");
    }

    [Test]
    public void CancelOutsideAFlushUnwindsAtTheHeadOfTheNextFlush()
    {
        // The cancellation is confirmed at once (the dump shows it), so the resume the victim already had reserved does
        // not run: the victim unwinds at the head of the next flush, before the other resumes.
        var sig = new Signal<int>();

        async FlowTask Victim()
        {
            try
            {
                await sig.Next();
                Log.Add("victim resumed");
            }
            finally
            {
                Log.Add("victim finally");
            }
        }

        async FlowTask Other()
        {
            await sig.Next();
            Log.Add("other resumed");
        }

        var victim = World.Run(Victim());
        World.Run(Other());
        sig.Emit(0);
        victim.Cancel();
        Assert.That(victim.Status, Is.EqualTo(FlowStatus.Running), "confirmed, not yet unwound");
        Assert.That(World.Dump(), Does.Contain("Victim (scope) [Default] <canceling: Explicit>"));
        AssertLog();
        Tick();
        AssertLog("victim finally", "other resumed");
        Assert.That(victim.Status, Is.EqualTo(FlowStatus.Canceled));
    }
}

/// <summary>Debugging and observability.</summary>
public class DiagnosticsTests : FlowTestBase
{
    [Test]
    public void DumpShowsNamesWaitTargetsClocksAndDurations()
    {
        var ui = World.CreateClock("UI", World.UnscaledClock);

        async FlowTask Battle() => await FlowTask.WaitForSeconds(5);

        async FlowTask InGame() => await Battle();

        async FlowTask Menu() => await FlowTask.Never();

        World.Run(InGame());
        World.Run(Flow.WithClock(ui, Menu()));
        TickFor(1.0);
        var dump = World.Dump();
        TestContext.Out.WriteLine(dump);
        Assert.That(dump, Does.Contain("InGame (scope) [Default] waiting: Battle for 1"));
        Assert.That(dump, Does.Contain("Battle (scope) [Default] waiting: WaitForSeconds(5s) on Default, 4s left"));
        Assert.That(dump, Does.Contain("Menu (scope) [UI] waiting: Never"));
    }

    [Test]
    public void UnhandledCleanupAndSwallowedCancellationCarryScopePaths()
    {
        CaptureExceptions();

        async FlowTask Deep()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                throw new Exception("cleanup failed");
            }
        }

        async FlowTask Outer() => await Deep();

        var h = World.Run(Outer());
        h.Cancel();
        Tick();
        Assert.That(Exceptions.Single().Kind, Is.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(Exceptions.Single().ScopePath, Is.EqualTo("Outer > Deep"));
    }

    [Test]
    public void FlowCanceledExceptionThrowSitesAreHiddenFromTheDebugger()
    {
        var methods = new[]
        {
            typeof(FlowTask.Awaiter).GetMethod(nameof(FlowTask.Awaiter.GetResult)),
            typeof(FlowTask<int>.Awaiter).GetMethod(nameof(FlowTask<int>.Awaiter.GetResult)),
        };
        foreach (var m in methods)
            Assert.That(m!.GetCustomAttributes(typeof(System.Diagnostics.DebuggerHiddenAttribute), false), Is.Not.Empty, m.DeclaringType + "." + m.Name);
        Assert.That(typeof(FlowCanceledException).GetCustomAttributes(typeof(System.Diagnostics.DebuggerNonUserCodeAttribute), false), Is.Not.Empty);
    }
}
