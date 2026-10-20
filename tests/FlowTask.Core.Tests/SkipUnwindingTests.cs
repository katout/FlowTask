using System.Reflection;
using Katout.FlowTask.Testing;

namespace Katout.FlowTask.Tests;

/// <summary>
/// FlowWorld.SkipUnwinding: canceled, the async methods of a type passed to it end at their await without throwing
/// FlowCanceledException. Each scenario runs in two Worlds, one that skips the unwinding of <see cref="SkipFlows"/> and
/// one that does not: both end the same way (the status, cause and exception of each flow, the order of the cleanups and
/// the reports), and only the second throws FlowCanceledException. FlowAssert.CanSkipUnwinding rejects a type that has a
/// block around an await that unwinding would run.
/// </summary>
public class SkipUnwindingTests
{
    sealed class Outcome
    {
        public readonly List<string> Log = new();
        public int CanceledThrows;
    }

    /// <summary>Runs <paramref name="scenario"/> in a new World, with <see cref="SkipFlows"/> passed to SkipUnwinding or not.</summary>
    static Outcome RunScenario(bool skip, Action<FlowWorld, List<string>> scenario)
    {
        var outcome = new Outcome();
        var thread = Environment.CurrentManagedThreadId;
        void Count(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is FlowCanceledException && Environment.CurrentManagedThreadId == thread) outcome.CanceledThrows++;
        }

        using var world = new FlowWorld("W");
        world.OnUnhandledException = p => outcome.Log.Add($"report {p.Kind} {p.Exception.GetType().Name} '{p.Exception.Message}' at {p.ScopePath}");
        world.OnWarning += w => outcome.Log.Add("warning " + w.Kind);
        if (skip) world.SkipUnwinding(typeof(SkipFlows));
        SkipFlows.Log = outcome.Log;
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            scenario(world, outcome.Log);
            if (!world.IsDisposed) world.Dispose();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
            SkipFlows.Log = null;
        }

        return outcome;
    }

    /// <summary>
    /// Both Worlds end the scenario the same way, which is <paramref name="expected"/>; only the one that unwinds throws
    /// FlowCanceledException (where the runtime reports first-chance exceptions at all).
    /// </summary>
    static void AssertSameEnd(Action<FlowWorld, List<string>> scenario, params string[] expected) => AssertSameEnd(scenario, 0, expected);

    /// <inheritdoc cref="AssertSameEnd(Action{FlowWorld, List{string}}, string[])"/>
    /// <param name="otherThrows">The FlowCanceledExceptions of flows of other types, which unwind in both Worlds.</param>
    static void AssertSameEnd(Action<FlowWorld, List<string>> scenario, int otherThrows, params string[] expected)
    {
        var unwound = RunScenario(false, scenario);
        var skipped = RunScenario(true, scenario);
        Assert.That(unwound.Log, Is.EqualTo(expected), "unwound: " + string.Join(" | ", unwound.Log));
        Assert.That(skipped.Log, Is.EqualTo(unwound.Log), "skipped: " + string.Join(" | ", skipped.Log));
        if (unwound.CanceledThrows == 0) return;
        Assert.That(unwound.CanceledThrows, Is.GreaterThan(otherThrows));
        Assert.That(skipped.CanceledThrows, Is.EqualTo(otherThrows), $"FlowCanceledException thrown without unwinding (unwound {unwound.CanceledThrows})");
    }

    static void Tick(FlowWorld w, int count)
    {
        for (var i = 0; i < count; i++) w.Tick(1.0 / 60);
    }

    static string Describe(string name, FlowHandle h) => $"{name}: {h.Status} {h.CancelCause} {h.Exception?.Message}";

    // ------------------------------------------------------------------ the four ways a method is canceled

    [Test]
    public void TheLosingBranchOfARaceEndsAsItWouldUnwinding()
    {
        // The losing branch waits in a child method, a parked wait and a spawned child: children end first, later-started
        // first, then each scope's cleanup, then the Race resumes.
        AssertSameEnd((w, log) =>
            {
                var h = w.Run(SkipFlows.RaceRoot());
                Tick(w, 4);
                log.Add(Describe("race", h));
            },
            "loser start", "loser.child start", "race won", "loser.child cleanup", "loser.spawned cleanup", "loser cleanup", "race 1",
            "race: Succeeded None ");
    }

    [Test]
    public void ANonCancelableAwaitIsWaitedForAndTheCodeAfterItRunsToTheNextAwait()
    {
        AssertSameEnd((w, log) =>
            {
                var h = w.Run(SkipFlows.Guarded());
                Tick(w, 1);
                h.Cancel();
                Tick(w, 5);
                log.Add(Describe("guarded", h));
            },
            "save start", "save end", "after save", "guarded cleanup", "guarded: Canceled Explicit ");
    }

    [Test]
    public void ANonCancelableAwaitThatFailsInACanceledScopeIsReportedAsWhenUnwinding()
    {
        AssertSameEnd((w, log) =>
            {
                var h = w.Run(SkipFlows.GuardedFails());
                Tick(w, 1);
                h.Cancel();
                Tick(w, 5);
                log.Add(Describe("guarded", h));
            },
            "report Undelivered InvalidOperationException 'save failed' at GuardedFails > FailsAfter", "guarded cleanup",
            "guarded: Canceled Explicit ");
    }

    [Test]
    public void ANonCancelableAwaitHeldByWorldDisposeIsCutAsWhenUnwinding()
    {
        AssertSameEnd((w, log) =>
            {
                var h = w.Run(SkipFlows.Guarded());
                Tick(w, 1);
                w.Dispose();
                log.Add(Describe("guarded", h));
            },
            "save start", "warning CleanupCutAtDispose", "guarded cleanup", "guarded: Canceled WorldDisposed ");
    }

    [Test]
    public void AScopeCanceledByTheFailureOfAChildItSpawnedEndsWithThatFailure()
    {
        // The scope waits on its own clock (a parked wait) and in a child method: it carries the child's exception and
        // ends Faulted with it, its other children canceled first.
        AssertSameEnd((w, log) =>
            {
                var parked = w.Run(SkipFlows.Carrier(parked: true));
                var child = w.Run(SkipFlows.Carrier(parked: false));
                Tick(w, 4);
                log.Add(Describe("parked", parked));
                log.Add(Describe("child", child));
            },
            "carrier.child start",
            "sibling cleanup", "carrier cleanup", "report Unhandled InvalidOperationException 'boom' at Carrier > FailsAfter",
            "carrier.child cleanup", "sibling cleanup", "carrier cleanup", "report Unhandled InvalidOperationException 'boom' at Carrier > FailsAfter",
            "parked: Faulted Fault boom", "child: Faulted Fault boom");
    }

    [Test]
    public void WorldDisposeEndsTheMethodsAsItWouldUnwindingThem()
    {
        AssertSameEnd((w, log) =>
            {
                var a = w.Run(SkipFlows.Tree("a"));
                var b = w.Run(SkipFlows.Tree("b"));
                Tick(w, 1);
                w.Dispose();
                log.Add(Describe("a", a));
                log.Add(Describe("b", b));
            },
            "a.child start", "b.child start",
            "b.child cleanup", "b.spawned cleanup", "b cleanup", "a.child cleanup", "a.spawned cleanup", "a cleanup",
            "a: Canceled WorldDisposed ", "b: Canceled WorldDisposed ");
    }

    [Test]
    public void ACatchFilteredToLetTheCancellationPassEndsAsItWouldUnwinding()
    {
        // The catch-all FLOW001 asks for, around an await: its filter lets FlowCanceledException pass, so unwinding does
        // not run the catch either.
        AssertSameEnd((w, log) =>
            {
                var h = w.Run(SkipFlows.Filtered());
                Tick(w, 1);
                h.Cancel();
                Tick(w, 2);
                log.Add(Describe("filtered", h));
            },
            "filtered start", "filtered cleanup", "filtered: Canceled Explicit ");
    }

    // ------------------------------------------------------------------ a scope canceled while it runs

    [Test]
    public void AScopeCanceledWhileItRunsEndsAtItsNextAwait()
    {
        AssertSameEnd((w, log) =>
            {
                var self = new FlowHandle[1];
                self[0] = w.Run(SkipFlows.CancelsItself(self, withChild: false));
                Tick(w, 3);
                log.Add(Describe("self", self[0]));
            },
            "after cancel", "self cleanup", "self: Canceled Explicit ");
    }

    [Test]
    public void AScopeCanceledWhileItRunsWaitsForAChildThatRunsItsCleanup()
    {
        // The child is of a type that unwinds: its await throws, and its finally block rethrows at its end.
        AssertSameEnd((w, log) =>
            {
                var self = new FlowHandle[1];
                self[0] = w.Run(SkipFlows.CancelsItself(self, withChild: true));
                Tick(w, 6);
                log.Add(Describe("self", self[0]));
            },
            2,
            "child save start", "after cancel", "child save end", "self cleanup", "self: Canceled Explicit ");
    }

    [Test]
    public void AGenericTypeStandsForAllItsConstructions()
    {
        using var w = new FlowWorld();
        w.SkipUnwinding(typeof(GenericFlows<int>));
        var throws = 0;
        var thread = Environment.CurrentManagedThreadId;
        void Count(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is FlowCanceledException && Environment.CurrentManagedThreadId == thread) throws++;
        }

        var unwound = new FlowWorld();
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            var h = unwound.Run(GenericFlows<string>.Wait());
            unwound.Tick(0.1);
            h.Cancel();
            unwound.Flush();
            var calibrated = throws > 0;
            throws = 0;
            var a = w.Run(GenericFlows<string>.Wait());
            var b = w.Run(GenericFlows<int>.WaitValue());
            w.Tick(0.1);
            a.Cancel();
            b.Cancel();
            w.Flush();
            Assert.That(a.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(b.Status, Is.EqualTo(FlowStatus.Canceled));
            if (calibrated) Assert.That(throws, Is.Zero);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
            unwound.Dispose();
        }
    }

    // ------------------------------------------------------------------ the check

    /// <summary>Whether this runtime gives method bodies (JIT and Mono do; NativeAOT and IL2CPP do not): the check reads them.</summary>
    static bool CanReadMethodBodies()
    {
        var sm = typeof(FinallyAroundAwait).GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault(t => typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(t));
        if (sm == null) return false; // stripped (IL2CPP, NativeAOT)
        try
        {
            return sm.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetMethodBody() != null;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    static void AssertRejected(Type type, string method)
    {
        if (!CanReadMethodBodies())
        {
            Assert.Throws<NotSupportedException>(() => FlowAssert.CanSkipUnwinding(type), "method bodies cannot be read here");
            return;
        }

        var e = Assert.Throws<FlowAssertionException>(() => FlowAssert.CanSkipUnwinding(type));
        Assert.That(e.Message, Does.Contain("'" + method + "'").And.Contain(type.Name));
    }

    static void AssertAccepted(Type type)
    {
        if (!CanReadMethodBodies())
        {
            Assert.Throws<NotSupportedException>(() => FlowAssert.CanSkipUnwinding(type), "method bodies cannot be read here");
            return;
        }

        Assert.DoesNotThrow(() => FlowAssert.CanSkipUnwinding(type));
    }

    [Test]
    public void AFinallyBlockAroundAnAwaitIsRejected()
    {
        AssertRejected(typeof(FinallyAroundAwait), "Run");
        AssertRejected(typeof(UnwoundFlows), "SavesInFinally");
    }

    [Test]
    public void AUsingBlockAroundAnAwaitIsRejected() => AssertRejected(typeof(UsingAroundAwait), "Run");

    [Test]
    public void AForeachThatAwaitsIsRejected() => AssertRejected(typeof(ForeachAroundAwait), "Run");

    [Test]
    public void ACatchThatTakesTheCancellationAroundAnAwaitIsRejected()
    {
        AssertRejected(typeof(CatchAllAroundAwait), "Run");
        AssertRejected(typeof(CatchCancellationAroundAwait), "Run");
        // catch (TEx) takes FlowCanceledException when TEx is Exception: the check cannot tell, so it rejects.
        AssertRejected(typeof(CatchTypeParameterAroundAwait), "Run");
    }

    [Test]
    public void AFilterThatLetsTheCancellationPassIsAccepted()
    {
        // when (e is not FlowCanceledException), as FLOW001 asks, and the forms that come to the same for it.
        AssertAccepted(typeof(FilterExcludingCancellation));
        AssertAccepted(typeof(FilterExcludingOperationCanceled));
        AssertAccepted(typeof(FilterExcludingCancellationFirst));
    }

    [Test]
    public void AFilterThatMayTakeTheCancellationIsRejected()
    {
        // A filter on another condition, or one that reads more than the exception's type first: the check cannot tell
        // that it lets FlowCanceledException pass, so it rejects. A filter that takes it is rejected too.
        AssertRejected(typeof(FilterOnAnotherCondition), "Run");
        AssertRejected(typeof(FilterCallingFirst), "Run");
        AssertRejected(typeof(FilterTakingCancellation), "Run");
    }

    [Test]
    public void TheMessageNamesWhatTheSourceHas()
    {
        // A lambda is named by the method it is written in; one in a field initializer by that.
        AssertRejected(typeof(LambdaWithFinally), "Make");
        if (CanReadMethodBodies())
        {
            var e = Assert.Throws<FlowAssertionException>(() => FlowAssert.CanSkipUnwinding(typeof(FieldInitializerWithFinally)));
            Assert.That(e.Message, Does.Contain("field initializer").And.Not.Contain("cctor"));
            e = Assert.Throws<FlowAssertionException>(() => FlowAssert.CanSkipUnwinding(typeof(LambdaWithFinally)));
            Assert.That(e.Message, Does.Contain("lambda in 'Make'"));
        }
        else
        {
            // Where method bodies cannot be read, the message names the type passed, not the check's own probe.
            var e = Assert.Throws<NotSupportedException>(() => FlowAssert.CanSkipUnwinding(typeof(LambdaWithFinally)));
            Assert.That(e.Message, Does.Contain(nameof(LambdaWithFinally)).And.Not.Contain("Probe"));
        }
    }

    [Test]
    public void AFinallyBlockInALambdaOrALocalFunctionIsRejected()
    {
        AssertRejected(typeof(LambdaWithFinally), "Make");
        AssertRejected(typeof(LocalFunctionWithFinally), "Inner");
    }

    [Test]
    public void BlocksThatUnwindingWouldNotRunAreAccepted()
    {
        // A catch of another exception, and blocks with no await inside: a cancellation happens only at an await.
        AssertAccepted(typeof(CatchOtherAroundAwait));
        AssertAccepted(typeof(BlocksWithoutAwait));
        AssertAccepted(typeof(SkipFlows));
        AssertAccepted(typeof(GenericFlows<int>));
    }

    [Test]
    public void TheBlocksOfTaskMethodsAreAccepted()
    {
        // Only a FlowTask method ends without unwinding: a Task or async void method in the type unwinds.
        AssertAccepted(typeof(TaskMethodsWithBlocks));
    }

    [Test]
    public void SkipUnwindingTakesAType()
    {
        using var w = new FlowWorld();
        Assert.Throws<ArgumentNullException>(() => w.SkipUnwinding(null));
        w.SkipUnwinding(typeof(SkipFlows));
        Assert.DoesNotThrow(() => w.SkipUnwinding(typeof(SkipFlows)), "passing a type again changes nothing");
    }

    static int s_counter;

    sealed class Resource : IDisposable
    {
        public void Dispose() => s_counter++;
    }

    static class FinallyAroundAwait
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                s_counter++;
            }
        }
    }

    static class UsingAroundAwait
    {
        internal static async FlowTask Run()
        {
            using (new Resource()) await FlowTask.NextFrame();
        }
    }

    static class ForeachAroundAwait
    {
        internal static async FlowTask Run(List<int> frames)
        {
            foreach (var f in frames) await FlowTask.DelayFrames(f);
        }
    }

    static class CatchAllAroundAwait
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (Exception)
            {
                s_counter++;
                throw;
            }
        }
    }

    static class CatchCancellationAroundAwait
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (OperationCanceledException)
            {
                s_counter++;
                throw;
            }
        }
    }

    static class CatchTypeParameterAroundAwait
    {
        internal static async FlowTask Run<TEx>()
            where TEx : Exception
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (TEx)
            {
                s_counter++;
                throw;
            }
        }
    }

    static class LambdaWithFinally
    {
        internal static Func<FlowTask> Make() => async () =>
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                s_counter++;
            }
        };
    }

    static class LocalFunctionWithFinally
    {
        internal static FlowTask Outer()
        {
            return Inner();

            static async FlowTask Inner()
            {
                try
                {
                    await FlowTask.NextFrame();
                }
                finally
                {
                    s_counter++;
                }
            }
        }
    }

    static readonly bool s_flag = Environment.ProcessorCount > 64;

    static bool Seen(Exception e) => e != null;

    static class FilterExcludingCancellation
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                s_counter += e.Message.Length;
            }
        }
    }

    static class FilterExcludingOperationCanceled
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                s_counter++;
            }
        }
    }

    static class FilterExcludingCancellationFirst
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (Exception e) when (e is not FlowCanceledException && Seen(e) && s_flag)
            {
                s_counter++;
            }
        }
    }

    static class FilterOnAnotherCondition
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (Exception) when (s_flag)
            {
                s_counter++;
                throw;
            }
        }
    }

    static class FilterCallingFirst
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (Exception e) when (Seen(e) && e is not FlowCanceledException)
            {
                s_counter++;
                throw;
            }
        }
    }

    static class FilterTakingCancellation
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (Exception e) when (e is FlowCanceledException)
            {
                s_counter++;
                throw;
            }
        }
    }

    static class FieldInitializerWithFinally
    {
        internal static readonly Func<FlowTask> Make = async () =>
        {
            try
            {
                await FlowTask.NextFrame();
            }
            finally
            {
                s_counter++;
            }
        };
    }

    static class TaskMethodsWithBlocks
    {
        internal static async System.Threading.Tasks.Task Run()
        {
            try
            {
                await System.Threading.Tasks.Task.Yield();
            }
            finally
            {
                s_counter++;
            }
        }

        internal static async void Fire()
        {
            using var resource = new Resource();
            await System.Threading.Tasks.Task.Yield();
        }
    }

    static class CatchOtherAroundAwait
    {
        internal static async FlowTask Run()
        {
            try
            {
                await FlowTask.NextFrame();
            }
            catch (InvalidOperationException)
            {
                s_counter++;
            }
        }
    }

    static class BlocksWithoutAwait
    {
        internal static async FlowTask Run(List<int> values)
        {
            try
            {
                s_counter++;
            }
            finally
            {
                s_counter--;
            }

            foreach (var v in values) s_counter += v;
            using (new Resource()) s_counter++;
            await FlowTask.NextFrame();
            s_counter++;
        }
    }
}

/// <summary>The flows of <see cref="SkipUnwindingTests"/>: no block around an await that unwinding would run.</summary>
internal static class SkipFlows
{
    internal static List<string> Log;

    static void L(string entry) => Log.Add(entry);

    static async FlowTask Child(string name)
    {
        Flow.AddCleanup(() => L(name + " cleanup"));
        L(name + " start");
        await FlowTask.Never();
        L(name + " never");
    }

    static async FlowTask Parked(string name)
    {
        Flow.AddCleanup(() => L(name + " cleanup"));
        await FlowTask.DelayFrames(1000);
        L(name + " never");
    }

    /// <summary>A scope that waits in a child method, with a spawned child that waits on its clock.</summary>
    internal static async FlowTask Tree(string name)
    {
        Flow.AddCleanup(() => L(name + " cleanup"));
        Flow.Spawn(Parked(name + ".spawned"));
        await Child(name + ".child");
        L(name + " never");
    }

    static async FlowTask Loser()
    {
        L("loser start");
        await Tree("loser");
    }

    static async FlowTask Winner()
    {
        await FlowTask.DelayFrames(2);
        L("race won");
    }

    internal static async FlowTask RaceRoot()
    {
        var r = await FlowTask.Race(Loser(), Winner());
        L("race " + r.Index);
    }

    static async FlowTask Step(string name, int frames)
    {
        L(name + " start");
        await FlowTask.DelayFrames(frames);
        L(name + " end");
    }

    internal static async FlowTask Guarded()
    {
        Flow.AddCleanup(() => L("guarded cleanup"));
        await Flow.NonCancelable(Step("save", 3));
        L("after save");
        await FlowTask.NextFrame();
        L("never");
    }

    static async FlowTask FailsAfter(int frames, string message)
    {
        await FlowTask.DelayFrames(frames);
        throw new InvalidOperationException(message);
    }

    internal static async FlowTask GuardedFails()
    {
        Flow.AddCleanup(() => L("guarded cleanup"));
        await Flow.NonCancelable(FailsAfter(3, "save failed"));
        L("never");
    }

    internal static async FlowTask Carrier(bool parked)
    {
        Flow.AddCleanup(() => L("carrier cleanup"));
        Flow.Spawn(Parked("sibling"));
        Flow.Spawn(FailsAfter(2, "boom"));
        if (parked) await FlowTask.DelayFrames(1000);
        else await Child("carrier.child");
        L("never");
    }

    /// <summary>The catch-all FLOW001 asks for, around an await: its filter lets FlowCanceledException pass.</summary>
    internal static async FlowTask Filtered()
    {
        Flow.AddCleanup(() => L("filtered cleanup"));
        L("filtered start");
        try
        {
            await FlowTask.Never();
        }
        catch (Exception e) when (e is not FlowCanceledException)
        {
            L("filtered caught " + e.Message);
        }

        L("never");
    }

    internal static async FlowTask CancelsItself(FlowHandle[] self, bool withChild)
    {
        Flow.AddCleanup(() => L("self cleanup"));
        if (withChild) Flow.Spawn(UnwoundFlows.SavesInFinally());
        await FlowTask.NextFrame();
        self[0].Cancel();
        L("after cancel");
        await FlowTask.NextFrame();
        L("never");
    }
}

/// <summary>Flows that unwind: a child of <see cref="SkipFlows"/> that waits in its finally block.</summary>
internal static class UnwoundFlows
{
    internal static async FlowTask SavesInFinally()
    {
        try
        {
            await FlowTask.Never();
        }
        finally
        {
            SkipFlows.Log.Add("child save start");
            await FlowTask.DelayFrames(3);
            SkipFlows.Log.Add("child save end");
        }
    }
}

/// <summary>A generic type for <see cref="SkipUnwindingTests.AGenericTypeStandsForAllItsConstructions"/>.</summary>
internal static class GenericFlows<T>
{
    internal static async FlowTask Wait() => await FlowTask.Never();

    internal static async FlowTask<T> WaitValue()
    {
        await FlowTask.DelayFrames(1000);
        return default;
    }
}
