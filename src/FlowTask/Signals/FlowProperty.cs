namespace Katout.FlowTask;

/// <summary>
/// A value that flows wait on: <see cref="WaitUntil(Func{T, bool}, string, int)"/> completes at once when the condition holds, and
/// otherwise the condition is checked on every change.
/// </summary>
public sealed class FlowProperty<T>
{
    readonly IEqualityComparer<T> _comparer;
    readonly ThreadBinding _binding = new();
    List<Waiter> _waiters = new();
    List<Waiter> _spare = new();
    Signal<T> _changed;
    T _value;
    long _version;          // counts the accepted Sets
    bool _notifying;
    List<Pending> _pending; // Sets made while a change is notified, notified after it in order

    /// <summary>
    /// Creates the property with <paramref name="initial"/>; <paramref name="comparer"/> decides whether a Set changes the
    /// value (the default comparer when null). Made inside a flow, it is bound to that flow's World.
    /// </summary>
    public FlowProperty(T initial = default, IEqualityComparer<T> comparer = null)
    {
        _value = initial;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _binding.BindCurrent(this);
    }

    /// <summary>The current value.</summary>
    public T Value => _value;

    /// <summary>
    /// Emitted after each change; receivers resume later. Shares this property's thread binding. Once closed, it emits no
    /// more, and the property goes on working.
    /// </summary>
    public Signal<T> Changed => _changed ??= new Signal<T>(_binding, "FlowProperty.Changed");

    /// <summary>
    /// Sets the value; nothing happens when it equals the current one. On the World's thread. <see cref="Value"/> changes
    /// at once; a Set made while a change is notified (from a WaitUntil condition, or from code that the notification
    /// unwinds) is notified after it, so every waiter sees the changes in the order they were made.
    /// </summary>
    public void Set(T value)
    {
        _binding.Check("FlowProperty.Set");
        if (_comparer.Equals(_value, value)) return;
        _value = value;
        _version++;
        if (_notifying)
        {
            (_pending ??= new List<Pending>()).Add(new Pending(value, _version));
            return;
        }

        _notifying = true;
        try
        {
            Notify(value, _version);
            for (var i = 0; _pending != null && i < _pending.Count; i++) Notify(_pending[i].Value, _pending[i].Version);
        }
        finally
        {
            _notifying = false;
            _pending?.Clear();
        }
    }

    /// <summary>Evaluates the waiters that started before this change, then emits Changed.</summary>
    void Notify(T value, long version)
    {
        if (_waiters.Count > 0)
        {
            // Waiters that start meanwhile go to a fresh list: this one is never changed while it is walked. A condition
            // that throws fails its wait after the walk, since that can run code that touches this property.
            var current = _waiters;
            _waiters = _spare;
            _spare = null;
            List<(Waiter, Exception)> failures = null;
            foreach (var w in current)
            {
                if (!w.Receiver.IsWaiting(w.Token)) continue;
                if (w.Since >= version)
                {
                    _waiters.Add(w); // it started after this value was set, and compared the newest one
                    continue;
                }

                bool satisfied;
                try
                {
                    satisfied = w.Receiver.Check(value);
                }
#pragma warning disable CA1031 // a WaitUntil condition that throws fails its own wait, not the caller of Set
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    (failures ??= new List<(Waiter, Exception)>()).Add((w, ex));
                    continue;
                }

                if (satisfied) w.Receiver.Satisfy(value, w.Token);
                else _waiters.Add(w);
            }

            current.Clear();
            _spare = current;
            if (failures != null)
            {
                foreach (var (w, ex) in failures) w.Receiver.FailWith(ex, w.Token);
            }
        }

        // Closed by its user: the property goes on without it.
        if (_changed is { IsClosed: false }) _changed.Emit(value);
    }

    /// <summary>Completes with the value once <paramref name="predicate"/> holds, at once when it already does.</summary>
    public FlowTask<T> WaitUntil(Func<T, bool> predicate, [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        var n = PropertyWaitNode<T, Func<T, bool>>.Rent(this, predicate, static (v, p) => p(v));
        n.SetSite(callerFilePath, callerLineNumber);
        return new FlowTask<T>(n, n.Token);
    }

    /// <summary>WaitUntil with explicit state, which needs no closure.</summary>
    public FlowTask<T> WaitUntil<TState>(TState state, Func<T, TState, bool> predicate, [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        var n = PropertyWaitNode<T, TState>.Rent(this, state, predicate);
        n.SetSite(callerFilePath, callerLineNumber);
        return new FlowTask<T>(n, n.Token);
    }

    internal void Bind(FlowWorld world) => _binding.Bind(world, this);

    internal void AddWaiter(IPropertyWaiter<T> waiter, uint token)
    {
        var count = _waiters.Count;
        if (count >= 8 && (count & (count - 1)) == 0) _waiters.RemoveAll(static w => !w.Receiver.IsWaiting(w.Token));
        _waiters.Add(new Waiter(waiter, token, _version));
    }

    /// <summary><c>FlowProperty(value)</c>.</summary>
    public override string ToString() => $"FlowProperty({_value})";

    /// <summary>A wait for the value; <c>Since</c> is the version of the value it compared when it started.</summary>
    readonly record struct Waiter(IPropertyWaiter<T> Receiver, uint Token, long Since);

    readonly record struct Pending(T Value, long Version);
}
