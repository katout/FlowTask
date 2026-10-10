namespace Katout.FlowTask.Tests;

/// <summary>World, Clock and time.</summary>
public class TimeTests : FlowTestBase
{
    [Test]
    public void SeveralWorldsOnOneThread()
    {
        using var a = new FlowWorld();
        using var b = new FlowWorld();
        var sa = new Signal<int>();
        var sb = new Signal<int>();

        async FlowTask Wait(Signal<int> s, string name) => Log.Add(name + await s.Next());

        a.Run(Wait(sa, "a"));
        b.Run(Wait(sb, "b"));
        sb.Emit(2);
        sa.Emit(1);
        a.Tick(Dt);
        AssertLog("a1");
        b.Tick(Dt);
        AssertLog("a1", "b2");
    }

    [Test]
    public void RunReturnsAHandleForCancelAndCompletion()
    {
        async FlowTask<int> Work()
        {
            await FlowTask.WaitForSeconds(0.1);
            return 7;
        }

        var h = World.Run(Work());
        Assert.That(h.IsCompleted, Is.False);
        TickFor(0.2);
        Assert.That(h.IsCompleted && h.Result == 7, Is.True);
        var h2 = World.Run(Work());
        h2.Cancel();
        Tick();
        Assert.That(h2.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void FlushDeliversWithoutAdvancingTime()
    {
        var sig = new Signal<int>();

        async FlowTask Root()
        {
            await sig.Next();
            Log.Add("signal");
            await FlowTask.WaitForSeconds(0.01);
            Log.Add("delay");
        }

        World.Run(Root());
        sig.Emit(1);
        World.Flush();
        AssertLog("signal");
        World.Flush();
        World.Flush();
        AssertLog("signal");
        Assert.That(World.DefaultClock.Time, Is.EqualTo(0));
        Tick();
        AssertLog("signal", "delay");
    }

    [Test]
    public void TheUnscaledClockCannotBePausedOrScaled()
    {
        Assert.Throws<FlowMisuseException>(() => World.UnscaledClock.Pause());
        Assert.Throws<FlowMisuseException>(() => World.UnscaledClock.TimeScale = 0);
        Assert.Throws<FlowMisuseException>(() => World.UnscaledClock.TimeScale = 1);
        World.DefaultClock.TimeScale = 0;
        using (World.DefaultClock.Pause())
        {
            Tick(10);
        }

        Assert.That(World.UnscaledClock.TimeScale, Is.EqualTo(1));
        Assert.That(World.UnscaledClock.Time, Is.EqualTo(10 * Dt).Within(1e-12));
        Assert.That(World.DefaultClock.Time, Is.EqualTo(0));
    }

    [Test]
    public void AClockMadeWithoutAParentFollowsDefaultClockAndAChildOfUnscaledClockKeepsRunning()
    {
        var game = World.CreateClock("Game");
        var ui = World.CreateClock("UI", World.UnscaledClock);
        Assert.That(World.Clocks, Has.Member(game).And.Member(ui));
        Assert.That(game.Parent, Is.SameAs(World.DefaultClock));
        Assert.That(ui.Parent, Is.SameAs(World.UnscaledClock));
        Assert.Throws<ArgumentNullException>(() => World.CreateClock("Root", null!));

        World.DefaultClock.TimeScale = 0.5; // what the engine integrations do with the engine's time scale
        Tick(4, 0.25);
        Assert.That(game.Time, Is.EqualTo(0.5));
        Assert.That(ui.Time, Is.EqualTo(1.0));
        using (World.DefaultClock.Pause())
        {
            Assert.That(game.IsPausedInHierarchy, Is.True);
            Tick(4, 0.25);
        }

        Assert.That(game.IsPausedInHierarchy, Is.False);
        Assert.That(game.Time, Is.EqualTo(0.5));
        Assert.That(ui.Time, Is.EqualTo(2.0));
        using (ui.Pause())
        {
            Tick(4, 0.25);
        }

        Assert.That(ui.Time, Is.EqualTo(2.0));
        Assert.That(World.UnscaledClock.Time, Is.EqualTo(3.0));
    }

    [Test]
    public void FlowClockIsRemovedWhenItsScopeEnds()
    {
        Clock clock = null!;

        async FlowTask Enemy()
        {
            Flow.AddCleanup(() => Log.Add("registered before: " + World.Clocks.Contains(clock)));
            clock = Flow.CreateClock("slow");
            Flow.AddCleanup(() => Log.Add("registered after: " + World.Clocks.Contains(clock)));
            await FlowTask.NextFrame();
        }

        World.Run(Enemy());
        Assert.That(World.Clocks, Has.Member(clock));
        Tick();
        Assert.That(World.Clocks, Has.No.Member(clock));
        AssertLog("registered after: True", "registered before: False"); // LIFO with AddCleanup

        // Clocks made per flow used to stay in the World for good, and every Tick walked all of them.
        async FlowTask Short(int i) => await FlowTask.NextFrame(Flow.CreateClock("c" + i, Flow.CurrentClock));

        for (var i = 0; i < 1000; i++) World.Run(Short(i));
        Assert.That(World.Clocks.Count, Is.EqualTo(1002));
        Tick();
        Assert.That(World.Clocks, Is.EqualTo(new[] { World.UnscaledClock, World.DefaultClock }));
    }

    [Test]
    public void FlowClocksMadeInALoopStayUntilTheScopeEnds()
    {
        // A scope keeps each clock it creates until it ends, so clocks made per iteration of a long-lived loop pile up.
        // Made in an awaited child method, each clock is removed when the child ends.
        async FlowTask SlowStep()
        {
            var slow = Flow.CreateClock("slow", Flow.CurrentClock);
            slow.TimeScale = 0.5;
            await Flow.WithClock(slow, FlowTask.NextFrame());
        }

        async FlowTask Enemy()
        {
            for (var i = 0; i < 10; i++)
            {
                var slow = Flow.CreateClock("slow", Flow.CurrentClock);
                slow.TimeScale = 0.5;
                await Flow.WithClock(slow, FlowTask.NextFrame());
            }

            Log.Add("after the loop: " + (World.Clocks.Count - 2));
            for (var i = 0; i < 10; i++) await SlowStep();
            Log.Add("after the child calls: " + (World.Clocks.Count - 2));
            await FlowTask.Never();
        }

        var h = World.Run(Enemy());
        Tick(25);
        AssertLog("after the loop: 10", "after the child calls: 10");
        h.Cancel();
        Tick();
        Assert.That(World.Clocks, Is.EqualTo(new[] { World.UnscaledClock, World.DefaultClock }));
    }

    [Test]
    public void FlowClockCanBeUsedByTheScopeAndItsChildren()
    {
        Clock slow = null!;

        async FlowTask Child(string name)
        {
            await FlowTask.WaitForSeconds(0.5);
            Log.Add(name + " on " + Flow.CurrentClock.Name);
        }

        async FlowTask Enemy()
        {
            slow = Flow.CreateClock("slow", Flow.CurrentClock);
            slow.TimeScale = 0.5;
            var spawned = Flow.Spawn(Flow.WithClock(slow, Child("spawned")));
            await FlowTask.WaitForSeconds(0.5, slow);
            Log.Add("scope waited");
            await Flow.WithClock(slow, Child("child"));
            await spawned.Join();
        }

        CaptureExceptions();
        var h = World.Run(Enemy());
        TickFor(0.9);
        AssertLog();
        TickFor(0.2);
        AssertLog("spawned on slow", "scope waited");
        TickFor(1.0);
        AssertLog("spawned on slow", "scope waited", "child on slow");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(World.Clocks, Has.No.Member(slow));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void UsingARemovedFlowClockThrowsFlowMisuseException()
    {
        CaptureExceptions();
        Clock leaked = null!;

        async FlowTask Owner()
        {
            leaked = Flow.CreateClock("leaked");
            await FlowTask.NextFrame();
        }

        async FlowTask Work() => await FlowTask.NextFrame();
        async FlowTask WaitOnIt() => await FlowTask.WaitForSeconds(1, leaked);
        async FlowTask RunOnIt() => await Flow.WithClock(leaked, Work());

        async FlowTask ChildOfIt()
        {
            Flow.CreateClock("child", leaked);
            await FlowTask.NextFrame();
        }

        World.Run(Owner());
        Tick(2);
        var time = leaked.Time;
        Tick();
        Assert.That(leaked.Time, Is.EqualTo(time), "a removed clock stops");
        Assert.That(leaked.DeltaTime, Is.EqualTo(0));
        Assert.That(leaked.IsPausedInHierarchy, Is.True);

        // Each root flow ends with the misuse, which reaches OnUnhandledException.
        World.Run(WaitOnIt());
        World.Run(RunOnIt());
        World.Run(ChildOfIt());
        Assert.That(Exceptions, Has.Count.EqualTo(3));
        Assert.That(Exceptions.Select(p => p.Exception), Has.All.TypeOf<FlowMisuseException>());
        Assert.That(Exceptions.Select(p => p.Exception.Message), Has.All.Contain("Clock 'leaked' was removed when its scope 'Owner' ended"));

        Assert.Throws<FlowMisuseException>(() => World.Run(Work(), leaked));
        Assert.Throws<FlowMisuseException>(() => leaked.TimeScale = 2);
        Assert.Throws<FlowMisuseException>(() => leaked.Pause());
        Assert.Throws<FlowMisuseException>(() => World.CreateClock("child", leaked));
        Assert.That(leaked.TimeScale, Is.EqualTo(1));
        Assert.That(leaked.PauseCount, Is.EqualTo(0));
    }

    [Test]
    public void AWaitOnARemovedFlowClockThrowsAtItsAwaitWhetherTheScopeWaitsItselfOrThroughANode([Values(false, true)] bool parked)
    {
        // A frame or time wait on the scope's own clock is parked (the state machine waits in the tick list itself);
        // Flow.Named keeps the same wait as a node. Parking is an optimization that must not show: when the clock is
        // removed under the wait, both fail the same way at the next Tick, with the misuse thrown at the await, which a
        // catch receives.
        CaptureExceptions();
        var end = new Signal<int>();
        Clock clock = null!;

        async FlowTask Owner()
        {
            clock = Flow.CreateClock("owned");
            await end.Next();
        }

        async FlowTask Waiter()
        {
            try
            {
                if (parked) await FlowTask.WaitForSeconds(5);
                else await Flow.Named("wait", FlowTask.WaitForSeconds(5));
                Log.Add("resumed");
            }
            catch (FlowMisuseException e)
            {
                Log.Add("caught " + (e.Message.Contains("Clock 'owned' was removed", StringComparison.Ordinal) ? "the removed clock" : e.Message));
            }

            Log.Add("goes on");
        }

        World.Run(Owner());
        var h = World.Run(Waiter(), clock);
        Tick();
        end.Emit(1);
        Tick(); // Owner ends in this flush and removes the clock
        AssertLog();
        Tick(); // step 3 finds the wait on the removed clock
        AssertLog("caught the removed clock", "goes on");
        Assert.That(Exceptions, Is.Empty);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void AWaitOnARemovedFlowClockWhoseScopeIsCanceledBeforeItResumesIsUndelivered([Values(false, true)] bool parked)
    {
        // The same failure when its receiver is canceled first, parked or not: two waits fail in the same step 3;
        // the first one's catch cancels the second flow before the failure reaches its await. The second does not
        // receive it: its catch does not run, its finally does, and the misuse is reported as Undelivered with the
        // path of the wait.
        CaptureExceptions();
        var end = new Signal<int>();
        Clock clock = null!;
        FlowHandle second = default;

        async FlowTask Owner()
        {
            clock = Flow.CreateClock("owned");
            await end.Next();
        }

        async FlowTask Wait()
        {
            if (parked) await FlowTask.WaitForSeconds(5);
            else await Flow.Named("wait", FlowTask.WaitForSeconds(5));
        }

        async FlowTask First()
        {
            try
            {
                await Wait();
            }
            catch (FlowMisuseException)
            {
                Log.Add("first caught");
                second.Cancel();
            }
        }

        async FlowTask Second()
        {
            try
            {
                await Wait();
                Log.Add("second resumed");
            }
            catch (FlowMisuseException)
            {
                Log.Add("second caught");
            }
            finally
            {
                Log.Add("second finally");
            }
        }

        World.Run(Owner());
        var first = World.Run(First(), clock);
        second = World.Run(Second(), clock);
        Tick();
        end.Emit(1);
        Tick(); // Owner ends and removes the clock
        Tick(); // step 3 fails both waits
        AssertLog("first caught", "second finally");
        Assert.That(Exceptions, Has.Count.EqualTo(1));
        Assert.That(Exceptions[0].Kind, Is.EqualTo(FlowExceptionKind.Undelivered));
        Assert.That(Exceptions[0].Exception, Is.TypeOf<FlowMisuseException>());
        Assert.That(Exceptions[0].ScopePath, Does.StartWith("Second > Wait"));
        Assert.That(first.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(second.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(second.CancelCause, Is.EqualTo(CancelCause.Explicit));
    }

    [Test]
    public void FlowClockParentMustOutliveIt()
    {
        CaptureExceptions();
        Clock sibling = null!, outer = null!, inner = null!;

        async FlowTask Holder()
        {
            sibling = Flow.CreateClock("sibling");
            await FlowTask.Never();
        }

        async FlowTask Inner()
        {
            inner = Flow.CreateClock("inner", outer); // an ancestor's clock outlives this scope
            Assert.Throws<FlowMisuseException>(() => Flow.CreateClock("bad", sibling));
            await FlowTask.NextFrame();
        }

        async FlowTask Outer()
        {
            outer = Flow.CreateClock("outer");
            await Inner();
            Log.Add("inner removed: " + !World.Clocks.Contains(inner) + ", outer kept: " + World.Clocks.Contains(outer));
        }

        // Without a parent, the clock this flow runs on is the parent: here another scope's clock.
        async FlowTask OnSibling()
        {
            var ex = Assert.Throws<FlowMisuseException>(() => Flow.CreateClock("defaulted"));
            Log.Add("names the owner: " + ex!.Message.Contains("scope 'Holder'", StringComparison.Ordinal));
            await FlowTask.NextFrame();
        }

        var holder = World.Run(Holder());
        Assert.Throws<FlowMisuseException>(() => World.CreateClock("world clock", sibling));
        World.Run(OnSibling(), sibling);
        World.Run(Outer());
        Tick();
        AssertLog("names the owner: True", "inner removed: True, outer kept: True");
        Assert.That(World.Clocks, Has.No.Member(outer));
        holder.Cancel();
        Tick();
        Assert.That(World.Clocks, Has.No.Member(sibling));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void FlowCreateClockOutsideAFlowThrows()
    {
        Assert.Throws<FlowMisuseException>(() => Flow.CreateClock("c"));
        Assert.That(World.Clocks, Has.Count.EqualTo(2));
    }

    [Test]
    public void AFlowThatCleanupCodeStartsOnAScopeClockFailsOnceTheClockIsRemoved()
    {
        // A scope clock is removed with its owner's cleanups, in LIFO order with AddCleanup and Own: it is not lent to
        // what an earlier cleanup started on it. That flow fails at its next wait, as any flow on a removed clock does.
        CaptureExceptions();
        FlowHandle late = default;

        async FlowTask Late()
        {
            await FlowTask.WaitForSeconds(0.1);
            Log.Add("unreachable");
        }

        async FlowTask Owner()
        {
            var clock = Flow.CreateClock("Slow");
            Flow.AddCleanup(() => late = World.Run(Late(), clock));
            await FlowTask.NextFrame();
        }

        World.Run(Owner());
        Tick(3);
        Assert.That(late.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(Exceptions.Single().Exception, Is.TypeOf<FlowMisuseException>().And.Message.Contains("was removed"));
        AssertLog();
    }

    [Test]
    public void FlowClocksStayUntilTheLastChildRunningItsFinallyEnds()
    {
        // Owner returns while the children it spawned still run their finally blocks, which wait on its clocks outer and
        // inner (a child of outer). The owner ends after its children and only then removes its clocks, inner first
        // (LIFO), so inner keeps running to the end; a parent removed first would leave inner paused for good, its waits
        // stopped without a failure.
        CaptureExceptions();
        Clock outer = null!, inner = null!;

        async FlowTask SaveOn(Clock c, double seconds, string name)
        {
            await FlowTask.WaitForSeconds(seconds, c);
            Log.Add(name + " saved, outer kept " + World.Clocks.Contains(outer) + ", inner kept " + World.Clocks.Contains(inner));
        }

        async FlowTask Worker(string name, double seconds)
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await SaveOn(inner, seconds, name);
                Log.Add(name + " ends on " + Flow.CurrentClock.Name);
            }
        }

        async FlowTask Owner()
        {
            outer = Flow.CreateClock("outer");
            inner = Flow.CreateClock("inner", outer);
            inner.TimeScale = 0.5;
            Flow.Spawn(Worker("short", 0.05));
            Flow.Spawn(Flow.WithClock(inner, Worker("long", 0.2)));
            await FlowTask.NextFrame();
        }

        var owner = World.Run(Owner());
        Tick(); // Owner returns while both workers run their finally blocks
        Assert.That(owner.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(World.Clocks, Has.Member(outer).And.Member(inner));
        TickFor(0.2); // the short worker's 0.05 s on inner (half speed) take 0.1 s
        AssertLog("short saved, outer kept True, inner kept True", "short ends on Default");
        Assert.That(World.Clocks, Has.Member(outer).And.Member(inner), "the long worker still runs its finally on inner");
        TickFor(0.3);
        AssertLog("short saved, outer kept True, inner kept True", "short ends on Default",
            "long saved, outer kept True, inner kept True", "long ends on inner");
        Assert.That(owner.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(World.Clocks, Has.No.Member(outer).And.No.Member(inner));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void AFinallyOfACanceledScopeWaitsOnTheClocksTheScopeMade()
    {
        // The awaits of a canceled scope's finally block run as a live scope's do, on the clocks it made: they are
        // removed when the scope ends, after that block. A clock made there can have one of them as its parent.
        CaptureExceptions();
        Clock slow = null!;

        async FlowTask FadeOut()
        {
            var fade = Flow.CreateClock("fade"); // under Flow.CurrentClock, the canceled scope's clock
            await FlowTask.WaitForSeconds(0.1, fade);
            Log.Add("faded on " + Flow.CurrentClock.Name + ", slow kept: " + World.Clocks.Contains(slow));
        }

        async FlowTask Enemy()
        {
            slow = Flow.CreateClock("slow");
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await Flow.WithClock(slow, FadeOut());
                Log.Add("enemy ends");
            }
        }

        var h = World.Run(Enemy());
        Tick();
        h.Cancel();
        TickFor(0.3);
        AssertLog("faded on slow, slow kept: True", "enemy ends");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(World.Clocks, Has.No.Member(slow));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void FlowClockFollowsItsParentsPauseAndScale()
    {
        var game = World.CreateClock("Game");
        Clock fx = null!, defaulted = null!, independent = null!;

        async FlowTask Enemy()
        {
            fx = Flow.CreateClock("fx", Flow.CurrentClock);
            defaulted = Flow.CreateClock("defaulted");
            independent = Flow.CreateClock("independent", World.UnscaledClock);
            Assert.Throws<ArgumentNullException>(() => Flow.CreateClock("root", null!));
            fx.TimeScale = 0.5;
            await FlowTask.Never();
        }

        var h = World.Run(Enemy(), game);
        Assert.That(defaulted.Parent, Is.SameAs(game));
        game.TimeScale = 0.5;
        Tick(4, 0.25);
        Assert.That(fx.Time, Is.EqualTo(0.25));
        Assert.That(defaulted.Time, Is.EqualTo(0.5));
        Assert.That(independent.Time, Is.EqualTo(1.0));
        var pause = game.Pause();
        Assert.That(fx.IsPausedInHierarchy, Is.True);
        Assert.That(defaulted.IsPausedInHierarchy, Is.True);
        Tick(4, 0.25);
        pause.Dispose();
        Assert.That(fx.IsPausedInHierarchy, Is.False);
        Assert.That(fx.Time, Is.EqualTo(0.25));
        Assert.That(defaulted.Time, Is.EqualTo(0.5));
        Assert.That(independent.Time, Is.EqualTo(2.0));
        h.Cancel();
        Tick();
        Assert.That(World.Clocks, Has.No.Member(fx).And.No.Member(defaulted).And.No.Member(independent));
    }

    [Test]
    public void TimeScaleRejectsNonFiniteAndNegativeValues()
    {
        var game = World.CreateClock("Game");
        foreach (var bad in new[] { double.PositiveInfinity, double.NegativeInfinity, double.NaN, -1.0 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => game.TimeScale = bad, "TimeScale = " + bad);
            Assert.That(game.TimeScale, Is.EqualTo(1), "unchanged after " + bad);
        }

        // The product with the ancestors' scales must stay finite too, whichever side is set.
        var parent = World.CreateClock("Parent");
        var child = World.CreateClock("Child", parent);
        parent.TimeScale = 1e200;
        Assert.Throws<ArgumentOutOfRangeException>(() => child.TimeScale = 1e200);
        Assert.That(child.TimeScale, Is.EqualTo(1));
        child.TimeScale = 1e100;
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.TimeScale = 1e300);
        Assert.That(parent.TimeScale, Is.EqualTo(1e200));
        World.Tick(0);
        Assert.That(double.IsNaN(child.Time), Is.False);

        // +Infinity used to be accepted, and a Tick(0) then turned the clock's time into NaN for good.
        var done = false;

        async FlowTask Wait()
        {
            await FlowTask.WaitForSeconds(1);
            done = true;
        }

        World.Run(Wait(), game);
        World.Tick(0);
        var ticks = 0;
        while (!done && ticks < 120)
        {
            World.Tick(Dt);
            ticks++;
        }

        Assert.That(double.IsNaN(game.Time), Is.False);
        Assert.That(done, Is.True);
        Assert.That(ticks, Is.InRange(60, 61));
    }

    [Test]
    public void WithClockSetsTheSubtreeClock()
    {
        var ui = World.CreateClock("UI", World.UnscaledClock);

        async FlowTask Child() => Log.Add("child clock " + Flow.CurrentClock.Name);

        async FlowTask Dialog()
        {
            Log.Add("dialog clock " + Flow.CurrentClock.Name);
            await Child();
        }

        async FlowTask Root()
        {
            Log.Add("root clock " + Flow.CurrentClock.Name);
            await Flow.WithClock(ui, Dialog());
        }

        World.Run(Root());
        AssertLog("root clock Default", "dialog clock UI", "child clock UI");
    }

    [Test]
    public void WithClockOnATaskThatHasStartedOrBeenConsumedThrows()
    {
        // The clock of a task is chosen before it starts. A consumed task's node may already run another task: setting
        // its clock would move that one.
        var ui = World.CreateClock("UI", World.UnscaledClock);
        var consumed = default(FlowTask);

        async FlowTask Consume()
        {
            consumed = FlowTask.DelayFrames(0);
            await consumed;
        }

        var running = FlowTask.Never();
        World.Run(running);
        World.Run(Consume());
        Assert.Throws<FlowMisuseException>(() => Flow.WithClock(ui, running));
        Assert.Throws<FlowMisuseException>(() => Flow.WithClock(ui, consumed));
    }

    [Test]
    public void PauseIsRefCountedOwnedByTheScopeAndIdempotent()
    {
        async FlowTask Pauser(double seconds)
        {
            World.DefaultClock.Pause();
            await FlowTask.WaitForSeconds(seconds, World.UnscaledClock);
        }

        var ui = World.CreateClock("UI", World.UnscaledClock); // the pausers must not run on the clock they pause
        World.Run(Pauser(0.1), ui);
        World.Run(Pauser(0.2), ui);
        Assert.That(World.DefaultClock.PauseCount, Is.EqualTo(2));
        TickFor(0.15);
        Assert.That(World.DefaultClock.PauseCount, Is.EqualTo(1));
        TickFor(0.1);
        Assert.That(World.DefaultClock.PauseCount, Is.EqualTo(0));
        var h = World.DefaultClock.Pause();
        h.Dispose();
        h.Dispose();
        Assert.That(World.DefaultClock.PauseCount, Is.EqualTo(0));
    }

    [Test]
    public void PausedScopesDoNotResumeAndKeepReservationOrder()
    {
        var game = World.CreateClock("Game");
        var sig = new Signal<int>();

        async FlowTask Waiter(string n)
        {
            await sig.Next();
            Log.Add(n);
            await FlowTask.NextFrame();
            Log.Add(n + " next frame");
        }

        World.Run(Waiter("a"), game);
        World.Run(Waiter("b"), game);
        var pause = game.Pause();
        sig.Emit(1);
        Tick(3);
        AssertLog();
        pause.Dispose();
        Tick();
        AssertLog("a", "b");
        Tick();
        AssertLog("a", "b", "a next frame", "b next frame");
    }

    [Test]
    public void ResumesReleasedMidFlushRunInTheSameFlush()
    {
        var game = World.CreateClock("Game");
        var ui = World.CreateClock("UI", World.UnscaledClock);
        var sig = new Signal<int>();

        async FlowTask Background()
        {
            await sig.Next();
            Log.Add("background resumed");
        }

        async FlowTask Dialog()
        {
            using var pause = game.Pause();
            sig.Emit(1);
            await FlowTask.NextFrame();
            Log.Add("dialog closes");
        }

        World.Run(Background(), game);
        World.Run(Dialog(), ui);
        Tick();
        AssertLog("dialog closes", "background resumed");
    }

    [Test]
    public void AResumeAPauseHeldDoesNotCutShortTheCleanupOfAScopeCanceledMeanwhile()
    {
        // The child fails while the scope's clock is paused, so the scope's resume is held. The scope is then canceled:
        // it unwinds at once, and its finally awaits a cleanup on a clock that runs. Releasing the pause must not resume
        // the scope again: the cleanup runs to its end, and the child's failure, which the scope never received, is
        // Undelivered.
        CaptureExceptions();
        var game = World.CreateClock("Game");
        var ui = World.CreateClock("UI", World.UnscaledClock);
        var sig = new Signal<int>();

        async FlowTask Child()
        {
            await sig.Next();
            throw new InvalidOperationException("child failed");
        }

        async FlowTask FadeOut()
        {
            await FlowTask.WaitForSeconds(0.5);
            Log.Add("faded");
        }

        async FlowTask Screen()
        {
            try
            {
                await Flow.WithClock(ui, Child());
            }
            finally
            {
                await Flow.WithClock(ui, FadeOut());
                Log.Add("screen ends");
            }
        }

        var h = World.Run(Screen(), game);
        var pause = game.Pause();
        sig.Emit(1);
        Tick(); // the child fails on ui; the resume of Screen, on game, is held
        h.Cancel();
        Tick(); // Screen unwinds, and its finally starts FadeOut on ui
        pause.Dispose();
        TickFor(1);
        AssertLog("faded", "screen ends");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(Exceptions.Select(p => p.Kind), Is.EqualTo(new[] { FlowExceptionKind.Undelivered }));
    }

    [Test]
    public void ResumesOnARemovedFlowClockAreNotHeld()
    {
        // A removed scope clock stays paused but never releases: a resume its pause held runs in the flush in which the
        // clock is removed, and a later resume on it runs in delivery order with the others.
        var held = new Signal<int>();
        var later = new Signal<int>();
        var other = new Signal<int>();
        var end = new Signal<int>();
        Clock clock = null!;

        async FlowTask Owner()
        {
            clock = Flow.CreateClock("owned");
            await end.Next();
        }

        async FlowTask OnSignal(Signal<int> signal, string name)
        {
            await signal.Next();
            Log.Add(name);
        }

        World.Run(Owner());
        World.Run(OnSignal(held, "held"), clock);
        World.Run(OnSignal(later, "later"), clock);
        World.Run(OnSignal(other, "on the default clock"));
        var pause = clock.Pause();
        held.Emit(1);
        Tick();
        AssertLog(); // held by the pause
        end.Emit(1);
        Tick(); // Owner ends: the clock is removed, and the held resume runs in the same flush
        AssertLog("held");
        later.Emit(1);
        other.Emit(1);
        World.Flush();
        AssertLog("held", "later", "on the default clock");
        pause.Dispose(); // a handle taken before the removal stays valid
        Assert.That(clock.PauseCount, Is.EqualTo(0));
        Assert.That(World.Diagnostics.Root.Children, Is.Empty);
    }

    [Test]
    public void WaitsOnTheScopeClockFinishOnTheSameTickAsWaitNodes()
    {
        // Without a clock argument the awaiting state machine waits in the tick list itself; with one, a wait node does.
        // Both must count the same ticks and time, skip paused ticks and keep registration order.
        var game = World.CreateClock("Game");

        async FlowTask Frames(string name, Clock clock)
        {
            await FlowTask.DelayFrames(3, clock);
            Log.Add(name);
        }

        async FlowTask Time(string name, Clock clock)
        {
            await FlowTask.WaitForSeconds(0.05, clock);
            Log.Add(name);
        }

        World.Run(Frames("frames scope", null), game);
        World.Run(Frames("frames node", game), game);
        World.Run(Time("time scope", null), game);
        World.Run(Time("time node", game), game);
        Assert.That(World.Dump(), Does.Contain("DelayFrames(3) on Game, 3 frame(s) left"));
        Tick();
        var pause = game.Pause();
        Tick(5);
        AssertLog();
        pause.Dispose();
        for (var i = 0; i < 6; i++)
        {
            Tick();
            var e = Log.Entries;
            Assert.That(e.Contains("frames scope"), Is.EqualTo(e.Contains("frames node")), "frame waits, tick " + i);
            Assert.That(e.Contains("time scope"), Is.EqualTo(e.Contains("time node")), "time waits, tick " + i);
        }

        Assert.That(Log.Entries, Is.EqualTo(new[] { "frames scope", "frames node", "time scope", "time node" }));
    }

    [Test]
    public void ClockTimeIsDouble()
    {
        Assert.That(typeof(Clock).GetProperty(nameof(Clock.Time))!.PropertyType, Is.EqualTo(typeof(double)));
        for (var i = 0; i < 100_000; i++) World.Tick(Dt);
        Assert.That(World.DefaultClock.Time, Is.EqualTo(100_000 * Dt).Within(1e-6));
    }
}
