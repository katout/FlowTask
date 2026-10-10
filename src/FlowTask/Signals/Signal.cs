namespace Katout.FlowTask;

/// <summary>
/// An event source without state. <see cref="Emit"/> reaches the receivers present at that moment: the scopes waiting on
/// <see cref="Next"/> (an edge) and the subscriptions, which buffer by their policy. Receivers are never resumed
/// synchronously: their resumes are queued. It plays the part of R3's Subject.
/// </summary>
public sealed class Signal<T> : IWaitSource<T>, IInboxItem
{
    readonly WaiterList<T> _waiters = new();
    readonly List<SubscriptionCore<T>> _subscriptions = new();
    Queue<T> _crossThread; // under the bound World's inbox lock
    readonly ThreadBinding _binding;
    bool _closed;
    Exception _closeError;

    /// <summary>
    /// Creates a signal. Created inside a flow, it is bound to that flow's World; otherwise to the first World that waits
    /// on it or subscribes to it. Until then, EmitFromAnyThread throws: use <see cref="Signal{T}(FlowWorld, string)"/>.
    /// </summary>
    public Signal(string name = null)
    {
        Name = name;
        _binding = new ThreadBinding();
        _binding.BindCurrent(this);
    }

    /// <summary>Creates a signal bound to <paramref name="world"/>, as EmitFromAnyThread needs before the World uses it.</summary>
    public Signal(FlowWorld world, string name = null)
    {
        Name = name;
        _binding = new ThreadBinding(world ?? throw new ArgumentNullException(nameof(world)));
    }

    /// <summary>A signal derived from an owner (FlowProperty.Changed), which shares its binding.</summary>
    internal Signal(ThreadBinding binding, string name)
    {
        Name = name;
        _binding = binding;
    }

    /// <summary>The name in dumps and waits, or null.</summary>
    public string Name { get; }

    /// <summary>True once <see cref="Close"/> (or a close from another thread) has been applied.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Delivers <paramref name="value"/> to the receivers present now. On the World's thread. On a closed signal it throws
    /// <see cref="FlowMisuseException"/> (<see cref="EmitFromAnyThread"/> drops the value instead).
    /// </summary>
    public void Emit(T value)
    {
        _binding.Check("Signal.Emit");
        if (_closed) throw new FlowMisuseException($"Emit on the closed signal '{this}'.");
        _waiters.DeliverAll(value);
        var overflowed = false;
        for (var i = 0; i < _subscriptions.Count; i++) overflowed |= _subscriptions[i].Push(value);
        if (!overflowed) return;
        foreach (var s in _subscriptions.ToArray()) s.RaisePendingOverflow();
    }

    /// <summary>
    /// Emits from any thread: the value is queued in the World's inbox and delivered in the intake at the start of the
    /// next Tick or Flush, in the order of every send of the thread. The signal must be bound to a World first (see the
    /// constructors). On a closed signal, or once that World is disposed, the value is dropped.
    /// </summary>
    public void EmitFromAnyThread(T value)
    {
        var world = _binding.WorldForAnyThread ?? throw Errors.EmitFromAnyThreadUnbound();
        // The value and its entry are queued under one lock: the n-th emit entry takes the n-th value.
        lock (world.Inbox.Lock)
        {
            if (world.Inbox.TryEnqueueLocked(this, EmitToken, InboxKind.Item)) (_crossThread ??= new Queue<T>()).Enqueue(value);
        }
    }

    /// <summary>
    /// Closes from any thread: applied in the intake at the start of the next Tick or Flush, after the emits sent before
    /// it. A signal that no World uses yet is marked closed at once. <paramref name="error"/> is the source's failure, as
    /// for <see cref="Close"/>.
    /// </summary>
    public void CloseFromAnyThread(Exception error = null)
    {
        var world = MarkClosedIfUnbound(error);
        if (world == null) return;
        if (error == null) world.Inbox.Post(this, CloseToken);
        else world.Inbox.Post(new CloseRequest(this, ClosedReason, error), 0);
    }

    const string ClosedReason = "the signal was closed";
    const uint EmitToken = 0;
    const uint CloseToken = 1;

    /// <summary>EventSignal.Dispose, from any thread: at once on the World's thread, through its inbox from another.</summary>
    internal void CloseByOwner(string reason)
    {
        var world = MarkClosedIfUnbound(null);
        if (world == null) return;
        if (world.IsWorldThread) CloseCore(reason, null);
        else world.Inbox.Post(new CloseRequest(this, reason, null), 0);
    }

    /// <summary>
    /// A signal no World uses yet is only marked closed (nothing waits on it: a wait binds it first) and null is returned;
    /// otherwise, the bound World. Under the binding's lock, which a first binding takes too.
    /// </summary>
    FlowWorld MarkClosedIfUnbound(Exception error)
    {
        lock (_binding.Gate)
        {
            var world = _binding.World;
            if (world == null && !_closed)
            {
                _closeError = error;
                _closed = true;
            }

            return world;
        }
    }

    sealed class CloseRequest : IInboxItem
    {
        readonly Signal<T> _signal;
        readonly string _reason;
        readonly Exception _error;

        internal CloseRequest(Signal<T> signal, string reason, Exception error)
        {
            _signal = signal;
            _reason = reason;
            _error = error;
        }

        public void ProcessInbox(uint token) => _signal.CloseCore(_reason, _error);

        public void DiscardInbox(uint token)
        {
        }
    }

    void IInboxItem.ProcessInbox(uint token)
    {
        if (token == CloseToken)
        {
            CloseCore(ClosedReason, null);
            return;
        }

        T value;
        lock (_binding.World.Inbox.Lock) value = _crossThread.Dequeue();
        if (!_closed) Emit(value); // closed after it was sent: nobody receives it
    }

    void IInboxItem.DiscardInbox(uint token)
    {
        if (token != EmitToken) return;
        lock (_binding.World.Inbox.Lock) _crossThread.Dequeue();
    }

    /// <summary>
    /// Waits for the next emit (an edge): emits made while this scope does not wait are dropped. To keep them,
    /// <see cref="Subscribe"/> and wait on the subscription's Next.
    /// </summary>
    public FlowTask<T> Next([CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        var n = NextNode<T>.Rent(this, 0);
        n.SetSite(callerFilePath, callerLineNumber);
        return new FlowTask<T>(n, n.Token);
    }

    /// <summary>
    /// Like <see cref="Next"/>, but completes with <c>(true, value)</c> for a value and with <c>(false, default)</c>
    /// instead of throwing <see cref="SignalClosedException"/> when the signal closes.
    /// </summary>
    public FlowTask<(bool Received, T Value)> NextOrClosed([CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        var n = NextOrClosedNode<T>.Rent(this, 0);
        n.SetSite(callerFilePath, callerLineNumber);
        return new FlowTask<(bool Received, T Value)>(n, n.Token);
    }

    /// <summary>
    /// Buffers the emits from now on, by <paramref name="policy"/>; the flow takes them with the subscription's Next or
    /// TryTake. The current scope owns the subscription; outside a flow nothing does (dispose it yourself), and
    /// <see cref="BufferOverflow.Fail"/>, which fails the owner, is refused.
    /// </summary>
    public Subscription<T> Subscribe(BufferPolicy policy)
    {
        if (!policy.IsValid) throw new ArgumentException("Use BufferPolicy.Latest or BufferPolicy.Queue(capacity, overflow).", nameof(policy));
        var world = FlowWorld.t_current;
        if (policy.Overflow == BufferOverflow.Fail && world?.CurrentScope is not { State: NodeState.Running })
            throw new FlowMisuseException("BufferOverflow.Fail fails the scope that owns the subscription, but this one is made outside a flow. Subscribe inside a FlowTask method, or choose DropOldest or DropNewest.");
        if (world != null) _binding.Bind(world, this);
        _binding.Check("Signal.Subscribe");
        var core = SubscriptionCore<T>.Rent(this, policy, _closed);
        _subscriptions.Add(core);
        core.Owner = ScopeOwnership.RegisterOwned(core, core.Token);
        core.OwnerToken = core.Owner?.Token ?? 0;
        return new Subscription<T>(core, core.Token);
    }

    internal void RemoveSubscription(SubscriptionCore<T> core) => _subscriptions.Remove(core);

    internal void CheckThread(string what) => _binding.Check(what);

    /// <summary>The World the signal is bound to, or null; read from any thread.</summary>
    internal FlowWorld BoundWorldForAnyThread => _binding.WorldForAnyThread;

    internal void BindForSubscription(FlowWorld world) => _binding.Bind(world, this);

    /// <summary>
    /// Closes the source: a <see cref="Next"/> waiting on it throws <see cref="SignalClosedException"/>, with
    /// <paramref name="error"/> (the source's failure) as its InnerException; <see cref="NextOrClosed"/> returns
    /// <c>(false, default)</c>. Subscriptions hand out their buffered values first. Closing again does nothing.
    /// </summary>
    public void Close(Exception error = null)
    {
        _binding.Check("Signal.Close");
        CloseCore(ClosedReason, error);
    }

    void CloseCore(string reason, Exception error)
    {
        if (_closed) return;
        _closed = true;
        _closeError = error;
        _waiters.CloseAll(reason);
        if (_subscriptions.Count == 0) return;
        // Telling one can end another (its waiters' unwinding), whose core may then serve another subscription: tokens.
        var subs = new (SubscriptionCore<T> Core, uint Token)[_subscriptions.Count];
        for (var i = 0; i < subs.Length; i++) subs[i] = (_subscriptions[i], _subscriptions[i].Token);
        foreach (var (core, token) in subs) core.OnSourceClosed(token, reason);
    }

    internal Exception ClosedWith => _closed ? _closeError : null;

    BeginResult IWaitSource<T>.Begin(IValueReceiver<T> receiver, uint receiverToken, uint sourceToken, FlowWorld world, out T value)
    {
        value = default;
        _binding.Bind(world, this);
        if (_closed) return BeginResult.Closed;
        _waiters.Add(receiver, receiverToken);
        return BeginResult.Registered;
    }

    string IWaitSource<T>.DescribeSource(uint sourceToken) => DescribeForWaits() + ".Next";

    internal string DescribeForWaits() => Name ?? "Signal<" + typeof(T).Name + ">";

    Exception IWaitSource<T>.CloseError(uint sourceToken) => ClosedWith;

    // A value a released wait did not hand on: an edge keeps nothing.
    void IWaitSource<T>.Unget(T value, uint sourceToken)
    {
    }

    /// <summary>The name, or <c>Signal&lt;T&gt;</c>.</summary>
    public override string ToString() => Name ?? $"Signal<{typeof(T).Name}>";
}
