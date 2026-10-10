namespace Katout.FlowTask;

/// <summary>
/// A signal fed by an external event (<see cref="FlowBridge.FromCallback{T}"/>). The scope that created it owns it: when
/// the scope ends, or on Dispose, the handler is detached and the signal closed, so a flow waiting on <see cref="Next"/>
/// throws <see cref="SignalClosedException"/> instead of waiting forever, <see cref="NextOrClosed"/> returns
/// <c>(false, default)</c>, and a subscription hands out its buffered values first.
/// </summary>
[LifetimeHandle]
public sealed class EventSignal<T> : IDisposable, IScopeOwned
{
    const string DisposedReason = "the event bridge was disposed (its scope ended, or Dispose was called; create it in a scope that outlives its waiters)";

    readonly Signal<T> _signal;
    Action _detach;
    int _disposed; // read by callbacks on any thread

    /// <summary>Made inside a flow, the signal is bound to its World; otherwise to the first World that uses it.</summary>
    internal EventSignal(string name) => _signal = new Signal<T>(name);

    /// <summary>The signal the events are emitted on, to pass where a <see cref="Signal{T}"/> is expected.</summary>
    public Signal<T> Signal => _signal;

    /// <summary>True once the handler is detached and the signal closed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Waits for the next event (an edge); to keep the events raised meanwhile, <see cref="Subscribe"/>.</summary>
    public FlowTask<T> Next() => _signal.Next();

    /// <summary>Like <see cref="Next"/>, but <c>(true, value)</c> for an event and <c>(false, default)</c> once the signal is closed.</summary>
    public FlowTask<(bool Received, T Value)> NextOrClosed() => _signal.NextOrClosed();

    /// <summary>Buffers the events from now on; see <see cref="Signal{T}.Subscribe"/>.</summary>
    public Subscription<T> Subscribe(BufferPolicy policy) => _signal.Subscribe(policy);

    internal void SetDetach(Action detach) => _detach = detach;

    internal void RegisterWithCurrentScope() => ScopeOwnership.RegisterOwned(this, 0);

    internal void Emit(T value)
    {
        // A callback after the bridge was disposed or its signal closed reaches nobody.
        if (IsDisposed || _signal.IsClosed) return;
        // The World the signal is bound to now: a bridge made outside a flow is bound later, by its first waiter. On another
        // thread the value goes through that World's inbox. A signal no World uses yet is a plain object, unchecked like
        // any other: the value goes to its subscriptions made outside a flow, if any.
        var world = _signal.BoundWorldForAnyThread;
        if (world == null || world.IsWorldThread) _signal.Emit(value);
        else _signal.EmitFromAnyThread(value);
    }

    /// <summary>
    /// Detaches the handler and closes the signal, once, from any thread: on another thread than the World's, the close is
    /// applied in the World's next intake.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var detach = Interlocked.Exchange(ref _detach, null);
        try
        {
            detach?.Invoke();
        }
        finally
        {
            // Nothing emits any more: a flow still waiting would wait forever.
            _signal.CloseByOwner(DisposedReason);
        }
    }

    bool IScopeOwned.IsActive(uint token) => !IsDisposed;

    void IScopeOwned.ReleaseByOwner(uint token) => Dispose();

    /// <summary><c>EventSignal(name)</c>.</summary>
    public override string ToString() => $"EventSignal({_signal})";
}
