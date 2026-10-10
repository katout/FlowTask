namespace Katout.FlowTask;

/// <summary>
/// The library's awaitable. Lazy: calling a FlowTask method does not run it; it starts when awaited, when passed to
/// Flow.Spawn or FlowWorld.Run, or when a combinator it was passed to starts, as a child of the scope that starts it.
/// Await it once.
/// </summary>
// partial only for the typed Race and WhenAll overloads that tools/gen_combinators.cs writes to Combinators.g.cs.
[AsyncMethodBuilder(typeof(FlowTaskMethodBuilder))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724", Justification = "FlowTask is the library's name and its central type, and the last part of its namespace Katout.FlowTask.")]
public readonly partial struct FlowTask
{
    internal readonly FlowNode<FlowUnit> Node;
    internal readonly uint Token;

    internal FlowTask(FlowNode<FlowUnit> node, uint token)
    {
        Node = node;
        Token = token;
    }

    /// <summary>
    /// A completed task; <c>default(FlowTask)</c> is the same. Awaiting it goes on at once, as a completed Task does, and
    /// counts as no synchronous completion: a loop whose only awaits are completed values never returns.
    /// </summary>
    public static FlowTask CompletedTask => default;

    /// <summary>
    /// The task's status, while the task is this one: Invalid once it was consumed and its node reused, and for a
    /// NextFrame, DelayFrames or WaitForSeconds that a scope awaited directly (it ran without a node).
    /// </summary>
    public FlowStatus Status => Node == null ? FlowStatus.Succeeded : Node.Token == Token ? Node.Status : FlowStatus.Invalid;

    /// <summary>Starts the task as a child of the current scope (the await of a FlowTask method calls it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Awaiter GetAwaiter() => new(this);

    /// <summary>
    /// Releases a task that will never be started (a branch left out before awaiting it). No effect once started. Not
    /// UniTask's <c>Forget()</c>: to run a task without awaiting it, use <see cref="Flow.Spawn(FlowTask)"/> or
    /// <see cref="FlowWorld.Run(FlowTask, Clock)"/>.
    /// </summary>
    public void Discard()
    {
        if (Node != null && Node.Token == Token && Node.State == NodeState.Unstarted) Node.ReleaseUnstarted();
    }

    /// <summary>The same task, with <see cref="FlowUnit"/> as its result.</summary>
    public static implicit operator FlowTask<FlowUnit>(FlowTask task) =>
        task.Node == null ? new FlowTask<FlowUnit>(FlowUnit.Default) : new FlowTask<FlowUnit>(task.Node, task.Token);

    /// <summary><c>FlowTask(name, status)</c>.</summary>
    public override string ToString() => Node == null ? "FlowTask(Succeeded)" : $"FlowTask({Node.DisplayName}, {Status})";

    /// <summary>A completed task with a value; <see cref="CompletedTask"/> tells how its await is counted.</summary>
    public static FlowTask<T> FromResult<T>(T value) => new(value);

    /// <summary>
    /// Waits <paramref name="seconds"/> (not milliseconds) of the scope's clock, or of <paramref name="clock"/>: it resumes in
    /// the first Tick whose accumulated time reaches the target. The time adds up in double precision, so a duration on a
    /// frame boundary can end one frame later; <see cref="DelayFrames"/> waits an exact number of frames.
    /// </summary>
    public static FlowTask WaitForSeconds(double seconds, Clock clock = null)
    {
        if (seconds < 0 || double.IsNaN(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var n = WaitForSecondsNode.Rent(seconds, clock);
        return new FlowTask(n, n.Token);
    }

    /// <summary>
    /// Waits <paramref name="frameCount"/> frames of the scope's clock, or of <paramref name="clock"/>: Ticks in which the
    /// clock is not paused (<see cref="Clock.FrameCount"/>). Zero completes at once; otherwise it never resumes in the
    /// flush in which it started.
    /// </summary>
    public static FlowTask DelayFrames(int frameCount, Clock clock = null)
    {
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        var n = DelayFramesNode.Rent(frameCount, clock);
        return new FlowTask(n, n.Token);
    }

    /// <summary>Waits for the next frame of the scope's clock: <c>DelayFrames(1)</c>.</summary>
    public static FlowTask NextFrame(Clock clock = null) => DelayFrames(1, clock);

    /// <summary>
    /// Waits until <paramref name="predicate"/> returns true: checked when awaited, then in every Tick in which the clock is
    /// not paused. An exception of the predicate is thrown at the await.
    /// </summary>
    public static FlowTask WaitUntil(Func<bool> predicate, Clock clock = null)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        var n = WaitUntilNode<Func<bool>>.Rent(predicate, static p => p(), clock);
        return new FlowTask(n, n.Token);
    }

    /// <summary>WaitUntil with explicit state, so that a static lambda needs no closure.</summary>
    public static FlowTask WaitUntil<TState>(TState state, Func<TState, bool> predicate, Clock clock = null)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        var n = WaitUntilNode<TState>.Rent(state, predicate, clock);
        return new FlowTask(n, n.Token);
    }

    /// <summary>A task that never completes: it ends when its scope is canceled.</summary>
    public static FlowTask Never()
    {
        var n = NeverNode.Rent();
        return new FlowTask(n, n.Token);
    }

    /// <summary>
    /// Waits for every task. When one throws, the others are canceled and unwound before the exception is thrown at the
    /// await (Task.WhenAll lets them run on). Each branch runs as a child of the combinator. For 2-3 branches use the typed
    /// overloads.
    /// </summary>
    public static FlowTask WhenAll(IReadOnlyList<FlowTask> tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        for (var i = 0; i < tasks.Count; i++) Flow.CheckStartable<FlowUnit>(tasks[i]);
        var n = WhenAllVoidNode.Rent(tasks.Count);
        for (var i = 0; i < tasks.Count; i++) n.AddBranch(Flow.Materialize<FlowUnit>(tasks[i]));
        return new FlowTask(n, n.Token);
    }

    /// <summary>Waits for every task and returns their values in order; see <see cref="WhenAll(IReadOnlyList{FlowTask})"/>.</summary>
    public static FlowTask<T[]> WhenAll<T>(IReadOnlyList<FlowTask<T>> tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        for (var i = 0; i < tasks.Count; i++) Flow.CheckStartable(tasks[i]);
        var n = WhenAllArrayNode<T>.Rent(tasks.Count);
        for (var i = 0; i < tasks.Count; i++) n.AddBranch(Flow.Materialize(tasks[i]));
        return new FlowTask<T[]>(n, n.Token);
    }

    /// <summary>
    /// Returns the index of the first task to complete; the others are canceled and unwound (their finally blocks run to
    /// their end) before the caller resumes. A task that throws first ends the Race as in WhenAll.
    /// </summary>
    /// <remarks>
    /// The tasks start in order, each up to its first wait, and one that completes as it starts wins at once. Tasks that
    /// complete in the same flush win in the order they are processed (time waits satisfied by one Tick, in the order they
    /// began), so put an event before the work it interrupts: <c>Race(hits.Next(), Patrol())</c>. A value that a losing
    /// wait on a <see cref="Subscription{T}"/> already received goes back to the subscription. For a timeout, race the
    /// work against <see cref="WaitForSeconds"/>.
    /// </remarks>
    public static FlowTask<int> Race(IReadOnlyList<FlowTask> tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        if (tasks.Count == 0) throw new ArgumentException("Race needs at least one task.", nameof(tasks));
        for (var i = 0; i < tasks.Count; i++) Flow.CheckStartable<FlowUnit>(tasks[i]);
        var n = RaceIndexNode.Rent(tasks.Count);
        for (var i = 0; i < tasks.Count; i++) n.AddBranch(Flow.Materialize<FlowUnit>(tasks[i]));
        return new FlowTask<int>(n, n.Token);
    }

    /// <summary>Returns the index and value of the first task to complete; see <see cref="Race(IReadOnlyList{FlowTask})"/>.</summary>
    public static FlowTask<RaceResult<T>> Race<T>(IReadOnlyList<FlowTask<T>> tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        if (tasks.Count == 0) throw new ArgumentException("Race needs at least one task.", nameof(tasks));
        for (var i = 0; i < tasks.Count; i++) Flow.CheckStartable(tasks[i]);
        var n = RaceArrayNode<T>.Rent(tasks.Count);
        for (var i = 0; i < tasks.Count; i++) n.AddBranch(Flow.Materialize(tasks[i]));
        return new FlowTask<RaceResult<T>>(n, n.Token);
    }

    /// <summary>The awaiter of a FlowTask; for the compiler. Used by hand only while <see cref="IsCompleted"/> is true.</summary>
    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        readonly FlowNode<FlowUnit> _node;
        readonly FlowNode _scope;
        readonly uint _token;
        readonly AwaitMode _mode;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Awaiter(in FlowTask task)
        {
            _node = task.Node;
            _token = task.Token;
            if (_node == null)
            {
                _scope = null;
                _mode = AwaitMode.Value;
            }
            else
            {
                _mode = AwaitCore.Begin(_node, _token, out _scope, allowPark: true);
            }
        }

        /// <summary>True when the task completed as it started; fixed when the awaiter is made.</summary>
        public bool IsCompleted
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => AwaitCore.IsCompleted(_mode);
        }

        /// <summary>Ends the await: throws the task's exception, or FlowCanceledException when the scope unwinds.</summary>
        [DebuggerHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetResult()
        {
            if (_mode == AwaitMode.Value) return;
            if (_mode == AwaitMode.Parkable)
            {
                switch (AwaitCore.EndPark(_node, _token, _scope))
                {
                    case ParkEnd.Resumed:
                        return;
                    case ParkEnd.Unwind:
                        throw new FlowCanceledException();
                }

                // NotParked: the awaiter was driven by hand and the node started; it ends as any node's await.
            }

            // Thrown here, not in a helper, to keep the unwinding stack short: the runtime allocates per captured frame.
            if (AwaitCore.PrepareUnwind(_mode, _node, _token, _scope)) throw new FlowCanceledException();
            AwaitCore.Consume(_node, _scope);
        }

        /// <summary>Registers the continuation of a FlowTask method; any other caller ends the scope with a FlowMisuseException.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCompleted(Action continuation) => AwaitCore.OnCompleted(_mode, _node, _token, _scope, continuation);

        /// <inheritdoc cref="OnCompleted"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeOnCompleted(Action continuation) => AwaitCore.OnCompleted(_mode, _node, _token, _scope, continuation);
    }
}

/// <summary>
/// The library's awaitable with a result; see <see cref="FlowTask"/>. <c>default(FlowTask&lt;T&gt;)</c> is not a task:
/// awaiting it, or passing it to Run, Spawn or a combinator, throws <see cref="FlowMisuseException"/>.
/// </summary>
[AsyncMethodBuilder(typeof(FlowTaskMethodBuilder<>))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724", Justification = "FlowTask is the library's name and its central type, and the last part of its namespace Katout.FlowTask.")]
public readonly struct FlowTask<T>
{
    /// <summary>The token of a completed value task (no node). Token 0 without a node is <c>default(FlowTask&lt;T&gt;)</c>.</summary>
    internal const uint ValueToken = 1;

    internal readonly FlowNode<T> Node;
    internal readonly uint Token;
    internal readonly T Value;

    internal FlowTask(FlowNode<T> node, uint token)
    {
        Node = node;
        Token = token;
        Value = default;
    }

    internal FlowTask(T value)
    {
        Node = null;
        Token = ValueToken;
        Value = value;
    }

    internal bool IsDefault => Node == null && Token == 0;

    /// <summary>
    /// The task's status, while the task is this one: Invalid once it was consumed and its node reused, and for
    /// <c>default(FlowTask&lt;T&gt;)</c>, which is not a task.
    /// </summary>
    public FlowStatus Status => Node == null ? (Token == 0 ? FlowStatus.Invalid : FlowStatus.Succeeded) : Node.Token == Token ? Node.Status : FlowStatus.Invalid;

    /// <inheritdoc cref="FlowTask.GetAwaiter"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Awaiter GetAwaiter() => new(this);

    /// <inheritdoc cref="FlowTask.Discard"/>
    public void Discard()
    {
        if (Node != null && Node.Token == Token && Node.State == NodeState.Unstarted) Node.ReleaseUnstarted();
    }

    /// <summary><c>FlowTask(name, status)</c>, <c>FlowTask(value)</c> or <c>FlowTask(default)</c>.</summary>
    public override string ToString() => Node == null ? (Token == 0 ? "FlowTask(default)" : $"FlowTask({Value})") : $"FlowTask({Node.DisplayName}, {Status})";

    /// <inheritdoc cref="FlowTask.Awaiter"/>
    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        readonly FlowNode<T> _node;
        readonly FlowNode _scope;
        readonly uint _token;
        readonly AwaitMode _mode;
        readonly T _value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Awaiter(in FlowTask<T> task)
        {
            _node = task.Node;
            _token = task.Token;
            if (_node == null)
            {
                if (_token == 0) Errors.ThrowDefaultFlowTask(typeof(T));
                _scope = null;
                _mode = AwaitMode.Value;
                _value = task.Value;
            }
            else
            {
                _value = default;
                _mode = AwaitCore.Begin(_node, _token, out _scope, allowPark: false);
            }
        }

        /// <summary>True when the task completed as it started; fixed when the awaiter is made.</summary>
        public bool IsCompleted
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => AwaitCore.IsCompleted(_mode);
        }

        /// <summary>Ends the await with the task's value: throws its exception, or FlowCanceledException when the scope unwinds.</summary>
        [DebuggerHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetResult()
        {
            if (_mode == AwaitMode.Value) return _value;
            if (AwaitCore.PrepareUnwind(_mode, _node, _token, _scope)) throw new FlowCanceledException();
            return AwaitCore.Consume(_node, _scope);
        }

        /// <summary>Registers the continuation of a FlowTask method; any other caller ends the scope with a FlowMisuseException.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCompleted(Action continuation) => AwaitCore.OnCompleted(_mode, _node, _token, _scope, continuation);

        /// <inheritdoc cref="OnCompleted"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeOnCompleted(Action continuation) => AwaitCore.OnCompleted(_mode, _node, _token, _scope, continuation);
    }
}
