namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW004: a lifetime handle created by a call whose result is not stored.</summary>
public class Flow004Tests
{
    [Test]
    public Task UnstoredHandlesAreReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            event Action Clicked;

            async FlowTask M(Clock clock, Signal<int> signal)
            {
                {|FLOW004:clock.Pause()|};
                {|FLOW004:signal.Subscribe(BufferPolicy.Latest)|};
                {|FLOW004:FlowBridge.FromCallback<FlowUnit>(emit => { System.Action h = () => emit(FlowUnit.Default); Clicked += h; return () => Clicked -= h; })|};
                await FlowTask.NextFrame();
            }

            void Plain(Clock clock) => {|FLOW004:clock.Pause()|};
        }
        """);

    [Test]
    public Task StoredOrExplicitlyDiscardedHandlesAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            ScopedHandle _pause;

            async FlowTask M(Clock clock, Signal<int> signal)
            {
                using var pause = clock.Pause();
                var events = FlowBridge.FromCallback<int>(emit => () => { });
                _ = signal.Subscribe(BufferPolicy.Latest); // scope lifetime, on purpose
                using (clock.Pause())
                {
                    await FlowTask.NextFrame();
                }

                _pause = clock.Pause();
                events.Dispose();
            }

            // Void lambdas are not checked for FLOW004: 'Assert.Throws(() => clock.Pause())' expects a throw.
            static void Throws(System.Action action) { }
            void ExpectThrow(Clock real) => Throws(() => real.Pause());
        }
        """);

    /// <summary>
    /// 'var _ = ...' declares a local named '_', but it is written as the discard '_ = ...;' (scope lifetime on
    /// purpose): 'using var' would end the pause at the end of the block instead.
    /// </summary>
    [Test]
    public Task FLOW004_LocalNamedUnderscoreIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock, Signal<int> signal)
            {
                var _ = clock.Pause();
                var {|FLOW004:subscription|} = signal.Subscribe(BufferPolicy.Latest);
                await FlowTask.NextFrame();
            }

            async FlowTask Typed(Signal<int> signal)
            {
                Subscription<int> _ = signal.Subscribe(BufferPolicy.Latest);
                await FlowTask.NextFrame();
            }
        }
        """);

    // ------------------------------------------------------------------ code fix

    [Test]
    public Task FixDeclaresAUsingVariable() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock)
            {
                // pause
                clock.Pause(); // behind the dialog
                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock)
            {
                // pause
                using var pause = clock.Pause(); // behind the dialog
                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        new UsingLifetimeHandleCodeFixProvider(),
        DiagnosticIds.LifetimeHandleDropped);

    [Test]
    public Task FixPicksANonConflictingName() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock, Clock other, Signal<int> signal)
            {
                using var pause = other.Pause();
                clock.Pause();
                signal.Subscribe(BufferPolicy.Latest);
                if (clock != null)
                {
                    var pause1 = 1;
                }

                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock, Clock other, Signal<int> signal)
            {
                using var pause = other.Pause();
                using var pause2 = clock.Pause();
                signal.Subscribe(BufferPolicy.Latest);
                if (clock != null)
                {
                    var pause1 = 1;
                }

                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        new UsingLifetimeHandleCodeFixProvider(),
        DiagnosticIds.LifetimeHandleDropped);

    [Test]
    public Task FixNamesSubscriptions() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Signal<int> signal)
            {
                signal.Subscribe(BufferPolicy.Latest);
                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Signal<int> signal)
            {
                using var subscription = signal.Subscribe(BufferPolicy.Latest);
                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        new UsingLifetimeHandleCodeFixProvider(),
        DiagnosticIds.LifetimeHandleDropped);

    [Test]
    public async Task NoFixForEmbeddedStatements()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using Katout.FlowTask;

            class C
            {
                void M(Clock clock, bool b)
                {
                    if (b) clock.Pause();
                }
            }
            """, new UsingLifetimeHandleCodeFixProvider(), DiagnosticIds.LifetimeHandleDropped);
        Assert.That(actions, Is.Empty);
    }

    // ------------------------------------------------------------------ locals never disposed or used

    [Test]
    public Task FLOW004_HandleStoredButNeverUsedIsReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock, Signal<int> signal)
            {
                var {|FLOW004:h|} = clock.Pause();
                var {|FLOW004:e|} = FlowBridge.FromCallback<int>(emit => () => { });
                Subscription<int> {|FLOW004:s|} = signal.Subscribe(BufferPolicy.Latest);
                await FlowTask.NextFrame();
            }
        }
        """,
        "The 'ScopedHandle' stored in 'h' is never disposed or used, so it stays active until the current scope ends; declare it with 'using var'",
        "The 'EventSignal<int>' stored in 'e'",
        "The 'Subscription<int>' stored in 's'");

    [Test]
    public Task FLOW004_HandleDisposedOrUsedIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            ScopedHandle _kept;

            static void Keep(ScopedHandle handle) { }

            async FlowTask M(Clock clock, Signal<int> sig)
            {
                // Disposed in a finally.
                var sub = sig.Subscribe(BufferPolicy.Latest);
                try
                {
                    await sub.Next();
                }
                finally
                {
                    sub.Dispose();
                }

                using var pause = clock.Pause();
                var passed = clock.Pause();
                Keep(passed);
                var stored = clock.Pause();
                _kept = stored;
                var used = clock.Pause();
                using (used) { }
                ScopedHandle none = default;
            }
        }
        """);

    /// <summary>Probes: handles dropped in statements, local functions, locals, discards and a void lambda.</summary>
    [Test]
    public Task FLOW004_Probes() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            static async FlowTask Child() { await FlowTask.NextFrame(); }

            static async FlowTask InFlow(int k, Clock clock, Clock other, Signal<int> sig)
            {
                {|FLOW004:clock.Pause()|};
                Action a1 = () => clock.Pause(); // void lambda: documented exclusion
                void P() { {|FLOW004:clock.Pause()|}; }
                void P2() => {|FLOW004:clock.Pause()|};
                var {|FLOW004:h|} = clock.Pause();
                _ = clock.Pause(); // documented intent: paused until the scope ends
                _ = k switch { 0 => clock.Pause(), _ => other.Pause() };
                {|FLOW004:sig.Subscribe(BufferPolicy.Latest)|};
                P();
                P2();
                await Child();
            }
        }
        """);

    [Test]
    public Task FLOW004_FixAddsUsingToTheDeclaration() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock)
            {
                // behind the dialog
                ScopedHandle pause = clock.Pause(); // paused
                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        """
        using Katout.FlowTask;

        class C
        {
            async FlowTask M(Clock clock)
            {
                // behind the dialog
                using ScopedHandle pause = clock.Pause(); // paused
                await FlowTask.WaitForSeconds(1);
            }
        }
        """,
        new UsingLifetimeHandleCodeFixProvider(),
        DiagnosticIds.LifetimeHandleDropped,
        UsingLifetimeHandleCodeFixProvider.UsingDeclarationEquivalenceKey);

    /// <summary>
    /// Only a local that makes a new handle (a call or 'new') owns it; a copy of a handle that exists elsewhere is not
    /// reported, and 'using' on it would dispose the shared handle.
    /// </summary>
    [Test]
    public Task FLOW004_CopyOfAnExistingHandleIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            ScopedHandle _pause;
            Subscription<int> Hits { get; set; }

            async FlowTask M(Clock clock, ScopedHandle[] handles, Clock maybe, bool c)
            {
                var field = _pause;
                var property = this.Hits;
                var element = handles[0];
                var {|FLOW004:conditional|} = maybe?.Pause();
                var {|FLOW004:either|} = c ? clock.Pause() : _pause;
                await FlowTask.NextFrame();
            }
        }
        """);

    [Test]
    public async Task FLOW004_NoUsingFixForALocalThatIsWrittenAgain()
    {
        var provider = new UsingLifetimeHandleCodeFixProvider();
        foreach (var write in new[] { "h = clock.Pause();", "Make(out h);", "(h, n) = (clock.Pause(), 1);" })
        {
            var actions = await CodeFixHarness.GetActionsAsync($$"""
                using Katout.FlowTask;

                class C
                {
                    static void Make(out ScopedHandle handle) => handle = default;

                    async FlowTask M(Clock clock)
                    {
                        int n;
                        var h = clock.Pause();
                        {{write}}
                        await FlowTask.NextFrame();
                    }
                }
                """, provider, DiagnosticIds.LifetimeHandleDropped);
            Assert.That(actions.Select(a => a.EquivalenceKey), Does.Not.Contain(UsingLifetimeHandleCodeFixProvider.UsingDeclarationEquivalenceKey), write);
        }
    }

    [Test]
    public async Task FLOW004_NoFixForADeclarationInASwitchSection()
    {
        var actions = await CodeFixHarness.GetActionsAsync("""
            using Katout.FlowTask;

            class C
            {
                void M(Clock clock, int k)
                {
                    switch (k)
                    {
                        case 0:
                            var pause = clock.Pause();
                            break;
                    }
                }
            }
            """, new UsingLifetimeHandleCodeFixProvider(), DiagnosticIds.LifetimeHandleDropped);
        Assert.That(actions, Is.Empty);
    }
}
