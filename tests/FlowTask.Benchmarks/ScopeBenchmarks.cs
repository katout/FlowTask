using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Cysharp.Threading.Tasks;

namespace Katout.FlowTask.Benchmarks;

/// <summary>
/// Idle costs to subtract from the per-Tick benchmarks: a World with no flows, and one flow awaiting NextFrame.
/// </summary>
[MemoryDiagnoser]
public class TickBaseline
{
    FlowWorld _world;

    [GlobalCleanup]
    public void Cleanup() => _world?.Dispose();

    [GlobalSetup(Target = nameof(IdleWorld_Tick))]
    public void SetupIdle() => _world = new FlowWorld();

    [Benchmark]
    public void IdleWorld_Tick() => _world.Tick(Bench.Dt);

    [GlobalSetup(Target = nameof(NextFrameLoop_Tick))]
    public void SetupLoop()
    {
        _world = new FlowWorld();
        _world.Run(Loop());
    }

    static async FlowTask Loop()
    {
        while (true) await FlowTask.NextFrame();
    }

    [Benchmark]
    public void NextFrameLoop_Tick() => _world.Tick(Bench.Dt);
}

/// <summary>
/// Scope creation cost: starting and completing <see cref="Children"/> child FlowTasks per Tick, sequentially
/// (synchronous children) or concurrently (WhenAll of children that each wait one frame). Every child is a scope in the
/// tree with a pooled node. UniTask/ValueTask sequential variants run the same loop without a scheduler.
/// Per-child cost = (op - NextFrameLoop_Tick) / Children.
/// </summary>
[MemoryDiagnoser]
public class ScopeCreation
{
    [Params(10, 100)]
    public int Children;

    FlowWorld _world;
    FlowTask[] _children;
    long _acc;

    [GlobalCleanup]
    public void Cleanup() => _world?.Dispose();

    [GlobalSetup(Target = nameof(FlowTask_SequentialSyncChildren))]
    public void SetupSequential()
    {
        _world = new FlowWorld();
        _world.Run(SequentialDriver());
    }

    async FlowTask SequentialDriver()
    {
        while (true)
        {
            await FlowTask.NextFrame();
            for (var i = 0; i < Children; i++) _acc += await SyncChild(i);
        }
    }

    static async FlowTask<int> SyncChild(int i) => i;

    [Benchmark]
    public void FlowTask_SequentialSyncChildren() => _world.Tick(Bench.Dt);

    [GlobalSetup(Target = nameof(FlowTask_WhenAllChildren))]
    public void SetupWhenAll()
    {
        _world = new FlowWorld();
        _children = new FlowTask[Children];
        _world.Run(WhenAllDriver());
    }

    async FlowTask WhenAllDriver()
    {
        while (true)
        {
            for (var i = 0; i < _children.Length; i++) _children[i] = FrameChild();
            await FlowTask.WhenAll(_children);
        }
    }

    static async FlowTask FrameChild() => await FlowTask.NextFrame();

    [Benchmark]
    public void FlowTask_WhenAllChildren() => _world.Tick(Bench.Dt);

    static async UniTask<int> UniSyncChild(int i) => i;

    async UniTask<long> UniParent()
    {
        long acc = 0;
        for (var i = 0; i < Children; i++) acc += await UniSyncChild(i);
        return acc;
    }

    [Benchmark]
    public void UniTask_SequentialSyncChildren() => _acc += UniParent().GetAwaiter().GetResult();

    static async ValueTask<int> VtSyncChild(int i) => i;

    async ValueTask<long> VtParent()
    {
        long acc = 0;
        for (var i = 0; i < Children; i++) acc += await VtSyncChild(i);
        return acc;
    }

    [Benchmark]
    public void ValueTask_SequentialSyncChildren() => _acc += VtParent().GetAwaiter().GetResult();
}
