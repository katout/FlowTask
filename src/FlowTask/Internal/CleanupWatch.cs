namespace Katout.FlowTask.Internal;

/// <summary>
/// The canceled scopes that wait in their cleanup: an await of a catch or finally block that FlowCanceledException
/// reached, or a Flow.NonCancelable await. Nothing cancels those awaits, so a cleanup that never ends holds its flow and
/// every scope around it: the first scope still running <see cref="LimitSeconds"/> of unscaled time after it began to
/// wait so is reported (<see cref="FlowWarningKind.LongCleanup"/>, once per World).
/// </summary>
internal sealed class CleanupWatch
{
    internal const double LimitSeconds = 10;

    readonly FlowWorld _world;
    readonly List<Entry> _entries = new();
    bool _warned;

    internal CleanupWatch(FlowWorld world) => _world = world;

    /// <summary>A canceled scope waits in its cleanup; from its first such wait, until it ends.</summary>
    internal void Add(FlowNode scope)
    {
        if (_warned || scope.CleanupWatched) return;
        scope.CleanupWatched = true;
        _entries.Add(new Entry(new NodeRef(scope), _world.UnscaledClock.Time));
    }

    /// <summary>At the end of a Tick.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Check()
    {
        if (_entries.Count > 0) CheckEntries();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void CheckEntries()
    {
        var now = _world.UnscaledClock.Time;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (!e.Scope.IsLive)
            {
                _entries[i] = _entries[^1];
                _entries.RemoveAt(_entries.Count - 1);
                continue;
            }

            if (now - e.Since < LimitSeconds) continue;
            _warned = true;
            _entries.Clear();
            Warn(e.Scope.Node, now - e.Since);
            return;
        }
    }

    void Warn(FlowNode scope, double seconds)
    {
        var paused = scope.Clock.EffectivelyPaused
            ? $" Its clock '{scope.Clock.Name}' is paused, and cleanup follows Pause: run cleanup that must go on during a pause with Flow.WithClock."
            : "";
        _world.Reporter.WarnOnce(
            FlowWarningKind.LongCleanup,
            $"A canceled scope still waits in its cleanup {ClockWait.Format(seconds)}s after it began to (an await of a catch or finally block, or a Flow.NonCancelable await). " +
            "Nothing cancels those awaits, so its flow and every scope that waits for it stay running: bound a cleanup that may not end with FlowTask.Race and a wait." + paused,
            scope);
    }

    readonly record struct Entry(NodeRef Scope, double Since);
}
