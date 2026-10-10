using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Katout.FlowTask.AotSmoke;

internal sealed class Enemy
{
    public readonly Signal<int> Damaged = new();
    public int Position;
    public int Stuns;
}

internal sealed class Resource : IDisposable
{
    readonly List<string> _log;
    readonly string _name;

    public Resource(List<string> log, string name)
    {
        _log = log;
        _name = name;
    }

    public void Dispose() => _log.Add("dispose " + _name);
}

/// <summary>Representative scenarios; each creates its own World and throws <see cref="SmokeFailure"/> on a mismatch.</summary>
internal static class Scenarios
{
    internal const double Dt = 1.0 / 60;

    internal static void Ticks(FlowWorld w, int count)
    {
        for (var i = 0; i < count; i++) w.Tick(Dt);
    }

    internal static void TickFor(FlowWorld w, double seconds)
    {
        var target = w.UnscaledClock.Time + seconds - 1e-9;
        while (w.UnscaledClock.Time < target) w.Tick(Dt);
    }

    static async FlowTask<int> Value(int value, int frames)
    {
        await FlowTask.DelayFrames(frames);
        return value;
    }

    public static string WaitForSeconds()
    {
        using var w = new FlowWorld();
        var resumedAt = -1.0;

        async FlowTask Root()
        {
            await FlowTask.WaitForSeconds(0.5);
            resumedAt = w.DefaultClock.Time;
        }

        var h = w.Run(Root());
        TickFor(w, 1.0);
        Check.Equal(FlowStatus.Succeeded, h.Status, "status");
        Check.That(resumedAt is >= 0.5 and < (0.5 + 1.5 * Dt), $"resumed at {resumedAt}");
        return $"resumed at t={resumedAt:0.000}";
    }

    public static string Race()
    {
        using var w = new FlowWorld();

        async FlowTask<int> Root()
        {
            var leaf = await FlowTask.Race(FlowTask.WaitForSeconds(1.0), FlowTask.NextFrame());
            var tuple = await FlowTask.Race(Value(1, 3), Value(2, 1), Value(3, 2));
            var array = await FlowTask.Race(new[] { Value(10, 2), Value(20, 1) });
            return leaf.Index * 1000 + tuple.Index * 100 + array.Value;
        }

        var h = w.Run(Root());
        Ticks(w, 10);
        Check.Equal(1 * 1000 + 1 * 100 + 20, h.Result, "race results");
        return null;
    }

    public static string WhenAll()
    {
        using var w = new FlowWorld();

        async FlowTask<int> Root()
        {
            var (a, b) = await FlowTask.WhenAll(Value(1, 2), Value(2, 1));
            var all = await FlowTask.WhenAll(new[] { Value(3, 1), Value(4, 3), Value(5, 0) });
            await FlowTask.WhenAll(FlowTask.NextFrame(), FlowTask.WaitForSeconds(0.05));
            return a + b + all[0] + all[1] + all[2];
        }

        var h = w.Run(Root());
        Ticks(w, 12);
        Check.Equal(15, h.Result, "sum");
        return null;
    }

    public static string SignalEdge()
    {
        using var w = new FlowWorld();
        var s = new Signal<int>();
        var log = new List<string>();

        async FlowTask Edge()
        {
            log.Add("edge " + await s.Next());
            while (true)
            {
                var (received, value) = await s.NextOrClosed();
                if (!received) break;
                log.Add("next " + value);
            }

            log.Add("closed");
        }

        w.Run(Edge());
        s.Emit(1);
        s.Emit(2); // the waiter is already satisfied by 1: edge semantics, 2 reaches nobody
        Check.That(log.Count == 0, "Emit must not resume synchronously");
        w.Tick(Dt);
        s.Emit(3);
        w.Tick(Dt);
        s.Close();
        w.Tick(Dt);
        Check.Sequence(log, "edge 1", "next 3", "closed");
        return null;
    }

    public static string Subscription()
    {
        using var w = new FlowWorld();
        var s = new Signal<int>();
        var log = new List<string>();

        async FlowTask Queue()
        {
            using var q = s.Subscribe(BufferPolicy.Queue(3, BufferOverflow.DropOldest));
            using var latest = s.Subscribe(BufferPolicy.Latest);
            await FlowTask.NextFrame();
            while (q.TryTake(out var v)) log.Add("q" + v);
            log.Add("latest" + await latest.Next());
            log.Add("q" + await q.Next()); // waits for the next emit
        }

        w.Run(Queue());
        for (var i = 1; i <= 5; i++) s.Emit(i);
        w.Tick(Dt);
        s.Emit(6);
        w.Tick(Dt);
        Check.Sequence(log, "q3", "q4", "q5", "latest5", "q6");

        // Subscriptions end with their scope: a Queue(1, Fail) subscription that outlived it would overflow here and
        // the unhandled exception would surface from Tick.
        async FlowTask Scoped()
        {
            using var one = s.Subscribe(BufferPolicy.Queue(1, BufferOverflow.Fail));
            await FlowTask.NextFrame();
        }

        w.Run(Scoped());
        w.Tick(Dt);
        s.Emit(7);
        s.Emit(8);
        w.Tick(Dt);
        return null;
    }

    public static string PropertyAndOnce()
    {
        using var w = new FlowWorld();
        var hp = new FlowProperty<int>(10);
        var ready = new Once<string>();
        var log = new List<string>();

        async FlowTask Watch()
        {
            log.Add("hp " + await hp.WaitUntil(v => v <= 0));
            log.Add("once " + await ready);
        }

        async FlowTask Waiter() => log.Add("waiter " + await ready.Wait());

        w.Run(Watch());
        w.Run(Waiter());
        hp.Set(5);
        hp.Set(0);
        w.Tick(Dt);
        ready.Set("go");
        w.Tick(Dt);
        Check.Sequence(log, "hp 0", "waiter go", "once go");
        return null;
    }

    public static string CaughtException()
    {
        using var w = new FlowWorld();
        var log = new List<string>();

        async FlowTask InGame()
        {
            try
            {
                await FlowTask.NextFrame();
                throw new InvalidOperationException("to title");
            }
            finally
            {
                log.Add("finally");
            }
        }

        async FlowTask<string> Game()
        {
            try
            {
                await InGame();
                return "completed";
            }
            catch (InvalidOperationException e)
            {
                return "caught " + e.Message;
            }
        }

        var h = w.Run(Game());
        Ticks(w, 3);
        Check.Equal("caught to title", h.Result, "result");
        Check.Sequence(log, "finally");
        return null;
    }

    public static string UncaughtException()
    {
        using var w = new FlowWorld();
        FlowExceptionInfo report = null;
        w.OnUnhandledException = p => report = p;

        async FlowTask<int> MiniGame()
        {
            await FlowTask.WaitForSeconds(0.1);
            object o = null;
            return o.GetHashCode();
        }

        async FlowTask<int> Arcade() => await MiniGame();

        var h = w.Run(Arcade());
        TickFor(w, 0.3);
        Check.Equal(FlowStatus.Faulted, h.Status, "status");
        Check.That(report != null && report.Exception is NullReferenceException, "OnUnhandledException gets the original exception");
        Check.Equal("Arcade > MiniGame", report.ScopePath, "scope path");
        return "path '" + report.ScopePath + "'";
    }

    public static string CleanupAfterCancel()
    {
        using var w = new FlowWorld();
        var log = new List<string>();

        async FlowTask FadeOut()
        {
            log.Add("fade start");
            await FlowTask.WaitForSeconds(0.1);
            log.Add("fade end");
        }

        async FlowTask<int> Load()
        {
            await FlowTask.WaitForSeconds(0.1);
            return 7;
        }

        async FlowTask Screen()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                // After the cancel reached this code, its awaits run to their end.
                await FadeOut();
                log.Add("loaded " + await Load());
            }
        }

        var h = w.Run(Screen());
        w.Tick(Dt);
        h.Cancel();
        w.Tick(Dt);
        Check.Equal(FlowStatus.Running, h.Status, "still running its cleanup");
        TickFor(w, 0.5);
        Check.Sequence(log, "fade start", "fade end", "loaded 7");
        Check.Equal(FlowStatus.Canceled, h.Status, "status");
        return null;
    }

    public static string CancellationWithFinally()
    {
        using var w = new FlowWorld();
        var log = new List<string>();

        async FlowTask Inner()
        {
            Flow.AddCleanup(() => log.Add("cleanup inner"));
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                log.Add("finally inner");
            }
        }

        async FlowTask Outer()
        {
            Flow.Own(new Resource(log, "outer"));
            try
            {
                await Inner();
            }
            finally
            {
                log.Add("finally outer");
            }
        }

        var h = w.Run(Outer());
        w.Tick(Dt);
        h.Cancel();
        w.Tick(Dt);
        Check.Sequence(log, "finally inner", "cleanup inner", "finally outer", "dispose outer");
        Check.Equal(FlowStatus.Canceled, h.Status, "status");
        Check.Equal(CancelCause.Explicit, h.CancelCause, "cause");
        return null;
    }

    public static string SpawnAndJoin()
    {
        using var w = new FlowWorld();
        var log = new List<string>();

        async FlowTask<int> Child(int v)
        {
            await FlowTask.DelayFrames(v);
            return v * 10;
        }

        async FlowTask Background()
        {
            try
            {
                while (true) await FlowTask.NextFrame();
            }
            finally
            {
                log.Add("background unwound with parent");
            }
        }

        async FlowTask<int> Parent()
        {
            _ = Flow.Spawn(Background());
            var a = Flow.Spawn(Child(2));
            var b = Flow.Spawn(Child(1));
            return await a.Join() + await b.Join();
        }

        var h = w.Run(Parent());
        Ticks(w, 4);
        Check.Equal(30, h.Result, "joined");
        Check.Sequence(log, "background unwound with parent");
        return null;
    }

    public static string Pause()
    {
        using var w = new FlowWorld();
        var game = w.CreateClock("Game");
        var done = false;

        async FlowTask Waiter()
        {
            await FlowTask.WaitForSeconds(0.5);
            done = true;
        }

        async FlowTask Pauser()
        {
            using (game.Pause())
            {
                await FlowTask.WaitForSeconds(1.0, w.UnscaledClock);
            }
        }

        w.Run(Waiter(), game);
        w.Run(Pauser());
        TickFor(w, 0.9);
        Check.That(!done && game.PauseCount > 0, "game clock paused");
        TickFor(w, 0.7);
        Check.That(done && game.PauseCount == 0, "resumed after the pause was released");
        return $"game t={game.Time:0.00} unscaled t={w.UnscaledClock.Time:0.00}";
    }

    static async FlowTask EnemyAI(Enemy self)
    {
        using var hits = self.Damaged.Subscribe(BufferPolicy.Latest);
        while (true)
        {
            var r = await FlowTask.Race(Patrol(self), hits.Next());
            if (r.Index == 1) await HitStun(self);
        }
    }

    static async FlowTask Patrol(Enemy self)
    {
        while (true)
        {
            self.Position++;
            await FlowTask.NextFrame();
        }
    }

    static async FlowTask HitStun(Enemy self)
    {
        self.Stuns++;
        await FlowTask.WaitForSeconds(0.8);
    }

    public static string EnemyAI()
    {
        using var w = new FlowWorld();
        var enemy = new Enemy();
        w.Run(EnemyAI(enemy));
        TickFor(w, 0.5);
        var patrolled = enemy.Position;
        Check.That(patrolled > 0, "patrols");
        enemy.Damaged.Emit(10);
        TickFor(w, 0.3);
        Check.Equal(patrolled, enemy.Position, "no patrol during the stun");
        enemy.Damaged.Emit(10); // during the stun: kept in the Latest buffer
        TickFor(w, 1.0);
        Check.Equal(2, enemy.Stuns, "stuns");
        Check.That(w.Dump().Contains("HitStun"), "stunned again");
        return $"position {enemy.Position}, stuns {enemy.Stuns}";
    }

    sealed class Button
    {
        public event Action<int> Clicked;
        public int Listeners => Clicked?.GetInvocationList().Length ?? 0;
        public void Click(int n) => Clicked?.Invoke(n);
    }

    public static string Bridges()
    {
        using var w = new FlowWorld();
        var button = new Button();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var worldThread = Environment.CurrentManagedThreadId;

        async FlowTask Root()
        {
            var clicks = FlowBridge.FromCallback<int>(emit =>
            {
                button.Clicked += emit;
                return () => button.Clicked -= emit;
            });
            log.Add("click " + await clicks.Next());
            log.Add("task " + await tcs.Task.AsFlow() + (Environment.CurrentManagedThreadId == worldThread ? " on world thread" : " on WRONG thread"));
            log.Add("sync " + await FlowBridge.FromTask(_ => Task.FromResult(7)));
        }

        var h = w.Run(Root());
        button.Click(4);
        w.Tick(Dt);
        Task.Run(() => tcs.SetResult(9)).Wait();
        SpinWait.SpinUntil(() => false, 20);
        w.Tick(Dt);
        Check.Sequence(log, "click 4", "task 9 on world thread", "sync 7");
        Check.Equal(FlowStatus.Succeeded, h.Status, "status");
        Check.Equal(0, button.Listeners, "event handler detached with the scope");
        return null;
    }

    public static string UnhandledException()
    {
        using var w = new FlowWorld();
        var reports = new List<FlowExceptionInfo>();
        w.OnUnhandledException = reports.Add;

        async FlowTask Broken()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("boom");
        }

        w.Run(Broken());
        w.Tick(Dt);
        Check.Equal(1, reports.Count, "reports");
        Check.That(reports[0].Exception is InvalidOperationException, "original exception");
        Check.Equal("Broken", reports[0].ScopePath, "path");
        return null;
    }

    /// <summary>
    /// An unbridged await is an unhandled exception: the builders tell the library's awaiters from foreign ones by the
    /// awaiter type (computed once per type with reflection on generic types), which must hold after AOT compilation.
    /// </summary>
    public static string UnbridgedAwait()
    {
        using var w = new FlowWorld();
        var reports = new List<FlowExceptionInfo>();
        w.OnUnhandledException = reports.Add;
        var tcs = new TaskCompletionSource<int>();
        var log = new List<string>();

        async FlowTask Root()
        {
            await FlowTask.NextFrame(); // FlowTask.Awaiter
            log.Add("value " + await Value(3, 1)); // FlowTask<int>.Awaiter
#pragma warning disable FLOW002 // on purpose: an unbridged await fails at the await
            await tcs.Task;
#pragma warning restore FLOW002
            log.Add("unreachable");
        }

        var h = w.Run(Root());
        Ticks(w, 3);
        tcs.SetResult(1);
        Ticks(w, 2);
        Check.Sequence(log, "value 3");
        Check.Equal(FlowStatus.Faulted, h.Status, "status");
        Check.Equal(1, reports.Count, "reports");
        var message = reports[0].Exception.Message;
        Check.That(reports[0].Exception is FlowMisuseException && message.Contains("TaskAwaiter<Int32>", StringComparison.Ordinal), "unbridged-await exception: " + message);
        var site = message[(message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim();
        return "await site: " + site;
    }

    public static string Dump()
    {
        using var w = new FlowWorld();
        var enemy = new Enemy();
        w.Run(EnemyAI(enemy));
        w.Tick(Dt);
        var dump = w.Dump();
        Check.That(dump.Contains("EnemyAI") && dump.Contains("Patrol") && dump.Contains("NextFrame"), "dump:\n" + dump);

        // The declaring type and the source method name (for the Scope Tree window) come from the state machine type
        // (Type.DeclaringType).
        var found = false;
        foreach (var s in w.Diagnostics.Walk())
        {
            if (s.Name != "Patrol") continue;
            found = true;
            Check.Equal(nameof(Scenarios), s.DeclaringType?.Name, "declaring type");
            Check.Equal("Patrol", s.MethodName, "method name");
        }

        Check.That(found, "Patrol scope found");
        return null;
    }
}
