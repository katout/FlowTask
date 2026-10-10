namespace Katout.FlowTask.Analyzers;

/// <summary>
/// FlowTask symbols resolved once per compilation. Types are identified by metadata name and the marker attribute
/// [LifetimeHandle], never by the spelling of the syntax; awaitables by the awaiter they return.
/// </summary>
internal sealed class FlowTaskTypes
{
    public const string FlowTaskMetadataName = "Katout.FlowTask.FlowTask";
    public const string FlowTaskOfTMetadataName = "Katout.FlowTask.FlowTask`1";
    public const string TaskBridgeMetadataName = "Katout.FlowTask.TaskBridge";
    public const string TaskBridgeOfTMetadataName = "Katout.FlowTask.TaskBridge`1";
    public const string FlowMetadataName = "Katout.FlowTask.Flow";
    public const string FlowBridgeMetadataName = "Katout.FlowTask.FlowBridge";
    public const string FlowWorldMetadataName = "Katout.FlowTask.FlowWorld";
    public const string SignalOfTMetadataName = "Katout.FlowTask.Signal`1";
    public const string EventSignalOfTMetadataName = "Katout.FlowTask.EventSignal`1";
    public const string BufferPolicyMetadataName = "Katout.FlowTask.BufferPolicy";
    public const string FlowCanceledExceptionMetadataName = "Katout.FlowTask.FlowCanceledException";
    public const string LifetimeHandleAttributeMetadataName = "Katout.FlowTask.LifetimeHandleAttribute";

    FlowTaskTypes(Compilation compilation, INamedTypeSymbol flowTask, INamedTypeSymbol flowTaskOfT)
    {
        FlowTask = flowTask;
        FlowTaskOfT = flowTaskOfT;
        FlowTaskAwaiter = NestedAwaiter(flowTask);
        FlowTaskOfTAwaiter = NestedAwaiter(flowTaskOfT);
        TaskBridge = compilation.GetTypeByMetadataName(TaskBridgeMetadataName);
        TaskBridgeOfT = compilation.GetTypeByMetadataName(TaskBridgeOfTMetadataName);
        Flow = compilation.GetTypeByMetadataName(FlowMetadataName);
        FlowBridge = compilation.GetTypeByMetadataName(FlowBridgeMetadataName);
        FlowWorld = compilation.GetTypeByMetadataName(FlowWorldMetadataName);
        SignalOfT = compilation.GetTypeByMetadataName(SignalOfTMetadataName);
        EventSignalOfT = compilation.GetTypeByMetadataName(EventSignalOfTMetadataName);
        BufferPolicy = compilation.GetTypeByMetadataName(BufferPolicyMetadataName);
        FlowCanceledException = compilation.GetTypeByMetadataName(FlowCanceledExceptionMetadataName);
        LifetimeHandleAttribute = compilation.GetTypeByMetadataName(LifetimeHandleAttributeMetadataName);
        Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        TaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        ValueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        ValueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        IDisposable = compilation.GetSpecialType(SpecialType.System_IDisposable);
        ConfiguredTaskAwaitable = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredTaskAwaitable");
        ConfiguredTaskAwaitableOfT = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1");
        ConfiguredValueTaskAwaitable = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable");
        ConfiguredValueTaskAwaitableOfT = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1");
        YieldAwaitable = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.YieldAwaitable");
        UniTaskYieldAwaitable = compilation.GetTypeByMetadataName("Cysharp.Threading.Tasks.YieldAwaitable");
    }

    public INamedTypeSymbol FlowTask { get; }
    public INamedTypeSymbol FlowTaskOfT { get; }

    /// <summary>FlowTask.Awaiter: with FlowTask&lt;T&gt;.Awaiter, the only awaiters that register with the current scope.</summary>
    public INamedTypeSymbol FlowTaskAwaiter { get; }

    /// <summary>FlowTask&lt;T&gt;.Awaiter (its definition).</summary>
    public INamedTypeSymbol FlowTaskOfTAwaiter { get; }

    public INamedTypeSymbol TaskBridge { get; }
    public INamedTypeSymbol TaskBridgeOfT { get; }
    public INamedTypeSymbol Flow { get; }
    public INamedTypeSymbol FlowBridge { get; }
    public INamedTypeSymbol FlowWorld { get; }
    public INamedTypeSymbol SignalOfT { get; }
    public INamedTypeSymbol EventSignalOfT { get; }
    public INamedTypeSymbol BufferPolicy { get; }
    public INamedTypeSymbol FlowCanceledException { get; }
    public INamedTypeSymbol LifetimeHandleAttribute { get; }
    public INamedTypeSymbol Task { get; }
    public INamedTypeSymbol TaskOfT { get; }
    public INamedTypeSymbol ValueTask { get; }
    public INamedTypeSymbol ValueTaskOfT { get; }
    public INamedTypeSymbol IDisposable { get; }
    public INamedTypeSymbol ConfiguredTaskAwaitable { get; }
    public INamedTypeSymbol ConfiguredTaskAwaitableOfT { get; }
    public INamedTypeSymbol ConfiguredValueTaskAwaitable { get; }
    public INamedTypeSymbol ConfiguredValueTaskAwaitableOfT { get; }
    public INamedTypeSymbol YieldAwaitable { get; }

    /// <summary>UniTask.Yield()'s awaitable (null when UniTask is not referenced).</summary>
    public INamedTypeSymbol UniTaskYieldAwaitable { get; }

    static INamedTypeSymbol NestedAwaiter(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers("Awaiter")) return nested;
        return null;
    }

    /// <summary>Returns null when the compilation does not reference FlowTask (nothing to analyze).</summary>
    public static FlowTaskTypes TryCreate(Compilation compilation)
    {
        var flowTask = compilation.GetTypeByMetadataName(FlowTaskMetadataName);
        var flowTaskOfT = compilation.GetTypeByMetadataName(FlowTaskOfTMetadataName);
        if (flowTask == null || flowTaskOfT == null) return null;
        return new FlowTaskTypes(compilation, flowTask, flowTaskOfT);
    }

    /// <summary>Katout.FlowTask.FlowTask or Katout.FlowTask.FlowTask&lt;T&gt; (a Nullable wrapper, from '?.', is looked through).</summary>
    public bool IsFlowTask(ITypeSymbol type)
    {
        type = UnwrapNullable(type);
        if (type == null) return false;
        var definition = type.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition, FlowTask) ||
               SymbolEqualityComparer.Default.Equals(definition, FlowTaskOfT);
    }

    /// <summary>
    /// A lazy task that does nothing until awaited: FlowTask, FlowTask&lt;T&gt;, and the bridges TaskBridge /
    /// TaskBridge&lt;T&gt; (whose factory only runs when the bridged FlowTask starts). Dropping one is FLOW003.
    /// </summary>
    public bool IsLazyTask(ITypeSymbol type)
    {
        if (IsFlowTask(type)) return true;
        type = UnwrapNullable(type);
        if (type == null) return false;
        var definition = type.OriginalDefinition;
        return (TaskBridge != null && SymbolEqualityComparer.Default.Equals(definition, TaskBridge)) ||
               (TaskBridgeOfT != null && SymbolEqualityComparer.Default.Equals(definition, TaskBridgeOfT));
    }

    /// <summary>An array of lazy tasks, or a type that implements IEnumerable&lt;T&gt; of a lazy task (List&lt;FlowTask&gt;, ...).</summary>
    public bool IsLazyTaskCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return IsLazyTask(array.ElementType);
        if (type is not INamedTypeSymbol named) return false;
        if (IsEnumerableOfLazyTask(named)) return true;
        foreach (var i in named.AllInterfaces)
        {
            if (IsEnumerableOfLazyTask(i)) return true;
        }

        return false;
    }

    bool IsEnumerableOfLazyTask(INamedTypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
        type.TypeArguments.Length == 1 &&
        IsLazyTask(type.TypeArguments[0]);

    /// <summary>
    /// FlowTask.Awaiter or FlowTask&lt;T&gt;.Awaiter. Only these register with the current scope at runtime (the
    /// awaiter itself does it): a user type is one only when its GetAwaiter returns one of them.
    /// </summary>
    public bool IsFlowTaskAwaiter(ITypeSymbol awaiter)
    {
        var definition = awaiter?.OriginalDefinition;
        return definition != null &&
               ((FlowTaskAwaiter != null && SymbolEqualityComparer.Default.Equals(definition, FlowTaskAwaiter)) ||
                (FlowTaskOfTAwaiter != null && SymbolEqualityComparer.Default.Equals(definition, FlowTaskOfTAwaiter)));
    }

    /// <summary>
    /// A type whose own GetAwaiter() returns FlowTask's awaiter: FlowTask, FlowTask&lt;T&gt;, Once&lt;T&gt;, TaskBridge,
    /// TaskBridge&lt;T&gt;. It registers with the current scope when awaited.
    /// </summary>
    public bool IsLibraryAwaitable(ITypeSymbol type)
    {
        if (type is null or { TypeKind: TypeKind.Error }) return false;
        foreach (var member in type.GetMembers("GetAwaiter"))
        {
            if (member is IMethodSymbol method && !method.IsStatic && method.Parameters.Length == 0 &&
                method.TypeParameters.Length == 0 && IsFlowTaskAwaiter(method.ReturnType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="awaitExpression"/> awaits through a FlowTask awaiter (FlowTask.Awaiter or
    /// FlowTask&lt;T&gt;.Awaiter), which registers with the current scope. Judged by the awaiter that GetAwaiter
    /// returns, so a user-written GetAwaiter extension returning FlowTask's awaiter counts; falls back to the awaited
    /// type when the awaiter is unknown (dynamic, errors).
    /// </summary>
    public bool AwaitsThroughFlowAwaiter(AwaitExpressionSyntax awaitExpression, SemanticModel model, CancellationToken cancellationToken)
    {
        var awaiter = model.GetAwaitExpressionInfo(awaitExpression).GetAwaiterMethod?.ReturnType;
        if (awaiter is { TypeKind: not TypeKind.Error }) return IsFlowTaskAwaiter(awaiter);
        return IsLibraryAwaitable(model.GetTypeInfo(awaitExpression.Expression, cancellationToken).Type);
    }

    /// <summary>Task, Task&lt;T&gt;, ValueTask, ValueTask&lt;T&gt;, or what their ConfigureAwait returns.</summary>
    public bool IsTaskLikeOrConfigured(ITypeSymbol type)
    {
        if (IsTaskLike(type)) return true;
        if (type == null) return false;
        var definition = type.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition, ConfiguredTaskAwaitable) ||
               SymbolEqualityComparer.Default.Equals(definition, ConfiguredTaskAwaitableOfT) ||
               SymbolEqualityComparer.Default.Equals(definition, ConfiguredValueTaskAwaitable) ||
               SymbolEqualityComparer.Default.Equals(definition, ConfiguredValueTaskAwaitableOfT);
    }

    /// <summary>Task.Yield()'s or UniTask.Yield()'s awaitable: resumes on the next turn of a foreign scheduler.</summary>
    public bool IsYieldAwaitable(ITypeSymbol type) =>
        type != null &&
        ((YieldAwaitable != null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, YieldAwaitable)) ||
         (UniTaskYieldAwaitable != null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, UniTaskYieldAwaitable)));

    /// <summary>Signal&lt;T&gt; or EventSignal&lt;T&gt;: Next() waits for the next emit only.</summary>
    public bool IsSignal(ITypeSymbol type)
    {
        var definition = type?.OriginalDefinition;
        return definition != null &&
               ((SignalOfT != null && SymbolEqualityComparer.Default.Equals(definition, SignalOfT)) ||
                (EventSignalOfT != null && SymbolEqualityComparer.Default.Equals(definition, EventSignalOfT)));
    }

    /// <summary>A type marked [LifetimeHandle] (ScopedHandle, Subscription, EventSignal).</summary>
    public bool IsLifetimeHandle(ITypeSymbol type) => HasAttribute(UnwrapNullable(type), LifetimeHandleAttribute);

    /// <summary>System.Threading.Tasks.Task, Task&lt;T&gt;, ValueTask or ValueTask&lt;T&gt; (bridgeable with AsFlow()).</summary>
    public bool IsTaskLike(ITypeSymbol type)
    {
        if (type == null) return false;
        var definition = type.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition, Task) ||
               SymbolEqualityComparer.Default.Equals(definition, TaskOfT) ||
               SymbolEqualityComparer.Default.Equals(definition, ValueTask) ||
               SymbolEqualityComparer.Default.Equals(definition, ValueTaskOfT);
    }

    /// <summary>
    /// True when a catch clause for <paramref name="caughtType"/> can receive FlowCanceledException: the caught type is
    /// FlowCanceledException itself or one of its base types (System.Exception, System.Object).
    /// </summary>
    public bool CanCatchFlowCanceledException(ITypeSymbol caughtType)
    {
        if (caughtType is null or { TypeKind: TypeKind.Error }) return false;
        if (FlowCanceledException == null)
        {
            // Should not happen with a real FlowTask reference; fall back to the catch-all types.
            return caughtType.SpecialType == SpecialType.System_Object ||
                   caughtType.ToDisplayString() == "System.Exception";
        }

        for (ITypeSymbol t = FlowCanceledException; t != null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, caughtType)) return true;
        }

        return false;
    }

    /// <summary>A method declared on Katout.FlowTask.Flow with the given name.</summary>
    public bool IsFlowMethod(IMethodSymbol method, string name) =>
        method != null && method.Name == name &&
        SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, Flow);

    public bool IsDisposable(ITypeSymbol type)
    {
        type = UnwrapNullable(type);
        if (type == null || IDisposable == null) return false;
        if (SymbolEqualityComparer.Default.Equals(type, IDisposable)) return true;
        foreach (var i in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(i, IDisposable)) return true;
        }

        return false;
    }

    static bool HasAttribute(ITypeSymbol type, INamedTypeSymbol attribute)
    {
        if (type == null || attribute == null || type.TypeKind == TypeKind.Error) return false;
        foreach (var a in type.OriginalDefinition.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)) return true;
        }

        return false;
    }

    public static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.Length == 1)
        {
            return named.TypeArguments[0];
        }

        return type;
    }
}
