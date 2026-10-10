namespace Katout.FlowTask.Unity.Tests;

static class TestUtil
{
    internal static FlowWorld W => FlowTaskUnity.World;

    /// <summary>Yields frames until the handle completes (or <paramref name="maxSeconds"/> of real time pass).</summary>
    internal static IEnumerator WaitFor<T>(FlowHandle<T> handle, float maxSeconds = 5f)
    {
        var start = Time.realtimeSinceStartup;
        while (!handle.IsCompleted && Time.realtimeSinceStartup - start < maxSeconds) yield return null;
    }

    internal static IEnumerator WaitFor(FlowHandle handle, float maxSeconds = 5f) => WaitFor((FlowHandle<FlowUnit>)handle, maxSeconds);

    internal static IEnumerator Frames(int count)
    {
        for (var i = 0; i < count; i++) yield return null;
    }
}
