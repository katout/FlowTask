namespace Katout.FlowTask.Internal;

/// <summary>
/// The cleanups a scope runs when it ends, the last added first: AddCleanup, Own, the lifetime handles it owns (a pause,
/// a subscription, an event bridge) and the clocks it made with Flow.CreateClock. A node creates it at its first
/// cleanup and keeps it while pooled. A class, not a struct field of the node: the runtime places struct fields after
/// the others, which moved the node's hot fields and made a Race 3-4% slower without PGO.
/// </summary>
internal sealed class CleanupStack
{
    CleanupEntry[] _entries = new CleanupEntry[4];
    int _count;

    internal int Count => _count;

    internal void Push(object target, CleanupKind kind, uint token)
    {
        // Lifetime handles disposed early (`using var` in a loop) leave dead entries behind: drop them, so that a
        // long-lived scope does not grow its list without bound.
        while (_count > 0 && IsReleasedOwned(_entries[_count - 1])) _entries[--_count] = default;
        if (_count == _entries.Length)
        {
            Compact();
            if (_count == _entries.Length) Array.Resize(ref _entries, _count * 2);
        }

        _entries[_count++] = new CleanupEntry { Target = target, Kind = kind, Token = token };
    }

    static bool IsReleasedOwned(in CleanupEntry e) =>
        e.Kind == CleanupKind.Owned && !((IScopeOwned)e.Target).IsActive(e.Token);

    void Compact()
    {
        var w = 0;
        for (var i = 0; i < _count; i++)
        {
            if (!IsReleasedOwned(_entries[i])) _entries[w++] = _entries[i];
        }

        Array.Clear(_entries, w, _count - w);
        _count = w;
    }

    internal bool ContainsClock(Clock clock)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_entries[i].Kind == CleanupKind.Clock && ReferenceEquals(_entries[i].Target, clock)) return true;
        }

        return false;
    }

    /// <summary>Runs every cleanup, the last added first. One that throws is a cleanup exception of <paramref name="owner"/>; the others still run.</summary>
    internal void RunAll(FlowNode owner)
    {
        var world = owner.World;
        while (_count > 0)
        {
            var i = --_count;
            var e = _entries[i];
            _entries[i] = default;
            try
            {
                switch (e.Kind)
                {
                    case CleanupKind.Disposable:
                        ((IDisposable)e.Target).Dispose();
                        break;
                    case CleanupKind.Action:
                        ((Action)e.Target)();
                        break;
                    case CleanupKind.Thunk:
                        ((ICleanupThunk)e.Target).Invoke();
                        break;
                    case CleanupKind.Owned:
                        ((IScopeOwned)e.Target).ReleaseByOwner(e.Token);
                        break;
                    case CleanupKind.Clock:
                        world.ClockTree.Remove((Clock)e.Target);
                        break;
                }
            }
#pragma warning disable CA1031 // a failing cleanup is a cleanup exception; the remaining cleanups still run
            catch (Exception ex)
#pragma warning restore CA1031
            {
                world.Reporter.ReportCleanupException(owner, ex);
            }
        }
    }

    /// <summary>Forgets the entries without running them; the array stays for the node's next use.</summary>
    internal void Clear()
    {
        if (_count == 0) return;
        Array.Clear(_entries, 0, _count);
        _count = 0;
    }
}

/// <summary>A lifetime object that a scope owns and releases when it ends (a pause, a subscription, an event bridge).</summary>
internal interface IScopeOwned
{
    bool IsActive(uint token);
    void ReleaseByOwner(uint token);
}

internal interface ICleanupThunk
{
    void Invoke();
}

internal enum CleanupKind : byte
{
    Disposable,
    Action,
    Thunk,
    Owned,
    /// <summary>A scope clock (Flow.CreateClock), removed when its owner ends.</summary>
    Clock,
}

internal struct CleanupEntry
{
    internal object Target;
    internal uint Token;
    internal CleanupKind Kind;
}

internal sealed class CleanupThunk<TState> : ICleanupThunk
{
    static NodePool<CleanupThunk<TState>> s_pool;

    TState _state;
    Action<TState> _action;

    internal static CleanupThunk<TState> Rent(TState state, Action<TState> action)
    {
        var t = s_pool.Rent() ?? new CleanupThunk<TState>();
        t._state = state;
        t._action = action;
        return t;
    }

    public void Invoke()
    {
        var state = _state;
        var action = _action;
        _state = default;
        _action = null;
        s_pool.Return(this);
        action(state);
    }
}

internal static class ScopeOwnership
{
    /// <summary>
    /// Makes the current scope own <paramref name="target"/> and returns that scope, or null outside a flow (the caller
    /// then disposes the handle itself).
    /// </summary>
    internal static FlowNode RegisterOwned(IScopeOwned target, uint token)
    {
        var scope = FlowWorld.t_current?.CurrentScope;
        if (scope is not { State: NodeState.Running }) return null;
        scope.AddCleanup(target, CleanupKind.Owned, token);
        return scope;
    }
}
