using System;
using System.Collections.Generic;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples.Tests;

/// <summary>The smoke test's checks of <see cref="Tutorial"/>.</summary>
public static class TutorialSmoke
{
    static int Balloons(Node overlay)
    {
        var n = 0;
        foreach (var child in overlay.GetChildren())
        {
            if (child is Label && !child.IsQueuedForDeletion()) n++;
        }

        return n;
    }

    static string BalloonText(Node overlay)
    {
        foreach (var child in overlay.GetChildren())
        {
            if (child is Label label && !label.IsQueuedForDeletion()) return label.Text;
        }

        return null;
    }

    public static async FlowTask<Verdict> Run(Node host)
    {
        // The overlay outlives the scenes (in a game, a CanvasLayer of an autoload); the stage is the scene it teaches.
        var overlay = NodeLifetime.Own(new Control { Name = "TutorialOverlay" });
        var skip = new Button { Name = "Skip", Text = "Skip" };
        overlay.AddChild(skip);
        host.AddChild(overlay);
        var sceneRoot = NodeLifetime.Own(new Node { Name = "SceneRoot" });
        host.AddChild(sceneRoot);

        bool moved = false, jumped = false;
        Control stage = null;

        TutorialStep[] StepsOn(Control on) => new[]
        {
            new TutorialStep("Move", on, () => moved),
            new TutorialStep("Jump", on, () => jumped),
        };

        Control NewStage(string name)
        {
            stage = new Control { Name = name };
            sceneRoot.AddChild(stage);
            return stage;
        }

        // All steps: the balloon follows them and is freed at the end.
        NewStage("Stage1");
        var all = Flow.Spawn(NodeLifetime.WhileInTree(stage, Tutorial.Run(overlay, skip, StepsOn(stage))));
        var first = BalloonText(overlay);
        moved = true;
        await FlowTask.WaitUntil(() => BalloonText(overlay) == "Jump");
        jumped = true;
        await FlowTask.WaitUntil(() => all.IsCompleted);
        await FlowTask.NextFrame();
        var afterAll = Balloons(overlay);

        // Skip during the second step: returns the one step done.
        moved = jumped = false;
        var skipped = Flow.Spawn(NodeLifetime.WhileInTree(stage, Tutorial.Run(overlay, skip, StepsOn(stage))));
        moved = true;
        await FlowTask.WaitUntil(() => BalloonText(overlay) == "Jump");
        skip.EmitSignal(BaseButton.SignalName.Pressed);
        await FlowTask.WaitUntil(() => skipped.IsCompleted);
        var afterSkip = Balloons(overlay);

        // The scene changes during the first step: the stage leaves the tree, the tutorial ends, its balloon goes.
        moved = jumped = false;
        var changed = Flow.Spawn(NodeLifetime.WhileInTree(stage, Tutorial.Run(overlay, skip, StepsOn(stage))));
        await FlowTask.NextFrame();
        var shown = Balloons(overlay);
        var afterChange = -1;
        Callable.From(() =>
        {
            // The scene change, from game code outside the flows: tree_exiting unwinds the tutorial right here.
            var old = stage;
            sceneRoot.RemoveChild(old);
            old.QueueFree();
            NewStage("Stage2");
            afterChange = Balloons(overlay);
        }).CallDeferred();
        await FlowTask.WaitUntil(() => changed.IsCompleted);
        await FlowTask.NextFrame();
        var freedAfterChange = overlay.GetChildCount() == 1; // only the Skip button

        return Verdict.Of($"all: {all.Result} (first balloon '{first}', {afterAll} left); skip: {skipped.Result} ({afterSkip} left); scene change: {changed.Status} {changed.Result}, balloons {shown} -> {afterChange}",
            (first == "Move", "the first balloon"),
            (all.Status == FlowStatus.Succeeded && all.Result.Completed && all.Result.Value == 2 && afterAll == 0, "all steps done, no balloon left"),
            (skipped.Result.Completed && skipped.Result.Value == 1 && afterSkip == 0, "Skip returned the steps done and freed the balloon"),
            (shown == 1 && afterChange == 0, "the scene change freed the balloon on the spot"),
            (changed.Status == FlowStatus.Succeeded && !changed.Result.Completed, "the tutorial ended with its scene (not completed)"),
            (freedAfterChange, "nothing left on the overlay"));
    }
}
