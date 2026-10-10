using System;
using Katout.FlowTask.Testing.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Katout.FlowTask.Unity.Editor.Tests;

/// <summary>
/// The synced core AllocationTests rely on Katout.FlowTask.Testing.Unity.AllocationProbe. In the Editor it must be precise;
/// otherwise their zero-allocation assertions would be vacuous (GC.GetAllocatedBytesForCurrentThread returns 0 here).
/// </summary>
public class AllocationProbeEditorTests
{
    static object s_sink;

    [Test]
    public void GetAllocatedBytesForCurrentThreadIsNotFunctionalInUnity()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        s_sink = new byte[64 * 1024];
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;
        s_sink = null;
        Debug.Log($"[FlowTask] GC.GetAllocatedBytesForCurrentThread reported {delta} B for a 64 KB allocation; probe mode {AllocationProbe.Mode} ({AllocationProbe.Calibration})");
        Assert.Pass($"GC.GetAllocatedBytesForCurrentThread delta for 64 KB: {delta} B");
    }

    [Test]
    public void ProbeIsPreciseInTheEditor()
    {
        Assert.That(AllocationProbe.Mode, Is.EqualTo(AllocationProbeMode.ProfilerGcAllocatedInFrame), AllocationProbe.Calibration);
        var before = AllocationProbe.CurrentThreadBytes();
        s_sink = new byte[10000];
        var delta = AllocationProbe.CurrentThreadBytes() - before;
        s_sink = null;
        Assert.That(delta, Is.InRange(10000, 10100), "a 10000-byte array plus its header");
        before = AllocationProbe.CurrentThreadBytes();
        var sum = 0;
        for (var i = 0; i < 1000; i++) sum += i;
        Assert.That(AllocationProbe.CurrentThreadBytes() - before, Is.EqualTo(0));
        Assert.That(sum, Is.EqualTo(499500));
    }
}
