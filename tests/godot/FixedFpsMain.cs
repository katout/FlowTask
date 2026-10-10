using System;
using Katout.FlowTask;
using Katout.FlowTask.Godot;
using Godot;

/// <summary>
/// The third scene of the smoke test (FixedFps.tscn). tools/godot/run-smoke.ps1 runs it in a Godot process of its own with
/// <c>--fixed-fps 60</c>, the frame rate Movie Maker forces too: Godot's delta is then 1/60 s x Engine.TimeScale whatever a
/// frame takes, and Godot does not wait between frames. Checks that the default FlowWorldNode (the autoload) keeps in step
/// with Godot frame by frame, also when frames take longer than 1/60 s as they do while recording: DefaultClock advances
/// by Godot's delta, UnscaledClock by Godot's unscaled step (what Timer and Tween with ignore_time_scale advance by), as
/// the physics-rate World does. Then the exceptions: the limit, Engine.TimeScale 0 (measured time), a scale changed before
/// the Tick, and a <c>GetDeltaTime</c> override that ticks with the measured time (<see cref="WallClockWorldNode"/>).
/// A script cannot tell whether Godot runs under --fixed-fps, so the checks also compare Godot's delta with 1/60 s.
/// </summary>
public partial class FixedFpsMain : Node
{
    const int Fps = 60;                 // run-smoke.ps1 passes --fixed-fps 60
    const double GodotStep = 1.0 / Fps; // Godot's unscaled process step under --fixed-fps 60
    const int Frames = 20;
    const int FrameMsec = 50;           // longer than 1/60 s, like a frame of a recording
    const ulong TimeoutMsec = 60_000;

    int _pass;
    int _fail;
    ulong _startMsec;
    bool _finished;
    FlowHandle _suite;
    WallClockWorldNode _wallClock;

    public override void _Ready()
    {
        _startMsec = Time.GetTicksMsec();
        GD.Print($"[smoke] FlowTask Godot smoke test, third scene (FixedFps.tscn, run with --fixed-fps {Fps}) on Godot {Engine.GetVersionInfo()["string"]}");
        var node = FlowWorldNode.Instance;
        Check("autoload", node is FlowAutoload && FlowAutoload.Physics != null, node == null ? "no FlowWorldNode" : node.GetType().Name);
        if (node == null)
        {
            Finish();
            return;
        }

        _wallClock = new WallClockWorldNode { Name = "WallClockWorld" };
        AddChild(_wallClock);
        _suite = FlowWorldNode.Default.Run(Suite());
    }

    public override void _Process(double delta)
    {
        if (_finished) return;
        if (_suite.IsCompleted)
        {
            if (_suite.Status != FlowStatus.Succeeded) Check("fixed-fps suite", false, $"suite ended as {_suite.Status}: {_suite.Exception}");
            Finish();
        }
        else if (Time.GetTicksMsec() - _startMsec > TimeoutMsec)
        {
            Check("fixed-fps suite finished in time", false, "timeout\n" + FlowWorldNode.Default.Dump());
            Finish();
        }
    }

    void Finish()
    {
        _finished = true;
        var ok = _fail == 0;
        GD.Print($"[smoke] FIXEDFPS RESULT: {(ok ? "PASS" : "FAIL")} ({_pass} passed, {_fail} failed)");
        GetTree().Quit(ok ? 0 : 1);
    }

    void Check(string name, bool ok, string detail = null)
    {
        if (ok) _pass++;
        else _fail++;
        GD.Print($"[smoke] {(ok ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")")}");
    }

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
        // A few frames first, so that WallClockWorldNode has had its first frame (which has no measured time yet).
        await FlowTask.DelayFrames(3);
        await Step("fixed-fps-clocks-follow-godot", ClocksFollowGodot(1));
        await Step("fixed-fps-clocks-follow-godot-at-time-scale-0.5", ClocksFollowGodot(0.5));
        await Step("fixed-fps-scale-changed-before-the-tick", ScaleChangedBeforeTheTick());
        await Step("fixed-fps-unscaled-wait-on-godot-time", UnscaledWaitOnGodotTime());
        await Step("fixed-fps-unscaled-wait-while-time-scale-zero", UnscaledWaitWhileTimeScaleZero());
        // Last: with physics steps held back by the lowered limit, Godot then adjusts its process step for a while to let
        // physics catch up (0.01806 s instead of 1/60 s seen right after it), which the checks above compare with 1/60 s.
        await Step("fixed-fps-step-over-the-limit-is-clamped", StepOverTheLimitIsClamped());
    }

    /// <summary>
    /// Frames of 50 ms under --fixed-fps 60: the default FlowWorldNode's clocks advance by Godot's time every frame, the
    /// physics-rate World as much, a 0.25 s wait on UnscaledClock ends with Godot's own 0.25 s ignore_time_scale timer, and
    /// the WallClockWorldNode override advances by the real time instead.
    /// </summary>
    async FlowTask<Verdict> ClocksFollowGodot(double scale)
    {
        var world = FlowWorldNode.Default;
        var physics = FlowAutoload.Physics;
        var wall = _wallClock.World;
        var saved = Engine.TimeScale;
        Engine.TimeScale = scale;
        try
        {
            await FlowTask.NextFrame(); // Godot reads Engine.TimeScale at the start of a frame
            var d0 = world.DefaultClock.Time;
            var u0 = world.UnscaledClock.Time;
            var p0 = physics.UnscaledClock.Time;
            var w0 = wall.UnscaledClock.Time;
            var us0 = Time.GetTicksUsec();
            var f0 = Engine.GetProcessFrames();
            var godot = 0.0;
            var worstGodot = 0.0;
            var worstDefault = 0.0;
            var worstUnscaled = 0.0;
            var timerFrame = -1L;
            var waitFrame = -1L;

            // Godot's own unscaled time: a SceneTreeTimer that ignores Engine.TimeScale advances by the process step.
            var timer = GetTree().CreateTimer(0.25, processAlways: true, processInPhysics: false, ignoreTimeScale: true);
            timer.Timeout += () => timerFrame = (long)(Engine.GetProcessFrames() - f0);

            async FlowTask WaitOnUnscaledClock()
            {
                await FlowTask.WaitForSeconds(0.25, world.UnscaledClock);
                waitFrame = (long)(Engine.GetProcessFrames() - f0);
            }

            var wait = Flow.Spawn(WaitOnUnscaledClock());
            for (var i = 0; i < Frames; i++)
            {
                OS.DelayMsec(FrameMsec); // this frame takes longer than 1/60 s
                await FlowTask.NextFrame();
                var g = GetProcessDeltaTime();
                godot += g;
                worstGodot = Math.Max(worstGodot, Math.Abs(g - GodotStep * scale));
                worstDefault = Math.Max(worstDefault, Math.Abs(world.DefaultClock.DeltaTime - g));
                worstUnscaled = Math.Max(worstUnscaled, Math.Abs(world.UnscaledClock.DeltaTime - GodotStep));
            }

            var real = (Time.GetTicksUsec() - us0) / 1e6;
            var d = world.DefaultClock.Time - d0;
            var u = world.UnscaledClock.Time - u0;
            var p = physics.UnscaledClock.Time - p0;
            // WallClockWorldNode ticks after the autoload in each frame, so its advance runs one frame behind; it is
            // compared with the real time with a tolerance of a frame.
            var w = wall.UnscaledClock.Time - w0;
            return Verify($"Engine.TimeScale {scale}, {Frames} frames in {real:0.###} s: Godot's delta {godot:0.#####} s, DefaultClock +{d:0.#####} s, UnscaledClock +{u:0.#####} s, physics World +{p:0.#####} s, WallClockWorldNode +{w:0.###} s; the 0.25 s timer (ignore_time_scale) fired at frame {timerFrame}, the 0.25 s wait on UnscaledClock ended at frame {waitFrame}",
                (worstGodot < 1e-12, $"Godot's delta is 1/{Fps} s x Engine.TimeScale every frame (run with --fixed-fps {Fps}?)"),
                (real >= Frames * FrameMsec / 1000.0, "the frames took longer than 1/60 s"),
                (worstDefault < 1e-12, "DefaultClock advanced by Godot's delta every frame"),
                (worstUnscaled < 1e-12, "UnscaledClock advanced by Godot's unscaled step every frame"),
                (Math.Abs(p - u) <= GodotStep + 1e-9, "the physics World advanced as much as UnscaledClock"),
                (wait.IsCompleted && timerFrame >= 0 && Math.Abs(timerFrame - waitFrame) <= 1, "the wait on UnscaledClock ended within a frame of Godot's timer"),
                (Math.Abs(w - real) <= FrameMsec / 1000.0 && w > 2 * u, "the GetDeltaTime override (WallClockWorldNode) ticked with the real time"));
        }
        finally
        {
            Engine.TimeScale = saved;
        }
    }

    /// <summary>
    /// Godot's step over the limit (MaxPhysicsStepsPerFrame / PhysicsTicksPerSecond) is clamped to it before the scale.
    /// Under --fixed-fps Godot does not clamp its own step, so a limit of 1/120 s (120 ticks, 1 step per frame) is below it.
    /// </summary>
    async FlowTask<Verdict> StepOverTheLimitIsClamped()
    {
        const double scale = 0.5;
        const int ticks = 120;
        var world = FlowWorldNode.Default;
        var savedTicks = Engine.PhysicsTicksPerSecond;
        var savedSteps = Engine.MaxPhysicsStepsPerFrame;
        var savedScale = Engine.TimeScale;
        double g, u, d;
        Engine.PhysicsTicksPerSecond = ticks;
        Engine.MaxPhysicsStepsPerFrame = 1;
        Engine.TimeScale = scale;
        try
        {
            await FlowTask.NextFrame(); // Godot reads Engine.TimeScale at the start of a frame
            await FlowTask.NextFrame();
            g = GetProcessDeltaTime();
            u = world.UnscaledClock.DeltaTime;
            d = world.DefaultClock.DeltaTime;
        }
        finally
        {
            Engine.PhysicsTicksPerSecond = savedTicks;
            Engine.MaxPhysicsStepsPerFrame = savedSteps;
            Engine.TimeScale = savedScale;
        }

        const double limit = 1.0 / ticks;
        return Verify($"limit {limit:0.#####} s, Engine.TimeScale {scale}: Godot's delta {g:0.#####} s, UnscaledClock +{u:0.#####} s, DefaultClock +{d:0.#####} s",
            (Math.Abs(g - GodotStep * scale) < 1e-12, "Godot's step stayed 1/60 s"),
            (Math.Abs(u - limit) < 1e-12, "UnscaledClock advanced by the limit"),
            (Math.Abs(d - limit * scale) < 1e-12, "DefaultClock advanced by the limit x Engine.TimeScale (clamped before the scale)"));
    }

    /// <summary>
    /// Godot computes a frame's delta with the Engine.TimeScale of the frame's start. A scale changed after that and before
    /// FlowWorldNode's Tick (here in _PhysicsProcess, by a flow on the physics-rate World) leaves DefaultClock on Godot's
    /// delta but puts UnscaledClock off for that frame by the ratio of the scales (docs/en/godot/setup.md); the next frame is back
    /// in step.
    /// </summary>
    async FlowTask<Verdict> ScaleChangedBeforeTheTick()
    {
        const double scale = 0.5;
        var world = FlowWorldNode.Default;
        var saved = Engine.TimeScale;
        var changed = false;
        double g1, u1, d1, g2, u2, d2;

        async FlowTask ChangeTheScaleInPhysics()
        {
            await FlowTask.NextFrame(); // the physics World's next Tick: _PhysicsProcess, before this frame's _Process
            Engine.TimeScale = scale;
            changed = true;
        }

        Engine.TimeScale = 1;
        try
        {
            await FlowTask.NextFrame();
            FlowAutoload.Physics.Run(ChangeTheScaleInPhysics());
            while (!changed) await FlowTask.NextFrame();
            g1 = GetProcessDeltaTime();
            u1 = world.UnscaledClock.DeltaTime;
            d1 = world.DefaultClock.DeltaTime;
            await FlowTask.NextFrame();
            g2 = GetProcessDeltaTime();
            u2 = world.UnscaledClock.DeltaTime;
            d2 = world.DefaultClock.DeltaTime;
        }
        finally
        {
            Engine.TimeScale = saved;
        }

        return Verify($"Engine.TimeScale 1 -> {scale} in _PhysicsProcess: that frame Godot's delta {g1:0.#####} s, DefaultClock +{d1:0.#####} s, UnscaledClock +{u1:0.#####} s; the next frame {g2:0.#####} s, +{d2:0.#####} s, +{u2:0.#####} s",
            (Math.Abs(g1 - GodotStep) < 1e-12, "that frame's delta used the scale of the frame's start"),
            (Math.Abs(d1 - g1) < 1e-12, "DefaultClock advanced by Godot's delta"),
            (Math.Abs(u1 - GodotStep / scale) < 1e-12, "UnscaledClock was off by the ratio of the scales for that frame"),
            (Math.Abs(g2 - GodotStep * scale) < 1e-12 && Math.Abs(d2 - g2) < 1e-12 && Math.Abs(u2 - GodotStep) < 1e-12, "the next frame is back in step"));
    }

    /// <summary>
    /// UnscaledClock follows Godot's time: under --fixed-fps a 0.1 s wait on it ends after 0.1 s of Godot's time (6 frames
    /// of 1/60 s), which can be much less real time, as Godot does not wait between frames.
    /// </summary>
    static async FlowTask<Verdict> UnscaledWaitOnGodotTime()
    {
        var f0 = Engine.GetProcessFrames();
        var us0 = Time.GetTicksUsec();
        await FlowTask.WaitForSeconds(0.1, FlowWorldNode.Default.UnscaledClock);
        var frames = Engine.GetProcessFrames() - f0;
        var ms = (Time.GetTicksUsec() - us0) / 1000.0;
        return Verify($"WaitForSeconds(0.1 s, UnscaledClock) ended after {frames} frames, {ms:0.##} ms of real time",
            (frames is >= 6 and <= 7, "after 0.1 s of Godot's time (6 frames of 1/60 s, 7 with rounding)"));
    }

    /// <summary>
    /// Under --fixed-fps: while Engine.TimeScale is 0 Godot's delta is 0, so the World ticks with the measured time and a
    /// wait on UnscaledClock ends after about 0.1 s of real time, however fast the frames run.
    /// </summary>
    static async FlowTask<Verdict> UnscaledWaitWhileTimeScaleZero()
    {
        var saved = Engine.TimeScale;
        double ms;
        Engine.TimeScale = 0;
        try
        {
            // The wait counts from the start of the Tick it starts in, the real time below from us0: start at a frame's start.
            await FlowTask.NextFrame();
            var us0 = Time.GetTicksUsec();
            await FlowTask.WaitForSeconds(0.1, FlowWorldNode.Default.UnscaledClock);
            ms = (Time.GetTicksUsec() - us0) / 1000.0;
        }
        finally
        {
            Engine.TimeScale = saved;
        }

        return Verify($"Engine.TimeScale 0: WaitForSeconds(0.1 s, UnscaledClock) ended after {ms:0.##} ms of real time",
            (ms is >= 90 and < 1000, "after about 0.1 s of real time"));
    }
}
