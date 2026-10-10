using UnityEngine;
using UnityEngine.UI;

namespace Katout.FlowTask.Samples
{
    /// <summary>
    /// Sample view of <see cref="ConfirmDialog"/>: the message, OK, Cancel and a close animation. A game makes it a prefab;
    /// the flow only needs the buttons and <see cref="PlayClose"/>.
    /// </summary>
    public sealed class ConfirmDialogView : MonoBehaviour
    {
        public Button OkButton { get; private set; }
        public Button CancelButton { get; private set; }
        public string Message { get; private set; }

        /// <summary>True from the start of the close animation.</summary>
        public bool IsClosing { get; private set; }

        public static ConfirmDialogView Create(Transform parent, string message)
        {
            var panel = SampleUi.Panel(parent, "ConfirmDialog", 360);
            var view = panel.gameObject.AddComponent<ConfirmDialogView>();
            view.Message = message;
            SampleUi.Label(panel, message).alignment = TextAnchor.MiddleCenter;
            view.OkButton = SampleUi.Button(panel, "OK");
            view.CancelButton = SampleUi.Button(panel, "Cancel");
            return view;
        }

        /// <summary>
        /// Shrinks the dialog over <paramref name="seconds"/> of <paramref name="clock"/> (pass a UI clock, so that it plays
        /// while the game is paused). Ends early when the dialog is destroyed meanwhile, e.g. with its scene.
        /// </summary>
        public async FlowTask PlayClose(Clock clock, double seconds = 0.25)
        {
            if (this == null) return;
            IsClosing = true;
            OkButton.interactable = false;
            CancelButton.interactable = false;
            var t = 0.0;
            while (t < seconds)
            {
                transform.localScale = Vector3.one * (float)(1 - t / seconds);
                await FlowTask.NextFrame(clock);
                if (this == null) return; // destroyed while the animation ran: nothing left to animate
                t += clock.DeltaTime;
            }

            transform.localScale = Vector3.zero;
        }
    }
}
