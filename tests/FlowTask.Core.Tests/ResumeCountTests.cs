namespace Katout.FlowTask.Tests;

/// <summary>
/// FlowHandle.ResumeCount: the times the task's async method continued after an await that suspended it. A flow that
/// goes on every Tick counts each Tick; one that only waits counts nothing until its wait ends; a canceled one counts the
/// resume at which it unwinds (or, skipping its unwinding, ends) and then keeps its count. The handle's own node is never
/// reused, so the count stays that flow's while other calls of the same method come and go.
/// </summary>
public class ResumeCountTests : FlowTestBase
{
    static async FlowTask EveryFrame()
    {
        while (true) await FlowTask.NextFrame();
    }

    static async FlowTask WaitSeconds(double seconds) => await FlowTask.WaitForSeconds(seconds);

    static async FlowTask<int> EndsAtOnce() => await FlowTask.FromResult(1);

    static async FlowTask<int> AwaitsWhatEndsAtOnce()
    {
        var sum = 0;
        for (var i = 0; i < 3; i++) sum += await EndsAtOnce();
        return sum;
    }

    static async FlowTask<int> Steps(int count)
    {
        for (var i = 0; i < count; i++) await FlowTask.NextFrame();
        return count;
    }

    [Test]
    public void AFlowThatGoesOnEveryTickCountsEachTick()
    {
        var h = World.Run(EveryFrame());
        Assert.That(h.ResumeCount, Is.EqualTo(0), "the start is not a resume");
        Tick(5);
        Assert.That(h.ResumeCount, Is.EqualTo(5));
        Tick(7);
        Assert.That(h.ResumeCount, Is.EqualTo(12));
        Assert.That(((FlowHandle<FlowUnit>)h).ResumeCount, Is.EqualTo(12), "the typed handle reads the same count");
    }

    [Test]
    public void AFlowThatOnlyWaitsCountsNothingUntilItsWaitEnds()
    {
        var h = World.Run(WaitSeconds(1));
        Tick(30);
        Assert.That(h.ResumeCount, Is.EqualTo(0));
        TickFor(1);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.ResumeCount, Is.EqualTo(1));
        Tick(10);
        Assert.That(h.ResumeCount, Is.EqualTo(1), "an ended task keeps its count");
    }

    [Test]
    public void AnAwaitThatDoesNotSuspendIsNoResume()
    {
        var h = World.Run(AwaitsWhatEndsAtOnce());
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.Result, Is.EqualTo(3));
        Assert.That(h.ResumeCount, Is.EqualTo(0));
    }

    [Test]
    public void ACanceledFlowCountsTheResumeAtWhichItUnwindsAndThenKeepsItsCount()
    {
        var h = World.Run(EveryFrame());
        Tick(3);
        h.Cancel();
        World.Flush();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.ResumeCount, Is.EqualTo(4), "three Ticks and the resume that throws FlowCanceledException");
        Tick(5);
        Assert.That(h.ResumeCount, Is.EqualTo(4));
    }

    [Test]
    public void AFlowThatSkipsItsUnwindingCountsTheSameAsOneThatUnwinds()
    {
        long CountAfterCancel(bool skip)
        {
            using var world = new FlowWorld();
            if (skip) world.SkipUnwinding(typeof(ResumeCountSkipFlows));
            var h = world.Run(ResumeCountSkipFlows.EveryFrame());
            for (var i = 0; i < 3; i++) world.Tick(Dt);
            h.Cancel();
            world.Flush();
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            for (var i = 0; i < 3; i++) world.Tick(Dt);
            return h.ResumeCount;
        }

        Assert.That(CountAfterCancel(false), Is.EqualTo(4));
        Assert.That(CountAfterCancel(true), Is.EqualTo(4));
    }

    [Test]
    public void AFlowCanceledWhileOnlyWaitingCountsOnlyItsUnwinding()
    {
        var h = World.Run(WaitSeconds(10));
        Tick(20);
        h.Cancel();
        World.Flush();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.ResumeCount, Is.EqualTo(1));
    }

    [Test]
    public void AHandleKeepsItsFlowsCountWhileOtherCallsOfTheSameMethodReuseTheirNodes()
    {
        var first = World.Run(Steps(3));
        Tick(3);
        Assert.That(first.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(first.ResumeCount, Is.EqualTo(3));

        // Calls awaited inside a flow end and go back to their pool; the next ones are made from those nodes.
        async FlowTask Many()
        {
            for (var i = 0; i < 5; i++) await Steps(2);
        }

        var many = World.Run(Many());
        Tick(20);
        Assert.That(many.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(many.ResumeCount, Is.EqualTo(5), "one resume per awaited call that suspended it");

        var second = World.Run(Steps(1));
        Tick(1);
        Assert.That(second.ResumeCount, Is.EqualTo(1), "a new flow starts from 0");
        Assert.That(first.ResumeCount, Is.EqualTo(3), "the old handle still reads its own flow");
        Assert.That(first.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [Test]
    public void AHandleWithNoTaskOrNoAsyncMethodCountsZero()
    {
        Assert.That(default(FlowHandle).ResumeCount, Is.EqualTo(0));
        Assert.That(default(FlowHandle<int>).ResumeCount, Is.EqualTo(0));

        FlowHandle self = default, refused = default, wait = default;
        async FlowTask Host()
        {
            wait = Flow.Spawn(FlowTask.DelayFrames(2));
            await FlowTask.DelayFrames(3);
            self.Cancel();
            // Until FlowCanceledException reaches its code, a Spawn starts nothing.
            refused = Flow.Spawn(EveryFrame());
            await FlowTask.Never();
        }

        self = World.Run(Host());
        Tick(3);
        Assert.That(wait.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(wait.ResumeCount, Is.EqualTo(0), "a wait is not an async method");
        Assert.That(self.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(refused.Status, Is.EqualTo(FlowStatus.Canceled), "a Spawn refused in a canceled scope");
        Assert.That(refused.ResumeCount, Is.EqualTo(0));
    }

    [Test]
    public void TheSameFlowsGivenTheSameTicksCountTheSame()
    {
        static List<long> Run()
        {
            using var world = new FlowWorld();
            var handles = new List<FlowHandle>();
            async FlowTask Mixed(int i)
            {
                for (var k = 0; k < i % 4; k++) await FlowTask.NextFrame();
                await FlowTask.Race(FlowTask.DelayFrames(i % 3 + 1), FlowTask.WaitForSeconds(i % 5 * 0.05));
                if (i % 2 == 0) await WaitSeconds(0.1);
            }

            for (var i = 0; i < 20; i++) handles.Add(world.Run(Mixed(i)));
            for (var t = 0; t < 30; t++)
            {
                world.Tick(Dt);
                if (t == 2) handles[7].Cancel();
            }

            var counts = new List<long>();
            foreach (var h in handles) counts.Add(h.ResumeCount);
            return counts;
        }

        var a = Run();
        Assert.That(Run(), Is.EqualTo(a));
        Assert.That(a, Has.Some.GreaterThan(1));
    }
}

/// <summary>The methods a World passes to FlowWorld.SkipUnwinding in <see cref="ResumeCountTests"/>.</summary>
internal static class ResumeCountSkipFlows
{
    internal static async FlowTask EveryFrame()
    {
        while (true) await FlowTask.NextFrame();
    }
}
