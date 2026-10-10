using Katout.FlowTask.Testing;

namespace Katout.FlowTask.Tests.Scenarios;

// ---------------------------------------------------------------------- shared app-layer fakes

public enum Reason
{
    SessionExpired,
    UserQuit,
}

/// <summary>Leaves the game for the title: thrown where the reason is found, caught by the game loop.</summary>
public sealed class ToTitle : Exception
{
    public ToTitle(Reason reason) : base("ToTitle(" + reason + ")") => Reason = reason;
    public Reason Reason { get; }
}

/// <summary>App-side button: a plain .NET event, bridged to a signal per scope.</summary>
public sealed class FakeButton
{
    public event Action Clicked;
    public int ListenerCount => Clicked?.GetInvocationList().Length ?? 0;
    public void Click() => Clicked?.Invoke();

    /// <summary>Bridge: the listener is removed when the scope ends.</summary>
    public EventSignal<FlowUnit> ClickedSignal() => FlowBridge.FromCallback<FlowUnit>(emit =>
    {
        Action h = () => emit(FlowUnit.Default);
        Clicked += h;
        return () => Clicked -= h;
    });
}

// ---------------------------------------------------------------------- game loop

/// <summary>The whole game loop and unwinding to the title.</summary>
public class GameLoopScenario
{
    readonly List<string> _log = new();
    readonly Signal<FlowUnit> _startPressed = new();
    readonly Signal<FlowUnit> _sessionExpired = new(); // Session.Expired

    async FlowTask Game()
    {
        while (true)
        {
            await Title();
            try
            {
                await InGame();
            }
            catch (ToTitle reason) // the unwind to the title ends here
            {
                await ShowNotice(reason);
            }
        }
    }

    async FlowTask InGame()
    {
        // A session expiry, unrelated to any particular await, is raced near the boundary.
        var r = await FlowTask.Race(Stages(), _sessionExpired.Next());
        if (r.Index == 1) throw new ToTitle(Reason.SessionExpired);
    }

    async FlowTask Title()
    {
        _log.Add("title");
        await _startPressed.Next();
    }

    async FlowTask Stages()
    {
        for (var stage = 1; ; stage++)
        {
            using var _ = new StageScope(_log, stage);
            await FlowTask.WaitForSeconds(1.0);
        }
    }

    sealed class StageScope : IDisposable
    {
        readonly List<string> _log;
        readonly int _stage;

        public StageScope(List<string> log, int stage)
        {
            _log = log;
            _stage = stage;
            log.Add("stage " + stage);
        }

        public void Dispose() => _log.Add("leave stage " + _stage);
    }

    async FlowTask ShowNotice(ToTitle reason)
    {
        _log.Add("notice " + reason.Reason);
        await FlowTask.WaitForSeconds(0.5);
    }

    [Test]
    public void SessionExpiryUnwindsToTitle()
    {
        using var world = new TestWorld();
        world.World.Run(Game());
        _startPressed.Emit(FlowUnit.Default);
        world.World.TickFor(1.5);
        _sessionExpired.Emit(FlowUnit.Default);
        world.World.TickFor(1.0);
        Assert.That(_log, Is.EqualTo(new[]
        {
            "title", "stage 1", "leave stage 1", "stage 2", "leave stage 2", "notice SessionExpired", "title",
        }));
        FlowAssert.ScopeIsWaitingOn(world, "Title", "Next");
    }
}

// ---------------------------------------------------------------------- enemy AI

public sealed class Enemy
{
    public readonly Signal<int> Damaged = new();
    public int Position;
    public int Stuns;
}

/// <summary>Enemy AI patrol interrupted by hits, without losing hits during the stun.</summary>
public class EnemyAIScenario
{
    internal static async FlowTask EnemyAI(Enemy self)
    {
        using var hits = self.Damaged.Subscribe(BufferPolicy.Latest); // keeps every hit from here on, no gaps

        while (true)
        {
            var r = await FlowTask.Race(Patrol(self), hits.Next());
            if (r.Index == 1) await HitStun(self, hits); // hits during the stun stay in the buffer
        }
    }

    internal static async FlowTask Patrol(Enemy self)
    {
        while (true)
        {
            self.Position++;
            await FlowTask.NextFrame();
        }
    }

    internal static async FlowTask HitStun(Enemy self, Subscription<int> hits)
    {
        self.Stuns++;
        await FlowTask.WaitForSeconds(0.8);
    }

    [Test]
    public void HitDuringStunIsNotLost()
    {
        using var world = new TestWorld();
        var enemy = new Enemy();
        world.World.Run(EnemyAI(enemy));
        world.World.TickFor(0.5);
        var patrolled = enemy.Position;
        Assert.That(patrolled, Is.GreaterThan(0));
        enemy.Damaged.Emit(10);
        world.World.TickFor(0.3);
        Assert.That(enemy.Position, Is.EqualTo(patrolled), "no patrol during the stun");
        enemy.Damaged.Emit(10); // during the stun
        world.World.TickFor(1.0);
        Assert.That(enemy.Stuns, Is.EqualTo(2));
    }
}

// ---------------------------------------------------------------------- confirmation dialog

public sealed class ConfirmView
{
    public readonly FakeButton OkButton = new();
    public readonly FakeButton CancelButton = new();
}

/// <summary>Confirmation dialog; rapid taps cannot run twice.</summary>
public class ConfirmScenario
{
    static async FlowTask<bool> Confirm(ConfirmView view)
    {
        using var ok = view.OkButton.ClickedSignal(); // bridge: listener removed when the scope ends
        using var cancel = view.CancelButton.ClickedSignal();

        var r = await FlowTask.Race(ok.Next(), cancel.Next()); // a second tap in the same frame reaches nobody
        return r.Index == 0;
    }

    [Test]
    public void DoubleTapsResolveOnce()
    {
        using var world = new TestWorld();
        var view = new ConfirmView();
        var purchases = 0;

        async FlowTask Shop()
        {
            if (await Confirm(view)) purchases++;
        }

        var h = world.World.Run(Shop());
        view.OkButton.Click();
        view.OkButton.Click();
        view.CancelButton.Click();
        world.World.Tick(1.0 / 60);
        Assert.That(h.Status, Is.EqualTo(FlowStatus.Succeeded));
        Assert.That(purchases, Is.EqualTo(1));
        Assert.That(view.OkButton.ListenerCount + view.CancelButton.ListenerCount, Is.EqualTo(0));
    }
}

// ---------------------------------------------------------------------- retry and purchase

public enum Layer
{
    Screen = 0,
    Dialog = 100,
    System = 200,
    Critical = 300,
}

/// <summary>
/// App-side back-key layers, from a list and Flow.Own: the entry on the highest layer gets the key, and within a layer
/// the one pushed last. The scope that pushes an entry owns it, so the entry leaves when that scope ends.
/// </summary>
public sealed class BackKeyLayers
{
    readonly List<Entry> _entries = new();

    public int Count => _entries.Count;

    public IDisposable Push(Layer layer, Signal<FlowUnit> pressed)
    {
        var index = _entries.Count;
        while (index > 0 && _entries[index - 1].Layer > layer) index--;
        var entry = new Entry(this, layer, pressed);
        _entries.Insert(index, entry);
        return Flow.Own(entry);
    }

    public bool TryGetTop(out Signal<FlowUnit> top)
    {
        top = _entries.Count > 0 ? _entries[^1].Pressed : null;
        return top != null;
    }

    sealed class Entry : IDisposable
    {
        readonly BackKeyLayers _owner;

        public Entry(BackKeyLayers owner, Layer layer, Signal<FlowUnit> pressed)
        {
            _owner = owner;
            Layer = layer;
            Pressed = pressed;
        }

        public Layer Layer { get; }
        public Signal<FlowUnit> Pressed { get; }

        public void Dispose() => _owner._entries.Remove(this); // a second call finds nothing to remove
    }
}

public enum ApiError
{
    Network,
}

/// <summary>An expected failure of a request: thrown by the request, caught by the flow that offers a retry.</summary>
public sealed class ApiException : Exception
{
    public ApiException(ApiError error) : base(error.ToString()) => Error = error;
    public ApiError Error { get; }
}

public sealed class Stage
{
    public string Name = "stage-1";
}

public sealed class RetryView : IDisposable
{
    public readonly FakeButton RetryButton = new();
    public bool Disposed;
    public void Dispose() => Disposed = true;
}

public sealed class Receipt
{
}

/// <summary>
/// In-place retry of a request (game time paused, back key captured), and the back key disabled while purchasing.
/// </summary>
public class RetryAndPurchaseScenario
{
    // ---- app layer: layers and back-key delivery
    BackKeyLayers BackStack;
    bool backKeyPressed;

    void Update()
    {
        if (backKeyPressed && BackStack.TryGetTop(out var top)) top.Emit(FlowUnit.Default);
        backKeyPressed = false;
    }

    // ---- app services (fakes)
    FlowWorld _world;
    Clock _game;
    Clock _ui;
    Queue<bool> _fetchResults;
    RetryView _openView;
    List<string> _log;

    [SetUp]
    public void ResetState()
    {
        // Each test gets fresh app state; NUnit reuses the fixture instance across tests.
        BackStack = new BackKeyLayers();
        backKeyPressed = false;
        _fetchResults = new Queue<bool>();
        _openView = null;
        _log = new List<string>();
    }

    async FlowTask<Stage> FetchStageApi()
    {
        await FlowTask.WaitForSeconds(0.1);
        if (!_fetchResults.Dequeue()) throw new ApiException(ApiError.Network);
        return new Stage();
    }

    async FlowTask<RetryView> OpenRetryView(ApiError error)
    {
        await FlowTask.NextFrame();
        _openView = new RetryView();
        _log.Add("dialog " + error);
        return _openView;
    }

    // ---- flow: retry
    async FlowTask<Stage> FetchStage()
    {
        while (true)
        {
            ApiError error;
            try
            {
                return await FetchStageApi();
            }
            catch (ApiException e) // an expected failure: offer a retry
            {
                error = e.Error;
            }

            var retry = await Flow.WithClock(_ui, RetryDialog(error)); // the dialog runs on the UI clock
            if (!retry) throw new ToTitle(Reason.UserQuit);
        }
    }

    async FlowTask<bool> RetryDialog(ApiError error)
    {
        using var pause = _game.Pause(); // pause the game behind the dialog
        var back = new Signal<FlowUnit>();
        using var entry = BackStack.Push(Layer.System, back); // capture the back key
        using var view = await OpenRetryView(error); // showing it is the app layer's job
        using var retry = view.RetryButton.ClickedSignal();

        var r = await FlowTask.Race(retry.Next(), back.Next());
        return r.Index == 0;
    }

    async FlowTask Background()
    {
        while (true)
        {
            _log.Add("bg");
            await FlowTask.WaitForSeconds(0.25);
        }
    }

    async FlowTask<string> Screen()
    {
        try
        {
            return "loaded " + (await FetchStage()).Name;
        }
        catch (ToTitle why)
        {
            return "title:" + why.Reason;
        }
    }

    TestWorld Setup()
    {
        var tw = new TestWorld();
        _world = tw.World;
        _game = _world.CreateClock("Game");
        _ui = _world.CreateClock("UI", _world.UnscaledClock);
        return tw;
    }

    [Test]
    public void RetryPausesTheGameAndSucceeds()
    {
        using var tw = Setup();
        _fetchResults.Enqueue(false);
        _fetchResults.Enqueue(true);
        _world.Run(Background(), _game);
        var h = _world.Run(Screen(), _game);
        _world.TickFor(0.2);
        Assert.That(_log, Does.Contain("dialog Network"));
        Assert.That(_game.PauseCount, Is.GreaterThan(0));
        var bgBefore = _log.FindAll(x => x == "bg").Count;
        _world.TickFor(2.0);
        Assert.That(_log.FindAll(x => x == "bg").Count, Is.EqualTo(bgBefore), "background paused behind the dialog");
        _openView.RetryButton.Click();
        _world.TickFor(0.5);
        Assert.That(h.Result, Is.EqualTo("loaded stage-1"));
        Assert.That(_openView.Disposed, Is.True);
        Assert.That(_game.PauseCount, Is.Zero);
        Assert.That(BackStack.Count, Is.EqualTo(0));
        Assert.That(_log.FindAll(x => x == "bg").Count, Is.GreaterThan(bgBefore), "background resumed");
    }

    [Test]
    public void BackKeyQuitsToTitle()
    {
        using var tw = Setup();
        _fetchResults.Enqueue(false);
        var h = _world.Run(Screen(), _game);
        _world.TickFor(0.2);
        backKeyPressed = true;
        Update();
        _world.TickFor(0.1);
        Assert.That(h.Result, Is.EqualTo("title:UserQuit"));
        Assert.That(_game.PauseCount, Is.Zero);
    }

    // ---- flow: purchase
    async FlowTask<Receipt> StorePurchase()
    {
        await FlowTask.WaitForSeconds(1.0);
        return new Receipt();
    }

    async FlowTask<Receipt> Purchase()
    {
        // An entry nobody waits on sits on top, so back-key presses are dropped.
        using var guard = BackStack.Push(Layer.Critical, new Signal<FlowUnit>());
        return await StorePurchase();
    }

    [Test]
    public void BackKeyIsIgnoredWhilePurchasing()
    {
        using var tw = Setup();

        async FlowTask ShopDialog()
        {
            var back = new Signal<FlowUnit>();
            using var entry = BackStack.Push(Layer.Dialog, back);
            var r = await FlowTask.Race(Purchase(), back.Next());
            _log.Add(r.Index == 0 ? "purchased " + (r.Value0 != null) : "backed out");
            var after = await FlowTask.Race(back.Next(), FlowTask.WaitForSeconds(10));
            _log.Add(after.Index == 0 ? "back works again" : "timeout");
        }

        _world.Run(ShopDialog());
        _world.TickFor(0.3);
        backKeyPressed = true;
        Update();
        _world.TickFor(1.0);
        backKeyPressed = true;
        Update();
        _world.TickFor(0.1);
        Assert.That(_log, Is.EqualTo(new[] { "purchased True", "back works again" }));
    }
}

// ---------------------------------------------------------------------- containing a failure

/// <summary>A failure of a mini game caught by the flow that runs it, without ending the arcade.</summary>
public class ContainedFailureScenario
{
    readonly List<Exception> _reports = new();

    async FlowTask<int> MiniGame()
    {
        await FlowTask.WaitForSeconds(0.1);
        object o = null;
        return o.GetHashCode();
    }

    async FlowTask<int> Arcade()
    {
        try
        {
            return await MiniGame();
        }
        catch (Exception e) when (e is not FlowCanceledException)
        {
            _reports.Add(e); // CrashReport.Send
            return 0;
        }
    }

    [Test]
    public void AFailureIsCaughtAndReported()
    {
        using var world = new TestWorld();
        var score = world.World.RunUntilComplete(Arcade());
        Assert.That(score, Is.EqualTo(0));
        Assert.That(_reports.Single(), Is.InstanceOf<NullReferenceException>());
        Assert.That(world.Exceptions, Is.Empty, "nothing reached the root");
    }
}

// ---------------------------------------------------------------------- testing without an engine

/// <summary>Testing without an engine: the enemy AI above, driven by virtual time.</summary>
public class EngineFreeTestScenario
{
    static FlowTask EnemyAI(Enemy enemy) => EnemyAIScenario.EnemyAI(enemy);

    [Test]
    public void HitDuringHitStunIsNotMissed()
    {
        using var world = new FlowWorld();
        var enemy = new Enemy();
        world.Run(EnemyAI(enemy));

        world.Tick(1.0 / 60);
        enemy.Damaged.Emit(10);        // the first hit starts a hit stun
        world.Tick(1.0 / 60);
        enemy.Damaged.Emit(10);        // a hit during the hit stun (kept in the buffer)
        world.TickFor(seconds: 1.0);   // the first hit stun ends

        // Tests may walk the scope tree.
        Assert.That(world.Dump(), Does.Contain("HitStun"));   // the second hit started another hit stun
        Assert.That(enemy.Stuns, Is.EqualTo(2));
    }
}
