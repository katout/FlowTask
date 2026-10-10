namespace Katout.FlowTask;

/// <summary>Conversions of FlowTask values.</summary>
public static class FlowTaskExtensions
{
    /// <summary>
    /// A task without a result: when the task succeeds, the flow receives the completion and ignores the value (a bridge's
    /// goes to its onDiscard, a subscription's counts as taken).
    /// </summary>
    public static FlowTask WithoutResult<T>(this FlowTask<T> task)
    {
        var inner = Flow.Materialize(task);
        var n = DiscardNode<T>.Rent(inner, inner.Token);
        // Transparent: a scope awaiting it shows the place of the task it wraps.
        n.SetSite(inner.SiteFile, inner.SiteLine);
        return new FlowTask(n, n.Token);
    }
}
