using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Katout.FlowTask.Unity.Tests;

/// <summary>
/// UnityEvent -&gt; Signal owned by the scope, AsyncOperation / Awaitable, uGUI helpers, and the Task bridges' resume
/// on the World thread under the real UnitySynchronizationContext.
/// </summary>
public class BridgeTests
{
    [UnityTest]
    public IEnumerator UnityEventToSignalIsOwnedByTheCurrentScope()
    {
        var ev = new UnityEvent<int>();
        EventSignal<int> signal = null;
        var received = 0;

        async FlowTask Listen()
        {
            signal = ev.ToSignal();
            received = await signal.Next();
        }

        var h = W.Run(Listen());
        Assert.That(signal, Is.Not.Null, "the flow started synchronously and attached the listener");
        ev.Invoke(7);
        Assert.That(received, Is.EqualTo(0), "receivers are never resumed synchronously");
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(received, Is.EqualTo(7));
        Assert.That(signal.IsDisposed, Is.True, "the listener is removed when the owning scope ends");
        ev.Invoke(8); // no receiver, no error
    }

    [UnityTest]
    public IEnumerator UnityEventWithoutArgumentsAndWithTwoArguments()
    {
        var click = new UnityEvent();
        var pair = new UnityEvent<string, float>();
        (string, float) got = default;

        async FlowTask Listen()
        {
            using (var c = click.ToSignal("click")) await c.Next();
            // Events with two or more arguments go through FromCallback (CONTRIBUTING.md).
            using var s = FlowBridge.FromCallback<(string, float)>(h =>
            {
                UnityAction<string, float> a = (x, y) => h((x, y));
                pair.AddListener(a);
                return () => pair.RemoveListener(a);
            }, "pair");
            got = await s.Next();
        }

        var h = W.Run(Listen());
        click.Invoke();
        yield return Frames(2);
        pair.Invoke("x", 1.5f);
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(got, Is.EqualTo(("x", 1.5f)));
    }

    [UnityTest]
    public IEnumerator CancelingTheScopeDetachesTheListener()
    {
        var ev = new UnityEvent();
        EventSignal<FlowUnit> signal = null;

        async FlowTask Listen()
        {
            signal = ev.ToSignal();
            await signal.Next();
        }

        var h = W.Run(Listen());
        h.Cancel();
        yield return null;
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(signal.IsDisposed, Is.True);
    }

    [UnityTest]
    public IEnumerator AsyncOperationAsFlowCompletes()
    {
        var op = Resources.UnloadUnusedAssets();
        var done = false;

        async FlowTask Unload()
        {
            await op.AsFlow();
            done = true;
        }

        var h = W.Run(Unload());
        yield return WaitFor(h, 10);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(done, Is.True);
        Assert.That(op.isDone, Is.True);
    }

    [UnityTest]
    public IEnumerator ResourceRequestReturnsTheAsset()
    {
        UnityEngine.Object asset = null;

        async FlowTask Load() => asset = await Resources.LoadAsync<TextAsset>("flowtask-resource").AsFlow();

        var h = W.Run(Load());
        yield return WaitFor(h, 10);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(asset, Is.InstanceOf<TextAsset>());
        Assert.That(((TextAsset)asset).text, Does.Contain("FlowTask"));
    }

    [UnityTest]
    public IEnumerator CanceledAsyncOperationFlowIgnoresTheCompletion()
    {
        var op = Resources.UnloadUnusedAssets();
        Assume.That(op.isDone, Is.False);
        var resumed = false;
        var cleaned = false;

        async FlowTask Unload()
        {
            try
            {
                await op.AsFlow();
                resumed = true;
            }
            finally
            {
                cleaned = true;
            }
        }

        var h = W.Run(Unload());
        h.Cancel();
        var start = Time.realtimeSinceStartup;
        while (!op.isDone && Time.realtimeSinceStartup - start < 10) yield return null;
        yield return Frames(2);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(cleaned, Is.True);
        Assert.That(resumed, Is.False);
    }

    [UnityTest]
    public IEnumerator AwaitableNextFrameAsFlow()
    {
        int before = 0, after = 0;

        async FlowTask F()
        {
            before = Time.frameCount;
            await Awaitable.NextFrameAsync().AsFlow();
            after = Time.frameCount;
        }

        var h = W.Run(F());
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(after, Is.GreaterThan(before));
    }

    static async Awaitable<int> ComputeAsync()
    {
        await Awaitable.NextFrameAsync();
        return 42;
    }

    [UnityTest]
    public IEnumerator AwaitableWithResultAsFlow()
    {
        var h = W.Run(ComputeAsync().AsFlow().ToFlowTask());
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(h.Result, Is.EqualTo(42));
    }

    static async Awaitable<int> OnBackgroundThreadAsync()
    {
        await Awaitable.BackgroundThreadAsync();
        return Environment.CurrentManagedThreadId;
    }

    [UnityTest]
    public IEnumerator AwaitableCompletingOnABackgroundThreadResumesOnTheWorldThread()
    {
        var main = Environment.CurrentManagedThreadId;
        int background = 0, resumedOn = 0;

        async FlowTask F()
        {
            background = await OnBackgroundThreadAsync().AsFlow();
            resumedOn = Environment.CurrentManagedThreadId;
        }

        var h = W.Run(F());
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(background, Is.Not.EqualTo(main));
        Assert.That(resumedOn, Is.EqualTo(main), "completion resumes on the World thread");
    }

    /// <summary>True once the awaitable completed; Unity detaches (recycles) an Awaitable after its result was consumed.</summary>
    static bool IsFinished(Awaitable awaitable)
    {
        try
        {
            return awaitable.IsCompleted;
        }
        catch (InvalidOperationException) // "Awaitable is in detached state": completed and consumed by the bridge
        {
            return true;
        }
    }

    [UnityTest]
    public IEnumerator CancelingTheScopeCancelsTheAwaitable()
    {
        var canceled = new AwaitableCompletionSource();
        var control = new AwaitableCompletionSource();
        var target = canceled.Awaitable;
        var cleaned = false;

        async FlowTask F()
        {
            try
            {
                await target.AsFlow();
            }
            finally
            {
                cleaned = true;
            }
        }

        var h = W.Run(F());
        var hc = W.Run(control.Awaitable.AsFlow());
        yield return null;
        Assert.That(IsFinished(target), Is.False);
        h.Cancel();
        yield return Frames(2);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(cleaned, Is.True);
        // A completion-source awaitable only finishes when someone completes or cancels it: the bridge called Cancel().
        Assert.That(IsFinished(target), Is.True, "Awaitable.Cancel() was called when the scope was canceled");
        Assert.That(IsFinished(control.Awaitable), Is.False, "control: an uncanceled bridge leaves the awaitable pending");
        control.SetResult();
        yield return WaitFor(hc);
        Assert.That(hc.Status, Is.EqualTo(FlowStatus.Succeeded));
    }

    [UnityTest]
    public IEnumerator AwaitableExceptionIsThrownAtTheAwait()
    {
        var source = new AwaitableCompletionSource();
        Exception caught = null;

        async FlowTask F()
        {
            try
            {
                await source.Awaitable.AsFlow();
            }
            catch (InvalidOperationException e)
            {
                caught = e;
            }
        }

        var h = W.Run(F());
        source.SetException(new InvalidOperationException("nope"));
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(caught?.Message, Is.EqualTo("nope"));
    }

    // Task bridges under the real UnitySynchronizationContext: a Task completed on the main thread resumes
    // the flow through the World inbox at the next Tick or flush point, whatever SynchronizationContext is current.
    // The core suite runs with the context cleared (UnityTestEnvironment), so only these tests see Unity's. The bridge
    // puts the completion into the inbox on the thread that completes the Task (a ContinueWith continuation that runs
    // synchronously). An await continuation with ConfigureAwait(false) would be moved to the thread pool under a
    // non-default context, and the resume would come some Ticks or a frame later.

    static void RequireUnitySynchronizationContext() =>
        Assert.That(SynchronizationContext.Current?.GetType().Name, Is.EqualTo("UnitySynchronizationContext"),
            "precondition: the test runs on the main thread under Unity's SynchronizationContext");

    [Test]
    public void TaskCompletedOnTheMainThreadResumesAtTheNextTickUnderUnitySynchronizationContext()
    {
        RequireUnitySynchronizationContext();
        var ticks = new List<int>();
        for (var trial = 0; trial < 20; trial++)
        {
            using var w = new FlowWorld();
            var tcs = new TaskCompletionSource<int>();

            async FlowTask Wait() => await FlowBridge.FromTask(ct => tcs.Task);

            var h = w.Run(Wait());
            w.Tick(1.0 / 60);
            tcs.SetResult(1);

            // A tight loop, as in an editor tool or a fast-forward: Unity does not run its context in between.
            var n = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!h.IsCompleted && clock.ElapsedMilliseconds < 5000)
            {
                w.Tick(1.0 / 60);
                n++;
            }

            ticks.Add(n);
        }

        var all = string.Join(",", ticks);
        Debug.Log("[FlowTask] Ticks until the flow resumed, per trial: " + all);
        Assert.That(ticks, Is.All.EqualTo(1), "Ticks until the flow resumed, per trial: " + all);
    }

    [UnityTest]
    public IEnumerator TaskCompletedInMonoBehaviourUpdateResumesAtTheNextFlushPoint()
    {
        RequireUnitySynchronizationContext();
        var go = new GameObject("TaskCompleter");
        var completer = go.AddComponent<TestTaskCompleter>();
        var trials = new List<string>();
        var late = 0;
        try
        {
            for (var trial = 0; trial < 20; trial++)
            {
                var tcs = new TaskCompletionSource<int>();
                var resumedFrame = -1;
                var resumedAfterPoints = -1L;

                async FlowTask Wait()
                {
                    await FlowBridge.FromTask(ct => tcs.Task);
                    resumedFrame = Time.frameCount;
                    resumedAfterPoints = FlowTaskUnity.TickCount + FlowTaskUnity.FlushCount;
                }

                var h = W.Run(Wait());
                yield return null;
                completer.CompleteNextUpdate = tcs;
                yield return WaitFor(h);
                Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
                // Points passed: Ticks and flushes that ended between the completion and the resume (0: the first one).
                var passed = resumedAfterPoints - completer.CompletedAfterPoints;
                trials.Add("frame " + completer.CompletedFrame + "->" + resumedFrame + " points " + passed);
                if (passed != 0 || resumedFrame != completer.CompletedFrame) late++;
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(go);
        }

        var all = string.Join(", ", trials);
        Debug.Log("[FlowTask] Task completed in Update -> resumed, per trial: " + all);
        Assert.That(late, Is.Zero, "the AfterUpdate flush point of the completing frame resumes the flow: " + all);
    }

    [UnityTest]
    public IEnumerator ButtonClickedSignal()
    {
        var go = new GameObject("OkButton", typeof(RectTransform), typeof(Button));
        var button = go.GetComponent<Button>();
        var clicks = 0;
        EventSignal<FlowUnit> clicked = null;

        async FlowTask F()
        {
            using (var first = button.ClickedSignal())
            {
                await first.Next();
            }

            clicks++;
            clicked = button.ClickedSignal();
            using var sub = clicked.Subscribe(BufferPolicy.Queue(4, BufferOverflow.DropOldest));
            await sub.Next();
            await sub.Next();
            clicks += 2;
        }

        var h = W.Run(F());
        button.onClick.Invoke();
        yield return Frames(2);
        Assert.That(clicks, Is.EqualTo(1));
        button.onClick.Invoke();
        button.onClick.Invoke();
        yield return WaitFor(h);
        UnityEngine.Object.Destroy(go);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(clicks, Is.EqualTo(3));
        Assert.That(clicked.Signal.Name, Is.EqualTo("OkButton.onClick"));
        Assert.That(clicked.IsDisposed, Is.True);
    }
}
