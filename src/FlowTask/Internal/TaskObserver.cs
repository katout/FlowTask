using System.Threading.Tasks;

namespace Katout.FlowTask.Internal;

internal interface IFlowObserver
{
    void OnTerminated(FlowNode node);
}

/// <summary>Work the World does once it is back at the top level of a Tick, Flush, Run or Dispose.</summary>
internal interface IDeferredCompletion
{
    void Complete();
}

/// <summary>
/// The Task of FlowHandle.AsTask. The outcome is recorded when the flow ends (inside the scheduler), and the Task is
/// completed by the World at the top level: its continuations run on the World thread, never inside a flush.
/// </summary>
internal sealed class TaskObserver<T> : IFlowObserver, IDeferredCompletion
{
    readonly TaskCompletionSource<T> _tcs = new();
    T _value;
    Exception _error;
    bool _canceled;

    internal Task<T> Task => _tcs.Task;

    public void OnTerminated(FlowNode node)
    {
        Record(node);
        node.World.QueueCompletion(this);
    }

    /// <summary>The flow had already ended when AsTask was called.</summary>
    internal void CompleteNow(FlowNode node)
    {
        Record(node);
        Complete();
    }

    void Record(FlowNode node)
    {
        switch (node.State)
        {
            case NodeState.Succeeded:
                _value = ((FlowNode<T>)node).Result;
                break;
            case NodeState.Faulted:
                _error = node.Failure?.Info?.Exception ?? new FlowJoinException("The task faulted.");
                break;
            default:
                _canceled = true;
                break;
        }
    }

    public void Complete()
    {
        if (_error != null) _tcs.TrySetException(_error);
        else if (_canceled) _tcs.TrySetCanceled();
        else _tcs.TrySetResult(_value);
        _value = default;
        _error = null;
    }
}

internal sealed class ObserverChain : IFlowObserver
{
    readonly IFlowObserver _a;
    readonly IFlowObserver _b;

    internal ObserverChain(IFlowObserver a, IFlowObserver b)
    {
        _a = a;
        _b = b;
    }

    public void OnTerminated(FlowNode node)
    {
        _a.OnTerminated(node);
        _b.OnTerminated(node);
    }
}
