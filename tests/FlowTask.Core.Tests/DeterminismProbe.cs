using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Katout.FlowTask.Tests;

/// <summary>
/// A busy, fully deterministic scenario (scopes, Race/WhenAll, signals, subscriptions, FlowProperty, Pause, time scale,
/// exceptions caught at several levels, frame and time waits awaited directly) that logs its execution order. Running it
/// with the same seed must produce the same log on every run and on every platform (.NET, Unity Mono/IL2CPP, Godot);
/// compare <see cref="Hash"/> across platforms.
/// </summary>
internal static class DeterminismProbe
{
    /// <summary>Runs the scenario and returns its log (one entry per observable event).</summary>
    public static IReadOnlyList<string> Run(int seed = 42, int ticks = 400)
    {
        var log = new List<string>();
        var rng = new Lcg(seed); // platform-independent PRNG (System.Random differs between runtimes)
        using var world = new FlowWorld();
        world.OnUnhandledException = p => log.Add("report " + p.Kind + " " + p.ScopePath);
        var game = world.CreateClock("Game");
        var sig = new Signal<int>();
        var property = new FlowProperty<int>();

        async FlowTask Member(int id)
        {
            using var sub = sig.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            try
            {
                for (var i = 0; ; i++)
                {
                    var r = await FlowTask.Race(sub.Next(), FlowTask.WaitForSeconds(0.05 * (id % 5 + 1)), property.WaitUntil(id, (v, n) => v == n));
                    log.Add("u" + id + ":" + r.Index);
                    // The value still equals the id: without waiting for a change, the next Race would complete at once, forever.
                    if (r.Index == 2) await property.WaitUntil(id, (v, n) => v != n);
                    if (id % 7 == 3 && i == 4) throw new InvalidOperationException("u" + id);
                    if (id % 11 == 5 && i == 3) throw new InvalidOperationException("stop");
                }
            }
            finally
            {
                log.Add("u" + id + " end");
            }
        }

        async FlowTask Squad(int id)
        {
            try
            {
                await FlowTask.WhenAll(Member(id * 3), Member(id * 3 + 1), Member(id * 3 + 2));
                log.Add("squad" + id + " done");
            }
            catch (InvalidOperationException e) when (e.Message == "stop")
            {
                log.Add("squad" + id + " stopped");
            }
        }

        async FlowTask ContainedSquad(int id)
        {
            try
            {
                await Squad(id);
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                log.Add("squad" + id + " failed: " + e.Message);
            }
        }

        async FlowTask Pacer(int id)
        {
            // Frame and time waits awaited directly: the scope waits in the tick list itself (docs/maintainers/internals.md).
            for (var i = 0; i < 60; i++)
            {
                if (i % 3 == 0) await FlowTask.NextFrame();
                else if (i % 3 == 1) await FlowTask.DelayFrames(id + 1);
                else await FlowTask.WaitForSeconds(0.02 * (id + 1));
                log.Add("p" + id + ":" + i);
            }
        }

        for (var p = 0; p < 3; p++)
            world.Run(Pacer(p), p == 1 ? game : null);

        for (var s = 0; s < 6; s++)
            world.Run(ContainedSquad(s), s % 2 == 0 ? game : null);

        for (var t = 0; t < ticks; t++)
        {
            var roll = rng.Next(10);
            if (roll < 4) sig.Emit(t);
            if (roll == 5) property.Set(rng.Next(20));
            if (t == 100) _ = game.Pause(); // held until the World is disposed: part of the scenario
            if (t == 180) game.TimeScale = 0.5;
            world.Tick(1.0 / 60);
        }

        // Without the places (" at File.cs:42"): the hash compares the execution order, so moving a line of this file must
        // not change it. WaitSiteTests and DiagnosticsReportingTests check the places.
        log.Add(Regex.Replace(world.Dump().Replace("\r", "", StringComparison.Ordinal), @" at [^ \n]+\.cs(:\d+)?", ""));
        return log;
    }

    /// <summary>SHA-256 of the joined log, for cross-platform comparison.</summary>
    public static string Hash(IReadOnlyList<string> log)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", log)));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    struct Lcg
    {
        ulong _state;

        public Lcg(int seed) => _state = (ulong)seed * 6364136223846793005UL + 1442695040888963407UL;

        public int Next(int maxExclusive)
        {
            _state = _state * 6364136223846793005UL + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)maxExclusive);
        }
    }
}
