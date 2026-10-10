using System;
using System.Collections.Generic;
using System.Linq;
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Katout.FlowTask.Samples.Tests;
using Godot;

/// <summary>
/// Headless smoke test (tools/godot/run-smoke.ps1). Runs FlowTask.Godot checks as flows on the autoload's World over
/// real engine frames, then the core test suite inside the Godot process (NUnit, see CoreSuiteRunner), prints
/// PASS/FAIL lines and quits with exit code 0 (all passed) or 1.
/// </summary>
public partial class SmokeMain : Node
{
    const ulong TimeoutMsec = 60_000;

    readonly List<FlowExceptionInfo> _rootExceptions = new();
    int _pass;
    int _fail;
    int _mainThread;
    ulong _startMsec;
    bool _finished;
    FlowHandle _suite;

    public override void _Ready()
    {
        _mainThread = System.Environment.CurrentManagedThreadId;
        _startMsec = Time.GetTicksMsec();
        GD.Print($"[smoke] FlowTask Godot smoke test on Godot {Engine.GetVersionInfo()["string"]}");

        var node = FlowWorldNode.Instance;
        Check("autoload", node is FlowAutoload && node.IsInsideTree() && node.ProcessMode == ProcessModeEnum.Always,
            node == null ? "no FlowWorldNode" : $"{node.GetType().Name} at {node.GetPath()}, ProcessMode={node.ProcessMode}");
        if (node == null)
        {
            Finish();
            return;
        }

        node.UnhandledException += p => _rootExceptions.Add(p);
        FlowWorldNode.Default.Run(ExitProbe());
        _suite = FlowWorldNode.Default.Run(Suite());
    }

    public override void _Process(double delta)
    {
        if (_finished) return;
        if (_suite.IsCompleted)
        {
            if (_suite.Status != FlowStatus.Succeeded) Check("engine suite", false, $"suite ended as {_suite.Status}: {_suite.Exception}");
            Finish();
        }
        else if (Time.GetTicksMsec() - _startMsec > TimeoutMsec)
        {
            Check("engine suite finished in time", false, "timeout\n" + FlowWorldNode.Default.Dump());
            Finish();
        }
    }

    // At quit the main scene exits the tree before the autoload (children exit last-to-first), i.e. before the World is disposed.
    public override void _ExitTree() => GD.Print($"[smoke] main scene _ExitTree (World disposed: {FlowWorldNode.Instance?.World?.IsDisposed ?? true})");

    void Finish()
    {
        _finished = true;
        if (FlowWorldNode.Instance != null)
        {
            GD.Print("[smoke] live scopes after the engine checks (only ExitProbe expected):");
            GD.Print(FlowWorldNode.Default.Dump());
        }

        RunCoreSuite();
        var ok = _fail == 0;
        GD.Print($"[smoke] SMOKE RESULT: {(ok ? "PASS" : "FAIL")} ({_pass} passed, {_fail} failed)");
        GetTree().Quit(ok ? 0 : 1);
    }

    void Check(string name, bool ok, string detail = null)
    {
        if (ok) _pass++;
        else _fail++;
        GD.Print($"[smoke] {(ok ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")")}");
    }

    /// <summary>Stays suspended until FlowWorldNode disposes the World in _ExitTree at quit; proves exit unwinding.</summary>
    static async FlowTask ExitProbe()
    {
        try
        {
            await FlowTask.Never();
        }
        finally
        {
            GD.Print("[smoke] EXIT UNWIND OK (finally ran when FlowWorldNode._ExitTree disposed the World)");
        }
    }

    // ---------------------------------------------------------------------- engine checks

    static Verdict Verify(string detail, params (bool ok, string what)[] checks) => Verdict.Of(detail, checks);

    async FlowTask Step(string name, FlowTask<Verdict> body)
    {
        try
        {
            var v = await body;
            Check(name, v.Failure == null, v.Failure == null ? v.Detail : v.Failure + " | " + v.Detail);
        }
        catch (Exception e) when (e is not FlowCanceledException)
        {
            Check(name, false, "exception: " + e);
        }
    }

    async FlowTask Suite()
    {
        await FlowTask.NextFrame(); // from here on, everything runs inside FlowWorldNode's Tick/flushes
        await Step("wait-for-seconds-on-default-clock", WaitForSecondsOnDefaultClock());
        await Step("next-frame", NextFrame());
        await Step("signal-race", SignalRace());
        await Step("timer-next-signal", TimerNextSignal());
        await Step("signal-bridge-resumes-in-flush", SignalBridgeResumesInFlush());
        await Step("signal-awaiter-asflow", SignalAwaiterAsFlow());
        await Step("typed-signal-bridges", TypedSignalBridges());
        await Step("cancel-runs-finally-and-disconnects", CancelRunsFinallyAndDisconnects());
        await Step("button-pressed-signal", ButtonPressedSignal());
        await Step("caught-exception", CaughtException());
        await Step("root-exception-routed", RootExceptionRouted());
        await Step("owned-by-scope", OwnedByScope());
        await Step("run-while-in-tree", RunWhileInTree());
        await Step("run-while-in-tree-not-in-tree", RunWhileInTreeNotInTree());
        await Step("run-while-in-tree-exit-in-first-step", RunWhileInTreeExitInFirstStep());
        await Step("run-while-in-tree-starts-at-the-root", RunWhileInTreeStartsAtTheRoot());
        await Step("run-while-in-tree-on-a-clock", RunWhileInTreeOnAClock());
        await Step("unscaled-clock-ignores-time-scale", UnscaledClockIgnoresTimeScale());
        await Step("physics-unscaled-wait-while-time-scale-zero", PhysicsUnscaledWaitWhileTimeScaleZero());
        await Step("long-frame-is-clamped", LongFrameIsClamped());
        await Step("rejected-default-clock-scale-still-ticks", RejectedDefaultClockScaleStillTicks());
        await Step("runs-while-tree-paused", RunsWhileTreePaused());
        await Step("pausable-world-skips-paused-time", PausableWorldSkipsPausedTime());
        await Step("physics-world", PhysicsWorld());
        await Step("physics-world-follows-time-scale", PhysicsWorldFollowsTimeScale());
        await Step("physics-world-rejected-scale-still-ticks", PhysicsWorldRejectedScaleStillTicks());
        await Step("physics-cross-world-signal", CrossWorldSignal());
        // The samples (samples/godot, docs/en/godot/samples.md), driven by pretend input (Samples/).
        await Step("confirm-dialog", ConfirmDialogSmoke.Run(this));
        await Step("enemy-ai", EnemySmoke.Run(this));
        await Step("tutorial", TutorialSmoke.Run(this));
        await Step("purchase", ShopSmoke.Run(this));
        await Step("pause", PauseSmoke.Run(this));
        await Step("parallel-loading", LoadingSmoke.Run(this));
    }

    async FlowTask<Verdict> WaitForSecondsOnDefaultClock()
    {
        var world = FlowWorldNode.Default;
        var t0 = world.DefaultClock.Time;
        var f0 = Engine.GetProcessFrames();
        var ms0 = Time.GetTicksMsec();
        await FlowTask.WaitForSeconds(0.25);
        var dt = world.DefaultClock.Time - t0;
        var frames = Engine.GetProcessFrames() - f0;
        var ms = Time.GetTicksMsec() - ms0;
        return Verify($"0.25s = {frames} frames, {ms} ms wall, clock +{dt:0.###}s",
            (dt >= 0.25 - 1e-9, "clock advanced"),
            (frames >= 2, "resumed over several _Process frames"),
            // The first Tick after the wait started adds the time since the previous frame, part of which passed before
            // the wait started, so the wall time can be up to a frame shorter than 0.25s.
            (ms >= 100, "real time elapsed"),
            (System.Environment.CurrentManagedThreadId == _mainThread, "main thread"));
    }

    static async FlowTask<Verdict> NextFrame()
    {
        var f0 = Engine.GetProcessFrames();
        await FlowTask.NextFrame();
        var d1 = Engine.GetProcessFrames() - f0;
        await FlowTask.DelayFrames(3);
        var d3 = Engine.GetProcessFrames() - f0;
        return Verify($"NextFrame +{d1}, DelayFrames(3) +{d3 - d1}", (d1 == 1, "NextFrame = next _Process"), (d3 == 4, "DelayFrames(3)"));
    }

    static async FlowTask<Verdict> SignalRace()
    {
        var hit = new Signal<int>("Hit");

        async FlowTask Emitter()
        {
            await FlowTask.WaitForSeconds(0.05);
            hit.Emit(42);
        }

        _ = Flow.Spawn(Emitter());
        var r = await FlowTask.Race(hit.Next(), FlowTask.WaitForSeconds(5));
        return Verify($"winner {r.Index}", (r.Index == 0, "signal won"), (r.Index == 0 && r.Value0 == 42, "value 42"));
    }

    /// <summary>One emission, disconnected afterwards (also on cancellation): the documented pattern.</summary>
    static async FlowTask<Variant[]> NextSignalOf(GodotObject source, StringName signal)
    {
        using var bridge = source.ToFlowSignalArgs(signal);
        return await bridge.Next();
    }

    Timer NewTimer(double seconds)
    {
        var timer = new Timer { WaitTime = seconds, OneShot = true };
        AddChild(NodeLifetime.Own(timer)); // freed when the current step ends
        return timer;
    }

    async FlowTask<Verdict> TimerNextSignal()
    {
        var timer = NewTimer(0.2);
        timer.Start();
        var ms0 = Time.GetTicksMsec();
        var args = await NextSignalOf(timer, Timer.SignalName.Timeout);
        var ms = Time.GetTicksMsec() - ms0;
        var left = timer.GetSignalConnectionList(Timer.SignalName.Timeout).Count;
        return Verify($"Timer(0.2s) resumed after {ms} ms, {args.Length} args, {left} connections left",
            (ms >= 100, "waited for the timer"), (args.Length == 0, "no args"), (left == 0, "disconnected after the wait"));
    }

    async FlowTask<Verdict> SignalBridgeResumesInFlush()
    {
        var timer = NewTimer(0.05);
        using var timeout = timer.ToFlowSignal(Timer.SignalName.Timeout); // connected first
        var emitFinished = false;
        ulong emitFrame = 0;
        timer.Timeout += () => // connected after the bridge: runs after it in the same emission
        {
            emitFinished = true;
            emitFrame = Engine.GetProcessFrames();
        };
        timer.Start();
        await timeout.Next();
        var sameFrame = Engine.GetProcessFrames() == emitFrame;
        return Verify($"resumed after the emission completed, same frame: {sameFrame} (end-of-frame flush)",
            (emitFinished, "not resumed synchronously inside Godot's emit"),
            (FlowWorld.Current == FlowWorldNode.Default, "resumed by the World"),
            (sameFrame, "late flush resumed it in the emitting frame"),
            (System.Environment.CurrentManagedThreadId == _mainThread, "main thread"));
    }

    async FlowTask<Verdict> SignalAwaiterAsFlow()
    {
        var timer = NewTimer(0.05);
        var awaiter = ToSignal(timer, Timer.SignalName.Timeout);
        var emitFinished = false;
        timer.Timeout += () => emitFinished = true;
        timer.Start();
        var args = await awaiter.AsFlow();

        // Cancellation: the result of a pending awaiter is ignored.
        var timer2 = NewTimer(0.05);
        var late = Flow.Spawn(ToSignal(timer2, Timer.SignalName.Timeout).AsFlow());
        timer2.Start();
        late.Cancel();
        await FlowTask.WaitForSeconds(0.15); // timer2 fires; the canceled bridge must not resume anything
        return Verify($"ToSignal(...).AsFlow() returned {args.Length} args; canceled bridge: {late.Status}",
            (args.Length == 0, "no args"), (emitFinished, "resumed after the emission (in a flush)"),
            (late.Status == FlowStatus.Canceled, "canceled bridge stays canceled"));
    }

    async FlowTask<Verdict> TypedSignalBridges()
    {
        var e = new SmokeEmitter { Name = "Emitter" };
        AddChild(NodeLifetime.Own(e));
        using var hit = e.ToFlowSignal<int>(SmokeEmitter.SignalName.Hit);
        using var raw = e.ToFlowSignalArgs(SmokeEmitter.SignalName.Moved);
        using var any = e.ToFlowSignal(SmokeEmitter.SignalName.Moved);
        using var rawSub = raw.Subscribe(BufferPolicy.Latest);
        using var anySub = any.Subscribe(BufferPolicy.Latest);

        async FlowTask EmitLater()
        {
            await FlowTask.NextFrame();
            e.EmitSignal(SmokeEmitter.SignalName.Hit, 7);
            e.EmitSignal(SmokeEmitter.SignalName.Moved, 3, "right");
        }

        _ = Flow.Spawn(EmitLater());
        var damage = await hit.Next();
        var rw = await rawSub.Next();
        await anySub.Next();
        var dump = FlowWorldNode.Default.Dump();
        return Verify($"Hit({damage}), raw[{string.Join(", ", rw.Select(v => v.ToString()))}]",
            (damage == 7, "ToFlowSignal<int>"),
            (rw.Length == 2 && rw[0].AsInt32() == 3 && rw[1].AsString() == "right", "ToFlowSignalArgs -> Variant[]"),
            (dump.Contains("TypedSignalBridges"), "scope visible in FlowWorld.Dump()"));
    }

    async FlowTask<Verdict> CancelRunsFinallyAndDisconnects()
    {
        var timer = NewTimer(30);
        timer.Start();
        int Connections() => timer.GetSignalConnectionList(Timer.SignalName.Timeout).Count;
        var before = Connections();
        var during = -1;
        var finallyRan = false;

        async FlowTask Waiter()
        {
            try
            {
                using var t = timer.ToFlowSignal(Timer.SignalName.Timeout);
                during = Connections();
                await t.Next();
            }
            finally
            {
                finallyRan = true;
            }
        }

        var h = Flow.Spawn(Waiter());
        var n = Flow.Spawn(NextSignalOf(timer, Timer.SignalName.Timeout));
        await FlowTask.NextFrame();
        var both = Connections();
        h.Cancel();
        n.Cancel();
        var after = Connections();
        return Verify($"connections: before {before}, ToFlowSignal {during}, +one-shot {both}, after cancel {after}",
            (during == before + 1, "ToFlowSignal connected"),
            (both == before + 2, "one-shot wait connected"),
            (finallyRan, "finally ran"),
            (after == before, "both disconnected on cancel"),
            (h.Status == FlowStatus.Canceled && n.Status == FlowStatus.Canceled, "canceled"));
    }

    async FlowTask<Verdict> ButtonPressedSignal()
    {
        var button = new Button { Name = "OkButton", Text = "OK" };
        AddChild(NodeLifetime.Own(button));
        int Connections() => button.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count;
        var baseline = Connections();
        var during = -1;

        async FlowTask<bool> Confirm()
        {
            using var ok = button.PressedSignal(); // disconnected when Confirm's scope ends
            during = Connections();
            var r = await FlowTask.Race(ok.Next(), FlowTask.WaitForSeconds(2));
            return r.Index == 0;
        }

        // Emitted by Godot outside the World (deferred call at the end of the frame), as a real click would be.
        button.CallDeferred(GodotObject.MethodName.EmitSignal, BaseButton.SignalName.Pressed);
        var pressed = await Confirm();
        var after = Connections();
        return Verify($"pressed={pressed}, connections {baseline} -> {during} -> {after}",
            (pressed, "press won the race"), (during == baseline + 1, "connected while waiting"), (after == baseline, "disconnected at scope end"));
    }

    async FlowTask<Verdict> CaughtException()
    {
        var before = _rootExceptions.Count;

        static async FlowTask<int> MiniGame()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("boom");
        }

        Exception caught = null;
        try
        {
            await MiniGame();
        }
        catch (InvalidOperationException e)
        {
            caught = e;
        }

        return Verify($"caught {caught?.Message}", (caught != null, "thrown at the await"), (_rootExceptions.Count == before, "nothing reached the root"));
    }

    async FlowTask<Verdict> RootExceptionRouted()
    {
        var before = _rootExceptions.Count;

        static async FlowTask DeliberateRootException()
        {
            await FlowTask.NextFrame();
            throw new InvalidOperationException("deliberate unhandled exception (expected)");
        }

        GD.Print("[smoke] (the next ERROR line is expected: a deliberate unhandled exception routed to GD.PushError)");
        var h = FlowWorldNode.Default.Run(DeliberateRootException());
        await FlowTask.WaitUntil(() => h.IsCompleted);
        var p = _rootExceptions.Count > before ? _rootExceptions[^1] : null;
        return Verify($"UnhandledException event: {p?.Kind} at '{p?.ScopePath}'",
            (_rootExceptions.Count == before + 1, "one report reached OnUnhandledException"), (p?.ScopePath == "DeliberateRootException", "scope path"),
            (h.Status == FlowStatus.Faulted, "handle faulted"));
    }

    async FlowTask<Verdict> OwnedByScope()
    {
        Node n = null;

        async FlowTask Scope()
        {
            n = new Node { Name = "Owned" };
            AddChild(NodeLifetime.Own(n));
            await FlowTask.NextFrame();
        }

        await Scope();
        var queued = GodotObject.IsInstanceValid(n) && n.IsQueuedForDeletion();
        await FlowTask.NextFrame();
        var freed = !GodotObject.IsInstanceValid(n);
        return Verify($"queued at scope end: {queued}, freed next frame: {freed}", (queued, "QueueFree at scope end"), (freed, "freed"));
    }

    async FlowTask<Verdict> RunWhileInTree()
    {
        var enemy = new Node { Name = "Enemy" };
        AddChild(enemy);
        var ticks = 0;
        var finallyInTree = false;
        var finallyRan = false;
        ulong freeFrame = 0, finallyFrame = 0;

        async FlowTask EnemyAI()
        {
            try
            {
                while (true)
                {
                    ticks++;
                    await FlowTask.NextFrame();
                }
            }
            finally
            {
                finallyRan = true;
                finallyFrame = Engine.GetProcessFrames();
                finallyInTree = GodotObject.IsInstanceValid(enemy) && enemy.IsInsideTree();
            }
        }

        var h = enemy.RunWhileInTree(EnemyAI()); // starts now, as a root flow, and ends when the node exits
        await FlowTask.WaitForSeconds(0.05);
        var dump = FlowWorldNode.Default.Dump();
        freeFrame = Engine.GetProcessFrames();
        enemy.QueueFree();
        await FlowTask.WaitUntil(() => h.IsCompleted);

        var other = new Node { Name = "Other" };
        AddChild(NodeLifetime.Own(other));
        var completed = await NodeLifetime.WhileInTree(other, FlowTask.WaitForSeconds(0.01));
        var value = await NodeLifetime.WhileInTree(other, Answer());

        return Verify($"ticks {ticks}, result {h.Result}, finally in tree: {finallyInTree}, frame {finallyFrame} (freed in {freeFrame}); completed={completed}, value={value}",
            (ticks > 1, "ran while in tree"), (h.Status == FlowStatus.Succeeded && !h.Result, "false after exit"),
            (finallyRan, "unwound on tree_exiting"), (finallyInTree, "unwound while the node was still in the tree"),
            (finallyFrame == freeFrame, "unwound in the same frame"),
            (dump.Contains("WhileInTree(Enemy) (scope)") && dump.Contains("EnemyAI (scope)"), "named WhileInTree(Enemy) in the dump"),
            (completed, "true when the task completes"), (value.Completed && value.Value == 42, "(true, result)"));
    }

    static async FlowTask<int> Answer()
    {
        await FlowTask.NextFrame();
        return 42;
    }

    static async FlowTask<Verdict> RunWhileInTreeNotInTree()
    {
        var orphan = new Node { Name = "Orphan" };
        var ran = false;

        async FlowTask Body()
        {
            ran = true;
            await FlowTask.NextFrame();
        }

        var r = await NodeLifetime.WhileInTree(orphan, Body());
        orphan.Free();
        return Verify($"result {r}, body ran: {ran}", (!r, "false"), (!ran, "task not run"));
    }

    /// <summary>
    /// A task whose first step (run inside RunWhileInTree) removes the owner from the tree is still ended by that exit:
    /// the wait for tree_exiting is in place before the task starts.
    /// </summary>
    async FlowTask<Verdict> RunWhileInTreeExitInFirstStep()
    {
        var owner = new Node { Name = "LeavesAtOnce" };
        AddChild(NodeLifetime.Own(owner));
        var cleaned = false;

        async FlowTask Body()
        {
            try
            {
                RemoveChild(owner);
                await FlowTask.Never();
            }
            finally
            {
                cleaned = true;
            }
        }

        var h = owner.RunWhileInTree(Body());
        var r = await FlowTask.Race(FlowTask.WaitUntil(() => h.IsCompleted), FlowTask.WaitForSeconds(1));
        return Verify($"ended: {r.Index == 0}, {h.Status} ({(h.Status == FlowStatus.Succeeded ? h.Result.ToString() : "-")}), finally ran: {cleaned}",
            (r.Index == 0 && h.Status == FlowStatus.Succeeded && !h.Result, "false after the exit in the first step"),
            (cleaned, "unwound"));
    }

    /// <summary>
    /// RunWhileInTree starts a root flow wherever it is called from: the node owns the flow, so a flow started from flow
    /// code does not end with the calling scope, only when the node leaves the tree.
    /// </summary>
    async FlowTask<Verdict> RunWhileInTreeStartsAtTheRoot()
    {
        var owner = new Node { Name = "Owner" };
        AddChild(NodeLifetime.Own(owner));
        FlowHandle<bool> fromFlow = default;

        async FlowTask StartsFromFlowCode()
        {
            fromFlow = owner.RunWhileInTree(FlowTask.Never());
            await FlowTask.NextFrame();
        }

        await StartsFromFlowCode(); // the scope that called RunWhileInTree has ended; the flow has not
        await FlowTask.NextFrame();
        var fromFlowRunning = fromFlow.Status == FlowStatus.Running;

        FlowHandle<bool> root = default;
        var rootStarted = false;
        var startedInFlow = true;
        Callable.From(() =>
        {
            startedInFlow = Flow.IsInFlow;
            root = owner.RunWhileInTree(FlowTask.Never());
            rootStarted = true;
        }).CallDeferred(); // runs outside flow code, like _Ready
        await FlowTask.WaitUntil(() => rootStarted);
        await FlowTask.NextFrame();
        var rootRunning = root.Status == FlowStatus.Running;
        RemoveChild(owner); // tree_exiting ends both flows; Own still frees the node when this step ends
        await FlowTask.WaitUntil(() => root.IsCompleted && fromFlow.IsCompleted);
        return Verify($"from flow code: running after the caller ended: {fromFlowRunning}, then {fromFlow.Status} ({fromFlow.Result}); " +
                      $"started in flow: {startedInFlow}, root running: {rootRunning}, then {root.Status} ({root.Result})",
            (fromFlowRunning, "from flow code: a root flow that outlives the calling scope"),
            (fromFlow.Status == FlowStatus.Succeeded && !fromFlow.Result, "from flow code: false when the node leaves the tree"),
            (!startedInFlow && rootRunning, "outside flow code: a root flow"),
            (root.Status == FlowStatus.Succeeded && !root.Result, "false when the node leaves the tree"));
    }

    /// <summary>RunWhileInTree runs the task on the clock it is given.</summary>
    async FlowTask<Verdict> RunWhileInTreeOnAClock()
    {
        var owner = new Node { Name = "Clocked" };
        AddChild(NodeLifetime.Own(owner));
        var unscaled = FlowWorldNode.Default.UnscaledClock;
        Clock seen = null;

        async FlowTask Body()
        {
            seen = Flow.CurrentClock;
            await FlowTask.Never();
        }

        var h = owner.RunWhileInTree(Body(), unscaled);
        RemoveChild(owner);
        await FlowTask.WaitUntil(() => h.IsCompleted);
        return Verify($"on the given clock: {ReferenceEquals(seen, unscaled)}, then {h.Status} ({h.Result})",
            (ReferenceEquals(seen, unscaled), "runs on the given clock"),
            (h.Status == FlowStatus.Succeeded && !h.Result, "false when the node leaves the tree"));
    }

    /// <summary>The World is ticked with unscaled time: UnscaledClock keeps going while Engine.TimeScale is 0.</summary>
    static async FlowTask<Verdict> UnscaledClockIgnoresTimeScale()
    {
        var world = FlowWorldNode.Default;
        var d0 = world.DefaultClock.Time;
        var u0 = world.UnscaledClock.Time;
        var ms0 = Time.GetTicksMsec();
        var saved = Engine.TimeScale;
        Engine.TimeScale = 0;
        try
        {
            await FlowTask.WaitForSeconds(0.1, world.UnscaledClock);
        }
        finally
        {
            Engine.TimeScale = saved;
        }

        var d = world.DefaultClock.Time - d0;
        var u = world.UnscaledClock.Time - u0;
        var ms = Time.GetTicksMsec() - ms0;
        return Verify($"Engine.TimeScale 0: UnscaledClock +{u:0.###}s, DefaultClock +{d:0.###}s, {ms} ms wall",
            (u >= 0.1 - 1e-9, "UnscaledClock advanced"),
            (d == 0, "DefaultClock followed Engine.TimeScale"),
            // The first Tick after the wait started adds the time since the previous frame, part of which passed before the
            // wait started, so the wall time can be up to a frame shorter than 0.1s (84 ms seen after a long frame).
            (ms >= 50, "real time elapsed"));
    }

    /// <summary>
    /// UnscaledClock also advances on FlowAutoload's physics World while Engine.TimeScale is 0 (Godot still runs the
    /// physics steps then, with a delta of 0), and a wait on it resumes in _PhysicsProcess.
    /// </summary>
    async FlowTask<Verdict> PhysicsUnscaledWaitWhileTimeScaleZero()
    {
        var world = FlowWorldNode.Default;
        var pw = FlowAutoload.Physics;
        var saved = Engine.TimeScale;
        var inPhysicsFrame = false;
        ulong physicsMs = 0;
        FlowHandle physics = default;

        async FlowTask OnPhysics(ulong start)
        {
            await FlowTask.WaitForSeconds(0.1, pw.UnscaledClock);
            physicsMs = Time.GetTicksMsec() - start;
            inPhysicsFrame = Engine.IsInPhysicsFrame();
        }

        Engine.TimeScale = 0;
        try
        {
            physics = pw.Run(OnPhysics(Time.GetTicksMsec()));
            // On UnscaledClock, which Engine.TimeScale 0 does not stop.
            await FlowTask.WaitUntil(() => physics.IsCompleted, world.UnscaledClock);
        }
        finally
        {
            Engine.TimeScale = saved;
        }

        return Verify($"Engine.TimeScale 0: a 0.1s wait on the physics World's UnscaledClock ended after {physicsMs} ms",
            (physics.Status == FlowStatus.Succeeded, "the wait completed"),
            (physicsMs is >= 70 and < 1000, "after about 0.1s of real time"),
            (inPhysicsFrame, "resumed in _PhysicsProcess"));
    }

    /// <summary>
    /// A long frame (a hitch, a breakpoint) advances the clocks by at most MaxPhysicsStepsPerFrame / PhysicsTicksPerSecond
    /// seconds, the limit Godot applies after a long frame, so waits (UnscaledClock ones included) do not all expire
    /// at once. Under Engine.TimeScale 0 the World ticks with the measured time, so a one-second frame hits the limit. Under
    /// 0.5 it ticks with Godot's unscaled step (delta / 0.5), which Godot itself keeps close to the limit after a long
    /// frame; the limit applies to it before the scale.
    /// </summary>
    async FlowTask<Verdict> LongFrameIsClamped()
    {
        var world = FlowWorldNode.Default;
        var limit = (double)Engine.MaxPhysicsStepsPerFrame / Engine.PhysicsTicksPerSecond;
        var step = 1.0 / Engine.PhysicsTicksPerSecond;
        var zero = await LongFrame(world, 0);
        const double half = 0.5;
        var scaled = await LongFrame(world, half);
        var expected = Math.Min(scaled.GodotDelta / half, limit);

        // Godot's own delta after a long frame is close to the limit x Engine.TimeScale but not equal to it (up to 0.75 of
        // a scaled physics step away in 13 runs), so it is compared with a tolerance of two scaled physics steps.
        return Verify($"{zero.Msec} ms frame at Engine.TimeScale 0: UnscaledClock +{zero.Unscaled:0.####}s (limit {limit:0.####}s), DefaultClock +{zero.Default:0.####}s, 0.5s wait done: {zero.WaitDone}; {scaled.Msec} ms frame at {half}: Godot delta {scaled.GodotDelta:0.####}s, UnscaledClock +{scaled.Unscaled:0.####}s, DefaultClock +{scaled.Default:0.####}s, 0.5s wait done: {scaled.WaitDone}",
            (zero.Msec >= 1000 && scaled.Msec >= 1000, "the frames took a second"),
            (Math.Abs(zero.Unscaled - limit) < 1e-9, "scale 0: UnscaledClock advanced by the limit (the measured time, clamped)"),
            (zero.Default == 0, "scale 0: DefaultClock stayed"),
            (Math.Abs(scaled.Unscaled - expected) < 1e-9, "scale 0.5: UnscaledClock advanced by Godot's unscaled step, at most the limit"),
            (Math.Abs(scaled.Default - scaled.Unscaled * half) < 1e-12, "scale 0.5: DefaultClock advanced by that x Engine.TimeScale (clamped before the scale)"),
            (Math.Abs(scaled.Default - scaled.GodotDelta) <= 2 * step * half, "scale 0.5: DefaultClock is within two scaled physics steps of Godot's delta"),
            (!zero.WaitDone && !scaled.WaitDone, "a 0.5s wait on UnscaledClock started before the frame is still running"));
    }

    readonly struct LongFrameSample
    {
        public readonly ulong Msec;
        public readonly double Unscaled;
        public readonly double Default;
        public readonly double GodotDelta;
        public readonly bool WaitDone;

        public LongFrameSample(ulong msec, double unscaled, double @default, double godotDelta, bool waitDone)
        {
            Msec = msec;
            Unscaled = unscaled;
            Default = @default;
            GodotDelta = godotDelta;
            WaitDone = waitDone;
        }
    }

    // Blocks a frame under Engine.TimeScale `scale` for a second; returns what the clocks and Godot's delta advanced by.
    async FlowTask<LongFrameSample> LongFrame(FlowWorld world, double scale)
    {
        var saved = Engine.TimeScale;
        Engine.TimeScale = scale;
        try
        {
            await FlowTask.NextFrame(); // a frame under the new Engine.TimeScale
            var wait = Flow.Spawn(FlowTask.WaitForSeconds(0.5, world.UnscaledClock));
            var ms0 = Time.GetTicksMsec();
            OS.DelayMsec(1000); // blocks this frame for a second
            await FlowTask.NextFrame();
            var sample = new LongFrameSample(Time.GetTicksMsec() - ms0, world.UnscaledClock.DeltaTime, world.DefaultClock.DeltaTime, GetProcessDeltaTime(), wait.IsCompleted);
            wait.Cancel();
            return sample;
        }
        finally
        {
            Engine.TimeScale = saved;
        }
    }

    /// <summary>
    /// Clock.TimeScale rejects an Engine.TimeScale whose product with a child clock's scale is not finite. The
    /// FlowWorldNode pushes the error and ticks with DefaultClock's previous scale, so its World does not stop.
    /// </summary>
    async FlowTask<Verdict> RejectedDefaultClockScaleStillTicks()
    {
        var node = new FlowWorldNode { Name = "HugeClockWorld" };
        AddChild(NodeLifetime.Own(node)); // freed when this step ends; its _ExitTree disposes the World
        var hw = node.World;
        var ticks = 0;

        async FlowTask HoldAHugeClock()
        {
            // Removed when this flow ends. Paused, so its time stays finite while it exists.
            var huge = Flow.CreateClock("huge", hw.DefaultClock);
            huge.TimeScale = double.MaxValue; // finite while DefaultClock.TimeScale is 1, infinite above it
            using var pause = huge.Pause();
            while (true)
            {
                await FlowTask.NextFrame();
                ticks++;
            }
        }

        var holder = hw.Run(HoldAHugeClock());
        await FlowTask.WaitUntil(() => ticks >= 2);
        var ticks0 = ticks;
        var time0 = hw.DefaultClock.Time;
        var frame0 = Engine.GetProcessFrames();
        ulong frames;
        var saved = Engine.TimeScale;
        GD.Print("[smoke] (the next ERROR lines are expected: FlowWorldNode reports the rejected DefaultClock scale on each frame)");
        Engine.TimeScale = 2;
        try
        {
            await FlowTask.DelayFrames(3);
            frames = Engine.GetProcessFrames() - frame0;
        }
        finally
        {
            Engine.TimeScale = saved;
        }

        var ticked = ticks - ticks0;
        var advanced = hw.DefaultClock.Time - time0;
        var scale = hw.DefaultClock.TimeScale;
        holder.Cancel();
        return Verify($"Engine.TimeScale 2 with a child clock at double.MaxValue: {ticked} Ticks in {frames} frames, DefaultClock TimeScale {scale}, +{advanced:0.####}s",
            (scale == 1, "DefaultClock kept its previous scale"),
            (ticked > 0 && ticked + 1 >= (int)frames, "the World ticked on the frames whose scale was rejected"),
            (advanced > 0, "DefaultClock advanced"));
    }

    async FlowTask<Verdict> RunsWhileTreePaused()
    {
        var tree = GetTree();
        tree.Paused = true;
        try
        {
            var f0 = Engine.GetProcessFrames();
            await FlowTask.WaitForSeconds(0.05);
            return Verify($"{Engine.GetProcessFrames() - f0} frames while paused", (tree.Paused, "tree paused"), (Engine.GetProcessFrames() > f0, "World ticked"));
        }
        finally
        {
            tree.Paused = false;
        }
    }

    /// <summary>
    /// A FlowWorldNode with ProcessMode Pausable stops while the tree is paused: it neither ticks nor flushes, so a Signal
    /// emitted during the pause resumes its flow only after the pause. Its first Tick after the pause adds that frame's
    /// Godot delta (unscaled, clamped), not the paused time. The node is a WallClockWorldNode: the default ticks with Godot's
    /// step whenever Engine.TimeScale is above 0, but the time measured since the node's previous frame, which this
    /// override ticks with, has to leave out the frames the node was not processed.
    /// </summary>
    async FlowTask<Verdict> PausableWorldSkipsPausedTime()
    {
        var node = new WallClockWorldNode { Name = "PausableWorld", ProcessMode = ProcessModeEnum.Pausable };
        AddChild(NodeLifetime.Own(node)); // freed when this step ends; its _ExitTree disposes the World
        var pw = node.World;
        var tree = GetTree();
        var limit = (double)Engine.MaxPhysicsStepsPerFrame / Engine.PhysicsTicksPerSecond;
        var ticks = 0;
        var ticksBefore = -1; // set when the tree is paused
        var firstDt = -1.0;
        var firstExpected = -2.0;

        async FlowTask Probe()
        {
            while (true)
            {
                await FlowTask.NextFrame();
                ticks++;
                if (ticksBefore >= 0 && ticks == ticksBefore + 1)
                {
                    // The node was not processed on the previous frame, so GetDeltaTime gets this frame's Godot delta,
                    // unscaled, instead of the time measured since its last Tick, and the override clamps it.
                    firstDt = pw.UnscaledClock.DeltaTime;
                    firstExpected = Math.Min(node.GetProcessDeltaTime() / Engine.TimeScale, limit);
                }
            }
        }

        var ping = new Signal<int>("Ping");
        var got = -1;
        var gotWhilePaused = false;

        async FlowTask Receiver()
        {
            got = await ping.Next();
            gotWhilePaused = tree.Paused;
        }

        pw.Run(Probe());
        pw.Run(Receiver());
        await FlowTask.WaitUntil(() => ticks >= 3);
        var unscaledBefore = 0.0;
        var gotBeforeUnpause = false;
        tree.Paused = true;
        try
        {
            ticksBefore = ticks;
            unscaledBefore = pw.UnscaledClock.Time;
            await FlowTask.NextFrame(); // this World (Always) runs while paused
            ping.Emit(7);               // queued for the paused World's flow
            await FlowTask.WaitForSeconds(0.3, FlowWorldNode.Default.UnscaledClock);
            gotBeforeUnpause = got != -1;
        }
        finally
        {
            tree.Paused = false;
        }

        var ticksWhilePaused = ticks - ticksBefore;
        var timeWhilePaused = pw.UnscaledClock.Time - unscaledBefore;
        await FlowTask.Race(FlowTask.WaitUntil(() => ticks > ticksBefore && got != -1), FlowTask.WaitForSeconds(2));
        return Verify($"ticks while paused {ticksWhilePaused} (+{timeWhilePaused:0.####}s), Signal emitted while paused: flow resumed {(got == -1 ? "never" : gotWhilePaused ? "during the pause" : "after the pause")} (got {got}), first dt after the pause {firstDt:0.####}s (frame delta {firstExpected:0.####}s)",
            (ticksWhilePaused == 0 && timeWhilePaused == 0, "no Tick while the tree was paused"),
            (!gotBeforeUnpause, "no flush while the tree was paused"),
            (got == 7 && !gotWhilePaused, "the Signal resumed the flow after the pause"),
            (Math.Abs(firstDt - firstExpected) < 1e-9, "the first dt after the pause is that frame's delta, not the paused time"));
    }

    static async FlowTask<Verdict> PhysicsWorld()
    {
        var pw = FlowAutoload.Physics;
        ulong p0 = 0, p1 = 0;
        var inPhysics = false;
        var ownWorld = false;
        var dt = 0.0;
        var unscaledDt = 0.0;

        async FlowTask OnPhysics()
        {
            await FlowTask.NextFrame();
            p0 = Engine.GetPhysicsFrames();
            await FlowTask.DelayFrames(3);
            p1 = Engine.GetPhysicsFrames();
            inPhysics = Engine.IsInPhysicsFrame();
            ownWorld = FlowWorld.Current == pw;
            dt = pw.DefaultClock.DeltaTime;
            unscaledDt = pw.UnscaledClock.DeltaTime;
        }

        var h = pw.Run(OnPhysics());
        await FlowTask.WaitUntil(() => h.IsCompleted);
        var step = 1.0 / Engine.PhysicsTicksPerSecond;
        return Verify($"DelayFrames(3) = {p1 - p0} physics frames, dt {dt:0.#####}, unscaled {unscaledDt:0.#####} (step {step:0.#####})",
            (h.Status == FlowStatus.Succeeded, "completed"), (p1 - p0 == 3, "3 physics frames"), (inPhysics, "resumed in _PhysicsProcess"),
            (ownWorld, "on the physics World"), (Math.Abs(dt - step) < 1e-6, "physics delta"),
            (Math.Abs(unscaledDt - step) < 1e-9, "UnscaledClock advances by the physics step"));
    }

    /// <summary>
    /// The physics World of FlowAutoload uses FlowWorldNode's time model: UnscaledClock advances by the physics step and
    /// DefaultClock by Godot's physics delta (step x Engine.TimeScale).
    /// </summary>
    static async FlowTask<Verdict> PhysicsWorldFollowsTimeScale()
    {
        var pw = FlowAutoload.Physics;
        var dt = 0.0;
        var unscaledDt = 0.0;
        var godotDelta = 0.0;
        var saved = Engine.TimeScale;

        async FlowTask OnPhysics()
        {
            await FlowTask.DelayFrames(2); // a whole physics frame under the new Engine.TimeScale
            dt = pw.DefaultClock.DeltaTime;
            unscaledDt = pw.UnscaledClock.DeltaTime;
            godotDelta = FlowWorldNode.Instance.GetPhysicsProcessDeltaTime();
        }

        Engine.TimeScale = 0.5;
        try
        {
            var h = pw.Run(OnPhysics());
            await FlowTask.WaitUntil(() => h.IsCompleted);
        }
        finally
        {
            Engine.TimeScale = saved;
        }

        var step = 1.0 / Engine.PhysicsTicksPerSecond;
        return Verify($"Engine.TimeScale 0.5: DefaultClock +{dt:0.#####}s (Godot physics delta {godotDelta:0.#####}s), UnscaledClock +{unscaledDt:0.#####}s (step {step:0.#####})",
            (Math.Abs(dt - godotDelta) < 1e-9, "DefaultClock advances by Godot's physics delta"),
            (Math.Abs(dt - step * 0.5) < 1e-9, "= step x Engine.TimeScale"),
            (Math.Abs(unscaledDt - step) < 1e-9, "UnscaledClock advances by the step"));
    }

    /// <summary>
    /// FlowAutoload applies Engine.TimeScale to its physics World as FlowWorldNode does to its own: a scale that
    /// Clock.TimeScale rejects is pushed as an error and the physics World still ticks with the previous scale.
    /// </summary>
    static async FlowTask<Verdict> PhysicsWorldRejectedScaleStillTicks()
    {
        var pw = FlowAutoload.Physics;
        var ticks = 0;

        async FlowTask HoldAHugeClock()
        {
            // Removed when this flow ends. Paused, so its time stays finite while it exists.
            var huge = Flow.CreateClock("huge", pw.DefaultClock);
            huge.TimeScale = double.MaxValue; // finite while DefaultClock.TimeScale is 1, infinite above it
            using var pause = huge.Pause();
            while (true)
            {
                await FlowTask.NextFrame();
                ticks++;
            }
        }

        var holder = pw.Run(HoldAHugeClock());
        await FlowTask.WaitUntil(() => ticks >= 2);
        var ticks0 = ticks;
        var time0 = pw.DefaultClock.Time;
        var physics0 = Engine.GetPhysicsFrames();
        ulong physicsFrames;
        var saved = Engine.TimeScale;
        GD.Print("[smoke] (the next ERROR lines are expected: FlowAutoload reports the rejected DefaultClock scale on each physics frame)");
        Engine.TimeScale = 2;
        try
        {
            // Physics frames, not process frames: a headless process frame is much shorter than a physics step. The limit
            // is on UnscaledClock, which Engine.TimeScale does not change.
            await FlowTask.Race(FlowTask.WaitUntil(() => Engine.GetPhysicsFrames() - physics0 >= 3),
                FlowTask.WaitForSeconds(2, FlowWorldNode.Default.UnscaledClock));
            physicsFrames = Engine.GetPhysicsFrames() - physics0;
        }
        finally
        {
            Engine.TimeScale = saved;
        }

        var ticked = ticks - ticks0;
        var advanced = pw.DefaultClock.Time - time0;
        var scale = pw.DefaultClock.TimeScale;
        holder.Cancel();
        return Verify($"Engine.TimeScale 2 with a child clock at double.MaxValue: {ticked} physics Ticks in {physicsFrames} physics frames, DefaultClock TimeScale {scale}, +{advanced:0.####}s",
            (scale == 1, "DefaultClock kept its previous scale"),
            (physicsFrames >= 3, "3 physics frames passed"),
            (ticked >= 2 && ticked + 1 >= (int)physicsFrames, "the physics World ticked on the physics frames whose scale was rejected"),
            (advanced > 0, "DefaultClock advanced"));
    }

    static async FlowTask<Verdict> CrossWorldSignal()
    {
        var go = new Signal<int>("Go");
        var got = -1;
        var gotInPhysics = false;

        async FlowTask PhysicsWaiter()
        {
            got = await go.Next();
            gotInPhysics = Engine.IsInPhysicsFrame();
        }

        var h = FlowAutoload.Physics.Run(PhysicsWaiter());
        await FlowTask.NextFrame();
        go.Emit(5); // emitted by a flow of the main World
        await FlowTask.WaitUntil(() => h.IsCompleted);
        return Verify($"physics flow got {got}, in physics frame: {gotInPhysics}", (got == 5, "value"), (gotInPhysics, "resumed by the physics World"));
    }

    // ---------------------------------------------------------------------- core suite

    void RunCoreSuite()
    {
        GD.Print($"[smoke] running the core test suite in the Godot process (NUnit, .NET {System.Environment.Version})...");
        CoreSuiteRunner.Report r;
        try
        {
            r = CoreSuiteRunner.Run(typeof(SmokeMain).Assembly);
        }
        catch (Exception ex)
        {
            Check("core test suite (in Godot)", false, ex.ToString());
            return;
        }

        GD.Print("[nunit] " + string.Join(", ", r.Fixtures));
        foreach (var f in r.Failures) GD.Print("[nunit] FAILED " + f);
        Check("core test suite (in Godot)", r.Ok,
            $"{r.Passed}/{r.Total} passed, {r.Failed} failed, {r.Skipped} skipped, {r.Inconclusive} inconclusive, {r.Duration.TotalSeconds:0.0}s");
    }
}
