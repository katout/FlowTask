using System;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace Katout.FlowTask;

/// <summary>
/// UniTask &lt;-&gt; FlowTask bridge.
/// <para>
/// UniTask -&gt; FlowTask: <see cref="FromUniTask{T}"/> starts the UniTask when the FlowTask starts and hands it a
/// <see cref="CancellationToken"/> that is canceled when the owning scope is canceled. Completion always
/// resumes the awaiting flow on the World thread, in a flush, even when the UniTask completes on another
/// thread. An exception is thrown at the await, where a catch can receive it. A UniTask canceled outside the flow
/// throws a plain <see cref="OperationCanceledException"/> there, an exception like any other.
/// </para>
/// <para>
/// FlowTask -&gt; UniTask: <see cref="ToUniTask{T}(FlowHandle{T})"/> completes when the flow ends: its result,
/// <see cref="OperationCanceledException"/> when it was canceled, or the exception that ended it when it faulted.
/// </para>
/// </summary>
public static class FlowUniTask
{
    // ------------------------------------------------------------------ UniTask -> FlowTask

    /// <summary>
    /// Bridges a UniTask-returning operation. <paramref name="factory"/> runs when the FlowTask starts (it is lazy
    /// like every FlowTask) and receives a token canceled with the scope.
    /// </summary>
    public static TaskBridge<T> FromUniTask<T>(Func<CancellationToken, UniTask<T>> factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        return FlowBridge.FromTask<T>(ct => factory(ct).AsTask());
    }

    /// <summary>Bridges a UniTask-returning operation without a result. See <see cref="FromUniTask{T}"/>.</summary>
    public static TaskBridge FromUniTask(Func<CancellationToken, UniTask> factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        return FlowBridge.FromTask(ct => factory(ct).AsTask());
    }

    /// <summary>
    /// Bridges an already running UniTask. No cancellation reaches it (it was started without the scope's token);
    /// prefer <see cref="FromUniTask{T}"/> when the operation accepts a token. The UniTask is consumed here: a UniTask
    /// may be awaited only once.
    /// </summary>
    public static TaskBridge<T> AsFlow<T>(this UniTask<T> task) => task.AsTask().AsFlow();

    /// <summary>Bridges an already running UniTask without a result. See <see cref="AsFlow{T}(UniTask{T})"/>.</summary>
    public static TaskBridge AsFlow(this UniTask task) => task.AsTask().AsFlow();

    // ------------------------------------------------------------------ FlowTask -> UniTask

    /// <summary>
    /// A UniTask that completes when the flow behind <paramref name="handle"/> ends. Does not
    /// consume the handle's single Join(). If a <see cref="SynchronizationContext"/> is current when this is called
    /// (e.g. Unity's main thread), completion is posted to it; otherwise the UniTask completes on the World thread right
    /// after the Tick, Flush or Run in which the flow ended, outside the flush (the same as
    /// <see cref="FlowHandle{T}.AsTask"/>).
    /// </summary>
    public static UniTask<T> ToUniTask<T>(this FlowHandle<T> handle) => FromTask(handle.AsTask(), SynchronizationContext.Current);

    /// <summary>A UniTask that completes when the flow behind <paramref name="handle"/> ends. See <see cref="ToUniTask{T}(FlowHandle{T})"/>.</summary>
    public static UniTask ToUniTask(this FlowHandle handle)
    {
        FlowHandle<FlowUnit> h = handle;
        return FromTask(h.AsTask(), SynchronizationContext.Current).AsUniTask();
    }

    static UniTask<T> FromTask<T>(Task<T> task, SynchronizationContext context)
    {
        if (task.IsCompleted)
        {
            if (task.Status == TaskStatus.RanToCompletion) return UniTask.FromResult(task.Result);
            if (task.IsCanceled) return UniTask.FromCanceled<T>();
            return UniTask.FromException<T>(Unwrap(task.Exception));
        }

        var source = new UniTaskCompletionSource<T>();
        task.ContinueWith(static (t, state) =>
        {
            var s = (Completion<T>)state;
            if (s.Context == null) s.Complete(t);
            else s.Context.Post(static st => ((Completion<T>)st).CompletePending(), s.WithPending(t));
        }, new Completion<T>(source, context), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return source.Task;
    }

    static Exception Unwrap(AggregateException ex) =>
        ex == null ? new InvalidOperationException("The task faulted without an exception.") : ex.InnerExceptions.Count == 1 ? ex.InnerExceptions[0] : ex;

    sealed class Completion<T>
    {
        readonly UniTaskCompletionSource<T> _source;
        internal readonly SynchronizationContext Context;
        Task<T> _pending;

        internal Completion(UniTaskCompletionSource<T> source, SynchronizationContext context)
        {
            _source = source;
            Context = context;
        }

        internal Completion<T> WithPending(Task<T> task)
        {
            _pending = task;
            return this;
        }

        internal void CompletePending() => Complete(_pending);

        internal void Complete(Task<T> t)
        {
            if (t.Status == TaskStatus.RanToCompletion) _source.TrySetResult(t.Result);
            else if (t.IsCanceled) _source.TrySetCanceled();
            else _source.TrySetException(Unwrap(t.Exception));
        }
    }
}
