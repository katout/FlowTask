using System;
using System.Diagnostics;
using System.Globalization;

namespace Katout.FlowTask.Benchmarks;

/// <summary>
/// Quick hot-path measurement for A/B comparisons (<c>tools/bench/ab.cs</c>): the SyncChain and Resume shapes of
/// <see cref="AwaitOverhead"/> plus the waits game code uses every frame (NextFrame, WaitForSeconds, signal and subscription
/// waits, Race, WhenAll, completed values), the unwinding of state machines (a Race's losing branch, a canceled chain
/// of scopes), and the shapes a change was measured on (FlowProperty.Set, a FromCallback bridge's callback, a Spawn
/// joined every Tick from a deep scope, and a WhenAll of a list built every Tick), timed
/// with a Stopwatch, best of 7
/// rounds after warm-up. A scenario takes about a second, so two builds can be run alternately many times;
/// BenchmarkDotNet's short job varies more between runs than the differences we look for.
/// <c>hotpath</c> runs every scenario, <c>hotpath "&lt;name&gt;"</c> one, <c>hotpath --list</c> prints the names.
/// Public API only, so the same file measures any published version of FlowTask; versions from before the public API
/// rename (only in the history before the first public commit) use the old names (FlowKit, World, Delay) and need a
/// renamed tree passed to ab.cs with --base-dir.
/// </summary>
internal static class HotPath
{
    const int Chains = 100;
    const int Flows = 100;
    const int Rounds = 7;
    static long s_acc;
    static int s_depth;
    static Signal<int> s_signal;
    static Signal<int> s_other;
    static FlowProperty<int>[] s_properties;
    static EventSignal<int> s_bridge;
    static Action<int> s_callback;
    static readonly FlowTask[] s_four = new FlowTask[4];

    static async FlowTask<int> SyncChain(int n) => n == 1 ? 1 : await SyncChain(n - 1) + 1;

    static async FlowTask SyncDriver()
    {
        while (true)
        {
            await FlowTask.NextFrame();
            for (var i = 0; i < Chains; i++) s_acc += await SyncChain(s_depth);
        }
    }

    static async FlowTask<int> ResumeChain(int n) => n == 1 ? await s_signal.Next() : await ResumeChain(n - 1) + 1;

    static async FlowTask ResumeDriver()
    {
        while (true) s_acc += await ResumeChain(s_depth);
    }

    static async FlowTask NextFrameLoop()
    {
        while (true) await FlowTask.NextFrame();
    }

    static async FlowTask WaitForSecondsLoop()
    {
        while (true) await FlowTask.WaitForSeconds(0.001);
    }

    static async FlowTask SignalLoop()
    {
        while (true) s_acc += await s_signal.Next();
    }

    static async FlowTask RaceLoop()
    {
        // NextFrame wins every Tick; the signal wait loses and is unwound.
        while (true) s_acc += (await FlowTask.Race(s_signal.Next(), FlowTask.NextFrame())).Index;
    }

    static async FlowTask WhenAllLoop()
    {
        while (true) await FlowTask.WhenAll(FlowTask.NextFrame(), FlowTask.NextFrame());
    }

    static async FlowTask LosingBranch() => await FlowTask.Never();

    static async FlowTask RaceStateMachineLoserLoop()
    {
        // NextFrame wins every Tick; the losing branch is a state machine, unwound with one FlowCanceledException throw.
        while (true) s_acc += (await FlowTask.Race(LosingBranch(), FlowTask.NextFrame())).Index;
    }

    static async FlowTask Nested(int n)
    {
        try
        {
            if (n == 1) await FlowTask.Never();
            else await Nested(n - 1);
        }
        finally
        {
            s_acc++;
        }
    }

    static async FlowTask FromResultLoop()
    {
        // Completed values: counted towards the sync-completion limit, 100 per Tick stays under it.
        while (true)
        {
            await FlowTask.NextFrame();
            for (var i = 0; i < Chains; i++) s_acc += await FlowTask.FromResult(i);
        }
    }

    static async FlowTask SubscriptionLoop()
    {
        using var sub = s_signal.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
        while (true) s_acc += await sub.Next();
    }

    static async FlowTask RaceSubscriptionLosesLoop()
    {
        // s_signal is emitted before s_other: its wait wins, the subscription's wait (which also received a value)
        // loses and hands the value back, and the next round takes it synchronously.
        using var sub = s_other.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
        while (true) s_acc += (await FlowTask.Race(s_signal.Next(), sub.Next())).Index;
    }

    static async FlowTask PropertyLoop(FlowProperty<int> property)
    {
        while (true) s_acc += await property.Changed.Next();
    }

    static async FlowTask CallbackOwner()
    {
        // A bridge created in a flow, as game code does; the callback is what the external event source would call.
        s_bridge = FlowBridge.FromCallback<int>(callback =>
        {
            s_callback = callback;
            return () => s_callback = null;
        });
        await FlowTask.Never();
    }

    static async FlowTask CallbackLoop()
    {
        while (true) s_acc += await s_bridge.Next();
    }

    static async FlowTask JoinNest(int n)
    {
        // The join starts n scopes deep: the check for joins that can never complete walks the joiner's ancestors.
        if (n > 1)
        {
            await JoinNest(n - 1);
            return;
        }

        while (true) await Flow.Spawn(FlowTask.NextFrame()).Join();
    }

    static async FlowTask WhenAllListLoop()
    {
        // The list overload, built every Tick: the factory checks every argument before it rents its node.
        while (true)
        {
            for (var i = 0; i < s_four.Length; i++) s_four[i] = FlowTask.NextFrame();
            await FlowTask.WhenAll(s_four);
        }
    }

    /// <summary>Scenario names, in the order they run.</summary>
    internal static readonly string[] Scenarios =
    {
        "SyncChain depth=1", "SyncChain depth=10", "Resume depth=1", "Resume depth=10",
        "NextFrame x100", "WaitForSeconds x100", "Signal.Next x100", "Race x100", "WhenAll(2) x100", "Empty Tick",
        "Race SM loser x100", "Cancel depth=10",
        "FromResult x100", "Subscription.Next x100", "Race(sub loses) x100",
        "FlowProperty.Set x100", "Signal.Emit (no receiver) x100", "FromCallback x100", "Join depth=10 x100", "Join depth=50 x100",
        "WhenAll(list 4) x100",
    };

    /// <summary>
    /// Runs one scenario, or all of them when <paramref name="name"/> is null. ab.cs runs each scenario in its own
    /// process: in a shared process, dynamic PGO specializes shared code (the tick list's virtual calls, for example)
    /// for the node types of the scenarios that ran first, which skews the later ones.
    /// </summary>
    internal static void Run(string name)
    {
        if (name == "--list")
        {
            foreach (var scenario in Scenarios) Console.WriteLine(scenario);
            return;
        }

        foreach (var scenario in Scenarios)
        {
            if (name == null || name == scenario) RunScenario(scenario);
        }

        GC.KeepAlive(s_acc);
    }

    static void RunScenario(string name)
    {
        switch (name)
        {
            case "SyncChain depth=1": SyncChainScenario(name, 1); break;
            case "SyncChain depth=10": SyncChainScenario(name, 10); break;
            case "Resume depth=1": ResumeScenario(name, 1); break;
            case "Resume depth=10": ResumeScenario(name, 10); break;
            case "NextFrame x100": PerFlow(name, NextFrameLoop, emit: false); break;
            case "WaitForSeconds x100": PerFlow(name, WaitForSecondsLoop, emit: false); break;
            case "Signal.Next x100": PerFlow(name, SignalLoop, emit: true); break;
            case "Race x100": PerFlow(name, RaceLoop, emit: false); break;
            case "WhenAll(2) x100": PerFlow(name, WhenAllLoop, emit: false); break;
            case "Empty Tick":
                using (var w = new FlowWorld()) Report(name, Best(200000, () => w.Tick(Bench.Dt)), "ns");
                break;
            case "Race SM loser x100": PerFlow(name, RaceStateMachineLoserLoop, emit: false); break;
            case "Cancel depth=10": CancelScenario(name, 10); break;
            case "FromResult x100": FromResultScenario(name); break;
            case "Subscription.Next x100": PerFlow(name, SubscriptionLoop, emit: true); break;
            case "Race(sub loses) x100": PerFlow(name, RaceSubscriptionLosesLoop, emit: true, emitOther: true); break;
            case "FlowProperty.Set x100": PropertyScenario(name); break;
            case "Signal.Emit (no receiver) x100": EmitNoReceiverScenario(name); break;
            case "FromCallback x100": CallbackScenario(name); break;
            case "Join depth=10 x100": PerFlow(name, () => JoinNest(10), emit: false); break;
            case "Join depth=50 x100": PerFlow(name, () => JoinNest(50), emit: false); break;
            case "WhenAll(list 4) x100": PerFlow(name, WhenAllListLoop, emit: false); break;
            default: throw new ArgumentException("Unknown scenario: " + name);
        }
    }

    static void SyncChainScenario(string name, int depth)
    {
        s_depth = depth;
        using var w = new FlowWorld();
        w.Run(SyncDriver());
        Report(name, Best(20000, () => w.Tick(Bench.Dt)) / Chains, "ns/chain");
    }

    static void ResumeScenario(string name, int depth)
    {
        s_depth = depth;
        using var w = new FlowWorld();
        s_signal = new Signal<int>();
        w.Run(ResumeDriver());
        Report(name, Best(200000, () =>
        {
            s_signal.Emit(1);
            w.Tick(Bench.Dt);
        }), "ns/op (emit+tick)");
    }

    /// <summary>
    /// Starts a chain of <paramref name="depth"/> nested scopes, each with a finally block, and cancels it: one
    /// FlowCanceledException throw per scope. Includes FlowWorld.Run (a root flow is not pooled) and the Flush that unwinds.
    /// </summary>
    static void CancelScenario(string name, int depth)
    {
        using var w = new FlowWorld();
        Report(name, Best(20000, () =>
        {
            var h = w.Run(Nested(depth));
            h.Cancel();
            w.Flush();
        }), "ns/tree");
    }

    static void FromResultScenario(string name)
    {
        using var w = new FlowWorld();
        w.Run(FromResultLoop());
        Report(name, Best(20000, () => w.Tick(Bench.Dt)) / Chains, "ns/await");
    }

    /// <summary>
    /// <see cref="Flows"/> properties, each with a flow waiting on its Changed signal; one Set of each per Tick.
    /// </summary>
    static void PropertyScenario(string name)
    {
        using var w = new FlowWorld();
        s_properties = new FlowProperty<int>[Flows];
        for (var i = 0; i < Flows; i++)
        {
            s_properties[i] = new FlowProperty<int>();
            w.Run(PropertyLoop(s_properties[i]));
        }

        var value = 0;
        Report(name, Best(4000, () =>
        {
            value++;
            for (var i = 0; i < Flows; i++) s_properties[i].Set(value);
            w.Tick(Bench.Dt);
        }) / Flows, "ns/flow/tick");
    }

    /// <summary>
    /// <see cref="Flows"/> signals that nothing waits on or subscribes to, as a game emits events nobody listens to at
    /// the moment; one Emit of each.
    /// </summary>
    static void EmitNoReceiverScenario(string name)
    {
        var signals = new Signal<int>[Flows];
        for (var i = 0; i < Flows; i++) signals[i] = new Signal<int>();
        Report(name, Best(40000, () =>
        {
            for (var i = 0; i < Flows; i++) signals[i].Emit(1);
        }) / Flows, "ns/emit");
    }

    /// <summary><see cref="Flows"/> flows waiting on one FlowBridge.FromCallback bridge; one callback per Tick.</summary>
    static void CallbackScenario(string name)
    {
        using var w = new FlowWorld();
        w.Run(CallbackOwner());
        for (var i = 0; i < Flows; i++) w.Run(CallbackLoop());
        Report(name, Best(4000, () =>
        {
            s_callback(1);
            w.Tick(Bench.Dt);
        }) / Flows, "ns/flow/tick");
    }

    /// <summary>
    /// <see cref="Flows"/> copies of a looping flow; reports one Tick (plus one Emit, and one of the other signal)
    /// divided by the flow count.
    /// </summary>
    static void PerFlow(string name, Func<FlowTask> loop, bool emit, bool emitOther = false)
    {
        using var w = new FlowWorld();
        s_signal = new Signal<int>();
        s_other = new Signal<int>();
        for (var i = 0; i < Flows; i++) w.Run(loop());
        Report(name, Best(4000, () =>
        {
            if (emit) s_signal.Emit(1);
            if (emitOther) s_other.Emit(1);
            w.Tick(Bench.Dt);
        }) / Flows, "ns/flow/tick");
    }

    /// <summary>Nanoseconds per call of <paramref name="op"/>: warm-up (tiering, PGO), then the best of several rounds.</summary>
    static double Best(int ops, Action op)
    {
        // At least half a second of warm-up: tier-up to optimized code starts 100 ms after the first calls.
        var warm = Stopwatch.StartNew();
        do
        {
            for (var i = 0; i < ops; i++) op();
        }
        while (warm.ElapsedMilliseconds < 500);

        var best = double.MaxValue;
        for (var r = 0; r < Rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < ops; i++) op();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / ops);
        }

        return best;
    }

    static void Report(string name, double ns, string unit) =>
        Console.WriteLine(name + ": " + ns.ToString("F1", CultureInfo.InvariantCulture) + " " + unit);
}
