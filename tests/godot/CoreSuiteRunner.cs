using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Builders;

/// <summary>
/// Runs the NUnit tests compiled into this assembly (the linked core test suite) inside the Godot process.
/// NUnitLite's TextRunner cannot be used: Godot loads the game assembly from memory, so Assembly.Location is empty and
/// NUnit's DefaultTestAssemblyBuilder rejects it. This builder names the assembly itself.
/// </summary>
public static class CoreSuiteRunner
{
    public sealed class Report
    {
        public int Total;
        public int Passed;
        public int Failed;
        public int Skipped;
        public int Inconclusive;
        public int Warnings;
        public bool Ok;
        public TimeSpan Duration;
        public readonly List<string> Failures = new();
        public readonly List<string> Fixtures = new();
    }

    /// <summary>
    /// How long one test may run before the run is given up as hung. The slowest core test takes a few seconds. Shorter
    /// than the 2 minutes of `dotnet test` (tests/test.runsettings), so that a hang is reported by name well within the
    /// 300 seconds after which tools/godot/run-smoke.ps1 kills Godot without saying where it stopped.
    /// </summary>
    static readonly TimeSpan PerTestLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs every test and waits for the run. NUnit runs the tests one after another on a thread it starts itself (a
    /// foreground thread with the default stack size), started here from a helper thread that waits for the run while
    /// this method watches it. Neither is the Godot main thread, so no test runs under Godot's SynchronizationContext
    /// (a test that blocks on a Task continuation cannot deadlock the main loop). A test that runs longer than
    /// <see cref="PerTestLimit"/> is reported as hung by name, together with the results of the tests that finished
    /// before it; the tests after it are not reported. Only tests are timed (SetUp and TearDown are part of a test):
    /// work at the fixture level (OneTimeSetUp, OneTimeTearDown, a fixture constructor, SetUpFixture) is not, and a hang
    /// there runs into the 300-second kill without a name. The core tests do no such work. Nothing can stop the hung
    /// test (no Thread.Abort on .NET): it, and the tests after it if it ever returns, keep running until the smoke test
    /// quits Godot, which ends the process. In a console host NUnit's foreground thread would keep the process alive
    /// after this returns, and Console.Out would stay redirected to NUnit's capture.
    /// </summary>
    public static Report Run(Assembly assembly)
    {
        Report report = null;
        Exception error = null;
        var started = Stopwatch.GetTimestamp();
        var progress = new Progress();
        var thread = new Thread(() =>
        {
            try
            {
                report = RunCore(assembly, progress);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }) { Name = "FlowTask core test run", IsBackground = true };
        thread.Start();
        while (!thread.Join(TimeSpan.FromSeconds(1)))
        {
            var (name, elapsed) = progress.Current();
            if (name == null || elapsed <= PerTestLimit) continue;
            var hung = progress.Finished();
            hung.Ok = false;
            hung.Duration = Stopwatch.GetElapsedTime(started);
            hung.Total++;
            hung.Failed++;
            hung.Failures.Add($"{name}: still running after {elapsed.TotalSeconds:0.0} s, more than the limit of {PerTestLimit.TotalSeconds:0} s; the run was abandoned and the tests after it are not reported");
            return hung;
        }

        if (error != null) throw new InvalidOperationException("The NUnit run failed.", error);
        return report;
    }

    static Report RunCore(Assembly assembly, ITestListener listener)
    {
        var started = Stopwatch.GetTimestamp();
        var runner = new NUnitTestAssemblyRunner(new InMemoryAssemblyBuilder());
        // NumberOfTestWorkers = 0: no parallel workers. NUnit still runs the tests one after another on a thread of its
        // own (not this one), and calls the listener from its event pump thread.
        var settings = new Dictionary<string, object> { ["NumberOfTestWorkers"] = 0 };
        runner.Load(assembly, settings);
        var result = runner.Run(listener, TestFilter.Empty);

        var report = new Report
        {
            Passed = result.PassCount,
            Failed = result.FailCount,
            Skipped = result.SkipCount,
            Inconclusive = result.InconclusiveCount,
            Warnings = result.WarningCount,
            Duration = Stopwatch.GetElapsedTime(started),
        };
        report.Total = report.Passed + report.Failed + report.Skipped + report.Inconclusive + report.Warnings;
        Collect(result, report);
        report.Ok = report.Failed == 0 && report.Total > 0 && result.ResultState.Status != TestStatus.Failed;
        if (result.ResultState.Status == TestStatus.Failed && report.Failed == 0)
            report.Failures.Add($"{result.FullName}: {result.ResultState} {result.Message}");
        return report;
    }

    static void Collect(ITestResult r, Report report)
    {
        if (IsFixture(r)) report.Fixtures.Add(FixtureLine(r));
        if (!r.HasChildren)
        {
            if (r.ResultState.Status == TestStatus.Failed) report.Failures.Add(FailureLine(r));
            return;
        }

        foreach (var c in r.Children) Collect(c, report);
    }

    static bool IsFixture(ITestResult r) => r.Test.IsSuite && r.Test.TypeInfo != null && r.Test.TestCaseCount > 0;

    static string FixtureLine(ITestResult r) => $"{r.Test.TypeInfo.Name}: {r.PassCount}/{r.Test.TestCaseCount}";

    static string FailureLine(ITestResult r)
    {
        var stack = r.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains(".cs:line"))?.Trim();
        return $"{r.FullName}: {r.Message?.Trim()} {stack}";
    }

    /// <summary>
    /// How far the run has got: the test that is running now and since when, and the results of the tests that
    /// finished. NUnit calls it from its event pump thread (asynchronously, in order), so it sees the run a little late
    /// (the pump's lag, far below the limit); Run reads it.
    /// </summary>
    sealed class Progress : ITestListener
    {
        readonly object _lock = new();
        readonly Report _finished = new();
        string _name;
        long _started; // a Stopwatch timestamp: unlike DateTime.UtcNow, a change of the system clock does not move it

        public (string Name, TimeSpan Elapsed) Current()
        {
            lock (_lock) return (_name, _name == null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_started));
        }

        /// <summary>A copy of the results so far (the pump thread keeps adding if the hung test ever returns).</summary>
        public Report Finished()
        {
            lock (_lock)
            {
                var r = new Report
                {
                    Total = _finished.Total,
                    Passed = _finished.Passed,
                    Failed = _finished.Failed,
                    Skipped = _finished.Skipped,
                    Inconclusive = _finished.Inconclusive,
                    Warnings = _finished.Warnings,
                };
                r.Failures.AddRange(_finished.Failures);
                r.Fixtures.AddRange(_finished.Fixtures);
                return r;
            }
        }

        public void TestStarted(ITest test)
        {
            if (test.IsSuite) return;
            lock (_lock)
            {
                _name = test.FullName;
                _started = Stopwatch.GetTimestamp();
            }
        }

        public void TestFinished(ITestResult result)
        {
            lock (_lock)
            {
                if (result.Test.IsSuite)
                {
                    if (IsFixture(result)) _finished.Fixtures.Add(FixtureLine(result));
                    return;
                }

                _name = null;
                _finished.Total++;
                switch (result.ResultState.Status)
                {
                    case TestStatus.Passed:
                        _finished.Passed++;
                        break;
                    case TestStatus.Failed:
                        _finished.Failed++;
                        _finished.Failures.Add(FailureLine(result));
                        break;
                    case TestStatus.Skipped:
                        _finished.Skipped++;
                        break;
                    case TestStatus.Inconclusive:
                        _finished.Inconclusive++;
                        break;
                    case TestStatus.Warning:
                        _finished.Warnings++;
                        break;
                }
            }
        }

        public void TestOutput(TestOutput output)
        {
        }

        public void SendMessage(TestMessage message)
        {
        }
    }

    sealed class InMemoryAssemblyBuilder : ITestAssemblyBuilder
    {
        public ITest Build(Assembly assembly, IDictionary<string, object> options)
        {
            var suite = new TestAssembly(assembly, assembly.GetName().Name + ".dll");
            var builder = new DefaultSuiteBuilder();
            foreach (var type in assembly.GetTypes())
            {
                var info = new TypeWrapper(type);
                if (builder.CanBuildFrom(info)) suite.Add(builder.BuildFrom(info));
            }

            // As NUnit's DefaultTestAssemblyBuilder does: assembly-level attributes, then alphabetical order. The order
            // matters: the retry-and-purchase fixture in ScenarioTests shares fixture state between its tests and only
            // passes in NUnit's default (sorted) order.
            suite.ApplyAttributesToTest(assembly);
            suite.Sort();
            return suite;
        }

        public ITest Build(string assemblyNameOrPath, IDictionary<string, object> options) =>
            throw new NotSupportedException("Only in-memory assemblies are supported.");
    }
}
