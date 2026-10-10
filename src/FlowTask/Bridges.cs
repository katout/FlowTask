using System.Threading.Tasks;

namespace Katout.FlowTask;

/// <summary>Bridges external operations and events into flows: Task, ValueTask and callbacks.</summary>
public static class FlowBridge
{
    /// <summary>
    /// Bridges an external operation: <paramref name="factory"/> runs when the FlowTask starts, with a token that is
    /// canceled when the scope is. The completion resumes the flow on the World thread, in a flush. The Task's exception
    /// is thrown at the await; an external cancellation (an OperationCanceledException that is not the flow's own) is
    /// such an exception.
    /// </summary>
    /// <param name="factory">Starts the operation with the scope's token.</param>
    /// <param name="onDiscard">
    /// Receives, once, a successful result that no flow received, to release it (<c>v =&gt; v.Dispose()</c>): one that
    /// arrived after the bridge was canceled, or one released before anything took it (a Race lost in the same flush, a
    /// WhenAll that failed, <c>WithoutResult</c>). It runs on the World thread in the next intake, or on the completing
    /// thread when the World is already disposed. A value a flow received at an await belongs to that flow; so does a
    /// value a handle holds (the bridge started with Flow.Spawn or FlowWorld.Run), since <c>Result</c> can read it at any
    /// time.
    /// </param>
    public static TaskBridge<T> FromTask<T>(Func<CancellationToken, Task<T>> factory, Action<T> onDiscard = null) =>
        new(factory ?? throw new ArgumentNullException(nameof(factory)), null, onDiscard);

    /// <inheritdoc cref="FromTask{T}(Func{CancellationToken, Task{T}}, Action{T})"/>
    public static TaskBridge FromTask(Func<CancellationToken, Task> factory) =>
        new(factory ?? throw new ArgumentNullException(nameof(factory)), null);

    /// <summary>Bridges a Task that is already running: the scope's cancellation does not reach it.</summary>
    public static TaskBridge<T> AsFlow<T>(this Task<T> task) => new(null, task ?? throw new ArgumentNullException(nameof(task)), null);

    /// <inheritdoc cref="AsFlow{T}(Task{T})"/>
    public static TaskBridge AsFlow(this Task task) => new(null, task ?? throw new ArgumentNullException(nameof(task)));

    /// <inheritdoc cref="AsFlow{T}(Task{T})"/>
    public static TaskBridge<T> AsFlow<T>(this ValueTask<T> task) => new(null, task.AsTask(), null);

    /// <inheritdoc cref="AsFlow{T}(Task{T})"/>
    public static TaskBridge AsFlow(this ValueTask task) => new(null, task.AsTask());

    /// <summary>
    /// Bridges an event source to a signal: <paramref name="attach"/> receives the emit callback and returns the detach
    /// action, which runs when the current scope ends (or on Dispose); the signal is then closed.
    /// <c>emit =&gt; { button.Clicked += emit; return () =&gt; button.Clicked -= emit; }</c>
    /// </summary>
    /// <remarks>
    /// The current scope owns the bridge; made outside a flow, nothing does: dispose it yourself. The callback may run on
    /// any thread: on the World's thread it emits at once, on another the value goes through the World's inbox to the
    /// next Tick or Flush. A callback before any World uses the signal (made outside a flow, not waited on yet) reaches
    /// nobody. After Dispose, callbacks are ignored.
    /// </remarks>
    public static EventSignal<T> FromCallback<T>(Func<Action<T>, Action> attach, string name = null)
    {
        if (attach == null) throw new ArgumentNullException(nameof(attach));
        var bridge = new EventSignal<T>(name);
        bridge.SetDetach(attach(bridge.Emit));
        bridge.RegisterWithCurrentScope();
        return bridge;
    }
}

/// <summary>A bridged external operation: await it, or pass it to a combinator.</summary>
public readonly struct TaskBridge<T>
{
    readonly Func<CancellationToken, Task<T>> _factory;
    readonly Task<T> _task;
    readonly Action<T> _onDiscard;

    internal TaskBridge(Func<CancellationToken, Task<T>> factory, Task<T> task, Action<T> onDiscard)
    {
        _factory = factory;
        _task = task;
        _onDiscard = onDiscard;
    }

    /// <summary>The bridge as a FlowTask, to pass to a combinator, Flow.Spawn or FlowWorld.Run. Each call is a new task.</summary>
    public FlowTask<T> ToFlowTask()
    {
        var n = TaskNode<T>.Create(_factory, _task, _onDiscard);
        return new FlowTask<T>(n, n.Token);
    }

    /// <summary>Starts the bridge as a child of the current scope.</summary>
    public FlowTask<T>.Awaiter GetAwaiter() => ToFlowTask().GetAwaiter();

    /// <summary>Same as <see cref="ToFlowTask"/>.</summary>
    public static implicit operator FlowTask<T>(TaskBridge<T> bridge) => bridge.ToFlowTask();
}

/// <summary>A bridged external operation without a result.</summary>
public readonly struct TaskBridge
{
    readonly Func<CancellationToken, Task> _factory;
    readonly Task _task;

    internal TaskBridge(Func<CancellationToken, Task> factory, Task task)
    {
        _factory = factory;
        _task = task;
    }

    /// <inheritdoc cref="TaskBridge{T}.ToFlowTask"/>
    public FlowTask ToFlowTask()
    {
        var n = TaskNode<FlowUnit>.Create(_factory, _task, null);
        return new FlowTask(n, n.Token);
    }

    /// <inheritdoc cref="TaskBridge{T}.GetAwaiter"/>
    public FlowTask.Awaiter GetAwaiter() => ToFlowTask().GetAwaiter();

    /// <summary>Same as <see cref="ToFlowTask"/>.</summary>
    public static implicit operator FlowTask(TaskBridge bridge) => bridge.ToFlowTask();
}
