namespace Katout.FlowTask;

/// <summary>Operations on the current scope: spawning, ownership, clocks, names and cancellation.</summary>
public static class Flow
{
    /// <summary>True when called from flow code running in a World.</summary>
    public static bool IsInFlow => FlowWorld.t_current?.CurrentScope != null;

    /// <summary>The clock of the current scope.</summary>
    public static Clock CurrentClock => CurrentScope(nameof(CurrentClock)).Clock;

    /// <summary>The scope path of the current scope, for logging.</summary>
    public static string CurrentScopePath => FlowWorld.t_current?.CurrentScope?.BuildScopePath();

    static FlowNode CurrentScope(string api) => FlowWorld.t_current?.CurrentScope ?? throw Errors.NoScope("Flow." + api);

    /// <summary>
    /// Starts <paramref name="task"/> as a child of the current scope, which does not wait for it: it is unwound when the
    /// scope ends. Await <c>handle.Join()</c> to wait for it. An exception that ends it while the scope runs cancels the
    /// scope (<see cref="CancelCause.Fault"/>) and reaches the scope's caller once the scope has unwound, as the scope's
    /// own would; a catch around the Spawn in the same method does not see it. In a canceled scope, until
    /// FlowCanceledException reaches its code, the task does not start. For work that outlives the scope, use
    /// <see cref="FlowWorld.Run(FlowTask, Clock)"/>.
    /// </summary>
    public static FlowHandle Spawn(FlowTask task)
    {
        var node = SpawnCore<FlowUnit>(task);
        return node == null ? default : new FlowHandle(node, node.Token);
    }

    /// <inheritdoc cref="Spawn(FlowTask)"/>
    public static FlowHandle<T> Spawn<T>(FlowTask<T> task)
    {
        var node = SpawnCore(task);
        return node == null ? default : new FlowHandle<T>(node, node.Token);
    }

    static FlowNode<T> SpawnCore<T>(FlowTask<T> task)
    {
        if (task.IsDefault) throw Errors.DefaultFlowTask(typeof(T));
        var scope = CurrentScope(nameof(Spawn));
        var node = Materialize(task);
        if (scope.IsCancelConfirmed && !scope.RunsCleanup)
        {
            node.ReleaseUnstarted();
            return null;
        }

        node.Flags |= NodeFlags.NoPool | NodeFlags.Spawned;
        node.Start(scope);
        return node;
    }

    /// <summary>
    /// The current scope owns <paramref name="resource"/>: it is disposed when the scope ends (after its finally blocks, LIFO
    /// with AddCleanup). Outside a flow nothing owns it, and the caller disposes it.
    /// </summary>
    public static T Own<T>(T resource) where T : class, IDisposable
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        FlowWorld.t_current?.CurrentScope?.AddCleanup(resource, CleanupKind.Disposable);
        return resource;
    }

    /// <summary>Runs <paramref name="action"/> when the current scope ends, however it ends (LIFO with Own).</summary>
    public static void AddCleanup(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        CurrentScope(nameof(AddCleanup)).AddCleanup(action, CleanupKind.Action);
    }

    /// <summary>AddCleanup with explicit state, which needs no closure.</summary>
    public static void AddCleanup<TState>(TState state, Action<TState> action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        CurrentScope(nameof(AddCleanup)).AddCleanup(CleanupThunk<TState>.Rent(state, action), CleanupKind.Thunk);
    }

    /// <summary>Runs <paramref name="task"/> and what it starts on <paramref name="clock"/>.</summary>
    public static FlowTask WithClock(Clock clock, FlowTask task)
    {
        if (clock == null) throw new ArgumentNullException(nameof(clock));
        if (Unstarted(task.Node, task.Token) is { } n) n.ClockOverride = clock;
        return task;
    }

    /// <inheritdoc cref="WithClock(Clock, FlowTask)"/>
    public static FlowTask<T> WithClock<T>(Clock clock, FlowTask<T> task)
    {
        if (clock == null) throw new ArgumentNullException(nameof(clock));
        if (Unstarted(task.Node, task.Token) is { } n) n.ClockOverride = clock;
        return task;
    }

    /// <summary>Overrides the name of <paramref name="task"/>'s scope in dumps and scope paths.</summary>
    public static FlowTask Named(string name, FlowTask task)
    {
        if (Unstarted(task.Node, task.Token) is { } n) n.CustomName = name;
        return task;
    }

    /// <inheritdoc cref="Named(string, FlowTask)"/>
    public static FlowTask<T> Named<T>(string name, FlowTask<T> task)
    {
        if (Unstarted(task.Node, task.Token) is { } n) n.CustomName = name;
        return task;
    }

    /// <summary>
    /// Keeps the cancellation of the scopes around from reaching <paramref name="task"/>: canceled before the await or
    /// while it waits, the scope still starts the task, waits for it and takes its result or exception, and the
    /// cancellation reaches the scope at its next await without this. For cleanup that must finish in a finally block the
    /// scope entered before it was canceled (by a return or an exception): once the cancellation has reached the scope,
    /// its catch and finally blocks run to their end without it. The task follows Pause and has no time limit (race it
    /// against a wait for one); World.Dispose still ends it. Await it directly in a FlowTask method: passed to
    /// Flow.Spawn, FlowWorld.Run or a combinator, it throws <see cref="FlowMisuseException"/>.
    /// </summary>
    public static FlowTask NonCancelable(FlowTask task)
    {
        if (Unstarted(task.Node, task.Token) is { } n) n.Flags |= NodeFlags.NonCancelable;
        return task;
    }

    /// <inheritdoc cref="NonCancelable(FlowTask)"/>
    public static FlowTask<T> NonCancelable<T>(FlowTask<T> task)
    {
        if (Unstarted(task.Node, task.Token) is { } n) n.Flags |= NodeFlags.NonCancelable;
        return task;
    }

    /// <summary>The unstarted node of a task (null for a completed value); a started or stale one is a misuse.</summary>
    static FlowNode Unstarted(FlowNode node, uint token)
    {
        if (node != null && (node.Token != token || node.State != NodeState.Unstarted)) throw Errors.AlreadyStarted(node, token);
        return node;
    }

    /// <summary>
    /// Creates a clock that the current scope owns, for time that belongs to one flow (one enemy's slow motion). It follows
    /// the pause and time scale of <paramref name="parent"/>, and is removed when the scope ends (LIFO with AddCleanup), after
    /// its children. A clock made so must belong to this scope or an ancestor to be a parent. A removed clock no longer
    /// advances: starting a task on it, Pause and TimeScale throw <see cref="FlowMisuseException"/>, and a wait still on it
    /// fails at the next Tick. Create a clock needed for a while in a child FlowTask method: a long-lived scope keeps every
    /// clock it creates until it ends.
    /// </summary>
    public static Clock CreateClock(string name, Clock parent)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));
        var scope = CurrentScope(nameof(CreateClock));
        return scope.World.ClockTree.CreateScopeClock(scope, name, parent);
    }

    /// <summary>Creates a clock that the current scope owns under <see cref="CurrentClock"/>; see <see cref="CreateClock(string, Clock)"/>.</summary>
    public static Clock CreateClock(string name)
    {
        var scope = CurrentScope(nameof(CreateClock));
        return scope.World.ClockTree.CreateScopeClock(scope, name, scope.Clock);
    }

    /// <summary>The node that runs <paramref name="task"/>: its own, or one that completes with its value.</summary>
    internal static FlowNode<T> Materialize<T>(FlowTask<T> task)
    {
        CheckStartable(task);
        return task.Node ?? ValueNode<T>.Create(task.Value);
    }

    /// <summary>
    /// Throws what <see cref="Materialize{T}"/> would, without creating anything: combinators check every argument before
    /// they take any.
    /// </summary>
    internal static void CheckStartable<T>(FlowTask<T> task)
    {
        if (task.Node == null)
        {
            if (task.Token == 0) throw Errors.DefaultFlowTask(typeof(T));
            return;
        }

        if (task.Node.Token != task.Token || task.Node.State != NodeState.Unstarted) throw Errors.AlreadyStarted(task.Node, task.Token);
        if ((task.Node.Flags & NodeFlags.NonCancelable) != 0) throw Errors.NonCancelableNotAwaited(task.Node);
    }
}
