using System;
using System.Diagnostics;

namespace Katout.FlowTask.AotSmoke;

/// <summary>
/// Steady-state zero allocation measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// Early rounds may allocate (pool fill); the last two of eight rounds must be exactly 0 bytes.
/// </summary>
internal static class AllocationChecks
{
    const double Dt = Scenarios.Dt;

    static string SteadyStateZero(Action round, int rounds = 8)
    {
        var results = new long[rounds];
        for (var i = 0; i < rounds; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            round();
            results[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var detail = "bytes/round: " + string.Join(", ", results);
        Check.That(results[rounds - 1] == 0 && results[rounds - 2] == 0, detail);
        return detail;
    }

    sealed class FrameState
    {
        public int Frame;
    }

    public static string PerTickWaits()
    {
        using var w = new FlowWorld();
        var state = new FrameState();

        async FlowTask Loop()
        {
            while (true)
            {
                await FlowTask.NextFrame();
                await FlowTask.WaitForSeconds(0);
                await FlowTask.DelayFrames(1);
                await FlowTask.WaitUntil(state, static s => s.Frame >= 0);
                await FlowTask.WaitForSeconds(1.0 / 120);
                state.Frame++;
            }
        }

        w.Run(Loop());
        var detail = SteadyStateZero(() => Scenarios.Ticks(w, 300));
        Check.That(state.Frame > 300, "loop progressed");
        return detail;
    }

    static async FlowTask<int> Step(int i)
    {
        Flow.AddCleanup(i, static _ => { }); // pooled thunk, runs when Step ends
        await FlowTask.NextFrame();
        return i + 1;
    }

    static async FlowTask<int> Sync(int i) => i + 1;

    static async FlowTask Inner(FrameState progress)
    {
        var i = 0;
        while (true)
        {
            i = await Step(i);
            i = await Sync(i);
            if (i % 7 == 0)
            {
                var (a, b) = await FlowTask.WhenAll(Step(1), Step(2));
                i += b - a;
            }

            progress.Frame++;
        }
    }

    public static string PooledStateMachines()
    {
        using var w = new FlowWorld();
        var progress = new FrameState();

        async FlowTask Outer() => await FlowTask.WhenAll(Inner(progress), Inner(progress));

        w.Run(Outer());
        var detail = SteadyStateZero(() => Scenarios.Ticks(w, 300));
        Check.That(progress.Frame > 300, "loop progressed");
        return detail;
    }

    public static string SignalSendReceive()
    {
        using var w = new FlowWorld();
        var edge = new Signal<int>();
        var queued = new Signal<int>();
        var sum = 0L;

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

        w.Run(EdgeReceiver());
        w.Run(QueueReceiver());
        w.Run(Sender());
        var detail = SteadyStateZero(() => Scenarios.Ticks(w, 300));
        Check.That(sum > 0, "received");
        return detail;
    }

    public static string RaceWithLeafLosers()
    {
        using var w = new FlowWorld();
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

        w.Run(Loop());
        var detail = SteadyStateZero(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                if (i % 2 == 0) hits.Emit(i);
                w.Tick(Dt);
            }
        });
        Check.That(wins > 0, "signal branch won");
        return detail;
    }

    static async FlowTask SubscribeAndPause(Signal<int> signal, Clock game)
    {
        using var subscription = signal.Subscribe(BufferPolicy.Latest);
        using (game.Pause())
        {
            await FlowTask.NextFrame();
        }
    }

    /// <summary>Lifetime handles created in a child scope per iteration: the child's pooled node recycles its cleanup list.</summary>
    public static string PropertySubscriptionPause()
    {
        using var w = new FlowWorld();
        var property = new FlowProperty<int>();
        var signal = new Signal<int>();
        var game = w.CreateClock("Game");

        async FlowTask Watcher()
        {
            while (true)
            {
                var v = property.Value;
                await property.WaitUntil(v, static (x, old) => x != old);
                await SubscribeAndPause(signal, game);
            }
        }

        w.Run(Watcher());
        var detail = SteadyStateZero(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                property.Set(property.Value + 1);
                w.Tick(Dt);
            }
        }, rounds: 12);
        Check.Equal(0, game.PauseCount, "pauses released");
        return detail;
    }

    /// <summary>
    /// Regression check (fixed in Core): lifetime handles disposed early (using) inside a long-lived scope must not
    /// accumulate in that scope's cleanup list. Before the fix the array doubled forever (16 KB, 32 KB, 64 KB, ...).
    /// Returns the per-round bytes; throws when growth is seen.
    /// </summary>
    public static string HandlesDisposedEarlyInALongLivedScope()
    {
        using var w = new FlowWorld();
        var signal = new Signal<int>();
        var game = w.CreateClock("Game");

        async FlowTask Loop()
        {
            while (true)
            {
                using var subscription = signal.Subscribe(BufferPolicy.Latest);
                using (game.Pause())
                {
                    await FlowTask.NextFrame();
                }
            }
        }

        w.Run(Loop());
        const int rounds = 12;
        var results = new long[rounds];
        for (var r = 0; r < rounds; r++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Scenarios.Ticks(w, 300);
            results[r] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var detail = $"2 handles x 300 iterations per round, bytes/round: {string.Join(", ", results)}";
        var grew = false;
        for (var r = 2; r < rounds; r++) grew |= results[r] != 0;
        if (grew) throw new SmokeFailure(detail);
        return detail;
    }

    // ------------------------------------------------------------------ informational measurements

    static double Micros(long ticks, int count) => ticks * 1_000_000.0 / Stopwatch.Frequency / count;

    public static string MeasureStateMachineLoser()
    {
        const int rounds = 2000;
        double leafUs;
        {
            using var w = new FlowWorld();
            var hits = new Signal<int>();

            async FlowTask Loop()
            {
                using var sub = hits.Subscribe(BufferPolicy.Latest);
                while (true) await FlowTask.Race(FlowTask.WaitForSeconds(100), sub.Next());
            }

            w.Run(Loop());
            for (var i = 0; i < 200; i++)
            {
                hits.Emit(i);
                w.Tick(Dt);
            }

            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < rounds; i++)
            {
                hits.Emit(i);
                w.Tick(Dt);
            }

            leafUs = Micros(Stopwatch.GetTimestamp() - t0, rounds);
        }

        {
            using var w = new FlowWorld();
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

            w.Run(Loop());
            for (var i = 0; i < 200; i++)
            {
                hits.Emit(i);
                w.Tick(Dt);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < rounds; i++)
            {
                hits.Emit(i);
                w.Tick(Dt);
            }

            var us = Micros(Stopwatch.GetTimestamp() - t0, rounds);
            var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)rounds;
            return $"state-machine loser: {bytes:0} B, {us:0.00} us per Emit+Tick; leaf loser (WaitForSeconds): 0 B, {leafUs:0.00} us";
        }
    }

    sealed class Counter
    {
        public int Finally;
    }

    static async FlowTask Suspended(Signal<int> gate, Counter counter)
    {
        try
        {
            await gate.Next();
        }
        finally
        {
            counter.Finally++;
        }
    }

    public static string MeasureTreeCancel()
    {
        const int width = 100;
        const int rounds = 200;
        using var w = new FlowWorld();
        var gate = new Signal<int>();
        var counter = new Counter();
        var children = new FlowTask[width];

        async FlowTask Tree()
        {
            for (var i = 0; i < width; i++) children[i] = Suspended(gate, counter);
            await FlowTask.WhenAll(children);
        }

        // Baseline: the same tree completing normally (no FlowCanceledException thrown).
        (double us, double bytes) Measure(bool cancel)
        {
            for (var i = 0; i < 20; i++) RoundTrip(cancel);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < rounds; i++) RoundTrip(cancel);
            return (Micros(Stopwatch.GetTimestamp() - t0, rounds), (GC.GetAllocatedBytesForCurrentThread() - before) / (double)rounds);
        }

        void RoundTrip(bool cancel)
        {
            var h = w.Run(Tree());
            if (cancel) h.Cancel();
            else gate.Emit(0);
            w.Flush();
            if (!h.IsCompleted) throw new SmokeFailure("tree did not end: " + h.Status);
        }

        var completed = Measure(false);
        counter.Finally = 0;
        var canceled = Measure(true);
        Check.Equal((20 + rounds) * width, counter.Finally, "every finally ran");
        var perScopeUs = (canceled.us - completed.us) / (width + 1);
        var perScopeBytes = (canceled.bytes - completed.bytes) / (width + 1);
        return $"cancel {canceled.us:0.0} us / {canceled.bytes:0} B per tree, complete {completed.us:0.0} us / {completed.bytes:0} B; unwind cost ~{perScopeUs:0.00} us + {perScopeBytes:0} B per scope ({width + 1} throws)";
    }
}
