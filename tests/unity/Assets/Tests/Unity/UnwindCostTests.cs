using System;
using System.Diagnostics;
using Katout.FlowTask.Testing.Unity;
using Debug = UnityEngine.Debug;

namespace Katout.FlowTask.Unity.Tests;

/// <summary>
/// Records (does not assert) what losing a Race costs on this runtime: a leaf loser, whose unwinding runs no code, and a
/// state-machine loser, which unwinds by throwing FlowCanceledException on every runtime. The
/// measurement still missing (docs/en/advanced/performance.md) is this one on IL2CPP devices: the Android IL2CPP test player
/// (tools/unity/run-tests.ps1 -Suites AndroidIl2cppBuild) logs it when it runs on a device.
/// </summary>
public class UnwindCostTests
{
    const double Dt = 1.0 / 60;
    const int Rounds = 2000;
    const int Repeats = 7;

    enum Loser
    {
        Leaf,
        StateMachine,
    }

    [Test]
    public void RaceLoserCostIsRecorded()
    {
        var runtime = Application.isEditor ? "Editor" : "Player";
#if ENABLE_IL2CPP
        const string backend = "IL2CPP";
#else
        const string backend = "Mono";
#endif
        var report = $"[FlowTask] Race loser cost on {runtime}/{backend} ({SystemInfo.deviceModel}), median of {Repeats} x {Rounds} Emit+Tick, probe {AllocationProbe.Mode}:";
        foreach (Loser loser in Enum.GetValues(typeof(Loser))) report += "\n  " + Measure(loser);
        Debug.Log(report);
        Assert.Pass(report);
    }

    static string Measure(Loser loser)
    {
        var world = new FlowWorld();
        try
        {
            var hits = new Signal<int>();

            async FlowTask Patrol()
            {
                while (true) await FlowTask.NextFrame();
            }

            async FlowTask Loop()
            {
                using var sub = hits.Subscribe(BufferPolicy.Latest);
                while (true)
                {
                    var first = loser == Loser.Leaf ? FlowTask.WaitForSeconds(100) : Patrol();
                    await FlowTask.Race(first, sub.Next());
                }
            }

            world.Run(Loop());
            var n = 0;
            for (var i = 0; i < 200; i++)
            {
                hits.Emit(n++);
                world.Tick(Dt);
            }

            var micros = new double[Repeats];
            for (var r = 0; r < Repeats; r++)
            {
                var t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < Rounds; i++)
                {
                    hits.Emit(n++);
                    world.Tick(Dt);
                }

                micros[r] = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency / Rounds;
            }

            Array.Sort(micros);
            var bytes = AllocationProbe.MeasureCoarse(() =>
            {
                hits.Emit(n++);
                world.Tick(Dt);
            }, Rounds) / (double)Rounds;
            return $"{loser}: {micros[Repeats / 2]:0.00} us (min {micros[0]:0.00}, max {micros[Repeats - 1]:0.00}), {bytes:0} B per resolution";
        }
        finally
        {
            world.Dispose();
        }
    }
}
