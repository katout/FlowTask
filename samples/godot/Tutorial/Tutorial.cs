using System;
using System.Collections.Generic;
using Katout.FlowTask.Godot;
using Godot;

namespace Katout.FlowTask.Samples;

/// <summary>A step of <see cref="Tutorial"/>: what to say, what to point at, and how to tell the player did it.</summary>
public sealed class TutorialStep
{
    public TutorialStep(string text, Control target, Func<bool> isDone)
    {
        Text = text;
        Target = target;
        IsDone = isDone;
    }

    public string Text { get; }
    public Control Target { get; }

    /// <summary>Polled every frame: has the player done it yet?</summary>
    public Func<bool> IsDone { get; }
}

/// <summary>
/// Sample: a tutorial. Start it bound to the scene it teaches
/// (<c>NodeLifetime.WhileInTree(stage, Tutorial.Run(overlay, skip, steps))</c>): changing the scene ends it, and the
/// balloons it put on an overlay that outlives the scene are freed.
/// </summary>
public static class Tutorial
{
    /// <summary>Returns the number of steps done: all of them, or fewer when the player pressed Skip.</summary>
    public static async FlowTask<int> Run(Control overlay, Button skip, IReadOnlyList<TutorialStep> steps)
    {
        using var skipped = skip.PressedSignal();
        var completed = 0;

        async FlowTask Steps()
        {
            foreach (var step in steps)
            {
                await Show(overlay, step);
                completed++;
            }
        }

        await FlowTask.Race(Steps(), skipped.Next());
        return completed;
    }

    // One scope per step, so what it owns is freed when the step ends: done, skipped, or the scene changing. The overlay
    // covers the screen from its top left corner and lets the clicks through (MouseFilter Ignore).
    static async FlowTask Show(Control overlay, TutorialStep step)
    {
        var target = step.Target.GetGlobalRect();
        var balloon = new Label { Name = "Balloon", Text = step.Text, Position = target.Position + new Vector2(target.Size.X + 12, 0) };
        overlay.AddChild(NodeLifetime.Own(balloon));
        var highlight = new ReferenceRect
        {
            Name = "Highlight",
            EditorOnly = false,
            BorderColor = Colors.Yellow,
            BorderWidth = 3,
            MouseFilter = Control.MouseFilterEnum.Ignore, // the highlighted button stays clickable
            Position = target.Position,
            Size = target.Size,
        };
        overlay.AddChild(NodeLifetime.Own(highlight));
        await FlowTask.WaitUntil(step.IsDone);
    }
}
