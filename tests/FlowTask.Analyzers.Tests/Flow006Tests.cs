namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW006 (Info): Signal&lt;T&gt; / EventSignal&lt;T&gt; Next() awaited in a loop drops the emits between iterations.</summary>
public class Flow006Tests
{
    [Test]
    public Task FLOW006_NextOfASignalAwaitedInALoopIsReported() => AnalyzerHarness.VerifyMessagesAsync("""
        using System.Collections.Generic;
        using Katout.FlowTask;

        class Enemy
        {
            readonly Signal<int> _damaged = new Signal<int>();
            readonly FlowProperty<int> _hp = new FlowProperty<int>(10);

            static async FlowTask Patrol() { await FlowTask.NextFrame(); }

            async FlowTask While()
            {
                while (true)
                {
                    var damage = await {|FLOW006:_damaged.Next()|};
                    await FlowTask.WaitForSeconds(0.8);
                }
            }

            async FlowTask For(Signal<int> hits)
            {
                for (var i = 0; i < 3; i++) _ = await {|FLOW006:hits.NextOrClosed()|};
            }

            async FlowTask ForEach(List<int> items)
            {
                foreach (var item in items) await {|FLOW006:_damaged.Next()|}.WithoutResult();
            }

            async FlowTask Do()
            {
                do { await {|FLOW006:_hp.Changed.Next()|}; } while (_hp.Value > 0);
            }

            async FlowTask RaceBranch()
            {
                while (true)
                {
                    var r = await FlowTask.Race({|FLOW006:_damaged.Next()|}.WithoutResult(), Patrol());
                }
            }

            async FlowTask Event(EventSignal<int> clicks)
            {
                while (true) await {|FLOW006:clicks.Next()|};
            }

            async FlowTask Condition()
            {
                while (await {|FLOW006:_damaged.Next()|} > 0) { }
            }

            async FlowTask NestedLoops(Signal<int> hits)
            {
                while (true)
                {
                    var s = new Signal<int>();
                    for (var i = 0; i < 3; i++) await {|FLOW006:s.Next()|};
                }
            }

            // A helper that passes its caller's file and line on.
            async FlowTask CallerPlacePassedOn(Signal<int> hits, string file, int line)
            {
                while (true) await {|FLOW006:hits.Next(file, line)|};
            }
        }
        """,
        "'_damaged.Next()' is awaited in a loop: values emitted while the loop body runs are dropped, because Next() waits only for the next emit; if they must not be lost, subscribe once before the loop with '_damaged.Subscribe(BufferPolicy...)' and await the subscription's Next()",
        "'hits.NextOrClosed()' is awaited in a loop",
        "'_damaged.Next()'",
        "'_hp.Changed.Next()'",
        "'_damaged.Next()'",
        "'clicks.Next()'",
        "'_damaged.Next()'",
        "'s.Next()'",
        "'hits.Next()' is awaited in a loop");

    [Test]
    public Task FLOW006_SubscriptionNextIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class Enemy
        {
            readonly Signal<int> _damaged = new Signal<int>();

            async FlowTask Run()
            {
                using var hits = _damaged.Subscribe(BufferPolicy.Latest);
                while (true)
                {
                    var r = await FlowTask.Race(FlowTask.NextFrame(), hits.Next().WithoutResult());
                    if (r.Index == 1) await FlowTask.WaitForSeconds(0.8);
                }
            }
        }
        """);

    [Test]
    public Task FLOW006_NextOutsideLoopsIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using Katout.FlowTask;

        class C
        {
            readonly Signal<int> _start = new Signal<int>();

            async FlowTask Once()
            {
                await _start.Next();
                for (var v = await _start.Next(); v < 3; v++) { }
                while (true)
                {
                    // A nested function is its own flow; its await is not repeated by this loop.
                    Func<FlowTask> wait = async () => await _start.Next();
                    await wait();
                }
            }
        }
        """);

    [Test]
    public Task FLOW006_SignalCreatedInsideTheLoopIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using Katout.FlowTask;

        class C
        {
            static async FlowTask Emit(Signal<int> s) { await FlowTask.NextFrame(); s.Emit(1); }

            async FlowTask Run()
            {
                while (true)
                {
                    var done = new Signal<int>();
                    _ = Flow.Spawn(Emit(done));
                    await done.Next();
                }
            }
        }
        """);

    /// <summary>
    /// A receiver that can be another signal on each iteration has no single signal to subscribe to before the loop.
    /// </summary>
    [Test]
    public Task FLOW006_ReceiverThatChangesInTheLoopIsNotReported() => AnalyzerHarness.VerifyAsync("""
        using System.Collections.Generic;
        using Katout.FlowTask;

        class Enemy { public Signal<int> Damaged = new Signal<int>(); }

        class C
        {
            Enemy Target { get; set; }

            async FlowTask Indexed(Signal<int>[] signals)
            {
                for (var i = 0; i < signals.Length; i++) await signals[i].Next();
            }

            async FlowTask IndexedByAWhile(Signal<int>[] signals)
            {
                var i = 0;
                while (i < signals.Length)
                {
                    await signals[i].Next();
                    i++;
                }
            }

            async FlowTask Reassigned(Signal<int> a, Signal<int> b)
            {
                var s = a;
                while (true)
                {
                    await s.Next();
                    s = b;
                }
            }

            async FlowTask TargetReplaced(Enemy next)
            {
                while (true)
                {
                    await this.Target.Damaged.Next();
                    Target = next;
                }
            }

            async FlowTask EachSignal(List<Signal<int>> signals)
            {
                foreach (var s in signals) await s.Next();
            }
        }
        """);

    // ------------------------------------------------------------------ code fix

    [Test]
    public Task FLOW006_FixSubscribesBeforeTheLoop() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class Enemy
        {
            readonly Signal<int> _damaged = new Signal<int>();
            readonly Signal<int> _healed = new Signal<int>();

            async FlowTask Run(bool alive)
            {
                // react to damage
                while (alive)
                {
                    var damage = await _damaged.Next();
                    var more = await _damaged.NextOrClosed();
                    await _healed.Next();
                }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class Enemy
        {
            readonly Signal<int> _damaged = new Signal<int>();
            readonly Signal<int> _healed = new Signal<int>();

            async FlowTask Run(bool alive)
            {
                using var subscription = _damaged.Subscribe(BufferPolicy.Latest);
                // react to damage
                while (alive)
                {
                    var damage = await subscription.Next();
                    var more = await subscription.NextOrClosed();
                    await _healed.Next();
                }
            }
        }
        """,
        new SubscribeBeforeLoopCodeFixProvider(),
        DiagnosticIds.SignalNextInLoop,
        SubscribeBeforeLoopCodeFixProvider.LatestEquivalenceKey);

    [Test]
    public Task FLOW006_FixKeepsThePlaceArguments() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class Enemy
        {
            readonly Signal<int> _damaged = new Signal<int>();

            async FlowTask Run(bool alive, string file, int line)
            {
                while (alive)
                {
                    var damage = await _damaged.Next(file, line);
                }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class Enemy
        {
            readonly Signal<int> _damaged = new Signal<int>();

            async FlowTask Run(bool alive, string file, int line)
            {
                using var subscription = _damaged.Subscribe(BufferPolicy.Latest);
                while (alive)
                {
                    var damage = await subscription.Next(file, line);
                }
            }
        }
        """,
        new SubscribeBeforeLoopCodeFixProvider(),
        DiagnosticIds.SignalNextInLoop,
        SubscribeBeforeLoopCodeFixProvider.LatestEquivalenceKey);

    [Test]
    public Task FLOW006_FixSubscribesBeforeTheOutermostLoopAndAddsTheUsing() => CodeFixHarness.VerifyAsync(
        """
        class Enemy
        {
            readonly Katout.FlowTask.Signal<int> _damaged = new Katout.FlowTask.Signal<int>();

            async Katout.FlowTask.FlowTask Run(int subscription)
            {
                while (true)
                {
                    for (var i = 0; i < 3; i++) await this._damaged.Next();
                }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class Enemy
        {
            readonly Katout.FlowTask.Signal<int> _damaged = new Katout.FlowTask.Signal<int>();

            async Katout.FlowTask.FlowTask Run(int subscription)
            {
                using var subscription1 = this._damaged.Subscribe(BufferPolicy.Latest);
                while (true)
                {
                    for (var i = 0; i < 3; i++) await subscription1.Next();
                }
            }
        }
        """,
        new SubscribeBeforeLoopCodeFixProvider(),
        DiagnosticIds.SignalNextInLoop,
        SubscribeBeforeLoopCodeFixProvider.LatestEquivalenceKey);

    [Test]
    public async Task FLOW006_NoFixWhenTheLoopIsNotDirectlyInABlock()
    {
        var provider = new SubscribeBeforeLoopCodeFixProvider();
        var embedded = await CodeFixHarness.GetActionsAsync("""
            using Katout.FlowTask;

            class C
            {
                async FlowTask Run(Signal<int> s, bool c)
                {
                    if (c) while (true) await s.Next();
                }
            }
            """, provider, DiagnosticIds.SignalNextInLoop);
        Assert.That(embedded, Is.Empty);

        var computed = await CodeFixHarness.GetActionsAsync("""
            using Katout.FlowTask;

            class C
            {
                Signal<int> Get() => new Signal<int>();

                async FlowTask Run()
                {
                    while (true) await Get().Next();
                }
            }
            """, provider, DiagnosticIds.SignalNextInLoop);
        Assert.That(computed, Is.Empty);
    }

    /// <summary>
    /// Subscribing before the loop evaluates the receiver earlier: not before a loop whose condition tests it or that
    /// checks it for null.
    /// </summary>
    [Test]
    public async Task FLOW006_NoFixBeforeALoopThatGuardsTheReceiver()
    {
        var provider = new SubscribeBeforeLoopCodeFixProvider();
        foreach (var loop in new[]
        {
            "while (target != null) { await target.Damaged.Next(); }",
            "while (true) { if (target == null) break; await target.Damaged.Next(); }",
            "while (true) { if (target is null) continue; await target.Damaged.Next(); target?.Heal(); }",
            "for (var alive = true; alive; alive = target.Alive) { await target.Damaged.Next(); }",
        })
        {
            var actions = await CodeFixHarness.GetActionsAsync($$"""
                using Katout.FlowTask;

                class Enemy
                {
                    public Signal<int> Damaged = new Signal<int>();
                    public bool Alive => true;
                    public void Heal() { }
                }

                class C
                {
                    Enemy target;

                    async FlowTask Run()
                    {
                        {{loop}}
                    }
                }
                """, provider, DiagnosticIds.SignalNextInLoop);
            Assert.That(actions, Is.Empty, loop);
        }
    }

    [Test]
    public Task FLOW006_FixSubscribesInsideTheLoopThatGuardsTheReceiver() => CodeFixHarness.VerifyAsync(
        """
        using Katout.FlowTask;

        class Enemy { public Signal<int> Damaged = new Signal<int>(); }

        class C
        {
            Enemy target;

            async FlowTask Run()
            {
                while (target != null)
                {
                    for (var i = 0; i < 3; i++) await target.Damaged.Next();
                }
            }
        }
        """,
        """
        using Katout.FlowTask;

        class Enemy { public Signal<int> Damaged = new Signal<int>(); }

        class C
        {
            Enemy target;

            async FlowTask Run()
            {
                while (target != null)
                {
                    using var subscription = target.Damaged.Subscribe(BufferPolicy.Latest);
                    for (var i = 0; i < 3; i++) await subscription.Next();
                }
            }
        }
        """,
        new SubscribeBeforeLoopCodeFixProvider(),
        DiagnosticIds.SignalNextInLoop,
        SubscribeBeforeLoopCodeFixProvider.LatestEquivalenceKey);
}
