using System.Threading.Tasks;

namespace Katout.FlowTask.Internal;

/// <summary>
/// A leaf that waits for a Task (a Task{T} for T, or a Task for <see cref="FlowUnit"/>). Not pooled: a Task
/// continuation cannot carry a token without allocating. Released like other nodes, so a completion that arrives later
/// is recognized as late.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The CancellationTokenSource is disposed on every exit path (Finish, HandleFault, OnUnregister); nodes are not IDisposable.")]
internal sealed class TaskNode<T> : LeafNode<T>, IInboxItem
{
    // ExecuteSynchronously runs the continuation on the thread that completes the Task, whatever its
    // SynchronizationContext, so the completion reaches the inbox before that thread goes on. The node is the state: no closure.
    static readonly Action<Task, object> s_onCompleted = (_, state) => ((TaskNode<T>)state).OnTaskCompleted();

    /// <summary>The inbox token of "released with a result nobody took". Node tokens are never 0.</summary>
    const uint DiscardRequest = 0;

    Func<CancellationToken, Task> _factory;
    Task _task;
    Action<T> _onDiscard;
    CancellationTokenSource _cts;
    FlowWorld _world;   // kept for a completion that arrives after the node is released
    uint _startToken;
    FlowNode _owner;    // the parent it started under, to name it once it has left the tree
    uint _ownerToken;
    string _scopePath;  // where it was when it stopped waiting, for reports about what arrives later
    T _value;           // the result, kept for onDiscard until a consumer takes it
    int _discarded;

    internal static TaskNode<T> Create(Func<CancellationToken, Task> factory, Task task, Action<T> onDiscard)
    {
        var n = new TaskNode<T> { _factory = factory, _task = task, _onDiscard = onDiscard };
        n.InitUnstarted();
        return n;
    }

    internal override string Name => "Task<" + typeof(T).Name + ">";

    internal override string DescribeWait() => $"external Task<{typeof(T).Name}> (bridged)";

    protected override void OnStart()
    {
        _world = World;
        _owner = Parent;
        _ownerToken = Parent.Token;
        var task = _task;
        if (task == null)
        {
            _cts = new CancellationTokenSource();
            try
            {
                task = _factory(_cts.Token) ?? throw new InvalidOperationException("The bridge factory returned null.");
            }
#pragma warning disable CA1031 // the external factory failed: thrown at the await
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HandleFault(ex, sync: true);
                return;
            }

            _task = task;
        }

        if (task.IsCompleted)
        {
            Finish(sync: true);
            return;
        }

        _startToken = Token;
        task.ContinueWith(s_onCompleted, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>On the thread that completed the Task.</summary>
    void OnTaskCompleted()
    {
        if (_world.Inbox.Post(this, _startToken)) return;

        // The World is disposed: nothing takes this completion on its thread. A result is released here, on the completing
        // thread; a failure has nowhere to go and is only marked as observed.
        if (_task.Status == TaskStatus.RanToCompletion)
        {
            if (_onDiscard == null || Interlocked.Exchange(ref _discarded, 1) != 0) return;
            try
            {
                _onDiscard(ReadValue());
            }
#pragma warning disable CA1031 // onDiscard is user cleanup code, and the World that would report it is gone: dropped
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
        else
        {
            _ = _task.Exception;
        }
    }

    void IInboxItem.ProcessInbox(uint token)
    {
        if (token == DiscardRequest)
        {
            OnUnclaimed();
            return;
        }

        if (token == Token && State == NodeState.Running && !IsCancelConfirmed)
        {
            Finish(sync: false);
            return;
        }

        ProcessLate();
    }

    void IInboxItem.DiscardInbox(uint token)
    {
        if (token == DiscardRequest) OnUnclaimed();
        else ProcessLate();
    }

    /// <summary>
    /// The Task finished after the bridge stopped waiting (its scope was canceled, it lost a Race, the World ended): a
    /// result goes to onDiscard, a cancellation is expected, and another failure had no receiver: Undelivered.
    /// </summary>
    void ProcessLate()
    {
        var task = _task;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            if (_onDiscard != null) Discard(ReadValue());
            return;
        }

        if (task.IsCanceled) return;
        // Reading Exception observes it. Some sources report a cancellation as a fault (UniTask's AsTask).
        var ex = task.Exception?.InnerException;
        if (ex is null or OperationCanceledException) return;
        _world.Reporter.ReportUndelivered(ex, ScopePathForReport());
    }

    /// <summary>Released with a result no consumer took (<see cref="NodeFlags.Unclaimed"/>, set only with onDiscard).</summary>
    void OnUnclaimed()
    {
        var value = _value;
        _value = default;
        Discard(value);
    }

    /// <summary>Hands a result nobody received to onDiscard, once, on the World's thread.</summary>
    void Discard(T value)
    {
        if (Interlocked.Exchange(ref _discarded, 1) != 0) return;
        try
        {
            _onDiscard(value);
        }
#pragma warning disable CA1031 // onDiscard is user cleanup code: a cleanup exception, and the intake goes on
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _world.Reporter.ReportCleanupException(ScopePathForReport(), ex);
        }
    }

    T ReadValue() => _task is Task<T> typed ? typed.Result : default;

    string ScopePathForReport()
    {
        if (_scopePath != null) return _scopePath;
        if (State == NodeState.Running) return BuildScopePath();
        return _owner != null && _owner.Token == _ownerToken ? PathUnder(_owner, DisplayName) : DisplayName;
    }

    internal override void UnwindSelf()
    {
        // The Task may still finish: record where it was while the path still exists.
        if (!IsTerminated && _scopePath == null) _scopePath = BuildScopePath();
        base.UnwindSelf();
    }

    void Finish(bool sync)
    {
        T value;
        try
        {
            if (_task is Task<T> typed)
            {
                value = typed.GetAwaiter().GetResult();
            }
            else
            {
                _task.GetAwaiter().GetResult();
                value = default;
            }
        }
#pragma warning disable CA1031 // the external Task failed: thrown at the await
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HandleFault(ex, sync);
            return;
        }

        DisposeCts();
        if (_onDiscard != null)
        {
            // Until a consumer takes the result, releasing the node hands the value to onDiscard.
            _value = value;
            Flags |= NodeFlags.Unclaimed;
        }

        if (sync) CompleteSync(value);
        else CompleteAsync(value);
    }

    void HandleFault(Exception ex, bool sync)
    {
        DisposeCts();
        if (sync) Fail(ex);
        else FailAsync(ex);
    }

    /// <summary>The scope's cancellation reaches the external operation through its token.</summary>
    protected override void OnUnregister()
    {
        var cts = _cts;
        if (cts == null || State != NodeState.Running) return;
        try
        {
            cts.Cancel();
        }
        catch (AggregateException ex)
        {
            World.Reporter.ReportCleanupException(this, ex);
        }
        finally
        {
            DisposeCts();
        }
    }

    void DisposeCts()
    {
        _cts?.Dispose();
        _cts = null;
    }

    protected override void ResetNode()
    {
        var unclaimed = (Flags & NodeFlags.Unclaimed) != 0;
        if (unclaimed && _scopePath == null) _scopePath = ScopePathForReport();
        base.ResetNode();
        // onDiscard runs in the next intake, not inside the scheduler's release; once the World is disposed, now.
        if (unclaimed && !_world.Inbox.Post(this, DiscardRequest)) OnUnclaimed();
    }

    protected override void ReturnToPool()
    {
        // Never reused: a pending continuation still refers to this node, and the fields it needs survive the release.
    }
}
