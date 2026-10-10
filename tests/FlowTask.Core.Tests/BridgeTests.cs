using System.Threading;

namespace Katout.FlowTask.Tests;

/// <summary>Bridges.</summary>
public class BridgeTests : FlowTestBase
{
    [Test]
    public void ScopeCancellationReachesTheExternalToken()
    {
        CancellationToken seen = default;

        async FlowTask Root()
        {
            await FlowBridge.FromTask(async ct =>
            {
                seen = ct;
                await Task.Delay(Timeout.Infinite, ct);
                return 1;
            });
        }

        var h = World.Run(Root());
        Assert.That(seen.CanBeCanceled && !seen.IsCancellationRequested, Is.True);
        h.Cancel();
        Tick();
        Assert.That(seen.IsCancellationRequested, Is.True);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void ABridgeAwaitedInTheCleanupOfACanceledScopeIsNotCanceledWithIt([Values] bool dispose)
    {
        // Once FlowCanceledException has reached the scope's finally, a bridge it awaits runs as in a live scope: its
        // token stays live and the await takes the result, while the handle stays Running. World.Dispose cancels it.
        var save = new TaskCompletionSource<int>();
        CancellationToken token = default;

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("saved " + await FlowBridge.FromTask(ct =>
                {
                    token = ct;
                    return save.Task;
                }));
            }
        }

        var h = World.Run(Screen());
        h.Cancel();
        Tick();
        Assert.That(token.CanBeCanceled && !token.IsCancellationRequested, Is.True, "the scope's cancellation does not reach it");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit), "closing");
        if (dispose)
        {
            World.Dispose();
            Assert.That(token.IsCancellationRequested, Is.True);
            AssertLog();
        }
        else
        {
            save.SetResult(1);
            Tick();
            AssertLog("saved 1");
        }

        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    sealed class NetError : Exception
    {
        public NetError(string m) : base(m) { }
    }

    [Test]
    public void AFaultedTasksExceptionIsThrownAtTheAwaitAndIsUnhandledWhenNotCaught()
    {
        CaptureExceptions();

        async FlowTask Root()
        {
            try
            {
                await FlowBridge.FromTask<int>(_ => Task.FromException<int>(new NetError("offline")));
                Log.Add("unreachable");
            }
            catch (NetError e)
            {
                Log.Add("err " + e.Message);
            }

            Log.Add("ok " + await FlowBridge.FromTask(_ => Task.FromResult(5)));
            await FlowBridge.FromTask<int>(_ => Task.FromException<int>(new InvalidOperationException("bug")));
            Log.Add("unreachable");
        }

        World.Run(Root());
        Tick();
        AssertLog("err offline", "ok 5");
        Assert.That(Exceptions.Single().Exception, Is.InstanceOf<InvalidOperationException>());
        Assert.That(Exceptions.Single().ScopePath, Does.StartWith("Root > Task"));
    }

    [Test]
    public void CompletionOnAnotherThreadResumesOnTheWorldThread()
    {
        var worldThread = Environment.CurrentManagedThreadId;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedOn = -1;

        async FlowTask Root()
        {
            var v = await tcs.Task.AsFlow();
            resumedOn = Environment.CurrentManagedThreadId;
            Log.Add("v" + v);
        }

        World.Run(Root());
        TestThreads.WaitOrFail(Task.Run(() => tcs.SetResult(9)));
        SpinWait.SpinUntil(() => false, 50);
        AssertLog();
        Tick();
        AssertLog("v9");
        Assert.That(resumedOn, Is.EqualTo(worldThread));
    }

    [Test]
    public async Task HandleAsTask()
    {
        async FlowTask<int> Work()
        {
            await FlowTask.NextFrame();
            return 3;
        }

        var h = World.Run(Work());
        var task = h.AsTask();
        Assert.That(task.IsCompleted, Is.False);
        Tick();
        Assert.That(await task, Is.EqualTo(3));
    }

    sealed class Button
    {
        public event Action<int> Clicked;
        public int HandlerCount => Clicked?.GetInvocationList().Length ?? 0;
        public void Click(int n) => Clicked?.Invoke(n);
    }

    EventSignal<int> BridgeOf(Button button) => FlowBridge.FromCallback<int>(emit =>
    {
        button.Clicked += emit;
        return () => button.Clicked -= emit;
    }, "SdkEvent");

    [Test]
    public void EventBridgeIsDetachedWhenTheScopeEnds()
    {
        var button = new Button();

        async FlowTask Root()
        {
            var clicks = BridgeOf(button);
            Log.Add("clicked " + await clicks.Next());
        }

        World.Run(Root());
        Assert.That(button.HandlerCount, Is.EqualTo(1));
        button.Click(4);
        Tick();
        AssertLog("clicked 4");
        Assert.That(button.HandlerCount, Is.EqualTo(0));
    }

    [Test]
    public void EventSignalIsClosedWhenItsOwningScopeEnds()
    {
        // Flows waiting on an event bridge whose owning scope ended used to hang silently (nothing emits any
        // more). Ending the owner closes the signal: Next throws SignalClosedException at the await (the flow that
        // does not catch it ends Faulted), NextOrClosed returns (false, default), and a subscription hands out what it
        // buffered first.
        CaptureExceptions();
        var button = new Button();
        var ready = new Once<EventSignal<int>>();

        async FlowTask Owner()
        {
            ready.Set(BridgeOf(button));
            await FlowTask.DelayFrames(2);
        }

        async FlowTask Edge()
        {
            var ev = await ready.Wait();
            try
            {
                while (true) Log.Add("edge " + await ev.Next());
            }
            finally
            {
                Log.Add("edge finally");
            }
        }

        async FlowTask Graceful()
        {
            var ev = await ready.Wait();
            while (true)
            {
                var (received, v) = await ev.NextOrClosed();
                if (!received) break;
                Log.Add("graceful " + v);
            }

            Log.Add("graceful closed");
        }

        async FlowTask Buffered()
        {
            var ev = await ready.Wait();
            using var sub = ev.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            while (true)
            {
                var (received, v) = await sub.NextOrClosed();
                if (!received) break;
                Log.Add("buffered " + v);
            }

            Log.Add("buffered closed");
        }

        World.Run(Owner());
        var edge = World.Run(Edge());
        var graceful = World.Run(Graceful());
        var buffered = World.Run(Buffered());
        button.Click(1);
        button.Click(2); // the edge waits get only the first: they resume before waiting again
        Tick();
        Assert.That(Log.Entries, Is.EquivalentTo(new[] { "edge 1", "graceful 1", "buffered 1", "buffered 2" }));
        Log.Clear();
        var ev0 = ready.Value;
        Assert.That(ev0.Signal.IsClosed, Is.False);

        Tick(); // the owner ends
        Assert.That(button.HandlerCount, Is.EqualTo(0), "detached");
        Assert.That(ev0.IsDisposed && ev0.Signal.IsClosed, Is.True);
        Assert.That(Log.Entries, Is.EquivalentTo(new[] { "edge finally", "graceful closed", "buffered closed" }));
        Assert.That(edge.Status, Is.EqualTo(FlowStatus.Faulted), "its Next threw, and it did not catch the exception");
        Assert.That(edge.CancelCause, Is.EqualTo(CancelCause.None));
        Assert.That(graceful.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(buffered.Status, Is.EqualTo(FlowStatus.Succeeded));
        var e = Exceptions.Single().Exception;
        Assert.That(e, Is.TypeOf<SignalClosedException>());
        Assert.That(e.Message, Does.Contain("SdkEvent").And.Contain("disposed"));

        Assert.Throws<FlowMisuseException>(() => ev0.Signal.Emit(3), "closed: a direct emit after the owner ended is a misuse");
    }

    [Test]
    public void EventSignalDisposedOnAnotherThreadIsClosedInTheNextIntake()
    {
        var button = new Button();
        EventSignal<int> ev = null;

        async FlowTask Owner()
        {
            ev = BridgeOf(button);
            Log.Add("closed " + !(await ev.NextOrClosed()).Received);
        }

        World.Run(Owner());
        Exception thrown = null;
        var other = new Thread(() =>
        {
            try
            {
                ev.Dispose();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        other.Start();
        TestThreads.JoinOrFail(other);
        Assert.That(thrown, Is.Null, "Dispose may be called from any thread");
        Assert.That(button.HandlerCount, Is.EqualTo(0), "detached on the disposing thread");
        Assert.That(ev.Signal.IsClosed, Is.False, "the close is applied in the World's next intake");
        Tick();
        Assert.That(ev.Signal.IsClosed, Is.True);
        AssertLog("closed True");
    }

    [Test]
    public void AnEventSignalNoWorldUsesIsMarkedClosedByADisposeOnAnotherThread()
    {
        // Created outside a flow and used by no World yet, the bridge's signal is only marked closed by a Dispose on
        // another thread, which also detaches it; its subscriptions are left alone. A flow that later waits on it sees
        // the close: on the signal at once, and on a subscription once it has taken what was buffered, instead of
        // waiting forever.
        var button = new Button();
        var ev = BridgeOf(button);
        var sub = ev.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
        button.Click(1);
        Exception thrown = null;
        var other = new Thread(() =>
        {
            try
            {
                ev.Dispose();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        other.Start();
        TestThreads.JoinOrFail(other);
        Assert.That(thrown, Is.Null, "Dispose may be called from any thread");
        Assert.That(button.HandlerCount, Is.EqualTo(0), "detached on the disposing thread");
        Assert.That(ev.IsDisposed && ev.Signal.IsClosed, Is.True, "marked closed");

        async FlowTask Reader()
        {
            Log.Add("signal " + await ev.NextOrClosed());
            Log.Add("first " + await sub.NextOrClosed());
            Log.Add("then " + await sub.NextOrClosed());
        }

        var h = World.Run(Reader());
        Tick();
        AssertLog("signal (False, 0)", "first (True, 1)", "then (False, 0)");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        sub.Dispose();
    }

    [Test]
    public void AnEventSignalDisposedFromTwoThreadsAtOnceDetachesOnce()
    {
        const int rounds = 2000;
        var detaches = 0;
        TestThreads.RaceRounds(rounds,
            setUp: () => FlowBridge.FromCallback<int>(emit => () => Interlocked.Increment(ref detaches), "SdkEvent"),
            otherThread: ev => ev.Dispose(),
            thisThread: ev => ev.Dispose(),
            afterBoth: ev => Assert.That(ev.IsDisposed && ev.Signal.IsClosed, Is.True));
        Assert.That(detaches, Is.EqualTo(rounds));
    }

    [Test]
    public void EventSignalIsClosedEvenWhenItsDetachThrows()
    {
        // The owner's cleanup runs the detach, which throws: a cleanup exception. The signal is closed all the same,
        // so a flow waiting on it does not hang.
        CaptureExceptions();
        EventSignal<int> ev = null;
        var ready = new Once<FlowUnit>();

        async FlowTask Owner()
        {
            ev = FlowBridge.FromCallback<int>(emit => () => throw new InvalidOperationException("detach bug"), "SdkEvent");
            ready.Set(FlowUnit.Default);
            await FlowTask.NextFrame();
        }

        async FlowTask Waiter()
        {
            await ready.Wait();
            Log.Add("closed " + !(await ev.NextOrClosed()).Received);
        }

        World.Run(Owner());
        var waiter = World.Run(Waiter());
        Tick(3);
        Assert.That(ev.IsDisposed && ev.Signal.IsClosed, Is.True);
        AssertLog("closed True");
        Assert.That(waiter.Status, Is.EqualTo(FlowStatus.Succeeded));
        var p = Exceptions.Single();
        Assert.That(p.Exception.Message, Is.EqualTo("detach bug"));
        Assert.That(p.Kind, Is.EqualTo(FlowExceptionKind.Cleanup), "a failing cleanup");
    }
}
