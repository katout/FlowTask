using System.Threading;
using Katout.FlowTask.Testing;
using Katout.FlowTask.Testing.NUnit;

namespace Katout.FlowTask.Tests;

/// <summary>Test support and determinism.</summary>
public class DeterminismAndTestingTests
{
    static IReadOnlyList<string> RunScenario(int seed) => DeterminismProbe.Run(seed);

    [Test]
    public void SameInputsGiveTheSameExecutionOrder()
    {
        var a = RunScenario(42);
        var b = RunScenario(42);
        Assert.That(a.Count, Is.GreaterThan(200));
        Assert.That(b, Is.EqualTo(a));
        Assert.That(a.Any(e => e.Contains(" failed: ")), Is.True, "the probe exercises exceptions caught two scopes up");
        Assert.That(a.Any(e => e.EndsWith(" stopped")), Is.True, "the probe exercises exceptions caught by the scope that awaits");
        TestContext.Out.WriteLine("Determinism probe hash (seed 42): " + DeterminismProbe.Hash(a));
    }

    /// <summary>
    /// Determinism across platforms: the reference hash was produced on .NET (x64). The same suite runs in Godot and Unity
    /// (Mono and IL2CPP), so a match there proves the execution order is identical on every platform. Update the
    /// constant only when the probe, the dump format or the execution order changes intentionally (last: the reduced Core,
    /// whose probe catches exceptions with try/catch, waits for the FlowProperty to change after it matched, and whose
    /// wait failures take their turn in the queue).
    /// </summary>
    const string ReferenceProbeHash = "1ad4d534bb87fbb4d896ce0d79dfc3bd8600f29c9e03f13279c00c4246cdc980";

    [Test]
    public void ProbeLogMatchesTheReferenceOnEveryPlatform()
    {
        var hash = DeterminismProbe.Hash(DeterminismProbe.Run(42));
        TestContext.Out.WriteLine("Determinism probe hash: " + hash);
        Assert.That(hash, Is.EqualTo(ReferenceProbeHash));
    }

    [Test]
    public void WorldsAreIndependentAndRunInParallel()
    {
        var expected = RunScenario(7);
        var results = new IReadOnlyList<string>[8];
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() => results[i] = RunScenario(7))).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) TestThreads.JoinOrFail(t);
        foreach (var r in results) Assert.That(r, Is.EqualTo(expected));
    }

    [Test]
    public void ManualDrivingAndHelpers()
    {
        using var tw = new TestWorld();
        FlowWorld world = tw;

        async FlowTask<int> Work()
        {
            await FlowTask.WaitForSeconds(0.5);
            return 5;
        }

        Assert.That(world.RunUntilComplete(Work()), Is.EqualTo(5));
        var ticks = world.TickFor(1.0, 0.1);
        Assert.That(ticks, Is.EqualTo(10));
        world.TickFrames(3);

        async FlowTask Screen() => await FlowTask.Never();

        async FlowTask Game() => await Screen();

        var h = world.Run(Game());
        FlowAssert.ScopePathExists(world, "Game > Screen");
        FlowAssert.ScopeIsWaitingOn(world, "Screen", "Never");
        h.Cancel();
        world.Flush();
        FlowAssert.NoLiveScopes(world);
        Assert.Throws<TimeoutException>(() => world.TickUntil(() => false, maxTicks: 5));
    }

    [Test]
    public void TestWorldFailsOnUnhandledException()
    {
        var tw = new TestWorld();

        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new Exception("bad");
        }

        tw.World.Run(Bad());
        tw.World.Tick(1.0 / 60);
        Assert.That(tw.Exceptions, Has.Count.EqualTo(1));
        Assert.Throws<FlowUnhandledExceptionAssertionException>(() => tw.Dispose());
    }

    [Test]
    [FailOnUnhandledFlowException]
    public void NUnitAdapterRecordsExceptions()
    {
        var w = FlowNUnit.CreateWorld();

        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new Exception("expected in test");
        }

        w.World.Run(Bad());
        w.World.Tick(1.0 / 60);
        Assert.That(w.Exceptions, Has.Count.EqualTo(1));
        w.AcceptExceptions(); // this test expects the report; otherwise [FailOnUnhandledFlowException] fails it
        w.Dispose();
    }

    [Test]
    public void ADisposedNUnitWorldIsNotLeftForTheAttribute()
    {
        // A world disposed by the test has failed it already if it recorded a report: FailOnUnhandledFlowException, run later on this thread,
        // does not hold it (nor fail another test for it).
        var attribute = new FailOnUnhandledFlowExceptionAttribute();
        attribute.BeforeTest(null);
        var w = FlowNUnit.CreateWorld();

        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("expected in test");
        }

        w.World.Run(Bad());
        w.World.Tick(1.0 / 60);
        Assert.Throws<FlowUnhandledExceptionAssertionException>(() => w.Dispose());
        Assert.DoesNotThrow(() => attribute.AfterTest(null));
    }
}
