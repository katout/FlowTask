namespace Katout.FlowTask.Analyzers;

/// <summary>Diagnostic ids reported by the FlowTask analyzers. See docs/en/tools/analyzers.md.</summary>
public static class DiagnosticIds
{
    /// <summary>A catch clause in a FlowTask method can catch FlowCanceledException.</summary>
    public const string CatchCanObserveCancellation = "FLOW001";

    /// <summary>A non-FlowTask awaitable is awaited in a FlowTask method.</summary>
    public const string ForeignAwaitInFlowTask = "FLOW002";

    /// <summary>A FlowTask is created and never awaited or started (dropped, discarded, or stored and not read on every path).</summary>
    public const string FlowTaskDropped = "FLOW003";

    /// <summary>A lifetime handle is created and not stored, or stored in a local that is never used.</summary>
    public const string LifetimeHandleDropped = "FLOW004";

    /// <summary>A FlowTask awaitable is awaited in an async function that does not return FlowTask.</summary>
    public const string FlowAwaitOutsideFlowTask = "FLOW005";

    /// <summary>Signal&lt;T&gt;.Next() / EventSignal&lt;T&gt;.Next() is awaited in a loop (emits between iterations are dropped).</summary>
    public const string SignalNextInLoop = "FLOW006";

    /// <summary>GetAwaiter() of a FlowTask awaitable is called in code instead of by await (it starts the task).</summary>
    public const string DirectGetAwaiter = "FLOW007";

    /// <summary>
    /// The handle of Flow.Spawn is dropped by a statement: the child ends with the current scope, which a spawn written
    /// like UniTask's Forget() does not expect.
    /// </summary>
    public const string SpawnHandleDropped = "FLOW008";

    /// <summary>Flow.Spawn is called in an event handler written in a FlowTask method.</summary>
    public const string SpawnInEventHandler = "FLOW009";

    /// <summary>
    /// A FlowTask awaitable is awaited in a finally block, or in a catch clause that passes its exception on, without
    /// Flow.NonCancelable: a cancel that comes while the block runs skips its rest.
    /// </summary>
    public const string CleanupAwaitNotProtected = "FLOW010";

    internal const string Category = "FlowTask";

    /// <summary>
    /// Base of every rule's helpLinkUri: the rule's section of the analyzer page on the documentation site (docs/en/tools/analyzers.md), whose anchors are the lower-case ids
    /// ('#flow001'). Absolute, so that IDEs and NuGet users can open it.
    /// </summary>
    internal const string HelpLinkBase = "https://katout.github.io/FlowTask/en/tools/analyzers/#";
}
