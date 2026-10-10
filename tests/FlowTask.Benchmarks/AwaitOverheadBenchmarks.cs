using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Cysharp.Threading.Tasks;

namespace Katout.FlowTask.Benchmarks;

/// <summary>
/// Await overhead of FlowTask vs UniTask vs ValueTask/Task.
/// <list type="bullet">
/// <item>SyncChain: a chain of <see cref="Depth"/> nested async methods that all complete synchronously. The FlowTask
/// variant runs <see cref="ChainsPerOp"/> chains per Tick (well under the sync-completion limit of 1024) and reports per chain.</item>
/// <item>Resume: the innermost method waits for an external completion (FlowTask: Signal.Next resumed in the Tick's flush;
/// UniTask: AutoResetUniTaskCompletionSource; ValueTask: a reusable IValueTaskSource), which then unwinds the whole
/// chain. One operation = one resume of the whole chain.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[CategoriesColumn]
public class AwaitOverhead
{
    const int ChainsPerOp = 100;

    [Params(1, 10, 100)]
    public int Depth;

    FlowWorld _world;
    Signal<int> _signal;
    long _acc;
    volatile bool _stop;
    AutoResetUniTaskCompletionSource<int> _uniSource;
    readonly ReusableValueTaskSource _vtSource = new();
    readonly ReusableValueTaskSource _vtPooledSource = new();

    [GlobalCleanup]
    public void Cleanup()
    {
        _stop = true;
        _world?.Dispose();
    }

    // ------------------------------------------------------------------ SyncChain

    [GlobalSetup(Target = nameof(FlowTask_SyncChain))]
    public void SetupFlowSync()
    {
        _world = new FlowWorld();
        _world.Run(SyncDriver());
        _world.Tick(Bench.Dt);
    }

    async FlowTask SyncDriver()
    {
        while (true)
        {
            await FlowTask.NextFrame();
            for (var i = 0; i < ChainsPerOp; i++) _acc += await FlowSyncChain(Depth);
        }
    }

    static async FlowTask<int> FlowSyncChain(int n) => n == 1 ? 1 : await FlowSyncChain(n - 1) + 1;

    [Benchmark(OperationsPerInvoke = ChainsPerOp), BenchmarkCategory("SyncChain")]
    public void FlowTask_SyncChain() => _world.Tick(Bench.Dt);

    static async UniTask<int> UniSyncChain(int n) => n == 1 ? 1 : await UniSyncChain(n - 1) + 1;

    [Benchmark(Baseline = true, OperationsPerInvoke = ChainsPerOp), BenchmarkCategory("SyncChain")]
    public void UniTask_SyncChain()
    {
        for (var i = 0; i < ChainsPerOp; i++) _acc += UniSyncChain(Depth).GetAwaiter().GetResult();
    }

    static async ValueTask<int> VtSyncChain(int n) => n == 1 ? 1 : await VtSyncChain(n - 1) + 1;

    [Benchmark(OperationsPerInvoke = ChainsPerOp), BenchmarkCategory("SyncChain")]
    public void ValueTask_SyncChain()
    {
        for (var i = 0; i < ChainsPerOp; i++) _acc += VtSyncChain(Depth).GetAwaiter().GetResult();
    }

    static async Task<int> TaskSyncChain(int n) => n == 1 ? 1 : await TaskSyncChain(n - 1) + 1;

    [Benchmark(OperationsPerInvoke = ChainsPerOp), BenchmarkCategory("SyncChain")]
    public void Task_SyncChain()
    {
        for (var i = 0; i < ChainsPerOp; i++) _acc += TaskSyncChain(Depth).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------ Resume

    [GlobalSetup(Target = nameof(FlowTask_ResumeChain))]
    public void SetupFlowResume()
    {
        _world = new FlowWorld();
        _signal = new Signal<int>(_world);
        _world.Run(FlowResumeDriver());
    }

    async FlowTask FlowResumeDriver()
    {
        while (true) _acc += await FlowResumeChain(Depth);
    }

    async FlowTask<int> FlowResumeChain(int n) => n == 1 ? await _signal.Next() : await FlowResumeChain(n - 1) + 1;

    [Benchmark, BenchmarkCategory("Resume")]
    public void FlowTask_ResumeChain()
    {
        _signal.Emit(1);
        _world.Tick(Bench.Dt);
    }

    [GlobalSetup(Target = nameof(UniTask_ResumeChain))]
    public void SetupUniResume() => UniResumeDriver().Forget();

    async UniTaskVoid UniResumeDriver()
    {
        while (!_stop) _acc += await UniResumeChain(Depth);
    }

    async UniTask<int> UniResumeChain(int n) => n == 1 ? await NextUni() : await UniResumeChain(n - 1) + 1;

    UniTask<int> NextUni()
    {
        _uniSource = AutoResetUniTaskCompletionSource<int>.Create();
        return _uniSource.Task;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Resume")]
    public void UniTask_ResumeChain() => _uniSource.TrySetResult(1);

    [GlobalSetup(Target = nameof(ValueTask_ResumeChain))]
    public void SetupVtResume() => _ = VtResumeDriver();

    async Task VtResumeDriver()
    {
        while (!_stop) _acc += await VtResumeChain(Depth);
    }

    async ValueTask<int> VtResumeChain(int n) => n == 1 ? await _vtSource.WaitAsync() : await VtResumeChain(n - 1) + 1;

    [Benchmark, BenchmarkCategory("Resume")]
    public void ValueTask_ResumeChain() => _vtSource.SetResult(1);

    [GlobalSetup(Target = nameof(PooledValueTask_ResumeChain))]
    public void SetupVtPooledResume() => _ = VtPooledResumeDriver();

    async Task VtPooledResumeDriver()
    {
        while (!_stop) _acc += await VtPooledResumeChain(Depth);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    async ValueTask<int> VtPooledResumeChain(int n) => n == 1 ? await _vtPooledSource.WaitAsync() : await VtPooledResumeChain(n - 1) + 1;

    [Benchmark, BenchmarkCategory("Resume")]
    public void PooledValueTask_ResumeChain() => _vtPooledSource.SetResult(1);
}

/// <summary>A reusable, allocation-free completion source: the ValueTask counterpart of a resettable signal.</summary>
internal sealed class ReusableValueTaskSource : IValueTaskSource<int>
{
    ManualResetValueTaskSourceCore<int> _core;

    public ValueTask<int> WaitAsync()
    {
        _core.Reset();
        return new ValueTask<int>(this, _core.Version);
    }

    public void SetResult(int value) => _core.SetResult(value);

    public int GetResult(short token) => _core.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

    public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
