using System;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Katout.FlowTask.Testing.NUnit;

/// <summary>
/// NUnit adapter. Worlds created with <see cref="FlowNUnit.CreateWorld"/> turn unhandled flow exceptions into test
/// failures: each report to OnUnhandledException is written to the test output, and the test fails when the world is
/// disposed, or after the test runs (with <see cref="FailOnUnhandledFlowExceptionAttribute"/>) for a world it did not
/// dispose.
/// </summary>
public static class FlowNUnit
{
    [ThreadStatic] static List<NUnitTestWorld> t_worlds;

    /// <summary>Creates a test world bound to the current NUnit test.</summary>
    public static NUnitTestWorld CreateWorld(string name = null)
    {
        var w = new NUnitTestWorld(name);
        (t_worlds ??= new List<NUnitTestWorld>()).Add(w);
        return w;
    }

    internal static List<NUnitTestWorld> TakeWorlds()
    {
        var list = t_worlds;
        t_worlds = null;
        return list;
    }

    /// <summary>A disposed world has done its own check: the attribute no longer holds it.</summary>
    internal static void Forget(NUnitTestWorld world) => t_worlds?.Remove(world);
}

/// <summary>A <see cref="TestWorld"/> that writes each report to the NUnit test output.</summary>
public sealed class NUnitTestWorld : TestWorld
{
    internal NUnitTestWorld(string name) : base(name)
    {
    }

    /// <inheritdoc/>
    protected override void OnExceptionRecorded(FlowExceptionInfo info) =>
        TestContext.Out.WriteLine($"[FlowTask] {info}");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) FlowNUnit.Forget(this);
        base.Dispose(disposing);
    }
}

/// <summary>Fails the test when a world created by <see cref="FlowNUnit.CreateWorld"/> recorded a report.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class FailOnUnhandledFlowExceptionAttribute : Attribute, ITestAction
{
    /// <summary>Each test.</summary>
    public ActionTargets Targets => ActionTargets.Test;

    /// <summary>Forgets the worlds of earlier tests.</summary>
    public void BeforeTest(ITest test) => FlowNUnit.TakeWorlds();

    /// <summary>Fails the test if a world it created, and did not dispose, recorded a report.</summary>
    public void AfterTest(ITest test)
    {
        var worlds = FlowNUnit.TakeWorlds();
        if (worlds == null) return;
        foreach (var w in worlds)
        {
            if (w.Exceptions.Count > 0)
                Assert.Fail($"Unhandled flow exception {w.Exceptions[0]}");
        }
    }
}
