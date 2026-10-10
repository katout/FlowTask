namespace Katout.FlowTask;

/// <summary>A value set exactly once. Any number of scopes may wait for it, before or after it is set.</summary>
public sealed class Once<T> : IWaitSource<T>
{
    readonly WaiterList<T> _waiters = new();
    readonly ThreadBinding _binding = new();
    T _value;
    bool _isSet;

    /// <summary>Creates an unset Once; made inside a flow, it is bound to that flow's World.</summary>
    public Once() => _binding.BindCurrent(this);

    /// <summary>True once <see cref="Set"/> was called.</summary>
    public bool IsSet => _isSet;

    /// <summary>The value; throws <see cref="InvalidOperationException"/> before it is set.</summary>
    public T Value => _isSet ? _value : throw new InvalidOperationException("Once has not been set.");

    /// <summary>The value, if it is set.</summary>
    public bool TryGetValue(out T value)
    {
        value = _value;
        return _isSet;
    }

    /// <summary>Sets the value and queues the resume of every waiter. A second call throws.</summary>
    public void Set(T value)
    {
        _binding.Check("Once.Set");
        if (_isSet) throw new FlowMisuseException("Once<T> can be set only once.");
        _value = value;
        _isSet = true;
        _waiters.DeliverAll(value);
    }

    /// <summary>Waits for the value; completes at once when it is set.</summary>
    public FlowTask<T> Wait()
    {
        var n = NextNode<T>.Rent(this, 0);
        return new FlowTask<T>(n, n.Token);
    }

    /// <summary>Awaits <see cref="Wait"/>.</summary>
    public FlowTask<T>.Awaiter GetAwaiter() => Wait().GetAwaiter();

    BeginResult IWaitSource<T>.Begin(IValueReceiver<T> receiver, uint receiverToken, uint sourceToken, FlowWorld world, out T value)
    {
        _binding.Bind(world, this);
        value = _value;
        if (_isSet) return BeginResult.Value;
        _waiters.Add(receiver, receiverToken);
        return BeginResult.Registered;
    }

    string IWaitSource<T>.DescribeSource(uint sourceToken) => "Once<" + typeof(T).Name + ">.Wait";

    Exception IWaitSource<T>.CloseError(uint sourceToken) => null;

    // The Once keeps its value anyway.
    void IWaitSource<T>.Unget(T value, uint sourceToken)
    {
    }

    /// <summary><c>Once(value)</c> or <c>Once(unset)</c>.</summary>
    public override string ToString() => _isSet ? $"Once({_value})" : "Once(unset)";
}
