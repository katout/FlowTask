namespace Katout.FlowTask.Internal;

/// <summary>
/// The World's reports: unhandled exceptions and the other kinds go to OnUnhandledException as they are found, or wait
/// for the outermost Tick, Flush, Run or Dispose to throw them; warnings go to OnWarning, each kind once.
/// </summary>
internal sealed class Reporter
{
    readonly FlowWorld _world;
    readonly List<FlowExceptionInfo> _unhandled = new();
    int _warnedKinds; // one bit per FlowWarningKind already raised in this World

    internal Reporter(FlowWorld world) => _world = world;

    internal bool HasUnhandled => _unhandled.Count > 0;

    /// <summary>
    /// An exception whose receiver was canceled or had settled: reported as <see cref="FlowExceptionKind.Undelivered"/>, with the
    /// path where it was thrown. It changes no result.
    /// </summary>
    internal FlowExceptionInfo ReportUndelivered(FlowExceptionInfo thrown) => ReportUndelivered(thrown.Exception, thrown.ScopePath);

    internal FlowExceptionInfo ReportUndelivered(Exception ex, string scopePath) => Report(new FlowExceptionInfo(ex, scopePath, FlowExceptionKind.Undelivered));

    /// <summary>An exception thrown while a scope ends (its cleanup, a canceled scope's code, onDiscard): reported; the cleanup goes on.</summary>
    internal void ReportCleanupException(FlowNode node, Exception ex) => ReportCleanupException(node.BuildScopePath(), ex);

    internal void ReportCleanupException(string scopePath, Exception ex) => Report(new FlowExceptionInfo(ex, scopePath, FlowExceptionKind.Cleanup));

    /// <summary>A canceled scope returned after FlowCanceledException reached its code: a catch swallowed it.</summary>
    internal void ReportSwallowedCancellation(FlowNode scope)
    {
        var path = scope.BuildScopePath();
        Report(new FlowExceptionInfo(Errors.SwallowedCancellation(path), path, FlowExceptionKind.SwallowedCancellation));
    }

    /// <summary>Hands <paramref name="info"/> to OnUnhandledException, or keeps it for the outermost Tick, Flush, Run or Dispose to throw.</summary>
    internal FlowExceptionInfo Report(FlowExceptionInfo info)
    {
        var handler = _world.OnUnhandledException;
        if (handler == null)
        {
            _unhandled.Add(info);
            return info;
        }

        try
        {
            handler(info);
        }
#pragma warning disable CA1031 // a failing OnUnhandledException handler must not hide the report it was given
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Both are thrown at the end of the call: the report, then the handler's own failure (a handler that rethrows
            // the reported exception, to fail fast in a test, did not fail on its own).
            _unhandled.Add(info);
            if (!ReferenceEquals(ex, info.Exception)) _unhandled.Add(new FlowExceptionInfo(ex, "<FlowWorld.OnUnhandledException handler>", FlowExceptionKind.Unhandled));
        }

        return info;
    }

    /// <summary>Raises a warning of <paramref name="kind"/> the first time it happens in this World.</summary>
    internal void WarnOnce(FlowWarningKind kind, string message, FlowNode node)
    {
        var bit = 1 << (int)kind;
        if ((_warnedKinds & bit) != 0) return;
        _warnedKinds |= bit;
        var handlers = _world.WarningHandlers;
        if (handlers == null) return;
        var warning = new FlowWarning(kind, message, node?.BuildScopePath());
        // A handler that throws does not stop the others: its failure is an unhandled exception.
        foreach (var h in handlers.GetInvocationList())
        {
            try
            {
                ((Action<FlowWarning>)h)(warning);
            }
#pragma warning disable CA1031 // user callback (OnWarning handler): reported instead of breaking the scheduler
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Report(new FlowExceptionInfo(ex, "<FlowWorld.OnWarning handler>", FlowExceptionKind.Unhandled));
            }
        }
    }

    /// <summary>Throws the reports no OnUnhandledException took, and forgets them.</summary>
    internal void ThrowUnhandled()
    {
        var infos = _unhandled.ToArray();
        _unhandled.Clear();
        throw new FlowUnhandledException(infos);
    }
}
