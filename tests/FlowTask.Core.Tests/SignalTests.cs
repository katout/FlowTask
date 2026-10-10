using System.Threading;

namespace Katout.FlowTask.Tests;

/// <summary>Signals, subscriptions, FlowProperty and Once.</summary>
public class SignalTests : FlowTestBase
{
    [Test]
    public void EmitReachesEveryCurrentWaiterAndIsLostOtherwise()
    {
        var sig = new Signal<int>();

        async FlowTask Waiter(string n)
        {
            var v = await sig.Next();
            Log.Add(n + v);
        }

        sig.Emit(0); // nobody waits: lost
        World.Run(Waiter("a"));
        World.Run(Waiter("b"));
        sig.Emit(1);
        World.Run(Waiter("late")); // registered after the emit: does not receive it
        Tick();
        AssertLog("a1", "b1");
        sig.Emit(2);
        Tick();
        AssertLog("a1", "b1", "late2");
    }

    [Test]
    public void EmitEndsTheWaitingNextAtOnceAndOnlyTheResumeWaitsForTheFlush()
    {
        // A dispatcher outside the flows hands a value to one waiter and reads Status to learn whether it was taken.
        var sig = new Signal<int>();
        var next = default(FlowTask<int>);

        async FlowTask Waiter()
        {
            next = sig.Next();
            Log.Add("got " + await next);
        }

        World.Run(Waiter());
        Assert.That(next.Status, Is.EqualTo(FlowStatus.Running));
        sig.Emit(5);
        Assert.That(next.Status, Is.EqualTo(FlowStatus.Succeeded));
        AssertLog();
        World.Flush();
        AssertLog("got 5");
    }

    [Test]
    public void EmitSkipsAWaiterCanceledFromOutsideBeforeItUnwinds()
    {
        var sig = new Signal<int>();
        var next = default(FlowTask<int>);

        async FlowTask Waiter()
        {
            try
            {
                next = sig.Next();
                Log.Add("got " + await next);
            }
            finally
            {
                Log.Add("finally");
            }
        }

        var h = World.Run(Waiter());
        h.Cancel(); // outside the flows: confirmed now, unwound at the head of the next flush
        sig.Emit(5);
        Assert.That(next.Status, Is.EqualTo(FlowStatus.Running)); // not handed the value; it ends with the unwinding
        World.Flush();
        AssertLog("finally");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void EmitStillReachesANonCancelableWaitAfterItsFlowIsCanceled()
    {
        var sig = new Signal<int>();
        var next = default(FlowTask<int>);

        async FlowTask Waiter()
        {
            next = sig.Next();
            Log.Add("got " + await Flow.NonCancelable(next));
        }

        var h = World.Run(Waiter());
        h.Cancel();
        sig.Emit(5);
        Assert.That(next.Status, Is.EqualTo(FlowStatus.Succeeded));
        World.Flush();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void LatestSubscriptionKeepsTheNewestValue()
    {
        var sig = new Signal<int>();

        async FlowTask Root()
        {
            using var sub = sig.Subscribe(BufferPolicy.Latest);
            await FlowTask.NextFrame();
            Log.Add("got " + await sub.Next());
            Log.Add("count " + sub.Count);
        }

        World.Run(Root());
        sig.Emit(1);
        sig.Emit(2);
        sig.Emit(3);
        Tick();
        AssertLog("got 3", "count 0");
    }

    [Test]
    public void QueueSubscriptionKeepsOrderAndIsOwnedByTheScope()
    {
        var sig = new Signal<int>();
        var sub = default(Subscription<int>);

        async FlowTask Root()
        {
            sub = sig.Subscribe(BufferPolicy.Queue(8, BufferOverflow.Fail));
            await FlowTask.NextFrame();
            for (var i = 0; i < 3; i++) Log.Add("q" + await sub.Next());
        }

        CaptureExceptions();
        var h = World.Run(Root());
        sig.Emit(1);
        sig.Emit(2);
        sig.Emit(3);
        Tick();
        AssertLog("q1", "q2", "q3");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        // The scope ended, so it released its subscription: inactive. An overflow cannot show this: once the owner has
        // ended there is nobody to fail, so emits after it are silent either way. (Count is 0 whenever IsActive is
        // false, so it adds nothing; that the signal stops writing into the released buffer is covered by
        // DisposingAStaleCopyDoesNotEndANewerSubscription.)
        Assert.That(sub.IsActive, Is.False, "the subscription outlived its scope");
        for (var i = 0; i < 20; i++) sig.Emit(i);
        Tick();
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void HitInTheSameFlushSurvivesALosingNext()
    {
        // The README pattern: a hit emitted by another flow in the flush where the windup ends reaches hits.Next, which
        // then loses the Race to the windup. The value goes back to the front of the subscription, and the next round
        // takes it.
        var damaged = new Signal<int>("Damaged");

        async FlowTask Player()
        {
            await FlowTask.WaitForSeconds(0.5);
            Log.Add("player hits");
            damaged.Emit(10);
        }

        async FlowTask<int> Windup()
        {
            await FlowTask.WaitForSeconds(0.5);
            return 1;
        }

        async FlowTask Enemy()
        {
            using var hits = damaged.Subscribe(BufferPolicy.Latest);
            for (var i = 0; i < 2; i++)
            {
                var r = await FlowTask.Race(Windup(), hits.Next());
                Log.Add(r.Index == 0 ? "windup finished -> attack" : "hit " + r.Value1 + " -> stun");
            }
        }

        World.Run(Player());
        World.Run(Enemy());
        TickFor(2);
        AssertLog("player hits", "windup finished -> attack", "hit 10 -> stun");
    }

    [Test]
    public void ValueDeliveredToACanceledWaiterGoesBack()
    {
        // The emit reserved the child's resume; the child was canceled before it resumed, so it never received the
        // value. The value goes back to the subscription, where its owner takes it.
        var sig = new Signal<int>("S");
        var gate = new Once<FlowUnit>();
        FlowHandle child = default;

        async FlowTask Child(Subscription<int> sub)
        {
            try
            {
                Log.Add("child got " + await sub.Next());
            }
            finally
            {
                Log.Add("child finally");
            }
        }

        async FlowTask Parent()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            child = Flow.Spawn(Child(sub));
            await gate.Wait();
            Log.Add("parent took " + (sub.TryTake(out var v) ? v.ToString() : "nothing"));
        }

        World.Run(Parent());
        sig.Emit(5); // the child's resume is reserved
        child.Cancel();
        Tick();
        AssertLog("child finally");
        gate.Set(FlowUnit.Default);
        Tick();
        AssertLog("child finally", "parent took 5");
    }

    [TestCase(false, "1,2")]
    [TestCase(true, "2")]
    public void ValuesTwoRaceLosersTookFromOneSubscriptionGoBackInTheirOrder(bool latest, string left)
    {
        // Both waits on the subscription took a value in the flush in which the other branch won. The losers are
        // released later-started first and each value goes back to the front, so the values end in the order they were
        // taken; Latest keeps only the newest.
        var win = new Signal<int>("Win");
        var s = new Signal<int>("S");
        var sub = default(Subscription<int>);

        async FlowTask Owner()
        {
            sub = s.Subscribe(latest ? BufferPolicy.Latest : BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            var r = await FlowTask.Race(win.Next(), sub.Next(), sub.Next());
            Log.Add("race " + r.Index);
            await FlowTask.Never();
        }

        World.Run(Owner());
        win.Emit(0);
        s.Emit(1);
        s.Emit(2);
        Tick();
        AssertLog("race 0");
        var taken = new List<int>();
        while (sub.TryTake(out var v)) taken.Add(v);
        Assert.That(string.Join(",", taken), Is.EqualTo(left));
    }

    [Test]
    public void ValueTakenFromTheBufferByAFailedWhenAllGoesBack()
    {
        // The wait took the buffered value as it started, then the other branch threw: the WhenAll failed before
        // anything received the value, which goes back to the subscription.
        var sig = new Signal<int>("S");

        async FlowTask Fails() => throw new InvalidOperationException("branch failed");

        async FlowTask Loop()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            await FlowTask.NextFrame(); // 7 is buffered now
            try
            {
                await FlowTask.WhenAll(sub.Next(), Fails());
            }
            catch (InvalidOperationException e)
            {
                Log.Add(e.Message + ", count " + sub.Count);
            }

            Log.Add("again " + await sub.Next());
        }

        World.Run(Loop());
        sig.Emit(7);
        Tick();
        AssertLog("branch failed, count 1", "again 7");
    }

    [Test]
    public void ValueThatARaceTookCountsAsReceivedWhenWithoutResultDropsTheRace()
    {
        // A value in a combinator's result counts as received when the combinator reads it: the Race received it, and
        // it does not go back to the subscription when WithoutResult drops the Race's result.
        var sig = new Signal<int>("S");

        async FlowTask Root()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            sig.Emit(1);
            await FlowTask.Race(sub.Next(), FlowTask.Never()).WithoutResult();
            Log.Add("left " + sub.Count);
        }

        World.Run(Root());
        Tick();
        AssertLog("left 0");
    }

    [Test]
    public void ValueReceivedThroughWithoutResultIsNotHandedBack()
    {
        // WithoutResult ignores the value, but the flow did receive the emit it waited for: the value is taken.
        var sig = new Signal<int>("S");

        async FlowTask Loop()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            await sub.Next().WithoutResult();
            Log.Add("count " + sub.Count);
            var r = await FlowTask.Race(sub.Next().WithoutResult(), FlowTask.WaitForSeconds(10));
            Log.Add("race " + r.Index + " count " + sub.Count);
        }

        World.Run(Loop());
        sig.Emit(1);
        Tick();
        sig.Emit(2);
        Tick();
        AssertLog("count 0", "race 0 count 0");
    }

    [Test]
    public void UsingEndsASubscriptionEarly()
    {
        CaptureExceptions();
        var sig = new Signal<int>();

        async FlowTask Root()
        {
            using (sig.Subscribe(BufferPolicy.Queue(1, BufferOverflow.Fail)))
            {
            }

            sig.Emit(1);
            sig.Emit(2); // would overflow a live Queue(1, Fail) subscription
            Log.Add("no overflow");
            await FlowTask.WaitForSeconds(1);
        }

        World.Run(Root());
        Tick();
        AssertLog("no overflow");
        Assert.That(Exceptions, Is.Empty);
    }

    // A subscription hands a value to the first receiver that is still waiting. A receiver whose
    // cancellation is confirmed is not (it only waits for its unwind), so the value stays in the buffer and the next
    // receiver takes it on the spot.
    [Test]
    public void EmitSkipsAWaiterWhoseCancellationIsConfirmed()
    {
        var sig = new Signal<int>();
        var sub = default(Subscription<int>);

        async FlowTask Owner()
        {
            sub = sig.Subscribe(BufferPolicy.Queue(8, BufferOverflow.DropNewest));
            await FlowTask.Never();
        }

        async FlowTask Receiver(string n)
        {
            await sub.Next();
            Log.Add(n + " got");
        }

        World.Run(Owner());
        var w1 = World.Run(Receiver("w1"));
        w1.Cancel(); // confirmed now, unwound at the head of the next flush
        sig.Emit(1);
        World.Run(Receiver("w2"));
        // Checked before the Tick: had w1 been handed the value, a subscription that takes back the value of a receiver
        // unwound in the Tick would pass it on to w2 there, and the log after the Tick alone could not tell.
        AssertLog("w2 got");
        Tick();
        AssertLog("w2 got");
        Assert.That(w1.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    // A value type used by this test only, so that its subscriptions have a pool of their own.
    struct StaleCopyValue
    {
    }

    // Disposing a copy of a subscription that was already disposed must not end a newer subscription that reuses its
    // pooled state (the pools are LIFO, so the newer subscription gets that state).
    [Test]
    public void DisposingAStaleCopyDoesNotEndANewerSubscription()
    {
        var sig = new Signal<StaleCopyValue>();
        var stale = default(Subscription<StaleCopyValue>);
        var live = default(Subscription<StaleCopyValue>);

        async FlowTask A()
        {
            var s = sig.Subscribe(BufferPolicy.Latest);
            stale = s;
            s.Dispose();
            await FlowTask.Never();
        }

        async FlowTask B()
        {
            live = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropNewest)); // rents the state A returned
            await FlowTask.Never();
        }

        World.Run(A());
        World.Run(B());
        stale.Dispose();
        sig.Emit(default);
        Assert.That(live.IsActive, Is.True);
        Assert.That(live.Count, Is.EqualTo(1));
    }

    [Test]
    public void QueueOverflowPolicies()
    {
        var sig = new Signal<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => BufferPolicy.Queue(0, BufferOverflow.DropOldest));
        Assert.Throws<ArgumentException>(() => sig.Subscribe(default));

        async FlowTask Drain(string name, BufferPolicy policy)
        {
            using var sub = sig.Subscribe(policy);
            await FlowTask.NextFrame();
            var values = "";
            while (sub.TryTake(out var v)) values += v;
            Log.Add(name + ":" + values);
        }

        World.Run(Drain("oldest", BufferPolicy.Queue(2, BufferOverflow.DropOldest)));
        World.Run(Drain("newest", BufferPolicy.Queue(2, BufferOverflow.DropNewest)));
        sig.Emit(1);
        sig.Emit(2);
        sig.Emit(3);
        Tick();
        AssertLog("oldest:23", "newest:12");
    }

    [Test]
    public void OverflowUnderFailEndsTheOwnerScopeWithTheExceptionForItsCaller()
    {
        // BufferOverflow.Fail cancels the scope that owns the subscription (CancelCause.Fault). It unwinds and ends with
        // SubscriptionOverflowException, which its caller receives at its await.
        var sig = new Signal<int>("hits");
        SubscriptionOverflowException caught = null;

        async FlowTask Subscriber()
        {
            try
            {
                using var sub = sig.Subscribe(BufferPolicy.Queue(1, BufferOverflow.Fail));
                await FlowTask.WaitForSeconds(10);
            }
            finally
            {
                Log.Add("subscriber unwound");
            }
        }

        async FlowTask Caller()
        {
            try
            {
                await Subscriber();
            }
            catch (SubscriptionOverflowException e)
            {
                caught = e;
                Log.Add("caller caught it");
            }
        }

        var h = World.Run(Caller());
        sig.Emit(1);
        sig.Emit(2);
        Tick();
        AssertLog("subscriber unwound", "caller caught it");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        // The message names the signal and says how to fix it.
        Assert.That(caught.Message, Does.Contain("'hits'").And.Contain("capacity 1").And.Contain("DropOldest"));
    }

    [Test]
    public void QueueCapacityIsNotAllocatedUpFront()
    {
        // A large capacity is an upper bound, not a buffer size. Queue(int.MaxValue, ...) used to throw
        // OutOfMemoryException when subscribing.
        var sig = new Signal<int>("feed");

        async FlowTask Reader()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(int.MaxValue, BufferOverflow.DropOldest));
            await FlowTask.NextFrame();
            while (sub.TryTake(out var v)) Log.Add("v" + v);
            Log.Add("next " + await sub.Next());
        }

        var before = Allocations.CurrentThreadBytes();
        World.Run(Reader());
        for (var i = 0; i < 3; i++) sig.Emit(i);
        Tick();
        sig.Emit(9);
        Tick();
        var allocated = Allocations.CurrentThreadBytes() - before;
        AssertLog("v0", "v1", "v2", "next 9");
        Assert.That(allocated, Is.LessThan(1 << 20), "the buffer grows with its content");
    }

    [TestCase(BufferOverflow.DropOldest, "3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22")]
    [TestCase(BufferOverflow.DropNewest, "3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22")]
    public void QueueKeepsOrderWhileItsBufferGrows(BufferOverflow overflow, string afterGrowth)
    {
        // The buffer grows on demand (from 4, doubling, up to the capacity) and must keep the oldest first, also when
        // the ring has wrapped around before it grows.
        var sig = new Signal<int>("feed");
        using var sub = sig.Subscribe(BufferPolicy.Queue(20, overflow));
        string TakeAll()
        {
            var taken = new List<int>();
            while (sub.TryTake(out var v)) taken.Add(v);
            return string.Join(",", taken);
        }

        sig.Emit(1);
        sig.Emit(2);
        sig.Emit(3);
        Assert.That(sub.TryTake(out var first) && first == 1, Is.True);
        Assert.That(sub.TryTake(out var second) && second == 2, Is.True);
        for (var i = 4; i <= 22; i++) sig.Emit(i); // wraps the first ring, then grows past it to the capacity (20)
        Assert.That(sub.Count, Is.EqualTo(20));
        Assert.That(TakeAll(), Is.EqualTo(afterGrowth));

        for (var i = 1; i <= 25; i++) sig.Emit(100 + i); // over the capacity: the policy decides
        Assert.That(sub.Count, Is.EqualTo(20));
        var expected = overflow == BufferOverflow.DropOldest
            ? string.Join(",", Enumerable.Range(106, 20))
            : string.Join(",", Enumerable.Range(101, 20));
        Assert.That(TakeAll(), Is.EqualTo(expected));
    }

    [TestCase(BufferOverflow.DropOldest, "2,3")]
    [TestCase(BufferOverflow.DropNewest, "1,2")]
    [TestCase(BufferOverflow.Fail, "1,2,3")]
    public void HandedBackValueFollowsTheOverflowPolicy(BufferOverflow overflow, string kept)
    {
        // 1 reached the subscription's wait, which lost the Race; 2 and 3 filled the buffer (capacity 2) meanwhile. The
        // handed-back 1 is the oldest and goes to the front: DropOldest drops it, DropNewest drops 3, and Fail keeps all
        // three (1 was accepted when it was emitted) without failing the owner. Past its capacity already, the Fail
        // subscription overflows at the next emit, which cancels the owner carrying SubscriptionOverflowException; the
        // owner ends with it once unwound, in the next flush.
        CaptureExceptions();
        var t = new Signal<int>("T");
        var s = new Signal<int>("S");
        var sub = default(Subscription<int>);

        async FlowTask Owner()
        {
            sub = s.Subscribe(BufferPolicy.Queue(2, overflow));
            var r = await FlowTask.Race(t.Next(), sub.Next());
            Log.Add("race " + r.Index);
            await FlowTask.Never();
        }

        var h = World.Run(Owner());
        t.Emit(0);
        s.Emit(1); // to the wait
        s.Emit(2);
        s.Emit(3);
        Tick();
        AssertLog("race 0");
        Assert.That(sub.Count, Is.EqualTo(kept.Split(',').Length));
        Assert.That(Exceptions, Is.Empty);
        if (overflow == BufferOverflow.Fail) s.Emit(4); // overflows: 4 is not kept

        var taken = new List<int>();
        while (sub.TryTake(out var v)) taken.Add(v);
        Assert.That(string.Join(",", taken), Is.EqualTo(kept));
        Tick();
        if (overflow != BufferOverflow.Fail)
        {
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
            Assert.That(Exceptions, Is.Empty);
            return;
        }

        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Fault));
        Assert.That(h.Exception, Is.TypeOf<SubscriptionOverflowException>());
        Assert.That(Exceptions.Single().Exception, Is.SameAs(h.Exception));
        Assert.That(Exceptions.Single().Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
    }

    [Test]
    public void FailPolicyNeedsAnOwningScope()
    {
        // Outside a flow a subscription has no owner, so an overflow under BufferOverflow.Fail would have nobody to
        // fail and would drop the value silently. Such a subscription is refused when it is made.
        CaptureExceptions();
        var sig = new Signal<int>("hits");
        var e = Assert.Throws<FlowMisuseException>(() => sig.Subscribe(BufferPolicy.Queue(1, BufferOverflow.Fail)));
        Assert.That(e!.Message, Does.Contain("outside a flow").And.Contain("DropOldest"));

        // The other policies drop values by design, so they need no owner.
        using (var oldest = sig.Subscribe(BufferPolicy.Queue(1, BufferOverflow.DropOldest)))
        using (var newest = sig.Subscribe(BufferPolicy.Queue(1, BufferOverflow.DropNewest)))
        using (var latest = sig.Subscribe(BufferPolicy.Latest))
        {
            sig.Emit(1);
            sig.Emit(2);
            Assert.That(oldest.TryTake(out var o) && o == 2, Is.True);
            Assert.That(newest.TryTake(out var n) && n == 1, Is.True);
            Assert.That(latest.TryTake(out var l) && l == 2, Is.True);
        }

        // Inside a flow, a Fail subscription is owned by the scope (OverflowUnderFailEndsTheOwnerScopeWithTheExceptionForItsCaller).
        async FlowTask Owner()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(1, BufferOverflow.Fail));
            Log.Add("subscribed");
            await FlowTask.NextFrame();
        }

        World.Run(Owner());
        Tick();
        AssertLog("subscribed");
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void EmitNeverResumesSynchronously()
    {
        var sig = new Signal<int>();

        async FlowTask Receiver()
        {
            await sig.Next();
            Log.Add("receiver");
        }

        async FlowTask Sender()
        {
            await FlowTask.NextFrame();
            sig.Emit(1);
            Log.Add("sender after emit");
            await FlowTask.NextFrame();
        }

        World.Run(Receiver());
        World.Run(Sender());
        Tick();
        AssertLog("sender after emit", "receiver");
    }

    [Test]
    public void CloseWhileWaitingOnNextFails()
    {
        CaptureExceptions();
        var sig = new Signal<int>();

        async FlowTask Waiter()
        {
            try
            {
                await sig.Next();
            }
            finally
            {
                Log.Add("waiter unwound");
            }
        }

        async FlowTask Graceful()
        {
            var (received, _) = await sig.NextOrClosed();
            Log.Add("graceful " + received);
        }

        World.Run(Waiter());
        World.Run(Graceful());
        sig.Close();
        Tick();
        Assert.That(Log.Entries, Is.EquivalentTo(new[] { "waiter unwound", "graceful False" }));
        Assert.That(Exceptions.Single().Exception, Is.InstanceOf<SignalClosedException>());
    }

    /// <summary>A type no other test subscribes with: the subscription pool of this type holds only this test's core.</summary>
    readonly struct Hit
    {
    }

    [Test]
    public void NextOnASubscriptionDisposedThroughACopySaysItWasDisposed()
    {
        // Subscription<T> is a struct: disposing a copy passed by value ends the original too. The next wait on it is a
        // misuse, not a closed source (the signal is still open), and must not name whatever signal reuses the core.
        CaptureExceptions();
        var hits = new Signal<Hit>("hits");
        var other = new Signal<Hit>("otherSignal");

        void Stop(Subscription<Hit> copy) => copy.Dispose();

        async FlowTask Waiter()
        {
            var sub = hits.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            Stop(sub);
            Log.Add("active " + sub.IsActive);
            using var reused = other.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest)); // may reuse the pooled core
            try
            {
                await sub.Next();
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("finally");
            }
        }

        World.Run(Waiter());
        AssertLog("active False", "finally");
        var e = Exceptions.Single().Exception;
        Assert.That(e, Is.TypeOf<FlowMisuseException>());
        Assert.That(e.Message, Does.Contain("disposed").And.Contain("copy"));
        Assert.That(e.Message, Does.Not.Contain("source is closed").And.Not.Contain("otherSignal"));
        Assert.That(hits.IsClosed, Is.False);
    }

    [Test]
    public void NextOrClosedOnASubscriptionThatHasEndedReturnsNotReceived()
    {
        // Subscription.NextOrClosed gives (false, default) once the source is closed and the buffer drained, or once
        // the subscription has ended; only Next treats a wait on an ended subscription as a misuse.
        CaptureExceptions();
        var hits = new Signal<int>("hits");

        async FlowTask<bool> Waiter()
        {
            var sub = hits.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            sub.Dispose();
            return (await sub.NextOrClosed()).Received;
        }

        var h = World.Run(Waiter());
        Assert.That(Exceptions, Is.Empty, () => Exceptions[0].Exception.ToString());
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.Result, Is.False);
    }

    [Test]
    public void DisposingASubscriptionThroughACopyWhileAFlowWaitsSaysSo()
    {
        CaptureExceptions();
        var hits = new Signal<int>("hits");
        var sub = default(Subscription<int>);

        async FlowTask Owner()
        {
            sub = hits.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            await FlowTask.WaitForSeconds(10);
        }

        async FlowTask Waiter() => await sub.Next();

        async FlowTask Graceful() => Log.Add("graceful " + (await sub.NextOrClosed()).Received);

        void Stop(Subscription<int> copy) => copy.Dispose();

        World.Run(Owner());
        World.Run(Waiter());
        World.Run(Graceful());
        Stop(sub);
        Tick();
        AssertLog("graceful False");
        Assert.That(Exceptions.Single().Exception, Is.TypeOf<SignalClosedException>());
        Assert.That(Exceptions.Single().Exception.Message, Does.Contain("disposed (maybe through a copy)"));
    }

    [Test]
    public void SubscriptionDrainsAfterClose()
    {
        var sig = new Signal<int>();

        async FlowTask Root()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.Fail));
            await FlowTask.NextFrame();
            while (true)
            {
                var (received, v) = await sub.NextOrClosed();
                if (!received) break;
                Log.Add("v" + v);
            }

            Log.Add("closed");
        }

        World.Run(Root());
        sig.Emit(1);
        sig.Emit(2);
        sig.Close();
        Tick();
        AssertLog("v1", "v2", "closed");
    }

    [Test]
    public void CloseWithAnErrorFailsNextWithTheErrorAsInnerException()
    {
        // A source that failed (an upstream stream's error) closes its signal with the error: the exception of every
        // flow that waited on Next carries it. NextOrClosed still only sees the close.
        CaptureExceptions();
        var sig = new Signal<int>(World, "feed");
        var error = new FormatException("upstream failed");

        async FlowTask Waiter() => await sig.Next();

        async FlowTask Graceful() => Log.Add("graceful " + (await sig.NextOrClosed()).Received);

        async FlowTask Subscriber()
        {
            var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.Fail));
            await FlowTask.NextFrame();
            Log.Add("buffered " + await sub.Next());
            await sub.Next(); // drained: fails with the close
        }

        async FlowTask Late() => await sig.Next();

        World.Run(Subscriber());
        sig.Emit(5); // buffered by the subscription only
        World.Run(Waiter());
        World.Run(Graceful());
        sig.Close(error);
        sig.Close(new InvalidOperationException("second close")); // ignored: already closed
        Tick();
        World.Run(Late());
        Tick();

        Assert.That(Log.Entries, Is.EquivalentTo(new[] { "graceful False", "buffered 5" }));
        Assert.That(Exceptions.Count, Is.EqualTo(3), "Waiter, Subscriber and Late");
        foreach (var p in Exceptions)
        {
            Assert.That(p.Exception, Is.TypeOf<SignalClosedException>());
            Assert.That(p.Exception.InnerException, Is.SameAs(error));
            Assert.That(p.Exception.Message, Does.Contain("upstream failed"));
        }
    }

    [Test]
    public void ACloseReachesTheCatchesOfItsWaitersInTheOrderTheyBeganToWait([Values] bool fromAFlow)
    {
        // Three flows wait on one signal: B and C at once, A, started first, only after a frame. Close(error) fails their
        // waits, and each catch receives SignalClosedException with the error as its InnerException, in the order the
        // waits began (B, C, A), whether the signal is closed between Ticks or by a flow. A flow that closes it goes on
        // first: the failures are delivered through the resume queue.
        var sig = new Signal<int>(World, "feed");
        var error = new FormatException("upstream failed");

        async FlowTask Waiter(string name, bool waitsAFrameFirst)
        {
            if (waitsAFrameFirst) await FlowTask.NextFrame();
            try
            {
                await sig.Next();
                Log.Add(name + " got a value");
            }
            catch (SignalClosedException e)
            {
                Log.Add(name + " caught " + (ReferenceEquals(e.InnerException, error) ? "the error" : "another error"));
            }
        }

        async FlowTask Closer()
        {
            await FlowTask.NextFrame();
            sig.Close(error);
            Log.Add("closed");
        }

        var waiters = new[] { World.Run(Waiter("A", true)), World.Run(Waiter("B", false)), World.Run(Waiter("C", false)) };
        Tick(); // A begins to wait
        if (fromAFlow) World.Run(Closer());
        else sig.Close(error);
        Tick();

        var caught = new[] { "B caught the error", "C caught the error", "A caught the error" };
        if (fromAFlow) AssertLog(new[] { "closed" }.Concat(caught).ToArray());
        else AssertLog(caught);
        foreach (var w in waiters) Assert.That(w.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void CloseFromAnyThreadKeepsTheError()
    {
        CaptureExceptions();
        var sig = new Signal<int>(World, "feed");
        var error = new FormatException("remote failed");

        async FlowTask Waiter() => await sig.Next();

        World.Run(Waiter());
        var other = new Thread(() => sig.CloseFromAnyThread(error));
        other.Start();
        TestThreads.JoinOrFail(other);
        Assert.That(sig.IsClosed, Is.False, "applied in the next intake");
        Tick();
        Assert.That(sig.IsClosed, Is.True);
        Assert.That(Exceptions.Single().Exception, Is.TypeOf<SignalClosedException>());
        Assert.That(Exceptions.Single().Exception.InnerException, Is.SameAs(error));
    }

    [Test]
    public void ASubscriptionSeesACloseFromAnyThreadOfASignalNoWorldUsed()
    {
        // With no World to apply it in, CloseFromAnyThread only marks the signal closed. A subscription made before it
        // gives what it buffered, then fails with the close's error instead of waiting forever.
        var sig = new Signal<int>("feed");
        var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
        sig.Emit(1);
        var error = new FormatException("remote failed");
        var other = new Thread(() => sig.CloseFromAnyThread(error));
        other.Start();
        TestThreads.JoinOrFail(other);
        Assert.That(sig.IsClosed, Is.True, "no World: marked at once");

        async FlowTask Reader()
        {
            Log.Add("first " + await sub.Next());
            try
            {
                await sub.Next();
            }
            catch (SignalClosedException e)
            {
                Log.Add("closed " + ReferenceEquals(e.InnerException, error));
            }
        }

        var h = World.Run(Reader());
        Tick();
        AssertLog("first 1", "closed True");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        sub.Dispose();
    }

    [Test]
    public void ACloseFromAnyThreadReachesAFirstWaitThatStartsAtTheSameTime([Values] bool onASubscription)
    {
        // CloseFromAnyThread on a signal no World uses yet only marks it closed, and the first wait binds the signal
        // to its World. When the two happen at the same moment, the wait either sees the mark, or the close sees the
        // binding and tells the waiters in the World's next intake. Each used to miss the other, and the wait hung.
        var left = 0;
        TestThreads.RaceRounds(CloseRaceRounds,
            setUp: () => new FirstWait(new Signal<int>("feed"), onASubscription),
            otherThread: w => w.Signal.CloseFromAnyThread(),
            thisThread: w => w.Start(World),
            afterBoth: w =>
            {
                Tick(); // the intake of a close that saw the binding
                if (!w.EndedClosed()) left++;
                w.End();
                Tick();
            });
        Assert.That(left, Is.Zero, $"waits that missed the close, of {CloseRaceRounds}");
    }

    internal const int CloseRaceRounds = 100000;

    /// <summary>The first wait on a signal no World uses yet: on the signal itself, or on a subscription made outside a flow.</summary>
    internal sealed class FirstWait
    {
        internal readonly Signal<int> Signal;
        readonly Subscription<int> _subscription;
        readonly FlowTask<(bool Received, int Value)> _wait;
        FlowHandle<(bool Received, int Value)> _handle;

        internal FirstWait(Signal<int> signal, bool onASubscription)
        {
            Signal = signal;
            if (onASubscription) _subscription = signal.Subscribe(BufferPolicy.Latest);
            _wait = onASubscription ? _subscription.NextOrClosed() : signal.NextOrClosed();
        }

        /// <summary>Runs the wait: its first use binds the signal to <paramref name="world"/>.</summary>
        internal void Start(FlowWorld world) => _handle = world.Run(_wait);

        internal bool EndedClosed() => _handle.Status == FlowStatus.Succeeded && !_handle.Result.Received;

        internal void End()
        {
            _handle.Cancel();
            _subscription.Dispose();
        }
    }

    [Test]
    public void EmitsAndACloseFromAnyThreadArriveInTheNextTickInTheOrderSent()
    {
        // Sends from another thread go through the World's inbox: nothing arrives before the next Tick, and there the
        // emits arrive in the order sent, before the close sent after them.
        var sig = new Signal<int>(World);

        async FlowTask Root()
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(8, BufferOverflow.Fail));
            while (true)
            {
                var (received, v) = await sub.NextOrClosed();
                if (!received) break;
                Log.Add("v" + v);
            }

            Log.Add("closed");
        }

        World.Run(Root());
        TestThreads.WaitOrFail(Task.Run(() =>
        {
            for (var i = 0; i < 5; i++) sig.EmitFromAnyThread(i);
            sig.CloseFromAnyThread();
        }));
        AssertLog();
        Tick();
        AssertLog("v0", "v1", "v2", "v3", "v4", "closed");
    }

    [Test]
    public void WorldPostRunsOnTheWorldThreadInOrder()
    {
        var worldThread = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        TestThreads.WaitOrFail(Task.Run(() =>
        {
            World.Post(() =>
            {
                Log.Add("a");
                threads.Add(Environment.CurrentManagedThreadId);
            });
            World.Post(() =>
            {
                Log.Add("b");
                threads.Add(Environment.CurrentManagedThreadId);
            });
        }));
        AssertLog();
        World.Flush();
        AssertLog("a", "b");
        Assert.That(threads, Has.All.EqualTo(worldThread));
    }

    [Test]
    public void TwoTapsInOneFrameReachOnlyOneReceiver()
    {
        // Two buttons tapped in one frame: the Race takes the tap emitted first, and the other is dropped (Next is an
        // edge).
        var ok = new Signal<FlowUnit>();
        var cancel = new Signal<FlowUnit>();

        async FlowTask<bool> Confirm()
        {
            var r = await FlowTask.Race(ok.Next(), cancel.Next());
            return r.Index == 0;
        }

        var h = World.Run(Confirm());
        ok.Emit(FlowUnit.Default);
        cancel.Emit(FlowUnit.Default);
        ok.Emit(FlowUnit.Default);
        Tick();
        Assert.That(h.Result, Is.True);
    }

    [Test]
    public void PropertyWaitUntilCompletesImmediatelyOrOnChange()
    {
        var hp = new FlowProperty<int>(10);

        async FlowTask Root()
        {
            await hp.WaitUntil(v => v > 0);
            Log.Add("alive");
            var v = await hp.WaitUntil(v => v <= 0);
            Log.Add("dead at " + v);
        }

        World.Run(Root());
        AssertLog("alive");
        hp.Set(5);
        hp.Set(0);
        hp.Set(3);
        Tick();
        AssertLog("alive", "dead at 0");
    }

    [Test]
    public void PropertyChangedSignalAndEquality()
    {
        var property = new FlowProperty<string>("a");

        async FlowTask Root()
        {
            using var changes = property.Changed.Subscribe(BufferPolicy.Queue(8, BufferOverflow.Fail));
            await FlowTask.NextFrame();
            while (changes.TryTake(out var v)) Log.Add(v);
        }

        World.Run(Root());
        property.Set("a"); // equal: no change
        property.Set("b");
        property.Set("c");
        Tick();
        AssertLog("b", "c");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SetFromAWaitUntilPredicateIsAppliedAfterTheCurrentEvaluation(bool setterFirst)
    {
        // A condition that Sets the same property. The nested Set used to re-enter the waiter evaluation: the
        // waiter list became null (later Sets threw NullReferenceException), or a waiter registered after the setter
        // was evaluated with the old value and hung. Now the nested Set is notified after the current one.
        CaptureExceptions();
        var prop = new FlowProperty<int>(0);
        var changes = new List<int>();

        async FlowTask Setter() => await prop.WaitUntil(x =>
        {
            if (x == 1) prop.Set(2);
            return false;
        });

        async FlowTask Waiter()
        {
            Log.Add("waiter " + await prop.WaitUntil(x => x == 2));
            Log.Add("waiter " + await prop.WaitUntil(x => x == 100));
        }

        async FlowTask Changes()
        {
            // Buffered: an edge Next would miss the second of two changes made in one Set.
            using var sub = prop.Changed.Subscribe(BufferPolicy.Queue(8, BufferOverflow.DropOldest));
            while (true) changes.Add(await sub.Next());
        }

        World.Run(Changes());
        if (setterFirst)
        {
            World.Run(Setter());
            World.Run(Waiter());
        }
        else
        {
            World.Run(Waiter());
            World.Run(Setter());
        }

        Assert.DoesNotThrow(() => prop.Set(1));
        Assert.That(prop.Value, Is.EqualTo(2));
        Tick();
        AssertLog("waiter 2");
        Assert.That(changes, Is.EqualTo(new[] { 1, 2 }), "each change is notified, in order");

        Assert.DoesNotThrow(() => prop.Set(100));
        Tick();
        AssertLog("waiter 2", "waiter 100");

        async FlowTask Late() => Log.Add("late " + await prop.WaitUntil(x => x == 5));
        World.Run(Late());
        prop.Set(5);
        Tick();
        AssertLog("waiter 2", "waiter 100", "late 5");
        Assert.That(changes, Is.EqualTo(new[] { 1, 2, 100, 5 }));
        Assert.That(Exceptions, Is.Empty);
    }

    [Test]
    public void AWaitUntilStartedDuringANotificationSeesOnlyTheValuesSetAfterIt()
    {
        // While 1 is notified, a condition Sets 2 and 3 (notified after 1, in order) and starts a flow that waits for 2.
        // That flow started when the value was already 3: the 2 notified after it was set before it started and does not
        // complete its wait; a Set of 2 made later does.
        var prop = new FlowProperty<int>(0);

        async FlowTask WaitForTwo() => Log.Add("saw " + await prop.WaitUntil(x => x == 2));

        bool OnOne(int x)
        {
            if (x != 1) return false;
            prop.Set(2);
            prop.Set(3);
            World.Run(WaitForTwo());
            return true;
        }

        async FlowTask Trigger() => await prop.WaitUntil(OnOne);

        World.Run(Trigger());
        prop.Set(1);
        Tick();
        AssertLog();
        Assert.That(prop.Value, Is.EqualTo(3));
        prop.Set(2);
        Tick();
        AssertLog("saw 2");
    }

    [Test]
    public void AFailingPredicateThrowsAtItsAwaitAfterTheNotificationThatRanIt()
    {
        // A condition throws while the property notifies a Set made in a flow. Its wait fails, and the exception is
        // thrown at the condition's await when that flow resumes, in the order of the resume queue: after the flow that
        // called Set has reached its next await. So the condition's finally, which Sets the property again, runs
        // outside the notification. (The failure used to unwind the condition's flow at once, inside the notification,
        // and the nested Set was notified after it, which SetFromAWaitUntilPredicateIsAppliedAfterTheCurrentEvaluation
        // keeps covering.)
        CaptureExceptions();
        var prop = new FlowProperty<int>(0);

        async FlowTask Failing()
        {
            try
            {
                await prop.WaitUntil(x => x == 1 ? throw new InvalidOperationException("condition bug") : false);
            }
            finally
            {
                prop.Set(7);
                Log.Add("finally " + prop.Value);
            }
        }

        async FlowTask Waiter() => Log.Add("waiter " + await prop.WaitUntil(x =>
        {
            Log.Add("eval " + x);
            return x == 7;
        }));

        async FlowTask Setter()
        {
            await FlowTask.NextFrame();
            prop.Set(1); // inside a flow: the condition's exception is thrown at its await after this flow's turn
            Log.Add("set 1 returned");
        }

        World.Run(Failing());
        var waiter = World.Run(Waiter());
        World.Run(Setter());
        Tick();
        AssertLog("eval 0", "eval 1", "set 1 returned", "eval 7", "finally 7", "waiter 7");
        Assert.That(prop.Value, Is.EqualTo(7));
        Assert.That(waiter.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(Exceptions.Single().Exception.Message, Is.EqualTo("condition bug"));
    }

    [Test]
    public void OnceIsSetOnlyOnce()
    {
        var once = new Once<int>();
        once.Set(1);
        Assert.Throws<FlowMisuseException>(() => once.Set(2));
        Assert.That(once.Value, Is.EqualTo(1));
    }
}
