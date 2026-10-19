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

    // The four tests below pin how step 3 of a Tick evaluates the waits today (FlowTask 0.1.0-preview.1). A change to how
    // step 3 finds the waits to evaluate (a deadline queue: ai_game_engine #946) must give the same results.

    [Test]
    public void ConditionThatPausesTheClockStopsLaterTimeWaitsInTheSameTick()
    {
        // Same result required after the deadline queue (#946). Both deadlines on DefaultClock pass in the second Tick, but
        // the condition, evaluated between the two time waits, pauses DefaultClock: the wait that began before it resumes,
        // the one that began after it is skipped in this Tick and resumes in the first Tick after the pause is released.
        // The flows run on the UnscaledClock, so the pause holds back only the wait (a flow on a paused clock does not
        // resume either, which would hide the difference).
        ScopedHandle pause = default;
        var pauseNow = false;

        async FlowTask Timed(string name)
        {
            await FlowTask.WaitForSeconds(1.5 * Dt, World.DefaultClock);
            Log.Add(name + " at frame " + World.DefaultClock.FrameCount);
        }

        async FlowTask Pauser()
        {
            await FlowTask.WaitUntil(() =>
            {
                if (!pauseNow) return false;
                pause = World.DefaultClock.Pause();
                Log.Add("pause");
                return true;
            });
            Log.Add("pauser resumed");
        }

        World.Run(Timed("before"), World.UnscaledClock);
        World.Run(Pauser(), World.UnscaledClock);
        var after = World.Run(Timed("after"), World.UnscaledClock);
        Tick();
        AssertLog();
        pauseNow = true;
        Tick();
        AssertLog("pause", "before at frame 2", "pauser resumed");
        Log.Clear();
        Tick(3);
        AssertLog();
        Assert.That(after.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(World.Diagnostics.Walk().Single(s => s.Name == "Timed").Waiting,
            Does.StartWith("WaitForSeconds(").And.Contain(" on Default"), World.Dump());
        pause.Dispose();
        Tick();
        AssertLog("after at frame 3");
        Assert.That(after.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void ConditionThatCancelsALaterFlowEndsItsWaitInTheSameTick()
    {
        // Same result required after the deadline queue (#946). Both deadlines pass in the second Tick, but the condition,
        // evaluated between the two time waits, cancels the later flow: on the World thread the cancel applies at once, so
        // its wait is not satisfied and the flow ends Canceled in this Tick. Its finally runs after step 3 (after the
        // condition that began after it was evaluated) and before the resumes that step 3 reserved, although the resume
        // of the wait that began before the condition was reserved before the cancel.
        FlowHandle later = default;
        var cancelNow = false;

        async FlowTask Timed(string name)
        {
            try
            {
                await FlowTask.WaitForSeconds(1.5 * Dt);
                Log.Add(name + " resumed");
            }
            finally
            {
                Log.Add(name + " finally");
            }
        }

        async FlowTask Canceler()
        {
            await FlowTask.WaitUntil(() =>
            {
                if (!cancelNow) return false;
                Log.Add("cancel");
                later.Cancel();
                Log.Add("canceled");
                return true;
            });
            Log.Add("canceler resumed");
        }

        async FlowTask Last()
        {
            // Began after the canceled wait: shows that the unwinding of the canceled flow runs after step 3.
            await FlowTask.WaitUntil(() =>
            {
                if (!cancelNow) return false;
                Log.Add("last evaluated");
                return true;
            });
            Log.Add("last resumed");
        }

        var before = World.Run(Timed("before"));
        World.Run(Canceler());
        later = World.Run(Timed("later"));
        World.Run(Last());
        Tick();
        AssertLog();
        cancelNow = true;
        Tick();
        AssertLog("cancel", "canceled", "last evaluated", "later finally", "before resumed", "before finally", "canceler resumed", "last resumed");
        Assert.That(before.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(later.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(later.CancelCause, Is.EqualTo(CancelCause.Explicit));
    }

    [Test]
    public void TimeFrameAndConditionWaitsSatisfiedInOneTickResumeInTheOrderTheyBegan()
    {
        // Same result required after the deadline queue (#946). The kinds alternate (time, frames, condition), and the
        // waits of the second group began 2 Ticks after the first group with shorter waits, so that all of them are
        // satisfied in the third Tick: they resume in the order the waits began, not by kind or by deadline. A flow that
        // awaits NextFrame 3 times began its last wait in the flush of the second Tick, so it comes after the rest of
        // the first group (in the order those resumes ran) and before the second group.
        var go = false;
        // 0 = WaitForSeconds, 1 = DelayFrames, 2 = NextFrame, 3 = WaitUntil
        var first = new[] { 0, 1, 3, 2, 3, 0, 2, 1, 0, 3, 1, 2 };
        var second = new[] { 3, 0, 2, 1, 3, 2, 0, 1 };

        async FlowTask Wait(string name, int kind, int frames)
        {
            switch (kind)
            {
                case 0:
                    await FlowTask.WaitForSeconds((frames - 0.5) * Dt);
                    break;
                case 1:
                    await FlowTask.DelayFrames(frames);
                    break;
                case 2:
                    for (var i = 0; i < frames; i++) await FlowTask.NextFrame();
                    break;
                default:
                    await FlowTask.WaitUntil(() => go);
                    break;
            }

            Log.Add(name);
        }

        // The first group waits 3 frames (or for the condition), the second, started after 2 Ticks, 1 frame.
        for (var i = 0; i < first.Length; i++) World.Run(Wait("a" + i, first[i], 3));
        Tick(2);
        for (var i = 0; i < second.Length; i++) World.Run(Wait("b" + i, second[i], 1));
        AssertLog();
        go = true;
        Tick();
        AssertLog("a0", "a1", "a2", "a4", "a5", "a7", "a8", "a9", "a10", "a3", "a6", "a11",
            "b0", "b1", "b2", "b3", "b4", "b5", "b6", "b7");
    }

    [Test]
    public void WaitsDueFromManyClocksAndKindsResumeInTheOrderTheyBeganEveryTick()
    {
        // The due waits of a Tick come from 5 buckets (frames of 4 clocks and time of one) plus a condition, and the
        // waits of the buckets began interleaved: the walk merges them by the order they began, Tick after Tick (#946).
        var game = World.CreateClock("Game");
        var child = World.CreateClock("Child", game);
        var order = new List<int>();
        const int count = 18;

        async FlowTask Loop(int i)
        {
            while (true)
            {
                switch (i % 6)
                {
                    case 0:
                        await FlowTask.NextFrame();
                        break;
                    case 1:
                        await FlowTask.NextFrame(World.UnscaledClock);
                        break;
                    case 2:
                        await FlowTask.WaitForSeconds(Dt / 2);
                        break;
                    case 3:
                        await FlowTask.NextFrame(game);
                        break;
                    case 4:
                        await FlowTask.DelayFrames(1, child);
                        break;
                    default:
                        var frame = World.DefaultClock.FrameCount;
                        await FlowTask.WaitUntil(() => World.DefaultClock.FrameCount > frame);
                        break;
                }

                order.Add(i);
            }
        }

        for (var i = 0; i < count; i++) World.Run(Loop(i));
        var expected = Enumerable.Range(0, count).ToArray();
        for (var t = 0; t < 4; t++)
        {
            order.Clear();
            Tick();
            Assert.That(order, Is.EqualTo(expected), "Tick " + (t + 1));
        }
    }

    [Test]
    public void ConditionThatChangesTheTimeScaleTakesEffectFromTheNextTick()
    {
        // Same result required after the deadline queue (#946). The clocks advance (step 2) before the waits are
        // evaluated (step 3), so a condition that halves DefaultClock's TimeScale does not change the time of this Tick:
        // the time wait that began after it and whose deadline passed resumes in this Tick. The new scale applies from
        // the next Tick, and delays the wait whose deadline was in the next Tick by one Tick.
        var scaleNow = false;

        async FlowTask Timed(string name, double seconds)
        {
            await FlowTask.WaitForSeconds(seconds);
            Log.Add(name + " at frame " + World.DefaultClock.FrameCount);
        }

        async FlowTask Scaler()
        {
            await FlowTask.WaitUntil(() =>
            {
                if (!scaleNow) return false;
                World.DefaultClock.TimeScale = 0.5;
                Log.Add("scale");
                return true;
            }, World.UnscaledClock);
            Log.Add("scaler resumed");
        }

        World.Run(Timed("before", 1.5 * Dt));
        World.Run(Scaler());
        World.Run(Timed("due now", 1.5 * Dt));
        World.Run(Timed("due next", 2.75 * Dt));
        Tick();
        AssertLog();
        scaleNow = true;
        Tick();
        AssertLog("scale", "before at frame 2", "scaler resumed", "due now at frame 2");
        Log.Clear();
        Tick();
        AssertLog();
        Assert.That(World.DefaultClock.Time, Is.EqualTo(2.5 * Dt).Within(1e-12));
        Tick();
        AssertLog("due next at frame 4");
    }

    [Test]
    public void ConditionThatCancelsTheOwnerOfAClockFailsTheWaitsOnTheClock()
    {
        // Same result required before and after the deadline queue (#946). A condition cancels the scope that owns a
        // clock while flows outside the scope wait on the clock, some due in this Tick and some not, before and after
        // the condition. The scope ends, and removes its clock, after step 3: the waits due in this Tick resume, and
        // step 3 of the next Tick fails the others.
        CaptureExceptions();
        Clock owned = null!;
        FlowHandle owner = default;
        var cancelNow = false;

        async FlowTask Owner()
        {
            owned = Flow.CreateClock("owned", World.UnscaledClock);
            await FlowTask.Never();
        }

        async FlowTask Waiter(string name, double seconds)
        {
            try
            {
                await FlowTask.WaitForSeconds(seconds, owned);
                Log.Add(name + " resumed at frame " + World.UnscaledClock.FrameCount);
            }
            catch (FlowMisuseException)
            {
                Log.Add(name + " failed at frame " + World.UnscaledClock.FrameCount);
            }
        }

        async FlowTask Canceler()
        {
            await FlowTask.WaitUntil(() =>
            {
                if (!cancelNow) return false;
                Log.Add("cancel");
                owner.Cancel();
                return true;
            });
            Log.Add("canceler resumed");
        }

        owner = World.Run(Owner());
        World.Run(Waiter("before due", 1.5 * Dt), World.UnscaledClock);
        World.Run(Waiter("before", 100), World.UnscaledClock);
        World.Run(Canceler(), World.UnscaledClock);
        World.Run(Waiter("after due", 1.5 * Dt), World.UnscaledClock);
        World.Run(Waiter("after", 100), World.UnscaledClock);
        Tick();
        AssertLog();
        cancelNow = true;
        Tick();
        Log.Add("tick");
        Tick();
        AssertLog("cancel", "before due resumed at frame 2", "canceler resumed", "after due resumed at frame 2", "tick",
            "before failed at frame 3", "after failed at frame 3");
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void ConditionThatCancelsAFlowWhoseFinallyWaitsAgainResumesTheNewWaitAsBefore()
    {
        // Same result required before and after the deadline queue (#946). A condition cancels flows that wait on their
        // own clock (the state machine waits itself) and began after the condition; their finally blocks wait again, for
        // no time and for a frame. The unwinding runs after step 3, so the new waits begin after it and resume in the
        // next Tick.
        var cancelNow = false;
        FlowHandle zero = default, frame = default;

        async FlowTask Parked(string name, bool frameAgain)
        {
            try
            {
                await FlowTask.WaitForSeconds(100);
            }
            finally
            {
                Log.Add(name + " finally at frame " + World.DefaultClock.FrameCount);
                if (frameAgain) await FlowTask.NextFrame();
                else await FlowTask.WaitForSeconds(0);
                Log.Add(name + " waited again at frame " + World.DefaultClock.FrameCount);
            }
        }

        async FlowTask Canceler()
        {
            await FlowTask.WaitUntil(() =>
            {
                if (!cancelNow) return false;
                Log.Add("cancel");
                zero.Cancel();
                frame.Cancel();
                return true;
            });
            Log.Add("canceler resumed");
        }

        World.Run(Canceler());
        zero = World.Run(Parked("zero", frameAgain: false));
        frame = World.Run(Parked("frame", frameAgain: true));
        Tick();
        AssertLog();
        cancelNow = true;
        Tick();
        Log.Add("tick");
        Tick();
        Log.Add("tick");
        Tick();
        AssertLog("cancel", "zero finally at frame 2", "frame finally at frame 2", "canceler resumed", "tick",
            "zero waited again at frame 3", "frame waited again at frame 3", "tick");
        Assert.That(zero.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(frame.Status, Is.EqualTo(FlowStatus.Canceled));
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
