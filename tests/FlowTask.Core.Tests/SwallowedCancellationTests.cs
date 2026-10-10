using System.IO;
using System.Threading;

// The catch forms that FLOW001 reports (a catch that takes FlowCanceledException and returns or goes on) are what this file
// tests at run time. Every catch clause here that can observe FlowCanceledException is one of the forms under test (the
// CatchForm cases and the swallows of the other tests), so the rule is off for the whole file rather than around each of
// them; a catch that is not under test does not belong in this file.
#pragma warning disable FLOW001 // on purpose: every catch clause in this file that can observe FlowCanceledException is a form under test

namespace Katout.FlowTask.Tests;

/// <summary>
/// A canceled scope that returns normally after FlowCanceledException reached its code (a catch took it and returned) is
/// reported once as <see cref="FlowExceptionKind.SwallowedCancellation"/> (a FlowMisuseException) and ends Canceled;
/// the Race, WhenAll or caller around it goes on. Once the exception has reached the scope's code, its awaits run as a
/// live scope's: a catch that goes on keeps running, a loop keeps looping, and the scope is reported only if it
/// returns. A scope canceled while it ran that returns without ever receiving the exception is not reported. An
/// OperationCanceledException that is not FlowCanceledException (an external cancellation) is an exception like any
/// other. While World.Dispose ends the flows, the awaits of a canceled scope throw again, up to a limit, and nothing is
/// reported.
/// </summary>
public class SwallowedCancellationTests : FailurePathTestBase
{
    [Test]
    public void ACatchThatTakesTheCancellationAndReturnsIsReportedOnceAndTheRaceAndItsCallerGoOn()
    {
        // The Race's loser catches its cancellation and returns. The swallow is reported once, to OnUnhandledException, with a message
        // that names the scope and the fix; it is not thrown at the caller, which gets the winner and goes on.
        CaptureExceptions();

        async FlowTask Loser()
        {
            try
            {
                await FlowTask.WaitForSeconds(5.0);
            }
            catch (OperationCanceledException)
            {
                Log.Add("loser swallowed");
                return;
            }

            Log.Add("unreachable");
        }

        async FlowTask Game()
        {
            try
            {
                var r = await FlowTask.Race(FlowTask.WaitForSeconds(0.1), Loser());
                Log.Add("race won by " + r.Index);
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                Log.Add("game caught " + e.GetType().Name);
            }

            await FlowTask.WaitForSeconds(0.2);
            Log.Add("game continues");
        }

        var h = World.Run(Game());
        TickFor(0.6);
        AssertLog("loser swallowed", "race won by 0", "game continues");
        var report = AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null, "Game > Loser");
        Assert.That(report.Exception.Message, Does.StartWith("Scope 'Game > Loser' returned after FlowCanceledException reached its code")
            .And.Contain("'throw;'").And.Contain("when (e is not FlowCanceledException)").And.Contain("FLOW001"));
        AssertSucceeded(h);
    }

    [Test]
    public void ACatchThatTakesTheCancellationAndGoesOnKeepsRunningAndIsReportedWhenItReturns()
    {
        // The code after the catch runs as a cleanup does: its await runs to its end instead of throwing again. The scope
        // stays Running meanwhile (its handle tells that it is closing) and is reported once it returns, then ends Canceled.
        CaptureExceptions();

        async FlowTask Swallower()
        {
            try
            {
                await FlowTask.WaitForSeconds(100);
            }
            catch (FlowCanceledException)
            {
                Log.Add("swallowed");
            }

            Log.Add("continues");
            await FlowTask.DelayFrames(3);
            Log.Add("resumed after the catch");
        }

        var h = World.Run(Swallower());
        Tick();
        h.Cancel();
        Tick(2);
        AssertLog("swallowed", "continues");
        AssertNoExceptions();
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
        Tick(2);
        AssertLog("swallowed", "continues", "resumed after the catch");
        AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null, "Swallower");
        AssertCanceled(h, CancelCause.Explicit);
    }

    [Test]
    public void ACatchThatSwallowsInALoopKeepsRunningUntilDisposeWhichEndsItWithoutAReport()
    {
        // The poller catches its cancellation and polls on: a cancel does not end it (FLOW001 is the guard), and it is not
        // reported, since it never returns. World.Dispose runs no Tick: each await throws FlowCanceledException again and
        // the catch takes it, 32 times; the next await ends the scope there. No exception is reported during Dispose (a warning
        // says that it cut a cleanup), and the flow
        // keeps the cause of its first cancellation.
        CaptureExceptions();
        var rounds = 0;

        async FlowTask Poller()
        {
            while (true)
            {
                try
                {
                    rounds++;
                    await FlowTask.NextFrame();
                }
                catch (FlowCanceledException)
                {
                    Log.Add("caught");
                }
            }
        }

        var h = World.Run(Poller());
        Tick();
        h.Cancel();
        Tick(10);
        AssertLog("caught");
        Assert.That(rounds, Is.EqualTo(12), "it polled on after the cancel");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));

        World.Dispose();
        Assert.That(Log.Entries.Count(e => e == "caught"), Is.EqualTo(1 + 32), "log: " + Log);
        Assert.That(rounds, Is.EqualTo(12 + 32));
        AssertNoExceptions();
        AssertCanceled(h, CancelCause.Explicit);
    }

    [Test]
    public void AScopeCanceledWhileItRunsThatReturnsWithoutAnotherAwaitIsNotReported()
    {
        // The scope cancels its own handle and returns before any await: FlowCanceledException never reached its code, so
        // nothing was swallowed. It ends Canceled without a report.
        CaptureExceptions();
        FlowHandle self = default;

        async FlowTask Worker()
        {
            await FlowTask.NextFrame();
            try
            {
                self.Cancel();
                Log.Add("canceled itself");
            }
            catch (OperationCanceledException)
            {
                Log.Add("unreachable");
            }

            Log.Add("returns");
        }

        self = World.Run(Worker());
        Tick(2);
        AssertLog("canceled itself", "returns");
        AssertNoExceptions();
        AssertCanceled(self, CancelCause.Explicit);
    }

    [Test]
    public void ACatchOfOperationCanceledExceptionTakesAnExternalCancellationAsAnExceptionButAFlowCancellationIsReported()
    {
        // The same catch (OperationCanceledException) { return 0; } in two flows. An external cancellation (a
        // library's own token) is an exception like any other: the catch takes it and the flow returns 0. The flow's own
        // cancellation is FlowCanceledException, which the catch also takes (it derives from OperationCanceledException):
        // returning is a swallow, reported, and the flow ends Canceled.
        CaptureExceptions();

        async FlowTask ExternalCancel()
        {
            await FlowTask.NextFrame();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        }

        async FlowTask<int> Fetch(bool external)
        {
            try
            {
                if (external) await ExternalCancel();
                else await FlowTask.WaitForSeconds(100);
                return 1;
            }
            catch (OperationCanceledException e)
            {
                Log.Add((external ? "external: " : "flow: ") + e.GetType().Name);
                return 0;
            }
        }

        var a = World.Run(Fetch(true));
        var b = World.Run(Fetch(false));
        Tick(2);
        b.Cancel();
        Tick(2);
        AssertLog("external: OperationCanceledException", "flow: FlowCanceledException");
        AssertSucceeded(a);
        Assert.That(a.Result, Is.Zero);
        AssertCanceled(b, CancelCause.Explicit);
        AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null, "Fetch");
    }

    [Test]
    public void ACatchBodyThatRunsWhileTheScopeIsLiveIsUnwoundAtItsAwait([Values] bool catchesTheCancellation)
    {
        // The callee's failure reaches the catch while the flow is live, and the catch shows a dialog. That is not a
        // cleanup: the cancel that comes during the dialog cancels it, and the dialog's await throws FlowCanceledException,
        // so the rest of the catch does not run. Catching the cancellation there and returning is a swallow, reported.
        CaptureExceptions();
        FlowHandle parent = default;

        async FlowTask Dialog()
        {
            Log.Add("dialog shown");
            await FlowTask.WaitForSeconds(0.2);
            Log.Add("dialog closed");
        }

        async FlowTask Parent()
        {
            try
            {
                await Throws(1, "net", 1.0);
            }
            catch (InvalidOperationException)
            {
                Log.Add("catch start");
                if (!catchesTheCancellation)
                {
                    await Dialog();
                }
                else
                {
                    try
                    {
                        await Dialog();
                    }
                    catch (FlowCanceledException)
                    {
                        Log.Add("cancellation caught, return");
                        return;
                    }
                }

                Log.Add("catch end");
            }

            Log.Add("after catch");
            await FlowTask.NextFrame();
        }

        async FlowTask Killer()
        {
            await FlowTask.WaitForSeconds(1.1);
            Log.Add("cancel parent");
            parent.Cancel();
        }

        parent = World.Run(Parent());
        World.Run(Killer());
        TickFor(1.6);
        if (catchesTheCancellation)
        {
            AssertLog("throw net", "catch start", "dialog shown", "cancel parent", "cancellation caught, return");
            AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null, "Parent");
        }
        else
        {
            AssertLog("throw net", "catch start", "dialog shown", "cancel parent");
            AssertNoExceptions();
        }

        AssertCanceled(parent, CancelCause.Explicit);
    }

    [Test]
    public void ANewsPanelThatHidesItselfOnOperationCanceledExceptionDoesNotTakeTheMenuDown([Values] bool backPressed)
    {
        // The UniTask habit catch (OperationCanceledException) { hide; return; }. A request that times out outside
        // (TaskCanceledException) is an exception: the panel hides itself and the Race goes on with it. Pressing Back
        // cancels the panel: its catch takes FlowCanceledException too and returns, which is reported as a swallow; the
        // panel ends Canceled and the main menu stays alive.
        CaptureExceptions();
        var news = new TaskCompletionSource<string>();
        var back = new Signal<int>(World, "Back");

        async FlowTask NewsPanel()
        {
            try
            {
                Log.Add("news: " + await FlowBridge.FromTask(_ => news.Task));
            }
            catch (OperationCanceledException)
            {
                Log.Add("news: canceled, hide the panel");
                return;
            }

            await FlowTask.WaitForSeconds(10);
        }

        async FlowTask MainMenu()
        {
            try
            {
                var r = await FlowTask.Race(NewsPanel(), back.Next());
                Log.Add("menu: race index " + r.Index);
                await FlowTask.WaitForSeconds(0.2);
                Log.Add("menu: still alive");
            }
            finally
            {
                Log.Add("menu finally");
            }
        }

        var h = World.Run(MainMenu());
        Tick();
        if (backPressed) back.Emit(1);
        else news.SetException(new TaskCanceledException("HttpClient.Timeout"));
        Tick();
        TickFor(0.5);
        AssertLog("news: canceled, hide the panel", "menu: race index " + (backPressed ? 1 : 0), "menu: still alive", "menu finally");
        if (backPressed) AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null, "MainMenu > NewsPanel");
        else AssertNoExceptions();
        AssertSucceeded(h);
    }

    [Test]
    public void ASwallowFoundWhileAnExceptionUnwindsTheScopeDoesNotReplaceIt([Values] bool callerCatches)
    {
        // The child the scope spawned fails, which cancels the scope carrying the child's exception (a network error, say).
        // The scope swallows the cancellation (a catch that returns): the swallow is reported on its own, and the scope
        // still ends with the exception it carries, which its caller receives.
        CaptureExceptions();

        async FlowTask Bomb()
        {
            await FlowTask.NextFrame();
            throw new IOException("503");
        }

        async FlowTask Swallower()
        {
            Flow.Spawn(Bomb());
            try
            {
                await FlowTask.Never();
            }
            catch (FlowCanceledException)
            {
                Log.Add("swallowed");
                return;
            }
        }

        async FlowTask Root()
        {
            if (!callerCatches)
            {
                await Swallower();
                return;
            }

            try
            {
                await Swallower();
            }
            catch (IOException e)
            {
                Log.Add("root caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        Tick(3);
        var swallow = ExceptionsOf(FlowExceptionKind.SwallowedCancellation);
        Assert.That(swallow, Has.Length.EqualTo(1), DescribeExceptions());
        Assert.That(swallow[0].ScopePath, Is.EqualTo("Root > Swallower"), DescribeExceptions());
        if (callerCatches)
        {
            AssertLog("swallowed", "root caught 503");
            Assert.That(Exceptions, Has.Count.EqualTo(1), DescribeExceptions());
            AssertSucceeded(h);
        }
        else
        {
            AssertLog("swallowed");
            Assert.That(Exceptions, Has.Count.EqualTo(2), DescribeExceptions());
            var report = ExceptionsOf(FlowExceptionKind.Unhandled).Single();
            Assert.That(report.Exception, Is.TypeOf<IOException>().And.Message.EqualTo("503"), DescribeExceptions());
            Assert.That(report.ScopePath, Is.EqualTo("Root > Swallower > Bomb"), DescribeExceptions());
            AssertFaulted(h, report.Exception);
        }
    }

    // ------------------------------------------------------------------ each catch form in each cancellation context

    public enum CatchForm
    {
        /// <summary><c>catch (OperationCanceledException) { return; }</c></summary>
        F01_OceReturn,
        /// <summary><c>catch (OperationCanceledException) { return -1; }</c>, awaited by a consumer of the value.</summary>
        F02_OceReturnValue,
        /// <summary><c>catch (FlowCanceledException) { return; }</c></summary>
        F03_FceReturn,
        /// <summary><c>catch (Exception) { log; return; }</c></summary>
        F04_CatchAllReturn,
        /// <summary><c>catch (Exception e) when (e is not OperationCanceledException)</c></summary>
        F05_FilterNotOce,
        /// <summary><c>catch (Exception e) when (e is not FlowCanceledException)</c> (the recommended catch-all)</summary>
        F05b_FilterNotFce,
        /// <summary><c>catch (OperationCanceledException) { cleanup; throw; }</c></summary>
        F06_OceCleanupRethrow,
        /// <summary><c>catch (OperationCanceledException) { await cleanup; throw; }</c>: the await runs to its end.</summary>
        F06b_OceAwaitThenRethrow,
        /// <summary><c>catch (OperationCanceledException e) { throw new OperationCanceledException("wrapped", e); }</c></summary>
        F07_OceWrap,
        /// <summary><c>catch (Exception e) when (Observe(e))</c>, a filter with a side effect that returns false.</summary>
        F08_ObservingFilter,
        /// <summary><c>catch (Exception) { }</c> and then the next await.</summary>
        F09_CatchAllThenAwait,
        /// <summary><c>catch (Exception) { }</c> and then synchronous code to the end.</summary>
        F10_CatchAllThenEnd,
        /// <summary><c>catch (FlowCanceledException) { }</c> and then synchronous code to the end.</summary>
        F10b_FceThenEnd,
        /// <summary><c>catch (OperationCanceledException e) when (e is not FlowCanceledException) { return; }</c></summary>
        F11_ExternalOceReturn,
    }

    public enum CancelContext
    {
        /// <summary>The root flow's handle is canceled.</summary>
        RootCanceled,
        /// <summary>A sibling the owner spawned fails; the owner and the probe it awaits are canceled, the caller catches.</summary>
        SpawnedSiblingFails,
        /// <summary>The probe loses a Race.</summary>
        RaceLoser,
        /// <summary>The probe's WhenAll sibling fails.</summary>
        WhenAllSibling,
        /// <summary>The root is canceled and the probe's own finally awaits (to its end) before the catch forms see the cancellation.</summary>
        FinallyAwaitsFirst,
        /// <summary>World.Dispose ends the flows: nothing is reported, and an await after the catch throws again.</summary>
        WorldDisposed,
        /// <summary>The awaited Task is canceled outside (an exception, not the flow's cancellation).</summary>
        ExternalCancellation,
    }

    TaskCompletionSource<bool> _external;

    FlowTask Wait(CancelContext c) =>
        c == CancelContext.ExternalCancellation ? FlowBridge.FromTask(_ => (Task)_external.Task) : FlowTask.WaitForSeconds(100);

    bool Observe(Exception e)
    {
        Log.Add("filter " + e.GetType().Name);
        return false;
    }

    async FlowTask Probe(CatchForm form, CancelContext c)
    {
        if (form == CatchForm.F02_OceReturnValue)
        {
            Log.Add("value " + await ProbeValue(c));
            return;
        }

        try
        {
            if (c == CancelContext.FinallyAwaitsFirst)
            {
                try
                {
                    await Wait(c);
                }
                finally
                {
                    Log.Add("fin");
                    await FlowTask.NextFrame();
                    Log.Add("fin end");
                }
            }
            else
            {
                await Wait(c);
            }

            Log.Add("try end");
        }
        catch (OperationCanceledException) when (form == CatchForm.F01_OceReturn)
        {
            Log.Add("catch");
            return;
        }
        catch (FlowCanceledException) when (form == CatchForm.F03_FceReturn)
        {
            Log.Add("catch");
            return;
        }
        catch (Exception) when (form == CatchForm.F04_CatchAllReturn)
        {
            Log.Add("catch");
            return;
        }
        catch (Exception e) when (form == CatchForm.F05_FilterNotOce && e is not OperationCanceledException)
        {
            Log.Add("catch " + e.GetType().Name);
        }
        catch (Exception e) when (form == CatchForm.F05b_FilterNotFce && e is not FlowCanceledException)
        {
            Log.Add("catch " + e.GetType().Name);
        }
        catch (OperationCanceledException) when (form == CatchForm.F06_OceCleanupRethrow)
        {
            Log.Add("cleanup");
            throw;
        }
        catch (OperationCanceledException) when (form == CatchForm.F06b_OceAwaitThenRethrow)
        {
            Log.Add("cleanup");
            await FlowTask.NextFrame();
            Log.Add("cleanup end");
            throw;
        }
        catch (OperationCanceledException e) when (form == CatchForm.F07_OceWrap)
        {
            Log.Add("catch");
            throw new OperationCanceledException("wrapped", e);
        }
        catch (Exception e) when (form == CatchForm.F08_ObservingFilter && Observe(e))
        {
            Log.Add("catch");
        }
        catch (Exception) when (form is CatchForm.F09_CatchAllThenAwait or CatchForm.F10_CatchAllThenEnd)
        {
            Log.Add("catch");
        }
        catch (FlowCanceledException) when (form == CatchForm.F10b_FceThenEnd)
        {
            Log.Add("catch");
        }
        catch (OperationCanceledException e) when (form == CatchForm.F11_ExternalOceReturn && e is not FlowCanceledException)
        {
            Log.Add("catch " + e.GetType().Name);
            return;
        }

        Log.Add("after");
        if (form == CatchForm.F09_CatchAllThenAwait)
        {
            await FlowTask.NextFrame();
            Log.Add("after await");
        }
    }

    async FlowTask<int> ProbeValue(CancelContext c)
    {
        try
        {
            if (c == CancelContext.FinallyAwaitsFirst)
            {
                try
                {
                    await Wait(c);
                }
                finally
                {
                    Log.Add("fin");
                    await FlowTask.NextFrame();
                    Log.Add("fin end");
                }
            }
            else
            {
                await Wait(c);
            }

            return 1;
        }
        catch (OperationCanceledException)
        {
            Log.Add("catch");
            return -1;
        }
    }

    async FlowTask Bomb()
    {
        await FlowTask.NextFrame();
        throw new InvalidOperationException("bomb");
    }

    async FlowTask Host(CatchForm form, CancelContext c)
    {
        switch (c)
        {
            case CancelContext.SpawnedSiblingFails:
                try
                {
                    await Owner(form, c);
                }
                catch (InvalidOperationException e)
                {
                    Log.Add("host caught " + e.Message);
                }

                break;
            case CancelContext.RaceLoser:
                Log.Add("race index " + (await FlowTask.Race(Probe(form, c), FlowTask.NextFrame())).Index);
                break;
            case CancelContext.WhenAllSibling:
                try
                {
                    await FlowTask.WhenAll(Probe(form, c), Bomb());
                    Log.Add("whenall ok");
                }
                catch (InvalidOperationException e)
                {
                    Log.Add("host caught " + e.Message);
                }

                break;
            default:
                await Probe(form, c);
                Log.Add("host continued");
                break;
        }
    }

    async FlowTask Owner(CatchForm form, CancelContext c)
    {
        Flow.Spawn(Bomb());
        await Probe(form, c);
        Log.Add("owner continued");
    }

    /// <summary>The forms that take the flow's cancellation and return or go on.</summary>
    static bool Swallows(CatchForm f) =>
        f is CatchForm.F01_OceReturn or CatchForm.F02_OceReturnValue or CatchForm.F03_FceReturn or CatchForm.F04_CatchAllReturn or
            CatchForm.F09_CatchAllThenAwait or CatchForm.F10_CatchAllThenEnd or CatchForm.F10b_FceThenEnd;

    /// <summary>The forms that let an external cancellation (TaskCanceledException) out of the probe.</summary>
    static bool LetsTheExternalCancellationOut(CatchForm f) =>
        f is CatchForm.F03_FceReturn or CatchForm.F05_FilterNotOce or CatchForm.F06_OceCleanupRethrow or CatchForm.F06b_OceAwaitThenRethrow or
            CatchForm.F07_OceWrap or CatchForm.F08_ObservingFilter or CatchForm.F10b_FceThenEnd;

    /// <summary>What the probe logs when <paramref name="e"/> reaches its catch clauses; at Dispose an await throws again.</summary>
    static List<string> ProbeLog(CatchForm f, string e, bool disposing)
    {
        var log = new List<string>();
        var isFlowCancellation = e == nameof(FlowCanceledException);
        switch (f)
        {
            case CatchForm.F01_OceReturn:
            case CatchForm.F04_CatchAllReturn:
            case CatchForm.F07_OceWrap:
                log.Add("catch");
                break;
            case CatchForm.F02_OceReturnValue:
                log.Add("catch");
                if (!isFlowCancellation) log.Add("value -1");
                break;
            case CatchForm.F03_FceReturn:
                if (isFlowCancellation) log.Add("catch");
                break;
            case CatchForm.F10b_FceThenEnd:
                if (isFlowCancellation) log.AddRange(new[] { "catch", "after" });
                break;
            case CatchForm.F05_FilterNotOce:
                break;
            case CatchForm.F05b_FilterNotFce:
                if (!isFlowCancellation) log.AddRange(new[] { "catch " + e, "after" });
                break;
            case CatchForm.F11_ExternalOceReturn:
                if (!isFlowCancellation) log.Add("catch " + e);
                break;
            case CatchForm.F06_OceCleanupRethrow:
                log.Add("cleanup");
                break;
            case CatchForm.F06b_OceAwaitThenRethrow:
                log.Add("cleanup");
                if (!disposing) log.Add("cleanup end");
                break;
            case CatchForm.F08_ObservingFilter:
                log.Add("filter " + e);
                break;
            case CatchForm.F09_CatchAllThenAwait:
                log.AddRange(new[] { "catch", "after" });
                if (!disposing) log.Add("after await");
                break;
            default:
                log.AddRange(new[] { "catch", "after" });
                break;
        }

        return log;
    }

    static IEnumerable<TestCaseData> FormsAndContexts()
    {
        foreach (CatchForm f in Enum.GetValues(typeof(CatchForm)))
        {
            foreach (CancelContext c in Enum.GetValues(typeof(CancelContext)))
            {
                yield return new TestCaseData(f, c);
            }
        }
    }

    [TestCaseSource(nameof(FormsAndContexts))]
    public void EachCatchFormInEachCancellationContext(CatchForm form, CancelContext context)
    {
        // The flow's cancellation reaches the probe's catch clauses in six contexts. A form that takes it and returns or
        // goes on runs its body (an await after it runs to its end), is reported once as SwallowedCancellation when the
        // probe returns, ends Canceled, and the combinator or caller around it goes on as if the probe had let the
        // cancellation pass. A form that lets it pass (a filter, a rethrow, a wrap in OperationCanceledException) is not
        // reported, and an await in its catch runs to its end. While World.Dispose ends the flows nothing is reported and an
        // await after the catch throws again. An external cancellation (a Task canceled outside) is an exception: a catch that
        // takes it goes on normally, one that does not lets it end the root flow Faulted.
        CaptureExceptions();
        _external = new TaskCompletionSource<bool>();
        var h = World.Run(Host(form, context));
        Tick();
        switch (context)
        {
            case CancelContext.RootCanceled:
            case CancelContext.FinallyAwaitsFirst:
                h.Cancel();
                break;
            case CancelContext.WorldDisposed:
                World.Dispose();
                break;
            case CancelContext.ExternalCancellation:
                _external.SetCanceled();
                break;
        }

        if (!World.IsDisposed) Tick(5);

        var disposing = context == CancelContext.WorldDisposed;
        var external = context == CancelContext.ExternalCancellation;
        var expected = context == CancelContext.FinallyAwaitsFirst ? new List<string> { "fin", "fin end" } : new List<string>();
        expected.AddRange(ProbeLog(form, external ? nameof(TaskCanceledException) : nameof(FlowCanceledException), disposing));
        var probeThrows = external && LetsTheExternalCancellationOut(form);
        switch (context)
        {
            case CancelContext.SpawnedSiblingFails:
            case CancelContext.WhenAllSibling:
                expected.Add("host caught bomb");
                break;
            case CancelContext.RaceLoser:
                expected.Add("race index 1");
                break;
            case CancelContext.ExternalCancellation:
                if (!probeThrows) expected.Add("host continued");
                break;
        }

        AssertLog(expected.ToArray());
        if (probeThrows)
        {
            Assert.That(Exceptions, Has.Count.EqualTo(1), DescribeExceptions() + " | log: " + Log);
            var report = Exceptions[0];
            Assert.That(report.Kind, Is.EqualTo(FlowExceptionKind.Unhandled), DescribeExceptions());
            Assert.That(report.Exception, form == CatchForm.F07_OceWrap
                ? Is.TypeOf<OperationCanceledException>().And.Message.EqualTo("wrapped")
                : Is.TypeOf<TaskCanceledException>(), DescribeExceptions());
            AssertFaulted(h, report.Exception);
            return;
        }

        if (!external && !disposing && Swallows(form))
        {
            var report = AssertSingleException<FlowMisuseException>(FlowExceptionKind.SwallowedCancellation, null);
            Assert.That(report.ScopePath, Does.EndWith(form == CatchForm.F02_OceReturnValue ? "Probe > ProbeValue" : "Probe"), DescribeExceptions());
        }
        else
        {
            AssertNoExceptions();
        }

        switch (context)
        {
            case CancelContext.RootCanceled:
            case CancelContext.FinallyAwaitsFirst:
                AssertCanceled(h, CancelCause.Explicit);
                break;
            case CancelContext.WorldDisposed:
                AssertCanceled(h, CancelCause.WorldDisposed);
                break;
            default:
                AssertSucceeded(h);
                break;
        }
    }
}
