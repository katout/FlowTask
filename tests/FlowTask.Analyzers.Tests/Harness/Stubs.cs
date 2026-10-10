namespace Katout.FlowTask.Analyzers.Tests.Harness;

/// <summary>
/// Source stand-ins for third-party awaitables, compiled together with a test snippet. Only the shapes the analyzers
/// look at are modelled (awaiters, the async method builder, AsFlow bridges); nothing here ever runs.
/// </summary>
public static class Stubs
{
    /// <summary>
    /// UniTask (Cysharp.Threading.Tasks): UniTask, UniTask&lt;T&gt;, UniTask.Yield(), WhenAll, Forget, the async method
    /// builders, and FlowTask.UniTask's bridges (FlowUniTask.FromUniTask, the AsFlow and ToUniTask extensions).
    /// </summary>
    public const string UniTask = """

        namespace Cysharp.Threading.Tasks
        {
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            [AsyncMethodBuilder(typeof(AsyncUniTaskMethodBuilder))]
            public readonly struct UniTask
            {
                public static UniTask CompletedTask => default;
                public static YieldAwaitable Yield() => default;
                public static UniTask WhenAll(IEnumerable<UniTask> tasks) => default;
                public static UniTask<T[]> WhenAll<T>(IEnumerable<UniTask<T>> tasks) => default;
                public System.Threading.Tasks.Task AsTask() => System.Threading.Tasks.Task.CompletedTask;
                public Awaiter GetAwaiter() => default;

                public readonly struct Awaiter : ICriticalNotifyCompletion
                {
                    public bool IsCompleted => true;
                    public void GetResult() { }
                    public void OnCompleted(Action continuation) => continuation();
                    public void UnsafeOnCompleted(Action continuation) => continuation();
                }
            }

            [AsyncMethodBuilder(typeof(AsyncUniTaskMethodBuilder<>))]
            public readonly struct UniTask<T>
            {
                public System.Threading.Tasks.Task<T> AsTask() => System.Threading.Tasks.Task.FromResult(default(T));
                public Awaiter GetAwaiter() => default;

                public readonly struct Awaiter : ICriticalNotifyCompletion
                {
                    public bool IsCompleted => true;
                    public T GetResult() => default;
                    public void OnCompleted(Action continuation) => continuation();
                    public void UnsafeOnCompleted(Action continuation) => continuation();
                }
            }

            public readonly struct YieldAwaitable
            {
                public Awaiter GetAwaiter() => default;

                public readonly struct Awaiter : ICriticalNotifyCompletion
                {
                    public bool IsCompleted => false;
                    public void GetResult() { }
                    public void OnCompleted(Action continuation) => continuation();
                    public void UnsafeOnCompleted(Action continuation) => continuation();
                }
            }

            public static class UniTaskExtensions
            {
                public static void Forget(this UniTask task) { }
                public static void Forget<T>(this UniTask<T> task) { }
            }

            public struct AsyncUniTaskMethodBuilder
            {
                public static AsyncUniTaskMethodBuilder Create() => default;
                public UniTask Task => default;
                public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult() { }
                public void SetException(Exception exception) { }
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
            }

            public struct AsyncUniTaskMethodBuilder<T>
            {
                public static AsyncUniTaskMethodBuilder<T> Create() => default;
                public UniTask<T> Task => default;
                public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult(T result) { }
                public void SetException(Exception exception) { }
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
                    where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
            }
        }

        namespace Katout.FlowTask
        {
            using System;
            using System.Threading;
            using Cysharp.Threading.Tasks;

            public static class FlowUniTask
            {
                public static TaskBridge<T> FromUniTask<T>(Func<CancellationToken, UniTask<T>> factory) => FlowBridge.FromTask<T>(ct => factory(ct).AsTask());
                public static TaskBridge FromUniTask(Func<CancellationToken, UniTask> factory) => FlowBridge.FromTask(ct => factory(ct).AsTask());
                public static TaskBridge<T> AsFlow<T>(this UniTask<T> task) => task.AsTask().AsFlow();
                public static TaskBridge AsFlow(this UniTask task) => task.AsTask().AsFlow();
                public static UniTask ToUniTask(this FlowHandle handle) => default;
            }
        }
        """;
}
