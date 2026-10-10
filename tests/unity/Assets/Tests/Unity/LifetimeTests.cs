using System;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Katout.FlowTask.Unity.Tests;

/// <summary>GameObject lifetime binding (RunWhileActive / WhileActive / WaitForDestroy).</summary>
public class LifetimeTests
{
    [UnityTest]
    public IEnumerator DestroyCancelsTheFlowAndCleanupRunsWhileTheObjectIsAccessible()
    {
        var go = new GameObject("Owner");
        var component = go.AddComponent<TestEmitter>();
        string seenInFinally = null;

        async FlowTask Patrol()
        {
            try
            {
                while (true) await FlowTask.NextFrame();
            }
            finally
            {
                // Runs inside Object.Destroy, which deactivates the object at once (OnDisable) and destroys it at the
                // end of the frame: it is still accessible.
                seenInFinally = go.name + "/" + component.GetType().Name;
            }
        }

        var h = component.gameObject.RunWhileActive(Patrol());
        yield return Frames(2);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Object.Destroy(go);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled), "unwound before Destroy returned");
        Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
        Assert.That(seenInFinally, Is.EqualTo("Owner/TestEmitter"));
        yield return null;
        Assert.That(go == null, Is.True);
    }

    // ---- Deactivation (SetActive(false), on the object or a parent): the pool's way of putting an object away.

    [UnityTest]
    public IEnumerator DeactivatingCancelsTheFlowAndUnwindsItInsideSetActive()
    {
        var go = new GameObject("Owner");
        var cleaned = false;
        var inactiveInFinally = false;

        async FlowTask Patrol()
        {
            try
            {
                while (true) await FlowTask.NextFrame();
            }
            finally
            {
                cleaned = true;
                inactiveInFinally = !go.activeInHierarchy;
            }
        }

        try
        {
            var h = go.RunWhileActive(Patrol());
            yield return Frames(2);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
            go.SetActive(false);
            // No frame in between: OnDisable canceled the flow and flushed the World inside SetActive(false).
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(h.CancelCause, Is.EqualTo(CancelCause.Explicit));
            Assert.That(cleaned, Is.True);
            Assert.That(inactiveInFinally, Is.True);
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator DeactivatingAParentCancelsTheFlow()
    {
        var parent = new GameObject("Parent");
        var child = new GameObject("Child");
        child.transform.SetParent(parent.transform);
        try
        {
            var h = child.RunWhileActive(FlowTask.Never());
            yield return Frames(2);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
            parent.SetActive(false);
            Assert.That(child.activeSelf, Is.True);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        }
        finally
        {
            Object.Destroy(parent);
        }
    }

    [UnityTest]
    public IEnumerator AFlowThatDeactivatesItsOwnObjectEndsAtItsNextAwait()
    {
        var go = new GameObject("SelfPooling");
        var afterSetActive = false;
        var afterAwait = false;
        var cleaned = false;

        async FlowTask Live()
        {
            try
            {
                await FlowTask.NextFrame();
                go.SetActive(false); // puts itself back in the pool: its own cancellation
                afterSetActive = true;
                await FlowTask.NextFrame();
                afterAwait = true;
            }
            finally
            {
                cleaned = true;
            }
        }

        try
        {
            var h = go.RunWhileActive(Live());
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(afterSetActive, Is.True, "the code up to the next await runs");
            Assert.That(afterAwait, Is.False);
            Assert.That(cleaned, Is.True);
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [Test]
    public void AFlowThatDeactivatesItsObjectBeforeItsFirstAwaitIsCanceledAtOnce()
    {
        // Run executes the flow up to its first await before RunWhileActive binds it: the OnDisable of that SetActive(false)
        // finds nothing bound, so RunWhileActive cancels and unwinds the flow itself, before it returns.
        var go = new GameObject("DeactivatesAtOnce");
        var afterAwait = false;
        var cleaned = false;

        async FlowTask Live()
        {
            try
            {
                go.SetActive(false);
                await FlowTask.NextFrame();
                afterAwait = true;
            }
            finally
            {
                cleaned = true;
            }
        }

        try
        {
            var h = go.RunWhileActive(Live());
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(cleaned, Is.True, "unwound before RunWhileActive returned");
            Assert.That(afterAwait, Is.False);
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [Test]
    public void AFlowThatDeactivatesItsObjectAndEndsWithoutAwaitingSucceeds()
    {
        // The flow ends inside Run, before RunWhileActive binds it: there is nothing left to cancel.
        var go = new GameObject("DeactivatesAndEnds");

        async FlowTask Live()
        {
            go.SetActive(false);
            await FlowTask.CompletedTask;
        }

        try
        {
            var h = go.RunWhileActive(Live());
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator ReactivatingDoesNotRestartTheFlow()
    {
        var go = new GameObject("Toggled");
        var starts = 0;

        async FlowTask Body()
        {
            starts++;
            await FlowTask.Never();
        }

        try
        {
            var h = go.RunWhileActive(Body());
            go.SetActive(false);
            go.SetActive(true);
            yield return Frames(2);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(go.GetComponent<FlowLifetimeTrigger>().LiveCount, Is.Zero);

            var again = go.RunWhileActive(Body()); // a new flow, started again by the caller
            Assert.That(again.Status, Is.EqualTo(FlowStatus.Running));
            Assert.That(starts, Is.EqualTo(2));
            go.SetActive(false);
            Assert.That(again.Status, Is.EqualTo(FlowStatus.Canceled), "the binding works again after a reactivation");
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [Test]
    public void AnInactiveGameObjectRunsNothing()
    {
        // As RunWhileInTree in Godot on a node outside the tree: the task is discarded, not started, and the handle is
        // already canceled. Nothing is bound either, so there is no hidden component to watch.
        var inactive = new GameObject("Inactive");
        inactive.SetActive(false);
        var parent = new GameObject("InactiveParent");
        parent.SetActive(false);
        var child = new GameObject("Child"); // active itself, but not in the hierarchy
        child.transform.SetParent(parent.transform);
        var watched = FlowLifetime.WatchedCount;
        var ran = 0;

        async FlowTask Body()
        {
            ran++;
            await FlowTask.Never();
        }

        async FlowTask<int> Answer()
        {
            ran++;
            await FlowTask.NextFrame();
            return 42;
        }

        try
        {
            var h = inactive.RunWhileActive(Body());
            var underParent = child.RunWhileActive(Body());
            var withResult = inactive.RunWhileActive(Answer());
            Assert.That(ran, Is.Zero, "the task did not run");
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(h.IsCompleted, Is.True);
            Assert.That(underParent.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(withResult.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(withResult.IsCompleted, Is.True);
            Assert.That(inactive.GetComponent<FlowLifetimeTrigger>(), Is.Null);
            Assert.That(child.GetComponent<FlowLifetimeTrigger>(), Is.Null);
            Assert.That(FlowLifetime.WatchedCount, Is.EqualTo(watched));
            h.Cancel(); // harmless on a handle that never ran
        }
        finally
        {
            Object.Destroy(inactive);
            Object.Destroy(parent);
        }
    }

    [UnityTest]
    public IEnumerator RunWhileActiveFromFlowCodeStartsARootFlow()
    {
        // The GameObject owns the flow, whoever starts it: started from flow code, it is not a child of that scope and
        // does not end with it.
        var go = new GameObject("Host");
        var childCleaned = false;
        FlowHandle bound = default;

        async FlowTask Child()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                childCleaned = true;
            }
        }

        async FlowTask Parent()
        {
            bound = go.RunWhileActive(Child());
            await FlowTask.Never();
        }

        try
        {
            var h = W.Run(Parent());
            yield return null;
            h.Cancel();
            yield return Frames(2);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(bound.Status, Is.EqualTo(FlowStatus.Running), "not ended with the scope that started it");
            Assert.That(childCleaned, Is.False);
            Object.Destroy(go);
            Assert.That(bound.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(childCleaned, Is.True);
        }
        finally
        {
            if (go != null) Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator AnExceptionOfAFlowStartedFromFlowCodeGoesToOnUnhandledException()
    {
        // A root flow: its exception does not cancel the scope that started it (as the exception of a Flow.Spawn child
        // would), it goes to OnUnhandledException.
        FlowExceptionInfo captured = null;
        FlowTaskUnity.Configure(new FlowTaskSettings { OnUnhandledException = p => captured = p });
        var go = new GameObject("Host");
        FlowHandle bound = default;

        async FlowTask Exploding()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("bound-boom");
        }

        async FlowTask Caller()
        {
            bound = go.RunWhileActive(Exploding());
            await FlowTask.Never();
        }

        try
        {
            var caller = W.Run(Caller());
            yield return WaitFor(bound);
            yield return null;
            Assert.That(bound.Status, Is.EqualTo(FlowStatus.Faulted));
            Assert.That(caller.Status, Is.EqualTo(FlowStatus.Running), "the caller is not canceled by it");
            Assert.That(captured?.Exception?.Message, Is.EqualTo("bound-boom"));
            caller.Cancel();
        }
        finally
        {
            FlowTaskUnity.Configure(new FlowTaskSettings());
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator FlowsOfSeveralWorldsBoundToOneObjectAllUnwind()
    {
        // Deactivated outside flow code, the object flushes every World that runs one of its flows. Deactivated from flow
        // code of one World, it flushes none: a flow of another World unwinds at that World's next Flush.
        var go = new GameObject("Shared");
        var other = new FlowWorld();
        var cleaned = new List<string>();

        async FlowTask Bound(string name)
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                cleaned.Add(name);
            }
        }

        async FlowTask Deactivate()
        {
            go.SetActive(false);
            await FlowTask.NextFrame();
        }

        try
        {
            var inDefault = go.RunWhileActive(Bound("default"));
            var inOther = go.RunWhileActive(Bound("other"), other.DefaultClock);
            yield return null;
            go.SetActive(false);
            Assert.That(cleaned, Is.EquivalentTo(new[] { "default", "other" }));
            Assert.That(inDefault.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(inOther.Status, Is.EqualTo(FlowStatus.Canceled));

            cleaned.Clear();
            go.SetActive(true);
            var again = go.RunWhileActive(Bound("other again"), other.DefaultClock);
            W.Run(Deactivate());
            Assert.That(cleaned, Is.Empty, "not unwound yet: the other World was not flushed from flow code");
            other.Flush();
            Assert.That(cleaned, Is.EqualTo(new[] { "other again" }));
            Assert.That(again.Status, Is.EqualTo(FlowStatus.Canceled));
        }
        finally
        {
            other.Dispose();
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator DisablingEveryBehaviourStopsTheFlowsAndLaterBindingsStillWork()
    {
        // Freezing an object by disabling all its MonoBehaviours disables the hidden component too: the flows stop then,
        // and a flow bound afterwards still stops when the object is deactivated.
        var go = new GameObject("Frozen");
        try
        {
            var before = go.RunWhileActive(FlowTask.Never());
            foreach (var behaviour in go.GetComponents<MonoBehaviour>()) behaviour.enabled = false;
            Assert.That(before.Status, Is.EqualTo(FlowStatus.Canceled));
            var after = go.RunWhileActive(FlowTask.Never());
            yield return null;
            Assert.That(after.Status, Is.EqualTo(FlowStatus.Running));
            go.SetActive(false);
            Assert.That(after.Status, Is.EqualTo(FlowStatus.Canceled));
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator APooledObjectRunsItsFlowFromOnEnableEachTimeItIsTakenOut()
    {
        // The pool recipe: one line in OnEnable (PooledBullet). Taken out from flow code, the flight is not a child of
        // that flow; put back (SetActive(false), from flow code or not), it unwinds before SetActive returns; taken out
        // again, a new flight starts.
        var go = new GameObject("Bullet");
        go.SetActive(false);
        var bullet = go.AddComponent<PooledBullet>();
        try
        {
            Assert.That(bullet.Flights, Is.Zero, "nothing runs while the bullet waits in the pool");

            async FlowTask TakeOut()
            {
                go.SetActive(true); // OnEnable starts the flight
                await FlowTask.NextFrame();
            }

            var takeOut = W.Run(TakeOut());
            yield return WaitFor(takeOut);
            Assert.That(takeOut.Status, Is.EqualTo(FlowStatus.Succeeded));
            var first = bullet.Flight;
            yield return Frames(2);
            Assert.That(first.Status, Is.EqualTo(FlowStatus.Running), "it outlived the flow that took the bullet out");
            Assert.That(bullet.Flights, Is.EqualTo(1));

            go.SetActive(false); // back to the pool, from outside flow code
            Assert.That(first.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(bullet.Cleanups, Is.EqualTo(1));
            Assert.That(bullet.CleanedWhileInactive, Is.True, "unwound inside OnDisable");

            go.SetActive(true); // out again, from outside flow code
            var second = bullet.Flight;
            Assert.That(bullet.Flights, Is.EqualTo(2));
            yield return Frames(2);
            Assert.That(second.Status, Is.EqualTo(FlowStatus.Running));
            Assert.That(first.Status, Is.EqualTo(FlowStatus.Canceled), "the first flight did not resume");

            var cleanupsSeenAfterSetActive = -1;

            async FlowTask PutBack()
            {
                go.SetActive(false); // from flow code: the flight unwinds on the spot
                cleanupsSeenAfterSetActive = bullet.Cleanups;
                await FlowTask.NextFrame();
            }

            var putBack = W.Run(PutBack());
            Assert.That(second.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(cleanupsSeenAfterSetActive, Is.EqualTo(2), "unwound before SetActive(false) returned");
            yield return WaitFor(putBack);
            Assert.That(putBack.Status, Is.EqualTo(FlowStatus.Succeeded));
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator CompletedFlowsDoNotKeepTheBindingAlive()
    {
        var go = new GameObject("Short");
        var h1 = go.RunWhileActive(FlowTask.NextFrame());
        yield return WaitFor(h1);
        var h2 = go.RunWhileActive(FlowTask.Never());
        Assert.That(go.GetComponent<FlowLifetimeTrigger>().LiveCount, Is.EqualTo(1));
        Object.Destroy(go);
        yield return null;
        Assert.That(h1.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h2.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [UnityTest]
    public IEnumerator RunWhileActiveRunsTheFlowOnTheGivenClock()
    {
        var go = new GameObject("Clocked");
        Clock seen = null;

        async FlowTask Body()
        {
            seen = Flow.CurrentClock;
            await FlowTask.Never();
        }

        try
        {
            var h = go.RunWhileActive(Body(), W.UnscaledClock);
            Assert.That(seen, Is.SameAs(W.UnscaledClock));
            yield return null;
            go.SetActive(false);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    // ---- WhileActive: the same binding, inside the awaiting scope.

    [UnityTest]
    public IEnumerator WhileActiveReturnsFalseWhenTheObjectIsDeactivatedAndUnwindsInsideSetActive()
    {
        var go = new GameObject("Owner");
        var cleaned = false;
        var inactiveInFinally = false;
        bool? completed = null;
        string dump = null;

        async FlowTask Patrol()
        {
            try
            {
                while (true) await FlowTask.NextFrame();
            }
            finally
            {
                cleaned = true;
                inactiveInFinally = !go.activeInHierarchy;
            }
        }

        async FlowTask Host()
        {
            completed = await go.WhileActive(Patrol());
            await FlowTask.Never(); // the awaiting scope goes on
        }

        var h = W.Run(Host());
        try
        {
            yield return Frames(2);
            dump = W.Dump();
            Assert.That(completed, Is.Null);
            go.SetActive(false);
            // No frame in between: OnDisable completed the deactivation wait and flushed the World inside SetActive(false).
            Assert.That(cleaned, Is.True);
            Assert.That(inactiveInFinally, Is.True);
            Assert.That(completed, Is.False);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Running), "the awaiting scope is not canceled");
            Assert.That(go.GetComponent<FlowLifetimeTrigger>().LiveCount, Is.Zero);
            Assert.That(dump, Does.Contain("WhileActive(Owner) (scope)"));
        }
        finally
        {
            h.Cancel();
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator WhileActiveReturnsTheResultWhenTheTaskCompletes()
    {
        var go = new GameObject("Owner");
        var completed = false;
        (bool Completed, int Value) value = default;

        async FlowTask<int> Answer()
        {
            await FlowTask.NextFrame();
            return 42;
        }

        async FlowTask Host()
        {
            completed = await go.WhileActive(FlowTask.NextFrame());
            value = await go.WhileActive(Answer());
        }

        try
        {
            var h = W.Run(Host());
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(completed, Is.True);
            Assert.That(value, Is.EqualTo((true, 42)));
            Assert.That(go.GetComponent<FlowLifetimeTrigger>().LiveCount, Is.Zero, "the waits are released when the scopes end");
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator WhileActiveWithAResultReturnsNotCompletedWhenTheObjectIsDestroyed()
    {
        var go = new GameObject("Owner");
        (bool Completed, int Value) value = (true, 1);
        string seenInFinally = null;

        async FlowTask<int> Forever()
        {
            try
            {
                await FlowTask.Never();
                return 0;
            }
            finally
            {
                seenInFinally = go.name; // throws MissingReferenceException once the object is gone
            }
        }

        async FlowTask Host()
        {
            value = await go.WhileActive(Forever());
        }

        var h = W.Run(Host());
        try
        {
            yield return Frames(2);
            Object.Destroy(go);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded), "decided inside Destroy");
            Assert.That(value.Completed, Is.False);
            Assert.That(seenInFinally, Is.EqualTo("Owner"));
        }
        finally
        {
            h.Cancel();
            if (go != null) Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator WhileActiveInAnotherWorldUnwindsInsideSetActive()
    {
        // The deactivation wait belongs to the World that runs the WhileActive: OnDisable flushes that World.
        var go = new GameObject("Owner");
        var other = new FlowWorld();
        var cleaned = false;
        bool? completed = null;

        async FlowTask Patrol()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                cleaned = true;
            }
        }

        async FlowTask Host()
        {
            completed = await go.WhileActive(Patrol());
        }

        try
        {
            var h = other.Run(Host());
            yield return null;
            go.SetActive(false);
            Assert.That(cleaned, Is.True, "unwound inside SetActive(false)");
            Assert.That(completed, Is.False);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        }
        finally
        {
            other.Dispose();
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator WhileActiveEndsWithTheAwaitingScope()
    {
        var go = new GameObject("Owner");
        var cleaned = false;

        async FlowTask Patrol()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                cleaned = true;
            }
        }

        async FlowTask Host()
        {
            await go.WhileActive(Patrol());
        }

        try
        {
            var h = W.Run(Host());
            yield return null;
            h.Cancel();
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(cleaned, Is.True);
            Assert.That(go.GetComponent<FlowLifetimeTrigger>().LiveCount, Is.Zero, "the wait is released with the scope");
            go.SetActive(false); // nothing left to end
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [UnityTest]
    public IEnumerator AnExceptionOfWhileActiveIsThrownInTheAwaitingScope()
    {
        var go = new GameObject("Owner");
        string caught = null;

        async FlowTask Fail()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("boom");
        }

        async FlowTask Host()
        {
            try
            {
                await go.WhileActive(Fail());
            }
            catch (InvalidOperationException ex)
            {
                caught = ex.Message;
            }
        }

        try
        {
            var h = W.Run(Host());
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(caught, Is.EqualTo("boom"));
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    [Test]
    public void WhileActiveOnADestroyedObjectRunsNothingAndANullReferenceIsRejected()
    {
        var go = new GameObject("Destroyed");
        Object.DestroyImmediate(go);
        var ran = false;

        async FlowTask Body()
        {
            ran = true;
            await FlowTask.Never();
        }

        bool? completed = null;

        async FlowTask Host()
        {
            completed = await go.WhileActive(Body());
        }

        var h = W.Run(Host());
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(completed, Is.False);
        Assert.That(ran, Is.False);

        GameObject none = null;
        Assert.Throws<ArgumentNullException>(() => W.Run(none.WhileActive(FlowTask.CompletedTask)));
    }

    [Test]
    public void WhileActiveOnAnInactiveObjectRunsNothing()
    {
        var inactive = new GameObject("Inactive");
        inactive.SetActive(false);
        var ran = 0;
        bool? completed = null;
        (bool Completed, int Value) value = (true, 1);

        async FlowTask Body()
        {
            ran++;
            await FlowTask.Never();
        }

        async FlowTask<int> Answer()
        {
            ran++;
            await FlowTask.Never();
            return 42;
        }

        async FlowTask Host()
        {
            completed = await inactive.WhileActive(Body());
            value = await inactive.WhileActive(Answer());
        }

        try
        {
            var h = W.Run(Host());
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(ran, Is.Zero, "the task did not run");
            Assert.That(completed, Is.False);
            Assert.That(value.Completed, Is.False);
            Assert.That(inactive.GetComponent<FlowLifetimeTrigger>(), Is.Null);
        }
        finally
        {
            Object.Destroy(inactive);
        }
    }

    [Test]
    public void WhileActiveWhoseTaskDeactivatesTheObjectBeforeItsFirstAwaitReturnsFalseAtTheNextFlush()
    {
        // The deactivation wait is in place before the task's first step, which runs inside the Race. Deactivated from
        // flow code, the wait completes there, and the Race is decided when the World next runs its queued resumes: later
        // in the same pass inside a Tick or a Flush, at the next flush point inside a Run called from outside (here).
        var go = new GameObject("DeactivatesAtOnce");
        var afterAwait = false;
        var cleaned = false;
        bool? completed = null;

        async FlowTask Live()
        {
            try
            {
                go.SetActive(false);
                await FlowTask.NextFrame();
                afterAwait = true;
            }
            finally
            {
                cleaned = true;
            }
        }

        async FlowTask Host()
        {
            completed = await go.WhileActive(Live());
        }

        try
        {
            var h = W.Run(Host());
            Assert.That(completed, Is.Null);
            W.Flush();
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(completed, Is.False);
            Assert.That(cleaned, Is.True);
            Assert.That(afterAwait, Is.False);
        }
        finally
        {
            Object.Destroy(go);
        }
    }

    // ---- WaitForDestroy: destruction only.

    [UnityTest]
    public IEnumerator WaitForDestroyCompletesWhenTheObjectIsDestroyed()
    {
        var go = new GameObject("Target");
        var observed = false;

        async FlowTask Observe()
        {
            await go.WaitForDestroy();
            observed = true;
        }

        var h = W.Run(Observe());
        yield return Frames(2);
        Assert.That(observed, Is.False);
        Object.Destroy(go);
        yield return WaitFor(h);
        Assert.That(observed, Is.True);
    }

    [UnityTest]
    public IEnumerator ARaceWithWaitForDestroyUnwindsItsLoserInsideOnDestroy()
    {
        // To stop at the destruction only: FlowTask.Race(task, go.WaitForDestroy()). Deactivating does not end it, and
        // the Race is decided inside OnDestroy (a flush right there), so the loser's finally runs while the object is
        // still accessible. Made in flow code (a leaf of the running World) and outside flow code (a state machine that
        // waits in the World that starts it).
        var go = new GameObject("Raced");
        string seenInFlow = null;
        string seenFromOutside = null;

        async FlowTask Work(bool inFlow)
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                // Throws MissingReferenceException once the object is gone.
                if (inFlow) seenInFlow = go.name;
                else seenFromOutside = go.name;
            }
        }

        async FlowTask Guarded()
        {
            await FlowTask.Race(Work(true), go.WaitForDestroy());
        }

        var inFlow = W.Run(Guarded());
        var fromOutside = W.Run(FlowTask.Race(Work(false), go.WaitForDestroy()));
        yield return Frames(2);
        go.SetActive(false);
        yield return Frames(2);
        Assert.That(inFlow.Status, Is.EqualTo(FlowStatus.Running), "deactivating does not complete WaitForDestroy");
        Assert.That(fromOutside.Status, Is.EqualTo(FlowStatus.Running));
        Object.Destroy(go);
        yield return null;
        Assert.That(inFlow.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(fromOutside.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(seenInFlow, Is.EqualTo("Raced"), "the loser unwound inside OnDestroy");
        Assert.That(seenFromOutside, Is.EqualTo("Raced"));
    }

    [UnityTest]
    public IEnumerator ActivatingAWatchedGameObjectStopsWatching()
    {
        // WaitForDestroy on a GameObject that was never active: the PlayerLoop watches it, as Unity would not call its
        // OnDestroy. Once activated, it is awake, and its OnDestroy does the work.
        var go = new GameObject("LateActive");
        go.SetActive(false);
        var h = W.Run(go.WaitForDestroy());
        Assert.That(FlowLifetime.WatchedCount, Is.GreaterThan(0));
        go.SetActive(true);
        yield return Frames(2);
        Assert.That(FlowLifetime.WatchedCount, Is.EqualTo(0));
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
        Object.Destroy(go);
        yield return null;
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [UnityTest]
    public IEnumerator WaitForDestroyWorksInTheNextWorldAfterShutdown()
    {
        // The GameObject outlives the World that first waited for its destruction (Shutdown then Initialize; a World
        // per session and DontDestroyOnLoad do the same). A wait belongs to the World that runs it, so the
        // next World can still wait: it used to fail with FlowMisuseException ("bound to ... which has been
        // disposed"), naming a Once the user never created.
        var go = new GameObject("Survivor");
        try
        {
            var first = W.Run(go.WaitForDestroy());
            yield return Frames(2);
            Assert.That(first.Status, Is.EqualTo(FlowStatus.Running));
            FlowTaskUnity.Shutdown();
            FlowTaskUnity.Initialize();
            Assert.That(first.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(first.CancelCause, Is.EqualTo(CancelCause.WorldDisposed));

            var observed = false;

            async FlowTask Observe()
            {
                await go.WaitForDestroy();
                observed = true;
            }

            var h = W.Run(Observe());
            yield return Frames(2);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Running));
            Object.Destroy(go);
            yield return WaitFor(h);
            Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(observed, Is.True);
        }
        finally
        {
            if (go != null) Object.Destroy(go);
            if (!FlowTaskUnity.IsInstalled) FlowTaskUnity.Initialize();
        }
    }

    [UnityTest]
    public IEnumerator WaitForDestroyCompletesInEveryWorldWaitingForTheObject()
    {
        // Two Worlds wait for the same GameObject at once (the default one, and one made by hand), from flow code and
        // from outside it: each waits on a Once of its own, and the Destroy completes both.
        var go = new GameObject("Shared");
        var other = new FlowWorld();
        try
        {
            var seen = 0;

            async FlowTask Observe()
            {
                await go.WaitForDestroy(); // flow code: the current World's Once
                seen++;
            }

            var inDefault = W.Run(Observe());
            var inOther = other.Run(Observe());
            var fromOutside = other.Run(go.WaitForDestroy()); // outside flow code: the Once of the World that starts it
            other.Tick(1.0 / 60);
            yield return Frames(2);
            Assert.That(inDefault.Status, Is.EqualTo(FlowStatus.Running));
            Assert.That(inOther.Status, Is.EqualTo(FlowStatus.Running));
            Object.Destroy(go);
            yield return WaitFor(inDefault);
            other.Tick(1.0 / 60);
            Assert.That(inDefault.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(inOther.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(fromOutside.Status, Is.EqualTo(FlowStatus.Succeeded));
            Assert.That(seen, Is.EqualTo(2));
        }
        finally
        {
            other.Dispose();
            if (go != null) Object.Destroy(go);
        }
    }

    // ---- Scene changes and objects that were never active. A scene made at run time needs no entry in the build
    // settings, so these also run in the test players.

    static int s_scenes;

    static GameObject CreateInNewScene(string name, bool active, out Scene scene)
    {
        scene = SceneManager.CreateScene("FlowTaskLifetime" + ++s_scenes);
        var go = new GameObject(name);
        if (!active) go.SetActive(false);
        SceneManager.MoveGameObjectToScene(go, scene);
        return go;
    }

    [UnityTest]
    public IEnumerator UnloadingTheSceneCancelsItsFlowsWhileItsObjectsAreAccessible()
    {
        var go = CreateInNewScene("InScene", true, out var scene);
        var sceneName = scene.name;
        string seenInFinally = null;
        var observed = false;

        async FlowTask Patrol()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                seenInFinally = go.name + " in " + go.scene.name; // the object and its scene are still there
            }
        }

        async FlowTask Observe()
        {
            await go.WaitForDestroy();
            observed = true;
        }

        var h = go.RunWhileActive(Patrol());
        var observer = W.Run(Observe());
        yield return Frames(2);
        yield return SceneManager.UnloadSceneAsync(scene);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(seenInFinally, Is.EqualTo("InScene in " + sceneName), "unwound during the unload");
        yield return WaitFor(observer);
        Assert.That(observed, Is.True, "WaitForDestroy completes when the scene is unloaded");
    }

    [UnityTest]
    public IEnumerator ObjectsMovedToDontDestroyOnLoadKeepTheirFlowsWhenTheSceneIsUnloaded()
    {
        var stays = CreateInNewScene("Stays", true, out var scene);
        var goes = new GameObject("Goes");
        SceneManager.MoveGameObjectToScene(goes, scene);
        var kept = stays.RunWhileActive(FlowTask.Never());
        var ended = goes.RunWhileActive(FlowTask.Never());
        Object.DontDestroyOnLoad(stays);
        try
        {
            yield return SceneManager.UnloadSceneAsync(scene);
            Assert.That(ended.Status, Is.EqualTo(FlowStatus.Canceled));
            Assert.That(kept.Status, Is.EqualTo(FlowStatus.Running), "the object left the scene before it was unloaded");
        }
        finally
        {
            Object.Destroy(stays);
        }

        yield return null;
        Assert.That(kept.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    [UnityTest]
    public IEnumerator WaitForDestroyOfANeverActivatedObjectCompletesWithinAFrameOfItsSceneUnload()
    {
        var go = CreateInNewScene("InactiveInScene", false, out var scene);
        var cleaned = false;

        async FlowTask Work()
        {
            try
            {
                await FlowTask.Never();
            }
            finally
            {
                cleaned = true;
            }
        }

        var h = W.Run(FlowTask.Race(Work(), go.WaitForDestroy()));
        Assert.That(FlowLifetime.WatchedCount, Is.GreaterThan(0));
        yield return SceneManager.UnloadSceneAsync(scene);
        // Unity calls no OnDestroy on an object that was never active: the next Tick's PollWatched finds it gone.
        yield return null;
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(cleaned, Is.True);
        Assert.That(FlowLifetime.WatchedCount, Is.EqualTo(0));
    }

    [UnityTest]
    public IEnumerator WaitForDestroyOfAnObjectUnderAnInactiveParentCompletesWhenTheParentIsDestroyed()
    {
        var parent = new GameObject("InactiveParent");
        parent.SetActive(false);
        var child = new GameObject("Child");
        child.transform.SetParent(parent.transform);
        // Active itself but not in the hierarchy: never awoken, so Unity will not call its OnDestroy either.
        var h = W.Run(child.WaitForDestroy());
        Assert.That(FlowLifetime.WatchedCount, Is.GreaterThan(0));
        Object.Destroy(parent);
        yield return Frames(2);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(FlowLifetime.WatchedCount, Is.EqualTo(0));
    }

    [Test]
    public void DestroyedObjectsAreRejected()
    {
        GameObject none = null;
        Assert.Throws<ArgumentNullException>(() => none.RunWhileActive(FlowTask.CompletedTask));
    }
}
