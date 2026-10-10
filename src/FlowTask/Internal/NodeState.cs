namespace Katout.FlowTask.Internal;

internal enum NodeState : byte
{
    Pooled = 0,
    Unstarted = 1,
    Running = 2,
    Succeeded = 3,
    Canceled = 4,
    Faulted = 5,
}

[Flags]
internal enum NodeFlags : ushort
{
    None = 0,
    /// <summary>Cancellation is confirmed.</summary>
    CancelConfirmed = 1 << 0,
    /// <summary>State machines: FlowCanceledException has reached the code; its catch and finally blocks run.</summary>
    UnwindBegun = 1 << 1,
    /// <summary>Settled or ending: the node takes no other outcome.</summary>
    Completing = 1 << 2,
    /// <summary>End has started (maybe held for the children); Terminate does nothing more.</summary>
    Ending = 1 << 3,
    /// <summary>Never pooled: a handle may observe it at any time (Run, Spawn, bridges).</summary>
    NoPool = 1 << 4,
    /// <summary>Nothing will read the result: released when it ends.</summary>
    ConsumerGone = 1 << 5,
    /// <summary>Released while its MoveNext ran: released when it returns.</summary>
    ReleasePending = 1 << 6,
    /// <summary>Started by Flow.Spawn or FlowWorld.Run.</summary>
    Spawned = 1 << 7,
    InTickList = 1 << 8,
    /// <summary>The handle was joined (once only).</summary>
    HandleAwaited = 1 << 9,
    /// <summary>State machines: waits in the tick list for a frame or time of its own clock, without a node.</summary>
    ParkedWait = 1 << 10,
    /// <summary>The end, or the unwinding of a canceled state machine, waits for the children to end.</summary>
    WaitsForChildren = 1 << 11,
    /// <summary>Leaves: holds a value its consumer has not received; released so, it goes back to its source.</summary>
    Unclaimed = 1 << 12,
    /// <summary>
    /// State machines: a resume for the current await is queued. A resume queued for an earlier await finds it cleared:
    /// the scope has run since (its unwinding), and may wait on something else now.
    /// </summary>
    ResumeQueued = 1 << 13,
    /// <summary>Leaves: failed, and the failure waits in the queue for its turn to reach the awaiter.</summary>
    FaultQueued = 1 << 14,
    /// <summary>
    /// Flow.NonCancelable: the cancellation of the scopes around does not enter this node (World.Dispose does), and the
    /// canceled scope that awaits it takes its result. The last bit: another flag widens the enum.
    /// </summary>
    NonCancelable = 1 << 15,
}

/// <summary>What kind of node this is; set by the node type, never reset by pooling.</summary>
[Flags]
internal enum NodeTraits : byte
{
    None = 0,
    StateMachine = 1 << 0,
    Leaf = 1 << 1,
    /// <summary>A frame or time wait on the awaiting scope's clock, which the scope can wait for itself.</summary>
    ParkableWait = 1 << 2,
}

internal enum AwaitKind : byte
{
    None = 0,
    Library,
    /// <summary>The await drops the continuation: the scope ends there.</summary>
    Discard,
}

internal enum AwaitMode : byte
{
    Value,
    Pending,
    Completed,
    /// <summary>An await in a canceled scope: throws FlowCanceledException once its continuation registers.</summary>
    CancelOnRegister,
    /// <summary>A clock wait not started: OnCompleted parks the scope.</summary>
    Parkable,
}

internal enum ParkKind : byte
{
    Frames,
    Time,
}

internal enum ParkEnd : byte
{
    NotParked,
    Resumed,
    Unwind,
}
