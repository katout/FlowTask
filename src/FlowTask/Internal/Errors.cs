namespace Katout.FlowTask.Internal;

internal static class Errors
{
    /// <summary>A FlowTask method suspended on an awaiter that is not the library's.</summary>
    internal static Exception UnbridgedAwait(string scopePath, Type awaiterType) =>
        new FlowMisuseException(
            $"FlowTask method '{scopePath}' suspended on {TypeName(awaiterType)}, which is not a FlowTask: a flow cannot be canceled while it waits on a " +
            "foreign awaitable, so the await was abandoned, and the rest of the method, its finally blocks and using statements do not run " +
            "(AddCleanup and Own do). Bridge it (FLOW002): FlowBridge.FromTask(ct => ...) starts the operation with the scope's token; " +
            ".AsFlow() wraps one that already runs (Task, ValueTask, UniTask, Unity Awaitable and AsyncOperation, Godot ToSignal).");

    /// <summary><c>TaskAwaiter&lt;Int32&gt;</c> rather than <c>TaskAwaiter`1</c>.</summary>
    static string TypeName(Type t)
    {
        var name = t.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        if (t.IsNested && !t.IsGenericParameter) name = TypeName(t.DeclaringType) + "." + name;
        if (!t.IsGenericType || t.IsGenericTypeDefinition) return name;
        var args = t.GetGenericArguments();
        var names = new string[args.Length];
        for (var i = 0; i < args.Length; i++) names[i] = TypeName(args[i]);
        return name + "<" + string.Join(", ", names) + ">";
    }

    internal static Exception SwallowedCancellation(string scopePath) =>
        new FlowMisuseException(
            $"Scope '{scopePath}' returned after FlowCanceledException reached its code, so a catch took it and did not rethrow it; the scope was ended as canceled. " +
            "End a catch of FlowCanceledException or OperationCanceledException with 'throw;', and filter a catch-all with 'when (e is not FlowCanceledException)' (FLOW001).");

    internal static Exception AwaitInANonFlowTaskMethod(string scopePath) =>
        new FlowMisuseException(
            $"A method that is not a FlowTask method (async Task, ValueTask, UniTask or async void) awaited a FlowTask in scope '{scopePath}', and the await did not " +
            "complete at once. Only a FlowTask method can wait in a flow: the method never resumes, and the scope is ended. Make the method return FlowTask " +
            "(FLOW005), or wait for the flow from outside with FlowWorld.Run(task).AsTask().");

    internal static Exception AwaitOutsideFlow() =>
        new FlowMisuseException("A FlowTask can only be awaited inside a FlowTask method running in a World (FLOW005). Start it with FlowWorld.Run; code that is not a flow awaits the handle's AsTask().");

    internal static Exception AlreadyStarted(FlowNode node, uint token) =>
        node.Token != token
            ? new FlowMisuseException("This FlowTask has already been awaited and consumed. A FlowTask can be awaited only once; use Once<T> for several waiters.")
            : new FlowMisuseException($"FlowTask '{node.DisplayName}' has already been started. A FlowTask can be awaited or passed to a combinator only once.");

    internal static Exception NonCancelableNotAwaited(FlowNode node) =>
        new FlowMisuseException(
            $"FlowTask '{node.DisplayName}' is marked with Flow.NonCancelable, which only an await in a FlowTask method honors. Started by Flow.Spawn, " +
            "FlowWorld.Run or a combinator, it could never be canceled; await it directly, and put a Race or WhenAll inside the mark: Flow.NonCancelable(FlowTask.Race(...)).");

    internal static Exception Consumed() => new FlowMisuseException("The FlowTask result was already consumed (an awaiter used twice?).");

    internal static Exception NoScope(string api) => new FlowMisuseException($"{api} must be called inside a FlowTask method running in a World.");

    internal static Exception Reentrant(string api) =>
        new FlowMisuseException($"FlowWorld.{api} cannot be called while the World runs flow code, a cleanup or a handler. Call it from the engine's loop.");

    internal static Exception DefaultFlowTask(Type resultType) =>
        new FlowMisuseException($"default(FlowTask<{TypeName(resultType)}>) is not a task. Use FlowTask.FromResult(value) for a completed one.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowDefaultFlowTask(Type resultType) => throw DefaultFlowTask(resultType);

    internal static Exception EmitFromAnyThreadUnbound() =>
        new FlowMisuseException(
            "EmitFromAnyThread was called on a signal that no World uses yet, so no World's inbox could take the value. Create it with new Signal<T>(world), " +
            "or let the World use it first on its own thread (create it in one of its flows, or wait on it or subscribe to it there).");

    internal static Exception RemovedClock(Clock clock) =>
        new FlowMisuseException($"Clock '{clock.Name}' was removed when its scope '{clock.OwnerName}' ended (Flow.CreateClock): it cannot be waited on, run on, paused or scaled. Use FlowWorld.CreateClock for a clock that outlives the scope.");

    internal static Exception ClockNotUsable(Clock clock) =>
        clock.IsRemoved ? RemovedClock(clock) : new FlowMisuseException($"Clock '{clock.Name}' belongs to another World.");
}
