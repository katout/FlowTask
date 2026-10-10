namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>
/// FLOW008 (Warning): a statement that drops the handle of Flow.Spawn. The child ends with the current scope, which a
/// spawn written like UniTask's Forget() does not expect; '_ = Flow.Spawn(x);' says that it is intended, and
/// FlowWorld.Run starts work that outlives the flow.
/// </summary>
public class Flow008Tests
{
    const string Message =
        "Flow.Spawn starts a child that stops when the current scope ends; start it with world.Run (FlowWorld.Current.Run in a flow) if it must outlive this flow, or write '_ = Flow.Spawn(...)' if it may end with this scope";

    const string Fragment = "Flow.Spawn starts a child that stops when the current scope ends";

    /// <summary>
    /// Both overloads, 'using static', a qualified call and an embedded statement; the rule looks at the statement
    /// wherever it is written (a helper called from a flow spawns into that flow's scope), and at an expression body
    /// whose value is dropped, as FLOW003 and FLOW004 do.
    /// </summary>
    [Test]
    public Task FLOW008_SpawnStatementsAreReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using System;
        using Katout.FlowTask;
        using static Katout.FlowTask.Flow;

        class Town
        {
            static async FlowTask Music() { await FlowTask.NextFrame(); }
            static async FlowTask<int> Count() { await FlowTask.NextFrame(); return 1; }

            async FlowTask Enter(bool night)
            {
                {|FLOW008:Flow.Spawn(Music())|};
                {|FLOW008:Flow.Spawn(Count())|};
                {|FLOW008:Flow.Spawn<int>(Count())|};
                {|FLOW008:Spawn(Music())|};
                {|FLOW008:Katout.FlowTask.Flow.Spawn(Music())|};
                if (night) {|FLOW008:Flow.Spawn(Music())|};
                await FlowTask.NextFrame();
            }

            void StartMusic() { {|FLOW008:Flow.Spawn(Music())|}; }

            void StartMusicArrow() => {|FLOW008:Flow.Spawn(Music())|};

            async FlowTask Lambdas()
            {
                Func<FlowTask> flow = async () => { {|FLOW008:Flow.Spawn(Music())|}; await FlowTask.NextFrame(); };
                Action action = () => { {|FLOW008:Flow.Spawn(Music())|}; };
                await flow();
                action();
            }
        }
        """,
        Message, Fragment, Fragment, Fragment, Fragment, Fragment, Fragment, Fragment, Fragment, Fragment);

    /// <summary>
    /// An expression lambda written in a FlowTask method drops the handle as a block lambda does: it runs now, in this
    /// flow ('ForEach'), or later from wherever it is called. A local that holds the handle and is never read drops it
    /// too (FLOW004 sees a lifetime handle the same way).
    /// </summary>
    [Test]
    public Task FLOW008_ExpressionLambdasInAFlowAndUnreadLocalsAreReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using Katout.FlowTask;

        class Shop
        {
            static async FlowTask Send(int x) { await FlowTask.NextFrame(); }
            static async FlowTask<int> Count() { await FlowTask.NextFrame(); return 1; }

            async FlowTask Open(List<int> items)
            {
                items.ForEach(x => {|FLOW008:Flow.Spawn(Send(x))|});
                Action send = () => {|FLOW008:Flow.Spawn(Send(1))|};
                send();
                var h = {|FLOW008:Flow.Spawn(Send(2))|};
                var n = {|FLOW008:Flow.Spawn(Count())|};
                FlowHandle typed = {|FLOW008:Flow.Spawn(Send(3))|};
                void Local()
                {
                    var inner = {|FLOW008:Flow.Spawn(Send(4))|};
                }

                Local();
                await FlowTask.NextFrame();
            }

            void Helper()
            {
                var h = {|FLOW008:Flow.Spawn(Send(5))|};
            }
        }
        """);

    /// <summary>
    /// A local that is read (awaited, canceled, passed, returned, captured), 'var _ =', a lambda that returns the
    /// handle, and an expression lambda outside a FlowTask method, where a spawn that runs now throws (the
    /// Assert.Throws idiom).
    /// </summary>
    [Test]
    public Task FLOW008_ReadLocalsAndLambdasOutsideAFlowAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        static class Assert
        {
            public static void Throws<T>(Action action) where T : Exception { }
        }

        class Shop
        {
            static async FlowTask Send() { await FlowTask.NextFrame(); }
            static void Keep(FlowHandle handle) { }

            async FlowTask<FlowHandle> Open(bool cancel)
            {
                var joined = Flow.Spawn(Send());
                var canceled = Flow.Spawn(Send());
                var kept = Flow.Spawn(Send());
                var returned = Flow.Spawn(Send());
                var captured = Flow.Spawn(Send());
                var _ = Flow.Spawn(Send());
                Func<FlowHandle> make = () => Flow.Spawn(Send());
                Action later = () => captured.Cancel();
                if (cancel) canceled.Cancel();
                Keep(kept);
                await joined.Join();
                later();
                make();
                return returned;
            }

            void SpawnOutsideAFlowThrows()
            {
                Assert.Throws<FlowMisuseException>(() => Flow.Spawn(Send()));
                Action start = () => Flow.Spawn(Send());
            }
        }
        """);

    /// <summary>
    /// A handle that is discarded with '_ =', stored, used, passed on or returned; a lambda that returns the handle;
    /// FlowWorld.Run; methods named Spawn that are not Flow's.
    /// </summary>
    [Test]
    public Task FLOW008_UsedOrExplicitlyDiscardedHandlesAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        class Pool
        {
            public FlowHandle Spawn(FlowTask task) => default;
        }

        class Town
        {
            FlowHandle _music;

            static async FlowTask Music() { await FlowTask.NextFrame(); }
            static FlowHandle StartMusic() => Flow.Spawn(Music());
            static void Keep(FlowHandle handle) { }
            static void Spawn(FlowTask task) { }

            FlowHandle Started => Flow.Spawn(Music());

            async FlowTask Enter(FlowWorld world, Pool pool)
            {
                _ = Flow.Spawn(Music());
                var h = Flow.Spawn(Music());
                _music = Flow.Spawn(Music());
                Keep(Flow.Spawn(Music()));
                Flow.Spawn(Music()).Cancel();
                await Flow.Spawn(Music()).Join();
                world.Run(Music());
                FlowWorld.Current.Run(Music());
                Func<FlowHandle> make = () => Flow.Spawn(Music());
                pool.Spawn(Music());
                Spawn(Music());
                await h.Join();
            }
        }
        """);

    /// <summary>
    /// A spawn that FLOW009 reports (in an event handler) is not reported again. The result type of the spawned flow
    /// does not matter: a dropped handle of a flow with a result is reported like any other.
    /// </summary>
    [Test]
    public Task FLOW008_SpawnsThatOtherRulesReportAreNotReportedTwice() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        public sealed class Button
        {
            public event Action Clicked;
        }

        class Town
        {
            static async FlowTask Music() { await FlowTask.NextFrame(); }
            static FlowTask<int> Fetch() => FlowTask.FromResult(1);

            async FlowTask Enter(Button button)
            {
                {|FLOW008:Flow.Spawn(Fetch())|};

                button.Clicked += delegate { {|FLOW009:Flow.Spawn(Music())|}; };
                await FlowTask.NextFrame();
            }
        }
        """);

    /// <summary>
    /// In a finally block and in a catch of the cancellation, a spawn statement is reported as anywhere else: the child
    /// starts there too, also while the scope is canceled, and stops when the scope ends.
    /// </summary>
    [Test]
    public Task FLOW008_SpawnStatementsInCleanupCodeAreReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class Town
        {
            static async FlowTask Music() { await FlowTask.NextFrame(); }

            async FlowTask Enter()
            {
                try { await FlowTask.NextFrame(); }
                finally { {|FLOW008:Flow.Spawn(Music())|}; }

                try { await FlowTask.NextFrame(); }
                catch (FlowCanceledException) { {|FLOW008:Flow.Spawn(Music())|}; throw; }

                try { await FlowTask.NextFrame(); }
                finally { _ = Flow.Spawn(Music()); }
            }
        }
        """);

    // ------------------------------------------------------------------ code fixes

    const string Before = """
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music() => FlowTask.CompletedTask;

            async FlowTask Enter()
            {
                // music
                Flow.Spawn(Music()); // trailing
            }
        }
        """;

    [Test]
    public Task FLOW008_FixDiscardsTheHandle() => CodeFixHarness.VerifyAsync(
        Before,
        """
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music() => FlowTask.CompletedTask;

            async FlowTask Enter()
            {
                // music
                _ = Flow.Spawn(Music()); // trailing
            }
        }
        """,
        new SpawnHandleCodeFixProvider(),
        DiagnosticIds.SpawnHandleDropped,
        SpawnHandleCodeFixProvider.DiscardEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.SpawnHandleDropped, DiagnosticIds.FlowTaskDropped });

    [Test]
    public Task FLOW008_FixRunsTheTaskAsARootFlow() => CodeFixHarness.VerifyAsync(
        Before,
        """
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music() => FlowTask.CompletedTask;

            async FlowTask Enter()
            {
                // music
                FlowWorld.Current.Run(Music()); // trailing
            }
        }
        """,
        new SpawnHandleCodeFixProvider(),
        DiagnosticIds.SpawnHandleDropped,
        SpawnHandleCodeFixProvider.RunEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.SpawnHandleDropped, DiagnosticIds.FlowTaskDropped });

    [Test]
    public Task FLOW008_FixDiscardsTheHandleOfAnExpressionBody() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music() => FlowTask.CompletedTask;

            void StartMusic() => Flow.Spawn(Music());
        }
        """,
        """
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music() => FlowTask.CompletedTask;

            void StartMusic() => _ = Flow.Spawn(Music());
        }
        """,
        new SpawnHandleCodeFixProvider(),
        DiagnosticIds.SpawnHandleDropped,
        SpawnHandleCodeFixProvider.DiscardEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.SpawnHandleDropped });

    [Test]
    public Task FLOW008_FixDiscardsTheHandleOfAnExpressionLambda() => CodeFixHarness.VerifyAsync(
        """
        using System.Collections.Generic;
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music(int x) => FlowTask.CompletedTask;

            async FlowTask Enter(List<int> items)
            {
                items.ForEach(x => Flow.Spawn(Music(x)));
            }
        }
        """,
        """
        using System.Collections.Generic;
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music(int x) => FlowTask.CompletedTask;

            async FlowTask Enter(List<int> items)
            {
                items.ForEach(x => _ = Flow.Spawn(Music(x)));
            }
        }
        """,
        new SpawnHandleCodeFixProvider(),
        DiagnosticIds.SpawnHandleDropped,
        SpawnHandleCodeFixProvider.DiscardEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.SpawnHandleDropped });

    [Test]
    public Task FLOW008_FixReplacesAnUnreadLocalWithADiscard() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music() => FlowTask.CompletedTask;

            async FlowTask Enter()
            {
                // music
                var h = Flow.Spawn(Music()); // trailing
                await FlowTask.NextFrame();
            }
        }
        """,
        """
        using Katout.FlowTask;

        class Town
        {
            static FlowTask Music() => FlowTask.CompletedTask;

            async FlowTask Enter()
            {
                // music
                _ = Flow.Spawn(Music()); // trailing
                await FlowTask.NextFrame();
            }
        }
        """,
        new SpawnHandleCodeFixProvider(),
        DiagnosticIds.SpawnHandleDropped,
        SpawnHandleCodeFixProvider.DiscardEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.SpawnHandleDropped, DiagnosticIds.FlowTaskDropped });

    /// <summary>
    /// Run keeps the type arguments, and spells FlowWorld in full where only 'using static Katout.FlowTask.Flow;'
    /// imports anything, so the fix adds no using directive (Fix All then changes nothing but the calls).
    /// </summary>
    [Test]
    public Task FLOW008_FixRunKeepsTypeArgumentsAndQualifiesFlowWorld() => CodeFixHarness.VerifyAsync(
        """
        using static Katout.FlowTask.Flow;

        class Town
        {
            static async Katout.FlowTask.FlowTask<int> Count() { await Katout.FlowTask.FlowTask.NextFrame(); return 1; }

            async Katout.FlowTask.FlowTask Enter()
            {
                Spawn<int>(Count());
            }
        }
        """,
        """
        using static Katout.FlowTask.Flow;

        class Town
        {
            static async Katout.FlowTask.FlowTask<int> Count() { await Katout.FlowTask.FlowTask.NextFrame(); return 1; }

            async Katout.FlowTask.FlowTask Enter()
            {
                global::Katout.FlowTask.FlowWorld.Current.Run<int>(Count());
            }
        }
        """,
        new SpawnHandleCodeFixProvider(),
        DiagnosticIds.SpawnHandleDropped,
        SpawnHandleCodeFixProvider.RunEquivalenceKey,
        absentAfter: new[] { DiagnosticIds.SpawnHandleDropped });

    /// <summary>
    /// Run is offered in a FlowTask method only, where FlowWorld.Current is set (as for FLOW003); the discard is not
    /// offered where a variable named '_' is in scope ('_ = x' would assign to it), nor for an unread local that is
    /// assigned again (removing its declaration would leave 'h = ...' without a variable).
    /// </summary>
    [Test]
    public async Task FLOW008_FixesAreOfferedWhereTheyKeepTheCodeValid()
    {
        var provider = new SpawnHandleCodeFixProvider();
        const string Source = """
            using Katout.FlowTask;

            class Town
            {
                static FlowTask Music() => FlowTask.CompletedTask;

                async FlowTask Enter()
                {
                    Flow.Spawn(Music());
                    await FlowTask.NextFrame();
                }

                void StartMusic()
                {
                    Flow.Spawn(Music());
                }

                async FlowTask WithUnderscore(int _)
                {
                    Flow.Spawn(Music());
                    await FlowTask.NextFrame();
                }

                async FlowTask Reassigned()
                {
                    var h = Flow.Spawn(Music());
                    h = Flow.Spawn(Music());
                    await FlowTask.NextFrame();
                }
            }
            """;

        var inFlow = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.SpawnHandleDropped, diagnosticIndex: 0);
        Assert.That(inFlow.Select(a => a.EquivalenceKey), Is.EqualTo(new[]
        {
            SpawnHandleCodeFixProvider.DiscardEquivalenceKey,
            SpawnHandleCodeFixProvider.RunEquivalenceKey,
        }));

        var outside = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.SpawnHandleDropped, diagnosticIndex: 1);
        Assert.That(outside.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { SpawnHandleCodeFixProvider.DiscardEquivalenceKey }));

        var underscore = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.SpawnHandleDropped, diagnosticIndex: 2);
        Assert.That(underscore.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { SpawnHandleCodeFixProvider.RunEquivalenceKey }));

        var reassigned = await CodeFixHarness.GetActionsAsync(Source, provider, DiagnosticIds.SpawnHandleDropped, diagnosticIndex: 3);
        Assert.That(reassigned.Select(a => a.EquivalenceKey), Is.EqualTo(new[] { SpawnHandleCodeFixProvider.RunEquivalenceKey }));
    }
}
