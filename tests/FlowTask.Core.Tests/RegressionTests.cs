using System.Threading;

namespace Katout.FlowTask.Tests;

/// <summary>Defects found by adversarial review: each test reproduces one and asserts the documented behavior.</summary>
public class RegressionTests : FailurePathTestBase
{
    // ------------------------------------------------------------------ a method whose end is held

    public enum CanceledByTheCleanup
    {
        ItsOwnFlow,
        TheFlowThatAwaitsIt,
        TheFlowWhoseRaceItLost,
    }

    [Test]
    public void AMethodWhoseEndIsHeldIsNeverResumedAgain([Values] CanceledByTheCleanup canceled)
    {
        // X returns at once, but its end cancels Save, a child it spawned, whose finally takes two frames: X's end waits
        // for it (and meanwhile the frame may win the Race around X). When Save ends, X's end goes on and runs its
        // AddCleanup, which cancels a flow around X. X must not run again, nor its cleanup.
        CaptureExceptions();
        FlowHandle h = default;
        var bodyRuns = 0;
        var cleanups = 0;

        async FlowTask Save()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.DelayFrames(2);
                Log.Add("saved");
            }
        }

        async FlowTask X()
        {
            bodyRuns++;
            Flow.AddCleanup(() =>
            {
                cleanups++;
                Log.Add("cleanup");
                h.Cancel();
            });
            Flow.Spawn(Save());
        }

        async FlowTask Session()
        {
            await X();
            Log.Add("session after X");
            await FlowTask.Never();
        }

        async FlowTask Root()
        {
            await FlowTask.Race(X(), FlowTask.NextFrame());
            Log.Add("race done");
            await FlowTask.Never();
        }

        h = canceled switch
        {
            CanceledByTheCleanup.ItsOwnFlow => World.Run(X()),
            CanceledByTheCleanup.TheFlowThatAwaitsIt => World.Run(Session()),
            _ => World.Run(Root()),
        };
        var task = h.AsTask();
        Tick(4);
        Assert.That((bodyRuns, cleanups), Is.EqualTo((1, 1)), "the method or its cleanup ran again after it had returned");
        AssertLog("saved", "cleanup");
        AssertNoExceptions();
        if (canceled == CanceledByTheCleanup.ItsOwnFlow) AssertSucceeded(h);
        else AssertCanceled(h, CancelCause.Explicit);
        Assert.That(task.Status, Is.EqualTo(h.Status == FlowStatus.Succeeded ? TaskStatus.RanToCompletion : TaskStatus.Canceled),
            "the handle and its AsTask agree");
    }

    [Test]
    public void AResumeQueuedBeforeACancelDoesNotCutTheCleanupShort([Values] bool parked)
    {
        // S's frame wait is satisfied in step 3, which queues S's resume. Earlier in the same flush, Canceler cancels S:
        // S unwinds on the spot, and its finally awaits a frame, as its own wait (parked) or through a task. The resume
        // queued before the cancellation must not wake that await: the cleanup runs to its end, a frame later.
        CaptureExceptions();
        FlowHandle s = default;

        async FlowTask Canceler()
        {
            await FlowTask.NextFrame();
            s.Cancel();
            Log.Add("canceled S at frame " + World.DefaultClock.FrameCount);
        }

        async FlowTask Fade()
        {
            await FlowTask.NextFrame();
            Log.Add("faded at frame " + World.DefaultClock.FrameCount);
        }

        async FlowTask S()
        {
            try
            {
                await FlowTask.NextFrame();
                Log.Add("S resumed");
            }
            finally
            {
                if (parked) await FlowTask.NextFrame();
                else await Fade();
                Log.Add("S cleaned up at frame " + World.DefaultClock.FrameCount);
            }
        }

        World.Run(Canceler()); // before S in the tick list
        s = World.Run(S());
        Tick(3);
        if (parked) AssertLog("canceled S at frame 1", "S cleaned up at frame 2");
        else AssertLog("canceled S at frame 1", "faded at frame 2", "S cleaned up at frame 2");
        AssertNoExceptions();
        AssertCanceled(s, CancelCause.Explicit);
    }

    // ------------------------------------------------------------------ a canceled Join

    [Test]
    public void AJoinBranchCanceledBeforeItsTargetEndsCompletesNothing()
    {
        // The Race fails outside a flush: it cancels its Join branch, whose unwind waits for the next flush, and its end
        // waits for that branch. Meanwhile the joined flow, in another World of this thread, succeeds and tells the Join.
        // The canceled Join completes nothing, and the Race ends once the Join has unwound.
        CaptureExceptions();
        using var other = new FlowWorld("Other");
        var failer = new Signal<int>("Failer");

        async FlowTask<int> Target()
        {
            await FlowTask.NextFrame();
            return 1;
        }

        var target = other.Run(Target());

        async FlowTask Waiter()
        {
            try
            {
                await FlowTask.Race(failer.Next(), target.Join());
            }
            catch (SignalClosedException)
            {
                Log.Add("caught");
            }
        }

        var waiter = World.Run(Waiter());
        failer.Close(new InvalidOperationException("boom"));
        other.Tick(Dt);
        Assert.That(target.Status, Is.EqualTo(FlowStatus.Succeeded));
        Tick();
        AssertLog("caught");
        AssertSucceeded(waiter);
        AssertNoExceptions();
    }

    // ------------------------------------------------------------------ a combinator released as it starts

    public enum SyncSettle
    {
        /// <summary>The first branch of the inner Race completes as it starts.</summary>
        RaceWinner,

        /// <summary>The first branch of the inner WhenAll throws as it starts.</summary>
        WhenAllFailure,
    }

    [Test]
    public void ACombinatorReleasedWhileItStartsItsBranchesIsNotTouchedAgain([Values] SyncSettle settle)
    {
        // The inner combinator settles while it starts its first branch, and WithoutResult releases it at once. That
        // settles the outer Race, which unwinds its loser: the loser's finally awaits a combinator of the same type, which
        // the pool gives the node just released. Only then does the inner combinator's start return: it must not go on
        // starting branches of the node, which another flow uses now.
        CaptureExceptions();

        async FlowTask Quick()
        {
        }

        async FlowTask Throws() => throw new InvalidOperationException("sync");

        async FlowTask Save(string what)
        {
            await FlowTask.NextFrame();
            Log.Add("saved " + what);
        }

        async FlowTask Loser()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                if (settle == SyncSettle.RaceWinner) await FlowTask.Race(Save("a"), Save("b"));
                else await FlowTask.WhenAll(Save("a"), Save("b"));
                Log.Add("loser cleaned up");
            }
        }

        FlowTask Inner()
        {
            if (settle == SyncSettle.RaceWinner) return FlowTask.Race(Quick(), FlowTask.Never()).WithoutResult();
            FlowTask<FlowUnit> whenAll = FlowTask.WhenAll(Throws(), FlowTask.Never());
            return whenAll.WithoutResult();
        }

        async FlowTask Root()
        {
            try
            {
                await FlowTask.Race(Loser(), Inner());
                Log.Add("raced");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("threw " + e.Message);
            }
        }

        var h = World.Run(Root());
        Tick(2);
        if (settle == SyncSettle.RaceWinner) AssertLog("saved a", "loser cleaned up", "raced");
        else AssertLog("saved a", "saved b", "loser cleaned up", "threw sync");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    // ------------------------------------------------------------------ a combinator whose start threw while its end is held

    [Test]
    public void ACombinatorWhoseLaterBranchCannotStartStaysInTheTreeUntilItsEarlierBranchEnds()
    {
        // The second branch cannot start (its clock was removed): the Race ends, and its await throws the misuse, which the
        // caller catches. The first branch, started already, unwinds, and its finally saves: the Race's end waits for it,
        // as any node's end waits for its children. Meanwhile the Race is still in the tree: it goes back to its pool only
        // once it has left it.
        CaptureExceptions();

        async FlowTask<Clock> TempClock()
        {
            var c = Flow.CreateClock("Temp");
            await FlowTask.NextFrame();
            return c; // removed when this scope ends
        }

        async FlowTask First()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                await FlowTask.WaitForSeconds(0.1);
                Log.Add("first saved");
            }
        }

        async FlowTask Other()
        {
            await FlowTask.WaitForSeconds(0.3);
            Log.Add("other waited");
        }

        async FlowTask Root()
        {
            var removed = await TempClock();
            try
            {
                await FlowTask.Race(First(), FlowTask.WaitForSeconds(1.0, removed));
            }
            catch (FlowMisuseException)
            {
                Log.Add("race could not start");
            }

            await FlowTask.WaitForSeconds(0.5);
            Log.Add("root done");
        }

        var h = World.Run(Root());
        World.Run(Other());
        TickFor(1.0);
        AssertLog("race could not start", "first saved", "other waited", "root done");
        AssertNoExceptions();
        AssertSucceeded(h);
        Assert.That(World.Diagnostics.Root.Children, Is.Empty, World.Dump());
    }

    // ------------------------------------------------------------------ stale copies of a task or an awaiter

    [Test]
    public void NamingOrClockingAStartedOrStaleTaskThrows()
    {
        // A task can be named or given a clock only before it starts. A copy kept after the task ended refers to a pooled
        // node that a later task may use: it must not rename that task or change its clock.
        FlowTask stale = default;
        var starts = new List<string>();

        async FlowTask Step()
        {
            starts.Add(Flow.CurrentScopePath + " on " + Flow.CurrentClock.Name);
            await FlowTask.NextFrame();
        }

        async FlowTask Root1()
        {
            stale = Step();
            await stale; // Step ends and its node goes back to its pool
        }

        async FlowTask Root2() => await Step(); // takes the same node

        World.Run(Root1());
        Assert.Throws<FlowMisuseException>(() => Flow.Named("Started", stale));
        Assert.Throws<FlowMisuseException>(() => Flow.WithClock(World.UnscaledClock, stale));
        Tick(2);
        Assert.Throws<FlowMisuseException>(() => Flow.Named("Stale", stale));
        Assert.Throws<FlowMisuseException>(() => Flow.WithClock(World.UnscaledClock, stale));
        World.Run(Root2());
        Tick(2);
        Assert.That(starts, Is.EqualTo(new[] { "Root1 > Step on Default", "Root2 > Step on Default" }));
    }

    readonly struct StoredAwaiter
    {
        readonly FlowTask.Awaiter _awaiter;

        public StoredAwaiter(FlowTask.Awaiter awaiter) => _awaiter = awaiter;

        public FlowTask.Awaiter GetAwaiter() => _awaiter;
    }

    [Test]
    public void AStaleAwaiterUsedAgainThrowsInsteadOfDamagingAnotherTask([Values] bool parked)
    {
        // An awaiter kept and awaited a second time: its task ended, and its node went back to its pool, where an unrelated
        // task took it. The second await throws FlowMisuseException and leaves that task alone, whether the first await was
        // a wait the scope waited for itself (parked; the other task has not started) or a task of its own (the other
        // task runs).
        CaptureExceptions();
        FlowTask victimTask = default;
        Exception misuse = null;

        async FlowTask Child() => await FlowTask.NextFrame();

        async FlowTask Victim()
        {
            await victimTask;
            Log.Add("victim resumed");
        }

        async FlowTask Misuser()
        {
            var a = parked ? FlowTask.WaitForSeconds(0.1).GetAwaiter() : Child().GetAwaiter();
            await new StoredAwaiter(a);
            victimTask = parked ? FlowTask.WaitForSeconds(0.1) : Child();
            if (!parked) World.Run(Victim());
            try
            {
                await new StoredAwaiter(a);
            }
            catch (FlowMisuseException e)
            {
                misuse = e;
            }

            if (parked) World.Run(Victim());
        }

        World.Run(Misuser());
        TickFor(0.5);
        AssertLog("victim resumed");
        Assert.That(misuse, Is.Not.Null, "the stale awaiter was used without FlowMisuseException");
        AssertNoExceptions();
    }

    // ------------------------------------------------------------------ step 3: the walk of the tick list

    [Test]
    public void AWaitSatisfiedInATickResumesInItWhenAnEarlierWaitsConditionUnwoundTheOneBefore([Values] bool parked)
    {
        // Step 3 walks a snapshot of the tick list: A's condition runs a flow that cancels C, which takes C's wait out of
        // the list; D, after C, is still evaluated in this Tick.
        CaptureExceptions();
        FlowHandle c = default;
        var fire = false;

        async FlowTask Canceler()
        {
            c.Cancel(); // inside FlowWorld.Run: unwinds C on the spot
            Log.Add("canceled C");
        }

        bool Condition()
        {
            if (fire)
            {
                fire = false;
                World.Run(Canceler());
            }

            return false;
        }

        async FlowTask A() => await FlowTask.WaitUntil(Condition);

        async FlowTask Waiter(string name)
        {
            if (parked) await FlowTask.NextFrame();
            else await FlowTask.NextFrame(World.DefaultClock);
            Log.Add(name + " at frame " + World.DefaultClock.FrameCount);
        }

        World.Run(A()); // tick list: A, C, D
        c = World.Run(Waiter("C"));
        World.Run(Waiter("D"));
        fire = true;
        Tick(3);
        AssertLog("canceled C", "D at frame 1");
        AssertNoExceptions();
    }

    // ------------------------------------------------------------------ signals and subscriptions

    // A value type used by this test only, so that its subscriptions have a pool of their own (pools are LIFO).
    struct Recycled
    {
        public int V;
    }

    [Test]
    public void ClosingASignalDoesNotCloseASubscriptionOfAnotherSignalThatReusesAnEndedOne()
    {
        // S has two subscriptions. Closing S tells the first one, whose waiter is a Race branch: the Race fails and unwinds
        // its other branch at once (the close runs in flow code). That branch owned S's second subscription, which ends
        // there, and its finally starts a flow that subscribes to another signal, S2: the pooled subscription is reused
        // for S2. The close must not then tell that subscription (it belongs to S2 now) that S closed.
        CaptureExceptions();
        var s = new Signal<Recycled>("S");
        var s2 = new Signal<Recycled>("S2");

        async FlowTask Consumer()
        {
            using var sub2 = s2.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            var (received, v) = await sub2.NextOrClosed();
            Log.Add(received ? "consumer got " + v.V : "consumer: S2 closed");
        }

        async FlowTask Branch()
        {
            try
            {
                using var second = s.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
                await FlowTask.Never();
            }
            finally
            {
                World.Run(Consumer());
            }
        }

        async FlowTask Waiter()
        {
            using var first = s.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            try
            {
                await FlowTask.Race(first.Next(), Branch());
            }
            catch (SignalClosedException)
            {
                Log.Add("waiter: S closed");
            }
        }

        async FlowTask Closer()
        {
            await FlowTask.NextFrame();
            s.Close();
        }

        World.Run(Waiter());
        World.Run(Closer());
        Tick();
        Assert.That(s2.IsClosed, Is.False);
        s2.Emit(new Recycled { V = 5 });
        Tick();
        AssertLog("waiter: S closed", "consumer got 5");
        AssertNoExceptions();
    }

    [Test]
    public void DisposingASubscriptionWhoseWaitLostARaceDoesNotAllocate()
    {
        // A subscription per round (per state of an AI, say) whose wait is a leaf loser of a Race most frames. Race
        // resolution with leaf losers and early-disposed subscriptions are both steady-state paths that allocate nothing;
        // together they allocate nothing either.
        var hits = new Signal<int>();
        var wins = 0;

        async FlowTask Loop()
        {
            while (true)
            {
                using var sub = hits.Subscribe(BufferPolicy.Latest);
                var r = await FlowTask.Race(sub.Next(), FlowTask.NextFrame());
                if (r.Index == 0) wins++;
            }
        }

        World.Run(Loop());
        AssertSteadyStateAllocationFree(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                if (i % 3 == 0) hits.Emit(i);
                World.Tick(Dt);
            }
        });
        Assert.That(wins, Is.GreaterThan(0));
    }

    // ------------------------------------------------------------------ FlowProperty

    static async FlowTask Await(FlowTask<int> task) => await task;

    [Test]
    public void AWaitUntilThatHoldsAtOnceBindsThePropertyToItsWorld()
    {
        // The first use by a World binds the property to that World's thread: a wait in a flow is such a use, also when its
        // condition holds at once. Afterwards a Set from another thread is refused before it changes the value.
        var property = new FlowProperty<int>(1);
        var h = World.Run(Await(property.WaitUntil(v => v > 0)));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Exception fromOtherThread = null;
        var thread = new Thread(() =>
        {
            try
            {
                property.Set(2);
            }
#pragma warning disable CA1031 // the helper thread reports what it threw to the test's thread
            catch (Exception e)
#pragma warning restore CA1031
            {
                fromOtherThread = e;
            }
        });
        thread.Start();
        TestThreads.JoinOrFail(thread);
        Assert.That(fromOtherThread, Is.InstanceOf<FlowThreadException>());
        Assert.That(property.Value, Is.EqualTo(1));
    }

    // ------------------------------------------------------------------ a deep chain of awaits

    public enum DeepChainEnd
    {
        Canceled,
        WorldDisposed,
        Completed,
        Dumped,
    }

    [Test]
    public void ADeepChainOfAwaitsEndsWithoutRecursingByItsDepth([Values] DeepChainEnd end)
    {
        // Each method waits a frame and awaits the next one: after 20,000 frames the tree is 20,000 scopes deep. Canceling
        // it, disposing its World, completing it and dumping it walk the tree without recursing once per level, on a
        // thread with a 1 MB stack.
        const int depth = 20_000;
        OnThreadWithSmallStack(() =>
        {
            var world = new FlowWorld();
            var bottom = false;
            var finallies = 0;

            async FlowTask Level(int n)
            {
                try
                {
                    await FlowTask.NextFrame();
                    if (n == depth)
                    {
                        bottom = true;
                        if (end != DeepChainEnd.Completed) await FlowTask.Never();
                        return;
                    }

                    await Level(n + 1);
                }
                finally
                {
                    finallies++;
                }
            }

            try
            {
                var h = world.Run(Level(1));
                for (var i = 0; i < depth + 2 && !bottom; i++) world.Tick(Dt);
                Assert.That(bottom, Is.True, "the chain did not reach its bottom");
                switch (end)
                {
                    case DeepChainEnd.Canceled:
                        h.Cancel();
                        world.Flush();
                        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
                        break;
                    case DeepChainEnd.WorldDisposed:
                        world.Dispose();
                        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
                        break;
                    case DeepChainEnd.Completed:
                        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
                        break;
                    case DeepChainEnd.Dumped:
                        var dump = world.Dump();
                        Assert.That(dump.Split('\n').Length, Is.GreaterThan(depth));
                        Assert.That(dump.Length, Is.LessThan(depth * 1000), "the indentation of a deep tree is bounded");
                        world.Dispose();
                        break;
                }

                Assert.That(finallies, Is.EqualTo(depth));
            }
            finally
            {
                world.Dispose();
            }
        });
    }

    /// <summary>Runs <paramref name="body"/> on a thread with a 1 MB stack and rethrows what it threw.</summary>
    static void OnThreadWithSmallStack(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
#pragma warning disable CA1031 // the helper thread reports what it threw to the test's thread
            catch (Exception e)
#pragma warning restore CA1031
            {
                failure = e;
            }
        }, 1024 * 1024);
        thread.Start();
        TestThreads.JoinOrFail(thread);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // ------------------------------------------------------------------ a task passed to two combinators

    [Test]
    public void ACombinatorReleasedUnstartedLeavesABranchAnotherCombinatorStarted()
    {
        // The same Next() goes to a WhenAll and to a Race. The WhenAll starts it; the Race is discarded unstarted. The Race
        // must not release the branch the WhenAll runs: the WhenAll receives the emitted value.
        var sig = new Signal<int>();

        static async FlowTask<int> ValueAfter(int frames, int value)
        {
            await FlowTask.DelayFrames(frames);
            return value;
        }

        var shared = sig.Next();
        var all = FlowTask.WhenAll(new[] { shared, ValueAfter(3, 7) });
        var race = FlowTask.Race(new[] { shared, ValueAfter(100, 9) });
        var h = World.Run(all);
        race.Discard();
        sig.Emit(42);
        Tick(5);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.Result, Is.EqualTo(new[] { 42, 7 }));
    }

    // ------------------------------------------------------------------ the synchronous-completion limit

    [Test]
    public void TheSynchronousCompletionLimitCountsPerFlushToo()
    {
        // A World driven only by Flush: a long-lived flow that completes 600,000 awaits synchronously per resume is not an
        // endless loop. The limit counts per Tick, Flush or Run, not over the life of the scope.
        CaptureExceptions();
        var sig = new Signal<int>();
        var rounds = 0;

        static async FlowTask Sync()
        {
        }

        async FlowTask Loop()
        {
            while (true)
            {
                await sig.Next();
                for (var i = 0; i < 600_000; i++) await Sync();
                rounds++;
            }
        }

        var h = World.Run(Loop());
        for (var i = 0; i < 3; i++)
        {
            sig.Emit(0);
            World.Flush();
        }

        Assert.That(rounds, Is.EqualTo(3));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        AssertNoExceptions();
    }

    // ------------------------------------------------------------------ derived signals closed by the user

    [Test]
    public void ClosingFlowPropertyChangedLeavesSetWorking()
    {
        var property = new FlowProperty<int>(0);
        var h = World.Run(Await(property.WaitUntil(v => v == 2)));
        property.Changed.Close();
        Assert.DoesNotThrow(() => property.Set(1));
        Assert.DoesNotThrow(() => property.Set(2));
        World.Flush();
        Assert.That(property.Value, Is.EqualTo(2));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    // ------------------------------------------------------------------ a bridge's result held by a handle

    [Test]
    public void ABridgeStartedAsARootFlowKeepsItsResultForTheHandle()
    {
        // The handle may read Result at any time, so the value is the handle's: onDiscard does not take it.
        var discarded = new List<int>();
        var tcs = new TaskCompletionSource<int>();
        var h = World.Run(FlowBridge.FromTask(_ => tcs.Task, discarded.Add).ToFlowTask());
        tcs.SetResult(5);
        Tick(3);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.Result, Is.EqualTo(5));
        Assert.That(discarded, Is.Empty);
    }

    // ------------------------------------------------------------------ AsTask on another thread

    [Test]
    public void AsTaskFromAnotherThreadIsRefused()
    {
        var h = World.Run(FlowTask.Never());
        Exception fromOtherThread = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = h.AsTask();
            }
#pragma warning disable CA1031 // the helper thread reports what it threw to the test's thread
            catch (Exception e)
#pragma warning restore CA1031
            {
                fromOtherThread = e;
            }
        });
        thread.Start();
        TestThreads.JoinOrFail(thread);
        Assert.That(fromOtherThread, Is.InstanceOf<FlowThreadException>());
    }

    // ------------------------------------------------------------------ the cause of a failed join

    [Test]
    public void AJoinOfAFaultedFlowCarriesItsException()
    {
        CaptureExceptions();
        var boom = new InvalidOperationException("boom");

        async FlowTask Fails()
        {
            await FlowTask.NextFrame();
            throw boom;
        }

        var target = World.Run(Fails());
        Exception joined = null;

        async FlowTask Joiner()
        {
            try
            {
                await target.Join();
            }
            catch (FlowJoinException e)
            {
                joined = e;
            }
        }

        World.Run(Joiner());
        Tick(2);
        Assert.That(joined, Is.Not.Null);
        Assert.That(joined.InnerException, Is.SameAs(boom));
    }

    [Test]
    public void AJoinOfAHandleWithNoTaskNamesNoEmptyTask()
    {
        CaptureExceptions();
        Exception joined = null;

        async FlowTask Joiner()
        {
            try
            {
                await default(FlowHandle).Join();
            }
            catch (FlowJoinException e)
            {
                joined = e;
            }
        }

        World.Run(Joiner());
        Assert.That(joined, Is.Not.Null);
        Assert.That(joined.Message, Does.Not.Contain("''"));
    }

    // ------------------------------------------------------------------ an event bridge bound after its creation

    [Test]
    public void AnEventBridgeMadeOutsideAFlowTakesOffThreadCallbacksThroughTheInbox()
    {
        // Made outside a flow, the bridge's signal is bound by the first World that waits on it. A callback raised on
        // another thread afterwards goes through that World's inbox instead of throwing on the event's thread.
        Action<int> raise = null;
        var ev = FlowBridge.FromCallback<int>(emit =>
        {
            raise = emit;
            return () => raise = null;
        });
        var h = World.Run(Await(ev.Next()));
        Exception fromOtherThread = null;
        var thread = new Thread(() =>
        {
            try
            {
                raise(9);
            }
#pragma warning disable CA1031 // the helper thread reports what it threw to the test's thread
            catch (Exception e)
#pragma warning restore CA1031
            {
                fromOtherThread = e;
            }
        });
        thread.Start();
        TestThreads.JoinOrFail(thread);
        Assert.That(fromOtherThread, Is.Null);
        World.Flush();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        ev.Dispose();
    }
}
