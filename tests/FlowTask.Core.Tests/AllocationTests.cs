namespace Katout.FlowTask.Tests;

/// <summary>
/// No heap allocation in steady state: per-Tick waits, loops while staying in a scope, signals and subscriptions, Race
/// and WhenAll resolution, FlowProperty and Pause.
/// </summary>
public class AllocationTests : FlowTestBase
{
    sealed class State
    {
        public int Frame;
    }

    [Test]
    public void PerTickWaits()
    {
        var state = new State();

        async FlowTask Loop()
        {
            while (true)
            {
                await FlowTask.NextFrame();
                await FlowTask.WaitForSeconds(0);
                await FlowTask.DelayFrames(1);
                await FlowTask.WaitUntil(state, s => s.Frame >= 0);
                state.Frame++;
            }
        }

        World.Run(Loop());
        AssertSteadyStateAllocationFree(() => Tick(300));
    }

    async FlowTask<int> Step(int i)
    {
        await FlowTask.NextFrame();
        return i + 1;
    }

    [Test]
    public void LoopsWhileStayingInAScope()
    {
        async FlowTask Inner()
        {
            while (true) await Step(0);
        }

        async FlowTask Outer() => await FlowTask.WhenAll(Inner(), Inner());

        World.Run(Outer());
        AssertSteadyStateAllocationFree(() => Tick(300));
    }

    [Test]
    public void SignalSendAndReceive()
    {
        var edge = new Signal<int>();
        var queued = new Signal<int>();
        var sum = 0;

        async FlowTask EdgeReceiver()
        {
            while (true) sum += await edge.Next();
        }

        async FlowTask QueueReceiver()
        {
            using var sub = queued.Subscribe(BufferPolicy.Queue(16, BufferOverflow.DropOldest));
            while (true) sum += await sub.Next();
        }

        async FlowTask Sender()
        {
            var i = 0;
            while (true)
            {
                edge.Emit(i);
                queued.Emit(i);
                queued.Emit(i + 1);
                i++;
                await FlowTask.NextFrame();
            }
        }

        World.Run(EdgeReceiver());
        World.Run(QueueReceiver());
        World.Run(Sender());
        AssertSteadyStateAllocationFree(() => Tick(300));
        Assert.That(sum, Is.GreaterThan(0));
    }

    [Test]
    public void RaceLoserHandingBackAValueDoesNotAllocate()
    {
        // Every frame the subscription's wait receives a value and loses the Race to t: the value goes back to the
        // subscription, and the next round's wait takes it from the buffer synchronously, which wins that Race.
        var t = new Signal<int>();
        var s = new Signal<int>();
        var sum = 0;
        var takenBack = 0;

        async FlowTask Loop()
        {
            using var sub = s.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            while (true)
            {
                var r = await FlowTask.Race(t.Next(), sub.Next());
                if (r.Index == 1)
                {
                    sum += r.Value1;
                    takenBack++;
                }
            }
        }

        async FlowTask Sender()
        {
            var i = 0;
            while (true)
            {
                t.Emit(i);
                s.Emit(i);
                i++;
                await FlowTask.NextFrame();
            }
        }

        World.Run(Loop());
        World.Run(Sender());
        AssertSteadyStateAllocationFree(() => Tick(300));
        Assert.That(sum, Is.GreaterThan(0));
        Assert.That(takenBack, Is.GreaterThan(0), "the handed back values were taken by the next round's wait");
    }

    [Test]
    public void RaceResolutionWithLeafLosers()
    {
        var hits = new Signal<int>();
        var wins = 0;

        async FlowTask Loop()
        {
            using var sub = hits.Subscribe(BufferPolicy.Latest);
            while (true)
            {
                var r = await FlowTask.Race(sub.Next(), FlowTask.WaitForSeconds(5), FlowTask.NextFrame());
                if (r.Index == 0) wins++;
            }
        }

        World.Run(Loop());
        AssertSteadyStateAllocationFree(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                if (i % 2 == 0) hits.Emit(i);
                World.Tick(Dt);
            }
        });
        Assert.That(wins, Is.GreaterThan(0));
    }

    [Test]
    public void WhenAllResolutionWithLeafBranches()
    {
        var a = new Signal<int>();
        var b = new Signal<int>();
        var both = 0;
        var sum = 0;

        async FlowTask Loop()
        {
            using var sa = a.Subscribe(BufferPolicy.Latest);
            using var sb = b.Subscribe(BufferPolicy.Latest);
            while (true)
            {
                var (x, y) = await FlowTask.WhenAll(sa.Next(), sb.Next());
                both++;
                sum += x + y;
            }
        }

        World.Run(Loop());
        AssertSteadyStateAllocationFree(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                if (i % 3 == 0)
                {
                    a.Emit(i);
                    b.Emit(i);
                }
                else if (i % 3 == 1)
                {
                    a.Emit(i); // one branch ends; the WhenAll still waits on b
                }
                else
                {
                    b.Emit(i);
                }

                World.Tick(Dt);
            }
        });
        Assert.That(both, Is.GreaterThan(0));
        Assert.That(sum, Is.GreaterThan(0));
    }

    [Test]
    public void PropertyWaitUntil()
    {
        var property = new FlowProperty<int>();

        async FlowTask Watcher()
        {
            while (true)
            {
                var v = property.Value;
                await property.WaitUntil(v, static (x, old) => x != old);
                await FlowTask.NextFrame();
            }
        }

        World.Run(Watcher());
        AssertSteadyStateAllocationFree(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                property.Set(property.Value + 1);
                World.Tick(Dt);
            }
        });
    }

    [Test]
    public void PauseAndClocks()
    {
        var game = World.CreateClock("Game");

        async FlowTask Pauser()
        {
            while (true)
            {
                using (game.Pause())
                {
                    await FlowTask.NextFrame(World.UnscaledClock);
                }

                await FlowTask.NextFrame(World.UnscaledClock);
            }
        }

        World.Run(Pauser(), World.DefaultClock);
        AssertSteadyStateAllocationFree(() => Tick(300));
    }

    /// <summary>
    /// Lifetime handles disposed early inside one long-lived scope (the loop of an enemy AI or of a retry dialog)
    /// must not accumulate in the scope's cleanup list: both LIFO and out-of-order disposal stay bounded and
    /// allocation-free. A round takes 20,000 handles: a list that kept the released ones would double its array past
    /// the 131,072nd, in the last of the 7 rounds.
    /// </summary>
    [Test]
    public void EarlyDisposedHandlesInALongLivedScopeDoNotAccumulate()
    {
        var game = World.CreateClock("Game");
        var sig = new Signal<int>();
        var iterations = 0;

        async FlowTask Loop()
        {
            while (true)
            {
                for (var i = 0; i < 50; i++)
                {
                    using var held = sig.Subscribe(BufferPolicy.Latest); // LIFO release
                    using (game.Pause())
                    {
                    }

                    var sub = sig.Subscribe(BufferPolicy.Latest); // released out of order below
                    var pause = game.Pause();
                    sub.Dispose();
                    pause.Dispose();
                    iterations++;
                }

                await FlowTask.NextFrame();
            }
        }

        World.Run(Loop());
        AssertSteadyStateAllocationFree(() => Tick(100), rounds: 7);
        Assert.That(iterations, Is.GreaterThanOrEqualTo(7 * 100 * 50));
    }

    /// <summary>
    /// Unwinding a state-machine loser must throw FlowCanceledException through its frames: one new exception
    /// per unwind, plus what the runtime's exception dispatch allocates (stack-trace capture). This test records the cost
    /// rather than asserting zero.
    /// </summary>
    [Test]
    public void RaceResolutionWithStateMachineLoser_RecordsExceptionCost()
    {
        var hits = new Signal<int>();

        async FlowTask Patrol()
        {
            while (true) await FlowTask.NextFrame();
        }

        async FlowTask Loop()
        {
            using var sub = hits.Subscribe(BufferPolicy.Latest);
            while (true) await FlowTask.Race(Patrol(), sub.Next());
        }

        World.Run(Loop());
        long last = 0;
        for (var round = 0; round < 6; round++)
        {
            var before = Allocations.CurrentThreadBytes();
            for (var i = 0; i < 100; i++)
            {
                hits.Emit(i);
                World.Tick(Dt);
            }

            last = Allocations.CurrentThreadBytes() - before;
        }

        TestContext.Out.WriteLine($"state-machine loser unwinds: {last / 100.0:0.#} bytes per Race resolution (runtime exception dispatch)");
        Assert.Pass($"{last / 100.0:0.#} bytes per resolution");
    }
}
