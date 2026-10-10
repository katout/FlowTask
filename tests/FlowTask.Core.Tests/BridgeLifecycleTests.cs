using System.Threading;

namespace Katout.FlowTask.Tests;

/// <summary>
/// The life of a bridged Task: when its completion reaches the World, where its result goes when no
/// flow takes it, and what happens to failures and results that arrive after the bridge stopped waiting.
/// </summary>
public class BridgeLifecycleTests : FlowTestBase
{
    /// <summary>
    /// Stands in for Unity's or Godot's main-thread context: a derived SynchronizationContext that runs posted work
    /// later, elsewhere. .NET does not inline await continuations under such a context.
    /// </summary>
    sealed class DeferringContext : SynchronizationContext
    {
        int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    public enum BridgeKind
    {
        FromTaskOfT,
        FromTask,
        AsFlowOfTask,
        AsFlowOfValueTaskOfT,
    }

    static async FlowTask AwaitBridge(BridgeKind kind, TaskCompletionSource<int> tcs)
    {
        switch (kind)
        {
            case BridgeKind.FromTaskOfT:
                await FlowBridge.FromTask(_ => tcs.Task);
                break;
            case BridgeKind.FromTask:
                await FlowBridge.FromTask((Func<CancellationToken, Task>)(_ => tcs.Task));
                break;
            case BridgeKind.AsFlowOfTask:
                await ((Task)tcs.Task).AsFlow();
                break;
            default:
                await new ValueTask<int>(tcs.Task).AsFlow();
                break;
        }
    }

    // ------------------------------------------------------------------ when the completion arrives

    [TestCase(BridgeKind.FromTaskOfT)]
    [TestCase(BridgeKind.FromTask)]
    [TestCase(BridgeKind.AsFlowOfTask)]
    [TestCase(BridgeKind.AsFlowOfValueTaskOfT)]
    public void BridgedTaskCompletedUnderASynchronizationContextResumesAtTheNextTick(BridgeKind kind)
    {
        // The Task is completed on the World thread between Ticks, as a game's own callback would, under a context
        // like Unity's. The completion must reach the inbox before SetResult returns, so the next Tick resumes.
        var context = new DeferringContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            for (var round = 0; round < 20; round++)
            {
                var tcs = new TaskCompletionSource<int>();
                var done = false;

                async FlowTask Waiter()
                {
                    await AwaitBridge(kind, tcs);
                    done = true;
                }

                World.Run(Waiter());
                Tick();
                tcs.SetResult(1);
                var ticks = 0;
                while (!done && ticks < 1000)
                {
                    Tick();
                    ticks++;
                }

                Assert.That(ticks, Is.EqualTo(1), $"round {round}: Ticks until the flow resumed");
            }

            Assert.That(context.Posts, Is.EqualTo(0), "posts to the SynchronizationContext");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // ------------------------------------------------------------------ factory errors

    [Test]
    public void FactoryReturningNullIsReportedForBothBridgeKinds()
    {
        CaptureExceptions();

        async FlowTask Typed() => await FlowBridge.FromTask<int>(_ => null);

        async FlowTask Untyped() => await FlowBridge.FromTask((Func<CancellationToken, Task>)(_ => null));

        World.Run(Typed());
        World.Run(Untyped());
        Tick();
        Assert.That(Exceptions.Count, Is.EqualTo(2));
        foreach (var p in Exceptions)
        {
            Assert.That(p.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(p.Exception.Message, Does.Contain("factory returned null"));
        }
    }

    // ------------------------------------------------------------------ AsTask

    /// <summary>Runs <paramref name="body"/> with no SynchronizationContext, as in MonoGame or a plain .NET host.</summary>
    static void WithoutSynchronizationContext(Action body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Test]
    public void AsTaskContinuationRunsOnTheWorldThreadAfterTheFlush()
    {
        WithoutSynchronizationContext(() =>
        {
            var worldThread = Environment.CurrentManagedThreadId;
            var thread = -1;
            var executing = true;

            async FlowTask<int> Work()
            {
                await FlowTask.NextFrame();
                Log.Add("flow end");
                return 5;
            }

            var h = World.Run(Work());

            async Task Consumer()
            {
                var v = await h.AsTask();
                thread = Environment.CurrentManagedThreadId;
                executing = World.IsExecuting;
                Log.Add("consumer " + v);
            }

            var consumer = Consumer();
            Assert.That(consumer.IsCompleted, Is.False);
            Tick();
            Assert.That(consumer.IsCompleted, Is.True, "the continuation ran before Tick returned");
            Assert.That(thread, Is.EqualTo(worldThread));
            Assert.That(executing, Is.False, "not inside the flush");
            AssertLog("flow end", "consumer 5");
        });
    }

    [Test]
    public void AsTaskContinuationThatTicksTheWorldCompletesTheTasksEndedThere()
    {
        WithoutSynchronizationContext(() =>
        {
            async FlowTask First() => await FlowTask.NextFrame();

            async FlowTask Second()
            {
                await FlowTask.NextFrame();
                await FlowTask.NextFrame();
            }

            var h1 = World.Run(First());
            var h2 = World.Run(Second());

            async Task Driver()
            {
                await h1.AsTask();
                Log.Add("first");
                World.Tick(Dt); // ends Second, whose AsTask this loop then completes
                Log.Add("ticked");
            }

            async Task Watcher()
            {
                await h2.AsTask();
                Log.Add("second");
            }

            var driver = Driver();
            var watcher = Watcher();
            Tick();
            Assert.That(driver.IsCompleted && watcher.IsCompleted, Is.True);
            AssertLog("first", "ticked", "second");
        });
    }

    [Test]
    public void UnhandledExceptionsOfATickAreThrownByThatTickNotByARunInAnAsTaskContinuation()
    {
        WithoutSynchronizationContext(() =>
        {
            // No OnUnhandledException: Tick throws the unhandled exceptions of its flows. The AsTask continuations run at the end of that Tick;
            // a Run, Tick or Flush they call is part of it and leaves the throwing to it.
            async FlowTask Ends() => await FlowTask.NextFrame();

            async FlowTask Bug()
            {
                await FlowTask.NextFrame();
                throw new FormatException("bug");
            }

            async FlowTask NextStage()
            {
                await FlowTask.NextFrame();
                throw new InvalidOperationException("next stage");
            }

            var ends = World.Run(Ends());
            World.Run(Bug());
            Exception inContinuation = null;
            var nextStage = default(FlowHandle);

            async Task Boot()
            {
                await ends.AsTask();
                try
                {
                    nextStage = World.Run(NextStage());
                    World.Flush();
                }
                catch (Exception ex)
                {
                    inContinuation = ex;
                }
            }

            var boot = Boot();
            var thrown = Assert.Throws<FlowUnhandledException>(() => Tick());
            Assert.That(boot.IsCompleted, Is.True);
            Assert.That(inContinuation, Is.Null, "the Tick's exception went to the continuation's Run");
            Assert.That(thrown.ExceptionInfos.Count, Is.EqualTo(1));
            Assert.That(thrown.ExceptionInfos[0].Exception, Is.TypeOf<FormatException>());
            Assert.That(nextStage.Status, Is.EqualTo(FlowStatus.Running));

            // An unhandled exception of a flow the continuation started is thrown by the next top-level call, as usual.
            thrown = Assert.Throws<FlowUnhandledException>(() => Tick());
            Assert.That(thrown.ExceptionInfos.Count, Is.EqualTo(1));
            Assert.That(thrown.ExceptionInfos[0].Exception, Is.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void ExceptionsRaisedInAnAsTaskContinuationAreThrownByTheOuterCallWithItsOwn()
    {
        WithoutSynchronizationContext(() =>
        {
            // No OnUnhandledException. The continuation runs at the end of the Tick; a Run and a Flush it calls raise unhandled exceptions
            // there. They do not throw them into the continuation: the Tick throws them once, with its own.
            var fail = true;
            var go = new Signal<int>(World, "go");

            async FlowTask Ends() => await FlowTask.NextFrame();

            async FlowTask Bug()
            {
                await FlowTask.NextFrame();
                throw new FormatException("the Tick's own");
            }

            async FlowTask FailsAtStart()
            {
                if (fail) throw new InvalidOperationException("started in the continuation");
                await FlowTask.NextFrame();
            }

            async FlowTask FailsWhenResumed()
            {
                await go.Next();
                throw new ArgumentException("resumed by the continuation's Flush");
            }

            var ends = World.Run(Ends());
            World.Run(Bug());
            World.Run(FailsWhenResumed());
            Exception inContinuation = null;

            async Task Boot()
            {
                await ends.AsTask();
                try
                {
                    World.Run(FailsAtStart());
                    go.Emit(1);
                    World.Flush();
                    Log.Add("continuation went on");
                }
                catch (Exception ex)
                {
                    inContinuation = ex;
                }
            }

            var boot = Boot();
            var thrown = Assert.Throws<FlowUnhandledException>(() => Tick());
            Assert.That(boot.IsCompleted, Is.True);
            Assert.That(inContinuation, Is.Null);
            AssertLog("continuation went on");
            var types = new List<Type>();
            foreach (var report in thrown.ExceptionInfos) types.Add(report.Exception.GetType());
            Assert.That(types, Is.EquivalentTo(new[] { typeof(FormatException), typeof(InvalidOperationException), typeof(ArgumentException) }));

            // Thrown once: the next top-level calls have nothing left to throw.
            Assert.DoesNotThrow(() => Tick());
            Assert.DoesNotThrow(() => World.Flush());
        });
    }

    [Test]
    public void AsTaskOfAFlowThatAlreadyEndedIsCompleted()
    {
        CaptureExceptions();

        async FlowTask<int> Instant() => 7;

        async FlowTask Failing()
        {
            await FlowTask.NextFrame();
            throw new FormatException("bug");
        }

        async FlowTask Waiting() => await FlowTask.Never();

        var done = World.Run(Instant());
        var failed = World.Run(Failing());
        var canceled = World.Run(Waiting());
        canceled.Cancel();
        Tick();

        var t1 = done.AsTask();
        Assert.That(t1.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        Assert.That(t1.Result, Is.EqualTo(7));
        var t2 = failed.AsTask();
        Assert.That(t2.IsFaulted, Is.True);
        Assert.That(t2.Exception.InnerException, Is.TypeOf<FormatException>());
        Assert.That(canceled.AsTask().IsCanceled, Is.True);
    }

    [Test]
    public void AsTaskIsCompletedWhenTheWorldIsDisposed()
    {
        WithoutSynchronizationContext(() =>
        {
            var worldThread = Environment.CurrentManagedThreadId;
            var thread = -1;
            var disposed = false;

            async FlowTask Waiting() => await FlowTask.Never();

            var task = World.Run(Waiting()).AsTask();
            var continuation = task.ContinueWith(_ =>
            {
                thread = Environment.CurrentManagedThreadId;
                disposed = World.IsDisposed;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            Tick();
            Assert.That(task.IsCompleted, Is.False);
            World.Dispose();
            Assert.That(task.IsCanceled, Is.True);
            Assert.That(continuation.IsCompleted, Is.True, "ran during Dispose");
            Assert.That(thread, Is.EqualTo(worldThread));
            Assert.That(disposed, Is.True, "after the World is disposed");
        });
    }

    [Test]
    public void AsTaskOfAWaitStartedWithRunCompletes()
    {
        // A wait passed to FlowWorld.Run directly completes through its delivery, not through the end of a scope.
        var sig = new Signal<int>(World, "sig");
        var tcs = new TaskCompletionSource<int>();
        var next = World.Run(sig.Next());
        var seconds = World.Run(FlowTask.WaitForSeconds(0.01));
        var frame = World.Run(FlowTask.NextFrame());
        var bridge = World.Run(FlowBridge.FromTask(_ => tcs.Task).ToFlowTask());
        var tasks = new Task[] { next.AsTask(), seconds.AsTask(), frame.AsTask(), bridge.AsTask() };

        // A root wait can also be joined: the join is its awaiter, and the AsTask Task must complete as well.
        var joined = World.Run(sig.Next());
        var joinedTask = joined.AsTask();

        async FlowTask Joiner() => Log.Add("joined " + await joined.Join());

        World.Run(Joiner());
        sig.Emit(4);
        tcs.SetResult(9);
        TickFor(0.05);

        foreach (var t in tasks) Assert.That(t.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        Assert.That(((Task<int>)tasks[0]).Result, Is.EqualTo(4));
        Assert.That(((Task<int>)tasks[3]).Result, Is.EqualTo(9));
        Assert.That(joinedTask.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        Assert.That(joinedTask.Result, Is.EqualTo(4));
        AssertLog("joined 4");
    }

    [Test]
    public void AsTaskOfAWaitStartedWithRunCompletesWhenTheWaitCompletesNotWhenItIsDelivered()
    {
        // The handle of a root wait is Succeeded as soon as the wait completes, and its AsTask completes at the end of that
        // call, even when the delivery itself is held by a paused clock, or never comes (the World is disposed first).
        var clock = World.CreateClock("paused");
        var sig = new Signal<int>(World, "sig");
        var held = new Signal<int>(World, "held");
        var h = World.Run(sig.Next());
        var heldHandle = World.Run(held.Next(), clock);
        var task = h.AsTask();
        var heldTask = heldHandle.AsTask();
        clock.Pause();
        held.Emit(6);
        Tick(); // the delivery of 'held' stays held by the pause
        Assert.That(heldHandle.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(heldTask.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        Assert.That(heldTask.Result, Is.EqualTo(6));

        sig.Emit(5); // outside a flow: the delivery waits for the next flush, which never comes
        World.Dispose();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(task.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        Assert.That(task.Result, Is.EqualTo(5));
    }

    // ------------------------------------------------------------------ what arrives after the bridge stopped waiting

    public enum LateBridgeKind
    {
        FromTask,
        AsFlow,
    }

    [TestCase(LateBridgeKind.FromTask)]
    [TestCase(LateBridgeKind.AsFlow)]
    public void BridgeFailureAfterCancellationIsUndelivered(LateBridgeKind kind)
    {
        // The external operation ignores the token and fails after the scope was canceled: the failure is reported once,
        // where the bridge was, instead of vanishing. No receiver could take it, so it is Undelivered.
        CaptureExceptions();
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            if (kind == LateBridgeKind.FromTask) await FlowBridge.FromTask(_ => tcs.Task);
            else await tcs.Task.AsFlow();
        }

        var h = World.Run(Root());
        Tick();
        h.Cancel();
        Tick();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(Exceptions, Is.Empty);

        var late = new FormatException("late");
        tcs.SetException(late);
        Tick();
        Assert.That(Exceptions.Count, Is.EqualTo(1));
        Assert.That(Exceptions[0].Kind, Is.EqualTo(FlowExceptionKind.Undelivered));
        Assert.That(Exceptions[0].Exception, Is.SameAs(late));
        Assert.That(Exceptions[0].ScopePath, Is.EqualTo("Root > Task<Int32>"));
    }

    [Test]
    public void ALateBridgeFailureKeepsThePathWhereItWasThrownAfterItsScopeIsReused()
    {
        // The bridge waits in Stage, a method awaited by the root flow, whose scope comes from a pool (a root flow's scope
        // is never pooled). Stage is canceled and ends, and its scope goes back to the pool; Stage then runs under another
        // flow and reuses it. When the first Task fails after that, the report names where that bridge waited, not where
        // the reused scope is now.
        CaptureExceptions();
        var first = new TaskCompletionSource<int>();
        var second = new TaskCompletionSource<int>();

        async FlowTask<int> Stage(TaskCompletionSource<int> tcs) => await FlowBridge.FromTask(_ => tcs.Task);

        async FlowTask Root() => await Stage(first);

        async FlowTask Retry() => Log.Add("retry got " + await Stage(second));

        var h = World.Run(Root());
        Tick();
        h.Cancel();
        Tick();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        var retry = World.Run(Retry());
        Tick();
        Assert.That(World.Diagnostics.Walk().Single(s => s.Name == "Stage").Path, Is.EqualTo("Retry > Stage"));

        var late = new FormatException("late");
        first.SetException(late);
        Tick();
        Assert.That(Exceptions.Count, Is.EqualTo(1));
        Assert.That(Exceptions[0].Kind, Is.EqualTo(FlowExceptionKind.Undelivered));
        Assert.That(Exceptions[0].Exception, Is.SameAs(late));
        Assert.That(Exceptions[0].ScopePath, Is.EqualTo("Root > Stage > Task<Int32>"));

        second.SetResult(2);
        Tick();
        AssertLog("retry got 2");
        Assert.That(retry.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void SecondBridgeFailureInTheSameIntakeIsReported()
    {
        // Both bridges of a WhenAll fail before the same Tick, and the intake queues both failures. The first one's
        // delivery fails the WhenAll, which releases the second bridge before its failure's turn in the queue. The second
        // failure has no receiver, so it is Undelivered; the first reaches the root, uncaught.
        CaptureExceptions();
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            await FlowTask.WhenAll(FlowBridge.FromTask(_ => a.Task).ToFlowTask(), FlowBridge.FromTask(_ => b.Task).ToFlowTask());
        }

        World.Run(Root());
        Tick();
        var failA = new FormatException("fail-A");
        var failB = new FormatException("fail-B");
        a.SetException(failA);
        b.SetException(failB);
        Tick();

        // Reports come as they are found, both in the flush: the second failure when the first one's delivery releases
        // its bridge, the first when the flush takes it up to the root.
        Assert.That(Exceptions.Count, Is.EqualTo(2));
        var undelivered = Exceptions[0];
        var report = Exceptions[1];
        Assert.That(undelivered.Exception, Is.SameAs(failB));
        Assert.That(undelivered.Kind, Is.EqualTo(FlowExceptionKind.Undelivered));
        Assert.That(undelivered.ScopePath, Is.EqualTo("Root > Task<Int32>"));
        Assert.That(report.Exception, Is.SameAs(failA));
        Assert.That(report.Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
    }

    [Test]
    public void LateCancellationOfTheExternalTaskIsSilent()
    {
        // A cancellation that arrives after the bridge stopped waiting is expected: the scope's token (or the source's
        // own cancellation) ended the operation. Some sources report it as a fault (UniTask's AsTask).
        CaptureExceptions();
        var canceled = new TaskCompletionSource<int>();
        var faultedWithCancellation = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            await FlowTask.WhenAll(FlowBridge.FromTask(_ => canceled.Task).ToFlowTask(), FlowBridge.FromTask(_ => faultedWithCancellation.Task).ToFlowTask());
        }

        var h = World.Run(Root());
        Tick();
        h.Cancel();
        Tick();
        canceled.SetCanceled();
        faultedWithCancellation.SetException(new OperationCanceledException());
        Tick();
        Assert.That(Exceptions, Is.Empty);
    }

    // ------------------------------------------------------------------ results nobody received: onDiscard

    [Test]
    public void OnDiscardReceivesAResultThatArrivesAfterCancellation()
    {
        // The operation ignores the token and still produces a result after the screen closed: it is released on the
        // World thread in the next intake.
        var worldThread = Environment.CurrentManagedThreadId;
        var thread = -1;
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Root() => Log.Add("got " + await FlowBridge.FromTask(_ => tcs.Task, v =>
        {
            thread = Environment.CurrentManagedThreadId;
            Log.Add("discard " + v);
        }));

        var h = World.Run(Root());
        Tick();
        h.Cancel();
        Tick();
        tcs.SetResult(42);
        AssertLog();
        Tick();
        AssertLog("discard 42");
        Assert.That(thread, Is.EqualTo(worldThread));
        Tick();
        AssertLog("discard 42");
    }

    [Test]
    public void OnDiscardReceivesTheValueOfABridgeThatLostARaceInTheSameIntake()
    {
        // Both finish before the same Tick; the signal's delivery was reserved first, so it wins and the bridge's value,
        // already delivered to the losing branch, is released.
        var sig = new Signal<int>(World, "sig");
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            var r = await FlowTask.Race(sig.Next(), FlowBridge.FromTask(_ => tcs.Task, v => Log.Add("discard " + v)).ToFlowTask());
            Log.Add("winner " + r.Index);
        }

        World.Run(Root());
        Tick();
        sig.Emit(1);
        tcs.SetResult(7);
        Tick(2);
        AssertLog("winner 0", "discard 7");
    }

    [Test]
    public void OnDiscardReceivesTheValueWhenTheAwaitingScopeUnwindsBeforeTakingIt()
    {
        // The value reaches the bridge in the same intake in which an outer Race cancels the scope awaiting it.
        var sig = new Signal<int>(World, "sig");
        var tcs = new TaskCompletionSource<int>();

        async FlowTask<int> Inner() => await FlowBridge.FromTask(_ => tcs.Task, v => Log.Add("discard " + v));

        async FlowTask Root()
        {
            var r = await FlowTask.Race(sig.Next(), Inner());
            Log.Add("winner " + r.Index);
        }

        World.Run(Root());
        Tick();
        sig.Emit(1);
        tcs.SetResult(7);
        Tick(2);
        AssertLog("winner 0", "discard 7");
    }

    [Test]
    public void OnDiscardReceivesTheValueWhenWhenAllFailsAfterItCompleted()
    {
        CaptureExceptions();
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            await FlowTask.WhenAll(
                FlowBridge.FromTask(_ => a.Task, v => Log.Add("discard " + v)).ToFlowTask(),
                FlowBridge.FromTask(_ => b.Task).ToFlowTask());
            Log.Add("unreachable");
        }

        World.Run(Root());
        Tick();
        a.SetResult(3);
        Tick();
        AssertLog();
        b.SetException(new FormatException("b failed"));
        Tick(2);
        AssertLog("discard 3");
        Assert.That(Exceptions.Count, Is.EqualTo(1));
        Assert.That(Exceptions[0].Exception, Is.TypeOf<FormatException>());
    }

    [Test]
    public void OnDiscardIsNotCalledForATakenValue()
    {
        CaptureExceptions();
        var discards = 0;
        Action<int> onDiscard = _ => discards++;
        var later = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            var sum = await FlowBridge.FromTask(_ => Task.FromResult(1), onDiscard); // synchronous completion
            sum += await FlowBridge.FromTask(_ => later.Task, onDiscard);            // delivered later
            var race = await FlowTask.Race(FlowBridge.FromTask(_ => Task.FromResult(3), onDiscard).ToFlowTask(), FlowTask.Never());
            sum += race.Value0;
            var all = await FlowTask.WhenAll(
                FlowBridge.FromTask(_ => Task.FromResult(4), onDiscard).ToFlowTask(),
                FlowBridge.FromTask(_ => Task.FromResult(5), onDiscard).ToFlowTask());
            sum += all.Item1 + all.Item2;
            Log.Add("sum " + sum);
        }

        World.Run(Root());
        Tick();
        later.SetResult(2);
        Tick(3);
        AssertLog("sum 15");
        Assert.That(discards, Is.EqualTo(0));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void OnDiscardReceivesAValueDiscardedWithWithoutResult()
    {
        // WithoutResult ignores the value as soon as the task succeeds: a bridge's goes to its onDiscard.
        async FlowTask Root()
        {
            await FlowBridge.FromTask(_ => Task.FromResult(8), v => Log.Add("discard " + v)).ToFlowTask().WithoutResult();
            Log.Add("done");
        }

        World.Run(Root());
        AssertLog("done");
        Tick();
        AssertLog("done", "discard 8");
    }

    [Test]
    public void OnDiscardLeavesTheResultOfABridgeStartedWithRunOrSpawnToItsHandle()
    {
        // The handle of a bridge started with FlowWorld.Run or Flow.Spawn keeps its result, readable at any time, even
        // after the parent ended and nobody reads it. onDiscard gets the result only when the bridge was canceled
        // before its result arrived (step 1 of the Tick or Flush after the Task finished), even if the Task had
        // already finished.
        var run = new TaskCompletionSource<int>();
        var spawned = new TaskCompletionSource<int>();
        var canceled = new TaskCompletionSource<int>();
        var finishedThenCanceled = new TaskCompletionSource<int>();
        var done = new Signal<int>(World, "done");
        var discarded = new List<int>();
        var spawnedHandle = default(FlowHandle<int>);

        async FlowTask Parent()
        {
            spawnedHandle = Flow.Spawn(FlowBridge.FromTask(_ => spawned.Task, discarded.Add).ToFlowTask());
            await done.Next();
        }

        var runHandle = World.Run(FlowBridge.FromTask(_ => run.Task, discarded.Add).ToFlowTask());
        var canceledHandle = World.Run(FlowBridge.FromTask(_ => canceled.Task, discarded.Add).ToFlowTask());
        var finishedThenCanceledHandle = World.Run(FlowBridge.FromTask(_ => finishedThenCanceled.Task, discarded.Add).ToFlowTask());
        var parent = World.Run(Parent());
        Tick();
        run.SetResult(1);
        spawned.SetResult(2);
        finishedThenCanceled.SetResult(4);
        finishedThenCanceledHandle.Cancel(); // the Task finished; its result arrives in step 1 of the next Tick
        Tick();
        Assert.That(discarded, Is.EqualTo(new[] { 4 }));
        done.Emit(0);
        canceledHandle.Cancel();
        Tick(2);
        canceled.SetResult(3);
        Tick(2);

        Assert.That(parent.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(runHandle.Result, Is.EqualTo(1));
        Assert.That(spawnedHandle.Result, Is.EqualTo(2));
        Assert.That(canceledHandle.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(finishedThenCanceledHandle.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(discarded, Is.EqualTo(new[] { 4, 3 }));
    }

    [Test]
    public void OnDiscardReceivesTheWinnersValueWhenALosersUnwindingCancelsTheRace()
    {
        // The bridge wins; the loser's finally cancels the flow that awaits the Race while the Race unwinds it. The Race
        // then ends canceled without a result, and the winner's value, which no one receives, goes to onDiscard.
        var tcs = new TaskCompletionSource<int>();
        FlowHandle root = default;

        async FlowTask Loser()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("loser cancels root");
                root.Cancel();
            }
        }

        async FlowTask Root() =>
            Log.Add("got " + (await FlowTask.Race(FlowBridge.FromTask(_ => tcs.Task, v => Log.Add("discard " + v)).ToFlowTask(), Loser())).Value0);

        root = World.Run(Root());
        tcs.SetResult(7);
        Tick(3);
        AssertLog("loser cancels root", "discard 7");
        Assert.That(root.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void OnDiscardRunsDuringWorldDisposeOnTheWorldThread()
    {
        // An operation that answers the scope's cancellation with a result (not with a cancellation) produces it while
        // Dispose unwinds; the result is released before Dispose returns.
        var worldThread = Environment.CurrentManagedThreadId;
        var thread = -1;
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Root() => await FlowBridge.FromTask(ct =>
        {
            ct.Register(() => tcs.TrySetResult(11));
            return tcs.Task;
        }, v =>
        {
            thread = Environment.CurrentManagedThreadId;
            Log.Add("discard " + v);
        });

        World.Run(Root());
        Tick();
        World.Dispose();
        AssertLog("discard 11");
        Assert.That(thread, Is.EqualTo(worldThread));
    }

    [Test]
    public void OnDiscardRunsOnTheCompletingThreadAfterWorldDispose()
    {
        var discards = 0;
        var thread = -1;
        var tcs = new TaskCompletionSource<int>();

        async FlowTask Root() => await FlowBridge.FromTask(_ => tcs.Task, v =>
        {
            discards++;
            thread = Environment.CurrentManagedThreadId;
        });

        World.Run(Root());
        Tick();
        World.Dispose();
        Assert.That(discards, Is.EqualTo(0));

        var completer = -1;
        var other = new Thread(() =>
        {
            completer = Environment.CurrentManagedThreadId;
            tcs.SetResult(12);
        });
        other.Start();
        TestThreads.JoinOrFail(other);
        Assert.That(discards, Is.EqualTo(1));
        Assert.That(thread, Is.EqualTo(completer));
    }

    [Test]
    public void OnDiscardExceptionAfterWorldDisposeIsDropped()
    {
        // With no World left to report it, the exception is dropped: it must not fault the Task continuation that ran
        // onDiscard, which the finalizer would raise as an unobserved Task exception.
        var thrown = new InvalidOperationException("onDiscard failed");
        var discards = 0;
        var unobserved = 0;
        var tcs = new TaskCompletionSource<int>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Contains(thrown)) Interlocked.Increment(ref unobserved);
        };

        async FlowTask Root() => await FlowBridge.FromTask(_ => tcs.Task, _ =>
        {
            discards++;
            throw thrown;
        });

        World.Run(Root());
        Tick();
        World.Dispose();

        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            var other = new Thread(() => tcs.SetResult(12));
            other.Start();
            TestThreads.JoinOrFail(other);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }

        Assert.That(discards, Is.EqualTo(1));
        Assert.That(Volatile.Read(ref unobserved), Is.EqualTo(0));
    }

    public enum DiscardKind
    {
        ArrivedLate,
        ReleasedUnread,
    }

    [TestCase(DiscardKind.ArrivedLate)]
    [TestCase(DiscardKind.ReleasedUnread)]
    public void OnDiscardExceptionIsACleanupException(DiscardKind kind)
    {
        CaptureExceptions();
        var tcs = new TaskCompletionSource<int>();
        var failure = new FormatException("discard failed");

        async FlowTask Root()
        {
            var bridge = FlowBridge.FromTask(_ => tcs.Task, _ => throw failure);
            if (kind == DiscardKind.ArrivedLate) await bridge;
            else await bridge.ToFlowTask().WithoutResult();
        }

        var h = World.Run(Root());
        Tick();
        if (kind == DiscardKind.ArrivedLate)
        {
            h.Cancel();
            Tick();
        }

        tcs.SetResult(1);
        Tick(2);
        Assert.That(Exceptions.Count, Is.EqualTo(1));
        Assert.That(Exceptions[0].Kind, Is.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(Exceptions[0].Exception, Is.SameAs(failure));
        Assert.That(Exceptions[0].ScopePath, Is.EqualTo("Root > Task<Int32>"));
    }

    [Test]
    public void OnDiscardDuringWorldDisposeCannotTickFlushDisposeOrStartAFlow()
    {
        // Dispose discards the inbox inside its own execution: a result that arrived after its bridge stopped waiting
        // goes to onDiscard there, where the World executes. Tick, Flush and Dispose are refused, and Run starts nothing.
        var tcs = new TaskCompletionSource<int>();
        var refused = new List<string>();
        FlowHandle started = default;
        var ran = false;

        async FlowTask Late()
        {
            ran = true;
            await FlowTask.NextFrame();
        }

        void OnDiscard(int v)
        {
            Log.Add("discard " + v);
            Refuse("Tick", () => World.Tick(Dt));
            Refuse("Flush", World.Flush);
            Refuse("Dispose", World.Dispose);
            started = World.Run(Late());
        }

        void Refuse(string name, Action call)
        {
            try
            {
                call();
            }
            catch (FlowMisuseException)
            {
                refused.Add(name);
            }
        }

        async FlowTask Load() => await FlowBridge.FromTask(_ => tcs.Task, OnDiscard);

        var h = World.Run(Load());
        Tick();
        h.Cancel();
        Tick();            // the bridge stops waiting
        tcs.SetResult(7);  // the result is queued for the next intake, which does not come
        World.Dispose();

        AssertLog("discard 7");
        Assert.That(refused, Is.EqualTo(new[] { "Tick", "Flush", "Dispose" }));
        Assert.That(started.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(ran, Is.False);
    }

    // ------------------------------------------------------------------ late results

    [Test]
    public void ALateBridgeResultGoesToOnDiscardAndIsOtherwiseDropped()
    {
        // The Tasks finish after their flows were canceled. A result goes to onDiscard when one was given; without it,
        // it is dropped, like a cancellation and a plain Task's end. Nothing is reported.
        CaptureExceptions();
        var plain = new TaskCompletionSource<int>();
        var released = new TaskCompletionSource<int>();
        var canceled = new TaskCompletionSource<int>();
        var untyped = new TaskCompletionSource<int>();

        async FlowTask Root()
        {
            await FlowTask.WhenAll(
                FlowBridge.FromTask(_ => plain.Task).ToFlowTask(),
                FlowBridge.FromTask(_ => released.Task, v => Log.Add("discard " + v)).ToFlowTask(),
                FlowBridge.FromTask(_ => canceled.Task).ToFlowTask());
        }

        async FlowTask Other() => await FlowBridge.FromTask((Func<CancellationToken, Task>)(_ => untyped.Task));

        var h1 = World.Run(Root());
        var h2 = World.Run(Other());
        Tick();
        h1.Cancel();
        h2.Cancel();
        Tick();
        plain.SetResult(1);
        released.SetResult(2);
        canceled.SetCanceled();
        untyped.SetResult(0);
        Tick();

        AssertLog("discard 2");
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void ALateCallbackToAnEndedEventSignalIsDropped()
    {
        // A rewarded ad: the screen that waited for the reward closes, and the ad SDK calls back later, once on the
        // World thread and once on its own thread. Nobody receives the reward (receive it in a flow that outlives the
        // screen): the callbacks are dropped without an exception or a report.
        CaptureExceptions();
        Action<int> grant = null;
        var detached = false;

        async FlowTask AdScreen()
        {
            var rewards = FlowBridge.FromCallback<int>(emit =>
            {
                grant = emit;
                return () => detached = true;
            }, "reward");
            await FlowTask.WaitForSeconds(0.05);
            Log.Add("screen closed");
            GC.KeepAlive(rewards);
        }

        World.Run(AdScreen());
        TickFor(0.1);
        AssertLog("screen closed");
        Assert.That(detached, Is.True);

        grant(100);
        var other = new Thread(() => grant(200));
        other.Start();
        TestThreads.JoinOrFail(other);
        Tick();
        Assert.That(Exceptions, Is.Empty);
    }
}
