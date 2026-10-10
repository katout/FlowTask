namespace Katout.FlowTask.Internal;

/// <summary>The buffer behind a <see cref="Subscription{T}"/>: a ring of the signal's emits. Pooled; the token tells stale copies apart.</summary>
internal sealed class SubscriptionCore<T> : IScopeOwned, IWaitSource<T>
{
    static NodePool<SubscriptionCore<T>> s_pool;

    internal uint Token = 1;
    internal FlowNode Owner;
    internal uint OwnerToken;
    readonly WaiterList<T> _waiters = new();
    Signal<T> _signal;
    T[] _buffer; // a ring over its whole length; grown on demand up to the capacity, kept when pooled
    int _head;
    int _count;
    int _capacity;
    BufferOverflow _overflow;
    bool _sourceClosed;
    bool _pendingOverflow;

    internal int Count => _count;

    internal void CheckThread(string what) => _signal?.CheckThread(what);

    internal static SubscriptionCore<T> Rent(Signal<T> signal, BufferPolicy policy, bool closed)
    {
        var c = s_pool.Rent() ?? new SubscriptionCore<T>();
        c._signal = signal;
        c._capacity = policy.Capacity;
        c._overflow = policy.Overflow;
        c._head = 0;
        c._count = 0;
        c._sourceClosed = closed;
        c._pendingOverflow = false;
        return c;
    }

    /// <summary>Takes an emit; true when a Fail policy overflowed (raised after the emit loop).</summary>
    internal bool Push(T value)
    {
        if (_count == 0 && _waiters.Count > 0 && _waiters.DeliverFirst(value)) return false;
        if (_count < _capacity)
        {
            var buffer = _buffer;
            if (buffer == null || _count == buffer.Length) buffer = Grow(_count + 1);
            buffer[(_head + _count) % buffer.Length] = value;
            _count++;
            return false;
        }

        switch (_overflow)
        {
            case BufferOverflow.DropOldest:
                // The oldest goes and the value becomes the newest; the ring may be longer than the count.
                var ring = _buffer;
                ring[_head] = default;
                _head = (_head + 1) % ring.Length;
                ring[(_head + _count - 1) % ring.Length] = value;
                return false;
            case BufferOverflow.DropNewest:
                return false;
            default:
                _pendingOverflow = true;
                return true;
        }
    }

    /// <summary>A Fail policy overflowed: the owner scope is canceled carrying a SubscriptionOverflowException.</summary>
    internal void RaisePendingOverflow()
    {
        if (!_pendingOverflow) return;
        _pendingOverflow = false;
        var owner = Owner;
        if (owner == null || owner.Token != OwnerToken || owner.State != NodeState.Running) return;
        var ex = new SubscriptionOverflowException(
            $"The subscription to '{_signal}' overflowed: its queue (capacity {_capacity}) was full (BufferOverflow.Fail). Take the values faster, raise the capacity, or choose DropOldest or DropNewest.");
        var world = owner.World;
        if (!owner.IsLive)
        {
            world.Reporter.ReportCleanupException(owner, ex);
            return;
        }

        Unwinder.CarryFault(owner, FailureRecord.ForFault(ex, owner.BuildScopePath()), null);
        world.Unwinder.UnwindOrDefer(owner);
    }

    internal bool TryTake(out T value)
    {
        if (_count == 0)
        {
            value = default;
            return false;
        }

        var buffer = _buffer;
        value = buffer[_head];
        buffer[_head] = default;
        _head = (_head + 1) % buffer.Length;
        _count--;
        return true;
    }

    /// <summary>
    /// A value handed to a wait that was released before it was received goes back to the front: it is older than what
    /// was buffered since. When the buffer is full, the policy decides as if it had stayed: DropOldest (and Latest) drops
    /// it, DropNewest drops the newest, Fail keeps it beyond the capacity.
    /// </summary>
    public void Unget(T value, uint sourceToken)
    {
        if (sourceToken != Token || _signal == null) return;
        if (_count == 0 && _waiters.Count > 0 && _waiters.DeliverFirst(value)) return;
        if (_count >= _capacity)
        {
            if (_overflow == BufferOverflow.DropOldest) return;
            if (_overflow == BufferOverflow.DropNewest)
            {
                _buffer[(_head + _count - 1) % _buffer.Length] = default;
                _count--;
            }
        }

        var buffer = _buffer;
        if (buffer == null || _count == buffer.Length) buffer = Grow(_count + 1);
        _head = (_head + buffer.Length - 1) % buffer.Length;
        buffer[_head] = value;
        _count++;
    }

    /// <summary>Makes room for <paramref name="needed"/> values: doubles from 4 up to the capacity, oldest first.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    T[] Grow(int needed)
    {
        var old = _buffer;
        var oldLength = old?.Length ?? 0;
        var length = (int)Math.Min(oldLength == 0 ? 4L : oldLength * 2L, _capacity);
        if (length < needed) length = needed;
        var buffer = new T[length];
        if (_count > 0)
        {
            var first = Math.Min(_count, oldLength - _head);
            Array.Copy(old, _head, buffer, 0, first);
            Array.Copy(old, 0, buffer, first, _count - first);
        }

        _buffer = buffer;
        _head = 0;
        return buffer;
    }

    /// <summary>The signal closed: the waiters are told once the buffer is drained.</summary>
    internal void OnSourceClosed(uint token, string reason)
    {
        if (token != Token) return;
        _sourceClosed = true;
        if (_count == 0) _waiters.CloseAll(reason);
    }

    public BeginResult Begin(IValueReceiver<T> receiver, uint receiverToken, uint sourceToken, FlowWorld world, out T value)
    {
        if (sourceToken != Token)
        {
            value = default;
            return BeginResult.Disposed;
        }

        _signal.BindForSubscription(world);
        if (TryTake(out value)) return BeginResult.Value;
        // The signal too: one closed from another thread before any World used it is only marked closed.
        if (_sourceClosed || _signal.IsClosed) return BeginResult.Closed;
        _waiters.Add(receiver, receiverToken);
        return BeginResult.Registered;
    }

    /// <summary>"Hits subscription.Next"; the signal is named only while the subscription the wait was made on is active.</summary>
    public string DescribeSource(uint sourceToken) =>
        (sourceToken == Token && _signal != null ? _signal.DescribeForWaits() : "Signal<" + typeof(T).Name + ">") + " subscription.Next";

    public Exception CloseError(uint sourceToken) => sourceToken == Token ? _signal?.ClosedWith : null;

    public bool IsActive(uint token) => token == Token && _signal != null;

    public void ReleaseByOwner(uint token)
    {
        if (token != Token || _signal == null) return;
        unchecked
        {
            if (++Token == 0) Token = 1;
        }

        _signal.RemoveSubscription(this);
        _signal = null;
        Owner = null;
        _waiters.CloseAll("the subscription was disposed (maybe through a copy) or its scope ended");
        if (_count > 0)
        {
            var first = Math.Min(_count, _buffer.Length - _head);
            Array.Clear(_buffer, _head, first);
            Array.Clear(_buffer, 0, _count - first);
            _count = 0;
        }

        _head = 0;
        s_pool.Return(this);
    }

    public override string ToString() => $"Subscription({_signal})";
}
