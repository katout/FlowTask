# Node lifetime

You can tie the lifetimes of Godot nodes and flows together in two directions. With `NodeLifetime.Own`, a scope owns a node (the node is freed when the scope ends). With `RunWhileInTree` and `NodeLifetime.WhileInTree`, a node owns a flow (the flow stops when the node leaves the tree).

## A scope owns a node: `NodeLifetime.Own`

```csharp
async FlowTask ShowToast(string text)
{
    var toast = NodeLifetime.Own(ToastScene.Instantiate<Label>());
    toast.Text = text;
    AddChild(toast);
    await FlowTask.WaitForSeconds(2.0);
}   // toast is QueueFree'd whether the flow completes or is canceled
```

`NodeLifetime.Own(node)` calls `QueueFree()` on the node when the current scope ends. It does so whether the scope completes, is canceled, or throws.

- It returns the node you pass in, so you can wrap the expression that creates it.
- The free is added to the same cleanup list as `Flow.AddCleanup` and `Flow.Own`, and runs in reverse order of registration (LIFO) (see [Scopes and cancellation](../guide/scopes-and-cancellation.md)).
- Called outside a flow, it owns nothing, as with `Flow.Own`: the caller frees the node.
- If the node has already been freed or queued for deletion when it is freed, it does nothing.

If you give each step its own scope, each step's nodes disappear when that step ends. [Samples](samples.md) has a tutorial whose balloons are written this way.

## A node owns a flow: `RunWhileInTree`

```csharp
public partial class Enemy : Node2D
{
    public override void _Ready()
    {
        this.RunWhileInTree(EnemyAI());   // returns a FlowHandle<bool>
    }
}
```

`owner.RunWhileInTree(task, clock)` starts the task immediately and cancels it when owner leaves the tree.

- The task runs on `clock` (the `DefaultClock` of `FlowWorldNode.Default` if omitted), as a root flow of that Clock's World. To run on the game's Clock, write `this.RunWhileInTree(EnemyAI(), clocks.Game)`. A Clock of the physics World runs the task in that World (`this.RunWhileInTree(Dash(body), FlowAutoload.Physics.DefaultClock)`; see [Running on physics frames](physics.md)).
- The node owns its lifetime. Even if you call it from flow code, it does not become a child of the calling scope, and it does not stop when that scope ends.
- The task's exceptions go to `OnUnhandledException` (Godot's log by default), not to the calling flow.
- The handle's result is `true` if the task completed, and `false` if owner left the tree first. Either way, the handle's state is success (`Succeeded`).
- If owner is not in the tree (or has been freed) at start, the task is freed without starting, and the result is `false`.

## The awaitable version: `NodeLifetime.WhileInTree`

```csharp
// Starts when awaited, like every FlowTask; canceled when enemy leaves the tree
bool completed = await NodeLifetime.WhileInTree(enemy, EnemyAI(enemy));

// With a result: (true, result) when it completed, (false, default) when the owner left first
var (finished, score) = await NodeLifetime.WhileInTree(this, MiniGame());
```

`WhileInTree` returns a FlowTask, and like any other FlowTask, it starts when awaited. It runs inside the awaiting scope, so use it when:

- You want to wait for the result inside a flow.
- You want it to stop not only when the node leaves the tree, but also when the calling scope ends.

For a task without a result it returns `bool` (`true` if completed), and for a `FlowTask<T>` with a result it returns `(bool Completed, T Value)` (`(true, result)` if completed, `(false, default)` if owner left the tree first). If owner is not in the tree at start, the task is freed without starting, and it returns `false` or `(false, default)`.

## Unwinding runs while the node is still in the tree

When a node leaves the tree through `QueueFree()` or a scene change, the flows tied to that node unwind while the node is still in the tree. From `finally`, `using`, and `AddCleanup`, you can touch the node's parent and siblings.

```csharp
async FlowTask EnemyAI()
{
    try
    {
        while (true) { /* patrol */ await FlowTask.NextFrame(); }
    }
    finally
    {
        // Runs inside tree_exiting: GetParent() and GetTree() still work here
        GetParent().GetNode<Hud>("Hud").RemoveMarker(this);
    }
}
```

- When `tree_exiting` arrives and the flow's World is not executing (`FlowWorld.IsExecuting` is false), it calls `FlowWorld.Flush()` immediately. So the unwinding runs within the same frame, while the Node is still in the tree.
- If `tree_exiting` arrives in the middle of the World's Flush, such as when the flow itself calls `RemoveChild` or `Free`, the flow unwinds within that same Flush. In that case it happens after the call has returned, so the Node is already out of the tree.

## How it looks in a dump

Flows running under `RunWhileInTree` and `WhileInTree` appear in dumps and scope paths as `WhileInTree(node name) > EnemyAI` (see [Debugging and diagnostics](../tools/debugging.md)).

## Which one to use

| What you want | How to write it |
| --- | --- |
| Remove a node created in a flow when the flow ends | `NodeLifetime.Own(node)` |
| In a node's `_Ready`, start a flow that runs only while that node is alive | `this.RunWhileInTree(task)` |
| Inside a flow, wait on work that runs only while a certain node is alive | `await NodeLifetime.WhileInTree(node, task)` |
