using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples.Tests;

/// <summary>The smoke test's checks of <see cref="ConfirmDialog"/> (the pretend input and the verdict).</summary>
public static class ConfirmDialogSmoke
{
    // The dialog Show added last: the ones before may still be in the tree, queued for deletion.
    static Control DialogUnder(Node parent)
    {
        for (var i = parent.GetChildCount() - 1; i >= 0; i--)
        {
            if (parent.GetChild(i) is Control c && !c.IsQueuedForDeletion()) return c;
        }

        return null;
    }

    static void Press(Control dialog, string button) =>
        ((Button)dialog.FindChild(button, owned: false)).EmitSignal(BaseButton.SignalName.Pressed);

    public static async FlowTask<Verdict> Run(Node host)
    {
        var ui = FlowWorldNode.Default.UnscaledClock;
        var router = new BackKeyRouter();
        var input = new BackKeyInput { Name = "BackKeyInput", Router = router };
        host.AddChild(NodeLifetime.Own(input));

        // OK, with Cancel pressed in the same frame: OK wins, the dialog closes, then it is freed.
        var screen = NodeLifetime.Own(new Control { Name = "Screen" });
        host.AddChild(screen);
        var h = Flow.Spawn(ConfirmDialog.Show(screen, "Buy?", router, ui));
        var dialog = DialogUnder(screen);
        var entries = router.Count;
        Press(dialog, "OK");
        Press(dialog, "Cancel");
        await FlowTask.WaitUntil(() => h.IsCompleted);
        var ok = h.Status == FlowStatus.Succeeded && h.Result;
        await FlowTask.NextFrame();
        var freed = !GodotObject.IsInstanceValid(dialog);

        // The back key through BackKeyInput (a ui_cancel event pushed into the viewport, as a key press would be).
        var back = Flow.Spawn(ConfirmDialog.Show(screen, "Buy?", router, ui));
        host.GetViewport().PushInput(new InputEventAction { Action = "ui_cancel", Pressed = true });
        await FlowTask.WaitUntil(() => back.IsCompleted);

        // No answer in time.
        var timeout = Flow.Spawn(ConfirmDialog.Show(screen, "Buy?", router, ui, timeoutSeconds: 0.1));
        await FlowTask.WaitUntil(() => timeout.IsCompleted);

        // The caller canceled: the close animation still plays (the flow stays Running meanwhile), then the dialog goes.
        var canceled = Flow.Spawn(ConfirmDialog.Show(screen, "Buy?", router, ui));
        var closing = DialogUnder(screen);
        await FlowTask.NextFrame();
        canceled.Cancel();
        await FlowTask.NextFrame();
        var animating = canceled.Status == FlowStatus.Running && GodotObject.IsInstanceValid(closing) && closing.Scale.X < 1;
        await FlowTask.WaitUntil(() => canceled.IsCompleted);
        await FlowTask.NextFrame();
        var canceledFreed = !GodotObject.IsInstanceValid(closing);

        // Bound to a screen that is freed while the dialog is open (RunWhileInTree): unwound, no error.
        var doomed = new Control { Name = "DoomedScreen" };
        host.AddChild(doomed);
        var bound = doomed.RunWhileInTree(ConfirmDialog.Show(doomed, "Buy?", router, ui).WithoutResult());
        await FlowTask.NextFrame();
        doomed.QueueFree();
        await FlowTask.WaitUntil(() => bound.IsCompleted);
        await FlowTask.NextFrame();

        return Verdict.Of($"OK: {h.Status} {h.Result}, freed after closing: {freed}; back key: {back.Result}; timeout: {timeout.Result}; canceled: animating {animating}, then {canceled.Status}, freed {canceledFreed}; screen freed: {bound.Status} ({bound.Result}), entries left {router.Count}",
            (entries == 1, "the dialog owned the back key"),
            (ok, "OK resolved true"), (freed, "freed after the close animation"),
            (back.Status == FlowStatus.Succeeded && !back.Result, "the back key resolved false"),
            (timeout.Status == FlowStatus.Succeeded && !timeout.Result, "no answer in time resolved false"),
            (animating, "a canceled dialog plays its close animation"),
            (canceled.Status == FlowStatus.Canceled && canceledFreed, "then it ended canceled and was freed"),
            (bound.Status == FlowStatus.Succeeded && !bound.Result, "the dialog ended with its screen"),
            (router.Count == 0, "no back-key entry left"));
    }
}
