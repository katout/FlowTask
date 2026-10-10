using System.Linq;
using Cysharp.Threading.Tasks;

namespace Katout.FlowTask.Bridges.Tests;

/// <summary>UniTask -> FlowTask: result, exceptions, cancellation, resume on the World thread.</summary>
public class UniTaskToFlowTests : BridgeTestBase
{
    [Test]
    public void ResultResumesTheFlowInTheNextFlushNotInsideTrySetResult()
    {
        var source = new UniTaskCompletionSource<int>();

        async FlowTask Root()
        {
            var v = await FlowUniTask.FromUniTask(_ => source.Task);
            Log.Add("v" + v);
        }

        World.Run(Root());
        Tick();
        AssertLog();
        source.TrySetResult(5);
        AssertLog(); // never resumed synchronously by the external completion
        Tick();
        AssertLog("v5");
    }

    [Test]
    public void SynchronouslyCompletedUniTaskCompletesTheAwaitSynchronously()
    {
        async FlowTask Root()
        {
            Log.Add("v" + await FlowUniTask.FromUniTask(_ => UniTask.FromResult(3)));
            await FlowUniTask.FromUniTask(_ => UniTask.CompletedTask);
            Log.Add("done");
        }

        var h = World.Run(Root());
        AssertLog("v3", "done");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    static async UniTask<int> Download(UniTaskCompletionSource gate, CancellationToken ct)
    {
        await gate.Task;
        ct.ThrowIfCancellationRequested();
        return 42;
    }

    [Test]
    public void AsyncUniTaskMethod()
    {
        var gate = new UniTaskCompletionSource();

        async FlowTask Root()
        {
            Log.Add("got " + await FlowUniTask.FromUniTask(ct => Download(gate, ct)));
        }

        World.Run(Root());
        Tick(3);
        AssertLog();
        gate.TrySetResult();
        Tick();
        AssertLog("got 42");
    }

    [Test]
    public void CompletionOnAnotherThreadResumesOnTheWorldThread()
    {
        var worldThread = Environment.CurrentManagedThreadId;
        var source = new UniTaskCompletionSource<int>();
        var resumedOn = -1;

        async FlowTask Root()
        {
            var v = await FlowUniTask.FromUniTask(_ => source.Task);
            resumedOn = Environment.CurrentManagedThreadId;
            Log.Add("v" + v);
        }

        World.Run(Root());
        Task.Run(() => source.TrySetResult(9)).Wait();
        AssertLog();
        Tick();
        AssertLog("v9");
        Assert.That(resumedOn, Is.EqualTo(worldThread));
    }

    [Test]
    public void NonGenericUniTask()
    {
        var source = new UniTaskCompletionSource();

        async FlowTask Root()
        {
            await FlowUniTask.FromUniTask(_ => source.Task);
            Log.Add("resumed");
        }

        World.Run(Root());
        source.TrySetResult();
        Tick();
        AssertLog("resumed");
    }

    [Test]
    public void AlreadyRunningUniTaskAsFlow()
    {
        var source = new UniTaskCompletionSource<string>();
        var voidSource = new UniTaskCompletionSource();

        async FlowTask Root()
        {
            Log.Add(await source.Task.AsFlow());
            await voidSource.Task.AsFlow();
            Log.Add("void");
        }

        World.Run(Root());
        source.TrySetResult("hello");
        Tick();
        AssertLog("hello");
        voidSource.TrySetResult();
        Tick();
        AssertLog("hello", "void");
    }

    // A UniTask's exception is thrown at the await: ExternalCancellationBridgeTests.TheUniTasksExceptionIsThrownAtTheAwait.

    [Test]
    public void ExceptionAfterSuspensionIsUnhandled()
    {
        var source = new UniTaskCompletionSource<int>();

        async FlowTask Root()
        {
            await FlowUniTask.FromUniTask(_ => source.Task);
            Log.Add("unreachable");
        }

        World.Run(Root());
        Tick();
        source.TrySetException(new InvalidOperationException("late bug"));
        Tick();
        AssertLog();
        Assert.That(Test.Exceptions.Single().Exception.Message, Is.EqualTo("late bug"));
        Test.AcceptExceptions();
    }

    [Test]
    public void ACatchAtTheAwaitTakesTheChosenExceptionOthersStayUnhandled()
    {
        // Completed at once or later, with or without a value: the exception reaches the catch around the await.
        var late = new UniTaskCompletionSource<int>();

        async FlowTask Root()
        {
            try
            {
                await FlowUniTask.FromUniTask<int>(_ => UniTask.FromException<int>(new NetError("offline")));
                Log.Add("unreachable");
            }
            catch (NetError e)
            {
                Log.Add("err " + e.Message);
            }

            Log.Add("ok " + await FlowUniTask.FromUniTask(_ => UniTask.FromResult(5)));
            try
            {
                await FlowUniTask.FromUniTask(_ => late.Task);
                Log.Add("unreachable");
            }
            catch (NetError e)
            {
                Log.Add("late err " + e.Message);
            }

            try
            {
                await FlowUniTask.FromUniTask(_ => UniTask.FromException(new NetError("void")));
                Log.Add("unreachable");
            }
            catch (NetError)
            {
                Log.Add("void err");
            }

            try
            {
                await FlowUniTask.FromUniTask<int>(_ => UniTask.FromException<int>(new InvalidOperationException("bug")));
            }
            catch (NetError)
            {
                Log.Add("unreachable");
            }

            Log.Add("unreachable");
        }

        World.Run(Root());
        Tick();
        late.TrySetException(new NetError("timeout"));
        Tick();
        AssertLog("err offline", "ok 5", "late err timeout", "void err");
        Assert.That(Test.Exceptions.Single().Exception, Is.InstanceOf<InvalidOperationException>());
        Test.AcceptExceptions();
    }

    // A UniTask canceled outside the flow throws an OperationCanceledException at the await, an exception like any other:
    // ExternalCancellationBridgeTests.AUniTaskCanceledOutsideThrowsAPlainOperationCanceledExceptionAtTheAwait.

    [Test]
    public void ScopeCancellationReachesTheUniTaskToken()
    {
        CancellationToken seen = default;

        async FlowTask Root()
        {
            try
            {
                await FlowUniTask.FromUniTask<int>(ct =>
                {
                    seen = ct;
                    return UniTask.Never<int>(ct);
                });
            }
            finally
            {
                Log.Add("finally");
            }
        }

        var h = World.Run(Root());
        Assert.That(seen.CanBeCanceled && !seen.IsCancellationRequested, Is.True);
        h.Cancel();
        Tick(2);
        Assert.That(seen.IsCancellationRequested, Is.True);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        AssertLog("finally");
        Assert.That(Test.Exceptions, Is.Empty, "the UniTask's own cancellation after the scope ended is not reported");
    }

    [Test]
    public void RaceLoserCancelsTheAsyncUniTaskMethod()
    {
        CancellationToken seen = default;
        var observedCancel = false;

        async UniTask<int> LongOperation(CancellationToken ct)
        {
            seen = ct;
            try
            {
                await UniTask.Never(ct);
                return 1;
            }
            catch (OperationCanceledException)
            {
                observedCancel = true;
                throw;
            }
        }

        async FlowTask Root()
        {
            var r = await FlowTask.Race(FlowUniTask.FromUniTask(LongOperation).ToFlowTask(), FlowTask.WaitForSeconds(0.1));
            Log.Add("winner " + r.Index);
        }

        World.Run(Root());
        World.TickFor(0.2);
        AssertLog("winner 1");
        Assert.That(seen.IsCancellationRequested, Is.True);
        Assert.That(observedCancel, Is.True);
        FlowAssert.NoLiveScopes(World);
    }
}

/// <summary>FlowHandle -> UniTask.</summary>
public class FlowToUniTaskTests : BridgeTestBase
{
    static async FlowTask<int> Work()
    {
        await FlowTask.NextFrame();
        return 3;
    }

    [Test]
    public void CompletesWithTheResult()
    {
        var h = World.Run(Work());
        var u = h.ToUniTask();
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Pending));
        Tick();
        SpinUntil(() => u.Status != UniTaskStatus.Pending);
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Succeeded));
        Assert.That(u.GetAwaiter().GetResult(), Is.EqualTo(3));
    }

    [Test]
    public void AlreadyCompletedHandleGivesACompletedUniTask()
    {
        async FlowTask<int> Now() => 7;

        var h = World.Run(Now());
        var u = h.ToUniTask();
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Succeeded));
        Assert.That(u.GetAwaiter().GetResult(), Is.EqualTo(7));
    }

    [Test]
    public void CanceledFlowGivesACanceledUniTask()
    {
        var h = World.Run(Work());
        var u = h.ToUniTask();
        h.Cancel();
        Tick();
        SpinUntil(() => u.Status != UniTaskStatus.Pending);
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Canceled));
        Assert.Throws<OperationCanceledException>(() => u.GetAwaiter().GetResult());
    }

    [Test]
    public void FaultedFlowGivesTheOriginalException()
    {
        async FlowTask<int> Broken()
        {
            await FlowTask.NextFrame();
            throw new NetError("broken");
        }

        var h = World.Run(Broken());
        var u = h.ToUniTask();
        Tick();
        SpinUntil(() => u.Status != UniTaskStatus.Pending);
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Faulted));
        Assert.Throws<NetError>(() => u.GetAwaiter().GetResult());
        Test.AcceptExceptions();
    }

    [Test]
    public void NonGenericHandle()
    {
        async FlowTask Step() => await FlowTask.NextFrame();

        var h = World.Run(Step());
        UniTask u = h.ToUniTask();
        Tick();
        SpinUntil(() => u.Status != UniTaskStatus.Pending);
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Succeeded));
    }

    [Test]
    public void DoesNotConsumeTheHandlesSingleAwait()
    {
        var h = World.Run(Work());
        var u = h.ToUniTask();

        async FlowTask Joiner() => Log.Add("joined " + await h.Join());

        World.Run(Joiner());
        Tick();
        AssertLog("joined 3");
        SpinUntil(() => u.Status != UniTaskStatus.Pending);
        Assert.That(u.GetAwaiter().GetResult(), Is.EqualTo(3));
    }

    [Test]
    public void CompletionIsPostedToTheCallersSynchronizationContext()
    {
        var context = new QueueSynchronizationContext();
        var previous = SynchronizationContext.Current;
        UniTask<int> u;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var h = World.Run(Work());
            u = h.ToUniTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Tick();
        SpinUntil(() => context.Pending > 0);
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Pending), "not completed until the context runs the post");
        context.RunAll(); // e.g. Unity's main-thread SynchronizationContext pump
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Succeeded));
        Assert.That(u.GetAwaiter().GetResult(), Is.EqualTo(3));
    }

    [Test]
    public void ToUniTaskWithoutASynchronizationContextCompletesOnTheWorldThread()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var worldThread = Environment.CurrentManagedThreadId;
            var thread = -1;
            var executing = true;
            var h = World.Run(Work());

            async UniTask Consumer()
            {
                var v = await h.ToUniTask();
                thread = Environment.CurrentManagedThreadId;
                executing = World.IsExecuting;
                Log.Add("consumer " + v);
            }

            var consumer = Consumer();
            Assert.That(consumer.Status, Is.EqualTo(UniTaskStatus.Pending));
            Tick();
            Assert.That(consumer.Status, Is.EqualTo(UniTaskStatus.Succeeded), "completed before Tick returned");
            Assert.That(thread, Is.EqualTo(worldThread));
            Assert.That(executing, Is.False, "not inside the flush");
            AssertLog("consumer 3");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Test]
    public async Task UniTaskCodeCanAwaitAFlow()
    {
        var h = World.Run(Work());

        async UniTask<int> Consumer(FlowHandle<int> handle) => await handle.ToUniTask() * 10;

        var consumer = Consumer(h).AsTask();
        Tick();
        Assert.That(await consumer.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(30));
    }
}
