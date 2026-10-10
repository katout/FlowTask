using System.Reflection;
using Katout.FlowTask.Testing;

namespace Katout.FlowTask.Tests;

/// <summary>Scopes.</summary>
public class ScopeTests : FlowTestBase
{
    async FlowTask Child()
    {
        try
        {
            Log.Add("child start");
            await FlowTask.WaitForSeconds(100);
            Log.Add("child end");
        }
        finally
        {
            Log.Add("child finally");
        }
    }

    [Test]
    public void StartedTaskIsAChildOfTheStartingScope()
    {
        async FlowTask Parent() => await Child();

        World.Run(Parent());
        var parent = World.Diagnostics.Walk().Single(s => s.Name == "Parent");
        Assert.That(parent.Children.Select(c => c.Name), Is.EqualTo(new[] { "Child" }));
        Assert.That(parent.Children[0].Path, Is.EqualTo("Parent > Child"));
    }

    async FlowTask Parked(string name)
    {
        try
        {
            await FlowTask.Never();
        }
        finally
        {
            Log.Add(name + " finally");
        }
    }

    // A parent that returns unwinds its live children, later-started first, then runs its own cleanups (LIFO among
    // themselves). The cleanup registered after the spawns also runs after the children: children and cleanups are not
    // one LIFO in registration order.
    [Test]
    public void AParentThatReturnsUnwindsItsLiveChildrenLaterStartedFirstThenRunsItsCleanups()
    {
        async FlowTask Parent()
        {
            Flow.AddCleanup(() => Log.Add("parent cleanup"));
            Flow.Spawn(Parked("a"));
            Flow.Spawn(Parked("b"));
            Flow.AddCleanup(() => Log.Add("cleanup after spawn"));
            await FlowTask.NextFrame();
            Log.Add("parent returns");
        }

        var h = World.Run(Parent());
        Tick();
        AssertLog("parent returns", "b finally", "a finally", "cleanup after spawn", "parent cleanup");
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        FlowAssert.NoLiveScopes(World);
    }

    [Test]
    public void SpawnReturnsACancellableHandleAwaitableOnce()
    {
        CaptureExceptions();
        FlowHandle<int> handle = default;

        async FlowTask<int> Work()
        {
            await FlowTask.WaitForSeconds(0.05);
            return 42;
        }

        async FlowTask Parent()
        {
            handle = Flow.Spawn(Work());
            Log.Add("spawned");
            var v = await handle.Join();
            Log.Add("joined " + v);
            var second = Flow.Spawn(Child());
            await FlowTask.NextFrame();
            second.Cancel();
            Log.Add("canceled " + second.Status);
            await handle.Join(); // second join: misuse
        }

        World.Run(Parent());
        TickFor(0.2);
        AssertLog("spawned", "joined 42", "child start", "child finally", "canceled Canceled");
        Assert.That(Exceptions.Single().Exception, Is.InstanceOf<FlowMisuseException>());
    }

    [Test]
    public void JoinComposesWithRaceAndWaitUntilObservesAnyEnd()
    {
        async FlowTask<int> Slow()
        {
            await FlowTask.WaitForSeconds(10);
            return 1;
        }

        async FlowTask Parent()
        {
            var h = Flow.Spawn(Slow());
            var r = await FlowTask.Race(h.Join(), FlowTask.WaitForSeconds(0.05));
            Log.Add("race " + r.Index);
            var h2 = Flow.Spawn(Slow());
            h2.Cancel();
            await FlowTask.WaitUntil(h2, x => x.IsCompleted); // any outcome, without the Join's exception
            Log.Add("ended " + h2.Status);
        }

        World.Run(Parent());
        TickFor(0.2);
        AssertLog("race 1", "ended Canceled");
    }

    [Test]
    public void JoiningACanceledTaskFails()
    {
        CaptureExceptions();

        async FlowTask Parent()
        {
            var h = Flow.Spawn(FlowTask.Never());
            h.Cancel();
            await h.Join();
            Log.Add("unreachable");
        }

        World.Run(Parent());
        Tick();
        Assert.That(Exceptions.Single().Exception, Is.InstanceOf<FlowJoinException>());
    }

    [Test]
    public void JoiningItselfOrAnEnclosingScopeFails()
    {
        CaptureExceptions();
        FlowHandle self = default, parent = default;

        async FlowTask Self()
        {
            await FlowTask.NextFrame();
            await self.Join(); // used to wait for its own end, silently
            Log.Add("unreachable");
        }

        async FlowTask Child()
        {
            await FlowTask.NextFrame();
            await parent.Join(); // Parent ends only after this child
            Log.Add("unreachable");
        }

        async FlowTask Parent()
        {
            Flow.Spawn(Child());
            await FlowTask.WaitForSeconds(10);
        }

        self = World.Run(Self());
        parent = World.Run(Parent());
        Tick();
        AssertLog();
        Assert.That(Exceptions.Select(p => p.Exception), Has.All.TypeOf<FlowMisuseException>());
        Assert.That(Exceptions.Select(p => p.ScopePath), Is.EqualTo(new[] { "Self > Join(Self)", "Parent > Child > Join(Parent)" }));
        Assert.That(Exceptions[0].Exception.Message, Does.Contain("can never complete"));
        // The join throws the misuse at its await: Self does not catch it and ends Faulted; the spawned Child's
        // exception goes to the root through Parent, which ends Faulted with it.
        Assert.That(self.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(parent.Status, Is.EqualTo(FlowStatus.Faulted));
        Assert.That(parent.CancelCause, Is.EqualTo(CancelCause.Fault));
    }

    [Test]
    public void ASpawnedChildsExceptionReachesTheCallerOfTheScopeThatSpawnedIt()
    {
        async FlowTask Bad()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("bad");
        }

        async FlowTask Inner()
        {
            Flow.Spawn(Bad());
            await FlowTask.WaitForSeconds(10);
        }

        async FlowTask Parent()
        {
            try
            {
                await Inner();
                Log.Add("ok");
            }
            catch (Exception e) when (e is not FlowCanceledException)
            {
                Log.Add("caught: " + e.Message);
            }
        }

        World.Run(Parent());
        Tick(3);
        AssertLog("caught: bad");
    }

    [Test]
    public void CurrentScopeIsRestoredOnEveryResume()
    {
        async FlowTask Leaf()
        {
            Log.Add(Flow.CurrentScopePath);
            await FlowTask.NextFrame();
            Log.Add(Flow.CurrentScopePath);
        }

        async FlowTask Mid()
        {
            await Leaf();
            Log.Add(Flow.CurrentScopePath);
        }

        World.Run(Mid());
        Tick(3);
        AssertLog("Mid > Leaf", "Mid > Leaf", "Mid");
    }

    [Test]
    public void CurrentScopeIsPerWorld()
    {
        using var other = new FlowWorld();

        async FlowTask A()
        {
            for (var i = 0; i < 3; i++)
            {
                Log.Add("A:" + (FlowWorld.Current == this.World) + ":" + Flow.CurrentScopePath);
                await FlowTask.NextFrame();
            }
        }

        async FlowTask B()
        {
            for (var i = 0; i < 3; i++)
            {
                Log.Add("B:" + (FlowWorld.Current == other) + ":" + Flow.CurrentScopePath);
                await FlowTask.NextFrame();
            }
        }

        World.Run(A());
        other.Run(B());
        for (var i = 0; i < 3; i++)
        {
            World.Tick(Dt);
            other.Tick(Dt);
        }

        Assert.That(Log.Entries.Where(e => e.StartsWith("A")), Has.All.EqualTo("A:True:A"));
        Assert.That(Log.Entries.Where(e => e.StartsWith("B")), Has.All.EqualTo("B:True:B"));
    }

    [Test]
    public void ScopeNameDefaultsToMethodNameAndCanBeOverridden()
    {
        async FlowTask Outer()
        {
            async FlowTask LocalHelper() => await FlowTask.WaitForSeconds(1);
            await FlowTask.WhenAll(LocalHelper(), Flow.Named("custom", Child()));
        }

        World.Run(Outer());
        var names = World.Diagnostics.Walk().Select(s => s.Name).ToArray();
        Assert.That(names, Does.Contain("Outer"));
        Assert.That(names, Does.Contain("LocalHelper"));
        Assert.That(names, Does.Contain("custom"));
    }

    static class NestedFlows
    {
        internal static async FlowTask InNestedClass() => await FlowTask.NextFrame();
    }

    sealed class GenericHost<T>
    {
        internal async FlowTask InGenericType() => await FlowTask.NextFrame();
    }

    async FlowTask OfTheTestClass() => await FlowTask.NextFrame();

    [Test]
    public void ScopeInfoReportsTheDeclaringTypeAndMethodName()
    {
        var captured = 0;

        async FlowTask LocalFunction()
        {
            await FlowTask.NextFrame();
            captured++;
        }

        Func<FlowTask> lambda = async () => await FlowTask.NextFrame();

        async FlowTask Root() => await FlowTask.WhenAll(new[]
        {
            OfTheTestClass(),
            NestedFlows.InNestedClass(),
            new GenericHost<int>().InGenericType(),
            LocalFunction(),
            lambda(),
            Flow.Named("renamed", OfTheTestClass()),
            FlowTask.WaitForSeconds(1),
        });

        World.Run(Root());
        var all = World.Diagnostics.Walk().ToList();
        var scopes = all.Where(s => s.Kind == FlowScopeKind.Scope).ToList();

        void Expect(string name, Type declaringType, string methodName)
        {
            var s = scopes.Single(x => x.Name == name);
            Assert.That(s.DeclaringType, Is.EqualTo(declaringType), name);
            Assert.That(s.MethodName, Is.EqualTo(methodName), name);
        }

        const string test = nameof(ScopeInfoReportsTheDeclaringTypeAndMethodName);
        Expect("Root", typeof(ScopeTests), "Root");
        Expect("OfTheTestClass", typeof(ScopeTests), "OfTheTestClass");
        Expect("InNestedClass", typeof(NestedFlows), "InNestedClass");
        Expect("InGenericType", typeof(GenericHost<>), "InGenericType"); // the generic type definition
        Expect("LocalFunction", typeof(ScopeTests), "LocalFunction"); // captures a variable: its closure class is skipped
        Expect(test + ".lambda", typeof(ScopeTests), test); // a lambda reports the method that contains it
        Expect("renamed", typeof(ScopeTests), "OfTheTestClass"); // Flow.Named changes Name only

        foreach (var other in all.Where(s => s.Kind != FlowScopeKind.Scope))
        {
            Assert.That(other.DeclaringType, Is.Null, other.Name);
            Assert.That(other.MethodName, Is.Null, other.Name);
        }

        Assert.That(all.Select(s => s.Kind), Has.Member(FlowScopeKind.Root).And.Member(FlowScopeKind.Combinator).And.Member(FlowScopeKind.Wait));
        Tick();
        Assert.That(captured, Is.EqualTo(1));
    }

    [Test]
    public void TreeSearchIsOnlyExposedThroughDiagnostics()
    {
        var worldApi = typeof(FlowWorld).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(FlowScopeInfo) || m.Name.StartsWith("Find")).ToArray();
        Assert.That(worldApi, Is.Empty);
        var flowApi = typeof(Flow).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Find") || m.ReturnType == typeof(FlowScopeInfo)).ToArray();
        Assert.That(flowApi, Is.Empty);
        Assert.That(typeof(FlowScopeInfo).Namespace, Is.EqualTo("Katout.FlowTask.Diagnostics"));
        Assert.That(typeof(FlowWarning).Namespace, Is.EqualTo("Katout.FlowTask"), "warnings are handled by game code, next to FlowExceptionInfo");
    }

    sealed class Resource : IDisposable
    {
        readonly Log _log;
        readonly string _name;

        public Resource(Log log, string name)
        {
            _log = log;
            _name = name;
        }

        public int DisposeCount;

        public void Dispose()
        {
            DisposeCount++;
            _log.Add("dispose " + _name);
        }
    }

    [Test]
    public void OwnAndAddCleanupRunLifoAfterFinally()
    {
        async FlowTask Scope()
        {
            Flow.Own(new Resource(Log, "r1"));
            Flow.AddCleanup(() => Log.Add("cleanup d1"));
            Flow.Own(new Resource(Log, "r2"));
            Flow.AddCleanup("d2", s => Log.Add("cleanup " + s));
            try
            {
                using var local = new Resource(Log, "using");
                await FlowTask.WaitForSeconds(10);
            }
            finally
            {
                Log.Add("finally");
            }
        }

        var h = World.Run(Scope());
        Tick();
        h.Cancel();
        Tick();
        AssertLog("dispose using", "finally", "cleanup d2", "dispose r2", "cleanup d1", "dispose r1");
    }

    [Test]
    public void OwnOutsideAScopeHasNoOwner()
    {
        var r = new Resource(Log, "free");
        Assert.That(Flow.Own(r), Is.SameAs(r));
        World.Dispose();
        Assert.That(r.DisposeCount, Is.EqualTo(0), "the caller disposes it: nothing tracks it");
    }

    [Test]
    public void PauseOutsideAScopeMustBeDisposedManually()
    {
        var pause = World.DefaultClock.Pause();
        Assert.That(World.DefaultClock.PauseCount, Is.GreaterThan(0));
        pause.Dispose();
        pause.Dispose();
        Assert.That(World.DefaultClock.PauseCount, Is.Zero);
    }

    [Test]
    public void AddCleanupOutsideAFlowThrows()
    {
        Assert.Throws<FlowMisuseException>(() => Flow.AddCleanup(() => { }));
    }
}
