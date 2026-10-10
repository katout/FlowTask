using System.Collections.Generic;
using R3;

namespace Katout.FlowTask.Bridges.Tests;

/// <summary>An observable that counts live subscriptions (to verify scope ownership of the R3 subscription).</summary>
sealed class CountingObservable<T>
{
    readonly Subject<T> _subject = new();

    public int Live { get; private set; }
    public int Total { get; private set; }

    public Observable<T> Observable => R3.Observable.Create<T>(observer =>
    {
        Live++;
        Total++;
        var inner = _subject.Subscribe(observer);
        return Disposable.Create(() =>
        {
            Live--;
            inner.Dispose();
        });
    });

    public void OnNext(T value) => _subject.OnNext(value);
    public void OnCompleted() => _subject.OnCompleted();
}

/// <summary>R3 Observable -> Signal.</summary>
public class ObservableToSignalTests : BridgeTestBase
{
    [Test]
    public void ValuesReachAWaitingScopeAndTheSubscriptionEndsWithTheScope()
    {
        var source = new CountingObservable<int>();

        async FlowTask Root()
        {
            var hits = source.Observable.ToSignal();
            Log.Add("hit " + await hits.Next());
        }

        World.Run(Root());
        Assert.That(source.Live, Is.EqualTo(1));
        source.OnNext(4);
        AssertLog(); // never resumed inside the emit
        Tick();
        AssertLog("hit 4");
        Assert.That(source.Live, Is.EqualTo(0), "R3 subscription disposed when the owning scope ended");
    }

    [Test]
    public void CancelingTheScopeDisposesTheSubscription()
    {
        var source = new CountingObservable<int>();

        async FlowTask Root()
        {
            var hits = source.Observable.ToSignal();
            while (true) Log.Add("hit " + await hits.Next());
        }

        var h = World.Run(Root());
        source.OnNext(1);
        Tick();
        source.OnNext(2);
        Tick();
        h.Cancel();
        Tick();
        AssertLog("hit 1", "hit 2");
        Assert.That(source.Live, Is.EqualTo(0));
        Assert.That(source.Total, Is.EqualTo(1));
    }

    [Test]
    public void UsingDisposesEarly()
    {
        var source = new CountingObservable<int>();

        async FlowTask Root()
        {
            using (var hits = source.Observable.ToSignal())
            {
                Log.Add("hit " + await hits.Next());
            }

            Log.Add("live " + source.Live);
            await FlowTask.NextFrame();
        }

        World.Run(Root());
        source.OnNext(8);
        Tick();
        AssertLog("hit 8", "live 0");
    }

    [Test]
    public void SubscribeBuffersBurstsFromR3()
    {
        var subject = new Subject<int>();

        async FlowTask Root()
        {
            var signal = subject.ToSignal();
            using var queue = signal.Subscribe(BufferPolicy.Queue(8, BufferOverflow.Fail));
            for (var i = 0; i < 3; i++) Log.Add("v" + await queue.Next());
        }

        var h = World.Run(Root());
        subject.OnNext(1);
        subject.OnNext(2);
        subject.OnNext(3);
        Tick();
        AssertLog("v1", "v2", "v3");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void CompletionOnTheWorldThreadClosesTheSignal()
    {
        var subject = new Subject<int>();

        async FlowTask Root()
        {
            var signal = subject.ToSignal();
            while (true)
            {
                var (received, v) = await signal.NextOrClosed();
                if (!received) break;
                Log.Add("v" + v);
            }

            Log.Add("closed");
        }

        var h = World.Run(Root());
        subject.OnNext(1);
        Tick();
        subject.OnCompleted();
        Tick();
        AssertLog("v1", "closed");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void CompletionDuringSubscribeClosesTheSignal()
    {
        async FlowTask<bool> Root()
        {
            var signal = R3.Observable.Return(1).ToSignal(); // OnNext + OnCompleted inside Subscribe
            var (received, _) = await signal.NextOrClosed();
            return received;
        }

        Assert.That(World.RunUntilComplete(Root()), Is.False);
    }

    [Test]
    public void NextOnACompletedSourceFailsLikeAnyClosedSignal()
    {
        var subject = new Subject<int>();

        async FlowTask Root()
        {
            var signal = subject.ToSignal();
            await signal.Next();
        }

        World.Run(Root());
        subject.OnCompleted();
        Tick();
        Assert.That(Test.Exceptions, Has.Count.EqualTo(1));
        Assert.That(Test.Exceptions[0].Exception, Is.InstanceOf<SignalClosedException>());
        Test.AcceptExceptions();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourceFailureIsTheInnerExceptionOfTheWaitersException(bool fromAnotherThread)
    {
        // The waiter's exception carries the upstream failure, not a bare SignalClosedException; R3's global handler still
        // gets it (where it shows when no flow waits).
        var previous = ObservableSystem.GetUnhandledExceptionHandler();
        var seenByR3 = new List<Exception>();
        ObservableSystem.RegisterUnhandledExceptionHandler(ex => seenByR3.Add(ex));
        try
        {
            var subject = new Subject<int>();
            var failure = new NetError("feed failed");

            async FlowTask Root()
            {
                var signal = subject.ToSignal("feed");
                await signal.Next();
            }

            World.Run(Root());
            Tick();
            if (fromAnotherThread)
            {
                var other = new Thread(() => subject.OnCompleted(R3.Result.Failure(failure)));
                other.Start();
                other.Join();
            }
            else
            {
                subject.OnCompleted(R3.Result.Failure(failure));
            }
            Tick();

            Assert.That(Test.Exceptions, Has.Count.EqualTo(1));
            Assert.That(Test.Exceptions[0].Exception, Is.InstanceOf<SignalClosedException>());
            Assert.That(Test.Exceptions[0].Exception.InnerException, Is.SameAs(failure));
            Assert.That(seenByR3, Is.EqualTo(new Exception[] { failure }));
            Test.AcceptExceptions();
        }
        finally
        {
            ObservableSystem.RegisterUnhandledExceptionHandler(previous);
        }
    }

    [Test]
    public void OnNextFromAnotherThreadGoesThroughTheWorldInbox()
    {
        var subject = new Subject<int>();
        var worldThread = Environment.CurrentManagedThreadId;
        var resumedOn = -1;

        async FlowTask Root()
        {
            var signal = subject.ToSignal();
            var v = await signal.Next();
            resumedOn = Environment.CurrentManagedThreadId;
            Log.Add("v" + v);
        }

        World.Run(Root());
        Task.Run(() => subject.OnNext(9)).Wait();
        AssertLog();
        Tick();
        AssertLog("v9");
        Assert.That(resumedOn, Is.EqualTo(worldThread));
    }

    [Test]
    public void CompletionFromAnotherThreadClosesTheSignalThroughTheInbox()
    {
        var subject = new Subject<int>();

        async FlowTask Root()
        {
            var signal = subject.ToSignal();
            using var sub = signal.Subscribe(BufferPolicy.Queue(8, BufferOverflow.Fail));
            while (true)
            {
                var (received, v) = await sub.NextOrClosed();
                if (!received) break;
                Log.Add("v" + v);
            }

            Log.Add("closed");
        }

        World.Run(Root());
        Task.Run(() =>
        {
            subject.OnNext(1);
            subject.OnCompleted();
        }).Wait();
        Tick();
        AssertLog("v1", "closed");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CompletionOnAnotherThreadDuringSubscribeClosesTheSignal(bool fails)
    {
        // The source completes on another thread inside Subscribe (the subscribing thread waits for it), before
        // ToSignal has bound the signal: a stand-in for a thread-pool source that completes at once. The completion
        // used to be lost there, and a flow waiting on NextOrClosed never ended.
        var previous = ObservableSystem.GetUnhandledExceptionHandler();
        var seenByR3 = new List<Exception>();
        ObservableSystem.RegisterUnhandledExceptionHandler(ex => seenByR3.Add(ex));
        try
        {
            var failure = new NetError("feed failed");
            var source = R3.Observable.Create<int>(observer =>
            {
                var other = new Thread(() =>
                {
                    observer.OnNext(1);
                    if (fails) observer.OnCompleted(R3.Result.Failure(failure));
                    else observer.OnCompleted();
                });
                other.Start();
                other.Join();
                return Disposable.Empty;
            });

            async FlowTask Root()
            {
                var signal = source.ToSignal("feed");
                using var sub = signal.Subscribe(BufferPolicy.Queue(8, BufferOverflow.Fail));
                try
                {
                    while (true) Log.Add("v" + await sub.Next());
                }
                catch (SignalClosedException e)
                {
                    Log.Add(e.InnerException == null ? "closed" : ReferenceEquals(e.InnerException, failure) ? "failed" : "other");
                }
            }

            World.Run(Root());
            Tick(); // step 1 applies the emit and the close, queued in that order by the other thread
            AssertLog("v1", fails ? "failed" : "closed");
            Assert.That(seenByR3, fails ? Is.EqualTo(new Exception[] { failure }) : Is.Empty);
        }
        finally
        {
            ObservableSystem.RegisterUnhandledExceptionHandler(previous);
        }
    }

    [Test]
    public void RaceAgainstAnR3Stream()
    {
        var clicks = new Subject<FlowUnit>();

        async FlowTask Root()
        {
            var click = clicks.ToSignal();
            var r = await FlowTask.Race(click.Next(), FlowTask.WaitForSeconds(1.0));
            Log.Add(r.Index == 0 ? "clicked" : "timeout");
        }

        World.Run(Root());
        Tick();
        clicks.OnNext(FlowUnit.Default);
        Tick();
        AssertLog("clicked");
        FlowAssert.NoLiveScopes(World);
    }
}

/// <summary>Signal -> R3 Observable.</summary>
public class SignalToObservableTests : BridgeTestBase
{
    [Test]
    public void EmitsAreForwardedInTheFlushInOrder()
    {
        var signal = new Signal<int>(World);
        var seen = new List<int>();
        using var subscription = signal.ToObservable(World).Subscribe(seen.Add);

        signal.Emit(1);
        signal.Emit(2);
        signal.Emit(3);
        Assert.That(seen, Is.Empty, "receivers resume in the flush, not inside Emit");
        Tick();
        Assert.That(seen, Is.EqualTo(new[] { 1, 2, 3 }));
        signal.Emit(4);
        Tick();
        Assert.That(seen, Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void ClosingTheSignalCompletesTheObserverAfterTheBufferDrains()
    {
        var signal = new Signal<string>(World);
        var seen = new List<string>();
        var completed = false;
        signal.ToObservable(World).Subscribe(seen.Add, r =>
        {
            Assert.That(r.IsSuccess, Is.True);
            completed = true;
        });

        signal.Emit("a");
        signal.Emit("b");
        signal.Close();
        Assert.That(completed, Is.False);
        Tick();
        Assert.That(seen, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(completed, Is.True);
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void ClosingTheSignalWithAnErrorFailsTheObserverAfterTheBufferDrains()
    {
        var signal = new Signal<int>(World);
        var failure = new InvalidOperationException("source failed");
        var seen = new List<int>();
        R3.Result? result = null;
        signal.ToObservable(World).Subscribe(seen.Add, r => result = r);

        signal.Emit(1);
        signal.Close(failure);
        Tick();
        Assert.That(seen, Is.EqualTo(new[] { 1 }));
        Assert.That(result.HasValue && result.Value.IsFailure, Is.True);
        Assert.That(result.Value.Exception, Is.SameAs(failure));
        Assert.That(Test.Exceptions, Is.Empty);
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void DisposingTheR3SubscriptionCancelsTheForwardingFlow()
    {
        var signal = new Signal<int>(World);
        var seen = new List<int>();
        var completed = false;
        var subscription = signal.ToObservable(World).Subscribe(seen.Add, _ => completed = true);
        FlowAssert.ScopeExists(World, "R3.ToObservable(Signal<Int32>)");

        signal.Emit(1);
        Tick();
        subscription.Dispose();
        signal.Emit(2);
        Tick();
        Assert.That(seen, Is.EqualTo(new[] { 1 }));
        Assert.That(completed, Is.False, "disposal by the subscriber does not complete it");
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void DisposingFromAnotherThreadIsPickedUpByTheNextTick()
    {
        var signal = new Signal<int>(World);
        var seen = new List<int>();
        var subscription = signal.ToObservable(World).Subscribe(seen.Add);

        Task.Run(() => subscription.Dispose()).Wait();
        signal.Emit(1); // already disposed: not delivered even though the flow is still alive
        Tick();
        Assert.That(seen, Is.Empty);
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void R3OperatorsThatDisposeUpstreamFromInsideOnNext()
    {
        var signal = new Signal<int>(World);
        var seen = new List<int>();
        var completed = false;
        signal.ToObservable(World).Where(x => x % 2 == 0).Take(2).Subscribe(seen.Add, _ => completed = true);

        for (var i = 1; i <= 6; i++) signal.Emit(i);
        Tick();
        Assert.That(seen, Is.EqualTo(new[] { 2, 4 }));
        Assert.That(completed, Is.True);
        Tick();
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void EachSubscriptionHasItsOwnForwardingFlow()
    {
        var signal = new Signal<int>(World);
        var a = new List<int>();
        var b = new List<int>();
        var observable = signal.ToObservable(World);
        using var sa = observable.Subscribe(a.Add);
        signal.Emit(1);
        using var sb = observable.Subscribe(b.Add);
        signal.Emit(2);
        Tick();
        Assert.That(a, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(b, Is.EqualTo(new[] { 2 }), "a subscription buffers from the moment it subscribes");
    }

    [Test]
    public void DisposingTheWorldCompletesTheObserverWithAFailure()
    {
        var signal = new Signal<int>(World);
        R3.Result? result = null;
        signal.ToObservable(World).Subscribe(_ => { }, r => result = r);
        World.Dispose();
        Assert.That(result.HasValue && result.Value.IsFailure, Is.True);
        Assert.That(result.Value.Exception, Is.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void RoundTripSignalToObservableToSignal()
    {
        var source = new Signal<int>(World);
        var observable = source.ToObservable(World).Select(x => x * 10);

        async FlowTask Consumer()
        {
            var mapped = observable.ToSignal();
            using var queue = mapped.Subscribe(BufferPolicy.Queue(4, BufferOverflow.Fail));
            Log.Add("got " + await queue.Next());
            Log.Add("got " + await queue.Next());
        }

        var h = World.Run(Consumer());
        source.Emit(1);
        source.Emit(2);
        Tick(3);
        AssertLog("got 10", "got 20");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Tick();
        FlowAssert.NoLiveScopes(World); // the consumer's scope ended, so the R3 chain and the forwarding flow ended
        FlowAssert.NoLiveScopes(World);
    }
}
