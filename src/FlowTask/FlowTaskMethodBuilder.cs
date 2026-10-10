namespace Katout.FlowTask;

/// <summary>The async method builder of <see cref="FlowTask"/>: Start copies the state machine into a scope and does not run it.</summary>
public struct FlowTaskMethodBuilder
{
    StateMachineScope<FlowUnit> _scope;

    /// <summary>The builder of a new call; for the compiler.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FlowTaskMethodBuilder Create() => default;

    /// <summary>The task of this call; for the compiler.</summary>
    public FlowTask Task => new(_scope, _scope.Token);

    /// <summary>Copies the state machine into a pooled scope, without running it (the task is lazy); for the compiler.</summary>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        var scope = FlowMethodScope<TStateMachine, FlowUnit>.Rent();
        // _scope first: the copy of the state machine holds a copy of this builder, which must refer to the scope.
        _scope = scope;
        scope.StateMachine = stateMachine;
    }

    /// <summary>Not used: the state machine is copied into its scope in Start. For the compiler.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance method required by the async method builder pattern.")]
    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    /// <summary>The method returned; for the compiler.</summary>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetResult() => _scope.OnSetResult(FlowUnit.Default);

    /// <summary>The method threw; for the compiler.</summary>
    [DebuggerHidden]
    public void SetException(Exception exception) => _scope.OnSetException(exception);

    /// <summary>The method suspends at an await; for the compiler.</summary>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var c = _scope.BeforeSuspend<TAwaiter>();
        if (c != null) awaiter.OnCompleted(c);
    }

    /// <inheritdoc cref="AwaitOnCompleted"/>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var c = _scope.BeforeSuspend<TAwaiter>();
        if (c != null) awaiter.UnsafeOnCompleted(c);
    }
}

/// <summary>The async method builder of <see cref="FlowTask{T}"/>.</summary>
public struct FlowTaskMethodBuilder<T>
{
    StateMachineScope<T> _scope;

    /// <summary>The builder of a new call; for the compiler.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FlowTaskMethodBuilder<T> Create() => default;

    /// <summary>The task of this call; for the compiler.</summary>
    public FlowTask<T> Task => new(_scope, _scope.Token);

    /// <summary>Copies the state machine into a pooled scope, without running it (the task is lazy); for the compiler.</summary>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        var scope = FlowMethodScope<TStateMachine, T>.Rent();
        _scope = scope;
        scope.StateMachine = stateMachine;
    }

    /// <summary>Not used: the state machine is copied into its scope in Start. For the compiler.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance method required by the async method builder pattern.")]
    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    /// <summary>The method returned; for the compiler.</summary>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetResult(T result) => _scope.OnSetResult(result);

    /// <summary>The method threw; for the compiler.</summary>
    [DebuggerHidden]
    public void SetException(Exception exception) => _scope.OnSetException(exception);

    /// <summary>The method suspends at an await; for the compiler.</summary>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var c = _scope.BeforeSuspend<TAwaiter>();
        if (c != null) awaiter.OnCompleted(c);
    }

    /// <inheritdoc cref="AwaitOnCompleted"/>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var c = _scope.BeforeSuspend<TAwaiter>();
        if (c != null) awaiter.UnsafeOnCompleted(c);
    }
}
