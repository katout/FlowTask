using System.Threading;
using NUnit.Framework;

/// <summary>
/// Unity-only harness setting for the synced core suite (not part of tests/FlowTask.Core.Tests). Declared in the global
/// namespace, so NUnit applies it to every test of this assembly.
/// <para>
/// The suite was written for NUnit on .NET, where the test thread has no SynchronizationContext. Unity's main thread has
/// a UnitySynchronizationContext that only runs posted continuations when the player loop pumps it, so a test that
/// bridges real <c>async Task</c> code (awaiting without ConfigureAwait(false)) while it drives the World synchronously
/// would wait forever. The context is removed while this assembly's tests run and restored afterwards. FlowTask itself
/// does not need it: a bridged Task's completion goes to the World inbox from the thread that completes the Task
/// (ContinueWith with ExecuteSynchronously), whatever that thread's SynchronizationContext.
/// (An assembly-level ITestAction is not applied by the Unity Test Framework, hence a SetUpFixture.)
/// </para>
/// </summary>
[SetUpFixture]
public sealed class UnityTestEnvironment
{
    SynchronizationContext _saved;

    [OneTimeSetUp]
    public void RemoveUnitySynchronizationContext()
    {
        _saved = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
    }

    [OneTimeTearDown]
    public void RestoreUnitySynchronizationContext() => SynchronizationContext.SetSynchronizationContext(_saved);
}
