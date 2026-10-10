using System;
using System.Threading;
using R3;

namespace Katout.FlowTask;

/// <summary>
/// R3 Observable &lt;-&gt; FlowTask Signal bridge.
/// <para>
/// <see cref="ToSignal{T}"/>: an <see cref="Observable{T}"/> feeding an <see cref="EventSignal{T}"/> owned by the
/// current scope. The R3 subscription is disposed when that scope ends (or on Dispose). OnNext values are emitted
/// (from another thread they go through the World inbox). Completion closes the signal; from another
/// thread the close also goes through the inbox, after the values queued before it. A failure closes the signal with
/// that error, so a flow waiting on <c>Next()</c> receives a <see cref="SignalClosedException"/> at its await whose
/// InnerException is the upstream error; it is also reported to R3's unhandled-exception handler, which is where it
/// shows when no flow waits.
/// </para>
/// <para>
/// <see cref="ToObservable{T}(Signal{T}, FlowWorld)"/>: each R3 subscription starts a forwarding flow in the World that
/// subscribes to the signal and forwards its values in the World's flush (never synchronously inside Emit).
/// The observer is completed when the signal closes and its buffer is drained: with a failure carrying the error when
/// the signal was closed with one. Disposing the R3 subscription cancels the forwarding flow.
/// </para>
/// </summary>
public static class FlowR3
{
    // Default buffering of ToObservable: up to 256 emits between two flushes; more is an unhandled exception, not a silent loss.
    static BufferPolicy DefaultForwardingPolicy => BufferPolicy.Queue(256, BufferOverflow.Fail);

    /// <summary>
    /// Bridges <paramref name="source"/> to a signal owned by the current scope: subscribed now, the
    /// subscription is disposed when the scope ends. Values arriving while nobody waits are lost unless you
    /// <see cref="EventSignal{T}.Subscribe"/> to buffer them.
    /// </summary>
    public static EventSignal<T> ToSignal<T>(this Observable<T> source, string name = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var link = new ObservableLink<T>();
        var signal = FlowBridge.FromCallback<T>(emit => link.Attach(source, emit), name);
        link.Bind(signal);
        return signal;
    }

    /// <summary>
    /// An observable of the signal's emits, forwarded by a flow running in <paramref name="world"/>. Subscribe on the
    /// World's thread. Buffers up to 256 emits between two flushes; more is an unhandled exception.
    /// </summary>
    public static Observable<T> ToObservable<T>(this Signal<T> signal, FlowWorld world) => ToObservable(signal, world, DefaultForwardingPolicy);

    /// <summary>
    /// An observable of the signal's emits with an explicit buffer policy for emits that arrive between two flushes.
    /// </summary>
    public static Observable<T> ToObservable<T>(this Signal<T> signal, FlowWorld world, BufferPolicy policy)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (world == null) throw new ArgumentNullException(nameof(world));
        return new SignalObservable<T>(signal, world, policy);
    }
}

/// <summary>
/// Connects one R3 subscription to one <see cref="EventSignal{T}"/>. The source may complete on any thread, also
/// inside Subscribe, before <see cref="Bind"/> has published the signal: Bind publishes the signal and then looks for
/// a completion, OnCompleted publishes the completion and then looks for the signal (both with full fences), so at
/// least one of them sees the other, and <see cref="CloseOnce"/> closes the signal once.
/// </summary>
internal sealed class ObservableLink<T>
{
    const int Running = 0, Completing = 1, Completed = 2;

    readonly int _threadId = Environment.CurrentManagedThreadId;
    Action<T> _emit;
    IDisposable _subscription;
    EventSignal<T> _signal;
    int _completed;
    int _closed;
    bool _completedOnAnotherThread;
    Exception _error;

    internal Action Attach(Observable<T> source, Action<T> emit)
    {
        _emit = emit;
        var subscription = source.Subscribe(this, static (value, link) => link._emit(value), static (result, link) => link.OnCompleted(result));
        if (Volatile.Read(ref _completed) != Running) subscription.Dispose();
        else _subscription = subscription;
        return Detach;
    }

    internal void Bind(EventSignal<T> signal)
    {
        Interlocked.Exchange(ref _signal, signal);
        if (Volatile.Read(ref _completed) == Completed) CloseOnce(); // completed inside Subscribe, on any thread
    }

    void Detach() => Interlocked.Exchange(ref _subscription, null)?.Dispose();

    void OnCompleted(Result result)
    {
        if (Interlocked.CompareExchange(ref _completed, Completing, Running) != Running) return;
        if (result.IsFailure)
        {
            _error = result.Exception;
            ObservableSystem.GetUnhandledExceptionHandler().Invoke(result.Exception);
        }

        _completedOnAnotherThread = Environment.CurrentManagedThreadId != _threadId;
        Interlocked.Exchange(ref _completed, Completed); // publishes _error and the thread with it
        if (Volatile.Read(ref _signal) != null) CloseOnce(); // else Bind has not run yet and closes
    }

    void CloseOnce()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        var s = _signal;
        if (s == null || s.IsDisposed) return;
        // Signal.Close is World-thread only. A completion on another thread closes through the World inbox, after any
        // emits that thread queued before it, also when Bind closes it on the World's thread.
        if (_completedOnAnotherThread) s.Signal.CloseFromAnyThread(_error);
        else if (!s.Signal.IsClosed) s.Signal.Close(_error);
    }
}

internal sealed class SignalObservable<T> : Observable<T>
{
    readonly Signal<T> _signal;
    readonly FlowWorld _world;
    readonly BufferPolicy _policy;

    internal SignalObservable(Signal<T> signal, FlowWorld world, BufferPolicy policy)
    {
        _signal = signal;
        _world = world;
        _policy = policy;
    }

    protected override IDisposable SubscribeCore(Observer<T> observer)
    {
        var forwarder = new SignalForwarder<T>(_signal, _world, _policy, observer);
        forwarder.Start();
        return forwarder;
    }
}

/// <summary>
/// One R3 subscription to a signal: a root flow that forwards it. Dispose cancels the flow through its handle, which
/// may be done from any thread: at once on the World thread, in the next Tick or Flush from another thread.
/// </summary>
internal sealed class SignalForwarder<T> : IDisposable
{
    readonly Signal<T> _signal;
    readonly FlowWorld _world;
    readonly BufferPolicy _policy;
    readonly Observer<T> _observer;
    FlowHandle _handle;
    volatile bool _disposed;

    internal SignalForwarder(Signal<T> signal, FlowWorld world, BufferPolicy policy, Observer<T> observer)
    {
        _signal = signal;
        _world = world;
        _policy = policy;
        _observer = observer;
    }

    internal void Start() => _handle = _world.Run(Flow.Named("R3.ToObservable(" + _signal + ")", Forward()));

    async FlowTask Forward()
    {
        using var subscription = _signal.Subscribe(_policy);
        var ended = false;
        try
        {
            while (true)
            {
                T value;
                try
                {
                    value = await subscription.Next();
                }
                catch (SignalClosedException closed)
                {
                    // Closed and drained. Next (unlike NextOrClosed) carries the error the signal was closed with, so a
                    // failed signal fails the observer instead of completing it as a success. Once per subscription.
                    ended = true;
                    if (_disposed) return;
                    if (closed.InnerException == null) _observer.OnCompleted();
                    else _observer.OnCompleted(Result.Failure(closed.InnerException));
                    return;
                }

                // Disposed on another thread: the cancel comes with the next Tick or Flush, maybe after this value.
                if (_disposed)
                {
                    ended = true;
                    return;
                }

                _observer.OnNext(value);
            }
        }
        finally
        {
            // Unwound without the signal closing and without Dispose: the World was disposed, or an exception (e.g. a
            // buffer overflow) ended the forwarding flow.
            if (!ended && !_disposed)
                _observer.OnCompleted(Result.Failure(new OperationCanceledException(
                    $"The flow forwarding '{_signal}' to R3 ended before the signal closed (World disposed, canceled or faulted).")));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Cancel(); // does nothing once the flow ended or its World was disposed
    }
}
