namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW002: a non-FlowTask awaitable awaited in a FlowTask method.</summary>
public class Flow002Tests
{
    [Test]
    public Task ForeignAwaitablesAreReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Cysharp.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Task task, Task<int> taskOfInt, ValueTask valueTask, TaskCompletionSource<int> tcs, UniTask uni, int k)
            {
                {|FLOW002:await Task.Delay(1)|};
                {|FLOW002:await task|};
                var v = {|FLOW002:await taskOfInt|};
                {|FLOW002:await valueTask|};
                v = {|FLOW002:await tcs.Task|};
                {|FLOW002:await Task.Yield()|};
                {|FLOW002:await task.ConfigureAwait(false)|};
                {|FLOW002:await uni|};

                // In a FlowTask lambda, in a switch expression, and UniTask.Yield.
                Func<FlowTask> f = async () => { {|FLOW002:await Task.Delay(1)|}; };
                {|FLOW002:await (k switch { 0 => Task.Delay(1), _ => Task.CompletedTask })|};
                {|FLOW002:await UniTask.Yield()|};
                {|FLOW002:await new ValueTask()|};
                await f();
            }

            async FlowTask<int> Stream(IAsyncEnumerable<int> source)
            {
                var sum = 0;
                {|FLOW002:await foreach|} (var x in source) sum += x;
                return sum;
            }

            async FlowTask Cleanup(Task task)
            {
                try { }
                finally
                {
                    {|FLOW002:await task|};
                }
            }
        }
        """ + Stubs.UniTask);

    [Test]
    public Task FlowTaskAwaitablesAndOtherMethodsAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Cysharp.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask<int> M(Task task, Task<int> taskOfInt, ValueTask<int> valueTask, Once<int> once, FlowHandle handle, FlowHandle<int> handleOfInt)
            {
                await FlowTask.WaitForSeconds(1);
                await task.AsFlow();
                var v = await taskOfInt.AsFlow();
                v += await valueTask.AsFlow();
                v += await FlowBridge.FromTask(ct => Task.FromResult(1));
                await FlowBridge.FromTask(ct => Task.Delay(1, ct));
                v += await once;
                await handle.Join();
                v += await handleOfInt.Join();

                // Edge cases: the UniTask bridges and a spawned flow's Join.
                await FlowUniTask.FromUniTask(ct => Work(ct));
                await Work(default).AsFlow();
                var h = Flow.Spawn(FlowTask.WaitForSeconds(1));
                await h.Join();

                // A nested non-FlowTask function is not the FlowTask method.
                Func<Task> external = async () => await Task.Delay(1);
                return v;
            }

            static UniTask Work(CancellationToken ct) => UniTask.CompletedTask;

            async Task NotAFlowTaskMethod(Task task)
            {
                await task;
                await Task.Yield();
            }
        }
        """ + Stubs.UniTask);

    /// <summary>A GetAwaiter extension that returns FlowTask's awaiter registers with the scope.</summary>
    [Test]
    public Task FLOW002_AwaiterFromAGetAwaiterExtensionIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using System.Threading.Tasks;
        using Katout.FlowTask;

        public sealed class Door
        {
            public bool Open { get; set; }
        }

        public static class DoorAwaitExtensions
        {
            public static FlowTask.Awaiter GetAwaiter(this Door door) => FlowTask.WaitUntil(() => door.Open).GetAwaiter();
        }

        class C
        {
            async FlowTask InFlow(Door door)
            {
                await door;
            }

            async Task InTask(Door door)
            {
                {|FLOW005:await door|};
            }
        }
        """);

    /// <summary>
    /// A user type is not exempt, whatever it is marked with: at runtime only FlowTask's awaiters register with the
    /// scope, so the scope ends with a FlowMisuseException when it stops on the user's awaiter. The analyzer goes by the
    /// awaiter type, not by an attribute such as this [FlowAwaitable] of the user.
    /// </summary>
    [Test]
    public Task FLOW002_AMarkedUserTypeIsNotAnExemption() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
        sealed class FlowAwaitableAttribute : Attribute { }

        [FlowAwaitable]
        struct MarkedAwaiter : INotifyCompletion
        {
            public bool IsCompleted => false;
            public void GetResult() { }
            public void OnCompleted(Action continuation) { }
        }

        struct PlainAwaiter : INotifyCompletion
        {
            public bool IsCompleted => false;
            public void GetResult() { }
            public void OnCompleted(Action continuation) { }
        }

        struct AwaitsMarked { public MarkedAwaiter GetAwaiter() => default; }

        [FlowAwaitable]
        struct MarkedAwaitable { public PlainAwaiter GetAwaiter() => default; }

        class C
        {
            async FlowTask InFlow()
            {
                {|FLOW002:await new AwaitsMarked()|};
                {|FLOW002:await new MarkedAwaitable()|};
            }

            async Task InTask()
            {
                await new AwaitsMarked(); // not a FlowTask awaitable, so not FLOW005 either
            }
        }
        """);

    [Test]
    public Task FLOW002_MessageNamesTheBridgeForTheAwaitedType() => AnalyzerHarness.VerifyMessagesAsync("""
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;
        using Cysharp.Threading.Tasks;
        using Katout.FlowTask;

        sealed class Custom
        {
            public Awaiter GetAwaiter() => default;

            public readonly struct Awaiter : INotifyCompletion
            {
                public bool IsCompleted => true;
                public void GetResult() { }
                public void OnCompleted(System.Action continuation) => continuation();
            }
        }

        class C
        {
            async FlowTask M(Task<int> task, ValueTask valueTask, UniTask uni, Custom custom, IAsyncEnumerable<int> source)
            {
                {|FLOW002:await task|};
                {|FLOW002:await valueTask.ConfigureAwait(false)|};
                {|FLOW002:await uni|};
                {|FLOW002:await Task.Yield()|};
                {|FLOW002:await UniTask.Yield()|};
                {|FLOW002:await custom|};
                {|FLOW002:await foreach|} (var x in source) { }
            }
        }
        """ + Stubs.UniTask,
        "'Task<int>' is not a FlowTask awaitable: if it does not complete immediately, the scope ends here with a FlowMisuseException",
        "bridge it with FlowBridge.FromTask(ct => ...) so that cancellation reaches it, or with .AsFlow() for a task that is already running",
        "'UniTask' is not a FlowTask awaitable: if it does not complete immediately, the scope ends here with a FlowMisuseException, because a canceled scope cannot be unwound while it waits on it; bridge it with .AsFlow()",
        "YieldAwaitable' resumes outside the World schedule, so the scope ends here with a FlowMisuseException; wait for the next frame with FlowTask.NextFrame()",
        "YieldAwaitable' resumes outside the World schedule, so the scope ends here with a FlowMisuseException; wait for the next frame with FlowTask.NextFrame()",
        "'Custom' is not a FlowTask awaitable: if it does not complete immediately, the scope ends here with a FlowMisuseException, because a canceled scope cannot be unwound while it waits on it; wrap it in a Task and bridge it with FlowBridge.FromTask(ct => ...)",
        "'await foreach' awaits 'ValueTask<bool>' on every iteration, and the scope ends with a FlowMisuseException when an iteration does not complete immediately");

    [Test]
    public Task AwaitUsingIsJudgedByWhatDisposeAsyncReturns() => AnalyzerHarness.VerifyMessagesAsync("""
        using System;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        sealed class Foreign : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => default;
        }

        sealed class Explicit : IAsyncDisposable
        {
            ValueTask IAsyncDisposable.DisposeAsync() => default;
        }

        sealed class Flowing
        {
            public FlowTask DisposeAsync() => FlowTask.CompletedTask;
        }

        class C
        {
            async FlowTask M(IAsyncDisposable any)
            {
                {|FLOW002:await using|} var a = new Foreign();
                {|FLOW002:await using|} (new Explicit()) { }
                {|FLOW002:await using|} (any) { }
                await using var b = new Flowing();
                await using (var c = new Flowing()) { }
            }

            async Task T()
            {
                {|FLOW005:await using|} var d = new Flowing();
                await using var e = new Foreign();
            }
        }
        """,
        "'await using' awaits the 'ValueTask' that DisposeAsync returns when the block ends, and the scope ends with a FlowMisuseException",
        "'await using' awaits the 'ValueTask' that DisposeAsync returns",
        "'await using' awaits the 'ValueTask' that DisposeAsync returns",
        "'FlowTask' is awaited in an async method that does not return FlowTask");

    // ------------------------------------------------------------------ code fix

    [Test]
    public Task FixBridgesATask() => CodeFixHarness.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Task task)
            {
                await task;
            }
        }
        """,
        """
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Task task)
            {
                await task.AsFlow();
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey);

    [Test]
    public Task FixInsertsTheUsingInCaseInsensitiveOrder() => CodeFixHarness.VerifyAsync(
        """
        using System;
        using Assets.Scripts;
        using UnityEngine;

        class C
        {
            async Katout.FlowTask.FlowTask M(System.Threading.Tasks.Task task)
            {
                await task;
            }
        }

        namespace Assets.Scripts { class Marker { } }
        namespace UnityEngine { class Marker { } }
        """,
        """
        using System;
        using Assets.Scripts;
        using Katout.FlowTask;
        using UnityEngine;

        class C
        {
            async Katout.FlowTask.FlowTask M(System.Threading.Tasks.Task task)
            {
                await task.AsFlow();
            }
        }

        namespace Assets.Scripts { class Marker { } }
        namespace UnityEngine { class Marker { } }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey);

    [Test]
    public Task FixBridgesAValueTaskWithAResultAndAddsTheUsing() => CodeFixHarness.VerifyAsync(
        """
        using System.Threading.Tasks;

        class C
        {
            ValueTask<int> Load() => new ValueTask<int>(1);

            async Katout.FlowTask.FlowTask<int> M(bool b)
            {
                return await (b ? Load() : default) + 1;
            }
        }
        """,
        """
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            ValueTask<int> Load() => new ValueTask<int>(1);

            async Katout.FlowTask.FlowTask<int> M(bool b)
            {
                return await (b ? Load() : default).AsFlow() + 1;
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey);

    [Test]
    public Task FixInsertsTheUsingBeforeAliasesAndKeepsTheHeader() => CodeFixHarness.VerifyAsync(
        """
        // Copyright header
        using Tasks = System.Threading.Tasks;

        class C
        {
            async Katout.FlowTask.FlowTask M()
            {
                await Tasks.Task.Delay(1);
            }
        }
        """,
        """
        // Copyright header
        using Katout.FlowTask;
        using Tasks = System.Threading.Tasks;

        class C
        {
            async Katout.FlowTask.FlowTask M()
            {
                await Tasks.Task.Delay(1).AsFlow();
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey);

    [Test]
    public Task FixReplacesConfigureAwait() => CodeFixHarness.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask<int> M(Task<int> task)
            {
                return await task.ConfigureAwait(false);
            }
        }
        """,
        """
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask<int> M(Task<int> task)
            {
                return await task.AsFlow();
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey);

    [Test]
    public async Task NoFixForAwaitablesWithoutABridge()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using System.Threading.Tasks;
            using Katout.FlowTask;

            class C
            {
                async FlowTask M()
                {
                    await Task.Yield();
                }
            }
            """, new BridgeAwaitCodeFixProvider(), DiagnosticIds.ForeignAwaitInFlowTask);
        Assert.That(actions, Is.Empty);
    }

    [Test]
    public Task FLOW002_FixBridgesACallWithFromTaskAndPassesTheToken() => CodeFixHarness.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M()
            {
                await Task.Delay(1).ConfigureAwait(false); // wait
            }
        }
        """,
        """
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M()
            {
                await FlowBridge.FromTask(ct => Task.Delay(1, ct)); // wait
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.FromTaskEquivalenceKey);

    [Test]
    public Task FLOW002_FixPassesTheTokenToAnOptionalParameterAndAddsTheUsing() => CodeFixHarness.VerifyAsync(
        """
        using System.Threading;
        using System.Threading.Tasks;

        class C
        {
            Task<int> LoadAsync(string path, int retries = 0, CancellationToken cancellationToken = default) => Task.FromResult(1);

            async Katout.FlowTask.FlowTask<int> M(int ct)
            {
                return await LoadAsync("save.dat") + ct;
            }
        }
        """,
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            Task<int> LoadAsync(string path, int retries = 0, CancellationToken cancellationToken = default) => Task.FromResult(1);

            async Katout.FlowTask.FlowTask<int> M(int ct)
            {
                return await FlowBridge.FromTask(ct1 => LoadAsync("save.dat", cancellationToken: ct1)) + ct;
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.FromTaskEquivalenceKey);

    [Test]
    public Task FLOW002_FixReplacesCancellationTokenNone() => CodeFixHarness.VerifyAsync(
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            Task SaveAsync(string path, CancellationToken token) => Task.CompletedTask;

            async FlowTask M()
            {
                await SaveAsync("a", CancellationToken.None);
                await SaveAsync("b", default);
            }
        }
        """,
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            Task SaveAsync(string path, CancellationToken token) => Task.CompletedTask;

            async FlowTask M()
            {
                await FlowBridge.FromTask(ct => SaveAsync("a", ct));
                await SaveAsync("b", default);
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.FromTaskEquivalenceKey);

    [Test]
    public Task FLOW002_FixConvertsAValueTaskWithAsTask() => CodeFixHarness.VerifyAsync(
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            ValueTask<int> ReadAsync(CancellationToken cancellationToken = default) => new ValueTask<int>(1);

            async FlowTask<int> M()
            {
                return await ReadAsync();
            }
        }
        """,
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            ValueTask<int> ReadAsync(CancellationToken cancellationToken = default) => new ValueTask<int>(1);

            async FlowTask<int> M()
            {
                return await FlowBridge.FromTask(ct => ReadAsync(cancellationToken: ct).AsTask());
            }
        }
        """,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.FromTaskEquivalenceKey);

    [Test]
    public async Task FLOW002_FixOffersOnlyAsFlowForARunningTask()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using System.Threading.Tasks;
            using Katout.FlowTask;

            class C
            {
                async FlowTask M(Task running)
                {
                    await running;
                }
            }
            """, new BridgeAwaitCodeFixProvider(), DiagnosticIds.ForeignAwaitInFlowTask);
        Assert.That(actions.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey }));
        Assert.That(actions[0].Title, Does.Contain("cancellation does not reach it"));
    }

    [Test]
    public async Task FLOW002_FixOffersFromTaskFirstForACall()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using System.Threading.Tasks;
            using Katout.FlowTask;

            class C
            {
                async FlowTask M()
                {
                    await Task.Delay(1);
                }
            }
            """, new BridgeAwaitCodeFixProvider(), DiagnosticIds.ForeignAwaitInFlowTask);
        Assert.That(actions.Select(a => a.EquivalenceKey), Is.EqualTo(new[]
        {
            BridgeAwaitCodeFixProvider.FromTaskEquivalenceKey,
            BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey,
        }));
        Assert.That(actions[0].Title, Does.Contain("the scope's cancellation reaches the task"));
    }

    /// <summary>A lambda in a struct cannot capture 'this' (CS1673): only the AsFlow fix compiles there.</summary>
    [Test]
    public async Task FLOW002_FixOffersOnlyAsFlowWhenTheLambdaCannotCaptureTheCall()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using System.Threading;
            using System.Threading.Tasks;
            using Katout.FlowTask;

            struct S
            {
                int _slot;

                Task SaveAsync(int slot, CancellationToken cancellationToken = default) => Task.CompletedTask;

                async FlowTask M()
                {
                    await SaveAsync(_slot);
                }
            }
            """, new BridgeAwaitCodeFixProvider(), DiagnosticIds.ForeignAwaitInFlowTask);
        Assert.That(actions.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey }));
    }

    /// <summary>
    /// Moved into the lambda, 'out var y' is out of scope after the await (CS0103) and 'out y' leaves y unassigned
    /// (CS0165): errors outside the replaced call, so the whole member is compared.
    /// </summary>
    [Test]
    public async Task FLOW002_FixOffersOnlyAsFlowWhenAnArgumentWritesAnOuterLocal()
    {
        const string Prefix = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Katout.FlowTask;

            class C
            {
                static Task Load(int x, CancellationToken cancellationToken = default) => Task.CompletedTask;
                static int Parse(string s, out int y) { y = 1; return 1; }

            """;
        foreach (var body in new[]
        {
            """
                async FlowTask M()
                {
                    await Load(Parse("1", out var y));
                    Console.WriteLine(y);
                }
            }
            """,
            """
                async FlowTask M()
                {
                    int y;
                    await Load(Parse("1", out y));
                    Console.WriteLine(y);
                }
            }
            """,
        })
        {
            var actions = await CodeFixHarness.GetActionsAsync(Prefix + body, new BridgeAwaitCodeFixProvider(), DiagnosticIds.ForeignAwaitInFlowTask);
            Assert.That(actions.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey }), body);
        }

        // Declared and used only in the call: still bridged with FromTask.
        await CodeFixHarness.VerifyAsync(
            Prefix + """
                async FlowTask M()
                {
                    await Load(Parse("1", out var y) + y);
                }
            }
            """,
            Prefix + """
                async FlowTask M()
                {
                    await FlowBridge.FromTask(ct => Load(Parse("1", out var y) + y, cancellationToken: ct));
                }
            }
            """,
            new BridgeAwaitCodeFixProvider(),
            DiagnosticIds.ForeignAwaitInFlowTask,
            BridgeAwaitCodeFixProvider.FromTaskEquivalenceKey);
    }

    [Test]
    public async Task FLOW002_FixOffersOnlyAsFlowWhenAnotherTokenIsPassed()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using System.Threading;
            using System.Threading.Tasks;
            using Katout.FlowTask;

            class C
            {
                readonly CancellationTokenSource _destroyed = new CancellationTokenSource();

                async FlowTask M()
                {
                    await Task.Delay(1, _destroyed.Token);
                }
            }
            """, new BridgeAwaitCodeFixProvider(), DiagnosticIds.ForeignAwaitInFlowTask);
        Assert.That(actions.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey }));
    }

    [Test]
    public Task FLOW002_FixBridgesAUniTaskWithItsAsFlow() => CodeFixHarness.VerifyAsync(
        """
        using Cysharp.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(UniTask running)
            {
                await running;
            }
        }
        """ + Stubs.UniTask,
        """
        using Cysharp.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(UniTask running)
            {
                await running.AsFlow();
            }
        }
        """ + Stubs.UniTask,
        new BridgeAwaitCodeFixProvider(),
        DiagnosticIds.ForeignAwaitInFlowTask,
        BridgeAwaitCodeFixProvider.AsFlowEquivalenceKey);
}
