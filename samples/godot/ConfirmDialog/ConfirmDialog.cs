using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>
/// Sample: a confirmation dialog. <c>if (await ConfirmDialog.Show(ui, "Buy?", router, clocks.Ui)) await Buy();</c>
/// </summary>
public static class ConfirmDialog
{
    /// <summary>
    /// Opens a dialog at the center of <paramref name="parent"/> (a full-screen Control) and returns true for OK; false
    /// for Cancel, the back key, or no answer within <paramref name="timeoutSeconds"/> of <paramref name="ui"/> time. On
    /// every exit, the caller's cancellation included, the dialog plays its close animation and is freed.
    /// </summary>
    public static async FlowTask<bool> Show(Node parent, string message, BackKeyRouter router, Clock ui, double timeoutSeconds = 10)
    {
        // Owned by this flow: QueueFree when it ends, after the finally block below.
        var dialog = NodeLifetime.Own(CreateView(message, out var ok, out var cancel));
        parent.AddChild(dialog);
        try
        {
            // Disconnected when this block ends.
            using var okPressed = ok.PressedSignal();
            using var cancelPressed = cancel.PressedSignal();
            return await Ask(okPressed.Signal, cancelPressed.Signal, router, ui, timeoutSeconds);
        }
        finally
        {
            // Flow.NonCancelable lets the animation play out when the cancel comes during it, after an answer returned into
            // this block too; the flows around this one wait for it.
            await Flow.NonCancelable(PlayClose(dialog));
        }
    }

    /// <summary>
    /// The decision alone, on plain signals, so that it can be tested without nodes (tests/godot/Samples in the
    /// repository): true when <paramref name="ok"/> comes first. The first branch that completes wins and the others are
    /// unwound before this flow resumes, so a second press in the same frame reaches nobody and the back-key entry leaves
    /// with its branch.
    /// </summary>
    public static async FlowTask<bool> Ask(Signal<FlowUnit> ok, Signal<FlowUnit> cancel, BackKeyRouter router, Clock ui, double timeoutSeconds)
    {
        var answer = await FlowTask.Race(new[]
        {
            ok.Next().WithoutResult(),
            cancel.Next().WithoutResult(),
            router.Next(BackKeyRouter.Dialog),
            FlowTask.WaitForSeconds(timeoutSeconds, ui),
        });
        return answer == 0;
    }

    // Built in code; a game makes it a scene. The flow only needs the two buttons.
    static Control CreateView(string message, out Button ok, out Button cancel)
    {
        var dialog = new PanelContainer { Name = "ConfirmDialog", CustomMinimumSize = new Vector2(320, 0) };
        dialog.SetAnchorsPreset(Control.LayoutPreset.Center);
        dialog.GrowHorizontal = Control.GrowDirection.Both;
        dialog.GrowVertical = Control.GrowDirection.Both;
        var box = new VBoxContainer();
        dialog.AddChild(box);
        box.AddChild(new Label { Text = message, HorizontalAlignment = HorizontalAlignment.Center });
        ok = new Button { Name = "OK", Text = "OK" };
        cancel = new Button { Name = "Cancel", Text = "Cancel" };
        box.AddChild(ok);
        box.AddChild(cancel);
        return dialog;
    }

    /// <summary>
    /// Shrinks the dialog with a Tween that ignores Engine.TimeScale and the tree pause. A Tween whose node is freed is
    /// killed without emitting finished, so the wait ends when the Tween is no longer valid (finished or killed). A Tween
    /// does not run while its node is out of the tree, so the wait also ends when the dialog leaves the tree.
    /// </summary>
    static async FlowTask PlayClose(Control dialog)
    {
        if (!GodotObject.IsInstanceValid(dialog) || !dialog.IsInsideTree()) return;
        dialog.PivotOffset = dialog.Size / 2; // shrink to the center
        var tween = dialog.CreateTween().SetIgnoreTimeScale().SetPauseMode(Tween.TweenPauseMode.Process);
        tween.TweenProperty(dialog, "scale", Vector2.Zero, 0.25);
        await FlowTask.WaitUntil((tween, dialog), s =>
            !s.tween.IsValid() || !GodotObject.IsInstanceValid(s.dialog) || !s.dialog.IsInsideTree());
    }
}
