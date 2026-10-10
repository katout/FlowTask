using System;
using Godot;

namespace Katout.FlowTask.Godot;

/// <summary>
/// Ties Godot node lifetimes to FlowTask scopes, in both directions: <see cref="Own{T}"/> frees a node when a scope
/// ends, <see cref="RunWhileInTree(Node, FlowTask, Clock)"/> and <see cref="WhileInTree(Node, FlowTask)"/> end a
/// flow when a node leaves the tree.
/// </summary>
public static class NodeLifetime
{
    static readonly Action<Node> s_queueFree = static n =>
    {
        if (GodotObject.IsInstanceValid(n) && !n.IsQueuedForDeletion()) n.QueueFree();
    };

    /// <summary>
    /// Scope owns node: <paramref name="node"/> is <c>QueueFree</c>d when the current scope ends (LIFO with
    /// <c>Flow.AddCleanup</c>/<c>Flow.Own</c>). Outside a flow nothing owns it, and the caller frees it, as with
    /// <c>Flow.Own</c>.
    /// </summary>
    public static T Own<T>(T node) where T : Node
    {
        if (node == null) throw new ArgumentNullException(nameof(node));
        if (Flow.IsInFlow) Flow.AddCleanup<Node>(node, s_queueFree);
        return node;
    }

    /// <summary>
    /// Node owns flow, started now: runs <paramref name="task"/> until <paramref name="owner"/> exits the tree
    /// (<c>tree_exiting</c>), then cancels it. The handle's result is true when the task completed, false when the
    /// exit canceled it (or the owner was not inside the tree, in which case the task is not run). The task runs on
    /// <paramref name="clock"/> (default: the DefaultClock of <see cref="FlowWorldNode.Default"/>), as a root flow of that
    /// clock's World, wherever it is called from: the node owns the flow, so it does not end with the scope that called
    /// this (called from flow code, it is not a child of that scope; its exception goes to <c>OnUnhandledException</c>). The exit is
    /// watched on the World's DefaultClock, so it ends the task while <paramref name="clock"/> is paused too. Typical use
    /// in <c>_Ready</c>: <c>this.RunWhileInTree(EnemyAI());</c>. To await the result in a flow, or to end the task with
    /// the calling scope too, use <see cref="WhileInTree(Node, FlowTask)"/>.
    /// </summary>
    public static FlowHandle<bool> RunWhileInTree(this Node owner, FlowTask task, Clock clock = null)
    {
        if (clock == null) return FlowWorldNode.Default.Run(WhileInTree(owner, task));
        // The clock is the task's only: the wait for the exit stays on the World's DefaultClock, so that leaving the tree
        // still ends the task while the clock is paused (a HUD freed while its menu pauses the game).
        return clock.World.Run(WhileInTree(owner, Flow.WithClock(clock, task)));
    }

    /// <summary>
    /// Node owns flow, lazy like every FlowTask: when awaited (or passed to a combinator), runs <paramref name="task"/>
    /// and cancels it when <paramref name="owner"/> exits the tree (<c>tree_exiting</c>). Returns true when the task
    /// completed, false when it was canceled by the exit. The unwind happens inside <c>tree_exiting</c> (an immediate
    /// <c>FlowWorld.Flush()</c> when the World is not executing), so finally/using blocks still see the node inside
    /// the tree. If the owner is not inside the tree when this starts, <paramref name="task"/> is not run and the
    /// result is false.
    /// </summary>
    public static FlowTask<bool> WhileInTree(this Node owner, FlowTask task)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        return Flow.Named(ScopeName(owner), WhileInTreeCore(owner, task));
    }

    /// <summary>
    /// <see cref="WhileInTree(Node, FlowTask)"/> for a task with a result: <c>(true, result)</c> when it completed,
    /// <c>(false, default)</c> when the owner left the tree first.
    /// </summary>
    public static FlowTask<(bool Completed, T Value)> WhileInTree<T>(this Node owner, FlowTask<T> task)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        return Flow.Named(ScopeName(owner), WhileInTreeCore(owner, task));
    }

    /// <summary>The scope's name in dumps and scope paths: "WhileInTree(Enemy) &gt; EnemyAI".</summary>
    static string ScopeName(Node owner) =>
        "WhileInTree(" + (GodotObject.IsInstanceValid(owner) ? owner.Name.ToString() : "freed node") + ")";

    static async FlowTask<bool> WhileInTreeCore(Node owner, FlowTask task)
    {
        if (!IsInTree(owner))
        {
            task.Discard();
            return false;
        }

        using var exiting = TreeExiting(owner, FlowWorld.Current);
        // The exit wait starts first: the task's first step runs inside Race, and a tree_exiting it causes there (a
        // synchronous RemoveChild) must find the wait already in place, or the exit would be missed.
        var r = await FlowTask.Race(exiting.Next(), task);
        return r.Index == 1;
    }

    static async FlowTask<(bool Completed, T Value)> WhileInTreeCore<T>(Node owner, FlowTask<T> task)
    {
        if (!IsInTree(owner))
        {
            task.Discard();
            return (false, default);
        }

        using var exiting = TreeExiting(owner, FlowWorld.Current);
        var r = await FlowTask.Race(exiting.Next(), task); // the exit wait first, as above
        return r.Index == 1 ? (true, r.Value1) : (false, default);
    }

    static bool IsInTree(Node node) => GodotObject.IsInstanceValid(node) && node.IsInsideTree();

    /// <summary>
    /// <c>tree_exiting</c> as a scope-owned signal that also flushes <paramref name="world"/> right away when it is not
    /// executing, so the resulting cancellation unwinds while the node is still in the tree.
    /// </summary>
    static EventSignal<FlowUnit> TreeExiting(Node owner, FlowWorld world)
    {
        var signal = Node.SignalName.TreeExiting;
        GodotSignalExtensions.CheckSignal(owner, signal);
        return FlowBridge.FromCallback<FlowUnit>(emit => GodotSignalExtensions.Connect(owner, signal, Callable.From(() =>
        {
            emit(FlowUnit.Default);
            if (world != null && !world.IsDisposed && !world.IsExecuting && world.IsBoundToCurrentThread) world.Flush();
        })), GodotSignalExtensions.Describe(owner, signal));
    }
}
