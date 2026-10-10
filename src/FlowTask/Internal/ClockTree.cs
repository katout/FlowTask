namespace Katout.FlowTask.Internal;

/// <summary>Step 2 of a Tick: the World's live clocks, parents before children, and their pause and time scale.</summary>
internal sealed class ClockTree
{
    readonly FlowWorld _world;
    readonly ResumeQueue _resumes;
    readonly List<Clock> _clocks = new();

    internal ClockTree(FlowWorld world, ResumeQueue resumes)
    {
        _world = world;
        _resumes = resumes;
    }

    internal IReadOnlyList<Clock> Clocks => _clocks;

    internal Clock Add(Clock clock)
    {
        _clocks.Add(clock);
        return clock;
    }

    /// <summary>FlowWorld.CreateClock: a clock that lives as long as the World.</summary>
    internal Clock CreateWorldClock(string name, Clock parent)
    {
        CheckParent(parent);
        if (parent.IsScoped) throw new FlowMisuseException($"Clock '{parent.Name}' belongs to scope '{parent.OwnerName}' and is removed when it ends: it cannot be the parent of a World clock.");
        return Add(new Clock(_world, name ?? "Clock", parent, unscaled: false));
    }

    /// <summary>Flow.CreateClock: a clock that <paramref name="scope"/> owns, removed when the scope ends.</summary>
    internal Clock CreateScopeClock(FlowNode scope, string name, Clock parent)
    {
        CheckParent(parent);
        if (parent.IsScoped && !IsOwnedByScopeOrAncestor(scope, parent))
            throw new FlowMisuseException($"Clock '{parent.Name}' belongs to scope '{parent.OwnerName}', which is not this scope or an ancestor, so it may be removed first. Use a clock of this scope, of an ancestor, or of the World as the parent.");
        var clock = Add(new Clock(_world, name ?? "Clock", parent, unscaled: false) { OwnerName = scope.DisplayName });
        scope.AddCleanup(clock, CleanupKind.Clock);
        return clock;
    }

    void CheckParent(Clock parent)
    {
        if (parent.World != _world) throw new ArgumentException("The parent clock belongs to another World.", nameof(parent));
        if (parent.IsRemoved) throw Errors.RemovedClock(parent);
    }

    static bool IsOwnedByScopeOrAncestor(FlowNode scope, Clock clock)
    {
        for (var n = scope; n != null; n = n.Parent)
        {
            if (n.OwnsClock(clock)) return true;
        }

        return false;
    }

    /// <summary>
    /// Takes a scope clock out of the Tick for good. It stays paused: step 3 fails the waits still on it, and starting a
    /// task on it, Pause and TimeScale throw. Its resumes are not held.
    /// </summary>
    internal void Remove(Clock clock)
    {
        _clocks.Remove(clock);
        clock.LiveWorld = null;
        clock.DeltaTime = 0;
        clock.EffectivelyPaused = true;
        _resumes.OnPauseReleased();
    }

    internal void Recompute()
    {
        foreach (var c in _clocks)
        {
            var wasPaused = c.EffectivelyPaused;
            var p = c.Parent;
            c.EffectiveScale = (p?.EffectiveScale ?? 1.0) * c.TimeScale;
            c.EffectivelyPaused = c.IsPaused || (p != null && p.EffectivelyPaused);
            if (wasPaused && !c.EffectivelyPaused) _resumes.OnPauseReleased();
        }
    }

    /// <summary>False when a scale product overflowed: Clock.TimeScale then restores the previous value.</summary>
    internal bool AllScalesFinite()
    {
        foreach (var c in _clocks)
        {
            if (double.IsInfinity(c.EffectiveScale)) return false;
        }

        return true;
    }

    internal void Advance(double dt)
    {
        foreach (var c in _clocks)
        {
            if (c.EffectivelyPaused)
            {
                c.DeltaTime = 0;
                continue;
            }

            var d = dt * c.EffectiveScale;
            c.Time += d;
            c.DeltaTime = d;
            c.FrameCount++;
        }
    }
}
