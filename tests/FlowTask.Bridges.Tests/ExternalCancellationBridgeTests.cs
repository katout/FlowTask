using System.Linq;
using Cysharp.Threading.Tasks;

namespace Katout.FlowTask.Bridges.Tests;

/// <summary>
/// Exceptions through the Task and UniTask bridges (docs/en/integrations/task.md): the exception of a bridged UniTask or Task is thrown at the await,
/// where a catch receives it, and it is unhandled only when nothing catches it. A UniTask canceled outside (TrySetCanceled, a method that honours its own token) throws a plain
/// OperationCanceledException, which <c>catch (TaskCanceledException)</c> misses and
/// <c>catch (OperationCanceledException e) when (e is not FlowCanceledException)</c> takes. A root flow ended by an
/// uncaught external cancellation is Faulted: AsTask faults with the original exception, and ToUniTask, which turns an
/// OperationCanceledException into cancellation, is Canceled.
/// </summary>
public class ExternalCancellationBridgeTests : BridgeTestBase
{
    [Test]
    public void TheUniTasksExceptionIsThrownAtTheAwait([Values(false, true)] bool catches)
    {
        // The awaiting flow's catch receives the exception; without a catch the flow ends Faulted with it and it is
        // reported once.
        async FlowTask Root()
        {
            try
            {
                await FlowUniTask.FromUniTask<int>(_ => UniTask.FromException<int>(new InvalidOperationException("bug")));
                Log.Add("unreachable");
            }
            catch (InvalidOperationException e) when (catches)
            {
                Log.Add("caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        Tick();
        if (catches)
        {
            AssertLog("caught bug");
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(Test.Exceptions, Is.Empty);
        }
        else
        {
            AssertLog();
            var report = Test.Exceptions.Single();
            Assert.That(report.Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
            Assert.That(report.Exception, Is.InstanceOf<InvalidOperationException>());
            Assert.That(report.ScopePath, Does.StartWith("Root > Task"));
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
            Assert.That(h.Exception, Is.SameAs(report.Exception));
            Test.AcceptExceptions();
        }
    }

    public enum CatchForm
    {
        None,
        CatchTaskCanceled,
        CatchExternalOnly,
    }

    public enum UniTaskCancel
    {
        /// <summary><c>UniTaskCompletionSource.TrySetCanceled</c>.</summary>
        SourceCanceled,
        /// <summary>A UniTask method that honours its own token.</summary>
        OwnToken,
    }

    [Test]
    public void AUniTaskCanceledOutsideThrowsAPlainOperationCanceledExceptionAtTheAwait([Values] UniTaskCancel how, [Values] CatchForm form)
    {
        // The external cancellation is an exception. catch (TaskCanceledException) does not take it (UniTask
        // throws OperationCanceledException), the general form does; uncaught, the root flow ends Faulted.
        var source = new UniTaskCompletionSource<int>();
        var gate = new UniTaskCompletionSource();
        using var own = new CancellationTokenSource();
        own.Cancel();

        async UniTask<int> Download(CancellationToken token)
        {
            await gate.Task;
            token.ThrowIfCancellationRequested();
            return 1;
        }

        async FlowTask Root()
        {
            try
            {
                var v = how == UniTaskCancel.SourceCanceled
                    ? await FlowUniTask.FromUniTask(_ => source.Task)
                    : await FlowUniTask.FromUniTask(_ => Download(own.Token));
                Log.Add("got " + v);
            }
            catch (TaskCanceledException e) when (form == CatchForm.CatchTaskCanceled)
            {
                Log.Add("caught " + e.GetType().Name);
            }
            catch (OperationCanceledException e) when (form == CatchForm.CatchExternalOnly && e is not FlowCanceledException)
            {
                Log.Add("caught " + e.GetType().Name);
            }
        }

        var h = World.Run(Root());
        Tick();
        if (how == UniTaskCancel.SourceCanceled) source.TrySetCanceled();
        else gate.TrySetResult();
        Tick(2);
        if (form == CatchForm.CatchExternalOnly)
        {
            AssertLog("caught OperationCanceledException");
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(Test.Exceptions, Is.Empty);
        }
        else
        {
            AssertLog();
            var report = Test.Exceptions.Single();
            Assert.That(report.Kind, Is.EqualTo(FlowExceptionKind.Unhandled));
            Assert.That(report.Exception.GetType(), Is.EqualTo(typeof(OperationCanceledException)));
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
            Test.AcceptExceptions();
        }
    }

    [Test]
    public void ARootEndedByAnUncaughtExternalCancellationFaultsItsTaskAndCancelsItsUniTask()
    {
        // The root flow awaits a Task that is canceled outside and does not catch it: it ends Faulted with the
        // TaskCanceledException. AsTask faults with that exception; ToUniTask turns an
        // OperationCanceledException into cancellation, so its UniTask is Canceled.
        var tcs = new TaskCompletionSource<int>();

        async FlowTask<int> Root() => await tcs.Task.AsFlow();

        var h = World.Run(Root());
        var u = h.ToUniTask();
        var t = h.AsTask();
        Tick();
        tcs.SetCanceled();
        Tick(2);
        SpinUntil(() => u.Status != UniTaskStatus.Pending && t.IsCompleted);
        var report = Test.Exceptions.Single();
        Assert.That(report.Exception, Is.TypeOf<TaskCanceledException>());
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(t.IsFaulted, Is.True, t.Status.ToString());
        Assert.That(t.Exception.InnerException, Is.SameAs(report.Exception));
        Assert.That(u.Status, Is.EqualTo(UniTaskStatus.Canceled));
        Test.AcceptExceptions();
    }
}
