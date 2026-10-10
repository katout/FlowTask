using System.Threading.Tasks;

namespace Katout.FlowTask;

/// <summary>
/// A task started with <see cref="Flow.Spawn(FlowTask)"/> or <see cref="FlowWorld.Run(FlowTask, Clock)"/>: cancel it,
/// query it, or wait for it once with <see cref="Join"/>. The handle is not awaitable itself.
/// </summary>
public readonly struct FlowHandle
{
    readonly FlowHandle<FlowUnit> _inner;

    internal FlowHandle(FlowNode<FlowUnit> node, uint token) => _inner = new FlowHandle<FlowUnit>(node, token);

    /// <inheritdoc cref="FlowHandle{T}.Status"/>
    public FlowStatus Status => _inner.Status;

    /// <inheritdoc cref="FlowHandle{T}.IsCompleted"/>
    public bool IsCompleted => _inner.IsCompleted;

    /// <inheritdoc cref="FlowHandle{T}.CancelCause"/>
    public CancelCause CancelCause => _inner.CancelCause;

    /// <inheritdoc cref="FlowHandle{T}.Exception"/>
    public Exception Exception => _inner.Exception;

    /// <inheritdoc cref="FlowHandle{T}.Cancel"/>
    public void Cancel() => _inner.Cancel();

    /// <inheritdoc cref="FlowHandle{T}.Join"/>
    public FlowTask Join([CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        var t = _inner.Join(callerFilePath, callerLineNumber);
        return new FlowTask(t.Node, t.Token);
    }

    /// <inheritdoc cref="FlowHandle{T}.AsTask"/>
    public Task AsTask() => _inner.AsTask();

    /// <inheritdoc cref="FlowHandle{T}.ToString"/>
    public override string ToString() => _inner.ToString();

    /// <summary>The same handle, typed with <see cref="FlowUnit"/> as its result.</summary>
    public static implicit operator FlowHandle<FlowUnit>(FlowHandle h) => h._inner;
}

/// <summary>A spawned or root task with a result.</summary>
public readonly struct FlowHandle<T>
{
    readonly FlowNode<T> _node;
    readonly uint _token;

    internal FlowHandle(FlowNode<T> node, uint token)
    {
        _node = node;
        _token = token;
    }

    internal FlowNode<T> Node => _node;

    internal bool IsValid => _node != null && _node.Token == _token;

    /// <summary>
    /// The task's status. After a Cancel it stays Running while its cleanup runs (a finally block that awaits):
    /// <see cref="CancelCause"/> tells that it is closing. A handle with no task (a Spawn refused in a canceled scope) is
    /// Canceled.
    /// </summary>
    public FlowStatus Status => IsValid ? _node.Status : _node == null ? FlowStatus.Canceled : FlowStatus.Invalid;

    /// <summary>True once the task has ended, however it ended (also for a handle with no task).</summary>
    public bool IsCompleted => !IsValid || _node.IsTerminated;

    /// <summary>Why the task was canceled, or <see cref="CancelCause.None"/>: set as soon as it is asked to end, while <see cref="Status"/> is still Running.</summary>
    public CancelCause CancelCause => IsValid ? _node.Cause : CancelCause.None;

    /// <summary>
    /// The exception that ended the task: set exactly when <see cref="Status"/> is Faulted. Unlike Task.Exception, it
    /// is not wrapped in an AggregateException.
    /// </summary>
    public Exception Exception => IsValid && _node.State == NodeState.Faulted ? _node.Failure?.Info?.Exception : null;

    /// <summary>The result of a task that succeeded; throws otherwise.</summary>
    public T Result => IsValid && _node.State == NodeState.Succeeded
        ? _node.Result
        : throw new InvalidOperationException($"The task has not completed normally (status {Status}).");

    /// <summary>
    /// Cancels the task and its subtree (<see cref="CancelCause.Explicit"/>). From any thread: on another, it is applied in
    /// the intake at the start of the next Tick or Flush, so <c>ct.Register(handle.Cancel)</c> works. Does nothing once
    /// the task has ended.
    /// </summary>
    public void Cancel()
    {
        if (!IsValid) return;
        var world = _node.World;
        if (!world.IsWorldThread)
        {
            // A handle's node is never pooled, so its token and World can be read from any thread.
            if (!_node.IsTerminated) world.Inbox.PostCancel(_node, _token);
            return;
        }

        world.Unwinder.CancelNode(_node, CancelCause.Explicit);
    }

    /// <summary>
    /// Waits for the task to succeed and returns its result; if it is canceled or faults, the await throws
    /// <see cref="FlowJoinException"/>. To wait for any ending, use <c>FlowTask.WaitUntil(handle, h => h.IsCompleted)</c>
    /// and look at <see cref="Status"/>. A handle can be joined once; a join from inside the task, or from a World on
    /// another thread, throws at its await.
    /// </summary>
    public FlowTask<T> Join([CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) =>
        JoinNode<T>.Create(this, callerFilePath, callerLineNumber);

    internal bool TryMarkJoined()
    {
        if (!IsValid) return _node == null;
        if ((_node.Flags & NodeFlags.HandleAwaited) != 0) return false;
        _node.Flags |= NodeFlags.HandleAwaited;
        return true;
    }

    /// <summary>
    /// A Task for code outside the flows. It completes on the World thread right after the Tick, Flush, Run or Dispose in
    /// which the flow ended, never inside a flush; its continuations run there, and a Run, Tick or Flush they call is part
    /// of that call. Awaited from the flow itself or a scope under it, it never completes: a flow ends after its children.
    /// On the World's thread (<see cref="FlowThreadException"/> on another).
    /// </summary>
    public Task<T> AsTask()
    {
        if (_node == null) return Task.FromCanceled<T>(new CancellationToken(true));
        _node.World.CheckThread();
        if (!IsValid) return Task.FromException<T>(new InvalidOperationException("Invalid handle."));
        var observer = new TaskObserver<T>();
        if (_node.IsTerminated) observer.CompleteNow(_node);
        else if (_node.Observer == null) _node.Observer = observer;
        else _node.Observer = new ObserverChain(_node.Observer, observer);
        return observer.Task;
    }

    /// <summary><c>FlowHandle(Screen, Running)</c>, <c>FlowHandle(Screen, Running: Explicit)</c> while it closes.</summary>
    public override string ToString()
    {
        if (_node == null) return "FlowHandle(Canceled)";
        if (!IsValid) return "FlowHandle(Invalid)";
        var cause = _node.Cause == CancelCause.None ? "" : ": " + _node.Cause;
        return $"FlowHandle({_node.DisplayName}, {_node.Status}{cause})";
    }
}

/// <summary>
/// A lifetime handle (a <see cref="Clock.Pause"/>): the scope that created it owns it and releases it when it ends;
/// dispose it earlier with <c>using</c>. Dispose can be called more than once.
/// </summary>
[LifetimeHandle]
public readonly struct ScopedHandle : IDisposable
{
    readonly IScopeOwned _target;
    readonly uint _token;

    internal ScopedHandle(IScopeOwned target, uint token)
    {
        _target = target;
        _token = token;
    }

    /// <summary>True until the handle is disposed or its scope has ended.</summary>
    public bool IsActive => _target != null && _target.IsActive(_token);

    /// <summary>Releases what the handle holds; does nothing once released.</summary>
    public void Dispose() => _target?.ReleaseByOwner(_token);
}
