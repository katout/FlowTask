using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace Katout.FlowTask.AotSmoke;

/// <summary>
/// FlowWorld.SkipUnwinding under NativeAOT: the same tree is canceled in a World that unwinds it and in one that skips
/// the unwinding of <see cref="SkipFlows"/>. Both end it the same way (statuses, cleanup order); the second throws no
/// FlowCanceledException.
/// </summary>
internal static class SkipUnwindingScenario
{
    static List<string> s_log = new();
    static int s_throws;

    static class SkipFlows
    {
        static async FlowTask Waiter(string name)
        {
            Flow.AddCleanup(() => s_log.Add(name + " cleanup"));
            await FlowTask.DelayFrames(1000);
        }

        static async FlowTask Child(string name)
        {
            Flow.AddCleanup(() => s_log.Add(name + " cleanup"));
            await FlowTask.Never();
        }

        internal static async FlowTask Owner()
        {
            Flow.AddCleanup(() => s_log.Add("owner cleanup"));
            _ = Flow.Spawn(Waiter("spawned"));
            await Child("child");
        }

        internal static async FlowTask<int> Loser()
        {
            await Child("loser");
            return 1;
        }

        internal static async FlowTask<int> RaceRoot()
        {
            var r = await FlowTask.Race(Loser(), FlowTask.DelayFrames(2));
            return r.Index;
        }
    }

    static void Count(object sender, FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is FlowCanceledException) s_throws++;
    }

    static (string Log, int Throws) RunOnce(bool skip)
    {
        s_log = new List<string>();
        s_throws = 0;
        using var w = new FlowWorld();
        if (skip) w.SkipUnwinding(typeof(SkipFlows));
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            var owner = w.Run(SkipFlows.Owner());
            var race = w.Run(SkipFlows.RaceRoot());
            Scenarios.Ticks(w, 1);
            owner.Cancel();
            Scenarios.Ticks(w, 3);
            s_log.Add($"owner {owner.Status} {owner.CancelCause}");
            s_log.Add($"race {race.Status} {race.Result}");
            var disposed = w.Run(SkipFlows.Owner());
            Scenarios.Ticks(w, 1);
            w.Dispose();
            s_log.Add($"disposed {disposed.Status} {disposed.CancelCause}");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }

        return (string.Join(", ", s_log), s_throws);
    }

    public static string Run()
    {
        var unwound = RunOnce(skip: false);
        var skipped = RunOnce(skip: true);
        Check.Equal(unwound.Log, skipped.Log, "the same end with and without SkipUnwinding");
        Check.Equal(
            "child cleanup, spawned cleanup, owner cleanup, loser cleanup, owner Canceled Explicit, race Succeeded 1, child cleanup, spawned cleanup, owner cleanup, disposed Canceled WorldDisposed",
            skipped.Log, "log");
        Check.That(skipped.Throws == 0, $"{skipped.Throws} FlowCanceledException thrown without unwinding (unwinding: {unwound.Throws})");
        return $"same end; FlowCanceledException thrown: unwinding {unwound.Throws}, skipping 0";
    }
}
