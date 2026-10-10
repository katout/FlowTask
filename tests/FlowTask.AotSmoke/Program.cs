using System;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Katout.FlowTask.AotSmoke;

/// <summary>
/// NativeAOT smoke test: representative scenarios plus steady-state zero-allocation checks, printed as PASS/FAIL.
/// Exit code 0 only when every check passes. No test framework, so the binary stays reflection-free.
/// </summary>
internal static class Program
{
    static int s_passed;
    static int s_failed;
    static int s_known;

    static int Main()
    {
        // PublishAot also sets the IsDynamicCodeSupported switch for `dotnet run`, so ask the JIT directly.
        var nativeAot = JitInfo.GetCompiledMethodCount() == 0;
        Console.WriteLine($"FlowTask AOT smoke | {RuntimeInformation.FrameworkDescription} | {RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture} | NativeAOT={nativeAot}");
        Console.WriteLine();

        Console.WriteLine("-- scenarios");
        Run("WaitForSeconds", Scenarios.WaitForSeconds);
        Run("Race (leaf, tuple, array)", Scenarios.Race);
        Run("WhenAll (tuple, array)", Scenarios.WhenAll);
        Run("Signal edge + Close/NextOrClosed", Scenarios.SignalEdge);
        Run("Subscription Queue/Latest", Scenarios.Subscription);
        Run("FlowProperty.WaitUntil + Once", Scenarios.PropertyAndOnce);
        Run("Exception caught by an outer flow", Scenarios.CaughtException);
        Run("Uncaught exception -> OnUnhandledException, scope path", Scenarios.UncaughtException);
        Run("Awaits in finally after a cancel", Scenarios.CleanupAfterCancel);
        Run("Cancellation runs finally/AddCleanup/Own", Scenarios.CancellationWithFinally);
        Run("Spawn + handle Join", Scenarios.SpawnAndJoin);
        Run("Clock Pause", Scenarios.Pause);
        Run("EnemyAI", Scenarios.EnemyAI);
        Run("Bridges (event, Task from another thread)", Scenarios.Bridges);
        Run("Unhandled exception reaches FlowWorld.OnUnhandledException", Scenarios.UnhandledException);
        Run("Unbridged await is an unhandled exception (awaiter type check)", Scenarios.UnbridgedAwait);
        Run("Dump (scope names, declaring types and method names from state-machine types)", Scenarios.Dump);

        Console.WriteLine();
        Console.WriteLine("-- steady-state allocation (last 2 of 8 rounds must be 0 bytes)");
        Run("alloc: per-tick waits", AllocationChecks.PerTickWaits);
        Run("alloc: child FlowTask methods + WhenAll (pooled builder)", AllocationChecks.PooledStateMachines);
        Run("alloc: signal send/receive", AllocationChecks.SignalSendReceive);
        Run("alloc: Race with leaf losers", AllocationChecks.RaceWithLeafLosers);
        Run("alloc: FlowProperty + Subscribe + Pause (handles in a child scope)", AllocationChecks.PropertySubscriptionPause);

        Console.WriteLine();
        Console.WriteLine("-- regression checks for fixed Core issues");
        Run("alloc: handles disposed early (using) in a long-lived loop scope", AllocationChecks.HandlesDisposedEarlyInALongLivedScope);

        Console.WriteLine();
        Console.WriteLine("-- measurements (informational)");
        Run("info: Race with a state-machine loser", AllocationChecks.MeasureStateMachineLoser);
        Run("info: cancel a handle with 100 suspended try/finally scopes", AllocationChecks.MeasureTreeCancel);

        Console.WriteLine();
        Console.WriteLine($"RESULT: {s_passed} passed, {s_failed} failed, {s_known} known Core issue(s) reproduced");
        return s_failed == 0 ? 0 : 1;
    }

    static void Run(string name, Func<string> check)
    {
        string detail;
        try
        {
            detail = check();
        }
        catch (Exception ex)
        {
            s_failed++;
            Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        s_passed++;
        Console.WriteLine(detail == null ? $"PASS  {name}" : $"PASS  {name} | {detail}");
    }

    static void KnownIssue(string name, Func<string> probe)
    {
        try
        {
            Console.WriteLine($"FIXED {name} | {probe()}");
        }
        catch (SmokeFailure ex)
        {
            s_known++;
            Console.WriteLine($"KNOWN {name} | {ex.Message}");
        }
    }
}

internal sealed class SmokeFailure : Exception
{
    public SmokeFailure(string message) : base(message)
    {
    }
}

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new SmokeFailure(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new SmokeFailure($"{what}: expected {expected}, got {actual}");
    }

    public static void Sequence(IReadOnlyList<string> actual, params string[] expected)
    {
        var ok = actual.Count == expected.Length;
        for (var i = 0; ok && i < expected.Length; i++) ok = actual[i] == expected[i];
        if (!ok) throw new SmokeFailure($"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }
}
