using System.IO;

namespace Katout.FlowTask.Tests;

/// <summary>
/// Shared helpers for the tests of the failure paths: an awaited child's exception is rethrown at the await, it goes up
/// one resumption at a time in the queue like a value, and an exception that no receiver can take any more is reported as
/// <see cref="FlowExceptionKind.Undelivered"/> without changing any result.
/// </summary>
public abstract class FailurePathTestBase : FlowTestBase
{
    /// <summary>Waits <paramref name="delay"/> seconds <paramref name="depth"/> awaits below the caller, then throws.</summary>
    protected async FlowTask Throws(int depth, string message, double delay)
    {
        if (depth == 0)
        {
            await FlowTask.WaitForSeconds(delay);
            Log.Add("throw " + message);
            throw new InvalidOperationException(message);
        }

        await Throws(depth - 1, message, delay);
    }

    /// <summary><see cref="Throws"/> with an <see cref="IOException"/> (an expected failure, such as a network error).</summary>
    protected async FlowTask ThrowsIO(int depth, string message, double delay)
    {
        if (depth == 0)
        {
            await FlowTask.WaitForSeconds(delay);
            Log.Add("throw " + message);
            throw new IOException(message);
        }

        await ThrowsIO(depth - 1, message, delay);
    }

    /// <summary>The path of <see cref="Throws"/> / <see cref="ThrowsIO"/> started at <paramref name="parent"/>.</summary>
    protected static string PathOf(string parent, string method, int depth)
    {
        var path = parent;
        for (var i = 0; i <= depth; i++) path += " > " + method;
        return path;
    }

    protected string DescribeExceptions() =>
        Exceptions.Count == 0 ? "no reports" : string.Join("; ", Exceptions.Select(p => $"[{p.Kind}] {p.Exception.GetType().Name}('{p.Exception.Message}') at '{p.ScopePath}'"));

    protected void AssertNoExceptions() => Assert.That(Exceptions, Is.Empty, DescribeExceptions() + " | log: " + Log);

    /// <summary>Exactly one report, of <paramref name="kind"/>, whose exception is a <typeparamref name="TException"/> with <paramref name="message"/>.</summary>
    protected FlowExceptionInfo AssertSingleException<TException>(FlowExceptionKind kind, string message, string scopePath = null) where TException : Exception
    {
        Assert.That(Exceptions.Count, Is.EqualTo(1), DescribeExceptions() + " | log: " + Log);
        AssertException<TException>(Exceptions[0], kind, message, scopePath);
        return Exceptions[0];
    }

    protected void AssertException<TException>(FlowExceptionInfo report, FlowExceptionKind kind, string message, string scopePath = null) where TException : Exception
    {
        Assert.That(report.Kind, Is.EqualTo(kind), DescribeExceptions());
        Assert.That(report.Exception, Is.TypeOf<TException>(), DescribeExceptions());
        if (message != null) Assert.That(report.Exception.Message, Is.EqualTo(message), DescribeExceptions());
        if (scopePath != null) Assert.That(report.ScopePath, Is.EqualTo(scopePath), DescribeExceptions());
    }

    /// <summary>The reports of <paramref name="kind"/>, in the order they were reported.</summary>
    protected FlowExceptionInfo[] ExceptionsOf(FlowExceptionKind kind) => Exceptions.Where(p => p.Kind == kind).ToArray();

    protected static void AssertCanceled(FlowHandle h, CancelCause cause) => AssertCanceled((FlowHandle<FlowUnit>)h, cause);

    protected static void AssertSucceeded(FlowHandle h) => AssertSucceeded((FlowHandle<FlowUnit>)h);

    protected static void AssertFaulted(FlowHandle h, Exception expected, CancelCause cause = CancelCause.None) =>
        AssertFaulted((FlowHandle<FlowUnit>)h, expected, cause);

    protected static void AssertFaulted(FlowHandle h, string message, CancelCause cause = CancelCause.None) =>
        AssertFaulted((FlowHandle<FlowUnit>)h, message, cause);

    /// <summary>A handle that ended without a failure passing through it.</summary>
    protected static void AssertCanceled<T>(FlowHandle<T> h, CancelCause cause)
    {
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(h.CancelCause, Is.EqualTo(cause));
        Assert.That(h.Exception, Is.Null, "a Canceled handle has no Exception");
    }

    protected static void AssertSucceeded<T>(FlowHandle<T> h)
    {
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.Exception, Is.Null);
    }

    /// <summary>A handle that a failure ended: Faulted, and its Exception is that exception.</summary>
    protected static void AssertFaulted<T>(FlowHandle<T> h, Exception expected, CancelCause cause = CancelCause.None)
    {
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(h.CancelCause, Is.EqualTo(cause));
        Assert.That(h.Exception, Is.SameAs(expected), "a Faulted handle holds its exception");
    }

    protected static void AssertFaulted<T>(FlowHandle<T> h, string message, CancelCause cause = CancelCause.None)
    {
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(h.CancelCause, Is.EqualTo(cause));
        Assert.That(h.Exception, Is.Not.Null, "a Faulted handle holds its exception");
        Assert.That(h.Exception.Message, Is.EqualTo(message));
    }
}
