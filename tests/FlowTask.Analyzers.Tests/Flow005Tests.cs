namespace Katout.FlowTask.Analyzers.Tests;

/// <summary>FLOW005: a FlowTask awaitable awaited in an async function that does not return FlowTask.</summary>
public class Flow005Tests
{
    [Test]
    public Task FlowAwaitsOutsideFlowTaskMethodsAreReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;

            async Task AsTask()
            {
                {|FLOW005:await DoThing()|};
            }

            async void AsyncVoid(FlowHandle handle, Once<int> once)
            {
                {|FLOW005:await handle.Join()|};
                var v = {|FLOW005:await once|};
            }

            async ValueTask<int> AsValueTask(Task<int> task)
            {
                return {|FLOW005:await task.AsFlow()|};
            }

            async IAsyncEnumerable<int> Stream()
            {
                {|FLOW005:await FlowTask.WaitForSeconds(1)|};
                yield return 1;
            }

            async FlowTask InsideAFlow()
            {
                // The lambda is its own async function returning Task: it is not a scope.
                await FlowBridge.FromTask(async ct =>
                {
                    {|FLOW005:await FlowTask.WaitForSeconds(1)|};
                });

                Func<Task> f = async () => {|FLOW005:await DoThing()|};
            }
        }
        """);

    [Test]
    public Task FlowAwaitInTopLevelStatementsIsReported() => AnalyzerHarness.VerifyAsync("""
        using System.Threading.Tasks;
        using Katout.FlowTask;

        using var world = new FlowWorld();
        var handle = world.Run(FlowTask.WaitForSeconds(1));
        {|FLOW005:await handle.Join()|};
        await handle.AsTask();
        """, Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);

    [Test]
    public Task AwaitsInFlowTaskMethodsAndForeignAwaitsElsewhereAreNotReported() => AnalyzerHarness.VerifyAsync("""
        using System;
        using System.Threading.Tasks;
        using Katout.FlowTask;

        class C
        {
            FlowTask DoThing() => FlowTask.CompletedTask;

            async FlowTask InFlow(FlowHandle handle)
            {
                await DoThing();
                await handle.Join();
                Func<FlowTask> f = async () => await DoThing();

                async FlowTask<int> Local() => await FlowTask.FromResult(1);
                await Local();
            }

            async Task External(FlowWorld world)
            {
                await Task.Delay(1);
                await world.Run(DoThing()).AsTask(); // bridged back to a Task
            }
        }
        """);
}
