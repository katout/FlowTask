namespace Katout.FlowTask.Internal;

/// <summary>A leaf waiting for a value from a signal, a subscription or a Once.</summary>
internal interface IValueReceiver<T>
{
    bool IsWaiting(uint token);
    void Deliver(T value, uint token);
    void DeliverClosed(uint token, string reason);
}

internal enum BeginResult : byte
{
    Registered,
    Value,
    Closed,

    /// <summary>The subscription the wait was made on has ended (disposed, maybe through a copy).</summary>
    Disposed,
}

/// <summary>Something a value receiver can wait on.</summary>
internal interface IWaitSource<T>
{
    BeginResult Begin(IValueReceiver<T> receiver, uint receiverToken, uint sourceToken, FlowWorld world, out T value);

    string DescribeSource(uint sourceToken);

    /// <summary>The error the source was closed with, or null.</summary>
    Exception CloseError(uint sourceToken);

    /// <summary>
    /// Takes back a value it handed to a wait released before anything received it (a Race loser, an awaiter that
    /// unwound). Only a subscription keeps it; a signal and a Once do nothing.
    /// </summary>
    void Unget(T value, uint sourceToken);
}

/// <summary>
/// Next and Wait (<typeparamref name="TResult"/> is T; a close fails the wait) or NextOrClosed ((bool Received, T Value); a
/// close gives (false, default)). A value released without being received goes back to its source.
/// </summary>
internal abstract class ValueWaitNode<T, TResult> : LeafNode<TResult>, IValueReceiver<T>
{
    IWaitSource<T> _source;
    uint _sourceToken;
    T _value;

    protected void Init(IWaitSource<T> source, uint sourceToken)
    {
        _source = source;
        _sourceToken = sourceToken;
        InitUnstarted();
    }

    protected abstract TResult Wrap(T value);

    /// <summary>The source closed: a Next fails with SignalClosedException, a NextOrClosed gives (false, default).</summary>
    protected abstract void OnClosed(string what, bool sync);

    internal override string Name => _source?.DescribeSource(_sourceToken) ?? "Wait";

    protected override void OnStart()
    {
        switch (_source.Begin(this, Token, _sourceToken, World, out var value))
        {
            case BeginResult.Value:
                // Taken from a subscription's buffer: unclaimed until received. Set before CompleteSync, whose consumer may take it at once.
                Take(value);
                CompleteSync(Wrap(value));
                break;
            case BeginResult.Closed:
                OnClosed("the source is closed", sync: true);
                break;
            case BeginResult.Disposed:
                OnSourceEnded();
                break;
        }
    }

    /// <summary>The subscription had ended before the wait started: a Next is a misuse, a NextOrClosed gives (false, default).</summary>
    protected virtual void OnSourceEnded() =>
        Fail(new FlowMisuseException($"{Name}: this subscription has already ended: it was disposed (maybe through a copy of the struct) or the scope that subscribed ended."));

    void Take(T value)
    {
        if (_sourceToken == 0) return; // a signal's or a Once's value is not given back
        _value = value;
        Flags |= NodeFlags.Unclaimed;
    }

    protected SignalClosedException ClosedException(string what)
    {
        var error = _source?.CloseError(_sourceToken);
        return error == null
            ? new SignalClosedException($"{Name}: {what}. Use NextOrClosed() to handle the close.")
            : new SignalClosedException($"{Name}: {what}, because its source failed: {error.Message}. NextOrClosed() returns (false, default) instead.", error);
    }

    public bool IsWaiting(uint token) => token == Token && State == NodeState.Running && !IsCancelConfirmed;

    public void Deliver(T value, uint token)
    {
        if (token != Token || State != NodeState.Running) return;
        _value = value;
        Flags |= NodeFlags.Unclaimed;
        CompleteAsync(Wrap(value));
    }

    public void DeliverClosed(uint token, string reason)
    {
        if (token == Token) OnClosed(reason + " while waiting", sync: false);
    }

    internal override string DescribeWait() => Name;

    /// <summary>WithoutResult: the flow received the value it waited for, which does not go back.</summary>
    internal override void IgnoreResult() => Flags &= ~NodeFlags.Unclaimed;

    protected override void ResetNode()
    {
        // Released with a value nobody received (a Race loser, an awaiter that unwound): back to its source.
        if ((Flags & NodeFlags.Unclaimed) != 0) _source.Unget(_value, _sourceToken);
        _source = null;
        _value = default;
        base.ResetNode();
    }
}

internal sealed class NextNode<T> : ValueWaitNode<T, T>
{
    static NodePool<NextNode<T>> s_pool;

    internal static NextNode<T> Rent(IWaitSource<T> source, uint sourceToken)
    {
        var n = s_pool.Rent() ?? new NextNode<T>();
        n.Init(source, sourceToken);
        return n;
    }

    protected override T Wrap(T value) => value;

    protected override void OnClosed(string what, bool sync)
    {
        if (sync) Fail(ClosedException(what));
        else FailAsync(ClosedException(what));
    }

    protected override void ReturnToPool() => s_pool.Return(this);
}

internal sealed class NextOrClosedNode<T> : ValueWaitNode<T, (bool Received, T Value)>
{
    static NodePool<NextOrClosedNode<T>> s_pool;

    internal static NextOrClosedNode<T> Rent(IWaitSource<T> source, uint sourceToken)
    {
        var n = s_pool.Rent() ?? new NextOrClosedNode<T>();
        n.Init(source, sourceToken);
        return n;
    }

    internal override string Name => base.Name + "OrClosed";

    protected override (bool Received, T Value) Wrap(T value) => (true, value);

    protected override void OnClosed(string what, bool sync)
    {
        if (sync) CompleteSync(default);
        else CompleteAsync(default);
    }

    protected override void OnSourceEnded() => CompleteSync(default);

    protected override void ReturnToPool() => s_pool.Return(this);
}

internal interface IPropertyWaiter<T>
{
    bool IsWaiting(uint token);
    bool Check(T value);
    void Satisfy(T value, uint token);
    void FailWith(Exception ex, uint token);
}

/// <summary>FlowProperty.WaitUntil: completes with the value once the condition holds.</summary>
internal sealed class PropertyWaitNode<T, TState> : LeafNode<T>, IPropertyWaiter<T>
{
    static NodePool<PropertyWaitNode<T, TState>> s_pool;

    FlowProperty<T> _property;
    TState _state;
    Func<T, TState, bool> _predicate;

    internal static PropertyWaitNode<T, TState> Rent(FlowProperty<T> property, TState state, Func<T, TState, bool> predicate)
    {
        var n = s_pool.Rent() ?? new PropertyWaitNode<T, TState>();
        n._property = property;
        n._state = state;
        n._predicate = predicate;
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "FlowProperty.WaitUntil";

    /// <summary>The first evaluation; a condition that throws fails the wait, as a later evaluation does.</summary>
    protected override void OnStart()
    {
        _property.Bind(World);
        var v = _property.Value;
        bool satisfied;
        try
        {
            satisfied = _predicate(v, _state);
        }
#pragma warning disable CA1031 // a condition that throws fails the wait, like a later evaluation
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Fail(ex);
            return;
        }

        if (satisfied) CompleteSync(v);
        else _property.AddWaiter(this, Token);
    }

    public bool IsWaiting(uint token) => token == Token && State == NodeState.Running && !IsCancelConfirmed;

    public bool Check(T value) => _predicate(value, _state);

    public void Satisfy(T value, uint token)
    {
        if (token == Token) CompleteAsync(value);
    }

    public void FailWith(Exception ex, uint token)
    {
        if (token == Token) FailAsync(ex);
    }

    internal override string DescribeWait() => _property == null ? Name : Name + " (value " + _property.Value + ")";

    protected override void ReturnToPool()
    {
        _property = null;
        _state = default;
        _predicate = null;
        s_pool.Return(this);
    }
}

/// <summary>
/// The waiters of a signal, subscription or Once, in registration order. Canceled waiters are dropped lazily (compaction
/// on growth), so registering and canceling waits does not allocate.
/// </summary>
internal sealed class WaiterList<T>
{
    WaiterEntry[] _items = new WaiterEntry[4];
    int _count;
    WaiterEntry[] _spare;

    internal int Count => _count;

    internal void Add(IValueReceiver<T> receiver, uint token)
    {
        if (_count == _items.Length)
        {
            var w = 0;
            for (var i = 0; i < _count; i++)
            {
                if (_items[i].IsWaiting) _items[w++] = _items[i];
            }

            Array.Clear(_items, w, _count - w);
            _count = w;
            if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        }

        _items[_count++] = new WaiterEntry(receiver, token);
    }

    /// <summary>Delivers <paramref name="value"/> to every live waiter.</summary>
    internal void DeliverAll(T value)
    {
        // Many emits find nobody waiting: they swap and clear nothing (that doubled the cost of such an Emit).
        if (_count == 0) return;
        var items = TakeAll(out var count);
        for (var i = 0; i < count; i++)
        {
            var e = items[i];
            items[i] = default;
            if (e.IsWaiting) e.Receiver.Deliver(value, e.Token);
        }

        _spare = items;
    }

    /// <summary>Tells every live waiter that the source closed.</summary>
    internal void CloseAll(string reason)
    {
        var items = TakeAll(out var count);
        for (var i = 0; i < count; i++)
        {
            if (items[i].IsWaiting) items[i].Receiver.DeliverClosed(items[i].Token, reason);
        }

        GiveBack(items, count);
    }

    /// <summary>Delivers to the first live waiter (queue semantics); false when nobody waits.</summary>
    internal bool DeliverFirst(T value)
    {
        for (var i = 0; i < _count; i++)
        {
            var e = _items[i];
            if (!e.IsWaiting) continue;
            Array.Copy(_items, i + 1, _items, 0, _count - i - 1);
            Array.Clear(_items, _count - i - 1, i + 1);
            _count -= i + 1;
            e.Receiver.Deliver(value, e.Token);
            return true;
        }

        Array.Clear(_items, 0, _count);
        _count = 0;
        return false;
    }

    // A delivery can run code that registers new waiters: the entries are taken out first, and the array is reused after.
    WaiterEntry[] TakeAll(out int count)
    {
        var items = _items;
        count = _count;
        _items = _spare ?? new WaiterEntry[items.Length];
        _spare = null;
        _count = 0;
        return items;
    }

    void GiveBack(WaiterEntry[] items, int count)
    {
        Array.Clear(items, 0, count);
        _spare = items;
    }

    readonly record struct WaiterEntry(IValueReceiver<T> Receiver, uint Token)
    {
        internal bool IsWaiting => Receiver.IsWaiting(Token);
    }
}
