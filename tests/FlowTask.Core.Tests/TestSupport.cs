using System.Threading;

namespace Katout.FlowTask.Tests;

/// <summary>Records execution order for assertions on the execution model.</summary>
public sealed class Log
{
    readonly List<string> _entries = new();

    public void Add(string entry) => _entries.Add(entry);

    public IReadOnlyList<string> Entries => _entries;

    public void Clear() => _entries.Clear();

    public override string ToString() => string.Join(", ", _entries);
}

/// <summary>
/// Waits for the helper threads and Tasks that tests start (cross-thread sends, completions). The wait has a limit far
/// above what the work takes, so a hang fails the test by name instead of stalling the run until the session timeout.
/// </summary>
public static class TestThreads
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static void JoinOrFail(Thread thread)
    {
        if (!thread.Join(Limit)) Assert.Fail($"A helper thread did not finish within {Limit.TotalSeconds:0} s.");
    }

    /// <summary>Like <see cref="Task.Wait()"/>: a failure of the Task is thrown (as an AggregateException).</summary>
    public static void WaitOrFail(Task task)
    {
        if (!task.Wait(Limit)) Assert.Fail($"A helper Task did not finish within {Limit.TotalSeconds:0} s.");
    }

    /// <summary>
    /// Runs <paramref name="rounds"/> rounds in which <paramref name="otherThread"/>, on a helper thread, and
    /// <paramref name="thisThread"/> start at the same moment. The helper first spins a number of iterations that
    /// changes from round to round, so that its part lands at different points of this thread's part.
    /// <paramref name="setUp"/> prepares each round on this thread, and <paramref name="afterBoth"/> runs on this thread
    /// once the helper's part has returned. Fails by name instead of hanging when the helper throws or stops.
    /// </summary>
    public static void RaceRounds<TState>(
        int rounds, Func<TState> setUp, Action<TState> otherThread, Action<TState> thisThread, Action<TState> afterBoth)
        where TState : class
    {
        TState state = null;
        var released = 0;
        var done = 0;
        var stop = false;
        Exception failure = null;
        var helper = new Thread(() =>
        {
            try
            {
                for (var i = 1; i <= rounds; i++)
                {
                    for (var spins = 1; Volatile.Read(ref released) < i; spins++)
                    {
                        if (Volatile.Read(ref stop)) return;
                        Pause(spins);
}

                    Thread.SpinWait(i % 64);
                    otherThread(Volatile.Read(ref state));
                    Volatile.Write(ref done, i);
                }
            }
#pragma warning disable CA1031 // The helper thread must not crash the test host; the failure is reported on the test's thread.
            catch (Exception e)
#pragma warning restore CA1031
            {
                Volatile.Write(ref failure, e);
            }
        });
        helper.Start();
        var clock = new System.Diagnostics.Stopwatch();
        try
        {
            for (var i = 1; i <= rounds; i++)
            {
                var s = setUp();
                Volatile.Write(ref state, s);
                Volatile.Write(ref released, i);
                thisThread(s);
                clock.Restart();
                for (var spins = 1; Volatile.Read(ref done) < i; spins++)
                {
                    var e = Volatile.Read(ref failure);
                    if (e != null) Assert.Fail($"The helper thread failed in round {i}: {e}");
                    if (clock.Elapsed > Limit) Assert.Fail($"The helper thread did not finish round {i} within {Limit.TotalSeconds:0} s.");
                    Pause(spins);
                }

                afterBoth(s);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            JoinOrFail(helper);
        }
    }

    /// <summary>
    /// Spins, and gives up the time slice now and then, for a machine with fewer cores than running threads. Never
    /// sleeps: SpinWait.SpinOnce sleeps a millisecond after a few rounds, which makes each round cost that much.
    /// </summary>
    static void Pause(int spins)
    {
        if (spins % 64 == 0) Thread.Yield();
        else Thread.SpinWait(8);
    }
}

/// <summary>
/// Bytes allocated on the current thread, for the allocation tests. GC.GetAllocatedBytesForCurrentThread() returns 0
/// in Unity (Mono/Boehm and IL2CPP), which would make those tests pass without measuring anything, so Unity asks the
/// verification project's probe instead: Unity's "GC Allocated In Frame" profiler counter (byte exact) or, in IL2CPP
/// release players, Boehm's GC_get_total_bytes (tests/unity/Assets/AllocationProbe/AllocationProbe.cs).
/// </summary>
public static class Allocations
{
    public static long CurrentThreadBytes() =>
#if UNITY_5_3_OR_NEWER
        global::Katout.FlowTask.Testing.Unity.AllocationProbe.CurrentThreadBytes();
#else
        GC.GetAllocatedBytesForCurrentThread();
#endif
}

public abstract class FlowTestBase
{
    protected FlowWorld World = null!;
    protected Log Log = null!;
    protected List<FlowExceptionInfo> Exceptions = null!;
    protected List<FlowWarning> Warnings = null!;

    protected const double Dt = 1.0 / 60;

    [SetUp]
    public void BaseSetUp()
    {
        World = new FlowWorld();
        Log = new Log();
        Exceptions = new List<FlowExceptionInfo>();
        Warnings = new List<FlowWarning>();
        World.OnWarning += w => Warnings.Add(w);
    }

    [TearDown]
    public void BaseTearDown()
    {
        if (!World.IsDisposed) World.Dispose();
    }

    /// <summary>Collect the reports instead of throwing them from Tick.</summary>
    protected void CaptureExceptions() => World.OnUnhandledException = p => Exceptions.Add(p);

    protected void Tick(int count = 1, double dt = Dt)
    {
        for (var i = 0; i < count; i++) World.Tick(dt);
    }

    protected void TickFor(double seconds, double dt = Dt)
    {
        var target = World.UnscaledClock.Time + seconds - 1e-9;
        while (World.UnscaledClock.Time < target) World.Tick(dt);
    }

    /// <summary>
    /// Steady-state allocation check: runs <paramref name="round"/> repeatedly and requires the final
    /// rounds to allocate nothing. Early rounds may include one-time warm-up (JIT tiering, pool fill).
    /// </summary>
    protected static void AssertSteadyStateAllocationFree(Action round, int rounds = 6)
    {
#if UNITY_5_3_OR_NEWER
        // Where only a coarse measurement is available (IL2CPP release players), the probe runs an amplified check
        // instead; the precise modes run the assertion below.
        if (global::Katout.FlowTask.Testing.Unity.AllocationProbe.AssertSteadyStateAllocationFreeIfCoarse(round, rounds)) return;
#endif
        var results = new long[rounds];
        for (var i = 0; i < rounds; i++)
        {
            var before = Allocations.CurrentThreadBytes();
            round();
            results[i] = Allocations.CurrentThreadBytes() - before;
        }

        Assert.That(results[rounds - 1] + results[rounds - 2], Is.EqualTo(0),
            "bytes allocated per round: " + string.Join(", ", results));
    }

    protected void AssertLog(params string[] expected) =>
        Assert.That(Log.Entries, Is.EqualTo(expected), "log: " + Log);
}
