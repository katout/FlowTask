using Katout.FlowTask.Unity;
using UnityEngine;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample: a confirmation dialog. <c>if (await ConfirmDialog.Show(...)) Buy();</c>
    /// </summary>
    public static class ConfirmDialog
    {
        /// <summary>
        /// Opens a dialog under <paramref name="parent"/> and returns true for OK; false for Cancel, the back key, or no
        /// answer within <paramref name="timeoutSeconds"/> of <paramref name="ui"/> time. The dialog closes (animation, then
        /// Destroy) on every exit, the caller's cancellation included.
        /// </summary>
        /// <param name="ui">A clock that runs while the game is paused, such as <see cref="GameClocks.Ui"/>.</param>
        public static async FlowTask<bool> Show(Transform parent, string message, BackKeyRouter router, Clock ui, double timeoutSeconds = 10)
        {
            var view = ConfirmDialogView.Create(parent, message);
            try
            {
                // The listeners are removed when this block ends.
                using var ok = view.OkButton.ClickedSignal();
                using var cancel = view.CancelButton.ClickedSignal();

                // The first branch that completes wins, and the others are unwound before this flow resumes: a second tap
                // in the same frame reaches nobody, and the back-key entry leaves with its branch.
                var answer = await FlowTask.Race(new[]
                {
                    ok.Next().WithoutResult(),
                    cancel.Next().WithoutResult(),
                    router.Next(BackKeyRouter.Dialog),
                    FlowTask.WaitForSeconds(timeoutSeconds, ui),
                });
                return answer == 0;
            }
            finally
            {
                // Runs on every exit, including the caller being canceled or the GameObject the flow is bound to being
                // destroyed. Flow.NonCancelable lets the animation play out and Destroy run when the cancel comes during
                // it, after an answer returned into this block too; the flows around this one wait for it. A view
                // destroyed first (with its scene) has nothing to close.
                if (view != null)
                {
                    await Flow.NonCancelable(view.PlayClose(ui));
                    if (view != null) Object.Destroy(view.gameObject);
                }
            }
        }
    }
}
