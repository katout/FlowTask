namespace Katout.FlowTask.Unity;

/// <summary>
/// Wraps a report to <see cref="FlowWorld.OnUnhandledException"/> for the Unity console. <c>Debug.LogException</c>
/// prints the original exception with its stack trace first and then
/// "Rethrow as FlowTaskUnhandledException: [Unhandled] at 'Game &gt; InGame &gt; Battle': ...".
/// </summary>
public sealed class FlowTaskUnhandledException : Exception
{
    public FlowTaskUnhandledException(FlowExceptionInfo info)
        : base(BuildMessage(info), info?.Exception)
    {
        Info = info;
    }

    /// <summary>The report as the World made it (kind, scope path, original exception).</summary>
    public FlowExceptionInfo Info { get; }

    static string BuildMessage(FlowExceptionInfo info)
    {
        if (info == null) return "Unhandled flow exception.";
        var path = string.IsNullOrEmpty(info.ScopePath) ? "<root>" : info.ScopePath;
        return $"[{info.Kind}] at '{path}': {info.Exception.GetType().Name}: {info.Exception.Message}";
    }
}
