using System.Threading;
using System.Threading.Tasks;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples.Tests;

/// <summary>The smoke test's checks of <see cref="PauseMenu"/> and <see cref="PauseLink"/>.</summary>
public static class PauseSmoke
{
    /// <summary>A settings store whose save completes when the smoke calls <see cref="Finish"/>, and never otherwise.</summary>
    sealed class PendingSettingsStore : ISettingsStore
    {
        TaskCompletionSource<bool> _pending;
        float _volume;

        public int Saves { get; private set; }
        public float SavedVolume { get; private set; } = float.NaN;
        public CancellationToken Token { get; private set; }
        public bool IsSaving => _pending != null && !_pending.Task.IsCompleted;

        public Task SaveAsync(float volume, CancellationToken ct)
        {
            Saves++;
            Token = ct;
            _volume = volume;
            var pending = new TaskCompletionSource<bool>();
            _pending = pending;
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        }

        public void Finish()
        {
            SavedVolume = _volume;
            _pending.TrySetResult(true);
        }
    }

    public static async FlowTask<Verdict> Run(Node host)
    {
        var tree = host.GetTree();
        var clocks = new GameClocks(FlowWorldNode.Default);
        var router = new BackKeyRouter();
        var store = new PendingSettingsStore();
        var settings = new GameSettings(store);
        // One per session, on a node that stays in the tree: the tree follows the Game clock.
        host.AddChild(NodeLifetime.Own(new PauseLink { Name = "PauseLink", Clock = clocks.Game }));
        var view = PauseMenuView.Create();
        host.AddChild(NodeLifetime.Own(view.Root));
        // The HUD runs on the Game clock, like the gameplay it sits on; the menu moves itself to the UI clock.
        var hud = view.Root.RunWhileInTree(PauseMenu.Hud(clocks, view, router, settings), clocks.Game);

        var enemy = new Enemy { Name = "PausedEnemy", Clocks = clocks };
        var body = new RigidBody2D { Name = "FallingBody" };
        host.AddChild(NodeLifetime.Own(body));
        Callable.From(() => host.AddChild(enemy)).CallDeferred(); // _Ready outside flow code: a root flow, as in a game
        await FlowTask.WaitUntil(enemy, e => e.IsInsideTree() && e.Position.X > 1);
        await FlowTask.WaitUntil(body, b => b.Position.Y > 1);

        // PauseLink follows the clock in its _Process: the frame after a change sees the tree follow.
        view.PauseButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(view, v => v.Menu.Visible);
        await FlowTask.NextFrame();
        var pausedByMenu = clocks.Game.PauseCount == 1 && tree.Paused;
        var x = enemy.Position.X;
        var y = body.Position.Y;
        enemy.TakeHit(1); // a world event during the pause
        await FlowTask.DelayFrames(5);
        var frozen = enemy.Position.X == x && enemy.Flinches == 0 && body.Position.Y == y;

        view.SettingsButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(view, v => v.Settings.Visible);
        var nested = clocks.Game.PauseCount == 2;
        router.Press(); // the back key closes the top screen: the settings
        await FlowTask.WaitUntil(view, v => !v.Settings.Visible);
        await FlowTask.NextFrame();
        var stillPaused = view.Menu.Visible && clocks.Game.PauseCount == 1 && tree.Paused;
        view.ResumeButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(view, v => !v.Menu.Visible);
        await FlowTask.NextFrame();
        var resumed = clocks.Game.PauseCount == 0 && !tree.Paused;
        await FlowTask.WaitUntil(enemy, e => e.Flinches == 1);
        await FlowTask.WaitUntil(body, b => b.Position.Y > y);

        // Another owner of a pause of Game (a cutscene): the tree follows the clock, whoever pauses it. The settings
        // opened and closed meanwhile leave the tree paused for the cutscene; it resumes when the cutscene lets go.
        var cutscene = clocks.Game.Pause();
        await FlowTask.NextFrame();
        var pausedByCutscene = tree.Paused;
        var overCutscene = Flow.Spawn(PauseMenu.Settings(clocks, view, router, settings));
        await FlowTask.WaitUntil(view, v => v.Settings.Visible);
        router.Press();
        await FlowTask.WaitUntil(() => overCutscene.IsCompleted);
        await FlowTask.NextFrame();
        var keptForCutscene = tree.Paused && clocks.Game.PauseCount == 1;
        cutscene.Dispose();
        await FlowTask.NextFrame();
        var cutsceneReleased = !tree.Paused;

        // The settings saved on close, in the finally block: nothing changed, nothing saved; only a completed save makes them clean.
        var unchanged = store.Saves == 0;
        var save = Flow.Spawn(PauseMenu.Settings(clocks, view, router, settings));
        await FlowTask.WaitUntil(view, v => v.Settings.Visible);
        settings.Volume = 0.5f; // what the screen's slider does
        router.Press();
        await FlowTask.WaitUntil(() => store.IsSaving);
        var dirtyWhileSaving = settings.IsDirty && !view.Settings.Visible && clocks.Game.PauseCount == 0;
        store.Finish();
        await FlowTask.WaitUntil(() => save.IsCompleted);
        var savedClean = !settings.IsDirty && store.SavedVolume == 0.5f;

        // A save that never completes is given up after SaveTimeoutSeconds and leaves the settings dirty.
        var cut = Flow.Spawn(PauseMenu.Settings(clocks, view, router, settings));
        await FlowTask.WaitUntil(view, v => v.Settings.Visible);
        settings.Volume = 0.25f;
        var ms0 = Time.GetTicksMsec();
        router.Press();
        await FlowTask.WaitUntil(() => cut.IsCompleted);
        var cutMs = Time.GetTicksMsec() - ms0;
        var cutDirty = cut.Status == FlowStatus.Succeeded && settings.IsDirty && store.Token.IsCancellationRequested &&
                       cutMs >= PauseMenu.SaveTimeoutSeconds * 1000 - 100;

        // The HUD freed while the menu and the settings are open, with a change: the save in the settings' finally runs to
        // its end, and the HUD ends after it, as through a call stack: the settings' pause is
        // released at once, the menu's once the save has ended; then the tree resumes.
        view.PauseButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(view, v => v.Menu.Visible);
        view.SettingsButton.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(view, v => v.Settings.Visible);
        await FlowTask.NextFrame();
        var pausedTwice = clocks.Game.PauseCount == 2 && tree.Paused;
        settings.Volume = 0.75f;
        view.Root.QueueFree();
        await FlowTask.DelayFrames(2);
        var savingAfterFree = store.IsSaving && !store.Token.IsCancellationRequested && !hud.IsCompleted && clocks.Game.PauseCount == 1;
        store.Finish();
        await FlowTask.WaitUntil(() => hud.IsCompleted);
        await FlowTask.NextFrame();
        var released = clocks.Game.PauseCount == 0 && !tree.Paused;
        var cleanAfterFree = !settings.IsDirty && store.SavedVolume == 0.75f;
        enemy.QueueFree();

        return Verdict.Of($"menu: paused {pausedByMenu}, frozen {frozen} (enemy and body), settings nested {nested}, still paused after closing them {stillPaused}, resumed {resumed}; cutscene: tree paused {pausedByCutscene}, kept after the settings {keptForCutscene}, released {cutsceneReleased}; save: unchanged {unchanged}, dirty while saving {dirtyWhileSaving}, clean after {savedClean}, cut after {cutMs} ms dirty {cutDirty}; HUD freed with both open: {hud.Status}, released {released}, saving {savingAfterFree}, clean {cleanAfterFree}",
            (pausedByMenu, "the menu paused Game, and the tree followed"),
            (frozen, "the enemy and the rigid body stopped, the hit waited"),
            (nested && stillPaused, "the settings paused too; closing them left the menu's pause"),
            (resumed, "Resume released every pause"),
            (pausedByCutscene && keptForCutscene && cutsceneReleased, "another owner's pause (a cutscene) kept the tree paused past the settings, and releasing it resumed the tree"),
            (unchanged && dirtyWhileSaving && savedClean, "closing the settings saved the change; only the completed save made them clean"),
            (cutDirty, "a save cut by its time limit left the settings dirty and canceled the store's token"),
            (hud.Status == FlowStatus.Succeeded && !hud.Result && pausedTwice && released, "the HUD, freed, ended after the save and released both pauses and the tree"),
            (savingAfterFree && cleanAfterFree, "the save started as the HUD was freed ran to its end, with the HUD waiting for it"),
            (router.Count == 0, "no back-key entry left"));
    }
}
