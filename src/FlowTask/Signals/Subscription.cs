namespace Katout.FlowTask;

/// <summary>
/// A receiver-side buffer of a signal's emits. The scope that subscribed owns it; <c>using</c> ends it earlier. Used on
/// the World's thread only, like the signal.
/// </summary>
[LifetimeHandle]
public readonly struct Subscription<T> : IDisposable
{
    readonly SubscriptionCore<T> _core;
    readonly uint _token;

    internal Subscription(SubscriptionCore<T> core, uint token)
    {
        _core = core;
        _token = token;
    }

    /// <summary>True until the subscription is disposed or its scope has ended.</summary>
    public bool IsActive => _core != null && _core.Token == _token;

    /// <summary>Buffered values not taken yet.</summary>
    public int Count => IsActive ? _core.Count : 0;

    /// <summary>
    /// Takes the oldest buffered value, or waits for the next emit: unlike <see cref="Signal{T}.Next"/>, emits made while
    /// nobody waits are kept. On a subscription that has ended (disposed, maybe through a copy), the wait fails with
    /// <see cref="FlowMisuseException"/>.
    /// </summary>
    public FlowTask<T> Next([CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        if (_core == null) throw DefaultSubscription();
        var n = NextNode<T>.Rent(_core, _token);
        n.SetSite(callerFilePath, callerLineNumber);
        return new FlowTask<T>(n, n.Token);
    }

    /// <summary>
    /// Like <see cref="Next"/>, but completes with <c>(true, value)</c> for a value and with <c>(false, default)</c> once
    /// the source is closed and the buffer drained, or the subscription has ended.
    /// </summary>
    public FlowTask<(bool Received, T Value)> NextOrClosed([CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        if (_core == null) throw DefaultSubscription();
        var n = NextOrClosedNode<T>.Rent(_core, _token);
        n.SetSite(callerFilePath, callerLineNumber);
        return new FlowTask<(bool Received, T Value)>(n, n.Token);
    }

    static FlowMisuseException DefaultSubscription() =>
        new("This Subscription is default: no signal made it. Take one from Signal.Subscribe(policy) (or EventSignal.Subscribe) and keep it in a variable.");

    /// <summary>Takes the next buffered value without waiting. On the World's thread.</summary>
    public bool TryTake(out T value)
    {
        if (!IsActive)
        {
            value = default;
            return false;
        }

        _core.CheckThread("Subscription.TryTake");
        return _core.TryTake(out value);
    }

    /// <summary>Ends the subscription before its scope does. On the World's thread; nothing on an ended one.</summary>
    public void Dispose()
    {
        var core = _core;
        if (core == null || core.Token != _token) return;
        core.CheckThread("Subscription.Dispose");
        core.ReleaseByOwner(_token);
    }
}

/// <summary>What a subscription does when its queue is full.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1008", Justification = "No zero member on purpose: default(BufferPolicy) is invalid, so the overflow policy is always an explicit choice.")]
public enum BufferOverflow : byte
{
    /// <summary>The oldest buffered value is dropped to make room for the new one.</summary>
    DropOldest = 1,

    /// <summary>The new value is dropped.</summary>
    DropNewest = 2,

    /// <summary>The scope that owns the subscription fails with <see cref="SubscriptionOverflowException"/>.</summary>
    Fail = 3,
}

/// <summary>How a subscription buffers: <see cref="Latest"/> keeps the newest value; <see cref="Queue"/> needs a capacity and a policy.</summary>
public readonly struct BufferPolicy
{
    internal readonly int Capacity;
    internal readonly BufferOverflow Overflow;

    BufferPolicy(int capacity, BufferOverflow overflow)
    {
        Capacity = capacity;
        Overflow = overflow;
    }

    internal bool IsValid => Capacity > 0 && Overflow != 0;

    /// <summary>Keeps only the most recent emit.</summary>
    public static BufferPolicy Latest => new(1, BufferOverflow.DropOldest);

    /// <summary>A FIFO queue with a capacity and a policy for when it is full.</summary>
    public static BufferPolicy Queue(int capacity, BufferOverflow overflow)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (overflow is not BufferOverflow.DropOldest and not BufferOverflow.DropNewest and not BufferOverflow.Fail)
            throw new ArgumentOutOfRangeException(nameof(overflow));
        return new BufferPolicy(capacity, overflow);
    }

    /// <summary><c>Latest</c> or <c>Queue(capacity, overflow)</c>.</summary>
    public override string ToString() => Capacity == 1 && Overflow == BufferOverflow.DropOldest ? "Latest" : $"Queue({Capacity}, {Overflow})";
}
