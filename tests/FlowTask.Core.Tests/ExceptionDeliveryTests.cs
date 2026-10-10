using System.IO;

namespace Katout.FlowTask.Tests;

/// <summary>
/// An exception that leaves a live scope ends it Faulted and is thrown again at the await of its awaiter, as with Task,
/// so a live scope's catch receives it; a Race or a WhenAll passes a branch's exception on once its other branches have
/// unwound. A failure goes up the await chain through the same FIFO resume queue as a value: one resumption per await.
/// A misuse the library finds is an exception of the scope where it happens.
/// </summary>
public class ExceptionDeliveryTests : FailurePathTestBase
{
    // ------------------------------------------------------------------ the direction: a live catch receives it

    [Test]
    public void AwaitedChildExceptionIsCaughtByTheAwaitingScope([Values(false, true)] bool synchronous)
    {
        // The catch of the awaiting scope receives the child's exception, whether the child threw after an await or
        // before its first await.
        CaptureExceptions();

        async FlowTask Child()
        {
            await FlowTask.WaitForSeconds(0.01);
            throw new InvalidOperationException("bug");
        }

        async FlowTask<int> Immediate() => throw new InvalidOperationException("bug");

        async FlowTask Parent()
        {
            try
            {
                if (synchronous) Log.Add("value " + await Immediate());
                else await Child();
                Log.Add("after await");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("caught " + e.Message);
            }
            finally
            {
                Log.Add("finally");
            }

            Log.Add("continues");
        }

        var h = World.Run(Parent());
        TickFor(0.1);
        AssertLog("caught bug", "finally", "continues");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    [Test]
    public void UncaughtExceptionEndsEveryFlowOnItsWayFaulted()
    {
        // A catch of another type does not take it; every finally on the way runs; the root flow ends Faulted
        // and OnUnhandledException receives the exception once, with the path where it was thrown.
        CaptureExceptions();

        async FlowTask Middle()
        {
            try
            {
                await Throws(1, "deep bug", 0.01);
            }
            catch (FormatException)
            {
                Log.Add("wrong catch");
            }
            finally
            {
                Log.Add("middle finally");
            }
        }

        async FlowTask Top()
        {
            try
            {
                await Middle();
            }
            finally
            {
                Log.Add("top finally");
            }
        }

        var h = World.Run(Top());
        TickFor(0.1);
        AssertLog("throw deep bug", "middle finally", "top finally");
        var report = AssertSingleException<InvalidOperationException>(FlowExceptionKind.Unhandled, "deep bug", PathOf("Top > Middle", "Throws", 1));
        AssertFaulted(h, report.Exception);
    }

    [Test]
    public void TheCaughtExceptionIsTheOneThrownWithTheStackTraceOfWhereItWasThrown()
    {
        // The exception goes up the await chain as the same instance, captured once where it
        // first left a flow (ExceptionDispatchInfo): its stack trace still shows the method that threw it.
        CaptureExceptions();
        Exception thrown = null;
        Exception caught = null;

        async FlowTask DeepThrower()
        {
            await FlowTask.NextFrame();
            thrown = new InvalidOperationException("deep");
            throw thrown;
        }

        async FlowTask Middle(int depth)
        {
            if (depth == 0) await DeepThrower();
            else await Middle(depth - 1);
        }

        async FlowTask Root()
        {
            try
            {
                await Middle(3);
            }
            catch (InvalidOperationException e)
            {
                caught = e;
            }
        }

        var h = World.Run(Root());
        Tick(3);
        Assert.That(caught, Is.SameAs(thrown));
        Assert.That(caught.StackTrace, Does.Contain(nameof(DeepThrower)));
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    // ------------------------------------------------------------------ order and combinators

    [Test]
    public void AFailureGoesUpItsAwaitChainOneResumptionAtATimeLikeAValue([Values(0, 1)] int depth, [Values] bool fails)
    {
        // The branch's wait and the timer are due in the same Tick, and the branch's resumed first. At depth 0 the
        // branch ends first and decides the Race, whether it returns or throws. One await deeper, its end takes one more
        // resumption, queued behind the timer's delivery: the timer wins either way (an exception does not overtake a
        // value), and the exception, whose receiver lost the Race, is Undelivered.
        CaptureExceptions();

        async FlowTask Branch(int d)
        {
            if (d > 0)
            {
                await Branch(d - 1);
                return;
            }

            await FlowTask.WaitForSeconds(1.0);
            Log.Add(fails ? "branch throws" : "branch returns");
            if (fails) throw new IOException("branch io");
        }

        async FlowTask Root()
        {
            try
            {
                var r = await FlowTask.Race(Branch(depth), FlowTask.WaitForSeconds(1.0));
                Log.Add("race index " + r.Index);
            }
            catch (IOException e)
            {
                Log.Add("root caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        TickFor(1.2);
        var first = fails ? "branch throws" : "branch returns";
        if (depth == 0) AssertLog(first, fails ? "root caught branch io" : "race index 0");
        else AssertLog(first, "race index 1");
        if (depth > 0 && fails) AssertSingleException<IOException>(FlowExceptionKind.Undelivered, "branch io", "Root > Branch > Branch");
        else AssertNoExceptions();
        AssertSucceeded(h);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AWaitsFailureDoesNotOvertakeAValueQueuedBeforeIt(bool fails)
    {
        // Both branches of the Race are waits on signals. In one flush a flow emits on the first, then emits on or
        // closes the second: the first branch's value is queued before the second branch ends. The first wins either
        // way, and the second's exception, whose receiver had settled, is Undelivered.
        CaptureExceptions();
        var a = new Signal<int>(World, "A");
        var b = new Signal<int>(World, "B");
        var go = new Signal<int>(World, "Go");

        async FlowTask Root()
        {
            try
            {
                var r = await FlowTask.Race(a.Next(), b.Next());
                Log.Add("race index " + r.Index);
            }
            catch (SignalClosedException e)
            {
                Log.Add("root caught " + e.InnerException?.Message);
            }
        }

        async FlowTask Driver()
        {
            await go.Next();
            a.Emit(1);
            if (fails) b.Close(new IOException("b io"));
            else b.Emit(2);
        }

        var h = World.Run(Root());
        World.Run(Driver());
        Tick();
        go.Emit(1);
        Tick();
        AssertLog("race index 0");
        if (fails)
        {
            var report = AssertSingleException<SignalClosedException>(FlowExceptionKind.Undelivered, null);
            Assert.That(report.Exception.InnerException?.Message, Is.EqualTo("b io"));
        }
        else
        {
            AssertNoExceptions();
        }

        AssertSucceeded(h);
    }

    [Test]
    public void ARaceOrWhenAllPassesABranchsExceptionOnOnceItsOtherBranchesHaveUnwound([Values] bool race, [Values] bool throwsAsItStarts)
    {
        // The second branch throws, as the combinator starts it or later. The combinator cancels the other branch
        // (CancelCause.Fault) and waits until its finally, which awaits, has run to its end; then its await throws the
        // exception.
        CaptureExceptions();

        string CauseOf(string name) => World.Diagnostics.Walk().First(s => s.Name == name).Cause.ToString();

        async FlowTask Other()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("other finally, " + CauseOf("Other"));
                await FlowTask.NextFrame();
                Log.Add("other finally end");
            }
        }

        async FlowTask Failing()
        {
            if (!throwsAsItStarts) await FlowTask.NextFrame();
            Log.Add("throw bug");
            throw new InvalidOperationException("bug");
        }

        async FlowTask Root()
        {
            try
            {
                if (race) await FlowTask.Race(Other(), Failing());
                else await FlowTask.WhenAll(Other(), Failing());
                Log.Add("unreachable");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("root caught " + e.Message);
            }
        }

        var h = World.Run(Root());
        Tick(3);
        AssertLog("throw bug", "other finally, Fault", "other finally end", "root caught bug");
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    public enum ConditionWaiter
    {
        /// <summary>The flow awaits the wait.</summary>
        Await,
        /// <summary>The wait is a branch of a Race.</summary>
        RaceBranch,
        /// <summary>A method the flow awaits spawns the wait (the method carries its exception to the flow).</summary>
        SpawnedChild,
        /// <summary>FlowProperty.WaitUntil, awaited by the flow.</summary>
        Property,
    }

    [Test]
    public void AWaitUntilConditionThatThrowsIsCaughtTheSameOnItsFirstAndALaterEvaluation([Values(1, 3)] int throwsOn, [Values] ConditionWaiter waiter)
    {
        // The condition of FlowTask.WaitUntil and FlowProperty.WaitUntil is evaluated when the
        // wait starts, then on every Tick (or every Set). An exception from the first evaluation fails the wait as one
        // from a later evaluation does: the flow's catch receives it at the await, through a Race or a spawning method
        // alike, and no wait is left behind in the tree.
        CaptureExceptions();
        var evaluations = 0;
        var property = new FlowProperty<int>(0);

        bool Condition()
        {
            if (++evaluations == throwsOn) throw new InvalidOperationException("condition");
            return false;
        }

        async FlowTask Owner()
        {
            Flow.Spawn(FlowTask.WaitUntil(Condition));
            await FlowTask.Never();
        }

        async FlowTask Setter()
        {
            for (var i = 1; i <= 3; i++)
            {
                await FlowTask.NextFrame();
                property.Set(i);
            }
        }

        async FlowTask Root()
        {
            try
            {
                switch (waiter)
                {
                    case ConditionWaiter.Await:
                        await FlowTask.WaitUntil(Condition);
                        break;
                    case ConditionWaiter.RaceBranch:
                        await FlowTask.Race(FlowTask.WaitUntil(Condition), FlowTask.Never());
                        break;
                    case ConditionWaiter.SpawnedChild:
                        await Owner();
                        break;
                    default:
                        await property.WaitUntil(_ => Condition());
                        break;
                }

                Log.Add("unreachable");
            }
            catch (InvalidOperationException e)
            {
                Log.Add("caught " + e.Message + " at evaluation " + evaluations);
            }

            // The flow goes on: nothing of the failed wait is left under it.
            Log.Add("left under the flow: " + string.Join(", ", World.Diagnostics.Walk().Where(s => s.Path.StartsWith("Root >", StringComparison.Ordinal)).Select(s => s.Name)));
            await FlowTask.NextFrame();
            Log.Add("flow goes on");
        }

        var h = World.Run(Root());
        World.Run(Setter());
        Tick(5);
        AssertLog("caught condition at evaluation " + throwsOn, "left under the flow: ", "flow goes on");
        AssertNoExceptions();
        AssertSucceeded(h);
        Assert.That(World.Diagnostics.Walk().Where(s => s.Name.Contains("WaitUntil")).Select(s => s.Path), Is.Empty);
        Assert.That(World.Diagnostics.Root.Children, Is.Empty);
    }

    // ------------------------------------------------------------------ misuses the library finds

    public enum Misuse
    {
        /// <summary>A FlowTask method suspends on an awaiter that is not the library's (FLOW002).</summary>
        UnbridgedAwait,
        /// <summary>An async Task method awaits a FlowTask that does not complete at once (FLOW005).</summary>
        AwaitInATaskMethod,
        /// <summary>A subscription with BufferOverflow.Fail overflows.</summary>
        SubscriptionOverflow,
        /// <summary>More than 1,000,000 awaits complete synchronously in one Tick.</summary>
        EndlessLoop,
        /// <summary>FlowCanceledException thrown by hand in a scope that was not canceled.</summary>
        CanceledExceptionThrownByHand,
    }

    [Test]
    public void AMisuseTheLibraryFindsIsAnExceptionOfTheScopeThatItsAwaiterCatches([Values] Misuse misuse)
    {
        // A misuse ends the scope where it happens with an exception, which the awaiting scope's catch receives like
        // any other. An unbridged await and an endless loop end the scope at that await (its finally does not run,
        // AddCleanup does); the others cancel it (CancelCause.Fault) carrying the exception, which it ends with once
        // unwound, or leave it as its own exception. The endless loop stops by itself at 2,000,000, so that a broken
        // limit fails the test instead of hanging it.
        CaptureExceptions();
        var values = new Signal<int>(World, "Values");
        var never = new TaskCompletionSource<int>();
        FlowCanceledException byHand = null;
        Exception caught = null;
        var iterations = 0;

        if (misuse == Misuse.CanceledExceptionThrownByHand)
        {
            async FlowTask Canceled()
            {
                try
                {
                    await FlowTask.Never();
                }
                catch (FlowCanceledException e)
                {
                    byHand = e;
                    throw;
                }
            }

            World.Run(Canceled()).Cancel();
            Tick();
        }

        async Task Helper() => await FlowTask.NextFrame();

        async FlowTask Source()
        {
            Flow.AddCleanup(() => Log.Add("cleanup"));
            try
            {
                await FlowTask.NextFrame();
                switch (misuse)
                {
                    case Misuse.UnbridgedAwait:
#pragma warning disable FLOW002 // on purpose: the misuse under test
                        await never.Task;
#pragma warning restore FLOW002
                        break;
                    case Misuse.AwaitInATaskMethod:
                        _ = Helper();
                        break;
                    case Misuse.SubscriptionOverflow:
                        using (values.Subscribe(BufferPolicy.Queue(1, BufferOverflow.Fail)))
                        {
                            values.Emit(1);
                            values.Emit(2); // one more than the queue holds
                            await FlowTask.NextFrame();
                        }

                        break;
                    case Misuse.EndlessLoop:
                        while (iterations < 2_000_000)
                        {
                            iterations++;
                            await FlowTask.DelayFrames(0);
                        }

                        break;
                    default:
                        throw byHand;
                }

                await FlowTask.NextFrame();
                Log.Add("unreachable");
            }
            finally
            {
                Log.Add("source finally");
            }
        }

        async FlowTask Root()
        {
            try
            {
                await Source();
                Log.Add("unreachable");
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                caught = e;
                Log.Add("root caught " + e.GetType().Name);
            }
        }

        var h = World.Run(Root());
        Tick(3);
        var expected = misuse == Misuse.SubscriptionOverflow ? nameof(SubscriptionOverflowException) : nameof(FlowMisuseException);
        if (misuse is Misuse.UnbridgedAwait or Misuse.EndlessLoop) AssertLog("cleanup", "root caught " + expected);
        else AssertLog("source finally", "cleanup", "root caught " + expected);
        if (misuse == Misuse.CanceledExceptionThrownByHand) Assert.That(caught.InnerException, Is.SameAs(byHand));
        if (misuse == Misuse.UnbridgedAwait) Assert.That(caught.Message, Does.StartWith("FlowTask method 'Root > Source' suspended on TaskAwaiter<Int32>,"));
        if (misuse == Misuse.EndlessLoop) Assert.That(iterations, Is.EqualTo(1_000_001));
        AssertNoExceptions();
        AssertSucceeded(h);
    }

    [TestCase(Misuse.UnbridgedAwait)]
    [TestCase(Misuse.AwaitInATaskMethod)]
    [TestCase(Misuse.SubscriptionOverflow)]
    [TestCase(Misuse.EndlessLoop)]
    public void AMisuseInTheCleanupOfACanceledScopeIsACleanupException(Misuse misuse)
    {
        // The same misuses in the finally of a canceled scope, here one that the failure of a child it spawned canceled.
        // The scope is ending anyway: the misuse is a cleanup exception and does not change how it ends, so its caller still
        // receives the child's exception. An unbridged await and an endless loop end the finally there. The endless loop
        // stops by itself at 2,000,000, so that a broken limit fails the test instead of hanging it.
        CaptureExceptions();
        var values = new Signal<int>(World, "Values");
        var never = new TaskCompletionSource<int>();
        var iterations = 0;

        async Task Helper() => await FlowTask.NextFrame();

        async FlowTask Owner()
        {
            try
            {
                Flow.Spawn(ThrowsIO(0, "child io", 0.05));
                await FlowTask.Never();
            }
            finally
            {
                Log.Add("owner finally");
                switch (misuse)
                {
                    case Misuse.UnbridgedAwait:
#pragma warning disable FLOW002 // on purpose: the misuse under test
                        await never.Task;
#pragma warning restore FLOW002
                        break;
                    case Misuse.AwaitInATaskMethod:
                        _ = Helper();
                        break;
                    case Misuse.SubscriptionOverflow:
                        using (values.Subscribe(BufferPolicy.Queue(1, BufferOverflow.Fail)))
                        {
                            values.Emit(1);
                            values.Emit(2); // one more than the queue holds
                        }

                        break;
                    default:
                        while (iterations < 2_000_000)
                        {
                            iterations++;
                            await FlowTask.DelayFrames(0);
                        }

                        break;
                }

                await FlowTask.NextFrame();
                Log.Add("owner finally end");
            }
        }

        async FlowTask Caller()
        {
            try
            {
                await Owner();
            }
            catch (IOException e)
            {
                Log.Add("caller caught " + e.Message);
            }
        }

        var h = World.Run(Caller());
        TickFor(0.2);
        if (misuse is Misuse.UnbridgedAwait or Misuse.EndlessLoop) AssertLog("throw child io", "owner finally", "caller caught child io");
        else AssertLog("throw child io", "owner finally", "owner finally end", "caller caught child io");
        var report = Exceptions.Count == 1 ? Exceptions[0] : null;
        Assert.That(report?.Kind, Is.EqualTo(FlowExceptionKind.Cleanup), DescribeExceptions());
        Assert.That(report.Exception, misuse == Misuse.SubscriptionOverflow ? Is.TypeOf<SubscriptionOverflowException>() : Is.TypeOf<FlowMisuseException>());
        Assert.That(report.ScopePath, Is.EqualTo("Caller > Owner"));
        if (misuse == Misuse.EndlessLoop) Assert.That(iterations, Is.EqualTo(1_000_001));
        AssertSucceeded(h);
    }
}
