using System.Collections.Generic;

namespace Katout.FlowTask.Bridges.Tests;

/// <summary>
/// A <see cref="TestWorld"/> per test: reports are recorded, and any report a test did not accept fails it on teardown.
/// </summary>
public abstract class BridgeTestBase
{
    protected TestWorld Test = null!;
    protected FlowWorld World = null!;
    protected List<string> Log = null!;

    [SetUp]
    public void BaseSetUp()
    {
        Test = new TestWorld();
        World = Test.World;
        Log = new List<string>();
    }

    [TearDown]
    public void BaseTearDown() => Test.Dispose();

    protected void Tick(int count = 1) => World.TickFrames(count);

    protected void AssertLog(params string[] expected) =>
        Assert.That(Log, Is.EqualTo(expected), "log: " + string.Join(", ", Log));

    /// <summary>Waits for a completion that is delivered on a thread-pool thread.</summary>
    protected static void SpinUntil(Func<bool> condition)
    {
        Assert.That(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), Is.True, "timed out");
    }
}

public sealed class NetError : Exception
{
    public NetError(string message) : base(message)
    {
    }
}

/// <summary>A SynchronizationContext that queues posts until the test pumps it (stands in for Unity's main thread).</summary>
public sealed class QueueSynchronizationContext : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback, object)> _queue = new();

    public int Pending
    {
        get
        {
            lock (_queue) return _queue.Count;
        }
    }

    public override void Post(SendOrPostCallback d, object state)
    {
        lock (_queue) _queue.Enqueue((d, state));
    }

    public void RunAll()
    {
        while (true)
        {
            (SendOrPostCallback, object) item;
            lock (_queue)
            {
                if (_queue.Count == 0) return;
                item = _queue.Dequeue();
            }

            item.Item1(item.Item2);
        }
    }
}
