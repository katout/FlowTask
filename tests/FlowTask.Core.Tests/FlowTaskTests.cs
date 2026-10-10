using Katout.FlowTask.Testing;

namespace Katout.FlowTask.Tests;

/// <summary>FlowTask.</summary>
public class FlowTaskTests : FlowTestBase
{
    async FlowTask Body(string name)
    {
        Log.Add(name);
        await FlowTask.NextFrame();
        Log.Add(name + " done");
    }

    async FlowTask<int> Value(int v)
    {
        await FlowTask.NextFrame();
        return v;
    }

    [Test]
    public void FlowTaskAndFlowTaskOfTAreAsyncReturnTypes()
    {
        async FlowTask<string> Root()
        {
            await Body("a");
            var v = await Value(5);
            return "v" + v;
        }

        var h = World.Run(Root());
        Tick(3);
        Assert.That(h.Result, Is.EqualTo("v5"));
    }

    [Test]
    public void CallingDoesNotRunTheBody()
    {
        var t = Body("a");
        Assert.That(Log.Entries, Is.Empty);
        Assert.That(t.Status, Is.EqualTo(FlowStatus.Unstarted));
        World.Run(t);
        AssertLog("a");
    }

    [Test]
    public void CombinatorCreationDoesNotStartBranchesUntilAwaited()
    {
        async FlowTask Root()
        {
            var race = FlowTask.Race(Body("x"), Body("y"));
            Log.Add("created");
            await race;
        }

        World.Run(Root());
        Assert.That(Log.Entries.First(), Is.EqualTo("created"));
        Assert.That(Log.Entries, Does.Contain("x"));
    }

    [Test]
    public void PassingTheSameTaskToTwoConsumersFails()
    {
        CaptureExceptions();

        async FlowTask Root()
        {
            var t = Value(1);
            var race = FlowTask.Race(t, FlowTask.WaitForSeconds(1));
            await race;
            await t;
        }

        World.Run(Root());
        Tick(3);
        Assert.That(Exceptions.Single().Exception, Is.InstanceOf<FlowMisuseException>());
    }

    [Test]
    public void OnceAllowsMultipleWaiters()
    {
        var once = new Once<int>();

        async FlowTask Waiter(string n)
        {
            var v = await once;
            Log.Add(n + v);
        }

        World.Run(Waiter("a"));
        World.Run(Waiter("b"));
        once.Set(3);
        Tick();
        World.Run(Waiter("c"));
        AssertLog("a3", "b3", "c3");
    }

    // The two tests below rely on the pools being LIFO: the node released last is the next one rented, so the stale
    // copy points at a node that now belongs to someone else. If the pooling changes, re-check that they still catch a
    // missing token check; they cannot fail spuriously (without reuse the stale copy is simply consumed).

    // Used by this test only, so that its state machine has a pool of its own.
    async FlowTask<int> ReusedValue(int v)
    {
        Log.Add("run " + v);
        await FlowTask.NextFrame();
        return v;
    }

    [Test]
    public void AStaleTaskCopyDoesNotStartAReusedNode()
    {
        CaptureExceptions();

        async FlowTask Root()
        {
            var t = ReusedValue(1);
            Log.Add("a " + await t);
            var t2 = ReusedValue(2); // rents the node that t used
            Log.Add("b " + await t); // stale copy: a misuse, not the start of t2
            Log.Add("c " + await t2);
        }

        World.Run(Root());
        Tick(5);
        AssertLog("run 1", "a 1"); // t2's body must not run through the stale copy
        Assert.That(Exceptions, Has.Count.EqualTo(1));
        Assert.That(Exceptions[0].Exception, Is.InstanceOf<FlowMisuseException>());
    }

    // A value type used by this test only, so that the wait nodes it rents have a pool of their own.
    struct PooledValue
    {
        public int V;
    }

    [Test]
    public void AStaleAwaiterDoesNotReadAnotherFlowsValue()
    {
        CaptureExceptions();
        var once = new Once<PooledValue>();
        once.Set(new PooledValue { V = 1 });
        var sig = new Signal<PooledValue>();
        FlowTask<PooledValue>.Awaiter saved = default;

        async FlowTask Emitter()
        {
            await FlowTask.NextFrame();
            sig.Emit(new PooledValue { V = 99 }); // completes the receiver's wait; its resume is queued after the reader's
        }

        async FlowTask Reader()
        {
#pragma warning disable FLOW007 // on purpose: the awaiter is used by hand, which is what this test checks
            var a = once.Wait().GetAwaiter(); // completes synchronously
#pragma warning restore FLOW007
            Log.Add("first " + a.GetResult().V); // consumes the result and returns the node to the pool
            saved = a;
            await FlowTask.NextFrame();
            Log.Add("stale " + saved.GetResult().V); // a misuse: must throw, not return the receiver's 99
        }

        async FlowTask Receiver()
        {
            var v = await sig.Next(); // rents the node the reader returned
            Log.Add("received " + v.V);
        }

        World.Run(Emitter());
        World.Run(Reader());
        World.Run(Receiver());
        Tick(3);
        Assert.That(Log.Entries, Does.Contain("first 1"), "log: " + Log);
        Assert.That(Log.Entries, Does.Contain("received 99"), "log: " + Log);
        Assert.That(Log.Entries, Has.None.StartsWith("stale"), "log: " + Log);
        Assert.That(Exceptions, Has.Count.EqualTo(1));
        Assert.That(Exceptions[0].Exception, Is.InstanceOf<FlowMisuseException>());
    }

    [Test]
    public void AnAwaiterDrivenByHandTakesNoContinuation()
    {
        // Only a FlowTask method's own await registers a continuation. One registered by hand (or by the builder of a Task
        // method) is never called, and the scope that runs the code is canceled carrying a FlowMisuseException (FLOW005):
        // it unwinds at its next await and ends with that exception. An awaiter that has completed takes no continuation:
        // that registration throws.
        CaptureExceptions();

#pragma warning disable CS1998 // completes at once, on purpose
        async FlowTask<int> AtOnce(int v) => v;
#pragma warning restore CS1998

        async FlowTask Root()
        {
            try
            {
#pragma warning disable FLOW007 // on purpose: the awaiter is used by hand, which is what this test checks
                var value = FlowTask.FromResult(1).GetAwaiter();
                Reject("value", () => value.OnCompleted(() => Log.Add("value ran")));
                var completed = AtOnce(2).GetAwaiter();
                Reject("completed", () => completed.OnCompleted(() => Log.Add("completed ran")));
                Log.Add("completed got " + completed.GetResult());
                var pending = Value(3).GetAwaiter();
#pragma warning restore FLOW007
                pending.OnCompleted(() => Log.Add("pending ran"));
                Log.Add("root goes on");
                await FlowTask.WaitForSeconds(0.1);
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("root finally");
            }
        }

        var h = World.Run(Root());
        TickFor(0.2);
        AssertLog("value rejected", "completed rejected", "completed got 2", "root goes on", "root finally");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Fault));
        var report = Exceptions.Single();
        Assert.That(report.ScopePath, Is.EqualTo("Root"));
        Assert.That(report.Exception, Is.TypeOf<FlowMisuseException>().And.Message.Contains("FLOW005"));
        FlowAssert.NoLiveScopes(World);
    }

    void Reject(string name, Action register)
    {
        try
        {
            register();
            Log.Add(name + " accepted");
        }
        catch (FlowMisuseException)
        {
            Log.Add(name + " rejected");
        }
    }

    static async FlowTask<int> SyncValue(int v) => v;

    [Test]
    public void SynchronousCompletionDoesNotAllocateInSteadyState()
    {
        var rounds = new System.Collections.Generic.List<long>();

        async FlowTask Root()
        {
            for (var round = 0; round < 6; round++)
            {
                var before = Allocations.CurrentThreadBytes();
                var sum = 0;
                for (var i = 0; i < 200; i++) sum += await SyncValue(i);
                await FlowTask.CompletedTask;
                sum += await FlowTask.FromResult(1);
                rounds.Add(Allocations.CurrentThreadBytes() - before);
                await FlowTask.NextFrame();
            }
        }

        World.Run(Root());
        Tick(8);
        Assert.That(rounds[^1] + rounds[^2], Is.EqualTo(0), "bytes per 200 synchronous awaits: " + string.Join(", ", rounds));
    }

    [Test]
    public void DefaultFlowTaskOfTIsNotACompletedTask()
    {
        CaptureExceptions();

        // `await default(FlowTask<int>)` used to return 0, and its Status was Succeeded.
        async FlowTask Main()
        {
            try
            {
                var v = await default(FlowTask<int>);
                Log.Add("defaultT=" + v);
            }
            catch (Exception ex) when (ex is not FlowCanceledException)
            {
                Log.Add("dT-throw:" + ex.GetType().Name);
            }
        }

        World.Run(Main());
        AssertLog("dT-throw:FlowMisuseException");

        // Uncaught, the misuse is an unhandled exception of the scope that awaited, spawned or composed it.
        async FlowTask AwaitIt()
        {
            await default(FlowTask<int>);
            Log.Add("unreachable");
        }

        async FlowTask SpawnIt()
        {
            Flow.Spawn(default(FlowTask<int>));
            await FlowTask.NextFrame();
            Log.Add("unreachable");
        }

        async FlowTask RaceIt()
        {
            await FlowTask.Race(new[] { default(FlowTask<int>), FlowTask.FromResult(1) });
            Log.Add("unreachable");
        }

        World.Run(AwaitIt());
        World.Run(SpawnIt());
        World.Run(RaceIt());
        Tick();
        Assert.That(Exceptions.Select(p => p.Exception.GetType()), Is.EqualTo(new[] { typeof(FlowMisuseException), typeof(FlowMisuseException), typeof(FlowMisuseException) }));
        Assert.That(Exceptions[0].Exception.Message, Does.Contain("default(FlowTask<Int32>)").And.Contain("FromResult"));
        AssertLog("dT-throw:FlowMisuseException");

        Assert.Throws<FlowMisuseException>(() => World.Run(default(FlowTask<int>)));
        Assert.That(default(FlowTask<int>).Status, Is.EqualTo(FlowStatus.Invalid));
        Assert.That(default(FlowTask<int>).ToString(), Is.EqualTo("FlowTask(default)"));

        // FromResult(default) is a completed task, as before.
        var zero = FlowTask.FromResult(0);
        Assert.That(zero.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(World.Run(zero).Result, Is.EqualTo(0));
    }

    [Test]
    public void ACombinatorThatRejectsAnArgumentTakesNoneOfThem()
    {
        // The factories check every argument before they take any: after a rejected one (default, already started), the
        // other arguments can still be awaited.
        async FlowTask<int> One()
        {
            await FlowTask.NextFrame();
            return 1;
        }

        async FlowTask Plain() => await FlowTask.NextFrame();

        async FlowTask Root()
        {
            var started = Plain();
            Flow.Spawn(started);
            var one = One();
            var plain = Plain();
            Assert.Throws<FlowMisuseException>(() => FlowTask.WhenAll(one, default(FlowTask<int>)));
            Assert.Throws<FlowMisuseException>(() => FlowTask.WhenAll(plain, started));
            Assert.Throws<FlowMisuseException>(() => FlowTask.Race(plain, default(FlowTask<int>)));
            Assert.Throws<FlowMisuseException>(() => FlowTask.Race(plain, one, started));
            Assert.Throws<FlowMisuseException>(() => FlowTask.WhenAll(new[] { one, default }));
            Assert.Throws<FlowMisuseException>(() => FlowTask.WhenAll(new[] { plain, started }));
            Assert.Throws<FlowMisuseException>(() => FlowTask.Race(new[] { one, default }));
            Assert.Throws<FlowMisuseException>(() => FlowTask.Race(new[] { plain, started }));
            await plain;
            Log.Add("one " + await one);
        }

        World.Run(Root());
        Tick(3);
        AssertLog("one 1");
    }

    [Test]
    public void DefaultFlowTaskIsTheCompletedTask()
    {
        // Only the non-generic default is a task: FlowTask.CompletedTask is defined as default(FlowTask).
        async FlowTask Main()
        {
            await default(FlowTask);
            Log.Add("default ok");
            FlowTask<FlowUnit> converted = default(FlowTask);
            await converted;
            Log.Add("converted ok");
        }

        World.Run(Main());
        AssertLog("default ok", "converted ok");
        Assert.That(default(FlowTask).Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(FlowTask.CompletedTask.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void StateMachinesArePooled()
    {
        async FlowTask Loop()
        {
            while (true)
            {
                await Value(1);
                await FlowTask.NextFrame();
            }
        }

        World.Run(Loop());
        AssertSteadyStateAllocationFree(() => Tick(200));
    }

    [Test]
    public void DiscardReleasesAnUnstartedTaskWithoutRunningIt()
    {
        var t = Body("discarded");
        t.Discard();
        Assert.That(t.Status, Is.EqualTo(FlowStatus.Invalid));
        t.Discard(); // idempotent
        Assert.That(Log.Entries, Is.Empty);
    }

    [Test]
    public void IsExecutingReflectsFlowCode()
    {
        async FlowTask Root()
        {
            Log.Add("inside " + World.IsExecuting);
            await FlowTask.NextFrame();
            Log.Add("resumed " + World.IsExecuting);
        }

        Assert.That(World.IsExecuting, Is.False);
        World.Run(Root());
        Assert.That(World.IsExecuting, Is.False);
        Tick();
        AssertLog("inside True", "resumed True");
        Assert.That(World.IsBoundToCurrentThread, Is.True);
    }

    [Test]
    public void TickAndFlushAreRejectedWhileTheWorldExecutes([Values("Run", "Tick", "Dispose")] string executing)
    {
        // Flow code and cleanups run inside the World's own call (Run, Tick, Dispose): a Tick or Flush from them would
        // run a whole Tick or flush inside it.
        void TryTickAndFlush()
        {
            foreach (var call in new Action[] { () => World.Tick(0.1), World.Flush })
            {
                try
                {
                    call();
                    Log.Add("ran");
                }
                catch (FlowMisuseException)
                {
                    Log.Add("rejected");
                }
            }
        }

        async FlowTask Root()
        {
            Flow.AddCleanup(TryTickAndFlush);
            if (executing == "Run") TryTickAndFlush();
            await FlowTask.NextFrame();
            if (executing == "Tick") TryTickAndFlush();
            await FlowTask.Never();
        }

        var h = World.Run(Root());
        Tick();
        if (executing == "Dispose")
        {
            World.Dispose();
        }
        else
        {
            h.Cancel();
            Tick();
        }

        AssertLog(executing == "Dispose"
            ? new[] { "rejected", "rejected" }
            : new[] { "rejected", "rejected", "rejected", "rejected" });
    }

    [Test]
    public void AwaitOutsideAFlowIsAMisuse()
    {
        var t = Value(1);
#pragma warning disable FLOW007 // on purpose: the awaiter is used by hand, which is what this test checks
        Assert.Throws<FlowMisuseException>(() => t.GetAwaiter());
#pragma warning restore FLOW007
    }
}
