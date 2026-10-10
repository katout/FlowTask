namespace Katout.FlowTask;

/// <summary>The state of a FlowTask or handle, and how it ended.</summary>
public enum FlowStatus : byte
{
    /// <summary>
    /// Not a task any more, or never one: the task was consumed (its node went back to the pool), a directly awaited
    /// NextFrame, DelayFrames or WaitForSeconds ran without a node, or the value is <c>default(FlowTask&lt;T&gt;)</c>.
    /// </summary>
    Invalid = 0,

    /// <summary>Created, not started.</summary>
    Unstarted,

    /// <summary>Started and not ended. A canceled task stays Running while its cleanup runs.</summary>
    Running,

    /// <summary>Its code returned, or its wait completed, and it was not canceled.</summary>
    Succeeded,

    /// <summary>Ended by a cancellation (<see cref="CancelCause"/> tells why). An exception that reached it after that was only reported.</summary>
    Canceled,

    /// <summary>
    /// Ended with an exception: one that left its code while it was live, or one it carried (a child it spawned failed,
    /// or the library found a misuse while it ran). A handle's <see cref="FlowHandle.Exception"/> holds it.
    /// </summary>
    Faulted,
}

/// <summary>Whether a scope was asked to end, and why. Independent of <see cref="FlowStatus"/>.</summary>
public enum CancelCause : byte
{
    /// <summary>Not canceled.</summary>
    None = 0,

    /// <summary>Its parent ended while it ran. Scopes under a canceled scope take that scope's cause.</summary>
    ParentEnded,

    /// <summary>Another branch won its Race.</summary>
    RaceLost,

    /// <summary><see cref="FlowHandle.Cancel"/> on it or an ancestor.</summary>
    Explicit,

    /// <summary>A failure: a sibling in a Race or WhenAll threw, its parent ended with an exception, a child it spawned failed, or a misuse was found in it.</summary>
    Fault,

    /// <summary><see cref="FlowWorld.Dispose"/>.</summary>
    WorldDisposed,
}

/// <summary>
/// What a <see cref="FlowExceptionInfo"/> reports to <see cref="FlowWorld.OnUnhandledException"/>. An
/// <see cref="Unhandled"/> exception ended a root flow; the other kinds are reports that change no result. The engine
/// integrations log Undelivered as a warning, the others as errors.
/// </summary>
public enum FlowExceptionKind
{
    /// <summary>An exception that no flow caught: it ended a flow started with FlowWorld.Run, or code outside every flow (a posted action, a handler).</summary>
    Unhandled,

    /// <summary>An exception thrown while a scope ends: by a canceled scope's code, AddCleanup, Own's Dispose, or onDiscard. The cleanup goes on.</summary>
    Cleanup,

    /// <summary>A canceled scope returned after FlowCanceledException reached its code: a catch swallowed it. It ended canceled.</summary>
    SwallowedCancellation,

    /// <summary>
    /// An exception whose receiver was canceled or had settled: the awaiting scope was canceled, a Race had a winner, a
    /// WhenAll had failed with an earlier exception, a bridged Task failed after its bridge was canceled, a spawned child
    /// failed while its owner was ending. It happens in normal play (two requests that fail in one frame).
    /// </summary>
    Undelivered,
}

/// <summary>An exception and the scope path where it was thrown.</summary>
/// <param name="Exception">The exception.</param>
/// <param name="ScopePath">Names from the root to the scope.</param>
/// <param name="Kind">What is reported.</param>
public sealed record FlowExceptionInfo(Exception Exception, string ScopePath, FlowExceptionKind Kind)
{
    /// <summary>The exception.</summary>
    public Exception Exception { get; } = Exception ?? throw new ArgumentNullException(nameof(Exception));

    /// <summary>Names from the root to the scope: <c>Game &gt; InGame &gt; Battle</c>.</summary>
    public string ScopePath { get; } = ScopePath ?? "";

    /// <summary><c>[Kind] at ScopePath: Exception</c>.</summary>
    public override string ToString() => $"[{Kind}] at {ScopePath}: {Exception}";
}

/// <summary>What a <see cref="FlowWarning"/> reports. Each kind is raised once per World.</summary>
public enum FlowWarningKind
{
    /// <summary>A flush reached its limit of resumes and left the rest for the next one: two flows that resume each other without end?</summary>
    FlushLimit,

    /// <summary>A flow paused the clock it runs on (or an ancestor): its own resumes wait for the pause to be released.</summary>
    PausedOwnClock,

    /// <summary>
    /// A canceled scope still waited in its cleanup (an await of a catch or finally block, or a Flow.NonCancelable
    /// await) 10 seconds of unscaled time after it began to: nothing cancels those awaits.
    /// </summary>
    LongCleanup,

    /// <summary>
    /// World.Dispose ended an await of a catch or finally block, or a Flow.NonCancelable await: it runs no Tick, so the
    /// rest of that block did not run.
    /// </summary>
    CleanupCutAtDispose,
}

/// <summary>A warning for <see cref="FlowWorld.OnWarning"/>: something abnormal that changed no result.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Message">What happened and what to do, in words.</param>
/// <param name="ScopePath">The scope where it happened, or null when it is not about one scope.</param>
public sealed record FlowWarning(FlowWarningKind Kind, string Message, string ScopePath)
{
    /// <summary><c>[Kind] Message at ScopePath</c>.</summary>
    public override string ToString() => ScopePath == null ? $"[{Kind}] {Message}" : $"[{Kind}] {Message} at {ScopePath}";
}

/// <summary>
/// Thrown at an await to unwind a canceled scope (a new instance each time). Let it pass (FLOW001): a catch of it or of
/// OperationCanceledException ends with <c>throw;</c>, and a catch-all excludes it with
/// <c>when (e is not FlowCanceledException)</c>. Once it has reached a scope's code, the awaits of its catch and finally
/// blocks run as usual.
/// </summary>
/// <remarks>
/// It derives from OperationCanceledException, so the .NET conventions apply: <c>catch (OperationCanceledException) { ...; throw; }</c>
/// runs, and <c>when (e is not OperationCanceledException)</c> lets it pass. Its CancellationToken is None. An
/// OperationCanceledException that is not this type (a Task canceled outside the flow) is an ordinary exception.
/// </remarks>
[DebuggerNonUserCode]
public sealed class FlowCanceledException : OperationCanceledException
{
    internal FlowCanceledException() : base("The flow scope was canceled and is unwinding. Let it propagate: end a catch of it with 'throw;' (FLOW001).")
    {
    }
}

/// <summary>A misuse of the API found at run time (a second await, an await outside a flow, an unbridged await...).</summary>
public class FlowMisuseException : InvalidOperationException
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public FlowMisuseException(string message) : base(message) { }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public FlowMisuseException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>An object bound to a World was used from another thread.</summary>
public sealed class FlowThreadException : FlowMisuseException
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public FlowThreadException(string message) : base(message) { }
}

/// <summary>A signal closed while a scope waited on <c>Next()</c>. When it was closed with an error, that error is the InnerException.</summary>
public sealed class SignalClosedException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public SignalClosedException(string message) : base(message) { }

    /// <summary>Creates the exception with <paramref name="message"/> and the error the signal was closed with.</summary>
    public SignalClosedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A subscription with <see cref="BufferOverflow.Fail"/> overflowed.</summary>
public sealed class SubscriptionOverflowException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public SubscriptionOverflowException(string message) : base(message) { }
}

/// <summary>A joined task did not succeed. When it faulted, its exception is the InnerException.</summary>
public sealed class FlowJoinException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public FlowJoinException(string message) : base(message) { }

    internal FlowJoinException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown by the outermost Tick, Flush, Run or Dispose when no <see cref="FlowWorld.OnUnhandledException"/> handler took
/// the World's reports (unhandled exceptions, and the other <see cref="FlowExceptionKind"/>s), or the handler threw.
/// </summary>
public sealed class FlowUnhandledException : AggregateException
{
    internal FlowUnhandledException(FlowExceptionInfo[] infos)
        : base(BuildMessage(infos), Array.ConvertAll(infos, i => i.Exception))
    {
        ExceptionInfos = infos;
    }

    /// <summary>The reports, in the order they were made; <see cref="AggregateException.InnerExceptions"/> holds their exceptions.</summary>
    public IReadOnlyList<FlowExceptionInfo> ExceptionInfos { get; }

    static string BuildMessage(FlowExceptionInfo[] infos)
    {
        var first = infos[0];
        var head = infos.Length == 1 ? "Unhandled flow exception" : $"{infos.Length} unhandled flow exceptions. First";
        return $"{head} [{first.Kind}] at '{first.ScopePath}': {first.Exception.Message}";
    }
}
