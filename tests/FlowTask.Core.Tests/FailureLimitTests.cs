namespace Katout.FlowTask.Tests;

/// <summary>
/// The limits that stay by specification: cases where C# itself replaces an exception, so the runtime cannot report
/// it (it cannot see an exception on its way through a finally). These tests pin that the runtime indeed reports
/// nothing more, and that the same code in a live scope behaves as C# does.
/// </summary>
public class FailureLimitTests : FailurePathTestBase
{
    public enum CanceledBy
    {
        NotCanceled,
        Handle,
        AChildItSpawned,
    }

    [Test]
    public void AnExceptionGoingThroughAFinallyThatAwaitsIsReplacedWhenTheScopeIsCanceledThere([Values] CanceledBy canceledBy)
    {
        // The callee's failure goes up into the parent's finally, which awaits. Left alone, the finally ends and the
        // failure goes on (plain C#). Canceled while it waits there, the await throws FlowCanceledException, which
        // replaces the failure (the C# rule): the failure is lost, with no report, and the parent ends Canceled or,
        // canceled by the failure of a child it spawned, with that child's exception.
        CaptureExceptions();

        async FlowTask Parent()
        {
            if (canceledBy == CanceledBy.AChildItSpawned) Flow.Spawn(Throws(0, "spawned bug", 0.15));
            try
            {
                await Throws(0, "child bug", 0.1);
            }
            finally
            {
                Log.Add("finally start");
                await FlowTask.WaitForSeconds(0.2);
                Log.Add("finally end");
            }
        }

        var parent = World.Run(Parent());
        TickFor(0.13);
        AssertLog("throw child bug", "finally start");
        if (canceledBy == CanceledBy.Handle) parent.Cancel();
        TickFor(0.5);
        switch (canceledBy)
        {
            case CanceledBy.NotCanceled:
                AssertLog("throw child bug", "finally start", "finally end");
                AssertFaulted(parent, AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "child bug", "Parent > Throws").Exception);
                break;
            case CanceledBy.Handle:
                AssertLog("throw child bug", "finally start");
                AssertNoExceptions();
                AssertCanceled(parent, CancelCause.Explicit);
                break;
            default:
                AssertLog("throw child bug", "finally start", "throw spawned bug");
                var report = AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "spawned bug", "Parent > Throws");
                AssertFaulted(parent, report.Exception, CancelCause.Fault);
                break;
        }
    }

    [Test]
    public void AFinallyThatThrowsWhileAFailureGoesThroughItReplacesTheFailure()
    {
        // The callee's failure goes up into the parent's finally, and the finally throws. In a live scope that is plain
        // C#: the finally's exception replaces the failure, ends the parent Faulted and is the one reported; the
        // callee's is lost.
        CaptureExceptions();

        async FlowTask Parent()
        {
            try
            {
                await ThrowsIO(1, "child io", 0.1);
            }
            finally
            {
                Log.Add("finally throws");
                throw new FormatException("cleanup bug");
            }
        }

        var parent = World.Run(Parent());
        TickFor(0.3);
        AssertLog("throw child io", "finally throws");
        var report = AssertSingleException<FormatException>(FlowExceptionKind.Unhandled, "cleanup bug", "Parent");
        AssertFaulted(parent, report.Exception);
    }
}
