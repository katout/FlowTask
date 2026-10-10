namespace Katout.FlowTask.Tests;

/// <summary>
/// How exceptions and warnings are reported when the reporting itself goes wrong (a throwing OnUnhandledException or OnWarning handler, a
/// World disposed without OnUnhandledException), the warnings (each kind once per World), and the text that tells where flows are: the
/// dump and a handle's ToString.
/// </summary>
public class DiagnosticsReportingTests : FlowTestBase
{
    sealed class Injected : Exception
    {
        public Injected(string tag) : base(tag) => Tag = tag;
        public string Tag { get; }
    }

    static string[] Tags(FlowUnhandledException ex) =>
        ex.ExceptionInfos.Select(p => p.Exception is Injected i ? i.Tag : p.Exception.GetType().Name).ToArray();

    // ------------------------------------------------------------------ OnUnhandledException handler failures

    [Test]
    public void FailingOnUnhandledExceptionHandlerKeepsTheOriginalReport()
    {
        var calls = 0;
        World.OnUnhandledException = p =>
        {
            calls++;
            throw new Injected("handler");
        };

        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new Injected("original");
        }

        World.Run(Bad());
        var ex = Assert.Throws<FlowUnhandledException>(() => Tick(2));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(Tags(ex!), Is.EqualTo(new[] { "original", "handler" }));
        Assert.That(ex!.ExceptionInfos[0].ScopePath, Is.EqualTo("Bad"));
        Assert.That(ex.ExceptionInfos[0].Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
        Assert.That(ex.ExceptionInfos[1].ScopePath, Is.EqualTo("<FlowWorld.OnUnhandledException handler>"));

        // The World keeps running.
        var ran = false;

        async FlowTask Next()
        {
            await FlowTask.NextFrame();
            ran = true;
        }

        World.Run(Next());
        Assert.DoesNotThrow(() => Tick(2));
        Assert.That(ran, Is.True);
    }

    [Test]
    public void OnUnhandledExceptionHandlerThatRethrowsTheExceptionReportsItOnce()
    {
        World.OnUnhandledException = p => throw p.Exception; // fail fast, as tests often do

        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new Injected("original");
        }

        World.Run(Bad());
        var ex = Assert.Throws<FlowUnhandledException>(() => Tick(2));
        Assert.That(Tags(ex!), Is.EqualTo(new[] { "original" }));
        Assert.That(ex!.ExceptionInfos[0].ScopePath, Is.EqualTo("Bad"));
        Assert.That(ex.ExceptionInfos[0].Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
    }

    [Test]
    public void CleanupExceptionSurvivesAFailingOnUnhandledExceptionHandler()
    {
        World.OnUnhandledException = p => throw new Injected("handler");

        async FlowTask F()
        {
            Flow.AddCleanup(() => throw new Injected("cleanup"));
            await FlowTask.NextFrame();
        }

        var h = World.Run(F());
        var ex = Assert.Throws<FlowUnhandledException>(() => Tick(2));
        Assert.That(Tags(ex!), Is.EqualTo(new[] { "cleanup", "handler" }));
        Assert.That(ex!.ExceptionInfos[0].Kind, Is.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(ex.ExceptionInfos[0].ScopePath, Is.EqualTo("F"));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ACleanupExceptionFoundWhileAFailureUnwindsASiblingIsReportedWhenFound([Values] bool caught, [Values] bool withHandler, [Values] bool inRun)
    {
        // The stage fails; the WhenAll unwinds the HUD, whose finally throws. Reports come as they are found: the cleanup
        // exception first, then the stage's failure when no catch takes it. A World without OnUnhandledException throws them in that order
        // too. Caught, the stage's failure is no report, and the cleanup exception is still reported. The same when the
        // stage fails before its first await, inside FlowWorld.Run.
        var reports = new List<FlowExceptionInfo>();
        if (withHandler) World.OnUnhandledException = reports.Add;

        async FlowTask Hud()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                throw new Injected("hud cleanup");
            }
        }

        async FlowTask Stage()
        {
            if (!inRun) await FlowTask.NextFrame();
            throw new Injected("stage");
        }

        async FlowTask Transition() => await FlowTask.WhenAll(Hud(), Stage());

        async FlowTask Root()
        {
            if (!caught)
            {
                await Transition();
                return;
            }

            try
            {
                await Transition();
            }
            catch (Injected e)
            {
                Log.Add("caught " + e.Tag);
            }
        }

        if (withHandler)
        {
            World.Run(Root());
            if (inRun) Assert.That(reports, Is.Not.Empty, "reported when Run returns");
            Tick(2);
        }
        else
        {
            var ex = Assert.Throws<FlowUnhandledException>(() =>
            {
                World.Run(Root());
                Tick(2);
            });
            reports.AddRange(ex!.ExceptionInfos);
        }

        var expected = caught
            ? new[] { (FlowExceptionKind.Cleanup, "hud cleanup") }
            : new[] { (FlowExceptionKind.Cleanup, "hud cleanup"), (FlowExceptionKind.Unhandled, "stage") };
        Assert.That(reports.Select(p => (p.Kind, ((Injected)p.Exception).Tag)), Is.EqualTo(expected));
        if (caught) AssertLog("caught stage");
    }

    public enum CallFromHandler
    {
        Tick,
        Flush,
        Dispose,
    }

    public enum ReportsEndOf
    {
        Tick,
        Flush,
        Run,
    }

    [Test]
    public void AnOnUnhandledExceptionHandlerCannotTickFlushOrDisposeTheWorld([Values] CallFromHandler call, [Values] ReportsEndOf end)
    {
        // Reports reach OnUnhandledException while a Tick, Flush or Run runs: a handler that calls Tick, Flush or Dispose gets
        // FlowMisuseException. The other reports still come, once each and in order, and the call returns; the World is
        // not disposed.
        var signal = new Signal<int>("s");
        var reports = new List<string>();
        var misuses = new List<string>();
        World.OnUnhandledException = p =>
        {
            reports.Add(p.Kind + " " + ((Injected)p.Exception).Tag);
            try
            {
                switch (call)
                {
                    case CallFromHandler.Tick:
                        World.Tick(0.1);
                        break;
                    case CallFromHandler.Flush:
                        World.Flush();
                        break;
                    default:
                        World.Dispose();
                        break;
                }
            }
            catch (FlowMisuseException e)
            {
                misuses.Add(e.GetType().Name);
            }
        };

        async FlowTask Loser(string name)
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                throw new Injected(name);
            }
        }

        async FlowTask Root()
        {
            // The losers start first; the winner ends the Race inside Run, or in the flush.
            if (end == ReportsEndOf.Run) await FlowTask.Race(Loser("a"), Loser("b"), FlowTask.WaitUntil(() => true));
            else await FlowTask.Race(Loser("a"), Loser("b"), signal.Next().WithoutResult());
            Log.Add("race done");
        }

        var h = World.Run(Root());
        signal.Emit(1);
        switch (end)
        {
            case ReportsEndOf.Tick:
                World.Tick(0.1);
                break;
            case ReportsEndOf.Flush:
                World.Flush();
                break;
        }

        Assert.That(reports, Is.EqualTo(new[] { "Cleanup b", "Cleanup a" }), "in the order they were found: the Race unwinds its losers last first");
        Assert.That(misuses, Is.EqualTo(new[] { nameof(FlowMisuseException), nameof(FlowMisuseException) }));
        AssertLog("race done");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(World.IsDisposed, Is.False);
        Assert.That(World.IsExecuting, Is.False);
    }

    // ------------------------------------------------------------------ reports while a World is disposed

    [Test]
    public void DisposeThrowsUnhandledExceptionsWhenOnUnhandledExceptionIsNotSet()
    {
        async FlowTask F()
        {
            Flow.AddCleanup(() => throw new Injected("cleanup"));
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("finally");
                throw new Injected("finally");
            }
        }

        var h = World.Run(F());
        Tick(2);
        var ex = Assert.Throws<FlowUnhandledException>(() => World.Dispose());
        AssertLog("finally");
        Assert.That(Tags(ex!), Is.EqualTo(new[] { "finally", "cleanup" }));
        Assert.That(ex!.ExceptionInfos.Select(p => p.Kind), Is.All.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(World.IsDisposed, Is.True);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.DoesNotThrow(() => World.Dispose(), "a second Dispose does nothing");
    }

    [Test]
    public void FailingOnUnhandledExceptionHandlerDuringDisposeIsSurfaced()
    {
        var calls = 0;
        World.OnUnhandledException = p =>
        {
            calls++;
            throw new Injected("handler");
        };

        async FlowTask F()
        {
            Flow.AddCleanup(() => throw new Injected("cleanup"));
            await FlowTask.Never();
        }

        World.Run(F());
        Tick();
        var ex = Assert.Throws<FlowUnhandledException>(() => World.Dispose());
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(Tags(ex!), Is.EqualTo(new[] { "cleanup", "handler" }));
        Assert.That(World.IsDisposed, Is.True);
    }

    [Test]
    public void DisposeWithAHandlerThrowsNothing()
    {
        CaptureExceptions();

        async FlowTask F()
        {
            Flow.AddCleanup(() => throw new Injected("cleanup"));
            await FlowTask.Never();
        }

        World.Run(F());
        Tick();
        Assert.DoesNotThrow(() => World.Dispose());
        Assert.That(Exceptions.Select(p => p.Exception.Message), Is.EqualTo(new[] { "cleanup" }));
    }

    [Test]
    public void ARunFromAnOnUnhandledExceptionHandlerDuringDisposeStartsNothing()
    {
        // A cleanup that throws while Dispose ends the flows reaches OnUnhandledException at once, inside Dispose. A handler that starts
        // a flow there gets a handle that has ended Canceled, and the flow's body never runs.
        var handles = new List<FlowHandle>();
        World.OnUnhandledException = p =>
        {
            Exceptions.Add(p);
            handles.Add(World.Run(Body()));
        };

        async FlowTask Body()
        {
            Log.Add("body ran");
            await FlowTask.NextFrame();
        }

        async FlowTask F()
        {
            Flow.AddCleanup(() => throw new Injected("cleanup"));
            await FlowTask.Never();
        }

        World.Run(F());
        Tick();
        Assert.DoesNotThrow(() => World.Dispose());
        Assert.That(Exceptions.Select(p => (p.Kind, p.Exception.Message)), Is.EqualTo(new[] { (FlowExceptionKind.Cleanup, "cleanup") }));
        Assert.That(handles.Count, Is.EqualTo(1));
        Assert.That(handles[0].Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(handles[0].IsCompleted, Is.True);
        AssertLog();
    }

    // ------------------------------------------------------------------ warnings: each kind once per World

    [Test]
    public void FlushLimitWarnsOncePerWorld()
    {
        // Two flows that resume each other without end: a flush processes 65,536 resumes, then leaves the rest for the next
        // one and warns, the first time only.
        var ping = new Signal<int>();
        var pong = new Signal<int>();
        var exchanges = 0;

        async FlowTask Player(Signal<int> me, Signal<int> other, bool serve)
        {
            if (serve) other.Emit(0);
            while (true)
            {
                await me.Next();
                exchanges++;
                other.Emit(0);
            }
        }

        World.Run(Player(ping, pong, false));
        World.Run(Player(pong, ping, true));
        Tick(3);
        Assert.That(exchanges, Is.EqualTo(3 * 65536));
        var warned = Warnings.Where(w => w.Kind == FlowWarningKind.FlushLimit).ToList();
        Assert.That(warned.Count, Is.EqualTo(1), "once per World");
        Assert.That(warned[0].Message, Does.StartWith("A flush processed 65536 resumes"));
        Assert.That(warned[0].ScopePath, Is.Null);
    }

    List<FlowWarning> OwnClockWarnings() => Warnings.Where(w => w.Kind == FlowWarningKind.PausedOwnClock).ToList();

    [Test]
    public void PausingTheClockAFlowRunsOnWarnsOncePerWorld()
    {
        var game = World.CreateClock("Game");
        var battle = World.CreateClock("Battle", game);

        async FlowTask PauseParent()
        {
            using (game.Pause()) Log.Add("paused Game");
            await FlowTask.NextFrame();
        }

        async FlowTask PauseSelf()
        {
            using (World.DefaultClock.Pause()) Log.Add("paused Default");
            await FlowTask.NextFrame();
        }

        World.Run(PauseParent(), battle); // runs on Battle, a child of Game: pausing Game stops it too
        World.Run(PauseSelf());
        Tick(2);
        AssertLog("paused Game", "paused Default");
        var warned = OwnClockWarnings();
        Assert.That(warned.Count, Is.EqualTo(1), "once per World");
        Assert.That(warned[0].ScopePath, Is.EqualTo("PauseParent"));
        Assert.That(warned[0].Message, Does.Contain("paused clock 'Game', which it runs on:"));
        Assert.That(warned[0].Message, Does.Contain("Flow.WithClock"));
    }

    [Test]
    public void PausingAnotherClockDoesNotWarn()
    {
        var game = World.CreateClock("Game");
        var battle = World.CreateClock("Battle", game);
        var ui = World.CreateClock("UI", World.UnscaledClock);
        var closed = false;

        async FlowTask Menu()
        {
            using (game.Pause()) await FlowTask.DelayFrames(2); // the menu runs on UI: it keeps running
            closed = true;
        }

        async FlowTask Director()
        {
            using (battle.Pause()) await FlowTask.NextFrame(); // a child of the clock it runs on
        }

        World.Run(Flow.WithClock(ui, Menu()));
        World.Run(Director(), game);
        using (game.Pause())
        {
        } // outside any flow

        Tick(4);
        Assert.That(closed, Is.True);
        Assert.That(OwnClockWarnings(), Is.Empty);
    }

    [Test]
    public void AWarningHandlerThatThrowsIsReportedAtTheRootAndTheOthersStillRun()
    {
        // Handlers run inside the scheduler: this one calls Tick, which is refused with FlowMisuseException. Its failure is
        // an unhandled exception at the root, not the flow's, and the handlers after it still get the warning.
        CaptureExceptions();
        World.OnWarning += w => World.Tick(0);
        var later = new List<FlowWarning>();
        World.OnWarning += w => later.Add(w);

        async FlowTask PauseSelf()
        {
            using (World.DefaultClock.Pause()) Log.Add("paused");
            await FlowTask.NextFrame();
            Log.Add("resumed");
        }

        var h = World.Run(PauseSelf());
        Tick(2);
        AssertLog("paused", "resumed");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded), "the handler's failure is not the flow's");
        Assert.That(Exceptions.Select(p => (p.Kind, p.ScopePath)), Is.EqualTo(new[] { (FlowExceptionKind.Unhandled, "<FlowWorld.OnWarning handler>") }));
        Assert.That(Exceptions[0].Exception, Is.InstanceOf<FlowMisuseException>());
        Assert.That(later.Select(w => w.Kind), Is.EqualTo(new[] { FlowWarningKind.PausedOwnClock }), "later subscribers still get the warning");
    }

    // ------------------------------------------------------------------ the dump and a handle's ToString

    [Test]
    public void DumpShowsWhatEachNodeWaitsForAndForHowLong()
    {
        // A scope shows what it waits for and for how long since it last suspended, a canceled one why it closes; a
        // combinator shows its branches, and a wait at the root what it waits for, without a time.
        var confirm = new Signal<int>(World, "Confirm");

        async FlowTask Busy()
        {
            while (true) await FlowTask.NextFrame();
        }

        async FlowTask Dialog() => await FlowTask.Race(Busy(), confirm.Next());

        async FlowTask Closing()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.WaitForSeconds(10); // runs to its end after the cancellation
            }
        }

        World.Run(Dialog());
        World.Run(new Signal<int>(World, "Login").Next());
        var closing = World.Run(Closing());
        TickFor(3);
        closing.Cancel();
        Tick();
        var dump = World.Dump();
        // Where each wait was created: its file name and line (WaitSiteTests checks the lines).
        const string At = @" at DiagnosticsReportingTests\.cs:\d+";
        Assert.That(dump, Does.StartWith("FlowWorld [Default]\n"), dump);
        Assert.That(dump, Does.Match(@"\n├─ Dialog \(scope\) \[Default\] waiting: Race" + At + @" for 3\.0\d*s\n"), dump);
        Assert.That(dump, Does.Match(@"\n│  └─ Race \(combinator\) \[Default\] waiting: Race 2/2 branches" + At + @"\n"), dump);
        Assert.That(dump, Does.Match(@"\n│     ├─ Busy \(scope\) \[Default\] waiting: NextFrame on Default, 1 frame\(s\) left" + At + @" for 0s\n"), dump);
        Assert.That(dump, Does.Match(@"\n│     └─ Confirm\.Next \(wait\) \[Default\] waiting: Confirm\.Next" + At + @"\n"), dump);
        Assert.That(dump, Does.Match(@"\n├─ Login\.Next \(wait\) \[Default\] waiting: Login\.Next" + At + @"\n"), dump);
        Assert.That(dump, Does.Match(@"\n└─ Closing \(scope\) \[Default\] waiting: WaitForSeconds\(10s\) on Default, 10s left" + At + @" for 0s <canceling: Explicit>\n$"), dump);

        var scopes = World.Diagnostics.Walk().ToDictionary(s => s.Name);
        Assert.That(scopes["Dialog"].WaitingSeconds, Is.GreaterThan(3.0));
        Assert.That(scopes["Busy"].WaitingSeconds, Is.EqualTo(0.0));
        Assert.That(scopes["Race"].WaitingSeconds, Is.EqualTo(0.0), "a combinator is not timed: the scopes under it are");
        Assert.That((scopes["Closing"].Status, scopes["Closing"].IsCanceling, scopes["Closing"].Cause),
            Is.EqualTo((FlowStatus.Running, true, CancelCause.Explicit)));
        Assert.That(scopes["Dialog"].Path, Is.EqualTo("Dialog"));
        Assert.That(scopes["Busy"].Path, Is.EqualTo("Dialog > Busy"));
    }

    [Test]
    public void AHandleShowsItsStatusAndWhyItWasCanceled()
    {
        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.NextFrame();
            }
        }

        async FlowTask<int> Answer() => 42;

        var h = World.Run(Screen());
        Assert.That(h.ToString(), Is.EqualTo("FlowHandle(Screen, Running)"));
        h.Cancel();
        Tick();
        Assert.That(h.ToString(), Is.EqualTo("FlowHandle(Screen, Running: Explicit)"), "closing: its finally awaits");
        Tick();
        Assert.That(h.ToString(), Is.EqualTo("FlowHandle(Screen, Canceled: Explicit)"));
        Assert.That(World.Run(Answer()).ToString(), Is.EqualTo("FlowHandle(Answer, Succeeded)"));
        Assert.That(default(FlowHandle).ToString(), Is.EqualTo("FlowHandle(Canceled)"));
    }

    [Test]
    public void DumpTellsSubscriptionsAndSignalsApart()
    {
        var hits = new Signal<int>(World, "Hits");
        var unnamed = new Signal<FlowUnit>(World);

        async FlowTask OnSignal() => await hits.Next();

        async FlowTask OnSubscription()
        {
            using var sub = hits.Subscribe(BufferPolicy.Latest);
            await sub.Next();
        }

        async FlowTask OnUnnamed() => await unnamed.Next();

        async FlowTask OnUnnamedSubscription()
        {
            using var sub = unnamed.Subscribe(BufferPolicy.Latest);
            await sub.Next();
        }

        World.Run(OnSignal());
        World.Run(OnSubscription());
        World.Run(OnUnnamed());
        World.Run(OnUnnamedSubscription());
        Tick();
        string WaitOf(string scope) => World.Diagnostics.Walk().Single(s => s.Name == scope).Waiting;
        Assert.That(WaitOf("OnSignal"), Is.EqualTo("Hits.Next"));
        Assert.That(WaitOf("OnSubscription"), Is.EqualTo("Hits subscription.Next"));
        Assert.That(WaitOf("OnUnnamed"), Is.EqualTo("Signal<FlowUnit>.Next"));
        Assert.That(WaitOf("OnUnnamedSubscription"), Is.EqualTo("Signal<FlowUnit> subscription.Next"));
        Assert.That(World.Dump(), Does.Contain("waiting: Hits subscription.Next"));
    }

    [Test]
    public void DumpShowsWhoPausesAClock()
    {
        var game = World.CreateClock("Game");
        var battle = World.CreateClock("Battle", game);
        Assert.That(World.Dump(), Does.Not.Contain("Paused clocks"));

        async FlowTask PauseMenu()
        {
            using var pause = game.Pause();
            await FlowTask.Never();
        }

        async FlowTask Main() => await PauseMenu();

        var menu = World.Run(Main());
        var outside = game.Pause(); // outside any flow: nobody releases it but the caller
        Tick();
        var dump = World.Dump();
        Assert.That(dump, Does.StartWith("Paused clocks:\n"));
        Assert.That(dump, Does.Contain("  Game: paused x2 by Main > PauseMenu, <outside any flow>\n"));
        Assert.That(dump, Does.Contain("  Battle: paused via Game\n"));

        outside.Dispose();
        dump = World.Dump();
        Assert.That(dump, Does.Contain("  Game: paused x1 by Main > PauseMenu\n"));
        Assert.That(dump, Does.Contain("  Battle: paused via Game\n"));

        using (battle.Pause())
            Assert.That(World.Dump(), Does.Contain("  Battle: paused x1 by <outside any flow>; also paused via Game\n"));

        menu.Cancel();
        Tick();
        Assert.That(game.PauseCount, Is.EqualTo(0));
        Assert.That(World.Dump(), Does.Not.Contain("Paused clocks"));
    }

    [Test]
    public void DumpMarksARemovedScopeClock()
    {
        // A flow run on a Flow.CreateClock clock outside its scope still waits on a signal after the clock was removed. The
        // clock stays paused for good, held by no pause, and holds no resume: the dump says it was removed and lists no
        // pause.
        var end = new Signal<int>(World, "End");
        var never = new Signal<int>(World, "Never");
        Clock clock = null!;

        async FlowTask Owner()
        {
            clock = Flow.CreateClock("owned");
            await end.Next();
        }

        async FlowTask Waiter() => await never.Next();

        World.Run(Owner());
        World.Run(Waiter(), clock);
        end.Emit(1);
        Tick();
        Assert.That(clock.ToString(), Does.Contain("removed"));
        Assert.That(World.Dump(), Does.Not.Contain("Paused clocks").And.Contain("Waiter (scope) [owned, removed] waiting: Never.Next"));
    }
}
