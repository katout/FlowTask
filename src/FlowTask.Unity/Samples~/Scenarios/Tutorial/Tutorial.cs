using System;
using System.Collections.Generic;
using Katout.FlowTask.Unity;
using UnityEngine;

namespace Katout.FlowTask.Samples
{
    /// <summary>A step of <see cref="Tutorial"/>: what to say, what to point at, and how to tell the player did it.</summary>
    public sealed class TutorialStep
    {
        public TutorialStep(string text, RectTransform target, Func<bool> isDone)
        {
            Text = text;
            Target = target;
            IsDone = isDone;
        }

        public string Text { get; }
        public RectTransform Target { get; }

        /// <summary>Polled every frame: has the player done it yet?</summary>
        public Func<bool> IsDone { get; }
    }

    /// <summary>
    /// Sample: a tutorial. Start it bound to the scene it teaches
    /// (<c>stage.RunWhileActive(Tutorial.Run(overlay, steps))</c>): a scene change then ends it, and the balloon and the
    /// highlight it showed are hidden, even on an overlay that survives the scene change.
    /// </summary>
    public static class Tutorial
    {
        /// <summary>Returns the number of steps done: all of them, or fewer when the player pressed Skip.</summary>
        public static async FlowTask<int> Run(TutorialOverlay overlay, IReadOnlyList<TutorialStep> steps)
        {
            using var skip = overlay.SkipButton.ClickedSignal();
            var completed = 0;

            async FlowTask Steps()
            {
                foreach (var step in steps)
                {
                    // Hidden on every exit of the step: done, skipped, or the tutorial canceled with its scene.
                    using var balloon = overlay.ShowBalloon(step.Text);
                    using var highlight = overlay.Highlight(step.Target);
                    await FlowTask.WaitUntil(step.IsDone);
                    completed++;
                }
            }

            await FlowTask.Race(Steps(), skip.Next());
            return completed;
        }
    }
}
