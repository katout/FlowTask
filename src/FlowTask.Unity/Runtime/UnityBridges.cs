using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Events;

namespace Katout.FlowTask.Unity;

/// <summary>
/// UnityEvent -&gt; Signal bridges. Call them from flow code: the listener is added now and removed when the
/// current scope ends (or when the returned <see cref="EventSignal{T}"/> is disposed earlier). Invocations resume
/// waiting flows at the next Tick/flush point, never synchronously. Events with more arguments bridge with
/// <see cref="FlowBridge.FromCallback{T}"/> in one lambda; a one-shot wait is <c>using var s = e.ToSignal(); await s.Next();</c>.
/// </summary>
public static class UnityEventBridge
{
    public static EventSignal<FlowUnit> ToSignal(this UnityEvent unityEvent, string name = null)
    {
        if (unityEvent == null) throw new ArgumentNullException(nameof(unityEvent));
        return FlowBridge.FromCallback<FlowUnit>(emit =>
        {
            UnityAction listener = () => emit(FlowUnit.Default);
            unityEvent.AddListener(listener);
            return () => unityEvent.RemoveListener(listener);
        }, name ?? "UnityEvent");
    }

    public static EventSignal<T> ToSignal<T>(this UnityEvent<T> unityEvent, string name = null)
    {
        if (unityEvent == null) throw new ArgumentNullException(nameof(unityEvent));
        return FlowBridge.FromCallback<T>(emit =>
        {
            UnityAction<T> listener = v => emit(v);
            unityEvent.AddListener(listener);
            return () => unityEvent.RemoveListener(listener);
        }, name ?? "UnityEvent<" + typeof(T).Name + ">");
    }
}

/// <summary>
/// AsyncOperation -&gt; FlowTask bridge. Completion is observed through <c>AsyncOperation.completed</c> and
/// resumes the flow at the next Tick/flush point. If the scope is canceled first, the handler is removed and the
/// completion is ignored (the Unity operation itself cannot be aborted and keeps running).
/// </summary>
public static class AsyncOperationBridge
{
    public static FlowTask AsFlow(this AsyncOperation operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        return operation.isDone ? FlowTask.CompletedTask : AwaitOperation(operation);
    }

    /// <summary>Loads through <c>Resources.LoadAsync</c> and returns the asset.</summary>
    public static FlowTask<UnityEngine.Object> AsFlow(this ResourceRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return request.isDone ? FlowTask.FromResult(request.asset) : AwaitResource(request);
    }

    static async FlowTask<UnityEngine.Object> AwaitResource(ResourceRequest request)
    {
        await AwaitOperation(request);
        return request.asset;
    }

    static async FlowTask AwaitOperation(AsyncOperation operation)
    {
        // Registering after completion makes Unity invoke the handler synchronously; Once handles both orders.
        var done = new Once<FlowUnit>();
        Action<AsyncOperation> onCompleted = _ =>
        {
            if (!done.IsSet) done.Set(FlowUnit.Default);
        };
        operation.completed += onCompleted;
        try
        {
            await done.Wait();
        }
        finally
        {
            operation.completed -= onCompleted;
        }
    }
}

/// <summary>
/// Awaitable -&gt; FlowTask bridge, built on <see cref="FlowBridge.FromTask(System.Func{CancellationToken, Task})"/>:
/// completion (on any thread) resumes the flow on the World thread at the next Tick/flush point; canceling the scope
/// calls <c>Awaitable.Cancel()</c> and ignores the outcome. The result is a <see cref="TaskBridge"/>, like
/// <c>task.AsFlow()</c>: await it, or pass it to combinators.
/// </summary>
public static class AwaitableBridge
{
    static readonly Action<object> s_cancel = state => ((Awaitable)state).Cancel();

    public static TaskBridge AsFlow(this Awaitable awaitable)
    {
        if (awaitable == null) throw new ArgumentNullException(nameof(awaitable));
        return FlowBridge.FromTask(ct => AsTask(awaitable, ct));
    }

    public static TaskBridge<T> AsFlow<T>(this Awaitable<T> awaitable)
    {
        if (awaitable == null) throw new ArgumentNullException(nameof(awaitable));
        return FlowBridge.FromTask(ct => AsTask(awaitable, ct));
    }

    static async Task AsTask(Awaitable awaitable, CancellationToken ct)
    {
        using (ct.Register(s_cancel, awaitable))
        {
            await awaitable;
        }
    }

    static async Task<T> AsTask<T>(Awaitable<T> awaitable, CancellationToken ct)
    {
        using (ct.Register(state => ((Awaitable<T>)state).Cancel(), awaitable))
        {
            return await awaitable;
        }
    }
}
