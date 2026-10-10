using BenchmarkDotNet.Attributes;

namespace Katout.FlowTask.Benchmarks;

/// <summary>
/// Cost of unwinding <see cref="Scopes"/> suspended state-machine scopes at once, each inside try/finally.
/// <list type="bullet">
/// <item>Wide: <c>FlowWorld.Run(WhenAll(children))</c>: the root is a combinator node (no throw), so exactly
/// <see cref="Scopes"/> FlowCanceledException throws.</item>
/// <item>Deep: a chain of <see cref="Scopes"/> nested methods; each level throws once.</item>
/// </list>
/// Cancel_* cancels the handle and flushes (the unwind runs at the head of the flush). Complete_* builds the same
/// tree and completes it normally (no throw); (Cancel - Complete) / Scopes is the unwinding cost per scope.
/// </summary>
[MemoryDiagnoser]
public class Cancellation
{
    [Params(1, 100)]
    public int Scopes;

    sealed class Box
    {
        public long Finally;
    }

    FlowWorld _world;
    Signal<int> _gate;
    FlowTask[] _children;
    readonly Box _box = new();

    [GlobalSetup]
    public void Setup()
    {
        _world = new FlowWorld();
        _gate = new Signal<int>(_world);
        _children = new FlowTask[Scopes];
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    static async FlowTask Suspended(Signal<int> gate, Box box)
    {
        try
        {
            await gate.Next();
        }
        finally
        {
            box.Finally++;
        }
    }

    FlowTask Wide()
    {
        for (var i = 0; i < _children.Length; i++) _children[i] = Suspended(_gate, _box);
        return FlowTask.WhenAll(_children);
    }

    async FlowTask Deep(int n)
    {
        try
        {
            if (n == 1) await _gate.Next();
            else await Deep(n - 1);
        }
        finally
        {
            _box.Finally++;
        }
    }

    [Benchmark]
    public void Cancel_Wide()
    {
        var h = _world.Run(Wide());
        h.Cancel();
        _world.Flush();
    }

    [Benchmark]
    public void Complete_Wide()
    {
        _world.Run(Wide());
        _gate.Emit(0);
        _world.Flush();
    }

    [Benchmark]
    public void Cancel_Deep()
    {
        var h = _world.Run(Deep(Scopes));
        h.Cancel();
        _world.Flush();
    }

    [Benchmark]
    public void Complete_Deep()
    {
        _world.Run(Deep(Scopes));
        _gate.Emit(0);
        _world.Flush();
    }
}

/// <summary>
/// Race resolution: losers are unwound before the winner's awaiter resumes. A leaf loser (WaitForSeconds, Next) is
/// unregistered without any exception; a state-machine loser unwinds with one FlowCanceledException throw (the unwinding
/// cost of a leaf vs. one scope). One op = Emit + Tick (or Tick only for the time-driven race).
/// </summary>
[MemoryDiagnoser]
public class RaceResolution
{
    FlowWorld _world;
    Signal<int> _hits;

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    void Start(FlowTask driver)
    {
        _world = new FlowWorld();
        _world.Run(driver);
    }

    [GlobalSetup(Target = nameof(Race_LeafLoser_WaitForSeconds))]
    public void SetupLeaf()
    {
        _hits = new Signal<int>();
        Start(LeafLoop(_hits));
    }

    static async FlowTask LeafLoop(Signal<int> hits)
    {
        using var sub = hits.Subscribe(BufferPolicy.Latest);
        while (true) await FlowTask.Race(sub.Next(), FlowTask.WaitForSeconds(5));
    }

    [Benchmark(Baseline = true)]
    public void Race_LeafLoser_WaitForSeconds()
    {
        _hits.Emit(1);
        _world.Tick(Bench.Dt);
    }

    [GlobalSetup(Target = nameof(Race_StateMachineLoser))]
    public void SetupStateMachine()
    {
        _hits = new Signal<int>();
        Start(StateMachineLoop(_hits));
    }

    static async FlowTask Patrol()
    {
        while (true) await FlowTask.NextFrame();
    }

    static async FlowTask StateMachineLoop(Signal<int> hits)
    {
        using var sub = hits.Subscribe(BufferPolicy.Latest);
        while (true) await FlowTask.Race(sub.Next(), Patrol());
    }

    [Benchmark]
    public void Race_StateMachineLoser()
    {
        _hits.Emit(1);
        _world.Tick(Bench.Dt);
    }

    [GlobalSetup(Target = nameof(Race3_TimeDriven_LeafLosers))]
    public void SetupTimeDriven()
    {
        _hits = new Signal<int>();
        Start(TimeDrivenLoop(_hits));
    }

    static async FlowTask TimeDrivenLoop(Signal<int> hits)
    {
        using var sub = hits.Subscribe(BufferPolicy.Latest);
        while (true) await FlowTask.Race(sub.Next(), FlowTask.WaitForSeconds(5), FlowTask.NextFrame());
    }

    [Benchmark]
    public void Race3_TimeDriven_LeafLosers() => _world.Tick(Bench.Dt);
}

/// <summary>
/// Signal throughput: one Emit resumes <see cref="Receivers"/> scopes waiting on Next (edge), or a burst of 8 Emits is
/// buffered by each receiver's Queue subscription and drained in the flush. One op = the emits + one Tick.
/// </summary>
[MemoryDiagnoser]
public class SignalThroughput
{
    const int Burst = 8;

    [Params(1, 100)]
    public int Receivers;

    FlowWorld _world;
    Signal<int> _signal;
    long _acc;

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [GlobalSetup(Target = nameof(Emit_EdgeReceivers))]
    public void SetupEdge()
    {
        _world = new FlowWorld();
        _signal = new Signal<int>(_world);
        for (var i = 0; i < Receivers; i++) _world.Run(EdgeReceiver());
    }

    async FlowTask EdgeReceiver()
    {
        while (true) _acc += await _signal.Next();
    }

    [Benchmark]
    public void Emit_EdgeReceivers()
    {
        _signal.Emit(1);
        _world.Tick(Bench.Dt);
    }

    [GlobalSetup(Target = nameof(EmitBurst_QueueSubscribers))]
    public void SetupQueue()
    {
        _world = new FlowWorld();
        _signal = new Signal<int>(_world);
        for (var i = 0; i < Receivers; i++) _world.Run(QueueReceiver());
    }

    async FlowTask QueueReceiver()
    {
        using var sub = _signal.Subscribe(BufferPolicy.Queue(16, BufferOverflow.DropOldest));
        while (true) _acc += await sub.Next();
    }

    [Benchmark(OperationsPerInvoke = Burst)]
    public void EmitBurst_QueueSubscribers()
    {
        for (var i = 0; i < Burst; i++) _signal.Emit(i);
        _world.Tick(Bench.Dt);
    }
}
