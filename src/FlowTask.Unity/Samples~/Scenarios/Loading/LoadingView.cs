using UnityEngine;
using UnityEngine.UI;

namespace Katout.FlowTask.Samples
{
    /// <summary>Sample view of <see cref="Loading"/>: the progress (or the error) and a Cancel button, built in code.</summary>
    public sealed class LoadingView : MonoBehaviour
    {
        Text _status;
        float _progress;
        string _error;

        public Button CancelButton { get; private set; }

        public float Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                Show();
            }
        }

        public string Error
        {
            get => _error;
            set
            {
                _error = value;
                Show();
            }
        }

        public static LoadingView Create(Transform parent)
        {
            var panel = SampleUi.Panel(parent, "Loading", 360);
            var view = panel.gameObject.AddComponent<LoadingView>();
            view._status = SampleUi.Label(panel, "");
            view.CancelButton = SampleUi.Button(panel, "Cancel");
            view.Show();
            return view;
        }

        void Show()
        {
            if (_status != null) _status.text = _error ?? $"Loading {_progress:P0}";
        }
    }
}
