using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Katout.FlowTask.Testing.Unity;

public enum AllocationProbeMode
{
    /// <summary><c>GC.GetAllocatedBytesForCurrentThread()</c>, as on .NET. Not functional in Unity (returns 0 on Mono and IL2CPP).</summary>
    PerThreadCounter,

    /// <summary>
    /// Unity's "GC Allocated In Frame" profiler counter read live through <c>ProfilerRecorder.CurrentValue</c>: byte
    /// exact, fed by Unity's GC.Alloc hook. Available in the Editor and in development players (Mono and IL2CPP).
    /// Resets at frame boundaries, which never fall inside a synchronous test.
    /// </summary>
    ProfilerGcAllocatedInFrame,

    /// <summary>
    /// IL2CPP release players: Boehm's cumulative allocation counter (<c>GC_get_total_bytes</c>, linked from the
    /// IL2CPP runtime via <c>__Internal</c>). Process-wide and updated per thread-local free-list refill (chunks).
    /// </summary>
    BoehmTotalBytes,

    /// <summary>Last resort: GC heap in use (<c>Profiler.GetMonoUsedSizeLong</c>), measured with the GC disabled.</summary>
    GcHeapUsage,
}

/// <summary>
/// Replacement for <c>GC.GetAllocatedBytesForCurrentThread()</c> in the core tests (Allocations in TestSupport.cs).
/// That API is not functional in Unity: Mono (Boehm) returns 0 (verified on 6000.3.7f1: 0 bytes reported for a 64 KB
/// allocation) and IL2CPP does not implement the icall (libil2cpp icalls/mscorlib/System/GC.cpp:
/// IL2CPP_NOT_IMPLEMENTED_ICALL, returns 0) — every zero-allocation assertion would pass vacuously. The probe
/// calibrates itself with a known allocation and uses the most precise counter that actually observes it.
/// </summary>
[Preserve]
public static class AllocationProbe
{
    /// <summary>Rounds measured together in coarse mode (after the normal warm-up rounds).</summary>
    public const int CoarseRounds = 64;

    /// <summary>Coarse mode tolerance: one GC block of counter granularity / background noise over all rounds.</summary>
    public const long CoarseToleranceBytes = 4096;

    static object[] s_sink;
    static AllocationProbeMode? s_mode;
    static string s_calibration = "";
    static ProfilerRecorder s_gcAllocInFrame;

    public static AllocationProbeMode Mode
    {
        get
        {
            if (s_mode == null) s_mode = Detect();
            return s_mode.Value;
        }
    }

    /// <summary>True when the probe measures individual allocations exactly (the original assertions apply unchanged).</summary>
    public static bool IsPrecise => Mode == AllocationProbeMode.PerThreadCounter || Mode == AllocationProbeMode.ProfilerGcAllocatedInFrame;

    /// <summary>How the mode was chosen (bytes observed for a known 80 KB allocation per candidate).</summary>
    public static string Calibration
    {
        get
        {
            _ = Mode;
            return s_calibration;
        }
    }

    public static long CurrentThreadBytes()
    {
        switch (Mode)
        {
            case AllocationProbeMode.PerThreadCounter:
                return GC.GetAllocatedBytesForCurrentThread();
            case AllocationProbeMode.ProfilerGcAllocatedInFrame:
                return s_gcAllocInFrame.CurrentValue;
            case AllocationProbeMode.BoehmTotalBytes:
                return BoehmTotalBytes();
            default:
                return Profiler.GetMonoUsedSizeLong();
        }
    }

    /// <summary>
    /// Hook called at the top of the synced <c>FlowTestBase.AssertSteadyStateAllocationFree</c>. With a precise probe
    /// it returns false and the original helper runs unchanged. With a coarse probe it performs an amplified check:
    /// the usual warm-up rounds, then <see cref="CoarseRounds"/> rounds measured as one block (GC disabled when the
    /// measurement is heap-based), which must stay within <see cref="CoarseToleranceBytes"/> in total.
    /// </summary>
    public static bool AssertSteadyStateAllocationFreeIfCoarse(Action round, int rounds)
    {
        if (IsPrecise) return false;
        var warmup = new long[rounds];
        for (var i = 0; i < rounds; i++)
        {
            var b = CurrentThreadBytes();
            round();
            warmup[i] = CurrentThreadBytes() - b;
        }

        var delta = MeasureCoarse(round, CoarseRounds);
        Assert.That(delta, Is.LessThanOrEqualTo(CoarseToleranceBytes),
            $"[{Mode}] bytes allocated over {CoarseRounds} steady-state rounds: {delta} (warm-up rounds: {string.Join(", ", warmup)}; {Calibration})");
        return true;
    }

    /// <summary>Bytes observed while running <paramref name="round"/> <paramref name="count"/> times.</summary>
    public static long MeasureCoarse(Action round, int count)
    {
        var disableGc = Mode == AllocationProbeMode.GcHeapUsage && !Application.isEditor;
        var previous = GarbageCollector.GCMode;
        if (disableGc)
        {
            GC.Collect();
            GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
        }

        try
        {
            var before = CurrentThreadBytes();
            for (var i = 0; i < count; i++) round();
            return CurrentThreadBytes() - before;
        }
        finally
        {
            if (disableGc) GarbageCollector.GCMode = previous;
        }
    }

    static AllocationProbeMode Detect()
    {
        // Known allocation: 1024 small objects plus a 64 KB array (>= 80 KB with object headers).
        const long expected = 64 * 1024 + 1024 * 16;
        var perThread = Observe(() => GC.GetAllocatedBytesForCurrentThread());
        s_calibration = $"calibration for {expected} B: GC.GetAllocatedBytesForCurrentThread saw {perThread} B";
        if (perThread >= expected) return AllocationProbeMode.PerThreadCounter;

        long recorder = -1;
        try
        {
            s_gcAllocInFrame = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            if (s_gcAllocInFrame.Valid) recorder = Observe(() => s_gcAllocInFrame.CurrentValue);
        }
        catch (Exception)
        {
            recorder = -1;
        }

        s_calibration += $", ProfilerRecorder(GC Allocated In Frame) saw {recorder} B";
        if (recorder >= expected && recorder < 2 * expected) return AllocationProbeMode.ProfilerGcAllocatedInFrame;

        var boehm = Observe(BoehmTotalBytes);
        s_calibration += $", GC_get_total_bytes saw {boehm} B";
        if (boehm >= expected) return AllocationProbeMode.BoehmTotalBytes;

        s_calibration += ", falling back to GC heap usage";
        return AllocationProbeMode.GcHeapUsage;
    }

    static long Observe(Func<long> counter)
    {
        try
        {
            var before = counter();
            if (before < 0) return -1;
            var sink = new object[1025];
            for (var i = 0; i < 1024; i++) sink[i] = new object();
            sink[1024] = new byte[64 * 1024];
            s_sink = sink;
            var after = counter();
            s_sink = null;
            return after - before;
        }
        catch (Exception)
        {
            return -1;
        }
    }

#if ENABLE_IL2CPP && !UNITY_EDITOR
    // Boehm GC is statically linked into the IL2CPP runtime (GameAssembly / libil2cpp), so "__Internal" resolves it.
    [DllImport("__Internal")]
    static extern UIntPtr GC_get_total_bytes();

    static long BoehmTotalBytes() => (long)GC_get_total_bytes().ToUInt64();
#else
    static long BoehmTotalBytes() => -1;
#endif
}
