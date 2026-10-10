using System.Threading;
using System.Threading.Tasks;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>
/// The nodes of <see cref="PauseMenu"/>, built in code (a game makes them a scene): the HUD's pause button, the menu and
/// the settings screen. The volume slider is wired to <see cref="GameSettings.Volume"/> by whoever owns both (the demo).
/// </summary>
public sealed class PauseMenuView
{
    public Control Root { get; private set; }
    public Button PauseButton { get; private set; }
    public Control Menu { get; private set; }
    public Button ResumeButton { get; private set; }
    public Button SettingsButton { get; private set; }
    public Control Settings { get; private set; }
    public HSlider VolumeSlider { get; private set; }
    public Button SettingsCloseButton { get; private set; }

    public static PauseMenuView Create(string name = "Hud")
    {
        // The menu has to work while the tree is paused. The root covers the screen and lets the clicks through.
        var root = new Control { Name = name, ProcessMode = Node.ProcessModeEnum.Always, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var view = new PauseMenuView { Root = root, PauseButton = new Button { Name = "Pause", Text = "Pause" } };
        view.PauseButton.SetAnchorsPreset(Control.LayoutPreset.TopRight); // grows to the left from 16 px off the corner
        view.PauseButton.GrowHorizontal = Control.GrowDirection.Begin;
        view.PauseButton.OffsetLeft = view.PauseButton.OffsetRight = -16;
        view.PauseButton.OffsetTop = view.PauseButton.OffsetBottom = 16;
        root.AddChild(view.PauseButton);

        view.Menu = Panel(root, "Menu");
        view.Menu.GetChild(0).AddChild(new Label { Text = "Paused", HorizontalAlignment = HorizontalAlignment.Center });
        view.ResumeButton = new Button { Name = "Resume", Text = "Resume" };
        view.SettingsButton = new Button { Name = "Settings", Text = "Settings" };
        view.Menu.GetChild(0).AddChild(view.ResumeButton);
        view.Menu.GetChild(0).AddChild(view.SettingsButton);

        view.Settings = Panel(root, "SettingsScreen");
        view.VolumeSlider = new HSlider { Name = "Volume", MaxValue = 1, Step = 0.01, Value = 1 };
        view.SettingsCloseButton = new Button { Name = "Close", Text = "Close" };
        view.Settings.GetChild(0).AddChild(new Label { Text = "Volume" });
        view.Settings.GetChild(0).AddChild(view.VolumeSlider);
        view.Settings.GetChild(0).AddChild(view.SettingsCloseButton);
        return view;
    }

    // A hidden box at the center of the screen, as tall as its content.
    static Control Panel(Control root, string name)
    {
        var panel = new PanelContainer { Name = name, Visible = false, CustomMinimumSize = new Vector2(260, 0) };
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.AddChild(new VBoxContainer());
        root.AddChild(panel);
        return panel;
    }
}

/// <summary>
/// Sample: pausing the game. The menu holds <c>clocks.Game.Pause()</c> while it is open, so the flows on
/// the Game clock stop (a world event that arrives meanwhile waits in its subscription), and <see cref="PauseLink"/>
/// pauses the tree while Game is paused, so that Godot stops the physics, the AnimationPlayers and the <c>_process</c>
/// of pausable nodes too. The screens take only the clock's pause, which is counted: the tree stays paused while any
/// owner (a screen on top, a cutscene) still pauses Game. The FlowWorldNode runs in every process mode (Always), so the
/// flows on other clocks, the menu's included, go on. The settings screen saves the <see cref="GameSettings"/> when it
/// closes, in its finally block.
/// </summary>
public static class PauseMenu
{
    /// <summary>The longest a save of the settings may take when the settings screen closes, in seconds of the UI clock.</summary>
    public const double SaveTimeoutSeconds = 2;

    /// <summary>The HUD: opens the menu on each press of the pause button. Presses while the menu is open are dropped.</summary>
    public static async FlowTask Hud(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings)
    {
        using var pausePressed = view.PauseButton.PressedSignal();
        while (true)
        {
            await pausePressed.Next();
            await Open(clocks, view, router, settings);
        }
    }

    /// <summary>
    /// Opens the menu until Resume or the back key. The menu runs on the UI clock: opened from a flow on the Game clock,
    /// it would otherwise stop under its own pause.
    /// </summary>
    public static FlowTask Open(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings) =>
        Flow.WithClock(clocks.Ui, OpenCore(clocks, view, router, settings));

    static async FlowTask OpenCore(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings)
    {
        using var pause = clocks.Game.Pause(); // PauseLink pauses the tree with it
        using var resume = view.ResumeButton.PressedSignal();
        using var settingsPressed = view.SettingsButton.PressedSignal();
        view.Menu.Visible = true;
        try
        {
            while (true)
            {
                var r = await FlowTask.Race(resume.Next(), router.Next(BackKeyRouter.Dialog), settingsPressed.Next());
                if (r.Index != 2) return;
                await Settings(clocks, view, router, settings);
            }
        }
        finally
        {
            // After a save in the settings' finally, which this flow waits for, the HUD may have been freed meanwhile.
            if (GodotObject.IsInstanceValid(view.Menu)) view.Menu.Visible = false;
        }
    }

    /// <summary>
    /// The settings screen. It pauses too, since it is also opened where the game is not paused (title, HUD). It saves
    /// <paramref name="settings"/> when it closes, if they changed.
    /// </summary>
    public static FlowTask Settings(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings) =>
        Flow.WithClock(clocks.Ui, SettingsCore(clocks, view, router, settings));

    static async FlowTask SettingsCore(GameClocks clocks, PauseMenuView view, BackKeyRouter router, GameSettings settings)
    {
        try
        {
            // The pause ends with the screen, before the save below: a slow save does not keep the game paused.
            using var pause = clocks.Game.Pause();
            using var close = view.SettingsCloseButton.PressedSignal();
            view.Settings.Visible = true;
            // The screen's controls set settings.Volume and the like (the demo wires the volume slider).
            await FlowTask.Race(close.Next(), router.Next(BackKeyRouter.Dialog));
        }
        finally
        {
            view.Settings.Visible = false;
            // Saved on every exit, the HUD freed with its scene included, and this flow waits for the save.
            // Flow.NonCancelable lets a save the close button started finish too when the HUD is freed meanwhile. A save
            // given up (not ended within SaveTimeoutSeconds of the UI clock, which the game's pause does not stop) leaves
            // the settings dirty for the next close.
            if (settings.IsDirty)
            {
                var saved = await Flow.NonCancelable(FlowTask.Race(settings.Save(), FlowTask.WaitForSeconds(SaveTimeoutSeconds)));
                if (saved.Index == 0) settings.MarkClean();
            }
        }
    }
}
