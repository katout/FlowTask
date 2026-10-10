using System.Text;

namespace Katout.FlowTask;

/// <summary>
/// A time line. Waits advance with the clock of their scope, not with real time. Clocks form a tree: a parent's pause and
/// time scale apply to its children.
/// </summary>
public sealed class Clock
{
    readonly bool _unscaled;
    double _timeScale = 1.0;
    List<Hold> _pauses; // the live pauses of this clock itself, in the order taken (dumps say who holds them)

    internal Clock(FlowWorld world, string name, Clock parent, bool unscaled)
    {
        World = world;
        LiveWorld = world;
        Name = name;
        Parent = parent;
        _unscaled = unscaled;
        EffectiveScale = parent?.EffectiveScale ?? 1.0;
        EffectivelyPaused = parent?.EffectivelyPaused ?? false;
    }

    /// <summary>The World the clock belongs to.</summary>
    public FlowWorld World { get; }

    /// <summary>The name in dumps and pauses.</summary>
    public string Name { get; }

    /// <summary>The clock whose pause and time scale this one follows; null for UnscaledClock and DefaultClock.</summary>
    public Clock Parent { get; }

    /// <summary>The time of this clock in seconds.</summary>
    public double Time { get; internal set; }

    /// <summary>The time the last Tick advanced; 0 while paused.</summary>
    public double DeltaTime { get; internal set; }

    /// <summary>The Ticks in which neither this clock nor an ancestor was paused (a TimeScale of 0 does not stop it).</summary>
    public long FrameCount { get; internal set; }

    /// <summary>
    /// Multiplied with the parent's. Finite and non-negative, and so must its product with every ancestor's be;
    /// <see cref="ArgumentOutOfRangeException"/> otherwise. The UnscaledClock's cannot be set.
    /// </summary>
    public double TimeScale
    {
        get => _timeScale;
        set
        {
            if (_unscaled) throw new FlowMisuseException($"Clock '{Name}' is the UnscaledClock: its TimeScale cannot be changed.");
            if (LiveWorld == null) throw Errors.RemovedClock(this);
            if (!(value >= 0) || double.IsInfinity(value)) throw InvalidTimeScale(value);
            World.CheckThread();
            var previous = _timeScale;
            _timeScale = value;
            World.ClockTree.Recompute();
            if (World.ClockTree.AllScalesFinite()) return;
            _timeScale = previous;
            World.ClockTree.Recompute();
            throw InvalidTimeScale(value);
        }
    }

    ArgumentOutOfRangeException InvalidTimeScale(double value) =>
        new(nameof(value), value, $"The TimeScale of clock '{Name}' must be finite and non-negative, and so must its product with the parent clocks' scales.");

    internal bool IsPaused => _pauses is { Count: > 0 };

    /// <summary>True while this clock or an ancestor is paused, and for a removed clock of Flow.CreateClock.</summary>
    public bool IsPausedInHierarchy => EffectivelyPaused;

    /// <summary>The live Pause handles of this clock itself.</summary>
    public int PauseCount => _pauses?.Count ?? 0;

    internal bool EffectivelyPaused;
    internal double EffectiveScale;

    /// <summary>The World a task may run on this clock in; null once a scope clock is removed.</summary>
    internal FlowWorld LiveWorld;

    /// <summary>Flow.CreateClock: the owner scope's name. Null for the World's clocks.</summary>
    internal string OwnerName;

    internal bool IsScoped => OwnerName != null;
    internal bool IsRemoved => LiveWorld == null;

    /// <summary>
    /// Pauses the clock and its children until every handle is disposed. The current scope owns the handle and releases it
    /// when it ends; dispose it earlier with <c>using</c>. Outside a flow nothing owns the handle: dispose it yourself, or
    /// the clock stays paused. A flow that pauses the clock it runs on stops itself until the pause is released
    /// (<see cref="FlowWarningKind.PausedOwnClock"/>): run it on another clock with Flow.WithClock.
    /// </summary>
    public ScopedHandle Pause()
    {
        if (_unscaled) throw new FlowMisuseException($"Clock '{Name}' is the UnscaledClock and cannot be paused. Pause DefaultClock or a clock made with CreateClock.");
        if (LiveWorld == null) throw Errors.RemovedClock(this);
        World.CheckThread();
        WarnIfPausingOwnClock();
        var hold = Hold.Rent(this);
        (_pauses ??= new List<Hold>()).Add(hold);
        World.ClockTree.Recompute();
        hold.SetOwner(ScopeOwnership.RegisterOwned(hold, hold.Token));
        return new ScopedHandle(hold, hold.Token);
    }

    void WarnIfPausingOwnClock()
    {
        var world = FlowWorld.t_current;
        if (!ReferenceEquals(world, World) || world.CurrentScope is not { } scope) return;
        for (var c = scope.Clock; c != null; c = c.Parent)
        {
            if (!ReferenceEquals(c, this)) continue;
            world.Reporter.WarnOnce(FlowWarningKind.PausedOwnClock,
                $"The flow paused clock '{Name}', which it runs on: its own resumes wait until the pause is released. Run the pausing flow on another clock (Flow.WithClock(ui, ...)).", scope);
            return;
        }
    }

    /// <summary>Who pauses this clock, for dumps: "paused x2 by Game &gt; Menu, &lt;outside any flow&gt;", or "paused via Game".</summary>
    internal string DescribePause()
    {
        if (!EffectivelyPaused) return null;
        if (LiveWorld == null) return "removed (its scope '" + OwnerName + "' ended)";
        var sb = new StringBuilder();
        if (IsPaused)
        {
            sb.Append("paused x").Append(_pauses.Count).Append(" by ");
            for (var i = 0; i < _pauses.Count; i++) sb.Append(i > 0 ? ", " : "").Append(_pauses[i].DescribeHolder());
        }

        var via = Parent;
        while (via != null && !via.IsPaused) via = via.Parent;
        if (via != null) sb.Append(sb.Length > 0 ? "; also paused via " : "paused via ").Append(via.Name);
        return sb.ToString();
    }

    /// <summary><c>Clock(Name, t=Time)</c>, and whether it is paused or removed.</summary>
    public override string ToString() => $"Clock({Name}, t={Time:0.###}{(LiveWorld == null ? ", removed" : EffectivelyPaused ? ", paused" : "")})";

    /// <summary>A pause: the scope that took it owns it and releases it when it ends. Pooled; the token tells stale handles apart.</summary>
    sealed class Hold : IScopeOwned
    {
        static NodePool<Hold> s_pool;

        internal uint Token = 1;
        Clock _clock;
        FlowNode _owner;
        uint _ownerToken;

        internal static Hold Rent(Clock clock)
        {
            var h = s_pool.Rent() ?? new Hold();
            h._clock = clock;
            return h;
        }

        internal void SetOwner(FlowNode owner)
        {
            _owner = owner;
            _ownerToken = owner?.Token ?? 0;
        }

        internal string DescribeHolder() =>
            _owner == null ? "<outside any flow>" : _owner.Token == _ownerToken ? _owner.BuildScopePath() : "<ended scope>";

        public bool IsActive(uint token) => token == Token && _clock != null;

        public void ReleaseByOwner(uint token)
        {
            var clock = _clock;
            if (token != Token || clock == null) return;
            clock.World.CheckThread();
            unchecked
            {
                if (++Token == 0) Token = 1;
            }

            _clock = null;
            _owner = null;
            clock._pauses.Remove(this);
            clock.World.ClockTree.Recompute();
            s_pool.Return(this);
        }
    }
}
