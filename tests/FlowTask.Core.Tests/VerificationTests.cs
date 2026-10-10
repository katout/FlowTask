using System.Threading;
using Katout.FlowTask.Testing;

namespace Katout.FlowTask.Tests;

/// <summary>
/// Checks on .NET that mixed awaitables keep the scope structure, and that every swallowing catch pattern is detected
/// by the analyzer or at runtime.
/// </summary>
public class VerificationTests : FlowTestBase
{
    // ------------------------------------------------------------------ mixed awaitables

    /// <summary>Ticks until the condition holds, letting real time pass for thread-pool completions.</summary>
    void TickUntilReal(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(World.Dump());
            World.Tick(Dt);
            Thread.Sleep(1);
        }
    }

    [Test]
    public void MixedBridgedAwaitablesKeepAutoRegistrationAndCancellation()
    {
        var tokens = new List<CancellationToken>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sig = new Signal<int>();

        async FlowTask<int> Leaf(int i)
        {
            try
            {
                // Task (factory with token), ValueTask, existing Task, then a library wait: all inside one scope.
                var a = await FlowBridge.FromTask(async ct =>
                {
                    tokens.Add(ct);
                    await Task.Delay(1, ct);
                    return 1;
                });
                var b = await FlowBridge.FromTask(ct => new ValueTask<int>(2).AsTask());
                var c = await tcs.Task.AsFlow();
                var d = await sig.Next();
                return a + b + c + d + i;
            }
            finally
            {
                Log.Add("leaf " + i + " finally");
            }
        }

        async FlowTask Root()
        {
            var (x, y) = await FlowTask.WhenAll(Leaf(0), Leaf(10));
            Log.Add("sum " + (x + y));
            await FlowBridge.FromTask(ct =>
            {
                tokens.Add(ct);
                return Task.Delay(Timeout.Infinite, ct);
            });
        }

        var h = World.Run(Root());
        TickUntilReal(() => tokens.Count >= 2 && World.Diagnostics.Walk().Count(s => s.Name.StartsWith("Task")) == 2);
        // Both bridged tasks are registered under their own scope.
        Assert.That(World.Diagnostics.Walk().Where(s => s.Name.StartsWith("Task")).Select(s => s.Path),
            Has.All.StartWith("Root > Leaf > Task"));
        TestThreads.WaitOrFail(Task.Run(() => tcs.SetResult(3)));
        TickUntilReal(() => World.Diagnostics.Walk().Count(s => s.Name.StartsWith("Signal")) == 2);
        sig.Emit(4);
        TickUntilReal(() => Log.Entries.Any(e => e.StartsWith("sum")));
        // Bridged Task completions arrive from the thread pool in any order, so the two leaves may finish in either order.
        Assert.That(Log.Entries, Is.EquivalentTo(new[] { "leaf 0 finally", "leaf 10 finally", "sum 30" }));
        Assert.That(Log.Entries.Last(), Is.EqualTo("sum 30"));
        TickUntilReal(() => tokens.Count == 3);
        h.Cancel();
        Tick();
        Assert.That(tokens.Last().IsCancellationRequested, Is.True, "scope cancellation reached the bridged Task");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Katout.FlowTask.Testing.FlowAssert.NoLiveScopes(World);
    }

    // ------------------------------------------------------------------ swallowing catch patterns
    // Analyzer side: FLOW001 reports untyped catch, catch (Exception), catch (FlowCanceledException) and filters on them
    // (tests/FlowTask.Analyzers.Tests). Runtime side is checked here for every pattern: a canceled scope that returns
    // after the cancellation reached its code is reported once, whatever it awaited meanwhile.

    static async FlowTask Cancellable(Func<FlowTask> body, List<string> log)
    {
        await body();
        log.Add("completed");
    }

    IEnumerable<(string Name, Func<FlowTask> Body, FlowExceptionKind Expected)> Patterns()
    {
#pragma warning disable FLOW001
        yield return ("untyped catch then await", async () =>
        {
            try { await FlowTask.Never(); } catch { }
            await FlowTask.NextFrame();
        }, FlowExceptionKind.SwallowedCancellation);
        yield return ("catch Exception then return", async () =>
        {
            try { await FlowTask.Never(); } catch (Exception) { }
        }, FlowExceptionKind.SwallowedCancellation);
        yield return ("catch FlowCanceledException in a loop", async () =>
        {
            // The loop goes on after the swallow, its awaits run as a live scope's: reported when the scope returns.
            for (var i = 0; i < 3; i++)
            {
                try { await FlowTask.NextFrame(); } catch (FlowCanceledException) { }
            }
        }, FlowExceptionKind.SwallowedCancellation);
        yield return ("filter that catches", async () =>
        {
            try { await FlowTask.Never(); } catch (Exception e) when (e is FlowCanceledException) { }
            await FlowTask.WaitForSeconds(1);
        }, FlowExceptionKind.SwallowedCancellation);
        yield return ("wrap and rethrow as another exception", async () =>
        {
            try { await FlowTask.Never(); } catch (Exception e) { throw new InvalidOperationException("wrapped", e); }
        }, FlowExceptionKind.Cleanup);
        yield return ("swallow in a nested scope", async () =>
        {
            await Inner();

            static async FlowTask Inner()
            {
                try { await FlowTask.Never(); } catch { }
                await FlowTask.NextFrame();
            }
        }, FlowExceptionKind.SwallowedCancellation);
#pragma warning restore FLOW001
    }

    [Test]
    public void EverySwallowingPatternIsDetectedAtRuntime()
    {
        CaptureExceptions();
        foreach (var (name, body, expected) in Patterns())
        {
            Exceptions.Clear();
            var log = new List<string>();
            var h = World.Run(Cancellable(body, log));
            Tick();
            h.Cancel();
            for (var i = 0; i < 120 && !h.IsCompleted; i++) Tick();
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled), name);
            Assert.That(Exceptions.Select(p => p.Kind), Is.EqualTo(new[] { expected }), name);
            Assert.That(log, Does.Not.Contain("completed"), name + ": the scope must not continue normally");
        }
    }

    [Test]
    public void NeitherARethrowNorAnAwaitInFinallyIsASwallow()
    {
        CaptureExceptions();

        async FlowTask Closing()
        {
#pragma warning disable FLOW001
            try
            {
                await FlowTask.Never();
            }
            catch (Exception)
            {
                Log.Add("observed");
                throw;
            }
            finally
            {
                await FlowTask.NextFrame(); // after the cancellation, it runs to its end as a live scope's await does
                Log.Add("finally done");
            }
#pragma warning restore FLOW001
        }

        var h = World.Run(Closing());
        h.Cancel();
        Tick();
        AssertLog("observed");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running), "the flow closes while its finally awaits");
        Tick();
        AssertLog("observed", "finally done");
        Assert.That(Exceptions, Is.Empty);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
    }
}
