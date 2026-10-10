namespace Katout.FlowTask.Tests;

public class SmokeTests : FlowTestBase
{
    async FlowTask Simple()
    {
        Log.Add("start");
        await FlowTask.WaitForSeconds(0.1);
        Log.Add("after delay");
    }

    [Test]
    public void RunsToFirstAwaitThenResumesOnTick()
    {
        var h = World.Run(Simple());
        AssertLog("start");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        TickFor(0.2);
        AssertLog("start", "after delay");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    async FlowTask<int> Add(int a, int b)
    {
        await FlowTask.NextFrame();
        return a + b;
    }

    async FlowTask Caller()
    {
        var x = await Add(1, 2);
        Log.Add("x=" + x);
        var y = await Add(x, 10);
        Log.Add("y=" + y);
    }

    [Test]
    public void NestedAwaitsReturnValues()
    {
        World.Run(Caller());
        Tick(5);
        AssertLog("x=3", "y=13");
    }

    async FlowTask WithFinally()
    {
        try
        {
            Log.Add("enter");
            await FlowTask.WaitForSeconds(10);
            Log.Add("never");
        }
        finally
        {
            Log.Add("finally");
        }
    }

    [Test]
    public void CancelRunsFinally()
    {
        var h = World.Run(WithFinally());
        Tick();
        h.Cancel();
        AssertLog("enter");
        Tick();
        AssertLog("enter", "finally");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [Test]
    public void RaceReturnsWinnerAndUnwindsLoser()
    {
        async FlowTask Root()
        {
            var r = await FlowTask.Race(WithFinally(), FlowTask.WaitForSeconds(0.05));
            Log.Add("winner=" + r.Index);
        }

        World.Run(Root());
        TickFor(0.1);
        AssertLog("enter", "finally", "winner=1");
    }

    [Test]
    public void SignalNextReceivesEmit()
    {
        var sig = new Signal<int>();

        async FlowTask Root()
        {
            var v = await sig.Next();
            Log.Add("got " + v);
        }

        World.Run(Root());
        sig.Emit(7);
        AssertLog();
        Tick();
        AssertLog("got 7");
    }

    [Test]
    public void ExceptionWithoutHandlerThrowsFromTick()
    {
        async FlowTask Boom()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("boom");
        }

        World.Run(Boom());
        var ex = Assert.Throws<FlowUnhandledException>(() => Tick(2));
        Assert.That(ex!.ExceptionInfos[0].Exception.Message, Is.EqualTo("boom"));
        Assert.That(ex.ExceptionInfos[0].ScopePath, Does.Contain("Boom"));
    }
}
