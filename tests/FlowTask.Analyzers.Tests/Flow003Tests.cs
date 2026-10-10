namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW003: a FlowTask that is created and never awaited or started.</summary>
public class Flow003Tests
{
    [Test]
    public Task DroppedFlowTasksAreReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;
            FlowTask<int> Compute() => FlowTask.FromResult(1);

            async FlowTask M(C other)
            {
                {|FLOW003:DoThing()|};
                {|FLOW003:Compute()|};
                {|FLOW003:FlowTask.WaitForSeconds(1)|};
                {|FLOW003:Flow.Named("x", DoThing())|};
                {|FLOW003:other?.DoThing()|};
                {|FLOW003:_ = DoThing()|};
                await FlowTask.NextFrame();
            }

            void Plain()
            {
                {|FLOW003:DoThing()|};
                Action a = () => {|FLOW003:DoThing()|};
                void Local() => {|FLOW003:DoThing()|};
                Func<FlowTask> f = async () => {|FLOW003:DoThing()|};
            }

            async FlowTask ExpressionBodied() => {|FLOW003:DoThing()|};
        }
        """);

    [Test]
    public Task DroppedLazyBridgesAreReported() => AnalyzerHarness.VerifyAsync("""
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Task<int> running)
            {
                {|FLOW003:FlowBridge.FromTask(ct => Task.FromResult(1))|};
                {|FLOW003:FlowBridge.FromTask(ct => Task.CompletedTask)|};
                {|FLOW003:_ = running.AsFlow()|};
                var {|FLOW003:unused|} = FlowBridge.FromTask(ct => Task.FromResult(2));
                await FlowBridge.FromTask(ct => Task.FromResult(3));
                var used = FlowBridge.FromTask(ct => Task.FromResult(4));
                await used;
            }
        }
        """);

    /// <summary>
    /// A bridge creates nothing until it is awaited, so skipping it on a path leaks nothing, and it has no Discard()
    /// for the message to recommend: only FlowTask locals are reported for some paths.
    /// </summary>
    [Test]
    public Task FLOW003_BridgeStartedOnSomePathsIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(bool c, Task<int> running)
            {
                var b = FlowBridge.FromTask(ct => Task.Delay(1, ct));
                if (c) await b;
                var r = running.AsFlow();
                if (c) await r;
                b = FlowBridge.FromTask(ct => Task.Delay(2, ct));
                if (c) await b;
            }
        }
        """);

    [Test]
    public Task FlowTaskLocalsThatAreNeverReadAreReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;

            async FlowTask M()
            {
                var {|FLOW003:t|} = DoThing();
                FlowTask<int> {|FLOW003:u|} = FlowTask.FromResult(1), used = FlowTask.FromResult(2);
                FlowTask {|FLOW003:onlyWritten|};
                onlyWritten = DoThing();
                FlowTask {|FLOW003:deconstructed|};
                int n;
                (deconstructed, n) = (DoThing(), 1);
                await used;
            }
        }
        """);

    [Test]
    public Task StartedOrUsedFlowTasksAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;
            FlowTask<int> Compute() => FlowTask.FromResult(1);
            void Use(FlowTask t) { }

            async FlowTask M(FlowWorld world)
            {
                await DoThing();
                var v = await Compute();
                _ = Flow.Spawn(DoThing());
                var h = Flow.Spawn(Compute());
                h.Cancel();
                world.Run(DoThing());
                await FlowTask.WhenAll(DoThing(), DoThing());

                var a = DoThing();
                await a;
                var b = DoThing();
                Use(b);
                var c = DoThing();
                Func<FlowTask> later = () => c;
                var list = new List<FlowTask>();
                var d = DoThing();
                list.Add(d);
                await FlowTask.WhenAll(list);
                FlowTask e;
                e = DoThing();
                await e;
            }

            FlowTask Returned() => DoThing();
            Func<FlowTask> Factory() => () => DoThing();
        }
        """);

    // ------------------------------------------------------------------ code fixes

    const string Before = """
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;

            async FlowTask M()
            {
                // start
                DoThing(); // trailing
            }
        }
        """;

    [Test]
    public Task FixAwaitsTheTask() => CodeFixHarness.VerifyAsync(
        Before,
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;

            async FlowTask M()
            {
                // start
                await DoThing(); // trailing
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.AwaitEquivalenceKey);

    [Test]
    public Task FixAwaitParenthesizesAConditional() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask A() => FlowTask.CompletedTask;
            FlowTask B() => FlowTask.CompletedTask;

            async FlowTask M(bool b)
            {
                _ = b ? A() : B();
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask A() => FlowTask.CompletedTask;
            FlowTask B() => FlowTask.CompletedTask;

            async FlowTask M(bool b)
            {
                await (b ? A() : B());
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.AwaitEquivalenceKey);

    [Test]
    public Task FixSpawnsTheTask() => CodeFixHarness.VerifyAsync(
        Before,
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;

            async FlowTask M()
            {
                // start
                _ = Flow.Spawn(DoThing()); // trailing
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.SpawnEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.FlowTaskDropped, DiagnosticIds.SpawnHandleDropped });

    [Test]
    public Task FixAwaitsADiscardedTask() => CodeFixHarness.VerifyAsync(
        """
        class C
        {
            Katout.FlowTask.FlowTask<int> Compute() => Katout.FlowTask.FlowTask.FromResult(1);

            async Katout.FlowTask.FlowTask M()
            {
                _ = Compute();
            }
        }
        """,
        """
        class C
        {
            Katout.FlowTask.FlowTask<int> Compute() => Katout.FlowTask.FlowTask.FromResult(1);

            async Katout.FlowTask.FlowTask M()
            {
                await Compute();
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.AwaitEquivalenceKey);

    [Test]
    public Task FixSpawnsADiscardedTaskAndAddsTheUsing() => CodeFixHarness.VerifyAsync(
        """
        // header
        namespace Game
        {
            class C
            {
                Katout.FlowTask.FlowTask<int> Compute() => Katout.FlowTask.FlowTask.FromResult(1);

                async Katout.FlowTask.FlowTask M()
                {
                    _ = Compute();
                }
            }
        }
        """,
        """
        // header
        using Katout.FlowTask;

        namespace Game
        {
            class C
            {
                Katout.FlowTask.FlowTask<int> Compute() => Katout.FlowTask.FlowTask.FromResult(1);

                async Katout.FlowTask.FlowTask M()
                {
                    _ = Flow.Spawn(Compute());
                }
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.SpawnEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.FlowTaskDropped, DiagnosticIds.SpawnHandleDropped });

    [Test]
    public async Task BothFixesAreOfferedInAFlowTaskMethodAndNoneElsewhere()
    {
        var provider = new StartFlowTaskCodeFixProvider();
        var inFlow = await CodeFixHarness.GetActionsAsync(Before, provider, DiagnosticIds.FlowTaskDropped);
        Assert.That(inFlow.Select(a => a.EquivalenceKey), Is.EquivalentTo(new[]
        {
            StartFlowTaskCodeFixProvider.AwaitEquivalenceKey,
            StartFlowTaskCodeFixProvider.SpawnEquivalenceKey,
            StartFlowTaskCodeFixProvider.RunEquivalenceKey,
        }));

        var outside = await CodeFixHarness.GetActionsAsync("""
            using Katout.FlowTask;

            class C
            {
                FlowTask DoThing() => FlowTask.CompletedTask;

                void Update()
                {
                    DoThing();
                }
            }
            """, provider, DiagnosticIds.FlowTaskDropped);
        Assert.That(outside, Is.Empty);
    }

    /// <summary>
    /// The Spawn fix writes '_ = Flow.Spawn(...)' (a Spawn statement is FLOW008), so a variable named '_' in scope
    /// leaves it out.
    /// </summary>
    [Test]
    public async Task FLOW003_SpawnFixIsNotOfferedWhereUnderscoreIsAVariable()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using Katout.FlowTask;

            class C
            {
                FlowTask DoThing() => FlowTask.CompletedTask;

                async FlowTask M(int _)
                {
                    DoThing();
                }
            }
            """, new StartFlowTaskCodeFixProvider(), DiagnosticIds.FlowTaskDropped);
        Assert.That(actions.Select(a => a.EquivalenceKey), Is.EquivalentTo(new[]
        {
            StartFlowTaskCodeFixProvider.AwaitEquivalenceKey,
            StartFlowTaskCodeFixProvider.RunEquivalenceKey,
        }));
    }

    /// <summary>
    /// In a finally block and in a catch of the cancellation the fixes are the ones offered elsewhere: the awaits of a
    /// canceled scope's cleanup run to their end, and Flow.Spawn starts there. A task with a result gets the same three
    /// fixes as one without.
    /// </summary>
    [Test]
    public async Task FLOW003_FixesInCleanupAreTheUsualOnesAlsoForATaskWithAResult()
    {
        var provider = new StartFlowTaskCodeFixProvider();
        const string Source = """
            using System;
            using Katout.FlowTask;

            class C
            {
                static async FlowTask Send() { await FlowTask.NextFrame(); }
                static async FlowTask<int> Fetch() { await FlowTask.NextFrame(); return 1; }

                async FlowTask M()
                {
                    try { await FlowTask.NextFrame(); }
                    finally { Send(); }

                    try { await FlowTask.NextFrame(); }
                    catch (FlowCanceledException) { Send(); throw; }

                    Fetch();

                    try { await FlowTask.NextFrame(); }
                    finally { Fetch(); }
                }
            }
            """;

        var all = new[]
        {
            StartFlowTaskCodeFixProvider.AwaitEquivalenceKey,
            StartFlowTaskCodeFixProvider.SpawnEquivalenceKey,
            StartFlowTaskCodeFixProvider.RunEquivalenceKey,
        };
        var inFinally = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.FlowTaskDropped, diagnosticIndex: 0);
        Assert.That(inFinally.Select(a => a.EquivalenceKey), Is.EqualTo(all));

        var inCancellationCatch = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.FlowTaskDropped, diagnosticIndex: 1);
        Assert.That(inCancellationCatch.Select(a => a.EquivalenceKey), Is.EqualTo(all));

        var result = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.FlowTaskDropped, diagnosticIndex: 2);
        Assert.That(result.Select(a => a.EquivalenceKey), Is.EqualTo(all));

        var resultInFinally = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.FlowTaskDropped, diagnosticIndex: 3);
        Assert.That(resultInFinally.Select(a => a.EquivalenceKey), Is.EqualTo(all));
    }

    /// <summary>
    /// The Await fix in a finally block: no other rule reports the result but FLOW010, which then asks whether the await
    /// must finish when a cancel comes (Flow.NonCancelable).
    /// </summary>
    [Test]
    public Task FLOW003_AwaitFixInAFinallyBlock() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            static async FlowTask FadeOut() { await FlowTask.NextFrame(); }

            async FlowTask Screen()
            {
                try { await FlowTask.WaitForSeconds(1); }
                finally
                {
                    FadeOut();
                }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            static async FlowTask FadeOut() { await FlowTask.NextFrame(); }

            async FlowTask Screen()
            {
                try { await FlowTask.WaitForSeconds(1); }
                finally
                {
                    await FadeOut();
                }
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.AwaitEquivalenceKey,
        absentAfter: AnalyzerHarness.Analyzers.SelectMany(a => a.SupportedDiagnostics).Select(d => d.Id).Distinct()
            .Where(id => id != DiagnosticIds.CleanupAwaitNotProtected).ToArray());

    // ------------------------------------------------------------------ wording, Discard(), FlowWorld.Current.Run

    [Test]
    public Task FLOW003_MessageRecommendsWorldRunForWorkThatOutlivesTheFlow() => AnalyzerHarness.VerifyMessagesAsync("""
        using Katout.FlowTask;

        class C
        {
            FlowTask Send() => FlowTask.CompletedTask;

            async FlowTask M()
            {
                {|FLOW003:Send()|};
                {|FLOW003:_ = Send()|};
                {|FLOW003:Send().Discard()|};
                await FlowTask.NextFrame();
            }
        }
        """,
        "This FlowTask is never awaited or started, so it never runs; await it, or start it with world.Run if it must outlive this flow (Flow.Spawn stops it when the current scope ends)",
        "Discarding a FlowTask with '_ =' does not run it; await it, or start it with world.Run if it must outlive this flow",
        "'Discard()' releases this FlowTask without ever running it (it is not UniTask's Forget); await it, or start it with world.Run if it must outlive this flow (Flow.Spawn stops it when the current scope ends)");

    [Test]
    public Task FLOW003_DiscardOnACallIsReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }
            static async FlowTask<int> Compute() { await FlowTask.NextFrame(); return 1; }

            async FlowTask M()
            {
                {|FLOW003:A().Discard()|};
                {|FLOW003:FlowTask.WaitForSeconds(1).Discard()|};
                {|FLOW003:Flow.Named("x", A()).Discard()|};
                {|FLOW003:Compute().Discard()|};
                {|FLOW003:(A()).Discard()|};
                await FlowTask.NextFrame();
            }

            void Plain() => {|FLOW003:A().Discard()|};
        }
        """);

    [Test]
    public Task FLOW003_DiscardOnAStoredTaskIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }

            FlowTask _pending = A();

            async FlowTask M(bool skip, FlowTask parameter)
            {
                var t = A();
                if (skip) t.Discard();
                else await t;

                parameter.Discard();
                _pending.Discard();
            }
        }
        """);

    [Test]
    public Task FLOW003_FixRunsItIndependentlyWithWorldRun() => CodeFixHarness.VerifyAsync(
        """
        class C
        {
            Katout.FlowTask.FlowTask Send() => Katout.FlowTask.FlowTask.CompletedTask;

            async Katout.FlowTask.FlowTask M()
            {
                Send(); // analytics
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            Katout.FlowTask.FlowTask Send() => Katout.FlowTask.FlowTask.CompletedTask;

            async Katout.FlowTask.FlowTask M()
            {
                FlowWorld.Current.Run(Send()); // analytics
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.RunEquivalenceKey);

    [Test]
    public Task FLOW003_FixStartsADiscardedCall() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask Send() => FlowTask.CompletedTask;

            async FlowTask M()
            {
                Send().Discard(); // fire and forget
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask Send() => FlowTask.CompletedTask;

            async FlowTask M()
            {
                FlowWorld.Current.Run(Send()); // fire and forget
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.RunEquivalenceKey);

    [Test]
    public Task FLOW003_FixAwaitsADiscardedCall() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask<int> Load() => FlowTask.FromResult(1);

            async FlowTask M()
            {
                Load().Discard();
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            FlowTask<int> Load() => FlowTask.FromResult(1);

            async FlowTask M()
            {
                await Load();
            }
        }
        """,
        new StartFlowTaskCodeFixProvider(),
        DiagnosticIds.FlowTaskDropped,
        StartFlowTaskCodeFixProvider.AwaitEquivalenceKey);

    // ------------------------------------------------------------------ collections

    [Test]
    public Task FLOW003_CollectionOfTasksNeverStartedIsReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using System.Collections.Generic;
        using Katout.FlowTask;

        class C
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }
            static async FlowTask B() { await FlowTask.NextFrame(); }

            async FlowTask M()
            {
                var {|FLOW003:list|} = new List<FlowTask> { A(), B() };
                FlowTask[] {|FLOW003:array|} = { A(), B() };
                var {|FLOW003:added|} = new List<FlowTask>();
                added.Add(A());
                added.Insert(0, B());
                var {|FLOW003:slots|} = new FlowTask[2];
                slots[0] = A();
                await FlowTask.NextFrame();
            }
        }
        """,
        "The FlowTasks stored in 'list' are never awaited or started, so they never run; pass the collection to FlowTask.WhenAll or Race, or start each task",
        "The FlowTasks stored in 'array'",
        "The FlowTasks stored in 'added'",
        "The FlowTasks stored in 'slots'");

    [Test]
    public Task FLOW003_CollectionPassedOnIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using System.Collections.Generic;
        using Katout.FlowTask;

        class C
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }

            static void Use(IEnumerable<FlowTask> tasks) { }

            async FlowTask M()
            {
                // Stored in a collection, then read.
                var t = A();
                var list = new List<FlowTask> { t, FlowTask.NextFrame() };
                await FlowTask.WhenAll(list);

                var passed = new List<FlowTask> { A() };
                Use(passed);
                var raced = new[] { A(), A() };
                await FlowTask.Race(raced);
                var looped = new List<FlowTask> { A() };
                foreach (var task in looped) await task;
                List<FlowTask> none = null;
            }

            List<FlowTask> Build()
            {
                var built = new List<FlowTask> { A() };
                return built;
            }
        }
        """);

    // ------------------------------------------------------------------ started on some paths only

    [Test]
    public Task FLOW003_TaskStartedOnSomePathsIsReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }
            static async FlowTask<int> B() { await FlowTask.NextFrame(); return 1; }

            async FlowTask If(bool c)
            {
                var {|FLOW003:t|} = A();
                if (c) await t;
            }

            async FlowTask Loop(bool c)
            {
                var {|FLOW003:t|} = A();
                while (c)
                {
                    await t;
                    break;
                }
            }

            async FlowTask<int> Conditional(bool c)
            {
                var {|FLOW003:t|} = B();
                return c ? await t : 0;
            }

            async FlowTask AndAlso(bool c)
            {
                var {|FLOW003:t|} = B();
                if (c && await t == 1) { }
            }

            async FlowTask SwitchWithoutDefault(int k)
            {
                var {|FLOW003:t|} = A();
                switch (k)
                {
                    case 0: await t; break;
                    case 1: await t; break;
                }
            }

            // A guard, or a pattern that does not match every value, is not a default.
            async FlowTask SwitchWithAGuardedVarCase(int k, bool c)
            {
                var {|FLOW003:t|} = A();
                switch (k) { case 1: await t; break; case var _ when c: await t; break; }
            }

            async FlowTask SwitchWithATypeCase(object o)
            {
                var {|FLOW003:t|} = A();
                switch (o) { case string _: await t; break; case object _: await t; break; }
            }

            async FlowTask OnlyInACatch()
            {
                var {|FLOW003:t|} = A();
                try { await FlowTask.NextFrame(); }
                catch (FormatException) { await t; }
            }

            async FlowTask StoredAgainAndNotStarted(bool c)
            {
                var t = A();
                await t;
                if (c)
                {
                    {|FLOW003:t|} = A();
                }
            }

            // The loop may not run, and the last task it stores is not read when the condition turns false.
            async FlowTask PrefetchWhile(bool c)
            {
                var {|FLOW003:next|} = A();
                while (c)
                {
                    await next;
                    {|FLOW003:next|} = A();
                }
            }
        }
        """,
        "The FlowTask stored in 't' is started only on some paths and never runs on the others; start it on every path, or call 't.Discard()' where it is skipped on purpose",
        "started only on some paths",
        "started only on some paths",
        "started only on some paths",
        "started only on some paths",
        "started only on some paths",
        "started only on some paths",
        "started only on some paths",
        "started only on some paths",
        "The FlowTask stored in 'next' is started only on some paths",
        "The FlowTask stored in 'next' is started only on some paths");

    [Test]
    public Task FLOW003_TaskStartedOnEveryPathIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }
            static async FlowTask B() { await FlowTask.NextFrame(); }

            async FlowTask IfElse(bool c)
            {
                var t = A();
                if (c) await t;
                else await FlowTask.WhenAll(t, B());
            }

            async FlowTask SwitchWithDefault(int k)
            {
                var t = A();
                switch (k)
                {
                    case 0: await t; break;
                    default: _ = Flow.Spawn(t); break;
                }
            }

            // Stored on both branches, awaited after them.
            async FlowTask AssignedOnBothBranches(bool c)
            {
                FlowTask t;
                if (c) t = A();
                else t = B();
                await t;
            }

            async FlowTask DoLoop(bool c)
            {
                var t = A();
                do { await t; } while (c);
            }

            async FlowTask ReadAfterNestedStatements(bool c)
            {
                var t = A();
                if (c) { await FlowTask.NextFrame(); }
                try { await t; } finally { }
            }

            // A path that throws leaves the function; it does not need to start the task.
            async FlowTask ElseThrows(bool c)
            {
                var t = A();
                if (c) await t;
                else throw new InvalidOperationException();
            }

            // A case that matches every value is a default.
            async FlowTask SwitchWithACaseThatMatchesEverything(int k)
            {
                var t = A();
                switch (k) { case 1: await t; break; case var _: await t; break; }
            }

            async FlowTask SwitchWithAVarCase(int k)
            {
                var t = A();
                switch (k) { case 1: await t; break; case var other: _ = Flow.Spawn(t); break; }
            }

            async FlowTask SwitchWithAThrowingDefault(int mode)
            {
                var t = A();
                switch (mode)
                {
                    case 1: await t; break;
                    case 2: await FlowTask.WhenAll(t, B()); break;
                    default: throw new ArgumentOutOfRangeException(nameof(mode));
                }
            }

            async FlowTask ThrowsFirst(bool c)
            {
                var t = A();
                if (!c) throw new InvalidOperationException();
                await t;
            }

            async FlowTask ConditionalThrow(bool c)
            {
                var t = A();
                await (c ? t : throw new InvalidOperationException());
            }

            // An early exit is not followed (no report rather than a false one).
            async FlowTask EarlyReturn(bool c)
            {
                var t = A();
                if (!c) return;
                await t;
            }

            async FlowTask Captured()
            {
                var t = A();
                Func<FlowTask> later = () => t;
                await later();
            }

            async FlowTask Skipped(bool skip)
            {
                var t = A();
                if (skip) t.Discard();
                else await t;
            }

            async FlowTask Reassigned()
            {
                var t = A();
                await t;
                t = B();
                await t;
                t = default;
            }

            async FlowTask InASwitchSection(int k)
            {
                switch (k)
                {
                    case 0:
                        var t = A();
                        await t;
                        break;
                }
            }

            // A loop on the constant true is left only by break, return or throw: a read anywhere in
            // its body counts, also for a task stored in the body and read at the top of the next iteration.
            async FlowTask Polling(Func<bool> ready)
            {
                var t = A();
                while (true)
                {
                    if (ready()) { await t; break; }
                    await FlowTask.NextFrame();
                }
            }

            async FlowTask OnePass()
            {
                var t = A();
                while (true) { await t; break; }
            }

            async FlowTask Prefetch()
            {
                var next = A();
                while (true)
                {
                    await next;
                    next = B();
                }
            }

            async FlowTask PrefetchFor()
            {
                var next = A();
                for (;;)
                {
                    await next;
                    next = B();
                }
            }

            async FlowTask PrefetchDo()
            {
                var next = A();
                do
                {
                    await next;
                    next = B();
                } while (true);
            }

            // A task stored in the body is read by the condition of the next iteration.
            static async FlowTask<bool> Step() { await FlowTask.NextFrame(); return true; }

            async FlowTask RetryWhile()
            {
                var t = Step();
                while (!await t) { t = Step(); }
            }

            async FlowTask RetryDo()
            {
                FlowTask<bool> t;
                do { t = Step(); } while (!await t);
            }
        }
        """);

    /// <summary>The FLOW003 probes (class P005), with the expectation of each line.</summary>
    [Test]
    public Task FLOW003_Probes() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        public static class P005
        {
            static async FlowTask A() { await FlowTask.NextFrame(); }
            static async FlowTask B() { await FlowTask.NextFrame(); }
            static async FlowTask<int> C() { await FlowTask.NextFrame(); return 1; }
            static FlowTask field;

            static async FlowTask InFlow(bool c, int k, FlowWorld w)
            {
                Func<FlowTask> f1 = async () => { {|FLOW003:A()|}; await FlowTask.NextFrame(); };
                void L1() { {|FLOW003:A()|}; }
                void L2() => {|FLOW003:A()|};
                Action a1 = () => {|FLOW003:A()|};
                Action a2 = () => { {|FLOW003:A()|}; };
                {|FLOW003:_ = k switch { 0 => A(), _ => B() }|};
                var {|FLOW003:t1|} = k switch { 0 => A(), _ => B() };
                {|FLOW003:_ = c ? A() : B()|};
                var {|FLOW003:t2|} = A(); if (c) await t2;
                var {|FLOW003:list|} = new List<FlowTask> { A(), B() };
                Func<FlowTask> f2 = () => A();
                {|FLOW003:f2()|};
                new List<int> { 1 }.ForEach(x => {|FLOW003:A()|});
                {|FLOW003:FlowBridge.FromTask(ct => Task.CompletedTask)|};
                {|FLOW003:C().WithoutResult()|};
                {|FLOW003:A().Discard()|};
                _ = Flow.Spawn(A());
                w.Run(B());
                field = A();
                await f1();
                L1();
                L2();
                await Flow.Named("x", A());
            }

            static void Sync(FlowWorld w) => {|FLOW003:A()|};

            static FlowTask Wrap()
            {
                {|FLOW003:A()|};
                return FlowTask.CompletedTask;
            }

            static FlowTask Prop => A();
        }
        """);
}
