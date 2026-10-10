using System;
using System.Collections.Generic;
using System.Linq;
using Katout.FlowTask.Diagnostics;

namespace Katout.FlowTask.Testing;

/// <summary>
/// Drive a World manually with virtual time. Everything is deterministic: the same sequence of
/// Emits and Ticks always produces the same execution order.
/// </summary>
public static class WorldTestExtensions
{
    /// <summary>Default virtual frame time: 1/60 s.</summary>
    public const double DefaultDeltaTime = 1.0 / 60;

    /// <summary>Ticks <paramref name="count"/> times with <paramref name="deltaTime"/>.</summary>
    public static void TickFrames(this FlowWorld world, int count, double deltaTime = DefaultDeltaTime)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        for (var i = 0; i < count; i++) world.Tick(deltaTime);
    }

    /// <summary>Ticks until <paramref name="seconds"/> of UnscaledClock time have passed.</summary>
    public static int TickFor(this FlowWorld world, double seconds, double deltaTime = DefaultDeltaTime)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (deltaTime <= 0) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        var target = world.UnscaledClock.Time + seconds - 1e-9;
        var ticks = 0;
        while (world.UnscaledClock.Time < target)
        {
            world.Tick(deltaTime);
            ticks++;
        }

        return ticks;
    }

    /// <summary>Ticks until <paramref name="condition"/> holds; throws after <paramref name="maxTicks"/>.</summary>
    public static int TickUntil(this FlowWorld world, Func<bool> condition, int maxTicks = 100_000, double deltaTime = DefaultDeltaTime)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (condition == null) throw new ArgumentNullException(nameof(condition));
        var ticks = 0;
        while (!condition())
        {
            if (ticks >= maxTicks) throw new TimeoutException($"Condition not met within {maxTicks} ticks.\n{world.Dump()}");
            world.Tick(deltaTime);
            ticks++;
        }

        return ticks;
    }

    /// <summary>
    /// Runs <paramref name="task"/> and ticks until it ends; returns its result. Throws
    /// <see cref="FlowUnhandledExceptionAssertionException"/> when it faulted, <see cref="FlowAssertionException"/>
    /// when it was canceled, and <see cref="TimeoutException"/> when it did not end within <paramref name="maxTicks"/>.
    /// </summary>
    public static T RunUntilComplete<T>(this FlowWorld world, FlowTask<T> task, int maxTicks = 100_000, double deltaTime = DefaultDeltaTime)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var h = world.Run(task);
        world.TickUntil(() => h.IsCompleted, maxTicks, deltaTime);
        return ResultOf(h);
    }

    /// <summary>Runs <paramref name="task"/> and ticks until it ends; throws as the overload with a result does.</summary>
    public static void RunUntilComplete(this FlowWorld world, FlowTask task, int maxTicks = 100_000, double deltaTime = DefaultDeltaTime)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var h = world.Run(task);
        world.TickUntil(() => h.IsCompleted, maxTicks, deltaTime);
        ResultOf((FlowHandle<FlowUnit>)h);
    }

    internal static FlowWorld Require(FlowWorld world) => world ?? throw new ArgumentNullException(nameof(world));

    static T ResultOf<T>(FlowHandle<T> h) => h.Status switch
    {
        FlowStatus.Succeeded => h.Result,
        // The handle holds only the exception: the scope path went to the World's OnUnhandledException.
        FlowStatus.Faulted => throw new FlowUnhandledExceptionAssertionException(
            h.Exception == null ? null : new FlowExceptionInfo(h.Exception, null, FlowExceptionKind.Unhandled)),
        _ => throw new FlowAssertionException($"Expected the task to complete, but it ended {h.Status} ({h.CancelCause})."),
    };
}

/// <summary>
/// Scope-tree assertions for tests. Tree access is allowed in tests (game logic has no API that searches the scope
/// tree). Every member throws <see cref="FlowAssertionException"/> (with the dump) when the assertion fails, so a call
/// is a complete check.
/// </summary>
public static class FlowAssert
{
    static IEnumerable<FlowScopeInfo> LiveScopes(FlowWorld world) =>
        WorldTestExtensions.Require(world).Diagnostics.Walk().Where(s => s.Kind == FlowScopeKind.Scope);

    /// <summary>Asserts a live scope named <paramref name="name"/> exists.</summary>
    public static FlowScopeInfo ScopeExists(FlowWorld world, string name)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var s = LiveScopes(world).FirstOrDefault(x => x.Name == name);
        if (!s.IsValid) throw new FlowAssertionException($"Expected a live scope '{name}'.\n{world.Dump()}");
        return s;
    }

    /// <summary>Asserts no live scope named <paramref name="name"/> exists.</summary>
    public static void ScopeDoesNotExist(FlowWorld world, string name)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (LiveScopes(world).Any(s => s.Name == name)) throw new FlowAssertionException($"Expected no live scope '{name}'.\n{world.Dump()}");
    }

    /// <summary>Asserts a live scope with exactly this path (e.g. "Game &gt; InGame &gt; Battle") exists.</summary>
    public static FlowScopeInfo ScopePathExists(FlowWorld world, string path)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var s = LiveScopes(world).FirstOrDefault(x => x.Path == path);
        if (!s.IsValid) throw new FlowAssertionException($"Expected a live scope at '{path}'.\n{world.Dump()}");
        return s;
    }

    /// <summary>Asserts the named scope is waiting on something whose description contains <paramref name="waitingFor"/>.</summary>
    public static void ScopeIsWaitingOn(FlowWorld world, string name, string waitingFor)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var s = ScopeExists(world, name);
        if (s.Waiting == null || s.Waiting.IndexOf(waitingFor, StringComparison.Ordinal) < 0)
            throw new FlowAssertionException($"Scope '{name}' waits on '{s.Waiting}', expected '{waitingFor}'.\n{world.Dump()}");
    }

    /// <summary>Asserts the World has no live scopes (every flow ended and nothing leaked).</summary>
    public static void NoLiveScopes(FlowWorld world)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var live = LiveScopes(world).Select(s => s.Path).ToArray();
        if (live.Length > 0) throw new FlowAssertionException($"Expected no live scopes, found: {string.Join(", ", live)}\n{world.Dump()}");
    }
}

/// <summary>A FlowTask.Testing assertion failed: the message tells what was expected, often with the scope tree.</summary>
public class FlowAssertionException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public FlowAssertionException(string message) : base(message) { }
}

/// <summary>
/// A flow exception went unhandled where a test expected none: <see cref="WorldTestExtensions.RunUntilComplete{T}"/>
/// ran a task that faulted, or a <see cref="TestWorld"/> recorded a report.
/// </summary>
public sealed class FlowUnhandledExceptionAssertionException : FlowAssertionException
{
    /// <summary>Creates the exception for <paramref name="info"/>.</summary>
    public FlowUnhandledExceptionAssertionException(FlowExceptionInfo info)
        : base(BuildMessage(info))
    {
        Info = info;
    }

    /// <summary>The report: its exception, scope path (empty when the task's handle gave only the exception) and kind.</summary>
    public FlowExceptionInfo Info { get; }

    static string BuildMessage(FlowExceptionInfo info)
    {
        if (info == null) return "Unhandled flow exception.";
        return info.ScopePath.Length == 0
            ? $"Unhandled flow exception [{info.Kind}]: {info.Exception}"
            : $"Unhandled flow exception [{info.Kind}] at '{info.ScopePath}': {info.Exception}";
    }
}

/// <summary>
/// A World for tests that records every report to OnUnhandledException (also cleanup exceptions and those during
/// Dispose) and fails when disposed if any occurred. Framework adapters build on it.
/// </summary>
public class TestWorld : IDisposable
{
    readonly List<FlowExceptionInfo> _exceptions = new();
    readonly List<FlowWarning> _warnings = new();
    bool _disposed;

    /// <summary>Creates the World, named <paramref name="name"/>, and records its reports and warnings.</summary>
    public TestWorld(string name = null)
    {
        World = new FlowWorld(name);
        World.OnUnhandledException = info =>
        {
            _exceptions.Add(info);
            OnExceptionRecorded(info);
        };
        World.OnWarning += w => _warnings.Add(w);
    }

    /// <summary>The World under test.</summary>
    public FlowWorld World { get; }

    /// <summary>The reports recorded so far, in the order they were made.</summary>
    public IReadOnlyList<FlowExceptionInfo> Exceptions => _exceptions;

    /// <summary>The warnings recorded so far.</summary>
    public IReadOnlyList<FlowWarning> Warnings => _warnings;

    /// <summary>Reports that the test expects; they no longer fail the test on Dispose.</summary>
    public void AcceptExceptions() => _exceptions.Clear();

    /// <summary>Called for each report as it is recorded: an adapter writes it to the test output.</summary>
    protected virtual void OnExceptionRecorded(FlowExceptionInfo info)
    {
    }

    /// <summary>The World under test, for the APIs that take a <see cref="FlowWorld"/>.</summary>
    public static implicit operator FlowWorld(TestWorld w) => w?.World;

    /// <summary>Throws <see cref="FlowUnhandledExceptionAssertionException"/> with the first report, if any was recorded so far.</summary>
    public void ThrowIfUnhandled()
    {
        if (_exceptions.Count == 0) return;
        var first = _exceptions[0];
        throw new FlowUnhandledExceptionAssertionException(first);
    }

    /// <summary>Disposes the World (unwinding every flow) and fails if a report was recorded.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the World and throws <see cref="FlowUnhandledExceptionAssertionException"/> if a report was recorded.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing) return;
        _disposed = true;
        World.Dispose();
        ThrowIfUnhandled();
    }
}
