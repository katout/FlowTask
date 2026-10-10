using System.Collections.Generic;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>
/// The demo scene (Demo.tscn, the main scene): one screen to try the samples. Run the project and use the buttons; Escape
/// is the back key. The session's objects (clocks, back-key router, settings) are made when the demo starts and passed to
/// the flows, and the demo's flow is bound to this node, so closing the game or freeing the scene ends every sample it
/// runs. The clocks belong to that flow and leave the World with it, so loading the scene again adds no clocks to the
/// World.
/// </summary>
public partial class Demo : Control
{
    // What DemoLoader pretends to load; ThreadedResourceLoader takes res:// paths instead.
    static readonly string[] ResourceNames = { "Forest", "Castle", "Music", "Enemies" };

    readonly Queue<string> _log = new();
    Label _logLabel;
    int _hits;

    public override void _Ready()
    {
        // The menus work while the tree is paused (PauseLink); the enemy's stage pauses with the tree (Enemies).
        ProcessMode = ProcessModeEnum.Always;
        // Opaque boxes, so that a screen opened over another one (the settings over the menu) hides it.
        Theme = new Theme();
        Theme.SetStylebox("panel", "PanelContainer", new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.18f, 0.23f),
            ContentMarginLeft = 12,
            ContentMarginTop = 12,
            ContentMarginRight = 12,
            ContentMarginBottom = 12,
        });
        var router = new BackKeyRouter();
        var input = new BackKeyInput { Router = router };
        input.Unhandled += () => Log("Back key: nothing to close (a game would ask whether to quit)");
        AddChild(input);
        this.RunWhileInTree(Run(router));
    }

    async FlowTask Run(BackKeyRouter router)
    {
        // Owned by this flow: the clocks leave the World when the demo ends.
        var clocks = GameClocks.ForCurrentScope();
        AddChild(new PauseLink { Clock = clocks.Game });
        // The demo runs on the UI clock: it goes on while the game is paused.
        await Flow.WithClock(clocks.Ui, RunSamples(clocks, router));
    }

    async FlowTask RunSamples(GameClocks clocks, BackKeyRouter router)
    {
        var menu = new VBoxContainer { Position = new Vector2(16, 16), CustomMinimumSize = new Vector2(240, 0) };
        AddChild(menu);
        menu.AddChild(new Label { Text = "FlowTask samples\nEscape: the back key" });
        var confirm = AddButton(menu, "Confirm dialog");
        var tutorial = AddButton(menu, "Tutorial");
        var shop = AddButton(menu, "Shop");
        var loading = AddButton(menu, "Parallel loading");
        var hit = AddButton(menu, "Hit the enemy");

        _logLabel = new Label { VerticalAlignment = VerticalAlignment.Bottom };
        _logLabel.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _logLabel.GrowVertical = GrowDirection.Begin;
        _logLabel.OffsetLeft = _logLabel.OffsetRight = 16;
        _logLabel.OffsetTop = _logLabel.OffsetBottom = -16;
        AddChild(_logLabel);

        var pauseView = PauseMenuView.Create();
        AddChild(pauseView.Root);
        var settings = new GameSettings(new DemoSettingsStore(Log));
        pauseView.VolumeSlider.ValueChanged += v => settings.Volume = (float)v;

        var overlay = new Control { Name = "Tutorial", MouseFilter = MouseFilterEnum.Ignore };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(overlay);
        var skip = new Button { Text = "Skip the tutorial", Visible = false };
        skip.SetAnchorsPreset(LayoutPreset.BottomRight);
        skip.GrowHorizontal = GrowDirection.Begin;
        skip.GrowVertical = GrowDirection.Begin;
        skip.OffsetLeft = skip.OffsetRight = -16;
        skip.OffsetTop = skip.OffsetBottom = -16;
        overlay.AddChild(skip);
        var steps = new[]
        {
            new TutorialStep("Hit the enemy.", hit, () => _hits > 0),
            new TutorialStep("Pause the game.", pauseView.PauseButton, () => clocks.Game.IsPausedInHierarchy),
            new TutorialStep("Resume it.", pauseView.ResumeButton, () => !clocks.Game.IsPausedInHierarchy),
        };

        Log("Pick a sample. Pausing freezes the enemy; the menus keep running.");
        await FlowTask.WhenAll(
            PauseMenu.Hud(clocks, pauseView, router, settings),
            Enemies(clocks, hit),
            OneSampleAtATime(clocks, router, confirm, tutorial, shop, loading, overlay, skip, steps));
    }

    // One sample at a time: a press of another sample's button meanwhile reaches nobody.
    async FlowTask OneSampleAtATime(GameClocks clocks, BackKeyRouter router, Button confirm, Button tutorial, Button shop,
        Button loading, Control overlay, Button skip, TutorialStep[] steps)
    {
        using var confirmPressed = confirm.PressedSignal();
        using var tutorialPressed = tutorial.PressedSignal();
        using var shopPressed = shop.PressedSignal();
        using var loadingPressed = loading.PressedSignal();
        var store = new Shop(new DemoStore(), router, clocks.Ui, r => Log($"Shop: receipt {r.TransactionId} came after leaving; granted"));
        var loader = new DemoLoader();
        while (true)
        {
            var chosen = await FlowTask.Race(new[]
            {
                confirmPressed.Next().WithoutResult(),
                tutorialPressed.Next().WithoutResult(),
                shopPressed.Next().WithoutResult(),
                loadingPressed.Next().WithoutResult(),
            });
            if (chosen == 0)
            {
                var ok = await ConfirmDialog.Show(this, "Buy the gem pack?", router, clocks.Ui);
                Log(ok ? "Confirm dialog: OK" : "Confirm dialog: no (Cancel, the back key or 10 s)");
            }
            else if (chosen == 1)
            {
                _hits = 0;
                skip.Visible = true;
                try
                {
                    var done = await Tutorial.Run(overlay, skip, steps);
                    Log($"Tutorial: {done} of {steps.Length} steps done");
                }
                finally
                {
                    if (IsInstanceValid(skip)) skip.Visible = false;
                }
            }
            else if (chosen == 2)
            {
                await OpenShop(store, router);
            }
            else
            {
                await LoadResources(loader);
            }
        }
    }

    async FlowTask OpenShop(Shop store, BackKeyRouter router)
    {
        var box = Panel("Shop"); // freed when this flow ends
        box.AddChild(new Label { Text = "Gem pack (the back key leaves)", HorizontalAlignment = HorizontalAlignment.Center });
        var buy = new Button { Text = "Buy" };
        var status = new Label();
        box.AddChild(buy);
        box.AddChild(status);
        await ShopScreen.Run(buy, status, store, router, bug => Log("Shop: bug " + bug.Message));
        Log("Shop: left with the back key");
    }

    async FlowTask LoadResources(IResourceLoader loader)
    {
        var box = Panel("Loading"); // freed when this flow ends
        var progress = new ProgressBar { MaxValue = 100 };
        var cancel = new Button { Text = "Cancel" };
        box.AddChild(progress);
        box.AddChild(cancel);
        try
        {
            var requests = await Loading.LoadAll(loader, ResourceNames, cancel, progress);
            if (requests == null)
            {
                Log("Loading: canceled");
            }
            else
            {
                Log($"Loading: {requests.Length} resources loaded");
                foreach (var request in requests) request.Release(); // the demo does not use them
            }
        }
        catch (ResourceLoadException e)
        {
            Log("Loading: " + e.Message);
        }
    }

    // An enemy at a time; the next one comes a second of game time after the last one died.
    async FlowTask Enemies(GameClocks clocks, Button hit)
    {
        using var hitPressed = hit.PressedSignal();
        // Pausable: it stops with the tree, while this demo (ProcessMode Always) goes on.
        var stage = NodeLifetime.Own(new Node2D { Position = GetViewportRect().Size / 2, ProcessMode = ProcessModeEnum.Pausable });
        AddChild(stage);
        MoveChild(stage, 0); // behind the menus
        while (true)
        {
            var enemy = new Enemy { Clocks = clocks, Waypoints = new[] { new Vector2(-200, 0), new Vector2(200, 0) } };
            enemy.Position = enemy.Waypoints[0];
            enemy.AddChild(new ColorRect { Size = new Vector2(48, 48), Position = new Vector2(-24, -24), MouseFilter = MouseFilterEnum.Ignore });
            stage.AddChild(enemy); // its _Ready starts the brain
            while (IsInstanceValid(enemy)) // freed after its death animation
            {
                var r = await FlowTask.Race(hitPressed.Next(), ShowEnemy(enemy, hit));
                if (r.Index != 0 || !IsInstanceValid(enemy)) continue;
                _hits++;
                enemy.TakeHit(1); // like a bullet: while the game is paused, the hit waits for the resume
            }

            Log("Enemy: defeated; the next one comes in a second");
            await FlowTask.WaitForSeconds(1, clocks.Game);
        }
    }

    // Shows what the enemy's brain does: white while patrolling, red while flinching, grey once dead.
    static async FlowTask ShowEnemy(Enemy enemy, Button hit)
    {
        while (IsInstanceValid(enemy))
        {
            enemy.Modulate = enemy.IsDead ? Colors.Gray : enemy.Hp == null || enemy.IsPatrolling ? Colors.White : new Color(1f, 0.35f, 0.35f);
            if (enemy.Hp != null) hit.Text = $"Hit the enemy (HP {enemy.Hp.Value})";
            await FlowTask.NextFrame();
        }
    }

    static Button AddButton(Control parent, string text)
    {
        var button = new Button { Text = text };
        parent.AddChild(button);
        return button;
    }

    // A box at the center of the screen, owned by the calling flow: freed when that flow ends, however it ends.
    VBoxContainer Panel(string name)
    {
        var panel = NodeLifetime.Own(new PanelContainer { Name = name, CustomMinimumSize = new Vector2(320, 0) });
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        var box = new VBoxContainer();
        panel.AddChild(box);
        AddChild(panel);
        return box;
    }

    void Log(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > 6) _log.Dequeue();
        if (IsInstanceValid(_logLabel)) _logLabel.Text = string.Join("\n", _log);
    }
}
