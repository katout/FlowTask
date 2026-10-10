using System;
using System.Text.RegularExpressions;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Katout.FlowTask.Unity.Tests;

/// <summary>PlayerLoop integration of the default World (its Tick and flush points).</summary>
public class PlayerLoopTests
{
    [TearDown]
    public void RestoreDefaults()
    {
        if (!FlowTaskUnity.IsInstalled) FlowTaskUnity.Initialize();
        else FlowTaskUnity.Configure(new FlowTaskSettings());
        Time.timeScale = 1;
    }

    [UnityTest]
    public IEnumerator DefaultWorldIsCreatedAtStartupAndTickedEveryFrame()
    {
        Assert.That(FlowTaskUnity.IsInstalled, Is.True);
        Assert.That(W.OnUnhandledException, Is.Not.Null, "reports are routed to the Unity console");
        var ticks = FlowTaskUnity.TickCount;
        var flushes = FlowTaskUnity.FlushCount;
        var unscaled = W.UnscaledClock.Time;
        var frame = Time.frameCount;
        yield return Frames(5);
        var frames = Time.frameCount - frame;
        Assert.That(FlowTaskUnity.TickCount - ticks, Is.EqualTo(frames), "one Tick per frame");
        Assert.That(FlowTaskUnity.FlushCount - flushes, Is.EqualTo(2 * frames), "two default flush points per frame");
        Assert.That(W.UnscaledClock.Time, Is.GreaterThan(unscaled));
    }

    [Test]
    public void SystemsAreInstalledAtTheDefaultPoints()
    {
        var d = FlowTaskUnity.DescribeInstalledSystems();
        Debug.Log("[FlowTask] installed PlayerLoop systems:\n" + d);
        Assert.That(d, Does.Contain("Update: FlowTaskTick (after"));
        Assert.That(d, Does.Contain("before ScriptRunBehaviourUpdate)"));
        Assert.That(d, Does.Contain("Update: FlowTaskFlushAfterUpdate"));
        Assert.That(d, Does.Contain("PreLateUpdate: FlowTaskFlushAfterLateUpdate"));
        Assert.That(d, Does.Not.Contain("EarlyUpdate:"));
        Assert.That(Regex.Matches(d, "FlowTaskTick").Count, Is.EqualTo(1));
    }

    [UnityTest]
    public IEnumerator WaitForSecondsAdvancesWithUnityTime()
    {
        var h = W.Run(FlowTask.WaitForSeconds(0.1));
        var start = Time.realtimeSinceStartup;
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(Time.realtimeSinceStartup - start, Is.GreaterThanOrEqualTo(0.09f));
    }

    [UnityTest]
    public IEnumerator EmitFromMonoBehaviourUpdateResumesInTheSameFrame()
    {
        var go = new GameObject("Emitter");
        var emitter = go.AddComponent<TestEmitter>();
        var resumedFrame = -1;

        async FlowTask Waiter()
        {
            await emitter.Signal.Next();
            resumedFrame = Time.frameCount;
        }

        var h = W.Run(Waiter());
        yield return null;
        emitter.EmitNextUpdate = true;
        yield return WaitFor(h);
        UnityEngine.Object.Destroy(go);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(resumedFrame, Is.EqualTo(emitter.EmittedFrame), "the AfterUpdate flush point resumes the receiver in the emitting frame");
    }

    [UnityTest]
    public IEnumerator WithoutFlushPointsTheReceiverResumesAtTheNextTick()
    {
        FlowTaskUnity.Configure(new FlowTaskSettings { FlushPoints = FlowFlushPoints.None });
        var go = new GameObject("Emitter");
        var emitter = go.AddComponent<TestEmitter>();
        var resumedFrame = -1;

        async FlowTask Waiter()
        {
            await emitter.Signal.Next();
            resumedFrame = Time.frameCount;
        }

        var h = W.Run(Waiter());
        yield return null;
        emitter.EmitNextUpdate = true;
        yield return WaitFor(h);
        UnityEngine.Object.Destroy(go);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(resumedFrame, Is.EqualTo(emitter.EmittedFrame + 1));
    }

    [UnityTest]
    public IEnumerator TimeScaleZeroStopsTheDefaultClockButNotTheUnscaledClock()
    {
        Time.timeScale = 0;
        var game = W.Run(FlowTask.WaitForSeconds(0.05));
        var unscaled = W.Run(FlowTask.WaitForSeconds(0.05, W.UnscaledClock));
        var start = Time.realtimeSinceStartup;
        while (!unscaled.IsCompleted && Time.realtimeSinceStartup - start < 2f) yield return null;
        yield return Frames(3);
        Assert.That(unscaled.Status, Is.EqualTo(FlowStatus.Succeeded), "UnscaledClock ignores Time.timeScale");
        Assert.That(game.IsCompleted, Is.False, "DefaultClock follows Time.timeScale");
        Assert.That(W.DefaultClock.TimeScale, Is.EqualTo(0));
        game.Cancel();
    }

    // Clock.TimeScale rejects a DefaultClock scale whose product with a descendant clock's scale overflows.
    // RunTick logs the rejection and still ticks the World, with DefaultClock at its previous scale.
    [UnityTest]
    public IEnumerator ARejectedDefaultClockScaleIsLoggedAndTheWorldStillTicks()
    {
        async FlowTask HoldAHugeClock()
        {
            // Removed when this scope ends. Paused, so its time stays finite while it exists.
            var huge = Flow.CreateClock("huge", W.DefaultClock);
            huge.TimeScale = double.MaxValue; // finite while DefaultClock.TimeScale is 1, infinite above it
            using var pause = huge.Pause();
            await FlowTask.WaitForSeconds(3600, W.UnscaledClock);
        }

        var rejected = 0;
        void CountRejections(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Exception && message.Contains("TimeScale of clock 'Default'")) rejected++;
        }

        var holder = W.Run(HoldAHugeClock());
        var ticks = FlowTaskUnity.TickCount;
        var time = W.DefaultClock.Time;
        var frame = Time.frameCount;
        long ticked;
        int frames;
        double advanced, scale;
        LogAssert.ignoreFailingMessages = true; // one logged exception per frame, counted below
        Application.logMessageReceived += CountRejections;
        try
        {
            Time.timeScale = 2; // from the coroutine: Unity's deltaTime uses it from the next frame
            yield return Frames(3);
            ticked = FlowTaskUnity.TickCount - ticks;
            frames = Time.frameCount - frame;
            advanced = W.DefaultClock.Time - time;
            scale = W.DefaultClock.TimeScale;
        }
        finally
        {
            Time.timeScale = 1;
            Application.logMessageReceived -= CountRejections;
            holder.Cancel();
        }

        yield return WaitFor(holder);
        Assert.That(rejected, Is.GreaterThan(0), "the rejected scale is logged");
        Assert.That(ticked, Is.EqualTo(frames), "the Tick runs on the frames whose scale was rejected");
        Assert.That(advanced, Is.GreaterThan(0));
        Assert.That(scale, Is.EqualTo(1), "DefaultClock keeps its previous scale");
        Assert.That(holder.Status, Is.EqualTo(FlowStatus.Canceled));
    }

    // DefaultClock advances by Time.deltaTime every frame (docs/en/unity/setup.md), including the frames where Unity's value
    // is not unscaledDeltaTime x timeScale: the provisional first frames and a timeScale changed in FixedUpdate.

    [Test]
    public void DefaultClockScaleFollowsTimeDeltaTimeWhereItIsNotUnscaledTimesTimeScale()
    {
        // (unscaled dt the World ticks by, Time.deltaTime, Time.timeScale), values measured in 6000.3.7f1.
        Assert.That(FlowTaskUnity.DefaultClockScale(0.038593, 0.02, 1), Is.EqualTo(0.02 / 0.038593), "provisional first frame (player)");
        Assert.That(FlowTaskUnity.DefaultClockScale(1.0 / 3, 0.02, 1), Is.EqualTo(0.06).Within(1e-12), "provisional first frame (Editor, dt clamped)");
        Assert.That(FlowTaskUnity.DefaultClockScale(0.01665, 0.01665, 0.5), Is.EqualTo(1), "timeScale 1 -> 0.5 in FixedUpdate");
        Assert.That(FlowTaskUnity.DefaultClockScale(0.1, 0.1, 0), Is.EqualTo(1), "timeScale 1 -> 0 in FixedUpdate");
        Assert.That(FlowTaskUnity.DefaultClockScale(0.000395, 0.000395, 1), Is.EqualTo(1), "steady state: exactly Time.timeScale");
        Assert.That(FlowTaskUnity.DefaultClockScale(0.016, 0.008, 0.5), Is.EqualTo(0.5));
        Assert.That(FlowTaskUnity.DefaultClockScale(0.01665f, 0.01665f * 0.3f, 0.3f), Is.EqualTo((double)0.3f), "float rounding is not a mismatch");
        // At a high timeScale the rounding of Time.deltaTime exceeds 1e-6 s (here 1.5e-6 s on a clamped frame) and is
        // still not a mismatch; a timeScale changed in FixedUpdate still is.
        Assert.That(FlowTaskUnity.DefaultClockScale(1f / 3, (float)(1f / 3 * 100f), 100), Is.EqualTo(100), "timeScale 100: float rounding");
        Assert.That(FlowTaskUnity.DefaultClockScale(0.333f, (float)(0.333f * 100f), 100), Is.EqualTo(100), "timeScale 100: float rounding");
        Assert.That(FlowTaskUnity.DefaultClockScale(0.2, 20, 50), Is.EqualTo(100), "timeScale 100 -> 50 in FixedUpdate");
        Assert.That(FlowTaskUnity.DefaultClockScale(0.02, 0, 0), Is.EqualTo(0));
        Assert.That(FlowTaskUnity.DefaultClockScale(0, 0.02, 1), Is.EqualTo(1), "no time passed: nothing to match");
    }

    [UnityTest]
    public IEnumerator DefaultClockAdvancesByTimeDeltaTimeEveryFrame()
    {
        var go = new GameObject("DeltaTimeProbe");
        var probe = go.AddComponent<DeltaTimeProbe>();
        try
        {
            foreach (var scale in new[] { 1f, 0.5f, 0f, 2f })
            {
                Time.timeScale = scale; // from the coroutine, i.e. after Update: Unity uses it from the next frame
                yield return Frames(15);
            }

            Time.timeScale = 1;
            // Changed in FixedUpdate: after Unity fixed the frame's Time.deltaTime, before the Tick of that frame.
            foreach (var scale in new[] { 0.5f, 1f, 0.25f, 0f })
            {
                probe.TimeScaleForNextFixedUpdate = scale;
                var start = Time.realtimeSinceStartup;
                while (probe.TimeScaleForNextFixedUpdate != null && Time.realtimeSinceStartup - start < 5f) yield return null;
                yield return Frames(3);
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(go);
        }

        var report = DeltaTimeSample.Describe(probe.Samples);
        Assert.That(probe.FixedUpdateChanges, Is.EqualTo(4), "every change was made in a FixedUpdate\n" + report);
        Assert.That(probe.Samples.FindAll(s => s.TimeScaleChangedInFixedUpdate).Count, Is.EqualTo(4), report);
        Assert.That(probe.Samples.Count, Is.GreaterThanOrEqualTo(60), report);
        Assert.That(probe.Samples.FindAll(s => !s.Matches), Is.Empty, "frames where DefaultClock.DeltaTime != Time.deltaTime:\n" + report);
    }

    [UnityTest]
    public IEnumerator DefaultClockMatchesTimeDeltaTimeFromTheFirstFrame()
    {
        // StartupDeltaTimeProbe was created before the first scene loaded and recorded the first frames of this Play
        // Mode session (or test player), where Unity reports a provisional Time.deltaTime.
        var start = Time.realtimeSinceStartup;
        while (!StartupDeltaTimeProbe.Done && Time.realtimeSinceStartup - start < 5f) yield return null;
        var samples = StartupDeltaTimeProbe.Samples;
        var report = DeltaTimeSample.Describe(samples);
        Debug.Log("[FlowTask] first frames:\n" + report);
        Assert.That(StartupDeltaTimeProbe.Done, Is.True);
        Assert.That(samples.Count, Is.GreaterThanOrEqualTo(2), report);
        // The provisional deltaTime is on frames 1 and 2: a probe that started recording later checks nothing.
        Assert.That(samples[0].Frame, Is.LessThanOrEqualTo(2), "the probe missed the first frames:\n" + report);
        Assert.That(samples.FindAll(s => !s.Matches), Is.Empty, "frames where DefaultClock.DeltaTime != Time.deltaTime:\n" + report);
    }

    [UnityTest]
    public IEnumerator ConfigureMovesTheTickAndFlushPoints()
    {
        FlowTaskUnity.Configure(new FlowTaskSettings { FlushPoints = FlowFlushPoints.None });
        var d = FlowTaskUnity.DescribeInstalledSystems();
        Assert.That(d, Does.Contain("Update: FlowTaskTick"));
        Assert.That(d, Does.Contain("before ScriptRunBehaviourUpdate)"));
        Assert.That(d, Does.Not.Contain("Flush"));
        var ticks = FlowTaskUnity.TickCount;
        yield return Frames(2);
        Assert.That(FlowTaskUnity.TickCount, Is.GreaterThan(ticks));

        FlowTaskUnity.Configure(new FlowTaskSettings { AutoTick = false, FlushPoints = FlowFlushPoints.All });
        d = FlowTaskUnity.DescribeInstalledSystems();
        Assert.That(d, Does.Not.Contain("FlowTaskTick"));
        Assert.That(d, Does.Contain("EarlyUpdate: FlowTaskFlushAfterEarlyUpdate"));
        Assert.That(d, Does.Contain("FixedUpdate: FlowTaskFlushAfterFixedUpdate"));
        Assert.That(d, Does.Contain("PostLateUpdate: FlowTaskFlushEndOfFrame"));
        yield return null; // PlayerLoop.SetPlayerLoop takes effect from the next frame
        ticks = FlowTaskUnity.TickCount;
        yield return Frames(2);
        Assert.That(FlowTaskUnity.TickCount, Is.EqualTo(ticks), "Manual: no automatic Tick");

    }

    [UnityTest]
    public IEnumerator UnhandledExceptionIsLoggedWithTheScopePath()
    {
        async FlowTask Exploding()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("boom-42");
        }

        LogAssert.Expect(LogType.Exception, new Regex("boom-42"));
        var h = W.Run(Flow.Named("Exploder", Exploding()));
        yield return WaitFor(h);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Faulted));
    }

    [Test]
        public void UndeliveredReportIsLoggedAsWarning()
    {
            // FlowExceptionKind.Undelivered (an exception no receiver could take) happens in normal play: the default handler logs
        // it as a warning with the scope path and the exception. A logged error or exception would fail the test.
            LogAssert.Expect(LogType.Warning, new Regex(@"\[FlowTask\] Undelivered at 'Game > Poller'.*net-down-7"));
            W.OnUnhandledException(new FlowExceptionInfo(new System.IO.IOException("net-down-7"), "Game > Poller", FlowExceptionKind.Undelivered));
        LogAssert.NoUnexpectedReceived();
    }

    [UnityTest]
    public IEnumerator CustomExceptionHandlerReceivesTheScopePath()
    {
        FlowExceptionInfo captured = null;
        FlowTaskUnity.Configure(new FlowTaskSettings { OnUnhandledException = p => captured = p });

        async FlowTask Exploding()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("custom");
        }

        var h = W.Run(Flow.Named("Exploder", Exploding()));
        yield return WaitFor(h);
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured.ScopePath, Is.EqualTo("Exploder"));
        Assert.That(captured.Exception.Message, Is.EqualTo("custom"));
        var wrapped = new FlowTaskUnhandledException(captured);
        Assert.That(wrapped.Message, Does.Contain("'Exploder'"));
        Assert.That(wrapped.InnerException, Is.SameAs(captured.Exception));
    }

    [UnityTest]
    public IEnumerator ShutdownUnwindsFlowsAndRemovesTheSystems()
    {
        var cleaned = false;

        async FlowTask Forever()
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

        var old = W;
        old.Run(Forever());
        yield return null;
        FlowTaskUnity.Shutdown();
        Assert.That(cleaned, Is.True, "FlowWorld.Dispose unwinds every flow");
        Assert.That(old.IsDisposed, Is.True);
        Assert.That(FlowTaskUnity.IsInstalled, Is.False);
        var noWorld = Assert.Throws<InvalidOperationException>(() => _ = FlowTaskUnity.World);
        Assert.That(noWorld.Message, Does.StartWith("No FlowTask World"));
        Assert.That(FlowTaskUnity.DescribeInstalledSystems(), Is.Empty);
        FlowTaskUnity.Shutdown(); // idempotent

        var fresh = FlowTaskUnity.Initialize();
        Assert.That(fresh, Is.Not.SameAs(old));
        var ticks = FlowTaskUnity.TickCount;
        yield return Frames(2);
        Assert.That(FlowTaskUnity.TickCount, Is.GreaterThan(ticks));
    }

    [UnityTest]
    public IEnumerator StartupHookRunningAgainStartsFromAFreshWorld()
    {
        // Entering Play Mode with domain reload disabled re-runs the SubsystemRegistration hook while static state
        // survives: the previous World must be disposed and the systems must not be duplicated.
        var old = W;
        var h = old.Run(FlowTask.Never());
        FlowTaskUnity.OnSubsystemRegistration();
        Assert.That(old.IsDisposed, Is.True);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Canceled));
        Assert.That(W, Is.Not.Null.And.Not.SameAs(old));
        Assert.That(Regex.Matches(FlowTaskUnity.DescribeInstalledSystems(), "FlowTaskTick").Count, Is.EqualTo(1));
        yield return Frames(2);
        Assert.That(FlowTaskUnity.TickCount, Is.GreaterThan(0));
    }

#if UNITY_EDITOR
    // Scripts recompiled during Play Mode (Script Changes While Playing = Recompile And Continue Playing): the reload
    // drops the World and the integration does not recover; it warns once per lost World and names the cause
    // (docs/en/unity/setup.md).

    [Test]
    public void ReloadDuringPlayModeWarnsAndIsRemembered()
    {
        var warnings = 0;
        void CountWarnings(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Warning) warnings++;
        }

        try
        {
            LogAssert.Expect(LogType.Warning, new Regex("Script Changes While Playing"));
            FlowTaskUnity.OnBeforeAssemblyReload();
            Assert.That(SessionState.GetBool(FlowTaskUnity.ScriptReloadKey, false), Is.True);

            // After the reload there is no World: a second reload in the same session neither warns nor records.
            // LogAssert does not fail on an unexpected warning, so the warnings are counted.
            SessionState.EraseBool(FlowTaskUnity.ScriptReloadKey);
            FlowTaskUnity.Shutdown();
            Application.logMessageReceived += CountWarnings;
            FlowTaskUnity.OnBeforeAssemblyReload();
            Application.logMessageReceived -= CountWarnings;
            Assert.That(warnings, Is.EqualTo(0), "no second warning");
            Assert.That(SessionState.GetBool(FlowTaskUnity.ScriptReloadKey, false), Is.False);
        }
        finally
        {
            Application.logMessageReceived -= CountWarnings;
            SessionState.EraseBool(FlowTaskUnity.ScriptReloadKey);
        }
    }

    [Test]
    public void NoWorldMessageNamesTheRecompileAfterAReloadDuringPlay()
    {
        var go = new GameObject("NoWorld");
        try
        {
            SessionState.SetBool(FlowTaskUnity.ScriptReloadKey, true);
            FlowTaskUnity.Shutdown();
            var ex = Assert.Throws<InvalidOperationException>(() => go.RunWhileActive(FlowTask.CompletedTask));
            Assert.That(ex.Message, Does.StartWith("No FlowTask World"));
            Assert.That(ex.Message, Does.Contain("recompiled during this Play Mode session"));
            Assert.That(ex.Message, Does.Contain("FlowTaskUnity.Initialize()"));
            Assert.That(ex.Message, Does.Contain("Script Changes While Playing"));
            Assert.That(ex.Message, Does.Not.Contain("Play Mode only"));
        }
        finally
        {
            SessionState.EraseBool(FlowTaskUnity.ScriptReloadKey);
            UnityEngine.Object.Destroy(go);
        }
    }

    [Test]
    public void NoWorldMessageWithoutAReloadNamesShutdownOrAutoInstall()
    {
        var go = new GameObject("NoWorld");
        try
        {
            FlowTaskUnity.Shutdown();
            var ex = Assert.Throws<InvalidOperationException>(() => go.RunWhileActive(FlowTask.CompletedTask));
            Assert.That(ex.Message, Does.StartWith("No FlowTask World"));
            Assert.That(ex.Message, Does.Contain("FlowTaskUnity.Initialize()"));
            Assert.That(ex.Message, Does.Contain("AutoInstall"));
            Assert.That(ex.Message, Does.Not.Contain("recompiled"));
        }
        finally
        {
            UnityEngine.Object.Destroy(go);
        }
    }

    [Test]
    public void InitializeAfterAReloadForgetsTheReload()
    {
        // A reload dropped the World, the game started FlowTask again (Initialize, e.g. from OnEnable, which Unity
        // calls again after the reload) and later shut it down: the reload is no longer the reason.
        var go = new GameObject("NoWorld");
        try
        {
            SessionState.SetBool(FlowTaskUnity.ScriptReloadKey, true);
            FlowTaskUnity.Shutdown();
            FlowTaskUnity.Initialize();
            Assert.That(SessionState.GetBool(FlowTaskUnity.ScriptReloadKey, false), Is.False);
            FlowTaskUnity.Shutdown();
            var ex = Assert.Throws<InvalidOperationException>(() => go.RunWhileActive(FlowTask.CompletedTask));
            Assert.That(ex.Message, Does.Contain("FlowTaskUnity.Initialize()"));
            Assert.That(ex.Message, Does.Not.Contain("recompiled"));
        }
        finally
        {
            SessionState.EraseBool(FlowTaskUnity.ScriptReloadKey);
            UnityEngine.Object.Destroy(go);
        }
    }
#endif

    [Test]
    public void AdoptedWorldIsNotDisposedByShutdown()
    {
        var mine = new FlowWorld();
        try
        {
            FlowTaskUnity.Initialize(new FlowTaskSettings(), mine);
            Assert.That(FlowTaskUnity.World, Is.SameAs(mine));
            FlowTaskUnity.Shutdown();
            Assert.That(mine.IsDisposed, Is.False);
            Assert.That(mine.OnUnhandledException, Is.Null);
        }
        finally
        {
            mine.Dispose();
        }
    }

    [Test]
    public void AdoptedWorldKeepsItsOwnExceptionHandler()
    {
        var mine = new FlowWorld();
        Action<FlowExceptionInfo> own = _ => { };
        mine.OnUnhandledException = own;
        try
        {
            FlowTaskUnity.Initialize(new FlowTaskSettings(), mine);
            Assert.That(mine.OnUnhandledException, Is.SameAs(own));
            FlowTaskUnity.Shutdown();
            Assert.That(mine.OnUnhandledException, Is.SameAs(own));
        }
        finally
        {
            mine.Dispose();
        }
    }

    [Test]
    public void SettingsReturnsACopy()
    {
        var copy = FlowTaskUnity.Settings;
        copy.FlushPoints = FlowFlushPoints.None;
        Assert.That(FlowTaskUnity.Settings.FlushPoints, Is.EqualTo(FlowFlushPoints.Default));
    }

    [Test]
    public void TheSettingsAssetConfiguresTheWorldCreatedAtStartup()
    {
        FlowTaskUnity.OnSubsystemRegistration(); // a fresh start: the startup World, nothing set from code
        var startup = W;
        FlowTaskUnity.ApplySettingsAsset(new FlowTaskSettings { FlushPoints = FlowFlushPoints.None });
        Assert.That(W, Is.SameAs(startup));
        Assert.That(FlowTaskUnity.Settings.FlushPoints, Is.EqualTo(FlowFlushPoints.None));

        FlowTaskUnity.OnSubsystemRegistration();
        FlowTaskUnity.ApplySettingsAsset(new FlowTaskSettings { AutoInstall = false });
        Assert.That(FlowTaskUnity.IsInstalled, Is.False, "AutoInstall = false removes the World created at startup");
        Assert.Throws<InvalidOperationException>(() => _ = FlowTaskUnity.World);
    }

    [Test]
    public void SettingsAppliedFromCodeBeforeTheFirstSceneWinOverTheAsset()
    {
        FlowTaskUnity.OnSubsystemRegistration();
        Action<FlowExceptionInfo> handler = _ => { };
        FlowTaskUnity.Configure(new FlowTaskSettings { FlushPoints = FlowFlushPoints.All, OnUnhandledException = handler });
        FlowTaskUnity.ApplySettingsAsset(new FlowTaskSettings { FlushPoints = FlowFlushPoints.None });
        Assert.That(FlowTaskUnity.Settings.FlushPoints, Is.EqualTo(FlowFlushPoints.All));
        Assert.That(FlowTaskUnity.Settings.OnUnhandledException, Is.SameAs(handler));

        // A World installed from code stays when the asset turns AutoInstall off.
        FlowTaskUnity.OnSubsystemRegistration();
        var mine = new FlowWorld();
        try
        {
            FlowTaskUnity.Initialize(new FlowTaskSettings(), mine);
            FlowTaskUnity.ApplySettingsAsset(new FlowTaskSettings { AutoInstall = false });
            Assert.That(FlowTaskUnity.World, Is.SameAs(mine));
            Assert.That(FlowTaskUnity.IsInstalled, Is.True);
            Assert.That(mine.IsDisposed, Is.False);
        }
        finally
        {
            FlowTaskUnity.Shutdown();
            mine.Dispose();
        }
    }

    [Test]
    public void TheRegistryListsWorldsReadOnly()
    {
        var worlds = FlowWorldRegistry.Worlds;
        Assert.That(worlds, Is.Not.InstanceOf<List<FlowWorld>>());
        Assert.That(worlds[0], Is.SameAs(W));
    }
}
