using System;
using Katout.FlowTask.Testing.Unity;

namespace Katout.FlowTask.Unity.Tests;

/// <summary>
/// Proves the allocation measurement used by the synced core AllocationTests actually observes allocations on this
/// runtime (otherwise a zero-allocation assertion would pass vacuously, e.g. on IL2CPP where
/// GC.GetAllocatedBytesForCurrentThread is not implemented), and runs the synchronous-completion scenario (awaits
/// that complete synchronously through the custom builder) in an amplified form that is meaningful even with a coarse
/// probe.
/// </summary>
public class AllocationProbeTests
{
    static object s_sink;

    [Test]
    public void ProbeObservesAKnownAllocation()
    {
        var runtime = Application.isEditor ? "Editor" : "Player";
#if ENABLE_IL2CPP
        const string backend = "IL2CPP";
#else
        const string backend = "Mono";
#endif
        Debug.Log($"[FlowTask] AllocationProbe on {runtime}/{backend}: mode={AllocationProbe.Mode} ({AllocationProbe.Calibration})");
        var delta = AllocationProbe.MeasureCoarse(() => s_sink = new byte[64 * 1024], 8);
        s_sink = null;
        Assert.That(delta, Is.GreaterThanOrEqualTo(AllocationProbe.IsPrecise ? 8 * 64 * 1024 : 4 * 64 * 1024),
            $"mode {AllocationProbe.Mode}: {delta} bytes observed for 512 KB allocated");
    }

    [Test]
    public void ProbeObservesSmallObjectAllocationsInAggregate()
    {
        var delta = AllocationProbe.MeasureCoarse(() =>
        {
            for (var i = 0; i < 1024; i++) s_sink = new object();
        }, 64);
        s_sink = null;
        // 65536 objects of at least 16 bytes = 1 MB; a coarse probe sees them in chunks.
        Assert.That(delta, Is.GreaterThan(AllocationProbe.CoarseToleranceBytes * 16), $"mode {AllocationProbe.Mode}: {delta}");
    }

    static async FlowTask<int> SyncValue(int v) => v;

    [Test]
    public void SynchronousCompletionThroughTheBuilderIsAllocationFree_Amplified()
    {
        var world = new FlowWorld();
        try
        {
            var sum = 0;

            async FlowTask Loop()
            {
                while (true)
                {
                    for (var i = 0; i < 200; i++) sum += await SyncValue(i);
                    sum += await FlowTask.FromResult(1);
                    await FlowTask.NextFrame();
                }
            }

            world.Run(Loop());
            for (var i = 0; i < 16; i++) world.Tick(1.0 / 60); // warm-up: pools filled
            var delta = AllocationProbe.MeasureCoarse(() => world.Tick(1.0 / 60), 256); // 51,456 awaits
            var limit = AllocationProbe.IsPrecise ? 0 : AllocationProbe.CoarseToleranceBytes;
            Assert.That(delta, Is.LessThanOrEqualTo(limit), $"[{AllocationProbe.Mode}] bytes over 256 Ticks x 201 synchronous awaits");
            Assert.That(sum, Is.GreaterThan(0));
        }
        finally
        {
            world.Dispose();
        }
    }

    [Test]
    public void PooledStateMachinesAcrossTicksAreAllocationFree_Amplified()
    {
        var world = new FlowWorld();
        try
        {
            async FlowTask<int> Step(int i)
            {
                await FlowTask.NextFrame();
                return i + 1;
            }

            async FlowTask Loop()
            {
                var i = 0;
                while (true)
                {
                    i = await Step(i);
                    if (i % 7 == 0) await FlowTask.WhenAll(Step(1), Step(2));
                }
            }

            world.Run(Loop());
            for (var i = 0; i < 32; i++) world.Tick(1.0 / 60);
            var delta = AllocationProbe.MeasureCoarse(() => world.Tick(1.0 / 60), 2048);
            var limit = AllocationProbe.IsPrecise ? 0 : AllocationProbe.CoarseToleranceBytes;
            Assert.That(delta, Is.LessThanOrEqualTo(limit), $"[{AllocationProbe.Mode}] bytes over 2048 Ticks (pooled async state machines + WhenAll)");
        }
        finally
        {
            world.Dispose();
        }
    }

    [Test]
    public void WaitForDestroyLosingARaceEveryFrameIsAllocationFree_Amplified()
    {
        // The documented form FlowTask.Race(work, go.WaitForDestroy()), in a loop: called from flow code, the wait is a
        // leaf, so losing the Race costs no state machine and no FlowCanceledException (an async wrapper allocated and
        // threw one per loss).
        var world = new FlowWorld();
        var go = new GameObject("RaceTarget");
        try
        {
            var frames = 0;

            async FlowTask Loop()
            {
                while (true)
                {
                    var r = await FlowTask.Race(FlowTask.NextFrame(), go.WaitForDestroy());
                    if (r.Index == 0) frames++;
                }
            }

            world.Run(Loop());
            for (var i = 0; i < 32; i++) world.Tick(1.0 / 60); // warm-up: pools filled, the GameObject's Once made
            var delta = AllocationProbe.MeasureCoarse(() => world.Tick(1.0 / 60), 2048);
            var limit = AllocationProbe.IsPrecise ? 0 : AllocationProbe.CoarseToleranceBytes;
            Assert.That(delta, Is.LessThanOrEqualTo(limit), $"[{AllocationProbe.Mode}] bytes over 2048 Ticks (Race lost by WaitForDestroy)");
            Assert.That(frames, Is.GreaterThan(2048));
        }
        finally
        {
            world.Dispose();
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
