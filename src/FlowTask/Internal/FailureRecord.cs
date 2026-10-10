using System.Runtime.ExceptionServices;

namespace Katout.FlowTask.Internal;

internal enum FailureKind : byte
{
    /// <summary>The node ended with an exception that its consumer receives.</summary>
    Fault,

    /// <summary>
    /// A scope canceled because of a failure it did not throw (a child it spawned failed, or the library found a misuse
    /// while it ran). It ends with that exception once unwound, unless an exception of its own leaves it first.
    /// </summary>
    Carried,

    /// <summary>The node ended with an exception that nobody could take; it was reported as Undelivered.</summary>
    Undelivered,
}

/// <summary>How a node failed: the exception, the path where it was thrown, and how it is delivered.</summary>
internal sealed class FailureRecord
{
    internal FailureKind Kind;
    internal FlowExceptionInfo Info;

    /// <summary>Captured once where the exception left a flow's code, and thrown again at each await it goes up through.</summary>
    internal ExceptionDispatchInfo Dispatch;

    /// <summary>A fault that a parked await has yet to throw (its clock was removed: FlowNode.FailPark).</summary>
    internal bool Pending;

    internal static FailureRecord ForFault(Exception ex, string scopePath) =>
        new() { Kind = FailureKind.Fault, Info = new FlowExceptionInfo(ex, scopePath, FlowExceptionKind.Unhandled), Dispatch = ExceptionDispatchInfo.Capture(ex) };

    internal static FailureRecord ForUndelivered(FlowExceptionInfo report) => new() { Kind = FailureKind.Undelivered, Info = report };

    /// <summary>What a scope canceled by <paramref name="fault"/> carries: the same exception, in a record of its own.</summary>
    internal static FailureRecord Carry(FailureRecord fault) => new() { Kind = FailureKind.Carried, Info = fault.Info, Dispatch = fault.Dispatch };
}
