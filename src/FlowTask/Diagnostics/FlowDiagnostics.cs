namespace Katout.FlowTask.Diagnostics;

/// <summary>Tooling view of a World: the scope tree. Not for game logic.</summary>
public readonly struct FlowDiagnostics
{
    readonly FlowWorld _world;

    internal FlowDiagnostics(FlowWorld world) => _world = world;

    /// <summary>The World's root scope.</summary>
    public FlowScopeInfo Root => new(_world.Root);

    /// <summary>
    /// Every node, depth first: scopes, combinators and waits. A NextFrame, DelayFrames or WaitForSeconds that a scope
    /// awaits directly has no node: the scope's <see cref="FlowScopeInfo.Waiting"/> describes it, and
    /// <see cref="FlowScopeInfo.WaitingFile"/> and <see cref="FlowScopeInfo.WaitingLine"/> give its place.
    /// </summary>
    public IEnumerable<FlowScopeInfo> Walk()
    {
        var stack = new Stack<FlowNode>();
        stack.Push(_world.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return new FlowScopeInfo(n);
            for (var c = n.LastChild; c != null; c = c.PrevSibling) stack.Push(c);
        }
    }
}

/// <summary>A read-only view of a node of the scope tree, for dumps, tools and tests.</summary>
public readonly struct FlowScopeInfo
{
    readonly FlowNode _node;
    readonly uint _token;

    internal FlowScopeInfo(FlowNode node)
    {
        _node = node;
        _token = node?.Token ?? 0;
    }

    /// <summary>True while the node is in the tree; once it has ended, the other members return empty values.</summary>
    public bool IsValid => _node != null && _node.Token == _token;

    /// <summary>The method name of a scope, the name given with Flow.Named, or what a wait or combinator is.</summary>
    public string Name => IsValid ? _node.DisplayName : "<gone>";

    /// <summary>For the scope of an async FlowTask method: the type that declares it (compiler-generated closure classes skipped), for tools that open its source.</summary>
    public Type DeclaringType => IsValid ? _node.SourceType : null;

    /// <summary>For the scope of an async FlowTask method: its name in the source (a lambda gives the method that contains it).</summary>
    public string MethodName => IsValid ? _node.SourceMethod : null;

    /// <summary>What the node is; <see cref="FlowScopeKind.Invalid"/> once it is gone.</summary>
    public FlowScopeKind Kind =>
        !IsValid ? FlowScopeKind.Invalid
        : _node is RootNode ? FlowScopeKind.Root
        : _node.IsStateMachine ? FlowScopeKind.Scope
        : _node.IsLeaf ? FlowScopeKind.Wait
        : FlowScopeKind.Combinator;

    /// <summary>The node's status.</summary>
    public FlowStatus Status => IsValid ? _node.Status : FlowStatus.Invalid;

    /// <summary>The name of the clock the node runs on.</summary>
    public string ClockName => IsValid ? _node.Clock?.Name : null;

    /// <summary>Why the node was canceled, or <see cref="CancelCause.None"/>.</summary>
    public CancelCause Cause => IsValid ? _node.Cause : CancelCause.None;

    /// <summary>Canceled and not ended yet: it runs its cleanup.</summary>
    public bool IsCanceling => IsValid && _node.IsCancelConfirmed && !_node.IsTerminated;

    /// <summary>What it waits for.</summary>
    public string Waiting => IsValid ? _node.DescribeWait() : null;

    /// <summary>
    /// The source file where what it waits for was created, as the compiler passed it (<c>[CallerFilePath]</c>): for a
    /// wait or combinator, its own place; for a scope, the place of the wait or combinator it awaits directly. Null when
    /// unknown: a scope that awaits the call of an async FlowTask method (a call records no place), a bridged Task, an
    /// awaited Once, or a wait made by code that passed no place.
    /// </summary>
    public string WaitingFile
    {
        get
        {
            if (!IsValid) return null;
            _node.GetWaitSite(out var file, out _);
            return file;
        }
    }

    /// <summary>The line in <see cref="WaitingFile"/> (<c>[CallerLineNumber]</c>), or 0 when unknown.</summary>
    public int WaitingLine
    {
        get
        {
            if (!IsValid) return 0;
            _node.GetWaitSite(out _, out var line);
            return line;
        }
    }

    /// <summary>For a scope: the seconds of UnscaledClock time since it last suspended; 0 for other nodes.</summary>
    public double WaitingSeconds => IsValid && _node.IsStateMachine && _node.World != null ? _node.World.UnscaledClock.Time - _node.SuspendedAt : 0;

    /// <summary>The scope path from the root.</summary>
    public string Path => IsValid ? _node.BuildScopePath() : null;

    /// <summary>The children, in the order they started.</summary>
    public IReadOnlyList<FlowScopeInfo> Children
    {
        get
        {
            var list = new List<FlowScopeInfo>();
            if (!IsValid) return list;
            for (var c = _node.FirstChild; c != null; c = c.NextSibling) list.Add(new FlowScopeInfo(c));
            return list;
        }
    }

    internal FlowNode Node => IsValid ? _node : null;

    /// <summary>The <see cref="Name"/>.</summary>
    public override string ToString() => Name;
}

/// <summary>What a node of the scope tree is (<see cref="FlowScopeInfo.Kind"/>).</summary>
public enum FlowScopeKind
{
    /// <summary>The node is gone: <see cref="FlowScopeInfo.IsValid"/> is false.</summary>
    Invalid = 0,

    /// <summary>The World's root scope.</summary>
    Root,

    /// <summary>A scope: one run of an async FlowTask method.</summary>
    Scope,

    /// <summary>A wait: time, frames, a condition, a signal, a FlowProperty, a bridged Task or a Join.</summary>
    Wait,

    /// <summary>A combinator (Race, WhenAll), or WithoutResult around one task.</summary>
    Combinator,
}
