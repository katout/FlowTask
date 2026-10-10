namespace Katout.FlowTask.Tests;

/// <summary>The failure paths (returned values, exceptions, cancellation) and the values that carry them.</summary>
public class FailureTests : FlowTestBase
{
    enum ApiError
    {
        None,
        Timeout,
    }

    // ------------------------------------------------------------------ value types

    [Test]
    public void FlowUnitAndFlowExceptionInfo()
    {
        Assert.That(FlowUnit.Default, Is.EqualTo(new FlowUnit()));
        var p = new FlowExceptionInfo(new Exception("e"), "A > B", FlowExceptionKind.Unhandled);
        Assert.That(p.ToString(), Does.Contain("A > B"));
    }

    [Test]
    public void ValueTypesCompareByValue()
    {
        Assert.That(FlowUnit.Default == new FlowUnit(), Is.True);

        var ex = new Exception("e");
        Assert.That(new FlowExceptionInfo(ex, "A", FlowExceptionKind.Unhandled), Is.EqualTo(new FlowExceptionInfo(ex, "A", FlowExceptionKind.Unhandled)));
    }

    [Test]
    public void FlowExceptionInfoChecksItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new FlowExceptionInfo(null, "A", FlowExceptionKind.Unhandled));
        Assert.That(new FlowExceptionInfo(new Exception("e"), null, FlowExceptionKind.Unhandled).ScopePath, Is.EqualTo(""));
    }

    // ------------------------------------------------------------------ exceptions

    async FlowTask Thrower()
    {
        await FlowTask.NextFrame();
        throw new InvalidOperationException("bug");
    }

    // An awaited child's exception is rethrown at the await: see
    // ExceptionDeliveryTests.AwaitedChildExceptionIsCaughtByTheAwaitingScope and
    // UncaughtExceptionEndsEveryFlowOnItsWayFaulted.

    // The flow that spawned a failing child carries its exception: see
    // SpawnFailureTests.ARootFlowThatSpawnedAFailingChildEndsFaultedAndReportsTheChildsExceptionOnce.

    [Test]
    public void UnhandledExceptionWithoutHandlerThrowsFromTick()
    {
        World.Run(Thrower());
        var ex = Assert.Throws<FlowUnhandledException>(() => Tick(2));
        Assert.That(ex!.ExceptionInfos.Single().Exception.Message, Is.EqualTo("bug"));
    }

    [Test]
    public void ARunCalledFromFlowCodeLeavesItsUnhandledExceptionsToTheOuterCall()
    {
        // Only the outermost Tick, Flush or Run throws: a Run in flow code returns, and the call running that code
        // throws the exception after its work.
        var fail = true;
        Exception caught = null;

        async FlowTask FailsAtStart()
        {
            if (fail) throw new InvalidOperationException("started from flow code");
            await FlowTask.NextFrame();
        }

        async FlowTask StartsAtStart()
        {
            try
            {
                World.Run(FailsAtStart());
                Log.Add("run returned at start");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            await FlowTask.NextFrame();
        }

        async FlowTask StartsWhenResumed()
        {
            await FlowTask.NextFrame();
            try
            {
                World.Run(FailsAtStart());
                Log.Add("run returned when resumed");
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        }

        var thrown = Assert.Throws<FlowUnhandledException>(() => World.Run(StartsAtStart()));
        Assert.That(thrown!.ExceptionInfos.Single().Exception.Message, Is.EqualTo("started from flow code"));
        World.Run(StartsWhenResumed());
        thrown = Assert.Throws<FlowUnhandledException>(() => Tick());
        Assert.That(thrown!.ExceptionInfos.Single().Exception.Message, Is.EqualTo("started from flow code"));
        Assert.That(caught, Is.Null);
        AssertLog("run returned at start", "run returned when resumed");
    }

    [Test]
    public void CleanupExceptionsAreReportedAndCleanupContinues()
    {
        CaptureExceptions();

        async FlowTask Root()
        {
            Flow.AddCleanup(() => Log.Add("cleanup 1"));
            Flow.AddCleanup(() => throw new InvalidOperationException("cleanup boom"));
            Flow.AddCleanup(() => Log.Add("cleanup 3"));
            try
            {
                await FlowTask.WaitForSeconds(100);
            }
            finally
            {
                Log.Add("finally");
                throw new InvalidOperationException("finally boom");
            }
        }

        var h = World.Run(Root());
        h.Cancel();
        Tick();
        AssertLog("finally", "cleanup 3", "cleanup 1");
        Assert.That(Exceptions.Select(p => p.Kind), Is.All.EqualTo(FlowExceptionKind.Cleanup));
        Assert.That(Exceptions.Select(p => p.Exception.Message), Is.EquivalentTo(new[] { "finally boom", "cleanup boom" }));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void AReturnedFailureAnExceptionAndACancellationStayApart()
    {
        // An expected failure can be a returned value (an error code here); a bug is an exception, which a catch
        // receives at the await; a cancellation ends the task it reaches. None of them is reported.
        CaptureExceptions();

        async FlowTask<ApiError> Fetch(bool fail)
        {
            await FlowTask.NextFrame();
            return fail ? ApiError.Timeout : ApiError.None;
        }

        async FlowTask Root()
        {
            var r = await Fetch(true);
            Log.Add("result: " + r);
            try
            {
                await Thrower();
            }
            catch (InvalidOperationException e)
            {
                Log.Add("exception: " + e.Message);
            }

            var c = Flow.Spawn(FlowTask.WaitForSeconds(10));
            c.Cancel();
            Log.Add("cancel: " + c.Status);
        }

        var h = World.Run(Root());
        Tick(5);
        AssertLog("result: Timeout", "exception: bug", "cancel: Canceled");
        Assert.That(Exceptions, Is.Empty);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }
}
