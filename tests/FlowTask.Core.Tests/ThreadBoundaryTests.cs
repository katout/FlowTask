using System.Diagnostics;
using System.Threading;

namespace Katout.FlowTask.Tests;

/// <summary>
/// The thread boundary: the World's single inbox for cross-thread sends, the binding of signals,
/// properties and Once values to the World's thread, and cross-thread cancellation.
/// </summary>
public class ThreadBoundaryTests : FlowTestBase
{
    static readonly TimeSpan StressTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs <paramref name="action"/> on a new thread and returns what it threw (null when nothing).</summary>
    static Exception OnOtherThread(Action action)
    {
        Exception caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                caught = e;
            }
        });
        thread.Start();
        TestThreads.JoinOrFail(thread);
        return caught;
    }

    // ------------------------------------------------------------------ one inbox

    [Test]
    public void EmitsAndPostsFromOneThreadArriveInTheOrderSent()
    {
        // Several threads mix EmitFromAnyThread and FlowWorld.Post on one signal. A post first drains the
        // subscription, then records itself: when the inbox keeps the order of sending, every emit a thread sent
        // before a post is recorded before that post, so each thread's sequence numbers arrive strictly increasing.
        const int producers = 4;
        const int perThread = 5000;
        var world = World;
        var sig = new Signal<long>(world, "mixed");
        var sub = default(Subscription<long>);

        async FlowTask Receiver()
        {
            sub = sig.Subscribe(BufferPolicy.Queue(1 << 16, BufferOverflow.Fail));
            await FlowTask.Never();
        }

        world.Run(Receiver());
        var last = new long[producers];
        for (var i = 0; i < producers; i++) last[i] = -1;
        long received = 0;
        long outOfOrder = 0;
        string firstProblem = null;

        void Record(long value)
        {
            var t = (int)(value >> 32);
            var seq = value & 0xFFFFFFFFL;
            received++;
            if (seq > last[t])
            {
                last[t] = seq;
                return;
            }

            outOfOrder++;
            firstProblem ??= $"thread {t}: #{seq} arrived after #{last[t]}";
        }

        void DrainSubscription()
        {
            while (sub.TryTake(out var v)) Record(v);
        }

        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[producers];
        for (var i = 0; i < producers; i++)
        {
            var t = i;
            threads[t] = new Thread(() =>
            {
                var rng = (uint)(t * 7919 + 17); // xorshift32: a fixed mix per thread
                start.Wait();
                for (long seq = 0; seq < perThread; seq++)
                {
                    var value = ((long)t << 32) | seq;
                    rng ^= rng << 13;
                    rng ^= rng >> 17;
                    rng ^= rng << 5;
                    if ((rng & 1) == 0) sig.EmitFromAnyThread(value);
                    else
                        world.Post(() =>
                        {
                            DrainSubscription();
                            Record(value);
                        });
                }
            });
            threads[t].Start();
        }

        start.Set();
        const long total = (long)producers * perThread;
        var sw = Stopwatch.StartNew();
        while (received < total && sw.Elapsed < StressTimeout)
        {
            world.Tick(Dt);
            DrainSubscription();
        }

        foreach (var th in threads) TestThreads.JoinOrFail(th);
        Assert.That(outOfOrder, Is.EqualTo(0), firstProblem);
        Assert.That(received, Is.EqualTo(total), "every emit and post arrives exactly once");
    }

    [Test]
    public void CloseFromAnyThreadKeepsEmitsSentBeforeIt()
    {
        // One thread emits and then closes while other threads keep emitting. Everything the closing thread sent
        // before CloseFromAnyThread must arrive before the close.
        const int trials = 20;
        const int closerEmits = 200;
        const int noiseThreads = 3;
        const int noiseLimit = 800;
        var failures = new List<string>();
        for (var trial = 0; trial < trials; trial++)
        {
            var sig = new Signal<long>(World, "closing");
            var got = new HashSet<long>();
            var closed = false;

            async FlowTask Receiver()
            {
                using var sub = sig.Subscribe(BufferPolicy.Queue(4096, BufferOverflow.Fail));
                while (true)
                {
                    var (received, v) = await sub.NextOrClosed();
                    if (!received) break;
                    got.Add(v);
                    while (sub.TryTake(out v)) got.Add(v);
                }

                closed = true;
            }

            World.Run(Receiver());
            var stop = 0;
            using var go = new ManualResetEventSlim(false);
            var threads = new List<Thread>
            {
                new(() =>
                {
                    go.Wait();
                    for (long s = 0; s < closerEmits; s++) sig.EmitFromAnyThread(s);
                    sig.CloseFromAnyThread();
                }),
            };
            for (var o = 1; o <= noiseThreads; o++)
            {
                var id = (long)o;
                threads.Add(new Thread(() =>
                {
                    go.Wait();
                    for (long s = 0; s < noiseLimit && Volatile.Read(ref stop) == 0; s++) sig.EmitFromAnyThread((id << 32) | s);
                }));
            }

            foreach (var th in threads) th.Start();
            go.Set();
            var sw = Stopwatch.StartNew();
            while (!closed && sw.Elapsed < StressTimeout) World.Tick(Dt);
            Volatile.Write(ref stop, 1);
            foreach (var th in threads) TestThreads.JoinOrFail(th);
            var lost = 0;
            for (long s = 0; s < closerEmits; s++)
                if (!got.Contains(s))
                    lost++;
            if (!closed) failures.Add($"trial {trial}: the close never arrived");
            else if (lost > 0) failures.Add($"trial {trial}: {lost} of the {closerEmits} emits sent before CloseFromAnyThread were lost");
        }

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void TickFlushAndDisposeFromAPostedActionAreRejected()
    {
        // Posted actions run in step 1 (intake), which counts as executing like the rest of a Tick or Flush. A nested
        // Flush there used to drain the inbox again under the running drain: a post queued by the action ran before
        // the posts queued ahead of it. A Dispose would end the World under the running intake.
        var order = new List<string>();
        var executing = false;
        var rejected = new List<string>();
        World.Post(() =>
        {
            order.Add("A");
            World.Post(() => order.Add("C"));
            executing = World.IsExecuting;
            try
            {
                World.Flush();
            }
            catch (FlowMisuseException)
            {
                rejected.Add("Flush");
            }

            try
            {
                World.Tick(Dt);
            }
            catch (FlowMisuseException)
            {
                rejected.Add("Tick");
            }

            try
            {
                World.Dispose();
            }
            catch (FlowMisuseException)
            {
                rejected.Add("Dispose");
            }
        });
        World.Post(() => order.Add("B"));

        World.Flush();
        Assert.That(order, Is.EqualTo(new[] { "A", "B" }), "C was posted after B");
        Assert.That(executing, Is.True);
        Assert.That(rejected, Is.EqualTo(new[] { "Flush", "Tick", "Dispose" }));
        Assert.That(World.IsExecuting, Is.False);
        Assert.That(World.IsDisposed, Is.False);

        World.Flush();
        Assert.That(order, Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [Test]
    public void APostedActionThatThrowsDoesNotStopTheIntake()
    {
        // A posted action runs outside every scope, so its exception is unhandled. The sends after it still run in the
        // same intake, in the order sent, and the Flush throws the exception once it has finished.
        var order = new List<string>();
        Assert.That(OnOtherThread(() =>
        {
            World.Post(() => throw new InvalidOperationException("posted"));
            World.Post(() => order.Add("P1"));
        }), Is.Null);
        var e = Assert.Throws<FlowUnhandledException>(() => World.Flush());
        Assert.That(e!.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(e.ExceptionInfos.Single().ScopePath, Is.EqualTo("<FlowWorld.Post>"));
        Assert.That(order, Is.EqualTo(new[] { "P1" }));
        Assert.That(World.IsExecuting, Is.False);

        World.Post(() => order.Add("P2"));
        World.Flush();
        Assert.That(order, Is.EqualTo(new[] { "P1", "P2" }));
    }

    // ------------------------------------------------------------------ Cancel from another thread

    async FlowTask LoggedUntilCanceled()
    {
        try
        {
            await FlowTask.Never();
        }
        finally
        {
            Log.Add("finally");
        }
    }

    [Test]
    public void CancelFromAnotherThreadIsAppliedAtTheNextFlush()
    {
        // FlowHandle.Cancel from another thread is queued, so ct.Register(handle.Cancel) works whatever thread cancels
        // the token.
        var h = World.Run(LoggedUntilCanceled());
        using var cts = new CancellationTokenSource();
        using var registration = cts.Token.Register(h.Cancel);

        Assert.That(OnOtherThread(() => cts.Cancel()), Is.Null);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running), "queued, not applied yet");
        AssertLog();

        World.Flush();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
        AssertLog("finally");

        Assert.That(OnOtherThread(() => h.Cancel()), Is.Null, "a Cancel after the end does nothing");
        World.Flush();
        AssertLog("finally");
    }

    // ------------------------------------------------------------------ binding to the World's thread

    static async FlowTask Await(FlowTask<int> task) => await task;

    [Test]
    public void SignalCreatedForAWorldOnAnotherThreadBelongsToTheWorldThread()
    {
        Signal<int> sig = null;
        Assert.That(OnOtherThread(() => sig = new Signal<int>(World, "made elsewhere")), Is.Null);

        async FlowTask Wait() => Log.Add("got " + await sig.Next());

        World.Run(Wait());
        sig.Emit(1); // the World's thread, not the thread that created the signal
        Tick();
        AssertLog("got 1");
        Assert.That(OnOtherThread(() => sig.Emit(2)), Is.InstanceOf<FlowThreadException>());
    }

    [Test]
    public void ObjectsOfADisposedWorldAreRejectedByTheNextWorld()
    {
        // Objects kept from an earlier session (in a static field with domain reload off, for example) stay bound to
        // that session's World. Local variables make the same situation without static state.
        var signal = new Signal<int>("bus");
        var property = new FlowProperty<int>(0);
        var once = new Once<int>();

        World.Run(Await(signal.Next()));
        World.Run(Await(property.WaitUntil(v => v > 0)));
        World.Run(Await(once.Wait()));
        World.Dispose();

        World = new FlowWorld(); // the next session, on the same thread
        CaptureExceptions();
        var handles = new[]
        {
            World.Run(Await(signal.Next())),
            World.Run(Await(property.WaitUntil(v => v > 0))),
            World.Run(Await(once.Wait())),
        };

        Assert.That(handles.Select(h => h.Status), Has.All.EqualTo(FlowStatus.Faulted));
        Assert.That(Exceptions, Has.Count.EqualTo(3));
        foreach (var p in Exceptions)
        {
            Assert.That(p.Exception, Is.TypeOf<FlowMisuseException>());
            Assert.That(p.Exception.Message, Does.Contain("the first World that used it, which is disposed"));
        }

        // The message names the object by its type, never by the user's value.
        var named = Exceptions.Select(p => p.Exception.Message[..p.Exception.Message.IndexOf(" is bound to", StringComparison.Ordinal)]);
        Assert.That(named, Is.EqualTo(new[] { "This Signal", "This FlowProperty", "This Once" }));
    }

    public enum MisusedWait
    {
        /// <summary>The flow awaits the wait.</summary>
        Await,
        /// <summary>The wait is the second branch of a Race, whose first branch has started.</summary>
        RaceBranch,
        /// <summary>The flow spawns the wait.</summary>
        Spawn,
        /// <summary>
        /// An async method the flow awaits awaits the wait and does not catch the misuse. The method is a state machine,
        /// whose start is not guarded like the start of a wait: its own catch ends it with the misuse.
        /// </summary>
        InMethod,
    }

    [Test]
    public void AWaitWhoseStartIsRejectedLeavesNothingUnderTheFlowThatCaughtIt([Values] MisusedWait wait)
    {
        // A wait on a signal of an earlier session is rejected when it starts, and the flow catches the misuse and goes
        // on. The wait (and the Race it is a branch of, with the branch it had started, or the async method that
        // awaited it) does not stay under the flow as a wait that never ends until the flow does (World.Dump).
        var signal = new Signal<int>("bus");
        World.Run(Await(signal.Next()));
        World.Dispose();
        World = new FlowWorld(); // the next session, on the same thread
        CaptureExceptions();

        async FlowTask Rejected()
        {
            await signal.Next(); // not caught here: the method ends with the misuse
            Log.Add("not reached");
        }

        async FlowTask Screen()
        {
            try
            {
                switch (wait)
                {
                    case MisusedWait.Await:
                        await signal.Next();
                        break;
                    case MisusedWait.RaceBranch:
                        await FlowTask.Race(FlowTask.WaitForSeconds(10), signal.Next());
                        break;
                    case MisusedWait.InMethod:
                        await Rejected();
                        break;
                    default:
                        Flow.Spawn(signal.Next());
                        break;
                }
            }
            catch (FlowMisuseException)
            {
                Log.Add("rejected");
            }

            Log.Add("under the flow: " + string.Join(", ", World.Diagnostics.Walk().Where(s => s.Path.StartsWith("Screen >", StringComparison.Ordinal)).Select(s => s.Name)));
            await FlowTask.NextFrame();
            Log.Add("goes on");
        }

        var h = World.Run(Screen());
        Tick(2);
        AssertLog("rejected", "under the flow: ", "goes on");
        Assert.That(Exceptions, Is.Empty);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(World.Diagnostics.Root.Children, Is.Empty);
        Assert.That(World.Dump(), Does.Not.Contain("bus"));
    }

    [Test]
    public void ObjectSharedByTwoWorldsCannotBeUsedAfterTheFirstIsDisposed()
    {
        // Two live Worlds on one thread may share an object: it stays bound to the first World that used it. Once
        // that World is disposed, the other one can no longer start using it, although it is still alive.
        var first = World;
        var second = new FlowWorld();
        try
        {
            var shared = new Signal<int>("shared");
            first.Run(Await(shared.Next()));
            var whileBothLive = second.Run(Await(shared.Next()));
            Assert.That(whileBothLive.Status, Is.EqualTo(FlowStatus.Running), "sharing is allowed while the first World lives");

            first.Dispose();
            Exception caught = null;
            second.OnUnhandledException = p => caught = p.Exception;
            var afterwards = second.Run(Await(shared.Next()));
            Assert.That(afterwards.Status, Is.EqualTo(FlowStatus.Faulted));
            Assert.That(caught, Is.TypeOf<FlowMisuseException>());
            Assert.That(caught.Message, Does.Contain("the first World that used it, which is disposed: another World cannot use it"));
        }
        finally
        {
            second.Dispose();
        }
    }

    [Test]
    public void AnObjectIsCheckedOnlyOnceAWorldUsesIt()
    {
        // (a) Before a World uses an object, nothing is checked, as for any .NET object: another thread may write it.
        var property = new FlowProperty<int>(0);
        property.Set(1);
        Assert.That(OnOtherThread(() => property.Set(2)), Is.Null);
        Assert.That(property.Value, Is.EqualTo(2));

        // (b) Objects written on another thread first belong to the first World that uses them, on its thread.
        var signal = new Signal<int>("written elsewhere");
        Assert.That(OnOtherThread(() => signal.Emit(1)), Is.Null); // nobody receives it
        CaptureExceptions();
        var fromSignal = World.Run(Await(signal.Next()));
        Assert.That(fromSignal.Status, Is.EqualTo(FlowStatus.Running));

        // (c) From then on a write from another thread is rejected before it changes anything, also through a derived
        // signal: a flow that waits only on Changed binds the property too.
        Assert.That(OnOtherThread(() => signal.Emit(2)), Is.InstanceOf<FlowThreadException>());

        async FlowTask Watch() => Log.Add("changed " + await property.Changed.Next());

        World.Run(Watch());
        Assert.That(OnOtherThread(() => property.Set(5)), Is.InstanceOf<FlowThreadException>());
        Assert.That(property.Value, Is.EqualTo(2));
        property.Set(5);
        Tick();
        AssertLog("changed 5");

        // (d) A WaitUntil whose condition already holds completes at once, and binds the property all the same.
        var holds = new FlowProperty<int>(1);
        World.Run(Await(holds.WaitUntil(v => v == 1)));
        Assert.That(OnOtherThread(() => holds.Set(2)), Is.InstanceOf<FlowThreadException>());
        Assert.That(holds.Value, Is.EqualTo(1));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void SubscriptionTryTakeAndDisposeAreRejectedFromAnotherThread()
    {
        // The buffer and the signal's subscription list are not synchronized: a TryTake or Dispose from another
        // thread raced the World's Emit. They are rejected before they change anything.
        var sig = new Signal<int>("buffered");
        var sub = default(Subscription<int>);

        async FlowTask Hold()
        {
            sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.Fail));
            await FlowTask.Never();
        }

        World.Run(Hold());
        sig.Emit(1);
        Assert.That(OnOtherThread(() => sub.TryTake(out _)), Is.InstanceOf<FlowThreadException>());
        Assert.That(OnOtherThread(() => sub.Dispose()), Is.InstanceOf<FlowThreadException>());
        Assert.That(sub.IsActive, Is.True);
        Assert.That(sub.Count, Is.EqualTo(1), "nothing was taken");

        sig.Emit(2);
        var taken = new List<int>();
        while (sub.TryTake(out var v)) taken.Add(v);
        Assert.That(taken, Is.EqualTo(new[] { 1, 2 }));
        sub.Dispose(); // the World's thread
        Assert.That(sub.IsActive, Is.False);
    }

    static async FlowTask AwaitNextOrClosed(FlowTask<(bool Received, int Value)> task) => await task;

    [Test]
    public void SubscriptionNextIsRejectedInAWorldOnAnotherThread()
    {
        // Next and NextOrClosed take from the same buffer as TryTake, or register a waiter that the World's Emit
        // resumes. A World on another thread that awaits them is rejected before it takes a value or registers.
        var sig = new Signal<int>("buffered");
        var sub = default(Subscription<int>);

        async FlowTask Hold()
        {
            sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.Fail));
            await FlowTask.Never();
        }

        World.Run(Hold());
        sig.Emit(1);
        var statuses = new List<FlowStatus>();
        var reports = new List<Exception>();
        Assert.That(OnOtherThread(() =>
        {
            using var other = new FlowWorld();
            other.OnUnhandledException = p => reports.Add(p.Exception);
            statuses.Add(other.Run(Await(sub.Next())).Status);
            statuses.Add(other.Run(AwaitNextOrClosed(sub.NextOrClosed())).Status);
        }), Is.Null);
        Assert.That(statuses, Is.EqualTo(new[] { FlowStatus.Faulted, FlowStatus.Faulted }));
        Assert.That(reports, Has.Count.EqualTo(2).And.All.TypeOf<FlowThreadException>());
        Assert.That(sub.Count, Is.EqualTo(1), "nothing was taken");

        async FlowTask Take()
        {
            Log.Add("got " + await sub.Next());
            Log.Add("got " + await sub.Next());
        }

        World.Run(Take()); // the World's thread: takes the buffered value, then waits
        sig.Emit(2);
        Tick();
        AssertLog("got 1", "got 2");
    }

    [Test]
    public void AJoinFromAWorldOnAnotherThreadIsRejected()
    {
        // A join waits in the joined task's nodes, which belong to its World's thread. A World on another thread that
        // joins it fails at the join's await, whether the task has ended or not; a join made there does not mark the
        // handle joined either.
        var running = World.Run(FlowTask.NextFrame());
        var ended = World.Run(FlowTask.CompletedTask);
        var runningForTheOtherWorld = World.Run(FlowTask.NextFrame());
        var statuses = new List<FlowStatus>();
        var reports = new List<Exception>();
        Assert.That(OnOtherThread(() =>
        {
            using var other = new FlowWorld();
            other.OnUnhandledException = p => reports.Add(p.Exception);
            statuses.Add(other.Run(running.Join()).Status);
            statuses.Add(other.Run(ended.Join()).Status);
        }), Is.Null);
        Assert.That(statuses, Is.EqualTo(new[] { FlowStatus.Faulted, FlowStatus.Faulted }));
        Assert.That(reports, Has.Count.EqualTo(2).And.All.TypeOf<FlowThreadException>());
        Assert.That(running.Status, Is.EqualTo(FlowStatus.Running));

        async FlowTask Joiner(FlowHandle h, string name)
        {
            await h.Join();
            Log.Add(name + " joined");
        }

        // The World's own thread still joins both once, and another World on this thread can join too: at once for a
        // task that has ended; for a running one, its end queues the join's resume in the joining World, which
        // resumes it in its own next Tick or Flush.
        World.Run(Joiner(running, "running"));
        using var sameThread = new FlowWorld();
        sameThread.Run(Joiner(ended, "ended"));
        sameThread.Run(Joiner(runningForTheOtherWorld, "other World"));
        AssertLog("ended joined");
        Tick();
        AssertLog("ended joined", "running joined");
        sameThread.Flush();
        AssertLog("ended joined", "running joined", "other World joined");
    }

    // ------------------------------------------------------------------ a disposed World

    [Test]
    public void SendsToADisposedWorldAreDropped()
    {
        var sig = new Signal<int>(World, "late");
        var ran = new List<string>();

        async FlowTask Work()
        {
            await FlowTask.Never();
        }

        var h = World.Run(Work());
        World.Post(() => ran.Add("posted before Dispose"));
        Assert.That(OnOtherThread(() =>
        {
            sig.EmitFromAnyThread(1);
            h.Cancel(); // queued: applied at the next Tick or Flush, which never comes
        }), Is.Null);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        World.Dispose(); // discards the queued post, emit and cancel: the flow ends as disposed, not as canceled by h

        var thrown = OnOtherThread(() =>
        {
            sig.EmitFromAnyThread(2);
            sig.CloseFromAnyThread();
            World.Post(() => ran.Add("posted after Dispose"));
            h.Cancel();
        });
        sig.Emit(3); // on the World's thread, outside any flow: no receiver, as before

        Assert.That(thrown, Is.Null, "sends to a disposed World are dropped, not rejected");
        Assert.That(ran, Is.Empty);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.WorldDisposed));
    }

    public enum UnboundSignal
    {
        /// <summary>Created outside every flow and not used yet.</summary>
        Unused,
        /// <summary>Subscribed outside every flow: the subscription does not bind it to a World.</summary>
        SubscribedOutsideAFlow,
        /// <summary>Emitted on this thread outside every flow: that makes the thread its owner, not a World.</summary>
        EmittedOutsideAFlow,
    }

    [Test]
    public void EmitFromAnyThreadOnASignalThatNoWorldUsesYetThrows([Values] UnboundSignal shape)
    {
        // A signal that no World uses yet has no inbox to queue a cross-thread emit in. The emit used to be dropped
        // silently, also when a subscription made outside a flow was waiting for it; it is a misuse, whatever thread
        // sends it, and the message says how to bind the signal first. Once a flow of the World uses the signal, the
        // emits from other threads arrive; once that World is disposed, they are dropped without an exception again.
        var sig = new Signal<int>("sdk");
        var sub = default(Subscription<int>);
        if (shape == UnboundSignal.SubscribedOutsideAFlow) sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropNewest));
        if (shape == UnboundSignal.EmittedOutsideAFlow) sig.Emit(0);

        var thrown = OnOtherThread(() => sig.EmitFromAnyThread(1));
        Assert.That(thrown, Is.TypeOf<FlowMisuseException>());
        Assert.That(thrown.Message, Does.Contain("no World uses yet").And.Contain("new Signal<T>(world)"));
        // The thread that owns it gets the same answer: the rule is about the binding, not the thread.
        Assert.Throws<FlowMisuseException>(() => sig.EmitFromAnyThread(2));

        async FlowTask Consume()
        {
            while (true) Log.Add("got " + await (shape == UnboundSignal.SubscribedOutsideAFlow ? sub.Next() : sig.Next()));
        }

        World.Run(Consume()); // the first wait binds the signal to this World
        Assert.That(OnOtherThread(() => sig.EmitFromAnyThread(3)), Is.Null);
        Tick();
        AssertLog("got 3");
        Assert.That(sub.Count, Is.EqualTo(0), "nothing else was buffered: the refused emits never reached the subscription");

        World.Dispose();
        Assert.That(OnOtherThread(() => sig.EmitFromAnyThread(4)), Is.Null, "a send to a disposed World is dropped, not rejected");
    }

    [Test]
    public void AnEventSignalCreatedInAFlowTakesCallbacksFromOtherThreads()
    {
        // FlowBridge.FromCallback inside a flow makes a signal bound to that flow's World, so a callback that an SDK
        // raises on another thread goes through the inbox (EmitFromAnyThread) and reaches the flow in the next Tick.
        Action<int> raise = null;

        async FlowTask Listen()
        {
            var ev = FlowBridge.FromCallback<int>(emit =>
            {
                raise = emit;
                return () => raise = null;
            }, "sdk");
            Log.Add("got " + await ev.Next());
        }

        var h = World.Run(Listen());
        Assert.That(OnOtherThread(() => raise(7)), Is.Null);
        Tick();
        AssertLog("got 7");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(raise, Is.Null, "detached when the flow ended");
    }
}
